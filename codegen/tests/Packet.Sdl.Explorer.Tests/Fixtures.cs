using Packet.Sdl.Explorer;
using Packet.Sdl.Interpreter;

namespace Packet.Sdl.Explorer.Tests;

/// <summary>Locates the JSON table sets the suite runs against: the committed current tables and the pre-fix fixtures.</summary>
internal static class Fixtures
{
    private static readonly Lazy<string> RepoRoot = new(FindRepoRoot);

    /// <summary>spec/json on the branch: the current tables (referenced, never copied).</summary>
    public static string CurrentDir => Path.Combine(RepoRoot.Value, "spec", "json");

    /// <summary>packethacking/ax25spec abd46b0: has the #38 fix, lacks the #40 out-of-window guard.</summary>
    public static string Pre40Dir => Path.Combine(RepoRoot.Value, "codegen", "tests", "Packet.Sdl.Explorer.Tests", "fixtures", "tables-pre40");

    /// <summary>packethacking/ax25spec fb727db: SREJ go-back-N in figc4.5 (pre #38) and no #40 guard.</summary>
    public static string Pre38Dir => Path.Combine(RepoRoot.Value, "codegen", "tests", "Packet.Sdl.Explorer.Tests", "fixtures", "tables-pre38");

    private static readonly Dictionary<string, TableSet> Cache = new(StringComparer.Ordinal);

    public static TableSet Tables(string dir)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(dir, out var tables))
            {
                tables = TableLoader.Load(dir);
                Cache[dir] = tables;
            }
            return tables;
        }
    }

    public static ExplorationResult Run(ExplorerOptions options) => Explorer.Run(options, Tables(options.TablesDir));

    private static string FindRepoRoot()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(Fixtures).Assembly.Location)!;
        var d = new DirectoryInfo(assemblyDir);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "spec-sdl")))
            d = d.Parent!;
        if (d is null)
            throw new InvalidOperationException($"can't locate repo root walking up from {assemblyDir}: no spec-sdl/ directory in any ancestor");
        return d.FullName;
    }
}
