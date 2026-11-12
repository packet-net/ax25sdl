using System.Globalization;
using Packet.Sdl.Explorer;

namespace Packet.Sdl.TlaCheck;

/// <summary>Which JSON tables a scenario runs against.</summary>
public enum TableSetKind
{
    /// <summary><c>spec/json</c> on the branch.</summary>
    Current,
    /// <summary>The explorer fixture <c>tables-pre40</c> (ax25spec abd46b0: #38 fixed, no #40 guard).</summary>
    Pre40,
    /// <summary>The explorer fixture <c>tables-pre38</c> (ax25spec fb727db: SREJ go-back-N, no #40 guard).</summary>
    Pre38,
    /// <summary>A seeded-defect fixture (fixtures/tables-ack-broken): the in-sequence I-frame arms acknowledge without delivering.</summary>
    AckBroken,
    /// <summary>A seeded-defect fixture (fixtures/tables-livelock): in-sequence I frames are discarded and TimerRecovery never counts RC up.</summary>
    Livelock,
    /// <summary>A seeded-defect fixture (fixtures/tables-release-stuck): Connected answers DISC with UA but stays Connected.</summary>
    ReleaseStuck,
    /// <summary>A seeded-defect fixture (fixtures/tables-dm-ignored): Connected ignores a DM and stays up.</summary>
    DmIgnored,
    /// <summary>A seeded-defect fixture (fixtures/tables-reconnect-swallowed): Connected ignores DL-CONNECT Request.</summary>
    ReconnectSwallowed,
    /// <summary>A seeded-defect fixture (fixtures/tables-confirm-as-disconnect): AwaitingConnection reports a completed connect as DL-DISCONNECT Indication.</summary>
    ConfirmAsDisconnect,
    /// <summary>A seeded-defect fixture (fixtures/tables-rnr-ignored): an RNR in Connected does not set peer receiver busy.</summary>
    RnrIgnored,
    /// <summary>A seeded-defect fixture (fixtures/tables-busy-delivers): Connected's own-busy test on I frames reads peer receiver busy.</summary>
    BusyDelivers,
    /// <summary>A seeded-defect fixture (fixtures/tables-busy-says-rr): a busy station answers a poll with RR.</summary>
    BusySaysRr,
    /// <summary>A seeded-defect fixture (fixtures/tables-enquiry-says-rr): a busy station's T1 poll is RR, not RNR (Transmit_Enquiry).</summary>
    EnquirySaysRr,
    /// <summary>A seeded-defect fixture (fixtures/tables-ui-swallowed): Connected swallows a UI command with P = 0.</summary>
    UiSwallowed,
    /// <summary>A seeded-defect fixture (fixtures/tables-ui-unsent): DL-UNIT-DATA Request in Connected sends nothing.</summary>
    UiUnsent,
    /// <summary>A seeded-defect fixture (fixtures/tables-ui-poll-unanswered): Connected does not answer a UI poll.</summary>
    UiPollUnanswered,
    /// <summary>A seeded-defect fixture (fixtures/tables-ui-response-delivered): UI Check delivers a UI response.</summary>
    UiResponseDelivered,
    /// <summary>A seeded-defect fixture (fixtures/tables-ui-arm-deleted): Timer Recovery has no UI arm, so a UI falls to its lower-layer catch-all.</summary>
    UiArmDeleted,
    /// <summary>A seeded-defect fixture (fixtures/tables-t3-dead): Connected has no T3 expiry arm.</summary>
    T3Dead,
    /// <summary>A seeded-defect fixture (fixtures/tables-no-keepalive): neither end of a cold connect starts T3.</summary>
    NoKeepalive,
    /// <summary>A seeded-defect fixture (fixtures/tables-frame-error-unreported): Connected's control field error arm (version 2.0) does not report DL-ERROR (L).</summary>
    FrameErrorUnreported,
    /// <summary>A seeded-defect fixture (fixtures/tables-frame-error-arms-deleted): Timer Recovery has no frame-error arms, so a corrupted frame falls to its lower-layer catch-all.</summary>
    FrameErrorArmsDeleted,
    /// <summary>A seeded-defect fixture (fixtures/tables-reset-unconfirmed): AwaitingConnection does not confirm a connect layer 3 initiated.</summary>
    ResetUnconfirmed,
    /// <summary>A seeded-defect fixture (fixtures/tables-rr-resets): an in-window RR in Connected resets the link.</summary>
    RrResets,
    /// <summary>A seeded-defect fixture (fixtures/tables-reset-wrong-code): Connected reports a reset by the peer (SABM, V(s) != V(a)) as DL-ERROR (A), not (F).</summary>
    ResetWrongCode,
    /// <summary>A seeded-defect fixture (fixtures/tables-injected-delivered): Connected delivers an I frame outside the window (P = 1), and delivers an in-sequence one with P = 1 twice.</summary>
    InjectedDelivered,
    /// <summary>A seeded-defect fixture (fixtures/tables-injected-undelivered): Connected accepts an in-sequence I frame with P = 1 without delivering it.</summary>
    InjectedUndelivered,
    /// <summary>A seeded-defect fixture (fixtures/tables-disconnect-swallowed): neither connect page saves DL-DISCONNECT Request, so it falls to the upper-layer catch-all.</summary>
    DisconnectSwallowed,
    /// <summary>A seeded-defect fixture (fixtures/tables-disconnect-released-early): Awaiting Connection does not save DL-DISCONNECT Request, so the v2.2 to v2.0 fallback releases it early.</summary>
    DisconnectReleasedEarly,
    /// <summary>A seeded-defect fixture (fixtures/tables-disconnect-unconfirmed): DL-DISCONNECT Request in Disconnected is not confirmed.</summary>
    DisconnectUnconfirmed,
    /// <summary>A seeded-defect fixture (fixtures/tables-disconnect-silent): DL-DISCONNECT Request in Connected confirms and goes Disconnected without sending DISC.</summary>
    DisconnectSilent,
    /// <summary>A seeded-defect fixture (fixtures/tables-disconnect-kept-connected): DL-DISCONNECT Request in Connected stays Connected, so a released request is lost.</summary>
    DisconnectKeptConnected,
    /// <summary>The current tables with ax25spec#169 reverted (fixtures/tables-169-reverted): Timer Recovery's SABM and SABME arms with V(s) = V(a) skip the reset box.</summary>
    Reverted169,
}

/// <summary>
/// A row's expected verdict, as TlaCheck enforces it: the kind (<c>clean</c>,
/// <c>bound</c>, or an <see cref="Invariants"/> name), and where the row
/// states them, the station, the counterexample length (the explorer's
/// steps; for a few rows, such as d38-*, d42-rc-off, d47-rc-on and
/// d40-pre40-rej-eq-vs, TlaCheck is the only place that pins it) and the number of distinct
/// states. <see cref="Label"/> is the text a reader sees.
/// </summary>
public sealed record Expectation(string Kind, Station? At = null, int? Steps = null, long? States = null, string? Note = null)
{
    public string Label =>
        Kind switch { "clean" => "clean", "bound" => "depth bound hit", _ => Kind }
        + (At is { } at ? " at " + at : "")
        + (Steps is { } n ? ", " + n.ToString(CultureInfo.InvariantCulture) : "")
        + (States is { } st ? ", " + st.ToString(CultureInfo.InvariantCulture) + " states" : "")
        + (Note is { } note ? " (" + note + ")" : "");

    /// <summary>Where the explorer's verdict differs from this expectation, or null where it meets it.</summary>
    public string? Mismatch(string kind, string station, int steps, long states)
    {
        var why = new List<string>();
        if (kind != Kind) why.Add($"verdict {kind}");
        else
        {
            if (At is { } at && station != at.ToString()) why.Add($"station '{station}'");
            if (Steps is { } n && steps != n) why.Add($"length {steps.ToString(CultureInfo.InvariantCulture)}");
            if (States is { } st && states != st) why.Add($"{states.ToString(CultureInfo.InvariantCulture)} states");
        }
        return why.Count == 0 ? null : $"expected {Label}, got {string.Join(", ", why)}";
    }
}

/// <summary>One cross-check row: a named explorer scenario and what the calibration suite says it shows.</summary>
/// <param name="Expect">The expected verdict, enforced by the cross-check; null on the grid, whose cells have none.</param>
/// <param name="DepthBound">The row relies on the explorer's depth bound (an infinite space); TLC applies the same bound as a state constraint.</param>
public sealed record Scenario(string Name, string Defect, TableSetKind Tables, ExplorerOptions Options, Expectation? Expect, bool DepthBound = false);

/// <summary>
/// The explorer's calibration suite (codegen/tests/Packet.Sdl.Explorer.Tests/CalibrationTests.cs),
/// scenario for scenario, plus the controls the suite runs beside them. The
/// options are the suite's own; only the tables directory is resolved here.
/// </summary>
public static class Scenarios
{
    private static readonly Invariants Default = Invariants.Default;

    private static Expectation Clean(long? states = null, string? note = null) => new("clean", States: states, Note: note);
    private static Expectation Bound(string? note = null) => new("bound", Note: note);
    private static Expectation Fails(Invariants kind, Station? at = null, int? steps = null, string? note = null) => new(kind.ToString(), at, steps, Note: note);

    public static IReadOnlyList<Scenario> Calibration(Func<TableSetKind, string> dir)
    {
        ExplorerOptions O(TableSetKind t) => new() { TablesDir = dir(t) };
        var list = new List<Scenario>();
        void Add(string name, string defect, TableSetKind t, Func<ExplorerOptions, ExplorerOptions> with, Expectation expect, bool depthBound = false) =>
            list.Add(new Scenario(name, defect, t, with(O(t)), expect, depthBound));

        // #40: out-of-window duplicate raises an unclearable exception.
        ExplorerOptions S40(ExplorerOptions o, bool srej) => o with { FramesAb = 2, FramesBa = 0, K = 4, Srej = srej, Budget = 1, Faults = FaultKinds.Duplicate };
        Add("d40-pre40-srej", "#40", TableSetKind.Pre40, o => S40(o, true), Fails(Invariants.RejectCoherence, Station.A, 4));
        Add("d40-pre40-nosrej", "#40", TableSetKind.Pre40, o => S40(o, false), Fails(Invariants.RejectCoherence, steps: 4));
        Add("d40-current-srej", "#40 fixed", TableSetKind.Current, o => S40(o, true), Clean());
        Add("d40-current-nosrej", "#40 fixed", TableSetKind.Current, o => S40(o, false), Clean());
        Add("d40-pre40-rej-eq-vs", "#40", TableSetKind.Pre40, o => S40(o, true) with { RejMayEqualVs = true }, Fails(Invariants.MachineError, Station.A, 5, "phantom retransmission"));

        // #38: documented limit, the two fixtures must give the same verdict.
        ExplorerOptions S38(ExplorerOptions o) => o with { FramesAb = 3, K = 4, Srej = true, Budget = 2, Faults = FaultKinds.Drop, Invariants = Default & ~Invariants.RejectCoherence };
        Add("d38-pre38", "#38", TableSetKind.Pre38, S38, Fails(Invariants.MachineError, Station.A, 15, "same verdict as d38-pre40"));
        Add("d38-pre40", "#38", TableSetKind.Pre40, S38, Fails(Invariants.MachineError, Station.A, 15, "same verdict as d38-pre38"));

        // #42: SREJ names the arriving frame, not the gap.
        ExplorerOptions S42(ExplorerOptions o) => o with { FramesAb = 3, FramesBa = 0, K = 4, Srej = true, Budget = 2, Faults = FaultKinds.Drop };
        Add("d42", "#42", TableSetKind.Current, S42, Fails(Invariants.RejectCoherence, Station.B, 6));
        Add("d42-rc-off", "#42", TableSetKind.Current, o => S42(o) with { Invariants = Default & ~Invariants.RejectCoherence }, Fails(Invariants.SequenceSanity, Station.B, 16, "SREJ count"));
        Add("d42-both-off", "#42", TableSetKind.Current, o => S42(o) with
        {
            Invariants = (Default | Invariants.SelectiveProgress) & ~(Invariants.RejectCoherence | Invariants.SequenceSanity),
            MaxDepth = 60,
        }, Bound("the runaway"), depthBound: true);

        // #47: TimerRecovery drain decrements V(r).
        Add("d47-rc-off", "#47", TableSetKind.Current, o => o with
        {
            FramesAb = 2, FramesBa = 1, K = 4, Srej = true, Budget = 3, Faults = FaultKinds.Drop,
            Invariants = Default & ~Invariants.RejectCoherence,
        }, Fails(Invariants.Delivery, Station.B, 20));
        Add("d47-k2", "#47", TableSetKind.Current, o => o with { FramesAb = 1, FramesBa = 3, K = 2, Srej = true, Budget = 2, Faults = FaultKinds.Drop }, Fails(Invariants.Delivery, Station.A, 24));
        Add("d47-rc-on", "#47/#42", TableSetKind.Current, o => o with { FramesAb = 2, FramesBa = 1, K = 4, Srej = true, Budget = 3, Faults = FaultKinds.Drop }, Fails(Invariants.RejectCoherence, Station.B, 16, "shorter than d47-rc-off's 20"));

        // #9: acknowledgement progress does not reset RC.
        Add("d9", "#9", TableSetKind.Current, o => o with
        {
            FramesAb = 3, FramesBa = 0, K = 4, N2 = 2, Budget = 3, Faults = FaultKinds.Drop, DropScope = DropScope.InteriorI,
        }, Fails(Invariants.Deadlock, steps: 30));

        // #13: window at half the modulus (protocol constraint).
        ExplorerOptions S13(ExplorerOptions o, int k) => o with { FramesAb = 3, FramesBa = 0, K = k, Srej = false, Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate };
        Add("d13-k7", "#13", TableSetKind.Current, o => S13(o, 7), Fails(Invariants.RejectCoherence, Station.A, 13));
        Add("d13-k4", "#13 control", TableSetKind.Current, o => S13(o, 4), Clean());

        // #44 / #48 from a cold connect, table-driven peer.
        Add("d44-48-cold", "#44/#48", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, Modulo128A = true, PeerDeclinesSabme = true, FramesAb = 1, Budget = 0,
        }, Fails(Invariants.Deadlock, steps: 2));
        Add("d44-48-cold-mod8", "#44/#48 control", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, Modulo128A = false, PeerDeclinesSabme = true, FramesAb = 0, Budget = 0,
        }, Clean());

        // ax25spec#161: figc4.2 discards data offered before DL-CONNECT
        // Confirm, as the 1998 edition of the figure (Figure C4.3) labels its
        // "Layer 3 Initiated?" decision, with no indication to layer 3.
        Add("d161-data-before-confirm", "ax25spec#161", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, Modulo128A = false, PeerDeclinesSabme = true, FramesAb = 1, Budget = 0,
        }, Fails(Invariants.Deadlock, steps: 2));

        // Cold connect, then data: each layer 3 submits only once its link is
        // up (DL_CONNECT_confirm at A, DL_CONNECT_indication at B), so #161
        // does not apply. The v2.2 connect comes up through figc4.2 (#44), and
        // B, accepting SABME, sets the version 2.2 flag but never modulo 128
        // (#54): invisible until A's V(s) passes 7, then B's N(r) wraps and A
        // takes it as an N(r) error, re-establishes and loses its queue.
        ExplorerOptions Cold(ExplorerOptions o, bool mod128) => o with { Seed = SeedKind.Disconnected, Modulo128A = mod128, DataAfterConfirm = true, Budget = 0 };
        Add("cold-v20-data", "#102 control", TableSetKind.Current, o => Cold(o, false) with
        {
            FramesAb = 2, FramesBa = 1, Invariants = Default | Invariants.SelectiveProgress,
        }, Clean());
        Add("cold-v22-data", "#44 route", TableSetKind.Current, o => Cold(o, true) with
        {
            FramesAb = 2, FramesBa = 1, Invariants = Default | Invariants.SelectiveProgress,
        }, Clean());
        Add("d54-cold-v22-wrap", "#54", TableSetKind.Current, o => Cold(o, true) with { FramesAb = 9, K = 1 }, Fails(Invariants.Deadlock, steps: 45));
        Add("d54-cold-v20-wrap", "#54 control", TableSetKind.Current, o => Cold(o, false) with { FramesAb = 9, K = 1 }, Clean());

        // #43: DL-FLOW-OFF on the wrong branch.
        ExplorerOptions S43(ExplorerOptions o) => o with { FramesAb = 2, FramesBa = 0, K = 4, Budget = 0, FlowControl = FlowControlAt.B };
        Add("d43", "#43", TableSetKind.Current, S43, Fails(Invariants.BusyTracksFlow, Station.B, 3));
        Add("d43-busy-off", "#43", TableSetKind.Current, o => S43(o) with { Invariants = Default & ~Invariants.BusyTracksFlow }, Fails(Invariants.FlowOffDelivery, Station.B, 5));
        Add("d43-both-off", "#43", TableSetKind.Current, o => S43(o) with { Invariants = Default & ~(Invariants.BusyTracksFlow | Invariants.FlowOffDelivery) }, Clean());

        // #45 / #48 against the v2.0 stub peer.
        ExplorerOptions V20(ExplorerOptions o, SeedKind seed, PeerKind peer, bool timerFree) => o with
        {
            Seed = seed, Modulo128A = seed == SeedKind.Disconnected, Peer = peer, FramesAb = 0, Budget = 0,
            Invariants = timerFree ? Default | Invariants.SelectiveProgress : Default,
        };
        Add("d45-cold-timer-free", "#44 masks #45", TableSetKind.Current, o => V20(o, SeedKind.Disconnected, PeerKind.V20Frmr, true), Fails(Invariants.SelectiveProgress));
        Add("d45-cold", "#44 masks #45", TableSetKind.Current, o => V20(o, SeedKind.Disconnected, PeerKind.V20Frmr, false), Clean());
        Add("d45-v22-timer-free", "#45", TableSetKind.Current, o => V20(o, SeedKind.AwaitingV22Connection, PeerKind.V20Frmr, true), Fails(Invariants.SelectiveProgress));
        Add("d45-v22", "#45", TableSetKind.Current, o => V20(o, SeedKind.AwaitingV22Connection, PeerKind.V20Frmr, false), Clean());
        Add("d48-v22-dm", "#48", TableSetKind.Current, o => V20(o, SeedKind.AwaitingV22Connection, PeerKind.V20Dm, false), Fails(Invariants.Deadlock, steps: 2));
        Add("d48-v22-tables", "#48", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.AwaitingV22Connection, PeerDeclinesSabme = true, FramesAb = 0, Budget = 0,
        }, Fails(Invariants.Deadlock));
        Add("d48-cold-dm", "#48", TableSetKind.Current, o => V20(o, SeedKind.Disconnected, PeerKind.V20Dm, false), Fails(Invariants.Deadlock, steps: 2));
        Add("v20-mod8-frmr", "#45/#48 control", TableSetKind.Current, o => V20(o, SeedKind.Disconnected, PeerKind.V20Frmr, true) with { Modulo128A = false }, Clean());
        Add("v20-mod8-dm", "#45/#48 control", TableSetKind.Current, o => V20(o, SeedKind.Disconnected, PeerKind.V20Dm, true) with { Modulo128A = false }, Clean());
        Add("v22-peer-ua", "#45/#48 control", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.AwaitingV22Connection, FramesAb = 0, Budget = 0, Invariants = Default | Invariants.SelectiveProgress,
        }, Clean());

        // Hypothesis H1 (docs/explorer.md): stuck in TimerRecovery.
        ExplorerOptions H1(ExplorerOptions o) => o with { FramesAb = 2, FramesBa = 1, K = 4, Srej = false, Budget = 2, Faults = FaultKinds.Drop };
        Add("h1", "H1", TableSetKind.Current, H1, Fails(Invariants.Deadlock, steps: 19));
        Add("h1-tr-quiescent", "H1 control", TableSetKind.Current, o => H1(o) with { TimerRecoveryIsQuiescent = true }, Clean());

        // Coverage batch 1 (ax25spec#116, Phase 2): existing moves, new
        // scenarios, chosen to fire table rows no earlier row reaches. A
        // deadlock row explores its whole graph before reporting, so it fires
        // every reachable row even though its verdict is the shortest
        // deadlock.
        //
        // A duplicated UA reaches a station that is already Connected: figc4.4
        // t17 resets the link (#114) and the reset SABM makes the peer discard
        // its queue (#50), so both stations' data is lost. At modulo 8 with
        // version 2.2 set, and at modulo 128.
        ExplorerOptions Dup114(ExplorerOptions o, bool mod128) => o with
        {
            Seed = SeedKind.Disconnected, Modulo128A = mod128, DataAfterConfirm = true,
        };
        Add("d114-cold-dup-ua", "#114/#50", TableSetKind.Current, o => Dup114(o, false) with
        {
            FramesAb = 1, FramesBa = 1, Srej = true, Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate,
        }, Fails(Invariants.Deadlock, steps: 7));
        Add("d114-cold-dup-ua-mod128", "#114/#50", TableSetKind.Current, o => Dup114(o, true) with
        {
            FramesAb = 0, FramesBa = 1, N2 = 2, Budget = 1, Faults = FaultKinds.Duplicate,
        }, Fails(Invariants.Deadlock, steps: 6));
        // The #44/#48 cold connect with B's data after its indication and
        // drops and duplicates: the same two-step deadlock, a wider graph.
        Add("d44-48-cold-faults", "#44/#48", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, Modulo128A = true, PeerDeclinesSabme = true, DataAfterConfirm = true,
            FramesAb = 0, FramesBa = 1, Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate,
        }, Fails(Invariants.Deadlock, steps: 2));
        // #44 masks #45 with N2 = 2: the swallowed FRMR costs one try, one
        // dropped SABM the other, and figc4.2 gives up.
        Add("d45-cold-n2", "#44 masks #45", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, Modulo128A = true, Peer = PeerKind.V20Frmr, FramesAb = 0, N2 = 2, Budget = 1, Faults = FaultKinds.Drop,
        }, Fails(Invariants.Deadlock, steps: 5));
        Add("v20-frmr-dup", "#44/#45 control", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, Modulo128A = true, Peer = PeerKind.V20Frmr, FramesAb = 0, Budget = 2, Faults = FaultKinds.Duplicate,
        }, Clean());
        // #91 (hypothesis H1) at k = 1 and at k = 2, reaching the figc4.5
        // I-frame arms the h1 row does not.
        Add("h1-k1", "#91 (H1)", TableSetKind.Current, o => o with
        {
            FramesAb = 1, FramesBa = 2, K = 1, Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate,
        }, Fails(Invariants.Deadlock, steps: 15));
        Add("h1-k2-ab3", "#91 (H1)", TableSetKind.Current, o => o with
        {
            FramesAb = 3, FramesBa = 1, K = 2, Budget = 2, Faults = FaultKinds.Drop,
        }, Fails(Invariants.Deadlock, steps: 17));

        // The orderly release (ExplorerOptions.Release): layer 3 issues
        // DL_DISCONNECT_request on an established link once everything is
        // delivered; quiescence is then both stations Disconnected.
        Add("release-a", "release control", TableSetKind.Current, o => o with { FramesAb = 2, Release = ReleaseBy.A }, Clean());
        Add("release-both-cold", "release", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, FramesAb = 0, Srej = true, Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate, Release = ReleaseBy.Both,
        }, Clean());
        Add("release-a-tr", "release, masks #91", TableSetKind.Current, o => o with
        {
            FramesAb = 1, FramesBa = 2, K = 1, Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate, Release = ReleaseBy.A,
        }, Clean());

        // Coverage batch 2: the give-up rule (ExplorerOptions.AcceptGiveUp). A
        // station that exhausts N2 after enough losses since its last progress
        // has given up honestly; a settled honest give-up with everything
        // delivered is rest, one that lost data is reported as GiveUpLoss, and
        // an unjustified give-up never rests.
        Add("gu-v22", "give-up control", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.AwaitingV22Connection, FramesAb = 0, N2 = 2, Budget = 2, Faults = FaultKinds.Drop, AcceptGiveUp = true,
        }, Clean());
        Add("gu-release", "give-up control", TableSetKind.Current, o => o with
        {
            FramesAb = 0, N2 = 1, Budget = 2, Faults = FaultKinds.Drop, Release = ReleaseBy.A, AcceptGiveUp = true,
        }, Clean());
        Add("gu-loss", "give-up loss control", TableSetKind.Current, o => o with
        {
            FramesAb = 0, FramesBa = 1, N2 = 1, Budget = 2, Faults = FaultKinds.Drop, Release = ReleaseBy.A, AcceptGiveUp = true,
        }, Fails(Invariants.GiveUpLoss, steps: 6));
        // The rule does not excuse #9: a give-up after the acknowledgements
        // progressed is unjustified, so the link dying stays a deadlock.
        Add("d9-give-up", "#9", TableSetKind.Current, o => o with
        {
            FramesAb = 3, FramesBa = 0, K = 4, N2 = 2, Budget = 3, Faults = FaultKinds.Drop, DropScope = DropScope.InteriorI, AcceptGiveUp = true,
        }, Fails(Invariants.Deadlock, steps: 30));
        Add("d9-n1-release", "#9, #91", TableSetKind.Current, o => o with
        {
            FramesAb = 0, FramesBa = 1, N2 = 1, Budget = 1, Faults = FaultKinds.Drop, Release = ReleaseBy.A, AcceptGiveUp = true,
        }, Fails(Invariants.Deadlock, steps: 11));

        // k = 7 with SREJ: a reject naming V(s) (the #13 regime, above half
        // the modulus) reaches the figc4.5 REJ and SREJ arms for N(r) = V(s).
        // With reject coherence on, that reject is the #13 verdict; off, the
        // run is clean.
        Add("k7-srej-rc-off", "#13 regime", TableSetKind.Current, o => o with
        {
            FramesAb = 2, K = 7, Srej = true, Budget = 2, Faults = FaultKinds.Drop, Invariants = Default & ~Invariants.RejectCoherence,
        }, Clean());

        // Reordering (off by default: one channel does not reorder; ax25spec#95
        // is the verification item for it). A reordered frame can carry an
        // N(r) the receiver has already passed, which figc4.4 and figc4.5 treat
        // as an N(r) error. Reject coherence presupposes a FIFO channel, so the
        // rows that reorder rejects run with it off.
        Add("ro-k1", "reorder control", TableSetKind.Current, o => o with
        {
            FramesAb = 1, K = 1, Budget = 3, Faults = FaultKinds.Reorder | FaultKinds.Drop | FaultKinds.Duplicate,
        }, Clean());
        Add("ro-k1-srej", "reorder control", TableSetKind.Current, o => o with
        {
            FramesAb = 1, K = 1, Srej = true, Budget = 3, Faults = FaultKinds.Reorder | FaultKinds.Drop | FaultKinds.Duplicate,
        }, Clean());
        Add("ro-srej-rc-off", "reorder control", TableSetKind.Current, o => o with
        {
            FramesAb = 2, K = 2, Srej = true, Budget = 2, Faults = FaultKinds.Reorder, Invariants = Default & ~Invariants.RejectCoherence,
        }, Clean());
        Add("ro-stale-rej", "ax25spec#95", TableSetKind.Current, o => o with
        {
            FramesAb = 2, K = 2, Budget = 3, Faults = FaultKinds.Reorder | FaultKinds.Drop, Invariants = Default & ~Invariants.RejectCoherence,
        }, Fails(Invariants.Deadlock, steps: 11));
        Add("ro-stale-i", "ax25spec#95", TableSetKind.Current, o => o with
        {
            FramesAb = 1, FramesBa = 2, K = 1, Budget = 2, Faults = FaultKinds.Reorder | FaultKinds.Drop,
        }, Fails(Invariants.Deadlock, steps: 9));
        Add("ro-reset-dup", "ax25spec#95", TableSetKind.Current, o => o with
        {
            FramesAb = 2, K = 2, Budget = 4, Faults = FaultKinds.Reorder | FaultKinds.Drop, Invariants = Default & ~Invariants.RejectCoherence,
        }, Fails(Invariants.Delivery, Station.B, 18));
        Add("ro-d42", "#42", TableSetKind.Current, o => o with
        {
            FramesAb = 2, K = 2, Srej = true, Budget = 4, Faults = FaultKinds.Reorder | FaultKinds.Drop, Invariants = Default & ~Invariants.RejectCoherence,
        }, Fails(Invariants.SequenceSanity, Station.B, 16));

        // Coverage batch 3: layer 3 primitives. Upper-layer primitives with no
        // arm on the page go to its all_other_primitives__from_upper_layer
        // catch-all, and a layer 3 that is told its link is down stops
        // submitting until the link is reported up again. This scenario was a
        // MachineError on main (A's layer 3 submitted into Disconnected at
        // step 15); now the whole graph is explored.
        Add("l3-down-mod128", "#114/#50 (was a MachineError)", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, Modulo128A = true, DataAfterConfirm = true, FramesAb = 1, FramesBa = 1,
            Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate,
        }, Fails(Invariants.Deadlock, steps: 7));
        // The same with a third fault: the path on which A's link goes down
        // with a0 held and comes back on B's retried SABME (the explorer's
        // replay test). The verdict is the same deadlock, but the state count
        // depends on layer 3 stopping on the disconnect: with the link-up
        // flag left set in TwoStation.tla alone, TLC finds 12384 states
        // against the explorer's 12394. TlaCheck compares the counts on
        // deadlock rows, so this row pins the rule in both tools.
        Add("l3-down-mod128-b3", "#114/#50 (layer 3 link-down pin)", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, Modulo128A = true, DataAfterConfirm = true, FramesAb = 1, FramesBa = 1,
            Budget = 3, Faults = FaultKinds.Drop | FaultKinds.Duplicate,
        }, Fails(Invariants.Deadlock, steps: 7));

        // DL_CONNECT_request on a live or waiting link (ExplorerOptions.Reconnect),
        // once everything is delivered; the layer 3 must then get its confirm
        // (or a DL_DISCONNECT_indication).
        Add("reconnect-a", "reconnect control", TableSetKind.Current, o => o with { FramesAb = 2, Reconnect = ReconnectBy.A }, Clean());
        Add("reconnect-both-srej", "reconnect control", TableSetKind.Current, o => o with { FramesAb = 2, Srej = true, Reconnect = ReconnectBy.Both }, Clean());
        Add("reconnect-cold", "reconnect control", TableSetKind.Current, o => o with { Seed = SeedKind.Disconnected, FramesAb = 0, Reconnect = ReconnectBy.A }, Clean());
        Add("reconnect-v22", "reconnect control", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.AwaitingV22Connection, FramesAb = 0, Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate, Reconnect = ReconnectBy.A,
        }, Clean());
        Add("reconnect-both-cold", "reconnect control", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, FramesAb = 0, Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate, Reconnect = ReconnectBy.Both,
        }, Clean());
        // ax25spec#167: figc4.5's DL-CONNECT Request stays in Timer Recovery
        // (the July 1998 figure ends that column in Awaiting Connection), so
        // the UA that answers its own SABM arrives on an up link and resets it
        // (#114); the reset clears Layer 3 Initiated and the layer 3 never
        // gets a confirm.
        Add("reconnect-tr", "ax25spec#167 (shows through #114)", TableSetKind.Current, o => o with
        {
            FramesAb = 1, Budget = 1, Faults = FaultKinds.Drop, Reconnect = ReconnectBy.A,
        }, Fails(Invariants.Deadlock, steps: 12));
        // A reset of the #44/#54 mod-128 link: B (version 2.2 flag, modulo 8)
        // sends SABM from Awaiting V2.2 Connection and retries with SABME, A
        // (modulo 128, flag clear) waits in Awaiting Connection and answers
        // that SABME with DM (#115's crossing), B tears down on it (#48).
        Add("reconnect-cold-mod128", "#44/#54, #115, #48", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, Modulo128A = true, FramesAb = 0, Budget = 1, Faults = FaultKinds.Drop, Reconnect = ReconnectBy.Both,
        }, Fails(Invariants.Deadlock, steps: 10));

        // DL_DISCONNECT_request while the link is being set up
        // (ExplorerOptions.ReleaseEarly). Figures C4.2 and C4.6 draw a Save
        // (Requeue); since both tools route through saves (packet-net/ax25sdl#97,
        // pinned semantic 19) the request is held and replayed once the link
        // is up. Before, it went to the catch-all and these were Deadlock, 3.
        Add("release-early-cold", "ax25sdl#97 (saved)", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, FramesAb = 0, Release = ReleaseBy.A, ReleaseEarly = true,
        }, Clean());
        Add("release-early-v22", "ax25sdl#97 (saved)", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.AwaitingV22Connection, FramesAb = 0, Release = ReleaseBy.A, ReleaseEarly = true,
        }, Clean());

        // Layer 3 abandons its own connect (ExplorerOptions.Abandon): the
        // request is held by figc4.2's or figc4.6's save, and acted on when
        // the link comes up (DISC, then the release), when the connect fails
        // (DL-DISCONNECT Confirm in Disconnected) or across the v2.2 to v2.0
        // fallback (still held in Awaiting Connection).
        Add("abandon-cold", "ax25sdl#97 save", TableSetKind.Current, o => o with { Seed = SeedKind.Disconnected, FramesAb = 0, Abandon = ReleaseBy.A }, Clean());
        Add("abandon-v22", "ax25sdl#97 save", TableSetKind.Current, o => o with { Seed = SeedKind.AwaitingV22Connection, FramesAb = 0, Abandon = ReleaseBy.A }, Clean());
        Add("abandon-v20-fallback", "ax25sdl#97 save, across the fallback", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.AwaitingV22Connection, FramesAb = 0, Peer = PeerKind.V20Frmr, Abandon = ReleaseBy.A,
        }, Clean());
        Add("abandon-gives-up", "ax25sdl#97 save, connect fails", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, FramesAb = 0, Abandon = ReleaseBy.A, N2 = 1, Budget = 1, Faults = FaultKinds.Drop, AcceptGiveUp = true,
        }, Clean());
        // A duplicated UA of the connect answers the replayed DISC in
        // Awaiting Release (figc4.3 t03 takes any UA F = 1); the DISC is then
        // lost and B stays up. A finding, recovered by T3 (the next row).
        Add("abandon-release-gives-up", "ax25sdl#97 save, release gives up", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, FramesAb = 0, Abandon = ReleaseBy.A, N2 = 1, Budget = 2, Faults = FaultKinds.Drop, AcceptGiveUp = true,
        }, Clean());
        Add("release-cold-faults", "ax25spec#99 (stale UA answers the DISC)", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, FramesAb = 0, Release = ReleaseBy.A, Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate,
        }, Fails(Invariants.Deadlock, steps: 5));
        Add("abandon-cold-faults", "ax25spec#99 (stale UA answers the DISC)", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, FramesAb = 0, Abandon = ReleaseBy.A, Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate,
        }, Fails(Invariants.Deadlock, steps: 5));
        Add("abandon-cold-faults-t3", "ax25sdl#97 save, faults, T3", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, FramesAb = 0, Abandon = ReleaseBy.A, Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate, T3 = true,
        }, Clean());
        Add("abandon-reconnect", "ax25sdl#97 save, after a reconnect", TableSetKind.Current, o => o with
        {
            FramesAb = 2, Reconnect = ReconnectBy.A, Abandon = ReleaseBy.A,
        }, Clean());
        Add("abandon-both-cold", "ax25sdl#97 save, both stations", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, FramesAb = 0, Reconnect = ReconnectBy.Both, Abandon = ReleaseBy.Both,
        }, Clean());
        // The seeded defects for the save rules (fixtures/README.md): a lost
        // disconnect (DisconnectKept), a request released before the link is
        // up, an unconfirmed request, and a release that never reaches the peer.
        Add("seeded-disconnect-swallowed", "seeded (lost disconnect)", TableSetKind.DisconnectSwallowed, o => o with
        {
            Seed = SeedKind.Disconnected, FramesAb = 0, Abandon = ReleaseBy.A,
        }, Fails(Invariants.DisconnectKept, Station.A, 1));
        Add("seeded-disconnect-released-early", "seeded (released early)", TableSetKind.DisconnectReleasedEarly, o => o with
        {
            Seed = SeedKind.AwaitingV22Connection, FramesAb = 0, Peer = PeerKind.V20Frmr, Abandon = ReleaseBy.A,
        }, Fails(Invariants.DisconnectKept, Station.A, 3));
        Add("seeded-disconnect-kept-connected", "seeded (released request lost)", TableSetKind.DisconnectKeptConnected, o => o with
        {
            Seed = SeedKind.Disconnected, FramesAb = 0, Abandon = ReleaseBy.A,
        }, Fails(Invariants.DisconnectKept, Station.A, 3));
        Add("seeded-disconnect-unconfirmed", "seeded (disconnect answer)", TableSetKind.DisconnectUnconfirmed, o => o with
        {
            Seed = SeedKind.Disconnected, FramesAb = 0, Abandon = ReleaseBy.A, N2 = 1, Budget = 1, Faults = FaultKinds.Drop, AcceptGiveUp = true,
        }, Fails(Invariants.Deadlock, steps: 3));
        Add("seeded-disconnect-silent", "seeded (abandon rest)", TableSetKind.DisconnectSilent, o => o with
        {
            Seed = SeedKind.Disconnected, FramesAb = 0, Abandon = ReleaseBy.A,
        }, Fails(Invariants.Deadlock, steps: 3));
        Add("seeded-disconnect-silent-v20", "seeded (abandon rest, v2.0 peer)", TableSetKind.DisconnectSilent, o => o with
        {
            Seed = SeedKind.AwaitingV22Connection, FramesAb = 0, Peer = PeerKind.V20Frmr, Abandon = ReleaseBy.A,
        }, Fails(Invariants.Deadlock, steps: 8));

        // Data offered before the confirm as a move (ExplorerOptions.DataBeforeConfirm):
        // a0 offered while A awaits the UA is discarded (ax25spec#161), a1
        // offered after it is delivered, and B sees the gap.
        Add("d161-move", "ax25spec#161", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, DataAfterConfirm = true, DataBeforeConfirm = true, FramesAb = 2,
        }, Fails(Invariants.Delivery, Station.B, 6));

        // Seeded defects (fixtures/README.md): no real defect fails these two
        // checks on any table set, so each has a deliberately broken table to
        // prove it can fire, in both tools.
        Add("seeded-ack", "seeded (AckCoherence)", TableSetKind.AckBroken, o => o with { FramesAb = 1 }, Fails(Invariants.AckCoherence, Station.A, 4));
        Add("seeded-livelock", "seeded (Quiescence)", TableSetKind.Livelock, o => o with { FramesAb = 1 }, Fails(Invariants.Quiescence, steps: 9));
        // The release quiescence rule: B acknowledges A's DISC but stays
        // Connected, so the link ends with one end Disconnected and the other
        // not. Both tools must call that stuck, not released.
        // The give-up rule's settled state: A gives up from TimerRecovery and
        // its DM reaches B, which ignores it and stays up. A peer that heard
        // the give-up and stayed up is not settled, in either tool.
        Add("seeded-dm-ignored", "seeded (give-up rest)", TableSetKind.DmIgnored, o => o with
        {
            FramesAb = 1, N2 = 1, Budget = 2, Faults = FaultKinds.Drop, AcceptGiveUp = true,
        }, Fails(Invariants.Deadlock));
        Add("seeded-release-stuck", "seeded (release rest)", TableSetKind.ReleaseStuck, o => o with { FramesAb = 2, Release = ReleaseBy.A }, Fails(Invariants.Deadlock));
        // The reconnect answer rule: Connected ignores DL-CONNECT Request, so
        // the link stays up and quiet but A's layer 3 never hears back. Both
        // tools must call that stuck (reconnect-a on the real tables is clean).
        Add("seeded-reconnect-swallowed", "seeded (reconnect answer)", TableSetKind.ReconnectSwallowed, o => o with { FramesAb = 2, Reconnect = ReconnectBy.A }, Fails(Invariants.Deadlock, steps: 7));
        // The report rule: a connect reported to layer 3 as DL-DISCONNECT
        // Indication while the station goes Connected. The indication would
        // otherwise answer the reconnect; at rest each layer 3's last report
        // must match its station's state, so both tools call it stuck
        // (reconnect-cold on the real tables is clean).
        Add("seeded-confirm-as-disconnect", "seeded (link report)", TableSetKind.ConfirmAsDisconnect, o => o with
        {
            Seed = SeedKind.Disconnected, FramesAb = 0, Reconnect = ReconnectBy.A,
        }, Fails(Invariants.Deadlock, steps: 3));

        // Coverage batch 4: the peer-busy family. A local busy condition
        // (ExplorerOptions.LocalBusy) is entered once with what the #43-fixed
        // DL-FLOW-OFF arm draws (own receiver busy, RNR with V(r)) and left
        // with what DL-FLOW-ON draws (RR); by default it is short (the peer's
        // T1 holds back once RC reaches N2 - 1).
        Add("busy-b", "peer-busy control", TableSetKind.Current, o => o with { FramesAb = 2, LocalBusy = FlowControlAt.B }, Clean());
        Add("busy-a-ba2", "peer-busy control", TableSetKind.Current, o => o with { FramesAb = 1, FramesBa = 2, LocalBusy = FlowControlAt.A }, Clean());
        Add("busy-both-trq", "peer-busy control (behind #91)", TableSetKind.Current, o => o with
        {
            FramesAb = 1, FramesBa = 1, LocalBusy = FlowControlAt.Both, TimerRecoveryIsQuiescent = true,
        }, Clean());
        Add("busy-b-b1", "#91 (after a busy period)", TableSetKind.Current, o => o with { FramesAb = 2, LocalBusy = FlowControlAt.B, Budget = 1 }, Fails(Invariants.Deadlock, steps: 21));
        Add("busy-b-long", "ax25spec#168", TableSetKind.Current, o => o with { FramesAb = 2, LocalBusy = FlowControlAt.B, LongBusy = true }, Fails(Invariants.Deadlock, steps: 18));
        Add("busy-b-ro", "ax25spec#95 (H4), H2", TableSetKind.Current, o => o with
        {
            FramesAb = 2, LocalBusy = FlowControlAt.B, Budget = 2, Faults = FaultKinds.Reorder | FaultKinds.Drop,
            Invariants = Default & ~Invariants.RejectCoherence,
        }, Fails(Invariants.MachineError, Station.A, 16));
        // H3: once own receiver busy is set, the table's own DL-FLOW-OFF busy
        // arm emits RNR with no N(r) staged (both #43 checks off, so the
        // search gets past #43 to it).
        Add("h3-busy-flow-off", "#43 (H3)", TableSetKind.Current, o => o with
        {
            FramesAb = 2, LocalBusy = FlowControlAt.B, FlowControl = FlowControlAt.B,
            Invariants = Default & ~(Invariants.BusyTracksFlow | Invariants.FlowOffDelivery),
        }, Fails(Invariants.MachineError, Station.B, 4));
        Add("d43-tr", "#43 (the Timer Recovery arms)", TableSetKind.Current, o => o with
        {
            FramesAb = 2, FramesBa = 1, Budget = 1, Faults = FaultKinds.Drop, FlowControl = FlowControlAt.B,
            Invariants = Default & ~(Invariants.BusyTracksFlow | Invariants.FlowOffDelivery),
        }, Clean());
        Add("seeded-rnr-ignored", "seeded (peer-busy hold)", TableSetKind.RnrIgnored, o => o with { FramesAb = 2, LocalBusy = FlowControlAt.B }, Fails(Invariants.PeerBusyHold, Station.A, 3));
        Add("seeded-busy-delivers", "seeded (busy delivery)", TableSetKind.BusyDelivers, o => o with { FramesAb = 2, LocalBusy = FlowControlAt.B }, Fails(Invariants.FlowOffDelivery, Station.B, 3));
        Add("seeded-busy-says-rr", "seeded (busy indication)", TableSetKind.BusySaysRr, o => o with { FramesAb = 2, LocalBusy = FlowControlAt.B }, Fails(Invariants.BusyIndication, Station.B, 4));
        // ax25spec#94's gate mutation: a busy station's own T1 poll
        // (Transmit_Enquiry) is RR instead of RNR. busy-a-enq is the cheap
        // scenario that reaches that path: A goes busy with a0 out and a0 is
        // lost, so A's T1 expires while it is busy.
        Add("busy-a-enq", "peer-busy control", TableSetKind.Current, o => o with
        {
            FramesAb = 1, LocalBusy = FlowControlAt.A, Budget = 1, Faults = FaultKinds.Drop,
        }, Clean());
        Add("seeded-enquiry-says-rr", "seeded (busy indication, Transmit_Enquiry)", TableSetKind.EnquirySaysRr, o => o with
        {
            FramesAb = 1, LocalBusy = FlowControlAt.A, Budget = 1, Faults = FaultKinds.Drop,
        }, Fails(Invariants.BusyIndication, Station.A, 5));

        // Coverage batch 4: T3 (ExplorerOptions.T3). The keepalive fires only
        // when nothing is in flight and no T1 runs at either station.
        Add("t3-idle", "T3 control", TableSetKind.Current, o => o with { FramesAb = 0, T3 = true }, Clean());
        Add("t3-data", "T3 control", TableSetKind.Current, o => o with { FramesAb = 2, FramesBa = 1, Budget = 1, T3 = true }, Clean());
        Add("t3-gu", "T3 give-up control", TableSetKind.Current, o => o with
        {
            FramesAb = 0, N2 = 2, Budget = 2, Faults = FaultKinds.Drop, AcceptGiveUp = true, T3 = true,
        }, Clean());
        Add("t3-gu-release", "T3 give-up control", TableSetKind.Current, o => o with
        {
            FramesAb = 0, N2 = 1, Budget = 2, Faults = FaultKinds.Drop, Release = ReleaseBy.A, AcceptGiveUp = true, T3 = true,
        }, Clean());
        Add("t3-h1-k1", "#91", TableSetKind.Current, o => o with
        {
            FramesAb = 1, FramesBa = 2, K = 1, Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate, T3 = true,
        }, Fails(Invariants.Quiescence, steps: 9));
        Add("t3-reconnect-cold", "ax25spec#167 (fault-free with T3)", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, FramesAb = 0, Reconnect = ReconnectBy.A, T3 = true,
        }, Fails(Invariants.Quiescence, steps: 9));
        Add("seeded-t3-dead", "seeded (give-up settles with T3)", TableSetKind.T3Dead, o => o with
        {
            FramesAb = 0, N2 = 1, Budget = 2, Faults = FaultKinds.Drop, Release = ReleaseBy.A, AcceptGiveUp = true, T3 = true,
        }, Fails(Invariants.Deadlock, steps: 5));
        Add("seeded-no-keepalive", "seeded (keepalive at rest)", TableSetKind.NoKeepalive, o => o with
        {
            Seed = SeedKind.Disconnected, FramesAb = 0, T3 = true,
        }, Fails(Invariants.Deadlock, steps: 2));

        // Coverage batch 5: UI frames (ExplorerOptions.UnitData). Each named
        // station's layer 3 sends one UI datagram, once, in any state; the
        // per-step UnitData check judges what the receiver does with it, and
        // at rest every datagram was sent and then arrived or was dropped.
        Add("ui-connected", "UI control", TableSetKind.Current, o => o with { FramesAb = 1, UnitData = FlowControlAt.Both }, Clean());
        Add("ui-tr", "UI control", TableSetKind.Current, o => o with
        {
            FramesAb = 1, UnitData = FlowControlAt.Both, Budget = 1, Faults = FaultKinds.Drop,
        }, Clean());
        Add("ui-cold", "UI control", TableSetKind.Current, o => o with { Seed = SeedKind.Disconnected, FramesAb = 0, UnitData = FlowControlAt.Both }, Clean());
        Add("ui-cold-poll", "ax25spec#154", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, FramesAb = 0, UnitData = FlowControlAt.Both, UiForm = UiForm.Poll,
        }, Fails(Invariants.Deadlock, steps: 8));
        Add("ui-v22", "ax25spec#170", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.AwaitingV22Connection, FramesAb = 0, UnitData = FlowControlAt.B,
        }, Fails(Invariants.UnitData, Station.A, 2));
        Add("ui-v22-poll", "ax25spec#170", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.AwaitingV22Connection, FramesAb = 0, UnitData = FlowControlAt.B, UiForm = UiForm.Poll,
        }, Fails(Invariants.UnitData, Station.A, 2));
        Add("ui-v22-check-off", "ax25spec#170", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.AwaitingV22Connection, FramesAb = 0, UnitData = FlowControlAt.B, Invariants = Default & ~Invariants.UnitData,
        }, Fails(Invariants.Deadlock, steps: 5));
        Add("ui-v22-a", "UI control", TableSetKind.Current, o => o with { Seed = SeedKind.AwaitingV22Connection, FramesAb = 0, UnitData = FlowControlAt.A }, Clean());
        Add("ui-poll", "UI control (ax25spec#154 reading)", TableSetKind.Current, o => o with { FramesAb = 0, UnitData = FlowControlAt.A, UiForm = UiForm.Poll }, Clean());
        Add("ui-poll-tr", "UI control (ax25spec#154 reading)", TableSetKind.Current, o => o with
        {
            FramesAb = 1, FramesBa = 1, UnitData = FlowControlAt.Both, UiForm = UiForm.Poll, Budget = 1, Faults = FaultKinds.Drop,
        }, Clean());
        Add("ui-poll-busy", "ax25spec#171 (ax25spec#154 reading)", TableSetKind.Current, o => o with
        {
            FramesAb = 0, UnitData = FlowControlAt.A, UiForm = UiForm.Poll, LocalBusy = FlowControlAt.B,
        }, Fails(Invariants.BusyIndication, Station.B, 3));
        Add("ui-response", "UI control", TableSetKind.Current, o => o with { FramesAb = 0, UnitData = FlowControlAt.Both, UiForm = UiForm.Response }, Clean());
        Add("ui-long", "UI control", TableSetKind.Current, o => o with { FramesAb = 0, UnitData = FlowControlAt.Both, UiForm = UiForm.Oversize }, Clean());
        Add("ui-release", "UI control", TableSetKind.Current, o => o with { FramesAb = 1, UnitData = FlowControlAt.Both, Release = ReleaseBy.A }, Clean());
        Add("ui-release-poll", "UI control (ax25spec#154 reading)", TableSetKind.Current, o => o with
        {
            FramesAb = 1, UnitData = FlowControlAt.Both, Release = ReleaseBy.A, UiForm = UiForm.Poll,
        }, Clean());
        Add("seeded-ui-swallowed", "seeded (UI delivered)", TableSetKind.UiSwallowed, o => o with { FramesAb = 1, UnitData = FlowControlAt.Both }, Fails(Invariants.UnitData, Station.B, 2));
        Add("seeded-ui-unsent", "seeded (UI accounted at rest)", TableSetKind.UiUnsent, o => o with { FramesAb = 1, UnitData = FlowControlAt.Both }, Fails(Invariants.Deadlock, steps: 6));
        Add("seeded-ui-poll-unanswered", "seeded (UI poll answered)", TableSetKind.UiPollUnanswered, o => o with { FramesAb = 0, UnitData = FlowControlAt.A, UiForm = UiForm.Poll }, Fails(Invariants.UnitData, Station.B, 2));
        Add("seeded-ui-response-delivered", "seeded (UI response refused)", TableSetKind.UiResponseDelivered, o => o with
        {
            FramesAb = 0, UnitData = FlowControlAt.Both, UiForm = UiForm.Response,
        }, Fails(Invariants.UnitData, Station.B, 2));
        Add("seeded-ui-arm-deleted", "seeded (UI handled on a catch-all)", TableSetKind.UiArmDeleted, o => o with
        {
            FramesAb = 1, UnitData = FlowControlAt.Both, Budget = 1, Faults = FaultKinds.Drop,
        }, Fails(Invariants.UnitData, Station.A, 5));

        // Coverage batch 6, part 1: frame errors (FrameErrors), one channel
        // fault, no other faults unless stated (a duplicated UA would reset
        // the link without justification, #114, and the reset rule never
        // lets that rest).
        const FrameErrors FE3 = FrameErrors.ControlField | FrameErrors.InfoNotPermitted | FrameErrors.Length;
        const FrameErrors FE5 = FE3 | FrameErrors.N1 | FrameErrors.IResponse;
        ExplorerOptions Fe(ExplorerOptions o, FrameErrors fe) => o with { FrameErrors = fe, Budget = 1, Faults = FaultKinds.None };
        Add("fe-cold", "frame-error control", TableSetKind.Current, o => Fe(o, FE3) with { Seed = SeedKind.Disconnected, FramesAb = 0 }, Clean());
        Add("fe-v22", "frame-error control", TableSetKind.Current, o => Fe(o, FE3) with { Seed = SeedKind.AwaitingV22Connection, FramesAb = 0 }, Clean());
        Add("fe-release", "frame-error control (honest reset, nothing lost)", TableSetKind.Current, o => Fe(o, FE3) with { FramesAb = 0, Release = ReleaseBy.A }, Clean());
        Add("fe-a1", "reset-loss control", TableSetKind.Current, o => Fe(o, FE5) with { FramesAb = 1 }, Fails(Invariants.ResetLoss, steps: 4));
        Add("fe-a1-srej", "reset-loss control", TableSetKind.Current, o => Fe(o, FE5) with { FramesAb = 1, Srej = true }, Fails(Invariants.ResetLoss, steps: 4));
        Add("fe-ab1-ctl", "reset-loss control", TableSetKind.Current, o => Fe(o, FrameErrors.ControlField) with { FramesAb = 1, FramesBa = 1 }, Fails(Invariants.ResetLoss, steps: 4));
        Add("fe-a2-k1-info", "reset-loss control", TableSetKind.Current, o => Fe(o, FrameErrors.InfoNotPermitted) with { FramesAb = 2, K = 1 }, Fails(Invariants.ResetLoss, steps: 6));
        Add("fe-iresp", "frame-error control", TableSetKind.Current, o => Fe(o, FrameErrors.IResponse) with { FramesAb = 1 }, Clean());
        Add("fe-t3-tr", "ax25spec#167 (shows through #114)", TableSetKind.Current, o => Fe(o, FE3) with { FramesAb = 0, T3 = true }, Fails(Invariants.Quiescence, steps: 9));
        Add("fe-tr-b3", "ax25spec#50", TableSetKind.Current, o => Fe(o, FrameErrors.N1 | FrameErrors.IResponse) with
        {
            FramesAb = 1, FramesBa = 1, Budget = 3, Faults = FaultKinds.Drop,
        }, Fails(Invariants.Deadlock, steps: 4));
        Add("fe-iresp-trq", "ax25spec#175, #176", TableSetKind.Current, o => Fe(o, FrameErrors.IResponse) with
        {
            FramesAb = 1, FramesBa = 1, Budget = 3, Faults = FaultKinds.Drop, TimerRecoveryIsQuiescent = true,
        }, Clean());
        Add("busy-a-tr2", "#91 (H1)", TableSetKind.Current, o => o with
        {
            FramesAb = 1, FramesBa = 1, LocalBusy = FlowControlAt.A, Budget = 2, Faults = FaultKinds.Drop,
        }, Fails(Invariants.Deadlock, steps: 14));
        Add("t3-reconnect-cold-mod128", "ax25spec#167 (fault-free with T3, modulo 128)", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, Modulo128A = true, FramesAb = 0, T3 = true, Reconnect = ReconnectBy.A,
        }, Fails(Invariants.Quiescence, steps: 9));

        // From the review of batch 6: rows the first cut recorded as not fired.
        Add("tr-rej", "#91 (H1)", TableSetKind.Current, o => o with { FramesAb = 1, FramesBa = 3, K = 2, Budget = 3, Faults = FaultKinds.Drop }, Fails(Invariants.Deadlock, steps: 17));
        Add("tr-srej-rc-off", "#42 (runaway), masked by #42/#47 with the checks on", TableSetKind.Current, o => o with
        {
            FramesAb = 1, FramesBa = 2, Srej = true, Budget = 4, Faults = FaultKinds.Drop,
            Invariants = Default & ~Invariants.RejectCoherence & ~Invariants.Delivery,
        }, Fails(Invariants.SequenceSanity, Station.A, 24));
        Add("dl-error-u", "ax25spec#168", TableSetKind.Current, o => o with
        {
            FramesAb = 0, LocalBusy = FlowControlAt.B, LongBusy = true, T3 = true, N2 = 1, Budget = 1, Faults = FaultKinds.Drop, AcceptGiveUp = true,
        }, Fails(Invariants.Deadlock, steps: 6));

        // Coverage batch 6, part 2: injected frames (Inject, InjectedFrame).
        ExplorerOptions Inj(ExplorerOptions o, FlowControlAt at, InjectedFrame f) => o with { Inject = at, InjectedFrame = f, Budget = 0 };
        Add("inj-rej-busy-tr", "ax25spec#171", TableSetKind.Current, o => Inj(o, FlowControlAt.A, InjectedFrame.RejPoll) with
        {
            FramesAb = 0, FramesBa = 1, LocalBusy = FlowControlAt.B, Budget = 1, Faults = FaultKinds.Drop,
        }, Fails(Invariants.BusyIndication, Station.B, 6));
        Add("inj-rej-tr", "REJ poll control", TableSetKind.Current, o => Inj(o, FlowControlAt.B, InjectedFrame.RejPoll) with { FramesAb = 1, Budget = 1, Faults = FaultKinds.Drop }, Clean());
        Add("inj-srej-tr", "ax25spec#164 (accepted)", TableSetKind.Current, o => Inj(o, FlowControlAt.B, InjectedFrame.SrejCommand) with { FramesAb = 1, Budget = 1, Faults = FaultKinds.Drop }, Clean());
        Add("inj-srejp-tr", "ax25spec#164 (accepted)", TableSetKind.Current, o => Inj(o, FlowControlAt.B, InjectedFrame.SrejPoll) with { FramesAb = 1, Budget = 1, Faults = FaultKinds.Drop }, Clean());
        Add("inj-ua-cold", "UA F=0 control", TableSetKind.Current, o => Inj(o, FlowControlAt.B, InjectedFrame.UaF0) with { Seed = SeedKind.Disconnected, FramesAb = 0 }, Clean());
        Add("inj-ua-v22", "UA F=0 control", TableSetKind.Current, o => Inj(o, FlowControlAt.B, InjectedFrame.UaF0) with { Seed = SeedKind.AwaitingV22Connection, FramesAb = 0 }, Clean());
        Add("inj-ua-release", "UA F=0 control", TableSetKind.Current, o => Inj(o, FlowControlAt.B, InjectedFrame.UaF0) with { FramesAb = 0, Release = ReleaseBy.A }, Clean());
        Add("inj-frmr-srej", "FRMR control", TableSetKind.Current, o => Inj(o, FlowControlAt.B, InjectedFrame.Frmr) with { FramesAb = 0, Srej = true }, Clean());
        Add("inj-frmr-t3", "FRMR control", TableSetKind.Current, o => Inj(o, FlowControlAt.B, InjectedFrame.Frmr) with { FramesAb = 0, T3 = true }, Clean());
        Add("inj-frmr-a1", "ax25spec#50", TableSetKind.Current, o => Inj(o, FlowControlAt.B, InjectedFrame.Frmr) with { FramesAb = 1 }, Fails(Invariants.Deadlock, steps: 4));
        Add("seeded-fe-unreported", "seeded (frame error reported)", TableSetKind.FrameErrorUnreported, o => Fe(o, FrameErrors.ControlField) with { FramesAb = 1 }, Fails(Invariants.FrameError, Station.B, 2));
        Add("seeded-fe-arms-deleted", "seeded (frame error on a catch-all)", TableSetKind.FrameErrorArmsDeleted, o => Fe(o, FE3) with { FramesAb = 0, T3 = true }, Fails(Invariants.FrameError, Station.A, 3));
        Add("seeded-reset-unconfirmed", "seeded (reset loss needs layer 3 told)", TableSetKind.ResetUnconfirmed, o => Fe(o, FrameErrors.ControlField) with { FramesAb = 1, FramesBa = 1 }, Fails(Invariants.Deadlock, steps: 4));
        Add("seeded-reset-unconfirmed-a", "seeded (reset loss needs layer 3 told)", TableSetKind.ResetUnconfirmed, o => Fe(o, FrameErrors.InfoNotPermitted) with { FramesAb = 2, K = 1 }, Fails(Invariants.Deadlock, steps: 6));
        Add("seeded-reset-wrong-code", "seeded (told by the reset's own code)", TableSetKind.ResetWrongCode, o => Fe(o, FrameErrors.ControlField) with { FramesAb = 1 }, Fails(Invariants.Deadlock, steps: 4));
        Add("seeded-rr-resets", "seeded (unjustified reset never rests)", TableSetKind.RrResets, o => Fe(o, FrameErrors.ControlField) with { FramesAb = 1, Budget = 0 }, Fails(Invariants.Deadlock, steps: 6));
        Add("seeded-rr-resets-loss", "seeded (unjustified reset is no reset loss)", TableSetKind.RrResets, o => Fe(o, FrameErrors.ControlField) with { FramesAb = 2, K = 1, Budget = 0 }, Fails(Invariants.Deadlock, steps: 6));

        // Coverage batch 7: injected I frames (InjectedFrame.IFrame and after),
        // with their own N(s) and payload label, and a second injection
        // (InjectedThen). Labels name the arm a row is there for.
        ExplorerOptions Ip(ExplorerOptions o, FlowControlAt at, InjectedFrame f, InjectedFrame then = InjectedFrame.None) =>
            o with { Inject = at, InjectedFrame = f, InjectedThen = then, FramesAb = 0, Budget = 0 };
        const InjectedFrame Ahead = InjectedFrame.IPollAhead, Stale = InjectedFrame.IPollStale;
        Add("ip-inseq", "I poll in sequence", TableSetKind.Current, o => Ip(o, FlowControlAt.A, InjectedFrame.IPoll), Clean());
        Add("ip-stale", "I poll outside the window", TableSetKind.Current, o => Ip(o, FlowControlAt.A, Stale), Clean());
        Add("ip-busy", "ax25spec#178 (figc4.4 busy I poll answered RR)", TableSetKind.Current, o => Ip(o, FlowControlAt.A, Stale) with { LocalBusy = FlowControlAt.B }, Fails(Invariants.BusyIndication, Station.B, 3));
        Add("ip-rejx", "I poll in reject exception; REJ F=1", TableSetKind.Current, o => Ip(o, FlowControlAt.A, Ahead, Ahead) with
        {
            FramesAb = 1, Budget = 1, Faults = FaultKinds.Drop, RejMayEqualVs = true,
        }, Clean());
        Add("ip-tr-inseq", "I poll in sequence, TR", TableSetKind.Current, o => Ip(o, FlowControlAt.A, InjectedFrame.IPoll) with { FramesBa = 1, Budget = 1, Faults = FaultKinds.Drop }, Clean());
        Add("ip-tr-stale", "#91 (H1)", TableSetKind.Current, o => Ip(o, FlowControlAt.A, Stale) with { FramesBa = 1, Budget = 1, Faults = FaultKinds.Drop }, Fails(Invariants.Deadlock, steps: 12));
        Add("ip-tr-busy", "I poll to a busy TR (behind ip-busy)", TableSetKind.Current, o => Ip(o, FlowControlAt.A, Stale) with
        {
            FramesBa = 1, Budget = 1, Faults = FaultKinds.Drop, LocalBusy = FlowControlAt.B,
            Invariants = Default & ~Invariants.BusyIndication, TimerRecoveryIsQuiescent = true,
        }, Clean());
        Add("ip-tr-rejx", "I poll in reject exception, TR", TableSetKind.Current, o => Ip(o, FlowControlAt.A, Ahead, Ahead) with
        {
            FramesAb = 1, FramesBa = 1, Budget = 1, Faults = FaultKinds.Drop, RejMayEqualVs = true,
        }, Clean());
        Add("ip-nr", "REJ F=1, N(r) beyond V(s)", TableSetKind.Current, o => Ip(o, FlowControlAt.A, InjectedFrame.IFrame, Ahead) with
        {
            T3 = true, Budget = 1, Faults = FaultKinds.Drop, Invariants = Default & ~Invariants.RejectCoherence,
        }, Clean());
        Add("ip-nr-v22", "REJ F=1, N(r) beyond V(s), v2.2", TableSetKind.Current, o => Ip(o, FlowControlAt.A, InjectedFrame.IFrame, Ahead) with
        {
            Seed = SeedKind.AwaitingV22Connection, T3 = true, Budget = 1, Faults = FaultKinds.Drop, Invariants = Default & ~Invariants.RejectCoherence,
        }, Clean());
        // Since ax25spec#180 the RNR arm splits by version; this is the
        // version 2.0 half (t19_rnr_received_no_no_no_no).
        Add("ip-nr-rnr", "RNR, N(r) beyond V(s)", TableSetKind.Current, o => Ip(o, FlowControlAt.A, InjectedFrame.IFrame) with
        {
            T3 = true, Budget = 1, Faults = FaultKinds.Drop, LocalBusy = FlowControlAt.B,
        }, Clean());
        Add("ip-nr-rnr-v22", "RNR, N(r) beyond V(s), v2.2", TableSetKind.Current, o => Ip(o, FlowControlAt.A, InjectedFrame.IFrame) with
        {
            Seed = SeedKind.AwaitingV22Connection, T3 = true, Budget = 1, Faults = FaultKinds.Drop, LocalBusy = FlowControlAt.B,
        }, Clean());
        // The reject-naming-vs arms: with RejMayEqualVs the injected REJ and
        // SREJ commands may also name the receiver's V(s).
        Add("rnv-rej", "REJ poll naming V(s)", TableSetKind.Current, o => Inj(o, FlowControlAt.B, InjectedFrame.RejPoll) with
        {
            FramesAb = 1, Budget = 1, Faults = FaultKinds.Drop, RejMayEqualVs = true,
        }, Clean());
        Add("rnv-srej", "SREJ command naming V(s)", TableSetKind.Current, o => Inj(o, FlowControlAt.B, InjectedFrame.SrejCommand) with
        {
            FramesAb = 0, T3 = true, Budget = 1, Faults = FaultKinds.Drop, RejMayEqualVs = true, Invariants = Default & ~Invariants.RejectCoherence,
        }, Clean());
        Add("rnv-srejp", "#91 (H1); ax25spec#164", TableSetKind.Current, o => Inj(o, FlowControlAt.B, InjectedFrame.SrejPoll) with
        {
            FramesAb = 1, Budget = 1, Faults = FaultKinds.Drop, RejMayEqualVs = true, Invariants = Default & ~Invariants.RejectCoherence,
        }, Fails(Invariants.Deadlock, steps: 11));
        Add("seeded-injected-stale", "seeded (injected payload delivered out of sequence)", TableSetKind.InjectedDelivered, o => Ip(o, FlowControlAt.A, Stale), Fails(Invariants.Delivery, Station.B, 2));
        Add("seeded-injected-twice", "seeded (injected payload delivered twice)", TableSetKind.InjectedDelivered, o => Ip(o, FlowControlAt.A, InjectedFrame.IPoll), Fails(Invariants.Delivery, Station.B, 2));
        Add("seeded-injected-undelivered", "seeded (injected payload accepted, not delivered)", TableSetKind.InjectedUndelivered, o => Ip(o, FlowControlAt.A, InjectedFrame.IPoll), Fails(Invariants.Delivery, Station.B, 2));
        // From the review of batch 7: both stations inject an I frame in
        // sequence and B, in Timer Recovery, meets an RR with N(r) past V(s).
        // #12's errata branch stayed in Timer Recovery and A delivered b0
        // twice (Delivery at A, 18); fixed by ax25spec#180, which leaves for
        // Awaiting Connection as the 1998 figure draws.
        Add("ip-both", "ax25spec#12 fixed (#180)", TableSetKind.Current, o => Ip(o, FlowControlAt.Both, InjectedFrame.IFrame) with
        {
            FramesBa = 1, T3 = true, Budget = 2, Faults = FaultKinds.Drop, Invariants = Default & ~Invariants.RejectCoherence,
        }, Clean(3531));

        // ax25spec#169: Timer Recovery's SABM and SABME arms with V(s) = V(a)
        // reset the link (Stop T1, Stop T3, V(s), V(a), V(r) := 0). A sends
        // one frame; once it is delivered B's T3 (only B's, so A stays in
        // Connected and #167 stays out of the way) takes B to Timer Recovery
        // holding V(r) = 1, A's layer 3 resets the link and, once that is answered with the link up,
        // submits one more frame (DataAfterReconnect). With the reset box B
        // takes it in sequence; without it B still holds V(r) = 1, discards
        // the frame as out of sequence and answers N(r) = 1, which
        // acknowledges at A a frame B never delivered.
        ExplorerOptions R169(ExplorerOptions o) => o with
        {
            FramesAb = 1, Budget = 0, T3 = true, T3At = FlowControlAt.B, Reconnect = ReconnectBy.A, DataAfterReconnect = true,
        };
        Add("reconnect-tr-data", "ax25spec#169 fixed", TableSetKind.Current, R169, Clean(45));
        Add("reverted-169", "ax25spec#169 reverted", TableSetKind.Reverted169, R169, Fails(Invariants.AckCoherence, Station.A, 15));
        // The rows above prove only V(r) := 0: B has sent nothing, so its V(s)
        // and V(a) are 0 when the reset arrives. With B sending b0 before the
        // reconnect they are 1, and leaving any one of V(s), V(a), V(r)
        // unreset fails the row. Stop T1 and Stop T3 are not caught by any
        // row (a T1 left running only costs an extra poll): a documented gap.
        Add("reconnect-tr-data-ba", "ax25spec#169 fixed (V(s), V(a))", TableSetKind.Current, o => R169(o) with { FramesBa = 1 }, Clean(170));
        Add("reverted-169-ba", "ax25spec#169 reverted", TableSetKind.Reverted169, o => R169(o) with { FramesBa = 1 }, Fails(Invariants.Quiescence, steps: 9));

        return list;
    }

    /// <summary>
    /// The explorer's data-phase grid (docs/explorer.md, "Grid"): frames
    /// 0..3 each way, budget 0..2, k in {2, 4}, SREJ off and on, faults
    /// drop, dup and drop+dup, all invariants on, visited-state cap 1.2
    /// million. Optionally with B's layer 3 flow-controlling, as the
    /// explorer's flow-control grid does.
    /// </summary>
    public static IReadOnlyList<Scenario> Grid(string currentDir, bool flowB)
    {
        var list = new List<Scenario>();
        foreach (var ab in new[] { 0, 1, 2, 3 })
        foreach (var ba in new[] { 0, 1, 2, 3 })
        foreach (var b in new[] { 0, 1, 2 })
        foreach (var k in new[] { 2, 4 })
        foreach (var srej in new[] { false, true })
        foreach (var (faults, fname) in new[] { (FaultKinds.Drop, "drop"), (FaultKinds.Duplicate, "dup"), (FaultKinds.Drop | FaultKinds.Duplicate, "drop+dup") })
        {
            var name = $"g-ab{ab}-ba{ba}-b{b}-k{k}-{(srej ? "srej" : "nosrej")}-{fname}{(flowB ? "-flowB" : "")}";
            list.Add(new Scenario(name, "grid", TableSetKind.Current, new ExplorerOptions
            {
                TablesDir = currentDir, FramesAb = ab, FramesBa = ba, Budget = b, K = k, Srej = srej, Faults = faults,
                FlowControl = flowB ? FlowControlAt.B : FlowControlAt.None, MaxStates = 1_200_000,
            }, null));
        }
        return list;
    }
}
