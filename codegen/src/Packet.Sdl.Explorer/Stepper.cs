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
}

/// <summary>One BFS edge: a move applied to a station (for channel moves, the station the channel flows into).</summary>
public readonly record struct Move(MoveKind Kind, Station Station)
{
    public bool IsFault => Kind is MoveKind.Drop or MoveKind.Duplicate or MoveKind.Reorder;

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
        var state = new SystemState { A = NewMachine(Station.A, initial), B = NewMachine(Station.B, initial) };

        if (_o.Seed == SeedKind.Disconnected)
            state = Require(Inject(state, Station.A, new EventInput("DL_CONNECT_request"), "DL_CONNECT_request (seed)", state.ToA, state.Budget, clearOwed: false, number: 0));

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
        return QuiescentState(s.A.State) && QuiescentState(s.B.State)
            && s.ToA.Count == 0 && s.ToB.Count == 0
            && s.A.QueueEntries.Count == 0 && s.B.QueueEntries.Count == 0
            && s.A.Vs == s.A.Va && s.B.Vs == s.B.Va
            && s.DeliveredAtB == _submittedA.Length && s.DeliveredAtA == _submittedB.Length
            && !s.SeizeOwedA && !s.SeizeOwedB;
    }

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

            if (PopChangesState(s, station))
            {
                popEnabled[(int)station] = true;
                moves.Add(new Move(MoveKind.Pop, station));
            }

            if (s.SeizeOwed(station) && SeizeConfirmEvent(s.Machine(station)) is not null)
                moves.Add(new Move(MoveKind.SeizeConfirm, station));
        }

        // Quiescent-timeout abstraction: a timer fires only once everything in
        // flight has been delivered or lost and no internal work is pending.
        var quiet = s.ToA.Count == 0 && s.ToB.Count == 0 && !s.SeizeOwedA && !s.SeizeOwedB && !popEnabled[0] && !popEnabled[1];
        if (quiet)
        {
            foreach (var station in new[] { Station.A, Station.B })
            {
                if (s.Machine(station).T1 == TimerStatus.Running && Handles(s.Machine(station), "T1_expiry"))
                    moves.Add(new Move(MoveKind.T1Expiry, station));
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
                return Inject(s, station, FrameInput(s.Machine(station), frame, station),
                    $"receives {frame.Render()} from {peer}", incoming.Skip(1).ToList(), s.Budget, clearOwed: false, number);
            }
            case MoveKind.Duplicate:
            {
                var frame = incoming[0];
                return Inject(s, station, FrameInput(s.Machine(station), frame, station),
                    $"receives {frame.Render()} from {peer} (channel fault: DUPLICATE, a copy stays at the head; budget {Inv(s.Budget)} -> {Inv(s.Budget - 1)})",
                    incoming, s.Budget - 1, clearOwed: false, number);
            }
            case MoveKind.Drop:
            {
                var frame = incoming[0];
                var next = WithIncoming(s, station, incoming.Skip(1).ToList()) with { Budget = s.Budget - 1 };
                return new StepOutcome(next,
                    new StepRecord(number, $"channel {peer}->{station}", $"DROPS {frame.Render()} (budget {Inv(s.Budget)} -> {Inv(s.Budget - 1)})",
                        null, Array.Empty<string>(), SystemState.Summary(next.A), SystemState.Summary(next.B)),
                    null);
            }
            case MoveKind.Reorder:
            {
                var swapped = new List<Frame>(incoming);
                (swapped[0], swapped[1]) = (swapped[1], swapped[0]);
                var next = WithIncoming(s, station, swapped) with { Budget = s.Budget - 1 };
                return new StepOutcome(next,
                    new StepRecord(number, $"channel {peer}->{station}", $"REORDERS {incoming[0].Render()} behind {incoming[1].Render()} (budget {Inv(s.Budget)} -> {Inv(s.Budget - 1)})",
                        null, Array.Empty<string>(), SystemState.Summary(next.A), SystemState.Summary(next.B)),
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
                        break;
                    }
                    case UpperEffect { Primitive: "DL_DATA_indication" } ue:
                    {
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
        };

        var record = new StepRecord(number, station.ToString(), action, result?.TransitionId, effectsText,
            SystemState.Summary(next.A), SystemState.Summary(next.B));
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
