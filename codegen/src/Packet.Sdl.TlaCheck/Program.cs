using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Packet.Sdl.Explorer;
using Packet.Sdl.Interpreter;
using Packet.Sdl.TlaCheck;

var usageText = """
usage: dotnet run --project codegen/src/Packet.Sdl.TlaCheck -- --jar PATH [options]

Runs the explorer and TLC on every calibration scenario and reports, row by
row, whether they agree: the same verdict, the same invariant, the same
station, the same counterexample length and transition sequence (safety and
deadlock), and the same number of distinct states (clean runs). Each
calibration row also states its expected verdict (Scenarios.cs): the kind,
and where the row states them the station, the explorer's counterexample
length and the number of distinct states. A row whose verdict differs from
its expectation fails, even where the two tools agree.

  --jar PATH        tla2tools.jar (default: $TLA2TOOLS_JAR)
  --work DIR        model directories (default: <temp>/ax25sdl-tlacheck)
  --only LIST       comma list of scenario names
  --list            print the scenario names and exit
  --grid            the explorer's 576-cell data-phase grid instead of the calibration suite
  --grid-flow       the same grid with B's layer 3 flow-controlling
  --grid-stub       the explorer's 72-cell v2.0 stub-peer grid (connect phase)
  --grid-with LIST  apply an option set to every grid cell: a comma list of
                    the explorer's option names without the dashes, bare or
                    NAME=VALUE (a list value joined with +), for example
                    t3,release=A or frame-errors=control+info; repeatable.
                    Refused combinations fail before anything runs. The
                    grid-compatible options (docs/tla.md, "Grid runs"):
{GRIDHELP}
  --grid-max-states N
                    grid runs: the explorer's visited-state cap for every cell
                    (default 1200000 on --grid and --grid-flow, 400000 on
                    --grid-stub); 0 for no cap. The cell names do not change
  --grid-max-depth N
                    grid runs: the explorer's depth limit for every cell
                    (default 80); 0 for no limit
  --grid-cells-from FILE
                    run the cells FILE names, one full cell name per line
                    (with its @options suffix; # starts a comment), from any
                    mix of --grid, --grid-flow and --grid-stub cells and
                    option sets: each cell runs with the options its name
                    carries. Not with --grid, --grid-flow, --grid-stub,
                    --grid-with or --grid-cells
  --grid-cells SPEC run only these grid cells: a comma list of indices (12),
                    ranges (0-143), strides (%4=1: index mod 4 = 1) and name
                    patterns (g-ab3-*); indices are those --list prints
  --results FILE    grid runs: append one JSON line per cell to FILE, flushed
                    as each cell completes; refuses an existing FILE unless
                    --resume
  --resume          with --results: skip the cells FILE already records (a
                    cell whose run failed runs again; a timed-out cell only
                    with --retry-timeouts)
  --retry-timeouts  with --resume: also rerun the cells recorded as timed out
                    (worth it only with a larger --timeout)
  --checkpoint-minutes N
                    grid runs: TLC checkpoints its Safety, Deadlock and
                    liveness passes every N minutes (default 30; 0 for none),
                    and a cell's model directory keeps each finished pass's
                    verdict, so --resume continues an unfinished cell: the
                    passes it finished are read back, an interrupted pass
                    restarts from its last checkpoint, and the explorer's
                    result is reused (docs/tla.md, "Grid runs"). Keep --work
                    on a disk that outlives the machine. SIGTERM or Ctrl-C
                    stops TlaCheck and its TLC runs, keeping their checkpoints
  --progress        read only: for each cell directory under --work, the TLC
                    passes finished and their verdicts, the pass in progress,
                    how long it has run, its latest progress line (distinct
                    states, queue, new states per minute) and its latest
                    checkpoint. Runs nothing else; safe while a run goes on
  --parallel N      scenarios run at once (default 1)
  --workers N       TLC workers per run (default 1; >1 loses the shortest-trace guarantee)
  --heap SIZE       TLC heap per run (default 2g)
  --timeout S       seconds per TLC run (default 600; 0 for no limit)
  --meaning M       the meaning module the runner is instantiated with: a
                    module name in spec/tla or a path to a .tla file
                    (default Ax25Meaning; see docs/tla.md)
  --lockstep        run Ax25Meaning and the --meaning module side by side
                    instead of the cross-check: every dispatch goes through
                    both, and a row stops at the first dispatch on which their
                    effects, error flag or fixed fields differ (docs/tla.md,
                    "Lockstep"); the explorer is not run
  --lockstep-iterate N
                    with --lockstep, look past each difference found and run
                    the row again, up to N runs, to find the next distinct
                    difference (default 1: the first difference only)
  --coverage-only   grid runs: run only the Coverage phase of each selected
                    cell (no explorer, no Safety, Deadlock or liveness
                    phases), for cells whose coverage was cut; with --results
                    each cell's line has mode "coverage", coverageOnly true,
                    the arms TLC fired and coverageSeconds, to merge into the
                    cell's earlier record (docs/tla.md, "Grid runs"); use a
                    results file of its own; only worth running for uncapped
                    cells, as a cross-check run skips a capped cell's coverage
  --merge-coverage CROSS COVERAGE OUT
                    write OUT: the cross-check results file CROSS with the
                    arms of each cell the --coverage-only results file
                    COVERAGE read, and explorerOnly and tlcOnly recomputed;
                    the verdicts are kept. Runs nothing else

A full calibration run (no --only, no --grid) also checks the accounting:
the rows no scenario fires must be exactly those unfired.txt records as
not fired, each with its status and reason.

A grid run deletes each cell's model directory once the cell agrees (or,
with --lockstep, shows no difference), so the disk holds only the cells in
progress and the ones worth reading. A grid cell whose explorer search was
cut short by its state cap or depth limit (TLC has neither) is "capped",
whatever the explorer's verdict: the state counts and counterexamples are not
compared, only the verdict kinds where the explorer found a violation (a
reachable state, so TLC must report the same kind). It does not count as a
disagreement unless TLC then reports clean or another kind; one where TLC
found a violation the explorer did not is flagged. A capped cell's Coverage
pass is not run (its arms are never compared, so it cannot change the result;
on the largest cells it is the slowest pass): the record says coverageSkipped
and the report counts the cells skipped.

exit code: 0 every row agrees and meets its expectation (and the accounting
holds), 1 some row disagrees, misses its expectation, a run failed, or the
accounting does not hold; with --lockstep,
0 the two meanings never differed on any row, 1 they did or a run failed
""";
var usage = usageText.Replace("{GRIDHELP}", GridOptions.Help(), StringComparison.Ordinal);

string? jar = Environment.GetEnvironmentVariable("TLA2TOOLS_JAR");
var work = Path.Combine(Path.GetTempPath(), "ax25sdl-tlacheck");
HashSet<string>? only = null;
var list = false;
var grid = false;
var gridFlow = false;
var gridStub = false;
var gridWith = new List<string>();
string? gridCells = null;
string? gridCellsFrom = null;
int? gridMaxStates = null, gridMaxDepth = null;
string? resultsPath = null;
var resume = false;
var retryTimeouts = false;
int parallel = 1, workers = 1, timeout = 600;
var heap = "2g";
var meaning = Tlc.DefaultMeaning;
var lockstep = false;
var iterate = 1;
var coverageOnly = false;
string[]? mergeCoverage = null;
int? checkpointMinutes = null;
var progress = false;

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
        case "--grid": grid = true; break;
        case "--grid-flow": grid = true; gridFlow = true; break;
        case "--grid-stub": gridStub = true; break;
        case "--grid-with": gridWith.Add(Next()); break;
        case "--grid-cells": gridCells = Next(); break;
        case "--grid-cells-from": gridCellsFrom = Next(); break;
        case "--grid-max-states": gridMaxStates = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--grid-max-depth": gridMaxDepth = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--results": resultsPath = Next(); break;
        case "--resume": resume = true; break;
        case "--retry-timeouts": retryTimeouts = true; break;
        case "--parallel": parallel = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--workers": workers = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--heap": heap = Next(); break;
        case "--timeout": timeout = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--meaning": meaning = Next(); break;
        case "--lockstep": lockstep = true; break;
        case "--lockstep-iterate": iterate = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--coverage-only": coverageOnly = true; break;
        case "--merge-coverage": mergeCoverage = [Next(), Next(), Next()]; break;
        case "--progress": progress = true; break;
        case "--checkpoint-minutes":
            try { checkpointMinutes = Checkpoint.ParseMinutes(Next()); }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
            break;
        default: Console.Error.WriteLine($"unknown option {args[i]}\n\n{usage}"); return 1;
    }
}

if (progress)
{
    Console.Write(Progress.Report(work, DateTime.Now));
    return 0;
}
if (mergeCoverage is not null)
{
    try { return CoverageOnly.Merge(mergeCoverage[0], mergeCoverage[1], mergeCoverage[2]); }
    catch (IOException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

var root = RepoRoot();
string Dir(TableSetKind t) => t switch
{
    TableSetKind.Current => Path.Combine(root, "spec", "json"),
    TableSetKind.Pre40 => Path.Combine(root, "codegen", "tests", "Packet.Sdl.Explorer.Tests", "fixtures", "tables-pre40"),
    TableSetKind.Pre38 => Path.Combine(root, "codegen", "tests", "Packet.Sdl.Explorer.Tests", "fixtures", "tables-pre38"),
    TableSetKind.AckBroken => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-ack-broken"),
    TableSetKind.ReleaseStuck => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-release-stuck"),
    TableSetKind.DmIgnored => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-dm-ignored"),
    TableSetKind.ReconnectSwallowed => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-reconnect-swallowed"),
    TableSetKind.ConfirmAsDisconnect => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-confirm-as-disconnect"),
    TableSetKind.RnrIgnored => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-rnr-ignored"),
    TableSetKind.BusyDelivers => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-busy-delivers"),
    TableSetKind.BusySaysRr => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-busy-says-rr"),
    TableSetKind.EnquirySaysRr => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-enquiry-says-rr"),
    TableSetKind.UiSwallowed => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-ui-swallowed"),
    TableSetKind.UiUnsent => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-ui-unsent"),
    TableSetKind.UiPollUnanswered => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-ui-poll-unanswered"),
    TableSetKind.UiResponseDelivered => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-ui-response-delivered"),
    TableSetKind.UiArmDeleted => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-ui-arm-deleted"),
    TableSetKind.T3Dead => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-t3-dead"),
    TableSetKind.NoKeepalive => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-no-keepalive"),
    TableSetKind.FrameErrorUnreported => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-frame-error-unreported"),
    TableSetKind.FrameErrorArmsDeleted => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-frame-error-arms-deleted"),
    TableSetKind.ResetUnconfirmed => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-reset-unconfirmed"),
    TableSetKind.RrResets => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-rr-resets"),
    TableSetKind.ResetWrongCode => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-reset-wrong-code"),
    TableSetKind.InjectedDelivered => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-injected-delivered"),
    TableSetKind.InjectedUndelivered => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-injected-undelivered"),
    TableSetKind.DisconnectSwallowed => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-disconnect-swallowed"),
    TableSetKind.DisconnectReleasedEarly => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-disconnect-released-early"),
    TableSetKind.DisconnectUnconfirmed => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-disconnect-unconfirmed"),
    TableSetKind.DisconnectSilent => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-disconnect-silent"),
    TableSetKind.DisconnectKeptConnected => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-disconnect-kept-connected"),
    TableSetKind.Reverted169 => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-169-reverted"),
    TableSetKind.SetVersionFlagOnly => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-set-version-flag-only"),
    _ => Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-livelock"),
};

var gridMode = grid || gridStub || gridCellsFrom is not null;
if (grid && gridStub)
{
    Console.Error.WriteLine("--grid-stub is a grid of its own: not with --grid or --grid-flow");
    return 1;
}
if (gridCellsFrom is not null && (grid || gridStub || gridWith.Count > 0 || gridCells is not null))
{
    Console.Error.WriteLine("--grid-cells-from takes each cell's grid and options from its name: not with --grid, --grid-flow, --grid-stub, --grid-with or --grid-cells");
    return 1;
}
if (!gridMode && (gridWith.Count > 0 || gridCells is not null || resultsPath is not null || resume || retryTimeouts || gridMaxStates is not null || gridMaxDepth is not null))
{
    Console.Error.WriteLine("--grid-with, --grid-cells, --grid-max-states, --grid-max-depth, --results and --resume need a grid (--grid, --grid-flow, --grid-stub or --grid-cells-from)");
    return 1;
}
if (gridMaxStates < 0 || gridMaxDepth < 0 || timeout < 0)
{
    Console.Error.WriteLine("--grid-max-states, --grid-max-depth and --timeout take a non-negative number (0: no limit)");
    return 1;
}
if (CoverageOnly.Refusal(coverageOnly, gridMode, lockstep) is { } coverageRefused)
{
    Console.Error.WriteLine(coverageRefused);
    return 1;
}
if (Checkpoint.Refusal(checkpointMinutes, gridMode, lockstep, coverageOnly) is { } checkpointRefused)
{
    Console.Error.WriteLine(checkpointRefused);
    return 1;
}
if (retryTimeouts && !resume)
{
    Console.Error.WriteLine("--retry-timeouts needs --resume");
    return 1;
}
if (resume && resultsPath is null)
{
    Console.Error.WriteLine("--resume needs --results FILE");
    return 1;
}
Func<ExplorerOptions, ExplorerOptions>? gridApply = null;
var gridTag = "";
Func<int, string, bool>? cellFilter = null;
var caps = GridOptions.Caps(gridMaxStates, gridMaxDepth);
IReadOnlyList<Scenario> allScenarios;
var cellIndex = new Dictionary<string, int>(StringComparer.Ordinal);
try
{
    if (gridCellsFrom is not null)
    {
        var named = GridOptions.FromNames(File.ReadLines(gridCellsFrom), Dir(TableSetKind.Current), caps);
        allScenarios = named.Select(c => c.Cell).ToList();
        foreach (var (cell, index) in named) cellIndex[cell.Name] = index;
    }
    else
    {
        if (gridMode) (gridApply, gridTag) = GridOptions.Parse(gridWith, gridStub);
        if (gridCells is not null) cellFilter = GridOptions.CellFilter(gridCells);
        var withCaps = GridOptions.Then(gridApply, caps);
        allScenarios = gridStub ? Scenarios.StubGrid(Dir(TableSetKind.Current), withCaps, gridTag)
            : grid ? Scenarios.Grid(Dir(TableSetKind.Current), gridFlow, withCaps, gridTag)
            : Scenarios.Calibration(Dir);
        for (var i = 0; i < allScenarios.Count; i++) cellIndex[allScenarios[i].Name] = i;
    }
}
catch (Exception ex) when (ex is ArgumentException or IOException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
var scenarios = allScenarios.Where((s, i) => (only is null || only.Contains(s.Name)) && (cellFilter is null || cellFilter(i, s.Name))).ToList();
if (gridMode)
{
    if (scenarios.Count == 0)
    {
        Console.Error.WriteLine("no grid cell selected");
        return 1;
    }
    // Every cell against the stepper's refusals before anything runs.
    if (GridOptions.FirstRefusal(scenarios) is { } refused)
    {
        Console.Error.WriteLine(refused);
        return 1;
    }
}
if (list)
{
    foreach (var s in scenarios) Console.WriteLine(gridMode ? $"{cellIndex[s.Name],4} {s.Name}" : $"{s.Name,-22} {s.Defect,-18} {s.Tables,-8} {s.Expect?.Label}");
    return 0;
}
if (jar is null || !File.Exists(jar))
{
    Console.Error.WriteLine("tla2tools.jar not found: pass --jar PATH or set TLA2TOOLS_JAR");
    return 1;
}

var harness = Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "TwoStation.tla");
var tlaDir = Path.Combine(root, "spec", "tla");
if (meaning.EndsWith(".tla", StringComparison.Ordinal)) meaning = Path.GetFullPath(meaning);
if (Tlc.CheckMeaning(tlaDir, meaning, harness) is { } badMeaning)
{
    Console.Error.WriteLine(badMeaning);
    return 1;
}
var tlc = new Tlc(jar,
    harness,
    tlaDir,
    meaning,
    workers, heap, timeout == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(timeout), lockstep)
{
    // Checkpoint and resume are for the cross-check's grid cells (docs/tla.md, "Grid runs").
    CheckpointMinutes = Checkpoint.Minutes(checkpointMinutes, gridMode, lockstep, coverageOnly),
    KeepProgress = gridMode && !lockstep && !coverageOnly,
    Resume = resume,
};

// Stopped by SIGTERM or Ctrl-C: stop the TLC runs too, before anything more is
// recorded, so their checkpoints stay for --resume (docs/tla.md, "Grid runs").
void Stop(PosixSignalContext context)
{
    context.Cancel = true;
    Console.Error.WriteLine($"{context.Signal}: stopping TLC and exiting; --resume continues from the TLC checkpoints");
    Tlc.StopAll();
    Environment.Exit(context.Signal == PosixSignal.SIGINT ? 130 : 143);
}
using var onTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
using var onInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);

ResultsFile? resultsFile = null;
if (resultsPath is not null)
{
    try { resultsFile = ResultsFile.Open(resultsPath, resume, retryTimeouts, GridRecords.Commit(root)); }
    catch (IOException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}
using var resultsOwner = resultsFile;

if (lockstep) return Lockstep.Run(scenarios, tlc, work, iterate, parallel, meaning, gridMode, resultsFile, cellIndex);
if (coverageOnly) return CoverageOnly.Run(scenarios, tlc, work, parallel, meaning, resultsFile, cellIndex);

var results = new ConcurrentDictionary<int, Row>();
var explorerBuild = new Lazy<string>(() => Checkpoint.ExplorerBuild(typeof(Explorer).Assembly, typeof(TableLoader).Assembly, typeof(Packet.Sdl.IR.GuardExpression).Assembly, typeof(Tlc).Assembly));
var skipped = 0;
void Each(Action<int> body)
{
    if (gridMode) GridOptions.LargestFirst(scenarios, parallel, body);
    else Parallel.For(0, scenarios.Count, new ParallelOptions { MaxDegreeOfParallelism = parallel }, body);
}
Each(idx =>
{
    var s = scenarios[idx];
    if (resultsFile?.Done(s.Name, "cross", Lockstep.MeaningName(meaning), null) is { } done)
    {
        results[idx] = GridRecords.Row(done);
        Interlocked.Increment(ref skipped);
        return;
    }
    Row row;
    if (gridMode)
    {
        // An unattended grid run records a cell that throws and carries on.
        try { row = Compare(s); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            row = new Row("", $"error: {ex.GetType().Name}: {ex.Message}", false, "the run failed", new HashSet<string>(), new HashSet<string>(), TlcKind: "error");
        }
        resultsFile?.Append(GridRecords.Cross(s.Name, cellIndex[s.Name], Lockstep.MeaningName(meaning), row));
        if (row.Agree)
        {
            var dir = Path.Combine(work, s.Name);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
    else
    {
        row = Compare(s);
    }
    results[idx] = row;
    var label = gridMode ? GridVerdict.Label(row) : row.Agree ? "agree   " : "DISAGREE";
    Console.Error.WriteLine($"{label} {scenarios[idx].Name}: explorer {row.Explorer}; TLC {row.Tlc}{(row.Why.Length > 0 ? " (" + row.Why + ")" : "")}{(row.Resumed.Length > 0 ? " [resumed: " + row.Resumed + "]" : "")}{(row.CoverageSkipped ? " [coverage skipped: capped]" : "")}");
    if (row.Unexpected is { } unexpected) Console.Error.WriteLine($"UNEXPECTED {scenarios[idx].Name}: {unexpected}");
});

var sb = new StringBuilder();
sb.Append("| Scenario | Defect | Tables | Explorer | TLC | Agree | Expected |\n| --- | --- | --- | --- | --- | --- | --- |\n");
for (var idx = 0; idx < scenarios.Count; idx++)
{
    var s = scenarios[idx];
    var r = results[idx];
    var expected = s.Expect is null ? "" : r.Unexpected is null ? "yes" : "**no**: " + r.Unexpected;
    sb.Append(CultureInfo.InvariantCulture, $"| {s.Name} | {s.Defect} | {Tables(s.Tables)} | {r.Explorer} | {r.Tlc} | {(r.Capped.Length > 0 ? (r.Why.Length > 0 ? "capped; **verdicts differ**: " + r.Why : r.CappedFinding ? "capped, not compared; **TLC found a violation**" : "capped, not compared") + (r.CoverageSkipped ? ", coverage skipped" : "") : r.Agree ? "yes" : "**no**: " + r.Why)} | {expected} |\n");
}
Console.WriteLine(sb.ToString());
var currentRuns = scenarios.Select((s, i) => (s, results[i])).Where(x => x.s.Tables == TableSetKind.Current).Select(x => (x.Item2.ExplorerArms, x.Item2.TlcArms)).ToList();
Console.WriteLine(Coverage.Report(Dir(TableSetKind.Current), currentRuns, results.Values.Count(r => r.CoverageSkipped)));
// The accounting check (unfired.txt) needs the whole calibration suite: a
// subset or the grid fires less, so it is skipped then.
var accounted = true;
if (only is null && !gridMode)
{
    var (ok, text) = Coverage.CheckExpectedUnfired(Dir(TableSetKind.Current),
        Path.Combine(root, "codegen", "src", "Packet.Sdl.TlaCheck", "unfired.txt"), Coverage.NeverFired(Dir(TableSetKind.Current), currentRuns));
    Console.WriteLine(text);
    accounted = ok;
}
var unexpectedRows = results.Values.Count(r => r.Unexpected is not null);
if (unexpectedRows > 0) Console.WriteLine($"{unexpectedRows} row(s) differ from their expected verdict (the Expected column).");
if (gridMode) Console.WriteLine(GridRecords.Summary(scenarios.Select((s, i) => (s.Name, results[i])).ToList(), skipped));
return results.Values.All(r => GridVerdict.Passes(r) && r.Unexpected is null) && accounted ? 0 : 1;

// ─── One row ──────────────────────────────────────────────────────────

Row Compare(Scenario s)
{
    var o = s.Options;
    var dir = Path.Combine(work, s.Name);
    // What a resumed grid cell took from the run before (docs/tla.md, "Grid runs").
    var resumed = new List<string>();
    var build = explorerBuild.Value;
    ExplorerSummary? ex = null;
    if (gridMode && Checkpoint.OpenCell(dir, (o with { TablesDir = "" }).ToString(), resume, () => tlc.Prepare(dir, o.TablesDir)))
    {
        ex = Checkpoint.ReadExplorer(dir, build);
        if (ex is not null) resumed.Add("explorer reused");
    }
    if (ex is null)
    {
        var exClock = System.Diagnostics.Stopwatch.StartNew();
        var run = Explorer.Run(o, TableLoader.Load(o.TablesDir));
        ex = new ExplorerSummary(
            run.Outcome switch
            {
                Outcome.NoViolation => "clean",
                Outcome.BoundHit => "bound",
                _ => run.Violation!.Kind.ToString(),
            },
            run.Violation?.Station?.ToString() ?? "",
            run.Trace.Select(t => Norm(t.Transition)).ToList(),
            run.StatesExplored, run.CounterexampleLength, run.DepthBoundHit, run.StateBoundHit,
            run.FiredTransitions.Order(StringComparer.Ordinal).ToList(),
            exClock.Elapsed.TotalSeconds);
        if (gridMode) Checkpoint.WriteExplorer(dir, build, ex);
    }
    var exSeconds = ex.Seconds;
    var exKind = ex.Kind;
    var exStation = ex.Station;
    var exTids = ex.Tids;
    var exFired = ex.Fired.ToHashSet(StringComparer.Ordinal);
    var exText = exKind switch
    {
        "clean" => $"clean, {ex.States} states",
        // A grid cell says which limit it hit; calibration keeps its wording.
        "bound" when gridMode && !s.DepthBound => $"{(ex.DepthBoundHit ? "depth limit" : "state cap")} hit, {ex.States} states",
        "bound" => $"depth bound hit, {ex.States} states",
        _ => $"{exKind}{(exStation.Length > 0 ? " at " + exStation : "")}, {ex.Length} steps"
            // A violation found after a limit was hit (a deadlock or a loss, found in the part explored).
            + (gridMode && !s.DepthBound && (ex.DepthBoundHit || ex.StateBoundHit) ? $" ({(ex.DepthBoundHit ? "depth limit" : "state cap")} hit, {ex.States} states)" : ""),
    };

    if (!gridMode)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        tlc.Prepare(dir, o.TablesDir);
    }
    TlcResult Pass(Phase phase, bool bound)
    {
        var r = tlc.Run(dir, o, phase, bound);
        if (r.Resumed.Length > 0) resumed.Add($"{phase} {r.Resumed}");
        return r;
    }
    var depthBound = s.DepthBound;
    var elapsed = TimeSpan.Zero;

    var safety = Pass(Phase.Safety, depthBound);
    elapsed += safety.Elapsed;
    var (kind, station, steps, tids, note) = Verdict(safety, Phase.Safety, o, depthBound);
    var states = safety.Distinct;
    // The safety run explores the whole graph unless a per-step check fires,
    // and so does the explorer before it reports a deadlock or a liveness
    // verdict, so on those rows the two distinct-state counts must match too:
    // that pins harness bookkeeping (the link-up flag, say) that changes the
    // graph without changing the verdict.
    var safetyClean = kind == "clean";

    if (kind == "clean" && o.Invariants.HasFlag(Invariants.Deadlock))
    {
        var dl = Pass(Phase.Deadlock, false);
        elapsed += dl.Elapsed;
        (kind, station, steps, tids, note) = Verdict(dl, Phase.Deadlock, o, false);
    }
    if (kind == "clean" && o.Invariants.HasFlag(Invariants.Quiescence))
    {
        var q = Pass(Phase.Quiescence, false);
        elapsed += q.Elapsed;
        (kind, station, steps, tids, note) = Verdict(q, Phase.Quiescence, o, false);
        if (kind == "clean" && o.Invariants.HasFlag(Invariants.SelectiveProgress))
        {
            var tf = Pass(Phase.TimerFree, false);
            elapsed += tf.Elapsed;
            (kind, station, steps, tids, note) = Verdict(tf, Phase.TimerFree, o, false);
        }
    }

    // A grid cell whose explorer search a limit cut short (TLC has none) is
    // capped: whatever the explorer's verdict, only the verdict kinds are
    // compared, and only where the explorer found a violation. Its fired arms
    // are never compared either, so its Coverage pass cannot change the result
    // and is not run (packet-net/ax25sdl#112).
    var capped = GridVerdict.Cap(gridMode, s.DepthBound, ex.DepthBoundHit, ex.StateBoundHit);
    var coverageSkipped = GridVerdict.SkipsCoverage(capped);

    // Coverage: rerun the safety search without the VIEW and read the arms fired.
    // On a grid cell that gave no verdict it would only time out (or fail) again.
    var tlcArms = new HashSet<string>(StringComparer.Ordinal);
    if (!coverageSkipped && !(gridMode && kind is "timeout" or "error"))
    {
        var cov = Pass(Phase.Coverage, depthBound);
        elapsed += cov.Elapsed;
        tlcArms = cov.Arms!.ToHashSet(StringComparer.Ordinal);
        // TLC failed during coverage (killed, out of memory): its arms are a part
        // only, so a grid cell is recorded as failed, and --resume runs it again.
        if (gridMode && cov.Kind == "error") (kind, note) = ("error", "coverage pass: " + cov.Message);
    }
    else if (coverageSkipped && gridMode)
    {
        // A pass an earlier run began (killed mid-way, or an older build): drop its leftovers so --progress does not show it in progress.
        Tlc.DropPass(dir, Phase.Coverage);
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
        if (states != ex.States) why.Add($"states {ex.States} vs {states}");
        // On a clean, unbounded run both searched the whole graph, so both
        // fired exactly the same table transitions.
        if (kind == "clean")
        {
            var tlcTransitions = tlcArms.Where(a => a.Contains('/', StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
            if (!tlcTransitions.SetEquals(exFired))
                why.Add($"transitions fired: {exFired.Count} by the explorer, {tlcTransitions.Count} by TLC, {exFired.Except(tlcTransitions).Union(tlcTransitions.Except(exFired)).Count()} differ");
        }
    }
    else if (kind is "Quiescence" or "SelectiveProgress")
    {
        if (safetyClean && states != ex.States) why.Add($"states {ex.States} vs {states}");
    }
    else
    {
        if (safetyClean && states != ex.States) why.Add($"states {ex.States} vs {states}");
        if (exStation != station) why.Add($"station '{exStation}' vs '{station}'");
        if (steps != ex.Length) why.Add($"length {ex.Length} vs {steps}");
        else if (tids.Count != steps) why.Add($"TLC trace has {tids.Count} steps, expected {steps}");
        else
        {
            var first = Enumerable.Range(0, steps).FirstOrDefault(j => exTids[j] != tids[j], -1);
            if (first >= 0) why.Add($"step {first + 1}: transition {exTids[first]} vs {tids[first]}");
        }
    }
    // The row's own expectation, against the explorer's verdict (where the
    // tools disagree the row fails anyway; on a liveness row the explorer's
    // length is the one the expectation states, TLC's lasso differs).
    var unexpected = s.Expect?.Mismatch(exKind, exStation, ex.Length, ex.States);
    if (unexpected is null && s.Expect is not null && kind != exKind) unexpected = s.Expect.Mismatch(kind, station, steps, states);
    var (agree, whyText) = GridVerdict.Judge(capped, exKind, kind, why);
    return new Row(exText, tlcText, agree, whyText, exFired, tlcArms, unexpected,
        exKind, kind, ex.States, states, exSeconds, elapsed.TotalSeconds, capped, string.Join("; ", resumed), coverageSkipped);
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
            // Under the give-up rule a deadlock in a settled honest give-up
            // that lost data is reported as that loss, as the explorer does.
            // So is a settled honest frame-error reset that lost data.
            return (lastStep?.Field("lossy") switch { "yes" => "GiveUpLoss", "reset" => "ResetLoss", _ => "Deadlock" }, "", steps, tids, "");
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
    TableSetKind.Pre38 => "pre-38",
    TableSetKind.AckBroken => "seeded ack-broken",
    TableSetKind.ReleaseStuck => "seeded release-stuck",
    TableSetKind.DmIgnored => "seeded dm-ignored",
    TableSetKind.ReconnectSwallowed => "seeded reconnect-swallowed",
    TableSetKind.ConfirmAsDisconnect => "seeded confirm-as-disconnect",
    TableSetKind.RnrIgnored => "seeded rnr-ignored",
    TableSetKind.BusyDelivers => "seeded busy-delivers",
    TableSetKind.BusySaysRr => "seeded busy-says-rr",
    TableSetKind.EnquirySaysRr => "seeded enquiry-says-rr",
    TableSetKind.UiSwallowed => "seeded ui-swallowed",
    TableSetKind.UiUnsent => "seeded ui-unsent",
    TableSetKind.UiPollUnanswered => "seeded ui-poll-unanswered",
    TableSetKind.UiResponseDelivered => "seeded ui-response-delivered",
    TableSetKind.UiArmDeleted => "seeded ui-arm-deleted",
    TableSetKind.T3Dead => "seeded t3-dead",
    TableSetKind.NoKeepalive => "seeded no-keepalive",
    TableSetKind.FrameErrorUnreported => "seeded frame-error-unreported",
    TableSetKind.FrameErrorArmsDeleted => "seeded frame-error-arms-deleted",
    TableSetKind.ResetUnconfirmed => "seeded reset-unconfirmed",
    TableSetKind.RrResets => "seeded rr-resets",
    TableSetKind.ResetWrongCode => "seeded reset-wrong-code",
    TableSetKind.InjectedDelivered => "seeded injected-delivered",
    TableSetKind.InjectedUndelivered => "seeded injected-undelivered",
    TableSetKind.DisconnectSwallowed => "seeded disconnect-swallowed",
    TableSetKind.DisconnectReleasedEarly => "seeded disconnect-released-early",
    TableSetKind.DisconnectUnconfirmed => "seeded disconnect-unconfirmed",
    TableSetKind.DisconnectSilent => "seeded disconnect-silent",
    TableSetKind.DisconnectKeptConnected => "seeded disconnect-kept-connected",
    TableSetKind.Reverted169 => "169-reverted",
    TableSetKind.SetVersionFlagOnly => "set-version-flag-only",
    _ => "seeded livelock",
};

static string RepoRoot()
{
    var d = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "spec-sdl"))) d = d.Parent;
    return d?.FullName ?? throw new InvalidOperationException("run from inside the ax25sdl checkout: no spec-sdl/ in any ancestor of the working directory");
}

/// <param name="ExplorerKind">The explorer's verdict kind (clean, bound, or the invariant).</param>
/// <param name="TlcKind">TLC's verdict kind; "error" or "timeout" when it gave none.</param>
/// <param name="CoverageSkipped">The cell is capped, so its Coverage pass was not run and its TLC arms are empty.</param>
/// <param name="Resumed">What a resumed grid cell took from an earlier run ("explorer reused; Safety reused; Quiescence recovered"); empty otherwise.</param>
internal sealed record Row(string Explorer, string Tlc, bool Agree, string Why, IReadOnlySet<string> ExplorerArms, IReadOnlySet<string> TlcArms, string? Unexpected = null,
    string ExplorerKind = "", string TlcKind = "", long ExplorerStates = 0, long TlcStates = 0, double ExplorerSeconds = 0, double TlcSeconds = 0, string Capped = "", string Resumed = "", bool CoverageSkipped = false)
{
    /// <summary>A capped cell on which TLC, which has no cap, found a violation the explorer did not: a finding, not noise.</summary>
    public bool CappedFinding => Capped.Length > 0 && GridVerdict.IsViolation(TlcKind) && !GridVerdict.IsViolation(ExplorerKind);
}
