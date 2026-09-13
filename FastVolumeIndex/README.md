# FastVolumeIndex.Core — DIR edition

This library provides the reusable path index used by DesktopIniManager
v3.0.0 DIR. Its assembly and namespace retain the historical FastVolumeIndex
name.

Workspace acquisition calls `VolumePathIndex.BuildFromDirCommand`, which
collects paths through Windows `dir /s /b`, reconstructs parent/child
relationships, and records project/solution paths. An ordinary .NET
directory-enumeration entry point also remains available.

The library supports folder/file lookup, name and extension searches,
repository-root discovery, and access to the indexed tree. The DIR edition
does not use raw NTFS MFT enumeration and does not require administrative
volume access.

## Build

Use the .NET 10 SDK on Windows:

```powershell
dotnet build FastVolumeIndex.sln -c Release
```

`FastVolumeIndex.sln` contains only `FastVolumeIndex.Core`.
The former `FastVolumeIndex.Cli` project has been removed, and
`mftree.exe` is not part of the v3.0.0 release package.

The main [DesktopIniManager README](../README.md) describes application
usage and self-contained release packaging.
