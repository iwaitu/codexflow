@{
    Dependencies = @(
        @{
            Name = 'ExcelDataReader'
            Version = '3.9.0'
            Repository = 'ExcelDataReader/ExcelDataReader'
            Commit = 'f3343f5524518923bdeb4801e5003c0417fd35f3'
            ArchiveSha256 = 'F673467087A70E3784B0E9C1E03474B149D3655041D533C563EE26588A1FC0AE'
            Framework = 'net8.0'
            Patch = 'exceldatareader-getfieldtype.patch'
            Project = 'src/ExcelDataReader/ExcelDataReader.csproj'
            License = 'LICENSE'
            BuildArguments = @('-p:TargetFrameworks=net8.0', '-p:GeneratePackageOnBuild=false', '-p:CI=true')
        }
        @{
            Name = 'Newtonsoft.Json'
            Version = '13.0.4'
            Repository = 'JamesNK/Newtonsoft.Json'
            Commit = '4e13299d4b0ec96bd4df9954ef646bd2d1b5bf2a'
            ArchiveSha256 = '83173A8606786E1E546D2502DBD37B33ED8078A4DEE5EED49E905770D1B15E56'
            Framework = 'net6.0'
            Patch = 'newtonsoft-jvalue-converters.patch'
            Project = 'Src/Newtonsoft.Json/Newtonsoft.Json.csproj'
            License = 'LICENSE.md'
            BuildArguments = @(
                '-p:LibraryFrameworks=net6.0', '-p:MicrosoftCodeAnalysisNetAnalyzersPackageVersion=10.0.404',
                '-p:AssemblyVersion=13.0.0.0', '-p:FileVersion=13.0.4.30916', '-p:VersionPrefix=13.0.4',
                '-p:VersionSuffix=', '-p:ContinuousIntegrationBuild=true',
                '-p:DebugType=none', '-p:SignAssembly=true', '-p:PublicSign=true', '-p:AdditionalConstants=SIGNED'
            )
        }
    )
}
