# Builds the correction engine into python-engine\dist\typryt-engine (one folder, no venv needed to run it), from typryt-engine.spec.
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File python-engine\build_engine.ps1
# installer\build_release.ps1 calls this and then checks the built engine.

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

foreach ($folder in "build", "dist") {
    if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
}

& ".\venv\Scripts\python.exe" -m pip install --quiet pyinstaller
if ($LASTEXITCODE -ne 0) { throw "pip install pyinstaller failed" }

& ".\venv\Scripts\python.exe" -m PyInstaller --noconfirm --clean typryt-engine.spec
if ($LASTEXITCODE -ne 0) { throw "PyInstaller failed" }

if (-not (Test-Path ".\dist\typryt-engine\typryt-engine.exe")) {
    throw "Engine build failed: typryt-engine.exe was not created."
}
Write-Host "Engine built: $PSScriptRoot\dist\typryt-engine\typryt-engine.exe"
