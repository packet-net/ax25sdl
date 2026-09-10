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

    /// <summary>Everything except <see cref="SelectiveProgress"/>.</summary>
    Default = DefinedState | SequenceSanity | Delivery | RejectCoherence | AckCoherence | Quiescence | Deadlock,
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

    public Invariants Invariants { get; init; } = Invariants.Default;

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
