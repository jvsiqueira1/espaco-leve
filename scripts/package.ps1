param(
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory=$true)][ValidateSet('win-x64','osx-x64','osx-arm64','linux-x64')][string]$Runtime
)
$ErrorActionPreference = 'Stop'
$hostPlatform = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } else { 'linux' }
$hostArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if ($Runtime -ne "$hostPlatform-$hostArchitecture") { throw 'Compile o pacote no sistema e arquitetura de destino.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$publishDirectory = Join-Path $projectRoot "artifacts/publish/$Runtime"
$releaseDirectory = Join-Path $projectRoot 'artifacts/release'
New-Item -ItemType Directory -Path $publishDirectory,$releaseDirectory -Force | Out-Null
& dotnet publish (Join-Path $projectRoot 'src/EspacoLeve.App') -c Release -r $Runtime --self-contained true "-p:Version=$Version" "-p:InformationalVersion=$Version" "-p:DebugType=None" -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Falha na compilação do pacote.' }
$compiledVersion = & dotnet (Join-Path $publishDirectory 'EspacoLeve.dll') --version
if ($LASTEXITCODE -ne 0 -or $compiledVersion.Trim() -ne $Version) { throw 'A versão do aplicativo não corresponde ao pacote.' }
if (!$IsWindows) {
    $guardName = if ($IsMacOS) { 'libespacoleve_native.dylib' } else { 'libespacoleve_native.so' }
    if (!(Test-Path -LiteralPath (Join-Path $publishDirectory $guardName))) { throw 'Proteção nativa ausente no pacote.' }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishDirectory
if ($Runtime.StartsWith('osx-')) {
    $bundle = Join-Path $projectRoot "artifacts/bundle/$Runtime/Espaço Leve.app"
    $macosDirectory = Join-Path $bundle 'Contents/MacOS'
    New-Item -ItemType Directory -Path $macosDirectory -Force | Out-Null
    Copy-Item -Path (Join-Path $publishDirectory '*') -Destination $macosDirectory -Recurse -Force
    $plist = '<?xml version="1.0" encoding="UTF-8"?><!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd"><plist version="1.0"><dict><key>CFBundleIdentifier</key><string>io.github.jvsiqueira1.espaco-leve</string><key>CFBundleName</key><string>Espaço Leve</string><key>CFBundleDisplayName</key><string>Espaço Leve</string><key>CFBundleExecutable</key><string>EspacoLeve</string><key>CFBundlePackageType</key><string>APPL</string><key>CFBundleShortVersionString</key><string>' + $Version + '</string><key>CFBundleVersion</key><string>' + $Version + '</string><key>NSHighResolutionCapable</key><true/></dict></plist>'
    [IO.File]::WriteAllText((Join-Path $bundle 'Contents/Info.plist'), $plist)
    & chmod +x (Join-Path $macosDirectory 'EspacoLeve')
    if ($LASTEXITCODE -ne 0) { throw 'Não foi possível preparar o executável.' }
    & codesign --force --deep --sign - $bundle
    if ($LASTEXITCODE -ne 0) { throw 'Falha na assinatura ad hoc do aplicativo.' }
    & ditto -c -k --sequesterRsrc --keepParent $bundle (Join-Path $releaseDirectory "EspacoLeve-$Version-$Runtime.zip")
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao criar o pacote macOS.' }
} elseif ($Runtime.StartsWith('linux-')) {
    & chmod +x (Join-Path $publishDirectory 'EspacoLeve')
    & tar -czf (Join-Path $releaseDirectory "EspacoLeve-$Version-$Runtime.tar.gz") -C $publishDirectory .
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao criar o pacote Linux.' }
} else {
    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath (Join-Path $releaseDirectory "EspacoLeve-$Version-$Runtime.zip") -Force
}
