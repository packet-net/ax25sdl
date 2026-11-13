using Packet.Sdl.Explorer;
using Packet.Sdl.Interpreter;

namespace Packet.Sdl.TlaCheck.Tests;

/// <summary>
/// How a grid cell is judged when the explorer's search was cut short by its
/// state cap or depth limit (packet-net/ax25sdl#112, the 13 November grid
/// runs): capped whatever the explorer's verdict, state counts not compared,
/// verdict kinds compared where the explorer found a violation.
/// </summary>
public class GridVerdictTests
{
    private static readonly Lazy<string> RepoRoot = new(() =>
    {
        var d = new DirectoryInfo(Path.GetDirectoryName(typeof(GridVerdictTests).Assembly.Location)!);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "spec-sdl"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("no spec-sdl/ in any ancestor of the test assembly");
    });

    private static string CurrentDir => Path.Combine(RepoRoot.Value, "spec", "json");

    private static Row Judged(string capped, string explorerKind, string tlcKind, params string[] differences)
    {
        var (agree, why) = GridVerdict.Judge(capped, explorerKind, tlcKind, differences);
        var none = new HashSet<string>();
        return new Row("", "", agree, why, none, none, null, explorerKind, tlcKind, Capped: capped);
    }

    [Fact]
    public void Cap_is_set_whatever_the_verdict_on_a_grid_cell()
    {
        GridVerdict.Cap(gridCell: true, depthBoundRow: false, depthBoundHit: false, stateBoundHit: true).Should().Be("states");
        GridVerdict.Cap(true, false, true, false).Should().Be("depth");
        GridVerdict.Cap(true, false, false, false).Should().Be("");
        // Calibration rows, and rows that rely on the depth bound, are never capped.
        GridVerdict.Cap(false, false, true, true).Should().Be("");
        GridVerdict.Cap(true, true, true, false).Should().Be("");
    }

    // The 13 November cells (frame-errors, reconnect-data, two inj-frmr-b01):
    // both tools found the same violation, the explorer at its 1.2 million cap
    // and TLC past it. They were recorded as disagreeing on the state count.
    [Theory]
    [InlineData("Deadlock", "Deadlock")]
    [InlineData("ResetLoss", "ResetLoss")]
    public void A_capped_cell_with_the_same_violation_is_capped_not_a_disagreement(string explorer, string tlc)
    {
        var r = Judged("states", explorer, tlc, "states 1200000 vs 1507245");
        r.Agree.Should().BeFalse();
        r.Why.Should().BeEmpty();
        r.CappedFinding.Should().BeFalse();
        GridVerdict.Passes(r).Should().BeTrue();
        GridVerdict.Label(r).Should().Be("capped, same verdict:");
        GridVerdict.Label(Judged("states", "bound", "timeout")).Should().Be("capped, not compared:");
    }

    // A violation the explorer reports under its cap is a reachable state:
    // TLC, searching the whole graph, must report the same one.
    [Theory]
    [InlineData("Deadlock", "ResetLoss")]
    [InlineData("Deadlock", "clean")]
    [InlineData("ResetLoss", "clean")]
    public void A_capped_cell_where_TLC_does_not_find_the_explorers_violation_disagrees(string explorer, string tlc)
    {
        var r = Judged("states", explorer, tlc);
        r.Why.Should().Be($"verdict {explorer} vs {tlc}");
        r.CappedFinding.Should().BeFalse();
        GridVerdict.Passes(r).Should().BeFalse();
        GridVerdict.Label(r).Should().Be("CAPPED, VERDICTS DIFFER:");
    }

    [Theory]
    [InlineData("bound", "Deadlock", true)]   // flow-behind43's largest cell
    [InlineData("bound", "clean", false)]
    [InlineData("bound", "timeout", false)]
    [InlineData("Deadlock", "timeout", false)] // local-busy's k=2 cell: explorer found it, TLC ran out of time
    [InlineData("Deadlock", "Deadlock", false)]
    public void A_finding_is_a_violation_only_TLC_found(string explorer, string tlc, bool finding)
    {
        var r = Judged("states", explorer, tlc);
        r.CappedFinding.Should().Be(finding);
        GridVerdict.Passes(r).Should().BeTrue();
    }

    [Fact]
    public void An_uncapped_cell_is_compared_in_full()
    {
        Judged("", "Deadlock", "Deadlock", "states 10 vs 11").Should().Match<Row>(r => !r.Agree && r.Why == "states 10 vs 11");
        Judged("", "clean", "clean").Agree.Should().BeTrue();
        GridVerdict.Label(Judged("", "clean", "timeout", "verdict clean vs timeout")).Should().Be("TIMEOUT ");
    }

    /// <summary>
    /// Seeded: a frame-errors grid cell whose reset loss (4 steps, 32
    /// states) the explorer still reports when its state cap stops the
    /// search short, as the 1.2 million cap did on the largest frame-errors
    /// cell on 13 November. Such a cell is capped, not compared on states.
    /// </summary>
    [Fact]
    public void The_explorer_reports_a_loss_past_its_cap_and_the_cell_is_capped()
    {
        var (with, tag) = GridOptions.Parse(["frame-errors=control+info+length+n1+i-response"], stubGrid: false);
        var cell = Scenarios.Grid(CurrentDir, flowB: false, with, tag).Single(c => c.Name == "g-ab0-ba1-b1-k2-nosrej-dup@" + tag);
        var tables = TableLoader.Load(CurrentDir);
        var full = Explorer.Explorer.Run(cell.Options, tables);
        full.Violation!.Kind.Should().Be(Invariants.ResetLoss);
        full.StateBoundHit.Should().BeFalse();
        GridVerdict.Cap(true, cell.DepthBound, full.DepthBoundHit, full.StateBoundHit).Should().Be("");

        var cut = Explorer.Explorer.Run(cell.Options with { MaxStates = full.StatesExplored - 1 }, tables);
        cut.Outcome.Should().Be(Outcome.Violation);
        cut.Violation!.Kind.Should().Be(Invariants.ResetLoss);
        cut.StateBoundHit.Should().BeTrue();
        GridVerdict.Cap(true, cell.DepthBound, cut.DepthBoundHit, cut.StateBoundHit).Should().Be("states");
    }

    [Fact]
    public void Caps_raise_the_limits_and_zero_means_none()
    {
        var o = new ExplorerOptions { TablesDir = "", MaxStates = 1_200_000, MaxDepth = 80 };
        GridOptions.Caps(null, null).Should().BeNull();
        GridOptions.Caps(5_000_000, null)!(o).Should().Match<ExplorerOptions>(x => x.MaxStates == 5_000_000 && x.MaxDepth == 80);
        GridOptions.Caps(0, 0)!(o).Should().Match<ExplorerOptions>(x => x.MaxStates == int.MaxValue && x.MaxDepth == int.MaxValue);
    }

    [Fact]
    public void Cells_from_a_list_run_with_the_options_their_names_carry()
    {
        var cells = GridOptions.FromNames(
            [
                "# leftovers",
                "g-ab3-ba3-b2-k4-nosrej-drop+dup@frame-errors=control+info+length+n1+i-response",
                "",
                "g-ab3-ba3-b2-k4-nosrej-drop+dup-flowB@disable=busy-tracks-flow+flow-off-delivery  # capped",
                "g-ab3-ba3-b2-k4-nosrej-drop+dup",
                "s-cold-frmr-b2-drop+dup-sp",
            ],
            CurrentDir,
            GridOptions.Caps(0, null));
        cells.Select(c => c.Cell.Name).Should().Equal(
            "g-ab3-ba3-b2-k4-nosrej-drop+dup@frame-errors=control+info+length+n1+i-response",
            "g-ab3-ba3-b2-k4-nosrej-drop+dup-flowB@disable=busy-tracks-flow+flow-off-delivery",
            "g-ab3-ba3-b2-k4-nosrej-drop+dup",
            "s-cold-frmr-b2-drop+dup-sp");
        // Each cell's index in its own grid, as --list prints it.
        cells[0].Index.Should().Be(572);
        cells[0].Cell.Options.FrameErrors.Should().NotBe(FrameErrors.None);
        cells[1].Cell.Options.FlowControl.Should().Be(FlowControlAt.B);
        cells[1].Cell.Options.Invariants.HasFlag(Invariants.FlowOffDelivery).Should().BeFalse();
        cells[2].Cell.Options.FrameErrors.Should().Be(FrameErrors.None);
        cells[3].Cell.Options.Peer.Should().Be(PeerKind.V20Frmr);
        cells.Should().OnlyContain(c => c.Cell.Options.MaxStates == int.MaxValue);
    }

    [Theory]
    [InlineData("g-ab9-ba3-b2-k4-nosrej-drop+dup", "no grid has")]
    [InlineData("x-ab3", "not a grid cell name")]
    [InlineData("g-ab3-ba3-b2-k4-nosrej-drop+dup@--t3", "write the options as a grid run names them")]
    [InlineData("g-ab3-ba3-b2-k4-nosrej-drop+dup@max-states=5", "--grid-max-states")]
    public void A_name_no_grid_has_is_refused(string name, string message)
    {
        var act = () => GridOptions.FromNames([name], CurrentDir);
        act.Should().Throw<ArgumentException>().WithMessage($"*{message}*");
    }
}
