# Builds the correction engine into python-engine\dist\typryt-engine (one folder, no venv needed to run it).
# Run from the python-engine folder after the venv exists:  powershell -ExecutionPolicy Bypass -File build_engine.ps1

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

& ".\venv\Scripts\python.exe" -m pip install --quiet pyinstaller

& ".\venv\Scripts\python.exe" -m PyInstaller `
    --noconfirm `
    --onedir `
    --name typryt-engine `
    --paths . `
    --add-data "dictionaries;dictionaries" `
    --collect-data symspellpy `
    --collect-submodules uvicorn `
    --collect-submodules spylls `
    --hidden-import uvicorn.logging `
    --hidden-import uvicorn.loops.auto `
    --hidden-import uvicorn.protocols.http.auto `
    --hidden-import uvicorn.protocols.websockets.auto `
    --hidden-import uvicorn.lifespan.on `
    engine_entry.py

if (-not (Test-Path ".\dist\typryt-engine\typryt-engine.exe")) {
    throw "Engine build failed: typryt-engine.exe was not created."
}
Write-Host "Engine built: $PSScriptRoot\dist\typryt-engine\typryt-engine.exe"
