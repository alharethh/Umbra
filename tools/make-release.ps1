# Builds the release downloads.
#
# Two flavours, and the difference matters to whoever downloads them:
#
#   framework-dependent  1.2 MB, needs the .NET 10 Desktop Runtime installed.
#   self-contained       ~60 MB zipped, carries its own runtime and needs nothing.
#
# The self-contained one only works because Umbra.Shell.csproj carries a FrameworkReference to
# Microsoft.WindowsDesktop.App.WindowsForms: ManagedShell's tasks service uses Windows Forms, and a
# framework-dependent build gets that free from the shared framework while a self-contained build
# publishes only what is referenced. Without it this flavour dies on the first ShellManager.

param(
    [string]$Version = "0.1.0",
    [string]$Repo = "C:\src\Umbra"
)

$ErrorActionPreference = "Stop"

$out = Join-Path $Repo "artifacts\release"
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $out | Out-Null

$project = Join-Path $Repo "src\Umbra.Shell"

$flavours = @(
    @{ Name = "win-x64";                Args = @() },
    @{ Name = "win-x64-selfcontained";  Args = @("-r", "win-x64", "--self-contained", "true") }
)

foreach ($flavour in $flavours) {
    $folder = Join-Path $out "Umbra-$Version-$($flavour.Name)"

    & dotnet publish $project -c Release -o $folder --nologo -v q @($flavour.Args)
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $($flavour.Name)" }

    # Symbols and XML docs are for building against, not for running.
    Remove-Item (Join-Path $folder "*.pdb"), (Join-Path $folder "*.xml") -ErrorAction SilentlyContinue

    Copy-Item (Join-Path $Repo "LICENSE") $folder
    Copy-Item (Join-Path $Repo "THIRD-PARTY-NOTICES.md") $folder
    Copy-Item (Join-Path $Repo "packaging\README.txt") $folder

    $zip = "$folder.zip"
    Compress-Archive -Path $folder -DestinationPath $zip -Force

    "{0,-34} {1,7:N1} MB" -f (Split-Path $zip -Leaf), ((Get-Item $zip).Length / 1MB)
}

Write-Host "`nBoth built. Attach them to the release for the matching tag."
