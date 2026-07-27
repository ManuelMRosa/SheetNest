# SheetNest: Releasing (betas and stables)

New features ship as **GitHub pre-releases (betas)** first, and only get a normal release
once they have matured. GitHub pre-releases appear on the Releases page tagged
"Pre-release" and **never become "Latest"**, which is what keeps them away from regular
users (see *Why betas never reach stable users* below).

## A beta is a ZIP, never an MSI

**This is the rule the rest of the convention hangs on.** A beta ships as a portable zip that
the tester unpacks and runs; only a stable gets an installer. So the tester keeps whatever
stable they already have, side by side, and never uninstalls anything to try a build.

It is not about size. GitHub takes 2 GB per asset and our installer is 0.6 GB. It is that
**Windows Installer imposes rules a zip does not have**: ProductVersion may only go up,
`MajorUpgrade` refuses anything it reads as a downgrade, and reinstalling the same number
leaves stale DLLs behind. Ship betas as MSIs and those rules decide your version numbers for
you.

> **Why this is written down (2026-07-26).** Betas had been shipping as MSIs, each bumping the
> patch: 1.2.0, 1.2.1, 1.2.2. That left no number above them for the stable, which went out as
> **1.1.7**, *below* the betas. Every tester was then stuck: the installer refused it as a
> downgrade and the in-app check never offered it, so each of them had to uninstall by hand
> first. Nobody made a mistake in the moment; the process guaranteed it. **Do not "simplify"
> this back into publishing beta installers.**

## Versioning convention

Pre-releases are named after the release they lead to, and the stable ships **that same
number**, so a beta never consumes a number the stable might need.

| | betas of the cycle | stable |
| --- | --- | --- |
| **Tag / title** | `v1.2.0-beta1`, `-beta2`, `-rc1` | `v1.2.0` |
| **Asset** | `SheetNest-1.2.0-beta1-win-x64.zip` | `SheetNest-1.2.0-win-x64.msi` |
| **`AssemblyVersion` / `FileVersion`** | `1.2.0.0` | `1.2.0.0` |
| **`Version` / `InformationalVersion`** | `1.2.0-beta1` | `1.2.0` |
| **GitHub** | `--prerelease` | normal release ⇒ Latest |

- The counter runs **per stage** (`beta1`, `beta2`, `rc1`), never by bumping the patch.
- `AssemblyVersion` and `FileVersion` **must stay numeric**, because MSBuild rejects a suffix there.
  The suffix lives in `Version` / `InformationalVersion`, which is what About shows.
- **A stable's tag must be plain `vX.Y.Z`.** `UpdateChecker` parses it with `Version.TryParse`,
  which fails on a suffix and would silently stop the update check working.
- **Never rebuild an already-published stable number.** Windows Installer skips files whose
  FileVersion is not newer, so reinstalling the same x.y.z leaves stale DLLs behind (workaround
  exists, `REINSTALL=ALL REINSTALLMODE=amus`, but takes >10 min on the ~2 GB FreeCAD payload;
  just bump instead).

Example timeline:

| Release        | Tag             | Asset | GitHub flag |
| -------------- | --------------- | ----- | ----------- |
| current stable | `v1.1.7`        | MSI 1.1.7 | Latest      |
| next beta 1    | `v1.2.0-beta1`  | zip   | Pre-release |
| next beta 2    | `v1.2.0-beta2`  | zip   | Pre-release |
| release cand.  | `v1.2.0-rc1`    | zip   | Pre-release |
| next stable    | `v1.2.0`        | MSI 1.2.0 | Latest      |

## Publishing a beta: checklist

1. **Tests green.** Run the two test projects SEPARATELY (`dotnet test` on the whole .sln
   hangs in restore, known quirk):
   `dotnet test DeepNestLib.CiTests\DeepNestLib.CiTests.csproj -c Release` and
   `dotnet test DeepNestSharp.CiTests\DeepNestSharp.CiTests.csproj -c Release`.
2. **Set the version** in `DeepNestSharp\DeepNestSharp.csproj` (the single authoritative
   source) to the release this beta leads to: `AssemblyVersion` and `FileVersion` numeric
   (`1.2.0.0`), `Version` and `InformationalVersion` with the suffix (`1.2.0-beta1`).
3. **Build the zip** from anywhere in the repo. The script takes the version as a parameter,
   so there is nothing to copy and no string to forget:
   ```
   .\installer\build-beta-zip.ps1 -Version 1.2.0-beta1
   ```
   Self-contained publish + FreeCAD payload + zip. It kills a running SheetNest itself (it
   locks the exe). Measured on the 1.1.7 payload: **745 MB out of 2.3 GB, and the zip step
   alone takes ~11 minutes**, so run it in the background and do something else.
4. **Publish the pre-release**:
   ```
   gh release create v1.2.0-beta1 SheetNest-1.2.0-beta1-win-x64.zip `
     --prerelease --title "SheetNest 1.2.0-beta1" --notes "<what's new + how to run it>"
   ```
   Say in the notes that it is a zip: unpack anywhere and run `SheetNest.exe`; nothing is
   installed and their existing SheetNest is untouched.
5. **Invite testers on the related issue**, **only with Manuel's explicit approval**, linking
   the pre-release and stating clearly that it is a beta. Keep the issue open until the stable
   ships.

## Promoting to stable: checklist

1. Fold in any beta feedback; tests green again.
2. **Drop the suffix** in the csproj. `Version` and `InformationalVersion` become the plain
   number the betas were named after (`1.2.0`). Nothing else changes.
3. **Build script**: copy `installer\build-msi-<prev>.ps1` to `build-msi-<ver>.ps1` and update
   its five version references (publish dir ×3, `-d Version=`, output MSI name).
4. **Build the MSI** from the repo root: `.\installer\build-msi-<ver>.ps1` (~600 MB, several
   minutes, so run it in the background).
5. **Verify the MSI without installing it**: read ProductVersion from the MSI database via
   COM (`WindowsInstaller.Installer`).
6. **Install it and actually use it.** A beta tester exercises the app, never the installer, so
   anything that only breaks once installed (paths, permissions, the FreeCAD payload) gets
   its first real test here. Uninstall the previous version first if the installer refuses it.
7. `gh release create vX.Y.Z <msi> --title "SheetNest X.Y.Z" --notes ...`, a normal release,
   which becomes **Latest** automatically. `--target` needs the **full 40-character SHA**;
   GitHub rejects a short one with `422 target_commitish is invalid`.
8. Nothing else to do: the website and the in-app updater pick it up on their own (below).
   Close the related issue(s).

## Why betas never reach stable users

Both distribution channels resolve GitHub's `releases/latest` endpoint, which **excludes
pre-releases and drafts by design**:

- **In-app updater**: `DeepNestSharp.Domain\UpdateChecker.cs:18` queries
  `https://api.github.com/repos/ManuelMRosa/SheetNest/releases/latest`.
- **sheetnest.io/download**: the droplet cron (`/usr/local/bin/sheetnest-download-url.sh`,
  hourly) regenerates the nginx 302 from the same `releases/latest` endpoint. Verified
  2026-07-19.

So a beta is only reachable from the GitHub Releases page itself. The website is never
touched as part of a release (hard rule: the site is a separate, non-GitHub project).
