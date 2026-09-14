param(
    [string]$ServerInstance = 'Server\MSSQLServer',
    [string]$DatabaseName = 'Restaurant',
    [string]$SqlUser = 'sa',
    [string]$SqlPassword = '123456',
    [string]$AppSettingsPath,
    [switch]$ConfigureOnly
)

$ErrorActionPreference = 'Stop'
$server = $ServerInstance
$database = $DatabaseName
$user = $SqlUser
$password = $SqlPassword
$schemaPath = Join-Path $PSScriptRoot 'MPESAscript.sql'

try {
    Add-Type -AssemblyName System.Data
    if (-not [string]::IsNullOrWhiteSpace($AppSettingsPath)) {
        $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
        $builder['Data Source'] = $server
        $builder['Initial Catalog'] = $database
        $builder['User ID'] = $user
        $builder['Password'] = $password
        $builder['Integrated Security'] = $false
        $builder['TrustServerCertificate'] = $true
        $builder['Connect Timeout'] = 10

        $configuration = Get-Content -Raw -LiteralPath $AppSettingsPath | ConvertFrom-Json
        if ($null -eq $configuration.ConnectionStrings) {
            $configuration | Add-Member -MemberType NoteProperty -Name ConnectionStrings -Value ([pscustomobject]@{})
        }
        $configuration.ConnectionStrings.Restaurant = $builder.ConnectionString
        $configuration | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $AppSettingsPath -Encoding UTF8
        Write-Host "Application database connection saved."
    }

    if ($ConfigureOnly) { exit 0 }

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
