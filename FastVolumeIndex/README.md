# FastVolumeIndex.Core — native enumeration

This library provides the reusable path index used by DesktopIniManager.
Its assembly and namespace retain the historical FastVolumeIndex
name.

Main-screen workspace acquisition calls `VolumePathIndex.BuildFromNativeEnumeration`.
It uses `FindFirstFileExW` (Basic information, LARGE_FETCH) and `FindNextFileW`
to collect names and attributes in one traversal, including empty directories.
Directory reparse points are indexed but not followed. Hidden/system files are
omitted; directory visibility is handled by the caller.
Access-denied and disappeared paths are skipped, and cancellation is checked
between entries. Providers rejecting LARGE_FETCH are retried without that flag.
The ordinary .NET enumeration entry point also remains available.

The differencer uses `EnumerateNativeDirectory` to obtain file sizes and UTC
write times together with names and attributes. Each comparison side is scanned
once, without a separate counting pass; progress shows collected file counts
until the total is known. Hidden/system files and protected-directory exclusions
retain the differencer's existing rules. Reparse-point files retain the previous
metadata-reading path. Synchronization and timestamp comparison rules are unchanged.

Grep also uses `EnumerateNativeDirectory`, combining its file/directory enumeration
and hidden-directory attribute lookup into one pass per directory. Existing
extension/exclusion filters, duplicate-file suppression, and unreadable-directory
handling are retained. File-size checks immediately before reading, text decoding,
regular expressions, and parallel content searching remain unchanged.

The library supports folder/file lookup, name and extension searches,
repository-root discovery, and access to the indexed tree. The library
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
usage and framework-dependent release packaging.
