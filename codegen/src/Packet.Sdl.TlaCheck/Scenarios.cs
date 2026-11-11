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
}

/// <summary>One cross-check row: a named explorer scenario and what the calibration suite says it shows.</summary>
/// <param name="DepthBound">The row relies on the explorer's depth bound (an infinite space); TLC applies the same bound as a state constraint.</param>
public sealed record Scenario(string Name, string Defect, TableSetKind Tables, ExplorerOptions Options, string Expect, bool DepthBound = false);

/// <summary>
/// The explorer's calibration suite (codegen/tests/Packet.Sdl.Explorer.Tests/CalibrationTests.cs),
/// scenario for scenario, plus the controls the suite runs beside them. The
/// options are the suite's own; only the tables directory is resolved here.
/// </summary>
public static class Scenarios
{
    private static readonly Invariants Default = Invariants.Default;

    public static IReadOnlyList<Scenario> Calibration(Func<TableSetKind, string> dir)
    {
        ExplorerOptions O(TableSetKind t) => new() { TablesDir = dir(t) };
        var list = new List<Scenario>();
        void Add(string name, string defect, TableSetKind t, Func<ExplorerOptions, ExplorerOptions> with, string expect, bool depthBound = false) =>
            list.Add(new Scenario(name, defect, t, with(O(t)), expect, depthBound));

        // #40: out-of-window duplicate raises an unclearable exception.
        ExplorerOptions S40(ExplorerOptions o, bool srej) => o with { FramesAb = 2, FramesBa = 0, K = 4, Srej = srej, Budget = 1, Faults = FaultKinds.Duplicate };
        Add("d40-pre40-srej", "#40", TableSetKind.Pre40, o => S40(o, true), "RejectCoherence at A, 4");
        Add("d40-pre40-nosrej", "#40", TableSetKind.Pre40, o => S40(o, false), "RejectCoherence, 4");
        Add("d40-current-srej", "#40 fixed", TableSetKind.Current, o => S40(o, true), "clean");
        Add("d40-current-nosrej", "#40 fixed", TableSetKind.Current, o => S40(o, false), "clean");
        Add("d40-pre40-rej-eq-vs", "#40", TableSetKind.Pre40, o => S40(o, true) with { RejMayEqualVs = true }, "MachineError (phantom retransmission)");

        // #38: documented limit, the two fixtures must give the same verdict.
        ExplorerOptions S38(ExplorerOptions o) => o with { FramesAb = 3, K = 4, Srej = true, Budget = 2, Faults = FaultKinds.Drop, Invariants = Default & ~Invariants.RejectCoherence };
        Add("d38-pre38", "#38", TableSetKind.Pre38, S38, "same verdict as d38-pre40");
        Add("d38-pre40", "#38", TableSetKind.Pre40, S38, "same verdict as d38-pre38");

        // #42: SREJ names the arriving frame, not the gap.
        ExplorerOptions S42(ExplorerOptions o) => o with { FramesAb = 3, FramesBa = 0, K = 4, Srej = true, Budget = 2, Faults = FaultKinds.Drop };
        Add("d42", "#42", TableSetKind.Current, S42, "RejectCoherence at B, 6");
        Add("d42-rc-off", "#42", TableSetKind.Current, o => S42(o) with { Invariants = Default & ~Invariants.RejectCoherence }, "SequenceSanity (SREJ count)");
        Add("d42-both-off", "#42", TableSetKind.Current, o => S42(o) with
        {
            Invariants = (Default | Invariants.SelectiveProgress) & ~(Invariants.RejectCoherence | Invariants.SequenceSanity),
            MaxDepth = 60,
        }, "depth bound hit (the runaway)", depthBound: true);

        // #47: TimerRecovery drain decrements V(r).
        Add("d47-rc-off", "#47", TableSetKind.Current, o => o with
        {
            FramesAb = 2, FramesBa = 1, K = 4, Srej = true, Budget = 3, Faults = FaultKinds.Drop,
            Invariants = Default & ~Invariants.RejectCoherence,
        }, "Delivery at B, 20");
        Add("d47-k2", "#47", TableSetKind.Current, o => o with { FramesAb = 1, FramesBa = 3, K = 2, Srej = true, Budget = 2, Faults = FaultKinds.Drop }, "Delivery at A, 24");
        Add("d47-rc-on", "#47/#42", TableSetKind.Current, o => o with { FramesAb = 2, FramesBa = 1, K = 4, Srej = true, Budget = 3, Faults = FaultKinds.Drop }, "RejectCoherence, < 20");

        // #9: acknowledgement progress does not reset RC.
        Add("d9", "#9", TableSetKind.Current, o => o with
        {
            FramesAb = 3, FramesBa = 0, K = 4, N2 = 2, Budget = 3, Faults = FaultKinds.Drop, DropScope = DropScope.InteriorI,
        }, "Deadlock, 30");

        // #13: window at half the modulus (protocol constraint).
        ExplorerOptions S13(ExplorerOptions o, int k) => o with { FramesAb = 3, FramesBa = 0, K = k, Srej = false, Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate };
        Add("d13-k7", "#13", TableSetKind.Current, o => S13(o, 7), "RejectCoherence at A, 13");
        Add("d13-k4", "#13 control", TableSetKind.Current, o => S13(o, 4), "clean");

        // #44 / #48 from a cold connect, table-driven peer.
        Add("d44-48-cold", "#44/#48", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, Modulo128A = true, PeerDeclinesSabme = true, FramesAb = 1, Budget = 0,
        }, "Deadlock, 2");
        Add("d44-48-cold-mod8", "#44/#48 control", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.Disconnected, Modulo128A = false, PeerDeclinesSabme = true, FramesAb = 1, Budget = 0,
        }, "clean");

        // #43: DL-FLOW-OFF on the wrong branch.
        ExplorerOptions S43(ExplorerOptions o) => o with { FramesAb = 2, FramesBa = 0, K = 4, Budget = 0, FlowControl = FlowControlAt.B };
        Add("d43", "#43", TableSetKind.Current, S43, "BusyTracksFlow at B, 3");
        Add("d43-busy-off", "#43", TableSetKind.Current, o => S43(o) with { Invariants = Default & ~Invariants.BusyTracksFlow }, "FlowOffDelivery at B, 5");
        Add("d43-both-off", "#43", TableSetKind.Current, o => S43(o) with { Invariants = Default & ~(Invariants.BusyTracksFlow | Invariants.FlowOffDelivery) }, "clean");

        // #45 / #48 against the v2.0 stub peer.
        ExplorerOptions V20(ExplorerOptions o, SeedKind seed, PeerKind peer, bool timerFree) => o with
        {
            Seed = seed, Modulo128A = seed == SeedKind.Disconnected, Peer = peer, FramesAb = 0, Budget = 0,
            Invariants = timerFree ? Default | Invariants.SelectiveProgress : Default,
        };
        Add("d45-cold-timer-free", "#44 masks #45", TableSetKind.Current, o => V20(o, SeedKind.Disconnected, PeerKind.V20Frmr, true), "SelectiveProgress");
        Add("d45-cold", "#44 masks #45", TableSetKind.Current, o => V20(o, SeedKind.Disconnected, PeerKind.V20Frmr, false), "clean");
        Add("d45-v22-timer-free", "#45", TableSetKind.Current, o => V20(o, SeedKind.AwaitingV22Connection, PeerKind.V20Frmr, true), "SelectiveProgress");
        Add("d45-v22", "#45", TableSetKind.Current, o => V20(o, SeedKind.AwaitingV22Connection, PeerKind.V20Frmr, false), "clean");
        Add("d48-v22-dm", "#48", TableSetKind.Current, o => V20(o, SeedKind.AwaitingV22Connection, PeerKind.V20Dm, false), "Deadlock, 2");
        Add("d48-v22-tables", "#48", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.AwaitingV22Connection, PeerDeclinesSabme = true, FramesAb = 0, Budget = 0,
        }, "Deadlock");
        Add("d48-cold-dm", "#48", TableSetKind.Current, o => V20(o, SeedKind.Disconnected, PeerKind.V20Dm, false), "Deadlock, 2");
        Add("v20-mod8-frmr", "#45/#48 control", TableSetKind.Current, o => V20(o, SeedKind.Disconnected, PeerKind.V20Frmr, true) with { Modulo128A = false }, "clean");
        Add("v20-mod8-dm", "#45/#48 control", TableSetKind.Current, o => V20(o, SeedKind.Disconnected, PeerKind.V20Dm, true) with { Modulo128A = false }, "clean");
        Add("v22-peer-ua", "#45/#48 control", TableSetKind.Current, o => o with
        {
            Seed = SeedKind.AwaitingV22Connection, FramesAb = 0, Budget = 0, Invariants = Default | Invariants.SelectiveProgress,
        }, "clean");

        // Hypothesis H1 (docs/explorer.md): stuck in TimerRecovery.
        ExplorerOptions H1(ExplorerOptions o) => o with { FramesAb = 2, FramesBa = 1, K = 4, Srej = false, Budget = 2, Faults = FaultKinds.Drop };
        Add("h1", "H1", TableSetKind.Current, H1, "Deadlock, 19");
        Add("h1-tr-quiescent", "H1 control", TableSetKind.Current, o => H1(o) with { TimerRecoveryIsQuiescent = true }, "clean");

        // Seeded defects (fixtures/README.md): no real defect fails these two
        // checks on any table set, so each has a deliberately broken table to
        // prove it can fire, in both tools.
        Add("seeded-ack", "seeded (AckCoherence)", TableSetKind.AckBroken, o => o with { FramesAb = 1 }, "AckCoherence at A, 4");
        Add("seeded-livelock", "seeded (Quiescence)", TableSetKind.Livelock, o => o with { FramesAb = 1 }, "Quiescence");

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
            }, ""));
        }
        return list;
    }
}
