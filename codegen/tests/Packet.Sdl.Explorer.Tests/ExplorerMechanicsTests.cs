using Packet.Sdl.Explorer;
using Xunit.Abstractions;

namespace Packet.Sdl.Explorer.Tests;

/// <summary>The model's own mechanics: a fault-free exchange converges, bounds report as bounds, traces read as logs.</summary>
public class ExplorerMechanicsTests(ITestOutputHelper output)
{
    [Fact]
    public void Fault_Free_Bidirectional_Exchange_Reaches_Quiescence_Everywhere()
    {
        var result = Fixtures.Run(new ExplorerOptions { TablesDir = Fixtures.CurrentDir, FramesAb = 2, FramesBa = 1, Budget = 0 });
        output.WriteLine(result.Render());
        result.Outcome.Should().Be(Outcome.NoViolation);
        result.StatesExplored.Should().BeGreaterThan(1);
    }

    [Fact]
    public void A_Single_Drop_With_Srej_Off_Is_Recovered_By_Rej_Or_T1()
    {
        var result = Fixtures.Run(new ExplorerOptions { TablesDir = Fixtures.CurrentDir, FramesAb = 3, Budget = 1, Faults = FaultKinds.Drop });
        output.WriteLine(result.Render());
        result.Outcome.Should().Be(Outcome.NoViolation);
    }

    [Fact]
    public void Hitting_The_State_Cap_Is_Reported_As_A_Bound_Not_As_Silence()
    {
        var result = Fixtures.Run(new ExplorerOptions { TablesDir = Fixtures.CurrentDir, FramesAb = 3, FramesBa = 3, Budget = 1, MaxStates = 50 });
        result.Outcome.Should().Be(Outcome.BoundHit);
        result.StateBoundHit.Should().BeTrue();
    }

    [Fact]
    public void Hitting_The_Depth_Cap_Is_Reported_As_A_Bound_With_A_Witness_Path()
    {
        var result = Fixtures.Run(new ExplorerOptions { TablesDir = Fixtures.CurrentDir, FramesAb = 2, FramesBa = 1, Budget = 0, MaxDepth = 3 });
        result.Outcome.Should().Be(Outcome.BoundHit);
        result.DepthBoundHit.Should().BeTrue();
        result.Trace.Should().HaveCount(3, "the witness is the path to the first state at the depth bound");
    }

    [Fact]
    public void Seeded_State_Fingerprints_Are_Deterministic()
    {
        var options = new ExplorerOptions { TablesDir = Fixtures.CurrentDir, FramesAb = 2, FramesBa = 2, Budget = 1 };
        var stepper = new Stepper(options, Fixtures.Tables(options.TablesDir));
        stepper.Seed().Fingerprint().Should().Be(stepper.Seed().Fingerprint());
        stepper.Seed().A.Queue.Should().Equal("a0", "a1");
        stepper.Seed().B.Queue.Should().Equal("b0", "b1");
    }

    [Fact]
    public void A_Counterexample_Reads_As_A_Step_Log()
    {
        var result = Fixtures.Run(new ExplorerOptions
        {
            TablesDir = Fixtures.Pre40Dir, FramesAb = 2, K = 4, Srej = true, Budget = 1, Faults = FaultKinds.Duplicate,
        });
        result.Outcome.Should().Be(Outcome.Violation);
        var text = result.Render();
        text.Should().Contain("transition: t03_i_frame_pops_off_queue_no_no_no");
        text.Should().Contain("channel fault: DUPLICATE");
        text.Should().Contain("A: Connected vs=");
        text.Should().Contain("B: Connected vs=");
    }

    [Fact]
    public void Selective_Recovery_Completes_Without_T1_For_An_Interior_Loss()
    {
        // 2 frames, the first lost: SREJ from the second recovers it with no timer.
        var result = Fixtures.Run(new ExplorerOptions
        {
            TablesDir = Fixtures.CurrentDir, FramesAb = 2, K = 4, Srej = true, Budget = 1,
            Faults = FaultKinds.Drop, DropScope = DropScope.InteriorI,
            Invariants = Invariants.Default | Invariants.SelectiveProgress,
        });
        output.WriteLine(result.Render());
        result.Outcome.Should().Be(Outcome.NoViolation);
    }
}
