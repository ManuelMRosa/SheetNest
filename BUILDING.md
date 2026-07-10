# Building SheetNest from source

You only need this if you want to build SheetNest yourself. Most people should just
download the installer from the [latest release](https://github.com/ManuelMRosa/SheetNest/releases/latest).

SheetNest is a .NET 6 / WPF desktop app for Windows.

## Prerequisites

- Windows 10/11 (64-bit)
- .NET 6 SDK
- Visual Studio 2022 (or the .NET SDK with your editor of choice)

## Build & test

```
dotnet build -c Release
dotnet test
```

Run the built `SheetNest.exe`, or open the solution in Visual Studio and start the app
project.

## Optional: the native minkowski.dll

The nesting geometry can use a small native helper, `minkowski.dll`. Prebuilt copies for
common Windows setups (AnyCpu / x86 / x64) are included, but you may need to build one for
your machine. You can also skip it entirely by turning off the DLL import in Settings and
using the built-in managed implementation instead (a little slower, fine for most jobs).

To build it:

1. Put your real BOOST (1.76+) path into `compile.bat`, e.g.
   ```
   cl /Ox -I "D:\boost\boost_1_76_0" /LD minkowski.cc
   ```
2. Run `compile.bat` from a **Developer Command Prompt for Visual Studio**.
3. Copy the resulting `minkowski.dll` next to `SheetNest.exe` (or into the `MinkowskiDlls`
   folder — the test project copies them for you). Preprocessor directives select the right
   DLL for the architecture.

## Installer (MSI)

The Windows installer is built with WiX; see `installer/` and the
`installer/build-msi-*.ps1` script. The MSI bundles the offline 3D-unfolding engine.

## License & credits

SheetNest is open-source software released under the MIT License. It builds on prior
open-source work; see [LICENSE](LICENSE) for the full attribution and terms.
