# Builds the Typryt release installer from the current code, in one repeatable run:
#   1. cleans old build output (engine build\ and dist\, publish\app)
#   2. builds the engine with PyInstaller (python-engine\typryt-engine.spec) and checks that every data file is in it
#   3. runs the BUILT engine on its own and checks its answers ("Priya" stays, "teh" -> "the", a rare typo is fixed)
#   4. publishes the app (self-contained, win-x64)
#   5. copies the engine into publish\app\engine
#   6. compiles installer\setup.iss with Inno Setup (called by its full path)
#   7. writes installer\Output\Typryt-Setup-<version>.exe and a .sha256 file next to it
# Usage: powershell -ExecutionPolicy Bypass -File installer\build_release.ps1  [-Inno "C:\path\to\ISCC.exe"]
param([string]$Inno = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Fail([string]$message) { Write-Host "BUILD FAILED: $message" -ForegroundColor Red; exit 1 }
function Step([string]$message) { Write-Host "`n== $message" -ForegroundColor Cyan }

$version = ([xml](Get-Content "csharp-app\Typryt.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { Fail "no <Version> in csharp-app\Typryt.csproj" }
$issVersion = (Select-String -Path "installer\setup.iss" -Pattern '#define MyAppVersion "([^"]+)"').Matches[0].Groups[1].Value
if ($issVersion -ne $version) { Fail "version mismatch: csproj $version, setup.iss $issVersion" }
Write-Host "Version $version"
if (-not (Test-Path $Inno)) { Fail "Inno Setup compiler not found at $Inno" }
foreach ($f in "LICENSE.txt", "THIRD_PARTY_NOTICES.md", "csharp-app\Assets\typryt.ico") {
    if (-not (Test-Path $f)) { Fail "missing $f" }
}

Step "1. Clean"
foreach ($path in "python-engine\build", "python-engine\dist", "publish\app", "csharp-app\obj\Release") {
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}

Step "2. Engine"
& powershell -NoProfile -ExecutionPolicy Bypass -File "python-engine\build_engine.ps1"
if ($LASTEXITCODE -ne 0) { Fail "engine build" }
$engine = "python-engine\dist\typryt-engine"
$required = @(
    "typryt-engine.exe",
    "_internal\dictionaries\en_US.aff", "_internal\dictionaries\en_US.dic",
    "_internal\symspellpy\frequency_dictionary_en_82_765.txt", "_internal\symspellpy\frequency_bigramdictionary_en_243_342.txt"
)
foreach ($lexicon in Get-ChildItem "python-engine\lexicons" -File) { $required += "_internal\lexicons\$($lexicon.Name)" }
foreach ($r in $required) {
    if (-not (Test-Path (Join-Path $engine $r))) { Fail "the built engine is missing $r" }
}
Write-Host "all $($required.Count) required engine files are present"

Step "3. Run the built engine on its own"
$port = 0
$listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0); $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
$token = -join ((1..32) | ForEach-Object { '{0:x2}' -f (Get-Random -Maximum 256) })
$data = Join-Path $env:TEMP "typryt-build-check-$PID"
New-Item -ItemType Directory -Force $data | Out-Null
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = (Resolve-Path "$engine\typryt-engine.exe").Path
$psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
$psi.EnvironmentVariables["TYPRYT_ENGINE_PORT"] = "$port"
$psi.EnvironmentVariables["TYPRYT_ENGINE_TOKEN"] = $token
$psi.EnvironmentVariables["TYPRYT_DATA_FOLDER"] = $data
foreach ($v in "PYTHONPATH", "VIRTUAL_ENV", "PYTHONHOME") { $psi.EnvironmentVariables.Remove($v) }
$proc = [System.Diagnostics.Process]::Start($psi)
try {
    $up = $false
    for ($i = 0; $i -lt 90 -and -not $up; $i++) {
        try { $r = Invoke-RestMethod "http://127.0.0.1:$port/health" -Headers @{ "X-Typryt-Token" = $token } -TimeoutSec 2; $up = ($r.status -eq "ok") } catch { Start-Sleep -Milliseconds 500 }
        if ($proc.HasExited) { Fail "the built engine exited (code $($proc.ExitCode))" }
    }
    if (-not $up) { Fail "the built engine did not answer /health" }
    function Ask([string]$word) {
        $body = @{ word = $word; session_id = "build"; learning_enabled = $false } | ConvertTo-Json
        $last = ""
        for ($k = 0; $k -lt 3; $k++) {   # the first Hunspell answer for a word can arrive late; ask again
            $last = (Invoke-RestMethod "http://127.0.0.1:$port/correct" -Method Post -Body $body -ContentType "application/json" -Headers @{ "X-Typryt-Token" = $token }).correction
            if ($last -ne $word) { break }
        }
        return $last
    }
    $checks = @(@("Priya", "Priya"), @("teh", "the"), @("chocolte", "chocolate"))
    foreach ($c in $checks) {
        $got = Ask $c[0]
        Write-Host ("  {0} -> {1}" -f $c[0], $got)
        if ($got -cne $c[1]) { Fail "the built engine answered '$got' for '$($c[0])', expected '$($c[1])'" }
    }
    $denied = $false
    try { Invoke-RestMethod "http://127.0.0.1:$port/health" -TimeoutSec 2 | Out-Null } catch { $denied = ($_.Exception.Response.StatusCode -eq 401) }
    if (-not $denied) { Fail "the built engine answered a request without the token" }
    Write-Host "  a request without the token is refused (401)"
} finally {
    if (-not $proc.HasExited) { $proc.Kill() }
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
}

Step "4. Publish the app"
& dotnet publish "csharp-app\Typryt.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none -o "publish\app" --nologo -v q
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish" }
if (-not (Test-Path "publish\app\Typryt.exe")) { Fail "publish\app\Typryt.exe is missing" }

Step "5. Copy the engine into the app folder"
Copy-Item $engine "publish\app\engine" -Recurse -Force
if (-not (Test-Path "publish\app\engine\typryt-engine.exe")) { Fail "engine copy" }

Step "6. Inno Setup"
& $Inno "installer\setup.iss" /Qp
if ($LASTEXITCODE -ne 0) { Fail "Inno Setup compile" }

Step "7. Result"
$setup = "installer\Output\Typryt-Setup-$version.exe"
if (-not (Test-Path $setup)) { Fail "$setup was not written" }
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
Set-Content "$setup.sha256" "$hash  Typryt-Setup-$version.exe" -Encoding ascii
Write-Host ("{0}  {1:N1} MB" -f $setup, ((Get-Item $setup).Length / 1MB))
Write-Host "SHA-256 $hash"
Write-Host "BUILD OK" -ForegroundColor Green
