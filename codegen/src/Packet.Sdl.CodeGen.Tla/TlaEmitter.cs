using System.Globalization;
using System.Text;
using System.Text.Json;
using Packet.Sdl.IR;

namespace Packet.Sdl.CodeGen.Tla;

/// <summary>
/// TLA+ emitter. Renders one machine's emitted JSON tables (the
/// <c>spec/json</c> directory, or a fixture copy of it) as a TLA+ module of
/// constant data for the TLC model checker: per state page and event, the
/// transitions in file order; per subroutine, its paths; each with its guard,
/// loop regions, action chain and next state.
/// </summary>
/// <remarks>
/// <para>
/// <b>Input is the JSON, not the YAML IR.</b> The JSON tables are exactly
/// what the reference interpreter and the two-station explorer execute, so
/// generating from them means TLC and the explorer check the same bytes; and
/// the explorer's pre-fix fixtures (<c>tables-pre38</c>, <c>tables-pre40</c>)
/// exist only as JSON.
/// </para>
/// <para>
/// <b>Data, like the other backends.</b> As the C#, Go, TS, Rust, C and
/// Python emitters emit the tables as data for a hand-written runtime, this
/// one emits them as TLA+ values for a hand-written TLA+ runtime,
/// <c>spec/tla/Ax25Semantics.tla</c>, which says what each verb and atom
/// means (the catalogues name them but do not define them) and runs the
/// tables the way the reference interpreter does. Nothing protocol-specific
/// is written by this emitter. Emitting the tables as operators instead was
/// tried first and abandoned: TLC's start-up level analysis unfolds every
/// operator application site separately, and a compiled table cost it about
/// fifteen seconds per run before a single state was explored.
/// </para>
/// <para>
/// TLA+ module names cannot contain a dot, so the generated file is
/// <c>Ax25DataLinkTables.tla</c> rather than a <c>.g.</c> name; its header
/// says it is generated.
/// </para>
/// </remarks>
public static class TlaEmitter
{
    public const string ModuleName = "Ax25DataLinkTables";
    public const string FileName = ModuleName + ".tla";
    public const string SemanticsModule = "Ax25Semantics";

    // ─── Table model (read from the JSON) ─────────────────────────────

    private sealed record Action(string Verb, string Kind);

    private sealed record Loop(int Start, int Length, GuardTermIR Predicate, bool TestAtEnd);

    private sealed record Transition(string Id, string On, string GuardText, IReadOnlyList<GuardTermIR> Guard,
        IReadOnlyList<Action> Actions, string Next, IReadOnlyList<Loop> Loops);

    private sealed record StatePage(string State, string Figure, IReadOnlyList<Transition> Transitions);

    private sealed record SubPath(string Id, string GuardText, IReadOnlyList<GuardTermIR> Guard,
        IReadOnlyList<Action> Actions, IReadOnlyList<Loop> Loops);

    private sealed record Subroutine(string Name, string Figure, IReadOnlyList<SubPath> Paths);

    /// <summary>Read the JSON tables of <paramref name="machine"/> from <paramref name="jsonDir"/> and render the module.</summary>
    /// <exception cref="InvalidDataException">The directory has no index.json, a page is malformed, or the tables use a shape the emitter cannot render.</exception>
    public static string EmitFromJsonDir(string jsonDir, string machine = "data_link")
    {
        var indexPath = Path.Combine(jsonDir, "index.json");
        if (!File.Exists(indexPath))
            throw new InvalidDataException($"no index.json in {jsonDir}: is this a spec/json output directory?");

        using var index = JsonDocument.Parse(File.ReadAllText(indexPath));
        var states = new List<StatePage>();
        var subs = new List<Subroutine>();
        foreach (var entry in index.RootElement.GetProperty("pages").EnumerateArray())
        {
            if (!string.Equals(entry.GetProperty("machine").GetString(), machine, StringComparison.Ordinal))
                continue;
            var file = entry.GetProperty("file").GetString() ?? throw new InvalidDataException($"{indexPath}: page entry with null file");
            var pagePath = Path.Combine(jsonDir, file);
            using var page = JsonDocument.Parse(File.ReadAllText(pagePath));
            var root = page.RootElement;
            var figure = root.GetProperty("source").GetProperty("figure").GetString() ?? "";
            switch (root.GetProperty("kind").GetString())
            {
                case "state":
                    states.Add(new StatePage(
                        root.GetProperty("state").GetString() ?? throw new InvalidDataException($"{pagePath}: null state"),
                        figure,
                        root.GetProperty("transitions").EnumerateArray().Select(t => new Transition(
                            Str(t, "id"), Str(t, "on"), Str(t, "guard"), GuardExpression.Parse(Str(t, "guard")),
                            ReadActions(t), Str(t, "next"), ReadLoops(t))).ToList()));
                    break;
                case "subroutines":
                    foreach (var s in root.GetProperty("subroutines").EnumerateArray())
                    {
                        subs.Add(new Subroutine(Str(s, "name"), figure,
                            s.GetProperty("paths").EnumerateArray().Select(p => new SubPath(
                                Str(p, "id"), Str(p, "guard"), GuardExpression.Parse(Str(p, "guard")),
                                ReadActions(p), ReadLoops(p))).ToList()));
                    }
                    break;
                default:
                    throw new InvalidDataException($"{pagePath}: unknown page kind");
            }
        }

        if (states.Count == 0)
            throw new InvalidDataException($"{jsonDir}: no state pages for machine `{machine}`");

        states.Sort((a, b) => string.CompareOrdinal(a.State, b.State));
        return Render(machine, states, subs);
    }

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString() ?? "";

    private static List<Action> ReadActions(JsonElement owner) =>
        owner.GetProperty("actions").EnumerateArray().Select(a => new Action(Str(a, "verb"), Str(a, "kind"))).ToList();

    private static List<Loop> ReadLoops(JsonElement owner) =>
        owner.GetProperty("loops").EnumerateArray().Select(l => new Loop(
            l.GetProperty("start").GetInt32(), l.GetProperty("length").GetInt32(),
            GuardExpression.ParseSingle(Str(l, "predicate")), l.GetProperty("test_at_end").GetBoolean())).ToList();

    // ─── Rendering ────────────────────────────────────────────────────

    private static string Render(string machine, List<StatePage> states, List<Subroutine> subs)
    {
        var sb = new StringBuilder();
        void Line(string text = "") => sb.Append(text).Append('\n');
        void Box(string text) => Line($"(* {text,-71} *)");

        var title = $" MODULE {ModuleName} ";
        var dashes = new string('-', (77 - title.Length) / 2);
        Line(dashes + title + dashes + (((77 - title.Length) % 2 == 1) ? "-" : ""));
        Line("(***************************************************************************)");
        Box($"GENERATED by codegen/src/Packet.Sdl.CodeGen.Tla from the JSON tables of");
        Box($"machine {machine}. Do not edit by hand: fix the figure in");
        Box("packethacking/ax25spec, bump the pin, regenerate spec/json, then");
        Box("  dotnet run --project codegen/src/Packet.Sdl.CodeGen -- --tla");
        Box("");
        Box("Table data only, in the shape of spec/json: per state page, per event,");
        Box("the transitions in file order; per subroutine, its paths. A guard is a");
        Box("sequence of Is(atom) / Not(atom) conjuncts; an action is a box of one");
        Box("shape class naming its verb; a loop is a region of the action chain.");
        Box($"{SemanticsModule}.tla says what the verbs and atoms mean and runs the");
        Box("tables as the reference interpreter does. See docs/tla.md.");
        Line("(***************************************************************************)");
        Line("EXTENDS TLC");
        Line();
        Line("\\* An action box: its SDL shape class and its canonical verb.");
        Line("Box(kind, verb) == [kind |-> kind, verb |-> verb]");
        Line();
        Line("\\* A guard conjunct: <<atom, negated>>.");
        Line("Is(atom) == <<atom, FALSE>>");
        Line("Not(atom) == <<atom, TRUE>>");
        Line();
        Line("\\* A loop region over actions[start .. start+len-1] (1-based); pred is the");
        Line("\\* continue condition; atEnd is do-while (TRUE) or while (FALSE).");
        Line("Loop(start, len, pred, atEnd) == [start |-> start, len |-> len, pred |-> pred, atEnd |-> atEnd]");
        Line();

        foreach (var s in states)
        {
            Line("(***************************************************************************)");
            Box($"{s.State} ({s.Figure})");
            Line("(***************************************************************************)");
            Line($"{PageOp(s.State)} ==");
            var groups = s.Transitions.GroupBy(t => t.On, StringComparer.Ordinal).ToList();
            for (var g = 0; g < groups.Count; g++)
            {
                Line($"    {(g == 0 ? "   " : "@@ ")}({Lit(groups[g].Key)} :> <<");
                var rows = groups[g].ToList();
                for (var i = 0; i < rows.Count; i++)
                {
                    var t = rows[i];
                    Line($"        [id |-> {Lit(t.Id)}, next |-> {Lit(t.Next)},");
                    Line($"         guard |-> {Guard(t.Guard)},");
                    Line($"         loops |-> {Loops(t.Id, t.Actions.Count, t.Loops)},");
                    Line($"         actions |-> {Actions(t.Actions, "                     ")}]{(i + 1 < rows.Count ? "," : "")}");
                }
                Line("    >>)");
            }
            if (groups.Count == 0) Line("    [ev \\in {} |-> << >>]");
            Line();
        }

        Line("\\* Every state page: Pages[state][event] is the sequence of that page's");
        Line("\\* transitions on that event, in file order.");
        Line("Pages ==");
        for (var i = 0; i < states.Count; i++)
            Line($"    {(i == 0 ? "   " : "@@ ")}({Lit(states[i].State)} :> {PageOp(states[i].State)})");
        Line();

        Line("(***************************************************************************)");
        Box($"Subroutines");
        Line("(***************************************************************************)");
        Line("\\* Subroutines[name] is the sequence of that subroutine's guarded paths.");
        Line("Subroutines ==");
        for (var i = 0; i < subs.Count; i++)
        {
            var sub = subs[i];
            Line($"    {(i == 0 ? "   " : "@@ ")}({Lit(sub.Name)} :> << \\* {sub.Figure}");
            for (var j = 0; j < sub.Paths.Count; j++)
            {
                var p = sub.Paths[j];
                Line($"        [id |-> {Lit(p.Id)},");
                Line($"         guard |-> {Guard(p.Guard)},");
                Line($"         loops |-> {Loops(sub.Name + " " + p.Id, p.Actions.Count, p.Loops)},");
                Line($"         actions |-> {Actions(p.Actions, "                     ")}]{(j + 1 < sub.Paths.Count ? "," : "")}");
            }
            Line("    >>)");
        }
        if (subs.Count == 0) Line("    [name \\in {} |-> << >>]");
        Line();
        Line(new string('=', 77));
        return sb.ToString();
    }

    private static string PageOp(string state) => "Page_" + Ident(state);

    private static string Actions(IReadOnlyList<Action> actions, string indent)
    {
        if (actions.Count == 0) return "<< >>";
        var sb = new StringBuilder("<<");
        for (var i = 0; i < actions.Count; i++)
        {
            sb.Append(i == 0 ? " " : ",\n" + indent + "   ");
            sb.Append("Box(").Append(Lit(actions[i].Kind)).Append(", ").Append(Lit(actions[i].Verb)).Append(')');
        }
        return sb.Append(" >>").ToString();
    }

    /// <summary>Loop regions, converted to 1-based starts. A region that runs past the chain is refused, not clipped.</summary>
    private static string Loops(string owner, int actionCount, IReadOnlyList<Loop> loops)
    {
        if (loops.Count == 0) return "<< >>";
        foreach (var l in loops)
        {
            if (l.Start < 0 || l.Length < 0 || l.Start + l.Length > actionCount)
                throw new InvalidDataException($"{owner}: loop over actions[{l.Start}..{l.Start + l.Length - 1}] does not fit the {actionCount}-action chain");
        }
        return "<< " + string.Join(", ", loops.Select(l =>
            $"Loop({(l.Start + 1).ToString(CultureInfo.InvariantCulture)}, {l.Length.ToString(CultureInfo.InvariantCulture)}, {Term(l.Predicate)}, {(l.TestAtEnd ? "TRUE" : "FALSE")})")) + " >>";
    }

    private static string Term(GuardTermIR t) => $"{(t.Negate ? "Not" : "Is")}({Lit(t.Atom)})";

    private static string Guard(IReadOnlyList<GuardTermIR> terms) =>
        terms.Count == 0 ? "<< >>" : "<< " + string.Join(", ", terms.Select(Term)) + " >>";

    /// <summary>A TLA+ string literal. Table text is plain ASCII; anything else (and a backtick, which SANY rejects inside a string) is refused rather than guessed at.</summary>
    private static string Lit(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in s)
        {
            if (ch is < ' ' or > '~' or '`')
                throw new InvalidDataException($"table string `{s}` contains a character outside printable ASCII, or a backtick, which a TLA+ string literal cannot carry");
            if (ch is '"' or '\\') sb.Append('\\');
            sb.Append(ch);
        }
        return sb.Append('"').ToString();
    }

    /// <summary>A TLA+ identifier fragment from a table name: letters, digits and underscores pass, anything else becomes an underscore.</summary>
    private static string Ident(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(char.IsAsciiLetterOrDigit(ch) || ch == '_' ? ch : '_');
        return sb.ToString();
    }
}
