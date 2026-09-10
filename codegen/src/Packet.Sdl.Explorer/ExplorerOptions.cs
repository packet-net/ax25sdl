namespace Packet.Sdl.Explorer;

/// <summary>Channel fault kinds the explorer may inject while the fault budget lasts.</summary>
[Flags]
public enum FaultKinds
{
    None = 0,
    /// <summary>Remove the head frame of a channel queue.</summary>
    Drop = 1,
    /// <summary>Deliver the head frame and leave a copy of it at the head.</summary>
    Duplicate = 2,
    /// <summary>Swap the first two frames of a channel queue.</summary>
    Reorder = 4,
}

/// <summary>Which frames a drop fault may hit.</summary>
public enum DropScope
{
    /// <summary>Any frame.</summary>
    Any,
    /// <summary>
    /// Only I frames that are not the last fresh frame submitted in their
    /// direction (so a later frame always exists to expose the gap). This is
    /// the precondition of the selective-recovery progress check: losing the
    /// trailing frame, or a supervisory frame, can only be recovered by T1.
    /// </summary>
    InteriorI,
}

/// <summary>Where the exploration starts.</summary>
public enum SeedKind
{
    /// <summary>Both stations Connected with zeroed variables, T3 running, all data already queued.</summary>
    Connected,
    /// <summary>Both stations Disconnected; A has issued DL_CONNECT_request and queued its data.</summary>
    Disconnected,
    /// <summary>
    /// A is in AwaitingV22Connection with its SABME P=1 already in flight
    /// to B (RC 1, layer 3 initiated, T1 running, modulo 128, version 2.2);
    /// B is Disconnected. This is the state figc4.6 exists for. On the
    /// current tables a cold DL_CONNECT_request never reaches it
    /// (packethacking/ax25spec#44 routes the SABME initiator to
    /// AwaitingConnection), so the seed enters it directly, exactly as the
    /// golden trace frmr-fallback-downgrades-to-sabm does. Connect phase
    /// only: no data.
    /// </summary>
    AwaitingV22Connection,
}

/// <summary>Which stations layer 3 may flow-control (DL_FLOW_OFF_request / DL_FLOW_ON_request moves).</summary>
public enum FlowControlAt
{
    None,
    A,
    B,
    Both,
}

/// <summary>What plays station B.</summary>
public enum PeerKind
{
    /// <summary>The table-driven reference interpreter, like A.</summary>
    Tables,
    /// <summary>
    /// The hand-written v2.0 stub peer (<see cref="V20Peer"/>): answers
    /// SABME with FRMR (an unknown control field, v2.0 §2.3.4.3.3.1),
    /// SABM with UA. Connect phase only, no data phase.
    /// </summary>
    V20Frmr,
    /// <summary>
    /// The same stub answering SABME with DM F=1 instead (a command other
    /// than SABM while disconnected, v2.0 §2.3.4.3.5 / §2.4.3.4.3; what
    /// XRouter does on the wire, packethacking/ax25spec#48).
    /// </summary>
    V20Dm,
}

/// <summary>The invariants, each individually switchable so the calibration suite can say which one fires.</summary>
[Flags]
public enum Invariants
{
    None = 0,
    /// <summary>Both stations are in a state the tables define.</summary>
    DefinedState = 1,
    /// <summary>V(s), V(a), V(r) in range and outstanding frames never exceed k.</summary>
    SequenceSanity = 2,
    /// <summary>Each station's upward deliveries are an exact in-order prefix of what the peer submitted.</summary>
    Delivery = 4,
    /// <summary>A REJ/SREJ names a frame the emitter lacks and the receiver can resend.</summary>
    RejectCoherence = 8,
    /// <summary>V(a) never advances past what the peer has delivered upward.</summary>
    AckCoherence = 16,
    /// <summary>From every reachable state a fault-free path reaches quiescence (graph liveness).</summary>
    Quiescence = 32,
    /// <summary>A terminal state (no enabled move) that is not quiescent.</summary>
    Deadlock = 64,
    /// <summary>With SREJ negotiated, quiescence is reachable without any T1 expiry (off by default; see docs/explorer.md).</summary>
    SelectiveProgress = 128,
    /// <summary>The interpreter threw: an unbound verb/atom, a non-deterministic table, or a pinned-semantics gap. Always reported.</summary>
    MachineError = 256,
    /// <summary>No DL_DATA_indication is delivered at a station while its layer 3 has flow off (after DL_FLOW_OFF_request, before DL_FLOW_ON_request).</summary>
    FlowOffDelivery = 512,
    /// <summary>After DL_FLOW_OFF_request the station's own-receiver-busy condition is set, and after DL_FLOW_ON_request it is clear (§6.4.10).</summary>
    BusyTracksFlow = 1024,

    /// <summary>Everything except <see cref="SelectiveProgress"/>.</summary>
    Default = DefinedState | SequenceSanity | Delivery | RejectCoherence | AckCoherence | Quiescence | Deadlock | FlowOffDelivery | BusyTracksFlow,
}

/// <summary>One exploration scenario. Bounds are deliberately small; see docs/explorer.md.</summary>
public sealed record ExplorerOptions
{
    /// <summary>Directory holding the JSON tables (<c>spec/json</c> or a fixture directory).</summary>
    public required string TablesDir { get; init; }

    /// <summary>I frames A submits for B (labels a0, a1, ...), all queued in the seeded state.</summary>
    public int FramesAb { get; init; } = 2;

    /// <summary>I frames B submits for A (labels b0, b1, ...), all queued in the seeded state.</summary>
    public int FramesBa { get; init; }

    /// <summary>Window size k (1..7 at modulo 8). Keep at 4 or below unless the point is the k > modulus/2 constraint.</summary>
    public int K { get; init; } = 4;

    /// <summary>Selective reject negotiated (also sets version_2_2 on both stations).</summary>
    public bool Srej { get; init; }

    /// <summary>Retry limit N2.</summary>
    public int N2 { get; init; } = 4;

    /// <summary>Total number of channel faults the explorer may inject along any one path.</summary>
    public int Budget { get; init; }

    public FaultKinds Faults { get; init; } = FaultKinds.Drop | FaultKinds.Duplicate;

    public DropScope DropScope { get; init; } = DropScope.Any;

    public SeedKind Seed { get; init; } = SeedKind.Connected;

    /// <summary>Disconnected seed only: station B answers SABME with DM (a v2.0 peer) but accepts SABM.</summary>
    public bool PeerDeclinesSabme { get; init; }

    /// <summary>Disconnected seed only: station A is seeded at modulo 128, so its DL_CONNECT_request sends SABME.</summary>
    public bool Modulo128A { get; init; }

    /// <summary>
    /// What plays station B. The v2.0 stub peers are for the connect-phase
    /// seeds only (Disconnected or AwaitingV22Connection) and carry no data
    /// phase, so both frame counts must be 0; the scenario ends at "link
    /// established with modulo 8", which is the quiescent condition.
    /// </summary>
    public PeerKind Peer { get; init; } = PeerKind.Tables;

    /// <summary>
    /// Which stations may issue DL_FLOW_OFF_request / DL_FLOW_ON_request.
    /// A station issues FLOW_OFF once it has delivered
    /// <see cref="FlowOffAfterDelivered"/> frames upward (and only on a page
    /// with a direct arm, i.e. Connected or TimerRecovery), then FLOW_ON at
    /// any later point; that is one round, and <see cref="FlowRounds"/>
    /// bounds the rounds so the space stays finite.
    /// </summary>
    public FlowControlAt FlowControl { get; init; } = FlowControlAt.None;

    /// <summary>Rounds of FLOW_OFF then FLOW_ON each flow-controlling station may issue.</summary>
    public int FlowRounds { get; init; } = 1;

    /// <summary>Frames a station must have delivered upward before its FLOW_OFF is enabled.</summary>
    public int FlowOffAfterDelivered { get; init; } = 1;

    public Invariants Invariants { get; init; } = Invariants.Default;

    /// <summary>
    /// Triage aid only: count a station in TimerRecovery as quiescent (when
    /// everything else is quiet). Lets a grid run look past the known
    /// "stuck in TimerRecovery after a lost poll" finding (docs/explorer.md,
    /// hypothesis H1) to whatever lies behind it. Off by default.
    /// </summary>
    public bool TimerRecoveryIsQuiescent { get; init; }

    /// <summary>
    /// Receiver-side reject coherence: also accept a REJ whose N(r) equals the
    /// receiver's V(s) (a pure acknowledgement). Off by default: on a FIFO
    /// channel such a REJ can only come from a reject raised on a stale
    /// duplicate, and figc4.7's do-while loop then pushes I(V(s)), a frame that
    /// was never sent. See docs/explorer.md.
    /// </summary>
    public bool RejMayEqualVs { get; init; }

    /// <summary>Depth bound (steps from the seed). Hitting it is reported as a bound hit, never as "no violation".</summary>
    public int MaxDepth { get; init; } = 80;

    /// <summary>Visited-state bound. Hitting it is reported as a bound hit, never as "no violation".</summary>
    public int MaxStates { get; init; } = 400_000;
}
