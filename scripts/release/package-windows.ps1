$ErrorActionPreference = 'Stop'
if (!$env:VERSION -or !$env:PUBLIC_KEY -or !$env:REPOSITORY) { throw 'Missing release configuration' }
$output = Join-Path $PWD 'artifacts/win-x64'
$installers = Join-Path $PWD 'artifacts/installers'
dotnet publish src/DbPilot/DbPilot.csproj -c Release -r win-x64 --self-contained true "-p:Version=$env:VERSION" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $output
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Invoke-WebRequest 'https://github.com/vslavik/winsparkle/releases/download/v0.9.4/WinSparkle-0.9.4.zip' -OutFile artifacts/WinSparkle.zip
if ((Get-FileHash artifacts/WinSparkle.zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne '6037df37fc263bd1650a1c4949681a9d40ffe991d01f35892a406cb5d103c976') { throw 'WinSparkle checksum mismatch' }
Expand-Archive artifacts/WinSparkle.zip artifacts/winsparkle -Force
$dll = @(Get-ChildItem artifacts/winsparkle -Recurse -Filter WinSparkle.dll | Where-Object { $_.FullName -match '[\\/]x64[\\/]' })
if ($dll.Count -ne 1) { throw 'Expected one x64 WinSparkle.dll' }
Copy-Item $dll[0].FullName $output
$licenses = Join-Path $output 'ThirdParty/WinSparkle'
New-Item -ItemType Directory -Force $licenses | Out-Null
Get-ChildItem artifacts/winsparkle -Recurse -File | Where-Object { $_.Name -match '^(COPYING|LICENSE|NOTICE)' } | ForEach-Object { Copy-Item $_.FullName $licenses }
$owner, $repo = $env:REPOSITORY -split '/', 2
@{ FeedUrl="https://$owner.github.io/$repo/stable/appcast-win-x64.xml"; PublicKey=$env:PUBLIC_KEY } | ConvertTo-Json | Set-Content "$output/updates.json" -Encoding utf8
$iscc = 'C:/Program Files (x86)/Inno Setup 6/ISCC.exe'
if (!(Test-Path $iscc)) {
    choco install innosetup --no-progress -y
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup installation failed' }
}
& $iscc "/DAppVersion=$env:VERSION" "/DSourceDir=$output" "/DOutputDir=$installers" packaging/windows/DbPilot.iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
