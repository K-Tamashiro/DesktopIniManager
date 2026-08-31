# FastVolumeIndex

`FastVolumeIndex.Core` is the NTFS indexing engine used by DesktopIniManager. It enumerates Master File Table records through `FSCTL_ENUM_USN_DATA`, normalizes paths once, removes duplicate and unreachable records, and builds stable parent/child dictionaries shared by every consumer.

The first prototype deliberately remains separate from the WPF application. This makes it possible to validate correctness and performance before replacing the standard directory scanner.

## Current capabilities

- Enumerate local NTFS files and directories without recursively opening every folder
- Reconstruct full paths from MFT file and parent reference numbers
- Restrict results to a selected root on the indexed volume
- Search file and folder names
- Find Git repository roots from `.git` directories or worktree marker files
- Display an MFT-backed physical folder tree, with optional files
- Inventory Git repositories, Visual Studio solutions, project files, and common build roots
- Parse Visual Studio solution files and map logical projects to physical MFT entries
- Compare Solution membership with physical projects and report both unreferenced and broken entries
- Report MFT enumeration and in-memory search timings
- Explain administrator, non-NTFS, and network-volume failures

## Build

```powershell
MSBuild.exe FastVolumeIndex.sln /t:Rebuild /p:Configuration=Debug
```

## Validate from an administrator terminal

Folder tree:

```powershell
mftree.exe
mftree.exe /f
FastVolumeIndex.Cli\bin\Debug\mftree.exe E:\Develop
FastVolumeIndex.Cli\bin\Debug\mftree.exe E:\Develop /f
```

The first command shows folders. `/f` shows folders and files. CLI output is UTF-8, including redirected output containing Japanese names.

The MFT engine opens the volume path, such as `\\.\F:`, and therefore normally requires an elevated process. It supports local NTFS volumes only. DesktopIniManager will retain its standard scanner as the fallback for SMB, Samba, exFAT, FAT, and denied volume access.
