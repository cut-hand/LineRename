param(
    [ValidateSet('All', 'SingleFile', 'FrameworkDependent', 'Folder')]
    [string]$Target = 'All'
)
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

    $app = 'src\LineRename.App\LineRename.App.csproj'
    $singleFileArgs = @('-c', 'Release', '-p:Platform=x64', '-p:PublishSingleFile=true', '-p:DebugType=None', '-p:DebugSymbols=false')

    if ($Target -in 'All', 'SingleFile') {
        & $dotnet publish $app @singleFileArgs -o 'artifacts\SingleFile'
        if ($LASTEXITCODE -ne 0) { throw 'Publishing failed (self-contained single file).' }
        Write-Host "Ready: $PSScriptRoot\artifacts\SingleFile\LineRename.exe"
        Write-Host 'Self-contained: no runtime required. Distribute only this EXE.'
    }

    if ($Target -in 'All', 'FrameworkDependent') {
        & $dotnet publish $app -c Release -p:Platform=x64 -p:PublishSingleFile=false '-p:SelfContained=false' '-p:WindowsAppSDKSelfContained=false' -o 'artifacts\LineRename-FrameworkDependent'
        if ($LASTEXITCODE -ne 0) { throw 'Publishing failed (framework-dependent folder).' }
        $zip = Join-Path $PSScriptRoot 'artifacts\LineRename-FrameworkDependent.zip'
        if (Test-Path $zip) { Remove-Item $zip -Force }
        Compress-Archive -Path (Join-Path $PSScriptRoot 'artifacts\LineRename-FrameworkDependent') -DestinationPath $zip -CompressionLevel Optimal
        Write-Host "Ready: $zip"
        Write-Host 'Framework-dependent: small download, but requires .NET 10 Desktop Runtime and Windows App SDK runtime on the target machine.'
    }

    if ($Target -in 'All', 'Folder') {
        & $dotnet publish $app -c Release -p:Platform=x64 -p:PublishSingleFile=false -o 'artifacts\LineRename'
        if ($LASTEXITCODE -ne 0) { throw 'Publishing failed (folder layout).' }
        Write-Host "Ready: $PSScriptRoot\artifacts\LineRename\LineRename.exe"
        Write-Host 'Copy the entire LineRename output folder when moving the app.'
    }
}
finally { Pop-Location }
