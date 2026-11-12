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

    public static int Run(IReadOnlyList<Scenario> scenarios, Tlc tlc, string work, int iterate, int parallel, string meaning)
    {
        var results = new ConcurrentDictionary<int, LockstepRow>();
        Parallel.For(0, scenarios.Count, new ParallelOptions { MaxDegreeOfParallelism = parallel }, idx =>
        {
            var row = One(scenarios[idx], tlc, work, iterate);
            results[idx] = row;
            var s = scenarios[idx];
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

        // Each distinct difference once, with its shortest first counterexample.
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
            // TLC's fingerprint and queue files are not read again; on a shared runner they fill the disk.
            foreach (var junk in new[] { "meta_Lockstep", "jtmp" })
                if (Directory.Exists(Path.Combine(dir, junk))) Directory.Delete(Path.Combine(dir, junk), recursive: true);
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
            var steps = r.Trace.Skip(1).Select(t => $"{t.Field("move")} at {t.Field("at")}: {t.Field("tid")}").ToList();
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
