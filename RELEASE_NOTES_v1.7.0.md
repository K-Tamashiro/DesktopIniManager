# DesktopIniManager v1.7.0

Released: 2026-09-03

This release refines the MFT Differencer workflow, adds solution cleaning,
and improves image previews, startup, and window navigation.

## MFT Differencer

- Add OBJ and BIN filter buttons using icon-library entries 8 and 9. Both
  default to off, hiding build-output folders at every depth from the tree,
  file list, and displayed folder counts. Root selection excludes hidden
  build outputs. Previously checked files keep their selection when hidden.
- Add MSBuild solution cleaning next to Compare dates. Choose solutions and
  configurations (Debug and Release by default), inspect the result log, and
  automatically compare again after cleaning.
- Prevent cleaning a solution containing the running DIM application. To
  clean DIM itself, run the complete release package from a separate folder
  outside the compared roots. This avoids deleting DIM's own dependencies.
- Reapply timestamps after replacement and verify destination metadata before
  reporting synchronization success. Failed files remain visible for review.
- Use a green Clean button, yellow Source to Target button, and blue Target
  to Source button. Direction icons point down/up to match the stacked roots.
  Matching borders and labels distinguish Source and Target solution choices.
- Add button icons, comparison cancellation, file-list progress, and navigation
  from the selected file to its containing folder.

## Diff View

- Reject executable, DLL, cache, and other recognized binary formats before
  opening the viewer. Check unrecognized extensions for binary content when
  reading them. Unsupported-file messages are in English.
- Preview images side by side at a shared scale that initially fits the panes.
  Add Fit and 100% controls; remove text-diff navigation from image previews.
  Image decoding runs in the background. No image analysis or editing is done.
- Add icons to previous/next difference, external editor, and zoom buttons.

## Application and Grep

- Show a splash screen with startup progress and company attribution.
- Share the elevation checkbox between the main, MFT, and Grep windows, and
  restore the originating screen after an elevation restart.
- Select available root folders when opening Grep without selected folders.
- Improve Grep cancellation, dark-mode controls, sizing, and progress placement.
- Bring windows forward when opened and restore the owner after closing a
  child or dialog, without leaving windows permanently on top.
- Convert common system exceptions to English while preserving original paths.

## Important behavior and limitations

- Supported comparison/synchronization targets are local NTFS folders.
  Google Drive and other cloud/virtual drives, network paths, and NAS are
  unsupported. Administrative permissions do not resolve virtual-drive
  timestamp precision differences.
- Comparison uses relative paths, sizes, and optional timestamps, not hashes.
  Equal metadata does not prove equal contents.
- A checked file present only on the receiving side is deleted. Check the
  direction and copy/overwrite/delete totals before synchronization.
- Checked files hidden by filters remain selected and are included in sync.
  `.git`, `.vs`, and `.vscode` remain excluded.
- Clean uses the solution's MSBuild targets and default platform. It requires
  Visual Studio or Build Tools with MSBuild; custom configurations may be
  entered explicitly. The feature is not a blanket deletion of bin/obj folders.

## Download and upgrade

Download `DesktopIniManager-v1.7.0-win-x64.zip` and extract the complete archive
to a writable folder. Run `DesktopIniManager.exe`; keep `FastVolumeIndex.Core.dll`
and `Assets` beside it. Windows 10/11 x64 and .NET Framework 4.8 are required.

The app, shared library, and `mftree.exe` are versioned 1.7.0.0. Do not mix DLLs
from earlier releases. Close the previous application before replacing files.
Existing settings in `%LOCALAPPDATA%\DesktopIniManager` are retained.

The archive includes the README, these notes, documentation, and both icon
libraries. A separate `.sha256` file accompanies the ZIP.
