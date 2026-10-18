param(
    [ValidateSet('win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')]
    [string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$localSdk = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
$sdk = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools/cli'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.tools/packages'
$output = Join-Path $PSScriptRoot "artifacts/$Runtime"
& $sdk publish (Join-Path $PSScriptRoot 'src/DbStudio/DbStudio.csproj') -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Output "Published $Runtime to $output"
