param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$localSdk = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
$sdk = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools\cli'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.tools\packages'
& $sdk build (Join-Path $PSScriptRoot 'src\DbStudio\DbStudio.csproj') -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $sdk run --project (Join-Path $PSScriptRoot 'tests\DbStudio.Checks\DbStudio.Checks.csproj') -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Publish) {
    & $sdk publish (Join-Path $PSScriptRoot 'src\DbStudio\DbStudio.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o (Join-Path $PSScriptRoot 'artifacts\win-x64')
    exit $LASTEXITCODE
}
