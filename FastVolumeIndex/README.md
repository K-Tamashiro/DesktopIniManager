# FastVolumeIndex

`FastVolumeIndex.Core` is an experimental NTFS search engine for DesktopIniManager. It enumerates Master File Table records through `FSCTL_ENUM_USN_DATA`, reconstructs paths from file reference numbers, and searches the resulting in-memory index.

The first prototype deliberately remains separate from the WPF application. This makes it possible to validate correctness and performance before replacing the standard directory scanner.

## Current capabilities

- Enumerate local NTFS files and directories without recursively opening every folder
- Reconstruct full paths from MFT file and parent reference numbers
- Restrict results to a selected root on the indexed volume
- Search file and folder names
- Find Git repository roots from `.git` directories or worktree marker files
- Display an MFT-backed physical folder tree with a configurable depth
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

Git repository search:

```powershell
FastVolumeIndex.Cli\bin\Debug\mftree.exe F:\Documents\Playground --git
```

Name search:

```powershell
FastVolumeIndex.Cli\bin\Debug\mftree.exe F:\Documents\Playground .sln
```

Development structure analysis:

```powershell
FastVolumeIndex.Cli\bin\Debug\mftree.exe E:\Develop --analyze
```

Visual Studio solution mapping:

```powershell
FastVolumeIndex.Cli\bin\Debug\mftree.exe E:\Develop --solutions
```

The solution map distinguishes physical projects, Visual Studio solution folders, and missing project references. It also reads `GlobalSection(NestedProjects)` to reproduce the logical folder hierarchy shown by Visual Studio.

Compare the Visual Studio view with the physical project layout:

```powershell
FastVolumeIndex.Cli\bin\Debug\mftree.exe E:\Develop --solution-diff
```

CLI output is UTF-8, including redirected output containing Japanese file and folder names.

Physical folder tree:

```powershell
FastVolumeIndex.Cli\bin\Debug\mftree.exe E:\Develop --tree --depth 3
```

Add `--files` to include files in the tree. The default tree contains directories only.

The MFT engine opens the volume path, such as `\\.\F:`, and therefore normally requires an elevated process. It supports local NTFS volumes only. DesktopIniManager will retain its standard scanner as the fallback for SMB, Samba, exFAT, FAT, and denied volume access.

## Next validation steps

1. Compare `.git` and `.sln` results with the existing recursive scanner.
2. Measure enumeration, path reconstruction, and query time on a large development volume.
3. Add cancellation and progress reporting around MFT enumeration.
4. Expose a provider interface shared by the MFT and standard scanners.
5. Integrate the provider into DesktopIniManager after result parity is confirmed.
6. Add USN Journal incremental updates only after full enumeration is proven reliable.
