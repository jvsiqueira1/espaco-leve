param(
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory=$true)][ValidateSet('win-x64','osx-x64','osx-arm64','linux-x64')][string]$Runtime
)
$ErrorActionPreference = 'Stop'
$hostPlatform = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } else { 'linux' }
$hostArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if ($Runtime -ne "$hostPlatform-$hostArchitecture") { throw 'Compile o pacote no sistema e arquitetura de destino.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$publishDirectory = Join-Path $projectRoot "artifacts/publish/$Version/$Runtime"
$releaseDirectory = Join-Path $projectRoot 'artifacts/release'
New-Item -ItemType Directory -Path $publishDirectory,$releaseDirectory -Force | Out-Null
$publishOptions = @()
if ($IsWindows) {
    $publishOptions = @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true')
}
& dotnet publish (Join-Path $projectRoot 'src/EspacoLeve.App') -c Release -r $Runtime --self-contained true "-p:Version=$Version" "-p:InformationalVersion=$Version" "-p:DebugType=None" @publishOptions -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Falha na compilação do pacote.' }
if ($IsWindows) {
    # Test only the single executable in a fresh directory, with no companion DLLs.
    $smokeDirectory = Join-Path $projectRoot ('work/package-smoke/' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $smokeDirectory -Force | Out-Null
    $smokeExecutable = Join-Path $smokeDirectory 'EspacoLeve.exe'
    Copy-Item -LiteralPath (Join-Path $publishDirectory 'EspacoLeve.exe') -Destination $smokeExecutable
    foreach ($mode in @('--version', '--smoke-test')) {
        $stdout = Join-Path $smokeDirectory ($mode.TrimStart('-') + '.stdout')
        $stderr = Join-Path $smokeDirectory ($mode.TrimStart('-') + '.stderr')
        $testProcess = Start-Process -FilePath $smokeExecutable -ArgumentList $mode -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        if (!$testProcess.WaitForExit(60000)) { $testProcess.Kill(); throw 'A verificação do executável excedeu o tempo disponível.' }
        $testProcess.Refresh()
        if ($testProcess.ExitCode -ne 0) { throw ('Falha no executável independente: ' + (Get-Content -LiteralPath $stderr -Raw)) }
        if ($mode -eq '--version' -and (Get-Content -LiteralPath $stdout -Raw).Trim() -ne $Version) { throw 'A versão do executável não corresponde ao pacote.' }
    }
} else {
    $compiledVersion = & dotnet (Join-Path $publishDirectory 'EspacoLeve.dll') --version
    if ($LASTEXITCODE -ne 0 -or $compiledVersion.Trim() -ne $Version) { throw 'A versão do aplicativo não corresponde ao pacote.' }
}
if (!$IsWindows) {
    $guardName = if ($IsMacOS) { 'libespacoleve_native.dylib' } else { 'libespacoleve_native.so' }
    if (!(Test-Path -LiteralPath (Join-Path $publishDirectory $guardName))) { throw 'Proteção nativa ausente no pacote.' }
}
if ($Runtime.StartsWith('osx-')) {
    & sh (Join-Path $PSScriptRoot 'package-macos.sh') $publishDirectory $Version $Runtime $releaseDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao criar a imagem de disco macOS.' }
} elseif ($Runtime.StartsWith('linux-')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishDirectory
    & chmod +x (Join-Path $publishDirectory 'EspacoLeve')
    & tar -czf (Join-Path $releaseDirectory "EspacoLeve-$Version-$Runtime.tar.gz") -C $publishDirectory .
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao criar o pacote Linux.' }
} else {
    Copy-Item -LiteralPath (Join-Path $publishDirectory 'EspacoLeve.exe') -Destination (Join-Path $releaseDirectory "EspacoLeve-$Version-$Runtime.exe") -Force
}
