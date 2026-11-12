using Packet.Sdl.Explorer;
using Packet.Sdl.Interpreter;
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
        // of degrading to SABM. Either way the link never comes up: a
        // deadlock, two steps from the seed. (A's frame was never queued:
        // figc4.2 discards it while layer 3 initiated, ax25spec#161.)
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
    public void Connect_Phase_Sanity_A_V20_Connect_Against_The_Same_Peer_Comes_Up()
    {
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, Seed = SeedKind.Disconnected, Modulo128A = false, PeerDeclinesSabme = true, FramesAb = 0, Budget = 0,
        });
        result.Outcome.Should().Be(Outcome.NoViolation);
    }

    // ─── ax25spec#161: data offered before DL-CONNECT Confirm is discarded ──

    [Fact]
    public void Data_Offered_Before_The_Connect_Confirm_Is_Discarded_Silently()
    {
        // The Disconnected seed submits DL_CONNECT_request and then one
        // DL_DATA_request. figc4.2 draws no Yes/No on "Layer 3 Initiated?";
        // the July 1998 edition of the figure (Figure C4.3, printed page 87)
        // labels it Yes skips, No pushes, so discarding the frame while layer
        // 3 initiated is the figure's own intent, not a defect in the reading.
        // What the row shows: the frame is never queued and no indication
        // reaches layer 3 (the missing indication is the open point on
        // ax25spec#161). The link comes up and the frame is simply gone: a
        // deadlock with 0 of 1 delivered, two steps from the seed.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, Seed = SeedKind.Disconnected, Modulo128A = false, PeerDeclinesSabme = true, FramesAb = 1, Budget = 0,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[1].Transition.Should().Be("t04_ua_received_yes_yes");
        result.Trace[1].SummaryA.Should().StartWith("Connected");
        result.CounterexampleLength.Should().Be(2);
    }

    // ─── Cold connect, then data (layer 3 waits for the link) ─────────

    private static ExplorerOptions ColdThenData(bool mod128) => new()
    {
        TablesDir = Fixtures.CurrentDir, Seed = SeedKind.Disconnected, Modulo128A = mod128, DataAfterConfirm = true, Budget = 0,
    };

    [Fact]
    public void Cold_V20_Connect_Then_Data_Both_Ways_Drains_Without_A_Timeout()
    {
        // The well-behaved layer 3: A submits only after DL_CONNECT_confirm,
        // B only after DL_CONNECT_indication. SABM, UA, then two frames A to
        // B and one B to A, all delivered, no T1 expiry needed.
        var result = Run(ColdThenData(mod128: false) with { FramesAb = 2, FramesBa = 1, Invariants = Invariants.Default | Invariants.SelectiveProgress });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.FiredTransitions.Should().Contain(["Disconnected/t13_sabm_received_yes", "AwaitingConnection/t04_ua_received_yes_yes", "Connected/t02_dl_data_request"]);
        result.StatesExplored.Should().Be(210);
    }

    [Fact]
    public void Cold_V22_Connect_Then_Data_Comes_Up_Through_Figc42_And_Drains()
    {
        // A at modulo 128 sends SABME. #44 routes it to AwaitingConnection
        // (figc4.2), not AwaitingV22Connection; B accepts SABME. The link
        // comes up and a short exchange drains: the same 210 states as the
        // v2.0 connect, because nothing here depends on the modulus.
        var result = Run(ColdThenData(mod128: true) with { FramesAb = 2, FramesBa = 1, Invariants = Invariants.Default | Invariants.SelectiveProgress });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.FiredTransitions.Should().Contain(["Disconnected/t14_sabme_received_yes", "AwaitingConnection/t04_ua_received_yes_yes"]);
        result.FiredTransitions.Should().NotContain(t => t.StartsWith("AwaitingV22Connection/", StringComparison.Ordinal), "#44: a cold SABME never reaches figc4.6");
        result.StatesExplored.Should().Be(210);
    }

    [Fact]
    public void Defect54_Cold_V22_Connect_Wraps_Cleanly_Because_Set_Version_2_2_Sets_Modulo_128()
    {
        // d54-cold-v22-wrap. figc4.1's SABME arm draws "Set Version 2.2" as a
        // processing box named after the figc4.7b subroutine; since
        // packet-net/ax25sdl#123 it sets the modulus and the reject mode as
        // well as the flag (pinned 9), so B accepting SABME runs modulo 128,
        // like A (seeded at 128 by hand: A's own connect path draws no Set
        // Version box). Nine frames at k = 1 wrap cleanly, as over a SABM
        // connect (d54-cold-v20-wrap).
        var result = Run(ColdThenData(mod128: true) with { FramesAb = 9, K = 1 });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(192);
        Run(ColdThenData(mod128: false) with { FramesAb = 9, K = 1 }).Outcome.Should().Be(Outcome.NoViolation);
    }

    [Fact]
    public void Defect54_Under_The_Flag_Only_Reading_B_Stays_At_Modulo_8_And_A_Loses_A_Frame()
    {
        // set-version-flag-only-v22: the twin fixture puts back what the old
        // flag-only reading did (B keeps modulo 8 and implicit reject after
        // Set Version 2.2). With
        // k = 1 and nine frames, B acknowledges a7 with N(r) = 0; A expects
        // 8, takes it as an N(r) error (DL-ERROR J), re-establishes, and the
        // re-establishment discards a8: deadlock with 8 of 9 delivered.
        var result = Run(ColdThenData(mod128: true) with { TablesDir = Fixtures.SetVersionFlagOnlyDir, FramesAb = 9, K = 1 });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Violation.Message.Should().Contain("at B: 8/9", "a8 is lost: 8 of 9 delivered");
        result.Trace[^3].Transition.Should().Be("t21_rr_received_no_no", "A's N(r) check fails on B's wrapped N(r)");
        result.Trace[^3].Effects.Should().Contain(e => e.Contains("DL-ERROR Indication (J)", StringComparison.Ordinal));
        result.Trace[^1].SummaryA.Should().StartWith("Connected vs=0 va=0 vr=0").And.Contain("q=[]");
        result.CounterexampleLength.Should().Be(45);
    }

    [Fact]
    public void A_Station_Configured_For_Modulo_128_That_Accepts_A_Sabm_Runs_Modulo_8()
    {
        // sabm-b-mod128-wrap (packet-net/ax25sdl#123): B is configured for
        // modulo 128 and accepts A's SABM; Set Version 2.0 brings it to
        // modulo 8 (4.3.3.1), so nine frames at k = 1 wrap cleanly. Its twin
        // set-version-flag-only-v20 keeps B at 128 after the box, as the old
        // flag-only reading did: B acknowledges a7 with N(r) = 8, which A,
        // at modulo 8, takes as V(a) = 8.
        var clean = Run(ColdThenData(mod128: false) with { Modulo128B = true, FramesAb = 9, K = 1 });
        clean.Outcome.Should().Be(Outcome.NoViolation);
        clean.StatesExplored.Should().Be(192);
        clean.FiredTransitions.Should().Contain("Disconnected/t13_sabm_received_yes");

        var flagOnly = Run(ColdThenData(mod128: false) with { TablesDir = Fixtures.SetVersionFlagOnlyDir, Modulo128B = true, FramesAb = 9, K = 1 });
        flagOnly.Outcome.Should().Be(Outcome.Violation);
        flagOnly.Violation!.Kind.Should().Be(Invariants.SequenceSanity);
        flagOnly.Violation.Station.Should().Be(Station.A);
        flagOnly.CounterexampleLength.Should().Be(42);
    }

    [Fact]
    public void Seeding_B_At_Modulo_128_Is_For_The_Disconnected_Seed_With_The_Table_Peer()
    {
        var act = () => new Stepper(new ExplorerOptions { TablesDir = Fixtures.CurrentDir, Modulo128B = true }, Fixtures.Tables(Fixtures.CurrentDir));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Data_After_Confirm_Is_For_The_Disconnected_Seed_Only()
    {
        var act = () => new Stepper(new ExplorerOptions { TablesDir = Fixtures.CurrentDir, DataAfterConfirm = true }, Fixtures.Tables(Fixtures.CurrentDir));
        act.Should().Throw<ArgumentException>();
    }

    // ─── #43: DL-FLOW-OFF acts on the wrong branch ────────────────────

    private static ExplorerOptions Scenario43 => new()
    {
        TablesDir = Fixtures.CurrentDir, FramesAb = 2, FramesBa = 0, K = 4, Budget = 0, FlowControl = FlowControlAt.B,
    };

    [Fact]
    public void Defect43_Flow_Off_At_A_Not_Busy_Station_Leaves_It_Not_Busy()
    {
        // B delivers a0 upward, its layer 3 turns flow off. figc4.4 draws the
        // Set-Own-Receiver-Busy / RNR chain on the "own receiver busy? yes"
        // arm, so a not-busy station takes the empty "no" arm and stays
        // not-busy and silent: layer 3 can never enter the busy condition
        // (§6.4.10) from a clean state.
        var result = Run(Scenario43);
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.BusyTracksFlow);
        result.Violation.Station.Should().Be(Station.B);
        result.Trace[^1].Transition.Should().Be("t05_dl_flow_off_request_no");
        result.Trace[^1].Effects.Should().BeEmpty("the not-busy arm does nothing: no RNR, no busy condition");
        result.Trace[^1].SummaryB.Should().NotContain("own_busy");
        result.CounterexampleLength.Should().Be(3);
    }

    [Fact]
    public void Defect43_With_The_Busy_Check_Off_Data_Arrives_While_Flow_Is_Off()
    {
        // The consequence layer 3 sees: the second frame is delivered upward
        // while flow is off, because nothing held it back.
        var result = Run(Scenario43 with { Invariants = Invariants.Default & ~Invariants.BusyTracksFlow });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.FlowOffDelivery);
        result.Violation.Station.Should().Be(Station.B);
        result.Violation.Message.Should().Contain("delivered `a1` upward while its layer 3 has flow off");
        result.Trace.Should().Contain(step => step.Transition == "t05_dl_flow_off_request_no");
        result.CounterexampleLength.Should().Be(5);
    }

    [Fact]
    public void Defect43_With_Both_Flow_Checks_Off_The_Flow_Moves_Are_No_Ops()
    {
        // On the current tables DL_FLOW_OFF / DL_FLOW_ON from a not-busy
        // station do nothing, so behind #43 the scenario is the plain 2-frame
        // exchange and it converges.
        var result = Run(Scenario43 with { Invariants = Invariants.Default & ~(Invariants.BusyTracksFlow | Invariants.FlowOffDelivery) });
        result.Outcome.Should().Be(Outcome.NoViolation);
    }

    [Fact]
    public void Defect43_Note_The_Busy_Arms_Emit_RNR_And_RR_Without_N_r_Staging()
    {
        // Not reachable by the explorer on the current tables (nothing sets
        // own-receiver-busy from not-busy, which is #43 itself), so pinned at
        // the interpreter where it will be noticed the moment the branches
        // are swapped upstream: the RNR on the DL-FLOW-OFF busy arm and the
        // RR Command on the DL-FLOW-ON busy arm (figc4.4 t05/t06 and the
        // figc4.5 twins) are drawn without an N(r) := V(r) staging box,
        // unlike every other RR/RNR emission in the figures (Transmit_Enquiry,
        // Enquiry_Response, the I-frame paths). Under the interpreter's
        // staging policy (docs/explorer.md, pinned semantic 3) they are
        // errors. Hypothesis H3 in docs/explorer.md.
        var tables = Fixtures.Tables(Fixtures.CurrentDir);
        foreach (var (state, ev, expected) in new[]
                 {
                     ("Connected", "DL_FLOW_OFF_request", "RNR Response"),
                     ("Connected", "DL_FLOW_ON_request", "RR Command"),
                     ("TimerRecovery", "DL_FLOW_OFF_request", "RNR Response (F = 0)"),
                     ("TimerRecovery", "DL_FLOW_ON_request", "RR Command (P = 0)"),
                 })
        {
            var m = new DataLinkMachine(tables, state);
            m.SetVariable("own_receiver_busy", "true");
            m.SetTimer("t1", TimerStatus.Running);
            var act = () => m.Dispatch(new EventInput(ev));
            act.Should().Throw<InvalidOperationException>()
                .WithMessage($"`{expected}` emitted without a preceding N(r) staging assignment*", $"{state} {ev} busy arm");
        }
    }

    // ─── #45 / #48: the v2.0 stub peer in the connect phase ───────────

    private static ExplorerOptions V20(SeedKind seed, PeerKind peer, bool timerFree) => new()
    {
        TablesDir = Fixtures.CurrentDir, Seed = seed, Modulo128A = seed == SeedKind.Disconnected, Peer = peer, FramesAb = 0, Budget = 0,
        Invariants = timerFree ? Invariants.Default | Invariants.SelectiveProgress : Invariants.Default,
    };

    [Fact]
    public void Defect45_Cold_V22_Connect_Never_Reaches_Figc46_So_The_Fallback_Is_Never_Attempted()
    {
        // The brief's seed: A Disconnected at modulo 128 issues
        // DL_CONNECT_request; B is a v2.0 station that answers SABME with
        // FRMR. #44 routes the SABME initiator to AwaitingConnection
        // (figc4.2), which has no FRMR arm: the FRMR is swallowed by the
        // "all other primitives" catch-all and the fallback of figc4.6 t14
        // is never even reached from a cold connect. The link only comes up
        // because figc4.2's T1 retry happens to send SABM, which the peer
        // accepts; so with the default checks the run is clean, and the
        // timer-free progress check is what sees it.
        var result = Run(V20(SeedKind.Disconnected, PeerKind.V20Frmr, timerFree: true));
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.SelectiveProgress);
        result.Trace[0].Transition.Should().StartWith("v20:sabme_unknown_control_field_frmr");
        result.Trace[1].Transition.Should().Be("t06_all_other_primitives__from_lower_layer", "#44: figc4.2 has no FRMR arm");
        result.Trace[1].SummaryA.Should().StartWith("AwaitingConnection");
        result.CounterexampleLength.Should().Be(2);

        var lenient = Run(V20(SeedKind.Disconnected, PeerKind.V20Frmr, timerFree: false));
        lenient.Outcome.Should().Be(Outcome.NoViolation, "the figc4.2 T1 retry sends SABM and the v2.0 peer accepts it; A stays at modulo 128 (figc4.2's UA arm draws no Set Version box: the initiator side of #54, which ax25sdl#123 does not change), which this abstract frame model cannot see");
    }

    [Fact]
    public void Defect45_From_AwaitingV22Connection_The_Frmr_Fallback_Resends_Sabme()
    {
        // Seeded where figc4.6 applies (as #44's fix would route a cold
        // connect): the FRMR takes t14, whose Establish Data Link runs before
        // Set Version 2.0 and so sends SABME again while modulo 128 is still
        // in effect. The v2.0 peer FRMRs that too; A is now on figc4.2, which
        // ignores it, and only the T1 retry's SABM brings the link up.
        var result = Run(V20(SeedKind.AwaitingV22Connection, PeerKind.V20Frmr, timerFree: true));
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.SelectiveProgress);
        result.Trace[1].Transition.Should().Be("t14_frmr_received");
        result.Trace[1].Effects.Should().Contain("frame SABME command pf=1", "#45: the fallback re-establishes with SABME, not SABM");
        result.Trace[1].SummaryA.Should().StartWith("AwaitingConnection");
        result.Trace[2].Transition.Should().StartWith("v20:sabme_unknown_control_field_frmr");
        result.Trace[3].Transition.Should().Be("t06_all_other_primitives__from_lower_layer");
        result.CounterexampleLength.Should().Be(4);

        Run(V20(SeedKind.AwaitingV22Connection, PeerKind.V20Frmr, timerFree: false)).Outcome.Should().Be(Outcome.NoViolation,
            "with a timeout allowed the figc4.2 retry rescues the connect");
    }

    [Fact]
    public void Defect48_Dm_To_Sabme_In_AwaitingV22Connection_Tears_Down()
    {
        // figc4.6's DM column: F=1 (what a well-behaved pre-v2.2 peer sends
        // to a polled SABME, and what XRouter sends on the wire) goes
        // straight to Disconnected with no fallback. One deliver each way.
        var result = Run(V20(SeedKind.AwaitingV22Connection, PeerKind.V20Dm, timerFree: false));
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[0].Transition.Should().StartWith("v20:sabme_is_not_sabm_dm_f1");
        result.Trace[1].Transition.Should().Be("t11_dm_received_yes");
        result.Trace[1].SummaryA.Should().StartWith("Disconnected");
        result.CounterexampleLength.Should().Be(2);

        // The table-driven peer refusing with DM (able_to_establish = false) lands on the same arm.
        var tables = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, Seed = SeedKind.AwaitingV22Connection, PeerDeclinesSabme = true, FramesAb = 0, Budget = 0,
        });
        tables.Outcome.Should().Be(Outcome.Violation);
        tables.Violation!.Kind.Should().Be(Invariants.Deadlock);
        tables.Trace[1].Transition.Should().Be("t11_dm_received_yes");
    }

    [Fact]
    public void Defect48_Cold_V22_Connect_Refused_With_Dm_Dies_On_Figc42()
    {
        // The brief's seed with the DM variant: same shape as the existing
        // #44/#48 case, now against the stub. figc4.2's DM F=1 arm tears down.
        var result = Run(V20(SeedKind.Disconnected, PeerKind.V20Dm, timerFree: false));
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[0].SummaryA.Should().StartWith("AwaitingConnection", "#44");
        result.Trace[1].Transition.Should().Be("t03_dm_received_yes", "#48 class on figc4.2");
        result.CounterexampleLength.Should().Be(2);
    }

    [Fact]
    public void Connect_Phase_Sanity_The_V20_Peer_Accepts_A_Mod8_Connect_Without_A_Timeout()
    {
        // No fixed tables exist to discriminate #45/#48 against; the control
        // is the same peer with a v2.0 initiator: SABM, UA, up, no timer.
        foreach (var peer in new[] { PeerKind.V20Frmr, PeerKind.V20Dm })
        {
            var result = Run(V20(SeedKind.Disconnected, peer, timerFree: true) with { Modulo128A = false });
            result.Outcome.Should().Be(Outcome.NoViolation, $"{peer}");
        }
    }

    [Fact]
    public void Connect_Phase_Sanity_A_V22_Peer_Answers_The_Seeded_Sabme_With_Ua()
    {
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, Seed = SeedKind.AwaitingV22Connection, FramesAb = 0, Budget = 0,
            Invariants = Invariants.Default | Invariants.SelectiveProgress,
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

    // ─── Coverage batch 1 (ax25spec#116 Phase 2): rows no earlier case reaches ──
    //
    // Each of these exists to fire table rows the rest of the suite never
    // reaches (docs/tla.md, the coverage paragraph). A deadlock is reported
    // only after the whole graph is explored, so a deadlock row fires every
    // reachable row even though its verdict is the shortest deadlock.

    [Fact]
    public void Defect114_A_Duplicated_Ua_Resets_An_Up_Link_And_Both_Queues_Are_Lost()
    {
        // Cold connect at modulo 8 with version 2.2 set, each layer 3 holding
        // one frame until its link is up. A's UA arrives twice: the first
        // connects A, the second hits Connected, where figc4.4 t17 resets the
        // link (ax25spec#114) and discards A's queued frame; B takes the reset
        // SABM on figc4.4 t14, whose Clear Exception Conditions discards B's
        // queue too (the #50 class). Both links come back up with nothing to send.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, Seed = SeedKind.Disconnected, DataAfterConfirm = true,
            FramesAb = 1, FramesBa = 1, Srej = true, Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[1].Transition.Should().Be("t04_ua_received_yes_yes");
        result.Trace[3].Transition.Should().Be("t17_ua_received_yes", "#114: the duplicate UA resets an up link");
        result.Trace[5].Transition.Should().Be("t14_sabm_received_yes", "#50: the reset discards the responder's queue");
        result.Violation.Message.Should().Contain("delivered at A: 0/1, at B: 0/1");
        result.CounterexampleLength.Should().Be(7);
    }

    [Fact]
    public void Defect114_At_Modulo_128_The_Duplicated_Ua_Resets_Through_The_Modulo_8_Arm()
    {
        // The same duplicate at modulo 128: A's version 2.2 flag is clear (the
        // tables never set it on a SABME connect, #44/#54), so figc4.4 takes
        // t17_ua_received_no and re-establishes with SABME from Awaiting
        // Connection; B resets on it and discards its frame.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, Seed = SeedKind.Disconnected, Modulo128A = true, DataAfterConfirm = true,
            FramesAb = 0, FramesBa = 1, N2 = 2, Budget = 1, Faults = FaultKinds.Duplicate,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[2].Transition.Should().Be("t17_ua_received_no");
        result.Trace[4].Transition.Should().Be("t15_sabme_received_yes");
        result.Violation.Message.Should().Contain("delivered at A: 0/1");
        result.CounterexampleLength.Should().Be(6);
    }

    [Fact]
    public void Defect44_48_Cold_Connect_With_Faults_Still_Dies_On_The_Dm_First()
    {
        // The #44/#48 seed with B's data after its indication, drops and
        // duplicates: a wider graph, the same two-step deadlock.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, Seed = SeedKind.Disconnected, Modulo128A = true, PeerDeclinesSabme = true, DataAfterConfirm = true,
            FramesAb = 0, FramesBa = 1, Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[1].Actor.Should().Be("A");
        result.Trace[1].Transition.Should().Be("t03_dm_received_yes");
        result.Trace[^1].SummaryA.Should().StartWith("Disconnected", "A is the station the DM tears down");
        result.CounterexampleLength.Should().Be(2);
    }

    [Fact]
    public void Defect45_Cold_With_N2_Of_2_The_Swallowed_Frmr_Costs_The_Connect()
    {
        // #44 masks #45 with N2 = 2: the FRMR is swallowed by figc4.2's
        // catch-all, so one try is gone; the T1 retry's SABM is dropped, and
        // the second expiry gives up (t05_t1_expiry_yes). With the fallback
        // the figures intend, the FRMR would have been answered by a SABM at
        // once and the retry would have connected.
        var result = Run(V20(SeedKind.Disconnected, PeerKind.V20Frmr, timerFree: false) with { N2 = 2, Budget = 1, Faults = FaultKinds.Drop });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[1].Transition.Should().Be("t06_all_other_primitives__from_lower_layer");
        result.Trace[^1].Transition.Should().Be("t05_t1_expiry_yes");
        result.Trace[^1].SummaryA.Should().StartWith("Disconnected");
        result.CounterexampleLength.Should().Be(5);

        Run(V20(SeedKind.Disconnected, PeerKind.V20Frmr, timerFree: false) with { Budget = 2, Faults = FaultKinds.Duplicate })
            .Outcome.Should().Be(Outcome.NoViolation, "duplicated FRMR, SABM and UA all resolve");
    }

    [Theory]
    [InlineData(1, 2, 1, 15, Station.A, 12)]
    [InlineData(3, 1, 2, 17, Station.B, 15)]
    public void HypothesisH1_Also_Strands_A_Station_At_Other_Windows(int ab, int ba, int k, int length, Station stuck, int strandingStep)
    {
        // ax25spec#91 (H1) again, from scenarios that reach figc4.5 I-frame
        // arms the h1 row does not: the station is left in TimerRecovery with
        // T1 stopped and nothing outstanding.
        var faults = k == 1 ? FaultKinds.Drop | FaultKinds.Duplicate : FaultKinds.Drop;
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = ab, FramesBa = ba, K = k, Budget = 2, Faults = faults,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        var summary = stuck == Station.A ? result.Trace[^1].SummaryA : result.Trace[^1].SummaryB;
        summary.Should().StartWith("TimerRecovery").And.Contain("t1=stopped");
        // The stranding step: an I frame acknowledging everything outstanding
        // arrives in TimerRecovery; Check_I_Frame_Acknowledged stops T1 and
        // nothing on figc4.5 ever takes the station back to Connected.
        var strand = result.Trace[strandingStep - 1];
        strand.Actor.Should().Be(stuck.ToString());
        strand.Transition.Should().Be("t22_i_received_yes_yes_yes_no_yes_no_no");
        result.CounterexampleLength.Should().Be(length);
    }

    // ─── The orderly release (ExplorerOptions.Release) ─────────────────

    [Fact]
    public void Release_After_Delivery_Disconnects_Both_Stations_Cleanly()
    {
        // A's layer 3 issues DL_DISCONNECT_request once both of its frames are
        // delivered: DISC, UA, both Disconnected, which is quiescence here.
        var result = Run(new ExplorerOptions { TablesDir = Fixtures.CurrentDir, FramesAb = 2, Release = ReleaseBy.A });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(44);
    }

    [Fact]
    public void Release_By_Either_Station_After_A_Cold_Connect_With_Faults_Is_Clean()
    {
        // Either layer 3 may release (so both DISCs can cross), with drops and
        // duplicates on the connect and on the release.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, Seed = SeedKind.Disconnected, FramesAb = 0, Srej = true,
            Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate, Release = ReleaseBy.Both,
        });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(829);
        result.FiredTransitions.Should().Contain(new[]
        {
            "Connected/t01_dl_disconnect_request", "Connected/t19_disc_received", "AwaitingRelease/t03_ua_received_yes",
            "AwaitingRelease/t12_disc_received", "AwaitingRelease/t02_t1_expiry_no", "Disconnected/t12_disc_received",
        });
    }

    [Fact]
    public void Release_Masks_Hypothesis_H1()
    {
        // The h1-k1 scenario with A's layer 3 releasing: the station stranded
        // in TimerRecovery (#91) is taken out of it by DL_DISCONNECT_request,
        // so the run is clean. Nothing is fixed: the release masks #91, and a
        // release scenario cannot see it (h1-k1 without the release does).
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 1, FramesBa = 2, K = 1, Budget = 2,
            Faults = FaultKinds.Drop | FaultKinds.Duplicate, Release = ReleaseBy.A,
        });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.FiredTransitions.Should().Contain("TimerRecovery/t01_dl_disconnect_request");
        result.StatesExplored.Should().Be(2890);
    }

    [Fact]
    public void Release_Is_For_The_Table_Driven_Peer_Only()
    {
        var act = () => Fixtures.Run(V20(SeedKind.Disconnected, PeerKind.V20Frmr, timerFree: false) with { Release = ReleaseBy.A });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Seeded_A_Station_Left_Connected_After_A_Release_Is_Stuck_Not_Released()
    {
        // The seeded fixture answers DISC with UA but leaves B Connected: the
        // release ends with A Disconnected and B not, which the release rest
        // must refuse. The same scenario on the real tables is release-a.
        var result = Run(new ExplorerOptions { TablesDir = Fixtures.ReleaseStuckDir, FramesAb = 2, Release = ReleaseBy.A });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[5].Actor.Should().Be("B");
        result.Trace[5].Transition.Should().Be("t19_disc_received", "the seeded arm: UA, but B stays Connected");
        result.Trace[6].Transition.Should().Be("t03_ua_received_yes");
        result.Trace[^1].SummaryA.Should().StartWith("Disconnected");
        result.Trace[^1].SummaryB.Should().StartWith("Connected");
        result.CounterexampleLength.Should().Be(9);
    }

    // ─── Coverage batch 2: the give-up rule (ExplorerOptions.AcceptGiveUp) ──

    [Fact]
    public void GiveUp_Seeded_A_Peer_That_Ignores_The_Dm_Is_Not_Settled()
    {
        // The seeded fixture ignores a DM in Connected. A gives up honestly
        // from TimerRecovery and its DM reaches B, which stays up: B heard the
        // give-up, so it is not settled, and the run is a plain deadlock. On
        // the real tables B disconnects and the same state is the give-up loss.
        var o = new ExplorerOptions
        {
            TablesDir = Fixtures.DmIgnoredDir, FramesAb = 1, N2 = 1, Budget = 2, Faults = FaultKinds.Drop, AcceptGiveUp = true,
        };
        var result = Run(o);
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[4].Transition.Should().Be("t21_t1_expiry_yes_no");
        result.Trace[5].Actor.Should().Be("B");
        result.Trace[5].Transition.Should().Be("t20_dm_received", "the seeded arm: the DM is ignored");
        result.Trace[^1].SummaryB.Should().StartWith("Connected");
        result.CounterexampleLength.Should().Be(6);

        var real = Run(o with { TablesDir = Fixtures.CurrentDir });
        real.Violation!.Kind.Should().Be(Invariants.GiveUpLoss);
        real.CounterexampleLength.Should().Be(6);
    }

    [Fact]
    public void GiveUp_An_Honest_Connect_Give_Up_Is_Rest_Only_With_The_Rule()
    {
        // From figc4.6 with N2 = 2 and two drops, both SABMEs are lost and A
        // gives up (t13_t1_expiry_yes, DL-ERROR G) with B never having heard
        // anything. Without the rule that is a deadlock; with it, two losses
        // are as many as the connect loop has attempts, so it is rest.
        var o = new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, Seed = SeedKind.AwaitingV22Connection, FramesAb = 0, N2 = 2, Budget = 2, Faults = FaultKinds.Drop,
        };
        var without = Run(o);
        without.Outcome.Should().Be(Outcome.Violation);
        without.Violation!.Kind.Should().Be(Invariants.Deadlock);
        without.Trace[^1].Transition.Should().Be("t13_t1_expiry_yes");
        without.CounterexampleLength.Should().Be(4);

        var with = Run(o with { AcceptGiveUp = true });
        with.Outcome.Should().Be(Outcome.NoViolation);
        with.StatesExplored.Should().Be(17);
        with.FiredTransitions.Should().Contain("AwaitingV22Connection/t13_t1_expiry_yes");
    }

    [Fact]
    public void GiveUp_An_Honest_Release_Give_Up_Is_Rest()
    {
        // A releases with nothing to send; both DISCs are lost (N2 = 1, so the
        // release loop has two attempts) and A gives up from AwaitingRelease,
        // leaving B Connected with nothing outstanding, which T3 (not modelled)
        // would later find out.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 0, N2 = 1, Budget = 2, Faults = FaultKinds.Drop, Release = ReleaseBy.A, AcceptGiveUp = true,
        });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(15);
        result.FiredTransitions.Should().Contain("AwaitingRelease/t02_t1_expiry_yes");
    }

    [Fact]
    public void GiveUp_Data_Lost_In_An_Honest_Give_Up_Is_Reported_As_Such()
    {
        // B's frame and then its poll are lost: B gives up honestly in
        // TimerRecovery (DL-ERROR I, two losses for a loop of N2 + 1 = 2
        // attempts) and A disconnects on the DM, with b0 never delivered. That
        // is not rest: it is reported as a give-up loss.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 0, FramesBa = 1, N2 = 1, Budget = 2, Faults = FaultKinds.Drop, Release = ReleaseBy.A, AcceptGiveUp = true,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.GiveUpLoss);
        result.Violation.Station.Should().BeNull();
        result.Trace[4].Actor.Should().Be("B");
        result.Trace[4].Transition.Should().Be("t21_t1_expiry_yes_no");
        result.Violation.Message.Should().Contain("delivered at A: 0/1");
        result.CounterexampleLength.Should().Be(6);
        result.FiredTransitions.Should().Contain(["AwaitingRelease/t13_dm_received_no", "Disconnected/t05_all_other_commands"]);
    }

    [Fact]
    public void GiveUp_The_Rule_Does_Not_Excuse_Defect9()
    {
        // d9 with the rule on: every loss was recovered and acknowledged, so
        // the give-up at the end has fewer losses since the last progress than
        // the loop has attempts. It stays the #9 deadlock, at the same length.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 3, FramesBa = 0, K = 4, N2 = 2, Budget = 3,
            Faults = FaultKinds.Drop, DropScope = DropScope.InteriorI, AcceptGiveUp = true,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[^2].Transition.Should().Be("t21_t1_expiry_yes_yes_no");
        result.CounterexampleLength.Should().Be(30);

        // Nor #44 masking #45: one drop where N2 = 2 needs two.
        var cold = Run(V20(SeedKind.Disconnected, PeerKind.V20Frmr, timerFree: false) with { N2 = 2, Budget = 1, Faults = FaultKinds.Drop, AcceptGiveUp = true });
        cold.Outcome.Should().Be(Outcome.Violation);
        cold.Violation!.Kind.Should().Be(Invariants.Deadlock);
        cold.Trace[^1].Transition.Should().Be("t05_t1_expiry_yes");
        cold.CounterexampleLength.Should().Be(5);
    }

    [Fact]
    public void GiveUp_Defect9_At_N2_Of_1_Kills_A_Link_Whose_Data_Was_All_Acknowledged()
    {
        // B's frame is lost; B polls, retransmits, and A's RR F=0 acknowledges
        // it in TimerRecovery (t18_rr_received_no_no_yes: V(a) moves, T1 and
        // RC do not). The next T1 expiry finds RC = N2 and tears the link down
        // with everything delivered (DL-ERROR T). One loss cannot justify a
        // loop of two attempts, so the rule leaves it a deadlock: #9.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 0, FramesBa = 1, N2 = 1, Budget = 1, Faults = FaultKinds.Drop, Release = ReleaseBy.A, AcceptGiveUp = true,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[8].Transition.Should().Be("t18_rr_received_no_no_yes");
        result.Trace[9].Actor.Should().Be("B");
        result.Trace[9].Transition.Should().Be("t21_t1_expiry_yes_yes_no");
        result.Violation.Message.Should().Contain("delivered at A: 1/1");
        result.CounterexampleLength.Should().Be(11);
    }

    // ─── Coverage batch 2: k = 7 and reordering ───────────────────────

    [Fact]
    public void K7_With_Srej_A_Reject_Naming_V_s_Reaches_The_Figc45_Arms_For_It()
    {
        // At k = 7 (above half the modulus, #13) a reject can name V(s). With
        // reject coherence on that is the #13 verdict; off, the figc4.5 REJ
        // and SREJ arms for N(r) = V(s) are reached and the run is clean.
        var o = new ExplorerOptions { TablesDir = Fixtures.CurrentDir, FramesAb = 2, K = 7, Srej = true, Budget = 2, Faults = FaultKinds.Drop };
        var on = Run(o);
        on.Outcome.Should().Be(Outcome.Violation);
        on.Violation!.Kind.Should().Be(Invariants.RejectCoherence);
        on.CounterexampleLength.Should().Be(14);

        var off = Run(o with { Invariants = Invariants.Default & ~Invariants.RejectCoherence });
        off.Outcome.Should().Be(Outcome.NoViolation);
        off.StatesExplored.Should().Be(149);
        off.FiredTransitions.Should().Contain(["TimerRecovery/t23_rej_received_no_no_yes_yes", "TimerRecovery/t24_srej_received_yes_yes_yes_yes"]);
    }

    [Theory]
    [InlineData(false, "TimerRecovery/t18_rr_received_yes_no_no")]
    [InlineData(true, "TimerRecovery/t18_rr_received_yes_no_yes")]
    public void Reorder_At_K1_A_Stale_Poll_Answer_Is_An_N_r_Error_And_Recovers(bool srej, string arm)
    {
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 1, K = 1, Srej = srej, Budget = 3,
            Faults = FaultKinds.Reorder | FaultKinds.Drop | FaultKinds.Duplicate,
        });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(245);
        result.FiredTransitions.Should().Contain(arm);
    }

    [Fact]
    public void Reorder_A_Stale_Srej_Is_An_N_r_Error_And_Recovers()
    {
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 2, K = 2, Srej = true, Budget = 2, Faults = FaultKinds.Reorder,
            Invariants = Invariants.Default & ~Invariants.RejectCoherence,
        });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(60);
        result.FiredTransitions.Should().Contain("Connected/t24_srej_received_no_yes");
    }

    [Fact]
    public void Reorder_A_Stale_Rej_Resets_The_Link_And_The_Outstanding_Frame_Is_Lost()
    {
        // ax25spec#95 (reordering; docs/explorer.md H4). A reordered REJ nr=0 arrives after the RR nr=1 that
        // overtook it: N(r) is below V(a), figc4.4 takes it as an N(r) error
        // (DL-ERROR J) and re-establishes; B resets on the SABM (DL-ERROR F),
        // and a1, outstanding at A, is never sent again.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 2, K = 2, Budget = 3, Faults = FaultKinds.Reorder | FaultKinds.Drop,
            Invariants = Invariants.Default & ~Invariants.RejectCoherence,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[8].Actor.Should().Be("A");
        result.Trace[8].Transition.Should().Be("t25_rej_received_no_no");
        result.Trace[9].Transition.Should().Be("t14_sabm_received_yes");
        result.Violation.Message.Should().Contain("delivered at A: 0/0, at B: 1/2");
        result.CounterexampleLength.Should().Be(11);
        // Since ax25spec#180 B's stale RR leaves Timer Recovery for Awaiting
        // Connection, so the UA no longer arrives in Timer Recovery here.
        result.FiredTransitions.Should().Contain("TimerRecovery/t18_rr_received_no_no_no_no");
    }

    [Fact]
    public void Reorder_A_Stale_N_r_On_An_I_Frame_Resets_The_Link_And_Both_Ends_Lose_Data()
    {
        // ax25spec#95, the same family with every check on: A's I frame a0 (nr=0)
        // is overtaken by A's RR nr=1, so B finds N(r) below V(a), discards a0
        // and re-establishes; neither a0 nor b1 is ever delivered.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 1, FramesBa = 2, K = 1, Budget = 2, Faults = FaultKinds.Reorder | FaultKinds.Drop,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[6].Actor.Should().Be("B");
        result.Trace[6].Transition.Should().Be("t26_i_received_yes_yes_no_no");
        result.Violation.Message.Should().Contain("delivered at A: 1/2, at B: 0/1");
        result.CounterexampleLength.Should().Be(9);
        result.FiredTransitions.Should().Contain("TimerRecovery/t22_i_received_yes_yes_no");
    }

    [Fact]
    public void Reorder_An_I_Frame_From_Before_A_Reset_Is_Delivered_Again_After_It()
    {
        // ax25spec#95. A stale RR causes an N(r) error reset (as above); a copy of
        // a0 sent before the reset is reordered behind the SABM and arrives
        // after it, where N(s) = 0 = V(r) again, so B delivers a0 a second time.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 2, K = 2, Budget = 4, Faults = FaultKinds.Reorder | FaultKinds.Drop,
            Invariants = Invariants.Default & ~Invariants.RejectCoherence,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Delivery);
        result.Violation.Station.Should().Be(Station.B);
        result.Trace[14].Transition.Should().Be("t21_rr_received_no_no");
        result.Trace[16].Transition.Should().Be("t14_sabm_received_yes");
        result.Trace[^1].Transition.Should().Be("t26_i_received_yes_yes_yes_no_yes_no_no");
        result.CounterexampleLength.Should().Be(18);
        result.FiredTransitions.Should().Contain("TimerRecovery/t23_rej_received_no_no_no_no");
    }

    [Fact]
    public void Reorder_Scenario_With_Srej_Meets_The_Defect42_Runaway_First()
    {
        // The trace uses drops only: B's SREJ names the frame it just received
        // (#42) and the SREJ count passes k. Reached on the way: the figc4.5
        // SREJ arm for an out-of-window N(r).
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 2, K = 2, Srej = true, Budget = 4, Faults = FaultKinds.Reorder | FaultKinds.Drop,
            Invariants = Invariants.Default & ~Invariants.RejectCoherence,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.SequenceSanity);
        result.Violation.Station.Should().Be(Station.B);
        result.Trace[^1].Transition.Should().Be("t26_i_received_yes_yes_yes_no_no_yes_no_yes_yes");
        result.CounterexampleLength.Should().Be(16);
        result.FiredTransitions.Should().Contain("TimerRecovery/t24_srej_received_yes_no_yes");
    }

    // ─── Coverage batch 3: layer 3 primitives ──

    private static ExplorerOptions LinkDownScenario(int budget) => new()
    {
        TablesDir = Fixtures.CurrentDir, Seed = SeedKind.Disconnected, Modulo128A = true, DataAfterConfirm = true,
        FramesAb = 1, FramesBa = 1, Budget = budget, Faults = FaultKinds.Drop | FaultKinds.Duplicate,
    };

    [Fact]
    public void Layer3_Link_Down_The_Scenario_That_Was_A_MachineError_Now_Explores_Its_Whole_Graph()
    {
        // On main, A's layer 3 kept submitting after DL_DISCONNECT_indication
        // and the DL_DATA_request reached Disconnected, which had no arm for
        // it: a MachineError at 15 steps. Now an upper-layer primitive with no
        // arm goes to the page's catch-all, and layer 3 stops submitting when
        // told the link is down, so the search runs to the end and reports the
        // shortest deadlock: the duplicated UA of #114 resetting the link (#50).
        var result = Run(LinkDownScenario(2));
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[3].Transition.Should().Be("t17_ua_received_no");
        result.CounterexampleLength.Should().Be(7);
        result.StatesExplored.Should().Be(3644);
        result.FiredTransitions.Should().NotContain("Disconnected/t04_all_other_primitives__from_upper_layer",
            "a layer 3 told its link is down submits nothing until the link is reported up again");
    }

    [Fact]
    public void Layer3_Link_Down_Stops_Submitting_And_Resumes_After_The_Next_Indication()
    {
        // The path to main's MachineError, replayed with one more drop in the
        // budget: A's link goes down (DM, DL_DISCONNECT_indication) with a0
        // not yet offered, so A's layer 3 holds it; B's retried SABME then
        // reaches A, A reports DL_CONNECT_indication, and a0 is offered.
        var o = LinkDownScenario(3);
        var stepper = new Stepper(o, Fixtures.Tables(o.TablesDir));
        var s = stepper.Seed();
        Move[] path =
        [
            new(MoveKind.Drop, Station.B), new(MoveKind.T1Expiry, Station.A), new(MoveKind.Duplicate, Station.B),
            new(MoveKind.Deliver, Station.A), new(MoveKind.Submit, Station.B), new(MoveKind.Pop, Station.B),
            new(MoveKind.Deliver, Station.A), new(MoveKind.SeizeConfirm, Station.A), new(MoveKind.Deliver, Station.B),
            new(MoveKind.Deliver, Station.A), new(MoveKind.Deliver, Station.B), new(MoveKind.Deliver, Station.A),
            new(MoveKind.Deliver, Station.B), new(MoveKind.Deliver, Station.A),
        ];
        var n = 0;
        foreach (var move in path)
        {
            stepper.EnabledMoves(s).Should().Contain(move, $"step {n + 1}");
            var outcome = stepper.Apply(s, move, ++n);
            output.WriteLine(outcome.Record.Render());
            outcome.Violation.Should().BeNull();
            s = outcome.Next;
        }
        s.A.State.Should().Be("Disconnected");
        s.LinkUpA.Should().BeFalse();
        s.OfferedA.Should().Be(0);
        stepper.EnabledMoves(s).Should().NotContain(new Move(MoveKind.Submit, Station.A));

        // The UA A sent B is lost, B's T1 retries the SABME, A accepts it.
        foreach (var move in new Move[] { new(MoveKind.Drop, Station.B), new(MoveKind.T1Expiry, Station.B), new(MoveKind.Deliver, Station.A) })
        {
            stepper.EnabledMoves(s).Should().Contain(move);
            var outcome = stepper.Apply(s, move, ++n);
            output.WriteLine(outcome.Record.Render());
            outcome.Violation.Should().BeNull();
            s = outcome.Next;
        }
        s.A.State.Should().Be("Connected");
        s.LinkUpA.Should().BeTrue();
        stepper.EnabledMoves(s).Should().Contain(new Move(MoveKind.Submit, Station.A));
    }

    [Fact]
    public void Layer3_Link_Down_With_A_Third_Fault_Pins_The_State_Count()
    {
        // TlaCheck row l3-down-mod128-b3: the verdict is unchanged, but the
        // graph depends on layer 3 stopping on the disconnect, so the state
        // count is pinned here and compared with TLC's.
        var result = Run(LinkDownScenario(3));
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.CounterexampleLength.Should().Be(7);
        result.StatesExplored.Should().Be(12735);
    }

    [Fact]
    public void Reconnect_On_A_Live_Or_Waiting_Link_Is_Answered_By_The_Confirm()
    {
        // DL_CONNECT_request once everything is delivered: from Connected
        // (v2.0 and, with the version 2.2 flag, through Awaiting V2.2
        // Connection), from Awaiting Connection and from Awaiting V2.2
        // Connection, with faults on the last two. Each layer 3 gets its
        // DL_CONNECT_confirm and the link ends up.
        (ExplorerOptions Options, int States, string Arm)[] cases =
        [
            (new() { TablesDir = Fixtures.CurrentDir, FramesAb = 2, Reconnect = ReconnectBy.A }, 40, "Connected/t07_dl_connect_request_no"),
            (new() { TablesDir = Fixtures.CurrentDir, FramesAb = 2, Srej = true, Reconnect = ReconnectBy.Both }, 150, "Connected/t07_dl_connect_request_yes"),
            (new() { TablesDir = Fixtures.CurrentDir, Seed = SeedKind.Disconnected, FramesAb = 0, Reconnect = ReconnectBy.A }, 8, "AwaitingConnection/t07_dl_connect_request"),
            (new()
            {
                TablesDir = Fixtures.CurrentDir, Seed = SeedKind.AwaitingV22Connection, FramesAb = 0, Budget = 2,
                Faults = FaultKinds.Drop | FaultKinds.Duplicate, Reconnect = ReconnectBy.A,
            }, 121, "AwaitingV22Connection/t02_dl_connect_request"),
            (new()
            {
                TablesDir = Fixtures.CurrentDir, Seed = SeedKind.Disconnected, FramesAb = 0, Budget = 2,
                Faults = FaultKinds.Drop | FaultKinds.Duplicate, Reconnect = ReconnectBy.Both,
            }, 1139, "Connected/t07_dl_connect_request_no"),
        ];
        foreach (var (options, states, arm) in cases)
        {
            var result = Run(options);
            result.Outcome.Should().Be(Outcome.NoViolation);
            result.StatesExplored.Should().Be(states);
            result.FiredTransitions.Should().Contain(arm);
        }
    }

    [Fact]
    public void Reconnect_From_Timer_Recovery_Is_Never_Confirmed()
    {
        // ax25spec#167, which shows through #114. A's RR is lost, A polls from
        // Timer Recovery, then its layer 3 resets the link: figc4.5 sends SABM
        // but stays in Timer Recovery. The poll answer takes A to Connected,
        // so the UA answering A's own SABM is "unexpected" (figc4.4 t17): DL-ERROR
        // K, a second reset with Layer 3 Initiated cleared, and the link comes
        // up through t04_ua_received_yes_no_yes, which sends no
        // DL_CONNECT_confirm. Everything is delivered and both ends are
        // Connected; A's layer 3 never hears back. Controls (docs/explorer.md):
        // with only t07's next state set to Awaiting Connection, as the July
        // 1998 figure draws it, the run is clean; with #114 fixed and the slip
        // kept, B delivers a0 twice.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 1, Budget = 1, Faults = FaultKinds.Drop, Reconnect = ReconnectBy.A,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[5].Transition.Should().Be("t07_dl_connect_request");
        result.Trace[5].SummaryA.Should().StartWith("TimerRecovery");
        result.Trace[7].Transition.Should().Be("t18_rr_received_yes_yes_yes");
        result.Trace[9].Transition.Should().Be("t17_ua_received_no");
        result.Trace[11].Transition.Should().Be("t04_ua_received_yes_no_yes");
        result.Trace[11].Effects.Should().BeEmpty();
        result.Trace[^1].SummaryA.Should().StartWith("Connected");
        result.Trace[^1].SummaryB.Should().StartWith("Connected");
        result.Violation.Message.Should().Contain("delivered at A: 0/0, at B: 1/1");
        result.CounterexampleLength.Should().Be(12);
    }

    [Fact]
    public void Reconnect_Of_A_Mod128_Link_Crosses_Into_A_Dm_And_One_End_Goes_Down()
    {
        // The #44 link: A at modulo 128 with the version 2.2 flag clear
        // (seeded by hand), B at modulo 128 with the flag set (Set Version
        // 2.2 on the SABME it accepted, pinned 9, packet-net/ax25sdl#123).
        // Both reset. B resets through Awaiting V2.2 Connection sending SABME
        // (Establish_Data_Link follows the modulus); A resets through
        // Awaiting Connection, sending SABME too. B's SABME reaches A there
        // and figc4.2 answers DM (the crossing of #115). B tears down on the
        // DM (#48) while A comes up on B's UA. Until #123 B stayed at modulo
        // 8, sent SABM first, and needed that SABM lost and a T1 retry to
        // reach the same end (10 steps); now no fault is used.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, Seed = SeedKind.Disconnected, Modulo128A = true, FramesAb = 0,
            Budget = 1, Faults = FaultKinds.Drop, Reconnect = ReconnectBy.Both,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[3].Actor.Should().Be("B");
        result.Trace[3].Transition.Should().Be("t07_dl_connect_request_yes");
        result.Trace[3].Effects.Should().ContainSingle(e => e.Contains("SABME command", StringComparison.Ordinal));
        result.Trace[4].Transition.Should().Be("t17_sabme_received");
        result.Trace[7].Transition.Should().Be("t11_dm_received_yes");
        result.Trace[^1].SummaryA.Should().StartWith("Connected");
        result.Trace[^1].SummaryB.Should().StartWith("Disconnected");
        result.CounterexampleLength.Should().Be(8);
    }

    [Fact]
    public void Seeded_A_Reconnect_That_Is_Never_Answered_Is_Stuck()
    {
        // The seeded fixture ignores DL-CONNECT Request in Connected: the link
        // stays up and quiet with everything delivered, but A's layer 3 never
        // hears back. The answer rule must refuse that as rest; reconnect-a on
        // the real tables is clean.
        var result = Run(new ExplorerOptions { TablesDir = Fixtures.ReconnectSwallowedDir, FramesAb = 2, Reconnect = ReconnectBy.A });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[4].Transition.Should().Be("t07_dl_connect_request_no", "the seeded arm: no SABM, no state change");
        result.Trace[4].Effects.Should().BeEmpty();
        result.Trace[^1].SummaryA.Should().StartWith("Connected");
        result.CounterexampleLength.Should().Be(7);
    }

    [Fact]
    public void Seeded_A_Connect_Reported_As_A_Disconnect_Does_Not_Answer_The_Reconnect()
    {
        // The seeded fixture reports the completed connect as
        // DL_DISCONNECT_indication while A goes Connected. That indication
        // would answer A's reconnect; the report rule (at rest each layer 3's
        // last report matches its station's state) refuses it as rest.
        // reconnect-cold on the real tables is clean.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.ConfirmAsDisconnectDir, Seed = SeedKind.Disconnected, FramesAb = 0, Reconnect = ReconnectBy.A,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[^1].Transition.Should().Be("t04_ua_received_yes_yes", "the seeded arm");
        result.Trace[^1].Effects.Should().ContainSingle(e => e.Contains("DL_DISCONNECT_indication", StringComparison.Ordinal));
        result.Trace[^1].SummaryA.Should().StartWith("Connected");
        result.CounterexampleLength.Should().Be(3);
    }

    [Fact]
    public void Reconnect_Is_For_The_Table_Driven_Peer_Only()
    {
        var act = () => Fixtures.Run(V20(SeedKind.Disconnected, PeerKind.V20Frmr, timerFree: false) with { Reconnect = ReconnectBy.A });
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(SeedKind.Disconnected, "AwaitingConnection", "t04_ua_received_yes_yes")]
    [InlineData(SeedKind.AwaitingV22Connection, "AwaitingV22Connection", "t12_ua_received_yes_yes")]
    public void Release_While_Connecting_Is_Held_And_Acted_On_Once_Connected(SeedKind seed, string holding, string connect)
    {
        // packet-net/ax25sdl#97. Figures C4.2 and C4.6 draw a Save (Requeue)
        // for DL-DISCONNECT Request while awaiting the connection. Until the
        // explorer routed through saves the request went to the upper-layer
        // catch-all and the link came up regardless (a 3-step deadlock); now
        // it is held, and replayed once the link is up (pinned semantic 19).
        var options = new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, Seed = seed, FramesAb = 0, Release = ReleaseBy.A, ReleaseEarly = true,
        };
        var result = Run(options);
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(8);

        var trace = Walk(options, MoveKind.Release, MoveKind.Deliver, MoveKind.Deliver);
        trace[0].Transition.Should().Be(DataLinkMachine.SaveTransitionId);
        trace[0].SummaryA.Should().StartWith(holding).And.Contain("held=[DL_DISCONNECT_request]");
        trace[2].Transition.Should().Be(connect);
        trace[2].Replayed.Should().Equal("Connected/t01_dl_disconnect_request");
        trace[2].Effects.Should().Contain(e => e.Contains("DL_CONNECT_confirm", StringComparison.Ordinal))
            .And.Contain(e => e.Contains("DISC", StringComparison.Ordinal));
        trace[2].SummaryA.Should().StartWith("AwaitingRelease");
    }

    // ─── Abandoning a connect (ExplorerOptions.Abandon, packet-net/ax25sdl#97) ──

    private static ExplorerOptions AbandonCold(string tables) => new()
    {
        TablesDir = tables, Seed = SeedKind.Disconnected, FramesAb = 0, Abandon = ReleaseBy.A,
    };

    private static ExplorerOptions AbandonFallback(string tables) => new()
    {
        TablesDir = tables, Seed = SeedKind.AwaitingV22Connection, FramesAb = 0, Peer = PeerKind.V20Frmr, Abandon = ReleaseBy.A,
    };

    private static ExplorerOptions AbandonGivesUp(string tables) => new()
    {
        TablesDir = tables, Seed = SeedKind.Disconnected, FramesAb = 0, Abandon = ReleaseBy.A,
        N2 = 1, Budget = 1, Faults = FaultKinds.Drop, AcceptGiveUp = true,
    };

    /// <summary>Apply the first enabled move of each kind in turn (A's before B's), from the seed.</summary>
    private static List<StepRecord> Walk(ExplorerOptions options, params MoveKind[] kinds)
    {
        var stepper = new Stepper(options, Fixtures.Tables(options.TablesDir));
        var state = stepper.Seed();
        var trace = new List<StepRecord>();
        foreach (var kind in kinds)
        {
            var move = stepper.EnabledMoves(state).First(m => m.Kind == kind);
            var outcome = stepper.Apply(state, move, trace.Count + 1);
            outcome.Violation.Should().BeNull(outcome.Record.Render());
            trace.Add(outcome.Record);
            state = outcome.Next;
        }
        return trace;
    }

    [Fact]
    public void Abandon_Cold_The_Request_Is_Held_And_Acted_On_When_The_Link_Comes_Up()
    {
        // abandon-cold: A's layer 3 asks for the link, then abandons the
        // attempt while figc4.2 waits for the UA. The request is held; the UA
        // brings the link up (DL-CONNECT Confirm), Connected consumes the held
        // request (DISC P = 1, Awaiting Release) and B's UA ends the release.
        var result = Run(AbandonCold(Fixtures.CurrentDir));
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(8);
        result.FiredTransitions.Should().Contain("Connected/t01_dl_disconnect_request");

        var trace = Walk(AbandonCold(Fixtures.CurrentDir), MoveKind.Abandon, MoveKind.Deliver, MoveKind.Deliver, MoveKind.Deliver, MoveKind.Deliver);
        trace[0].Transition.Should().Be(DataLinkMachine.SaveTransitionId);
        trace[2].Replayed.Should().Equal("Connected/t01_dl_disconnect_request");
        trace[4].Transition.Should().Be("t03_ua_received_yes");
        trace[4].Effects.Should().ContainSingle(e => e.Contains("DL_DISCONNECT_confirm", StringComparison.Ordinal));
        trace[4].SummaryA.Should().StartWith("Disconnected");
        trace[4].SummaryB.Should().StartWith("Disconnected");
    }

    [Fact]
    public void Abandon_V22_The_Request_Is_Held_In_Awaiting_V22_Connection()
    {
        var options = AbandonCold(Fixtures.CurrentDir) with { Seed = SeedKind.AwaitingV22Connection };
        var result = Run(options);
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(8);

        var trace = Walk(options, MoveKind.Abandon, MoveKind.Deliver, MoveKind.Deliver);
        trace[0].SummaryA.Should().StartWith("AwaitingV22Connection").And.Contain("held=[DL_DISCONNECT_request]");
        trace[2].Transition.Should().Be("t12_ua_received_yes_yes");
        trace[2].Replayed.Should().Equal("Connected/t01_dl_disconnect_request");
    }

    [Fact]
    public void Abandon_The_Request_Is_Held_Across_The_V22_To_V20_Fallback()
    {
        // abandon-v20-fallback: held in Awaiting V2.2 Connection, still held
        // after the FRMR from the v2.0 peer moves A to Awaiting Connection
        // (figc4.6 t14), which saves it too; consumed once the SABM is
        // answered, and the stub's UA to the DISC ends it.
        var result = Run(AbandonFallback(Fixtures.CurrentDir));
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(18);

        var trace = Walk(AbandonFallback(Fixtures.CurrentDir), MoveKind.Abandon, MoveKind.Deliver, MoveKind.Deliver);
        trace[2].Transition.Should().Be("t14_frmr_received");
        trace[2].Replayed.Should().BeEmpty("Awaiting Connection saves the request too (pinned 19(a))");
        trace[2].SummaryA.Should().StartWith("AwaitingConnection").And.Contain("held=[DL_DISCONNECT_request]");
    }

    [Fact]
    public void Abandon_The_Request_Is_Confirmed_In_Disconnected_When_The_Connect_Fails()
    {
        // abandon-gives-up: the SABM is lost and A gives up (N2 = 1): figc4.2
        // t05 goes to Disconnected with DL-DISCONNECT Indication, and
        // Disconnected consumes the held request with DL-DISCONNECT Confirm.
        var result = Run(AbandonGivesUp(Fixtures.CurrentDir));
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(23);
        result.FiredTransitions.Should().Contain("Disconnected/t01_dl_disconnect_request");

        var trace = Walk(AbandonGivesUp(Fixtures.CurrentDir), MoveKind.Abandon, MoveKind.Drop, MoveKind.T1Expiry);
        trace[2].Transition.Should().Be("t05_t1_expiry_yes");
        trace[2].Replayed.Should().Equal("Disconnected/t01_dl_disconnect_request");
        trace[2].Effects.Should().ContainInOrder(
            trace[2].Effects.First(e => e.Contains("DL_DISCONNECT_indication", StringComparison.Ordinal)),
            trace[2].Effects.First(e => e.Contains("DL_DISCONNECT_confirm", StringComparison.Ordinal)));
    }

    [Fact]
    public void Abandon_With_Faults_A_Duplicated_Connect_UA_Answers_The_Disc()
    {
        // abandon-cold-faults (packethacking/ax25spec#99; release-cold-faults
        // shows the same without a save).
        // The duplicated UA of the connect reaches A in Awaiting Release,
        // where figc4.3 t03 takes any UA F = 1 as the answer to its DISC; the
        // DISC is then lost and B stays up with nothing to tell it otherwise.
        var result = Run(AbandonCold(Fixtures.CurrentDir) with { Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[3].Transition.Should().Be("t03_ua_received_yes");
        result.Trace[^1].SummaryA.Should().StartWith("Disconnected");
        result.Trace[^1].SummaryB.Should().StartWith("Connected");
        result.CounterexampleLength.Should().Be(5);
    }

    [Fact]
    public void Abandon_With_Faults_And_T3_The_Idle_Poll_Recovers()
    {
        // The same with T3: B's keepalive poll finds A Disconnected (DM).
        var result = Run(AbandonCold(Fixtures.CurrentDir) with { Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate, T3 = true });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(283);
    }

    [Fact]
    public void Abandon_After_A_Reconnect_Is_Clean()
    {
        // abandon-reconnect: the gate's other half. A's layer 3 resets an
        // established link once its data is delivered, then may abandon that
        // connect while it is pending.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 2, Reconnect = ReconnectBy.A, Abandon = ReleaseBy.A,
        });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(74);
    }

    [Fact]
    public void Abandon_By_Both_After_Reconnects_From_Cold_Is_Clean()
    {
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, Seed = SeedKind.Disconnected, FramesAb = 0, Reconnect = ReconnectBy.Both, Abandon = ReleaseBy.Both,
        });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(140);
    }

    [Fact]
    public void Abandon_Is_Only_Offered_While_Layer_3_Waits_For_Its_Own_Connect()
    {
        // Once the connect is reported (here the confirm), the gate is shut;
        // and a station whose layer 3 never asked (B) is never offered it.
        // (That it stays shut if the link later returns to a connect page
        // without a new layer 3 connect is pinned only through the state
        // count of abandon-cold-faults-t3.)
        var options = AbandonCold(Fixtures.CurrentDir) with { Abandon = ReleaseBy.Both };
        var stepper = new Stepper(options, Fixtures.Tables(options.TablesDir));
        var seed = stepper.Seed();
        stepper.EnabledMoves(seed).Should().Contain(new Move(MoveKind.Abandon, Station.A));
        stepper.EnabledMoves(seed).Should().NotContain(new Move(MoveKind.Abandon, Station.B));

        var sabm = stepper.Apply(seed, new Move(MoveKind.Deliver, Station.B), 1).Next;
        var up = stepper.Apply(sabm, new Move(MoveKind.Deliver, Station.A), 2).Next;
        up.A.State.Should().Be("Connected");
        stepper.EnabledMoves(up).Should().NotContain(m => m.Kind == MoveKind.Abandon);
    }

    [Fact]
    public void Abandon_Is_Refused_With_Data_After_The_Confirm_Or_Without_A_Connect()
    {
        ((Action)(() => Fixtures.Run(AbandonCold(Fixtures.CurrentDir) with { DataAfterConfirm = true, FramesAb = 1 }))).Should().Throw<ArgumentException>();
        ((Action)(() => Fixtures.Run(new ExplorerOptions { TablesDir = Fixtures.CurrentDir, Abandon = ReleaseBy.A }))).Should().Throw<ArgumentException>();
        ((Action)(() => Fixtures.Run(AbandonFallback(Fixtures.CurrentDir) with { Abandon = ReleaseBy.Both }))).Should().Throw<ArgumentException>();
        // One layer 3, one DL-DISCONNECT Request: never abandon and release together.
        ((Action)(() => Fixtures.Run(AbandonCold(Fixtures.CurrentDir) with { Release = ReleaseBy.A, ReleaseEarly = true }))).Should().Throw<ArgumentException>();
    }

    // Seeded defects for the save rules (fixtures/README.md).

    [Fact]
    public void Seeded_A_Request_The_Connect_Page_Does_Not_Save_Is_A_Lost_Disconnect()
    {
        // tables-disconnect-swallowed: neither connect page saves the request,
        // so it falls to the upper-layer catch-all and is gone.
        var result = Run(AbandonCold(Fixtures.DisconnectSwallowedDir));
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.DisconnectKept);
        result.Violation.Station.Should().Be(Station.A);
        result.Trace[0].Transition.Should().Be("t11_all_other_primitives__from_upper_layer");
        result.CounterexampleLength.Should().Be(1);
    }

    [Fact]
    public void Seeded_A_Request_Released_At_The_Fallback_Is_A_Lost_Disconnect()
    {
        // tables-disconnect-released-early: Awaiting Connection does not save
        // the request, so the FRMR fallback releases it there, early, into
        // the catch-all.
        var result = Run(AbandonFallback(Fixtures.DisconnectReleasedEarlyDir));
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.DisconnectKept);
        result.Trace[0].Transition.Should().Be(DataLinkMachine.SaveTransitionId);
        result.Trace[2].Transition.Should().Be("t14_frmr_received");
        result.Trace[2].Replayed.Should().Equal("AwaitingConnection/t11_all_other_primitives__from_upper_layer");
        result.CounterexampleLength.Should().Be(3);
    }

    [Fact]
    public void Seeded_A_Released_Request_Consumed_Without_Going_Down_Is_A_Lost_Disconnect()
    {
        // tables-disconnect-kept-connected: Connected's DL-DISCONNECT arm only
        // discards the I frame queue and stays Connected. The held request is
        // released into it once the link is up: the released branch of
        // disconnect kept.
        var result = Run(AbandonCold(Fixtures.DisconnectKeptConnectedDir));
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.DisconnectKept);
        result.Violation.Message.Should().Contain("Connected/t01_dl_disconnect_request (released from a save)");
        result.Trace[2].Replayed.Should().Equal("Connected/t01_dl_disconnect_request");
        result.CounterexampleLength.Should().Be(3);
    }

    [Fact]
    public void Abandon_A_Release_That_Gives_Up_Is_Answered_By_The_Indication()
    {
        // abandon-release-gives-up: both DISCs are lost and Awaiting Release
        // gives up with DL-DISCONNECT Indication only, which answers the
        // request (the Indication half of the answer rule).
        var result = Run(AbandonCold(Fixtures.CurrentDir) with { N2 = 1, Budget = 2, Faults = FaultKinds.Drop, AcceptGiveUp = true });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(27);
    }

    [Fact]
    public void Release_With_Faults_A_Duplicated_Connect_UA_Answers_The_Disc()
    {
        // release-cold-faults: the abandon-cold-faults deadlock without any
        // save (packethacking/ax25spec#99): a plain release after the connect.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, Seed = SeedKind.Disconnected, FramesAb = 0, Release = ReleaseBy.A, Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[3].Transition.Should().Be("t03_ua_received_yes");
        result.CounterexampleLength.Should().Be(5);
    }

    [Fact]
    public void Seeded_A_Released_Request_Must_Be_Confirmed()
    {
        // tables-disconnect-unconfirmed: Disconnected consumes the released
        // request without DL-DISCONNECT Confirm. The indication before it
        // answered the connect, not the request, so the layer 3 never hears
        // back.
        var result = Run(AbandonGivesUp(Fixtures.DisconnectUnconfirmedDir));
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[2].Replayed.Should().Equal("Disconnected/t01_dl_disconnect_request");
        result.CounterexampleLength.Should().Be(3);
    }

    [Fact]
    public void Seeded_A_Released_Request_Must_Take_The_Peer_Down_Too()
    {
        // tables-disconnect-silent: Connected answers the request with
        // DL-DISCONNECT Confirm and goes Disconnected without sending DISC.
        // The request is kept and answered, but the peer stays up.
        var result = Run(AbandonCold(Fixtures.DisconnectSilentDir));
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[^1].Replayed.Should().Equal("Connected/t01_dl_disconnect_request");
        result.Trace[^1].SummaryB.Should().StartWith("Connected");
        result.CounterexampleLength.Should().Be(3);

        // With the v2.0 stub as B: the stub stays connected.
        var stub = Run(AbandonFallback(Fixtures.DisconnectSilentDir));
        stub.Outcome.Should().Be(Outcome.Violation);
        stub.Violation!.Kind.Should().Be(Invariants.Deadlock);
        stub.Trace[^1].SummaryB.Should().Contain("connected, modulo 8");
        stub.CounterexampleLength.Should().Be(8);
    }

    [Fact]
    public void Release_Early_Needs_A_Releasing_Station()
    {
        var act = () => Fixtures.Run(new ExplorerOptions { TablesDir = Fixtures.CurrentDir, ReleaseEarly = true });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Data_Offered_Before_The_Confirm_As_A_Move_Leaves_A_Gap()
    {
        // ax25spec#161 with the submission as a move: a0 is offered while A
        // awaits the UA and figc4.2 discards it (Layer 3 Initiated, the empty
        // arm); a1 is offered once the link is up and delivered, so B delivers
        // a1 where a0 was due.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, Seed = SeedKind.Disconnected, DataAfterConfirm = true, DataBeforeConfirm = true, FramesAb = 2,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Delivery);
        result.Violation.Station.Should().Be(Station.B);
        result.Trace[0].Transition.Should().Be("t09_dl_data_request_yes");
        result.Trace[0].Effects.Should().BeEmpty();
        result.Trace[3].Transition.Should().Be("t02_dl_data_request");
        result.Violation.Message.Should().Contain("delivered `a1` upward where A's submission #0 is `a0`");
        result.CounterexampleLength.Should().Be(6);
    }

    [Fact]
    public void Data_Before_Confirm_Is_A_Variant_Of_Data_Offered_By_Moves()
    {
        var act = () => Fixtures.Run(new ExplorerOptions { TablesDir = Fixtures.CurrentDir, Seed = SeedKind.Disconnected, DataBeforeConfirm = true });
        act.Should().Throw<ArgumentException>();
    }

    // ─── Coverage batch 4: the peer-busy family (ExplorerOptions.LocalBusy) ──

    private static ExplorerOptions Busy(FlowControlAt at, int ab, int ba) => new()
    {
        TablesDir = Fixtures.CurrentDir, FramesAb = ab, FramesBa = ba, LocalBusy = at,
    };

    [Fact]
    public void Busy_B_Holds_A_And_Clears_Cleanly()
    {
        // B enters its local busy condition (RNR), A stops sending and polls,
        // B answers RNR F=1, then B clears (RR) and the data drains.
        var result = Run(Busy(FlowControlAt.B, 2, 0));
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(1069);
        result.FiredTransitions.Should().Contain(new[]
        {
            "Connected/t22_rnr_received_yes", "Connected/t26_i_received_yes_yes_yes_yes_no",
            "TimerRecovery/t19_rnr_received_yes_yes_no", "TimerRecovery/t19_rnr_received_yes_yes_yes",
        });
    }

    [Fact]
    public void Busy_A_While_B_Sends_Is_Clean()
    {
        var result = Run(Busy(FlowControlAt.A, 1, 2));
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(2369);
    }

    [Fact]
    public void Busy_Both_Is_Clean_Behind_Hypothesis_H1()
    {
        // Both stations busy once, both sending: clean once a station left in
        // TimerRecovery counts as at rest (#91 otherwise). Reaches the busy
        // station's own poll (RNR command P=1) at its peer in TimerRecovery.
        var result = Run(Busy(FlowControlAt.Both, 1, 1) with { TimerRecoveryIsQuiescent = true });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(20221);
        result.FiredTransitions.Should().Contain(new[] { "TimerRecovery/t19_rnr_received_no_yes_yes", "TimerRecovery/t19_rnr_received_no_no_yes" });
    }

    [Fact]
    public void Busy_B_Then_A_Lost_Poll_Strands_B_In_Timer_Recovery()
    {
        // #91 after a busy period: clearing the busy condition starts T1 with
        // nothing outstanding (DL-FLOW-ON's busy arm), B's poll is lost, and B
        // never gets the F=1 response that is figc4.5's only exit.
        var result = Run(Busy(FlowControlAt.B, 2, 0) with { Budget = 1 });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[7].Transition.Should().Be(Stepper.BusyLeaveId);
        result.Trace[9].Transition.Should().Be("t12_t1_expiry");
        result.Trace[9].Actor.Should().Be("B");
        result.Trace[^1].SummaryB.Should().StartWith("TimerRecovery").And.Contain("t1=stopped");
        result.CounterexampleLength.Should().Be(21);
    }

    [Fact]
    public void Busy_B_Outlasting_N2_Polls_Tears_The_Link_Down()
    {
        // ax25spec#168 (docs/explorer.md, H5): with frames outstanding, each poll
        // answered by RNR F=1 takes figc4.5 t19_rnr_received_yes_yes_no, which
        // restarts T1 without resetting RC (and re-queues the outstanding frame
        // again), so after N2 RNR answers the link goes down with DL-ERROR I.
        var result = Run(Busy(FlowControlAt.B, 2, 0) with { LongBusy = true });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace.Count(r => r.Transition == "t19_rnr_received_yes_yes_no").Should().Be(4);
        result.Trace[16].Transition.Should().Be("t21_t1_expiry_yes_no");
        result.Trace[16].Effects.Should().Contain(e => e.Contains("DL-ERROR Indication (I)", StringComparison.Ordinal));
        result.CounterexampleLength.Should().Be(18);
    }

    [Fact]
    public void Busy_B_Under_Reordering_Meets_H4_Then_H2()
    {
        // ax25spec#95 (H4): B's busy-clearing RR is overtaken by a newer RR, A
        // takes it as an N(r) error and resets; a1 reordered behind the SABM
        // draws REJ nr=0 = V(s) on the new link, and figc4.7's do-while
        // re-queues frames never sent (H2).
        var result = Run(Busy(FlowControlAt.B, 2, 0) with
        {
            Budget = 2, Faults = FaultKinds.Reorder | FaultKinds.Drop, Invariants = Invariants.Default & ~Invariants.RejectCoherence,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.MachineError);
        result.Violation.Station.Should().Be(Station.A);
        result.Trace[9].Transition.Should().Be("t21_rr_received_no_no");
        result.CounterexampleLength.Should().Be(16);
    }

    [Fact]
    public void H3_The_Tables_Busy_Flow_Off_Arm_Emits_RNR_Without_N_r()
    {
        // Once own receiver busy is set, a DL-FLOW-OFF takes figc4.4
        // t05_dl_flow_off_request_yes, whose RNR has no N(r) staged (H3; the
        // #43 checks are off so the search gets past #43).
        var result = Run(Busy(FlowControlAt.B, 2, 0) with
        {
            FlowControl = FlowControlAt.B, Invariants = Invariants.Default & ~(Invariants.BusyTracksFlow | Invariants.FlowOffDelivery),
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.MachineError);
        result.Violation.Station.Should().Be(Station.B);
        result.Trace[2].Transition.Should().Be(Stepper.BusyEnterId);
        result.Trace[3].Action.Should().StartWith("DL_FLOW_OFF_request");
        result.CounterexampleLength.Should().Be(4);
    }

    [Fact]
    public void Flow_Control_In_Timer_Recovery_Reaches_The_Figc4_5_No_Op_Arms()
    {
        // #43 on figc4.5: with both #43 checks off, DL-FLOW-OFF and DL-FLOW-ON
        // in Timer Recovery take the empty not-busy arms.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 2, FramesBa = 1, Budget = 1, Faults = FaultKinds.Drop, FlowControl = FlowControlAt.B,
            Invariants = Invariants.Default & ~(Invariants.BusyTracksFlow | Invariants.FlowOffDelivery),
        });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(1170);
        result.FiredTransitions.Should().Contain(new[] { "TimerRecovery/t05_dl_flow_off_request_no", "TimerRecovery/t06_dl_flow_on_request_no" });
    }

    [Fact]
    public void Seeded_A_Station_That_Ignores_RNR_Breaks_The_Peer_Busy_Hold()
    {
        // The seeded fixture drops set_peer_receiver_busy from Connected's RNR
        // arms: A keeps sending after B's RNR. busy-b on the real tables is clean.
        var result = Run(Busy(FlowControlAt.B, 2, 0) with { TablesDir = Fixtures.RnrIgnoredDir });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.PeerBusyHold);
        result.Violation.Station.Should().Be(Station.A);
        result.Trace[1].Transition.Should().Be("t22_rnr_received_yes");
        result.CounterexampleLength.Should().Be(3);
    }

    [Fact]
    public void Seeded_A_Busy_Station_That_Delivers_Is_Caught()
    {
        // The seeded fixture reads peer receiver busy where Connected's I-frame
        // arms test own receiver busy, so busy B accepts and delivers a0.
        var result = Run(Busy(FlowControlAt.B, 2, 0) with { TablesDir = Fixtures.BusyDeliversDir });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.FlowOffDelivery);
        result.Violation.Station.Should().Be(Station.B);
        result.Trace[1].Transition.Should().Be(Stepper.BusyEnterId);
        result.CounterexampleLength.Should().Be(3);
    }

    [Fact]
    public void Seeded_A_Busy_Station_That_Answers_RR_Is_Caught()
    {
        // The seeded fixture answers a poll with RR on Enquiry_Response's busy
        // path (the answer side; #94's gate names Transmit_Enquiry, see
        // Seeded_A_Busy_Station_That_Polls_With_RR_Is_Caught): busy B claims to
        // be ready. busy-b on the real tables is clean.
        var result = Run(Busy(FlowControlAt.B, 2, 0) with { TablesDir = Fixtures.BusySaysRrDir });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.BusyIndication);
        result.Violation.Station.Should().Be(Station.B);
        result.Trace[^1].Transition.Should().Be("t21_rr_received_yes");
        result.CounterexampleLength.Should().Be(4);
    }

    [Fact]
    public void Busy_A_Polls_With_RNR_When_Its_Frame_Is_Lost()
    {
        // A goes busy with a0 out, a0 is lost, and A's T1 expires while it is
        // busy: Transmit_Enquiry's busy path polls with RNR command P=1.
        var result = Run(Busy(FlowControlAt.A, 1, 0) with { Budget = 1, Faults = FaultKinds.Drop });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(222);
    }

    [Fact]
    public void Seeded_A_Busy_Station_That_Polls_With_RR_Is_Caught()
    {
        // ax25spec#94's gate mutation: the seeded fixture sends RR Command on
        // Transmit_Enquiry's busy path, so busy A's T1 poll claims it is ready.
        // busy-a-enq on the real tables is clean.
        var result = Run(Busy(FlowControlAt.A, 1, 0) with { Budget = 1, Faults = FaultKinds.Drop, TablesDir = Fixtures.EnquirySaysRrDir });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.BusyIndication);
        result.Violation.Station.Should().Be(Station.A);
        result.Trace[^1].Transition.Should().Be("t12_t1_expiry");
        result.CounterexampleLength.Should().Be(5);
    }

    [Fact]
    public void Long_Busy_Needs_A_Busy_Station_And_Busy_Needs_The_Table_Driven_Peer()
    {
        var a = () => Fixtures.Run(new ExplorerOptions { TablesDir = Fixtures.CurrentDir, LongBusy = true });
        a.Should().Throw<ArgumentException>();
        var b = () => Fixtures.Run(V20(SeedKind.Disconnected, PeerKind.V20Frmr, timerFree: false) with { LocalBusy = FlowControlAt.A });
        b.Should().Throw<ArgumentException>();
    }

    // ─── Coverage batch 4: T3 (ExplorerOptions.T3) ──────────────────────

    [Theory]
    [InlineData(0, 0, 0, 5)]
    [InlineData(2, 1, 1, 758)]
    public void T3_On_A_Live_Link_Is_Clean(int ab, int ba, int budget, int states)
    {
        var result = Run(new ExplorerOptions { TablesDir = Fixtures.CurrentDir, FramesAb = ab, FramesBa = ba, Budget = budget, T3 = true });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(states);
        result.FiredTransitions.Should().Contain("Connected/t13_t3_expiry");
    }

    [Fact]
    public void T3_Finds_A_Dead_Peer_And_Gives_Up_Honestly()
    {
        // An idle link whose keepalive polls are lost: the T3 loop gives up
        // after N2 polls (its threshold is N2, no loss before it), which the
        // give-up rule accepts.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 0, N2 = 2, Budget = 2, Faults = FaultKinds.Drop, AcceptGiveUp = true, T3 = true,
        });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(33);
        result.FiredTransitions.Should().Contain("TimerRecovery/t21_t1_expiry_yes_yes_no");
    }

    [Fact]
    public void T3_A_Release_Give_Up_Settles_Once_The_Peer_Polls()
    {
        // gu-release with T3: B never hears A give up, and with T3 modelled it
        // is not settled until its keepalive finds A gone.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 0, N2 = 1, Budget = 2, Faults = FaultKinds.Drop, Release = ReleaseBy.A,
            AcceptGiveUp = true, T3 = true,
        });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(95);
    }

    [Fact]
    public void Seeded_Without_A_T3_Arm_A_Peer_Left_Up_Never_Settles()
    {
        // The seeded fixture removes figc4.4's T3 expiry arm: B never learns
        // that A gave up. Before T3 was modelled such a B counted as settled;
        // with T3 it does not, so the run is a deadlock.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.T3DeadDir, FramesAb = 0, N2 = 1, Budget = 2, Faults = FaultKinds.Drop, Release = ReleaseBy.A,
            AcceptGiveUp = true, T3 = true,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[4].Transition.Should().Be("t02_t1_expiry_yes");
        result.Trace[^1].SummaryB.Should().StartWith("Connected");
        result.CounterexampleLength.Should().Be(5);
    }

    [Fact]
    public void Seeded_A_Link_With_No_Keepalive_Is_Not_At_Rest()
    {
        // The seeded fixture starts T3 at neither end of a cold connect: both
        // idle with T1 and T3 stopped, which the keepalive rule refuses.
        var result = Run(new ExplorerOptions { TablesDir = Fixtures.NoKeepaliveDir, Seed = SeedKind.Disconnected, FramesAb = 0, T3 = true });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.CounterexampleLength.Should().Be(2);
        var real = Run(new ExplorerOptions { TablesDir = Fixtures.CurrentDir, Seed = SeedKind.Disconnected, FramesAb = 0, T3 = true });
        real.Outcome.Should().Be(Outcome.NoViolation);
        real.StatesExplored.Should().Be(10);
    }

    [Fact]
    public void T3_Turns_Hypothesis_H1_Into_A_Livelock()
    {
        // #91: the stranded station's peer keeps polling it from T3, and the
        // polls are answered from Timer Recovery and change nothing.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 1, FramesBa = 2, K = 1, Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate, T3 = true,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Quiescence);
        result.StatesExplored.Should().Be(1448);
    }

    [Fact]
    public void T3_Makes_A_Reset_From_Timer_Recovery_Fault_Free()
    {
        // ax25spec#167 with no fault: A's keepalive poll takes it to Timer
        // Recovery, its layer 3 resets, figc4.5 t07 sends SABM and stays in
        // Timer Recovery, and the reset is never confirmed.
        var result = Run(new ExplorerOptions { TablesDir = Fixtures.CurrentDir, Seed = SeedKind.Disconnected, FramesAb = 0, Reconnect = ReconnectBy.A, T3 = true });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Quiescence);
        result.Trace[2].Transition.Should().Be("t13_t3_expiry");
        result.Trace[3].Transition.Should().Be("t07_dl_connect_request");
        result.Trace[3].SummaryA.Should().StartWith("TimerRecovery");
        result.StatesExplored.Should().Be(47);
    }
    // ─── Coverage batch 5: UI frames (ExplorerOptions.UnitData) ─────────

    private static ExplorerOptions Ui(FlowControlAt at, UiForm form = UiForm.Plain) => new()
    {
        TablesDir = Fixtures.CurrentDir, FramesAb = 0, UnitData = at, UiForm = form,
    };

    [Theory]
    [InlineData("connected", 1, 0, 51, "Connected/t04_dl_unit_data_request", "Connected/t18_ui_received_no")]
    [InlineData("tr", 1, 1, 214, "TimerRecovery/t04_dl_unit_data_request", "TimerRecovery/t16_ui_received_no")]
    public void UI_Datagrams_On_A_Live_Link_Are_Delivered(string name, int ab, int budget, int states, string send, string receive)
    {
        _ = name;
        var result = Run(Ui(FlowControlAt.Both) with { FramesAb = ab, Budget = budget, Faults = FaultKinds.Drop });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(states);
        result.FiredTransitions.Should().Contain(new[] { send, receive });
    }

    [Fact]
    public void UI_Datagrams_Around_A_Cold_Connect_Are_Delivered()
    {
        // B's datagram from Disconnected reaches A in AwaitingConnection; A's
        // from AwaitingConnection queues behind its SABM, so it reaches B in
        // Connected.
        var result = Run(Ui(FlowControlAt.Both) with { Seed = SeedKind.Disconnected });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(27);
        result.FiredTransitions.Should().Contain(new[]
        {
            "Disconnected/t02_dl_unit_data_request", "AwaitingConnection/t08_dl_unit_data_request", "AwaitingConnection/t12_ui_received_no",
        });
    }

    [Fact]
    public void UI_Poll_Answered_With_DM_Tears_Down_A_Link_That_Has_Just_Come_Up()
    {
        // ax25spec#154 (the tables take 4.3.3.6's reading: a UI poll is
        // answered with DM in the disconnected state). B's UI poll reaches A
        // while A awaits its UA; figc4.2 answers DM F=1; by the time the DM
        // arrives B has accepted A's SABM, and the DM tears B down.
        var result = Run(Ui(FlowControlAt.Both, UiForm.Poll) with { Seed = SeedKind.Disconnected });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[2].Transition.Should().Be("t12_ui_received_yes");
        result.Trace[^1].Transition.Should().Be("t20_dm_received");
        result.Trace[^1].SummaryA.Should().StartWith("Connected");
        result.Trace[^1].SummaryB.Should().StartWith("Disconnected");
        result.CounterexampleLength.Should().Be(8);
    }

    [Theory]
    [InlineData(UiForm.Plain, "t02_dl_unit_data_request", "t10_ui_received_no")]
    [InlineData(UiForm.Poll, Stepper.UiPollId, "t10_ui_received_yes")]
    public void UI_In_Awaiting_V22_Connection_Is_Answered_The_Wrong_Way_Round(UiForm form, string send, string receive)
    {
        // ax25spec#170: figc4.6's UI column draws "P == 1?" with Yes going
        // straight to the next state and No to DM (F = 1), the reverse of
        // figc4.2 and of the July 1998 edition. A UI with P = 0 draws a DM; a
        // UI poll draws nothing.
        var result = Run(Ui(FlowControlAt.B, form) with { Seed = SeedKind.AwaitingV22Connection });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.UnitData);
        result.Violation.Station.Should().Be(Station.A);
        result.Trace[0].Transition.Should().Be(send);
        result.Trace[1].Transition.Should().Be(receive);
        result.CounterexampleLength.Should().Be(2);
    }

    [Fact]
    public void UI_In_Awaiting_V22_Connection_Without_The_Check_Tears_Down_The_Peer()
    {
        // The figc4.6 swap with the UnitData check off: B's plain UI draws
        // DM F=1, which reaches B after it accepted the SABME, and B goes
        // down while A comes up.
        var result = Run(Ui(FlowControlAt.B) with { Seed = SeedKind.AwaitingV22Connection, Invariants = Invariants.Default & ~Invariants.UnitData });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[1].Transition.Should().Be("t10_ui_received_no");
        result.Trace[^1].Transition.Should().Be("t20_dm_received");
        result.CounterexampleLength.Should().Be(5);
    }

    [Fact]
    public void UI_Sent_From_Awaiting_V22_Connection_Is_Clean()
    {
        var result = Run(Ui(FlowControlAt.A) with { Seed = SeedKind.AwaitingV22Connection });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(8);
        result.FiredTransitions.Should().Contain("AwaitingV22Connection/t03_dl_unit_data_request");
    }

    [Theory]
    [InlineData(0, 0, 0, FlowControlAt.A, 4, "Connected/t18_ui_received_yes")]
    [InlineData(1, 1, 1, FlowControlAt.Both, 2865, "TimerRecovery/t16_ui_received_yes")]
    public void UI_Polls_On_A_Live_Link_Are_Answered(int ab, int ba, int budget, FlowControlAt at, int states, string arm)
    {
        var result = Run(Ui(at, UiForm.Poll) with { FramesAb = ab, FramesBa = ba, Budget = budget, Faults = FaultKinds.Drop });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(states);
        result.FiredTransitions.Should().Contain(arm);
    }

    [Fact]
    public void UI_Poll_To_A_Busy_Station_Is_Answered_With_RR()
    {
        // ax25spec#171: Enquiry_Response's head decision (added in the 2017
        // redraw) sends a UI poll, whose frame is neither RR, RNR nor I, down
        // the No branch, which sends RR without testing own receiver busy;
        // the July 1998 edition tested it first. 4.3.3.6 says RR "or RNR, if
        // appropriate" (ax25spec#154).
        var result = Run(Ui(FlowControlAt.A, UiForm.Poll) with { LocalBusy = FlowControlAt.B });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.BusyIndication);
        result.Violation.Station.Should().Be(Station.B);
        result.Trace[2].Transition.Should().Be("t18_ui_received_yes");
        result.CounterexampleLength.Should().Be(3);
    }

    [Theory]
    [InlineData(UiForm.Response, 9)]
    [InlineData(UiForm.Oversize, 9)]
    public void UI_Responses_And_Oversize_UI_Are_Refused_With_DL_ERROR(UiForm form, int states)
    {
        var result = Run(Ui(FlowControlAt.Both, form));
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(states);
    }

    [Theory]
    [InlineData(UiForm.Plain, 202, "AwaitingRelease/t05_dl_unit_data_request", "AwaitingRelease/t14_ui_received_no")]
    [InlineData(UiForm.Poll, 512, "Disconnected/t11_ui_received_yes", "AwaitingRelease/t14_ui_received_yes")]
    public void UI_Around_A_Release_Is_Clean(UiForm form, int states, string arm1, string arm2)
    {
        var result = Run(Ui(FlowControlAt.Both, form) with { FramesAb = 1, Release = ReleaseBy.A });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(states);
        result.FiredTransitions.Should().Contain(new[] { arm1, arm2 });
    }

    [Fact]
    public void Seeded_A_Swallowed_UI_Is_Caught()
    {
        var result = Run(Ui(FlowControlAt.Both) with { FramesAb = 1, TablesDir = Fixtures.UiSwallowedDir });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.UnitData);
        result.Violation.Station.Should().Be(Station.B);
        result.Trace[^1].Transition.Should().Be("t18_ui_received_no");
        result.CounterexampleLength.Should().Be(2);
    }

    [Fact]
    public void Seeded_A_UI_Never_Sent_Is_Not_At_Rest()
    {
        var result = Run(Ui(FlowControlAt.Both) with { FramesAb = 1, TablesDir = Fixtures.UiUnsentDir });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace.Should().Contain(r => r.Transition == "t04_dl_unit_data_request" && r.Effects.Count == 0);
        result.CounterexampleLength.Should().Be(6);
    }

    [Fact]
    public void Seeded_An_Unanswered_UI_Poll_Is_Caught()
    {
        var result = Run(Ui(FlowControlAt.A, UiForm.Poll) with { TablesDir = Fixtures.UiPollUnansweredDir });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.UnitData);
        result.Violation.Station.Should().Be(Station.B);
        result.Trace[1].Transition.Should().Be("t18_ui_received_yes");
        result.CounterexampleLength.Should().Be(2);
    }

    [Fact]
    public void Seeded_A_Delivered_UI_Response_Is_Caught()
    {
        var result = Run(Ui(FlowControlAt.Both, UiForm.Response) with { TablesDir = Fixtures.UiResponseDeliveredDir });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.UnitData);
        result.Violation.Station.Should().Be(Station.B);
        result.CounterexampleLength.Should().Be(2);
    }

    [Fact]
    public void Seeded_A_UI_Falling_To_A_Catch_All_Is_Caught()
    {
        // The seeded fixture deletes Timer Recovery's UI arms, so B's UI
        // reaching A there falls to the lower-layer catch-all and is never
        // delivered. The check keys on the frame, not on the event.
        var result = Run(Ui(FlowControlAt.Both) with { FramesAb = 1, Budget = 1, Faults = FaultKinds.Drop, TablesDir = Fixtures.UiArmDeletedDir });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.UnitData);
        result.Violation.Station.Should().Be(Station.A);
        result.Trace[^1].Transition.Should().Be("t25_all_other_primitives__from_lower_layer");
        result.CounterexampleLength.Should().Be(5);
    }

    [Fact]
    public void UI_Options_Need_The_Table_Driven_Peer_And_A_Sending_Station()
    {
        var a = () => Fixtures.Run(new ExplorerOptions { TablesDir = Fixtures.CurrentDir, UiForm = UiForm.Poll });
        a.Should().Throw<ArgumentException>();
        var b = () => Fixtures.Run(V20(SeedKind.Disconnected, PeerKind.V20Frmr, timerFree: false) with { UnitData = FlowControlAt.A });
        b.Should().Throw<ArgumentException>();
    }
    // ─── Coverage batch 6, part 1: frame errors ─────────────────────────

    private const FrameErrors FE3 = FrameErrors.ControlField | FrameErrors.InfoNotPermitted | FrameErrors.Length;
    private const FrameErrors FE5 = FE3 | FrameErrors.N1 | FrameErrors.IResponse;

    /// <summary>One frame error (one corrupted frame), no other fault.</summary>
    private static ExplorerOptions Fe(FrameErrors fe, string? tables = null) => new()
    {
        TablesDir = tables ?? Fixtures.CurrentDir, FrameErrors = fe, Budget = 1, Faults = FaultKinds.None, FramesAb = 0,
    };

    [Theory]
    [InlineData(SeedKind.Disconnected, 11, "Disconnected/t07_control_field_error", "AwaitingConnection/t15_u_or_s_frame_length_error")]
    [InlineData(SeedKind.AwaitingV22Connection, 11, "AwaitingV22Connection/t07_control_field_error", "AwaitingV22Connection/t09_u_or_s_frame_length_error")]
    public void Frame_Errors_While_Connecting_Are_Reported_And_Recovered(SeedKind seed, int states, string arm1, string arm2)
    {
        // fe-cold, fe-v22: a corrupted SABM(E) or UA is reported (DL-ERROR
        // L, M or N) and the connect completes on the T1 retry.
        var result = Run(Fe(FE3) with { Seed = seed });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(states);
        result.FiredTransitions.Should().Contain(new[] { arm1, arm2 });
    }

    [Fact]
    public void Frame_Errors_Around_A_Release_Are_Clean_And_An_Honest_Reset_Rests()
    {
        // fe-release: a corrupted UA in Awaiting Release is reported; a
        // corrupted DISC makes B reset honestly (nothing to lose), and the
        // release still completes.
        var result = Run(Fe(FE3) with { Release = ReleaseBy.A });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(14);
        result.FiredTransitions.Should().Contain(new[] { "AwaitingRelease/t08_info_not_permitted_in_frame", "Connected/t09_control_field_error_no" });
    }

    [Theory]
    [InlineData(false, 25, "t04_ua_received_yes_yes", "Connected/t26_i_received_yes_no_no")]
    [InlineData(true, 25, "t12_ua_received_yes_yes", "Connected/t26_i_received_yes_no_yes")]
    public void A_Frame_Error_Reset_That_Loses_Data_Is_Reset_Loss(bool srej, int states, string confirm, string n1Arm)
    {
        // fe-a1, fe-a1-srej: a0 arrives corrupted, B reports DL-ERROR (L),
        // discards its queue and re-establishes; A takes the SABM in
        // Connected (DL-ERROR F, DL-CONNECT Indication), B's UA is
        // confirmed. a0 is gone and both layer 3s were told.
        var result = Run(Fe(FE5) with { FramesAb = 1, Srej = srej });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.ResetLoss);
        result.Trace[1].Transition.Should().Be(srej ? "t09_control_field_error_yes" : "t09_control_field_error_no");
        result.Trace[2].Transition.Should().Be("t14_sabm_received_no");
        result.Trace[3].Transition.Should().Be(confirm);
        result.CounterexampleLength.Should().Be(4);
        result.StatesExplored.Should().Be(states);
        result.FiredTransitions.Should().Contain(n1Arm);
    }

    [Fact]
    public void A_Reset_Loss_At_Both_Ends_Is_Reset_Loss()
    {
        // fe-ab1-ctl: B also loses its queued b0, and is told (the
        // explicit discard, then DL-CONNECT Confirm on the UA).
        var result = Run(Fe(FrameErrors.ControlField) with { FramesAb = 1, FramesBa = 1 });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.ResetLoss);
        result.CounterexampleLength.Should().Be(4);
        result.StatesExplored.Should().Be(105);
    }

    [Fact]
    public void A_Reset_Loss_At_The_Resetting_Station_Is_Reset_Loss()
    {
        // fe-a2-k1-info: B's RR reaches A with an information field; A
        // resets, discarding a1, and is told on the UA.
        var result = Run(Fe(FrameErrors.InfoNotPermitted) with { FramesAb = 2, K = 1 });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.ResetLoss);
        result.Trace[3].Transition.Should().Be("t10_info_not_permitted_in_frame_no");
        result.Trace[^1].Transition.Should().Be("t04_ua_received_yes_yes");
        result.CounterexampleLength.Should().Be(6);
        result.StatesExplored.Should().Be(15);
    }

    [Fact]
    public void An_I_Response_In_Connected_Is_Reported_And_Recovered()
    {
        // fe-iresp: a0 arrives as a response, figc4.4 reports DL-ERROR (O)
        // and discards it, A's T1 recovers it.
        var result = Run(Fe(FrameErrors.IResponse) with { FramesAb = 1 });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(16);
        result.FiredTransitions.Should().Contain("Connected/t26_i_received_no");
    }

    [Fact]
    public void A_Frame_Error_In_Timer_Recovery_Is_Defect167()
    {
        // fe-t3-tr, ax25spec#167: the keepalive poll's answer arrives
        // corrupted in Timer Recovery; figc4.5 sends SABM and stays there, so
        // the UA answering it is an unexpected UA (t11, a reset nothing
        // justifies, #114) and the link never rests. With figc4.5's
        // frame-error arms ending in Awaiting Connection, as the July 1998
        // edition draws, the row is clean (35 states).
        var result = Run(Fe(FE3) with { T3 = true });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Quiescence);
        result.Trace[^1].Transition.Should().Be("t08_control_field_error");
        result.Trace[^1].SummaryA.Should().StartWith("TimerRecovery");
        result.StatesExplored.Should().Be(53);
        result.FiredTransitions.Should().Contain(new[] { "TimerRecovery/t09_info_not_permitted_in_frame", "TimerRecovery/t10_u_or_s_frame_length_error" });
    }

    [Fact]
    public void An_I_Frame_Failing_N1_Discards_The_Resetting_Stations_Queue_Unannounced()
    {
        // fe-tr-b3, ax25spec#50: a0 fails the N1 check at B, whose arm
        // re-establishes with Layer 3 Initiated cleared; Establish_Data_Link
        // discards B's queued b0 and, with V(s) = V(a), the UA brings no
        // DL-CONNECT Confirm, so B's layer 3 hears only DL-ERROR (O). Not a
        // reset loss: a deadlock. The search reaches figc4.5's two I frame
        // error arms on the way.
        var result = Run(Fe(FrameErrors.N1 | FrameErrors.IResponse) with { FramesAb = 1, FramesBa = 1, Budget = 3, Faults = FaultKinds.Drop });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[1].Transition.Should().Be("t26_i_received_yes_no_no");
        result.Trace[^1].Transition.Should().Be("t04_ua_received_yes_no_yes");
        result.CounterexampleLength.Should().Be(4);
        result.StatesExplored.Should().Be(2124);
        result.FiredTransitions.Should().Contain(new[] { "TimerRecovery/t22_i_received_yes_no", "TimerRecovery/t22_i_received_no" });
    }

    [Fact]
    public void An_I_Response_In_Timer_Recovery_Goes_To_Connected()
    {
        // fe-iresp-trq, the lead from batch 5: figc4.5 t22_i_received_no
        // reports DL-ERROR (O) and goes to Connected (the 2017 redraw; the
        // July 1998 edition draws DL-ERROR (S) and stays in Timer Recovery).
        // Clean with Timer Recovery counted as at rest; the state count pins
        // the next state (1229 with Timer Recovery).
        var result = Run(Fe(FrameErrors.IResponse) with
        {
            FramesAb = 1, FramesBa = 1, Budget = 3, Faults = FaultKinds.Drop, TimerRecoveryIsQuiescent = true,
        });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(1281);
        result.FiredTransitions.Should().Contain("TimerRecovery/t22_i_received_no");
    }

    [Fact]
    public void A_Busy_Station_In_Timer_Recovery_Discards_An_I_Frame()
    {
        // busy-a-tr2: reaches figc4.5's own-busy I frame arm (residue until
        // this batch); its shortest violation is H1 (#91).
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 1, FramesBa = 1, LocalBusy = FlowControlAt.A, Budget = 2, Faults = FaultKinds.Drop,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[^1].SummaryA.Should().StartWith("TimerRecovery");
        result.CounterexampleLength.Should().Be(14);
        result.StatesExplored.Should().Be(6257);
        result.FiredTransitions.Should().Contain("TimerRecovery/t22_i_received_yes_yes_yes_yes_no");
    }

    [Fact]
    public void A_Reset_At_Modulo_128_Reaches_Timer_Recovery_With_SABME()
    {
        // t3-reconnect-cold-mod128: t3-reconnect-cold with A at modulo 128.
        // B's keepalive poll puts B in Timer Recovery and A's layer 3 reset
        // sends SABME, which reaches figc4.5's SABME column with V(s) = V(a)
        // (t14_sabme_received_yes, residue until this batch). The verdict is
        // the same #167 livelock as at modulo 8.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, Seed = SeedKind.Disconnected, Modulo128A = true, FramesAb = 0, T3 = true, Reconnect = ReconnectBy.A,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Quiescence);
        result.StatesExplored.Should().Be(47);
        result.FiredTransitions.Should().Contain("TimerRecovery/t14_sabme_received_yes");
    }

    [Fact]
    public void Timer_Recovery_Reaches_Its_Reject_Exception_Arm_Before_H1()
    {
        // tr-rej (from the review of batch 6): figc4.5's I frame arm with a
        // reject exception fires with all checks on; the row ends in H1.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 1, FramesBa = 3, K = 2, Budget = 3, Faults = FaultKinds.Drop,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[^1].SummaryA.Should().StartWith("TimerRecovery");
        result.CounterexampleLength.Should().Be(17);
        result.StatesExplored.Should().Be(3666);
        result.FiredTransitions.Should().Contain("TimerRecovery/t22_i_received_yes_yes_yes_no_no_yes_yes_no");
    }

    [Fact]
    public void Timer_Recovery_Reaches_Its_SREJ_Exception_Arm_With_The_Checks_Off()
    {
        // tr-srej-rc-off: with SREJ the #42/#47 checks fire first; with
        // reject coherence and delivery off the row reaches the arm with an
        // SREJ outstanding on the way to the #42 runaway, which the SREJ
        // count bound catches (as d42-rc-off).
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 1, FramesBa = 2, Srej = true, Budget = 4, Faults = FaultKinds.Drop,
            Invariants = Invariants.Default & ~Invariants.RejectCoherence & ~Invariants.Delivery,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.SequenceSanity);
        result.Violation.Station.Should().Be(Station.A);
        result.CounterexampleLength.Should().Be(24);
        result.StatesExplored.Should().Be(2753);
        result.FiredTransitions.Should().Contain("TimerRecovery/t22_i_received_yes_yes_yes_no_no_yes_no_yes_yes");
    }

    [Fact]
    public void A_Lost_Poll_To_A_Busy_Peer_Ends_In_DL_ERROR_U()
    {
        // dl-error-u, ax25spec#168: after the RNR A polls on T1, the poll
        // is lost, and with N2 = 1 the next expiry gives up with DL-ERROR U.
        // The give-up rule calls it unjustified (one loss against N2 + 1).
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 0, LocalBusy = FlowControlAt.B, LongBusy = true, T3 = true, N2 = 1,
            Budget = 1, Faults = FaultKinds.Drop, AcceptGiveUp = true,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[4].Transition.Should().Be("t21_t1_expiry_yes_yes_yes");
        result.Trace[4].Effects.Should().Contain(e => e.Contains("DL-ERROR Indication (U)", StringComparison.Ordinal));
        result.CounterexampleLength.Should().Be(6);
    }

    [Fact]
    public void Seeded_A_Reset_Told_With_The_Wrong_Code_Is_Not_A_Reset_Loss()
    {
        // The peer reports the reset as DL-ERROR (A), not (F): its layer 3
        // was not told what happened, so the loss of a0 is a deadlock.
        var result = Run(Fe(FrameErrors.ControlField, Fixtures.ResetWrongCodeDir) with { FramesAb = 1 });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.CounterexampleLength.Should().Be(4);
    }

    [Fact]
    public void Seeded_An_Unreported_Frame_Error_Is_Caught()
    {
        var result = Run(Fe(FrameErrors.ControlField, Fixtures.FrameErrorUnreportedDir) with { FramesAb = 1 });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.FrameError);
        result.Violation.Station.Should().Be(Station.B);
        result.Trace[^1].Transition.Should().Be("t09_control_field_error_no");
        result.CounterexampleLength.Should().Be(2);
    }

    [Fact]
    public void Seeded_A_Frame_Error_Falling_To_A_Catch_All_Is_Caught()
    {
        // Timer Recovery's frame-error arms deleted: the corrupted frame
        // falls to the lower-layer catch-all. The check keys on the
        // corruption, not on the event.
        var result = Run(Fe(FE3, Fixtures.FrameErrorArmsDeletedDir) with { T3 = true });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.FrameError);
        result.Violation.Station.Should().Be(Station.A);
        result.Trace[^1].Transition.Should().Be("t25_all_other_primitives__from_lower_layer");
        result.CounterexampleLength.Should().Be(3);
    }

    [Theory]
    [InlineData(FrameErrors.ControlField, 1, 1, 4, 4)]
    [InlineData(FrameErrors.InfoNotPermitted, 2, 0, 1, 6)]
    public void Seeded_A_Reset_Loss_Layer_3_Was_Not_Told_Of_Is_A_Deadlock(FrameErrors fe, int ab, int ba, int k, int length)
    {
        // Awaiting Connection no longer confirms the connect a frame-error
        // reset set Layer 3 Initiated for, so the resetting station's layer
        // 3 is never told of the reset that discarded its data: B's b0 in
        // the first case, A's a1 in the second. On the real tables both are
        // reset losses (fe-ab1-ctl, fe-a2-k1-info).
        var result = Run(Fe(fe, Fixtures.ResetUnconfirmedDir) with { FramesAb = ab, FramesBa = ba, K = k });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.CounterexampleLength.Should().Be(length);
    }

    [Theory]
    [InlineData(1, 4, 5)]
    [InlineData(2, 1, 9)]
    public void Seeded_A_Reset_No_Frame_Error_Justifies_Never_Rests(int ab, int k, int cleanStates)
    {
        // An in-window RR resets the link (reported, layer 3 told). With
        // nothing lost (one frame) the real tables' run is clean and the
        // fixture's a deadlock, not a rest; with a1 discarded (k = 1) it is
        // a deadlock, not a reset loss. No frame is corrupted (budget 0): the
        // rule is on with the option.
        var o = Fe(FrameErrors.ControlField) with { FramesAb = ab, K = k, Budget = 0 };
        Run(o).Outcome.Should().Be(Outcome.NoViolation);
        Run(o).StatesExplored.Should().Be(cleanStates);
        var result = Run(o with { TablesDir = Fixtures.RrResetsDir });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[3].Transition.Should().Be("t21_rr_received_yes");
        result.CounterexampleLength.Should().Be(6);
    }

    [Fact]
    public void Frame_Errors_Need_The_Table_Driven_Peer()
    {
        var a = () => Fixtures.Run(V20(SeedKind.Disconnected, PeerKind.V20Frmr, timerFree: false) with { FrameErrors = FrameErrors.ControlField });
        a.Should().Throw<ArgumentException>();
    }
    // ─── Coverage batch 6, part 2: injected frames ──────────────────────

    private static ExplorerOptions Inj(FlowControlAt at, InjectedFrame f) => new()
    {
        TablesDir = Fixtures.CurrentDir, Inject = at, InjectedFrame = f, FramesAb = 0, Budget = 0,
    };

    [Fact]
    public void A_REJ_Poll_To_A_Busy_Station_In_Timer_Recovery_Is_Answered_With_RR()
    {
        // inj-rej-busy-tr, ax25spec#171's strongest caller: B's b0 is lost,
        // B polls from Timer Recovery and enters its busy condition; A's REJ
        // command P=1 takes figc4.5 t23_rej_received_no_yes_yes_no, whose
        // Enquiry Response (F = 1) sends a REJ caller down the No branch of
        // its 2017 head decision to RR Response, past the busy test.
        var result = Run(Inj(FlowControlAt.A, InjectedFrame.RejPoll) with
        {
            FramesBa = 1, LocalBusy = FlowControlAt.B, Budget = 1, Faults = FaultKinds.Drop,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.BusyIndication);
        result.Violation.Station.Should().Be(Station.B);
        result.Trace.Should().Contain(r => r.Transition == Stepper.InjectId(InjectedFrame.RejPoll));
        result.Trace[^1].Transition.Should().Be("t23_rej_received_no_yes_yes_no");
        result.CounterexampleLength.Should().Be(6);
    }

    [Theory]
    [InlineData(InjectedFrame.RejPoll, 67, "TimerRecovery/t23_rej_received_no_yes_yes_no")]
    [InlineData(InjectedFrame.SrejCommand, 83, "TimerRecovery/t24_srej_received_no_yes_no_no")]
    [InlineData(InjectedFrame.SrejPoll, 101, "TimerRecovery/t24_srej_received_no_yes_yes_no")]
    public void Reject_Commands_In_Timer_Recovery_Are_Accepted(InjectedFrame form, int states, string arm)
    {
        // inj-rej-tr, inj-srej-tr, inj-srejp-tr: a REJ poll, and an SREJ
        // command (ax25spec#164: SREJ is a response only, but figc4.5 acts on
        // the command: it retransmits the frame named), each naming A's
        // outstanding a0 while A polls after a lost frame.
        var result = Run(Inj(FlowControlAt.B, form) with { FramesAb = 1, Budget = 1, Faults = FaultKinds.Drop });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(states);
        result.FiredTransitions.Should().Contain(arm);
    }

    [Theory]
    [InlineData(SeedKind.Disconnected, ReleaseBy.None, 13, "AwaitingConnection/t04_ua_received_no")]
    [InlineData(SeedKind.AwaitingV22Connection, ReleaseBy.None, 13, "AwaitingV22Connection/t12_ua_received_no")]
    [InlineData(SeedKind.Connected, ReleaseBy.A, 11, "AwaitingRelease/t03_ua_received_no")]
    public void A_UA_With_F_0_While_Waiting_Is_Reported_And_Ignored(SeedKind seed, ReleaseBy release, int states, string arm)
    {
        // inj-ua-cold, inj-ua-v22, inj-ua-release: DL-ERROR (D), and the
        // station keeps waiting for the UA with F = 1.
        var result = Run(Inj(FlowControlAt.B, InjectedFrame.UaF0) with { Seed = seed, Release = release });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(states);
        result.FiredTransitions.Should().Contain(arm);
    }

    [Theory]
    [InlineData(true, false, 5, "Connected/t16_frmr_received_yes")]
    [InlineData(false, true, 25, "TimerRecovery/t15_frmr_received")]
    public void An_FRMR_In_The_Data_Phase_Resets_The_Link(bool srej, bool t3, int states, string arm)
    {
        // inj-frmr-srej, inj-frmr-t3: with nothing queued the reset loses
        // nothing and the link comes back up.
        var result = Run(Inj(FlowControlAt.B, InjectedFrame.Frmr) with { Srej = srej, T3 = t3 });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(states);
        result.FiredTransitions.Should().Contain(arm);
    }

    [Fact]
    public void An_FRMR_Discards_The_Queue_Unannounced()
    {
        // inj-frmr-a1, ax25spec#50 (the comment there names FRMR): figc4.4
        // t16 re-establishes with Layer 3 Initiated cleared; A's queued a0 is
        // discarded by Establish_Data_Link and, with V(s) = V(a), the UA
        // brings no DL-CONNECT Confirm. A's layer 3 hears only DL-ERROR (K).
        var result = Run(Inj(FlowControlAt.B, InjectedFrame.Frmr) with { FramesAb = 1 });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[1].Transition.Should().Be("t16_frmr_received_no");
        result.Trace[^1].Transition.Should().Be("t04_ua_received_yes_no_yes");
        result.CounterexampleLength.Should().Be(4);
    }

    [Fact]
    public void Injected_Frames_Need_A_Form_And_The_Table_Driven_Peer()
    {
        var a = () => Fixtures.Run(new ExplorerOptions { TablesDir = Fixtures.CurrentDir, Inject = FlowControlAt.A });
        a.Should().Throw<ArgumentException>();
        var b = () => Fixtures.Run(new ExplorerOptions { TablesDir = Fixtures.CurrentDir, InjectedFrame = InjectedFrame.Frmr });
        b.Should().Throw<ArgumentException>();
        var c = () => Fixtures.Run(V20(SeedKind.Disconnected, PeerKind.V20Frmr, timerFree: false) with { Inject = FlowControlAt.A, InjectedFrame = InjectedFrame.UaF0 });
        c.Should().Throw<ArgumentException>();
    }

    // ─── Coverage batch 7: injected I frames with P = 1 ──────────────

    private static ExplorerOptions Ip(InjectedFrame first, InjectedFrame then = InjectedFrame.None) => new()
    {
        TablesDir = Fixtures.CurrentDir, Inject = FlowControlAt.A, InjectedFrame = first, InjectedThen = then, FramesAb = 0, Budget = 0,
    };

    [Theory]
    [InlineData(InjectedFrame.IPoll, 6, "Connected/t26_i_received_yes_yes_yes_no_yes_yes")]
    [InlineData(InjectedFrame.IPollStale, 4, "Connected/t26_i_received_yes_yes_yes_no_no_no_yes")]
    public void An_Injected_I_Poll_In_Connected_Is_Answered_RR_F1(InjectedFrame form, int states, string arm)
    {
        // ip-inseq, ip-stale. In sequence the payload xa0 is delivered (not a
        // submission, so no delivery count moves) and B's RR F=1 acknowledges
        // a number A never used: A's N(r) error recovery resets the link, with
        // nothing left to lose.
        var result = Run(Ip(form));
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(states);
        result.FiredTransitions.Should().Contain(arm);
    }

    [Fact]
    public void An_Injected_I_Poll_In_Sequence_Is_Delivered_Once()
    {
        // The claim behind ip-inseq, stepped by hand: B's arm delivers xa0
        // and moves V(r) past it, in the one dispatch.
        var o = Ip(InjectedFrame.IPoll);
        var stepper = new Stepper(o, Fixtures.Tables(o.TablesDir));
        var s = stepper.Seed();
        var inject = stepper.Apply(s, new Move(MoveKind.Inject, Station.A), 1);
        inject.Violation.Should().BeNull();
        var deliver = stepper.Apply(inject.Next, new Move(MoveKind.Deliver, Station.B), 2);
        deliver.Violation.Should().BeNull();
        deliver.Record.Transition.Should().Be("t26_i_received_yes_yes_yes_no_yes_yes");
        deliver.Record.Effects.Count(e => e.Contains("DL_DATA_indication", StringComparison.Ordinal) && e.Contains("xa0", StringComparison.Ordinal)).Should().Be(1);
        deliver.Next.B.Vr.Should().Be(1);
        deliver.Next.DeliveredAtB.Should().Be(0);
    }

    [Fact]
    public void A_Busy_Station_In_Connected_Answers_An_I_Poll_With_RR()
    {
        // ip-busy, a hypothesis for a second reader: figc4.4's own-busy I
        // frame arm with P = 1 sends RR F=1 where figc4.5's sends RNR F=1.
        var result = Run(Ip(InjectedFrame.IPollStale) with { LocalBusy = FlowControlAt.B });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.BusyIndication);
        result.Violation.Station.Should().Be(Station.B);
        result.Trace[^1].Transition.Should().Be("t26_i_received_yes_yes_yes_yes_yes");
        result.CounterexampleLength.Should().Be(3);
    }

    [Theory]
    [InlineData(1, 0, 159, "Connected/t26_i_received_yes_yes_yes_no_no_yes_yes_yes")]
    [InlineData(1, 1, 444, "TimerRecovery/t22_i_received_yes_yes_yes_no_no_yes_yes_yes")]
    public void A_Second_I_Poll_Ahead_Meets_The_Reject_Exception(int ab, int ba, int states, string arm)
    {
        // ip-rejx, ip-tr-rejx: the first frame ahead of sequence draws REJ
        // F=1 and sets the reject exception, the second meets it. The REJ F=1
        // reaches A in Timer Recovery on some paths (figc4.5's F=1 arms).
        var result = Run(Ip(InjectedFrame.IPollAhead, InjectedFrame.IPollAhead) with
        {
            FramesAb = ab, FramesBa = ba, Budget = 1, Faults = FaultKinds.Drop, RejMayEqualVs = true,
        });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(states);
        result.FiredTransitions.Should().Contain(arm);
        result.FiredTransitions.Should().Contain("TimerRecovery/t23_rej_received_yes_yes_yes");
        result.FiredTransitions.Should().Contain("TimerRecovery/t23_rej_received_yes_yes_no");
    }

    [Fact]
    public void I_Polls_Reach_Timer_Recovery()
    {
        // ip-tr-inseq, ip-tr-stale (#91, H1: the I frame's N(r) acknowledges
        // everything, Check I Frame Acknowledged stops T1 and B stays in
        // Timer Recovery), ip-tr-busy (behind ip-busy, so with the busy check
        // off and Timer Recovery counted at rest).
        var o = Ip(InjectedFrame.IPoll) with { FramesBa = 1, Budget = 1, Faults = FaultKinds.Drop };
        var inseq = Run(o);
        inseq.Outcome.Should().Be(Outcome.NoViolation);
        inseq.StatesExplored.Should().Be(79);
        inseq.FiredTransitions.Should().Contain("TimerRecovery/t22_i_received_yes_yes_yes_no_yes_yes");

        var stale = Run(o with { InjectedFrame = InjectedFrame.IPollStale });
        stale.Outcome.Should().Be(Outcome.Violation);
        stale.Violation!.Kind.Should().Be(Invariants.Deadlock);
        stale.CounterexampleLength.Should().Be(12);
        stale.FiredTransitions.Should().Contain("TimerRecovery/t22_i_received_yes_yes_yes_no_no_no_yes");

        var busy = Run(o with
        {
            InjectedFrame = InjectedFrame.IPollStale, LocalBusy = FlowControlAt.B,
            Invariants = Invariants.Default & ~Invariants.BusyIndication, TimerRecoveryIsQuiescent = true,
        });
        busy.Outcome.Should().Be(Outcome.NoViolation);
        busy.StatesExplored.Should().Be(1244);
        busy.FiredTransitions.Should().Contain("TimerRecovery/t22_i_received_yes_yes_yes_yes_yes");
    }

    [Theory]
    [InlineData(SeedKind.Connected, false, 333, "TimerRecovery/t23_rej_received_yes_no_no")]
    [InlineData(SeedKind.AwaitingV22Connection, false, 476, "TimerRecovery/t24_srej_received_yes_no_yes")]
    [InlineData(SeedKind.Connected, true, 1374, "TimerRecovery/t19_rnr_received_no_no_no_no")]
    [InlineData(SeedKind.AwaitingV22Connection, true, 2233, "TimerRecovery/t19_rnr_received_yes_no_yes")]
    public void An_I_Frame_In_Sequence_Puts_The_Peers_N_r_Past_V_s(SeedKind seed, bool busy, int states, string arm)
    {
        // ip-nr, ip-nr-v22, ip-nr-rnr, ip-nr-rnr-v22: A, in Timer Recovery after T3 with
        // its poll lost, sends an I frame in sequence that its own machine
        // never numbered; then a frame ahead of sequence draws REJ F=1 (SREJ
        // F=1 at version 2.2: B accepted SABME, so Set Version 2.2 enabled
        // selective reject, packet-net/ax25sdl#123), or B's
        // busy condition draws RNR, each with N(r) = V(s) + 1 at A. Reject
        // coherence is off for the REJ rows: that reject is incoherent by
        // construction.
        var o = busy
            ? Ip(InjectedFrame.IFrame) with { LocalBusy = FlowControlAt.B }
            : Ip(InjectedFrame.IFrame, InjectedFrame.IPollAhead) with { Invariants = Invariants.Default & ~Invariants.RejectCoherence };
        var result = Run(o with { Seed = seed, T3 = true, Budget = 1, Faults = FaultKinds.Drop });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(states);
        result.FiredTransitions.Should().Contain(arm);
    }

    [Fact]
    public void At_Version_2_2_The_Initiators_Implicit_Reject_Reaches_The_Rej_N_r_Error_Arms()
    {
        // ip-nr-v22-b: ip-nr-v22 with the injector at B. A, the initiator on
        // the awaiting-v22 seed, keeps its configured implicit reject (its UA
        // arm runs no Set Version box), so it answers B's frame ahead with
        // REJ F=1; B, at version 2.2, takes the N(r) error on the REJ arms.
        var result = Run(Ip(InjectedFrame.IFrame, InjectedFrame.IPollAhead) with
        {
            Inject = FlowControlAt.B, Seed = SeedKind.AwaitingV22Connection, T3 = true, Budget = 1, Faults = FaultKinds.Drop,
            Invariants = Invariants.Default & ~Invariants.RejectCoherence,
        });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(466);
        result.FiredTransitions.Should().Contain(["Connected/t25_rej_received_no_yes", "TimerRecovery/t23_rej_received_yes_no_yes"]);
    }

    [Theory]
    [InlineData(InjectedFrame.RejPoll, 1, false, 75, "TimerRecovery/t23_rej_received_no_yes_yes_yes")]
    [InlineData(InjectedFrame.SrejCommand, 0, true, 25, "TimerRecovery/t24_srej_received_no_yes_no_yes")]
    public void Reject_Commands_May_Name_V_s_With_RejMayEqualVs(InjectedFrame form, int ab, bool t3, int states, string arm)
    {
        // rnv-rej, rnv-srej: the reject-naming-vs command arms.
        var result = Run(Inj(FlowControlAt.B, form) with
        {
            FramesAb = ab, T3 = t3, Budget = 1, Faults = FaultKinds.Drop, RejMayEqualVs = true,
            Invariants = form == InjectedFrame.RejPoll ? Invariants.Default : Invariants.Default & ~Invariants.RejectCoherence,
        });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(states);
        result.FiredTransitions.Should().Contain(arm);
    }

    [Fact]
    public void An_SREJ_Poll_Naming_V_s_Goes_Unanswered_And_Strands_Timer_Recovery()
    {
        // rnv-srejp: figc4.5 t24_srej_received_no_yes_yes_yes acknowledges
        // everything through Check I Frame Acknowledged, which stops T1, and
        // sends nothing for the poll (#91, H1; ax25spec#164).
        var result = Run(Inj(FlowControlAt.B, InjectedFrame.SrejPoll) with
        {
            FramesAb = 1, Budget = 1, Faults = FaultKinds.Drop, RejMayEqualVs = true, Invariants = Invariants.Default & ~Invariants.RejectCoherence,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.CounterexampleLength.Should().Be(11);
        result.FiredTransitions.Should().Contain("TimerRecovery/t24_srej_received_no_yes_yes_yes");
    }

    [Theory]
    [InlineData(InjectedFrame.IPollStale, false, "t26_i_received_yes_yes_yes_no_no_no_yes")]
    [InlineData(InjectedFrame.IPoll, false, "t26_i_received_yes_yes_yes_no_yes_yes")]
    [InlineData(InjectedFrame.IPoll, true, "t26_i_received_yes_yes_yes_no_yes_yes")]
    public void An_Injected_Payload_Is_Delivered_Exactly_When_Accepted(InjectedFrame form, bool undelivered, string arm)
    {
        // seeded-injected-stale, seeded-injected-twice,
        // seeded-injected-undelivered: delivering the frame outside the
        // window, delivering the in-sequence one twice in one dispatch, or
        // accepting it without delivering it, is a Delivery violation, so
        // the injected label is no blanket leniency.
        var result = Run(Ip(form) with { TablesDir = undelivered ? Fixtures.InjectedUndeliveredDir : Fixtures.InjectedDeliveredDir });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Delivery);
        result.Violation.Station.Should().Be(Station.B);
        result.CounterexampleLength.Should().Be(2);
        result.Trace[^1].Transition.Should().Be(arm);
    }

    [Fact]
    public void Both_Stations_Injecting_In_Sequence_Are_Clean_Now_The_N_r_Error_Leaves_Timer_Recovery()
    {
        // ip-both (from the review of batch 7; ax25spec#12, H9): B in Timer
        // Recovery receives an RR with N(r) past V(s). Until ax25spec#180 the
        // errata branch ran N(r) Recovery and stayed in Timer Recovery with
        // its variables, so after A's reset B retransmitted b0 and A
        // delivered it again (Delivery at A, 18 steps). Since #180 the arm
        // leaves for Awaiting Connection, as the 1998 figure draws, and the
        // row is clean.
        var result = Run(Ip(InjectedFrame.IFrame) with
        {
            Inject = FlowControlAt.Both, FramesBa = 1, T3 = true, Budget = 2, Faults = FaultKinds.Drop,
            Invariants = Invariants.Default & ~Invariants.RejectCoherence,
        });
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(3531);
        result.FiredTransitions.Should().Contain("TimerRecovery/t18_rr_received_no_no_no_no");
    }

    // ax25spec#169 (packet-net/ax25sdl#119): A sends a0; B's T3 (only B's,
    // so #167 does not end the run with A in Timer Recovery) takes B to Timer
    // Recovery holding V(r) = 1 with V(s) = V(a); A's layer 3 resets the link
    // and, once that is answered with the link up, submits a1. With #169's reset box B takes a1 in
    // sequence. Without it B keeps V(r) = 1, discards a1 (N(s) = 0) and
    // answers N(r) = 1, which acknowledges at A a frame B never delivered.
    private static ExplorerOptions Reset169(string dir) => new()
    {
        TablesDir = dir, FramesAb = 1, Budget = 0, T3 = true, T3At = FlowControlAt.B, Reconnect = ReconnectBy.A, DataAfterReconnect = true,
    };

    [Fact]
    public void A_Reset_Of_Timer_Recovery_With_Nothing_Outstanding_Zeroes_The_Variables()
    {
        // reconnect-tr-data
        var result = Run(Reset169(Fixtures.CurrentDir));
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().Be(45);
        result.FiredTransitions.Should().Contain("TimerRecovery/t13_sabm_received_yes");
    }

    [Fact]
    public void Without_The_169_Reset_Box_Data_After_The_Reset_Is_Acknowledged_Undelivered()
    {
        // reverted-169
        var result = Run(Reset169(Fixtures.Reverted169Dir));
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.AckCoherence);
        result.Violation.Station.Should().Be(Station.A);
        result.CounterexampleLength.Should().Be(15);
        result.Trace.Select(t => t.Transition).Should().Contain("t13_sabm_received_yes");
    }

    [Fact]
    public void With_Data_Both_Ways_The_Reset_Box_Zeroes_V_s_And_V_a_Too()
    {
        // reconnect-tr-data-ba: B also sends b0 before the reset, so its V(s)
        // and V(a) are 1 when the SABM arrives; the box zeroes them. Without
        // it (reverted-169-ba) both stay 1 against A's reset V(r) = 0 and the
        // link never rests. Leaving out V(s) or V(a) alone is caught too
        // (AckCoherence or SequenceSanity at B, checked by mutation).
        var clean = Run(Reset169(Fixtures.CurrentDir) with { FramesBa = 1 });
        clean.Outcome.Should().Be(Outcome.NoViolation);
        clean.StatesExplored.Should().Be(170);
        var reverted = Run(Reset169(Fixtures.Reverted169Dir) with { FramesBa = 1 });
        reverted.Outcome.Should().Be(Outcome.Violation);
        reverted.Violation!.Kind.Should().Be(Invariants.Quiescence);
        reverted.CounterexampleLength.Should().Be(9);
    }

    [Fact]
    public void Data_After_A_Reconnect_And_T3_At_One_Station_Need_Their_Preconditions()
    {
        var a = () => Fixtures.Run(new ExplorerOptions { TablesDir = Fixtures.CurrentDir, DataAfterReconnect = true });
        a.Should().Throw<ArgumentException>();
        var b = () => Fixtures.Run(new ExplorerOptions { TablesDir = Fixtures.CurrentDir, T3At = FlowControlAt.B });
        b.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Injected_I_Frames_Need_Their_Preconditions()
    {
        var a = () => Fixtures.Run(Ip(InjectedFrame.IPollAhead) with { Srej = true });
        a.Should().Throw<ArgumentException>();
        var b = () => Fixtures.Run(Ip(InjectedFrame.None, InjectedFrame.IPoll));
        b.Should().Throw<ArgumentException>();
    }
}
