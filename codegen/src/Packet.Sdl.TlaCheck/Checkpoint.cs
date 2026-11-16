using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Packet.Sdl.TlaCheck;

/// <summary>What a resumed cell does with one TLC pass (<see cref="Checkpoint.Plan"/>).</summary>
public enum PassAction
{
    /// <summary>Run the pass from the start.</summary>
    Fresh,
    /// <summary>The pass finished in an earlier run: read its verdict back from <c>tlc_&lt;Phase&gt;.out</c>.</summary>
    Reuse,
    /// <summary>The pass was interrupted after a checkpoint: restart TLC with <c>-recover</c>.</summary>
    Recover,
}

/// <param name="Why">For <see cref="PassAction.Fresh"/>, why earlier progress (if any) is not used; empty when there was none.</param>
/// <param name="Done">For <see cref="PassAction.Reuse"/>: the finished pass's record.</param>
/// <param name="Progress">For <see cref="PassAction.Recover"/>: the interrupted pass's record.</param>
/// <param name="CheckpointDir">For <see cref="PassAction.Recover"/>: the directory TLC takes with <c>-recover</c>.</param>
public sealed record PassPlan(PassAction Action, string Why = "", PassDone? Done = null, PassProgress? Progress = null, string? CheckpointDir = null);

/// <summary>
/// <c>tlc_&lt;Phase&gt;.done</c>: a pass that gave a verdict. The verdict itself is
/// <c>tlc_&lt;Phase&gt;.out</c>, parsed again exactly as when it was written.
/// </summary>
/// <param name="Config">SHA-256 of the pass's TLC configuration.</param>
/// <param name="ElapsedTicks">TLC's time on the pass, over every run of it.</param>
/// <param name="Arms">For the Coverage pass, the arms read off its dump; null for the others.</param>
public sealed record PassDone(string Config, long ElapsedTicks, IReadOnlyList<string>? Arms);

/// <summary>
/// <c>meta_&lt;Phase&gt;/tlacheck.json</c>: a checkpointing pass in progress,
/// written before TLC starts and again each time TLC completes a checkpoint.
/// </summary>
/// <param name="Config">SHA-256 of the pass's TLC configuration.</param>
/// <param name="Fp">The fingerprint polynomial (TLC's <c>-fp</c>) the pass runs with: a recovery must use the same one.</param>
/// <param name="Workers">TLC's <c>-workers</c>: each worker keeps its own trace file, so a recovery must use the same number.</param>
/// <param name="ElapsedTicks">TLC's time on the pass up to its last completed checkpoint, over every run of it.</param>
public sealed record PassProgress(string Config, int Fp, int Workers, long ElapsedTicks);

/// <summary>
/// Checkpoint and resume for grid runs (packet-net/ax25sdl#131, docs/tla.md,
/// "Grid runs"). A cell's model directory keeps, per TLC pass, either the
/// pass's verdict (<c>tlc_&lt;Phase&gt;.out</c> with <c>tlc_&lt;Phase&gt;.done</c>) or,
/// while it runs, TLC's metadir with its latest checkpoint
/// (<c>meta_&lt;Phase&gt;</c>, with <c>tlacheck.json</c>), and the explorer's
/// result (<c>explorer.json</c>). A resumed cell reads finished passes back,
/// restarts an interrupted Safety, Deadlock or liveness pass from its
/// checkpoint, and runs the rest from the start.
/// </summary>
public static class Checkpoint
{
    /// <summary>The checkpoint interval a grid run uses unless told otherwise, in minutes.</summary>
    public const int DefaultMinutes = 30;

    /// <summary>TLC 1.7.4's number of fingerprint polynomials: <c>-fp</c> takes 0 to this less one.</summary>
    public const int FpCount = 131;

    /// <summary>The file in a cell's model directory that says which model and options the directory was made for.</summary>
    public const string CellFile = "cell.json";

    /// <summary>The explorer's result, saved so a resumed cell need not search again.</summary>
    public const string ExplorerFile = "explorer.json";

    /// <summary>The progress record inside a pass's metadir.</summary>
    public const string ProgressFile = "tlacheck.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>The passes TLC can resume from a checkpoint. Coverage cannot: its dump would lose every state written before the checkpoint.</summary>
    public static bool Recoverable(Phase phase) => phase is Phase.Safety or Phase.Deadlock or Phase.Quiescence or Phase.TimerFree;

    /// <summary>The passes a resumed cell reads back once finished.</summary>
    public static bool Reusable(Phase phase) => Recoverable(phase) || phase == Phase.Coverage;

    /// <summary><c>--checkpoint-minutes N</c>: a non-negative whole number of minutes, 0 for none.</summary>
    public static int ParseMinutes(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 0
            ? n
            : throw new ArgumentException($"--checkpoint-minutes: expected a non-negative whole number of minutes (0: no checkpoints), got `{value}`");

    /// <summary>Why <c>--checkpoint-minutes</c> cannot be given with these options, or null when it can (or was not given).</summary>
    public static string? Refusal(int? minutes, bool gridMode, bool lockstep, bool coverageOnly)
    {
        if (minutes is null) return null;
        if (!gridMode) return "--checkpoint-minutes needs a grid (--grid, --grid-flow, --grid-stub or --grid-cells-from)";
        if (minutes > 0 && (lockstep || coverageOnly))
            return "--checkpoint-minutes applies to the cross-check's Safety, Deadlock and liveness passes: not with --lockstep or --coverage-only";
        return null;
    }

    /// <summary>The checkpoint interval a run uses: the one given, else <see cref="DefaultMinutes"/> on a cross-check grid run, else none.</summary>
    public static int Minutes(int? given, bool gridMode, bool lockstep, bool coverageOnly) =>
        !gridMode || lockstep || coverageOnly ? 0 : given ?? DefaultMinutes;

    /// <summary>A short SHA-256 of <paramref name="text"/>, hex.</summary>
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32].ToLowerInvariant();

    /// <summary>
    /// What identifies a prepared model directory: every module in it (the
    /// tables, the meaning, the runner, the harness) and the scenario's
    /// options as the explorer reads them (its state cap and depth limit
    /// included, which the cell name leaves out).
    /// </summary>
    public static string ModelKey(string workDir, string options)
    {
        var sb = new StringBuilder(options).Append('\n');
        foreach (var f in Directory.GetFiles(workDir, "*.tla").Order(StringComparer.Ordinal))
            sb.Append(Path.GetFileName(f)).Append('\n').Append(Hash(File.ReadAllText(f))).Append('\n');
        return Hash(sb.ToString());
    }

    /// <summary>
    /// Make the model directory for a cell. Without <paramref name="resume"/>
    /// it is emptied first, as before; with it, a directory an earlier run left
    /// for the same model and options (<see cref="ModelKey"/>) is kept, with
    /// whatever progress it holds, and any other is emptied.
    /// </summary>
    /// <param name="prepare">Writes the model's modules into the directory (<see cref="Tlc.Prepare"/>): the same files each time for the same cell.</param>
    /// <returns>True when an earlier run's directory was kept.</returns>
    public static bool OpenCell(string dir, string options, bool resume, Action prepare)
    {
        var file = Path.Combine(dir, CellFile);
        if (resume && File.Exists(file))
        {
            prepare();
            string? stored = null;
            try { stored = JsonDocument.Parse(File.ReadAllText(file)).RootElement.GetProperty("model").GetString(); }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
            if (stored == ModelKey(dir, options)) return true;
        }
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        prepare();
        WriteAtomic(file, JsonSerializer.Serialize(new { model = ModelKey(dir, options) }, Json));
        return false;
    }

    /// <summary>
    /// What a resumed cell does with <paramref name="phase"/>: read back a
    /// finished pass run with the same configuration, recover an interrupted
    /// one from a whole checkpoint taken with the same configuration and
    /// workers, or run it from the start.
    /// </summary>
    /// <param name="config">What the pass depends on besides the modules (<see cref="Tlc.PassIdentity"/>): its configuration and TLC options.</param>
    public static PassPlan Plan(string workDir, Phase phase, string config, int workers)
    {
        if (!Reusable(phase)) return new PassPlan(PassAction.Fresh);
        var hash = Hash(config);
        var done = Read<PassDone>(DoneFile(workDir, phase));
        if (done is not null && File.Exists(OutFile(workDir, phase)))
        {
            if (done.Config == hash && (phase != Phase.Coverage || done.Arms is not null)) return new PassPlan(PassAction.Reuse, Done: done);
            return new PassPlan(PassAction.Fresh, "its earlier verdict was for another configuration");
        }
        if (!Recoverable(phase)) return new PassPlan(PassAction.Fresh);
        var meta = MetaDir(workDir, phase);
        var progress = Read<PassProgress>(Path.Combine(meta, ProgressFile));
        if (progress is null) return new PassPlan(PassAction.Fresh);
        if (progress.Config != hash) return new PassPlan(PassAction.Fresh, "its checkpoint was for another configuration");
        if (progress.Workers != workers) return new PassPlan(PassAction.Fresh, $"its checkpoint was taken with {progress.Workers} TLC worker(s), not {workers}");
        var dirs = Directory.GetDirectories(meta).Where(d => File.Exists(Path.Combine(d, "queue.chkpt"))).Order(StringComparer.Ordinal).ToList();
        if (dirs.Count == 0) return new PassPlan(PassAction.Fresh, "no checkpoint was completed");
        var latest = dirs[^1];
        if (!Whole(latest)) return new PassPlan(PassAction.Fresh, "it was stopped while TLC was writing a checkpoint");
        return new PassPlan(PassAction.Recover, Progress: progress, CheckpointDir: latest);
    }

    // TLC's checkpoint files while one is being written (TLC 1.7.4,
    // ModelChecker.checkpoint): the queue, each worker's trace, the intern
    // table and the liveness graphs, in that order, each first to X.tmp
    // (begin), then each renamed over X.chkpt (commit), in the same order.
    // The queue's commit first deletes the queue's disk pools the last
    // checkpoint still points at, then renames queue.tmp.
    private static readonly Regex CheckpointTemp = new(@"^(queue\.tmp|.+-\d+\.tmp|vars\.tmp|dgraph_\d+\.chkpt\.tmp)$");

    /// <summary>
    /// Whether the checkpoint files in <paramref name="dir"/> are one whole
    /// checkpoint: the queue's is there and no part's .tmp is. A .tmp left
    /// means TLC stopped while writing or committing a checkpoint, and then
    /// the last one may not stand either: part way through the renames the
    /// parts are from two checkpoints, and once the queue's commit has begun
    /// the last checkpoint's queue pools may already be deleted. Such a pass
    /// runs again from the start; a checkpoint takes seconds to write, so
    /// this is rare.
    /// </summary>
    public static bool Whole(string dir) =>
        File.Exists(Path.Combine(dir, "queue.chkpt")) && !Directory.GetFiles(dir).Any(f => CheckpointTemp.IsMatch(Path.GetFileName(f)));

    public static string MetaDir(string workDir, Phase phase) => Path.Combine(workDir, "meta_" + phase);

    public static string OutFile(string workDir, Phase phase) => Path.Combine(workDir, "tlc_" + phase + ".out");

    public static string DoneFile(string workDir, Phase phase) => Path.Combine(workDir, "tlc_" + phase + ".done");

    public static void WriteDone(string workDir, Phase phase, PassDone done) => WriteAtomic(DoneFile(workDir, phase), JsonSerializer.Serialize(done, Json));

    public static void WriteProgress(string workDir, Phase phase, PassProgress progress) =>
        WriteAtomic(Path.Combine(MetaDir(workDir, phase), ProgressFile), JsonSerializer.Serialize(progress, Json));

    /// <summary>The explorer's result an earlier run of this cell saved under the same <paramref name="build"/>, or null.</summary>
    public static ExplorerSummary? ReadExplorer(string workDir, string build)
    {
        var saved = Read<SavedExplorer>(Path.Combine(workDir, ExplorerFile));
        return saved is not null && saved.Build == build ? saved.Result : null;
    }

    public static void WriteExplorer(string workDir, string build, ExplorerSummary result) =>
        WriteAtomic(Path.Combine(workDir, ExplorerFile), JsonSerializer.Serialize(new SavedExplorer(build, result), Json));

    private sealed record SavedExplorer(string Build, ExplorerSummary Result);

    /// <summary>
    /// The code that makes a cell's explorer result: a hash of the built
    /// explorer, interpreter, IR and TlaCheck assemblies, so a change to any of
    /// them, committed or not, runs the explorer again (the build is
    /// deterministic, so the same source gives the same hash).
    /// </summary>
    public static string ExplorerBuild(params System.Reflection.Assembly[] assemblies)
    {
        var sb = new StringBuilder();
        foreach (var a in assemblies)
            sb.Append(a.GetName().Name).Append(' ').Append(a.Location.Length > 0 && File.Exists(a.Location) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) : a.ManifestModule.ModuleVersionId.ToString()).Append('\n');
        return Hash(sb.ToString());
    }

    private static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Write through a temporary file and a rename, so a kill never leaves half a file.</summary>
    private static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".writing";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// What a cross-check needs of the explorer's search, saved in the cell's
/// model directory so a resumed cell can skip it.
/// </summary>
/// <param name="Kind">"clean", "bound", or the violated invariant.</param>
/// <param name="Tids">The counterexample's transitions, normalised as the comparison reads them.</param>
/// <param name="Fired">The table transitions fired.</param>
public sealed record ExplorerSummary(string Kind, string Station, IReadOnlyList<string> Tids, int States, int Length,
    bool DepthBoundHit, bool StateBoundHit, IReadOnlyList<string> Fired, double Seconds);
