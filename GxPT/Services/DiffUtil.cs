// DiffUtil.cs
// Line-level text diff for the edit-tool diff views (approval dialog + transcript history).
// Target: .NET 3.5, Windows XP compatible (no external deps).
//
// Produces a unified-diff BODY (no ---/+++/@@ file headers; the UI supplies its own path header):
// each output line is prefixed with ' ' (context / unchanged), '-' (removed) or '+' (added), so it
// renders directly through the existing "diff" syntax highlighter (DiffHighlighter + the
// Addition/Deletion/Normal token colors and background bands).

using System;
using System.Collections.Generic;
using System.Text;

namespace GxPT
{
    /// <summary>Result of a line-level diff: the prefixed body plus added/removed line counts.</summary>
    internal sealed class LineDiffResult
    {
        public string Body;     // ' '/'-'/'+' prefixed lines joined by '\n' (no trailing newline)
        public int Added;       // number of '+' lines
        public int Removed;     // number of '-' lines
    }

    internal static class DiffUtil
    {
        // Cap on the LCS table size (rows * cols). Edit spans are small in practice; beyond this we
        // fall back to a naive "remove all old, add all new" diff rather than allocate a huge table.
        private const int MaxLcsCells = 4000000; // ~2000 x 2000 lines

        /// <summary>
        /// Build a line-level diff body from two text spans (e.g. an edit's old_string / new_string).
        /// Common lines render as context; only genuine changes get '-'/'+'. Within a change region
        /// removals are listed before additions (the familiar git-diff grouping).
        /// </summary>
        public static LineDiffResult BuildLineDiff(string oldText, string newText)
        {
            string[] a = SplitLines(oldText);
            string[] b = SplitLines(newText);

            List<int> ops; // see EmitOps: 0 = context(a&b), -1 = delete(a), +1 = insert(b)
            if ((long)a.Length * b.Length > MaxLcsCells)
                ops = NaiveOps(a.Length, b.Length);
            else
                ops = LcsOps(a, b);

            return Render(a, b, ops);
        }

        /// <summary>
        /// Like <see cref="BuildLineDiff"/> but surrounds the change with up to <paramref name="contextLines"/>
        /// lines of real file context (for the approval prompt). Locates old_string in the file, expands to
        /// full-line boundaries N lines out each way, and diffs the context-wrapped old/new blocks so the
        /// surrounding lines render as context. Falls back to a bare old/new diff if the file text is
        /// unavailable or old_string isn't found verbatim.
        /// </summary>
        public static LineDiffResult BuildLineDiffWithContext(string fileText, string oldText, string newText, int contextLines)
        {
            try
            {
                if (string.IsNullOrEmpty(fileText) || string.IsNullOrEmpty(oldText) || contextLines <= 0)
                    return BuildLineDiff(oldText, newText);
                int idx = fileText.IndexOf(oldText, StringComparison.Ordinal);
                if (idx < 0) return BuildLineDiff(oldText, newText);
                int endIdx = idx + oldText.Length;

                int ctxStart = LineStartNAbove(fileText, idx, contextLines);
                int ctxEnd = LineEndNBelow(fileText, endIdx, contextLines);

                string before = fileText.Substring(ctxStart, idx - ctxStart);
                string after = fileText.Substring(endIdx, ctxEnd - endIdx);
                return BuildLineDiff(before + oldText + after, before + newText + after);
            }
            catch
            {
                return BuildLineDiff(oldText, newText);
            }
        }

        // Char index of the start of the line that is `n` lines above the line containing `pos`.
        private static int LineStartNAbove(string s, int pos, int n)
        {
            int p = pos < 0 ? 0 : (pos > s.Length ? s.Length : pos);
            while (p > 0 && s[p - 1] != '\n') p--; // start of the line containing pos
            for (int k = 0; k < n && p > 0; k++)
            {
                int prevStart = p - 1;               // the '\n' ending the previous line
                while (prevStart > 0 && s[prevStart - 1] != '\n') prevStart--;
                p = prevStart;
            }
            return p;
        }

        // Char index of the end (exclusive of the trailing '\n') of the line `n` lines below the line
        // containing `pos`.
        private static int LineEndNBelow(string s, int pos, int n)
        {
            int p = pos < 0 ? 0 : (pos > s.Length ? s.Length : pos);
            while (p < s.Length && s[p] != '\n') p++;  // end of the line containing pos
            for (int k = 0; k < n && p < s.Length; k++)
            {
                int nextEnd = p + 1;                  // start of the next line
                while (nextEnd < s.Length && s[nextEnd] != '\n') nextEnd++;
                p = nextEnd;
            }
            return p;
        }

        // ---- line splitting ----

        // Split into lines on '\n', dropping a trailing '\r' (so "\r\n" and "\n" compare equal) and a
        // single trailing empty line caused by a final newline. Empty/null input yields no lines.
        private static string[] SplitLines(string s)
        {
            if (string.IsNullOrEmpty(s)) return new string[0];
            string[] raw = s.Split('\n');
            int count = raw.Length;
            if (count > 0 && raw[count - 1].Length == 0) count--; // drop trailing "" from a final '\n'

            string[] outArr = new string[count];
            for (int i = 0; i < count; i++)
            {
                string line = raw[i];
                if (line.Length > 0 && line[line.Length - 1] == '\r')
                    line = line.Substring(0, line.Length - 1);
                outArr[i] = line;
            }
            return outArr;
        }

        // ---- diff core ----

        // Classic LCS over lines, then a backtrack into an op list (-1 delete a[x], +1 insert b[y],
        // 0 keep both). Ordinal comparison.
        private static List<int> LcsOps(string[] a, string[] b)
        {
            int n = a.Length, m = b.Length;
            // lcs[i,j] = length of LCS of a[i..] and b[j..]
            int[,] lcs = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
            {
                for (int j = m - 1; j >= 0; j--)
                {
                    if (string.Equals(a[i], b[j], StringComparison.Ordinal))
                        lcs[i, j] = lcs[i + 1, j + 1] + 1;
                    else
                        lcs[i, j] = lcs[i + 1, j] >= lcs[i, j + 1] ? lcs[i + 1, j] : lcs[i, j + 1];
                }
            }

            List<int> ops = new List<int>(n + m);
            int x = 0, y = 0;
            while (x < n && y < m)
            {
                if (string.Equals(a[x], b[y], StringComparison.Ordinal)) { ops.Add(0); x++; y++; }
                else if (lcs[x + 1, y] >= lcs[x, y + 1]) { ops.Add(-1); x++; }
                else { ops.Add(1); y++; }
            }
            while (x < n) { ops.Add(-1); x++; }
            while (y < m) { ops.Add(1); y++; }
            return ops;
        }

        // Fallback for pathologically large inputs: delete everything, then add everything.
        private static List<int> NaiveOps(int n, int m)
        {
            List<int> ops = new List<int>(n + m);
            for (int i = 0; i < n; i++) ops.Add(-1);
            for (int j = 0; j < m; j++) ops.Add(1);
            return ops;
        }

        // Render ops into prefixed text. Buffer consecutive deletes/inserts and flush as all '-' then
        // all '+' so a replaced block reads like a git diff rather than interleaving line by line.
        private static LineDiffResult Render(string[] a, string[] b, List<int> ops)
        {
            StringBuilder sb = new StringBuilder();
            List<string> pendingDel = new List<string>();
            List<string> pendingIns = new List<string>();
            int added = 0, removed = 0;
            int x = 0, y = 0;
            bool first = true;

            for (int k = 0; k < ops.Count; k++)
            {
                int op = ops[k];
                if (op == 0)
                {
                    FlushChange(sb, pendingDel, pendingIns, ref first);
                    AppendLine(sb, " ", a[x], ref first);
                    x++; y++;
                }
                else if (op == -1)
                {
                    pendingDel.Add(a[x]); x++; removed++;
                }
                else
                {
                    pendingIns.Add(b[y]); y++; added++;
                }
            }
            FlushChange(sb, pendingDel, pendingIns, ref first);

            LineDiffResult r = new LineDiffResult();
            r.Body = sb.ToString();
            r.Added = added;
            r.Removed = removed;
            return r;
        }

        private static void FlushChange(StringBuilder sb, List<string> del, List<string> ins, ref bool first)
        {
            for (int i = 0; i < del.Count; i++) AppendLine(sb, "-", del[i], ref first);
            for (int i = 0; i < ins.Count; i++) AppendLine(sb, "+", ins[i], ref first);
            del.Clear();
            ins.Clear();
        }

        private static void AppendLine(StringBuilder sb, string prefix, string text, ref bool first)
        {
            if (!first) sb.Append('\n');
            sb.Append(prefix).Append(text);
            first = false;
        }
    }
}
