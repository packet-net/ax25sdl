using System.Text.Json;
using Packet.Sdl.IR;

namespace Packet.Sdl.Interpreter;

/// <summary>
/// The loaded state tables for one machine: every state page plus the
/// subroutines page, exactly as emitted to <c>spec/json/</c>.
/// </summary>
public sealed record TableSet(
    string Machine,
    IReadOnlyDictionary<string, StateTable> States,
    IReadOnlyDictionary<string, SubroutineTable> Subroutines);

/// <summary>One state page (one <c>*.g.json</c> of kind <c>state</c>).</summary>
public sealed record StateTable(string State, string Figure, IReadOnlyList<TransitionRow> Transitions);

/// <summary>
/// One transition row. <see cref="Guard"/> is the composed guard string
/// parsed by <see cref="GuardExpression"/> — the same semantics the typed
/// backends carry (a conjunction of optionally-negated atoms).
/// </summary>
public sealed record TransitionRow(
    string Id,
    string On,
    string GuardText,
    IReadOnlyList<GuardTermIR> Guard,
    IReadOnlyList<ActionRow> Actions,
    string Next,
    IReadOnlyList<LoopRow> Loops);

/// <summary>One action step: the (already canonicalised) verb + its SDL shape class.</summary>
public sealed record ActionRow(string Verb, string Kind);

/// <summary>
/// A loop region over <c>Actions[Start .. Start+Length-1]</c>. The
/// predicate is the continue condition; <see cref="TestAtEnd"/> selects
/// do-while (true) vs while (false) topology.
/// </summary>
public sealed record LoopRow(int Start, int Length, GuardTermIR Predicate, bool TestAtEnd);

/// <summary>One subroutine (figc4.7-style): guarded paths, first-match-wins is NOT assumed — exactly one path must match.</summary>
public sealed record SubroutineTable(string Name, IReadOnlyList<SubroutinePathRow> Paths);

/// <summary>One guarded path through a subroutine.</summary>
public sealed record SubroutinePathRow(
    string Id,
    string GuardText,
    IReadOnlyList<GuardTermIR> Guard,
    IReadOnlyList<ActionRow> Actions,
    IReadOnlyList<LoopRow> Loops);

/// <summary>
/// Loads the emitted JSON tables (<c>spec/json/*.g.json</c> + <c>index.json</c>)
/// for one machine. This is the interpreter's only input — it never reads a
/// backend's generated source.
/// </summary>
public static class TableLoader
{
    /// <summary>
    /// Load every page of <paramref name="machine"/> from <paramref name="jsonDir"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The directory has no index.json, or a page is malformed.</exception>
    public static TableSet Load(string jsonDir, string machine = "data_link")
    {
        var indexPath = Path.Combine(jsonDir, "index.json");
        if (!File.Exists(indexPath))
            throw new InvalidDataException($"no index.json in {jsonDir} — is this a spec/json output directory?");

        using var index = JsonDocument.Parse(File.ReadAllText(indexPath));

        var states = new Dictionary<string, StateTable>(StringComparer.Ordinal);
        var subs = new Dictionary<string, SubroutineTable>(StringComparer.Ordinal);

        foreach (var entry in index.RootElement.GetProperty("pages").EnumerateArray())
        {
            if (!string.Equals(entry.GetProperty("machine").GetString(), machine, StringComparison.Ordinal))
                continue;

            var file = entry.GetProperty("file").GetString()
                       ?? throw new InvalidDataException($"{indexPath}: page entry with null file");
            var pagePath = Path.Combine(jsonDir, file);
            using var page = JsonDocument.Parse(File.ReadAllText(pagePath));
            var root = page.RootElement;
            var kind = root.GetProperty("kind").GetString();

            if (string.Equals(kind, "state", StringComparison.Ordinal))
            {
                var table = ReadStatePage(root, pagePath);
                if (!states.TryAdd(table.State, table))
                    throw new InvalidDataException($"{pagePath}: duplicate state page for `{table.State}`");
            }
            else if (string.Equals(kind, "subroutines", StringComparison.Ordinal))
            {
                foreach (var sub in ReadSubroutinesPage(root, pagePath))
                {
                    if (!subs.TryAdd(sub.Name, sub))
                        throw new InvalidDataException($"{pagePath}: duplicate subroutine `{sub.Name}`");
                }
            }
            else
            {
                throw new InvalidDataException($"{pagePath}: unknown page kind `{kind}`");
            }
        }

        if (states.Count == 0)
            throw new InvalidDataException($"{jsonDir}: no state pages found for machine `{machine}`");

        return new TableSet(machine, states, subs);
    }

    private static StateTable ReadStatePage(JsonElement root, string pagePath)
    {
        var state = root.GetProperty("state").GetString()
                    ?? throw new InvalidDataException($"{pagePath}: state page with null state");
        var figure = root.GetProperty("source").GetProperty("figure").GetString() ?? "";

        var transitions = new List<TransitionRow>();
        foreach (var t in root.GetProperty("transitions").EnumerateArray())
        {
            var guardText = t.GetProperty("guard").GetString() ?? "";
            transitions.Add(new TransitionRow(
                Id: t.GetProperty("id").GetString() ?? "",
                On: t.GetProperty("on").GetString() ?? "",
                GuardText: guardText,
                Guard: GuardExpression.Parse(guardText),
                Actions: ReadActions(t),
                Next: t.GetProperty("next").GetString() ?? "",
                Loops: ReadLoops(t)));
        }

        return new StateTable(state, figure, transitions);
    }

    private static IEnumerable<SubroutineTable> ReadSubroutinesPage(JsonElement root, string pagePath)
    {
        foreach (var s in root.GetProperty("subroutines").EnumerateArray())
        {
            var name = s.GetProperty("name").GetString()
                       ?? throw new InvalidDataException($"{pagePath}: subroutine with null name");
            var paths = new List<SubroutinePathRow>();
            foreach (var p in s.GetProperty("paths").EnumerateArray())
            {
                var guardText = p.GetProperty("guard").GetString() ?? "";
                paths.Add(new SubroutinePathRow(
                    Id: p.GetProperty("id").GetString() ?? "",
                    GuardText: guardText,
                    Guard: GuardExpression.Parse(guardText),
                    Actions: ReadActions(p),
                    Loops: ReadLoops(p)));
            }
            yield return new SubroutineTable(name, paths);
        }
    }

    private static List<ActionRow> ReadActions(JsonElement owner)
    {
        var actions = new List<ActionRow>();
        foreach (var a in owner.GetProperty("actions").EnumerateArray())
        {
            actions.Add(new ActionRow(
                Verb: a.GetProperty("verb").GetString() ?? "",
                Kind: a.GetProperty("kind").GetString() ?? ""));
        }
        return actions;
    }

    private static List<LoopRow> ReadLoops(JsonElement owner)
    {
        var loops = new List<LoopRow>();
        foreach (var l in owner.GetProperty("loops").EnumerateArray())
        {
            loops.Add(new LoopRow(
                Start: l.GetProperty("start").GetInt32(),
                Length: l.GetProperty("length").GetInt32(),
                Predicate: GuardExpression.ParseSingle(l.GetProperty("predicate").GetString() ?? ""),
                TestAtEnd: l.GetProperty("test_at_end").GetBoolean()));
        }
        return loops;
    }
}
