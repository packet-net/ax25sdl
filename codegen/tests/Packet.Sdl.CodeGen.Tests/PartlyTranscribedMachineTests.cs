namespace Packet.Sdl.CodeGen.Tests;

/// <summary>
/// Black-box tests of how codegen treats a machine that is only partly
/// transcribed: a page whose transitions lead to an Annex C state that has
/// no page yet. The machine's pages are checked but nothing is generated
/// from them, and the rest of the build is unaffected. A target that is not
/// an Annex C state at all is still an error.
/// </summary>
public class PartlyTranscribedMachineTests
{
    private const string Events = """
        primitives_upper:
          - DL_DISCONNECT_request
        catchalls:
          - all_other_primitives__from_upper_layer
        link_multiplexer:
          - LM_DATA_request
        primitives_physical:
          - PH_BUSY_indication
        """;

    private const string DataLinkPage = """
        machine: data_link
        state: Connected
        source: { spec: test, figure: figc4.4 }
        transitions:
          - id: t01_dl_disconnect_request
            on: DL_DISCONNECT_request
            path:
              - { action: send_disc, kind: signal_lower }
            next: Connected
          - id: t02_all_other_primitives__from_upper_layer
            on: all_other_primitives__from_upper_layer
            path: []
            next: Connected
        """;

    // Figure C3.1 in miniature: one column leads to Sieze Pending (C3.2),
    // which has no page yet.
    private const string LinkMuxIdlePage = """
        machine: link_mux
        state: Idle
        source: { spec: test, figure: figc3.1 }
        transitions:
          - id: t01_lm_data_request
            on: LM_DATA_request
            path:
              - { action: PH-SIEZE Request, kind: signal_lower }
            next: SiezePending
          - id: t02_all_other_primitives__from_upper_layer
            on: all_other_primitives__from_upper_layer
            path:
              - { action: Queue Event, kind: subroutine }
            next: Idle
          - id: t03_ph_busy_indication
            on: PH_BUSY_indication
            path:
              - { action: Suspend Link Layer Timing, kind: processing }
            next: Idle
        """;

    [Fact]
    public void A_partly_transcribed_machine_is_checked_but_not_generated()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("data-link/connected.sdl.yaml", DataLinkPage);
        r.WritePage("link-multiplexer/idle.sdl.yaml", LinkMuxIdlePage);

        var result = r.Run();

        result.ExitCode.Should().Be(0, $"stderr: {result.Stderr}\nstdout: {result.Stdout}");
        result.Stdout.Should().Contain("withheld  link_mux: no page yet for SiezePending");
        r.GeneratedExists("DataLink_Connected.g.cs").Should().BeTrue();
        r.GeneratedExists("LinkMux_Idle.g.cs").Should().BeFalse();

        // Events only the withheld machine uses stay out of the closed event
        // set; events the generated machines use stay in.
        var events = r.ReadGenerated("Ax25Event.g.cs");
        events.Should().Contain("DL_DISCONNECT_request");
        events.Should().NotContain("LM_DATA_request");
        events.Should().NotContain("PH_BUSY_indication");

        // Its action verbs stay out of the closed verb set too.
        var verbs = r.ReadGenerated("Ax25ActionVerb.g.cs");
        verbs.Should().Contain("SendDisc");
        verbs.Should().NotContain("Suspend");
    }

    [Fact]
    public void A_partly_transcribed_machine_is_still_checked()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("link-multiplexer/idle.sdl.yaml", LinkMuxIdlePage.Replace(
            "on: PH_BUSY_indication", "on: PH_NOT_AN_EVENT_indication"));

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("PH_NOT_AN_EVENT_indication").And.Contain("events.yaml");
    }

    [Fact]
    public void A_target_that_is_not_an_Annex_C_state_is_an_error()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("link-multiplexer/idle.sdl.yaml", LinkMuxIdlePage.Replace(
            "next: SiezePending", "next: SiezePendnig"));

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("SiezePendnig").And.Contain("not a known state in machine `link_mux`");
    }

    [Fact]
    public void A_published_machine_is_never_withheld()
    {
        // A Data Link page leads to a state whose yaml is missing (a deleted
        // file, or a bad submodule checkout). Withholding would quietly
        // delete Data Link's generated code; it must fail instead.
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        var page = DataLinkPage.Replace(
            "kind: signal_lower }\n    next: Connected",
            "kind: signal_lower }\n    next: AwaitingRelease");
        page.Should().Contain("next: AwaitingRelease");
        r.WritePage("data-link/connected.sdl.yaml", page);

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("`data_link` is published").And.Contain("AwaitingRelease");
        r.GeneratedExists("DataLink_Connected.g.cs").Should().BeFalse();
    }

    [Fact]
    public void Machines_sharing_a_state_name_get_separate_generated_files()
    {
        // Ready is a state of four machines. Published machines keep their
        // bare file names; every other machine's are machine-qualified.
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("management-data-link/ready.sdl.yaml", """
            machine: management_data_link
            state: Ready
            coverage: partial
            source: { spec: test, figure: figc5.1 }
            transitions:
              - id: t01_dl_disconnect_request
                on: DL_DISCONNECT_request
                path: []
                next: Ready
            """);
        r.WritePage("segmenter/ready.sdl.yaml", """
            machine: segmenter
            state: Ready
            source: { spec: test, figure: figc6.1 }
            transitions:
              - id: t01_all_other_primitives__from_upper_layer
                on: all_other_primitives__from_upper_layer
                path: []
                next: Ready
            """);

        var result = r.RunJson();

        result.ExitCode.Should().Be(0, $"stderr: {result.Stderr}\nstdout: {result.Stdout}");
        File.Exists(Path.Combine(r.JsonOutDir, "ready.g.json")).Should().BeTrue();
        File.Exists(Path.Combine(r.JsonOutDir, "segmenter_ready.g.json")).Should().BeTrue();
        File.ReadAllText(Path.Combine(r.JsonOutDir, "ready.g.json")).Should().Contain("management_data_link");
    }

    [Fact]
    public void Two_pages_with_one_output_name_is_an_error()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("data-link/connected.sdl.yaml", DataLinkPage);
        r.WritePage("other/data-link/connected.sdl.yaml", DataLinkPage);

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("would all generate files named `connected.g.*`");
    }

    // ─── Known figure defects (spec-sdl/known-figure-defects.yaml) ─────

    private const string SizedDefect = """
        defects:
          - machine: link_mux
            state: Idle
            transition: t01_lm_data_request
            next: Sized
            issue: https://github.com/packethacking/ax25spec/issues/121
        """;

    private static string IdleLeadingToSized() =>
        LinkMuxIdlePage.Replace("next: SiezePending", "next: Sized");

    [Fact]
    public void A_listed_figure_defect_warns_and_keeps_the_machine_withheld()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("data-link/connected.sdl.yaml", DataLinkPage);
        r.WritePage("link-multiplexer/idle.sdl.yaml", IdleLeadingToSized());
        File.WriteAllText(Path.Combine(r.SpecDir, "known-figure-defects.yaml"), SizedDefect);

        var result = r.Run();

        result.ExitCode.Should().Be(0, $"stderr: {result.Stderr}\nstdout: {result.Stdout}");
        result.Stderr.Should().Contain("::warning::").And.Contain("known figure defect").And.Contain("issues/121");
        result.Stdout.Should().Contain("withheld  link_mux: Sized (known figure defect");
        r.GeneratedExists("LinkMux_Idle.g.cs").Should().BeFalse();
        r.GeneratedExists("DataLink_Connected.g.cs").Should().BeTrue();
    }

    [Fact]
    public void An_unlisted_figure_defect_is_an_error()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("link-multiplexer/idle.sdl.yaml", IdleLeadingToSized());

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("targets state `Sized`").And.Contain("known-figure-defects.yaml");
    }

    [Fact]
    public void A_listed_defect_that_no_longer_occurs_is_an_error()
    {
        // The figure was fixed: the page now leads to SiezePending, but the
        // entry is still there. Like a strict xfail, that fails the run.
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("link-multiplexer/idle.sdl.yaml", LinkMuxIdlePage);
        File.WriteAllText(Path.Combine(r.SpecDir, "known-figure-defects.yaml"), SizedDefect);

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("`t01_lm_data_request` leads to `Sized`").And.Contain("no longer occurs");
    }

    [Theory]
    [InlineData("issue: https://github.com/packethacking/ax25spec/issues/121", "issue: 121", "needs `issue:`")]
    [InlineData("issue: https://github.com/packethacking/ax25spec/issues/121", "issue: https://github.com/packet-net/ax25sdl/issues/121", "packethacking/ax25spec issue")]
    [InlineData("issue: https://github.com/packethacking/ax25spec/issues/121", "issue: https://github.com/packethacking/ax25spec/pull/121", "packethacking/ax25spec issue")]
    [InlineData("next: Sized", "nxet: Sized", "known-figure-defects.yaml")]
    public void A_malformed_defect_entry_is_an_error(string from, string to, string expected)
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("link-multiplexer/idle.sdl.yaml", IdleLeadingToSized());
        File.WriteAllText(Path.Combine(r.SpecDir, "known-figure-defects.yaml"), SizedDefect.Replace(from, to));

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain(expected);
    }

    [Fact]
    public void A_figure_defect_on_a_published_machine_is_still_an_error()
    {
        // Listing it would withhold Data Link, which is never allowed.
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("data-link/connected.sdl.yaml", DataLinkPage.Replace(
            "kind: signal_lower }\n    next: Connected", "kind: signal_lower }\n    next: Conected"));
        File.WriteAllText(Path.Combine(r.SpecDir, "known-figure-defects.yaml"), """
            defects:
              - machine: data_link
                state: Connected
                transition: t01_dl_disconnect_request
                next: Conected
                issue: https://github.com/packethacking/ax25spec/issues/1
            """);

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("`data_link` is published and is never withheld, so a defect on it can't be allowlisted");
        // Not reported as withheld, and not as missing pages.
        result.Stderr.Should().NotContain("::warning::");
        result.Stderr.Should().NotContain("submodule checkout");
    }

    [Fact]
    public void A_withheld_machine_still_gets_the_totality_lint()
    {
        // Only generation is withheld: the totality / determinism lint
        // checks the withheld page's three (state, event) arms.
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("link-multiplexer/idle.sdl.yaml", LinkMuxIdlePage);

        var result = r.Run();

        result.ExitCode.Should().Be(0, $"stderr: {result.Stderr}");
        result.Stdout.Should().Contain("withheld  link_mux");
        result.Stdout.Should().Contain("totality/determinism: 3 (state, event) arm(s)");
    }

    // ─── missing_catchall: a published machine whose figure draws none ──

    private const string MdlReadyWithoutCatchall = """
        machine: management_data_link
        state: Ready
        source: { spec: test, figure: figc5.1 }
        transitions:
          - id: t01_dl_disconnect_request
            on: DL_DISCONNECT_request
            path: []
            next: Ready
        """;

    private const string MissingCatchallEntry = """
        defects:
          - kind: missing_catchall
            machine: management_data_link
            state: Ready
            issue: https://github.com/packethacking/ax25spec/issues/130
        """;

    [Fact]
    public void A_listed_missing_catchall_lets_a_published_machine_generate()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("management-data-link/ready.sdl.yaml", MdlReadyWithoutCatchall);
        File.WriteAllText(Path.Combine(r.SpecDir, "known-figure-defects.yaml"), MissingCatchallEntry);

        var result = r.Run();

        result.ExitCode.Should().Be(0, $"stderr: {result.Stderr}\nstdout: {result.Stdout}");
        result.Stderr.Should().Contain("::warning::").And.Contain("has no catch-all input").And.Contain("partial coverage");
        result.Stdout.Should().NotContain("withheld");
        r.GeneratedExists("ManagementDataLink_Ready.g.cs").Should().BeTrue();
    }

    [Fact]
    public void An_unlisted_missing_catchall_is_still_an_error()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("management-data-link/ready.sdl.yaml", MdlReadyWithoutCatchall);

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("has no transition triggered by a catch-all event").And.Contain("missing_catchall");
    }

    [Fact]
    public void A_missing_catchall_entry_for_a_page_that_has_one_is_stale()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("management-data-link/ready.sdl.yaml", MdlReadyWithoutCatchall + "\n" + """
              - id: t02_all_other_primitives__from_upper_layer
                on: all_other_primitives__from_upper_layer
                path: []
                next: Ready
            """);
        File.WriteAllText(Path.Combine(r.SpecDir, "known-figure-defects.yaml"), MissingCatchallEntry);

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("has no catch-all input").And.Contain("no longer occurs");
    }

    [Theory]
    [InlineData("kind: missing_catchall", "kind: something_else", "unknown kind")]
    [InlineData("    state: Ready\n", "    state: Ready\n    transition: t01_dl_disconnect_request\n", "names a state, not a transition")]
    public void A_malformed_missing_catchall_entry_is_an_error(string from, string to, string expected)
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("management-data-link/ready.sdl.yaml", MdlReadyWithoutCatchall);
        var entry = MissingCatchallEntry.Replace(from, to);
        entry.Should().NotBe(MissingCatchallEntry);
        File.WriteAllText(Path.Combine(r.SpecDir, "known-figure-defects.yaml"), entry);

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain(expected);
    }

    // ─── Subroutine calls must name a subroutine of the machine ────────

    private const string LinkMuxSubroutines = """
        machine: link_mux
        source: { spec: test, figure: figc3.4 }
        subroutines:
          - name: Queue_Event
            paths:
              - id: t01_queue_event
                path:
                  - { action: Push On Current Queue, kind: internal_out }
        """;

    private static string IdleCalling(string call) =>
        LinkMuxIdlePage.Replace("{ action: Queue Event, kind: subroutine }", $"{{ action: {call}, kind: subroutine }}");

    [Theory]
    [InlineData("Queue Event")]
    [InlineData("QUEUE EVENT")]
    [InlineData("Queue Event (Now)")]   // a bracketed argument is not part of the name
    public void A_call_that_names_a_subroutine_of_the_machine_passes(string call)
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("link-multiplexer/idle.sdl.yaml", IdleCalling(call));
        r.WritePage("link-multiplexer/subroutines.sdl.yaml", LinkMuxSubroutines);

        var result = r.Run();

        result.Stderr.Should().NotContain("names no subroutine");
    }

    [Fact]
    public void A_call_that_names_no_subroutine_is_an_error_unless_listed()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("link-multiplexer/idle.sdl.yaml", IdleCalling("Queue Evnet"));
        r.WritePage("link-multiplexer/subroutines.sdl.yaml", LinkMuxSubroutines);

        var unlisted = r.Run();
        unlisted.ExitCode.Should().NotBe(0);
        unlisted.Stderr.Should().Contain("subroutine call \"Queue Evnet\" names no subroutine of `link_mux`");

        File.WriteAllText(Path.Combine(r.SpecDir, "known-figure-defects.yaml"), """
            defects:
              - kind: unknown_subroutine
                machine: link_mux
                call: Queue Evnet
                issue: https://github.com/packethacking/ax25spec/issues/144
            """);
        var listed = r.Run();
        listed.Stderr.Should().NotContain("names no subroutine");
        listed.Stderr.Should().Contain("::warning::").And.Contain("calls subroutine \"Queue Evnet\", which it does not define");
    }

    [Fact]
    public void An_unknown_subroutine_entry_for_a_call_that_resolves_is_stale()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("link-multiplexer/idle.sdl.yaml", LinkMuxIdlePage);
        r.WritePage("link-multiplexer/subroutines.sdl.yaml", LinkMuxSubroutines);
        File.WriteAllText(Path.Combine(r.SpecDir, "known-figure-defects.yaml"), """
            defects:
              - kind: unknown_subroutine
                machine: link_mux
                call: Queue Event
                issue: https://github.com/packethacking/ax25spec/issues/144
            """);

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("calls subroutine \"Queue Event\"").And.Contain("no longer occurs");
    }

    [Fact]
    public void An_unknown_subroutine_entry_must_name_a_call_and_nothing_else()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("link-multiplexer/idle.sdl.yaml", LinkMuxIdlePage);
        File.WriteAllText(Path.Combine(r.SpecDir, "known-figure-defects.yaml"), """
            defects:
              - kind: unknown_subroutine
                machine: link_mux
                state: Idle
                call: Queue Event
                issue: https://github.com/packethacking/ax25spec/issues/144
            """);

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("needs machine and call");
    }

    // ─── Catch-all family and cross-machine targets (figc6.x) ──────────

    [Fact]
    public void All_other_DL_primitives_counts_as_a_catchall()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events + "\n  - all_other_dl_primitives__from_lower_layer\n");
        r.WritePage("reassembler/ready.sdl.yaml", """
            machine: reassembler
            state: Ready
            source: { spec: test, figure: figc6.2 }
            transitions:
              - id: t01_all_other_dl_primitives__from_lower_layer
                on: all_other_dl_primitives__from_lower_layer
                path: []
                next: Ready
            """);

        var result = r.Run();

        result.ExitCode.Should().Be(0, $"stderr: {result.Stderr}\nstdout: {result.Stdout}");
    }

    [Fact]
    public void A_target_that_is_another_machines_state_says_so()
    {
        // figc6.1 (Segmenter Ready) leads to Reassembling Data, a Reassembler state.
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("segmenter/ready.sdl.yaml", """
            machine: segmenter
            state: Ready
            source: { spec: test, figure: figc6.1 }
            transitions:
              - id: t01_dl_disconnect_request
                on: DL_DISCONNECT_request
                path: []
                next: ReassemblingData
              - id: t02_all_other_primitives__from_upper_layer
                on: all_other_primitives__from_upper_layer
                path: []
                next: Ready
            """);

        var result = r.Run();
        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("`ReassemblingData` is a state of `reassembler`, a different machine");

        // Listed as a known figure defect, the Segmenter is withheld instead.
        File.WriteAllText(Path.Combine(r.SpecDir, "known-figure-defects.yaml"), """
            defects:
              - machine: segmenter
                state: Ready
                transition: t01_dl_disconnect_request
                next: ReassemblingData
                issue: https://github.com/packethacking/ax25spec/issues/140
            """);
        var listed = r.Run();
        listed.ExitCode.Should().Be(0, $"stderr: {listed.Stderr}");
        listed.Stdout.Should().Contain("withheld  segmenter: ReassemblingData (known figure defect");
    }

    [Fact]
    public void A_machine_that_is_not_in_Annex_C_is_an_error()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("link-multiplexer/idle.sdl.yaml", LinkMuxIdlePage.Replace(
            "machine: link_mux", "machine: link_multiplexer"));

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("`link_multiplexer` is not an Annex C machine");
    }
}
