using System.Globalization;
using System.Text;
using Packet.Sdl.Interpreter;

namespace Packet.Sdl.Explorer;

/// <summary>The kinds of BFS edge.</summary>
public enum MoveKind
{
    /// <summary>Deliver the head frame of the channel into the station.</summary>
    Deliver,
    /// <summary>Channel fault: drop the head frame of the channel into the station.</summary>
    Drop,
    /// <summary>Channel fault: deliver the head frame and leave a copy at the head.</summary>
    Duplicate,
    /// <summary>Channel fault: swap the first two frames of the channel into the station.</summary>
    Reorder,
    /// <summary>I_frame_pops_off_queue at the station.</summary>
    Pop,
    /// <summary>LM_SEIZE_confirm at the station (it owes one after LM_seize_request).</summary>
    SeizeConfirm,
    /// <summary>T1_expiry at the station (quiescent-timeout abstraction; see docs/explorer.md).</summary>
    T1Expiry,
    /// <summary>T3_expiry at the station (opt-in, <c>--t3</c>; same quiescent-timeout rule, only on a page with a T3 arm).</summary>
    T3Expiry,
    /// <summary>DL_FLOW_OFF_request from the station's layer 3 (scenario-enabled; see docs/explorer.md).</summary>
    FlowOff,
    /// <summary>DL_FLOW_ON_request from the station's layer 3, lifting an earlier FlowOff.</summary>
    FlowOn,
}

/// <summary>One BFS edge: a move applied to a station (for channel moves, the station the channel flows into).</summary>
public readonly record struct Move(MoveKind Kind, Station Station)
{
    public bool IsFault => Kind is MoveKind.Drop or MoveKind.Duplicate or MoveKind.Reorder;

    /// <summary>A timer expiry (T1 or T3): the edges the timer-free progress check ignores.</summary>
    public bool IsTimer => Kind is MoveKind.T1Expiry or MoveKind.T3Expiry;

    public override string ToString() => $"{Kind}@{Station}";
}

/// <summary>One line of a counterexample: what happened, which transition fired, what came out, and both stations afterwards.</summary>
public sealed record StepRecord(
    int Number,
    string Actor,
    string Action,
    string? Transition,
    IReadOnlyList<string> Effects,
    string SummaryA,
    string SummaryB)
{
    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append(Number.ToString(CultureInfo.InvariantCulture).PadLeft(3)).Append(". [").Append(Actor).Append("] ").Append(Action);
        if (Transition is not null)
        {
            sb.Append("\n       transition: ").Append(Transition);
            sb.Append("\n       effects: ").Append(Effects.Count == 0 ? "(none)" : string.Join("; ", Effects));
        }
        sb.Append("\n       A: ").Append(SummaryA);
        sb.Append("\n       B: ").Append(SummaryB);
        return sb.ToString();
    }
}

/// <summary>An invariant violation: which invariant, which station (when attributable), and a precise message.</summary>
public sealed record Violation(Invariants Kind, Station? Station, string Message);

/// <summary>The result of applying one move.</summary>
public sealed record StepOutcome(SystemState Next, StepRecord Record, Violation? Violation);

/// <summary>
/// The transition relation of the two-station model: seeds the system,
/// enumerates the enabled moves of a state, applies a move (dispatching
/// the corresponding event on a cloned machine, routing its effects) and
/// checks the per-step safety invariants. Pure with respect to the input
/// state: machines are cloned before they are dispatched.
/// </summary>
public sealed class Stepper
{
    private static readonly HashSet<string> KnownStates = new(StringComparer.Ordinal)
    {
        "Disconnected", "AwaitingConnection", "AwaitingV22Connection",
        "Connected", "AwaitingRelease", "TimerRecovery",
    };

    private readonly ExplorerOptions _o;
    private readonly TableSet _tables;
    private readonly Dictionary<string, HashSet<string>> _eventsByState = new(StringComparer.Ordinal);
    private readonly string[] _submittedA;
    private readonly string[] _submittedB;

    public Stepper(ExplorerOptions options, TableSet tables)
    {
        _o = options ?? throw new ArgumentNullException(nameof(options));
        _tables = tables ?? throw new ArgumentNullException(nameof(tables));
        if (_o.K is < 1 or > 7) throw new ArgumentException("k must be 1..7 at modulo 8", nameof(options));
        if (_o.Seed == SeedKind.Disconnected && _o.FramesBa > 0)
            throw new ArgumentException("the Disconnected seed supports A-to-B data only (B has no DL_DATA_request arm while Disconnected)", nameof(options));
        if (_o.Seed == SeedKind.AwaitingV22Connection && (_o.FramesAb > 0 || _o.FramesBa > 0))
            throw new ArgumentException("the AwaitingV22Connection seed is a connect-phase seed: no data (figc4.6 discards DL_DATA_request while layer 3 initiated)", nameof(options));
        if (_o.Peer != PeerKind.Tables && _o.Seed == SeedKind.Connected)
            throw new ArgumentException("the v2.0 stub peer is for the connect-phase seeds only (--seed disconnected or awaiting-v22)", nameof(options));
        if (_o.Peer != PeerKind.Tables && (_o.FramesAb > 0 || _o.FramesBa > 0))
            throw new ArgumentException("the v2.0 stub peer has no data phase: both frame counts must be 0 (the scenario ends at link establishment)", nameof(options));
        if (_o.FlowRounds < 0 || _o.FlowOffAfterDelivered < 0)
            throw new ArgumentException("flow-control rounds and the delivered-frames threshold must be non-negative", nameof(options));
        if (_o.BusyPolls < 0)
            throw new ArgumentException("the busy-polls bound must be non-negative", nameof(options));
        foreach (var (state, page) in tables.States)
            _eventsByState[state] = new HashSet<string>(page.Transitions.Select(t => t.On), StringComparer.Ordinal);
        _submittedA = Enumerable.Range(0, _o.FramesAb).Select(i => "a" + Inv(i)).ToArray();
        _submittedB = Enumerable.Range(0, _o.FramesBa).Select(i => "b" + Inv(i)).ToArray();
    }

    /// <summary>Payload labels A submits for B, in order.</summary>
    public IReadOnlyList<string> SubmittedA => _submittedA;

    /// <summary>Payload labels B submits for A, in order.</summary>
    public IReadOnlyList<string> SubmittedB => _submittedB;

    private string[] Submitted(Station s) => s == Station.A ? _submittedA : _submittedB;

    // ─── Seeding ──────────────────────────────────────────────────────

    /// <summary>
    /// Build the seeded state. All data is submitted up front (DL_DATA_request
    /// per frame) so the queues start full; the Disconnected seed additionally
    /// has A issue DL_CONNECT_request first (so its SABM/SABME is in flight and
    /// its data queues behind the connection attempt).
    /// </summary>
    public SystemState Seed()
    {
        var initial = _o.Seed == SeedKind.Connected ? "Connected" : "Disconnected";
        var stub = _o.Peer == PeerKind.Tables ? null : new V20Peer(_o.Peer, Connected: false);
        // With a stub peer the table-driven B is a placeholder that is never dispatched.
        var state = new SystemState { A = NewMachine(Station.A, initial), B = NewMachine(Station.B, initial), StubPeer = stub };

        if (_o.Seed == SeedKind.Disconnected)
            state = Require(Inject(state, Station.A, new EventInput("DL_CONNECT_request"), "DL_CONNECT_request (seed)", state.ToA, state.Budget, clearOwed: false, number: 0));

        if (_o.Seed == SeedKind.AwaitingV22Connection)
        {
            // The golden trace's initial state (frmr-fallback-downgrades-to-sabm)
            // plus the SABME that put A there, still on the air.
            var a = new DataLinkMachine(_tables, "AwaitingV22Connection");
            a.SetVariable("modulo", "128");
            a.SetVariable("k", Inv(_o.K));
            a.SetVariable("n2", Inv(_o.N2));
            a.SetVariable("version_2_2", "true");
            a.SetVariable("srej_enabled", _o.Srej ? "true" : "false");
            a.SetVariable("rc", "1");
            a.SetVariable("layer_3_initiated", "true");
            a.SetTimer("t1", TimerStatus.Running);
            state = state with { A = a, ToB = new[] { new Frame("SABME", Command: true, Pf: true, Nr: null, Ns: null, Data: null) } };
        }

        foreach (var label in _submittedA)
            state = Require(Inject(state, Station.A, new EventInput("DL_DATA_request", Data: label), "DL_DATA_request (seed)", state.ToA, state.Budget, clearOwed: false, number: 0));
        foreach (var label in _submittedB)
            state = Require(Inject(state, Station.B, new EventInput("DL_DATA_request", Data: label), "DL_DATA_request (seed)", state.ToB, state.Budget, clearOwed: false, number: 0));

        return state with { Budget = _o.Budget, AckedA = 0, AckedB = 0 };

        static SystemState Require(StepOutcome outcome) =>
            outcome.Violation is null
                ? outcome.Next
                : throw new InvalidOperationException($"seeding failed: {outcome.Violation.Message}");
    }

    private DataLinkMachine NewMachine(Station station, string initialState)
    {
        var m = new DataLinkMachine(_tables, initialState);
        m.SetVariable("modulo", _o.Modulo128A && station == Station.A ? "128" : "8");
        m.SetVariable("k", Inv(_o.K));
        m.SetVariable("n2", Inv(_o.N2));
        // A link actually using SREJ has negotiated v2.2 (§4.3.2.4, §6.3.2).
        m.SetVariable("srej_enabled", _o.Srej ? "true" : "false");
        m.SetVariable("version_2_2", _o.Srej ? "true" : "false");
        if (initialState == "Connected")
            m.SetTimer("t3", TimerStatus.Running); // idle Connected: T3 runs while T1 does not (§4.4.5.2)
        return m;
    }

    // ─── Quiescence ───────────────────────────────────────────────────

    /// <summary>
    /// Both Connected, nothing in flight or queued, V(s) = V(a) on both, all
    /// submitted data delivered, no owed internal events.
    /// </summary>
    public bool IsQuiescent(SystemState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        // With the v2.0 stub as B, "B at rest" is simply "the stub is
        // connected" (link established with modulo 8; it has no queue,
        // variables or timers).
        var bAtRest = s.StubPeer is not null
            ? s.StubPeer.Connected
            : QuiescentState(s.B.State) && s.B.QueueEntries.Count == 0 && s.B.Vs == s.B.Va && !s.SeizeOwedB;
        return QuiescentState(s.A.State) && bAtRest
            && s.ToA.Count == 0 && s.ToB.Count == 0
            && s.A.QueueEntries.Count == 0
            && s.A.Vs == s.A.Va
            && s.DeliveredAtB == _submittedA.Length && s.DeliveredAtA == _submittedB.Length
            && !s.SeizeOwedA;
    }

    /// <summary>One-line summary of a station for the counterexample log (the stub's own summary when it plays B).</summary>
    private static string SummaryOf(SystemState s, Station station) =>
        station == Station.B && s.StubPeer is not null ? s.StubPeer.Summary() : SystemState.Summary(s.Machine(station));

    private bool FlowControls(Station station) => _o.FlowControl switch
    {
        FlowControlAt.Both => true,
        FlowControlAt.A => station == Station.A,
        FlowControlAt.B => station == Station.B,
        _ => false,
    };

    private bool QuiescentState(string state) =>
        state == "Connected" || (_o.TimerRecoveryIsQuiescent && state == "TimerRecovery");

    // ─── Enabled moves ────────────────────────────────────────────────

    public IReadOnlyList<Move> EnabledMoves(SystemState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var moves = new List<Move>();
        var popEnabled = new bool[2];

        foreach (var station in new[] { Station.A, Station.B })
        {
            var incoming = s.Incoming(station);
            if (incoming.Count > 0)
            {
                moves.Add(new Move(MoveKind.Deliver, station));
                if (s.Budget > 0)
                {
                    if (_o.Faults.HasFlag(FaultKinds.Drop) && DropAllowed(incoming[0], station))
                        moves.Add(new Move(MoveKind.Drop, station));
                    if (_o.Faults.HasFlag(FaultKinds.Duplicate))
                        moves.Add(new Move(MoveKind.Duplicate, station));
                    if (_o.Faults.HasFlag(FaultKinds.Reorder) && incoming.Count >= 2)
                        moves.Add(new Move(MoveKind.Reorder, station));
                }
            }

            // The stub peer has no queue, timers or link multiplexer, and
            // layer 3 never flow-controls it: channel moves only.
            if (station == Station.B && s.StubPeer is not null) continue;

            if (PopChangesState(s, station))
            {
                popEnabled[(int)station] = true;
                moves.Add(new Move(MoveKind.Pop, station));
            }

            if (s.SeizeOwed(station) && SeizeConfirmEvent(s.Machine(station)) is not null)
                moves.Add(new Move(MoveKind.SeizeConfirm, station));

            // Layer-3 flow control, only where the page has a direct arm
            // (Connected, TimerRecovery): FLOW_OFF once the station has
            // delivered enough frames upward and a round is left, FLOW_ON
            // whenever flow is off.
            if (FlowControls(station))
            {
                if (s.FlowOff(station))
                {
                    if (Handles(s.Machine(station), "DL_FLOW_ON_request")) moves.Add(new Move(MoveKind.FlowOn, station));
                }
                else if (s.FlowRounds(station) < _o.FlowRounds && s.DeliveredAt(station) >= _o.FlowOffAfterDelivered
                         && Handles(s.Machine(station), "DL_FLOW_OFF_request"))
                {
                    moves.Add(new Move(MoveKind.FlowOff, station));
                }
            }
        }

        // Quiescent-timeout abstraction: a timer fires only once everything in
        // flight has been delivered or lost and no internal work is pending.
        var quiet = s.ToA.Count == 0 && s.ToB.Count == 0 && !s.SeizeOwedA && !s.SeizeOwedB && !popEnabled[0] && !popEnabled[1];
        if (quiet)
        {
            foreach (var station in new[] { Station.A, Station.B })
            {
                var m = s.Machine(station);
                // --busy-polls: while the peer's layer 3 has flow off, only so
                // many T1 polls may go into the busy peer before layer 3 must
                // turn flow back on (a bound on the busy period, not a fault).
                var busyBound = s.FlowOff(SystemState.Peer(station)) && s.PollsIntoBusy(station) >= _o.BusyPolls;
                if (m.T1 == TimerStatus.Running && Handles(m, "T1_expiry") && !busyBound)
                    moves.Add(new Move(MoveKind.T1Expiry, station));
                if (_o.T3 && m.T3 == TimerStatus.Running && Handles(m, "T3_expiry"))
                    moves.Add(new Move(MoveKind.T3Expiry, station));
            }
        }

        return moves;
    }

    private bool DropAllowed(Frame head, Station into)
    {
        if (_o.DropScope == DropScope.Any) return true;
        if (head.Type != "I" || head.Data is null) return false;
        var submitted = Submitted(SystemState.Peer(into));
        return submitted.Length > 0 && !string.Equals(head.Data, submitted[^1], StringComparison.Ordinal);
    }

    private bool Handles(DataLinkMachine m, string eventName) =>
        _eventsByState.TryGetValue(m.State, out var events) && events.Contains(eventName);

    /// <summary>
    /// A pop is enabled only when it changes something: a window-full or
    /// peer-busy pop pushes the frame straight back (a self-loop), which would
    /// otherwise keep T1 disabled forever under the quiescent-timeout rule.
    /// </summary>
    private bool PopChangesState(SystemState s, Station station)
    {
        var m = s.Machine(station);
        if (m.QueueEntries.Count == 0 || !Handles(m, "I_frame_pops_off_queue")) return false;
        var probe = m.Clone();
        var before = SystemState.MachineFingerprint(probe);
        try
        {
            var result = probe.Dispatch(new EventInput("I_frame_pops_off_queue"));
            if (result.Effects.Any(e => e is FrameEffect)) return true;
        }
        catch (InvalidOperationException)
        {
            return true; // let Apply surface the interpreter error as a MachineError
        }
        return !string.Equals(before, SystemState.MachineFingerprint(probe), StringComparison.Ordinal);
    }

    // ─── Event routing ────────────────────────────────────────────────

    /// <summary>
    /// The event a received frame raises in the station's current state:
    /// the frame's own <c>*_received</c> arm when the page has one, else
    /// the page's catch-all arm (<c>all_other_commands</c>,
    /// <c>i_or_s_command_received</c>, <c>all_other_primitives__from_lower_layer</c>).
    /// Null when the page has no arm at all.
    /// </summary>
    private string? FrameEvent(DataLinkMachine m, Frame frame)
    {
        if (!_eventsByState.TryGetValue(m.State, out var events)) return null;
        var direct = frame.Type + "_received";
        if (events.Contains(direct)) return direct;
        if (frame.Command && events.Contains("all_other_commands")) return "all_other_commands";
        if (frame.Command && frame.IsIOrSupervisory && events.Contains("i_or_s_command_received")) return "i_or_s_command_received";
        if (events.Contains("all_other_primitives__from_lower_layer")) return "all_other_primitives__from_lower_layer";
        return null;
    }

    private string? SeizeConfirmEvent(DataLinkMachine m)
    {
        if (!_eventsByState.TryGetValue(m.State, out var events)) return null;
        if (events.Contains("LM_SEIZE_confirm")) return "LM_SEIZE_confirm";
        if (events.Contains("all_other_primitives__from_lower_layer")) return "all_other_primitives__from_lower_layer";
        return null;
    }

    // ─── Applying a move ──────────────────────────────────────────────

    public StepOutcome Apply(SystemState s, Move move, int number)
    {
        ArgumentNullException.ThrowIfNull(s);
        var station = move.Station;
        var peer = SystemState.Peer(station);
        var incoming = s.Incoming(station);

        switch (move.Kind)
        {
            case MoveKind.Deliver:
            {
                var frame = incoming[0];
                if (station == Station.B && s.StubPeer is not null)
                    return InjectStub(s, frame, $"receives {frame.Render()} from {peer}", incoming.Skip(1).ToList(), s.Budget, number);
                return Inject(s, station, FrameInput(s.Machine(station), frame, station),
                    $"receives {frame.Render()} from {peer}", incoming.Skip(1).ToList(), s.Budget, clearOwed: false, number);
            }
            case MoveKind.Duplicate:
            {
                var frame = incoming[0];
                var action = $"receives {frame.Render()} from {peer} (channel fault: DUPLICATE, a copy stays at the head; budget {Inv(s.Budget)} -> {Inv(s.Budget - 1)})";
                if (station == Station.B && s.StubPeer is not null)
                    return InjectStub(s, frame, action, incoming, s.Budget - 1, number);
                return Inject(s, station, FrameInput(s.Machine(station), frame, station), action, incoming, s.Budget - 1, clearOwed: false, number);
            }
            case MoveKind.FlowOff:
                return Inject(s, station, new EventInput("DL_FLOW_OFF_request"), "DL_FLOW_OFF_request (layer 3 turns flow off)", incoming, s.Budget, clearOwed: false, number);
            case MoveKind.FlowOn:
                return Inject(s, station, new EventInput("DL_FLOW_ON_request"), "DL_FLOW_ON_request (layer 3 turns flow back on)", incoming, s.Budget, clearOwed: false, number);
            case MoveKind.Drop:
            {
                var frame = incoming[0];
                var next = WithIncoming(s, station, incoming.Skip(1).ToList()) with { Budget = s.Budget - 1 };
                return new StepOutcome(next,
                    new StepRecord(number, $"channel {peer}->{station}", $"DROPS {frame.Render()} (budget {Inv(s.Budget)} -> {Inv(s.Budget - 1)})",
                        null, Array.Empty<string>(), SummaryOf(next, Station.A), SummaryOf(next, Station.B)),
                    null);
            }
            case MoveKind.Reorder:
            {
                var swapped = new List<Frame>(incoming);
                (swapped[0], swapped[1]) = (swapped[1], swapped[0]);
                var next = WithIncoming(s, station, swapped) with { Budget = s.Budget - 1 };
                return new StepOutcome(next,
                    new StepRecord(number, $"channel {peer}->{station}", $"REORDERS {incoming[0].Render()} behind {incoming[1].Render()} (budget {Inv(s.Budget)} -> {Inv(s.Budget - 1)})",
                        null, Array.Empty<string>(), SummaryOf(next, Station.A), SummaryOf(next, Station.B)),
                    null);
            }
            case MoveKind.Pop:
                return Inject(s, station, new EventInput("I_frame_pops_off_queue"), "I frame pops off queue", incoming, s.Budget, clearOwed: false, number);
            case MoveKind.SeizeConfirm:
            {
                var ev = SeizeConfirmEvent(s.Machine(station)) ?? "LM_SEIZE_confirm";
                return Inject(s, station, new EventInput(ev), "LM_SEIZE_confirm (link multiplexer grants the channel)", incoming, s.Budget, clearOwed: true, number);
            }
            case MoveKind.T1Expiry:
                return Inject(s, station, new EventInput("T1_expiry"), "T1 expiry (quiescent timeout: nothing in flight)", incoming, s.Budget, clearOwed: false, number);
            case MoveKind.T3Expiry:
                return Inject(s, station, new EventInput("T3_expiry"), "T3 expiry (quiescent timeout: nothing in flight)", incoming, s.Budget, clearOwed: false, number);
            default:
                throw new ArgumentOutOfRangeException(nameof(move));
        }
    }

    private EventInput? FrameInput(DataLinkMachine m, Frame frame, Station into)
    {
        var ev = FrameEvent(m, frame);
        if (ev is null) return null;
        var atoms = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["able_to_establish"] = !(_o.PeerDeclinesSabme && into == Station.B && frame.Type == "SABME"),
            ["info_field_length_le_N1_and_content_is_octet_aligned"] = true,
        };
        return new EventInput(ev, Pf: frame.Pf, Command: frame.Command, Nr: frame.Nr, Ns: frame.Ns, Data: frame.Data, Atoms: atoms);
    }

    private static SystemState WithIncoming(SystemState s, Station station, IReadOnlyList<Frame> incoming) =>
        station == Station.A ? s with { ToA = incoming } : s with { ToB = incoming };

    /// <summary>
    /// Deliver a frame to the v2.0 stub playing station B: its responses
    /// join the B-to-A channel and the rule that fired is the transition.
    /// The stub is the environment, so no invariant is checked here; a
    /// frame outside its rule set is a MachineError so the gap is visible.
    /// </summary>
    private static StepOutcome InjectStub(SystemState s, Frame frame, string action, IReadOnlyList<Frame> newIncoming, int newBudget, int number)
    {
        var stub = s.StubPeer!;
        Violation? violation = null;
        string? rule = null;
        var outgoing = new List<Frame>(s.ToA);
        var effectsText = new List<string>();
        try
        {
            var (nextStub, responses, firedRule) = stub.Receive(frame);
            rule = firedRule;
            foreach (var response in responses)
            {
                outgoing.Add(response);
                effectsText.Add("frame " + response.Render());
            }
            stub = nextStub;
        }
        catch (InvalidOperationException ex)
        {
            violation = new Violation(Invariants.MachineError, Station.B, $"v2.0 stub peer at station B: {ex.Message}");
        }

        var next = s with { StubPeer = stub, ToB = newIncoming, ToA = outgoing, Budget = newBudget };
        var record = new StepRecord(number, "B", action, rule, effectsText, SummaryOf(next, Station.A), SummaryOf(next, Station.B));
        return new StepOutcome(next, record, violation);
    }

    /// <summary>
    /// Dispatch <paramref name="input"/> on a clone of the station's machine,
    /// route the effects (frames to the outgoing channel, DL_DATA_indication
    /// to the delivery bookkeeping, LM_seize_request to the owed flag) and run
    /// the per-step invariants. A null input means the page had no arm for
    /// the frame (reported as a MachineError).
    /// </summary>
    private StepOutcome Inject(SystemState s, Station station, EventInput? input, string action,
        IReadOnlyList<Frame> newIncoming, int newBudget, bool clearOwed, int number)
    {
        var peer = SystemState.Peer(station);
        var pre = s.Machine(station);
        var m = pre.Clone();
        Violation? violation = null;
        DispatchResult? result = null;

        if (input is null)
        {
            violation = new Violation(Invariants.MachineError, station,
                $"station {station} in state {m.State} has no arm (direct or catch-all) for the received frame");
        }
        else
        {
            if (violation is null && On(Invariants.RejectCoherence) && input.Event is "REJ_received" or "SREJ_received" && IsDataState(m.State))
                violation = ReceiverRejectCheck(station, m, input);

            if (violation is null)
            {
                try
                {
                    result = m.Dispatch(input);
                }
                catch (InvalidOperationException ex)
                {
                    violation = new Violation(Invariants.MachineError, station, $"interpreter error at station {station}: {ex.Message}");
                }
            }
        }

        var outgoing = new List<Frame>(s.Outgoing(station));
        var owed = s.SeizeOwed(station) && !clearOwed;
        var delivered = s.DeliveredAt(station);
        var acked = s.Acked(station);
        var effectsText = new List<string>();

        if (result is not null)
        {
            foreach (var effect in result.Effects)
            {
                effectsText.Add(effect.Render());
                switch (effect)
                {
                    case FrameEffect fe:
                    {
                        var frame = Frame.FromEffect(fe);
                        outgoing.Add(frame);
                        if (violation is null && frame.IsReject && On(Invariants.RejectCoherence))
                            violation = EmitterRejectCheck(station, m, frame);
                        if (violation is null && On(Invariants.BusyRnr))
                            violation = BusyAnnouncementCheck(station, pre, m, frame);
                        if (violation is null && On(Invariants.PeerBusyHolds) && frame.Type == "I" && pre.PeerReceiverBusy && m.PeerReceiverBusy)
                        {
                            violation = new Violation(Invariants.PeerBusyHolds, station,
                                $"station {station} sent {frame.Render()} while its peer-receiver-busy condition is set (before and after this step): a station that has received RNR stops transmitting I frames until the busy condition is cleared (§6.4.9); only polls and responses may go out");
                        }
                        break;
                    }
                    case UpperEffect { Primitive: "DL_DATA_indication" } ue:
                    {
                        if (violation is null && On(Invariants.FlowOffDelivery) && s.FlowOff(station))
                        {
                            violation = new Violation(Invariants.FlowOffDelivery, station,
                                $"station {station} delivered `{ue.Detail}` upward while its layer 3 has flow off (DL_FLOW_OFF_request issued and not yet lifted): the busy condition did not hold the data back");
                        }
                        var submitted = Submitted(peer);
                        var expected = delivered < submitted.Length ? submitted[delivered] : null;
                        if (expected is not null && string.Equals(ue.Detail, expected, StringComparison.Ordinal))
                        {
                            delivered++;
                        }
                        else if (violation is null && On(Invariants.Delivery))
                        {
                            violation = new Violation(Invariants.Delivery, station, expected is null
                                ? $"station {station} delivered `{ue.Detail}` upward but {peer} submitted only {Inv(submitted.Length)} frame(s), all already delivered: duplicate or spurious delivery"
                                : $"station {station} delivered `{ue.Detail}` upward where {peer}'s submission #{Inv(delivered)} is `{expected}`: out-of-order, duplicate or gapped delivery");
                        }
                        break;
                    }
                    case LmEffect { Primitive: "LM_seize_request" }:
                        owed = true;
                        break;
                    default:
                        break;
                }
            }

            // Absolute acknowledgement counter: V(a) moves forward by less than
            // the modulus per step, so the modular distance is exact. A link
            // (re)establishment zeroes the variables without acknowledging
            // anything, so it is excluded.
            var reset = input!.Event is "SABM_received" or "SABME_received" or "UA_received" && m.Vs == 0 && m.Va == 0 && m.Vr == 0;
            if (!reset) acked += Distance(pre.Va, m.Va, m.Modulo);
        }

        // Layer-3 flow control bookkeeping and the busy-condition check
        // (§6.4.10: entering the busy condition is what DL-FLOW-OFF is for,
        // and the figure's own DL-FLOW-ON arm only acts when busy is set).
        var flowOff = s.FlowOff(station);
        var flowRounds = s.FlowRounds(station);
        var pollsIntoBusy = s.PollsIntoBusy(station);
        var peerPollsIntoBusy = s.PollsIntoBusy(peer);
        if (result is not null && input!.Event == "DL_FLOW_OFF_request")
        {
            flowOff = true;
            if (violation is null && On(Invariants.BusyTracksFlow) && !m.OwnReceiverBusy)
                violation = new Violation(Invariants.BusyTracksFlow, station,
                    $"station {station} took {result.TransitionId} on DL_FLOW_OFF_request but its own-receiver-busy condition is still clear: layer 3 cannot enter the busy condition (§6.4.10) from not-busy");
        }
        else if (result is not null && input!.Event == "DL_FLOW_ON_request")
        {
            flowOff = false;
            flowRounds++;
            peerPollsIntoBusy = 0; // the busy period is over: the peer's poll budget starts afresh
            if (violation is null && On(Invariants.BusyTracksFlow) && m.OwnReceiverBusy)
                violation = new Violation(Invariants.BusyTracksFlow, station,
                    $"station {station} took {result.TransitionId} on DL_FLOW_ON_request but its own-receiver-busy condition is still set");
        }
        else if (result is not null && input!.Event == "T1_expiry" && s.FlowOff(peer))
        {
            pollsIntoBusy++;
        }

        if (violation is null && On(Invariants.DefinedState) && !KnownStates.Contains(m.State))
            violation = new Violation(Invariants.DefinedState, station, $"station {station} is in undefined state `{m.State}`");

        if (violation is null && On(Invariants.SequenceSanity))
            violation = SequenceSanityCheck(station, m);

        if (violation is null && On(Invariants.AckCoherence) && acked > s.DeliveredAt(peer))
        {
            violation = new Violation(Invariants.AckCoherence, station,
                $"station {station} has seen {Inv(acked)} of its frames acknowledged (V(a)={Inv(m.Va)}) but {peer} has delivered only {Inv(s.DeliveredAt(peer))} of them upward");
        }

        var next = s with
        {
            A = station == Station.A ? m : s.A,
            B = station == Station.B ? m : s.B,
            ToA = station == Station.A ? newIncoming : outgoing,
            ToB = station == Station.B ? newIncoming : outgoing,
            SeizeOwedA = station == Station.A ? owed : s.SeizeOwedA,
            SeizeOwedB = station == Station.B ? owed : s.SeizeOwedB,
            Budget = newBudget,
            DeliveredAtA = station == Station.A ? delivered : s.DeliveredAtA,
            DeliveredAtB = station == Station.B ? delivered : s.DeliveredAtB,
            AckedA = station == Station.A ? acked : s.AckedA,
            AckedB = station == Station.B ? acked : s.AckedB,
            FlowOffA = station == Station.A ? flowOff : s.FlowOffA,
            FlowOffB = station == Station.B ? flowOff : s.FlowOffB,
            FlowRoundsA = station == Station.A ? flowRounds : s.FlowRoundsA,
            FlowRoundsB = station == Station.B ? flowRounds : s.FlowRoundsB,
            PollsIntoBusyA = station == Station.A ? pollsIntoBusy : peerPollsIntoBusy,
            PollsIntoBusyB = station == Station.B ? pollsIntoBusy : peerPollsIntoBusy,
        };

        var record = new StepRecord(number, station.ToString(), action, result?.TransitionId, effectsText,
            SummaryOf(next, Station.A), SummaryOf(next, Station.B));
        return new StepOutcome(next, record, violation);
    }

    // ─── Invariants ───────────────────────────────────────────────────

    private bool On(Invariants inv) => _o.Invariants.HasFlag(inv);

    private static bool IsDataState(string state) => state is "Connected" or "TimerRecovery";

    private static Violation? SequenceSanityCheck(Station station, DataLinkMachine m)
    {
        var n = m.Modulo;
        if (m.Vs < 0 || m.Vs >= n) return new Violation(Invariants.SequenceSanity, station, $"station {station}: V(s)={Inv(m.Vs)} out of range [0,{Inv(n)})");
        if (m.Va < 0 || m.Va >= n) return new Violation(Invariants.SequenceSanity, station, $"station {station}: V(a)={Inv(m.Va)} out of range [0,{Inv(n)})");
        if (m.Vr < 0 || m.Vr >= n) return new Violation(Invariants.SequenceSanity, station, $"station {station}: V(r)={Inv(m.Vr)} out of range [0,{Inv(n)})");
        var outstanding = Distance(m.Va, m.Vs, n);
        if (outstanding > m.K)
            return new Violation(Invariants.SequenceSanity, station,
                $"station {station}: window exceeded, V(s)={Inv(m.Vs)} V(a)={Inv(m.Va)} gives {Inv(outstanding)} outstanding > k={Inv(m.K)} (state {m.State})");
        // Each outstanding SREJ condition names a distinct missing frame inside
        // the receive window, so a coherent receiver never has more than k-1 of
        // them; a count above k means the station keeps raising selective
        // rejects for frames it does not need (the #42 runaway).
        if (m.SrejectException > m.K)
            return new Violation(Invariants.SequenceSanity, station,
                $"station {station}: SREJ exception count {Inv(m.SrejectException)} exceeds the window k={Inv(m.K)}: more selective rejects outstanding than there can be gaps (state {m.State})");
        return null;
    }

    /// <summary>
    /// The busy announcement: an RNR carries N(r) = V(r) (it acknowledges up
    /// to N(r)-1, §4.3.2.2, and the busy arms are the one place the figures
    /// draw no staging box, hypothesis H3), and a station whose own receiver
    /// is busy before and after the step sends no RR, REJ or SREJ, because any
    /// of those clears the peer's busy condition (§4.3.2.2) and invites I
    /// frames the busy station will discard; §6.4.10 says the indication is
    /// RNR, and RNR F=1 to a poll.
    /// </summary>
    private static Violation? BusyAnnouncementCheck(Station station, DataLinkMachine pre, DataLinkMachine m, Frame frame)
    {
        if (frame.Type == "RNR" && frame.Nr != m.Vr)
        {
            return new Violation(Invariants.BusyRnr, station,
                $"station {station} sent {frame.Render()} but its V(r) is {Inv(m.Vr)}: an RNR acknowledges everything below N(r) (§4.3.2.2), so a busy station's RNR must carry N(r) = V(r)");
        }
        if (frame.Type is "RR" or "REJ" or "SREJ" && pre.OwnReceiverBusy && m.OwnReceiverBusy)
        {
            return new Violation(Invariants.BusyRnr, station,
                $"station {station} sent {frame.Render()} while its own receiver is busy: a busy station indicates the condition with RNR (§6.4.10), and this frame instead clears the peer's busy condition (§4.3.2.2), inviting I frames the station will discard");
        }
        return null;
    }

    /// <summary>Emitter side: a REJ/SREJ with N(r)=x must name V(r) or a not-yet-held frame inside the receive window.</summary>
    private static Violation? EmitterRejectCheck(Station station, DataLinkMachine m, Frame frame)
    {
        var x = frame.Nr ?? -1;
        if (x == m.Vr) return null;
        var d = Distance(m.Vr, x, m.Modulo);
        var held = m.SavedFrames.ContainsKey(x);
        if (d > 0 && d < m.K && !held) return null;
        var why = held
            ? $"it already holds I(ns={Inv(x)}) in its receive buffer {{{string.Join(",", m.SavedFrames.Keys.Order().Select(Inv))}}}"
            : $"N(r)={Inv(x)} is outside its receive window [V(r)={Inv(m.Vr)}, V(r)+k={Inv((m.Vr + m.K) % m.Modulo)})";
        return new Violation(Invariants.RejectCoherence, station,
            $"reject coherence (emitter): station {station} sent {frame.Render()} with V(r)={Inv(m.Vr)}, but {why}: it asks for a frame it does not need");
    }

    /// <summary>Receiver side: the named frame must be inside the receiver's outstanding set [V(a), V(s)).</summary>
    private Violation? ReceiverRejectCheck(Station station, DataLinkMachine m, EventInput input)
    {
        var x = input.Nr ?? -1;
        var outstanding = Distance(m.Va, m.Vs, m.Modulo);
        if (Distance(m.Va, x, m.Modulo) < outstanding) return null;
        if (_o.RejMayEqualVs && input.Event == "REJ_received" && x == m.Vs) return null;
        var kind = input.Event == "REJ_received" ? "REJ" : "SREJ";
        var set = outstanding == 0
            ? "{}"
            : "{" + string.Join(",", Enumerable.Range(0, outstanding).Select(i => Inv((m.Va + i) % m.Modulo))) + "}";
        return new Violation(Invariants.RejectCoherence, station,
            $"reject coherence (receiver): station {station} received {kind} nr={Inv(x)} but its outstanding frames are [V(a)={Inv(m.Va)}, V(s)={Inv(m.Vs)}) = {set}: the peer asked for a frame {station} never sent, or one already acknowledged");
    }

    private static int Distance(int from, int to, int modulo) => ((to - from) % modulo + modulo) % modulo;

    private static string Inv(int v) => v.ToString(CultureInfo.InvariantCulture);
}
