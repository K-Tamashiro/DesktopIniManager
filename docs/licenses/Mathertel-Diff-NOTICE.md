# Matthias Hertel's Diff implementation

DIM's `Services/Diff.cs` is a modified derivative of Matthias Hertel's C#
implementation of Eugene Myers' O(ND) difference algorithm.

- Author and original copyright holder: Matthias Hertel.
- Original source: <https://github.com/mathertel/Diff/blob/main/Diff.cs>
- Original license: <https://github.com/mathertel/Diff/blob/main/LICENSE>
- License: BSD-3-Clause. The complete upstream text is in
  [Mathertel-Diff-LICENSE.txt](Mathertel-Diff-LICENSE.txt).
- Source and license checked on 2026-10-08. The exact version originally
  imported into DIM was not recorded; this notice does not assert a commit of origin.

The upstream source credits Matthias Hertel and implements the algorithm described
by Eugene Myers in “An O(ND) Difference Algorithm and its Variations”,
Algorithmica 1 (1986), pp. 251–266. The algorithm paper and this C# implementation
are distinct works; the BSD license here covers Hertel's implementation.

## DIM modifications

- Replaced non-generic collections with typed collections and immutable result records.
- Added direct line-array comparison, avoiding concatenation and re-splitting.
- Used invariant case normalization, modern naming and a DIM-scoped internal API.
- Removed unused text-wrapper APIs and retained the core Myers partitioning,
  optimization and edit-script logic.
- Adapted edit ranges into DIM's aligned rows with original line numbers.

These modifications are maintained by DIM; they are not represented as upstream
changes or endorsed by Matthias Hertel.

## Redistribution

Retain the copyright notice, all three conditions and disclaimer when distributing
source. For binaries, include that same license text in the accompanying materials.
Do not use the author's or contributors' names to imply endorsement without prior
written permission. Modification does not remove these obligations, and this license
does not require publishing DIM's source or sending changes to the author.

DIM copies `docs/licenses` to build/publish output and includes it in release ZIPs.
Keep this notice and the complete license with redistributions.
