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

    // ─── #43 fixed (the tables-43fixed fixture) and the busy family ───
    //
    // ax25spec#94 (V1): the busy family is unreachable on the current tables
    // because #43 makes a station unable to become busy, so it is explored on
    // a hand-patched copy (Fixtures.Fixed43Dir, README in the directory) with
    // the #43 branch swap and the H3 N(r) := V(r) box. The calibration gate
    // plants three mutations in copies of that fixture and shows each is
    // caught; the #43 rows above must flip clean on it.

    /// <summary>The busy scenario: A sends 2, B's layer 3 turns flow off after its first delivery; busy periods bounded at 2 polls, T3 on.</summary>
    private static ExplorerOptions Busy(string tables) => new()
    {
        TablesDir = tables, FramesAb = 2, FramesBa = 0, K = 4, Budget = 0, FlowControl = FlowControlAt.B, BusyPolls = 2, T3 = true,
    };

    /// <summary>Mutation 1: "Set Own Receiver Busy" removed from the DL-FLOW-OFF busy arm (figc4.4 and figc4.5).</summary>
    private static string MutantNoSetOwnBusy => Fixtures.Mutant(Fixtures.Fixed43Dir, "43fixed-no-set-own-busy", (file, root) =>
    {
        if (file is not ("connected.g.json" or "timer_recovery.g.json")) return;
        Fixtures.RemoveAction(Fixtures.Transition(root, "t05_dl_flow_off_request_yes"), "Set Own Receiver Busy");
    });

    /// <summary>Mutation 2: Transmit_Enquiry's busy path sends RR Command instead of RNR Command.</summary>
    private static string MutantRrInBusyEnquiry => Fixtures.Mutant(Fixtures.Fixed43Dir, "43fixed-rr-in-transmit-enquiry", (file, root) =>
    {
        if (file != "subroutines.g.json") return;
        Fixtures.ReplaceAction(Fixtures.SubroutinePath(root, "Transmit_Enquiry", "t02_transmit_enquiry_yes"), "RNR Command", "RR Command");
    });

    /// <summary>Mutation 3 (the third busy invariant's teeth): the pop path's "peer receiver busy? yes" arm sends the frame instead of pushing it back.</summary>
    private static string MutantPopIgnoresPeerBusy => Fixtures.Mutant(Fixtures.Fixed43Dir, "43fixed-pop-ignores-peer-busy", (file, root) =>
    {
        if (file is not ("connected.g.json" or "timer_recovery.g.json")) return;
        Fixtures.CopyActions(from: Fixtures.Transition(root, "t03_i_frame_pops_off_queue_no_no_yes"), to: Fixtures.Transition(root, "t03_i_frame_pops_off_queue_yes"));
    });

    [Fact]
    public void Defect43_Fixed_Fixture_The_Flow_Off_Scenario_Is_Clean()
    {
        // The #43 row flipped: on the fixed fixture B becomes busy, announces
        // it, holds a1 back and delivers it after FLOW_ON; every invariant
        // (the two flow checks and the three busy checks included) is quiet.
        // The busy period is bounded to 2 polls; unbounded, what remains is
        // hypothesis H4 (next test), not #43.
        Run(Scenario43 with { TablesDir = Fixtures.Fixed43Dir, BusyPolls = 2 }).Outcome.Should().Be(Outcome.NoViolation);
        Run(Busy(Fixtures.Fixed43Dir)).Outcome.Should().Be(Outcome.NoViolation);
        Run(Busy(Fixtures.Fixed43Dir) with { FramesAb = 3, K = 2 }).Outcome.Should().Be(Outcome.NoViolation, "a window-full pop and a peer-busy pop coexist");
    }

    [Fact]
    public void Defect43_Fixed_Fixture_The_Busy_Arms_Announce_Busy_With_N_r_Equal_To_V_r()
    {
        // The H3 twin of Defect43_Note_...: with the staging box in place the
        // DL-FLOW-OFF arm sets busy and sends RNR with N(r) = V(r); DL-FLOW-ON
        // clears busy and sends RR Command with N(r) = V(r). Both pages.
        var tables = Fixtures.Tables(Fixtures.Fixed43Dir);
        foreach (var state in new[] { "Connected", "TimerRecovery" })
        {
            var m = new DataLinkMachine(tables, state);
            m.SetVariable("vr", "3");
            m.SetTimer("t1", TimerStatus.Running);

            var off = m.Dispatch(new EventInput("DL_FLOW_OFF_request"));
            off.TransitionId.Should().Be("t05_dl_flow_off_request_yes", $"{state}: the swapped guard puts the busy chain on the not-busy arm");
            m.OwnReceiverBusy.Should().BeTrue();
            off.Effects.Should().ContainSingle(e => e is FrameEffect)
                .Which.Should().Match<FrameEffect>(f => f.Frame == "RNR" && !f.Command && !f.Pf && f.Nr == 3);

            var on = m.Dispatch(new EventInput("DL_FLOW_ON_request"));
            on.TransitionId.Should().Be("t06_dl_flow_on_request_yes_yes");
            m.OwnReceiverBusy.Should().BeFalse();
            on.Effects.Should().ContainSingle(e => e is FrameEffect)
                .Which.Should().Match<FrameEffect>(f => f.Frame == "RR" && f.Command && !f.Pf && f.Nr == 3);
        }
    }

    [Fact]
    public void Calibration_Mutation_Removing_Set_Own_Receiver_Busy_Is_Caught_By_Busy_Tracks_Flow()
    {
        // The RNR still goes out (with N(r) = V(r)), but the station is not
        // busy, so the next frame is delivered while flow is off: the same
        // two symptoms as #43 on the current tables, in the same order.
        var result = Run(Busy(MutantNoSetOwnBusy));
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.BusyTracksFlow);
        result.Violation.Station.Should().Be(Station.B);
        result.Trace[^1].Transition.Should().Be("t05_dl_flow_off_request_yes");
        result.Trace[^1].Effects.Should().Contain("frame RNR response pf=0 nr=1", "the announcement is sent, the condition is not entered");
        result.CounterexampleLength.Should().Be(3);

        var delivery = Run(Busy(MutantNoSetOwnBusy) with { Invariants = Invariants.Default & ~Invariants.BusyTracksFlow });
        delivery.Outcome.Should().Be(Outcome.Violation);
        delivery.Violation!.Kind.Should().Be(Invariants.FlowOffDelivery);
        delivery.Violation.Message.Should().Contain("delivered `a1` upward while its layer 3 has flow off");
        delivery.CounterexampleLength.Should().Be(5);

        Run(Busy(MutantNoSetOwnBusy) with { Invariants = Invariants.Default & ~(Invariants.BusyTracksFlow | Invariants.FlowOffDelivery) })
            .Outcome.Should().Be(Outcome.NoViolation, "behind the two flow checks the mutation is invisible: the data still flows");
    }

    /// <summary>B has data of its own, so its T1 runs and expires while it is busy; one drop lets the poll be needed.</summary>
    private static ExplorerOptions BusyPolled(string tables) => Busy(tables) with { FramesBa = 1, Budget = 1, Faults = FaultKinds.Drop };

    [Fact]
    public void Calibration_Mutation_RR_In_Transmit_Enquiry_Busy_Path_Is_Caught_By_Busy_Rnr()
    {
        // A busy station's timer poll must be RNR P=1; sent as RR P=1 it
        // clears the peer's busy condition and invites I frames the busy
        // station will discard.
        var result = Run(BusyPolled(MutantRrInBusyEnquiry));
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.BusyRnr);
        result.Violation.Station.Should().Be(Station.B);
        result.Violation.Message.Should().Contain("sent RR cmd P=1 nr=1 while its own receiver is busy");
        result.Trace[^1].Transition.Should().BeOneOf("t12_t1_expiry", "t13_t3_expiry", "t21_t1_expiry_no");
        result.Trace[^1].SummaryB.Should().Contain("own_busy");

        // With the check off the mutation's harm is airtime, not order: the
        // peer sends a1 into the busy receiver twice (both discarded), the
        // busy polls consume the retry budget and the run ends in #9's
        // teardown (DL-ERROR T with everything acknowledged).
        var behind = Run(BusyPolled(MutantRrInBusyEnquiry) with { Invariants = Invariants.Default & ~Invariants.BusyRnr });
        behind.Outcome.Should().Be(Outcome.Violation);
        behind.Violation!.Kind.Should().Be(Invariants.Deadlock);
        behind.Trace.Count(s => s.Transition == "t26_i_received_yes_yes_yes_yes_no").Should().BeGreaterThanOrEqualTo(2, "I frames sent into a busy receiver are discarded");
        behind.Trace[^2].Transition.Should().Be("t21_t1_expiry_yes_yes_no");
        behind.Trace[^2].Effects.Should().Contain("dl DL-ERROR Indication (T)");
    }

    [Fact]
    public void Calibration_Mutation_Pop_Ignoring_Peer_Busy_Is_Caught_By_Peer_Busy_Holds()
    {
        var scenario = Busy(Fixtures.Fixed43Dir) with { FramesAb = 3 };
        var result = Run(scenario with { TablesDir = MutantPopIgnoresPeerBusy });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.PeerBusyHolds);
        result.Violation.Station.Should().Be(Station.A);
        result.Violation.Message.Should().Contain("sent I cmd P=0 ns=1 nr=0 [a1] while its peer-receiver-busy condition is set");
        result.CounterexampleLength.Should().Be(5);

        Run(scenario with { TablesDir = MutantPopIgnoresPeerBusy, Invariants = Invariants.Default & ~Invariants.PeerBusyHolds })
            .Outcome.Should().Be(Outcome.NoViolation, "the busy receiver discards what it is sent; the harm is airtime, which no other invariant sees");
        Run(scenario).Outcome.Should().Be(Outcome.NoViolation, "the fixed fixture holds the frames and resumes after RR");
    }

    // ─── Novel hypothesis H4 (docs/explorer.md): answered RNR polls count against N2 ──

    [Fact]
    public void HypothesisH4_Polling_A_Busy_Peer_With_Data_Outstanding_Tears_The_Link_Down_After_N2_Answered_Polls()
    {
        // No faults. B turns flow off after a0 and stays busy; A's a1 is
        // discarded. Every T1 poll is answered with RNR F=1, and because
        // V(s) != N(r) figc4.5 takes the Invoke Retransmission arm (Start T1,
        // RC untouched) instead of the Start T3 / RC := 0 one. After N2
        // answered polls the T1 expiry tears the link down with DL-ERROR (I)
        // while the peer is still busy. §6.4.9 says an RNR answering the poll
        // stops T1 and starts T3; Linux resets n2count on any F=1 answer.
        var result = Run(Scenario43 with { TablesDir = Fixtures.Fixed43Dir });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace.Count(s => s.Transition == "t19_rnr_received_yes_yes_no").Should().Be(4, "four answered polls, each re-queueing a1 and restarting T1 with RC untouched");
        result.Trace.Should().NotContain(s => s.Transition == "pinned:retransmit_old_i_frame", "the re-queued frame is held while the peer is busy");
        result.Trace[^2].Transition.Should().Be("t21_t1_expiry_yes_no");
        result.Trace[^2].Effects.Should().Contain("dl DL-ERROR Indication (I)");
        result.Trace[^1].SummaryB.Should().StartWith("Disconnected").And.Contain("own_busy");
        result.CounterexampleLength.Should().Be(21);

        Run(Scenario43 with { TablesDir = Fixtures.Fixed43Dir, BusyPolls = 3 }).Outcome.Should().Be(Outcome.NoViolation, "a busy period of N2-1 answered polls is survived");
        // N2 answered polls leave RC at N2: the busy period ends, but the
        // next T1 expiry (after everything is acknowledged) tears down with
        // DL-ERROR (T), which is #9 stacked on H4 (33 steps, verified by hand).
        var atN2 = Run(Scenario43 with { TablesDir = Fixtures.Fixed43Dir, BusyPolls = 4 });
        atN2.Violation!.Kind.Should().Be(Invariants.Deadlock);
        atN2.Trace[^2].Transition.Should().Be("t21_t1_expiry_yes_yes_no", "#9: RC was never reset by the progress after FLOW_ON");
        Run(Scenario43 with { TablesDir = Fixtures.Fixed43Dir, BusyPolls = 5 }).CounterexampleLength.Should().Be(21, "the (N2+1)th poll is the H4 teardown");
    }

    // ─── Known signatures the busy family reaches with fewer faults ───

    [Fact]
    public void BusyFamily_H1_Is_Reached_With_No_Channel_Fault_At_All()
    {
        // Both stations turn flow off once; A's poll into busy B is answered
        // RNR F=1 with data outstanding, so A is in TimerRecovery on the
        // retransmission arm; B's busy-clearing RR is a P=0 command, which
        // figc4.5 does not leave TimerRecovery on; the acknowledgement that
        // then arrives on an I frame stops T1 with no exit (H1, ax25spec#91).
        // Both ends end up there. Zero faults; the base grid needed one.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.Fixed43Dir, FramesAb = 2, FramesBa = 2, K = 2, Budget = 0, FlowControl = FlowControlAt.Both, BusyPolls = 2, T3 = true,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[^1].SummaryA.Should().StartWith("TimerRecovery vs=2 va=2").And.Contain("t1=stopped t3=running");
        result.Trace[^1].SummaryB.Should().StartWith("TimerRecovery vs=2 va=2").And.Contain("t1=stopped t3=running");
        result.Trace.Should().Contain(s => s.Transition == "t18_rr_received_no_no_yes", "the busy-clearing RR command P=0 is absorbed inside TimerRecovery");
        result.CounterexampleLength.Should().Be(34);
    }

    [Fact]
    public void BusyFamily_Defect47_Is_Reached_With_No_Channel_Fault_And_At_K4()
    {
        // The busy discard makes the gap SREJ needs, A's poll into busy B
        // puts A in TimerRecovery, and the figc4.5 drain loop then steps V(r)
        // back after delivering the stored frame, so the go-back-N copy of b1
        // is accepted in sequence again. Zero faults, k = 4; the base grid
        // needed k = 2 and two or three drops.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.Fixed43Dir, FramesAb = 2, FramesBa = 3, K = 4, Srej = true, Budget = 0, FlowControl = FlowControlAt.Both, BusyPolls = 2, T3 = true,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Delivery);
        result.Violation.Station.Should().Be(Station.A);
        result.Violation.Message.Should().Contain("delivered `b1` upward").And.Contain("duplicate");
        result.Trace.Should().Contain(s => s.Transition == "t22_i_received_yes_yes_yes_no_yes_no_yes"
            && s.Effects.Contains("dl DL_DATA_indication [b2]"), "the drain delivers the stored frame and steps V(r) back");
        result.CounterexampleLength.Should().Be(29);
    }

    [Fact]
    public void BusyFamily_Defect42_Is_Reached_With_One_Duplicate_Instead_Of_Two_Drops()
    {
        // A discards b1 while busy; after FLOW_ON b2 arrives out of sequence
        // and draws SREJ nr=1; a duplicate of b2 then takes the #42 arm and
        // SREJs the frame A already holds.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.Fixed43Dir, FramesAb = 0, FramesBa = 3, K = 2, Srej = true, Budget = 1, Faults = FaultKinds.Duplicate,
            FlowControl = FlowControlAt.Both, BusyPolls = 2, T3 = true,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.RejectCoherence);
        result.Violation.Station.Should().Be(Station.A);
        result.Violation.Message.Should().Contain("SREJ rsp F=0 nr=2").And.Contain("already holds I(ns=2)");
        result.Trace.Should().Contain(s => s.Transition == "t26_i_received_yes_yes_yes_yes_no", "the busy discard is the gap");
        result.CounterexampleLength.Should().Be(11);
    }

    [Fact]
    public void BusyFamily_A_Lost_Busy_Clearing_RR_Is_Recovered_Only_By_T3()
    {
        // The RR that lifts a busy condition is a P=0 command sent once. When
        // it is lost and the peer has nothing outstanding (T1 stopped), only a
        // T3 poll can discover the peer is ready (§6.4.9). Without the T3
        // move the model deadlocks with A holding a1 behind a stale busy
        // flag; with it the run reaches H1 instead (a lost T3 poll), and
        // counting TimerRecovery as quiescent makes it clean.
        var scenario = new ExplorerOptions
        {
            TablesDir = Fixtures.Fixed43Dir, FramesAb = 2, FramesBa = 1, Budget = 1, Faults = FaultKinds.Drop, FlowControl = FlowControlAt.Both, BusyPolls = 2,
        };
        var withoutT3 = Run(scenario);
        withoutT3.Outcome.Should().Be(Outcome.Violation);
        withoutT3.Violation!.Kind.Should().Be(Invariants.Deadlock);
        withoutT3.Trace.Should().Contain(s => s.Action.StartsWith("DROPS RR cmd P=0", StringComparison.Ordinal));
        withoutT3.Trace[^1].SummaryA.Should().StartWith("Connected").And.Contain("t1=stopped t3=running").And.Contain("peer_busy").And.Contain("q=[a1]");

        var withT3 = Run(scenario with { T3 = true });
        withT3.Outcome.Should().Be(Outcome.Violation);
        withT3.Violation!.Kind.Should().Be(Invariants.Quiescence);
        withT3.Trace.Should().Contain(s => s.Transition == "t13_t3_expiry");
        withT3.Trace.Should().Contain(s => s.Action.StartsWith("DROPS RNR cmd P=1", StringComparison.Ordinal) || s.Action.StartsWith("DROPS RR cmd P=1", StringComparison.Ordinal));

        Run(scenario with { T3 = true, TimerRecoveryIsQuiescent = true }).Outcome.Should().Be(Outcome.NoViolation);
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
        lenient.Outcome.Should().Be(Outcome.NoViolation, "the figc4.2 T1 retry sends SABM and the v2.0 peer accepts it; A stays at modulo 128 (never reassigned by the tables, the #54 blind spot), which this abstract frame model cannot see");
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
}
