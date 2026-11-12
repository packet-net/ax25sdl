using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Packet.Sdl.TlaCheck;

/// <summary>One difference between the two meanings: the first counterexample TLC found for it.</summary>
/// <param name="Signature">The station's state, the event and the components that differ.</param>
/// <param name="Steps">The moves from the seed to the dispatch that differed, the last being that dispatch.</param>
/// <param name="Detail">The harness message: where, what each meaning did.</param>
public sealed record LockstepDiff(string Signature, IReadOnlyList<string> Steps, string Detail);

/// <summary>A row's lockstep result.</summary>
/// <param name="Error">Set when a TLC run gave no verdict (a parse error, a timeout, a failure that is not a difference).</param>
public sealed record LockstepRow(IReadOnlyList<LockstepDiff> Diffs, long States, bool Exhausted, string Error, double Seconds);

/// <summary>
/// TlaCheck --lockstep: every calibration row under Lockstep.tla, the default
/// meaning and another one side by side (docs/tla.md, "Lockstep").
/// </summary>
public static class Lockstep
{
    private static readonly Regex Sig = new(@"lockstep\{([^}]*)\}");
    private static readonly string[] Seeding = ["(while seeding)"];

    /// <param name="grid">
    /// A grid run: a cell that throws is recorded as an error and the run
    /// goes on, and a cell's model directory is deleted once it shows no
    /// difference.
    /// </param>
    /// <param name="resultsFile">With a grid run, the results file: cells it records with a verdict are skipped, the others appended.</param>
    /// <param name="cellIndex">With a grid run, each cell's index in the whole grid.</param>
    public static int Run(IReadOnlyList<Scenario> scenarios, Tlc tlc, string work, int iterate, int parallel, string meaning,
        bool grid = false, ResultsFile? resultsFile = null, IReadOnlyDictionary<string, int>? cellIndex = null)
    {
        var results = new ConcurrentDictionary<int, LockstepRow>();
        var skipped = 0;
        Parallel.For(0, scenarios.Count, new ParallelOptions { MaxDegreeOfParallelism = parallel }, idx =>
        {
            var s = scenarios[idx];
            if (resultsFile?.Done(s.Name, "lockstep", MeaningName(meaning)) is { } done)
            {
                results[idx] = GridRecords.LockstepRow(done);
                Interlocked.Increment(ref skipped);
                return;
            }
            LockstepRow row;
            if (grid)
            {
                try { row = One(s, tlc, work, iterate); }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or UnauthorizedAccessException)
                {
                    row = new LockstepRow(Array.Empty<LockstepDiff>(), 0, false, $"{ex.GetType().Name}: {ex.Message}", 0);
                }
                resultsFile?.Append(GridRecords.Lockstep(s.Name, cellIndex is not null && cellIndex.TryGetValue(s.Name, out var i) ? i : idx, MeaningName(meaning), row));
                if (row.Error.Length == 0 && row.Diffs.Count == 0)
                {
                    var dir = Path.Combine(work, s.Name);
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                }
            }
            else
            {
                row = One(s, tlc, work, iterate);
            }
            results[idx] = row;
            Console.Error.WriteLine(row.Error.Length > 0 ? $"ERROR    {s.Name}: {row.Error}"
                : row.Diffs.Count == 0 ? $"same     {s.Name}: {row.States} states ({row.Seconds:0.0} s)"
                : $"DIFFER   {s.Name}: {string.Join("; ", row.Diffs.Select(d => d.Signature + " (" + d.Steps.Count + " steps)"))}");
        });

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Lockstep: Ax25Meaning (A) against {Path.GetFileNameWithoutExtension(meaning)} (B), up to {iterate} run(s) per row.\n\n");
        sb.Append("| Scenario | Tables | Result |\n| --- | --- | --- |\n");
        for (var i = 0; i < scenarios.Count; i++)
        {
            var r = results[i];
            var text = r.Error.Length > 0 ? string.Concat(r.Diffs.Select(d => $"`{d.Signature}` ({d.Steps.Count} steps)<br>")) + "error: " + r.Error
                : r.Diffs.Count == 0 ? $"same, {r.States} states"
                : string.Join("<br>", r.Diffs.Select(d => $"`{d.Signature}` ({d.Steps.Count} steps)")) + (r.Exhausted ? "<br>(more not looked for)" : "");
            sb.Append(CultureInfo.InvariantCulture, $"| {scenarios[i].Name} | {scenarios[i].Tables} | {text} |\n");
        }

        // Every row's own differences, in the order found, each with its counterexample.
        sb.Append("\n## Each row's differences\n");
        for (var i = 0; i < scenarios.Count; i++)
        {
            var r = results[i];
            if (r.Diffs.Count == 0) continue;
            sb.Append(CultureInfo.InvariantCulture, $"\n### {scenarios[i].Name}\n");
            for (var n = 0; n < r.Diffs.Count; n++)
            {
                var d = r.Diffs[n];
                sb.Append(CultureInfo.InvariantCulture, $"\n{n + 1}. `{d.Signature}`, {d.Steps.Count} steps: {string.Join("; ", d.Steps.Select((st, j) => $"{j + 1}. {st}"))}\n\n   `{d.Detail}`\n");
            }
        }

        // Each distinct signature once, with its shortest first counterexample.
        sb.Append("\n## Each signature\n");
        var bySig = new SortedDictionary<string, List<(string Row, LockstepDiff Diff)>>(StringComparer.Ordinal);
        for (var i = 0; i < scenarios.Count; i++)
            foreach (var d in results[i].Diffs)
            {
                if (!bySig.TryGetValue(d.Signature, out var l)) bySig[d.Signature] = l = new();
                l.Add((scenarios[i].Name, d));
            }
        var same = results.Values.Count(r => r.Error.Length == 0 && r.Diffs.Count == 0);
        var errors = results.Values.Count(r => r.Error.Length > 0);
        sb.Append(CultureInfo.InvariantCulture, $"\nRows run: {scenarios.Count}; the same throughout: {same}; with a difference: {scenarios.Count - same - errors}; errors: {errors}; distinct differences: {bySig.Count}.\n");
        if (grid) sb.Append(CultureInfo.InvariantCulture, $"Grid: {skipped} cell(s) read from the results file; {results.Values.Sum(r => r.Seconds):0} s of TLC in all; largest cell {results.Values.Select(r => r.States).DefaultIfEmpty(0).Max()} distinct states.\n");
        foreach (var (sig, list) in bySig)
        {
            var (row, first) = list.OrderBy(x => x.Diff.Steps.Count).First();
            sb.Append(CultureInfo.InvariantCulture, $"\n### {sig}\n\nRows: {string.Join(", ", list.Select(x => x.Row))}\n\nShortest: {row}, {first.Steps.Count} steps:\n\n");
            for (var j = 0; j < first.Steps.Count; j++) sb.Append(CultureInfo.InvariantCulture, $"{j + 1}. {first.Steps[j]}\n");
            sb.Append("\n```\n").Append(first.Detail).Append("\n```\n");
        }
        Console.WriteLine(sb.ToString());
        return same == scenarios.Count ? 0 : 1;
    }

    /// <summary>The meaning's module name, as the results file records it.</summary>
    public static string MeaningName(string meaning) => meaning.EndsWith(".tla", StringComparison.Ordinal) ? Path.GetFileNameWithoutExtension(meaning) : meaning;

    private static LockstepRow One(Scenario s, Tlc tlc, string work, int iterate)
    {
        var dir = Path.Combine(work, s.Name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        tlc.Prepare(dir, s.Options.TablesDir);
        var known = new List<string>();
        var diffs = new List<LockstepDiff>();
        long states = 0;
        var seconds = 0.0;
        for (var run = 1; run <= iterate; run++)
        {
            var r = tlc.Run(dir, s.Options, Phase.Lockstep, s.DepthBound, known);
            seconds += r.Elapsed.TotalSeconds;
            states = r.Distinct;
            File.Copy(Path.Combine(dir, "tlc_Lockstep.out"), Path.Combine(dir, $"tlc_Lockstep_{run}.out"), overwrite: true);
            if (r.Kind == "clean") return new LockstepRow(diffs, states, false, "", seconds);
            var d = Difference(r, dir);
            if (d is null) return new LockstepRow(diffs, states, false, (diffs.Count > 0 ? $"after {diffs.Count} difference(s), " : "") + r.Kind + ": " + r.Message, seconds);
            if (known.Contains(d.Signature, StringComparer.Ordinal))
                return new LockstepRow(diffs, states, false, "difference " + d.Signature + " found again after it was set aside", seconds);
            diffs.Add(d);
            known.Add(d.Signature);
        }
        return new LockstepRow(diffs, states, true, "", seconds);
    }

    /// <summary>The difference a TLC run stopped on, or null when it stopped on something else.</summary>
    private static LockstepDiff? Difference(TlcResult r, string dir)
    {
        if (r.Kind == "invariant" && r.Invariant == "LockstepAgree" && r.Trace.Count > 0)
        {
            var msg = r.Trace[^1].Field("violMsg");
            var m = Sig.Match(msg);
            if (!m.Success) return null;
            // The last step is the dispatch that differed: it failed, so it has no transition of its own.
            var moves = r.Trace.Skip(1).ToList();
            var steps = moves.Select((t, i) => $"{t.Field("move")} at {t.Field("at")}: "
                + (i == moves.Count - 1 ? "(the dispatch on which the meanings differ)" : t.Field("tid").Length > 0 ? t.Field("tid") : "(no transition)")).ToList();
            return new LockstepDiff(m.Groups[1].Value, steps, msg);
        }
        // A difference while seeding stops TLC in its initial state (Seeded's Assert).
        if (r.Kind == "error")
        {
            var text = File.ReadAllText(Path.Combine(dir, "tlc_Lockstep.out"));
            var m = Sig.Match(text);
            if (!m.Success) return null;
            var at = text.IndexOf("seeding failed", StringComparison.Ordinal);
            var line = at >= 0 ? text[at..].Split('\n')[0].Trim().TrimEnd('"') : m.Value;
            return new LockstepDiff(m.Groups[1].Value, Seeding, line);
        }
        return null;
    }
}
