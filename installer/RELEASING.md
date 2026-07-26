# SheetNest — Releasing (betas and stables)

New features ship as **GitHub pre-releases (betas)** first, and only get a normal release
once they have matured. GitHub pre-releases appear on the Releases page tagged
"Pre-release" and **never become "Latest"** — which is what keeps them away from regular
users (see *Why betas never reach stable users* below).

## Versioning convention

- **Beta**: tag `vX.Y.Z-beta` (standard semver pre-release suffix). The MSI's
  ProductVersion is the plain `X.Y.Z` (Windows Installer does not accept suffixes).
- **Further betas**: bump the patch — `v1.2.1-beta` (MSI `1.2.1`), and so on.
- **Stable**: the next unused patch, no suffix — e.g. `v1.2.2` (MSI `1.2.2`) — published as
  a normal release, which becomes "Latest".
- **Never rebuild an already-published version number.** Windows Installer skips copying
  files whose FileVersion is not newer, so reinstalling the *same* x.y.z leaves stale DLLs
  behind (workaround exists — `REINSTALL=ALL REINSTALLMODE=amus` — but takes >10 min on the
  ~2 GB FreeCAD payload; just bump instead). A strictly increasing ProductVersion keeps
  every upgrade path clean: stable → beta → beta → stable.

> **Departure, 2026-07-26 — v1.1.7 shipped stable while betas 1.2.0–1.2.2 were already published.**
> Manuel's call, made knowing the cost: `MajorUpgrade` refuses a downgrade, and the in-app updater compares
> the three numbers, so **anyone running a 1.2.x beta cannot install 1.1.7 and is never offered it** — they
> have to uninstall the beta first. GitHub still flags it Latest, because that goes by publish date, not by
> version. If this comes up again, prefer the next unused patch; it is the only number that upgrades cleanly
> from both the last stable and the betas.

Example timeline:

| Release            | Tag           | MSI ProductVersion | GitHub flag  |
| ------------------ | ------------- | ------------------ | ------------ |
| current stable     | `v1.1.6`      | 1.1.6              | Latest       |
| offcut beta 1      | `v1.2.0-beta` | 1.2.0              | Pre-release  |
| offcut beta 2      | `v1.2.1-beta` | 1.2.1              | Pre-release  |
| offcut stable      | `v1.2.2`      | 1.2.2              | Latest       |

## Publishing a beta — checklist

1. **Tests green** — run the two test projects SEPARATELY (`dotnet test` on the whole .sln
   hangs in restore, known quirk):
   `dotnet test DeepNestLib.CiTests\DeepNestLib.CiTests.csproj -c Release` and
   `dotnet test DeepNestSharp.CiTests\DeepNestSharp.CiTests.csproj -c Release`.
2. **Bump the version** in `DeepNestSharp\DeepNestSharp.csproj` (the single authoritative
   source, four fields: `AssemblyVersion`, `FileVersion`, `Version`,
   `InformationalVersion`).
3. **Build script**: copy `installer\build-msi-<prev>.ps1` to `build-msi-<ver>.ps1` and
   update its five version references (publish dir ×3, `-d Version=`, output MSI name).
   Close any running SheetNest built from `bin\Release` first (it locks the exe).
4. **Build the MSI** from the repo root: `.\installer\build-msi-<ver>.ps1`
   (self-contained publish + FreeCAD payload + WiX; ~598 MB).
5. **Verify the MSI without installing it** — read ProductVersion from the MSI database via
   COM (`WindowsInstaller.Installer`), and spot-check a changed DLL if in doubt (admin
   extract: `msiexec /a <msi> /qn TARGETDIR=<dir>`).
6. **Publish the pre-release**:
   ```
   gh release create vX.Y.Z-beta SheetNest-X.Y.Z-win-x64.msi `
     --prerelease --title "SheetNest X.Y.Z (beta)" --notes "<what's new + 'beta' caveat>"
   ```
7. **Invite testers on the related issue** (for the offcut package: issue #2 /
   `Dara1LT`) — **only with Manuel's explicit approval**, linking the pre-release and
   stating clearly that it is a beta. Keep the issue open until the stable ships.

## Promoting to stable — checklist

1. Fold in any beta feedback; tests green again.
2. Bump to the next unused patch (never reuse a beta's number), rebuild the MSI fresh.
3. `gh release create vX.Y.Z <msi> --title "SheetNest X.Y.Z" --notes ...` — a normal
   release, which becomes **Latest** automatically.
4. Nothing else to do: the website and the in-app updater pick it up on their own (below).
   Close the related issue(s).

## Why betas never reach stable users

Both distribution channels resolve GitHub's `releases/latest` endpoint, which **excludes
pre-releases and drafts by design**:

- **In-app updater**: `DeepNestSharp.Domain\UpdateChecker.cs:18` queries
  `https://api.github.com/repos/ManuelMRosa/SheetNest/releases/latest`.
- **sheetnest.io/download**: the droplet cron (`/usr/local/bin/sheetnest-download-url.sh`,
  hourly) regenerates the nginx 302 from the same `releases/latest` endpoint — verified
  2026-07-19.

So a beta is only reachable from the GitHub Releases page itself. The website is never
touched as part of a release (hard rule: the site is a separate, non-GitHub project).
