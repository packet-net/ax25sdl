// xUnit1026: the parametric tests below take fixture metadata (machine,
// state, expected counts) for documentation alongside the graphml name
// even when a specific test only uses a subset. Suppressing locally keeps
// the test data tabular and self-documenting.
#pragma warning disable xUnit1026

using AwesomeAssertions;
using Packet.Sdl.Transcribe;

namespace Packet.Sdl.CodeGen.Tests;

/// <summary>
/// End-to-end tests of the graphml → sdl.yaml pipeline. Drives the walker
/// against the committed corpus of state-page graphmls under
/// spec-sdl/v2.2-errata/data-link/sdl/ and asserts on (a) basic structural
/// invariants and (b) the per-page snapshot under Fixtures/transcribe/.
///
/// The Subroutines page (figc4.7) has a different topology and is not
/// yet handled by the walker; tests for it are pending the subroutines
/// walker variant.
/// </summary>
public class TranscribeTests
{
    public static IEnumerable<object[]> StatePageGraphmls()
    {
        yield return ["DataLink_Disconnected.graphml",         "data_link", "Disconnected",          17, 3];
        yield return ["DataLink_AwaitingConnection.graphml",   "data_link", "AwaitingConnection",    25, 8];
        yield return ["DataLink_AwaitingV22Connection.graphml","data_link", "AwaitingV22Connection", 25, 8];
        yield return ["DataLink_AwaitingRelease.graphml",      "data_link", "AwaitingRelease",       20, 5];
        yield return ["DataLink_Connected.graphml",            "data_link", "Connected",             68, 39];
        // figc4.5 — has two decision diamonds whose edges are both labelled
        // "undefined" (raised against the spec authors at
        // packethacking/ax25spec#10 and #11). Walker yields paths through
        // those edges with branch: Undefined and marks the page coverage: partial.
        yield return ["DataLink_TimerRecovery.graphml",        "data_link", "TimerRecovery",         92, 47];
    }

    [Theory]
    [MemberData(nameof(StatePageGraphmls))]
    public void Walks_every_existing_state_page_graphml_without_throwing(
        string graphmlName, string expectedMachine, string expectedState,
        int expectedTransitions, int expectedDecisions)
    {
        var path = LocateRepoGraphml(graphmlName);
        var graph = GraphmlReader.Load(path);

        var page = Walker.Walk(graph, path);

        page.Machine.Should().Be(expectedMachine);
        page.State.Should().Be(expectedState);
        page.Transitions.Should().HaveCount(expectedTransitions);
        page.Decisions.Should().HaveCount(expectedDecisions);
    }

    [Theory]
    [MemberData(nameof(StatePageGraphmls))]
    public void Every_transition_has_a_terminal_state(
        string graphmlName, string _, string __, int ___, int ____)
    {
        var path = LocateRepoGraphml(graphmlName);
        var graph = GraphmlReader.Load(path);
        var page = Walker.Walk(graph, path);

        foreach (var t in page.Transitions)
        {
            t.Next.Should().NotBeNullOrEmpty($"transition {t.Id} has no `next:` state");
            t.On.Should().NotBeNullOrEmpty($"transition {t.Id} has no `on:` event");
        }
    }

    [Theory]
    [MemberData(nameof(StatePageGraphmls))]
    public void Every_decision_id_referenced_in_paths_is_in_the_decisions_list(
        string graphmlName, string _, string __, int ___, int ____)
    {
        var path = LocateRepoGraphml(graphmlName);
        var graph = GraphmlReader.Load(path);
        var page = Walker.Walk(graph, path);

        var declaredIds = page.Decisions.Select(d => d.Id).ToHashSet();
        foreach (var t in page.Transitions)
            foreach (var step in t.Path.OfType<DecisionBranch>())
                declaredIds.Should().Contain(step.DecisionId,
                    $"transition {t.Id} references decision '{step.DecisionId}' but it isn't declared in `decisions:`");
    }

    [Theory]
    [MemberData(nameof(StatePageGraphmls))]
    public void All_action_kinds_are_canonical_schema_values(
        string graphmlName, string _, string __, int ___, int ____)
    {
        var canonicalKinds = new HashSet<string>
        {
            "signal_upper", "signal_lower", "processing", "subroutine", "internal_out"
        };
        var path = LocateRepoGraphml(graphmlName);
        var graph = GraphmlReader.Load(path);
        var page = Walker.Walk(graph, path);

        foreach (var t in page.Transitions)
            foreach (var step in t.Path.OfType<ActionStep>())
                canonicalKinds.Should().Contain(step.Kind,
                    $"transition {t.Id} has action '{step.Action}' with non-canonical kind '{step.Kind}'");
    }

    [Theory]
    [MemberData(nameof(StatePageGraphmls))]
    public void Yaml_round_trips_through_emitter_matches_committed_snapshot(
        string graphmlName, string _, string __, int ___, int ____)
    {
        var graphmlPath = LocateRepoGraphml(graphmlName);
        var graph = GraphmlReader.Load(graphmlPath);
        var page = Walker.Walk(graph, graphmlPath);
        page.SourceGraphmlPath = graphmlPath;
        var emitted = YamlEmitter.Emit(page);

        var snapshotName = Path.GetFileNameWithoutExtension(graphmlName);
        // "DataLink_Disconnected" → "disconnected"  (drop machine prefix, lowercase)
        var underscore = snapshotName.IndexOf('_');
        var stateOnly = underscore < 0 ? snapshotName : snapshotName[(underscore + 1)..];
        var snakeState = SnakeCase(stateOnly);
        var snapshotPath = LocateFixture(Path.Combine("transcribe", snakeState + ".sdl.yaml"));

        if (!File.Exists(snapshotPath))
        {
            // First-time snapshot bootstrap: write the fixture so the next
            // test-run compares against it. Test fails with a clear message
            // explaining what just happened.
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            File.WriteAllText(snapshotPath, emitted);
            throw new InvalidOperationException(
                $"Bootstrapped new snapshot at {snapshotPath}. Re-run the test; subsequent runs assert byte-identity.");
        }

        var expected = File.ReadAllText(snapshotPath);
        emitted.Should().Be(expected,
            $"tool output for {graphmlName} should match the committed snapshot at Fixtures/transcribe/{snakeState}.sdl.yaml. If the change is intentional, delete the snapshot to regenerate.");
    }

    [Fact]
    public void Subroutines_page_is_detected_by_topology()
    {
        var path = LocateRepoGraphml("DataLink_Subroutines.graphml");
        var graph = GraphmlReader.Load(path);

        SubroutinesWalker.IsSubroutinesPage(graph).Should().BeTrue();
    }

    [Fact]
    public void State_pages_are_not_detected_as_subroutines_pages()
    {
        var path = LocateRepoGraphml("DataLink_Disconnected.graphml");
        var graph = GraphmlReader.Load(path);

        SubroutinesWalker.IsSubroutinesPage(graph).Should().BeFalse();
    }

    [Fact]
    public void Subroutines_page_walks_and_emits_yaml()
    {
        var path = LocateRepoGraphml("DataLink_Subroutines.graphml");
        var graph = GraphmlReader.Load(path);

        var page = SubroutinesWalker.Walk(graph, path);

        page.Machine.Should().Be("data_link");
        // 13 Subroutine_start nodes, all transcribing cleanly since the
        // graphml fix for #11 landed (Establish Data Link + Establish
        // Extended Data Link both now have the SABM-side edge).
        page.Subroutines.Should().HaveCount(13);
        page.Subroutines.Should().OnlyHaveUniqueItems(s => s.Name);
    }

    [Fact]
    public void Subroutines_page_yaml_matches_committed_snapshot()
    {
        var graphmlPath = LocateRepoGraphml("DataLink_Subroutines.graphml");
        var graph = GraphmlReader.Load(graphmlPath);
        var page = SubroutinesWalker.Walk(graph, graphmlPath);
        page.SourceGraphmlPath = graphmlPath;
        var emitted = YamlEmitter.EmitSubroutines(page);

        var snapshotPath = LocateFixture(Path.Combine("transcribe", "subroutines.sdl.yaml"));
        if (!File.Exists(snapshotPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            File.WriteAllText(snapshotPath, emitted);
            throw new InvalidOperationException(
                $"Bootstrapped new snapshot at {snapshotPath}. Re-run the test; subsequent runs assert byte-identity.");
        }
        var expected = File.ReadAllText(snapshotPath);
        emitted.Should().Be(expected,
            "subroutines yaml should match the committed snapshot");
    }

    [Theory]
    [InlineData("N(r) Error Recovery",         "N_r_Error_Recovery")]
    [InlineData("UI Check",                     "UI_Check")]
    [InlineData("Set Version 2.0",              "Set_Version_2_0")]
    [InlineData("Check Need for Response",      "Check_Need_for_Response")]
    [InlineData("Invoke Retransmission",        "Invoke_Retransmission")]
    [InlineData("Establish Extended Data Link", "Establish_Extended_Data_Link")]
    public void Subroutine_names_normalise_per_schema_pattern(string figureLabel, string expectedName)
    {
        // The schema's `name` pattern is ^[A-Z][A-Za-z0-9_]*$. Parens
        // become word separators so V(r), N(r), etc. tokenise into V_r, N_r.
        // Test via the public walker: feed a tiny synthetic graph with one
        // start node carrying the label.
        var graph = SynthesiseSingleStartGraph(figureLabel);

        var page = SubroutinesWalker.Walk(graph, "DataLink_Subroutines.graphml");
        page.Subroutines.Should().NotBeEmpty();
        page.Subroutines[0].Name.Should().Be(expectedName);
    }

    private static GraphmlGraph SynthesiseSingleStartGraph(string startLabel)
    {
        // Subroutine_start (n0) → Return_from_Subroutine (n1)
        var nodes = new List<GraphmlNode>
        {
            new("n0", "Subroutine start",        startLabel,      0, 0),
            new("n1", "Return from Subroutine",  "",              0, 100),
        };
        var edges = new List<GraphmlEdge> { new("e0", "n0", "n1", "") };
        return new GraphmlGraph("synthetic", nodes, edges);
    }

    // ─── EventResolver unit tests ─────────────────────────────────────

    [Theory]
    [InlineData("DL-DISCONNECT Request",   "Signal reception from upper layer", "DL_DISCONNECT_request")]
    [InlineData("DL-CONNECT Request",      "Signal reception from upper layer", "DL_CONNECT_request")]
    [InlineData("DL-UNIT-DATA Request",    "Signal reception from upper layer", "DL_UNIT_DATA_request")]
    [InlineData("SABM",                    "Signal reception from lower layer", "SABM_received")]
    [InlineData("SABME",                   "Signal reception from lower layer", "SABME_received")]
    [InlineData("UI",                      "Signal reception from lower layer", "UI_received")]
    [InlineData("DISC",                    "Signal reception from lower layer", "DISC_received")]
    [InlineData("UA",                      "Signal reception from lower layer", "UA_received")]
    [InlineData("All Other Commands",      "Signal reception from lower layer", "all_other_commands")]
    [InlineData("All Other Primitives",    "Signal reception from lower layer", "all_other_primitives__from_lower_layer")]
    [InlineData("All Other Primitives",    "Signal reception from upper layer", "all_other_primitives__from_upper_layer")]
    [InlineData("Control Field Error",     "Signal reception from lower layer", "control_field_error")]
    [InlineData("Info Not Permitted In Frame", "Signal reception from lower layer", "info_not_permitted_in_frame")]
    [InlineData("U or S Frame Length Error", "Signal reception from lower layer", "u_or_s_frame_length_error")]
    // Annex C machines other than Data Link (packethacking/ax25spec#116).
    [InlineData("LM-SEIZE Confirm",        "Signal reception from lower layer", "LM_SEIZE_confirm")]
    [InlineData("LM-SIEZE Request",        "Signal reception from upper layer", "LM_SIEZE_request")]
    [InlineData("LM-EXPEDITED-DATA Request", "Signal reception from upper layer", "LM_EXPEDITED_DATA_request")]
    [InlineData("PH-SIEZE Confirm",        "Signal reception from lower layer", "PH_SIEZE_confirm")]
    [InlineData("PH-Quiet Indication",     "Signal reception from lower layer", "PH_QUIET_indication")]
    [InlineData("PH_SIEZE Confirm",        "Signal reception from lower layer", "PH_SIEZE_confirm")]
    [InlineData("PH-EXPEDITED_DATA Request", "Signal reception from upper layer", "PH_EXPEDITED_DATA_request")]
    [InlineData("HW-AOS Indication",       "Signal reception from lower layer", "HW_AOS_indication")]
    [InlineData("MDL-NEGOTIATE Request",   "Signal reception from upper layer", "MDL_NEGOTIATE_request")]
    [InlineData("Timer T3 Expiry",         "Internal Signal Reception",         "T3_expiry")]
    [InlineData("T102 Expiry",             "Internal Signal Reception",         "T102_expiry")]
    [InlineData("TM 201 Expiry",           "Signal reception from upper layer", "TM201_expiry")]
    [InlineData("TR210 Expiry",            "Signal reception from lower layer", "TR210_expiry")]
    [InlineData("XID Response",            "Signal reception from lower layer", "XID_response_received")]
    [InlineData("XID Command",             "Signal reception from lower layer", "XID_command_received")]
    [InlineData("FRMR Response",           "Signal reception from lower layer", "FRMR_response_received")]
    [InlineData("All Other DL Primitives", "Signal reception from upper layer", "all_other_dl_primitives__from_upper_layer")]
    [InlineData("All Other DL Primitives", "Signal reception from lower layer", "all_other_dl_primitives__from_lower_layer")]
    [InlineData("Queue Empty",             "Internal Signal Reception",         "queue_empty")]
    [InlineData("PH-DATA\nIndication",     "Signal reception from lower layer", "PH_DATA_indication")]
    public void EventResolver_canonicalises_trigger_labels(string label, string shapeClass, string expected)
    {
        EventResolver.ResolveTriggerEvent(label, shapeClass).Should().Be(expected);
    }

    [Theory]
    [InlineData("T106 Expiry Or T108?", "Internal Signal Reception")]
    [InlineData("LM-\nEXPEDITED-\nDATA Request", "Signal reception from upper layer")]
    public void EventResolver_rejects_a_label_with_no_rule_rather_than_emit_a_bad_id(string label, string shapeClass)
    {
        // Before #116 an unruled label fell through to a lower-cased id that
        // could carry dashes or '?', breaking the transition-id pattern.
        var act = () => EventResolver.ResolveTriggerEvent(label, shapeClass);
        act.Should().Throw<InvalidDataException>().WithMessage("*not a valid event id*EventResolver.cs*");
    }

    // ─── Annex C machine and figure table ─────────────────────────────

    [Theory]
    [InlineData("DataLink_Disconnected.graphml",          "data_link",            "Disconnected", "figc4.1")]
    [InlineData("DataLink_AwaitingV22Connection.graphml", "data_link",            "AwaitingV22Connection", "figc4.6")]
    [InlineData("LinkMux_Idle.graphml",                   "link_mux",             "Idle",         "figc3.1")]
    [InlineData("LinkMux_SiezePending.graphml",           "link_mux",             "SiezePending", "figc3.2")]
    // Ready is a state of four machines; each gets its own figure.
    [InlineData("SimplexPhy_Ready.graphml",               "simplex_phy",          "Ready",        "figc2a.1")]
    [InlineData("ManagementDataLink_Ready.graphml",       "management_data_link", "Ready",        "figc5.1")]
    [InlineData("Segmenter_Ready.graphml",                "segmenter",            "Ready",        "figc6.1")]
    [InlineData("Reassembler_Ready.graphml",              "reassembler",          "Ready",        "figc6.2")]
    [InlineData("DuplexPhy_Receiving.graphml",            "duplex_phy",           "Receiving",    "figc2b.2")]
    [InlineData("SimplexPhy_Receiving.graphml",           "simplex_phy",          "Receiving",    "figc2a.2")]
    public void Walker_takes_machine_and_figure_from_the_Annex_C_table(
        string filename, string expectedMachine, string expectedState, string expectedFigure)
    {
        var page = Walker.Walk(SingleColumnGraph(expectedState, "All Other Primitives"), filename);

        page.Machine.Should().Be(expectedMachine);
        page.State.Should().Be(expectedState);
        page.Source.Figure.Should().Be(expectedFigure);
    }

    [Theory]
    [InlineData("DataLink_Subroutines.graphml",                         "data_link",            "figc4.7")]
    [InlineData("LinkMux_Subroutines.graphml",                          "link_mux",             "figc3.4")]
    [InlineData("SimplexPhy_Subroutines.graphml",                       "simplex_phy",          "figc2a.s")]
    [InlineData("ManagementDataLink_T1NegotiationSubroutines.graphml",  "management_data_link", "figc5.8")]
    public void Subroutines_walker_takes_machine_and_figure_from_the_Annex_C_table(
        string filename, string expectedMachine, string expectedFigure)
    {
        var page = SubroutinesWalker.Walk(SynthesiseSingleStartGraph("Queue Event"), filename);

        page.Machine.Should().Be(expectedMachine);
        page.Source.Figure.Should().Be(expectedFigure);
    }

    [Theory]
    [InlineData("LinkMultiplexer_Idle.graphml", "*prefix 'LinkMultiplexer' is not an Annex C machine*LinkMux*")]
    [InlineData("LinkMux_Seize.graphml",        "*'Seize' is not a page of Link Multiplexer*SiezePending*")]
    [InlineData("DataLink_Subroutines.graphml", "*subroutines page*no Subroutine start node*")]
    [InlineData("Idle.graphml",                 "*<Machine>_<Page>.graphml*")]
    public void Walker_rejects_a_filename_the_Annex_C_table_does_not_know(string filename, string expectedMessage)
    {
        var act = () => Walker.Walk(SingleColumnGraph("Idle", "All Other Primitives"), filename);
        act.Should().Throw<InvalidDataException>().WithMessage(expectedMessage);
    }

    [Fact]
    public void A_PH_trigger_gets_a_schema_shaped_transition_id()
    {
        // The C3.1 trial transcriptions got `t07_ph-sieze_confirm`, which the
        // schema's ^t[0-9]{2}_[a-z0-9_]+$ pattern rejects.
        var page = Walker.Walk(SingleColumnGraph("Idle", "PH-SIEZE Confirm", "Signal reception from lower layer"),
                               "LinkMux_Idle.graphml");

        var t = page.Transitions.Should().ContainSingle().Subject;
        t.Id.Should().Be("t01_ph_sieze_confirm").And.MatchRegex("^t[0-9]{2}_[a-z0-9_]+$");
        t.On.Should().Be("PH_SIEZE_confirm");
        t.OnLabel.Should().Be("PH-SIEZE Confirm");
    }

    [Fact]
    public void Every_Annex_C_machine_id_is_in_the_schema_enum()
    {
        var schemaPath = Path.Combine(FindRepoRoot(Path.GetDirectoryName(typeof(TranscribeTests).Assembly.Location)!),
                                      "spec-sdl", "schema", "sdl-machine.schema.json");
        var schema = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(schemaPath))!;
        var allowed = schema["properties"]!["machine"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

        Packet.Sdl.IR.AnnexC.Machines.Select(m => m.Id).Should().BeSubsetOf(allowed);
    }

    [Fact]
    public void Annex_C_table_is_consistent()
    {
        var table = Packet.Sdl.IR.AnnexC.Pages;
        table.Select(p => p.Figure).Should().OnlyHaveUniqueItems();
        table.Select(p => (p.Machine, p.Key)).Should().OnlyHaveUniqueItems();
        table.Select(p => p.Machine).Distinct()
             .Should().BeEquivalentTo(Packet.Sdl.IR.AnnexC.Machines.Select(m => m.Id));
        table.Where(p => p.IsSubroutines).Should().OnlyContain(p => p.Key.EndsWith("Subroutines"));
        // C2a.1 to C2a.7 + C2a.s, C2b.1 to C2b.5, C3.1 to C3.4, C4.1 to C4.7,
        // C5.1 to C5.8, C6.1 to C6.4.
        table.Should().HaveCount(8 + 5 + 4 + 7 + 8 + 4);
    }

    /// <summary>Entry State → one trigger → back to the same State.</summary>
    private static GraphmlGraph SingleColumnGraph(string state, string trigger,
                                                  string shapeClass = "Signal reception from upper layer")
    {
        var nodes = new List<GraphmlNode>
        {
            new("n0", "State", "0 " + state,  0, 0),
            new("n1", shapeClass, trigger,    0, 1),
            new("n2", "State", "0 " + state,  0, 2),
        };
        var edges = new List<GraphmlEdge> { new("e0", "n0", "n1", ""), new("e1", "n1", "n2", "") };
        return new GraphmlGraph("synthetic", nodes, edges);
    }

    [Theory]
    [InlineData("Processing description",          "processing")]
    [InlineData("Signal generation to upper layer", "signal_upper")]
    [InlineData("Signal generation to lower layer", "signal_lower")]
    [InlineData("Subroutine call",                  "subroutine")]
    [InlineData("Internal Signal Generation",       "internal_out")]
    [InlineData("State",                            null)]
    public void EventResolver_maps_shape_class_to_action_kind(string shapeClass, string? expectedKind)
    {
        EventResolver.ShapeClassToActionKind(shapeClass).Should().Be(expectedKind);
    }

    // ─── Walker strictness (#29) ──────────────────────────────────────

    [Theory]
    [InlineData("Yes", "")]
    [InlineData("",    "No")]
    [InlineData("",    "")]
    public void Walker_throws_when_a_two_out_edge_decision_is_missing_a_branch_label(
        string label0, string label1)
    {
        // Minimal graph: entry State → trigger → decision diamond with two
        // out-edges → two terminal states. The decision out-edges carry the
        // labels supplied by the test parameters.
        var nodes = new List<GraphmlNode>
        {
            new("n0", "State",            "TestState",        0, 0),
            new("n1", "Signal reception from upper layer", "DL-CONNECT Request", 1, 0),
            new("n2", "Test or decision", "Question?",        2, 0),
            new("n3", "State",            "TestState",        3, -1),
            new("n4", "State",            "OtherState",       3,  1),
        };
        var edges = new List<GraphmlEdge>
        {
            new("e0", "n0", "n1", ""),
            new("e1", "n1", "n2", ""),
            new("e2", "n2", "n3", label0),
            new("e3", "n2", "n4", label1),
        };
        var graph = new GraphmlGraph("DataLink_Connected.graphml", nodes, edges);

        var act = () => Walker.Walk(graph, "DataLink_Connected.graphml");

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*no branch label*every out-edge of a Test-or-decision must be labelled*");
    }

    [Fact]
    public void Walker_accepts_a_two_out_edge_decision_when_both_branches_are_labelled()
    {
        var nodes = new List<GraphmlNode>
        {
            new("n0", "State",            "TestState",        0, 0),
            new("n1", "Signal reception from upper layer", "DL-CONNECT Request", 1, 0),
            new("n2", "Test or decision", "Question?",        2, 0),
            new("n3", "State",            "TestState",        3, -1),
            new("n4", "State",            "OtherState",       3,  1),
        };
        var edges = new List<GraphmlEdge>
        {
            new("e0", "n0", "n1", ""),
            new("e1", "n1", "n2", ""),
            new("e2", "n2", "n3", "Yes"),
            new("e3", "n2", "n4", "No"),
        };
        var graph = new GraphmlGraph("DataLink_Connected.graphml", nodes, edges);

        var act = () => Walker.Walk(graph, "DataLink_Connected.graphml");

        act.Should().NotThrow();
    }

    // ─── Loop recovery (m0lte/ax25sdl#44 / #48 / #49) ─────────────────
    //
    // These assert, against the real spec graphmls, that each back-edge is
    // recovered as a faithful loop_while — not flattened to one iteration
    // (the pre-fix bug) and not silently dropped.

    [Fact]
    public void Invoke_Retransmission_recovered_as_tail_test_do_while_loop()
    {
        var graph = GraphmlReader.Load(LocateRepoGraphml("DataLink_Subroutines.graphml"));
        var page = SubroutinesWalker.Walk(graph, "DataLink_Subroutines.graphml");

        var sub = page.Subroutines.Single(s => s.Name == "Invoke_Retransmission");
        var steps = sub.Paths.Single().Steps;
        var loop = steps.OfType<LoopWhileStep>().Single();

        loop.DecisionId.Should().Be("invoke_retransmission_vs_eq_x");
        loop.Branch.Should().Be("No", "the figure's V(s)==X? Yes edge exits; the No edge re-enters the body");
        loop.TestAtEnd.Should().BeTrue("V(s)==X? is drawn after the body — a do-while");
        loop.Body.OfType<ActionStep>().Select(a => a.Action).Should()
            .Equal("Push Old I Frame onto Queue", "V(s) := V(s) + 1");

        // Set-up runs once, before the loop step (not part of the body).
        steps.OfType<ActionStep>().Select(a => a.Action).Should()
            .Equal("Backtrack", "X := V(s)", "V(s) := N(r)");
    }

    [Fact]
    public void Connected_drain_recovered_as_head_test_while_loop()
    {
        var graph = GraphmlReader.Load(LocateRepoGraphml("DataLink_Connected.graphml"));
        var page = Walker.Walk(graph, "DataLink_Connected.graphml");

        var loop = page.Transitions.SelectMany(t => t.Path).OfType<LoopWhileStep>().First();
        loop.DecisionId.Should().Be("i_vr_i_frame_stored");
        loop.Branch.Should().Be("Yes", "the Yes edge re-enters the body — loop while a frame is stored");
        loop.TestAtEnd.Should().BeFalse("V(r) I Frame Stored? is drawn before the body — a while");
        loop.Body.OfType<ActionStep>().Select(a => a.Action).Should()
            .Equal("Retrieve Stored V(r) I Frame", "DL-DATA Indication", "V(r) := V(r) + 1");
    }

    [Fact]
    public void TimerRecovery_drain_recovered_as_head_test_while_loop()
    {
        var graph = GraphmlReader.Load(LocateRepoGraphml("DataLink_TimerRecovery.graphml"));
        var page = Walker.Walk(graph, "DataLink_TimerRecovery.graphml");

        var loop = page.Transitions.SelectMany(t => t.Path).OfType<LoopWhileStep>().First();
        loop.DecisionId.Should().Be("i_vr_i_frame_stored");
        loop.Branch.Should().Be("Yes");
        loop.TestAtEnd.Should().BeFalse();
        // figc4.5 draws V(r) := V(r) - 1 here (verification_pending — see #49).
        loop.Body.OfType<ActionStep>().Select(a => a.Action).Should()
            .Equal("Retrieve Stored V(r) I Frame", "DL-DATA Indication", "V(r) := V(r) - 1");
    }

    [Fact]
    public void Unguarded_back_edge_is_rejected_rather_than_silently_dropped()
    {
        // A cycle with no controlling Test-or-decision at either end. The
        // walker must throw rather than truncate it (the pre-fix behaviour
        // silently skipped any back-edge to an already-visited node).
        var nodes = new List<GraphmlNode>
        {
            new("n0", "State",                  "TestState", 0, 0),
            new("n1", "Processing description", "step A",    1, 0),
            new("n2", "Processing description", "step B",    2, 0),
        };
        var edges = new List<GraphmlEdge>
        {
            new("e0", "n0", "n1", ""),
            new("e1", "n1", "n2", ""),
            new("e2", "n2", "n1", ""),   // unguarded back-edge
        };
        var graph = new GraphmlGraph("DataLink_Connected.graphml", nodes, edges);

        var act = () => Walker.Walk(graph, "DataLink_Connected.graphml");

        act.Should().Throw<System.IO.InvalidDataException>().WithMessage("*guarded*");
    }

    // ─── Locator helpers ──────────────────────────────────────────────

    private static string LocateRepoGraphml(string filename)
    {
        var assemblyDir = Path.GetDirectoryName(typeof(TranscribeTests).Assembly.Location)!;
        var repoRoot = FindRepoRoot(assemblyDir);
        return Path.Combine(repoRoot, "spec-sdl", "v2.2-errata", "data-link", "sdl", filename);
    }

    private static string LocateFixture(string relativePath)
    {
        var assemblyDir = Path.GetDirectoryName(typeof(TranscribeTests).Assembly.Location)!;
        // Fixtures get copied to the output directory at build time (see
        // <None Include="Fixtures/**"> in the csproj). They sit alongside
        // the test assembly in bin/Debug/...
        return Path.Combine(assemblyDir, "Fixtures", relativePath);
    }

    private static string FindRepoRoot(string start)
    {
        // The slnx file is in codegen/, so we look for either the .slnx
        // OR the spec-sdl/ directory at the repo root.
        var d = new DirectoryInfo(start);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "spec-sdl")))
            d = d.Parent;
        if (d is null)
            throw new InvalidOperationException(
                $"can't locate repo root walking up from {start} — no spec-sdl/ directory in any ancestor.");
        return d.FullName;
    }

    private static string SnakeCase(string pascal)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i])) sb.Append('_');
            sb.Append(char.ToLowerInvariant(pascal[i]));
        }
        return sb.ToString();
    }
}
