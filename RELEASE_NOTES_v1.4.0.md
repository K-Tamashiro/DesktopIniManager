# DesktopIniManager v1.4.0

This release separates physical project layout, Visual Studio solution structure, and search results into persistent views for faster navigation through large repositories.

## Highlights

- Added independent `Physical`, `Solution`, and `Search` tabs.
- Git acquisition now generates and retains both Physical and Solution trees in one operation.
- Regular searches update only the Search tab and never replace an acquired Physical or Solution tree.
- Excluded folders carrying the Windows `FileAttributes.Hidden` attribute from Physical trees in both standard and fast NTFS modes.
- Rebuilt Solution trees from `.sln` and `.csproj` logical structure instead of raw directory enumeration.
- Limited Solution trees to logical folders; files remain available in the existing file list.
- Added Visual Studio-style ordering: `Properties`, dependencies, and then folders alphabetically.
- Excluded unreferenced files and generated output such as `bin` and `obj` from Solution trees.
- Added compact and comfortable folder-tree density controls and refined the tab presentation.

## Requirements

- Windows 10 or Windows 11, x64
- .NET Framework 4.8

## Download

Download `DesktopIniManager-v1.4.0-win-x64.zip`, extract it to a writable folder, and run `DesktopIniManager.exe`.

The archive also includes `mftree.exe`, `FastVolumeIndex.Core.dll`, the bundled icon assets, the README, and these release notes.
