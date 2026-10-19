$ErrorActionPreference='Stop'
$fstTestRoot=Join-Path ([IO.Path]::GetTempPath()) ('fst-uninstall-test-'+[Guid]::NewGuid().ToString('N'))
$fstTestData=Join-Path $fstTestRoot 'FST'
[IO.Directory]::CreateDirectory($fstTestData)|Out-Null
$fstTestExe=Join-Path $fstTestRoot 'FST.exe'
[IO.File]::WriteAllText($fstTestExe,'test executable placeholder')
[IO.File]::WriteAllText((Join-Path $fstTestData 'settings.json'),'{}')
[IO.File]::WriteAllText((Join-Path $fstTestRoot 'preserve.txt'),'keep')
$fstTestScript=Join-Path $fstTestRoot 'cleanup.ps1'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\src\Fst.App\Finish-Uninstall.ps1') -Destination $fstTestScript
$fstTestPlan=Join-Path $fstTestRoot 'plan.json'
@{ProcessId=2147483647;Executable=$fstTestExe;DataRoot=$fstTestData;TestMode=$true}|ConvertTo-Json|Set-Content -LiteralPath $fstTestPlan
$fstTestStart=[Diagnostics.ProcessStartInfo]::new('powershell.exe')
$fstTestStart.UseShellExecute=$false;$fstTestStart.CreateNoWindow=$true
$fstTestStart.Arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$fstTestScript+'" -PlanFile "'+$fstTestPlan+'"'
$fstTestStart.Environment['LOCALAPPDATA']=$fstTestRoot
$fstTestProcess=[Diagnostics.Process]::Start($fstTestStart);$fstTestProcess.WaitForExit()
if((Test-Path -LiteralPath $fstTestExe) -or (Test-Path -LiteralPath $fstTestData) -or -not (Test-Path -LiteralPath (Join-Path $fstTestRoot 'preserve.txt'))){throw ('Uninstall sandbox failed: '+$fstTestRoot)}
Write-Output ('PASS uninstaller removed only sandbox FST files: '+$fstTestRoot)
