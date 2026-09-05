# DesktopIniManager v2.0.0

Released: 2026-09-05

This major release expands DesktopIniManager with live UI language switching,
refined Scoped GREP behavior, enhanced editor integration, selective MFT refresh,
and a substantially improved Diff View with external diff integration.

## Language

- Add a compact language combo to the left of the icon-remove button.
- Support English, Japanese, Simplified Chinese, and Korean.
- Apply overlay strings immediately and persist the selected language.
- Reopen child windows after language changes while preserving position and size.

## Scoped GREP

- Keep checked folders independent and pass the actual checked scope to GREP.
- When nothing is checked, select the visible roots automatically.
- Expand editor history to 20 entries.
- Seed editor presets for MIFES, Hidemaru, Mery, and VS Code.
- Preserve editor selection and launch arguments.
- Allow individual history entries to be removed.

## MFT Differencer

- Add a refresh button for the currently selected folder.
- Refresh only the selected folder subtree instead of rebuilding the entire comparison.
- Update affected file rows, folder nodes, counts, and parent state as required.

## Diff View

- Automatically jump to the first difference after loading.
- Add Previous / Next hunk navigation.
- Add Open Source and Open Target using the associated Windows application.
- Add external diff integration.
- Include presets for VS Code, MIFES, WinMerge, and Visual Studio.
- Preserve the last selected external diff command.
- Add Open Ext Diff button.
- Detect edits made in external diff tools and refresh the affected file and Diff View.
- Improve the difference map:
  - removed content on the left side
  - added content on the right side
  - changed content on both sides
- Make Diff map colors follow light and dark themes.
- Synchronize horizontal scrolling between Source and Target.
- Normalize horizontal scroll range between both panes.
- Improve button theming for light and dark mode.

## Important behavior and limitations

- Supported comparison/synchronization targets remain local NTFS folders.
- Cloud/virtual drives, network paths, and NAS are unsupported.
- Comparison uses relative paths, sizes, and optional timestamps, not hashes.
- Existing settings under `%LOCALAPPDATA%\DesktopIniManager` are retained.

## Download and upgrade

Download `DesktopIniManager-v2.0.0-win-x64.zip` and extract the complete archive
to a writable folder.

Windows 10/11 x64 and .NET Framework 4.8 are required.

The app, shared library, and `mftree.exe` are versioned 2.0.0.0.
Do not mix DLLs from earlier releases.
