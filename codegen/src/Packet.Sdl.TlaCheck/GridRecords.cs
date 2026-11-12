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
            ["done"] = r.TlcKind is not ("error" or "timeout") && r.ExplorerKind.Length > 0,
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
            (double?)o["explorerSeconds"] ?? 0, (double?)o["tlcSeconds"] ?? 0);
    }

    /// <summary>A lockstep cell.</summary>
    public static JsonObject Lockstep(string cell, int index, string meaning, LockstepRow r) => new()
    {
        ["cell"] = cell,
        ["index"] = index,
        ["mode"] = "lockstep",
        ["meaning"] = meaning,
        ["done"] = r.Error.Length == 0,
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
    public static string Summary(IReadOnlyList<Row> rows, int skipped)
    {
        var sb = new StringBuilder();
        var agree = rows.Count(r => r.Agree);
        var noVerdict = rows.Count(r => r.TlcKind is "error" or "timeout" || r.ExplorerKind.Length == 0);
        sb.Append(CultureInfo.InvariantCulture, $"Grid summary: {rows.Count} cell(s), {agree} agree, {rows.Count - agree - noVerdict} disagree, {noVerdict} without a verdict (rerun them with --resume)");
        if (skipped > 0) sb.Append(CultureInfo.InvariantCulture, $"; {skipped} read from the results file");
        sb.Append(".\n");
        sb.Append("Verdicts (explorer): ").Append(string.Join(", ", rows.Where(r => r.ExplorerKind.Length > 0).GroupBy(r => r.ExplorerKind)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count().ToString(CultureInfo.InvariantCulture)}"))).Append(".\n");
        var capped = rows.Count(r => r.ExplorerKind == "bound");
        if (capped > 0) sb.Append(CultureInfo.InvariantCulture, $"{capped} cell(s) hit the explorer's 1.2 million state cap (TLC has none): a disagreement there is the cap, not a finding.\n");
        var largest = rows.OrderByDescending(r => r.TlcStates).FirstOrDefault();
        sb.Append(CultureInfo.InvariantCulture, $"Time: explorer {rows.Sum(r => r.ExplorerSeconds):0} s, TLC {rows.Sum(r => r.TlcSeconds):0} s in all; largest cell {largest?.TlcStates ?? 0} distinct states.\n");
        return sb.ToString();
    }
}
