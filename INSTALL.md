# Installation

## Quick install (NETLOAD, per-session)

1. Build the plugin:
   ```powershell
   dotnet build CSharp/CatchmentTool.csproj -c Release
   ```
2. In Civil 3D, type `NETLOAD` and select `CSharp/bin/CatchmentTool.dll`.
3. Type `DELINEATE` (auto-delineation) or `MAKECATCHMENTS` (linework → catchments).

The DLL stays loaded for the current Civil 3D session only.

## Permanent install (ApplicationPlugins bundle)

The repo's `Distribution/CatchmentTool.bundle/` is an AutoCAD ApplicationPlugins bundle. Drop a copy in:

```
%PROGRAMDATA%\Autodesk\ApplicationPlugins\
```

Civil 3D auto-loads the bundle on startup; commands are available without `NETLOAD`.

## Custom Civil 3D path

By default the build references `C:\Program Files\Autodesk\AutoCAD 2026`. Override:

```powershell
dotnet build CSharp/CatchmentTool.csproj -c Release -p:Civil3DPath="D:\Autodesk\AutoCAD 2026"
```

Or set `CIVIL3D_PATH` as an environment variable before building.

## Requirements

- Civil 3D 2026 (or 2025+ with .NET 8 enabled)
- Windows 10/11
- .NET 8 SDK (build only — runtime is bundled with Civil 3D 2026)

No Python dependency in v2.

## Verifying the install

After NETLOAD:

1. Type `DELINEATE` — the dialog should open with TIN surface and pipe network dropdowns populated.
2. Type `MAKECATCHMENTS` — should prompt to select polylines.

If the commands aren't recognized, check that the DLL is loaded: `(arx)` lists loaded native libs but for managed DLLs you can verify with `NETUNLOAD CatchmentTool` (errors if not loaded).

## Troubleshooting build errors

| Error                                                                                        | Fix                                                                                                              |
| -------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------- |
| `error MSB3245: Could not resolve this reference. Could not locate the assembly "AeccDbMgd"` | Civil 3D path is wrong. Set `-p:Civil3DPath="..."` or `CIVIL3D_PATH`.                                            |
| `error CS0246: The type or namespace name 'Catchment' could not be found`                    | `AeccDbMgd.dll` not referenced — check the Civil 3D path resolves to a directory containing `C3D/AeccDbMgd.dll`. |
| `error NU1100: Unable to find package 'Newtonsoft.Json'`                                     | Restore: `dotnet restore CSharp/CatchmentTool.csproj`.                                                           |
