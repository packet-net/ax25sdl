using System.Globalization;
using Packet.Sdl.Explorer;

namespace Packet.Sdl.TlaCheck;

/// <summary>
/// <c>--grid-with LIST</c>: an extra option set applied to every grid cell.
/// LIST is a comma list of the explorer's own option names without the
/// leading dashes, each bare (<c>t3</c>) or with a value (<c>release=A</c>);
/// where the explorer takes a comma list as the value, the items are joined
/// with <c>+</c> here (<c>frame-errors=control+info</c>,
/// <c>inject-frame=i-poll+i-poll-ahead</c>). The flag may be repeated; the
/// items accumulate. Only the options that leave the grid's own axes and seed
/// alone are accepted (docs/tla.md, "Grid runs with options"); the rest are
/// refused with the reason, before anything runs.
/// </summary>
public static class GridOptions
{
    private delegate ExplorerOptions Apply(ExplorerOptions o, string? value);

    private sealed record Known(bool TakesValue, string Values, Apply Apply);

    private static FlowControlAt Station(string option, string? v, bool allowNone = false) => v switch
    {
        "A" or "a" => FlowControlAt.A,
        "B" or "b" => FlowControlAt.B,
        "both" => FlowControlAt.Both,
        "none" when allowNone => FlowControlAt.None,
        _ => throw new ArgumentException($"--grid-with {option}: unknown station `{v}` (A, B, both)"),
    };

    private static ReleaseBy ReleaseStation(string option, string? v) => v switch
    {
        "A" or "a" => ReleaseBy.A,
        "both" => ReleaseBy.Both,
        _ => throw new ArgumentException($"--grid-with {option}: unknown station `{v}` (A, both)"),
    };

    private static int NonNegative(string option, string? v) =>
        int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : throw new ArgumentException($"--grid-with {option}: expected a non-negative integer, got `{v}`");

    private static InjectedFrame Form(string v) => v switch
    {
        "rej-poll" => InjectedFrame.RejPoll,
        "srej-command" => InjectedFrame.SrejCommand,
        "srej-poll" => InjectedFrame.SrejPoll,
        "ua-f0" => InjectedFrame.UaF0,
        "frmr" => InjectedFrame.Frmr,
        "i-frame" => InjectedFrame.IFrame,
        "i-poll" => InjectedFrame.IPoll,
        "i-poll-ahead" => InjectedFrame.IPollAhead,
        "i-poll-stale" => InjectedFrame.IPollStale,
        _ => throw new ArgumentException($"--grid-with inject-frame: unknown injected frame `{v}` (rej-poll, srej-command, srej-poll, ua-f0, frmr, i-frame, i-poll, i-poll-ahead, i-poll-stale)"),
    };

    private static Invariants Invariant(string v) => v switch
    {
        "defined-state" => Invariants.DefinedState,
        "sequence" => Invariants.SequenceSanity,
        "delivery" => Invariants.Delivery,
        "reject-coherence" => Invariants.RejectCoherence,
        "ack-coherence" => Invariants.AckCoherence,
        "quiescence" => Invariants.Quiescence,
        "deadlock" => Invariants.Deadlock,
        "flow-off-delivery" => Invariants.FlowOffDelivery,
        "busy-tracks-flow" => Invariants.BusyTracksFlow,
        "peer-busy-hold" => Invariants.PeerBusyHold,
        "busy-indication" => Invariants.BusyIndication,
        "unit-data" => Invariants.UnitData,
        "frame-error" => Invariants.FrameError,
        "disconnect-kept" => Invariants.DisconnectKept,
        _ => throw new ArgumentException($"--grid-with disable: unknown invariant `{v}`"),
    };

    private static string[] Items(string? v) => (v ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The options a grid cell may take, as the explorer names them.</summary>
    private static readonly Dictionary<string, Known> Accepted = new(StringComparer.Ordinal)
    {
        ["release"] = new(true, "A|both", (o, v) => o with { Release = ReleaseStation("release", v) }),
        ["release-early"] = new(false, "", (o, _) => o with { ReleaseEarly = true }),
        ["abandon"] = new(true, "A|both", (o, v) => o with { Abandon = ReleaseStation("abandon", v) }),
        ["reconnect"] = new(true, "A|both", (o, v) => o with { Reconnect = v switch { "A" or "a" => ReconnectBy.A, "both" => ReconnectBy.Both, _ => throw new ArgumentException($"--grid-with reconnect: unknown station `{v}` (A, both)") } }),
        ["data-after-reconnect"] = new(false, "", (o, _) => o with { DataAfterReconnect = true }),
        ["accept-give-up"] = new(false, "", (o, _) => o with { AcceptGiveUp = true }),
        ["local-busy"] = new(true, "A|B|both", (o, v) => o with { LocalBusy = Station("local-busy", v) }),
        ["long-busy"] = new(false, "", (o, _) => o with { LongBusy = true }),
        ["t3"] = new(false, "", (o, _) => o with { T3 = true }),
        ["t3-at"] = new(true, "A|B|both", (o, v) => o with { T3At = Station("t3-at", v) }),
        ["unit-data"] = new(true, "A|B|both", (o, v) => o with { UnitData = Station("unit-data", v) }),
        ["ui-form"] = new(true, "plain|poll|response|long", (o, v) => o with
        {
            UiForm = v switch
            {
                "plain" => UiForm.Plain,
                "poll" => UiForm.Poll,
                "response" => UiForm.Response,
                "long" => UiForm.Oversize,
                _ => throw new ArgumentException($"--grid-with ui-form: unknown UI form `{v}` (plain, poll, response, long)"),
            },
        }),
        ["frame-errors"] = new(true, "control+info+length+n1+i-response", (o, v) => o with
        {
            FrameErrors = Items(v).Aggregate(FrameErrors.None, (acc, f) => acc | f switch
            {
                "control" => FrameErrors.ControlField,
                "info" => FrameErrors.InfoNotPermitted,
                "length" => FrameErrors.Length,
                "n1" => FrameErrors.N1,
                "i-response" => FrameErrors.IResponse,
                _ => throw new ArgumentException($"--grid-with frame-errors: unknown frame error `{f}` (control, info, length, n1, i-response)"),
            }),
        }),
        ["inject"] = new(true, "A|B|both", (o, v) => o with { Inject = Station("inject", v) }),
        ["inject-frame"] = new(true, "FORM[+FORM]", (o, v) =>
        {
            var forms = Items(v).Select(Form).ToArray();
            if (forms.Length is < 1 or > 2) throw new ArgumentException("--grid-with inject-frame takes one form, or two joined with +");
            return o with { InjectedFrame = forms[0], InjectedThen = forms.Length == 2 ? forms[1] : InjectedFrame.None };
        }),
        ["n2"] = new(true, "N", (o, v) => o with { N2 = NonNegative("n2", v) }),
        ["drop-scope"] = new(true, "any|interior-i", (o, v) => o with
        {
            DropScope = v switch
            {
                "any" => DropScope.Any,
                "interior-i" => DropScope.InteriorI,
                _ => throw new ArgumentException($"--grid-with drop-scope: unknown drop scope `{v}` (any, interior-i)"),
            },
        }),
        ["flow-rounds"] = new(true, "N", (o, v) => o with { FlowRounds = NonNegative("flow-rounds", v) }),
        ["flow-off-after"] = new(true, "N", (o, v) => o with { FlowOffAfterDelivered = NonNegative("flow-off-after", v) }),
        ["disable"] = new(true, "INV[+INV]", (o, v) => o with { Invariants = Items(v).Aggregate(o.Invariants, (acc, i) => acc & ~Invariant(i)) }),
        ["selective-progress"] = new(false, "", (o, _) => o with { Invariants = o.Invariants | Invariants.SelectiveProgress }),
        ["rej-may-equal-vs"] = new(false, "", (o, _) => o with { RejMayEqualVs = true }),
        ["quiescence-accepts-timer-recovery"] = new(false, "", (o, _) => o with { TimerRecoveryIsQuiescent = true }),
    };

    /// <summary>The explorer options a grid cell may not take, and why.</summary>
    private static readonly Dictionary<string, string> Refused = new(StringComparer.Ordinal)
    {
        ["frames-ab"] = "frame counts are a grid axis",
        ["frames-ba"] = "frame counts are a grid axis",
        ["budget"] = "the fault budget is a grid axis",
        ["k"] = "k is a grid axis",
        ["srej"] = "SREJ is a grid axis",
        ["faults"] = "the fault kinds are a grid axis",
        ["seed"] = "the data-phase grids start from the connected seed and the stub grid has its own seeds",
        ["peer"] = "the data-phase grids run the table-driven peer; the v2.0 stub peers have their own grid (--grid-stub)",
        ["mod128"] = "a connect-phase seed option (the stub grid's cold seed sets it)",
        ["mod128b"] = "a Disconnected-seed option; the grids do not start there",
        ["peer-declines-sabme"] = "a connect-phase option; on the connected seed nothing sends SABME",
        ["data-after-confirm"] = "a Disconnected-seed option; the grids do not start there",
        ["data-before-confirm"] = "a Disconnected-seed option; the grids do not start there",
        ["flow-control"] = "use --grid-flow for the flow-control grid (B flow-controlling)",
        ["max-depth"] = "use --grid-max-depth N, which keeps the cell names as they are",
        ["max-states"] = "use --grid-max-states N, which keeps the cell names as they are",
        ["tables"] = "the grids run the current tables",
        ["fired"] = "an explorer report option; the coverage report covers it",
    };

    /// <summary>The usage text for the accepted options.</summary>
    public static string Help() =>
        string.Join("\n", Accepted.Select(kv => "                      " + kv.Key + (kv.Value.TakesValue ? "=" + kv.Value.Values : "")));

    /// <summary>
    /// Parse the accumulated <c>--grid-with</c> items into one function that
    /// applies them, in order, and a tag for the cell names (the items as
    /// given, joined with commas). Throws <see cref="ArgumentException"/> on
    /// an unknown, refused, repeated or malformed item.
    /// </summary>
    /// <param name="stubGrid">The stub grid, whose timer-free progress check is an axis.</param>
    public static (Func<ExplorerOptions, ExplorerOptions>? With, string Tag) Parse(IReadOnlyList<string> lists, bool stubGrid)
    {
        var items = lists.SelectMany(l => l.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        if (items.Count == 0) return (null, "");
        var steps = new List<(Apply Apply, string? Value)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var eq = item.IndexOf('=', StringComparison.Ordinal);
            var name = (eq < 0 ? item : item[..eq]).TrimStart('-');
            var value = eq < 0 ? null : item[(eq + 1)..];
            if (Refused.TryGetValue(name, out var why)) throw new ArgumentException($"--grid-with {name} is not grid-compatible: {why}");
            if (stubGrid && name == "selective-progress") throw new ArgumentException("--grid-with selective-progress is not compatible with --grid-stub: timer-free progress is a stub-grid axis");
            if (!Accepted.TryGetValue(name, out var known)) throw new ArgumentException($"--grid-with: unknown option `{name}`; the grid-compatible options are:\n{Help()}");
            if (!seen.Add(name)) throw new ArgumentException($"--grid-with {name} given twice");
            if (known.TakesValue && string.IsNullOrEmpty(value)) throw new ArgumentException($"--grid-with {name} needs a value: {name}={known.Values}");
            if (!known.TakesValue && value is not null) throw new ArgumentException($"--grid-with {name} takes no value");
            // Parse now, so a bad value fails here rather than in the first cell.
            known.Apply(new ExplorerOptions { TablesDir = "" }, value);
            steps.Add((known.Apply, value));
        }
        return (o => steps.Aggregate(o, (acc, s) => s.Apply(acc, s.Value)), string.Join(",", items.Select(i => i.TrimStart('-'))));
    }

    /// <summary>
    /// <c>--grid-max-states N</c> and <c>--grid-max-depth N</c>: the
    /// explorer's limits for every grid cell, 0 meaning none. Null when
    /// neither is given, so the grids keep their own. The limits are not part
    /// of a cell's name: a cell rerun with larger limits is the same cell.
    /// </summary>
    public static Func<ExplorerOptions, ExplorerOptions>? Caps(int? maxStates, int? maxDepth)
    {
        if (maxStates is null && maxDepth is null) return null;
        static int Limit(int n) => n == 0 ? int.MaxValue : n;
        return o => o with
        {
            MaxStates = maxStates is { } s ? Limit(s) : o.MaxStates,
            MaxDepth = maxDepth is { } d ? Limit(d) : o.MaxDepth,
        };
    }

    /// <summary><paramref name="first"/> then <paramref name="then"/>, either of which may be null.</summary>
    public static Func<ExplorerOptions, ExplorerOptions>? Then(Func<ExplorerOptions, ExplorerOptions>? first, Func<ExplorerOptions, ExplorerOptions>? then) =>
        first is null ? then : then is null ? first : o => then(first(o));

    /// <summary>
    /// <c>--grid-cells-from FILE</c>: the cells named, one full name per line
    /// (blank lines and <c># comments</c> skipped), from any of the grids and
    /// option sets. The name says the grid (<c>s-</c>: the stub grid;
    /// <c>g-</c>: the data-phase grid, with B flow-controlling when the part
    /// before any <c>@</c> ends <c>-flowB</c>) and the option set (the
    /// <c>@</c> suffix, parsed as <c>--grid-with</c>), so each cell runs as
    /// it did in its own grid, then with <paramref name="caps"/>. Each comes
    /// with its index in its own grid. Throws <see cref="ArgumentException"/>
    /// on a name no grid has, or one given twice.
    /// </summary>
    public static IReadOnlyList<(Scenario Cell, int Index)> FromNames(IEnumerable<string> lines, string currentDir, Func<ExplorerOptions, ExplorerOptions>? caps = null)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var hash = raw.IndexOf('#', StringComparison.Ordinal);
            var name = (hash >= 0 ? raw[..hash] : raw).Trim();
            if (name.Length == 0) continue;
            if (!seen.Add(name)) throw new ArgumentException($"--grid-cells-from: {name} is named twice");
            names.Add(name);
        }
        if (names.Count == 0) throw new ArgumentException("--grid-cells-from: no cells named");

        var grids = new Dictionary<(bool Stub, bool FlowB, string Tag), Dictionary<string, (Scenario Cell, int Index)>>();
        var result = new List<(Scenario, int)>();
        foreach (var name in names)
        {
            var at = name.IndexOf('@', StringComparison.Ordinal);
            var stem = at < 0 ? name : name[..at];
            var tag = at < 0 ? "" : name[(at + 1)..];
            var stub = stem.StartsWith("s-", StringComparison.Ordinal);
            if (!stub && !stem.StartsWith("g-", StringComparison.Ordinal))
                throw new ArgumentException($"--grid-cells-from: {name} is not a grid cell name (g-... or s-...)");
            var key = (stub, !stub && stem.EndsWith("-flowB", StringComparison.Ordinal), tag);
            if (!grids.TryGetValue(key, out var cells))
            {
                var (with, parsedTag) = Parse(tag.Length > 0 ? [tag] : [], stub);
                // The tag a grid run would have written: options given with
                // dashes or spaces would make a name no run ever recorded.
                if (parsedTag != tag) throw new ArgumentException($"--grid-cells-from: {name}: write the options as a grid run names them, @{parsedTag}");
                with = Then(with, caps);
                var grid = stub ? Scenarios.StubGrid(currentDir, with, tag) : Scenarios.Grid(currentDir, key.Item2, with, tag);
                cells = grid.Select((c, i) => (c, i)).ToDictionary(x => x.c.Name, x => x, StringComparer.Ordinal);
                grids[key] = cells;
            }
            if (!cells.TryGetValue(name, out var found)) throw new ArgumentException($"--grid-cells-from: no grid has a cell named {name}");
            result.Add(found);
        }
        return result;
    }

    /// <summary>
    /// Every cell checked against the stepper's refusals before anything
    /// runs: the first refused cell's reason, naming the cell, or null.
    /// </summary>
    public static string? FirstRefusal(IEnumerable<Scenario> cells)
    {
        foreach (var c in cells)
        {
            try { Stepper.Validate(c.Options); }
            catch (ArgumentException ex)
            {
                var msg = ex.Message;
                var cut = msg.IndexOf(" (Parameter '", StringComparison.Ordinal);
                return $"the grid with these options is refused, first at cell {c.Name}: {(cut >= 0 ? msg[..cut] : msg)}";
            }
        }
        return null;
    }

    /// <summary>
    /// Run <paramref name="body"/> over the cells' indices, the likely largest
    /// first and one cell at a time per slot (no chunking), so a long run does
    /// not end with one slot grinding through the biggest cells alone. The
    /// size guess grows with the frames and steeply with the fault budget
    /// (3+3 frames: about 1 000 states at budget 0, 51 000 at 1, 600 000 at 2).
    /// </summary>
    public static void LargestFirst(IReadOnlyList<Scenario> cells, int parallel, Action<int> body)
    {
        var order = Enumerable.Range(0, cells.Count)
            .OrderByDescending(i => Math.Pow(cells[i].Options.FramesAb + cells[i].Options.FramesBa + 1, cells[i].Options.Budget + 2))
            .ThenBy(i => i)
            .ToList();
        Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(order, System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering),
            new ParallelOptions { MaxDegreeOfParallelism = parallel }, body);
    }

    /// <summary>
    /// <c>--grid-cells SPEC</c>: a comma list of cell indices (<c>12</c>),
    /// index ranges (<c>0-143</c>), strides (<c>%4=1</c>: every index i with
    /// i mod 4 = 1) and name patterns (anything else, with <c>*</c> and
    /// <c>?</c> as wildcards). A cell is selected when any item matches it.
    /// Indices count from 0 in the order <c>--list</c> prints.
    /// </summary>
    public static Func<int, string, bool> CellFilter(string spec)
    {
        var tests = new List<Func<int, string, bool>>();
        foreach (var item in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (item.StartsWith('%'))
            {
                var parts = item[1..].Split('=');
                if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var m) || m < 1
                    || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var r) || r >= m)
                    throw new ArgumentException($"--grid-cells: `{item}` is not a stride %M=R with 0 <= R < M");
                tests.Add((i, _) => i % m == r);
            }
            else if (int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var one))
            {
                tests.Add((i, _) => i == one);
            }
            else if (item.Split('-') is [var lo, var hi]
                     && int.TryParse(lo, NumberStyles.None, CultureInfo.InvariantCulture, out var from)
                     && int.TryParse(hi, NumberStyles.None, CultureInfo.InvariantCulture, out var to))
            {
                if (to < from) throw new ArgumentException($"--grid-cells: range `{item}` runs backwards");
                tests.Add((i, _) => i >= from && i <= to);
            }
            else
            {
                var re = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(item).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$");
                tests.Add((_, n) => re.IsMatch(n));
            }
        }
        if (tests.Count == 0) throw new ArgumentException("--grid-cells: no cells named");
        return (i, n) => tests.Any(t => t(i, n));
    }
}
