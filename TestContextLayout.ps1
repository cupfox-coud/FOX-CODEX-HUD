param([string]$PythonExecutable = 'pythonw.exe', [switch]$Probe, [switch]$Showcase)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskCsc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskWpf = Join-Path (Split-Path -Parent $taskCsc) 'WPF'
$taskOut = Join-Path $taskRoot 'build\tests\ContextLayoutTests.exe'
New-Item -ItemType Directory -Path (Split-Path -Parent $taskOut) -Force | Out-Null
$taskSources = @('CodexTokenHud.cs','RuntimeConfiguration.cs','ActivityDisplay.cs','CornerDisplay.cs','SpeedDisplay.cs','SpeedInstrument.cs','ResetRadarDisplay.cs','ResetNotifications.cs','tests\CornerHudTests.cs','tests\ContextLayoutTests.cs') | ForEach-Object { Join-Path $taskRoot $_ }
& $taskCsc '/nologo' '/target:exe' '/define:HUD_TEST' '/main:ContextLayoutTests' "/out:$taskOut" '/r:System.Windows.Forms.dll' '/r:System.Drawing.dll' '/r:System.Web.Extensions.dll' '/r:Microsoft.CSharp.dll' "/r:$taskWpf\UIAutomationClient.dll" "/r:$taskWpf\UIAutomationTypes.dll" "/r:$taskWpf\WindowsBase.dll" @taskSources
if ($LASTEXITCODE -ne 0) { throw 'Context layout test compilation failed.' }
& $taskOut $taskRoot (Get-Command $PythonExecutable).Source $(if ($Probe) {'probe'} elseif ($Showcase) {'showcase'} else {'render'})
if ($LASTEXITCODE -ne 0) { throw 'Context layout tests failed.' }
