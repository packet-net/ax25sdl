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
    /// <summary>Both stations Disconnected; A has issued DL_CONNECT_request and then submitted its data (which figc4.2 discards while layer 3 initiated, as the 1998 edition of the figure labels it, with no indication: ax25spec#161).</summary>
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

/// <summary>Which stations' layer 3 issues DL_DISCONNECT_request (an orderly release) once the exchange is complete.</summary>
public enum ReleaseBy
{
    None,
    /// <summary>Station A's layer 3 only.</summary>
    A,
    /// <summary>Either station's layer 3 (so both may release at once).</summary>
    Both,
}

/// <summary>Which stations' layer 3 issues DL_CONNECT_request on a live or waiting link (<see cref="ExplorerOptions.Reconnect"/>).</summary>
public enum ReconnectBy
{
    None,
    /// <summary>Station A's layer 3 only.</summary>
    A,
    /// <summary>Either station's layer 3.</summary>
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
    /// <summary>
    /// Not a switch: the kind a deadlock is reported under, with
    /// <see cref="ExplorerOptions.AcceptGiveUp"/>, when the stuck state is a
    /// settled honest give-up that lost data (everything else about it is
    /// rest). Data lost in a give-up is reported as such, never accepted.
    /// </summary>
    GiveUpLoss = 2048,
    /// <summary>
    /// A station that has received RNR from its peer sends no I frame until it
    /// receives RR, REJ, SREJ or a frame that resets or ends the link (§6.4.9:
    /// it "stops transmitting I frames until the busy condition is cleared").
    /// Checked on the frames sent, not on the station's peer-busy flag, so a
    /// figure that ignores RNR is caught. Only RNR raises it, and on today's
    /// tables only <see cref="ExplorerOptions.LocalBusy"/> produces one.
    /// </summary>
    PeerBusyHold = 4096,
    /// <summary>
    /// A station whose own receiver is busy indicates it: every supervisory
    /// frame it sends is RNR, never RR, REJ or SREJ (§6.4.10). Only checked with
    /// <see cref="ExplorerOptions.LocalBusy"/>, the only way own receiver busy
    /// is ever set on today's tables.
    /// </summary>
    BusyIndication = 8192,
    /// <summary>
    /// A UI frame reaching a station is handled as UI Check and section
    /// 4.3.3.6 say (only checked with <see cref="ExplorerOptions.UnitData"/>,
    /// the only source of UI frames): a UI command whose information field
    /// passes the N1 check is delivered upward exactly once
    /// (DL_UNIT_DATA_indication with its label) and nothing else is; a UI
    /// response is not delivered and draws DL-ERROR (Q); a UI command that
    /// fails the N1 check is not delivered and draws DL-ERROR (K). A UI
    /// command with P = 1 is answered by exactly one response with F = 1: DM
    /// outside the information transfer state, RR or RNR in it (Connected,
    /// TimerRecovery); a UI command with P = 0 is answered by nothing.
    /// </summary>
    UnitData = 16384,

    /// <summary>
    /// A corrupted frame reaching a station (only with
    /// <see cref="ExplorerOptions.FrameErrors"/>, the only source of them) is
    /// reported to layer 3 with the DL-ERROR Indication the figures draw for
    /// it and is not delivered: (L) for a control field error, (M) for an
    /// information field in a frame that may not carry one, (N) for a U or S
    /// frame of the wrong length, (O) for an I frame that fails the N1 check
    /// and (O) for an I response (the figures' code; the error list in
    /// Appendix C4 gives S). Keyed on the corruption, not on the event the
    /// page dispatches it as, so a page that lost its arm is caught.
    /// </summary>
    FrameError = 32768,

    /// <summary>
    /// Not a switch: the kind a deadlock is reported under, with
    /// <see cref="ExplorerOptions.FrameErrors"/>, when the stuck state is a
    /// settled honest frame-error reset that lost data, with the layer 3 of
    /// every station that lost data told (a DL-ERROR Indication, then a
    /// DL-CONNECT Confirm or Indication). Data lost in a reset is reported as
    /// such, never accepted (the precedent is <see cref="GiveUpLoss"/>).
    /// </summary>
    ResetLoss = 65536,

    /// <summary>
    /// A DL-DISCONNECT Request from layer 3 is never lost (packet-net/ax25sdl#97,
    /// pinned semantic 19): when a transition consumes it, fresh or released
    /// from a save, that transition takes the station to Disconnected or
    /// Awaiting Release. A request that reaches an "all other" column, or an
    /// arm that leaves the station where it was, is a lost disconnect. A
    /// request a state saves is not consumed, so holding it is not a loss.
    /// </summary>
    DisconnectKept = 131072,

    /// <summary>Everything except <see cref="SelectiveProgress"/>.</summary>
    Default = DefinedState | SequenceSanity | Delivery | RejectCoherence | AckCoherence | Quiescence | Deadlock | FlowOffDelivery | BusyTracksFlow | PeerBusyHold | BusyIndication | UnitData | FrameError | DisconnectKept,
}

/// <summary>
/// The corrupted forms a frame can arrive in (<see cref="ExplorerOptions.FrameErrors"/>):
/// the frame-error inputs of the Data Link figures.
/// </summary>
[Flags]
public enum FrameErrors
{
    None = 0,
    /// <summary>Any frame, its control field unknown or not implemented: <c>control_field_error</c>.</summary>
    ControlField = 1,
    /// <summary>An S or U frame that may not carry an information field (not UI, FRMR or I) arrives with one: <c>info_not_permitted_in_frame</c>.</summary>
    InfoNotPermitted = 2,
    /// <summary>An S or U frame (not UI) of the wrong length for its type: <c>u_or_s_frame_length_error</c>.</summary>
    Length = 4,
    /// <summary>An I frame reaching a station in Connected or TimerRecovery fails the N1 check (<c>info_field_length_le_N1_and_content_is_octet_aligned</c> false).</summary>
    N1 = 8,
    /// <summary>An I frame reaching a station in Connected or TimerRecovery arrives as a response (its command bit lost).</summary>
    IResponse = 16,
}

/// <summary>
/// A frame the harness puts on the air as from another implementation
/// (<see cref="ExplorerOptions.InjectedFrame"/>): forms no table sends.
/// </summary>
public enum InjectedFrame
{
    None,
    /// <summary>A REJ command with P = 1 and N(r) = the sender's V(r), naming a frame outstanding at the receiver, while the receiver is in TimerRecovery.</summary>
    RejPoll,
    /// <summary>An SREJ command (P = 0) with N(r) = the sender's V(r), naming a frame outstanding at the receiver (ax25spec#164: SREJ is a response only).</summary>
    SrejCommand,
    /// <summary>The same SREJ command with P = 1.</summary>
    SrejPoll,
    /// <summary>A UA response with F = 0, while the receiver waits on a connect or release page.</summary>
    UaF0,
    /// <summary>An FRMR response (F = 0), while the receiver is in Connected or TimerRecovery.</summary>
    Frmr,
    /// <summary>
    /// An I command with P = 0 and N(s) = the receiver's V(r) (the next in
    /// sequence), N(r) = the sender's V(r), carrying the injected payload
    /// label (<c>xa0</c>, <c>xb0</c>; a second injection <c>xa1</c>). Only
    /// while the receiver's V(r) is the sender's V(s), the sender's queue is
    /// empty and all its submitted data has been delivered, so the frame takes
    /// a sequence number the sender has not used and the sender has nothing
    /// left that it could collide with.
    /// </summary>
    IFrame,
    /// <summary>The same I command with P = 1.</summary>
    IPoll,
    /// <summary>
    /// An I command with P = 1 and N(s) = the receiver's V(r) + 1 (out of
    /// sequence; inside the window when k is 2 or more), N(r) = the sender's
    /// V(r). Without SREJ only, so every arm discards it.
    /// </summary>
    IPollAhead,
    /// <summary>
    /// An I command with P = 1 and N(s) = the receiver's V(r) - 1 (outside
    /// the window: an old number), N(r) = the sender's V(r). Without SREJ
    /// only.
    /// </summary>
    IPollStale,
}

/// <summary>The form of a UI datagram on the air (<see cref="ExplorerOptions.UiForm"/>).</summary>
public enum UiForm
{
    /// <summary>A UI command with P = 0, through the sender's DL_UNIT_DATA_request arm.</summary>
    Plain,
    /// <summary>A UI command with P = 1, put on the air by the harness.</summary>
    Poll,
    /// <summary>A UI response with F = 0, put on the air by the harness.</summary>
    Response,
    /// <summary>A UI command with P = 0 through the sender's arm, whose information field fails the receiver's N1 check.</summary>
    Oversize,
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
    /// Disconnected seed only: each station's layer 3 holds its data until
    /// its data link has reported the link up (DL_CONNECT_confirm at A,
    /// DL_CONNECT_indication at B), as a well-behaved layer 3 would, then
    /// submits it one DL_DATA_request per move. Off by default: the seed
    /// submits A's data straight after DL_CONNECT_request, where figc4.2
    /// discards it while layer 3 initiated, with no indication (ax25spec#161). With this on, B may
    /// submit data too.
    /// </summary>
    public bool DataAfterConfirm { get; init; }

    /// <summary>
    /// Orderly release. The layer 3 of each station named here issues
    /// DL_DISCONNECT_request once, on an established link (Connected or
    /// TimerRecovery, the pages with a direct arm for it), once every frame
    /// either station submitted has been delivered upward at its peer: the
    /// application knows the exchange is complete. Quiescence then means both
    /// stations Disconnected with everything delivered, not both Connected.
    /// Table-driven peer only. Off by default.
    /// </summary>
    public ReleaseBy Release { get; init; } = ReleaseBy.None;

    /// <summary>
    /// With <see cref="Release"/>: the release may also come while the link
    /// is still being set up (AwaitingConnection, AwaitingV22Connection), not
    /// only on an established link. Figures C4.2 and C4.6 draw a Save
    /// (Requeue) for DL-DISCONNECT Request there, so the request is held and
    /// replayed once the station reaches a state that does not save it
    /// (pinned semantic 19). Off by default.
    /// </summary>
    public bool ReleaseEarly { get; init; }

    /// <summary>
    /// Layer 3 abandons its own connect: the layer 3 of each station named
    /// here may issue DL_DISCONNECT_request once while the link is being set
    /// up (AwaitingConnection or AwaitingV22Connection), and only while it
    /// has asked to connect and not yet heard back: station A from the
    /// Disconnected and AwaitingV22Connection seeds until its first link
    /// report, or any station after a reconnect (<see cref="Reconnect"/>)
    /// until that is answered. Figures C4.2 and C4.6 save the request, so it
    /// is held until the connect succeeds (then DISC) or fails (then
    /// DL-DISCONNECT Confirm in Disconnected). The move is optional: a path
    /// on which the link comes up first rests as usual. Once a station has
    /// abandoned, quiescence means both stations Disconnected (a v2.0 stub
    /// peer disconnected), and every DL_DISCONNECT_request consumed must have
    /// been answered with DL_DISCONNECT_confirm or DL_DISCONNECT_indication.
    /// Station A may abandon with the v2.0 stub as B. Off by default.
    /// </summary>
    public ReleaseBy Abandon { get; init; } = ReleaseBy.None;

    /// <summary>
    /// Layer 3 resets the link: the layer 3 of each station named here issues
    /// DL_CONNECT_request once, on a live or waiting link (AwaitingConnection,
    /// AwaitingV22Connection, Connected or TimerRecovery), once every frame
    /// either station submitted has been delivered upward at its peer (the
    /// same gate as the release). The layer 3 then waits for the answer:
    /// quiescence requires that every such request was answered by
    /// DL_CONNECT_confirm or DL_DISCONNECT_indication. Table-driven peer only.
    /// Off by default.
    /// </summary>
    public ReconnectBy Reconnect { get; init; } = ReconnectBy.None;

    /// <summary>
    /// With <see cref="DataAfterConfirm"/>: station A's layer 3 does not wait
    /// for DL_CONNECT_confirm but submits its data by moves from the seed on,
    /// so data can be offered while the connection is still being set up
    /// (where figc4.2 discards it while layer 3 initiated, ax25spec#161), or
    /// after it is up. B's layer 3 still waits for its indication. Off by
    /// default.
    /// </summary>
    public bool DataBeforeConfirm { get; init; }

    /// <summary>
    /// The give-up rule. A station that exhausts its N2 retries (a T1 expiry
    /// that takes it to Disconnected) has given up honestly when at least as
    /// many frames were dropped, on either channel, since that station last
    /// made progress (its frames acknowledged further, or a link
    /// (re)establishment reported to its layer 3) as its retry loop has
    /// attempts: N2 on AwaitingConnection and AwaitingV22Connection, whose
    /// Establish_Data_Link starts RC at 1, and N2 + 1 on TimerRecovery and
    /// AwaitingRelease, whose count starts at 0 (DL-DISCONNECT) or reaches 1
    /// only after the first loss (Connected's T1 expiry). Any other give-up is
    /// unjustified. Quiescence then also accepts a settled honest give-up:
    /// some station gave up honestly and none unjustifiably, each station
    /// Disconnected or at rest as in plain quiescence, nothing in flight or
    /// owed, and everything submitted delivered. A settled honest give-up
    /// that lost data is not rest: it is a deadlock, reported as
    /// <see cref="Invariants.GiveUpLoss"/> so the loss is visible as such.
    /// With this on, no rest counts once a station has given up without
    /// justification. Off by default.
    /// </summary>
    public bool AcceptGiveUp { get; init; }

    /// <summary>
    /// A local busy condition (§6.4.10, "Whenever a TNC enters a busy
    /// condition, it indicates this by sending an RNR response at the next
    /// opportunity"), injected by the harness at the stations named here, once
    /// each, on an established link (Connected or TimerRecovery). Entering it
    /// does what the figures draw for DL-FLOW-OFF Request once #43 and H3 are
    /// fixed (ax25spec PR #109): Set Own Receiver Busy, N(r) := V(r), RNR
    /// response F = 0, Clear Acknowledge Pending. Leaving it, which is enabled
    /// whenever the condition still holds, does what the figures draw for
    /// DL-FLOW-ON Request with H3's N(r) box: Clear Own Receiver Busy,
    /// N(r) := V(r), RR command P = 0, Clear Acknowledge Pending, and when T1
    /// is not running Stop T3 and Start T1. On today's tables nothing else sets
    /// own receiver busy (#43), so this is how the peer-busy and own-busy arms
    /// are reached. Table-driven stations only. Off by default.
    /// </summary>
    public FlowControlAt LocalBusy { get; init; } = FlowControlAt.None;

    /// <summary>
    /// With <see cref="LocalBusy"/>, the busy condition may outlast the peer's
    /// retries. By default it does not: it leaves the peer at least one retry
    /// for the poll that finds it cleared, so while a station is busy its
    /// peer's T1 does not fire in Timer Recovery once RC has reached N2 - 1
    /// (each RNR-answered poll costs a retry: figc4.5 counts RC up on T1 and
    /// an RNR answer does not reset it). With this
    /// on, that hold is lifted and a long busy period can end the link
    /// (figc4.5 t21 with peer busy, DL-ERROR U or I). Off by default.
    /// </summary>
    public bool LongBusy { get; init; }

    /// <summary>
    /// Model T3 expiry: a T3_expiry move at a station whose T3 is running,
    /// while no T1 runs at either station (T3 is longer than T1, §6.7.1.3, so
    /// a running T1 expires first), under the quiescent-timeout rule T1 uses (nothing in
    /// flight, owed, ready to pop or waiting to be submitted anywhere), on a
    /// page with a T3 arm (figc4.4 only; figc4.5 has none, #91). Two rules come
    /// with it: at rest a Connected station keeps its keepalive (T1 or T3
    /// running), and with <see cref="AcceptGiveUp"/> a station left up after
    /// its peer gave up is never settled (its T3 will find the peer gone). Off
    /// by default.
    /// </summary>
    public bool T3 { get; init; }

    /// <summary>
    /// The stations whose layer 3 sends one UI datagram (labelled
    /// <c>ua0</c> at A, <c>ub0</c> at B) with DL_UNIT_DATA_request, once, in
    /// any state: every Data Link page has an arm for it. The move is a
    /// layer-3 decision, not work in flight, so it does not hold back T1.
    /// UI is unacknowledged, so a datagram the channel drops is honestly
    /// lost; quiescence asks only that every datagram was sent and then
    /// either reached its peer or was dropped, and the per-step
    /// <see cref="Invariants.UnitData"/> check judges what the peer did with
    /// it. Table-driven peer only. Off by default.
    /// </summary>
    public FlowControlAt UnitData { get; init; } = FlowControlAt.None;

    /// <summary>
    /// With <see cref="UnitData"/>, the form the datagram takes on the air.
    /// <see cref="UiForm.Plain"/> and <see cref="UiForm.Oversize"/> go through the
    /// sender's DL_UNIT_DATA_request arm (UI command, P = 0); a long one
    /// fails the receiver's N1 check. <see cref="UiForm.Poll"/> (UI command
    /// with P = 1) and <see cref="UiForm.Response"/> (UI response, F = 0) are
    /// put on the air by the harness, as from another implementation: layer
    /// 3 cannot ask for either (DL-UNIT-DATA Request carries no P bit and
    /// the figures only send UI commands), and no table row runs at the
    /// sender.
    /// </summary>
    public UiForm UiForm { get; init; } = UiForm.Plain;

    /// <summary>
    /// Frame errors: a channel fault, while the fault budget lasts, that
    /// delivers the head frame of a channel corrupted, in each form named here
    /// that applies to it (see <see cref="FrameErrors"/>). The
    /// receiver sees the frame-error input the figures draw for that form, not
    /// the frame. Two rules come with it (pinned semantic 17): the
    /// <see cref="Invariants.FrameError"/> check on each corrupted frame, and
    /// the reset rule: a station that resets the link (sends SABM or SABME
    /// from Connected or TimerRecovery, other than at layer 3's own
    /// DL-CONNECT Request) does so honestly only on the corrupted frame
    /// itself; no state is at rest after any other reset, and a settled
    /// honest reset that lost data, with every losing station's layer 3 told,
    /// is reported as <see cref="Invariants.ResetLoss"/>. Table-driven
    /// stations only. Off by default.
    /// </summary>
    public FrameErrors FrameErrors { get; init; } = FrameErrors.None;

    /// <summary>
    /// The stations that put one injected frame on the air, once, in the form
    /// <see cref="InjectedFrame"/> names, as from another implementation
    /// (the pattern of the UI poll and response): a REJ command with P = 1
    /// while the receiver is in TimerRecovery, or an SREJ command while it is
    /// in Connected or TimerRecovery, each naming a frame outstanding at the
    /// receiver, from a sender in Connected or TimerRecovery; a UA with F = 0 while the
    /// receiver waits in AwaitingConnection, AwaitingV22Connection or
    /// AwaitingRelease; an FRMR while the receiver is in Connected or
    /// TimerRecovery; an I command (see <see cref="InjectedFrame.IFrame"/>
    /// and the forms after it) while both are in Connected or TimerRecovery,
    /// whose payload the Delivery check requires the receiver to deliver
    /// exactly when it accepts that frame in sequence (V(r) moves past its
    /// N(s)), once within that dispatch, and in no other dispatch. No table row runs at the sender (the step log reports
    /// <c>pinned:inject_*</c>). Not work in flight and, like a channel fault,
    /// not a move the liveness checks may use. Table-driven peer only. Off by
    /// default.
    /// </summary>
    public FlowControlAt Inject { get; init; } = FlowControlAt.None;

    /// <summary>With <see cref="Inject"/>, the injected frame's form.</summary>
    public InjectedFrame InjectedFrame { get; init; } = InjectedFrame.None;

    /// <summary>
    /// With <see cref="Inject"/>, a second injected frame the same station
    /// puts on the air after the first, once the first has left the channel
    /// (<see cref="InjectedFrame.None"/>: only one). Used with the injected I
    /// frames: an I frame in sequence followed by one out of sequence makes the
    /// receiver answer with an N(r) beyond anything the sender's machine sent.
    /// </summary>
    public InjectedFrame InjectedThen { get; init; } = InjectedFrame.None;

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
    /// was never sent. See docs/explorer.md. With <see cref="Inject"/> it also
    /// widens the injection gate: the injected REJ and SREJ commands may name
    /// the receiver's V(s), while it is in TimerRecovery with both channels
    /// empty (and, for an SREJ, nothing outstanding).
    /// </summary>
    public bool RejMayEqualVs { get; init; }

    /// <summary>Depth bound (steps from the seed). Hitting it is reported as a bound hit, never as "no violation".</summary>
    public int MaxDepth { get; init; } = 80;

    /// <summary>Visited-state bound. Hitting it is reported as a bound hit, never as "no violation".</summary>
    public int MaxStates { get; init; } = 400_000;
}
