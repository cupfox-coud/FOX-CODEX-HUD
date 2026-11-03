[CmdletBinding()]
param([string]$PythonExecutable = 'python.exe')
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskPython = (Get-Command $PythonExecutable -ErrorAction Stop).Source
$taskPythonw = Join-Path (Split-Path -Parent $taskPython) 'pythonw.exe'
$taskOutput = Join-Path $taskRoot 'build\tests'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskCsc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $taskCsc)) { $taskCsc = Join-Path $env:WINDIR 'Microsoft.NET\FrameworkArm64\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $taskCsc)) { $taskCsc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$taskWpf = Join-Path (Split-Path -Parent $taskCsc) 'WPF'
$taskExe = Join-Path $taskOutput 'HoverOpacityTests.exe'
$taskArgs = @('/nologo', '/target:exe', '/platform:anycpu', '/define:HUD_TEST', '/main:HoverOpacityTests',
    "/out:$taskExe", '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll', '/r:Microsoft.CSharp.dll',
    "/r:$taskWpf\UIAutomationClient.dll", "/r:$taskWpf\UIAutomationTypes.dll", "/r:$taskWpf\WindowsBase.dll",
    "$taskRoot\CodexTokenHud.cs", "$taskRoot\RuntimeConfiguration.cs", "$taskRoot\ActivityDisplay.cs", "$taskRoot\CornerDisplay.cs",
    "$taskRoot\SpeedDisplay.cs", "$taskRoot\SpeedInstrument.cs", "$taskRoot\ResetRadarDisplay.cs", "$taskRoot\ResetNotifications.cs",
    "$taskRoot\tests\HoverOpacityTests.cs")
& $taskCsc @taskArgs
if ($LASTEXITCODE -ne 0) { throw 'Hover test compilation failed.' }
& $taskExe $taskRoot $taskPythonw
if ($LASTEXITCODE -ne 0) { throw 'Hover opacity checks failed.' }
