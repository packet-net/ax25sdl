using Packet.Sdl.Interpreter;

namespace Packet.Sdl.Interpreter.Tests;

/// <summary>
/// Replays every golden trace in <c>traces/*.trace.yaml</c> against the
/// committed JSON tables (<c>spec/json/</c>). A trace with
/// <c>expected_failure: &lt;upstream-issue-url&gt;</c> is a strict xfail: it
/// must fail (the figure itself is defective), reports as skipped with the
/// issue URL, and starts failing the suite the moment the upstream fix lands
/// (so the marker can't go stale).
/// </summary>
public class GoldenTraceTests
{
    private static readonly Lazy<string> RepoRoot = new(FindRepoRoot);

    private static readonly Lazy<TableSet> Tables = new(() =>
        TableLoader.Load(Path.Combine(RepoRoot.Value, "spec", "json")));

    public static TheoryData<string> TraceFiles
    {
        get
        {
            var dir = Path.Combine(FindRepoRoot(), "traces");
            var data = new TheoryData<string>();
            foreach (var path in Directory.EnumerateFiles(dir, "*.trace.yaml").Order(StringComparer.Ordinal))
                data.Add(Path.GetFileName(path));
            return data;
        }
    }

    [SkippableTheory]
    [MemberData(nameof(TraceFiles))]
    public void Golden_Trace_Conforms_To_Tables(string fileName)
    {
        var trace = TraceLoader.Load(Path.Combine(RepoRoot.Value, "traces", fileName));
        var result = TraceRunner.Run(trace, Tables.Value);

        if (trace.ExpectedFailure is { Length: > 0 } upstreamIssue)
        {
            result.Passed.Should().BeFalse(
                $"trace `{trace.Scenario}` is marked expected_failure ({upstreamIssue}); if the upstream defect " +
                "has been fixed in the figures/tables, remove the expected_failure marker");
            Skip.If(true, $"xfail ({upstreamIssue}): {result.Message}");
        }

        result.Passed.Should().BeTrue($"trace `{trace.Scenario}` diverged: {result.Message}");
    }

    [Fact]
    public void Every_Trace_File_Has_A_Citation()
    {
        foreach (var path in Directory.EnumerateFiles(Path.Combine(RepoRoot.Value, "traces"), "*.trace.yaml"))
        {
            var trace = TraceLoader.Load(path);
            trace.Citations.Should().NotBeNullOrEmpty(
                $"{Path.GetFileName(path)}: golden traces must cite the spec prose they were derived from");
        }
    }

    private static string FindRepoRoot()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(GoldenTraceTests).Assembly.Location)!;
        var d = new DirectoryInfo(assemblyDir);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "spec-sdl")))
            d = d.Parent!;
        if (d is null)
            throw new InvalidOperationException(
                $"can't locate repo root walking up from {assemblyDir} — no spec-sdl/ directory in any ancestor.");
        return d.FullName;
    }
}
