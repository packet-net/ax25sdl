namespace Packet.Sdl.TlaCheck.Tests;

/// <summary>
/// The Coverage phase reads TLC's state dump through a named pipe so a large
/// cell's dump (tens of gigabytes) never touches the disk, and
/// <c>--coverage-only</c> reruns that phase alone for cells whose coverage
/// was cut (the 15 November grid run).
/// </summary>
public class CoverageDumpTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tlacheck-dump-").FullName;

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    // As TLC prints a dump: an arms set on one line, one running over several, an empty one.
    private const string Dump = """
        State 1:
        /\ last = [move |-> "seed", arms |-> {}]
        /\ x = 0

        State 2:
        /\ last = [ move |-> "A sends",
          arms |-> {"Connected/t03_i_frame_pops_off_queue_no_no_no", "Check_Need_for_Response:t01_check_need_for_response_yes"} ]
        /\ x = 1

        State 3:
        /\ last = [ move |-> "B receives",
          arms |->
              { "Connected/t21_rr_received_yes",
                "Connected/t12_t1_expiry" },
          at |-> "B" ]
        /\ x = 2

        """;

    private static readonly string[] DumpArms =
    [
        "Check_Need_for_Response:t01_check_need_for_response_yes",
        "Connected/t03_i_frame_pops_off_queue_no_no_no",
        "Connected/t12_t1_expiry",
        "Connected/t21_rr_received_yes",
    ];

    [Fact]
    public void Arms_are_read_from_a_dump_text()
    {
        var arms = new HashSet<string>(StringComparer.Ordinal);
        Tlc.ReadArms(new StringReader(Dump), arms);
        arms.Should().BeEquivalentTo(DumpArms);
    }

    [Fact]
    public void The_file_fallback_reads_the_same_arms_and_deletes_the_dump()
    {
        var dump = Path.Combine(_dir, "states.dump");
        File.WriteAllText(dump, Dump);
        Tlc.ReadArms(_dir).Should().BeEquivalentTo(DumpArms);
        File.Exists(dump).Should().BeFalse();
    }

    [Fact]
    public async Task A_pipe_written_by_another_process_gives_the_same_arms_and_is_removed()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "states.dump");
        DumpPipe.TryCreate(path).Should().BeTrue();
        var reader = DumpPipe.Drain(path);
        // Far more than a pipe's buffer, so the writer only finishes if the reader keeps draining.
        var writer = Task.Run(() =>
        {
            using var w = new StreamWriter(new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
            for (var i = 0; i < 2000; i++) w.Write(Dump);
        });
        // Times out unless the reader drains the pipe as it is written.
        await writer.WaitAsync(TimeSpan.FromSeconds(30));
        DumpPipe.Finish(path, reader).Should().BeEquivalentTo(DumpArms);
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task A_pipe_no_writer_ever_opens_does_not_hang()
    {
        if (OperatingSystem.IsWindows()) return;
        // TLC failed before opening its dump (no java, a bad jar, a crash at start).
        var path = Path.Combine(_dir, "states.dump");
        DumpPipe.TryCreate(path).Should().BeTrue();
        var reader = DumpPipe.Drain(path);
        var arms = await Task.Run(() => DumpPipe.Finish(path, reader)).WaitAsync(TimeSpan.FromSeconds(30));
        arms.Should().BeEmpty();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task A_pipe_the_writer_leaves_part_written_is_still_drained()
    {
        if (OperatingSystem.IsWindows()) return;
        // TLC killed at the timeout: its end of the pipe closes mid-state.
        var path = Path.Combine(_dir, "states.dump");
        DumpPipe.TryCreate(path).Should().BeTrue();
        var reader = DumpPipe.Drain(path);
        using (var w = new StreamWriter(new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)))
            w.Write(Dump[..Dump.IndexOf("State 3", StringComparison.Ordinal)] + "State 3:\n/\\ last = [ arms |-> { \"Connected/t21");
        var arms = await Task.Run(() => DumpPipe.Finish(path, reader)).WaitAsync(TimeSpan.FromSeconds(30));
        arms.Should().BeEquivalentTo(DumpArms.Take(2));
    }

    [Fact]
    public void Coverage_only_needs_a_grid_and_not_lockstep()
    {
        CoverageOnly.Refusal(coverageOnly: false, gridMode: false, lockstep: true).Should().BeNull();
        CoverageOnly.Refusal(true, gridMode: true, lockstep: false).Should().BeNull();
        CoverageOnly.Refusal(true, gridMode: false, lockstep: false).Should().Contain("needs a grid");
        CoverageOnly.Refusal(true, gridMode: true, lockstep: true).Should().Contain("not with --lockstep");
    }

    [Fact]
    public void A_coverage_only_record_is_marked_and_reads_back()
    {
        var row = new CoverageRow("clean", 371, DumpArms.ToHashSet(StringComparer.Ordinal), 1.94, "");
        var o = GridRecords.Coverage("g-ab1-ba1-b1-k2-nosrej-drop+dup", 194, "Ax25Meaning", row);
        ((string?)o["mode"]).Should().Be("coverage");
        ((bool?)o["coverageOnly"]).Should().BeTrue();
        ((bool?)o["done"]).Should().BeTrue();
        ((double?)o["coverageSeconds"]).Should().Be(1.9);
        o["arms"]!.AsArray().Select(n => (string)n!).Should().Equal(DumpArms);
        var back = GridRecords.CoverageRow(o);
        back.Kind.Should().Be("clean");
        back.States.Should().Be(371);
        back.Arms.Should().BeEquivalentTo(DumpArms);

        var failed = GridRecords.Coverage("c", 0, "Ax25Meaning", new CoverageRow("error", 0, new HashSet<string>(), 0, "boom"));
        ((bool?)failed["done"]).Should().BeFalse();
        var timedOut = GridRecords.Coverage("c", 0, "Ax25Meaning", new CoverageRow("timeout", 0, new HashSet<string>(), 600, "no verdict"));
        ((bool?)timedOut["done"]).Should().BeTrue();
        ((bool?)timedOut["timedOut"]).Should().BeTrue();
    }
}
