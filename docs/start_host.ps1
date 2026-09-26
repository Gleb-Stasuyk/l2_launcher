# Local test update host for L2Launcher. Listens ONLY on 127.0.0.1:8765, serves E:\l2_launcher_testhost\www
$root = 'E:\l2_launcher_testhost'
$pidFile = Join-Path $root 'host.pid'
if (Test-Path $pidFile) {
    $old = Get-Process -Id (Get-Content $pidFile) -ErrorAction SilentlyContinue
    if ($old) { Write-Output "Host already running (PID $($old.Id)) at http://127.0.0.1:8765/"; exit 0 }
}
$p = Start-Process -FilePath 'python' -ArgumentList @('-m','http.server','8765','--bind','127.0.0.1','--directory',(Join-Path $root 'www')) `
     -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $root 'host.log') -RedirectStandardOutput (Join-Path $root 'host.out.log')
$p.Id | Set-Content $pidFile
Start-Sleep -Seconds 2
Write-Output "Started test host PID $($p.Id): http://127.0.0.1:8765/manifest.json"
