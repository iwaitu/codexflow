param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$ExcelAssembly,
    [Parameter(Mandatory = $true)][string]$NewtonsoftAssembly
)

$ErrorActionPreference = 'Stop'
# MSBuild can inherit a PowerShell 7 module path while invoking Windows PowerShell.
Import-Module (Join-Path $PSHOME 'Modules/Microsoft.PowerShell.Management')
Import-Module (Join-Path $PSHOME 'Modules/Microsoft.PowerShell.Utility')

$curl = (Get-Command curl -CommandType Application | Select-Object -First 1).Source
$tar = (Get-Command tar -CommandType Application | Select-Object -First 1).Source

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable exited with code $LASTEXITCODE."
    }
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Test-FileHash([string]$Path, [string]$Expected) {
    (Test-Path -LiteralPath $Path -PathType Leaf) -and ((Get-Sha256 $Path) -ceq $Expected)
}

$lockPath = Join-Path $PSScriptRoot 'sources.lock.psd1'
$dependencies = (Import-PowerShellDataFile -LiteralPath $lockPath).Dependencies
$officialAssemblies = @{
    'ExcelDataReader' = $ExcelAssembly
    'Newtonsoft.Json' = $NewtonsoftAssembly
}
$sdkVersion = (Invoke-Checked 'dotnet' @('--version')).Trim()
$keyParts = @($sdkVersion, 'Release', (Get-Sha256 $PSCommandPath), (Get-Sha256 $lockPath))
$outputNames = @()
foreach ($dependency in $dependencies) {
    $name = $dependency.Name
    $official = Get-Item -LiteralPath $officialAssemblies[$name]
    $actualFramework = $official.Directory.Name
    $actualVersion = $official.Directory.Parent.Parent.Name
    if ($actualFramework -ne $dependency.Framework -or $actualVersion -ne $dependency.Version) {
        throw "Expected $name $($dependency.Version) $($dependency.Framework), but received $($official.FullName). Update the source lock and verify the patch before changing the package."
    }
    $keyParts += (Get-Sha256 $official.FullName), (Get-Sha256 (Join-Path $PSScriptRoot $dependency.Patch))
    $outputNames += "$name.dll", "$name.LICENSE.txt"
}
$cacheKey = $keyParts -join '/'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$statePath = Join-Path $OutputDirectory 'verified-build.json'

if (Test-Path -LiteralPath $statePath -PathType Leaf) {
    $state = try { Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json } catch { $null }
    if ($state.key -ceq $cacheKey) {
        $valid = $true
        foreach ($outputName in $outputNames) {
            if (-not (Test-FileHash (Join-Path $OutputDirectory $outputName) $state.files.$outputName)) {
                $valid = $false
                break
            }
        }
        if ($valid) {
            Write-Host 'Using verified Native AOT dependency builds.'
            return
        }
    }
    Remove-Item -LiteralPath $statePath
}

$downloads = Join-Path $OutputDirectory 'downloads'
$stage = Join-Path $OutputDirectory ('build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $downloads, $stage -Force | Out-Null
$files = [ordered]@{}
foreach ($dependency in $dependencies) {
    $name = $dependency.Name
    $archive = Join-Path $downloads ($dependency.ArchiveSha256 + '.tar.gz')
    if (-not (Test-FileHash $archive $dependency.ArchiveSha256)) {
        $download = Join-Path $stage "$name.tar.gz"
        $uri = "https://codeload.github.com/$($dependency.Repository)/tar.gz/$($dependency.Commit)"
        Invoke-Checked $curl @('--fail', '--location', '--silent', '--show-error', '--retry', '2',
            '--connect-timeout', '20', '--max-time', '120', '--output', $download, $uri)
        if (-not (Test-FileHash $download $dependency.ArchiveSha256)) {
            throw "The downloaded source archive for $name does not match its locked SHA-256."
        }
        Move-Item -LiteralPath $download -Destination $archive -Force
    }

    $source = Join-Path $stage $name
    New-Item -ItemType Directory -Path $source | Out-Null
    Invoke-Checked $tar @('-xzf', $archive, '-C', $source, '--strip-components', '1')
    $patch = Join-Path $PSScriptRoot $dependency.Patch
    $previousGitCeiling = $env:GIT_CEILING_DIRECTORIES
    try {
        # The intermediate directory can be inside the main Git checkout.
        $env:GIT_CEILING_DIRECTORIES = $stage
        Invoke-Checked 'git' @('-C', $source, 'apply', '--check', '--whitespace=error-all', $patch)
        Invoke-Checked 'git' @('-C', $source, 'apply', '--whitespace=error-all', $patch)
    }
    finally {
        $env:GIT_CEILING_DIRECTORIES = $previousGitCeiling
    }

    $project = Join-Path $source $dependency.Project
    $artifacts = Join-Path $source 'artifacts'
    $buildArguments = @('build', $project, '-c', 'Release', '-m:1', '-nr:false', '-warnaserror',
        '-p:TreatWarningsAsErrors=true', '-p:UseSharedCompilation=false', '-p:EnableSourceControlManagerQueries=false',
        "-p:SourceRevisionId=$($dependency.Commit)",
        "-p:NuGetLockFilePath=$(Join-Path $stage "$name.restore.lock.json")",
        '--artifacts-path', $artifacts) + $dependency.BuildArguments
    $officialIdentity = [Reflection.AssemblyName]::GetAssemblyName($officialAssemblies[$name])
    if ($name -eq 'Newtonsoft.Json') {
        $publicKey = Join-Path $source 'official-public.snk'
        [IO.File]::WriteAllBytes($publicKey, $officialIdentity.GetPublicKey())
        $buildArguments += "-p:AssemblyOriginatorKeyFile=$publicKey"
    }
    Invoke-Checked 'dotnet' $buildArguments

    $dll = Join-Path $artifacts "bin/$name/release_$($dependency.Framework)/$name.dll"
    $builtIdentity = [Reflection.AssemblyName]::GetAssemblyName($dll)
    if ($builtIdentity.FullName -cne $officialIdentity.FullName) {
        throw "The rebuilt identity for $name differs from $($officialIdentity.FullName)."
    }
    $files["$name.dll"] = $dll
    $files["$name.LICENSE.txt"] = Join-Path $source $dependency.License
}

$hashes = [ordered]@{}
foreach ($outputName in $outputNames) {
    $output = Join-Path $OutputDirectory $outputName
    Copy-Item -LiteralPath $files[$outputName] -Destination $output -Force
    $hashes[$outputName] = Get-Sha256 $output
}
# Publish the cache record only after both verified assemblies and licenses have been copied.
$temporaryState = Join-Path $stage 'verified-build.json'
@{ key = $cacheKey; files = $hashes } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $temporaryState -Encoding UTF8
Move-Item -LiteralPath $temporaryState -Destination $statePath -Force
