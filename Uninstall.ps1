$ErrorActionPreference = 'Stop'
$taskExe = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'CodexTokenHud.exe'))
if (Test-Path -LiteralPath $taskExe) {
    $taskSetup = Start-Process -FilePath $taskExe -ArgumentList '--disable-app-follow' -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru
    if (-not $taskSetup.WaitForExit(35000)) { throw '关闭随应用启动超时。' }
    if ($taskSetup.ExitCode -ne 0) { throw '无法关闭本副本的随应用启动，未移除其他副本的设置。' }
}
& (Join-Path $PSScriptRoot 'Stop.ps1')
$taskLinkPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'FOX CODEX HUD.lnk'
if (Test-Path -LiteralPath $taskLinkPath) {
    $taskShell = New-Object -ComObject WScript.Shell
    $taskLink = $taskShell.CreateShortcut($taskLinkPath)
    if ($taskLink.TargetPath -eq $taskExe) { Remove-Item -LiteralPath $taskLinkPath }
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskLink) | Out-Null
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskShell) | Out-Null
}
Write-Output '状态条已停止，启动任务和桌面入口已移除。源码与布局配置已保留。'
