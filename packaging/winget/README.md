# winget manifest

`manifests/` mirrors the layout of [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs),
so the version folder can be copied into a fork of that repository as it is:

```
manifests/a/alharethh/Umbra/<version>/
  alharethh.Umbra.yaml                 version manifest
  alharethh.Umbra.installer.yaml       the download, its SHA256, InstallerType: portable
  alharethh.Umbra.locale.en-US.yaml    name, description, licence, links
```

Schema 1.12.0, because it is what the community repository actually merges. Of the last 30
merged `New package` pull requests there (checked 2026-10-20), 24 used 1.12.0 and none used a
newer schema; the new-package pull requests on 1.28.0 that passed the pipeline are all still
waiting on a moderator. 1.12.0 is also what `wingetcreate submit` emits, the manifest uses no
field newer than it, and `winget validate` 1.29 accepts it.

**Only the standalone `.exe` is listed, by choice.** The 1.2 MB zip would work too: winget
installs a package's `Dependencies: PackageDependencies` before the package, and RetroBar, the
closest peer, ships exactly that way, as a `zip` with `NestedInstallerType: portable` and a
dependency on `Microsoft.DotNet.DesktopRuntime.10`. The exe was chosen because it needs nothing
else: no machine-wide runtime install and no UAC prompt for one, one file for the pipeline's and
users' antivirus to scan, and the same file as the direct download, so a bug report names the
same build however it was installed. The cost is size: 73 MB, against 1.2 MB plus a runtime the
user may already have. The zip with the runtime dependency stays a real option for later, for
example as a second installer entry.

`InstallerType: portable` is what makes this work with no installer: winget copies
the file into its own packages folder, adds a `umbra` command, and writes the Apps & features
entry itself.

## Refreshing it for a new release

1. Copy the newest version folder to one named after the new version.
2. In all three files, change `PackageVersion`. In the installer file, change `InstallerUrl`,
   `InstallerSha256` and `ReleaseDate`; in the locale file, `ReleaseNotesUrl`.
3. Take the hash from the **published** asset, not a local build, and check the two agree:

   ```
   gh release view v<version> --repo alharethh/Umbra --json assets --jq ".assets[] | .name + \"  \" + .digest"
   gh release download v<version> --repo alharethh/Umbra --pattern "Umbra-<version>-win-x64.exe" --dir <scratch>
   Get-FileHash <scratch>\Umbra-<version>-win-x64.exe
   ```

   winget wants it in upper case. A hash read off `artifacts\release` is only right if nothing
   was rebuilt since the upload: for 0.2.1 the local exe still matched, the local zip already did not.
4. `winget validate --manifest packaging\winget\manifests\a\alharethh\Umbra\<version>`.
   It checks structure and that the three files agree with each other. It does **not** download
   the file or check the hash; only the submission pipeline does that.
5. Commit the new folder. Old version folders can stay; winget-pkgs keeps one folder per version.

## Submitting (the owner)

Submission needs a GitHub account to open the pull request from and a one-time agreement to
Microsoft's Contributor License Agreement, so it is the owner's to do.

1. Fork <https://github.com/microsoft/winget-pkgs>, copy the version folder to the same path in
   the fork, and open a pull request titled `New package: alharethh.Umbra version <version>`.
   Alternatively, `wingetcreate submit <folder>` does the fork and pull request in one step.
2. Sign the CLA when the bot asks. The pipeline then checks the URL and hash, scans the file with
   several antivirus engines and Microsoft Defender, and installs and runs it in a sandbox.
   A moderator approves it after that.
3. Later versions are pull requests titled `New version: …`, made the same way.

**Expect a possible antivirus false positive.** A 73 MB self-contained single-file executable
looks like a packed binary to heuristics. A flag from one engine is common and is resolved in the
pull request's conversation; it is also an early warning of what users' own antivirus will say.

## Pre-release versions

Every Umbra release so far is marked pre-release on GitHub, and winget has no pre-release channel:
whatever version is in the repository is what `winget install` and `winget upgrade` deliver,
without telling the user it is a pre-release.

What was found (2026-10-20), from winget-pkgs' authoring, policy and moderation documents and its
existing packages:

- **No written rule refuses a pre-release.** The documents say nothing either way. What they do
  require is that the URL is the publisher's own and unique per version, which this is.
- **The convention for a project that also ships stable builds is a separate identifier** for
  the pre-releases: `Microsoft.PowerShell.Preview` beside `Microsoft.PowerShell`, LibreOffice's
  `TheDocumentFoundation.LibreOffice.PreRelease.*`. That keeps users of the stable package from
  being upgraded onto a beta.
- **Umbra has no stable channel**, so there is nobody to protect that way, and new 0.x packages
  under a plain identifier pass the pipeline routinely. Whether a moderator objects to the GitHub
  pre-release flag itself is not documented; the first submission will say. `alharethh.Umbra` is
  therefore used as it is. If a stable 1.0 line is ever published alongside continuing
  pre-releases, the pre-releases should move to `alharethh.Umbra.Preview` at that point, since an
  identifier cannot be renamed later, only replaced.

Version sorting is not a problem as long as releases stay plain `0.MINOR.PATCH`: a suffix such as
`0.3.0-beta` would sort *below* `0.3.0`, which is correct, but would also be offered to every
existing user as an upgrade.

## Things worth knowing about a winget install

- **`winget uninstall` deletes the exe, not the bar's state.** Exit the bar first (right-click,
  Exit Umbra) and untick "Start Umbra when I sign in"; `%LOCALAPPDATA%\Umbra` stays behind, as it
  does with the plain download. Uninstalling while the bar runs should fail, the file being in use; not tried.
- **Not tested:** an actual `winget install --manifest` of this package. It would change the machine it
  ran on, and needs winget's local-manifest setting turned on from an elevated prompt.
