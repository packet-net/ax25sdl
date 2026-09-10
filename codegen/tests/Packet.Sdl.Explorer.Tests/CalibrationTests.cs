using Packet.Sdl.Explorer;
using Xunit.Abstractions;

namespace Packet.Sdl.Explorer.Tests;

/// <summary>
/// The calibration suite (docs/fuzzing-the-figures.md, "Calibrate before
/// trusting a single novel finding"): the explorer must rediscover the
/// catalogued figure defects on the tables that carry them, and stay silent
/// on the same scenario against tables that carry the fix where one exists.
/// The score table in docs/explorer.md is derived from these tests. Cases
/// the model cannot reach are written as explicit documented limits, not as
/// passing assertions.
/// </summary>
public class CalibrationTests(ITestOutputHelper output)
{
    private ExplorationResult Run(ExplorerOptions options)
    {
        var result = Fixtures.Run(options);
        output.WriteLine(result.Render());
        return result;
    }

    // ─── #40: out-of-window duplicate raises an unclearable exception ──

    private static ExplorerOptions Scenario40(string tables, bool srej) => new()
    {
        TablesDir = tables, FramesAb = 2, FramesBa = 0, K = 4, Srej = srej, Budget = 1, Faults = FaultKinds.Duplicate,
    };

    [Fact]
    public void Defect40_Pre40_Tables_Duplicate_Of_A_Received_Frame_Draws_An_Incoherent_Reject()
    {
        var result = Run(Scenario40(Fixtures.Pre40Dir, srej: true));
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.RejectCoherence);
        result.Violation.Station.Should().Be(Station.A, "the sender receives a reject naming a frame it never sent");
        result.Violation.Message.Should().Contain("REJ nr=1");
        result.CounterexampleLength.Should().Be(4);
    }

    [Fact]
    public void Defect40_Pre40_Tables_Fire_With_Srej_Off_Too()
    {
        var result = Run(Scenario40(Fixtures.Pre40Dir, srej: false));
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.RejectCoherence);
        result.CounterexampleLength.Should().Be(4);
    }

    [Fact]
    public void Defect40_Current_Tables_Discard_The_Duplicate_And_Stay_Silent()
    {
        Run(Scenario40(Fixtures.CurrentDir, srej: true)).Outcome.Should().Be(Outcome.NoViolation);
        Run(Scenario40(Fixtures.CurrentDir, srej: false)).Outcome.Should().Be(Outcome.NoViolation);
    }

    [Fact]
    public void Defect40_Pre40_Tables_With_The_Rej_Equals_Vs_Exemption_Still_Fire_At_The_Phantom_Retransmission()
    {
        // With the receiver-side exemption on, the REJ nr=V(s) is let through and
        // figc4.7's do-while pushes I(V(s)), a frame that was never sent; the
        // pinned retransmission refuses to invent it.
        var result = Run(Scenario40(Fixtures.Pre40Dir, srej: true) with { RejMayEqualVs = true });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.MachineError);
        result.Violation.Message.Should().Contain("retransmission of I(ns=1) requested");
    }

    // ─── #38: SREJ go-back-N in TimerRecovery (documented limit) ──────

    [Fact]
    public void Defect38_Is_An_Inefficiency_The_Explorer_Does_Not_Discriminate()
    {
        // Documented limit, not a passing assertion: go-back-N on an SREJ still
        // delivers everything in order, so no invariant here distinguishes the
        // pre-38 tables from the pre-40 ones. The golden traces (#76) are the
        // detector for #38. This test pins that the two table sets give the
        // SAME verdict on the SREJ loss scenario, so the limit stays visible.
        var scenario = new ExplorerOptions
        {
            TablesDir = Fixtures.Pre38Dir, FramesAb = 3, K = 4, Srej = true, Budget = 2, Faults = FaultKinds.Drop,
            Invariants = Invariants.Default & ~Invariants.RejectCoherence,
        };
        var pre38 = Run(scenario);
        var pre40 = Run(scenario with { TablesDir = Fixtures.Pre40Dir });
        pre38.Outcome.Should().Be(pre40.Outcome);
        pre38.Violation?.Kind.Should().Be(pre40.Violation?.Kind);
        pre38.CounterexampleLength.Should().Be(pre40.CounterexampleLength);
    }

    // ─── #42: SREJ names the just-arrived frame, not the gap ──────────

    private static ExplorerOptions Scenario42 => new()
    {
        TablesDir = Fixtures.CurrentDir, FramesAb = 3, FramesBa = 0, K = 4, Srej = true, Budget = 2, Faults = FaultKinds.Drop,
    };

    [Fact]
    public void Defect42_Current_Tables_Second_Srej_Names_A_Frame_The_Receiver_Already_Holds()
    {
        var result = Run(Scenario42);
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.RejectCoherence);
        result.Violation.Station.Should().Be(Station.B);
        result.Violation.Message.Should().Contain("SREJ rsp F=0 nr=2").And.Contain("already holds I(ns=2)");
        result.CounterexampleLength.Should().Be(6);
    }

    [Fact]
    public void Defect42_With_Reject_Coherence_Off_The_Srej_Count_Runs_Away()
    {
        // The wrongly-named SREJ makes the sender resend a frame the receiver
        // holds, which the receiver SREJs again: an exchange that never repeats
        // a state because the SREJ exception count climbs on every round.
        var result = Run(Scenario42 with { Invariants = Invariants.Default & ~Invariants.RejectCoherence });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.SequenceSanity);
        result.Violation.Message.Should().Contain("SREJ exception count");
    }

    [Fact]
    public void Defect42_With_Both_Checks_Off_The_Runaway_Hits_The_Depth_Bound()
    {
        // Documented: the selective-recovery progress check never gets to run on
        // #42 because the defect makes the reachable space infinite (the SREJ
        // count is unbounded), so the liveness analysis cannot complete.
        var result = Run(Scenario42 with
        {
            Invariants = (Invariants.Default | Invariants.SelectiveProgress) & ~(Invariants.RejectCoherence | Invariants.SequenceSanity),
            MaxDepth = 60,
        });
        result.Outcome.Should().Be(Outcome.BoundHit);
        result.DepthBoundHit.Should().BeTrue();
        result.Trace.Count.Should().Be(60);
        var srejs = result.Trace.Count(step => step.Effects.Any(e => e.StartsWith("frame SREJ", StringComparison.Ordinal)));
        srejs.Should().BeGreaterThan(8, "the witness path is the runaway: far more SREJs than a window of 4 could ever need");
    }

    // ─── #47: TimerRecovery drain loop decrements V(r) ────────────────

    [Fact]
    public void Defect47_Current_Tables_Timer_Recovery_Drain_Re_Delivers_A_Frame()
    {
        // Bidirectional so the receiver can be in TimerRecovery with a stored
        // frame when the missing one lands. Reject coherence is off because
        // the go-back-N retransmission that a poll answer triggers re-presents
        // the stored frame and the #42 arm SREJs it first (shorter path).
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 2, FramesBa = 1, K = 4, Srej = true, Budget = 3, Faults = FaultKinds.Drop,
            Invariants = Invariants.Default & ~Invariants.RejectCoherence,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Delivery);
        result.Violation.Station.Should().Be(Station.B);
        result.Violation.Message.Should().Contain("delivered `a0` upward").And.Contain("duplicate");
        result.Trace[^2].Transition.Should().Be("t22_i_received_yes_yes_yes_no_yes_no_no", "the figc4.5 in-sequence arm whose drain loop decrements V(r)");
        result.Trace[^2].Effects.Should().Contain("dl DL_DATA_indication [a1]", "the stored frame is drained");
        result.CounterexampleLength.Should().Be(20);
    }

    [Fact]
    public void Defect47_At_K2_Fires_With_Every_Invariant_On()
    {
        // At k = 2 a second out-of-sequence frame is outside the receive window
        // and discarded, so the #42 arm is unreachable and nothing masks the
        // drain-loop decrement: the same duplicate delivery, all checks on.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 1, FramesBa = 3, K = 2, Srej = true, Budget = 2, Faults = FaultKinds.Drop,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Delivery);
        result.Violation.Station.Should().Be(Station.A);
        result.Violation.Message.Should().Contain("delivered `b1` upward").And.Contain("duplicate");
        result.Trace.Should().Contain(step => step.Transition == "t22_i_received_yes_yes_yes_no_yes_no_yes"
            && step.Effects.Contains("dl DL_DATA_indication [b2]"), "the figc4.5 drain delivers the stored frame and then steps V(r) back");
        result.CounterexampleLength.Should().Be(24);
    }

    [Fact]
    public void Defect47_With_Reject_Coherence_On_The_Defect42_Arm_Fires_First()
    {
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 2, FramesBa = 1, K = 4, Srej = true, Budget = 3, Faults = FaultKinds.Drop,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.RejectCoherence);
        result.Violation.Message.Should().Contain("already holds I(ns=1)");
        result.CounterexampleLength.Should().BeLessThan(20);
    }

    // ─── #9: acknowledgement progress does not reset RC ───────────────

    [Fact]
    public void Defect9_Current_Tables_Link_Dies_With_Everything_Acknowledged()
    {
        // Drops limited to interior I frames so no poll or acknowledgement is
        // ever lost: every frame is individually recoverable, and the peer's
        // acknowledgements keep making progress. The figure still counts every
        // T1 expiry against N2 and tears the link down (DL-ERROR T: everything
        // was acknowledged) once RC, never reset by progress, reaches N2.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 3, FramesBa = 0, K = 4, N2 = 2, Budget = 3,
            Faults = FaultKinds.Drop, DropScope = DropScope.InteriorI,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        var teardown = result.Trace[^2];
        teardown.Transition.Should().Be("t21_t1_expiry_yes_yes_no", "RC = N2 with V(s) = V(a): nothing was outstanding");
        teardown.Effects.Should().Contain("dl DL-ERROR Indication (T)").And.Contain("dl DL_DISCONNECT_indication");
        result.Trace[^1].SummaryA.Should().StartWith("Disconnected vs=3 va=3");
        result.Trace[^1].SummaryB.Should().StartWith("Disconnected");
        result.CounterexampleLength.Should().Be(30);
    }

    // ─── #13: window at half the modulus (protocol constraint) ────────

    private static ExplorerOptions Scenario13(int k) => new()
    {
        TablesDir = Fixtures.CurrentDir, FramesAb = 3, FramesBa = 0, K = k, Srej = false, Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate,
    };

    [Fact]
    public void Constraint13_K7_Wrapped_Duplicate_Raises_A_Reject_The_Sender_Cannot_Honour()
    {
        // Classified as the protocol's own constraint (k above half the
        // modulus), NOT a figure defect: a late copy of frame 0 lands two
        // frames after the original, sits at distance 6 < k = 7 inside the
        // receive window, and draws REJ nr=V(s). This is the brief's
        // "unsound by construction" trap; every other case runs at k <= 4.
        var result = Run(Scenario13(7));
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.RejectCoherence);
        result.Violation.Station.Should().Be(Station.A);
        result.Violation.Message.Should().Contain("REJ nr=2");
        result.CounterexampleLength.Should().Be(13);
    }

    [Fact]
    public void Constraint13_Same_Scenario_At_K4_Is_Clean()
    {
        Run(Scenario13(4)).Outcome.Should().Be(Outcome.NoViolation);
    }

    // ─── #44 / #48: connect-phase (Disconnected seed) ─────────────────

    [Fact]
    public void Defects44And48_A_V22_Connect_Refused_By_A_V20_Peer_Never_Comes_Up()
    {
        // A (modulo 128) sends SABME; B is a v2.0 peer that answers DM(F=1)
        // but would accept SABM. #44: A is in AwaitingConnection, not
        // AwaitingV22Connection. #48: the DM tears the attempt down instead
        // of degrading to SABM. Either way the link never comes up and A's
        // queued data is discarded: a deadlock, two steps from the seed.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, Seed = SeedKind.Disconnected, Modulo128A = true, PeerDeclinesSabme = true, FramesAb = 1, Budget = 0,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[0].SummaryA.Should().StartWith("AwaitingConnection", "#44: the SABME initiator lands in figc4.2");
        result.Trace[1].Transition.Should().Be("t03_dm_received_yes", "#48 class: DM to the SABME tears down");
        result.Trace[1].SummaryA.Should().StartWith("Disconnected");
        result.CounterexampleLength.Should().Be(2);
    }

    [Fact]
    public void Connect_Phase_Sanity_A_V20_Connect_Against_The_Same_Peer_Comes_Up_And_Drains()
    {
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, Seed = SeedKind.Disconnected, Modulo128A = false, PeerDeclinesSabme = true, FramesAb = 1, Budget = 0,
        });
        result.Outcome.Should().Be(Outcome.NoViolation);
    }

    // ─── Novel hypothesis H1 (docs/explorer.md): stuck in TimerRecovery ──

    [Fact]
    public void HypothesisH1_Timer_Recovery_Is_Never_Left_After_A_Lost_Poll_Is_Overtaken_By_The_Peers_Ack()
    {
        // Not a catalogued defect: recorded here so a change in the figures is
        // noticed. A's poll is lost; the peer's own poll (RR command) and then
        // its I frame acknowledge everything A sent; Check_I_Frame_Acknowledged
        // stops T1; figc4.5 only leaves TimerRecovery on an F=1 response and
        // has no T3 arm, so A sits in TimerRecovery with T1 stopped for good.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 2, FramesBa = 1, K = 4, Srej = false, Budget = 2, Faults = FaultKinds.Drop,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[^1].SummaryA.Should().StartWith("TimerRecovery vs=2 va=2").And.Contain("t1=stopped");
        result.Trace[^1].SummaryB.Should().StartWith("Connected");
        result.CounterexampleLength.Should().Be(19);
    }

    [Fact]
    public void HypothesisH1_Counting_Timer_Recovery_As_Quiescent_Makes_The_Same_Scenario_Clean()
    {
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 2, FramesBa = 1, K = 4, Srej = false, Budget = 2, Faults = FaultKinds.Drop,
            TimerRecoveryIsQuiescent = true,
        });
        result.Outcome.Should().Be(Outcome.NoViolation);
    }
}
