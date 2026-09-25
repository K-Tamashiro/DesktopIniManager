# DesktopIniManager v3.3.2 --- DIR edition

![DesktopIniManager DIR edition](docs/images/app-overview-dark.png)

DesktopIniManager is a Windows workspace for exploring development
folders, browsing Visual Studio solutions, searching source code,
comparing working trees, and applying custom folder icons through
`desktop.ini`.

**v3.3.2 extends Developer Differencer with path-preserving ZIP export
and optional selection of identical (Same) files, while keeping the
existing difference-selection workflow unchanged.** Workspace
acquisition now uses `FindFirstFileExW` / `FindNextFileW` to build a reusable
path index. Grep and folder comparison use the same native directory enumeration.
This edition does not read the NTFS MFT and does not require elevation
just to enumerate folders.

## Download and requirements

Release package: **DesktopIniManager-v3.3.2-DIR-win-x64.zip**

Download the asset from [GitHub
Releases](https://github.com/K-Tamashiro/DesktopIniManager/releases)
when v3.3.0 is published. A locally generated package is placed in
`release/`. See the [v3.3.2 release notes](docs/releases/v3.3.2-DIR.md).

-   Windows 10 or Windows 11, x64.
-   .NET 10 Desktop Runtime (x64) must be installed separately.
-   Read access to the folders being inspected; write access for icon
    changes and synchronization.
-   MSBuild is required for **Build**, **Rebuild**, and **Clean
    solution**.
-   Configure a text editor for the tree and file-list exports. External
    diff tools are optional.

Extract the ZIP into a new folder and run `DesktopIniManager.exe`. Keep
the accompanying application DLLs, JSON files, `Assets`, and `Languages`
together. When upgrading from v2.x, use a fresh extraction directory to
avoid mixing .NET Framework and .NET 10 files.

Network and cloud-backed locations depend on their provider,
connectivity, permissions, and availability of file contents. They are
not covered by a blanket compatibility guarantee. Make cloud files
available locally before reading or synchronizing them.

## What's new in v3.3.2

-   Export the currently selected Developer Differencer files to ZIP
    from either **Source** or **Target**, preserving each file's
    relative path from the comparison root.
-   Create ZIP files directly from the selected comparison set; no
    temporary directory tree is required.
-   Use a timestamped default ZIP file name in the form
    `xxxxx_yyyyMMdd_HHmmss.zip`, while allowing the destination folder
    and file name to be changed before execution.
-   Add a dedicated **Same ON/OFF** operation beside the ZIP controls.
    It acts only on identical files and does not alter the existing
    selection state of **Different / Source only / Target only** files.
-   Keep the conventional tree and folder check behavior for files with
    differences. Same files remain excluded from ordinary tree-based
    selection.
-   Allow selected Same files to participate in both ZIP export and
    normal synchronization. When synchronized, an explicitly selected
    Same file is overwritten in the selected Source/Target direction.
-   Support accumulating Same selections by folder: selecting another
    folder and turning Same ON adds that folder's displayed Same files
    without clearing Same files selected in previously visited folders.
-   Reset the Same ON/OFF control for each folder selection so the first
    operation in a newly selected folder is ON.
-   Preserve selected Same files while the Same category is visible,
    even when those files are outside the currently selected folder. If
    the Same category is filtered out, operating the Same ON/OFF control
    clears hidden Same selections.
-   Keep folder check state independent from the Same ON/OFF file
    operation.
-   Hide zero-folder information from ZIP/synchronization confirmation
    when no folder operation is involved.

### Retained improvements from v3.3.0

-   Improve txt/log detection for BOM-less UTF-16 and terminal control
    characters.
-   Skip binary contents silently during previous/next navigation;
    retain the message when opening unsupported files directly from the
    difference list.
-   Select the last viewed file on close without the single-selection
    exception.
-   Synchronize line numbers with text scrolling and keep short files
    top-aligned.
-   Align overview markers and the visible range to document height;
    show the selected difference in yellow inside the pink viewport
    frame.
-   Open images and icons fitted to the viewport, with manual 100% zoom
    available.
-   Reduce comparison work for common prefixes/suffixes and avoid
    redundant reads and sorting; prevent overlapping file navigation.

### Retained improvements from v3.2.1

-   Refresh the bundled icon library and the compact/comfortable tree
    icons.
-   Show directional icons beside Source and Target during
    synchronization confirmation; restore the normal folder icons when
    the dialog closes.
-   Refine icon-button sizing, spacing, and transparent backgrounds in
    both themes.

### Retained improvements from v3.2.0

-   Synchronize folder differences as well as files. Source-only and
    Target-only folders can be selected and synchronized in either
    direction, including empty folders that contain no files.
-   Show folder synchronization operations in the confirmation dialog
    with **Create folder** and **Delete folder** counts before
    execution.
-   Propagate folder selection through the tree so checking a parent
    applies to eligible descendant differences.
-   Keep Developer Differencer folder icons, counts, check states, and
    selectable items consistent with the active **Same / Diff / Source
    only / Target only** filters.
-   Apply `obj` and `bin` exclusions consistently to both files and
    folders. Differences that exist only inside excluded build folders
    no longer make the visible parent appear different.
-   Re-evaluate folder status icons when `obj` / `bin` filters change,
    so the tree reflects the effective comparison set immediately.
-   Reduce the cost of bulk check/uncheck operations in large
    comparisons by refreshing selection counts without rebuilding
    unchanged difference counts and folder status information.
-   Retain the v3.1.0 search navigation, Solution browsing,
    Build/Rebuild commands, context-menu exports, script execution, and
    Diff View refinements.

## Workspace views

### Physical and repository acquisition

Choose a search location and use the repository-acquisition button to
build the **Physical** and **Solution** trees. The Physical view shows
the on-disk folder structure, repository markers, and summaries of files
in each folder. A saved, valid search root is acquired while the splash
screen is displayed at the next startup.

![Repository workspace](docs/images/repository-tree-dark.png)

### Solution

The Solution view presents the projects reconstructed from Visual Studio
solution files. Switch between Physical and Solution while retaining the
workspace. Solution parsing reports progress and the current solution.

![Solution view](docs/images/solution-tree-dark.png)

### Search

Search has its own results tree. Temporary searches and filters do not
replace the acquired Physical and Solution trees. Use keywords or
extensions to find folders and files, then narrow the displayed results
with the filter field.

![Search view](docs/images/search-tree-dark.png)

Tree controls support selection/inversion, expansion/collapse,
hiding/restoring folders, and compact display. The file pane offers
list, large-icon, and small-icon layouts. Paths and search terms have
independent input histories; long history paths prioritize the end of
the path.

## Scoped Code Search

Select the projects or folders you need and open Grep. Scopes from
different tree branches are retained; a selected ancestor covers its
selected descendants. Folders can also be dropped into the Grep window.

![Scoped Code Search](docs/images/scoped-code-search.png)

-   Multiple scopes and language profiles, with editable extension
    filters.
-   Plain-text or regular-expression search, match case, and whole word.
-   File, line, column, and matching text in grouped, filterable
    results.
-   Progress and cancellation, result export, and input history.
-   External editor presets including MIFES, Hidemaru, Mery, and VS
    Code.

## Developer Differencer

Open **Developer Differencer** using the comparison button at the bottom
of the main window. Choose Source and Target roots, compare them, and
select the differences to synchronize in either direction.

![Developer Differencer](docs/images/mft-differencer.png)

Files are matched by relative path and compared by size and, when
**Compare dates** is enabled, last-write time at whole-second precision.
This does not compare file contents or hashes. With dates disabled,
equal-sized files are classified as identical.

Combine **Same / Diff / Left / Right** filters and optionally include
`obj` and `bin`. Folder selection follows the visible difference
categories. The root shows files from all levels. **Update** refreshes
already-listed direct files in the selected folder; use Compare to
discover new files.

Difference files continue to use the normal tree/folder check workflow.
The dedicated **Same ON/OFF** control is separate and affects only
identical files. This makes it possible to select the required
differences first, then show Same files and add selected identical files
without disturbing the difference selection. Same selections can be
accumulated by moving through folders; each newly selected folder starts
with the Same control ready for an ON operation.

Synchronization presents file copy, overwrite, and delete counts
together with folder creation and deletion counts before execution.
Source-only and Target-only folders participate in directional
synchronization, including empty folders. Checking a parent folder
applies the current visible difference categories to eligible
descendants.

**A checked file or folder present only on the receiving side is deleted
from that side.** Review the Source/Target direction and selection
before confirming. Identical files are not ordinary tree-selection
candidates, but can be explicitly selected with **Same ON/OFF**. A
selected Same file is a valid synchronization and ZIP target. The **Same
/ Diff / Source only / Target only** and `obj` / `bin` filters also
control the effective folder state shown by the tree. Metadata
directories `.git`, `.vs`, and `.vscode` are excluded.

### ZIP export

The Source and Target ZIP buttons package the currently selected files
from the corresponding side while preserving their relative folder
structure. This is useful for creating a partial working-tree backup,
transferring only the selected paths, or retaining a selected state
before further work.

The ZIP confirmation allows the output location and file name to be
changed. The default file name uses `xxxxx_yyyyMMdd_HHmmss.zip`. Same
files explicitly selected with **Same ON/OFF** are included together
with selected difference files.

### Diff View

![Side-by-side Diff View](docs/images/mft-diff-view.png)

Text comparison includes line numbers, colored changes, linked
scrolling, a central difference map, and navigation between changes.
Text can be selected across lines without copying the displayed line
numbers. Image comparison provides shared zoom, Fit, and 100% views.

Diff View is read only. Open either file in its associated application,
or send both files to an external diff tool. External diff presets
include VS Code, MIFES, WinMerge, and Visual Studio; changed files are
refreshed when returning to the viewer.

### Clean solution

Choose solutions and configurations to run MSBuild Clean before
comparing again. Solutions containing the running application are
excluded from Clean. To clean DesktopIniManager itself, run the
extracted release from a separate directory outside that solution.

See the [comparison and synchronization guide](docs/mft-differencer.md)
for details and limitations.

## Folder icons

Choose an ICO, ICL, DLL, or EXE resource, select folders, and apply the
icon. The bundled `Assets/folder_set.icl` contains development-oriented
folder icons.

![Icon picker](docs/images/icon-picker-dark.png)

DesktopIniManager writes `IconResource` in `desktop.ini`, sets the
necessary file/folder attributes, and refreshes Explorer. It can also
add `desktop.ini` to `.gitignore`. Remove clears the customization.

![Folder icon management](docs/images/folder-icon-apply-dark.png)

## Themes and languages

Switch between light and dark themes. English, Japanese, Simplified
Chinese, and Korean can be selected without restarting; the choice is
retained.

![Light theme](docs/images/physical-tree-light.png)

  -----------------------------------------------------------------------------------------------
  English                                        Japanese
  ---------------------------------------------- ------------------------------------------------
  ![English](docs/images/language-english.png)   ![Japanese](docs/images/language-japanese.png)

  -----------------------------------------------------------------------------------------------

  -----------------------------------------------------------------------------------------
  Simplified Chinese                           Korean
  -------------------------------------------- --------------------------------------------
  ![Simplified                                 ![Korean](docs/images/language-korean.png)
  Chinese](docs/images/language-chinese.png)   

  -----------------------------------------------------------------------------------------

## Build and package

Use the .NET 10 SDK on Windows, or Visual Studio with .NET 10/WPF
support.

``` powershell
dotnet build DesktopIniManager.sln -c Release
pwsh -File scripts/Build-Release.ps1
```

To package an already-built Release directory without rebuilding:

``` powershell
pwsh -File scripts/Build-Release.ps1 -PrebuiltDirectory bin/Release/net10.0-windows/win-x64
```

The prebuilt directory must contain the v3.3.2 binaries and current
README, documentation, assets, and language files. Debug symbols are
omitted from the ZIP.

The packaging script publishes a framework-dependent Windows x64
application to a fresh staging directory, validates its version and
exact file layout, and writes:

``` text
release/
  DesktopIniManager-v3.3.2-DIR-win-x64.zip
  DesktopIniManager-v3.3.2-DIR-win-x64.zip.sha256
```

The ZIP contains the application at its root:

``` text
DesktopIniManager.exe
DesktopIniManager.dll
DesktopIniManager.deps.json
DesktopIniManager.runtimeconfig.json
FastVolumeIndex.Core.dll
Assets/
  folder_set.icl
  DeveloperDifferencer_iconset.icl
  Flag.icl
Languages/
README.md
docs/
```

The application and `FastVolumeIndex.Core` are the two solution
projects. The library retains its historical name; this edition's
workspace acquisition uses native Windows directory enumeration. The deleted `FastVolumeIndex.Cli` /
`mftree.exe` is not included.

The standalone regression harness is separate from the application
solution:

``` powershell
dotnet build Tests/DesktopIniManager.DifferencerTests.csproj -c Release
dotnet run --project Tests/DesktopIniManager.DifferencerTests.csproj -c Release --no-build
```

The older [SMVVM progress memo](docs/smvvm-progress.md) is historical.
The screenshots illustrate the DIR edition and may not show every v3.3.2
control. See the release notes for this version's changes and validation
status.
