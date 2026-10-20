using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Packet.Sdl.Explorer;
using Packet.Sdl.Interpreter;
using Packet.Sdl.TlaCheck;

var usage = """
usage: dotnet run --project codegen/src/Packet.Sdl.TlaCheck -- --jar PATH [options]

Runs the explorer and TLC on every calibration scenario and reports, row by
row, whether they agree: the same verdict, the same invariant, the same
station, the same counterexample length and transition sequence (safety and
deadlock), and the same number of distinct states (clean runs).

  --jar PATH        tla2tools.jar (default: $TLA2TOOLS_JAR)
  --work DIR        model directories (default: <temp>/ax25sdl-tlacheck)
  --only LIST       comma list of scenario names
  --list            print the scenario names and exit
  --parallel N      scenarios run at once (default 1)
  --workers N       TLC workers per run (default 1; >1 loses the shortest-trace guarantee)
  --heap SIZE       TLC heap per run (default 2g)
  --timeout S       seconds per TLC run (default 600)

exit code: 0 every row agrees, 1 some row disagrees or a run failed
""";

string? jar = Environment.GetEnvironmentVariable("TLA2TOOLS_JAR");
var work = Path.Combine(Path.GetTempPath(), "ax25sdl-tlacheck");
HashSet<string>? only = null;
var list = false;
int parallel = 1, workers = 1, timeout = 600;
var heap = "2g";

for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--help" or "-h": Console.WriteLine(usage); return 0;
        case "--jar": jar = Next(); break;
        case "--work": work = Next(); break;
        case "--only": only = new HashSet<string>(Next().Split(','), StringComparer.Ordinal); break;
        case "--list": list = true; break;
        case "--parallel": parallel = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--workers": workers = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--heap": heap = Next(); break;
        case "--timeout": timeout = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        default: Console.Error.WriteLine($"unknown option {args[i]}\n\n{usage}"); return 1;
    }
}

var root = RepoRoot();
string Dir(TableSetKind t) => t switch
{
    TableSetKind.Current => Path.Combine(root, "spec", "json"),
    TableSetKind.Pre40 => Path.Combine(root, "codegen", "tests", "Packet.Sdl.Explorer.Tests", "fixtures", "tables-pre40"),
    _ => Path.Combine(root, "codegen", "tests", "Packet.Sdl.Explorer.Tests", "fixtures", "tables-pre38"),
};

var scenarios = Scenarios.Calibration(Dir).Where(s => only is null || only.Contains(s.Name)).ToList();
if (list)
{
    foreach (var s in scenarios) Console.WriteLine($"{s.Name,-22} {s.Defect,-18} {s.Tables,-8} {s.Expect}");
    return 0;
}
if (jar is null || !File.Exists(jar))
{
    Console.Error.WriteLine("tla2tools.jar not found: pass --jar PATH or set TLA2TOOLS_JAR");
    return 1;
}

var tlc = new Tlc(jar,
    Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "TwoStation.tla"),
    Path.Combine(root, "spec", "tla", "Ax25Semantics.tla"),
    workers, heap, TimeSpan.FromSeconds(timeout));

var results = new ConcurrentDictionary<int, Row>();
Parallel.For(0, scenarios.Count, new ParallelOptions { MaxDegreeOfParallelism = parallel }, idx =>
{
    var row = Compare(scenarios[idx]);
    results[idx] = row;
    Console.Error.WriteLine($"{(row.Agree ? "agree   " : "DISAGREE")} {scenarios[idx].Name}: explorer {row.Explorer}; TLC {row.Tlc}{(row.Why.Length > 0 ? " (" + row.Why + ")" : "")}");
});

var sb = new StringBuilder();
sb.Append("| Scenario | Defect | Tables | Explorer | TLC | Agree |\n| --- | --- | --- | --- | --- | --- |\n");
for (var idx = 0; idx < scenarios.Count; idx++)
{
    var s = scenarios[idx];
    var r = results[idx];
    sb.Append(CultureInfo.InvariantCulture, $"| {s.Name} | {s.Defect} | {Tables(s.Tables)} | {r.Explorer} | {r.Tlc} | {(r.Agree ? "yes" : "**no**: " + r.Why)} |\n");
}
Console.WriteLine(sb.ToString());
return results.Values.All(r => r.Agree) ? 0 : 1;

// ─── One row ──────────────────────────────────────────────────────────

Row Compare(Scenario s)
{
    var o = s.Options;
    var ex = Explorer.Run(o, TableLoader.Load(o.TablesDir));
    var exKind = ex.Outcome switch
    {
        Outcome.NoViolation => "clean",
        Outcome.BoundHit => "bound",
        _ => ex.Violation!.Kind.ToString(),
    };
    var exStation = ex.Violation?.Station?.ToString() ?? "";
    var exTids = ex.Trace.Select(t => Norm(t.Transition)).ToList();
    var exText = ex.Outcome switch
    {
        Outcome.NoViolation => $"clean, {ex.StatesExplored} states",
        Outcome.BoundHit => $"depth bound hit, {ex.StatesExplored} states",
        _ => $"{exKind}{(exStation.Length > 0 ? " at " + exStation : "")}, {ex.CounterexampleLength} steps",
    };

    var dir = Path.Combine(work, s.Name);
    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    tlc.Prepare(dir, o.TablesDir);
    var depthBound = s.Name == "d42-both-off";
    var elapsed = TimeSpan.Zero;

    var safety = tlc.Run(dir, o, Phase.Safety, depthBound);
    elapsed += safety.Elapsed;
    var (kind, station, steps, tids, note) = Verdict(safety, Phase.Safety, o, depthBound);
    var states = safety.Distinct;

    if (kind == "clean" && o.Invariants.HasFlag(Invariants.Deadlock))
    {
        var dl = tlc.Run(dir, o, Phase.Deadlock, false);
        elapsed += dl.Elapsed;
        (kind, station, steps, tids, note) = Verdict(dl, Phase.Deadlock, o, false);
    }
    if (kind == "clean" && o.Invariants.HasFlag(Invariants.Quiescence))
    {
        var q = tlc.Run(dir, o, Phase.Quiescence, false);
        elapsed += q.Elapsed;
        (kind, station, steps, tids, note) = Verdict(q, Phase.Quiescence, o, false);
        if (kind == "clean" && o.Invariants.HasFlag(Invariants.SelectiveProgress))
        {
            var tf = tlc.Run(dir, o, Phase.TimerFree, false);
            elapsed += tf.Elapsed;
            (kind, station, steps, tids, note) = Verdict(tf, Phase.TimerFree, o, false);
        }
    }

    var secs = elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
    var tlcText = kind switch
    {
        "clean" => $"clean, {states} states ({secs} s)",
        "bound" => $"depth bound hit, {states} states ({secs} s)",
        "Quiescence" or "SelectiveProgress" => $"{kind}, lasso of {steps} states ({secs} s)",
        "error" or "timeout" => $"{kind}: {note}",
        _ => $"{kind}{(station.Length > 0 ? " at " + station : "")}, {steps} steps ({secs} s)",
    };

    // Agreement: the same verdict; for a safety or deadlock counterexample the
    // same station, length and transition sequence; for a clean or bounded
    // run the same number of distinct states.
    var why = new List<string>();
    if (kind != exKind) why.Add($"verdict {exKind} vs {kind}");
    else if (kind is "clean" or "bound")
    {
        if (states != ex.StatesExplored) why.Add($"states {ex.StatesExplored} vs {states}");
    }
    else if (kind is not ("Quiescence" or "SelectiveProgress"))
    {
        if (exStation.Length > 0 && exStation != station) why.Add($"station {exStation} vs {station}");
        if (steps != ex.CounterexampleLength) why.Add($"length {ex.CounterexampleLength} vs {steps}");
        else if (tids.Count != steps) why.Add($"TLC trace has {tids.Count} steps, expected {steps}");
        else
        {
            var first = Enumerable.Range(0, steps).FirstOrDefault(j => exTids[j] != tids[j], -1);
            if (first >= 0) why.Add($"step {first + 1}: transition {exTids[first]} vs {tids[first]}");
        }
    }
    return new Row(exText, tlcText, why.Count == 0, string.Join("; ", why));
}

static (string Kind, string Station, int Steps, List<string> Tids, string Note) Verdict(TlcResult r, Phase phase, ExplorerOptions o, bool depthBound)
{
    var steps = Math.Max(0, r.Trace.Count - 1);
    var tids = r.Trace.Skip(1).Select(t => Norm(t.Field("tid"))).ToList();
    var lastStep = r.Trace.Count > 0 ? r.Trace[^1] : null;
    switch (r.Kind)
    {
        case "clean":
            // A depth-bounded run is the explorer's "bound hit" when a state at the bound exists.
            return (depthBound && r.Depth > o.MaxDepth ? "bound" : "clean", "", 0, tids, "");
        case "invariant":
            return r.Invariant == "NoStepViolation"
                ? (lastStep!.Field("viol"), lastStep.Field("violAt"), steps, tids, "")
                : (r.Invariant!, lastStep?.Field("at") ?? "", steps, tids, "");
        case "deadlock":
            return ("Deadlock", "", steps, tids, "");
        case "temporal":
            // The lasso: the states TLC prints, the last being "Back to state n" or a stuttering marker.
            return (phase == Phase.TimerFree ? "SelectiveProgress" : "Quiescence", "", r.Trace.Count, tids, "");
        default:
            return (r.Kind, "", 0, tids, r.Message);
    }
}

// The explorer reports the stub peer's rule with its citation; the transition id is the part before it.
static string Norm(string? tid)
{
    if (string.IsNullOrEmpty(tid)) return "";
    var cut = tid.IndexOf(" (", StringComparison.Ordinal);
    return cut >= 0 ? tid[..cut] : tid;
}

static string Tables(TableSetKind t) => t switch
{
    TableSetKind.Current => "current",
    TableSetKind.Pre40 => "pre-40",
    _ => "pre-38",
};

static string RepoRoot()
{
    var d = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "spec-sdl"))) d = d.Parent;
    return d?.FullName ?? throw new InvalidOperationException("run from inside the ax25sdl checkout: no spec-sdl/ in any ancestor of the working directory");
}

internal sealed record Row(string Explorer, string Tlc, bool Agree, string Why);
