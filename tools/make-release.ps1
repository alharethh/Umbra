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
#
# Every release is signed. SmartScreen's trust is a reputation counter keyed to the signing
# identity, so an unsigned release starts from zero and spends downloads that a signed one would
# have banked; a release that goes out unsigned by accident is worse than one that waits. So the
# real thing refuses to build without -CertificateThumbprint, and only a scratch -Out may skip it.
# With Certum's cloud certificate, SimplySign Desktop has to be connected while this runs: that is
# what puts the certificate in the CurrentUser store.

param(
    # The release's own version, MAJOR.MINOR.PATCH, e.g. 0.1.3. Required: it is stamped into the
    # binaries, and a default would quietly stamp the wrong one into any release that forgot it.
    [Parameter(Mandatory = $true)]
    [string]$Version,
    # The checkout this script sits in, so it builds whichever copy it was run from.
    [string]$Repo = (Split-Path $PSScriptRoot -Parent),
    # A scratch folder for the two downloads, to try the script without touching the real
    # artifacts\release or artifacts\Umbra. It must be missing or empty; a relative path is taken
    # from PowerShell's current location. Left out, the downloads go to artifacts\release, which
    # is emptied first, and artifacts\Umbra is refreshed to match.
    [string]$Out,
    # The thumbprint of the code-signing certificate in Cert:\CurrentUser\My. Required for a real
    # release; left out, the script lists the code-signing certificates it can see and stops.
    [string]$CertificateThumbprint,
    # Builds without signing. Only with -Out, to try the script without a certificate.
    [switch]$Unsigned,
    # Accepts a signature Windows does not trust, so the script can be tried end to end with a
    # self-signed certificate. Only with -Out: a release must verify as Valid.
    [switch]$AllowUntrusted
)

$ErrorActionPreference = "Stop"

# Certum's own timestamp server. A timestamped signature stays valid after the certificate expires;
# one without a timestamp dies with it, a year from now, on every copy anyone has downloaded.
$timestampServer = "http://time.certum.pl"

# A scratch -Out is refused unless it is missing or empty, rather than emptied the way
# artifacts\release is. Emptying is safe there because the script owns that folder; an -Out is
# whatever path somebody typed, and "-Out ." or "-Out $HOME\Downloads" must not be wiped. Deleting
# only the files the script makes was the other way, and lost: it would still overwrite a
# same-named download of the user's, and leaves their own files mixed in with the release's.
#
# Resolved once, here, against PowerShell's location. dotnet and the .NET file APIs resolve a
# relative path against the process directory instead, which PowerShell does not keep in step.
$scratch = [bool]$Out
if ($scratch) {
    $Out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
    if ((Test-Path -LiteralPath $Out) -and @(Get-ChildItem -LiteralPath $Out -Force).Count -gt 0) {
        throw "-Out '$Out' is not empty. Give a missing or empty folder; nothing was deleted."
    }
} else {
    $Out = Join-Path $Repo "artifacts\release"
}

# The version goes into every publish as -p:Version. Without it the exe said 1.0.0.0 in Explorer's
# Properties, in crash reports and in Defender submissions, whatever the release, so nobody could
# tell from the file which build they had.
#
# Version rather than FileVersion alone, because MSBuild derives the rest from it: FileVersion and
# AssemblyVersion become 0.1.3.0 (a Windows file version is always four parts, so the fourth is 0),
# and InformationalVersion -- the "Product version" line -- becomes 0.1.3 plus the commit it was
# built from. Setting FileVersion alone would leave the product version reading 1.0.0.
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must be MAJOR.MINOR.PATCH, e.g. 0.1.3 (got '$Version')."
}
$fileVersion = "$Version.0"
$versionArg = "-p:Version=$Version"

# --- the certificate, found before anything is built ---------------------------------------------
#
# Checked up front for the same reason the running bar is: three minutes of publishing is a poor way
# to learn the certificate is missing or SimplySign is disconnected.
if (($Unsigned -or $AllowUntrusted) -and -not $scratch) {
    throw "-Unsigned and -AllowUntrusted are for trying the script with -Out. A release is signed with a trusted certificate."
}

$certificate = $null
if (-not $Unsigned) {
    if (-not $CertificateThumbprint) {
        $found = @(Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue)
        $list = if ($found.Count -eq 0) { "  none. With Certum's cloud certificate, connect SimplySign Desktop first." }
                else { ($found | ForEach-Object { "  {0}  {1}  (until {2:yyyy-MM-dd})" -f $_.Thumbprint, $_.Subject, $_.NotAfter }) -join "`n" }
        throw "A release is signed. Pass -CertificateThumbprint. Code-signing certificates in CurrentUser\My:`n$list"
    }

    $certificate = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq ($CertificateThumbprint -replace '\s', '').ToUpperInvariant() }
    if (-not $certificate) { throw "No code-signing certificate with thumbprint $CertificateThumbprint in CurrentUser\My." }
    if (-not $certificate.HasPrivateKey) { throw "The certificate $($certificate.Subject) has no private key here. Is SimplySign Desktop connected?" }
    if ($certificate.NotAfter -lt (Get-Date)) { throw "The certificate $($certificate.Subject) expired on $($certificate.NotAfter.ToString('yyyy-MM-dd'))." }

    "{0,-38} {1}" -f "signing as", $certificate.Subject
}

# Signs one file and reads the signature back. Checked rather than trusted, like the version:
# Set-AuthenticodeSignature reports a failed timestamp as a status, not an exception, and a file
# signed without one looks perfect until the certificate expires.
#
# Set-AuthenticodeSignature goes through the same Windows crypto layer as signtool, so nothing has
# to be installed. Certum documents signtool; if SimplySign ever refuses this route, the Windows
# SDK's signtool with /sha1 <thumbprint> /fd sha256 /tr $timestampServer /td sha256 is the same
# signature.
function Invoke-Sign([string]$Path) {
    if (-not $certificate) { return }

    $result = Set-AuthenticodeSignature -LiteralPath $Path -Certificate $certificate `
        -HashAlgorithm SHA256 -TimestampServer $timestampServer
    $check = Get-AuthenticodeSignature -LiteralPath $Path
    $name = Split-Path $Path -Leaf

    if ($check.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
        throw "$name is not signed by $($certificate.Subject): $($result.StatusMessage)"
    }
    if (-not $check.TimeStamperCertificate) {
        throw "$name was signed but not timestamped. Is $timestampServer reachable?"
    }
    if ($check.Status -ne 'Valid' -and -not $AllowUntrusted) {
        throw "$name does not verify: $($check.Status), $($check.StatusMessage)"
    }
}

# Signs what in a folder is ours to sign: every exe and dll without a valid signature of its own.
# Microsoft's runtime files arrive signed and keep their signature. The rest, ManagedShell's
# included, carry ours, because Smart App Control judges every binary that loads, and an unsigned
# dll in a signed application is still an unsigned dll.
function Invoke-SignFolder([string]$Folder) {
    if (-not $certificate) { return }

    $signed = 0
    # Filtered by extension here, not with -Include, which -LiteralPath silently ignores: the first
    # version tried to sign Umbra.deps.json.
    $binaries = Get-ChildItem -LiteralPath $Folder -Recurse -File |
        Where-Object { $_.Extension -in '.exe', '.dll' }
    foreach ($file in $binaries) {
        if ((Get-AuthenticodeSignature -LiteralPath $file.FullName).Status -eq 'Valid') { continue }
        Invoke-Sign $file.FullName
        $signed++
    }
    "{0,-38} {1}" -f "  signed", "$signed file(s)"
}

# Reads the stamped version back off a built file. Checked rather than trusted: a publish that
# ignored the property would still succeed, and the file would still say 1.0.0.0.
function Assert-FileVersion([string]$Path) {
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    $product = ($info.ProductVersion -split '\+')[0]
    if ($info.FileVersion -ne $fileVersion -or $product -ne $Version) {
        throw "$(Split-Path $Path -Leaf) carries file version '$($info.FileVersion)' and product version '$($info.ProductVersion)', expected $fileVersion and $Version."
    }
    "{0,-38} {1}" -f "  version", $info.ProductVersion
}

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

if (-not $scratch -and $holding.Count -gt 0) {
    $pid0 = $holding[0].Id
    throw "Umbra is running from artifacts\Umbra (pid $pid0). Exit it - right-click the bar, Exit - then run this again."
}

$out = $Out
if (-not $scratch) { Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $out | Out-Null

$project = Join-Path $Repo "src\Umbra.Shell"

# --- the standalone one -------------------------------------------------------------------------
$standalone = Join-Path $out "standalone"

& dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    $versionArg `
    -o $standalone --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "standalone publish failed" }

$exe = Join-Path $out "Umbra-$Version-win-x64.exe"
Move-Item (Join-Path $standalone "Umbra.exe") $exe -Force
Remove-Item -Recurse -Force $standalone

"{0,-38} {1,7:N1} MB" -f (Split-Path $exe -Leaf), ((Get-Item $exe).Length / 1MB)
Assert-FileVersion $exe

# The bundle is signed as a whole. The assemblies inside it are extracted to memory, not disk, and
# carry no signatures of their own; signing them would mean signing before bundling.
Invoke-Sign $exe
if ($certificate) { "{0,-38} {1}" -f "  signed", "and timestamped" }

# --- and the small one for anyone who has the runtime --------------------------------------------
$folder = Join-Path $out "Umbra-$Version-win-x64"

& dotnet publish $project -c Release $versionArg -o $folder --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "framework-dependent publish failed" }

# Both files: the exe is only the apphost, which takes its version resource from the dll.
Assert-FileVersion (Join-Path $folder "Umbra.exe")
Assert-FileVersion (Join-Path $folder "Umbra.dll")

# Symbols and XML docs are for building against, not for running.
Remove-Item (Join-Path $folder "*.pdb"), (Join-Path $folder "*.xml") -ErrorAction SilentlyContinue

Invoke-SignFolder $folder

Copy-Item (Join-Path $Repo "LICENSE") $folder
Copy-Item (Join-Path $Repo "THIRD-PARTY-NOTICES.md") $folder
Copy-Item (Join-Path $Repo "packaging\README.txt") $folder

$zip = "$folder.zip"
Compress-Archive -Path $folder -DestinationPath $zip -Force
Remove-Item -Recurse -Force $folder

"{0,-38} {1,7:N1} MB" -f (Split-Path $zip -Leaf), ((Get-Item $zip).Length / 1MB)

# --- and the runnable copy, because it is the one anybody actually launches ----------------------
#
# artifacts\Umbra is the path the README tells you to run, and this script used to
# leave it alone. So a release build produced two fresh downloads beside a stale Umbra.exe, and
# testing "the new build" ran the previous day's -- silently, because the exe is still there and
# still starts. That cost a round of "I tried it and it still doesn't work" against a fix that was
# fine. Same publish the docs describe, so the copy that gets launched is never older than the
# ones that get uploaded.
#
# Not for a scratch -Out: a trial version in artifacts\Umbra would then be what gets launched.
if ($scratch) {
    $how = if ($Unsigned) { "UNSIGNED" } elseif ($AllowUntrusted) { "signed, trust not required" } else { "signed" }
    Write-Host "`nBoth downloads built in $out ($how). artifacts\Umbra left alone (scratch run)."
    return
}

Remove-Item -Recurse -Force $runnable -ErrorAction SilentlyContinue

& dotnet publish $project -c Release $versionArg -o $runnable --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "runnable publish failed" }
Assert-FileVersion (Join-Path $runnable "Umbra.exe")

$stamp = (Get-Item (Join-Path $runnable "Umbra.exe")).LastWriteTime.ToString("HH:mm:ss")
"{0,-38} {1,7}" -f "artifacts\Umbra\Umbra.exe", $stamp

Write-Host "`nBoth downloads built, and artifacts\Umbra refreshed to match."
Write-Host "Attach the two in $out to the release for the matching tag."
