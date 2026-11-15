using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Packet.Sdl.TlaCheck;

/// <summary>One grid cell's Coverage phase run on its own.</summary>
/// <param name="Kind">TLC's verdict on the coverage run ("clean", the invariant it stopped at, "timeout" or "error").</param>
/// <param name="States">The distinct states of the coverage search (no VIEW, so more than the cross-check's count).</param>
/// <param name="Arms">The table arms TLC fired (empty on a timeout or failure).</param>
/// <param name="Message">What went wrong, for "error" and "timeout".</param>
public sealed record CoverageRow(string Kind, long States, IReadOnlySet<string> Arms, double Seconds, string Message)
{
    /// <summary>The arms were read in full: TLC gave a verdict.</summary>
    public bool Read => Kind is not ("error" or "timeout");
}

/// <summary>
/// TlaCheck <c>--coverage-only</c>: for grid cells whose Coverage phase was cut
/// (docs/tla.md, "Grid runs"), run that phase alone, without the explorer or
/// the Safety, Deadlock and liveness phases, and record the arms TLC fired so
/// they can be merged into the cells' earlier records. The phase needs nothing
/// from the earlier ones: its configuration is the scenario's own, with the
/// row's depth bound (<see cref="Scenario.DepthBound"/>, never set on a grid
/// cell), exactly as a full cell runs it.
/// </summary>
public static class CoverageOnly
{
    /// <summary>The mode a coverage-only record carries in the results file.</summary>
    public const string Mode = "coverage";

    /// <summary>Why <c>--coverage-only</c> cannot run with these options, or null when it can.</summary>
    public static string? Refusal(bool coverageOnly, bool gridMode, bool lockstep)
    {
        if (!coverageOnly) return null;
        if (!gridMode) return "--coverage-only needs a grid (--grid, --grid-flow, --grid-stub or --grid-cells-from)";
        if (lockstep) return "--coverage-only reruns the cross-check's Coverage phase: not with --lockstep";
        return null;
    }

    /// <param name="resultsFile">The results file: cells it records with a coverage-only verdict are skipped, the others appended.</param>
    /// <param name="cellIndex">Each cell's index in the whole grid.</param>
    /// <returns>The exit code: 0 when every cell's arms were read, 1 when a cell timed out or failed.</returns>
    public static int Run(IReadOnlyList<Scenario> scenarios, Tlc tlc, string work, int parallel, string meaning,
        ResultsFile? resultsFile, IReadOnlyDictionary<string, int> cellIndex)
    {
        var results = new ConcurrentDictionary<int, CoverageRow>();
        var skipped = 0;
        var meaningName = Lockstep.MeaningName(meaning);
        GridOptions.LargestFirst(scenarios, parallel, idx =>
        {
            var s = scenarios[idx];
            if (resultsFile?.Done(s.Name, Mode, meaningName, null) is { } done)
            {
                results[idx] = GridRecords.CoverageRow(done);
                Interlocked.Increment(ref skipped);
                return;
            }
            CoverageRow row;
            try { row = One(s, tlc, work); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                row = new CoverageRow("error", 0, new HashSet<string>(StringComparer.Ordinal), 0, $"{ex.GetType().Name}: {ex.Message}");
            }
            resultsFile?.Append(GridRecords.Coverage(s.Name, cellIndex[s.Name], meaningName, row));
            if (row.Read)
            {
                var dir = Path.Combine(work, s.Name);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            results[idx] = row;
            Console.Error.WriteLine($"{(row.Read ? "coverage" : row.Kind.ToUpperInvariant()),-8} {s.Name}: {Text(row)}");
        });

        var sb = new StringBuilder();
        sb.Append("| Cell | Coverage |\n| --- | --- |\n");
        for (var i = 0; i < scenarios.Count; i++)
            sb.Append(CultureInfo.InvariantCulture, $"| {scenarios[i].Name} | {Text(results[i])} |\n");
        var rows = results.Values.ToList();
        var read = rows.Count(r => r.Read);
        var timedOut = rows.Count(r => r.Kind == "timeout");
        var arms = rows.SelectMany(r => r.Arms).ToHashSet(StringComparer.Ordinal);
        sb.Append(CultureInfo.InvariantCulture, $"\nCoverage only: {rows.Count} cell(s): {read} read, {timedOut} timed out (rerun with --resume --retry-timeouts and a larger --timeout), {rows.Count - read - timedOut} failed (rerun with --resume)");
        if (skipped > 0) sb.Append(CultureInfo.InvariantCulture, $"; {skipped} read from the results file");
        sb.Append(CultureInfo.InvariantCulture, $". {arms.Count} distinct arms fired across them; TLC {rows.Sum(r => r.Seconds):0} s in all.\n");
        Console.WriteLine(sb.ToString());
        return read == rows.Count ? 0 : 1;
    }

    private static CoverageRow One(Scenario s, Tlc tlc, string work)
    {
        var o = s.Options;
        var dir = Path.Combine(work, s.Name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        tlc.Prepare(dir, o.TablesDir);
        var cov = tlc.Run(dir, o, Phase.Coverage, s.DepthBound);
        return new CoverageRow(cov.Kind == "invariant" ? cov.Invariant ?? "invariant" : cov.Kind, cov.Distinct,
            cov.Arms ?? new HashSet<string>(StringComparer.Ordinal), cov.Elapsed.TotalSeconds, cov.Message);
    }

    private static string Text(CoverageRow r) => r.Read
        ? $"{r.Kind}, {r.States} states, {r.Arms.Count} arms ({r.Seconds.ToString("0.0", CultureInfo.InvariantCulture)} s)"
        : $"{r.Kind}: {r.Message}";
}
