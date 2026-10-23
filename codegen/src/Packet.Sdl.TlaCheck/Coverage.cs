using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Packet.Sdl.TlaCheck;

/// <summary>
/// Arm coverage: which rows of the current tables the compared runs ever
/// fired. A transition is "State/id", a subroutine path "Subroutine:id"
/// (the keys both tools record). An arm no scenario fires is an arm neither
/// checker says anything about.
/// </summary>
public static class Coverage
{
    private sealed record Arm(string Key, string Where, string Input);

    private static List<Arm> Inventory(string jsonDir)
    {
        var arms = new List<Arm>();
        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(jsonDir, "index.json")));
        foreach (var entry in index.RootElement.GetProperty("pages").EnumerateArray())
        {
            if (entry.GetProperty("machine").GetString() != "data_link") continue;
            using var page = JsonDocument.Parse(File.ReadAllText(Path.Combine(jsonDir, entry.GetProperty("file").GetString()!)));
            var root = page.RootElement;
            if (root.GetProperty("kind").GetString() == "state")
            {
                var st = root.GetProperty("state").GetString()!;
                foreach (var t in root.GetProperty("transitions").EnumerateArray())
                    arms.Add(new Arm(st + "/" + t.GetProperty("id").GetString(), st, t.GetProperty("on").GetString()!));
            }
            else
            {
                foreach (var sub in root.GetProperty("subroutines").EnumerateArray())
                {
                    var name = sub.GetProperty("name").GetString()!;
                    foreach (var p in sub.GetProperty("paths").EnumerateArray())
                        arms.Add(new Arm(name + ":" + p.GetProperty("id").GetString(), "subroutine " + name, ""));
                }
            }
        }
        return arms;
    }

    public static string Report(string currentDir, IReadOnlyList<(IReadOnlySet<string> Explorer, IReadOnlySet<string> Tlc)> runs)
    {
        var arms = Inventory(currentDir);
        var transitions = arms.Where(a => a.Key.Contains('/', StringComparison.Ordinal)).ToList();
        var paths = arms.Where(a => !a.Key.Contains('/', StringComparison.Ordinal)).ToList();
        var ex = runs.SelectMany(r => r.Explorer).ToHashSet(StringComparer.Ordinal);
        var tlc = runs.SelectMany(r => r.Tlc).ToHashSet(StringComparer.Ordinal);
        int Count(IEnumerable<Arm> set, HashSet<string> fired) => set.Count(a => fired.Contains(a.Key));
        string Pct(int n, int d) => d == 0 ? "0" : (100.0 * n / d).ToString("0", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        var tlcT = Count(transitions, tlc);
        var exT = Count(transitions, ex);
        var tlcP = Count(paths, tlc);
        var all = tlcT + tlcP;
        sb.Append(CultureInfo.InvariantCulture, $"Arm coverage over the current tables, across the {runs.Count} scenario(s) run on them: {all} of {arms.Count} rows fired ({Pct(all, arms.Count)}%).\n");
        sb.Append(CultureInfo.InvariantCulture, $"  transitions: TLC {tlcT} of {transitions.Count}, explorer {exT} of {transitions.Count}{(tlcT == exT && transitions.All(a => tlc.Contains(a.Key) == ex.Contains(a.Key)) ? " (the same set)" : " (the sets differ)")}\n");
        sb.Append(CultureInfo.InvariantCulture, $"  subroutine paths: TLC {tlcP} of {paths.Count} (the explorer does not report paths)\n");
        sb.Append("Never fired, by state and input:\n");
        foreach (var g in arms.Where(a => !tlc.Contains(a.Key) && !ex.Contains(a.Key)).GroupBy(a => a.Where))
        {
            sb.Append("  ").Append(g.Key).Append('\n');
            foreach (var byInput in g.GroupBy(a => a.Input))
                sb.Append("    ").Append(byInput.Key.Length == 0 ? "(paths)" : byInput.Key).Append(": ")
                  .Append(string.Join(", ", byInput.Select(a => a.Key[(a.Key.IndexOfAny(['/', ':']) + 1)..]))).Append('\n');
        }
        return sb.ToString();
    }
}
