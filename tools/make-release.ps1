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

Write-Host "`nBoth built. Attach them to the release for the matching tag."
