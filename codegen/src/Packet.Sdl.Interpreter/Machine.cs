using System.Globalization;
using Packet.Sdl.IR;

namespace Packet.Sdl.Interpreter;

/// <summary>Symbolic timer status. Expiry is remembered so the <c>T1_expired</c> guard atom can be answered.</summary>
public enum TimerStatus
{
    Stopped,
    Running,
    Expired,
}

/// <summary>
/// One injected stimulus: an event name (a member of the tables' <c>on:</c>
/// vocabulary) plus the received-frame fields the guards and actions may
/// consult, plus explicit valuations for the few environment-supplied guard
/// atoms the machine cannot derive from its own state.
/// </summary>
/// <param name="Event">Event name, e.g. <c>SABM_received</c>, <c>DL_CONNECT_request</c>, <c>T1_expiry</c>.</param>
/// <param name="Pf">P/F bit of the received frame (P for commands, F for responses).</param>
/// <param name="Command">True = received frame is a command; false = response. Defaults exist for frame types with a fixed role.</param>
/// <param name="Nr">N(R) of the received frame.</param>
/// <param name="Ns">N(S) of the received frame.</param>
/// <param name="Data">Opaque payload label; used for queue bookkeeping (DL_DATA_request / I_frame_pops_off_queue).</param>
/// <param name="Atoms">Environment-atom valuations (only atoms in <see cref="DataLinkMachine.EnvironmentAtoms"/> may appear).</param>
public sealed record EventInput(
    string Event,
    bool? Pf = null,
    bool? Command = null,
    int? Nr = null,
    int? Ns = null,
    string? Data = null,
    IReadOnlyDictionary<string, bool>? Atoms = null);

/// <summary>The outcome of dispatching one event.</summary>
public sealed record DispatchResult(string TransitionId, string NextState, IReadOnlyList<Effect> Effects);

/// <summary>
/// Reference interpreter for one Data Link machine instance over the JSON
/// tables. Consumes an event + guard-atom valuations, selects the unique
/// matching transition, executes its actions (including subroutine calls
/// and loop regions) as abstract effects, and applies the state change.
/// </summary>
/// <remarks>
/// <para><b>Guard timing.</b> The composed transition guard is evaluated
/// once, against the pre-transition state plus the event — the tables are
/// built for select-then-execute (the Resolver's stale-read substitution,
/// e.g. <c>vs_eq_nr</c>, exists precisely to keep hoisted guards correct).
/// Subroutine path guards and loop predicates, by contrast, are evaluated
/// <em>live</em> against the mutating state, because the figures place those
/// decisions after processing boxes in the same chain (e.g.
/// <c>set_peer_receiver_busy</c> runs before <c>Check_I_Frame_Acknowledged</c>
/// branches on <c>peer_receiver_busy</c>).</para>
/// <para><b>Pending TX registers.</b> The figures stage outgoing-frame
/// fields in P / F / N(s) / N(r) registers and every frame emission consumes
/// them. P and F reset to 0 at the start of each dispatch (§6.2: "When not
/// used, the P/F bit is set to 0"); N(s) / N(r) reset to unset, and a frame
/// emission that needs one while unset is an error — it means the table
/// omitted the staging assignment.</para>
/// </remarks>
public sealed class DataLinkMachine
{
    private const int MaxSubroutineDepth = 16;
    private const int MaxLoopIterations = 4096;

    /// <summary>Guard atoms the machine cannot derive; the environment (the trace step) supplies them. Anything else in <see cref="EventInput.Atoms"/> is rejected.</summary>
    public static readonly IReadOnlySet<string> EnvironmentAtoms = new HashSet<string>(StringComparer.Ordinal)
    {
        "able_to_establish",
        "info_field_length_le_N1_and_content_is_octet_aligned",
    };

    private readonly TableSet _tables;

    // ─── Modelled SDL state ───────────────────────────────────────────
    public string State { get; private set; }
    public int Vs { get; private set; }
    public int Vr { get; private set; }
    public int Va { get; private set; }
    public int Rc { get; private set; }
    public int Modulo { get; private set; } = 8;
    public int K { get; private set; } = 4;
    public int N1 { get; private set; } = 256;
    public int N2 { get; private set; } = 10;
    public bool Version22 { get; private set; }
    public bool SrejEnabled { get; private set; }
    public bool HalfDuplex { get; private set; }
    public bool LayerThreeInitiated { get; private set; }
    public bool OwnReceiverBusy { get; private set; }
    public bool PeerReceiverBusy { get; private set; }
    public bool RejectException { get; private set; }
    public int SrejectException { get; private set; }
    public bool AcknowledgePending { get; private set; }
    public TimerStatus T1 { get; private set; } = TimerStatus.Stopped;
    public TimerStatus T3 { get; private set; } = TimerStatus.Stopped;

    private readonly List<string> _queue = new();
    private readonly Dictionary<int, string> _savedIFrames = new();
    private readonly Dictionary<string, string> _symbolic = new(StringComparer.Ordinal);

    /// <summary>The I-frame queue, head first. Entries are opaque payload labels.</summary>
    public IReadOnlyList<string> Queue => _queue;

    /// <summary>Symbolic registers (SRT, T1V, T2, NextT1): name → last assigned expression text.</summary>
    public IReadOnlyDictionary<string, string> Symbolic => _symbolic;

    // ─── Per-dispatch scratch ─────────────────────────────────────────
    private EventInput _event = new("");
    private string? _frameType;
    private List<Effect> _effects = new();
    private int _pendingP;
    private int _pendingF;
    private int? _pendingNr;
    private int? _pendingNs;
    private int? _x;
    private int _queueInsertIndex;
    private string? _poppedData;

    public DataLinkMachine(TableSet tables, string initialState)
    {
        _tables = tables ?? throw new ArgumentNullException(nameof(tables));
        if (!tables.States.ContainsKey(initialState))
            throw new InvalidOperationException(
                $"unknown initial state `{initialState}` — tables define: {string.Join(", ", tables.States.Keys.Order(StringComparer.Ordinal))}");
        State = initialState;
    }

    // ─── Seeding + assertion surface (string-keyed for the trace runner) ──

    /// <summary>Set one variable by trace key (e.g. <c>vs</c>, <c>modulo</c>, <c>layer_3_initiated</c>).</summary>
    public void SetVariable(string key, string value)
    {
        switch (key)
        {
            case "vs": Vs = ParseInt(key, value); break;
            case "vr": Vr = ParseInt(key, value); break;
            case "va": Va = ParseInt(key, value); break;
            case "rc": Rc = ParseInt(key, value); break;
            case "modulo": Modulo = ParseInt(key, value); break;
            case "k": K = ParseInt(key, value); break;
            case "n1": N1 = ParseInt(key, value); break;
            case "n2": N2 = ParseInt(key, value); break;
            case "sreject_exception": SrejectException = ParseInt(key, value); break;
            case "version_2_2": Version22 = ParseBool(key, value); break;
            case "srej_enabled": SrejEnabled = ParseBool(key, value); break;
            case "half_duplex": HalfDuplex = ParseBool(key, value); break;
            case "layer_3_initiated": LayerThreeInitiated = ParseBool(key, value); break;
            case "own_receiver_busy": OwnReceiverBusy = ParseBool(key, value); break;
            case "peer_receiver_busy": PeerReceiverBusy = ParseBool(key, value); break;
            case "reject_exception": RejectException = ParseBool(key, value); break;
            case "ack_pending": AcknowledgePending = ParseBool(key, value); break;
            default:
                throw new InvalidOperationException($"unknown variable key `{key}` (see docs/golden-traces.md for the vocabulary)");
        }
    }

    /// <summary>Read one variable by trace key, in canonical string form (ints invariant, bools lowercase).</summary>
    public string GetVariable(string key) => key switch
    {
        "vs" => Invariant(Vs),
        "vr" => Invariant(Vr),
        "va" => Invariant(Va),
        "rc" => Invariant(Rc),
        "modulo" => Invariant(Modulo),
        "k" => Invariant(K),
        "n1" => Invariant(N1),
        "n2" => Invariant(N2),
        "sreject_exception" => Invariant(SrejectException),
        "version_2_2" => Bool(Version22),
        "srej_enabled" => Bool(SrejEnabled),
        "half_duplex" => Bool(HalfDuplex),
        "layer_3_initiated" => Bool(LayerThreeInitiated),
        "own_receiver_busy" => Bool(OwnReceiverBusy),
        "peer_receiver_busy" => Bool(PeerReceiverBusy),
        "reject_exception" => Bool(RejectException),
        "ack_pending" => Bool(AcknowledgePending),
        _ => throw new InvalidOperationException($"unknown variable key `{key}` (see docs/golden-traces.md for the vocabulary)"),
    };

    public void SetTimer(string timer, TimerStatus status)
    {
        switch (timer)
        {
            case "t1": T1 = status; break;
            case "t3": T3 = status; break;
            default: throw new InvalidOperationException($"unknown timer `{timer}` — the data_link tables model t1 and t3");
        }
    }

    public TimerStatus GetTimer(string timer) => timer switch
    {
        "t1" => T1,
        "t3" => T3,
        _ => throw new InvalidOperationException($"unknown timer `{timer}` — the data_link tables model t1 and t3"),
    };

    // ─── Dispatch ─────────────────────────────────────────────────────

    /// <summary>
    /// Inject one event: select the unique matching transition in the
    /// current state, execute its action chain, move to the next state.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No transition matches, more than one matches (a table-determinism
    /// violation), a guard consults an event field the step didn't supply,
    /// or an action verb has no defined semantics.
    /// </exception>
    public DispatchResult Dispatch(EventInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _event = input;
        _frameType = FrameTypeFromEvent(input.Event);
        _effects = new List<Effect>();
        _pendingP = 0;
        _pendingF = 0;
        _pendingNr = null;
        _pendingNs = null;
        _x = null;
        _queueInsertIndex = 0;

        if (input.Atoms is not null)
        {
            foreach (var key in input.Atoms.Keys)
            {
                if (!EnvironmentAtoms.Contains(key))
                    throw new InvalidOperationException(
                        $"atom `{key}` cannot be supplied by the environment — it is derived from machine/event state. " +
                        $"Environment atoms: {string.Join(", ", EnvironmentAtoms.Order(StringComparer.Ordinal))}");
            }
        }

        ApplyTimerExpiry(input.Event);
        _poppedData = PopQueueHead(input);

        var page = _tables.States[State];
        var candidates = page.Transitions.Where(t => string.Equals(t.On, input.Event, StringComparison.Ordinal)).ToList();
        if (candidates.Count == 0)
        {
            var known = page.Transitions.Select(t => t.On).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            throw new InvalidOperationException(
                $"state {State} has no transition for event `{input.Event}`. Events handled here: {string.Join(", ", known)}");
        }

        var matching = candidates.Where(t => t.Guard.All(EvaluateTerm)).ToList();
        if (matching.Count == 0)
            throw new InvalidOperationException(
                $"state {State}, event `{input.Event}`: no guard matched. Candidates: " +
                string.Join("; ", candidates.Select(t => $"{t.Id} [{t.GuardText}]")));
        if (matching.Count > 1)
            throw new InvalidOperationException(
                $"state {State}, event `{input.Event}`: {matching.Count} transitions match simultaneously — the table is non-deterministic here: " +
                string.Join("; ", matching.Select(t => $"{t.Id} [{t.GuardText}]")));

        var transition = matching[0];
        ExecuteActions(transition.Actions, transition.Loops, depth: 0);

        if (!_tables.States.ContainsKey(transition.Next))
            throw new InvalidOperationException($"transition {transition.Id} targets unknown state `{transition.Next}`");
        State = transition.Next;

        return new DispatchResult(transition.Id, State, _effects);
    }

    /// <summary>
    /// <c>I_frame_pops_off_queue</c> consumes the queue head. When the trace
    /// queued the payload in an earlier step the head is removed (and must
    /// match the step's <c>data</c> label if both are given); a pop against
    /// an empty queue is allowed so traces may model the queue abstractly.
    /// </summary>
    private string? PopQueueHead(EventInput input)
    {
        if (!string.Equals(input.Event, "I_frame_pops_off_queue", StringComparison.Ordinal) || _queue.Count == 0)
            return null;

        var head = _queue[0];
        _queue.RemoveAt(0);
        if (input.Data is not null && !string.Equals(input.Data, head, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"I_frame_pops_off_queue: the step says `{input.Data}` popped, but the queue head is `{head}`");
        return head;
    }

    private void ApplyTimerExpiry(string eventName)
    {
        switch (eventName)
        {
            case "T1_expiry":
                if (T1 != TimerStatus.Running)
                    throw new InvalidOperationException($"T1_expiry injected but T1 is {T1} — a stopped timer cannot expire");
                T1 = TimerStatus.Expired;
                break;
            case "T3_expiry":
                if (T3 != TimerStatus.Running)
                    throw new InvalidOperationException($"T3_expiry injected but T3 is {T3} — a stopped timer cannot expire");
                T3 = TimerStatus.Expired;
                break;
            default:
                break;
        }
    }

    // ─── Guard atoms ──────────────────────────────────────────────────

    private bool EvaluateTerm(GuardTermIR term)
    {
        var value = EvaluateAtom(term.Atom);
        return term.Negate ? !value : value;
    }

    private bool EvaluateAtom(string atom)
    {
        if (_event.Atoms is not null && _event.Atoms.TryGetValue(atom, out var supplied))
            return supplied;

        return atom switch
        {
            // Received-frame fields (P for commands, F for responses — one P/F bit on the wire).
            "F_eq_1" => RequirePf(atom),
            "P_eq_1" => RequirePf(atom),
            "P_or_F_eq_1" => RequirePf(atom),
            "command" => RequireCommand(atom),
            "response" => !RequireCommand(atom),
            "command_and_P_eq_1" => RequireCommand(atom) && RequirePf(atom),
            "response_and_F_eq_1" => !RequireCommand(atom) && RequirePf(atom),

            // Enquiry_Response's head decision: the F *parameter* the caller
            // passed (staged in the pending F register by Enquiry_Response_F_1 /
            // "Enquiry Response (F = 0)") AND the received frame's type.
            "F_eq_1_and_frame_eq_RR_or_frame_eq_RNR_or_frame_eq_I" =>
                _pendingF == 1 && _frameType is "RR" or "RNR" or "I",

            // Sequence-variable comparisons (all window maths mod Modulo).
            "nr_eq_va" => RequireNr(atom) == Va,
            "nr_eq_vs" => RequireNr(atom) == Vs,
            "ns_eq_vr" => RequireNs(atom) == Vr,
            "ns_gt_vr_plus_1" => Distance(Vr, RequireNs(atom)) > 1,
            "va_le_nr_le_vs" => Distance(Va, RequireNr(atom)) <= Distance(Va, Vs),
            "vs_eq_nr" => Vs == RequireNr(atom), // Resolver stale-read substitution: V(s) == the received N(r)
            "vs_eq_va" => Vs == Va,
            "vs_eq_va_plus_k" => Vs == (Va + K) % Modulo,
            "vs_eq_X" => Vs == (_x ?? throw new InvalidOperationException("vs_eq_X consulted but X was never assigned")),

            // Machine state.
            "RC_eq_0" => Rc == 0,
            "RC_eq_N2" => Rc == N2,
            "T1_expired" => T1 == TimerStatus.Expired,
            "T1_running" => T1 == TimerStatus.Running,
            "ack_pending" => AcknowledgePending,
            "layer_3_initiated" => LayerThreeInitiated,
            "mod_128" => Modulo == 128,
            "mod_8" => Modulo == 8,
            "own_receiver_busy" => OwnReceiverBusy,
            "peer_receiver_busy" => PeerReceiverBusy,
            "reject_exception" => RejectException,
            "sreject_exception_gt_0" => SrejectException > 0,
            "SREJ_enabled" => SrejEnabled,
            "version_2_2" => Version22,
            "out_of_sequence_frames_in_receive_buffer" => _savedIFrames.Count > 0,
            "vr_I_frame_stored" => _savedIFrames.ContainsKey(Vr),

            // Environment atoms have defaults when the step doesn't supply them.
            "able_to_establish" => true,
            "info_field_length_le_N1_and_content_is_octet_aligned" => true,

            "RC_eq_NM201" => throw new InvalidOperationException(
                "atom RC_eq_NM201 belongs to the management_data_link machine, which this interpreter does not model yet"),

            _ => throw new InvalidOperationException($"guard atom `{atom}` has no binding in the reference interpreter"),
        };
    }

    /// <summary>Forward distance from <paramref name="from"/> to <paramref name="to"/> around the sequence-number circle.</summary>
    private int Distance(int from, int to) => ((to - from) % Modulo + Modulo) % Modulo;

    private bool RequirePf(string atom) =>
        _event.Pf ?? throw new InvalidOperationException($"guard atom `{atom}` consulted, but the step does not set `pf` for event {_event.Event}");

    private bool RequireCommand(string atom) =>
        _event.Command
        ?? DefaultRole(_frameType)
        ?? throw new InvalidOperationException($"guard atom `{atom}` consulted, but the step does not set `command` for event {_event.Event}");

    private int RequireNr(string atom) =>
        _event.Nr ?? throw new InvalidOperationException($"guard atom or action `{atom}` needs N(r), but the step does not set `nr` for event {_event.Event}");

    private int RequireNs(string atom) =>
        _event.Ns ?? throw new InvalidOperationException($"guard atom or action `{atom}` needs N(s), but the step does not set `ns` for event {_event.Event}");

    /// <summary>Fixed command/response roles per AX.25 v2.2 (SABM/SABME/DISC are always commands; UA/DM/FRMR always responses; I frames are commands).</summary>
    private static bool? DefaultRole(string? frameType) => frameType switch
    {
        "SABM" or "SABME" or "DISC" or "I" => true,
        "UA" or "DM" or "FRMR" => false,
        _ => null,
    };

    private static string? FrameTypeFromEvent(string eventName) => eventName switch
    {
        "I_received" => "I",
        "RR_received" => "RR",
        "RNR_received" => "RNR",
        "REJ_received" => "REJ",
        "SREJ_received" => "SREJ",
        "UA_received" => "UA",
        "DM_received" => "DM",
        "DISC_received" => "DISC",
        "SABM_received" => "SABM",
        "SABME_received" => "SABME",
        "FRMR_received" => "FRMR",
        "UI_received" => "UI",
        "XID_response_received" => "XID",
        _ => null,
    };

    // ─── Action execution ─────────────────────────────────────────────

    private void ExecuteActions(IReadOnlyList<ActionRow> actions, IReadOnlyList<LoopRow> loops, int depth)
    {
        if (depth > MaxSubroutineDepth)
            throw new InvalidOperationException($"subroutine call depth exceeded {MaxSubroutineDepth} — recursive tables?");

        var i = 0;
        while (i < actions.Count)
        {
            var loop = loops.FirstOrDefault(l => l.Start == i && l.Length > 0);
            if (loop is not null)
            {
                RunLoop(actions, loop, depth);
                i = loop.Start + loop.Length;
                continue;
            }

            Apply(actions[i], depth);
            i++;
        }
    }

    private void RunLoop(IReadOnlyList<ActionRow> actions, LoopRow loop, int depth)
    {
        var iterations = 0;
        if (loop.TestAtEnd)
        {
            do
            {
                RunRange(actions, loop, depth, ref iterations);
            }
            while (EvaluateTerm(loop.Predicate));
        }
        else
        {
            while (EvaluateTerm(loop.Predicate))
            {
                RunRange(actions, loop, depth, ref iterations);
            }
        }
    }

    private void RunRange(IReadOnlyList<ActionRow> actions, LoopRow loop, int depth, ref int iterations)
    {
        if (++iterations > MaxLoopIterations)
            throw new InvalidOperationException($"loop over actions[{loop.Start}..{loop.Start + loop.Length - 1}] exceeded {MaxLoopIterations} iterations — diverging predicate `{loop.Predicate.Atom}`");
        for (var j = loop.Start; j < loop.Start + loop.Length; j++)
            Apply(actions[j], depth);
    }

    private void Apply(ActionRow action, int depth)
    {
        switch (action.Kind)
        {
            case "signal_upper":
                _effects.Add(new UpperEffect(action.Verb));
                return;
            case "signal_lower":
                ApplySignalLower(action.Verb);
                return;
            case "internal_out":
                ApplyInternalOut(action.Verb);
                return;
            case "subroutine":
                InvokeSubroutine(action.Verb, depth);
                return;
            case "processing":
                ApplyProcessing(action.Verb, depth);
                return;
            default:
                throw new InvalidOperationException($"unknown action kind `{action.Kind}` for verb `{action.Verb}`");
        }
    }

    // ─── signal_lower: frames + LM primitives ─────────────────────────

    private void ApplySignalLower(string verb)
    {
        switch (verb)
        {
            case "LM_seize_request":
            case "LM_release_request":
            case "LM_data_request":
                _effects.Add(new LmEffect(verb));
                return;

            case "UA": EmitU("UA", command: false, PendingFBit(), expedited: false); return;
            case "Expedited UA": EmitU("UA", command: false, PendingFBit(), expedited: true); return;
            case "DM": EmitU("DM", command: false, PendingFBit(), expedited: false); return;
            case "DM (F = 1)": EmitU("DM", command: false, pf: true, expedited: false); return;
            case "DM Response (F = 0)": EmitU("DM", command: false, pf: false, expedited: false); return;
            case "Expedited DM": EmitU("DM", command: false, PendingFBit(), expedited: true); return;
            case "SABM": EmitU("SABM", command: true, PendingPBit(), expedited: false); return;
            case "SABM (P == 1)": EmitU("SABM", command: true, pf: true, expedited: false); return;
            case "SABME": EmitU("SABME", command: true, PendingPBit(), expedited: false); return;
            case "SABME (P = 1)": EmitU("SABME", command: true, pf: true, expedited: false); return;
            case "DISC (P = 1)": EmitU("DISC", command: true, pf: true, expedited: false); return;
            case "UI Command": EmitU("UI", command: true, PendingPBit(), expedited: false); return;
            case "XID_command": EmitU("XID", command: true, PendingPBit(), expedited: false); return;

            case "I Command":
                _effects.Add(new FrameEffect("I", Command: true, Pf: PendingPBit(), Nr: RequirePendingNr(verb), Ns: RequirePendingNs(verb), Expedited: false));
                return;

            case "RR":
            case "RR Response": EmitS("RR", command: false, PendingFBit(), verb); return;
            case "RR Command": EmitS("RR", command: true, PendingPBit(), verb); return;
            case "RR Command (P = 0)": EmitS("RR", command: true, pf: false, verb); return;
            case "RNR":
            case "RNR Response": EmitS("RNR", command: false, PendingFBit(), verb); return;
            case "RNR Response (F = 0)": EmitS("RNR", command: false, pf: false, verb); return;
            case "RNR Command": EmitS("RNR", command: true, PendingPBit(), verb); return;
            case "REJ": EmitS("REJ", command: false, PendingFBit(), verb); return;
            case "SREJ": EmitS("SREJ", command: false, PendingFBit(), verb); return;

            default:
                throw new InvalidOperationException($"signal_lower verb `{verb}` has no frame semantics in the reference interpreter");
        }
    }

    private void EmitU(string frame, bool command, bool pf, bool expedited) =>
        _effects.Add(new FrameEffect(frame, command, pf, Nr: null, Ns: null, Expedited: expedited));

    private void EmitS(string frame, bool command, bool pf, string verb) =>
        _effects.Add(new FrameEffect(frame, command, pf, Nr: RequirePendingNr(verb), Ns: null, Expedited: false));

    private bool PendingPBit() => _pendingP == 1;
    private bool PendingFBit() => _pendingF == 1;

    private int RequirePendingNr(string verb) =>
        _pendingNr ?? throw new InvalidOperationException(
            $"`{verb}` emitted without a preceding N(r) staging assignment — the table omits `N(r) := …` on this path");

    private int RequirePendingNs(string verb) =>
        _pendingNs ?? throw new InvalidOperationException(
            $"`{verb}` emitted without a preceding N(s) staging assignment — the table omits `N(s) := …` on this path");

    // ─── internal_out ─────────────────────────────────────────────────

    private void ApplyInternalOut(string verb)
    {
        switch (verb)
        {
            case "Push on I Frame Queue":
            case "Push on I Frame Queue (note: word order?)":
            case "Push I Frame on I Queue":
            case "push_frame_on_queue":
            {
                var label = _event.Data ?? _poppedData ?? "frame";
                // Fresh data from layer 3 joins the FIFO tail (C4.3: "All
                // queues are first-in, first-out"); a frame pushed back
                // after popping (peer busy / window full) returns to the
                // head so ordering is preserved.
                if (string.Equals(_event.Event, "DL_DATA_request", StringComparison.Ordinal))
                    _queue.Add(label);
                else
                    QueueInsert(label);
                _effects.Add(new InternalEffect(verb, label));
                return;
            }
            case "Push Old I Frame onto Queue":
            {
                // Invoke_Retransmission loop body: the frame being re-queued is
                // the one numbered by the current (rewound) V(s).
                var label = $"I(ns={Invariant(Vs)})";
                QueueInsert(label);
                _effects.Add(new InternalEffect(verb, label));
                return;
            }
            case "Push Old I Frame N(r) on Queue":
            {
                var label = $"I(ns={Invariant(RequireNr(verb))})";
                QueueInsert(label);
                _effects.Add(new InternalEffect(verb, label));
                return;
            }
            case "MDL-NEGOTIATE Request":
                _effects.Add(new InternalEffect(verb, null));
                return;
            default:
                throw new InvalidOperationException($"internal_out verb `{verb}` has no semantics in the reference interpreter");
        }
    }

    /// <summary>
    /// Queue pushes within one dispatch land at the head of the FIFO but keep
    /// their own relative order, ahead of anything already queued — so a
    /// pushed-back popped frame pops next, and a retransmission batch pops in
    /// ascending N(s) order before any newer traffic.
    /// </summary>
    private void QueueInsert(string label) => _queue.Insert(_queueInsertIndex++, label);

    // ─── subroutines ──────────────────────────────────────────────────

    /// <summary>
    /// Call-site spelling → subroutine table name. The JSON tables carry the
    /// figures' verbatim call labels, which do not always match the
    /// subroutine page's declared name — this map is the interpreter's
    /// explicit statement of which body each call site binds to.
    /// </summary>
    private static readonly Dictionary<string, string> SubroutineBindings = new(StringComparer.Ordinal)
    {
        ["Check Need For Response"] = "Check_Need_for_Response",
        ["Clear Exception Conditions"] = "Clear_Exception_Conditions",
        ["Enquiry Response (F = 0)"] = "Enquiry_Response",
        ["Enquiry_Response_F_1"] = "Enquiry_Response",
        ["Invoke Retransmission"] = "Invoke_Retransmission",
        ["N(r) Error Recovery"] = "N_r_Error_Recovery",
        ["N(r) Recovery"] = "N_r_Error_Recovery",
        ["Select_T1_Value"] = "Select_T1",
        ["Transmit Enquery"] = "Transmit_Enquiry", // figc4.4 spelling
        ["Transmit Enquiry"] = "Transmit_Enquiry",
        ["UI Check"] = "UI_Check",
    };

    private void InvokeSubroutine(string verb, int depth)
    {
        // The Enquiry Response subroutine takes an F parameter; the call-site
        // verb carries it. Stage it in the pending F register (which the
        // subroutine's head decision and its RR/RNR emissions consume).
        if (string.Equals(verb, "Enquiry_Response_F_1", StringComparison.Ordinal)) _pendingF = 1;
        if (string.Equals(verb, "Enquiry Response (F = 0)", StringComparison.Ordinal)) _pendingF = 0;

        var name = SubroutineBindings.TryGetValue(verb, out var bound) ? bound : verb;
        if (!_tables.Subroutines.TryGetValue(name, out var sub))
            throw new InvalidOperationException(
                $"subroutine call `{verb}` does not bind to any subroutine table (tried `{name}`). " +
                $"Known: {string.Join(", ", _tables.Subroutines.Keys.Order(StringComparer.Ordinal))}");

        // Path guards are evaluated live (see class remarks).
        var matching = sub.Paths.Where(p => p.Guard.All(EvaluateTerm)).ToList();
        if (matching.Count == 0)
            throw new InvalidOperationException(
                $"subroutine {name}: no path guard matched. Paths: " +
                string.Join("; ", sub.Paths.Select(p => $"{p.Id} [{p.GuardText}]")));
        if (matching.Count > 1)
            throw new InvalidOperationException(
                $"subroutine {name}: {matching.Count} paths match simultaneously — the table is non-deterministic here: " +
                string.Join("; ", matching.Select(p => $"{p.Id} [{p.GuardText}]")));

        ExecuteActions(matching[0].Actions, matching[0].Loops, depth + 1);
    }

    // ─── processing ───────────────────────────────────────────────────

    private void ApplyProcessing(string verb, int depth)
    {
        switch (verb)
        {
            // Pending TX registers.
            case "F := 0": _pendingF = 0; return;
            case "F := 1": _pendingF = 1; return;
            case "F := P": _pendingF = RequirePf("F := P") ? 1 : 0; return;
            case "P := 0": _pendingP = 0; return;
            case "P := 1": _pendingP = 1; return;
            case "N(r) := V(r)": _pendingNr = Vr; return;
            case "N(r) := N(s)": _pendingNr = RequireNs(verb); return;
            case "N(s) := V(s)": _pendingNs = Vs; return;

            // Sequence variables.
            case "V(s) := 0": Vs = 0; return;
            case "V(s) := N(r)": Vs = RequireNr(verb); return;
            case "V(s) := V(s) + 1": Vs = (Vs + 1) % Modulo; return;
            case "V(r) := 0": Vr = 0; return;
            case "V(r) := V(r) + 1": Vr = (Vr + 1) % Modulo; return;
            case "V(r) := V(r) - 1": Vr = ((Vr - 1) % Modulo + Modulo) % Modulo; return;
            case "V(a) := 0": Va = 0; return;
            case "V(a) := N(r)": Va = RequireNr(verb); return;
            case "X := V(s)": _x = Vs; return;

            // Retry counter.
            case "RC := 0": Rc = 0; return;
            case "RC := 1": Rc = 1; return;
            case "RC := RC + 1": Rc++; return;

            // Timers (symbolic).
            case "Start T1": T1 = TimerStatus.Running; return;
            case "Stop T1": T1 = TimerStatus.Stopped; return;
            case "Start T3": T3 = TimerStatus.Running; return;
            case "Stop T3": T3 = TimerStatus.Stopped; return;

            // Flags.
            case "Set Layer 3 Initiated": LayerThreeInitiated = true; return;
            case "Clear Layer 3 Initiated": LayerThreeInitiated = false; return;
            case "set_peer_receiver_busy": PeerReceiverBusy = true; return;
            case "clear_peer_receiver_busy": PeerReceiverBusy = false; return;
            case "Set Own Receiver Busy": OwnReceiverBusy = true; return;
            case "Clear Own Receiver Busy": OwnReceiverBusy = false; return;
            case "Set Reject Exception": RejectException = true; return;
            case "Clear Reject Exception": RejectException = false; return;
            case "Clear Reject Condition": RejectException = false; return;
            case "Clear Sreject Condition": SrejectException = 0; return;
            case "Increment Sreject Exception": SrejectException++; return;
            case "Sreject := Sreject + 1": SrejectException++; return;
            case "Decrement Sreject Exception if > 0":
                if (SrejectException > 0) SrejectException--;
                return;
            case "set_acknowledge_pending": AcknowledgePending = true; return;
            case "Clear Acknowledge Pending": AcknowledgePending = false; return;

            // Version / parameter assignments.
            case "Set Version 2.2": Version22 = true; return;
            case "set_version_2_0": Version22 = false; return;
            case "Set Half Duplex": HalfDuplex = true; return;
            case "Set Selective Reject": SrejEnabled = true; return;
            case "Set Implicit Reject": SrejEnabled = false; return;
            case "Modulo := 8": Modulo = 8; return;
            case "Modulo := 128": Modulo = 128; return;
            case "k := 8": K = 8; return;
            case "k := 32": K = 32; return;
            case "N1 := 2048": N1 = 2048; return;
            case "N2 := 10": N2 = 10; return;

            // Timer-arithmetic registers: modelled symbolically (the tables
            // reference SRT / T1V / T2 only through these formulas; traces do
            // not assert on them).
            case "SRT := Initial Default": _symbolic["SRT"] = "Initial Default"; return;
            case "SRT := 7(SRT)/8 + (T1)/8 - (Remaining Time on T1 When Last Stopped)/8":
                _symbolic["SRT"] = "7(SRT)/8 + (T1)/8 - (Remaining Time on T1 When Last Stopped)/8"; return;
            case "T1V := 2 * SRT": _symbolic["T1V"] = "2 * SRT"; return;
            case "T2 := 3000": _symbolic["T2"] = "3000"; return;
            case "Next T1 := 2 * SRT": _symbolic["NextT1"] = "2 * SRT"; return;
            case "Next T1 := (RC*0.25)+SRT*2": _symbolic["NextT1"] = "(RC*0.25)+SRT*2"; return;

            // Queue + frame-store bookkeeping.
            case "discard_I_frame_queue":
            case "discard_frame_queue":
            case "Discard Queue":
            case "Discard I Queue Entries":
                _queue.Clear();
                return;
            case "Save Contents of I Frame":
            {
                var ns = RequireNs(verb);
                _savedIFrames[ns] = _event.Data ?? $"I(ns={Invariant(ns)})";
                return;
            }
            case "Retrieve Stored V(r) I Frame":
                if (!_savedIFrames.Remove(Vr))
                    throw new InvalidOperationException($"Retrieve Stored V(r) I Frame: no stored frame with ns={Invariant(Vr)}");
                return;
            case "Discard Contents of I Frame":
            case "Discard I Frame":
            case "Discard Primitive":
                return; // the frame/primitive is simply not acted on

            // Retransmission bookkeeping: the send-history rewind itself is
            // abstract — the queue effects carry the observable behaviour.
            case "Backtrack":
                return;

            // The figures draw "Clear Exception Conditions" as a plain
            // processing box on some pages and as a subroutine call on others;
            // both mean the figc4.7 subroutine body.
            case "Clear Exception Conditions":
                InvokeSubroutine("Clear Exception Conditions", depth);
                return;

            default:
                throw new InvalidOperationException($"processing verb `{verb}` has no semantics in the reference interpreter");
        }
    }

    // ─── helpers ──────────────────────────────────────────────────────

    private static string Invariant(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Bool(bool v) => v ? "true" : "false";

    private static int ParseInt(string key, string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"variable `{key}` expects an integer, got `{value}`");

    private static bool ParseBool(string key, string value) => value switch
    {
        "true" => true,
        "false" => false,
        _ => throw new InvalidOperationException($"variable `{key}` expects true/false, got `{value}`"),
    };
}
