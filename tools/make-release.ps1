# Builds the release downloads.
#
# Two of them, and the difference is only what the machine already has:
#
#   Umbra.exe        ~73 MB, one file, nothing to install. Carries its own .NET runtime.
#   ...win-x64.zip   ~1.2 MB, needs the .NET 10 Desktop Runtime installed.
#
# Both only work because Umbra.Shell.csproj carries a FrameworkReference to
# Microsoft.WindowsDesktop.App.WindowsForms. ManagedShell's tasks service uses Windows Forms; a
# framework-dependent build gets that free from the shared framework, while a self-contained one
# publishes only what is referenced. Without it the standalone build dies on the first ShellManager
# with a FileNotFoundException — which is what made single-file look impossible for an afternoon.
#
# The licences are embedded in the binary and written beside the settings on first run, because the
# single-file download has no files next to it to put them in.

param(
    [string]$Version = "0.1.0",
    [string]$Repo = "C:\src\Umbra"
)

$ErrorActionPreference = "Stop"

# Checked before anything is built rather than at the step that needs it. A bar running out of
# artifacts\Umbra holds Umbra.exe open, and being told that after three minutes of publishing is
# a worse way to find out than being told straight away.
#
# The folder is matched with its trailing separator. Without it, "artifacts\Umbra" is also a
# prefix of "artifacts\Umbra-dev", where the debug bar lives for the whole of development, so the
# script refused to run whenever anyone was working on the bar.
function Test-RunsFromFolder([string]$ProcessPath, [string]$Folder) {
    $prefix = $Folder.TrimEnd('\') + '\'
    return [bool]$ProcessPath -and $ProcessPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

$runnable = Join-Path $Repo "artifacts\Umbra"
$holding = @(Get-Process -ErrorAction SilentlyContinue |
    Where-Object { Test-RunsFromFolder $_.Path $runnable })

if ($holding.Count -gt 0) {
    $pid0 = $holding[0].Id
    throw "Umbra is running from artifacts\Umbra (pid $pid0). Exit it - right-click the bar, Exit - then run this again."
}

$out = Join-Path $Repo "artifacts\release"
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $out | Out-Null

$project = Join-Path $Repo "src\Umbra.Shell"

# --- the standalone one -------------------------------------------------------------------------
$standalone = Join-Path $out "standalone"

& dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $standalone --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "standalone publish failed" }

$exe = Join-Path $out "Umbra-$Version-win-x64.exe"
Move-Item (Join-Path $standalone "Umbra.exe") $exe -Force
Remove-Item -Recurse -Force $standalone

"{0,-38} {1,7:N1} MB" -f (Split-Path $exe -Leaf), ((Get-Item $exe).Length / 1MB)

# --- and the small one for anyone who has the runtime --------------------------------------------
$folder = Join-Path $out "Umbra-$Version-win-x64"

& dotnet publish $project -c Release -o $folder --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "framework-dependent publish failed" }

# Symbols and XML docs are for building against, not for running.
Remove-Item (Join-Path $folder "*.pdb"), (Join-Path $folder "*.xml") -ErrorAction SilentlyContinue

Copy-Item (Join-Path $Repo "LICENSE") $folder
Copy-Item (Join-Path $Repo "THIRD-PARTY-NOTICES.md") $folder
Copy-Item (Join-Path $Repo "packaging\README.txt") $folder

$zip = "$folder.zip"
Compress-Archive -Path $folder -DestinationPath $zip -Force
Remove-Item -Recurse -Force $folder

"{0,-38} {1,7:N1} MB" -f (Split-Path $zip -Leaf), ((Get-Item $zip).Length / 1MB)

# --- and the runnable copy, because it is the one anybody actually launches ----------------------
#
# artifacts\Umbra is the path the README and the project notes tell you to run, and this script used to
# leave it alone. So a release build produced two fresh downloads beside a stale Umbra.exe, and
# testing "the new build" ran the previous day's -- silently, because the exe is still there and
# still starts. That cost a round of "I tried it and it still doesn't work" against a fix that was
# fine. Same publish the docs describe, so the copy that gets launched is never older than the
# ones that get uploaded.
Remove-Item -Recurse -Force $runnable -ErrorAction SilentlyContinue

& dotnet publish $project -c Release -o $runnable --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "runnable publish failed" }

$stamp = (Get-Item (Join-Path $runnable "Umbra.exe")).LastWriteTime.ToString("HH:mm:ss")
"{0,-38} {1,7}" -f "artifacts\Umbra\Umbra.exe", $stamp

Write-Host "`nBoth downloads built, and artifacts\Umbra refreshed to match."
Write-Host "Attach the two in artifacts\release to the release for the matching tag."
