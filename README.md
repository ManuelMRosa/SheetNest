<p align="center">
  <img src="assets/banner.png" alt="SheetNest — nesting for sheet metal" width="900">
</p>

# SheetNest

**SheetNest** is a fork of [DeepNestSharp](https://github.com/9swampy/DeepNestSharp) — itself a C# port of [Deepnest](https://github.com/Jack000/Deepnest) — focused on production sheet-metal nesting (laser / plasma) with a streamlined operator workflow.

## ⬇️ Download &amp; install (Windows 10/11, 64-bit)

**Recommended:** open the [**latest release**](https://github.com/ManuelMRosa/SheetNest/releases/latest) and download the **`SheetNest-x.y.z-win-x64.msi`** installer — double-click, done. It installs per-user (no admin needed), adds Start-menu and desktop shortcuts, and upgrades cleanly over previous versions.

**Portable option:** download the `.zip` instead, extract anywhere, and run `SheetNest.exe`.

No .NET or anything else to install (self-contained build). The first launch may show a Windows SmartScreen prompt (the app isn't code-signed) → **More info → Run anyway**.

### What SheetNest adds (v1.1)
- **Sheet stock management (v1.1.1)** — the Sheets tab is live inventory: quantities are deducted the moment a nest completes, **Clear Result** discards the nest and puts its sheets back, and parts/sheets are locked while a result is active so the stock can't drift. Your stock and sheet edge margin survive closing the app — reopen and the leftover is waiting.
- **Fast raster nesting engine** — bit-packed collision, best-of rotation profiles evaluated in parallel, pattern replication for production runs. Real-world 800-part jobs nest in seconds.
- **Common-line cutting done right** — per-part "common line" nests copies at a CAM-safe mini-gap (0.003″, below kerf but above CAM merge tolerance), with a hard no-overlap guarantee and tight-pack retries that recover the last part instead of opening an extra sheet.
- **Mixed sheet stock with optimal size selection** — list every stock size you have; the nester probes each one and puts the bulk on the size that packs densest and the tail on the sheet that wastes least. Add Sheet offers the standard US stock sizes plus custom.
- **Radan-style per-part controls** — Edit Part dialog with the seven orientation permissions (as drawn / 90° only / 0°+90° / 0°+180° / 90°+270° / 4-way / free), per-part spacing, priority, and required + spare quantities. Geometry-detected "no turn" suggestion for circles and squares.
- **Industrial production plan** — identical sheets group into "cut N × layout A + M × layout B"; **one DXF per distinct layout** (parts only — no sheet-outline entity for the CAM to trip on).
- **Nest report PDF** — a one-click report: MATERIAL REQUIRED box for purchasing (sheets per size + sq ft), cutting plan, part totals, and a page per layout with a scaled drawing.
- **Manual nest editing** — drag, rotate and nudge placed parts in the viewer with spacing/collision enforcement, undo/redo, and drop-to-contact.
- Plus: reusable-remnant packing (the offcut is a full short-dimension strip), sheet quantity limits with honest "didn't fit" reporting, inch workflow, and a single-toolbar UI.

SheetNest is built on the MIT-licensed DeepNestSharp; its original lineage and license are preserved below and in [LICENSE](LICENSE).

---

# DeepNestSharp
DeepNest - The Original (https://github.com/Jack000/Deepnest)<br />
DeepNestPort - C# port (https://github.com/fel88/DeepNestPort)

**"If I have seen further, it is by standing upon the shoulders of giants"**<br />
Jack and Felix have done some great work but the Original's use of a remote service
to translate between image formats was an issue and the Port just wasn't proving flexible/stable 
enough for my needs. I really needed the ability to save projects, nest results and 
individual sheet placements, and wanted to add the ability to seed subsequent nests with 
the results of prior nests (outstanding) and the ability to edit placements (implemented) - to 
slip that last piece in to the gaps on the sheet that the algorithm just wasn't finding.

Felix was keen to keep true to the original DeepNest code in DeepNestPort...
> > [Hope you consider breaking away from the legacy code base because it's getting really hard to merge.](https://github.com/fel88/DeepNestPort/issues/12#issuecomment-875273391)
> 
> I'll try, but it is important to keep compatibility with the original code...
> I think we shouldn't entangle our repositories too much

...so DeepNestSharp was born. It completely rebuilds the UI using WPF on Net.Core
and is a huge refactor which has paid some dividends but also introduced some 
compromises and issues, some of which are outstanding... 

DXF Import/Export: https://github.com/IxMilia/Dxf

**Project status: WIP**

<img src="imgs/2.png"/>
<img src="imgs/3.png"/>
<img src="imgs/NestResultEditor.png"/>
<img src="imgs/SheetPlacementEditor.png"/>
On the Sheet Placement Editor you can edit the offsets or Shift+Click on parts to drag/drop in the Preview. 
FYI dragging is a little out of sync so multiple small moves work better than one large move. . . and atm
you can only move around parts already present; todo => moving from one sheet to another, adding & removing 
additional parts etc.
<img src="imgs/SaveFiles.png"/>
Individual Parts, whole Nest Result sets and single Sheet Placements can be saved, edited and reloaded. You 
can also persist and view the interim calculation objects; SheetNfp and FinalNfp - for debugging purposes.

## Compiling minkowski.dll
Included are a set of minkowski.dlls that work on various Windows setups I 
have; AnyCpu, x86 & x64; but you'll likely need to build the dlls for your
own setup. You can avoid the need for the C++ import altogether if you
switch off DllImport in the settings; and use the internal C# implementation
instead. Be warned that this internal implementation is not as performant as 
the C++ import atm, and it sometimes generates sub-optimal nests but it's 
an easy-start option that's proving good enough most of the time. . .

1. Replace <boost_1.76_path> with your real BOOST (1.76+) path in compile.bat

Example:
```
cl /Ox -I "D:\boost\boost_1_76_0" /LD minkowski.cc
```
2. Run compile.bat using Developer Command Prompt for Visual Studio
3. Copy minkowski.dll to MinkowskiDlls folder. If you're running in Visual Studio
DeepNestLib.CiTests has a PostBuild task to copy the DLLs from there for you. 
Otherwise make sure the appropriate DLLs get to the DeepNestSharp.exe folder. Note
there's preprocessor directives to pick the right DLL dependent on which Arch 
you're running. Works for me; YMMV.

## Contributors
* https://github.com/kelyamany/DeepNestPort (port to Net.Core)
* https://github.com/Daniel-t-1/DeepNestPort (dxf export)
* https://github.com/9swampy/DeepNestPort (simplification features)
* https://github.com/fel88/DeepNestPort (WinForms C# port)
* https://github.com/Jack000/Deepnest (The original DeepNest)
