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
    /// <summary>DL_FLOW_OFF_request from the station's layer 3 (scenario-enabled; see docs/explorer.md).</summary>
    FlowOff,
    /// <summary>DL_FLOW_ON_request from the station's layer 3, lifting an earlier FlowOff.</summary>
    FlowOn,
    /// <summary>DL_DATA_request for the station's next frame, once the link is up (<see cref="ExplorerOptions.DataAfterConfirm"/>).</summary>
    Submit,
    /// <summary>DL_DISCONNECT_request from the station's layer 3 once the exchange is complete (<see cref="ExplorerOptions.Release"/>).</summary>
    Release,
    /// <summary>DL_CONNECT_request from the station's layer 3 on a live or waiting link (<see cref="ExplorerOptions.Reconnect"/>).</summary>
    Reconnect,
    /// <summary>DL_DISCONNECT_request from the station's layer 3 while its own connect is still being set up (<see cref="ExplorerOptions.Abandon"/>).</summary>
    Abandon,
    /// <summary>The station enters its local busy condition (<see cref="ExplorerOptions.LocalBusy"/>): own receiver busy, RNR response.</summary>
    BusyOn,
    /// <summary>The station leaves its local busy condition: own receiver busy cleared, RR command.</summary>
    BusyOff,
    /// <summary>T3_expiry at the station (<see cref="ExplorerOptions.T3"/>; quiescent-timeout rule, T1 not running).</summary>
    T3Expiry,
    /// <summary>The station's layer 3 sends its UI datagram (<see cref="ExplorerOptions.UnitData"/>), in the form <see cref="ExplorerOptions.UiForm"/> names.</summary>
    UnitData,
    /// <summary>The station puts its injected frame on the air (<see cref="ExplorerOptions.Inject"/>).</summary>
    Inject,
    /// <summary>Channel fault: the head frame arrives with an unknown control field (<see cref="FrameErrors.ControlField"/>).</summary>
    CorruptControl,
    /// <summary>Channel fault: the head frame arrives with an information field it may not carry (<see cref="FrameErrors.InfoNotPermitted"/>).</summary>
    CorruptInfo,
    /// <summary>Channel fault: the head frame arrives with the wrong length for its type (<see cref="FrameErrors.Length"/>).</summary>
    CorruptLength,
    /// <summary>Channel fault: the head I frame arrives failing the N1 check (<see cref="FrameErrors.N1"/>).</summary>
    CorruptN1,
    /// <summary>Channel fault: the head I frame arrives as a response (<see cref="FrameErrors.IResponse"/>).</summary>
    CorruptIResponse,
}

/// <summary>One BFS edge: a move applied to a station (for channel moves, the station the channel flows into).</summary>
public readonly record struct Move(MoveKind Kind, Station Station)
{
    public bool IsFault => Kind is MoveKind.Drop or MoveKind.Duplicate or MoveKind.Reorder or MoveKind.Inject || IsCorrupt;

    /// <summary>A frame-error fault (<see cref="ExplorerOptions.FrameErrors"/>).</summary>
    public bool IsCorrupt => Kind is MoveKind.CorruptControl or MoveKind.CorruptInfo or MoveKind.CorruptLength or MoveKind.CorruptN1 or MoveKind.CorruptIResponse;

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
    /// <summary>
    /// The transitions that consumed inputs released from a save after the
    /// step's own transition (pinned semantic 19), in order, each as
    /// <c>State/transition</c> (the state it fired in).
    /// </summary>
    public IReadOnlyList<string> Replayed { get; init; } = Array.Empty<string>();

    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append(Number.ToString(CultureInfo.InvariantCulture).PadLeft(3)).Append(". [").Append(Actor).Append("] ").Append(Action);
        if (Transition is not null)
        {
            sb.Append("\n       transition: ").Append(Transition);
            sb.Append("\n       effects: ").Append(Effects.Count == 0 ? "(none)" : string.Join("; ", Effects));
            if (Replayed.Count > 0) sb.Append("\n       replayed: ").Append(string.Join("; ", Replayed));
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

    /// <summary>
    /// The option combinations the stepper refuses, checked without building
    /// anything: throws <see cref="ArgumentException"/> with the reason. The
    /// constructor runs it first; TlaCheck runs it over every grid cell before
    /// it starts, so a refused combination fails at once rather than per cell.
    /// </summary>
    public static void Validate(ExplorerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var o = options;
        if (o.K is < 1 or > 7) throw new ArgumentException("k must be 1..7 at modulo 8", nameof(options));
        if (o.Modulo128B && (o.Seed != SeedKind.Disconnected || o.Peer != PeerKind.Tables))
            throw new ArgumentException("seeding B at modulo 128 is for the Disconnected seed with the table-driven peer only", nameof(options));
        if (o.DataAfterConfirm && o.Seed != SeedKind.Disconnected)
            throw new ArgumentException("data after the connect confirm is for the Disconnected seed only (the other seeds never report a connect to layer 3)", nameof(options));
        if (o.Seed == SeedKind.Disconnected && o.FramesBa > 0 && !o.DataAfterConfirm)
            throw new ArgumentException("the Disconnected seed supports A-to-B data only (B has no DL_DATA_request arm while Disconnected)", nameof(options));
        if (o.Seed == SeedKind.AwaitingV22Connection && (o.FramesAb > 0 || o.FramesBa > 0))
            throw new ArgumentException("the AwaitingV22Connection seed is a connect-phase seed: no data (figc4.6 discards DL_DATA_request while layer 3 initiated)", nameof(options));
        if (o.Peer != PeerKind.Tables && o.Seed == SeedKind.Connected)
            throw new ArgumentException("the v2.0 stub peer is for the connect-phase seeds only (--seed disconnected or awaiting-v22)", nameof(options));
        if (o.Peer != PeerKind.Tables && (o.FramesAb > 0 || o.FramesBa > 0))
            throw new ArgumentException("the v2.0 stub peer has no data phase: both frame counts must be 0 (the scenario ends at link establishment)", nameof(options));
        if (o.Release != ReleaseBy.None && o.Peer != PeerKind.Tables)
            throw new ArgumentException("the orderly release is for the table-driven peer only (the v2.0 stub has no data phase to complete)", nameof(options));
        if (o.ReleaseEarly && o.Release == ReleaseBy.None)
            throw new ArgumentException("an early release needs a releasing station (--release)", nameof(options));
        if (o.Abandon == ReleaseBy.Both && o.Peer != PeerKind.Tables)
            throw new ArgumentException("with the v2.0 stub as B only station A can abandon its connect (--abandon A)", nameof(options));
        if (o.Abandon != ReleaseBy.None && o.Release != ReleaseBy.None)
            throw new ArgumentException("abandon and release at once could have one layer 3 issue two DL_DISCONNECT_requests (abandon, then release while still connecting); use one or the other", nameof(options));
        if (o.Abandon != ReleaseBy.None && o.DataAfterConfirm)
            throw new ArgumentException("abandoning a connect is for scenarios whose data is submitted up front: a layer 3 that abandons its connect never offers data after a confirm, so that data could never be delivered", nameof(options));
        if (o.Abandon != ReleaseBy.None && o.Seed == SeedKind.Connected && o.Reconnect == ReconnectBy.None)
            throw new ArgumentException("abandoning a connect needs one to abandon: a connect-phase seed or a reconnect (--reconnect)", nameof(options));
        if (o.Reconnect != ReconnectBy.None && o.Peer != PeerKind.Tables)
            throw new ArgumentException("the layer-3 reconnect is for the table-driven peer only", nameof(options));
        if (o.LocalBusy != FlowControlAt.None && o.Peer != PeerKind.Tables)
            throw new ArgumentException("the local busy condition is for the table-driven peer only (the v2.0 stub has no data phase)", nameof(options));
        if (o.LongBusy && o.LocalBusy == FlowControlAt.None)
            throw new ArgumentException("a long busy period needs a busy station (--local-busy)", nameof(options));
        if (o.UnitData != FlowControlAt.None && o.Peer != PeerKind.Tables)
            throw new ArgumentException("UI datagrams are for the table-driven peer only (the v2.0 stub ignores UI)", nameof(options));
        if (o.UiForm != UiForm.Plain && o.UnitData == FlowControlAt.None)
            throw new ArgumentException("a UI form needs a station that sends UI (--unit-data)", nameof(options));
        if (o.FrameErrors != FrameErrors.None && o.Peer != PeerKind.Tables)
            throw new ArgumentException("frame errors are for the table-driven peer only (the v2.0 stub has no frame-error inputs)", nameof(options));
        if (o.Inject != FlowControlAt.None && (o.Peer != PeerKind.Tables || o.InjectedFrame == InjectedFrame.None))
            throw new ArgumentException("an injected frame needs the table-driven peer and a form (--inject-frame)", nameof(options));
        if (o.InjectedFrame != InjectedFrame.None && o.Inject == FlowControlAt.None)
            throw new ArgumentException("an injected frame form needs a station that injects it (--inject)", nameof(options));
        if (o.InjectedThen != InjectedFrame.None && o.InjectedFrame == InjectedFrame.None)
            throw new ArgumentException("a second injected frame needs a first (--inject-frame FIRST,SECOND)", nameof(options));
        if (o.Srej && (o.InjectedFrame is InjectedFrame.IPollAhead or InjectedFrame.IPollStale || o.InjectedThen is InjectedFrame.IPollAhead or InjectedFrame.IPollStale))
            throw new ArgumentException("the out-of-sequence injected I frames are for links without SREJ (with SREJ the receiver saves the frame, and it would collide with the sender's own frame of that number)", nameof(options));
        if (o.DataBeforeConfirm && !o.DataAfterConfirm)
            throw new ArgumentException("data before the confirm is a variant of data offered by moves (--data-after-confirm)", nameof(options));
        if (o.DataAfterReconnect && (o.Reconnect == ReconnectBy.None || o.Seed != SeedKind.Connected || o.DataAfterConfirm))
            throw new ArgumentException("data after a reconnect needs a reconnecting station (--reconnect) on the connected seed, without data offered by moves (--data-after-confirm)", nameof(options));
        if (o.T3At != FlowControlAt.Both && !o.T3)
            throw new ArgumentException("choosing the stations whose T3 expires needs T3 modelled (--t3)", nameof(options));
        if (o.FlowRounds < 0 || o.FlowOffAfterDelivered < 0)
            throw new ArgumentException("flow-control rounds and the delivered-frames threshold must be non-negative", nameof(options));
    }

    public Stepper(ExplorerOptions options, TableSet tables)
    {
        _o = options ?? throw new ArgumentNullException(nameof(options));
        _tables = tables ?? throw new ArgumentNullException(nameof(tables));
        Validate(_o);
        foreach (var (state, page) in tables.States)
            _eventsByState[state] = new HashSet<string>(page.Transitions.Select(t => t.On), StringComparer.Ordinal);
        // With DataAfterReconnect a reconnecting station has one more frame,
        // submitted once its reconnect is answered and the link reported up.
        _submittedA = Enumerable.Range(0, _o.FramesAb + (_o.DataAfterReconnect && Reconnects(Station.A) ? 1 : 0)).Select(i => "a" + Inv(i)).ToArray();
        _submittedB = Enumerable.Range(0, _o.FramesBa + (_o.DataAfterReconnect && Reconnects(Station.B) ? 1 : 0)).Select(i => "b" + Inv(i)).ToArray();
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
    /// its data is submitted behind the connection attempt, where figc4.2 discards it while layer 3 initiated, as the
    /// 1998 edition of the figure labels it, with no indication to layer 3: ax25spec#161).
    /// With <see cref="ExplorerOptions.DataAfterConfirm"/> nothing is submitted
    /// here: each station's layer 3 submits its data by Submit moves once its
    /// link is up.
    /// </summary>
    public SystemState Seed()
    {
        var initial = _o.Seed == SeedKind.Connected ? "Connected" : "Disconnected";
        var stub = _o.Peer == PeerKind.Tables ? null : new V20Peer(_o.Peer, Connected: false);
        // With a stub peer the table-driven B is a placeholder that is never dispatched.
        var state = new SystemState { A = NewMachine(Station.A, initial), B = NewMachine(Station.B, initial), StubPeer = stub };

        if (_o.Seed == SeedKind.Disconnected)
            state = Require(Inject(state, Station.A, new EventInput(UpperEvent(state.A, "DL_CONNECT_request")), "DL_CONNECT_request (seed)", state.ToA, state.Budget, clearOwed: false, number: 0));

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

        // With DataBeforeConfirm, A's layer 3 treats the link as usable as soon
        // as it has asked for it.
        if (_o.DataAfterConfirm)
            return state with { Budget = _o.Budget, AckedA = 0, AckedB = 0, LinkUpA = _o.DataBeforeConfirm };

        foreach (var label in _submittedA.Take(_o.FramesAb))
            state = Require(Inject(state, Station.A, new EventInput(UpperEvent(state.A, "DL_DATA_request"), Data: label), "DL_DATA_request (seed)", state.ToA, state.Budget, clearOwed: false, number: 0));
        foreach (var label in _submittedB.Take(_o.FramesBa))
            state = Require(Inject(state, Station.B, new EventInput(UpperEvent(state.B, "DL_DATA_request"), Data: label), "DL_DATA_request (seed)", state.ToB, state.Budget, clearOwed: false, number: 0));

        return state with { Budget = _o.Budget, AckedA = 0, AckedB = 0 };

        static SystemState Require(StepOutcome outcome) =>
            outcome.Violation is null
                ? outcome.Next
                : throw new InvalidOperationException($"seeding failed: {outcome.Violation.Message}");
    }

    private DataLinkMachine NewMachine(Station station, string initialState)
    {
        var m = new DataLinkMachine(_tables, initialState);
        m.SetVariable("modulo", (_o.Modulo128A && station == Station.A) || (_o.Modulo128B && station == Station.B) ? "128" : "8");
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
    /// submitted data delivered, no owed internal events. With
    /// <see cref="ExplorerOptions.Release"/>, the link must also have been
    /// released: both Disconnected after a layer 3 issued its
    /// DL_DISCONNECT_request, nothing in flight or queued, all data delivered.
    /// </summary>
    public bool IsQuiescent(SystemState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        // A layer 3 still waiting for the answer to its DL_CONNECT_request
        // (Reconnect) is never at rest.
        if (s.ReconnectPendingA || s.ReconnectPendingB) return false;
        // Nor one whose DL_DISCONNECT_request was consumed and not answered.
        if (s.DisconnectPendingA || s.DisconnectPendingB) return false;
        if (!ReportsMatch(s)) return false;
        if (!UnitDataAccounted(s)) return false;
        // The reset rule: no rest counts after a reset the injected error
        // does not justify.
        if (_o.FrameErrors != FrameErrors.None && (s.ResetA == Unjustified || s.ResetB == Unjustified)) return false;
        if (_o.AcceptGiveUp)
        {
            // The give-up rule: no rest counts after an unjustified give-up,
            // and a settled honest give-up with everything delivered is rest.
            if (s.GaveUpA == Unjustified || s.GaveUpB == Unjustified) return false;
            if (GiveUpSettled(s) && AllDelivered(s)) return true;
        }
        return PlainRest(s) && AllDelivered(s);
    }

    /// <summary>
    /// Plain quiescence, everything but the delivery count: both stations at
    /// rest (or, with a release, both Disconnected after it), nothing in
    /// flight or queued, nothing outstanding or owed.
    /// </summary>
    private bool PlainRest(SystemState s)
    {
        // A station that abandoned its connect (Abandon) wants the link down
        // too, so its path rests as a released one; a v2.0 stub as B is then
        // at rest once it is disconnected.
        if (_o.Release != ReleaseBy.None || s.AbandonedA || s.AbandonedB)
        {
            return (s.ReleasedA || s.ReleasedB || s.AbandonedA || s.AbandonedB)
                && s.A.State == "Disconnected" && s.B.State == "Disconnected"
                && (s.StubPeer is null || !s.StubPeer.Connected)
                && s.ToA.Count == 0 && s.ToB.Count == 0
                && s.A.QueueEntries.Count == 0 && s.B.QueueEntries.Count == 0
                && !s.SeizeOwedA && !s.SeizeOwedB;
        }
        // With the v2.0 stub as B, "B at rest" is simply "the stub is
        // connected" (link established with modulo 8; it has no queue,
        // variables or timers).
        var bAtRest = s.StubPeer is not null
            ? s.StubPeer.Connected
            : QuiescentState(s.B.State) && s.B.QueueEntries.Count == 0 && s.B.Vs == s.B.Va && !s.SeizeOwedB && KeepsAlive(s.B);
        return QuiescentState(s.A.State) && bAtRest && KeepsAlive(s.A)
            && s.ToA.Count == 0 && s.ToB.Count == 0
            && s.A.QueueEntries.Count == 0
            && s.A.Vs == s.A.Va
            && !s.SeizeOwedA;
    }

    /// <summary>
    /// With <see cref="ExplorerOptions.FrameErrors"/>: the state is a settled
    /// honest reset that lost data, so a deadlock there is reported as
    /// <see cref="Invariants.ResetLoss"/> rather than as a plain deadlock.
    /// Settled: some station reset honestly and none without justification,
    /// the system is at rest in every respect but the delivery count, and the
    /// layer 3 of every station whose data was lost has been told (a DL-ERROR
    /// Indication, then a DL-CONNECT Confirm or Indication).
    /// </summary>
    public bool IsResetLoss(SystemState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (_o.FrameErrors == FrameErrors.None || AllDelivered(s)) return false;
        if (s.ResetA != Honest && s.ResetB != Honest) return false;
        if (s.ResetA == Unjustified || s.ResetB == Unjustified) return false;
        if (s.ReconnectPendingA || s.ReconnectPendingB || s.DisconnectPendingA || s.DisconnectPendingB || !ReportsMatch(s) || !UnitDataAccounted(s)) return false;
        if (s.DeliveredAtB < _submittedA.Length && s.ToldA != 2) return false;
        if (s.DeliveredAtA < _submittedB.Length && s.ToldB != 2) return false;
        return PlainRest(s);
    }

    /// <summary>
    /// At rest, each table-driven station's last link report to its layer 3
    /// matches its state: "up" only while Connected or TimerRecovery, "down"
    /// only while Disconnected (no report yet constrains nothing). So a
    /// DL_DISCONNECT_indication answers a reconnect only if the station
    /// really is down, and a layer 3 told the link is down while it is up (or
    /// the reverse) is never at rest.
    /// </summary>
    private static bool ReportsMatch(SystemState s)
    {
        static bool Match(int report, string state) => report switch
        {
            1 => IsDataState(state),
            2 => state == "Disconnected",
            _ => true,
        };
        return Match(s.ReportA, s.A.State) && (s.StubPeer is not null || Match(s.ReportB, s.B.State));
    }

    /// <summary>
    /// With <see cref="ExplorerOptions.T3"/>, the keepalive rule: a station at
    /// rest in Connected has T1 or T3 running, so an idle link still polls its
    /// peer (§4.4.5.2: T3 runs whenever T1 does not on an established link).
    /// Without T3 modelled the timer is not observed and every station passes.
    /// </summary>
    private bool KeepsAlive(DataLinkMachine m) =>
        !_o.T3 || m.State != "Connected" || m.T1 == TimerStatus.Running || m.T3 == TimerStatus.Running;

    private const int Honest = 1;
    private const int Unjustified = 2;

    /// <summary>
    /// With <see cref="ExplorerOptions.AcceptGiveUp"/>: the state is a
    /// settled honest give-up that lost data, so a deadlock there is reported
    /// as <see cref="Invariants.GiveUpLoss"/> rather than as a plain deadlock.
    /// </summary>
    public bool IsGiveUpLoss(SystemState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return _o.AcceptGiveUp && GiveUpSettled(s) && !AllDelivered(s);
    }

    private bool AllDelivered(SystemState s) =>
        s.DeliveredAtB == _submittedA.Length && s.DeliveredAtA == _submittedB.Length;

    /// <summary>
    /// The give-up rule's settled state, data aside: some station gave up
    /// honestly and none without justification, each table-driven station is
    /// Disconnected or at rest as plain quiescence has it and has heard
    /// nothing from its peer since the peer gave up (the v2.0 stub, with no
    /// queue or timers, always is), nothing in flight, nothing owed.
    /// </summary>
    private bool GiveUpSettled(SystemState s)
    {
        if (s.GaveUpA != Honest && s.GaveUpB != Honest) return false;
        if (s.GaveUpA == Unjustified || s.GaveUpB == Unjustified) return false;
        if (s.ReconnectPendingA || s.ReconnectPendingB) return false;
        // Nor one whose DL_DISCONNECT_request was consumed and not answered.
        if (s.DisconnectPendingA || s.DisconnectPendingB) return false;
        if (!ReportsMatch(s)) return false;
        // A station still up counts only if nothing its peer sent after giving
        // up reached it: one that received the DM and stayed up is not settled.
        // With T3 modelled none still up is: its T3 polls and finds the peer
        // gone, so the give-up has settled only once both are Disconnected.
        bool Settled(DataLinkMachine m, bool heard) =>
            m.State == "Disconnected" || (!_o.T3 && !heard && QuiescentState(m.State) && m.QueueEntries.Count == 0 && m.Vs == m.Va);
        return Settled(s.A, s.HeardGiveUpA) && (s.StubPeer is not null || Settled(s.B, s.HeardGiveUpB))
            && s.ToA.Count == 0 && s.ToB.Count == 0
            && !s.SeizeOwedA && !s.SeizeOwedB;
    }

    /// <summary>
    /// Retry-loop attempts on a page: the frames a station must lose before
    /// it gives up honestly there. Establish_Data_Link starts RC at 1, so the
    /// connect loops give up after N2 transmissions; DL-DISCONNECT starts RC
    /// at 0, and Connected's T1 expiry sets it to 1 only after a first loss,
    /// so AwaitingRelease and TimerRecovery give up after N2 + 1. With
    /// <see cref="ExplorerOptions.T3"/>, a Timer Recovery loop entered by a T3
    /// expiry (figc4.4 t13: RC := 1, then the poll) gives up after N2 polls
    /// with no loss before it, so its threshold is N2.
    /// </summary>
    private int GiveUpThreshold(string page, bool t3Loop) =>
        page is "AwaitingConnection" or "AwaitingV22Connection" || (page == "TimerRecovery" && t3Loop) ? _o.N2 : _o.N2 + 1;

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

    private bool Releases(Station station) => _o.Release switch
    {
        ReleaseBy.Both => true,
        ReleaseBy.A => station == Station.A,
        _ => false,
    };

    private bool Abandons(Station station) => _o.Abandon switch
    {
        ReleaseBy.Both => true,
        ReleaseBy.A => station == Station.A,
        _ => false,
    };

    private bool Reconnects(Station station) => _o.Reconnect switch
    {
        ReconnectBy.Both => true,
        ReconnectBy.A => station == Station.A,
        _ => false,
    };

    private bool T3Expires(Station station) => _o.T3At switch
    {
        FlowControlAt.Both => true,
        FlowControlAt.A => station == Station.A,
        FlowControlAt.B => station == Station.B,
        _ => false,
    };

    private bool SendsUnitData(Station station) => _o.UnitData switch
    {
        FlowControlAt.Both => true,
        FlowControlAt.A => station == Station.A,
        FlowControlAt.B => station == Station.B,
        _ => false,
    };

    /// <summary>
    /// With <see cref="ExplorerOptions.UnitData"/>, at rest every UI datagram
    /// has been sent and then either reached its peer or was dropped by the
    /// channel. UI is unacknowledged, so a dropped datagram is honestly lost;
    /// one that never left its station, or is still on the air, is not
    /// accounted for. What the peer did with one that arrived is the per-step
    /// <see cref="Invariants.UnitData"/> check's business.
    /// </summary>
    private bool UnitDataAccounted(SystemState s) =>
        (!SendsUnitData(Station.A) || (s.UiSentA && s.UiDoneA)) && (!SendsUnitData(Station.B) || (s.UiSentB && s.UiDoneB));

    private bool QuiescentState(string state) =>
        state == "Connected" || (_o.TimerRecoveryIsQuiescent && state == "TimerRecovery");

    // ─── Enabled moves ────────────────────────────────────────────────

    public IReadOnlyList<Move> EnabledMoves(SystemState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var moves = new List<Move>();
        var popEnabled = new bool[2];
        var submitEnabled = new bool[2];

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
                    // Frame errors, one move per form that applies to the
                    // head frame (never into the v2.0 stub).
                    if (!(station == Station.B && s.StubPeer is not null))
                    {
                        foreach (var (kind, form) in CorruptKinds)
                        {
                            if (_o.FrameErrors.HasFlag(form) && CorruptApplies(form, incoming[0], s.Machine(station)))
                                moves.Add(new Move(kind, station));
                        }
                    }
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

            // Layer 3 submits its next frame once its link is up.
            if (SubmitReady(s, station))
            {
                submitEnabled[(int)station] = true;
                moves.Add(new Move(MoveKind.Submit, station));
            }

            // Orderly release: layer 3 disconnects an established link once
            // everything either side submitted has been delivered.
            if (ReleaseReady(s, station))
                moves.Add(new Move(MoveKind.Release, station));

            // Layer 3 resets a live or waiting link, under the same gate.
            if (ReconnectReady(s, station))
                moves.Add(new Move(MoveKind.Reconnect, station));

            // Layer 3 abandons its own connect while it is being set up.
            if (AbandonReady(s, station))
                moves.Add(new Move(MoveKind.Abandon, station));

            // The local busy condition: entered once on an established link,
            // left whenever it still holds.
            if (BusyOnReady(s, station))
                moves.Add(new Move(MoveKind.BusyOn, station));
            if (BusyOffReady(s, station))
                moves.Add(new Move(MoveKind.BusyOff, station));

            // Layer 3 sends its UI datagram once, in any state.
            if (SendsUnitData(station) && !s.UiSent(station))
                moves.Add(new Move(MoveKind.UnitData, station));

            // The harness puts the station's injected frame on the air, once.
            if (InjectReady(s, station))
                moves.Add(new Move(MoveKind.Inject, station));
        }

        // Quiescent-timeout abstraction: a timer fires only once everything in
        // flight has been delivered or lost and no internal work is pending
        // (a layer 3 holding data for a link that is up counts as pending; a
        // pending release does not, like flow control: it is a layer-3
        // decision, not work in flight).
        var quiet = s.ToA.Count == 0 && s.ToB.Count == 0 && !s.SeizeOwedA && !s.SeizeOwedB && !popEnabled[0] && !popEnabled[1]
            && !submitEnabled[0] && !submitEnabled[1];
        if (quiet)
        {
            foreach (var station in new[] { Station.A, Station.B })
            {
                if (s.Machine(station).T1 == TimerStatus.Running && Handles(s.Machine(station), "T1_expiry") && !BusyHoldsT1(s, station))
                    moves.Add(new Move(MoveKind.T1Expiry, station));
            }
            // T3 (the idle keepalive) fires under the same rule and only while
            // no T1 runs at either station: the figures stop T3 whenever they
            // start T1, and T3 is longer than T1 (§6.7.1.3), so a running T1,
            // here or at the peer, expires first.
            if (_o.T3 && s.A.T1 != TimerStatus.Running && (s.StubPeer is not null || s.B.T1 != TimerStatus.Running))
            {
                foreach (var station in new[] { Station.A, Station.B })
                {
                    if (station == Station.B && s.StubPeer is not null) continue;
                    var m = s.Machine(station);
                    if (m.T3 == TimerStatus.Running && Handles(m, "T3_expiry") && T3Expires(station))
                        moves.Add(new Move(MoveKind.T3Expiry, station));
                }
            }
        }

        return moves;
    }

    /// <summary>The frame-error moves, in move order, with the form each injects.</summary>
    private static readonly (MoveKind Kind, FrameErrors Form)[] CorruptKinds =
    {
        (MoveKind.CorruptControl, FrameErrors.ControlField),
        (MoveKind.CorruptInfo, FrameErrors.InfoNotPermitted),
        (MoveKind.CorruptLength, FrameErrors.Length),
        (MoveKind.CorruptN1, FrameErrors.N1),
        (MoveKind.CorruptIResponse, FrameErrors.IResponse),
    };

    private static FrameErrors FormOf(MoveKind kind) => CorruptKinds.First(c => c.Kind == kind).Form;

    /// <summary>
    /// Whether a frame can arrive corrupted in this form: a control field
    /// error can hit any frame; an information field where none is permitted
    /// an S or U frame that may not carry one (not I, UI or FRMR); a length
    /// error an S or U frame other than UI (whose length is UI Check's N1
    /// test); the N1 failure and the I response an I frame, and only at a
    /// station in Connected or TimerRecovery, the pages that draw an I frame
    /// input (elsewhere an I frame goes to a catch-all, corrupted or not).
    /// </summary>
    private static bool CorruptApplies(FrameErrors form, Frame frame, DataLinkMachine into) => form switch
    {
        FrameErrors.ControlField => true,
        FrameErrors.InfoNotPermitted => frame.Type is not ("I" or "UI" or "FRMR"),
        FrameErrors.Length => frame.Type is not ("I" or "UI"),
        _ => frame.Type == "I" && IsDataState(into.State),
    };

    /// <summary>The frame-error input each form raises, and the DL-ERROR Indication the figures report it with.</summary>
    private static (string Event, string Error) FrameErrorInput(FrameErrors form) => form switch
    {
        FrameErrors.ControlField => ("control_field_error", "DL-ERROR Indication (L)"),
        FrameErrors.InfoNotPermitted => ("info_not_permitted_in_frame", "DL-ERROR Indication (M)"),
        FrameErrors.Length => ("u_or_s_frame_length_error", "DL-ERROR Indication (N)"),
        _ => ("I_received", "DL-ERROR Indication (O)"),
    };

    private bool Injects(Station station) => _o.Inject switch
    {
        FlowControlAt.Both => true,
        FlowControlAt.A => station == Station.A,
        FlowControlAt.B => station == Station.B,
        _ => false,
    };

    /// <summary>
    /// The injected frame's gate: a REJ poll only while the receiver is in
    /// TimerRecovery (figc4.5's REJ command columns; figc4.4's REJ arms do not
    /// test the command bit), an SREJ command (P = 0 or 1) while it is in
    /// Connected or TimerRecovery, each from a sender in one of those and naming a frame
    /// outstanding at the receiver (the sender's V(r) inside the receiver's
    /// [V(a), V(s))), so it is a coherent reject; a UA with F = 0 only while
    /// the receiver waits on a connect or release page; an FRMR only while
    /// the receiver is in Connected or TimerRecovery.
    /// </summary>
    private bool InjectReady(SystemState s, Station station)
    {
        if (!Injects(station) || s.Injected(station) >= InjectCount) return false;
        var m = s.Machine(station);
        var peer = SystemState.Peer(station);
        var r = s.Machine(peer);
        var form = InjectForm(s.Injected(station));
        return form switch
        {
            // The injected I frames: both stations in the data phase and no
            // I frame on the channel to the receiver (the previous injected
            // one, or one of the sender's own), so the receiver's V(r) the
            // frame is numbered from is still current when it arrives (on a
            // channel without reordering). The in-sequence
            // forms take a number the sender's machine has not used, so the
            // receiver's next acknowledgement is an N(r) error at the sender
            // and the link is reset: only once everything either station
            // submitted has been delivered and the sender's queue is empty, so
            // nothing of the sender's own can collide with the frame and the
            // reset loses nothing the harness did not cause.
            InjectedFrame.IFrame or InjectedFrame.IPoll or InjectedFrame.IPollAhead or InjectedFrame.IPollStale when
                !IsDataState(m.State) || !IsDataState(r.State) || s.Incoming(peer).Any(f => f.Type == "I") => false,
            InjectedFrame.IFrame or InjectedFrame.IPoll => r.Vr == m.Vs && m.QueueEntries.Count == 0 && AllDelivered(s),
            // Without SREJ the receiver answers the frame ahead of sequence
            // with a REJ F=1 naming its V(r). When that is the sender's V(s)
            // (nothing outstanding), only while the sender is in
            // TimerRecovery with both channels empty, so the REJ reaches it
            // there (figc4.5's F=1 arm, where N(r) = V(s) brings it back to
            // Connected). In Connected, figc4.4's REJ arm takes N(r) = V(s)
            // as in range and Invoke Retransmission runs round the whole
            // modulus (a reject naming no outstanding frame, the
            // reject-naming-vs group), which is not this frame's to show.
            InjectedFrame.IPollAhead => r.Vr != m.Vs
                || (m.State == "TimerRecovery" && s.ToA.Count == 0 && s.ToB.Count == 0),
            InjectedFrame.IPollStale => true,
            InjectedFrame.RejPoll or InjectedFrame.SrejCommand or InjectedFrame.SrejPoll => IsDataState(m.State)
                && (form == InjectedFrame.RejPoll ? r.State == "TimerRecovery" : IsDataState(r.State))
                && (Distance(r.Va, m.Vr, r.Modulo) < Distance(r.Va, r.Vs, r.Modulo)
                    // With RejMayEqualVs the reject may also name the
                    // receiver's V(s), no outstanding frame (the
                    // reject-naming-vs arms of figc4.5): only while the
                    // receiver is in TimerRecovery with both channels empty,
                    // so the reject reaches it there. In Connected, figc4.4
                    // takes N(r) = V(s) as in range and Invoke Retransmission
                    // runs round the whole modulus. An SREJ command does not
                    // move V(a), so it names V(s) coherently only with
                    // nothing outstanding at all (V(a) = V(s)); otherwise it
                    // asks for a frame never sent.
                    || (_o.RejMayEqualVs && m.Vr == r.Vs && r.State == "TimerRecovery" && s.ToA.Count == 0 && s.ToB.Count == 0
                        && (form == InjectedFrame.RejPoll || r.Va == r.Vs))),
            InjectedFrame.UaF0 => r.State is "AwaitingConnection" or "AwaitingV22Connection" or "AwaitingRelease",
            InjectedFrame.Frmr => IsDataState(r.State),
            _ => false,
        };
    }

    /// <summary>Transition ids the harness reports for the injected frames (not table rows).</summary>
    public static string InjectId(InjectedFrame form) => form switch
    {
        InjectedFrame.RejPoll => "pinned:inject_rej_poll",
        InjectedFrame.SrejCommand => "pinned:inject_srej_command",
        InjectedFrame.SrejPoll => "pinned:inject_srej_poll",
        InjectedFrame.UaF0 => "pinned:inject_ua_f0",
        InjectedFrame.IFrame => "pinned:inject_i_frame",
        InjectedFrame.IPoll => "pinned:inject_i_poll",
        InjectedFrame.IPollAhead => "pinned:inject_i_poll_ahead",
        InjectedFrame.IPollStale => "pinned:inject_i_poll_stale",
        _ => "pinned:inject_frmr",
    };

    /// <summary>How many frames each injecting station puts on the air: one, or two with <see cref="ExplorerOptions.InjectedThen"/>.</summary>
    private int InjectCount => _o.InjectedThen == InjectedFrame.None ? 1 : 2;

    /// <summary>The form of a station's injected frame number <paramref name="n"/> (0 the first).</summary>
    private InjectedFrame InjectForm(int n) => n == 0 ? _o.InjectedFrame : _o.InjectedThen;

    /// <summary>The payload label of a station's injected I frame number <paramref name="n"/>: <c>xa0</c>, <c>xa1</c>, <c>xb0</c>, <c>xb1</c>.</summary>
    public static string InjectedLabel(Station sender, int n) => (sender == Station.A ? "xa" : "xb") + Inv(n);

    /// <summary>Whether a payload label is an injected I frame's (never a submitted or UI label).</summary>
    public static bool IsInjectedLabel(string? label) => label is not null && label.StartsWith('x');

    /// <summary>The station puts its injected frame on the air (<see cref="ExplorerOptions.Inject"/>).</summary>
    private StepOutcome InjectFrame(SystemState s, Station station, int number)
    {
        var m = s.Machine(station);
        var n = s.Injected(station);
        var form = InjectForm(n);
        var r = s.Machine(SystemState.Peer(station));
        var label = InjectedLabel(station, n);
        var frame = form switch
        {
            InjectedFrame.IFrame or InjectedFrame.IPoll => new Frame("I", Command: true, Pf: form == InjectedFrame.IPoll, Nr: m.Vr, Ns: r.Vr, Data: label),
            InjectedFrame.IPollAhead => new Frame("I", Command: true, Pf: true, Nr: m.Vr, Ns: (r.Vr + 1) % r.Modulo, Data: label),
            InjectedFrame.IPollStale => new Frame("I", Command: true, Pf: true, Nr: m.Vr, Ns: (r.Vr + r.Modulo - 1) % r.Modulo, Data: label),
            InjectedFrame.RejPoll => new Frame("REJ", Command: true, Pf: true, Nr: m.Vr, Ns: null, Data: null),
            InjectedFrame.SrejCommand or InjectedFrame.SrejPoll => new Frame("SREJ", Command: true, Pf: form == InjectedFrame.SrejPoll, Nr: m.Vr, Ns: null, Data: null),
            InjectedFrame.UaF0 => new Frame("UA", Command: false, Pf: false, Nr: null, Ns: null, Data: null),
            _ => new Frame("FRMR", Command: false, Pf: false, Nr: null, Ns: null, Data: null),
        };
        var outgoing = new List<Frame>(s.Outgoing(station)) { frame };
        var next = station == Station.A ? s with { ToB = outgoing, InjectedA = n + 1 } : s with { ToA = outgoing, InjectedB = n + 1 };
        var record = new StepRecord(number, station.ToString(), $"{frame.Render()} put on the air by the harness (another implementation)",
            InjectId(form), new[] { "frame " + frame.Render() }, SummaryOf(next, Station.A), SummaryOf(next, Station.B));
        return new StepOutcome(next, record, null);
    }

    private bool SubmitReady(SystemState s, Station station) =>
        (_o.DataAfterConfirm && s.LinkUp(station) && s.Offered(station) < Submitted(station).Length)
        || (_o.DataAfterReconnect && s.Offered(station) == 0 && SeedFrames(station) < Submitted(station).Length
            && s.Reconnected(station) && !s.ReconnectPending(station) && s.Report(station) == 1 && IsDataState(s.Machine(station).State));

    /// <summary>The frames a station's layer 3 submits at the seed or by its first moves: all but a <see cref="ExplorerOptions.DataAfterReconnect"/> frame.</summary>
    private int SeedFrames(Station station) => station == Station.A ? _o.FramesAb : _o.FramesBa;

    private bool ReleaseReady(SystemState s, Station station) =>
        Releases(station) && !s.Released(station)
        && (_o.ReleaseEarly ? IsLinkState(s.Machine(station).State) : IsDataState(s.Machine(station).State))
        && s.DeliveredAtB == _submittedA.Length && s.DeliveredAtA == _submittedB.Length;

    /// <summary>
    /// The abandon gate (<see cref="ExplorerOptions.Abandon"/>): once per
    /// station, while it is being set up (AwaitingConnection or
    /// AwaitingV22Connection), and only while its layer 3 has asked to
    /// connect and not heard back: station A from a connect-phase seed until
    /// its first link report, any station after a reconnect until that is
    /// answered. A connect the data link started itself (a reset) is not
    /// layer 3's to abandon.
    /// </summary>
    private bool AbandonReady(SystemState s, Station station) =>
        Abandons(station) && !s.Abandoned(station)
        && s.Machine(station).State is "AwaitingConnection" or "AwaitingV22Connection"
        && (s.ReconnectPending(station) || (station == Station.A && _o.Seed != SeedKind.Connected && s.ReportA == 0));

    private bool ReconnectReady(SystemState s, Station station) =>
        Reconnects(station) && !s.Reconnected(station) && IsLinkState(s.Machine(station).State)
        && s.DeliveredAtB == _o.FramesAb && s.DeliveredAtA == _o.FramesBa;

    private bool LocalBusies(Station station) => _o.LocalBusy switch
    {
        FlowControlAt.Both => true,
        FlowControlAt.A => station == Station.A,
        FlowControlAt.B => station == Station.B,
        _ => false,
    };

    /// <summary>
    /// With <see cref="ExplorerOptions.LocalBusy"/> and not
    /// <see cref="ExplorerOptions.LongBusy"/>: a busy period is short, so it
    /// leaves the peer at least one retry for the poll that finds it cleared.
    /// Every T1 expiry in Timer Recovery costs a retry (figc4.5 t21 counts RC
    /// up, and an RNR answer does not reset it), so while the station is busy
    /// its peer's T1 does not fire in Timer Recovery once RC has reached
    /// N2 - 1. Only while the busy station is in a data state (Connected or
    /// TimerRecovery): own receiver busy can stay set after it leaves them,
    /// and then the leave move can never fire, so a hold there would last for
    /// ever.
    /// </summary>
    private bool BusyHoldsT1(SystemState s, Station station)
    {
        if (_o.LocalBusy == FlowControlAt.None || _o.LongBusy) return false;
        var peer = SystemState.Peer(station);
        var m = s.Machine(station);
        var busy = s.Machine(peer);
        return LocalBusies(peer) && busy.OwnReceiverBusy && IsDataState(busy.State) && m.State == "TimerRecovery" && m.Rc >= _o.N2 - 1;
    }

    private bool BusyOnReady(SystemState s, Station station) =>
        LocalBusies(station) && !s.BusyUsed(station) && IsDataState(s.Machine(station).State) && !s.Machine(station).OwnReceiverBusy;

    private bool BusyOffReady(SystemState s, Station station) =>
        LocalBusies(station) && s.BusyUsed(station) && IsDataState(s.Machine(station).State) && s.Machine(station).OwnReceiverBusy;

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
    /// The event an upper-layer primitive raises in the station's current
    /// state: its own arm when the page has one, else the page's
    /// <c>all_other_primitives__from_upper_layer</c> catch-all (pinned
    /// semantic 5). With neither, the primitive itself, which the
    /// interpreter then reports as a missing arm.
    /// </summary>
    private string UpperEvent(DataLinkMachine m, string primitive) =>
        Handles(m, primitive) || m.IsSavedHere(primitive) || !Handles(m, "all_other_primitives__from_upper_layer") ? primitive : "all_other_primitives__from_upper_layer";

    /// <summary>
    /// The route a released held input takes in the state it is released
    /// into (pinned semantic 19(c)): exactly a fresh input's. Its own arm when
    /// the page has one, else the page's catch-all for its kind (pinned
    /// semantic 5): a received frame's (as <see cref="FrameEvent"/>), an
    /// upper-layer primitive's, or the lower-layer one for anything else.
    /// With no catch-all, the input itself, which the interpreter then
    /// reports as a missing arm.
    /// </summary>
    private EventInput RouteReleased(string state, EventInput held)
    {
        if (!_eventsByState.TryGetValue(state, out var events) || events.Contains(held.Event)) return held;
        string? routed;
        if (held.Event.EndsWith("_received", StringComparison.Ordinal) && held.Command is not null)
        {
            var type = held.Event[..^"_received".Length];
            routed = CatchAllForFrame(events, held.Command.Value, type is "I" or "RR" or "RNR" or "REJ" or "SREJ");
        }
        else if (held.Event.StartsWith("DL_", StringComparison.Ordinal))
        {
            routed = events.Contains("all_other_primitives__from_upper_layer") ? "all_other_primitives__from_upper_layer" : null;
        }
        else
        {
            routed = events.Contains("all_other_primitives__from_lower_layer") ? "all_other_primitives__from_lower_layer" : null;
        }
        return routed is null ? held : held with { Event = routed };
    }

    private static string? CatchAllForFrame(HashSet<string> events, bool command, bool iOrSupervisory)
    {
        if (command && events.Contains("all_other_commands")) return "all_other_commands";
        if (command && iOrSupervisory && events.Contains("i_or_s_command_received")) return "i_or_s_command_received";
        if (events.Contains("all_other_primitives__from_lower_layer")) return "all_other_primitives__from_lower_layer";
        return null;
    }

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
        // An explicit save takes precedence over the catch-all (pinned 19).
        if (events.Contains(direct) || m.IsSavedHere(direct)) return direct;
        return CatchAllForFrame(events, frame.Command, frame.IsIOrSupervisory);
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
        var outcome = ApplyMove(s, move, number);
        // The peer-busy hold: an RNR reaching a table-driven station raises
        // it, and a frame that clears the busy condition or resets or ends the
        // link lowers it; an I frame sent while it is up is a violation.
        if (move.Kind is MoveKind.Deliver or MoveKind.Duplicate && !(move.Station == Station.B && s.StubPeer is not null))
        {
            var heard = HeardBusyAfter(s.HeardBusy(move.Station), s.Incoming(move.Station)[0].Type);
            outcome = outcome with
            {
                Next = move.Station == Station.A ? outcome.Next with { HeardBusyA = heard } : outcome.Next with { HeardBusyB = heard },
            };
        }
        else if (move.Kind == MoveKind.Pop && outcome.Violation is null && On(Invariants.PeerBusyHold) && s.HeardBusy(move.Station)
                 && outcome.Record.Effects.Any(e => e.StartsWith("frame I ", StringComparison.Ordinal)))
        {
            outcome = outcome with
            {
                Violation = new Violation(Invariants.PeerBusyHold, move.Station,
                    $"station {move.Station} sent an I frame although its peer's RNR has not been cleared (section 6.4.9: a station that receives RNR stops transmitting I frames until the busy condition is cleared)"),
            };
        }
        // A UI datagram is accounted for once it reaches its peer or the
        // channel drops it (UnitData); the only UI frames are datagrams.
        if (_o.UnitData != FlowControlAt.None && (move.Kind is MoveKind.Deliver or MoveKind.Duplicate or MoveKind.Drop || move.IsCorrupt)
            && s.Incoming(move.Station)[0].Type == "UI")
        {
            outcome = outcome with
            {
                Next = move.Station == Station.A ? outcome.Next with { UiDoneB = true } : outcome.Next with { UiDoneA = true },
            };
        }
        // The give-up rule: a frame reaching a station after its peer gave up
        // was sent at or after the give-up (T1 only fires with both channels
        // empty), so the station has heard it.
        if (_o.AcceptGiveUp && (move.Kind is MoveKind.Deliver or MoveKind.Duplicate || move.IsCorrupt) && s.GaveUp(SystemState.Peer(move.Station)) != 0)
        {
            outcome = outcome with
            {
                Next = move.Station == Station.A ? outcome.Next with { HeardGiveUpA = true } : outcome.Next with { HeardGiveUpB = true },
            };
        }
        return outcome;
    }

    /// <summary>The peer-busy hold after receiving a frame of this type: RNR raises it; RR, REJ, SREJ and the frames that reset or end the link lower it; anything else leaves it.</summary>
    private static bool HeardBusyAfter(bool heard, string type) => type switch
    {
        "RNR" => true,
        "RR" or "REJ" or "SREJ" or "SABM" or "SABME" or "UA" or "DM" or "DISC" or "FRMR" => false,
        _ => heard,
    };

    private StepOutcome ApplyMove(SystemState s, Move move, int number)
    {
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
                    $"receives {frame.Render()} from {peer}", incoming.Skip(1).ToList(), s.Budget, clearOwed: false, number, frame.Type);
            }
            case MoveKind.Duplicate:
            {
                var frame = incoming[0];
                var action = $"receives {frame.Render()} from {peer} (channel fault: DUPLICATE, a copy stays at the head; budget {Inv(s.Budget)} -> {Inv(s.Budget - 1)})";
                if (station == Station.B && s.StubPeer is not null)
                    return InjectStub(s, frame, action, incoming, s.Budget - 1, number);
                return Inject(s, station, FrameInput(s.Machine(station), frame, station), action, incoming, s.Budget - 1, clearOwed: false, number, frame.Type);
            }
            case MoveKind.FlowOff:
                return Inject(s, station, new EventInput("DL_FLOW_OFF_request"), "DL_FLOW_OFF_request (layer 3 turns flow off)", incoming, s.Budget, clearOwed: false, number);
            case MoveKind.FlowOn:
                return Inject(s, station, new EventInput("DL_FLOW_ON_request"), "DL_FLOW_ON_request (layer 3 turns flow back on)", incoming, s.Budget, clearOwed: false, number);
            case MoveKind.Drop:
            {
                var frame = incoming[0];
                var next = WithIncoming(s, station, incoming.Skip(1).ToList()) with { Budget = s.Budget - 1 };
                // The give-up rule counts every drop as a loss for both stations.
                if (_o.AcceptGiveUp) next = next with { LossesA = s.LossesA + 1, LossesB = s.LossesB + 1 };
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
            case MoveKind.CorruptControl or MoveKind.CorruptInfo or MoveKind.CorruptLength or MoveKind.CorruptN1 or MoveKind.CorruptIResponse:
            {
                // The receiver sees the frame-error input, not the frame; the
                // frame itself is lost. N1 and I response are I frame inputs
                // with the atom false or the command bit lost.
                var frame = incoming[0];
                var form = FormOf(move.Kind);
                var m = s.Machine(station);
                EventInput? input;
                if (form is FrameErrors.N1 or FrameErrors.IResponse)
                {
                    input = FrameInput(m, form == FrameErrors.IResponse ? frame with { Command = false } : frame, station, n1Fails: form == FrameErrors.N1);
                }
                else
                {
                    var ev = FrameErrorInput(form).Event;
                    input = Handles(m, ev) ? new EventInput(ev)
                        : Handles(m, "all_other_primitives__from_lower_layer") ? new EventInput("all_other_primitives__from_lower_layer")
                        : null;
                }
                var what = form switch
                {
                    FrameErrors.ControlField => "control field error",
                    FrameErrors.InfoNotPermitted => "information field not permitted",
                    FrameErrors.Length => "U or S frame length error",
                    FrameErrors.N1 => "information field fails the N1 check",
                    _ => "arrives as a response",
                };
                var outcome = Inject(s, station, input,
                    $"receives {frame.Render()} from {peer} corrupted: {what} (channel fault: FRAME ERROR; budget {Inv(s.Budget)} -> {Inv(s.Budget - 1)})",
                    incoming.Skip(1).ToList(), s.Budget - 1, clearOwed: false, number, frameType: null, corrupt: form);
                // The give-up rule counts a corrupted frame as a loss, as a drop.
                if (_o.AcceptGiveUp)
                    outcome = outcome with { Next = outcome.Next with { LossesA = outcome.Next.LossesA + 1, LossesB = outcome.Next.LossesB + 1 } };
                return outcome;
            }
            case MoveKind.Pop:
                return Inject(s, station, new EventInput("I_frame_pops_off_queue"), "I frame pops off queue", incoming, s.Budget, clearOwed: false, number);
            case MoveKind.SeizeConfirm:
            {
                var ev = SeizeConfirmEvent(s.Machine(station)) ?? "LM_SEIZE_confirm";
                return Inject(s, station, new EventInput(ev), "LM_SEIZE_confirm (link multiplexer grants the channel)", incoming, s.Budget, clearOwed: true, number);
            }
            case MoveKind.Submit:
            {
                var label = Submitted(station)[_o.DataAfterReconnect ? SeedFrames(station) + s.Offered(station) : s.Offered(station)];
                var outcome = Inject(s, station, new EventInput(UpperEvent(s.Machine(station), "DL_DATA_request"), Data: label), _o.DataBeforeConfirm && station == Station.A ? "DL_DATA_request (layer 3, not waiting for the confirm)" : "DL_DATA_request (layer 3, link up)", incoming, s.Budget, clearOwed: false, number);
                var next = station == Station.A
                    ? outcome.Next with { OfferedA = s.OfferedA + 1 }
                    : outcome.Next with { OfferedB = s.OfferedB + 1 };
                return outcome with { Next = next };
            }
            case MoveKind.Release:
            {
                var outcome = Inject(s, station, new EventInput(UpperEvent(s.Machine(station), "DL_DISCONNECT_request")), "DL_DISCONNECT_request (layer 3 releases the link: everything is delivered)", incoming, s.Budget, clearOwed: false, number, primitive: "DL_DISCONNECT_request");
                var next = station == Station.A
                    ? outcome.Next with { ReleasedA = true }
                    : outcome.Next with { ReleasedB = true };
                return outcome with { Next = next };
            }
            case MoveKind.Abandon:
            {
                var outcome = Inject(s, station, new EventInput(UpperEvent(s.Machine(station), "DL_DISCONNECT_request")), "DL_DISCONNECT_request (layer 3 abandons its connect while it is being set up)", incoming, s.Budget, clearOwed: false, number, primitive: "DL_DISCONNECT_request");
                var next = station == Station.A
                    ? outcome.Next with { AbandonedA = true }
                    : outcome.Next with { AbandonedB = true };
                return outcome with { Next = next };
            }
            case MoveKind.Reconnect:
            {
                var outcome = Inject(s, station, new EventInput(UpperEvent(s.Machine(station), "DL_CONNECT_request")), "DL_CONNECT_request (layer 3 resets the link: everything is delivered)", incoming, s.Budget, clearOwed: false, number);
                // The pending flag is set after the dispatch, so an arm that
                // answered the request within the same dispatch (a confirm or
                // a disconnect indication from the DL-CONNECT arm itself)
                // would be overwritten and the request left pending. No
                // current arm does: each sends SABM(E) or nothing.
                var next = station == Station.A
                    ? outcome.Next with { ReconnectedA = true, ReconnectPendingA = true }
                    : outcome.Next with { ReconnectedB = true, ReconnectPendingB = true };
                return outcome with { Next = next };
            }
            case MoveKind.T1Expiry:
                return Inject(s, station, new EventInput("T1_expiry"), "T1 expiry (quiescent timeout: nothing in flight)", incoming, s.Budget, clearOwed: false, number);
            case MoveKind.T3Expiry:
                return Inject(s, station, new EventInput("T3_expiry"), "T3 expiry (idle keepalive: nothing in flight, T1 not running)", incoming, s.Budget, clearOwed: false, number);
            case MoveKind.BusyOn:
            case MoveKind.BusyOff:
                return LocalBusy(s, station, move.Kind == MoveKind.BusyOn, number);
            case MoveKind.UnitData:
                return SendUnitData(s, station, incoming, number);
            case MoveKind.Inject:
                return InjectFrame(s, station, number);
            default:
                throw new ArgumentOutOfRangeException(nameof(move));
        }
    }

    /// <summary>Transition ids the harness reports for the local busy condition (not table rows).</summary>
    public const string BusyEnterId = "pinned:local_busy_enter";

    public const string BusyLeaveId = "pinned:local_busy_leave";

    /// <summary>
    /// Enter or leave the local busy condition (<see cref="ExplorerOptions.LocalBusy"/>):
    /// the action stacks the figures draw for DL-FLOW-OFF and DL-FLOW-ON
    /// Request with #43 and H3 fixed. Entering: Set Own Receiver Busy,
    /// N(r) := V(r), RNR response F = 0, Clear Acknowledge Pending. Leaving:
    /// Clear Own Receiver Busy, N(r) := V(r), RR command P = 0, Clear
    /// Acknowledge Pending, and when T1 is not running Stop T3, Start T1. No
    /// table row runs, so no per-step check can fire here.
    /// </summary>
    private static StepOutcome LocalBusy(SystemState s, Station station, bool enter, int number)
    {
        var m = s.Machine(station).Clone();
        m.SetVariable("own_receiver_busy", enter ? "true" : "false");
        m.SetVariable("ack_pending", "false");
        Frame frame;
        if (enter)
        {
            frame = new Frame("RNR", Command: false, Pf: false, Nr: m.Vr, Ns: null, Data: null);
        }
        else
        {
            frame = new Frame("RR", Command: true, Pf: false, Nr: m.Vr, Ns: null, Data: null);
            if (m.T1 != TimerStatus.Running)
            {
                m.SetTimer("t3", TimerStatus.Stopped);
                m.SetTimer("t1", TimerStatus.Running);
            }
        }
        var outgoing = new List<Frame>(s.Outgoing(station)) { frame };
        var next = station == Station.A
            ? s with { A = m, ToB = outgoing, BusyUsedA = s.BusyUsedA || enter }
            : s with { B = m, ToA = outgoing, BusyUsedB = s.BusyUsedB || enter };
        var record = new StepRecord(number, station.ToString(),
            enter ? "local busy condition entered (own receiver busy, RNR)" : "local busy condition cleared (RR)",
            enter ? BusyEnterId : BusyLeaveId, new[] { "frame " + frame.Render() }, SummaryOf(next, Station.A), SummaryOf(next, Station.B));
        return new StepOutcome(next, record, null);
    }

    /// <summary>Transition ids the harness reports for a UI datagram it puts on the air itself (not table rows).</summary>
    public const string UiPollId = "pinned:ui_poll";

    public const string UiResponseId = "pinned:ui_response";

    /// <summary>The label of the station's UI datagram.</summary>
    public static string UiLabel(Station station) => station == Station.A ? "ua0" : "ub0";

    /// <summary>
    /// The station's layer 3 sends its UI datagram (<see cref="ExplorerOptions.UnitData"/>).
    /// A plain or long datagram goes through the page's DL_UNIT_DATA_request
    /// arm; a poll (UI command, P = 1) or a response (UI response, F = 0) is
    /// put on the air by the harness, since layer 3 cannot ask for either.
    /// </summary>
    private StepOutcome SendUnitData(SystemState s, Station station, IReadOnlyList<Frame> incoming, int number)
    {
        var label = UiLabel(station);
        if (_o.UiForm is UiForm.Plain or UiForm.Oversize)
        {
            var outcome = Inject(s, station, new EventInput(UpperEvent(s.Machine(station), "DL_UNIT_DATA_request"), Data: label),
                "DL_UNIT_DATA_request (layer 3 sends a UI datagram)", incoming, s.Budget, clearOwed: false, number);
            return outcome with { Next = station == Station.A ? outcome.Next with { UiSentA = true } : outcome.Next with { UiSentB = true } };
        }
        var poll = _o.UiForm == UiForm.Poll;
        var frame = new Frame("UI", Command: poll, Pf: poll, Nr: null, Ns: null, Data: label);
        var outgoing = new List<Frame>(s.Outgoing(station)) { frame };
        var next = station == Station.A ? s with { ToB = outgoing, UiSentA = true } : s with { ToA = outgoing, UiSentB = true };
        var record = new StepRecord(number, station.ToString(),
            poll ? "UI command P=1 put on the air (a UI poll)" : "UI response put on the air",
            poll ? UiPollId : UiResponseId, new[] { "frame " + frame.Render() }, SummaryOf(next, Station.A), SummaryOf(next, Station.B));
        return new StepOutcome(next, record, null);
    }

    private EventInput? FrameInput(DataLinkMachine m, Frame frame, Station into, bool n1Fails = false)
    {
        var ev = FrameEvent(m, frame);
        if (ev is null) return null;
        var atoms = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["able_to_establish"] = !(_o.PeerDeclinesSabme && into == Station.B && frame.Type == "SABME"),
            // Only a long UI datagram fails the N1 check.
            // and an I frame corrupted to fail it (FrameErrors.N1).
            ["info_field_length_le_N1_and_content_is_octet_aligned"] = !(_o.UiForm == UiForm.Oversize && frame.Type == "UI") && !n1Fails,
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
        IReadOnlyList<Frame> newIncoming, int newBudget, bool clearOwed, int number, string? frameType = null, FrameErrors corrupt = FrameErrors.None,
        string? primitive = null)
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
                    // Saves (pinned semantic 19): a released input is routed as a fresh one would be.
                    result = m.Dispatch(input, RouteReleased);
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
        var linkUp = s.LinkUp(station);
        var reconnectPending = s.ReconnectPending(station);
        var report = s.Report(station);
        var connectReported = false;
        var told = s.Told(station);
        var effectsText = new List<string>();
        // An injected I frame's payload (InjectedFrame.IFrame and after) is
        // delivered exactly when the dispatch of that frame itself, received
        // in sequence (N(s) = V(r) before it), accepts it (V(r) moves past
        // N(s)): once, in that dispatch, and in no other. It is not one of the
        // peer's submissions, so it moves no delivery count.
        var injectedDeliverable = input is { Event: "I_received", Command: true } && IsInjectedLabel(input.Data) && input.Ns == pre.Vr
            ? input.Data
            : null;
        var injectedInSequence = injectedDeliverable;

        // Saves (pinned semantic 19): the transitions this dispatch ran that
        // consumed an input, in order: the step's own (unless the input was
        // held), then one per released held input, each with the state it
        // fired in and whether the input was layer 3's DL-DISCONNECT Request.
        var consumed = new List<(DispatchResult R, string From, bool Request)>();
        if (result is not null)
        {
            if (result.TransitionId != DataLinkMachine.SaveTransitionId)
                consumed.Add((result, pre.State, (primitive ?? input!.Event) == "DL_DISCONNECT_request"));
            var from = result.NextState;
            foreach (var r in result.Replayed)
            {
                consumed.Add((r, from, r.Input?.Event == "DL_DISCONNECT_request"));
                from = r.NextState;
            }
        }

        // DisconnectKept: a consumed DL-DISCONNECT Request takes the station to
        // Disconnected or Awaiting Release, or it is lost.
        if (violation is null && On(Invariants.DisconnectKept))
        {
            foreach (var (r, from, request) in consumed)
            {
                if (!request || r.NextState is "Disconnected" or "AwaitingRelease") continue;
                violation = new Violation(Invariants.DisconnectKept, station,
                    $"station {station}'s layer 3 DL_DISCONNECT_request was consumed by {from}/{r.TransitionId}{(ReferenceEquals(r, result) ? "" : " (released from a save)")}, which leaves the station in {r.NextState}: the disconnect is lost");
                break;
            }
        }

        // The disconnect answer rule: a consumed DL-DISCONNECT Request is
        // pending until DL_DISCONNECT_confirm or DL_DISCONNECT_indication.
        var disconnectPending = s.DisconnectPending(station);
        foreach (var (r, _, request) in consumed)
        {
            if (request) disconnectPending = true;
            if (r.Effects.Any(e => e is UpperEffect { Primitive: "DL_DISCONNECT_confirm" or "DL_DISCONNECT_indication" })) disconnectPending = false;
        }

        if (result is not null)
        {
            foreach (var effect in result.AllEffects)
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
                        if (violation is null && _o.LocalBusy != FlowControlAt.None && On(Invariants.BusyIndication))
                            violation = BusyIndicationCheck(station, m, frame);
                        break;
                    }
                    case UpperEffect { Primitive: "DL_DATA_indication" } ue:
                    {
                        if (violation is null && On(Invariants.FlowOffDelivery) && s.FlowOff(station))
                        {
                            violation = new Violation(Invariants.FlowOffDelivery, station,
                                $"station {station} delivered `{ue.Detail}` upward while its layer 3 has flow off (DL_FLOW_OFF_request issued and not yet lifted): the busy condition did not hold the data back");
                        }
                        // With the local busy condition modelled, a station
                        // whose own receiver was busy when the event arrived
                        // delivers nothing either (§6.4.10).
                        if (violation is null && On(Invariants.FlowOffDelivery) && _o.LocalBusy != FlowControlAt.None && pre.OwnReceiverBusy)
                        {
                            violation = new Violation(Invariants.FlowOffDelivery, station,
                                $"station {station} delivered `{ue.Detail}` upward while its own receiver is busy (local busy condition): the busy condition did not hold the data back");
                        }
                        var submitted = Submitted(peer);
                        var expected = delivered < submitted.Length ? submitted[delivered] : null;
                        if (IsInjectedLabel(ue.Detail))
                        {
                            if (injectedDeliverable is not null && string.Equals(ue.Detail, injectedDeliverable, StringComparison.Ordinal))
                                injectedDeliverable = null;
                            else if (violation is null && On(Invariants.Delivery))
                                violation = new Violation(Invariants.Delivery, station,
                                    $"station {station} delivered the injected payload `{ue.Detail}` upward other than once, in the dispatch that accepts that I frame in sequence: out-of-sequence, duplicate or stored delivery");
                        }
                        else if (expected is not null && string.Equals(ue.Detail, expected, StringComparison.Ordinal))
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
                    case UpperEffect { Primitive: "DL_CONNECT_confirm" or "DL_CONNECT_indication" } ue:
                        if (_o.DataAfterConfirm) linkUp = true;
                        if (ue.Primitive == "DL_CONNECT_confirm") reconnectPending = false;
                        report = 1;
                        connectReported = true;
                        if (told >= 1) told = 2;
                        break;
                    case UpperEffect { Primitive: "DL_DISCONNECT_indication" or "DL_DISCONNECT_confirm" } ue:
                        // Layer 3 stops submitting once it is told the link is
                        // down, and resumes after the next confirm or
                        // indication; what it had already offered stays counted.
                        if (_o.DataAfterConfirm) linkUp = false;
                        if (ue.Primitive == "DL_DISCONNECT_indication") reconnectPending = false;
                        report = 2;
                        break;
                    case LmEffect { Primitive: "LM_seize_request" }:
                        owed = true;
                        break;
                    case UpperEffect ue when ue.Primitive == "DL-ERROR Indication (F)"
                                             || (corrupt != FrameErrors.None && ue.Primitive == FrameErrorInput(corrupt).Error):
                        // The reset rule: layer 3 has been told of the error
                        // behind a reset (the corrupted frame's own code at
                        // the station that received it, (F) "data link reset"
                        // at a station that takes the peer's SABM), and of the
                        // reset once a connect report follows.
                        told = 1;
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

        // The other half of the injected payload rule: accepted (V(r) moved)
        // without delivering it, or delivered without accepting it.
        if (violation is null && result is not null && injectedInSequence is not null && On(Invariants.Delivery))
        {
            var accepted = m.Vr != pre.Vr;
            var deliveredIt = injectedDeliverable is null;
            if (accepted != deliveredIt)
                violation = new Violation(Invariants.Delivery, station, accepted
                    ? $"station {station} accepted the injected I frame `{injectedInSequence}` in sequence (V(r) {Inv(pre.Vr)} to {Inv(m.Vr)}) but did not deliver it upward: lost delivery"
                    : $"station {station} delivered the injected payload `{injectedInSequence}` upward but V(r) stayed {Inv(pre.Vr)}: delivered without accepting the frame");
        }

        // A UI frame's handling (UnitData): delivered or refused as UI Check
        // says, and a poll answered as section 4.3.3.6 says. Keyed on the
        // frame being UI, not on the event it is dispatched as, so a page
        // that lost its UI arm (the frame falls to a catch-all) is caught.
        if (violation is null && result is not null && frameType == "UI" && _o.UnitData != FlowControlAt.None && On(Invariants.UnitData))
            violation = UnitDataCheck(station, pre, input!, _o.UiForm == UiForm.Oversize, result.Effects);

        // A corrupted frame (FrameError): reported with the DL-ERROR the
        // figures draw for its form and not delivered, whatever event the
        // page dispatched it as.
        if (violation is null && result is not null && corrupt != FrameErrors.None && On(Invariants.FrameError))
            violation = FrameErrorCheck(station, pre, corrupt, result.Effects);

        // The reset rule's bookkeeping (FrameErrors): a station that sends
        // SABM or SABME from Connected or TimerRecovery has reset the link;
        // honestly only on the corrupted frame itself. A reset layer 3 asked
        // for (DL-CONNECT Request) is not the data link's.
        var resetMark = s.Reset(station);
        if (_o.FrameErrors != FrameErrors.None && result is not null && IsDataState(pre.State) && input!.Event != "DL_CONNECT_request"
            && result.Effects.OfType<FrameEffect>().Any(f => f.Frame is "SABM" or "SABME"))
            resetMark = Math.Max(resetMark, corrupt != FrameErrors.None ? Honest : Unjustified);
        if (_o.FrameErrors == FrameErrors.None) told = 0;

        // Layer-3 flow control bookkeeping and the busy-condition check
        // (§6.4.10: entering the busy condition is what DL-FLOW-OFF is for,
        // and the figure's own DL-FLOW-ON arm only acts when busy is set).
        var flowOff = s.FlowOff(station);
        var flowRounds = s.FlowRounds(station);
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
            if (violation is null && On(Invariants.BusyTracksFlow) && m.OwnReceiverBusy)
                violation = new Violation(Invariants.BusyTracksFlow, station,
                    $"station {station} took {result.TransitionId} on DL_FLOW_ON_request but its own-receiver-busy condition is still set");
        }

        // The give-up rule's bookkeeping: progress (this station's frames
        // acknowledged further, or a link (re)establishment reported to its
        // layer 3) clears its loss count; a T1 expiry that takes it to
        // Disconnected is a give-up, honest if the losses since its last
        // progress reach the page's retry-loop attempts.
        var losses = s.Losses(station);
        var gaveUp = s.GaveUp(station);
        if (_o.AcceptGiveUp && result is not null)
        {
            if (connectReported || acked > s.Acked(station)) losses = 0;
            if (input!.Event == "T1_expiry" && m.State == "Disconnected")
                gaveUp = Math.Max(gaveUp, losses >= GiveUpThreshold(pre.State, s.T3Loop(station)) ? Honest : Unjustified);
        }
        // A Timer Recovery loop entered by T3 lasts until the station leaves
        // Timer Recovery.
        var t3Loop = result is not null
            ? m.State == "TimerRecovery" && (s.T3Loop(station) || input!.Event == "T3_expiry")
            : s.T3Loop(station);

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
            LinkUpA = station == Station.A ? linkUp : s.LinkUpA,
            LinkUpB = station == Station.B ? linkUp : s.LinkUpB,
            ReconnectPendingA = station == Station.A ? reconnectPending : s.ReconnectPendingA,
            ReconnectPendingB = station == Station.B ? reconnectPending : s.ReconnectPendingB,
            DisconnectPendingA = station == Station.A ? disconnectPending : s.DisconnectPendingA,
            DisconnectPendingB = station == Station.B ? disconnectPending : s.DisconnectPendingB,
            ReportA = station == Station.A ? report : s.ReportA,
            ReportB = station == Station.B ? report : s.ReportB,
            LossesA = station == Station.A ? losses : s.LossesA,
            LossesB = station == Station.B ? losses : s.LossesB,
            GaveUpA = station == Station.A ? gaveUp : s.GaveUpA,
            GaveUpB = station == Station.B ? gaveUp : s.GaveUpB,
            T3LoopA = station == Station.A ? t3Loop : s.T3LoopA,
            T3LoopB = station == Station.B ? t3Loop : s.T3LoopB,
            ResetA = station == Station.A ? resetMark : s.ResetA,
            ResetB = station == Station.B ? resetMark : s.ResetB,
            ToldA = station == Station.A ? told : s.ToldA,
            ToldB = station == Station.B ? told : s.ToldB,
        };

        var record = new StepRecord(number, station.ToString(), action, result?.TransitionId, effectsText,
            SummaryOf(next, Station.A), SummaryOf(next, Station.B))
        {
            Replayed = consumed.Where(c => !ReferenceEquals(c.R, result)).Select(c => c.From + "/" + c.R.TransitionId).ToList(),
        };
        return new StepOutcome(next, record, violation);
    }

    // ─── Invariants ───────────────────────────────────────────────────

    private bool On(Invariants inv) => _o.Invariants.HasFlag(inv);

    private static bool IsDataState(string state) => state is "Connected" or "TimerRecovery";

    /// <summary>A live or waiting link: established, or being set up.</summary>
    private static bool IsLinkState(string state) => state is "AwaitingConnection" or "AwaitingV22Connection" or "Connected" or "TimerRecovery";

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
    /// The UnitData check on one received UI frame. Delivery (UI Check,
    /// figc4.7): a command whose information field passes the N1 check is
    /// delivered upward once, with its label; a
    /// response is not delivered and draws DL-ERROR (Q); a command that fails
    /// the N1 check is not delivered and draws DL-ERROR (K). The answer
    /// (section 4.3.3.6, the reading the tables take on ax25spec#154): a
    /// command with P = 1 is answered by exactly one frame, a response with
    /// F = 1, DM outside the information transfer state and RR or RNR in it;
    /// a command with P = 0 by none. What a station sends on a UI response is
    /// not judged. Whether the information field is too long is the
    /// scenario's (<see cref="UiForm.Oversize"/>), not read back from the
    /// atom the harness supplied, so a harness that failed to supply it is
    /// caught too; and the label it must carry is the peer's datagram's, not
    /// read back from the frame, so a frame that lost its label is caught.
    /// </summary>
    private static Violation? UnitDataCheck(Station station, DataLinkMachine pre, EventInput input, bool longInfo, IReadOnlyList<Effect> effects)
    {
        var command = input.Command ?? true;
        var poll = command && input.Pf == true;
        var label = UiLabel(SystemState.Peer(station));
        var indications = effects.OfType<UpperEffect>().Where(e => e.Primitive == "DL_UNIT_DATA_indication").ToList();
        var errors = effects.OfType<UpperEffect>().Where(e => e.Primitive.StartsWith("DL-ERROR", StringComparison.Ordinal)).Select(e => e.Primitive).ToList();
        var frames = effects.OfType<FrameEffect>().ToList();
        var what = $"station {station} in {pre.State} received UI {(command ? "cmd P=" : "rsp F=")}{(input.Pf == true ? "1" : "0")} [{label}]{(longInfo ? " (information field too long)" : "")}";
        if (command && !longInfo)
        {
            if (indications.Count != 1 || indications[0].Detail != label)
                return new Violation(Invariants.UnitData, station,
                    $"{what} but delivered {(indications.Count == 0 ? "nothing" : string.Join(", ", indications.Select(i => i.Detail ?? "(no label)")))} upward: a UI command that passes UI Check is delivered once (DL-UNIT-DATA Indication)");
        }
        else
        {
            var expected = command ? "DL-ERROR Indication (K)" : "DL-ERROR Indication (Q)";
            if (indications.Count != 0 || !errors.Contains(expected))
                return new Violation(Invariants.UnitData, station,
                    $"{what} and {(indications.Count != 0 ? "delivered it upward" : "did not report " + expected)}: UI Check refuses it with {expected}");
        }
        if (!command) return null;
        if (!poll)
        {
            return frames.Count == 0 ? null : new Violation(Invariants.UnitData, station,
                $"{what} and sent {string.Join(", ", frames.Select(f => Frame.FromEffect(f).Render()))}: a UI command with P = 0 asks for no response");
        }
        var dataState = IsDataState(pre.State);
        var answered = frames.Count == 1 && !frames[0].Command && frames[0].Pf
            && (dataState ? frames[0].Frame is "RR" or "RNR" : frames[0].Frame == "DM");
        return answered ? null : new Violation(Invariants.UnitData, station,
            $"{what} and sent {(frames.Count == 0 ? "nothing" : string.Join(", ", frames.Select(f => Frame.FromEffect(f).Render())))}: section 4.3.3.6 answers a UI command with P = 1 with {(dataState ? "RR or RNR" : "DM")} F=1");
    }

    /// <summary>
    /// The FrameError check on one corrupted frame: the station reports the
    /// DL-ERROR Indication the figures draw for the form ((L), (M), (N); (O)
    /// for an I frame failing the N1 check and for an I response) and
    /// delivers nothing upward.
    /// </summary>
    private static Violation? FrameErrorCheck(Station station, DataLinkMachine pre, FrameErrors form, IReadOnlyList<Effect> effects)
    {
        var expected = FrameErrorInput(form).Error;
        var ups = effects.OfType<UpperEffect>().ToList();
        if (ups.Any(e => e.Primitive is "DL_DATA_indication" or "DL_UNIT_DATA_indication"))
            return new Violation(Invariants.FrameError, station,
                $"station {station} in {pre.State} delivered a corrupted frame ({form}) upward");
        return ups.Any(e => e.Primitive == expected) ? null : new Violation(Invariants.FrameError, station,
            $"station {station} in {pre.State} received a corrupted frame ({form}) and did not report {expected} to layer 3");
    }

    /// <summary>A station whose own receiver is busy sends RNR, not RR, REJ or SREJ (§6.4.10).</summary>
    private static Violation? BusyIndicationCheck(Station station, DataLinkMachine m, Frame frame) =>
        m.OwnReceiverBusy && frame.Type is "RR" or "REJ" or "SREJ"
            ? new Violation(Invariants.BusyIndication, station,
                $"station {station} sent {frame.Render()} while its own receiver is busy: a busy station indicates it with RNR (section 6.4.10)")
            : null;

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
