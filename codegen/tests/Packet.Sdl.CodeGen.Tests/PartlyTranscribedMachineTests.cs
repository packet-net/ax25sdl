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
        result.Stdout.Should().Contain("withheld  link_mux").And.Contain("SiezePending");
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
