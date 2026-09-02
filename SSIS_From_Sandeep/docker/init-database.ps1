<#
.SYNOPSIS
    Starts the SQL Server 2022 container (if not already running) and applies the
    SSISDemo schema + seed scripts from ..\sql\ using sqlcmd inside the container.

.EXAMPLE
    .\init-database.ps1
#>

$ErrorActionPreference = "Stop"

$composeFile = Join-Path $PSScriptRoot "docker-compose.yml"
$sqlDir      = Join-Path $PSScriptRoot "..\sql"
$container   = "ssisdemo-sqlserver"
$saPassword  = "YourStrong!Passw0rd123"

Write-Host "Starting SQL Server 2022 container via docker compose..." -ForegroundColor Cyan
docker compose -f $composeFile up -d

Write-Host "Waiting for SQL Server to report healthy (this can take ~30-60s on first start)..." -ForegroundColor Cyan
$maxWaitSeconds = 180
$elapsed = 0
while ($true) {
    $status = docker inspect --format='{{.State.Health.Status}}' $container 2>$null
    if ($status -eq "healthy") { break }
    if ($elapsed -ge $maxWaitSeconds) {
        throw "SQL Server container did not become healthy within $maxWaitSeconds seconds. Check 'docker logs $container'."
    }
    Start-Sleep -Seconds 5
    $elapsed += 5
    Write-Host "  ...still waiting ($elapsed s)"
}
Write-Host "SQL Server is healthy." -ForegroundColor Green

function Invoke-SqlFile {
    param([string]$Path)
    $fileName = Split-Path $Path -Leaf
    Write-Host "Applying $fileName ..." -ForegroundColor Cyan

    # Piping through stdin re-introduces a BOM on Windows; copy the file into
    # the container (BOM-stripped) and run sqlcmd against it there instead.
    $text = [System.IO.File]::ReadAllText($Path).TrimStart([char]0xFEFF)
    $tempPath = Join-Path $env:TEMP $fileName
    [System.IO.File]::WriteAllText($tempPath, $text, (New-Object System.Text.UTF8Encoding($false)))

    docker cp $tempPath "${container}:/tmp/$fileName" | Out-Null
    docker exec $container /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $saPassword -C -b -i "/tmp/$fileName"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed applying $fileName (sqlcmd exit code $LASTEXITCODE)"
    }
    Remove-Item $tempPath -Force
}

Invoke-SqlFile (Join-Path $sqlDir "01_create_database.sql")
Invoke-SqlFile (Join-Path $sqlDir "02_create_tables.sql")
Invoke-SqlFile (Join-Path $sqlDir "03_seed_data.sql")

Write-Host "`nSSISDemo database is ready on localhost,1433 (login: sa / $saPassword)." -ForegroundColor Green
