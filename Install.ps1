$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskExe = Join-Path $taskRoot 'CodexTokenHud.exe'
if (-not (Test-Path -LiteralPath $taskExe)) { & (Join-Path $taskRoot 'Build.ps1') }
$taskSetup = Start-Process -FilePath $taskExe -ArgumentList '--enable-app-follow' -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru
if (-not $taskSetup.WaitForExit(35000)) { throw '启用随应用启动超时。' }
if ($taskSetup.ExitCode -ne 0) { throw '无法启用随应用启动。请检查权限，或先关闭另一副本的自动启动。' }
$taskShell = New-Object -ComObject WScript.Shell
$taskLink = $taskShell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'FOX CODEX HUD.lnk'))
$taskLink.TargetPath = $taskExe
$taskLink.WorkingDirectory = $taskRoot
$taskLink.Description = 'FOX CODEX HUD：额度、响应速度、网络延迟与动态异常提示'
$taskLink.WindowStyle = 1
$taskLink.Save()
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskLink) | Out-Null
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskShell) | Out-Null
Write-Output '已创建 FOX CODEX HUD 桌面入口并启用登录后常驻。可通过托盘管理显示器、透明度和点击穿透。'
