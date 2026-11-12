using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Packet.Sdl.CodeGen.Tla;
using Packet.Sdl.Explorer;

namespace Packet.Sdl.TlaCheck;

/// <summary>
/// One state of a TLC counterexample: the fields read from its <c>viol</c>
/// and <c>last</c> variables (viol, violAt, violMsg, move, at, tid, lossy).
/// </summary>
public sealed record TlcStep(int Number, string Header, IReadOnlyDictionary<string, string> Fields)
{
    public string Field(string name) => Fields.TryGetValue(name, out var v) ? v : "";
}

/// <summary>What one TLC run reported.</summary>
/// <param name="Kind">"clean", "invariant", "deadlock", "temporal", "error" or "timeout".</param>
/// <param name="Invariant">The violated invariant's name, for <c>invariant</c>.</param>
public sealed record TlcResult(
    string Kind,
    string? Invariant,
    long Generated,
    long Distinct,
    int Depth,
    IReadOnlyList<TlcStep> Trace,
    TimeSpan Elapsed,
    string Message);

/// <summary>Which of the explorer's checks one TLC run stands in for.</summary>
public enum Phase
{
    /// <summary>Per-step and state invariants, breadth-first, no deadlock check: the explorer's search.</summary>
    Safety,
    /// <summary>TLC's deadlock check, which the Rest stutter makes exactly the explorer's deadlock (a terminal, non-quiescent state).</summary>
    Deadlock,
    /// <summary>[]&lt;&gt;Quiescent under weak fairness of the non-fault moves (the explorer's Quiescence).</summary>
    Quiescence,
    /// <summary>The same with no fairness for T1 (the explorer's SelectiveProgress).</summary>
    TimerFree,
    /// <summary>
    /// The safety search again without the VIEW, dumping every state, so the
    /// step record of every edge survives and the table arms fired can be read
    /// off (TwoStation.tla's last.arms). For coverage only; it stops at the
    /// first violation, as the explorer does.
    /// </summary>
    Coverage,
}

/// <summary>Writes a TLC model for an explorer scenario and runs it.</summary>
public sealed class Tlc
{
    private readonly string _jar;
    private readonly string _harness;
    private readonly string _tlaDir;
    private readonly string _meaning;
    private readonly int _workers;
    private readonly string _heap;
    private readonly TimeSpan _timeout;

    /// <summary>The meaning module Ax25Semantics.tla names when no other is selected.</summary>
    public const string DefaultMeaning = "Ax25Meaning";

    /// <param name="tlaDir">The hand-written modules (spec/tla): the runner, the shared values, the meanings and Ax25Semantics.tla.</param>
    /// <param name="meaning">
    /// The meaning module the runner is instantiated with: a module name in
    /// <paramref name="tlaDir"/>, or a path to a .tla file elsewhere.
    /// </param>
    public Tlc(string jar, string harness, string tlaDir, string meaning, int workers, string heap, TimeSpan timeout)
    {
        _jar = jar;
        _harness = harness;
        _tlaDir = tlaDir;
        _meaning = meaning;
        _workers = workers;
        _heap = heap;
        _timeout = timeout;
    }

    /// <summary>
    /// Lay out a model directory: the tables rendered from <paramref name="tablesDir"/>, the
    /// hand-written modules, the selected meaning and the harness. The meaning is selected by
    /// rewriting the one EXTENDS line of Ax25Semantics.tla, so TLC sees the same modules as
    /// for the default, under another name, with no runtime indirection.
    /// </summary>
    public void Prepare(string workDir, string tablesDir)
    {
        Directory.CreateDirectory(workDir);
        File.WriteAllText(Path.Combine(workDir, TlaEmitter.FileName), TlaEmitter.EmitFromJsonDir(tablesDir));
        foreach (var file in Directory.GetFiles(_tlaDir, "*.tla"))
        {
            var name = Path.GetFileName(file);
            if (name is TlaEmitter.FileName or SemanticsFile) continue;
            File.Copy(file, Path.Combine(workDir, name), overwrite: true);
        }
        var module = _meaning;
        if (_meaning.EndsWith(".tla", StringComparison.Ordinal))
        {
            module = Path.GetFileNameWithoutExtension(_meaning);
            File.Copy(_meaning, Path.Combine(workDir, module + ".tla"), overwrite: true);
        }
        else if (CheckMeaning(_tlaDir, _meaning, _harness) is { } why)
        {
            throw new InvalidOperationException(why);
        }
        File.WriteAllText(Path.Combine(workDir, SemanticsFile), SelectMeaning(File.ReadAllText(Path.Combine(_tlaDir, SemanticsFile)), module));
        File.Copy(_harness, Path.Combine(workDir, Path.GetFileName(_harness)), overwrite: true);
    }

    private const string SemanticsFile = TlaEmitter.SemanticsModule + ".tla";

    /// <summary>
    /// Why <paramref name="meaning"/> cannot be selected, or null when it can: a module
    /// name must exist in <paramref name="tlaDir"/>; a .tla path must exist and must not
    /// share its name with a module in <paramref name="tlaDir"/> (it would replace that
    /// module in the model directory) or with the generated tables or the harness.
    /// </summary>
    public static string? CheckMeaning(string tlaDir, string meaning, string harness)
    {
        if (!meaning.EndsWith(".tla", StringComparison.Ordinal))
            return File.Exists(Path.Combine(tlaDir, meaning + ".tla")) ? null : $"meaning module {meaning}.tla not found in {tlaDir}";
        if (!File.Exists(meaning)) return $"meaning file {meaning} not found";
        var name = Path.GetFileName(meaning);
        if (File.Exists(Path.Combine(tlaDir, name)) || name == TlaEmitter.FileName || name == Path.GetFileName(harness))
            return $"meaning file {name} has the name of a module the model already uses; rename it (and its MODULE line)";
        return null;
    }

    /// <summary>Ax25Semantics.tla with its EXTENDS line naming <paramref name="module"/> instead of the default meaning.</summary>
    public static string SelectMeaning(string semantics, string module)
    {
        var extends = new Regex("^EXTENDS " + DefaultMeaning + "(?=\\r?$)", RegexOptions.Multiline);
        if (extends.Count(semantics) != 1)
            throw new InvalidOperationException($"{SemanticsFile} must contain exactly one line 'EXTENDS {DefaultMeaning}'");
        return extends.Replace(semantics, "EXTENDS " + module);
    }

    private static string Bool(bool b) => b ? "TRUE" : "FALSE";

    private static string Inv(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static string Set(IEnumerable<string> items) => "{" + string.Join(", ", items.Select(i => "\"" + i + "\"")) + "}";

    /// <summary>
    /// Rows with T3, a local busy condition or UI datagrams check liveness with a weak
    /// fairness per one-shot layer 3 move as well (TwoStation.tla,
    /// QuiescenceLiveL3): their poll cycles would otherwise meet the
    /// disjunctive fairness while such a move waits.
    /// </summary>
    private static bool OneShotFair(ExplorerOptions o) => o.T3 || o.LocalBusy != FlowControlAt.None || o.UnitData != FlowControlAt.None;

    /// <summary>The TLC configuration for <paramref name="o"/> in one phase.</summary>
    public static string Config(ExplorerOptions o, Phase phase, bool depthBound)
    {
        bool On(Invariants i) => o.Invariants.HasFlag(i);
        var faults = new List<string>();
        if (o.Faults.HasFlag(FaultKinds.Drop)) faults.Add("drop");
        if (o.Faults.HasFlag(FaultKinds.Duplicate)) faults.Add("dup");
        if (o.Faults.HasFlag(FaultKinds.Reorder)) faults.Add("reorder");
        var flowAt = o.FlowControl switch
        {
            FlowControlAt.A => new[] { "A" },
            FlowControlAt.B => new[] { "B" },
            FlowControlAt.Both => new[] { "A", "B" },
            _ => Array.Empty<string>(),
        };
        var releaseAt = o.Release switch
        {
            ReleaseBy.A => new[] { "A" },
            ReleaseBy.Both => new[] { "A", "B" },
            _ => Array.Empty<string>(),
        };
        var reconnectAt = o.Reconnect switch
        {
            ReconnectBy.A => new[] { "A" },
            ReconnectBy.Both => new[] { "A", "B" },
            _ => Array.Empty<string>(),
        };
        var busyAt = o.LocalBusy switch
        {
            FlowControlAt.A => new[] { "A" },
            FlowControlAt.B => new[] { "B" },
            FlowControlAt.Both => new[] { "A", "B" },
            _ => Array.Empty<string>(),
        };
        var unitDataAt = o.UnitData switch
        {
            FlowControlAt.A => new[] { "A" },
            FlowControlAt.B => new[] { "B" },
            FlowControlAt.Both => new[] { "A", "B" },
            _ => Array.Empty<string>(),
        };
        var stepChecks = new List<string>();
        if (On(Invariants.RejectCoherence)) stepChecks.Add("RejectCoherence");
        if (On(Invariants.Delivery)) stepChecks.Add("Delivery");
        if (On(Invariants.FlowOffDelivery)) stepChecks.Add("FlowOffDelivery");
        if (On(Invariants.BusyTracksFlow)) stepChecks.Add("BusyTracksFlow");
        if (On(Invariants.PeerBusyHold)) stepChecks.Add("PeerBusyHold");
        if (On(Invariants.BusyIndication)) stepChecks.Add("BusyIndication");
        if (On(Invariants.UnitData)) stepChecks.Add("UnitData");
        if (On(Invariants.FrameError)) stepChecks.Add("FrameError");
        var injectAt = o.Inject switch
        {
            FlowControlAt.A => new[] { "A" },
            FlowControlAt.B => new[] { "B" },
            FlowControlAt.Both => new[] { "A", "B" },
            _ => Array.Empty<string>(),
        };
        var frameErrors = new List<string>();
        if (o.FrameErrors.HasFlag(FrameErrors.ControlField)) frameErrors.Add("control");
        if (o.FrameErrors.HasFlag(FrameErrors.InfoNotPermitted)) frameErrors.Add("info");
        if (o.FrameErrors.HasFlag(FrameErrors.Length)) frameErrors.Add("length");
        if (o.FrameErrors.HasFlag(FrameErrors.N1)) frameErrors.Add("n1");
        if (o.FrameErrors.HasFlag(FrameErrors.IResponse)) frameErrors.Add("iresp");

        var sb = new StringBuilder();
        sb.Append("\\* Generated by Packet.Sdl.TlaCheck from an explorer scenario; phase ").Append(phase).Append('\n');
        sb.Append("CONSTANTS\n");
        void C(string name, string value) => sb.Append("    ").Append(name).Append(" = ").Append(value).Append('\n');
        C("FramesAB", Inv(o.FramesAb));
        C("FramesBA", Inv(o.FramesBa));
        C("K", Inv(o.K));
        C("Srej", Bool(o.Srej));
        C("N2", Inv(o.N2));
        C("Budget", Inv(o.Budget));
        C("Faults", Set(faults));
        C("InteriorDropsOnly", Bool(o.DropScope == DropScope.InteriorI));
        C("Seed", o.Seed switch
        {
            SeedKind.Connected => "\"connected\"",
            SeedKind.Disconnected => "\"disconnected\"",
            _ => "\"awaiting-v22\"",
        });
        C("Mod128A", Bool(o.Modulo128A));
        C("DataAfterConfirm", Bool(o.DataAfterConfirm));
        C("PeerDeclinesSabme", Bool(o.PeerDeclinesSabme));
        C("Peer", o.Peer switch
        {
            PeerKind.Tables => "\"tables\"",
            PeerKind.V20Frmr => "\"v20-frmr\"",
            _ => "\"v20-dm\"",
        });
        C("FlowAt", Set(flowAt));
        C("ReleaseAt", Set(releaseAt));
        C("ReleaseEarly", Bool(o.ReleaseEarly));
        C("ReconnectAt", Set(reconnectAt));
        C("DataBeforeConfirm", Bool(o.DataBeforeConfirm));
        C("AcceptGiveUp", Bool(o.AcceptGiveUp));
        C("BusyAt", Set(busyAt));
        C("LongBusy", Bool(o.LongBusy));
        C("T3Moves", Bool(o.T3));
        C("UnitDataAt", Set(unitDataAt));
        C("UiForm", o.UiForm switch
        {
            UiForm.Poll => "\"poll\"",
            UiForm.Response => "\"response\"",
            UiForm.Oversize => "\"long\"",
            _ => "\"plain\"",
        });
        C("FrameErrors", Set(frameErrors));
        C("InjectAt", Set(injectAt));
        static string Form(InjectedFrame f) => f switch
        {
            InjectedFrame.RejPoll => "\"rej-poll\"",
            InjectedFrame.SrejCommand => "\"srej-command\"",
            InjectedFrame.SrejPoll => "\"srej-poll\"",
            InjectedFrame.UaF0 => "\"ua-f0\"",
            InjectedFrame.Frmr => "\"frmr\"",
            InjectedFrame.IFrame => "\"i-frame\"",
            InjectedFrame.IPoll => "\"i-poll\"",
            InjectedFrame.IPollAhead => "\"i-poll-ahead\"",
            InjectedFrame.IPollStale => "\"i-poll-stale\"",
            _ => "\"none\"",
        };
        C("InjectForm", Form(o.InjectedFrame));
        C("InjectThen", Form(o.InjectedThen));
        C("FlowRounds", Inv(o.FlowRounds));
        C("FlowOffAfter", Inv(o.FlowOffAfterDelivered));
        C("StepChecks", Set(stepChecks));
        C("TimerRecoveryQuiescent", Bool(o.TimerRecoveryIsQuiescent));
        C("RejMayEqualVs", Bool(o.RejMayEqualVs));
        C("MaxDepth", Inv(o.MaxDepth));

        switch (phase)
        {
            case Phase.Safety:
            case Phase.Deadlock:
            case Phase.Coverage:
                sb.Append("INIT Init\nNEXT Next\n");
                sb.Append("INVARIANTS NoStepViolation");
                if (On(Invariants.DefinedState)) sb.Append(" DefinedState");
                if (On(Invariants.SequenceSanity)) sb.Append(" SequenceSanity");
                if (On(Invariants.AckCoherence)) sb.Append(" AckCoherence");
                sb.Append('\n');
                // The VIEW leaves out `last`, so distinct states are the explorer's.
                if (phase != Phase.Coverage) sb.Append("VIEW View\n");
                break;
            case Phase.Quiescence:
                sb.Append("SPECIFICATION Spec\nPROPERTY ").Append(OneShotFair(o) ? "QuiescenceLiveL3" : "QuiescenceLive").Append('\n');
                break;
            case Phase.TimerFree:
                sb.Append("SPECIFICATION Spec\nPROPERTY ").Append(OneShotFair(o) ? "TimerFreeLiveL3" : "TimerFreeLive").Append('\n');
                break;
            default:
                break;
        }
        if (depthBound) sb.Append("CONSTRAINT WithinDepth\n");
        return sb.ToString();
    }

    public TlcResult Run(string workDir, ExplorerOptions o, Phase phase, bool depthBound)
    {
        var cfgName = "MC_" + phase + ".cfg";
        File.WriteAllText(Path.Combine(workDir, cfgName), Config(o, phase, depthBound));
        var meta = Path.Combine(workDir, "meta_" + phase);
        if (Directory.Exists(meta)) Directory.Delete(meta, recursive: true);
        var tmp = Path.Combine(workDir, "jtmp");
        Directory.CreateDirectory(tmp);

        var psi = new ProcessStartInfo("java")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // TLC extracts its standard modules into java.io.tmpdir; keep them in
        // the model directory rather than the shared /tmp.
        foreach (var a in new[]
                 {
                     "-XX:+UseParallelGC", "-Xmx" + _heap, "-Djava.io.tmpdir=" + tmp,
                     "-cp", _jar, "tlc2.TLC",
                     "-workers", Inv(_workers), "-metadir", meta, "-config", cfgName, "-nowarning",
                 })
        {
            psi.ArgumentList.Add(a);
        }
        // The harness's Rest stutter means TLC's deadlock check is exactly the
        // explorer's; every other phase leaves it to the Deadlock phase.
        if (phase != Phase.Deadlock) psi.ArgumentList.Add("-deadlock");
        if (phase == Phase.Coverage)
        {
            psi.ArgumentList.Add("-dump");
            psi.ArgumentList.Add("states");
        }
        if (phase is Phase.Quiescence or Phase.TimerFree)
        {
            psi.ArgumentList.Add("-lncheck");
            psi.ArgumentList.Add("final");
        }
        psi.ArgumentList.Add("TwoStation.tla");

        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start java");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(_timeout))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            p.WaitForExit();
            File.WriteAllText(Path.Combine(workDir, "tlc_" + phase + ".out"), stdout.Result + stderr.Result);
            return new TlcResult("timeout", null, 0, 0, 0, Array.Empty<TlcStep>(), sw.Elapsed, $"no verdict within {_timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s");
        }
        var text = stdout.Result + stderr.Result;
        File.WriteAllText(Path.Combine(workDir, "tlc_" + phase + ".out"), text);
        return Parse(text, sw.Elapsed);
    }

    private static readonly Regex Quoted = new(@"""((?:[^""\\]|\\.)*)""");

    /// <summary>
    /// The table arms (State/transition, Subroutine:path) on any state of a
    /// Coverage run's dump. The dump of a large cell runs to gigabytes, so it
    /// is streamed a line at a time and deleted once read.
    /// </summary>
    public static HashSet<string> ReadArms(string workDir)
    {
        var arms = new HashSet<string>(StringComparer.Ordinal);
        var dump = Path.Combine(workDir, "states.dump");
        if (!File.Exists(dump)) return arms;
        StringBuilder? field = null;
        foreach (var line in File.ReadLines(dump))
        {
            var at = line.IndexOf("arms |->", StringComparison.Ordinal);
            if (field is null && at < 0) continue;
            field ??= new StringBuilder();
            field.Append(at >= 0 ? line[at..] : line).Append(' ');
            var text = field.ToString();
            var open = text.IndexOf('{', StringComparison.Ordinal);
            if (open < 0) continue;
            var close = text.IndexOf('}', open);
            if (close < 0) continue;
            foreach (Match q in Quoted.Matches(text[open..close])) arms.Add(q.Groups[1].Value);
            field = null;
        }
        File.Delete(dump);
        return arms;
    }

    private static readonly Regex StatesLine = new(@"^(\d+) states generated, (\d+) distinct states found", RegexOptions.Multiline);
    private static readonly Regex DepthLine = new(@"The depth of the complete state graph search is (\d+)\.", RegexOptions.Multiline);
    private static readonly Regex InvariantLine = new(@"^Error: Invariant (\w+) is violated", RegexOptions.Multiline);
    private static readonly Regex StateHeader = new(@"^(?:State )?(\d+): (.*)$");
    private static readonly Regex VariableLine = new(@"^/\\ (\w+) = (.*)$");

    public static TlcResult Parse(string text, TimeSpan elapsed)
    {
        long generated = 0, distinct = 0;
        foreach (Match m in StatesLine.Matches(text))
        {
            generated = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            distinct = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        var depth = 0;
        var dm = DepthLine.Match(text);
        if (dm.Success) depth = int.Parse(dm.Groups[1].Value, CultureInfo.InvariantCulture);

        // Each state prints as "State n: <action>" followed by one "/\ var = value"
        // block per variable, a value possibly running over several lines.
        var trace = new List<TlcStep>();
        Dictionary<string, string>? fields = null;
        string? variable = null;
        var value = new StringBuilder();
        void Flush()
        {
            if (fields is null || variable is null) return;
            var v = value.ToString();
            if (variable == "viol")
            {
                fields["viol"] = Field(v, "kind");
                fields["violAt"] = Field(v, "at");
                fields["violMsg"] = Field(v, "msg");
            }
            else if (variable == "last")
            {
                fields["move"] = Field(v, "move");
                fields["at"] = Field(v, "at");
                fields["tid"] = Field(v, "tid");
                fields["lossy"] = Field(v, "lossy");
            }
            variable = null;
            value.Clear();
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var h = StateHeader.Match(line);
            if (h.Success)
            {
                Flush();
                fields = new Dictionary<string, string>(StringComparer.Ordinal);
                trace.Add(new TlcStep(int.Parse(h.Groups[1].Value, CultureInfo.InvariantCulture), h.Groups[2].Value, fields));
                continue;
            }
            var f = VariableLine.Match(line);
            if (f.Success && fields is not null)
            {
                Flush();
                variable = f.Groups[1].Value;
                value.Append(f.Groups[2].Value);
                continue;
            }
            if (line.Length == 0 || !line.StartsWith(' '))
            {
                Flush();
                if (line.Length == 0) fields = null;
                continue;
            }
            if (variable is not null) value.Append(' ').Append(line.Trim());
        }
        Flush();

        string kind;
        string? invariant = null;
        var im = InvariantLine.Match(text);
        if (im.Success)
        {
            kind = "invariant";
            invariant = im.Groups[1].Value;
        }
        else if (text.Contains("Error: Deadlock reached.", StringComparison.Ordinal)) kind = "deadlock";
        else if (text.Contains("Error: Temporal properties were violated.", StringComparison.Ordinal)) kind = "temporal";
        else if (text.Contains("Model checking completed. No error has been found.", StringComparison.Ordinal)) kind = "clean";
        else kind = "error";

        var message = kind == "error"
            ? string.Join(" | ", text.Split('\n').Where(l => l.StartsWith("Error", StringComparison.Ordinal) || l.Contains("Exception", StringComparison.Ordinal)).Take(4))
            : "";
        return new TlcResult(kind, invariant, generated, distinct, depth, trace, elapsed, message);
    }

    /// <summary>The first string field <paramref name="name"/> in a printed record value.</summary>
    private static string Field(string record, string name)
    {
        var m = Regex.Match(record, @"\b" + name + @" \|-> ""((?:[^""\\]|\\.)*)""");
        return m.Success ? m.Groups[1].Value.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal) : "";
    }
}
