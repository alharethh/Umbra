<#
.SYNOPSIS
    Rebuilds the debug bar and swaps it in for the running one, with nobody touching the mouse.

.DESCRIPTION
    1. Builds src\Umbra.Shell in Debug first, so a compile error stops here with the old bar still
       up rather than leaving the user with no bar at all.
    2. Asks the running bar to exit through Shell\DevControl.cs's event — the same clean path as
       its Exit menu item, so Explorer's taskbar is handed back properly. Debug builds only.
    3. Waits until the process has really gone: its windows disappear several seconds before it
       releases the single-instance lock, and a launch inside that gap is refused as a duplicate.
    4. Publishes into artifacts\Umbra-dev and starts it through explorer.exe, so it runs outside
       whatever container the caller is in and reads the user's real %LOCALAPPDATA%.

    A release bar (artifacts\Umbra, the download, the sign-in entry) has no exit event. If one is
    running the script says so and, with -WaitForManualExit, waits for it to be exited by hand
    once; every dev bar after that restarts by itself.

.PARAMETER WaitForManualExit
    Minutes to wait for a non-dev bar to be exited by hand. 0 fails immediately instead.
#>
param(
    [int]$WaitForManualExit = 0
)

$ErrorActionPreference = 'Stop'

$root    = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Umbra.Shell'
$out     = Join-Path $root 'artifacts\Umbra-dev'
$exe     = Join-Path $out 'Umbra.exe'

$lockName = 'Local\Umbra.SingleInstance'
$exitName = 'Local\Umbra.Dev.Exit'

function Test-BarRunning {
    $mutex = $null
    if ([Threading.Mutex]::TryOpenExisting($lockName, [ref]$mutex)) { $mutex.Dispose(); return $true }
    return $false
}

function Wait-Until([scriptblock]$Condition, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while (-not (& $Condition)) {
        if ((Get-Date) -gt $deadline) { return $false }
        Start-Sleep -Milliseconds 250
    }
    return $true
}

Write-Host 'Building (Debug)...'
& dotnet build $project -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed; the running bar was left alone.' }

if (Test-BarRunning) {
    $signal = $null
    if ([Threading.EventWaitHandle]::TryOpenExisting($exitName, [ref]$signal)) {
        [void]$signal.Set()
        $signal.Dispose()
        Write-Host 'Asked the running dev bar to exit.'
        $seconds = 30
    }
    elseif ($WaitForManualExit -gt 0) {
        Write-Host "A release bar is running and cannot be asked to exit. Right-click it -> Exit; waiting up to $WaitForManualExit min."
        $seconds = $WaitForManualExit * 60
    }
    else {
        throw 'A release bar is running and cannot be asked to exit. Exit it from its menu once, or pass -WaitForManualExit.'
    }

    if (-not (Wait-Until { -not (Test-BarRunning) } $seconds)) { throw 'The running bar did not exit.' }
    Write-Host 'Old bar has exited.'
}

# The lock is released a moment before the process finishes tearing down; the files stay locked
# until it has.
$unlocked = Wait-Until {
    if (-not (Test-Path $exe)) { return $true }
    try { $f = [IO.File]::Open($exe, 'Open', 'ReadWrite', 'None'); $f.Close(); return $true } catch { return $false }
} 15
if (-not $unlocked) { throw "$exe is still locked." }

& dotnet publish $project -c Debug -o $out --no-build --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed. No bar is running; start the last good one by hand.' }

Start-Process explorer.exe -ArgumentList "`"$exe`""

if (-not (Wait-Until { Test-BarRunning } 20)) { throw 'The new bar did not start. Check bring-up.log.' }

Write-Host ("Dev bar running, built {0:HH:mm:ss}." -f (Get-Item (Join-Path $out 'Umbra.dll')).LastWriteTime)
