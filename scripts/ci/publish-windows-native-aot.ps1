[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$ArtifactsDirectory,
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$publishDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$repository = Join-Path $PSScriptRoot '../..'
$arguments = @('publish', 'CodexFlow.Local/CodexFlow.Local.csproj', '-c', 'Release', '-r', 'win-x64',
    '-m:1', '-nr:false', '-p:UseSharedCompilation=false',
    '-p:PublishAot=true', '-p:RuntimeFrameworkVersion=10.0.15', '-p:TrimmerSingleWarn=false',
    '-p:TreatWarningsAsErrors=true', '-p:ILLinkTreatWarningsAsErrors=true', '-warnaserror', '-o', $publishDirectory)

if ($ArtifactsDirectory) {
    $artifacts = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ArtifactsDirectory)
    $arguments += '--artifacts-path', $artifacts
}
if ($Version) {
    $arguments += "-p:Version=$Version"
}

Push-Location $repository
try {
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Windows x64 Native AOT publish failed (exit $LASTEXITCODE)."
    }
}
finally {
    Pop-Location
}

$requiredFiles = @(
    'CodexFlow.Local.exe',
    'e_sqlite3.dll',
    'code-analysis/worker/CodexFlow.CodeAnalysis.Worker.exe',
    'third-party-licenses/ExcelDataReader.LICENSE.txt',
    'third-party-licenses/Newtonsoft.Json.LICENSE.txt'
)
foreach ($file in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $file) -PathType Leaf)) {
        throw "The Windows x64 Native AOT publish is missing $file."
    }
}
