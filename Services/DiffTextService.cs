using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopIniManager.Services
{
    internal sealed class DiffLine
    {
        public string Left { get; set; }
        public string Right { get; set; }
        public string Kind { get; set; }
        public int LeftNumber { get; set; }
        public int RightNumber { get; set; }
        public string LeftDisplay { get { return (LeftNumber == 0 ? "" : LeftNumber.ToString()).PadLeft(6) + "  " + (Left ?? "").Replace("\t", "    "); } }
        public string RightDisplay { get { return (RightNumber == 0 ? "" : RightNumber.ToString()).PadLeft(6) + "  " + (Right ?? "").Replace("\t", "    "); } }
    }
    internal static class DiffTextService
    {
        public static List<DiffLine> Compare(string[] left, string[] right)
        {
            var matches = new List<Tuple<int, int>>();
            // Exact LCS for small inputs; monotonic anchors bound memory for large files.
            if ((long)(left.Length + 1) * (right.Length + 1) <= 4000000)
            {
                var lengths = new int[left.Length + 1, right.Length + 1];
                for (int i = left.Length - 1; i >= 0; i--) for (int j = right.Length - 1; j >= 0; j--)
                    lengths[i, j] = left[i] == right[j] ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
                int a = 0, b = 0;
                while (a < left.Length && b < right.Length)
                { if (left[a] == right[b]) { matches.Add(Tuple.Create(a++, b++)); } else if (lengths[a + 1, b] >= lengths[a, b + 1]) a++; else b++; }
            }
            else
            {
                var positions = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
                for (int j = 0; j < right.Length; j++) { Queue<int> queue; if (!positions.TryGetValue(right[j], out queue)) positions.Add(right[j], queue = new Queue<int>()); queue.Enqueue(j); }
                int last = -1;
                for (int i = 0; i < left.Length; i++)
                {
                    Queue<int> queue; if (!positions.TryGetValue(left[i], out queue)) continue;
                    while (queue.Count > 0 && queue.Peek() <= last) queue.Dequeue();
                    if (queue.Count > 0) { last = queue.Dequeue(); matches.Add(Tuple.Create(i, last)); }
                }
            }
            matches.Add(Tuple.Create(left.Length, right.Length));
            int li = 0, ri = 0; var rows = new List<DiffLine>();
            foreach (var match in matches)
            {
                while (li < match.Item1 || ri < match.Item2)
                {
                    bool l = li < match.Item1, r = ri < match.Item2;
                    rows.Add(new DiffLine { Left = l ? left[li] : null, Right = r ? right[ri] : null, LeftNumber = l ? li + 1 : 0, RightNumber = r ? ri + 1 : 0, Kind = l && r ? "変更" : l ? "削除" : "追加" });
                    if (l) li++; if (r) ri++;
                }
                if (li < left.Length && ri < right.Length) { rows.Add(new DiffLine { Left = left[li], Right = right[ri], LeftNumber = ++li, RightNumber = ++ri, Kind = "一致" }); }
            }
            return rows;
        }
    }
}
