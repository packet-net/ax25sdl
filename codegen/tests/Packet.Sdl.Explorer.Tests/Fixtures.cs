using System.Text.Json;
using System.Text.Json.Nodes;
using Packet.Sdl.Explorer;
using Packet.Sdl.Interpreter;

namespace Packet.Sdl.Explorer.Tests;

/// <summary>Locates the JSON table sets the suite runs against: the committed current tables, the pre-fix fixtures, the fixed-figure fixture, and mutants of any of them.</summary>
internal static class Fixtures
{
    private static readonly Lazy<string> RepoRoot = new(FindRepoRoot);

    /// <summary>spec/json on the branch: the current tables (referenced, never copied).</summary>
    public static string CurrentDir => Path.Combine(RepoRoot.Value, "spec", "json");

    /// <summary>packethacking/ax25spec abd46b0: has the #38 fix, lacks the #40 out-of-window guard.</summary>
    public static string Pre40Dir => Path.Combine(RepoRoot.Value, "codegen", "tests", "Packet.Sdl.Explorer.Tests", "fixtures", "tables-pre40");

    /// <summary>packethacking/ax25spec fb727db: SREJ go-back-N in figc4.5 (pre #38) and no #40 guard.</summary>
    public static string Pre38Dir => Path.Combine(RepoRoot.Value, "codegen", "tests", "Packet.Sdl.Explorer.Tests", "fixtures", "tables-pre38");

    /// <summary>
    /// The current tables with the ax25spec#43 branch swap and the H3
    /// <c>N(r) := V(r)</c> staging box applied to the DL-FLOW-OFF / DL-FLOW-ON
    /// busy arms of figc4.4 and figc4.5 by hand (its README carries the exact
    /// diff). The fixture route of ax25spec#94: the busy family is unreachable
    /// on the current tables because #43 makes a station unable to become busy.
    /// </summary>
    public static string Fixed43Dir => Path.Combine(RepoRoot.Value, "codegen", "tests", "Packet.Sdl.Explorer.Tests", "fixtures", "tables-43fixed");

    private static readonly Dictionary<string, TableSet> Cache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Mutants = new(StringComparer.Ordinal);

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

    /// <summary>
    /// A copy of <paramref name="sourceDir"/> with one mutation applied to
    /// its JSON pages, written under the test output directory (one directory
    /// per <paramref name="name"/>, regenerated on every run so it can never
    /// drift from its source). This is how the calibration gate plants a
    /// defect: the mutation is code, next to the test that must catch it, and
    /// the resulting directory can be handed to the explorer CLI as
    /// <c>--tables</c> for a rerun. <paramref name="mutate"/> is called once
    /// per <c>*.g.json</c> page with the file name and the parsed root.
    /// </summary>
    public static string Mutant(string sourceDir, string name, Action<string, JsonNode> mutate)
    {
        lock (Mutants)
        {
            if (Mutants.TryGetValue(name, out var existing)) return existing;
            var dir = Path.Combine(AppContext.BaseDirectory, "mutants", name);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            foreach (var file in Directory.GetFiles(sourceDir, "*.json"))
            {
                var target = Path.Combine(dir, Path.GetFileName(file));
                if (!file.EndsWith(".g.json", StringComparison.Ordinal))
                {
                    File.Copy(file, target);
                    continue;
                }
                var root = JsonNode.Parse(File.ReadAllText(file)) ?? throw new InvalidDataException($"{file}: empty JSON");
                mutate(Path.GetFileName(file), root);
                File.WriteAllText(target, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            }
            Mutants[name] = dir;
            return dir;
        }
    }

    /// <summary>The transition with this id on a state page.</summary>
    public static JsonNode Transition(JsonNode page, string id) =>
        page["transitions"]!.AsArray().Single(t => string.Equals(t!["id"]!.GetValue<string>(), id, StringComparison.Ordinal))!;

    /// <summary>The path with this id in a subroutine on the subroutines page.</summary>
    public static JsonNode SubroutinePath(JsonNode page, string subroutine, string id) =>
        page["subroutines"]!.AsArray().Single(s => string.Equals(s!["name"]!.GetValue<string>(), subroutine, StringComparison.Ordinal))!
            ["paths"]!.AsArray().Single(p => string.Equals(p!["id"]!.GetValue<string>(), id, StringComparison.Ordinal))!;

    /// <summary>The verbs of a transition's or path's action list, in order.</summary>
    public static IReadOnlyList<string> Verbs(JsonNode transitionOrPath) =>
        transitionOrPath["actions"]!.AsArray().Select(a => a!["verb"]!.GetValue<string>()).ToList();

    /// <summary>Remove every action with this verb from a transition or path (it must occur at least once).</summary>
    public static void RemoveAction(JsonNode transitionOrPath, string verb)
    {
        var actions = transitionOrPath["actions"]!.AsArray();
        var victims = actions.Where(a => string.Equals(a!["verb"]!.GetValue<string>(), verb, StringComparison.Ordinal)).ToList();
        if (victims.Count == 0) throw new InvalidOperationException($"no action `{verb}` to remove");
        foreach (var v in victims) actions.Remove(v);
    }

    /// <summary>Replace the verb of every action spelled <paramref name="from"/> with <paramref name="to"/> (it must occur at least once).</summary>
    public static void ReplaceAction(JsonNode transitionOrPath, string from, string to)
    {
        var hit = false;
        foreach (var a in transitionOrPath["actions"]!.AsArray())
        {
            if (!string.Equals(a!["verb"]!.GetValue<string>(), from, StringComparison.Ordinal)) continue;
            a["verb"] = to;
            hit = true;
        }
        if (!hit) throw new InvalidOperationException($"no action `{from}` to replace");
    }

    /// <summary>Replace a transition's or path's whole action list with a deep copy of another's.</summary>
    public static void CopyActions(JsonNode from, JsonNode to) =>
        to["actions"] = JsonNode.Parse(from["actions"]!.ToJsonString());

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
