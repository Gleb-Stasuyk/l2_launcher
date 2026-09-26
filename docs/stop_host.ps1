# Stops the local test update host (python http.server on 127.0.0.1:8765)
$root = 'E:\l2_launcher_testhost'
$pidFile = Join-Path $root 'host.pid'
if (Test-Path $pidFile) { Stop-Process -Id (Get-Content $pidFile) -Force -ErrorAction SilentlyContinue; Remove-Item $pidFile -Force }
Get-CimInstance Win32_Process -Filter "Name like 'python%'" |
    Where-Object { $_.CommandLine -like '*http.server*8765*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; Write-Output "stopped PID $($_.ProcessId)" }
Write-Output 'Test host stopped.'
