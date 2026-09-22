param([switch]$SingleFile)
$ErrorActionPreference = 'Stop'
$dotnet = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $installer = Join-Path $PSScriptRoot 'scripts\dotnet-install.ps1'
    if (-not (Test-Path $installer)) {
        Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
    }
    & $installer -Version '10.0.401' -InstallDir (Join-Path $PSScriptRoot '.tools\dotnet') -NoPath
    if (-not (Test-Path $dotnet)) { throw 'Local .NET SDK installation failed.' }
}
$env:DOTNET_ROOT = Split-Path $dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $PSScriptRoot
try {
    & $dotnet run --project 'tests\LineRename.Tests\LineRename.Tests.csproj' -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed. Publishing stopped.' }
    if ($SingleFile) {
        & $dotnet publish 'src\LineRename.App\LineRename.App.csproj' -c Release -p:Platform=x64 -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o 'artifacts\SingleFile'
    }
    else {
        & $dotnet publish 'src\LineRename.App\LineRename.App.csproj' -c Release -p:Platform=x64 -p:PublishSingleFile=false -o 'artifacts\LineRename'
    }
    if ($LASTEXITCODE -ne 0) { throw 'Publishing failed.' }
    if ($SingleFile) {
        Write-Host "Ready: $PSScriptRoot\artifacts\SingleFile\LineRename.exe"
        Write-Host 'Distribute only this EXE. Runtime files are extracted automatically on launch.'
    }
    else {
        Write-Host "Ready: $PSScriptRoot\artifacts\LineRename\LineRename.exe"
        Write-Host 'Copy the entire LineRename output folder when moving the app.'
    }
}
finally { Pop-Location }
