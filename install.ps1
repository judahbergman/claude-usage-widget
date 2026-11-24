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
Write-Host '==> Downloading the latest Claude Usage (about 70 MB)...'
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

# First install only: explain the sign-in step before the sign-in window opens.
if (-not (Test-Path (Join-Path $env:LOCALAPPDATA 'ClaudeUsage\session.dat'))) {
    Write-Host ''
    Write-Host '==================================================================' -ForegroundColor Yellow
    Write-Host '  NEXT: SIGN IN (please read before continuing)' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '  A "Sign in to Claude" window will open. Sign in with your own'
    Write-Host '  Claude account (the one you use for work).'
    Write-Host ''
    Write-Host '  If you sign in with your EMAIL:' -ForegroundColor Yellow
    Write-Host '    1. Type your email and continue.'
    Write-Host "    2. Open the email from Claude. DON'T click the link in it."
    Write-Host '    3. Right-click the link and choose "Copy link".'
    Write-Host '    4. In the sign-in window, click "Paste Link" at the top.'
    Write-Host ''
    Write-Host "  The window closes by itself when you're signed in."
    Write-Host '==================================================================' -ForegroundColor Yellow
    Write-Host ''
    try { Read-Host 'Press Enter to open the sign-in window' | Out-Null } catch { }
}

Write-Host '==> Starting Claude Usage'
Start-Process $exe

Write-Host ''
Write-Host 'Done! Claude Usage is running.' -ForegroundColor Green
Write-Host '  * Sign in once in the "Sign in to Claude" window and it stays signed in.'
Write-Host '  * Your usage appears as a small colored number in the taskbar, next to the clock.'
Write-Host '    If you do not see it, click the small up-arrow (^) next to the clock.'
Write-Host '  * Click the number for details. Right-click it for options.'
Write-Host '  * It updates itself automatically from now on. You can close this window.'
Write-Host ''
