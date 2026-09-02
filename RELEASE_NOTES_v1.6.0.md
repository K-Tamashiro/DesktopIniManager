# DesktopIniManager v1.6.0

Released: 2026-09-03

This release adds MFT Differencer, saves the main window's folder trees between
sessions, and improves navigation in the read-only Diff View.

## MFT Differencer

- Compare two roots by relative path, size, and optional last-write time.
  Use MFT enumeration on local NTFS with administrator permissions, with a
  file-system scan fallback when MFT enumeration is unavailable.
- Combine Same, Diff, Left, and Right filters while retaining relevant parent
  folders and subtree file counts.
- Browse all levels from the root in file-name order. Folder filtering preserves
  that order; same-name files are ordered by relative path.
- Inspect timestamps, sizes, NEW/OLD indicators, and supported image thumbnails.
- Select individual files or folders and synchronize in either direction after
  reviewing copy, overwrite, and delete totals. Selections survive filtering.
- Record operation results and failures, then compare again to refresh the list.
- Exclude `.git`, `.vs`, and `.vscode` path components from comparison and sync.
- Show comparison progress and virtualize file rows for responsive scrolling.

## Read-only Diff View

- Display text side by side with line numbers, colored changes, a central map,
  and previous/next change navigation.
- Show the visible range as a frame in the map. Drag it to scroll both panes;
  the frame also follows normal scrolling and window resizing.
- Support horizontal/side-wheel and Shift + wheel scrolling with linked panes.
  Vertical scrollbars are hidden; normal wheel scrolling remains available.
- Display images with shared zoom and aligned positions. Open either file in
  the configured external editor; in-app editing and merging are not provided.

## Main window and packaging

- Persist Physical and Solution hierarchy, expansion, current folder, and icons.
  Search/filter views do not replace the base trees. Interrupted rebuilds keep
  the last complete saved snapshot.
- Align the main window, comparison window, and Diff View to an initial
  1280 × 800 size and match progress spacing between the main/comparison windows.
- Remove the unused duplicate differencer service and include the MFT status
  icon library in build output and the release archive.
- Include illustrated MFT Differencer documentation and screenshots.

## Comparison and synchronization behavior

Comparison uses metadata, not content hashes. Equal sizes and timestamps do not
guarantee equal contents; disabling Compare dates treats equal sizes alone as
identical. Diff View reads contents for inspection.

**A checked file present only on the receiving side is deleted during sync.**
Checked files hidden by a filter remain selected. Review operation totals before
proceeding. Git history is not synchronized, and empty folders are not mirrored.
Stop external writes while comparing and syncing.

## Requirements and download

- Windows 10 or Windows 11, x64
- .NET Framework 4.8
- Administrator permissions and local NTFS for MFT enumeration

Extract `DesktopIniManager-v1.6.0-win-x64.zip` to a writable folder and run
`DesktopIniManager.exe`. Keep `Assets` alongside the executable. The archive
includes `mftree.exe`, `FastVolumeIndex.Core.dll`, both icon libraries, the README,
documentation images, usage documentation, and these release notes.

Existing settings in `%LOCALAPPDATA%\DesktopIniManager` are retained. Main tree
snapshots use `folder-trees.xml`; synchronization logs use `mft-sync-*.log` in
the same directory.
