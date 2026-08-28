 $ErrorActionPreference = 'Stop'
 $installDir = Join-Path $env:ProgramFiles 'MPesa'
 $sourceDir = Join-Path $PSScriptRoot 'publish'

 try {
     if (-not (Test-Path (Join-Path $sourceDir 'MPesa.exe'))) {
         throw "publish\MPesa.exe was not found. Keep the publish folder beside this script."
     }

     New-Item -ItemType Directory -Path $installDir -Force | Out-Null
     Copy-Item (Join-Path $sourceDir '*') $installDir -Force
     Copy-Item (Join-Path $PSScriptRoot 'MPESAscript.sql') $installDir -Force
    Copy-Item (Join-Path $PSScriptRoot 'Setup-Database.ps1') $installDir -Force
     Copy-Item (Join-Path $PSScriptRoot 'README.md') $installDir -Force

$shell = New-Object -ComObject WScript.Shell
$startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\MPesa'
New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
foreach ($shortcutPath in @(
    (Join-Path $startMenu 'M-Pesa Message Puller.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'M-Pesa Message Puller.lnk')
)) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $installDir 'MPesa.exe'
    $shortcut.WorkingDirectory = $installDir
    $shortcut.Description = 'M-Pesa Message Puller'
    $shortcut.Save()
}

     Write-Host "Installed M-Pesa Message Puller to $installDir"
     Write-Host "Edit $installDir\appsettings.json before starting the application."
     $runDatabaseSetup = Read-Host "Create/update the Restaurant database now? (Y/N)"
     if ($runDatabaseSetup -match '^(Y|y)$') {
         & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $installDir 'Setup-Database.ps1')
         if ($LASTEXITCODE -ne 0) { Write-Host "Database setup did not complete. Run Setup-Database.ps1 later." -ForegroundColor Yellow }
     }
     $runAtStartup = Read-Host "Start M-Pesa automatically with Windows? (Y/N)"
     if ($runAtStartup -match '^(Y|y)$') {
         New-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Force | Out-Null
         Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'MPesaMessagePuller' -Value (Join-Path $installDir 'MPesa.exe')
         Write-Host "Windows startup enabled."
     }
 }
 catch {
     Write-Host "Installation failed: $($_.Exception.Message)" -ForegroundColor Red
     exit 1
 }
