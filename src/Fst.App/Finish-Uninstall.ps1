param([Parameter(Mandatory=$true)][string]$PlanFile)
$ErrorActionPreference='Stop'
try {
    $fstPlan=Get-Content -LiteralPath $PlanFile -Raw | ConvertFrom-Json
    $fstData=[IO.Path]::GetFullPath([string]$fstPlan.DataRoot).TrimEnd('\')
    $fstExpected=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'FST')).TrimEnd('\')
    if(-not $fstData.Equals($fstExpected,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid FST data path'}
    $fstExe=[IO.Path]::GetFullPath([string]$fstPlan.Executable)
    if([IO.Path]::GetFileName($fstExe) -ne 'FST.exe'){throw 'Invalid FST executable'}
    $fstProcess=Get-Process -Id ([int]$fstPlan.ProcessId) -ErrorAction SilentlyContinue
    if($fstProcess -and -not $fstProcess.WaitForExit(30000)){throw 'FST did not exit'}
    if(-not $fstPlan.TestMode){Remove-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name FST -ErrorAction SilentlyContinue}
    # Remove the injected store card after the injector process has stopped.
    if(-not $fstPlan.TestMode){try {
        $fstTabs=Invoke-RestMethod -Uri 'http://127.0.0.1:8080/json' -TimeoutSec 2
        foreach($fstTab in $fstTabs){
            $fstUri=$null
            if(-not [Uri]::TryCreate([string]$fstTab.url,[UriKind]::Absolute,[ref]$fstUri) -or $fstUri.Host -ne 'store.steampowered.com'){continue}
            $fstWsUri=[Uri]$fstTab.webSocketDebuggerUrl
            if(-not $fstWsUri.IsLoopback -or $fstWsUri.Scheme -ne 'ws'){continue}
            $fstSocket=[Net.WebSockets.ClientWebSocket]::new();$fstTimeout=[Threading.CancellationTokenSource]::new(3000)
            try{
                $fstSocket.ConnectAsync($fstWsUri,$fstTimeout.Token).GetAwaiter().GetResult()
                $fstPayload=[Text.Encoding]::UTF8.GetBytes(' {"id":1,"method":"Runtime.evaluate","params":{"expression":"window.__FSTStore?.dispose?.();delete window.__FSTStore;delete window.__FST"}}')
                $fstSegment=[ArraySegment[byte]]::new($fstPayload)
                $fstSocket.SendAsync($fstSegment,[Net.WebSockets.WebSocketMessageType]::Text,$true,$fstTimeout.Token).GetAwaiter().GetResult()
                $fstBuffer=[byte[]]::new(8192);$null=$fstSocket.ReceiveAsync([ArraySegment[byte]]::new($fstBuffer),$fstTimeout.Token).GetAwaiter().GetResult()
            }finally{$fstSocket.Dispose();$fstTimeout.Dispose()}
        }
    }catch{}}
    $fstMarkerRecord=Join-Path $fstData 'debug-marker-created.txt'
    if(Test-Path -LiteralPath $fstMarkerRecord){
        $fstMarker=(Get-Content -LiteralPath $fstMarkerRecord -Raw).Trim()
        if([IO.Path]::GetFileName($fstMarker) -eq '.cef-enable-remote-debugging' -and (Test-Path -LiteralPath $fstMarker -PathType Leaf)){
            $fstMarkerInfo=Get-Item -LiteralPath $fstMarker -Force
            if($fstMarkerInfo.Length -eq 0 -and -not ($fstMarkerInfo.Attributes -band [IO.FileAttributes]::ReparsePoint)){Remove-Item -LiteralPath $fstMarker -Force}
        }
    }
    if(Test-Path -LiteralPath $fstData){
        $fstDataInfo=Get-Item -LiteralPath $fstData -Force
        if(($fstDataInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -or @(Get-ChildItem -LiteralPath $fstData -Recurse -Force -Attributes ReparsePoint).Count){throw 'FST data contains links; preserving files'}
        Remove-Item -LiteralPath $fstData -Recurse -Force
    }
    for($fstTry=0;$fstTry -lt 20;$fstTry++){
        try{if(Test-Path -LiteralPath $fstExe){Remove-Item -LiteralPath $fstExe -Force};break}catch{Start-Sleep -Milliseconds 300}
    }
    if(Test-Path -LiteralPath $fstExe){throw 'Could not remove FST.exe'}
    $fstPdb=[IO.Path]::ChangeExtension($fstExe,'.pdb');if(Test-Path -LiteralPath $fstPdb){Remove-Item -LiteralPath $fstPdb -Force}
    $fstCorePdb=Join-Path ([IO.Path]::GetDirectoryName($fstExe)) 'Fst.Core.pdb';if(Test-Path -LiteralPath $fstCorePdb){Remove-Item -LiteralPath $fstCorePdb -Force}
    if(-not $fstPlan.TestMode){
        $fstExtracted=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) '.net\FST'))
        if(Test-Path -LiteralPath $fstExtracted){
            $fstExtractedInfo=Get-Item -LiteralPath $fstExtracted -Force
            if(-not ($fstExtractedInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -and @(Get-ChildItem -LiteralPath $fstExtracted -Recurse -Force -Attributes ReparsePoint).Count -eq 0){Remove-Item -LiteralPath $fstExtracted -Recurse -Force}
        }
    }
    Remove-Item -LiteralPath $PlanFile -Force
    Remove-Item -LiteralPath $PSCommandPath -Force
}catch{
    $_.Exception.Message | Set-Content -LiteralPath ($PlanFile+'.error.txt')
}
