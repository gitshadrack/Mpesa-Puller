$ErrorActionPreference = 'Stop'
$server = 'Server\MSSQLServer'
$database = 'Restaurant'
$user = 'sa'
$password = '123456'
$schemaPath = Join-Path $PSScriptRoot 'MPESAscript.sql'

try {
    Add-Type -AssemblyName System.Data
    $masterConnection = New-Object System.Data.SqlClient.SqlConnection("Server=$server;Database=master;User ID=$user;Password=$password;TrustServerCertificate=True;")
    $masterConnection.Open()
    $createCommand = $masterConnection.CreateCommand()
    $createCommand.CommandText = "IF DB_ID(N'$database') IS NULL CREATE DATABASE [$database];"
    $createCommand.ExecuteNonQuery() | Out-Null
    $masterConnection.Close()
    $masterConnection.Dispose()

    $schema = Get-Content -Raw -Encoding Unicode $schemaPath
    $batches = [regex]::Split($schema, '(?im)^\s*GO\s*(?:--.*)?\s*$')
    $connection = New-Object System.Data.SqlClient.SqlConnection("Server=$server;Database=$database;User ID=$user;Password=$password;TrustServerCertificate=True;")
    $connection.Open()
    foreach ($batch in $batches) {
        if ([string]::IsNullOrWhiteSpace($batch)) { continue }
        $command = $connection.CreateCommand()
        $command.CommandText = $batch
        $command.CommandTimeout = 60
        $command.ExecuteNonQuery() | Out-Null
        $command.Dispose()
    }
    $connection.Close()
    $connection.Dispose()
    Write-Host "Restaurant database schema applied successfully."
    exit 0
}
catch {
    Write-Host "Database setup failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "If the tables already exist, the application can still run; otherwise verify SQL Server, TCP/IP, and the sa credentials." -ForegroundColor Yellow
    exit 1
}
