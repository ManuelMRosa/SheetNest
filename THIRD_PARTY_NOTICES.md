# Third-party notices

SheetNest itself is released under the MIT License (see [LICENSE](LICENSE)).

To turn 3D parts (STEP / IGES) into flat patterns, the SheetNest installer bundles an
offline 3D-unfolding engine made of the open-source components listed below. SheetNest runs
that engine as a **separate program** (it invokes `freecadcmd.exe`); it does **not** link
against these libraries. Each component is redistributed **unmodified**, and its full license
text ships inside the `freecad\` folder of the installed application. Corresponding source
code is available from the upstream projects linked below.

| Component | Version | License | Source | Bundled license text |
|-----------|---------|---------|--------|----------------------|
| FreeCAD | 1.1 | LGPL-2.1-or-later | https://www.freecad.org · https://github.com/FreeCAD/FreeCAD | `freecad\doc\LICENSE.html` |
| SheetMetal workbench | bundled | LGPL-2.1 | https://github.com/shaise/FreeCAD_SheetMetal | `freecad\Mod\SheetMetal\LICENSE` |
| NetworkX | 3.6.1 | BSD-3-Clause | https://networkx.org · https://github.com/networkx/networkx | `freecad\bin\Lib\site-packages\networkx-3.6.1.dist-info\licenses\LICENSE.txt` |

FreeCAD in turn bundles further third-party libraries (OpenCASCADE, Qt, Coin3D, Python and
others), each under its own license; those licenses are documented in
`freecad\doc\LICENSE.html`.

## Nesting engine

To arrange parts on the sheet, the SheetNest installer bundles an offline nesting engine
(`sparrow.exe`, next to `SheetNest.exe`). SheetNest runs it as a **separate program**; it does
**not** link against these libraries. `sparrow.exe` is a self-contained Rust binary; the
`jagua-rs` geometry library is compiled into it **unmodified**, while `sparrow` carries a small
local change so that nesting the same job twice gives the same result: a `--max-iterations`
budget that ends the search on a counter instead of on elapsed time, a fixed worker-thread count
instead of one derived from the machine's cores, and the failure-based (rather than time-based)
compression decay whenever that budget is used. The change is kept in this repository as
`SparrowEngine/sheetnest-sparrow.patch`, which applies to the upstream commit below.
Corresponding source is available from the upstream projects below.

| Component | Version | License | Source |
|-----------|---------|---------|--------|
| sparrow | 2025 (upstream `961ec31`) | MIT | https://github.com/JeroenGar/sparrow |
| jagua-rs | 0.7.2 | MPL-2.0 | https://github.com/JeroenGar/jagua-rs |

If you need the corresponding source for any bundled LGPL component, it is available from the
upstream project links above; you may also request it through the
[SheetNest repository](https://github.com/ManuelMRosa/SheetNest).
