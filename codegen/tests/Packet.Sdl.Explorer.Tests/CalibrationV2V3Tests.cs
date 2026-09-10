using Packet.Sdl.Explorer;
using Packet.Sdl.Interpreter;
using Xunit.Abstractions;

namespace Packet.Sdl.Explorer.Tests;

/// <summary>
/// Calibration for two verification issues of the v2.3 programme
/// (packethacking/ax25spec#93): V2 (#95), the channel-reordering half, and
/// V3 (#96), the seeded modulo-128 route. Each has a gate: a catalogued
/// defect that must still fire in the new mode, and a planted mutation in a
/// fixture copy of the tables that the new mode must catch. The rows at the
/// end of the calibration table in docs/explorer.md are derived from here.
/// The premature-T1 half of #95 and the establishment route of #96 (blocked
/// by #54) are not covered.
/// </summary>
public class CalibrationV2V3Tests(ITestOutputHelper output)
{
    private ExplorationResult Run(ExplorerOptions options)
    {
        var result = Fixtures.Run(options);
        output.WriteLine(result.Render());
        return result;
    }

    // ═══ V2 (#95): channel reordering ══════════════════════════════════

    // ─── Gate 1: #40 must still fire with reordering on ───────────────

    private static ExplorerOptions Scenario40(string tables, bool srej, FaultKinds faults) => new()
    {
        TablesDir = tables, FramesAb = 2, FramesBa = 0, K = 4, Srej = srej, Budget = 1, Faults = faults,
    };

    [Fact]
    public void V2_Defect40_Pre40_Tables_Still_Fire_With_Reordering_On()
    {
        foreach (var faults in new[] { FaultKinds.Duplicate | FaultKinds.Reorder, FaultKinds.Drop | FaultKinds.Duplicate | FaultKinds.Reorder })
        {
            foreach (var srej in new[] { true, false })
            {
                var result = Run(Scenario40(Fixtures.Pre40Dir, srej, faults));
                result.Outcome.Should().Be(Outcome.Violation, $"{faults} srej={srej}");
                result.Violation!.Kind.Should().Be(Invariants.RejectCoherence);
                result.Violation.Station.Should().Be(Station.A);
                result.Violation.Message.Should().Contain("REJ nr=1");
                result.CounterexampleLength.Should().Be(4, "the duplicate path is unchanged and still the shortest");
            }
        }
    }

    [Fact]
    public void V2_Reordering_Alone_Rediscovers_Defect40_On_The_Pre40_Tables()
    {
        // A reordered frame is a late copy: I(1) before I(0) draws a REJ nr=0,
        // I(0) is delivered, and the retransmitted I(1) then lands as an
        // out-of-window duplicate, which the pre-40 tables REJ with nr=V(s).
        var result = Run(Scenario40(Fixtures.Pre40Dir, srej: true, FaultKinds.Reorder));
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.RejectCoherence);
        result.Violation.Station.Should().Be(Station.A);
        result.Violation.Message.Should().Contain("REJ nr=2");
        result.CounterexampleLength.Should().Be(9);

        Run(Scenario40(Fixtures.CurrentDir, srej: true, FaultKinds.Reorder)).Outcome.Should().Be(Outcome.NoViolation,
            "the #40 window check discards the late copy, and the reset a stale N(r) causes lands after everything was delivered");
    }

    // ─── Gate 2: the RR-received arm without V(a) <= N(r) <= V(s) ─────

    private static ExplorerOptions MutRr(FaultKinds faults, int budget = 1) => new()
    {
        TablesDir = Fixtures.MutRrNrUncheckedDir, FramesAb = 2, FramesBa = 0, K = 4, Budget = budget, Faults = faults,
    };

    [Fact]
    public void V2_Mutation_RrNrUnchecked_A_Reordered_Acknowledgement_Is_Caught_By_Ack_Coherence()
    {
        // B acknowledges a0 (RR nr=1) and a1 (RR nr=2); the channel delivers
        // nr=2 first. The mutated arm accepts the stale nr=1 and V(a) steps
        // back from 2 to 1, which the absolute acknowledgement counter sees
        // as seven more frames acknowledged than B ever delivered.
        var result = Run(MutRr(FaultKinds.Reorder));
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.AckCoherence);
        result.Violation.Station.Should().Be(Station.A);
        result.Violation.Message.Should().Contain("V(a)=1");
        result.Trace[^2].Action.Should().Contain("RR rsp F=0 nr=2");
        result.Trace[^1].Action.Should().Contain("RR rsp F=0 nr=1");
        result.Trace[^1].Transition.Should().Be("t21_rr_received_yes", "the mutated arm, taken for an out-of-window N(r)");
        result.CounterexampleLength.Should().Be(9);

        var lateDuplicate = Run(MutRr(FaultKinds.Duplicate | FaultKinds.Reorder, budget: 2));
        lateDuplicate.Outcome.Should().Be(Outcome.Violation);
        lateDuplicate.Violation!.Kind.Should().Be(Invariants.AckCoherence);
        lateDuplicate.CounterexampleLength.Should().Be(9);
    }

    [Fact]
    public void V2_Mutation_RrNrUnchecked_Is_Invisible_On_A_Fifo_Channel()
    {
        // A peer never sends an N(r) outside [V(a), V(s)] on an ordered
        // channel, so the removed decision is never false: drop and dup give
        // the current tables' verdict, cell for cell.
        Run(MutRr(FaultKinds.Drop | FaultKinds.Duplicate)).Outcome.Should().Be(Outcome.NoViolation);

        var cell = new ExplorerOptions { TablesDir = Fixtures.MutRrNrUncheckedDir, FramesAb = 3, FramesBa = 3, K = 4, Srej = true, Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate };
        var mutant = Run(cell);
        var current = Run(cell with { TablesDir = Fixtures.CurrentDir });
        mutant.Outcome.Should().Be(current.Outcome);
        mutant.Violation!.Kind.Should().Be(current.Violation!.Kind);
        mutant.Violation.Station.Should().Be(current.Violation.Station);
        mutant.CounterexampleLength.Should().Be(current.CounterexampleLength);

        // And the current tables under the same reorder are clean: the stale
        // RR takes N(r) Error Recovery, but by then everything was delivered.
        Run(MutRr(FaultKinds.Reorder) with { TablesDir = Fixtures.CurrentDir }).Outcome.Should().Be(Outcome.NoViolation);
    }

    // ─── Hypothesis H4 (docs/explorer.md): a stale N(r) resets the link ──

    private static ExplorerOptions ScenarioH4(int modulo) => new()
    {
        TablesDir = Fixtures.CurrentDir, FramesAb = 2, FramesBa = 1, K = 4, Modulo = modulo, Budget = 1, Faults = FaultKinds.Reorder,
    };

    [Fact]
    public void V2_HypothesisH4_A_Stale_Nr_Behind_A_Newer_One_Resets_The_Link_And_Discards_Data()
    {
        // A's I(a0, nr=0) is overtaken by its own RR nr=1. B accepts nr=1,
        // then sees nr=0 as an N(r) sequence error (J), resets with SABM, and
        // A, Connected with a1 outstanding, discards its queue on the SABM.
        // Both end Connected, zeroed, with a0 and a1 never delivered.
        var result = Run(ScenarioH4(8));
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Violation.Message.Should().Contain("at B: 0/2");
        var reset = result.Trace.Single(step => step.Transition == "t26_i_received_yes_yes_no_no");
        reset.Effects.Should().Contain("dl DL-ERROR Indication (J)").And.Contain("frame SABM command pf=1");
        result.Trace.Should().Contain(step => step.Transition == "t14_sabm_received_no", "A had a1 outstanding, so the SABM arm discards its queue");
        result.Trace[^1].SummaryA.Should().StartWith("Connected vs=0 va=0 vr=0").And.Contain("q=[]");
        result.Trace[^1].SummaryB.Should().StartWith("Connected vs=0 va=0 vr=0");
        result.CounterexampleLength.Should().Be(9);
    }

    [Fact]
    public void V2_HypothesisH4_Is_The_Same_At_Modulo_128()
    {
        var result = Run(ScenarioH4(128));
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace.Should().Contain(step => step.Transition == "t26_i_received_yes_yes_no_no");
        result.Trace[^1].SummaryA.Should().Contain("mod=128");
        result.CounterexampleLength.Should().Be(9);
    }

    // ─── Hypothesis H2 reached through reordering ─────────────────────

    private static ExplorerOptions ScenarioH2 => new()
    {
        TablesDir = Fixtures.CurrentDir, FramesAb = 1, FramesBa = 2, K = 2, Budget = 2, Faults = FaultKinds.Reorder,
    };

    [Fact]
    public void V2_HypothesisH2_A_Reject_From_Before_A_Reset_Arriving_After_It_Pushes_The_Whole_Modulus()
    {
        // A REJ nr=0 raised before the H4 reset is overtaken by the SABM and
        // arrives at a freshly zeroed B: N(r) = V(s) = 0 passes the window
        // check and figc4.7's do-while pushes I(0)..I(7), none of which B
        // ever sent. With the receiver-side reject check on, the same REJ is
        // reported as incoherent two steps earlier.
        var result = Run(ScenarioH2 with { Invariants = Invariants.Default & ~Invariants.RejectCoherence });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.MachineError);
        result.Violation.Station.Should().Be(Station.B);
        result.Violation.Message.Should().Contain("retransmission of I(ns=0) requested");
        result.Trace.Should().Contain(step => step.Effects.Contains("dl DL-ERROR Indication (J)"), "the reset comes first");
        result.Trace[^2].Transition.Should().Be("t25_rej_received_yes");
        result.Trace[^2].Effects.Count(e => e.StartsWith("internal Push Old I Frame onto Queue", StringComparison.Ordinal)).Should().Be(8);
        result.CounterexampleLength.Should().Be(11);

        var strict = Run(ScenarioH2);
        strict.Outcome.Should().Be(Outcome.Violation);
        strict.Violation!.Kind.Should().Be(Invariants.RejectCoherence);
        strict.Violation.Message.Should().Contain("receiver").And.Contain("REJ nr=0");
        strict.CounterexampleLength.Should().Be(9);
    }

    // ═══ V3 (#96): the seeded modulo-128 route ═════════════════════════

    // ─── The seed and the interpreter's arithmetic at the wrap ────────

    [Fact]
    public void V3_Seeding_Near_The_Wrap_Numbers_Frames_Across_It()
    {
        foreach (var (modulo, offset) in new[] { (128, 126), (8, 6) })
        {
            var options = new ExplorerOptions { TablesDir = Fixtures.CurrentDir, FramesAb = 3, FramesBa = 0, K = 4, Modulo = modulo, SequenceOffset = offset };
            var stepper = new Stepper(options, Fixtures.Tables(options.TablesDir));
            var state = stepper.Seed();
            state.A.Modulo.Should().Be(modulo);
            state.B.Modulo.Should().Be(modulo);
            state.A.Vs.Should().Be(offset);
            for (var n = 1; n <= 3; n++)
            {
                var outcome = stepper.Apply(state, new Move(MoveKind.Pop, Station.A), n);
                outcome.Violation.Should().BeNull();
                state = outcome.Next;
            }
            state.ToB.Select(f => f.Ns).Should().Equal(offset, offset + 1, 0);
            state.A.Vs.Should().Be(1);
            state.A.Va.Should().Be(offset);
        }
    }

    [Fact]
    public void V3_A_Fault_Free_Exchange_Across_The_Wrap_Reaches_Quiescence_At_Both_Moduli()
    {
        foreach (var (modulo, offset) in new[] { (128, 126), (8, 6) })
        {
            var result = Run(new ExplorerOptions { TablesDir = Fixtures.CurrentDir, FramesAb = 3, FramesBa = 3, K = 4, Modulo = modulo, SequenceOffset = offset, Budget = 0 });
            result.Outcome.Should().Be(Outcome.NoViolation, $"modulo {modulo} offset {offset}");
        }
    }

    [Fact]
    public void V3_The_Seed_Rejects_What_It_Cannot_Mean()
    {
        var ok = new ExplorerOptions { TablesDir = Fixtures.CurrentDir, FramesAb = 1 };
        var tables = Fixtures.Tables(ok.TablesDir);
        foreach (var bad in new[]
                 {
                     ok with { Modulo = 16 },
                     ok with { K = 8 },
                     ok with { Modulo = 128, K = 128 },
                     ok with { Modulo = 128, SequenceOffset = 128 },
                     ok with { SequenceOffset = 3, Seed = SeedKind.Disconnected },
                     ok with { Modulo = 128, Seed = SeedKind.Disconnected },
                     ok with { Modulo128A = true },
                 })
        {
            var act = () => new Stepper(bad, tables);
            act.Should().Throw<ArgumentException>();
        }
        var fine = () => new Stepper(ok with { Modulo = 128, K = 8, SequenceOffset = 127 }, tables);
        fine.Should().NotThrow();
    }

    // ─── Gate 1: #47 must fire at modulo 128 ──────────────────────────

    [Fact]
    public void V3_Defect47_Fires_At_Modulo_128()
    {
        // The k=2, all-checks-on shape, at modulo 128 and again across the wrap.
        foreach (var offset in new[] { 0, 126 })
        {
            var result = Run(new ExplorerOptions
            {
                TablesDir = Fixtures.CurrentDir, FramesAb = 1, FramesBa = 3, K = 2, Modulo = 128, SequenceOffset = offset, Srej = true, Budget = 2, Faults = FaultKinds.Drop,
            });
            result.Outcome.Should().Be(Outcome.Violation, $"offset {offset}");
            result.Violation!.Kind.Should().Be(Invariants.Delivery);
            result.Violation.Station.Should().Be(Station.A);
            result.Violation.Message.Should().Contain("delivered `b1` upward").And.Contain("duplicate");
            result.CounterexampleLength.Should().Be(24);
        }

        // The k=4 shape with reject coherence off, and the same at k=8 (the brief's other window).
        foreach (var k in new[] { 4, 8 })
        {
            var result = Run(new ExplorerOptions
            {
                TablesDir = Fixtures.CurrentDir, FramesAb = 2, FramesBa = 1, K = k, Modulo = 128, Srej = true, Budget = 3, Faults = FaultKinds.Drop,
                Invariants = Invariants.Default & ~Invariants.RejectCoherence,
            });
            result.Outcome.Should().Be(Outcome.Violation, $"k {k}");
            result.Violation!.Kind.Should().Be(Invariants.Delivery);
            result.Violation.Station.Should().Be(Station.B);
            result.Violation.Message.Should().Contain("delivered `a0` upward").And.Contain("duplicate");
            result.Trace[^2].Transition.Should().Be("t22_i_received_yes_yes_yes_no_yes_no_no");
            result.CounterexampleLength.Should().Be(20);
        }
    }

    // ─── #42, #9, H1 and #40 re-fire at modulo 128 at their modulo-8 lengths ──

    [Fact]
    public void V3_Defect42_Fires_At_Modulo_128_At_Both_Windows_And_Across_The_Wrap()
    {
        foreach (var (k, offset, held) in new[] { (4, 0, "I(ns=2)"), (8, 0, "I(ns=2)"), (4, 126, "I(ns=0)") })
        {
            var result = Run(new ExplorerOptions
            {
                TablesDir = Fixtures.CurrentDir, FramesAb = 3, FramesBa = 0, K = k, Modulo = 128, SequenceOffset = offset, Srej = true, Budget = 2, Faults = FaultKinds.Drop,
            });
            result.Outcome.Should().Be(Outcome.Violation, $"k {k} offset {offset}");
            result.Violation!.Kind.Should().Be(Invariants.RejectCoherence);
            result.Violation.Station.Should().Be(Station.B);
            result.Violation.Message.Should().Contain("already holds " + held);
            result.CounterexampleLength.Should().Be(6);
        }
    }

    [Fact]
    public void V3_Defect9_Fires_At_Modulo_128()
    {
        foreach (var (offset, final) in new[] { (0, "Disconnected vs=3 va=3"), (126, "Disconnected vs=1 va=1") })
        {
            var result = Run(new ExplorerOptions
            {
                TablesDir = Fixtures.CurrentDir, FramesAb = 3, FramesBa = 0, K = 4, Modulo = 128, SequenceOffset = offset, N2 = 2, Budget = 3,
                Faults = FaultKinds.Drop, DropScope = DropScope.InteriorI,
            });
            result.Outcome.Should().Be(Outcome.Violation, $"offset {offset}");
            result.Violation!.Kind.Should().Be(Invariants.Deadlock);
            result.Trace[^2].Transition.Should().Be("t21_t1_expiry_yes_yes_no");
            result.Trace[^2].Effects.Should().Contain("dl DL-ERROR Indication (T)");
            result.Trace[^1].SummaryA.Should().StartWith(final).And.Contain("mod=128");
            result.CounterexampleLength.Should().Be(30);
        }
    }

    [Fact]
    public void V3_HypothesisH1_Fires_At_Modulo_128()
    {
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 2, FramesBa = 1, K = 4, Modulo = 128, Srej = false, Budget = 2, Faults = FaultKinds.Drop,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.Deadlock);
        result.Trace[^1].SummaryA.Should().StartWith("TimerRecovery vs=2 va=2").And.Contain("t1=stopped");
        result.CounterexampleLength.Should().Be(19);
    }

    [Fact]
    public void V3_Defect40_Pre40_Tables_Fire_At_Modulo_128()
    {
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.Pre40Dir, FramesAb = 2, FramesBa = 0, K = 4, Modulo = 128, Srej = true, Budget = 1, Faults = FaultKinds.Duplicate,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.RejectCoherence);
        result.Violation.Message.Should().Contain("REJ nr=1");
        result.CounterexampleLength.Should().Be(4);
    }

    [Fact]
    public void V3_Constraint13_Is_The_Half_Modulus_Rule_So_K7_Is_Clean_At_Modulo_128()
    {
        // The modulo-8 row fires at k=7 because 7 > 8/2; at modulo 128 the
        // same late copy sits at distance 126, far outside a window of 7.
        var result = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 3, FramesBa = 0, K = 7, Modulo = 128, Srej = false, Budget = 2, Faults = FaultKinds.Drop | FaultKinds.Duplicate,
        });
        result.Outcome.Should().Be(Outcome.NoViolation);
    }

    // ─── Gate 2: Establish_Data_Link with SABM and SABME swapped ──────

    private static ExplorerOptions EstablishCell(string tables, int modulo, bool coherence) => new()
    {
        TablesDir = tables, FramesAb = 2, FramesBa = 1, K = 4, Modulo = modulo, Budget = 1, Faults = FaultKinds.Reorder,
        Invariants = coherence ? Invariants.Default | Invariants.ModulusCoherence : Invariants.Default,
    };

    [Fact]
    public void V3_Mutation_EstablishSwapped_Is_Caught_By_Modulus_Coherence_On_The_Seeded_Route()
    {
        // From the Connected seed Establish_Data_Link is reached through N(r)
        // Error Recovery (the H4 reorder); on the mutant the mod_128 path
        // then sends SABM, which the emitter-side modulus check reports.
        var result = Run(EstablishCell(Fixtures.MutEstablishSwappedDir, 128, coherence: true));
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.ModulusCoherence);
        result.Violation.Station.Should().Be(Station.B);
        result.Violation.Message.Should().Contain("sent SABM cmd P=1 while operating at modulo 128");
        result.Trace[^1].Transition.Should().Be("t26_i_received_yes_yes_no_no");
        result.Trace[^1].Effects.Should().Contain("dl DL-ERROR Indication (J)").And.Contain("frame SABM command pf=1");
        result.CounterexampleLength.Should().Be(7);

        // The current tables at the same cell: SABME at modulo 128, no
        // coherence violation, the H4 deadlock two steps later instead.
        var current = Run(EstablishCell(Fixtures.CurrentDir, 128, coherence: true));
        current.Outcome.Should().Be(Outcome.Violation);
        current.Violation!.Kind.Should().Be(Invariants.Deadlock);
        current.CounterexampleLength.Should().Be(9);

        // Without the check the mutation is invisible: the peer accepts the
        // SABM, stays at modulo 128 (#54) and the run ends in the same H4.
        var blind = Run(EstablishCell(Fixtures.MutEstablishSwappedDir, 128, coherence: false));
        blind.Outcome.Should().Be(Outcome.Violation);
        blind.Violation!.Kind.Should().Be(Invariants.Deadlock);
        blind.CounterexampleLength.Should().Be(9);

        // And the other half of the swap, at modulo 8.
        var mod8 = Run(EstablishCell(Fixtures.MutEstablishSwappedDir, 8, coherence: true));
        mod8.Outcome.Should().Be(Outcome.Violation);
        mod8.Violation!.Kind.Should().Be(Invariants.ModulusCoherence);
        mod8.Violation.Message.Should().Contain("sent SABME cmd P=1 while operating at modulo 8");
        mod8.CounterexampleLength.Should().Be(7);
    }

    [Fact]
    public void V3_Mutation_EstablishSwapped_Is_Caught_On_The_Figc46_Frmr_Route_Too()
    {
        // figc4.6 t14 (the #45 arm) calls Establish_Data_Link at modulo 128:
        // SABME on the current tables, SABM on the mutant.
        var route = new ExplorerOptions
        {
            TablesDir = Fixtures.MutEstablishSwappedDir, Seed = SeedKind.AwaitingV22Connection, Peer = PeerKind.V20Frmr, FramesAb = 0, Budget = 0,
            Invariants = Invariants.Default | Invariants.ModulusCoherence,
        };
        var result = Run(route);
        result.Outcome.Should().Be(Outcome.Violation);
        result.Violation!.Kind.Should().Be(Invariants.ModulusCoherence);
        result.Violation.Station.Should().Be(Station.A);
        result.Trace[1].Transition.Should().Be("t14_frmr_received");
        result.Trace[1].Effects.Should().Contain("frame SABM command pf=1");
        result.CounterexampleLength.Should().Be(2);

        // On the current tables t14 sends SABME (#45) and the check fires
        // later, at figc4.2's T1 retry, which sends SABM while A is still at
        // modulo 128: the #44 / #54 stack, now visible.
        var current = Run(route with { TablesDir = Fixtures.CurrentDir });
        current.Outcome.Should().Be(Outcome.Violation);
        current.Violation!.Kind.Should().Be(Invariants.ModulusCoherence);
        current.Trace[1].Effects.Should().Contain("frame SABME command pf=1");
        current.Trace[^1].Transition.Should().Be("t05_t1_expiry_no");
        current.CounterexampleLength.Should().Be(5);
    }

    // ─── Modulus coherence on the current tables: #54 becomes visible ──

    [Fact]
    public void V3_Modulus_Coherence_Sees_Defect54_On_The_Connect_Phase_Seeds()
    {
        // The responder accepts a SABME and stays at modulo 8: the tables
        // never invoke Set_Version_2_2 (#54). Both stations Connected, A at
        // 128 and B at 8, two steps from either connect-phase seed.
        foreach (var options in new[]
                 {
                     new ExplorerOptions { TablesDir = Fixtures.CurrentDir, Seed = SeedKind.AwaitingV22Connection, FramesAb = 0, Invariants = Invariants.Default | Invariants.ModulusCoherence },
                     new ExplorerOptions { TablesDir = Fixtures.CurrentDir, Seed = SeedKind.Disconnected, Modulo128A = true, FramesAb = 1, Invariants = Invariants.Default | Invariants.ModulusCoherence },
                 })
        {
            var result = Run(options);
            result.Outcome.Should().Be(Outcome.Violation, options.Seed.ToString());
            result.Violation!.Kind.Should().Be(Invariants.ModulusCoherence);
            result.Violation.Station.Should().BeNull("agreement is a property of the pair");
            result.Violation.Message.Should().Contain("A is at modulo 128 and B at modulo 8");
            result.Trace[0].Transition.Should().Be("t14_sabme_received_yes");
            result.Trace[^1].SummaryA.Should().Contain("mod=128");
            result.Trace[^1].SummaryB.Should().Contain("mod=8");
            result.CounterexampleLength.Should().Be(2);
        }

        // Cold connect against the v2.0 stub: figc4.2's retry sends SABM at
        // modulo 128 (#44 put A on figc4.2, #54 never downgraded it).
        var stub = Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, Seed = SeedKind.Disconnected, Modulo128A = true, Peer = PeerKind.V20Frmr, FramesAb = 0,
            Invariants = Invariants.Default | Invariants.ModulusCoherence,
        });
        stub.Outcome.Should().Be(Outcome.Violation);
        stub.Violation!.Kind.Should().Be(Invariants.ModulusCoherence);
        stub.Violation.Message.Should().Contain("sent SABM cmd P=1 while operating at modulo 128");
        stub.Trace[^1].Transition.Should().Be("t05_t1_expiry_no");
        stub.CounterexampleLength.Should().Be(3);
    }

    [Fact]
    public void V3_Modulus_Coherence_Is_Silent_On_A_Link_Seeded_Consistently()
    {
        foreach (var modulo in new[] { 8, 128 })
        {
            var result = Run(new ExplorerOptions
            {
                TablesDir = Fixtures.CurrentDir, FramesAb = 2, FramesBa = 1, K = 4, Modulo = modulo, Budget = 1, Faults = FaultKinds.Drop,
                Invariants = Invariants.Default | Invariants.ModulusCoherence,
            });
            result.Outcome.Should().Be(Outcome.NoViolation, $"modulo {modulo}");
        }
    }
}
