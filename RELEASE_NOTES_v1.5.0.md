# DesktopIniManager v1.5.0

This release refactors the indexing, solution-analysis, search, and icon pipelines to improve responsiveness on large repositories and directory trees.

## Highlights

- Reduced repeated filesystem and path work during NTFS index construction and lookup.
- Refactored `.sln` and project parsing with cancellation-aware traversal and more efficient collections.
- Improved SDK-style and classic project item evaluation while preserving the folder-only Solution tree.
- Reduced allocations in development-language analysis and repository ownership lookup.
- Optimized folder-name/content scanning and scoped code-search traversal.
- Added folder-icon caching and explicit invalidation after icon changes.
- Batched Explorer refresh notifications after multi-folder Apply and Remove operations.
- Improved file-list metadata handling and search-result counting.
- Simplified tree sorting and selected Grep scope reduction.

## Requirements

- Windows 10 or Windows 11, x64
- .NET Framework 4.8

## Download

Download `DesktopIniManager-v1.5.0-win-x64.zip`, extract it to a writable folder, and run `DesktopIniManager.exe`.

The archive also includes `mftree.exe`, `FastVolumeIndex.Core.dll`, the bundled icon assets, the README, and these release notes.
