using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Packet.Sdl.TlaCheck;

/// <summary>One of TLC's periodic progress lines.</summary>
/// <param name="At">TLC's own clock (the machine's local time).</param>
/// <param name="Depth">The search depth so far (the number in <c>Progress(n)</c>).</param>
public sealed record TlcProgressLine(DateTime At, int Depth, long Generated, long Distinct, long Queue);

/// <summary>What one TLC pass's output says so far.</summary>
/// <param name="Started">TLC's <c>Starting...</c> time, or null.</param>
/// <param name="Last">The latest progress line, or null.</param>
/// <param name="DistinctPerMinute">New distinct states per minute between the last two progress lines, or null with fewer than two.</param>
/// <param name="LastCheckpoint">When TLC last completed a checkpoint, or null.</param>
/// <param name="Recovered">The run started from a checkpoint.</param>
public sealed record PassOutput(DateTime? Started, TlcProgressLine? Last, double? DistinctPerMinute, DateTime? LastCheckpoint, bool Recovered);

/// <summary>
/// TlaCheck <c>--progress</c>: a read-only look at a grid run's work
/// directory while it runs (docs/tla.md, "Grid runs"). For each cell
/// directory: the passes finished and their verdicts, the pass in progress,
/// how long it has run, its latest progress line and its latest checkpoint.
/// TLC's output is copied to <c>tlc_&lt;Phase&gt;.out</c> as it arrives, so
/// this reads it there; nothing is changed.
/// </summary>
public static class Progress
{
    private static readonly Regex ProgressLine = new(
        @"^Progress\((\d+)\) at (\d{4}-\d\d-\d\d \d\d:\d\d:\d\d): ([\d,]+) states generated(?: \([\d,]+ s/min\))?, ([\d,]+) distinct states found(?: \([\d,]+ ds/min\))?, ([\d,]+) states left on queue\.",
        RegexOptions.CultureInvariant);

    private static readonly Regex StartedLine = new(@"^Starting\.\.\. \((\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)\)", RegexOptions.CultureInvariant);

    private static readonly Regex CheckpointLine = new(@"^Checkpointing completed at \((\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)\)", RegexOptions.CultureInvariant);

    private static DateTime Time(string s) => DateTime.ParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static long Count(string s) => long.Parse(s.Replace(",", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);

    /// <summary>A TLC progress line, or null when <paramref name="line"/> is not one.</summary>
    public static TlcProgressLine? ParseLine(string line)
    {
        var m = ProgressLine.Match(line);
        return m.Success
            ? new TlcProgressLine(Time(m.Groups[2].Value), int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), Count(m.Groups[3].Value), Count(m.Groups[4].Value), Count(m.Groups[5].Value))
            : null;
    }

    /// <summary>What a pass's output (complete or still being written) says so far.</summary>
    public static PassOutput Read(IEnumerable<string> lines)
    {
        DateTime? started = null, checkpoint = null;
        TlcProgressLine? last = null, before = null;
        var recovered = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (ParseLine(line) is { } p)
            {
                before = last;
                last = p;
            }
            else if (StartedLine.Match(line) is { Success: true } s) started ??= Time(s.Groups[1].Value);
            else if (CheckpointLine.Match(line) is { Success: true } c) checkpoint = Time(c.Groups[1].Value);
            else if (line.StartsWith("Recovery completed.", StringComparison.Ordinal)) recovered = true;
        }
        double? rate = null;
        if (before is not null && last is not null && last.At > before.At)
            rate = (last.Distinct - before.Distinct) / (last.At - before.At).TotalMinutes;
        return new PassOutput(started, last, rate, checkpoint, recovered);
    }

    private static readonly Phase[] Passes = [Phase.Safety, Phase.Deadlock, Phase.Quiescence, Phase.TimerFree, Phase.Coverage];

    /// <summary>The report for every cell directory under <paramref name="work"/>, oldest activity last.</summary>
    public static string Report(string work, DateTime now)
    {
        if (!Directory.Exists(work)) return $"{work}: no such directory\n";
        var cells = Directory.GetDirectories(work)
            .Where(d => File.Exists(Path.Combine(d, Checkpoint.CellFile)) || Directory.GetFiles(d, "tlc_*.out").Length > 0)
            .Select(d => (Dir: d, Latest: Directory.GetFiles(d).Select(File.GetLastWriteTime).DefaultIfEmpty(Directory.GetLastWriteTime(d)).Max()))
            .OrderByDescending(c => c.Latest)
            .ToList();
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{cells.Count} cell director{(cells.Count == 1 ? "y" : "ies")} under {work} at {now:yyyy-MM-dd HH:mm:ss}\n");
        foreach (var (dir, _) in cells) sb.Append('\n').Append(Cell(dir, now));
        return sb.ToString();
    }

    /// <summary>One cell's block.</summary>
    public static string Cell(string dir, DateTime now)
    {
        var sb = new StringBuilder();
        sb.Append(Path.GetFileName(dir)).Append('\n');
        if (File.Exists(Path.Combine(dir, Checkpoint.ExplorerFile))) sb.Append("  explorer: done\n");
        var finished = new List<string>();
        string? current = null;
        DateTime currentWrite = DateTime.MinValue;
        foreach (var phase in Passes)
        {
            var outFile = Checkpoint.OutFile(dir, phase);
            if (!File.Exists(outFile)) continue;
            if (File.Exists(Checkpoint.DoneFile(dir, phase)))
            {
                var r = Tlc.Parse(File.ReadAllText(outFile), TimeSpan.Zero);
                finished.Add($"{phase} {(r.Kind == "invariant" ? r.Invariant : r.Kind)}");
                continue;
            }
            var written = File.GetLastWriteTime(outFile);
            if (written >= currentWrite)
            {
                current = phase.ToString();
                currentWrite = written;
            }
        }
        sb.Append("  finished: ").Append(finished.Count > 0 ? string.Join(", ", finished) : "none").Append('\n');
        if (current is null)
        {
            sb.Append("  in progress: none\n");
            return sb.ToString();
        }
        var phaseNow = Enum.Parse<Phase>(current);
        var outPath = Checkpoint.OutFile(dir, phaseNow);
        PassOutput o;
        using (var reader = new StreamReader(new FileStream(outPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
            o = Read(Lines(reader));
        var quiet = now - currentWrite;
        sb.Append("  in progress: ").Append(current);
        if (o.Recovered) sb.Append(" (recovered from a checkpoint)");
        sb.Append('\n');
        if (o.Started is { } started)
        {
            sb.Append("    running for ").Append(Span(now - started)).Append(" (since ").Append(started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(')');
            if (o.Recovered && ReadEarlierTicks(dir, phaseNow) is { } earlier && earlier > 0) sb.Append(", plus ").Append(Span(TimeSpan.FromTicks(earlier))).Append(" before its last checkpoint");
            sb.Append('\n');
        }
        sb.Append("    output last written ").Append(Span(quiet)).Append(" ago");
        if (quiet > TimeSpan.FromMinutes(3)) sb.Append(" (TLC reports every minute: stopped, or not running?)");
        sb.Append('\n');
        if (o.Last is { } l)
        {
            sb.Append(CultureInfo.InvariantCulture, $"    latest progress {l.At:yyyy-MM-dd HH:mm:ss}: depth {l.Depth}, {l.Distinct:N0} distinct states, {l.Queue:N0} on the queue, {l.Generated:N0} generated");
            if (o.DistinctPerMinute is { } rate) sb.Append(CultureInfo.InvariantCulture, $", {rate:N0} new distinct states per minute");
            sb.Append('\n');
        }
        else
        {
            sb.Append("    no progress line yet\n");
        }
        sb.Append("    latest checkpoint: ").Append(o.LastCheckpoint is { } c ? c.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "none in this run").Append('\n');
        return sb.ToString();
    }

    private static IEnumerable<string> Lines(StreamReader reader)
    {
        while (reader.ReadLine() is { } line) yield return line;
    }

    // TLC's time on a recovered pass before its checkpoint, from the pass's progress record.
    private static long? ReadEarlierTicks(string dir, Phase phase)
    {
        var path = Path.Combine(Checkpoint.MetaDir(dir, phase), Checkpoint.ProgressFile);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<PassProgress>(File.ReadAllText(path))?.ElapsedTicks : null; }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    /// <summary>A duration as days, hours and minutes, plain ASCII.</summary>
    public static string Span(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalMinutes < 1) return $"{(int)t.TotalSeconds} s";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} min";
        if (t.TotalDays < 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
        return $"{(int)t.TotalDays} d {t.Hours} h {t.Minutes} min";
    }
}
