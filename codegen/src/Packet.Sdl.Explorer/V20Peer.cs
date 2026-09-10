namespace Packet.Sdl.Explorer;

/// <summary>
/// A hand-written stub of an AX.25 v2.0 (October 1984) station in the
/// connect phase, standing in for station B. It is not table-driven: the
/// v2.2 figures cannot play a peer that does not know SABME, and the point
/// of the connect-phase seeds is what a v2.2 initiator does when its peer
/// is one. The stub is the environment, so no invariant is checked on it.
/// </summary>
/// <remarks>
/// <para>Rules, each with the 1984 text that settles it (spec-sdl/v2.0/):</para>
/// <list type="bullet">
/// <item>SABME is a control field a v2.0 station does not know. In the
/// <see cref="PeerKind.V20Frmr"/> variant it answers FRMR with F = P:
/// §2.3.4.3.3.1 "The FRMR response frame is sent to report that the
/// receiver of a frame cannot successfully process that frame ... 1. The
/// reception of an invalid or not implemented command or response frame",
/// with "an invalid or not implemented command or response is defined as a
/// frame with a control field that is unknown to the receiver". That is
/// the trigger figc4.6 t14 is drawn for (v2.2 §4.3.3.9, §6.3.2).</item>
/// <item>In the <see cref="PeerKind.V20Dm"/> variant it answers DM with
/// F = 1 instead: §2.3.4.3.5 "While a DXE is in the disconnected mode, it
/// will respond to any command other than a SABM or UI frame with a DM
/// response with the P/F bit set to 1" (also §2.4.3.4.3). Both readings
/// are legal v2.0 behaviour; XRouter takes the DM one on the wire
/// (packethacking/ax25spec#48).</item>
/// <item>SABM is accepted with UA F = P and the stub is then connected at
/// modulo 8: §2.3.4.3.4 "The UA response frame is sent to acknowledge the
/// reception and acceptance of a SABM or DISC command frame", §2.4.3.4.2.</item>
/// <item>DISC: UA F = P when connected (§2.3.4.3.4), DM F = 1 when not
/// (§2.4.3.4.1). Any other command while disconnected: DM F = 1
/// (§2.3.4.3.5). A supervisory command with P = 1 while connected is
/// answered RR F = 1, N(r) = 0 (§2.4.2, the P/F bit procedure); with P = 0
/// nothing is sent (no T2 delayed acknowledgement is modelled). UI frames
/// and every response are ignored.</item>
/// <item>No data phase: an I frame is an error, and the connect-phase seeds
/// enforce zero frames each way. The scenario therefore ends at "link
/// established with modulo 8", which is the quiescent condition for the
/// stub; this was chosen over a minimal RR-acknowledging data phase because
/// it is smaller and the defects under test are all in the connect phase.</item>
/// <item>The stub has no timers and never sends unsolicited frames, so it
/// adds no moves of its own; the only bookkeeping is whether it is
/// connected.</item>
/// </list>
/// </remarks>
public sealed record V20Peer(PeerKind Kind, bool Connected)
{
    /// <summary>One-line summary for the counterexample log.</summary>
    public string Summary() =>
        (Kind == PeerKind.V20Dm ? "v2.0 peer (refuses SABME with DM) " : "v2.0 peer (refuses SABME with FRMR) ")
        + (Connected ? "connected, modulo 8" : "disconnected");

    /// <summary>Canonical serialisation for the visited set.</summary>
    public string Fingerprint() => (Kind == PeerKind.V20Dm ? "dm" : "frmr") + (Connected ? "1" : "0");

    /// <summary>
    /// Receive one frame: the next stub state, the frames it sends back (in
    /// order), and the rule that fired (reported as the step's transition).
    /// </summary>
    /// <exception cref="InvalidOperationException">The frame is outside the stub's rule set (an I frame).</exception>
    public (V20Peer Next, IReadOnlyList<Frame> Responses, string Rule) Receive(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var none = Array.Empty<Frame>();

        if (!frame.Command)
            return (this, none, "v20:response_ignored");

        switch (frame.Type)
        {
            case "SABME":
                return Kind == PeerKind.V20Dm
                    ? (this, new[] { new Frame("DM", Command: false, Pf: true, Nr: null, Ns: null, Data: null) }, "v20:sabme_is_not_sabm_dm_f1 (§2.3.4.3.5)")
                    : (this, new[] { new Frame("FRMR", Command: false, Pf: frame.Pf, Nr: null, Ns: null, Data: null) }, "v20:sabme_unknown_control_field_frmr (§2.3.4.3.3.1)");
            case "SABM":
                return (this with { Connected = true }, new[] { new Frame("UA", Command: false, Pf: frame.Pf, Nr: null, Ns: null, Data: null) }, "v20:sabm_accepted_ua (§2.3.4.3.4)");
            case "DISC":
                return Connected
                    ? (this with { Connected = false }, new[] { new Frame("UA", Command: false, Pf: frame.Pf, Nr: null, Ns: null, Data: null) }, "v20:disc_ua (§2.3.4.3.4)")
                    : (this, new[] { new Frame("DM", Command: false, Pf: true, Nr: null, Ns: null, Data: null) }, "v20:disc_while_disconnected_dm_f1 (§2.4.3.4.1)");
            case "UI":
                return (this, none, "v20:ui_ignored");
            case "I":
                throw new InvalidOperationException("the v2.0 stub peer has no data phase: it received an I frame (the connect-phase seeds require --frames-ab 0)");
            case "RR" or "RNR" or "REJ":
                if (!Connected)
                    return (this, new[] { new Frame("DM", Command: false, Pf: true, Nr: null, Ns: null, Data: null) }, "v20:command_while_disconnected_dm_f1 (§2.3.4.3.5)");
                return frame.Pf
                    ? (this, new[] { new Frame("RR", Command: false, Pf: true, Nr: 0, Ns: null, Data: null) }, "v20:poll_answered_rr_f1 (§2.4.2)")
                    : (this, none, "v20:supervisory_p0_ignored");
            default:
                // SREJ, XID, TEST: control fields v2.0 does not know.
                return Connected
                    ? (this, new[] { new Frame("FRMR", Command: false, Pf: frame.Pf, Nr: null, Ns: null, Data: null) }, "v20:unknown_control_field_frmr (§2.3.4.3.3.1)")
                    : (this, new[] { new Frame("DM", Command: false, Pf: true, Nr: null, Ns: null, Data: null) }, "v20:command_while_disconnected_dm_f1 (§2.3.4.3.5)");
        }
    }
}
