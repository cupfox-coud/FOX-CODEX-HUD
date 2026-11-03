$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskExe = Join-Path $taskRoot 'CodexTokenHud.exe'
if (-not (Test-Path -LiteralPath $taskExe)) { & (Join-Path $taskRoot 'Build.ps1') }
$taskSetup = Start-Process -FilePath $taskExe -ArgumentList '--enable-app-follow' -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru
if (-not $taskSetup.WaitForExit(35000)) { throw '启用随应用启动超时。' }
if ($taskSetup.ExitCode -ne 0) { throw '无法启用随应用启动。请检查权限，或先关闭另一副本的自动启动。' }
$taskShell = New-Object -ComObject WScript.Shell
try {
    foreach ($taskFolder in @('Desktop', 'Programs')) {
        $taskShortcutDirectory = [Environment]::GetFolderPath($taskFolder)
        if ([string]::IsNullOrWhiteSpace($taskShortcutDirectory)) { throw ('无法定位快捷方式目录：' + $taskFolder) }
        New-Item -ItemType Directory -Path $taskShortcutDirectory -Force | Out-Null
        $taskLink = $taskShell.CreateShortcut((Join-Path $taskShortcutDirectory 'FOX CODEX HUD.lnk'))
        try {
            $taskLink.TargetPath = $taskExe
            $taskLink.WorkingDirectory = $taskRoot
            $taskLink.Description = 'FOX CODEX HUD：额度、响应速度、网络延迟与动态异常提示'
            $taskLink.IconLocation = $taskExe + ',0'
            $taskLink.WindowStyle = 1
            $taskLink.Save()
        } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskLink) | Out-Null }
    }
} finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskShell) | Out-Null }
Write-Output '已创建 FOX CODEX HUD 桌面和开始菜单入口，并启用登录后常驻。按 Windows 键搜索 FOX CODEX HUD 即可打开；托盘可管理显示器、透明度和点击穿透。'
