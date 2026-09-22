# 构建 LineRename（Tauri + Rust）。产物：src-tauri\target\release\LineRename.exe
# 前置：Rust 工具链（https://rustup.rs）与 WebView2 运行时（Win11 内置；Win10 需安装）。
$ErrorActionPreference = 'Stop'
$cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
if (-not (Test-Path $cargo)) { $cargo = 'cargo' }

Push-Location (Join-Path $PSScriptRoot 'src-tauri')
try {
    & $cargo build --release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    $exe = Join-Path $PSScriptRoot 'src-tauri\target\release\LineRename.exe'
    $size = (Get-Item $exe).Length
    Write-Host "Ready: $exe ($size bytes)"
    Write-Host 'Single EXE. Requires WebView2 runtime (built into Windows 11).'
}
finally { Pop-Location }
