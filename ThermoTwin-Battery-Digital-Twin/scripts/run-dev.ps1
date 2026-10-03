# Starts the ThermoTwin API (http://localhost:5180) and the Angular dashboard (http://localhost:4200).
$root = Split-Path -Parent $PSScriptRoot
Start-Process -FilePath "dotnet" -ArgumentList "run -c Release --project `"$root\src\backend\ThermoTwin.Api`"" -WorkingDirectory $root
$web = Join-Path $root "src\frontend\thermotwin-web"
if (-not (Test-Path (Join-Path $web "node_modules"))) {
    Push-Location $web; npm install; Pop-Location
}
Start-Process -FilePath "npm.cmd" -ArgumentList "start" -WorkingDirectory $web
Write-Host "API:       http://localhost:5180/swagger"
Write-Host "Dashboard: http://localhost:4200"
