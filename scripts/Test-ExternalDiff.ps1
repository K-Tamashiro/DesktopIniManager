param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\bin\Debug\net10.0-windows\win-x64\DesktopIniManager.exe')
)
$ErrorActionPreference = 'Stop'
$held = @()
$viewers = @()
$fixture = Join-Path $PSScriptRoot ('..\Tests\bin\external-diff-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
$left = Join-Path $fixture 'source file.txt'
$right = Join-Path $fixture 'target file.txt'
[IO.File]::WriteAllText($left, "common`nsource")
[IO.File]::WriteAllText($right, "common`ntarget")
try {
    # Hold every free slot; slots held by running DIM instances stay untouched.
    foreach ($slot in 1..5) {
        $mutex = [Threading.Mutex]::new($false, "Local\DesktopIniManager-dim-$slot")
        try { $acquired = $mutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $acquired = $true }
        if ($acquired) { $held += $mutex } else { $mutex.Dispose() }
    }
    foreach ($iteration in 1..2) {
        $viewer = Start-Process -FilePath $Executable -ArgumentList @('-diff', ('"' + $left + '"'), ('"' + $right + '"')) -WindowStyle Hidden -PassThru
        $viewers += $viewer
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            Start-Sleep -Milliseconds 200
            $viewer.Refresh()
        } while (!$viewer.HasExited -and $viewer.MainWindowTitle -notlike '*source file.txt*target file.txt*' -and [DateTime]::UtcNow -lt $deadline)
        if ($viewer.HasExited -or $viewer.MainWindowTitle -notlike '*source file.txt*target file.txt*') {
            throw "Standalone viewer failed: $($viewer.MainWindowTitle)"
        }
    }
    Write-Output 'PASS two independent DIFF VIEW processes open while all five slots are occupied; paths with spaces are preserved.'
    foreach ($viewer in $viewers) {
        if (!$viewer.CloseMainWindow() -or !$viewer.WaitForExit(10000)) { throw 'Viewer did not exit after closing.' }
        if ($viewer.ExitCode -ne 0) { throw "Unexpected exit code: $($viewer.ExitCode)" }
    }
    Write-Output 'PASS closing each viewer exits its process with code 0.'
}
finally {
    foreach ($viewer in $viewers) {
        if (!$viewer.HasExited) { $viewer.Kill() }
        $viewer.Dispose()
    }
    foreach ($mutex in $held) { $mutex.ReleaseMutex(); $mutex.Dispose() }
}
