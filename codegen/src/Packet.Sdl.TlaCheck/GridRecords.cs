using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Packet.Sdl.TlaCheck;

/// <summary>
/// The lines of a grid run's results file (<see cref="ResultsFile"/>) and the
/// summary printed at the end. A recorded cell is read back into the same row
/// the run would have made, so a resumed run prints the same report.
/// </summary>
internal static class GridRecords
{
    private static JsonArray Strings(IEnumerable<string> items) => new(items.Order(StringComparer.Ordinal).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());

    private static HashSet<string> Set(JsonNode? array) =>
        array is JsonArray a ? a.Select(n => (string)n!).ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);

    private static double Round(double seconds) => Math.Round(seconds, 1);

    /// <summary>
    /// A cross-check cell. The arms are stored once: TLC's (transitions and
    /// subroutine paths), and the transitions only one tool fired (on an
    /// agreeing clean cell, none).
    /// </summary>
    public static JsonObject Cross(string cell, int index, string meaning, Row r)
    {
        var tlcTransitions = r.TlcArms.Where(a => a.Contains('/', StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        return new JsonObject
        {
            ["cell"] = cell,
            ["index"] = index,
            ["mode"] = "cross",
            ["meaning"] = meaning,
            ["done"] = r.TlcKind != "error" && r.ExplorerKind.Length > 0,
            ["timedOut"] = r.TlcKind == "timeout",
            ["compared"] = r.Capped.Length == 0,
            ["capped"] = r.Capped,
            ["cappedFinding"] = r.CappedFinding,
            ["agree"] = r.Agree,
            ["explorerKind"] = r.ExplorerKind,
            ["tlcKind"] = r.TlcKind,
            ["explorer"] = r.Explorer,
            ["tlc"] = r.Tlc,
            ["why"] = r.Why,
            ["explorerStates"] = r.ExplorerStates,
            ["tlcStates"] = r.TlcStates,
            ["explorerSeconds"] = Round(r.ExplorerSeconds),
            ["tlcSeconds"] = Round(r.TlcSeconds),
            ["arms"] = Strings(r.TlcArms),
            ["explorerOnly"] = Strings(r.ExplorerArms.Where(a => !tlcTransitions.Contains(a))),
            ["tlcOnly"] = Strings(tlcTransitions.Where(a => !r.ExplorerArms.Contains(a))),
            ["finished"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>A recorded cross-check cell as the row the run would have made.</summary>
    public static Row Row(JsonObject o)
    {
        var tlcArms = Set(o["arms"]);
        var tlcOnly = Set(o["tlcOnly"]);
        var explorerArms = tlcArms.Where(a => a.Contains('/', StringComparison.Ordinal) && !tlcOnly.Contains(a)).Concat(Set(o["explorerOnly"])).ToHashSet(StringComparer.Ordinal);
        return new Row((string?)o["explorer"] ?? "", (string?)o["tlc"] ?? "", (bool?)o["agree"] ?? false, (string?)o["why"] ?? "", explorerArms, tlcArms, null,
            (string?)o["explorerKind"] ?? "", (string?)o["tlcKind"] ?? "", (long?)o["explorerStates"] ?? 0, (long?)o["tlcStates"] ?? 0,
            (double?)o["explorerSeconds"] ?? 0, (double?)o["tlcSeconds"] ?? 0, (string?)o["capped"] ?? "");
    }

    /// <summary>A lockstep cell.</summary>
    public static JsonObject Lockstep(string cell, int index, string meaning, int iterate, LockstepRow r) => new()
    {
        ["cell"] = cell,
        ["index"] = index,
        ["mode"] = "lockstep",
        ["meaning"] = meaning,
        ["iterate"] = iterate,
        ["done"] = r.Error.Length == 0 || TimedOut(r),
        ["timedOut"] = TimedOut(r),
        ["result"] = r.Error.Length > 0 ? "error" : r.Diffs.Count == 0 ? "same" : "differ",
        ["states"] = r.States,
        ["exhausted"] = r.Exhausted,
        ["error"] = r.Error,
        ["seconds"] = Round(r.Seconds),
        ["diffs"] = new JsonArray(r.Diffs.Select(d => (JsonNode?)new JsonObject
        {
            ["signature"] = d.Signature,
            ["steps"] = Ordered(d.Steps),
            ["detail"] = d.Detail,
        }).ToArray()),
        ["finished"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
    };

    private static bool TimedOut(LockstepRow r) => r.Error.Contains("timeout:", StringComparison.Ordinal);

    /// <summary>The checkout's commit (git rev-parse HEAD), or "unknown".</summary>
    public static string Commit(string repoRoot)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = repoRoot, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("HEAD");
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return "unknown";
            var head = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 && head.Length > 0 ? head : "unknown";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
    }

    // Steps keep their order.
    private static JsonArray Ordered(IEnumerable<string> items) => new(items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());

    /// <summary>A recorded lockstep cell as the row the run would have made.</summary>
    public static LockstepRow LockstepRow(JsonObject o)
    {
        var diffs = (o["diffs"] as JsonArray ?? new JsonArray()).Select(n => n!.AsObject()).Select(d => new LockstepDiff(
            (string?)d["signature"] ?? "",
            (d["steps"] as JsonArray ?? new JsonArray()).Select(x => (string)x!).ToList(),
            (string?)d["detail"] ?? "")).ToList();
        return new LockstepRow(diffs, (long?)o["states"] ?? 0, (bool?)o["exhausted"] ?? false, (string?)o["error"] ?? "", (double?)o["seconds"] ?? 0);
    }

    /// <summary>The closing summary of a cross-check grid run: the verdicts, agreement and the time spent.</summary>
    public static string Summary(IReadOnlyList<(string Name, Row Row)> cells, int skipped)
    {
        var rows = cells.Select(c => c.Row).ToList();
        var sb = new StringBuilder();
        var capped = rows.Where(r => r.Capped.Length > 0).ToList();
        var compared = rows.Where(r => r.Capped.Length == 0).ToList();
        var agree = compared.Count(r => r.Agree);
        var failed = compared.Count(r => r.TlcKind == "error" || r.ExplorerKind.Length == 0);
        var timedOut = compared.Count(r => r.TlcKind == "timeout" && r.ExplorerKind.Length > 0);
        sb.Append(CultureInfo.InvariantCulture, $"Grid summary: {rows.Count} cell(s): {agree} agree, {compared.Count - agree - failed - timedOut} disagree, {capped.Count} capped (not compared), {timedOut} timed out (rerun with --resume --retry-timeouts and a larger --timeout), {failed} failed (rerun with --resume)");
        if (skipped > 0) sb.Append(CultureInfo.InvariantCulture, $"; {skipped} read from the results file");
        sb.Append(".\n");
        sb.Append("Verdicts (explorer): ").Append(string.Join(", ", rows.Where(r => r.ExplorerKind.Length > 0).GroupBy(r => r.ExplorerKind)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count().ToString(CultureInfo.InvariantCulture)}"))).Append(".\n");
        if (capped.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"Capped, not compared: {capped.Count(r => r.Capped == "states")} at the explorer's state cap, {capped.Count(r => r.Capped == "depth")} at its depth limit (TLC has neither).\n");
            foreach (var (name, r) in cells.Where(c => c.Row.CappedFinding))
                sb.Append(CultureInfo.InvariantCulture, $"FINDING to look at: {name} is capped in the explorer, and TLC found {r.Tlc}\n");
        }
        var largest = rows.OrderByDescending(r => r.TlcStates).FirstOrDefault();
        sb.Append(CultureInfo.InvariantCulture, $"Time: explorer {rows.Sum(r => r.ExplorerSeconds):0} s, TLC {rows.Sum(r => r.TlcSeconds):0} s in all; largest cell {largest?.TlcStates ?? 0} distinct states.\n");
        return sb.ToString();
    }
}
