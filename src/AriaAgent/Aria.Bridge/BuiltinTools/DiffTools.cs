using System.Text;

namespace Aria.Bridge;

/// <summary>
/// Simple line-oriented unified diff used by the builtin file tools. No external dependency.
/// Uses classic Wagner-Fischer DP — acceptable because these diffs are of a single edited file
/// (typically a few hundred lines), not huge baselines.
/// </summary>
public static class DiffTools
{
    public const int DefaultContext = 3;
    public const int PreImageCap = 512 * 1024; // bytes

    public static DiffResult ComputeUnifiedDiff(
        string?[] beforeLines,
        string?[] afterLines,
        string path,
        int context = DefaultContext)
    {
        var edits = BuildEditScript(beforeLines, afterLines);
        var hunks = BuildHunks(edits, context);

        var adds = edits.Count(e => e.Op == DiffOp.Insert);
        var dels = edits.Count(e => e.Op == DiffOp.Delete);

        var sb = new StringBuilder();
        var beforeLabel = beforeLines.Length == 0 ? "/dev/null" : $"a/{path}";
        var afterLabel = afterLines.Length == 0 ? "/dev/null" : $"b/{path}";
        sb.AppendLine($"--- {beforeLabel}");
        sb.AppendLine($"+++ {afterLabel}");

        foreach (var hunk in hunks)
            hunk.WriteTo(sb);

        return new DiffResult(sb.ToString(), adds, dels);
    }

    private static List<Edit> BuildEditScript(string?[] a, string?[] b)
    {
        var n = a.Length;
        var m = b.Length;

        // dp[i,j] = length of shortest edit script for a[0..i-1] and b[0..j-1].
        var dp = new int[n + 1, m + 1];
        for (var i = 1; i <= n; i++) dp[i, 0] = i;
        for (var j = 1; j <= m; j++) dp[0, j] = j;

        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= m; j++)
            {
                if (Equals(a[i - 1], b[j - 1]))
                    dp[i, j] = dp[i - 1, j - 1];
                else
                    dp[i, j] = 1 + Math.Min(dp[i - 1, j], dp[i, j - 1]);
            }

        // Backtrack to recover the edit script in forward order.
        var edits = new List<Edit>();
        var (i2, j2) = (n, m);
        while (i2 > 0 || j2 > 0)
        {
            if (i2 > 0 && j2 > 0 && Equals(a[i2 - 1], b[j2 - 1]))
            {
                edits.Add(new Edit(DiffOp.Equal, a[i2 - 1], b[j2 - 1]));
                i2--; j2--;
            }
            else if (j2 == 0 || (i2 > 0 && dp[i2 - 1, j2] <= dp[i2, j2 - 1]))
            {
                edits.Add(new Edit(DiffOp.Delete, a[i2 - 1], null));
                i2--;
            }
            else
            {
                edits.Add(new Edit(DiffOp.Insert, null, b[j2 - 1]));
                j2--;
            }
        }

        edits.Reverse();
        return edits;
    }

    private static List<Hunk> BuildHunks(List<Edit> edits, int context)
    {
        var hunks = new List<Hunk>();
        var changeIndices = edits
            .Select((e, idx) => (e, idx))
            .Where(x => x.e.Op != DiffOp.Equal)
            .Select(x => x.idx)
            .ToList();

        if (changeIndices.Count == 0)
            return hunks;

        var lastHunkEnd = -1;
        foreach (var idx in changeIndices)
        {
            var hunkStart = Math.Max(0, idx - context);
            var hunkEnd = Math.Min(edits.Count - 1, idx + context);

            if (hunkStart <= lastHunkEnd + 1 && hunks.Count > 0)
            {
                hunks[^1].EndIndex = Math.Max(hunks[^1].EndIndex, hunkEnd);
            }
            else
            {
                hunks.Add(new Hunk(hunkStart, hunkEnd));
            }

            lastHunkEnd = hunks[^1].EndIndex;
        }

        foreach (var hunk in hunks)
            hunk.Populate(edits);

        return hunks;
    }

    private enum DiffOp { Equal, Insert, Delete }

    private sealed record Edit(DiffOp Op, string? OldLine, string? NewLine);

    private sealed class Hunk
    {
        public int StartIndex { get; }
        public int EndIndex { get; set; }
        private readonly List<string> _lines = [];
        private int _startA;
        private int _startB;
        private int _countA;
        private int _countB;

        public Hunk(int startIndex, int endIndex)
        {
            StartIndex = startIndex;
            EndIndex = endIndex;
        }

        public void Populate(List<Edit> edits)
        {
            // Compute starting line numbers by counting pre-hunk equal/delete/insert operations.
            _startA = 1;
            _startB = 1;
            for (var i = 0; i < StartIndex; i++)
            {
                var e = edits[i];
                if (e.Op is DiffOp.Equal or DiffOp.Delete) _startA++;
                if (e.Op is DiffOp.Equal or DiffOp.Insert) _startB++;
            }

            // If the hunk starts with an insert, the before-side line number is the line after which
            // the insert happens; unified diff shows 0 in that case.
            if (edits[StartIndex].Op == DiffOp.Insert) _startA--;
            if (edits[StartIndex].Op == DiffOp.Delete) _startB--;

            for (var i = StartIndex; i <= EndIndex && i < edits.Count; i++)
            {
                var e = edits[i];
                switch (e.Op)
                {
                    case DiffOp.Equal:
                        _lines.Add(" " + e.NewLine);
                        _countA++;
                        _countB++;
                        break;
                    case DiffOp.Insert:
                        _lines.Add("+" + e.NewLine);
                        _countB++;
                        break;
                    case DiffOp.Delete:
                        _lines.Add("-" + e.OldLine);
                        _countA++;
                        break;
                }
            }
        }

        public void WriteTo(StringBuilder sb)
        {
            sb.AppendLine($"@@ -{_startA},{_countA} +{_startB},{_countB} @@");
            foreach (var line in _lines)
                sb.AppendLine(line);
        }
    }
}

public sealed record DiffResult(string Diff, int Adds, int Dels);
