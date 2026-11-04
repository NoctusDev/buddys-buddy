param([switch]$Tests, [switch]$Audio, [switch]$Setup, [switch]$Relay, [switch]$WebRelay, [switch]$InternetTools, [switch]$Internet)
$ErrorActionPreference = 'Stop'
$cacheRoot = Join-Path $PSScriptRoot '..\.build-cache'
$env:DOTNET_ROOT = 'E:\dotnet'
$env:DOTNET_CLI_HOME = Join-Path $cacheRoot 'home'
$env:NUGET_PACKAGES = Join-Path $cacheRoot 'packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $cacheRoot 'http'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $cacheRoot 'plugins'
$env:APPDATA = Join-Path $cacheRoot 'appdata'
$env:LOCALAPPDATA = Join-Path $cacheRoot 'localappdata'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DALAMUD_HOME = 'D:\Multibox\Glumishxiv3\addon\Hooks\dev'
New-Item -ItemType Directory -Force $env:APPDATA,$env:LOCALAPPDATA | Out-Null
if ($InternetTools) {
    $toolsFolder = Join-Path $PSScriptRoot 'VenueAudio.Plugin\Tools'
    New-Item -ItemType Directory -Force $toolsFolder | Out-Null
    $tunnelFile = Join-Path $toolsFolder 'cloudflared.exe'
    if (-not (Test-Path -LiteralPath $tunnelFile)) {
        Invoke-WebRequest -Uri 'https://github.com/cloudflare/cloudflared/releases/download/2026.10.7/cloudflared-windows-amd64.exe' -OutFile $tunnelFile
    }
    if ((Get-FileHash -LiteralPath $tunnelFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne '2493dbc168923bb3e59e5e7716f71deced92992eea9acd6e9d74f84ac7751343') {
        throw 'Tunnel helper checksum does not match the pinned official release.'
    }
    $gatewayProject = Join-Path $PSScriptRoot 'VenueAudio.WebRelay\VenueAudio.WebRelay.csproj'
    & 'E:\dotnet\dotnet.exe' publish $gatewayProject -c Release -r win-x64 --self-contained true --nologo '-p:PublishSingleFile=true' '-p:EnableCompressionInSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:PublishDir=..\VenueAudio.Plugin\Tools\Relay\' '-p:RestoreFallbackFolders=E:\dotnet-home\nuget\packages'
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$target = if ($Tests) { Join-Path $PSScriptRoot 'VenueAudio.Tests\VenueAudio.Tests.csproj' } elseif ($Setup) { Join-Path $PSScriptRoot 'VenueAudio.Setup\VenueAudio.Setup.csproj' } elseif ($WebRelay) { Join-Path $PSScriptRoot 'VenueAudio.WebRelay\VenueAudio.WebRelay.csproj' } elseif ($Relay) { Join-Path $PSScriptRoot 'VenueAudio.Relay\VenueAudio.Relay.csproj' } else { Join-Path $PSScriptRoot 'VenueAudio.Plugin\VenueAudio.Plugin.csproj' }
if ($Setup) {
    & 'E:\dotnet\dotnet.exe' publish $target -c Release --nologo '-p:RestoreFallbackFolders=E:\dotnet-home\nuget\packages' '-p:PublishDir=bin\Release\installer\'
    if ($LASTEXITCODE -eq 0) {
        $delivery = Join-Path $PSScriptRoot '..\Ready to Install'
        New-Item -ItemType Directory -Force $delivery | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'VenueAudio.Setup\bin\Release\installer\Install Venue Audio.exe') -Destination $delivery -Force
    }
    exit $LASTEXITCODE
}
& 'E:\dotnet\dotnet.exe' build $target -c Release --nologo '-p:RestoreFallbackFolders=E:\dotnet-home\nuget\packages'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Tests) {
    $testDll = Join-Path $PSScriptRoot 'VenueAudio.Tests\bin\Release\net10.0-windows\VenueAudio.Tests.dll'
    if ($Internet) { & 'E:\dotnet\dotnet.exe' $testDll '--internet' } elseif ($Audio) { & 'E:\dotnet\dotnet.exe' $testDll '--audio' } else { & 'E:\dotnet\dotnet.exe' $testDll }
    exit $LASTEXITCODE
}
