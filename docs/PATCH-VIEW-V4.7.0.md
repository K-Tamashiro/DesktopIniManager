# V4.7.0 Patch View — agreed specification and work log

## Agreed behavior

- A dedicated mode inside Diff View parses unified/Git patches; never executes patch commands.
- Source/Target selectors list the patch's changed relative paths and stay synchronized.
- Without an actual Target, display old/new patch text, folded unknown intervals, no copy arrows.
- With Target, display post-change patch text on the left and actual file on the right. Copy only left to right.
- Infer repository root from the first selected actual file and its patch-relative path. If ambiguous, ask for a root.
- Missing modified files remain preview-only. Explicit new-file entries can accumulate copied hunks and save as new files.
- Moving one hunk changes only its position, by one line within its current neighbors. Applied line-count changes shift subsequent target positions.
- Compare each moved hunk with the corresponding actual lines. Mark preimage/context mismatches; do not silently relocate hunks.
- Retain per-file working text, undo and adjusted positions on file switching; save/discard/cancel on close.
- Preserve existing target encoding, line endings and EOF; let new-file saves choose encoding and line endings.
- File deletion patches remove text; saving leaves an empty file. Do not delete files or automatically rename/move them.
- Show unsupported binary, mode, symlink entries as unsupported. Reject paths escaping the target root and do not overwrite existing files as new-file creations.
- Keep font, space/tab, newline markers and 50% applied-block shading. Reuse standard text-view rendering where possible.
- Up/down icons are pending user supply; use clearly labelled temporary controls.

## Isolation

Build to `artifacts/v4.7.0/`, not the operational V4.6.0 output. Do not publish or replace an installed executable.

## Initial implementation

In progress: byte-aware patch parser, per-file application state, existing text-view integration, tests against synthetic patches and the three supplied patch files.
