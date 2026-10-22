[CmdletBinding()]
param([string]$PythonExecutable, [switch]$RenderOnly)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskOutput = Join-Path $taskRoot 'build\tests'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskCsc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskWpf = Join-Path (Split-Path -Parent $taskCsc) 'WPF'
$taskExe = Join-Path $taskOutput 'CornerHudTests.exe'
$taskArgs = @('/nologo', '/target:exe', '/platform:anycpu', '/main:CornerHudTests',
    "/out:$taskExe", '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll', '/r:Microsoft.CSharp.dll',
    "/r:$taskWpf\UIAutomationClient.dll", "/r:$taskWpf\UIAutomationTypes.dll", "/r:$taskWpf\WindowsBase.dll",
    "$taskRoot\CodexTokenHud.cs", "$taskRoot\RuntimeConfiguration.cs", "$taskRoot\ActivityDisplay.cs", "$taskRoot\CornerDisplay.cs", "$taskRoot\ResetRadarDisplay.cs", "$taskRoot\ResetNotifications.cs", "$taskRoot\tests\CornerHudTests.cs")
& $taskCsc @taskArgs
if ($LASTEXITCODE -ne 0) { throw 'Corner tests failed to compile.' }
if ($RenderOnly) { & $taskExe $taskRoot $PythonExecutable '--render-only' }
else { & $taskExe $taskRoot $PythonExecutable }
if ($LASTEXITCODE -ne 0) { throw 'Corner HUD verification failed.' }
$taskActivityExe = Join-Path $taskOutput 'ActivityDisplayTests.exe'
$taskActivityArgs = $taskArgs | ForEach-Object {
    if ($_ -eq '/main:CornerHudTests') { '/main:ActivityDisplayTests' }
    elseif ($_ -eq "/out:$taskExe") { "/out:$taskActivityExe" }
    elseif ($_ -eq "$taskRoot\tests\CornerHudTests.cs") { "$taskRoot\tests\ActivityDisplayTests.cs" }
    else { $_ }
}
& $taskCsc @taskActivityArgs
if ($LASTEXITCODE -ne 0) { throw 'Activity tests failed to compile.' }
& $taskActivityExe
if ($LASTEXITCODE -ne 0) { throw 'Activity verification failed.' }
foreach ($taskTestName in @('ResetRadarDisplayTests', 'ResetNotificationTests')) {
    $taskResetExe = Join-Path $taskOutput ($taskTestName + '.exe')
    $taskResetArgs = $taskArgs | ForEach-Object {
        if ($_ -eq '/main:CornerHudTests') { '/main:' + $taskTestName }
        elseif ($_ -eq "/out:$taskExe") { "/out:$taskResetExe" }
        elseif ($_ -eq "$taskRoot\tests\CornerHudTests.cs") { "$taskRoot\tests\$taskTestName.cs" }
        else { $_ }
    }
    & $taskCsc @taskResetArgs
    if ($LASTEXITCODE -ne 0) { throw "$taskTestName failed to compile." }
    & $taskResetExe
    if ($LASTEXITCODE -ne 0) { throw "$taskTestName verification failed." }
}
