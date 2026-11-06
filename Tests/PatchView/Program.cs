using System;
using System.IO;
using System.Linq;
using System.Text;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;

internal static class Program
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            if (args.Length > 0 && args[0] == "--ui") return PatchUiTests.Run();
            var inlineSpace = DiffTextService.Compare(new[] { "ABCD" }, new[] { "AB CD" }, ignoreSpaces: true, calculateInlineDifferences: true);
            Check(inlineSpace.Any(row => row.Kind != DiffLineKind.Unchanged) && inlineSpace.Any(row => row.RightChanges?.Any(changed => changed) == true), "body space remains a visible inline difference when indentation is ignored");
            Check(DiffTextService.Compare(new[] { "\tAB CD" }, new[] { "  AB CD" }, ignoreSpaces: true).All(row => row.Kind == DiffLineKind.Unchanged), "indentation alone can be ignored");
            Check(DiffTextService.Compare(new[] { "AB\t CD  " }, new[] { "AB CD" }, ignoreSpaces: true).All(row => row.Kind == DiffLineKind.Unchanged), "body whitespace runs compare as one space when spaces are ignored");
            Check(DiffTextService.Compare(new[] { "AB\tCD" }, new[] { "AB CD" }, ignoreSpaces: false).Any(row => row.Kind != DiffLineKind.Unchanged), "body tab is significant when spaces are compared");
            var minus = DiffTextService.Compare(new[] { "Const X = ( - 4)" }, new[] { "Const X = ( -4)" }, ignoreSpaces: true, calculateInlineDifferences: true);
            Check(minus.Count == 1 && minus[0].Kind == DiffLineKind.Modified, "presence of a body space is still a difference when spaces are ignored");
            Check(minus[0].LeftChanges.Select((changed, index) => changed ? index : -1).Where(index => index >= 0).SequenceEqual(new[] { 13 }) &&
                  !minus[0].RightChanges.Any(changed => changed), "only the removed space is an inline difference");
            var runs = DiffTextService.Compare(new[] { "a  =  b" }, new[] { "a = c" }, ignoreSpaces: true, calculateInlineDifferences: true)[0];
            Check(!runs.LeftChanges.Take(6).Any(changed => changed) && runs.LeftChanges[6], "whitespace-run length is not an inline difference when spaces are ignored");
            var split = DiffTextService.Compare(new[] { "aaaaaa", "bbb", "", "cccc", "ddddddd", "" }, new[] { "xxxxxxxx", "" }, ignoreSpaces: true, compareBlankLines: false);
            Check(split.Count == 6 && split[2].Kind == DiffLineKind.Unchanged && split[2].LeftNumber == 3 && split[2].RightNumber == 0 &&
                  split[1].Kind != DiffLineKind.Unchanged && split[3].Kind != DiffLineKind.Unchanged, "newline-only row is a standalone unchanged row between two hunks");
            Check(DiffTextService.Compare(new[] { "a", "", "b" }, new[] { "x" }, compareBlankLines: true).Take(3).All(row => row.Kind != DiffLineKind.Unchanged), "newline-only row stays inside one hunk when newline comparison is on");
            static DiffViewModel.TextDocument Doc(params string[] lines) => new()
            {
                Lines = lines, Newlines = "CRLF",
                Endings = lines.Select((_, index) => index == lines.Length - 1 ? "" : "\r\n").ToArray()
            };
            DiffEditSession Session(DiffViewModel.TextDocument left, DiffViewModel.TextDocument right) =>
                new(left, right, DiffTextService.Compare(left.Lines, right.Lines, ignoreSpaces: true, compareBlankLines: false));
            var sourceDoc = Doc("aaaaaa", "bbb", "", "cccc", "ddddddd", "");
            var targetDoc = Doc("xxxxxxxx", "");
            var gapEdits = Session(sourceDoc, targetDoc);
            gapEdits.Copy(new[] { 0, 1 }, true);
            Check(DiffViewModel.Text(gapEdits.Document(false)) == "aaaaaa\r\nbbb\r\ncccc\r\nddddddd\r\n", "multi-hunk copy omits the newline-only gap");
            gapEdits.UndoLast();
            Check(DiffViewModel.Text(gapEdits.Document(false)) == "xxxxxxxx\r\n", "undo restores hunks and gap together");
            gapEdits.Copy(new[] { 0 }, true);
            Check(DiffViewModel.Text(gapEdits.Document(false)) == "aaaaaa\r\nbbb\r\n\r\n", "single-hunk source-to-target copy inserts the adjacent newline-only row");
            gapEdits.UndoHunk(0, true);
            Check(DiffViewModel.Text(gapEdits.Document(false)) == "xxxxxxxx\r\n" && !gapEdits.CanUndo(0, true), "undoing a hunk also undoes its gap");
            gapEdits = Session(sourceDoc, targetDoc);
            gapEdits.Copy(new[] { 0 }, false);
            Check(DiffViewModel.Text(gapEdits.Document(true)) == "xxxxxxxx\r\ncccc\r\nddddddd\r\n", "target-to-source copy removes the newline-only row from the source");
            var matched = Session(Doc("a", "", "b", ""), Doc("a", "", "c", ""));
            matched.Copy(new[] { 0 }, true);
            Check(DiffViewModel.Text(matched.Document(false)) == "a\r\n\r\nb\r\n", "newline-only rows matched on both sides are kept");
            Check(DiffTextService.Compare(new[] { "a", "b" }, new[] { "a", "", "b" }, compareBlankLines: false).All(row => row.Kind == DiffLineKind.Unchanged), "newline-only row is ignored when newline comparison is off");
            Check(DiffTextService.Compare(new[] { "a", "b" }, new[] { "a", "", "b" }, compareBlankLines: true).Any(row => row.Kind != DiffLineKind.Unchanged), "newline-only row differs when newline comparison is on");
            var root = Path.Combine(AppContext.BaseDirectory, "patch-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            const string diff = "--- a/test.txt\n+++ b/test.txt\n@@ -2,2 +2,3 @@\n common\n-old\n+new\n+extra\n@@ -6 +7 @@\n-last\n+final\n\\ No newline at end of file\n";
            var parsed = PatchDocument.Parse(Encoding.UTF8.GetBytes(diff));
            Check(parsed.Files.Count == 1 && parsed.Files[0].Hunks.Count == 2, "plain unified diff parses");
            var email = "From abc Mon Sep 17 00:00:00 2001\nFrom: Developer <dev@example.test>\nSubject: [PATCH] =?UTF-8?q?=E6=9B=B4=E6=96=B0?=\n\nFirst paragraph.\n\nSecond paragraph.\n---\n test.txt | 3 ++-\n" + diff;
            var mail = PatchDocument.Parse(Encoding.UTF8.GetBytes(email));
            Check(mail.CommitTitle == "[PATCH] 更新" && mail.CommitMessage == "First paragraph." + Environment.NewLine + Environment.NewLine + "Second paragraph.", "format-patch title and body decode");
            var path = Path.Combine(root, "test.txt");
            File.WriteAllText(path, "head\r\ncommon\r\nold\r\ngap\r\ngap2\r\nlast", new UnicodeEncoding(false, true));
            var state = new PatchFileState(parsed.Files[0]);
            Check(!state.CanApply, "missing modified target is preview-only");
            state.Attach(path);
            Check(state.Matches(0) && state.Matches(1), "old hunk coordinates match actual file");
            var nextPosition = state.Positions[1];
            state.Move(0, 1);
            Check(state.Positions[1] == nextPosition && !state.Matches(0), "manual movement leaves neighbor fixed and exposes mismatch");
            state.Move(0, -1);
            state.Copy(new[] { 0 });
            Check(state.Positions[1] == nextPosition + 1, "applying insertion shifts following target coordinate");
            Check(DiffViewModel.Text(state.Document()) == "head\r\ncommon\r\nnew\r\nextra\r\ngap\r\ngap2\r\nlast", "hunk copy preserves unrelated target lines");
            state.Copy(new[] { 1 });
            Check(DiffViewModel.Text(state.Document()).EndsWith("final", StringComparison.Ordinal), "patch EOF marker removes final newline");
            state.UndoHunk(0);
            Check(state.IsApplied(1) && state.Positions[1] == nextPosition && DiffViewModel.Text(state.Document()).Contains("old\r\n"), "selective undo retains later applied hunk");
            state.Undo();
            Check(!state.IsDirty, "undo restores original bytes' text");
            state.Copy(new[] { 0, 1 });
            state.Save();
            var saved = DiffViewModel.ReadDocument(path);
            Check(saved.EncodingName == "UTF-16 LE" && saved.HasBom && !state.IsDirty, "save retains existing encoding and BOM");
            state.UndoHunk(1);
            File.AppendAllText(path, "external", new UnicodeEncoding(false, false));
            var externalBytes = File.ReadAllBytes(path);
            var refused = false;
            try { state.Save(); } catch (IOException) { refused = true; }
            Check(refused && externalBytes.SequenceEqual(File.ReadAllBytes(path)), "external edits are never overwritten");
            var patchedPath = Path.Combine(root, "patched.txt");
            File.WriteAllText(patchedPath, "head\r\ncommon\r\nnew\r\nextra\r\ngap\r\ngap2\r\nfinal");
            var present = new PatchFileState(parsed.Files[0]);
            present.Attach(patchedPath);
            Check(present.IsPresent(0) && present.IsPresent(1) && !present.CanCopy(0) && !present.CanMove(0, 1) &&
                  present.Matches(0) && present.Matches(1), "hunks already contained in the target are recognized at shifted positions");
            var presentRows = present.Rows(false, false, false);
            Check(presentRows.All(row => row.Kind == DiffLineKind.Unchanged) && present.Runs.Count == 0 &&
                  presentRows.Count(row => row.Right != null) == 7,
                  "already-contained hunk compares its full post-change range with the target and has no run");
            var shiftedPath = Path.Combine(root, "shifted.txt");
            File.WriteAllText(shiftedPath, "inserted above\r\nhead\r\ncommon\r\nold\r\ngap\r\ngap2\r\nlast");
            var shifted = new PatchFileState(parsed.Files[0]);
            shifted.Attach(shiftedPath);
            Check(shifted.Positions[0] == 2 && shifted.Positions[1] == 6 && shifted.Matches(0) && shifted.Matches(1),
                  "hunks are found at the nearest matching position when the target gained lines above");
            shifted.Rows(false, false, false);
            shifted.CopyRuns(Enumerable.Range(0, shifted.Runs.Count));
            Check(DiffViewModel.Text(shifted.Document()) == "inserted above\r\nhead\r\ncommon\r\nnew\r\nextra\r\ngap\r\ngap2\r\nfinal",
                  "applying shifted hunks does not duplicate their context");
            var runFixture = PatchDocument.Parse(Encoding.UTF8.GetBytes("--- a/r.txt\n+++ b/r.txt\n@@ -1,5 +1,5 @@\n a\n-b\n+B\n c\n-d\n+D\n e\n"));
            var runPath = Path.Combine(root, "r.txt");
            File.WriteAllText(runPath, "a\nb\nc\nd\ne\n");
            var runs = new PatchFileState(runFixture.Files[0]);
            runs.Attach(runPath);
            var runRows = runs.Rows(false, false, false);
            Check(runs.Runs.Count == 2 && runRows.Where(row => row.Kind == DiffLineKind.Unchanged).All(row => row.EditHunkId < 0),
                  "one patch hunk shows one run per changed block; unchanged rows belong to no run");
            runs.CopyRuns(new[] { 1 });
            Check(DiffViewModel.Text(runs.Document()) == "a\nb\nc\nD\ne\n", "a run applies only its own rows");
            runs.Rows(false, false, false);
            Check(runs.Runs.Count == 2 && runs.IsRunApplied(1) && !runs.IsRunApplied(0), "an applied run stays visible for undo");
            runs.CopyRuns(new[] { 0 });
            Check(DiffViewModel.Text(runs.Document()) == "a\nB\nc\nD\ne\n", "the earlier run applies after a later one");
            runs.Rows(false, false, false);
            runs.UndoRun(1);
            Check(DiffViewModel.Text(runs.Document()) == "a\nB\nc\nd\ne\n", "undoing one run keeps the other");
            var crlfPatch = PatchDocument.Parse(Encoding.UTF8.GetBytes("--- a/e.txt\n+++ b/e.txt\n@@ -1,2 +1,2 @@\n a\r\n-b\r\n+c\r\n"));
            Check(crlfPatch.Files[0].Hunks[0].Lines.All(line => line.Crlf && line.Text.Length == 1), "CR before a patch line's LF is kept as the CRLF ending, not text");
            var lfPath = Path.Combine(root, "e.txt");
            File.WriteAllText(lfPath, "a\nc\n");
            var endingState = new PatchFileState(crlfPatch.Files[0]);
            endingState.Attach(lfPath);
            Check(endingState.Rows(false, false, false).All(row => row.Kind == DiffLineKind.Unchanged),
                  "CRLF/LF differences are unchanged rows (foldable) when newline comparison is off");
            Check(endingState.Rows(false, false, false, true).Count(row => row.Kind == DiffLineKind.Modified) == 2,
                  "CRLF/LF differences are ordinary changed rows when newline comparison is on");
            var addition = PatchDocument.Parse(Encoding.UTF8.GetBytes("--- /dev/null\n+++ b/new/file.txt\n@@ -0,0 +1,2 @@\n+one\n+two\n"));
            var fresh = new PatchFileState(addition.Files[0]);
            fresh.Copy(new[] { 0 });
            Check(fresh.IsDirty, "new-file hunk can be copied before choosing save root");
            var filled = new PatchFileState(addition.Files[0]);
            filled.Attach(Path.Combine(root, "filled", "new", "file.txt"));
            Check(filled.IsDirty && !filled.HasUserEdits && DiffViewModel.Text(filled.Document()) == "one\r\ntwo\r\n",
                  "a new file without a target is filled from the patch and can be saved");
            filled.Attach(Path.Combine(root, "other", "new", "file.txt"));
            Check(filled.IsDirty && filled.TargetPath.Contains("other"), "an automatically filled new file follows a repository change");
            var newPath = DiffViewModel.ResolvePatchPath(root, addition.Files[0].RelativePath);
            fresh.AssignNewPath(newPath);
            fresh.Save(new UTF8Encoding(false, true), "\n");
            Check(File.ReadAllText(newPath) == "one\ntwo\n" && !fresh.IsDirty, "new-file save uses chosen newline and creates parent directory");
            Check(!PatchDocument.IsSafeRelativePath("../outside.txt") && !PatchDocument.IsSafeRelativePath("C:/outside.txt"), "unsafe patch paths rejected");
            var deleted = PatchDocument.Parse(Encoding.UTF8.GetBytes("--- a/delete.txt\n+++ /dev/null\n@@ -1 +0,0 @@\n-remove\n"));
            var deletePath = Path.Combine(root, "delete.txt");
            File.WriteAllText(deletePath, "remove\n");
            var removal = new PatchFileState(deleted.Files[0]); removal.Attach(deletePath); removal.Copy(new[] { 0 }); removal.Save();
            Check(File.Exists(deletePath) && new FileInfo(deletePath).Length == 0, "deletion patch saves empty file without deleting it");
            var sjis = CodePagesEncodingProvider.Instance.GetEncoding(932);
            var mixed = Encoding.UTF8.GetBytes("--- a/mixed.txt\n+++ b/mixed.txt\n@@ -1 +1 @@\n-日本語\n")
                .Concat(sjis.GetBytes("+変更後\n")).ToArray();
            var mixedPatch = PatchDocument.Parse(mixed);
            Check(mixedPatch.Files[0].Hunks[0].Before[0] == "日本語" && mixedPatch.Files[0].Hunks[0].After[0] == "変更後", "UTF-8 and Shift-JIS lines coexist");
            foreach (var supplied in args)
            {
                var before = File.ReadAllBytes(supplied);
                if (Path.GetExtension(supplied).Equals(".frm", StringComparison.OrdinalIgnoreCase))
                {
                    var form = DiffViewModel.ReadDocument(supplied);
                    Check(form.EncodingName == "S-JIS" && form.Lines.Any(line => line.Trim() == "Option Explicit"), "VB6 form opens without binary classification");
                    Check(before.SequenceEqual(File.ReadAllBytes(supplied)), "original form bytes unchanged");
                    continue;
                }
                var real = PatchDocument.Read(supplied);
                Check(real.Files.Count > 0 && real.Files.Sum(file => file.Hunks.Count) > 0, "supplied patch parses: " + Path.GetFileName(supplied));
                Check(before.SequenceEqual(File.ReadAllBytes(supplied)), "original patch bytes unchanged");
                Console.WriteLine($"FILES {real.Files.Count}, HUNKS {real.Files.Sum(file => file.Hunks.Count)}, DECODE ERRORS {real.DecodingErrorLines}, SUBJECT {real.CommitTitle}");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
