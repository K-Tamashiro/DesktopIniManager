# DesktopIniManager v4.3.0

v4.3.0 improves comparison window responsiveness, adds configurable `.gitignore` exclusions, and expands DIFF VIEW with GIF frame selection and drag-and-drop comparison.

## Developer Differencer

- Load comparison history asynchronously when opening the window, without automatically comparing folders or rebuilding the previous result tree.
- Save comparison history in the background when closing, and wait for persistence to finish before exiting.
- Allow cancellation while restoring a selected history tab, including ignore discovery, file-row preparation, and folder-tree construction. Cancelling discards the partial display while preserving the saved history for later selection.

## Configurable .gitignore exclusions

- Respect `.gitignore` files within both selected roots, including nested rules, wildcard patterns, anchored paths, escaped characters, and negation rules.
- Open the merged Source/Target exclusion list using the ignore button beside the filter button. The button is disabled when no exclusion rules are available.
- Show folder patterns ending in `/` first, followed by other patterns, with each group sorted in ascending order.
- Keep a rule checked to exclude matching paths. Uncheck it and apply to rescan both sides and update the current comparison tab. Other enabled exclusions and negation rules remain effective.
- Provide Cancel, Invert Selection, and Apply actions.
- Retain exclusion choices with comparison history and apply them during synchronization, without modifying the original `.gitignore` files.

Rules above the selected roots and global Git excludes are not used. A child cannot be re-included while its parent directory remains excluded.

## Image and GIF comparison

- Fix image display errors that prevented zoom controls from appearing.
- Preserve sharp pixels with nearest-neighbor scaling for BMP, JPG/JPEG, PNG, GIF, and TIFF/TIF images, without smoothing.
- Retain zoom up to **1600%**, with the pixel grid visible only at that maximum.
- Preserve independent Source/Target image selection and XOR overlays.
- Select animated GIF frames using the slider below the image panes to compare them as still images.
- Use the larger frame count for the shared slider range. Frames unavailable on the shorter side are displayed as missing.
- Display original frame delays and compare composed frames with transparency, local frame rectangles, and frame disposal applied. Playback is not automatic.

MP4 remains outside the supported DIFF VIEW formats.

## External DIFF VIEW and drag-and-drop

Open a comparison even when one of the specified files is missing, allowing added or deleted files to be reviewed:

```text
DesktopIniManager.exe -diff "C:\work\Source.txt" "C:\work\Target.txt"
```

Open an empty viewer without specifying paths:

```text
DesktopIniManager.exe -diff
```

- Drop one file onto Source or Target, or drop two files together to populate both sides in drop order.
- The first input determines text or image mode. Mixed types, unsupported binary files, and files already loaded on either side are rejected.
- Use the clear button immediately to the left of Reload to clear both sides and reset the comparison mode.
- Clear and drag-and-drop work in both text and image views, including viewers opened from Developer Differencer. Manually loaded pairs remain independent of the original folder comparison results.

Invalid arguments, directory arguments, and two specified paths with neither file present remain errors.

## Language support and documentation

- Localize the new ignore settings and drag-and-drop guidance in English, Japanese, Simplified Chinese, and Korean.
- Update the README and Japanese manual for v4.3.0.

## Requirements

- Windows 10 or Windows 11, x64.
- .NET 10 Desktop Runtime, x64.
