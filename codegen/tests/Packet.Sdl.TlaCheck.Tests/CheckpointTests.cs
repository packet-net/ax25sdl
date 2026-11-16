using Packet.Sdl.Explorer;

namespace Packet.Sdl.TlaCheck.Tests;

/// <summary>
/// Checkpoint and resume for grid runs (packet-net/ax25sdl#131): which TLC
/// passes a resumed cell reads back, recovers from a checkpoint or runs again,
/// and the <c>--checkpoint-minutes</c> option.
/// </summary>
public class CheckpointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tlacheck-ckpt-").FullName;

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private const string Config = "CONSTANTS\n    K = 4\nINIT Init\nNEXT Next\n";

    // A TLC deadlock report, as tlc_Deadlock.out holds it.
    private const string DeadlockOut = """
        TLC2 Version 2.19 of 08 August 2024 (rev: 5a47802)
        Error: Deadlock reached.
        Error: The behavior up to this point is:
        State 1: <Initial predicate>
        /\ last = [move |-> "seed", at |-> "", tid |-> "", lossy |-> "no"]
        /\ viol = [kind |-> "", at |-> "", msg |-> ""]

        State 2: <Send line 10, col 5 to line 12, col 40 of module TwoStation>
        /\ last = [move |-> "A sends", at |-> "A", tid |-> "Connected/t03_i_frame_pops_off_queue_no_no_no", lossy |-> "no"]
        /\ viol = [kind |-> "", at |-> "", msg |-> ""]

        12 states generated, 7 distinct states found, 0 states left on queue.
        The depth of the complete state graph search is 3.
        Finished in 01s at (2026-11-16 18:00:00)

        """;

    private static PassProgress Progress(string config = Config, int workers = 1) => new(Checkpoint.Hash(config), 77, workers, TimeSpan.FromHours(5).Ticks);

    /// <summary>A pass's metadir as TLC leaves it after a checkpoint, with the given files in its run directory.</summary>
    private string Meta(Phase phase, PassProgress? progress, params string[] files)
    {
        var meta = Checkpoint.MetaDir(_dir, phase);
        var run = Path.Combine(meta, "26-11-16-18-24-09");
        Directory.CreateDirectory(run);
        if (progress is not null) Checkpoint.WriteProgress(_dir, phase, progress);
        foreach (var f in files) File.WriteAllText(Path.Combine(run, f), "");
        return run;
    }

    private static readonly string[] WholeCheckpoint = ["queue.chkpt", "TwoStation-0.chkpt", "TwoStation-0.st", "TwoStation.st.chkpt", "vars.chkpt", "TwoStation_0.fp"];

    // ─── --checkpoint-minutes ────────────────────────────────────────────

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("30", 30)]
    [InlineData("1440", 1440)]
    public void Checkpoint_minutes_take_a_non_negative_whole_number(string value, int minutes) =>
        Checkpoint.ParseMinutes(value).Should().Be(minutes);

    [Theory]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData("1.5")]
    [InlineData("30m")]
    [InlineData("")]
    public void Checkpoint_minutes_refuse_anything_else(string value) =>
        FluentActions.Invoking(() => Checkpoint.ParseMinutes(value)).Should().Throw<ArgumentException>().WithMessage("--checkpoint-minutes*");

    [Fact]
    public void Checkpoint_minutes_need_a_cross_check_grid_run()
    {
        Checkpoint.Refusal(null, gridMode: false, lockstep: false, coverageOnly: false).Should().BeNull();
        Checkpoint.Refusal(10, gridMode: true, lockstep: false, coverageOnly: false).Should().BeNull();
        Checkpoint.Refusal(10, gridMode: false, lockstep: false, coverageOnly: false).Should().Contain("needs a grid");
        Checkpoint.Refusal(10, gridMode: true, lockstep: true, coverageOnly: false).Should().Contain("not with --lockstep");
        Checkpoint.Refusal(10, gridMode: true, lockstep: false, coverageOnly: true).Should().Contain("not with --lockstep or --coverage-only");
        // Turning them off is never refused on a grid.
        Checkpoint.Refusal(0, gridMode: true, lockstep: true, coverageOnly: false).Should().BeNull();
    }

    [Fact]
    public void A_cross_check_grid_run_checkpoints_every_thirty_minutes_unless_told_otherwise()
    {
        Checkpoint.Minutes(null, gridMode: true, lockstep: false, coverageOnly: false).Should().Be(30);
        Checkpoint.Minutes(5, gridMode: true, lockstep: false, coverageOnly: false).Should().Be(5);
        Checkpoint.Minutes(0, gridMode: true, lockstep: false, coverageOnly: false).Should().Be(0);
        // Calibration, lockstep and coverage-only runs never checkpoint.
        Checkpoint.Minutes(null, gridMode: false, lockstep: false, coverageOnly: false).Should().Be(0);
        Checkpoint.Minutes(null, gridMode: true, lockstep: true, coverageOnly: false).Should().Be(0);
        Checkpoint.Minutes(null, gridMode: true, lockstep: false, coverageOnly: true).Should().Be(0);
    }

    [Fact]
    public void Only_the_safety_deadlock_and_liveness_passes_recover_from_a_checkpoint()
    {
        Checkpoint.Recoverable(Phase.Safety).Should().BeTrue();
        Checkpoint.Recoverable(Phase.Deadlock).Should().BeTrue();
        Checkpoint.Recoverable(Phase.Quiescence).Should().BeTrue();
        Checkpoint.Recoverable(Phase.TimerFree).Should().BeTrue();
        // A recovered Coverage pass would dump only the states found after the checkpoint.
        Checkpoint.Recoverable(Phase.Coverage).Should().BeFalse();
        Checkpoint.Recoverable(Phase.Lockstep).Should().BeFalse();
        Checkpoint.Reusable(Phase.Coverage).Should().BeTrue();
        Checkpoint.Reusable(Phase.Lockstep).Should().BeFalse();
    }

    // ─── Which passes are skipped, recovered or run again ────────────────

    [Fact]
    public void A_pass_with_nothing_left_by_an_earlier_run_runs_from_the_start()
    {
        var plan = Checkpoint.Plan(_dir, Phase.Safety, Config, 1);
        plan.Action.Should().Be(PassAction.Fresh);
        plan.Why.Should().BeEmpty();
    }

    [Fact]
    public void A_finished_pass_is_read_back()
    {
        File.WriteAllText(Checkpoint.OutFile(_dir, Phase.Deadlock), DeadlockOut);
        Checkpoint.WriteDone(_dir, Phase.Deadlock, new PassDone(Checkpoint.Hash(Config), 1234, null));
        var plan = Checkpoint.Plan(_dir, Phase.Deadlock, Config, 1);
        plan.Action.Should().Be(PassAction.Reuse);
        plan.Done!.ElapsedTicks.Should().Be(1234);
    }

    [Fact]
    public void A_finished_pass_under_another_configuration_runs_again()
    {
        File.WriteAllText(Checkpoint.OutFile(_dir, Phase.Deadlock), DeadlockOut);
        Checkpoint.WriteDone(_dir, Phase.Deadlock, new PassDone(Checkpoint.Hash(Config), 1234, null));
        var plan = Checkpoint.Plan(_dir, Phase.Deadlock, Config + "CONSTRAINT WithinDepth\n", 1);
        plan.Action.Should().Be(PassAction.Fresh);
        plan.Why.Should().Contain("another configuration");
    }

    [Fact]
    public void A_pass_that_only_left_its_output_runs_again()
    {
        // A timeout or a failure writes tlc_<Phase>.out but no .done: not a verdict to reuse.
        File.WriteAllText(Checkpoint.OutFile(_dir, Phase.Safety), "TLC threw an unexpected exception");
        Checkpoint.Plan(_dir, Phase.Safety, Config, 1).Action.Should().Be(PassAction.Fresh);
    }

    [Fact]
    public void A_finished_coverage_pass_is_read_back_with_its_arms()
    {
        File.WriteAllText(Checkpoint.OutFile(_dir, Phase.Coverage), DeadlockOut);
        Checkpoint.WriteDone(_dir, Phase.Coverage, new PassDone(Checkpoint.Hash(Config), 99, ["Connected/t03"]));
        var plan = Checkpoint.Plan(_dir, Phase.Coverage, Config, 1);
        plan.Action.Should().Be(PassAction.Reuse);
        plan.Done!.Arms.Should().Equal("Connected/t03");
    }

    [Theory]
    [InlineData(Phase.Safety)]
    [InlineData(Phase.Deadlock)]
    [InlineData(Phase.Quiescence)]
    [InlineData(Phase.TimerFree)]
    public void An_interrupted_pass_recovers_from_its_checkpoint(Phase phase)
    {
        var run = Meta(phase, Progress(), WholeCheckpoint);
        var plan = Checkpoint.Plan(_dir, phase, Config, 1);
        plan.Action.Should().Be(PassAction.Recover);
        plan.CheckpointDir.Should().Be(run);
        plan.Progress!.Fp.Should().Be(77);
        plan.Progress.ElapsedTicks.Should().Be(TimeSpan.FromHours(5).Ticks);
    }

    [Fact]
    public void An_interrupted_coverage_pass_runs_from_the_start()
    {
        Meta(Phase.Coverage, Progress(), WholeCheckpoint);
        Checkpoint.Plan(_dir, Phase.Coverage, Config, 1).Action.Should().Be(PassAction.Fresh);
    }

    [Fact]
    public void An_interrupted_pass_without_a_completed_checkpoint_runs_from_the_start()
    {
        Meta(Phase.Safety, Progress(), "TwoStation-0.st", "TwoStation_0.fp");
        var plan = Checkpoint.Plan(_dir, Phase.Safety, Config, 1);
        plan.Action.Should().Be(PassAction.Fresh);
        plan.Why.Should().Contain("no checkpoint");
    }

    [Fact]
    public void A_checkpoint_without_its_progress_record_is_not_used()
    {
        // No record of the fingerprint polynomial: a recovery under another would count states twice.
        Meta(Phase.Safety, null, WholeCheckpoint);
        Checkpoint.Plan(_dir, Phase.Safety, Config, 1).Action.Should().Be(PassAction.Fresh);
    }

    [Fact]
    public void A_checkpoint_under_another_configuration_is_not_used()
    {
        Meta(Phase.Quiescence, Progress(Config + "PROPERTY QuiescenceLive\n"), WholeCheckpoint);
        var plan = Checkpoint.Plan(_dir, Phase.Quiescence, Config, 1);
        plan.Action.Should().Be(PassAction.Fresh);
        plan.Why.Should().Contain("another configuration");
    }

    [Fact]
    public void A_checkpoint_taken_with_another_number_of_workers_is_not_used()
    {
        Meta(Phase.Safety, Progress(workers: 4), WholeCheckpoint);
        var plan = Checkpoint.Plan(_dir, Phase.Safety, Config, 1);
        plan.Action.Should().Be(PassAction.Fresh);
        plan.Why.Should().Contain("4 TLC worker(s)");
    }

    [Fact]
    public void A_finished_verdict_wins_over_a_leftover_checkpoint()
    {
        Meta(Phase.Safety, Progress(), WholeCheckpoint);
        File.WriteAllText(Checkpoint.OutFile(_dir, Phase.Safety), DeadlockOut);
        Checkpoint.WriteDone(_dir, Phase.Safety, new PassDone(Checkpoint.Hash(Config), 5, null));
        Checkpoint.Plan(_dir, Phase.Safety, Config, 1).Action.Should().Be(PassAction.Reuse);
    }

    [Fact]
    public void A_checkpoint_stopped_part_way_through_its_renames_is_not_used()
    {
        // The queue is renamed (no queue.tmp), the intern table and a trace are not: two checkpoints mixed.
        Meta(Phase.Safety, Progress(), [.. WholeCheckpoint, "vars.tmp", "TwoStation-0.tmp"]);
        var plan = Checkpoint.Plan(_dir, Phase.Safety, Config, 1);
        plan.Action.Should().Be(PassAction.Fresh);
        plan.Why.Should().Contain("committing a checkpoint");
    }

    [Fact]
    public void Whole_checkpoint_files_are_told_from_torn_ones()
    {
        var run = Path.Combine(_dir, "run");
        Directory.CreateDirectory(run);
        void Files(params string[] names)
        {
            foreach (var f in Directory.GetFiles(run)) File.Delete(f);
            foreach (var n in names) File.WriteAllText(Path.Combine(run, n), "");
        }
        Files(WholeCheckpoint);
        Checkpoint.Whole(run).Should().BeTrue();
        // Stopped while writing the next one: the queue's .tmp is still there, so nothing was renamed yet.
        Files([.. WholeCheckpoint, "queue.tmp", "TwoStation-0.tmp", "vars.tmp"]);
        Checkpoint.Whole(run).Should().BeTrue();
        // The liveness graph not yet renamed, everything before it renamed.
        Files([.. WholeCheckpoint, "dgraph_0.chkpt.tmp"]);
        Checkpoint.Whole(run).Should().BeFalse();
        // A fingerprint set flushing to disk is not a checkpoint file.
        Files([.. WholeCheckpoint, "TwoStation_0.fp.tmp"]);
        Checkpoint.Whole(run).Should().BeTrue();
        // The first checkpoint never committed.
        Files("queue.tmp", "TwoStation-0.st");
        Checkpoint.Whole(run).Should().BeFalse();
    }

    // ─── Reading a finished pass back ────────────────────────────────────

    [Fact]
    public void A_finished_pass_reads_back_as_the_run_that_wrote_it_reported()
    {
        var o = new ExplorerOptions { TablesDir = _dir };
        var config = Tlc.Config(o, Phase.Deadlock, false);
        File.WriteAllText(Checkpoint.OutFile(_dir, Phase.Deadlock), DeadlockOut);
        var elapsed = TimeSpan.FromSeconds(123.4567);
        Checkpoint.WriteDone(_dir, Phase.Deadlock, new PassDone(Checkpoint.Hash(config), elapsed.Ticks, null));
        // No java is started: the jar does not exist.
        var tlc = new Tlc(Path.Combine(_dir, "no.jar"), "TwoStation.tla", _dir, Tlc.DefaultMeaning, 1, "1g", TimeSpan.FromSeconds(1))
        {
            CheckpointMinutes = 30,
            KeepProgress = true,
            Resume = true,
        };

        var reused = tlc.Run(_dir, o, Phase.Deadlock, false);

        var fresh = Tlc.Parse(DeadlockOut, elapsed);
        reused.Resumed.Should().Be("reused");
        reused.Should().BeEquivalentTo(fresh, c => c.Excluding(r => r.Resumed));
        reused.Elapsed.Should().Be(elapsed);
        reused.Kind.Should().Be("deadlock");
        reused.Trace.Should().HaveCount(2);
    }

    // ─── The cell's model directory and the explorer's result ────────────

    private void Prepare()
    {
        Directory.CreateDirectory(Cell);
        File.WriteAllText(Path.Combine(Cell, "TwoStation.tla"), "---- MODULE TwoStation ----\n====\n");
    }

    private string Cell => Path.Combine(_dir, "cell");

    [Fact]
    public void Without_resume_the_cell_directory_starts_empty()
    {
        Directory.CreateDirectory(Cell);
        File.WriteAllText(Path.Combine(Cell, "tlc_Safety.out"), "old");
        Checkpoint.OpenCell(Cell, "options", resume: false, Prepare).Should().BeFalse();
        File.Exists(Path.Combine(Cell, "tlc_Safety.out")).Should().BeFalse();
        File.Exists(Path.Combine(Cell, Checkpoint.CellFile)).Should().BeTrue();
    }

    [Fact]
    public void On_resume_the_cell_directory_is_kept_for_the_same_model_and_options()
    {
        Checkpoint.OpenCell(Cell, "options", resume: false, Prepare);
        File.WriteAllText(Path.Combine(Cell, "tlc_Safety.out"), "earlier");
        Checkpoint.OpenCell(Cell, "options", resume: true, Prepare).Should().BeTrue();
        File.ReadAllText(Path.Combine(Cell, "tlc_Safety.out")).Should().Be("earlier");
    }

    [Fact]
    public void On_resume_a_cell_directory_for_other_options_starts_empty()
    {
        Checkpoint.OpenCell(Cell, "options", resume: false, Prepare);
        File.WriteAllText(Path.Combine(Cell, "tlc_Safety.out"), "earlier");
        Checkpoint.OpenCell(Cell, "other options", resume: true, Prepare).Should().BeFalse();
        File.Exists(Path.Combine(Cell, "tlc_Safety.out")).Should().BeFalse();
    }

    [Fact]
    public void On_resume_a_cell_directory_whose_modules_changed_starts_empty()
    {
        Checkpoint.OpenCell(Cell, "options", resume: false, Prepare);
        File.WriteAllText(Path.Combine(Cell, "tlc_Safety.out"), "earlier");
        Checkpoint.OpenCell(Cell, "options", resume: true, () =>
        {
            Directory.CreateDirectory(Cell);
            File.WriteAllText(Path.Combine(Cell, "TwoStation.tla"), "changed");
        }).Should().BeFalse();
        File.Exists(Path.Combine(Cell, "tlc_Safety.out")).Should().BeFalse();
    }

    [Fact]
    public void The_explorer_result_is_read_back_only_under_the_same_commit()
    {
        Directory.CreateDirectory(Cell);
        var ex = new ExplorerSummary("Deadlock", "A", ["Connected/t03", "Connected/t12"], 1234, 2, false, true, ["Connected/t03"], 5.5);
        Checkpoint.WriteExplorer(Cell, "abc123", ex);
        Checkpoint.ReadExplorer(Cell, "abc123").Should().BeEquivalentTo(ex);
        Checkpoint.ReadExplorer(Cell, "def456").Should().BeNull();
        Checkpoint.WriteExplorer(Cell, "unknown", ex);
        Checkpoint.ReadExplorer(Cell, "unknown").Should().BeNull();
    }

    [Fact]
    public void Only_a_resumed_cell_record_says_what_it_resumed()
    {
        var none = new HashSet<string>(StringComparer.Ordinal);
        var row = new Row("clean, 9 states", "clean, 9 states (1.0 s)", true, "", none, none, null, "clean", "clean", 9, 9, 0.1, 1.0);
        GridRecords.Cross("cell", 1, "Ax25Meaning", row).ContainsKey("resumed").Should().BeFalse();
        var resumed = GridRecords.Cross("cell", 1, "Ax25Meaning", row with { Resumed = "explorer reused; Safety reused; Quiescence recovered" });
        ((string?)resumed["resumed"]).Should().Be("explorer reused; Safety reused; Quiescence recovered");
    }
}
