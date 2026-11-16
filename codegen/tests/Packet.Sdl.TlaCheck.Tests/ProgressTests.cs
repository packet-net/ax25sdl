namespace Packet.Sdl.TlaCheck.Tests;

/// <summary>
/// <c>--progress</c>: reading a running TLC pass's output, which TlaCheck
/// copies to <c>tlc_&lt;Phase&gt;.out</c> as it arrives.
/// </summary>
public class ProgressTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tlacheck-progress-").FullName;

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    // As TLC 1.7.4 prints them: a first line with per-interval counts, a final one without.
    [Fact]
    public void A_progress_line_is_read_with_its_counts()
    {
        var p = Progress.ParseLine("Progress(16) at 2026-11-16 18:36:30: 172,811 states generated (166,070 s/min), 52,035 distinct states found (49,164 ds/min), 6,352 states left on queue.");
        p.Should().Be(new TlcProgressLine(new DateTime(2026, 11, 16, 18, 36, 30), 16, 172_811, 52_035, 6_352));
        Progress.ParseLine("Progress(27) at 2026-11-16 18:36:52: 93,445 states generated, 61,491 distinct states found, 0 states left on queue.")
            .Should().Be(new TlcProgressLine(new DateTime(2026, 11, 16, 18, 36, 52), 27, 93_445, 61_491, 0));
    }

    [Theory]
    [InlineData("Checkpointing completed at (2026-11-16 18:36:30)")]
    [InlineData("214209 states generated, 61491 distinct states found, 0 states left on queue.")]
    [InlineData("")]
    public void Other_lines_are_not_progress(string line) => Progress.ParseLine(line).Should().BeNull();

    private const string Running = """
        TLC2 Version 2.19 of 08 August 2024 (rev: 5a47802)
        Starting... (2026-11-16 18:35:26)
        Computing initial states...
        Progress(9) at 2026-11-16 18:35:30: 6,741 states generated (6,741 s/min), 2,871 distinct states found (2,871 ds/min), 1,609 states left on queue.
        Checkpointing of run meta_Quiescence/26-11-16-18-35-26
        Checkpointing completed at (2026-11-16 18:36:30)
        Progress(16) at 2026-11-16 18:36:30: 172,811 states generated (166,070 s/min), 52,035 distinct states found (49,164 ds/min), 6,352 states left on queue.
        Progress(21) at 2026-11-16 18:38:30: 272,811 states generated (100,000 s/min), 72,035 distinct states found (20,000 ds/min), 352 states left on queue.
        """;

    [Fact]
    public void A_running_pass_reports_its_start_latest_progress_rate_and_checkpoint()
    {
        var o = Progress.Read(Running.Split('\n'));
        o.Started.Should().Be(new DateTime(2026, 11, 16, 18, 35, 26));
        o.Last!.Distinct.Should().Be(72_035);
        o.Last.Queue.Should().Be(352);
        // 20 000 new distinct states over the two minutes between the last two lines.
        o.DistinctPerMinute.Should().Be(10_000);
        o.LastCheckpoint.Should().Be(new DateTime(2026, 11, 16, 18, 36, 30));
        o.Recovered.Should().BeFalse();
    }

    [Fact]
    public void A_pass_with_one_progress_line_has_no_rate_and_a_recovered_one_says_so()
    {
        var o = Progress.Read([
            "Starting... (2026-11-16 18:36:38)",
            "Starting recovery from checkpoint meta_Q/26-11-16-18-35-26/",
            "Recovery completed. 52035 states examined. 6355 states on queue.",
            "Progress(16) at 2026-11-16 18:36:42: 59,770 states generated (59,770 s/min), 54,033 distinct states found (54,033 ds/min), 5,463 states left on queue.",
        ]);
        o.Recovered.Should().BeTrue();
        o.DistinctPerMinute.Should().BeNull();
        o.LastCheckpoint.Should().BeNull();
    }

    [Fact]
    public void A_cell_block_names_finished_passes_and_the_pass_in_progress()
    {
        var cell = Path.Combine(_dir, "g-cell");
        Directory.CreateDirectory(cell);
        File.WriteAllText(Path.Combine(cell, Checkpoint.CellFile), "{}");
        File.WriteAllText(Checkpoint.OutFile(cell, Phase.Safety), "Model checking completed. No error has been found.\n");
        Checkpoint.WriteDone(cell, Phase.Safety, new PassDone("x", 1, null));
        // A partial output with no .done is the pass in progress, never a finished one.
        File.WriteAllText(Checkpoint.OutFile(cell, Phase.Quiescence), Running);
        var block = Progress.Cell(cell, new DateTime(2026, 11, 16, 18, 39, 0));
        block.Should().Contain("finished: Safety clean\n");
        block.Should().Contain("in progress: Quiescence\n");
        block.Should().Contain("running for 3 min");
        block.Should().Contain("72,035 distinct states, 352 on the queue");
        block.Should().Contain("10,000 new distinct states per minute");
        block.Should().Contain("latest checkpoint: 2026-11-16 18:36:30");
        block.All(c => c < 128).Should().BeTrue("the report is plain ASCII");
    }

    [Theory]
    [InlineData(30, "30 s")]
    [InlineData(150, "2 min")]
    [InlineData(3 * 3600 + 120, "3 h 2 min")]
    [InlineData(2 * 86400 + 3600, "2 d 1 h 0 min")]
    public void Durations_read_plainly(int seconds, string text) => Progress.Span(TimeSpan.FromSeconds(seconds)).Should().Be(text);
}
