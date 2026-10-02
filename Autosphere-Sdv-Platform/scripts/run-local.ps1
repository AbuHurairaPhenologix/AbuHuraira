# Runs the infrastructure in Docker and the API + gateway (with simulated ECUs) as local processes.
#   Copy-Item .env.example .env ; ./scripts/run-local.ps1
# Then: cd src/frontend/autosphere-web ; npm start   -> http://localhost:4200
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root

if (-not (Test-Path .env)) { throw ".env not found. Run: Copy-Item .env.example .env" }
$envValues = @{}
Get-Content .env | Where-Object { $_ -match '^\s*[^#].*=' } | ForEach-Object {
    $name, $value = $_ -split '=', 2
    $envValues[$name.Trim()] = $value.Trim()
}

docker compose up -d postgres redis mqtt

# Secrets are passed as process environment variables only.
$port = if ($envValues['POSTGRES_PORT']) { $envValues['POSTGRES_PORT'] } else { '5432' }
$env:PGPASSWORD = $envValues['POSTGRES_PASSWORD']
$env:ConnectionStrings__AutoSphere = "Host=localhost;Port=$port;Database=$($envValues['POSTGRES_DB']);Username=$($envValues['POSTGRES_USER'])"
$env:Jwt__SigningKey = $envValues['JWT_SIGNING_KEY']
$env:Seed__AdminPassword = $envValues['SEED_ADMIN_PASSWORD']
$env:Seed__EngineerPassword = $envValues['SEED_ENGINEER_PASSWORD']
$env:Seed__ViewerPassword = $envValues['SEED_VIEWER_PASSWORD']
$env:Gateway__SecurityAccessSecret = $envValues['SECURITY_ACCESS_SECRET']

dotnet build AutoSphere.sln -v quiet
$api = Start-Process dotnet -ArgumentList 'run', '--project', 'src/backend/AutoSphere.Api', '--no-build' -PassThru -NoNewWindow
try {
    Start-Sleep -Seconds 8
    Write-Host 'API: http://localhost:5080/swagger - starting the vehicle gateway (Ctrl+C stops it)'
    dotnet run --project src/gateway/AutoSphere.VehicleGateway --no-build
}
finally {
    Stop-Process -Id $api.Id -ErrorAction SilentlyContinue
}
