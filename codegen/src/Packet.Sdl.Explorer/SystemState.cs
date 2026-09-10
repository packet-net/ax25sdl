using System.Globalization;
using System.Text;
using Packet.Sdl.Interpreter;

namespace Packet.Sdl.Explorer;

/// <summary>Which of the two stations.</summary>
public enum Station
{
    A,
    B,
}

/// <summary>
/// The composed two-station state: both machines, the two channel queues,
/// the owed link-multiplexer confirms, the remaining fault budget, and the
/// delivery/acknowledgement bookkeeping the cross-station invariants need.
/// Treated as immutable once built: successors are produced with
/// <c>with</c> plus cloned machines.
/// </summary>
public sealed record SystemState
{
    public required DataLinkMachine A { get; init; }
    public required DataLinkMachine B { get; init; }

    /// <summary>Frames in flight from A to B, head first.</summary>
    public IReadOnlyList<Frame> ToB { get; init; } = Array.Empty<Frame>();

    /// <summary>Frames in flight from B to A, head first.</summary>
    public IReadOnlyList<Frame> ToA { get; init; } = Array.Empty<Frame>();

    /// <summary>Station A has issued LM_seize_request and is owed an LM_SEIZE_confirm.</summary>
    public bool SeizeOwedA { get; init; }

    public bool SeizeOwedB { get; init; }

    public int Budget { get; init; }

    /// <summary>Number of B's frames station A has delivered upward, in order (checked incrementally).</summary>
    public int DeliveredAtA { get; init; }

    /// <summary>Number of A's frames station B has delivered upward, in order.</summary>
    public int DeliveredAtB { get; init; }

    /// <summary>Absolute (unwrapped) count of A's frames that A has seen acknowledged.</summary>
    public int AckedA { get; init; }

    public int AckedB { get; init; }

    /// <summary>Station A's layer 3 has issued DL_FLOW_OFF_request and not yet DL_FLOW_ON_request.</summary>
    public bool FlowOffA { get; init; }

    public bool FlowOffB { get; init; }

    /// <summary>Completed FLOW_OFF/FLOW_ON rounds at station A (bounded by the scenario).</summary>
    public int FlowRoundsA { get; init; }

    public int FlowRoundsB { get; init; }

    /// <summary>The v2.0 stub playing station B, or null when B is the table-driven machine <see cref="B"/>.</summary>
    public V20Peer? StubPeer { get; init; }

    public DataLinkMachine Machine(Station s) => s == Station.A ? A : B;

    public bool FlowOff(Station s) => s == Station.A ? FlowOffA : FlowOffB;

    public int FlowRounds(Station s) => s == Station.A ? FlowRoundsA : FlowRoundsB;

    /// <summary>Channel carrying frames INTO <paramref name="s"/>.</summary>
    public IReadOnlyList<Frame> Incoming(Station s) => s == Station.A ? ToA : ToB;

    /// <summary>Channel carrying frames OUT OF <paramref name="s"/>.</summary>
    public IReadOnlyList<Frame> Outgoing(Station s) => s == Station.A ? ToB : ToA;

    public bool SeizeOwed(Station s) => s == Station.A ? SeizeOwedA : SeizeOwedB;

    public int DeliveredAt(Station s) => s == Station.A ? DeliveredAtA : DeliveredAtB;

    public int Acked(Station s) => s == Station.A ? AckedA : AckedB;

    public static Station Peer(Station s) => s == Station.A ? Station.B : Station.A;

    /// <summary>Canonical serialisation for the visited set.</summary>
    public string Fingerprint()
    {
        var sb = new StringBuilder(256);
        AppendMachine(sb, A);
        sb.Append('|');
        AppendMachine(sb, B);
        sb.Append("|ab:");
        foreach (var f in ToB) sb.Append(f.Render()).Append(',');
        sb.Append("|ba:");
        foreach (var f in ToA) sb.Append(f.Render()).Append(',');
        sb.Append("|o").Append(SeizeOwedA ? '1' : '0').Append(SeizeOwedB ? '1' : '0')
          .Append("|b").Append(Inv(Budget))
          .Append("|d").Append(Inv(DeliveredAtA)).Append(',').Append(Inv(DeliveredAtB))
          .Append("|k").Append(Inv(AckedA)).Append(',').Append(Inv(AckedB))
          .Append("|f").Append(FlowOffA ? '1' : '0').Append(FlowOffB ? '1' : '0').Append(Inv(FlowRoundsA)).Append(',').Append(Inv(FlowRoundsB));
        if (StubPeer is not null) sb.Append("|p").Append(StubPeer.Fingerprint());
        return sb.ToString();
    }

    /// <summary>One-line summary of one machine's key variables, for counterexample logs.</summary>
    public static string Summary(DataLinkMachine m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var sb = new StringBuilder(m.State)
            .Append(" vs=").Append(Inv(m.Vs)).Append(" va=").Append(Inv(m.Va)).Append(" vr=").Append(Inv(m.Vr))
            .Append(" rc=").Append(Inv(m.Rc))
            .Append(" t1=").Append(m.T1.ToString().ToLowerInvariant())
            .Append(" t3=").Append(m.T3.ToString().ToLowerInvariant());
        if (m.RejectException) sb.Append(" rej_exc");
        if (m.SrejectException > 0) sb.Append(" srej_exc=").Append(Inv(m.SrejectException));
        if (m.AcknowledgePending) sb.Append(" ack_pending");
        if (m.PeerReceiverBusy) sb.Append(" peer_busy");
        if (m.OwnReceiverBusy) sb.Append(" own_busy");
        sb.Append(" q=[").Append(string.Join(",", m.Queue)).Append(']');
        if (m.SavedFrames.Count > 0)
            sb.Append(" saved={").Append(string.Join(",", m.SavedFrames.Keys.Order().Select(Inv))).Append('}');
        return sb.ToString();
    }

    /// <summary>Canonical serialisation of one machine (used to tell whether a move changed anything).</summary>
    public static string MachineFingerprint(DataLinkMachine m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var sb = new StringBuilder(128);
        AppendMachine(sb, m);
        return sb.ToString();
    }

    private static void AppendMachine(StringBuilder sb, DataLinkMachine m)
    {
        sb.Append(m.State).Append(';')
          .Append(Inv(m.Vs)).Append(',').Append(Inv(m.Vr)).Append(',').Append(Inv(m.Va)).Append(',').Append(Inv(m.Rc)).Append(';')
          .Append(Inv(m.Modulo)).Append(',').Append(Inv(m.K)).Append(',').Append(Inv(m.N2)).Append(';')
          .Append(m.Version22 ? '1' : '0').Append(m.SrejEnabled ? '1' : '0').Append(m.HalfDuplex ? '1' : '0')
          .Append(m.LayerThreeInitiated ? '1' : '0').Append(m.OwnReceiverBusy ? '1' : '0').Append(m.PeerReceiverBusy ? '1' : '0')
          .Append(m.RejectException ? '1' : '0').Append(Inv(m.SrejectException)).Append(m.AcknowledgePending ? '1' : '0').Append(';')
          .Append((int)m.T1).Append((int)m.T3).Append(";q:");
        foreach (var e in m.QueueEntries)
            sb.Append(e.Label).Append('/').Append(e.OldNs is null ? "-" : Inv(e.OldNs.Value)).Append('/').Append(e.Data ?? "-").Append(',');
        sb.Append(";s:");
        foreach (var (ns, data) in m.SavedFrames.OrderBy(kv => kv.Key))
            sb.Append(Inv(ns)).Append('=').Append(data).Append(',');
        sb.Append(";r:");
        foreach (var (ns, data) in m.RetainedFrames.OrderBy(kv => kv.Key))
            sb.Append(Inv(ns)).Append('=').Append(data).Append(',');
    }

    private static string Inv(int v) => v.ToString(CultureInfo.InvariantCulture);
}
