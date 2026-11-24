# Installs (or reinstalls) Claude Usage for Windows from the latest GitHub release.
#
#   Install:  irm https://raw.githubusercontent.com/JoolBaby/claude-usage-widget/main/install.ps1 | iex
#
# After this, the app keeps itself up to date. To remove it, right-click its icon by the
# clock and choose "Uninstall Claude Usage…".

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # makes the download much faster
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

$repo = 'JoolBaby/claude-usage-widget'
$dir  = Join-Path $env:LOCALAPPDATA 'Programs\ClaudeUsage'
$exe  = Join-Path $dir 'ClaudeUsage.exe'
$tmp  = Join-Path $dir 'ClaudeUsage.download'

Write-Host ''
Write-Host '==> Downloading the latest Claude Usage (about 60 MB)...'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/$repo/releases/latest/download/ClaudeUsage-Windows.exe" -OutFile $tmp
if ((Get-Item $tmp).Length -lt 1MB) {
    Remove-Item $tmp -Force
    throw 'The download did not look right. Please try again in a few minutes.'
}

Write-Host '==> Installing'
Get-Process ClaudeUsage -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
Move-Item -Force $tmp $exe
Unblock-File $exe -ErrorAction SilentlyContinue

Write-Host '==> Setting it to start with Windows'
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'ClaudeUsage' -Value "`"$exe`""

Write-Host '==> Adding it to the Start menu'
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Claude Usage.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.Description = 'Claude Usage'
$shortcut.Save()

Write-Host '==> Starting Claude Usage'
Start-Process $exe

Write-Host ''
Write-Host 'Done! Claude Usage is running.' -ForegroundColor Green
Write-Host '  * A "Sign in to Claude" window opens. Sign in once and it stays signed in.'
Write-Host '  * Your usage appears as a small colored number in the taskbar, next to the clock.'
Write-Host '    If you do not see it, click the small up-arrow (^) next to the clock.'
Write-Host '  * Click the number for details. Right-click it for options.'
Write-Host '  * It updates itself automatically from now on. You can close this window.'
Write-Host ''
