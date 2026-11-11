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

    /// <summary>With <see cref="ExplorerOptions.DataAfterConfirm"/>: station A's layer 3 believes the link is up (set by DL_CONNECT_confirm or DL_CONNECT_indication, or from the seed with <see cref="ExplorerOptions.DataBeforeConfirm"/>; cleared by DL_DISCONNECT_indication or DL_DISCONNECT_confirm).</summary>
    public bool LinkUpA { get; init; }

    public bool LinkUpB { get; init; }

    /// <summary>With <see cref="ExplorerOptions.DataAfterConfirm"/>: frames A's layer 3 has submitted so far.</summary>
    public int OfferedA { get; init; }

    public int OfferedB { get; init; }

    /// <summary>With <see cref="ExplorerOptions.Release"/>: station A's layer 3 has issued its DL_DISCONNECT_request.</summary>
    public bool ReleasedA { get; init; }

    public bool ReleasedB { get; init; }

    /// <summary>With <see cref="ExplorerOptions.Reconnect"/>: station A's layer 3 has issued its DL_CONNECT_request on a live or waiting link.</summary>
    public bool ReconnectedA { get; init; }

    public bool ReconnectedB { get; init; }

    /// <summary>With <see cref="ExplorerOptions.Reconnect"/>: station A's layer 3 is waiting for the answer to that request (DL_CONNECT_confirm or DL_DISCONNECT_indication).</summary>
    public bool ReconnectPendingA { get; init; }

    public bool ReconnectPendingB { get; init; }

    /// <summary>The last link report station A's data link gave its layer 3: 0 none yet, 1 up (DL_CONNECT_confirm or DL_CONNECT_indication), 2 down (DL_DISCONNECT_indication or DL_DISCONNECT_confirm). At rest it must match the station's state.</summary>
    public int ReportA { get; init; }

    public int ReportB { get; init; }

    /// <summary>With <see cref="ExplorerOptions.AcceptGiveUp"/>: frames dropped on either channel since station A last made progress.</summary>
    public int LossesA { get; init; }

    public int LossesB { get; init; }

    /// <summary>With <see cref="ExplorerOptions.AcceptGiveUp"/>: 0 station A never gave up, 1 it gave up honestly, 2 it gave up without the losses to justify it (sticky).</summary>
    public int GaveUpA { get; init; }

    public int GaveUpB { get; init; }

    /// <summary>With <see cref="ExplorerOptions.AcceptGiveUp"/>: station A has received a frame its peer sent after giving up (the DM, say), so A cannot be left up as if it never heard.</summary>
    public bool HeardGiveUpA { get; init; }

    public bool HeardGiveUpB { get; init; }

    /// <summary>The v2.0 stub playing station B, or null when B is the table-driven machine <see cref="B"/>.</summary>
    public V20Peer? StubPeer { get; init; }

    public DataLinkMachine Machine(Station s) => s == Station.A ? A : B;

    public bool FlowOff(Station s) => s == Station.A ? FlowOffA : FlowOffB;

    public int FlowRounds(Station s) => s == Station.A ? FlowRoundsA : FlowRoundsB;

    public bool LinkUp(Station s) => s == Station.A ? LinkUpA : LinkUpB;

    public int Offered(Station s) => s == Station.A ? OfferedA : OfferedB;

    public bool Released(Station s) => s == Station.A ? ReleasedA : ReleasedB;

    public bool Reconnected(Station s) => s == Station.A ? ReconnectedA : ReconnectedB;

    public bool ReconnectPending(Station s) => s == Station.A ? ReconnectPendingA : ReconnectPendingB;

    public int Report(Station s) => s == Station.A ? ReportA : ReportB;

    public int Losses(Station s) => s == Station.A ? LossesA : LossesB;

    public int GaveUp(Station s) => s == Station.A ? GaveUpA : GaveUpB;

    public bool HeardGiveUp(Station s) => s == Station.A ? HeardGiveUpA : HeardGiveUpB;

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
          .Append("|f").Append(FlowOffA ? '1' : '0').Append(FlowOffB ? '1' : '0').Append(Inv(FlowRoundsA)).Append(',').Append(Inv(FlowRoundsB))
          .Append("|l").Append(LinkUpA ? '1' : '0').Append(LinkUpB ? '1' : '0').Append(Inv(OfferedA)).Append(',').Append(Inv(OfferedB))
          .Append("|r").Append(ReleasedA ? '1' : '0').Append(ReleasedB ? '1' : '0')
          .Append("|g").Append(Inv(LossesA)).Append(',').Append(Inv(LossesB)).Append(',').Append(Inv(GaveUpA)).Append(',').Append(Inv(GaveUpB))
          .Append(HeardGiveUpA ? '1' : '0').Append(HeardGiveUpB ? '1' : '0')
          .Append("|c").Append(ReconnectedA ? '1' : '0').Append(ReconnectedB ? '1' : '0').Append(ReconnectPendingA ? '1' : '0').Append(ReconnectPendingB ? '1' : '0')
          .Append("|u").Append(Inv(ReportA)).Append(Inv(ReportB));
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
