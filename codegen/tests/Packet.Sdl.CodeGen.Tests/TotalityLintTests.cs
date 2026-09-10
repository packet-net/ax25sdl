namespace Packet.Sdl.CodeGen.Tests;

/// <summary>
/// Black-box tests of the totality / determinism lint (docs/lint-totality.md)
/// through the codegen CLI. Each test drops a small fixture page into a
/// sandbox, runs the codegen, and asserts on the exit code and the
/// <c>::error::</c> / <c>::warning::</c> lines.
/// </summary>
public class TotalityLintTests
{
    private const string Events = """
        primitives_upper:
          - DL_DISCONNECT_request
        frames_received:
          - I_received
          - RR_received
        catchalls: []
        internal: []
        timers: []
        """;

    private const string Header = """
        machine: data_link
        state: Connected
        coverage: partial
        source: { spec: test, figure: f }
        """;

    // Two independent flags, three of the four combinations drawn: the
    // (false, false) corner is a hole that the syntactic lints cannot see
    // (every decision has both branches somewhere, every pair of guards
    // contradicts on some literal).
    private const string HolePage = Header + """

        decisions:
          - id: d_ack
            question: "Ack pending?"
            predicate: ack_pending
          - id: d_busy
            question: "Own receiver busy?"
            predicate: own_receiver_busy
        transitions:
          - id: t01_ack_busy
            on: I_received
            path:
              - { decision: d_ack, branch: "Yes" }
              - { decision: d_busy, branch: "Yes" }
              - { action: alpha, kind: processing }
            next: Connected
          - id: t02_ack_not_busy
            on: I_received
            path:
              - { decision: d_ack, branch: "Yes" }
              - { decision: d_busy, branch: "No" }
              - { action: beta, kind: processing }
            next: Connected
          - id: t03_no_ack_busy
            on: I_received
            path:
              - { decision: d_ack, branch: "No" }
              - { decision: d_busy, branch: "Yes" }
              - { action: gamma, kind: processing }
            next: Connected
        """;

    // HolePage with the missing corner drawn: total, so the lint is silent on it.
    private const string TotalPage = HolePage + """

          - id: t04_no_ack_not_busy
            on: I_received
            path:
              - { decision: d_ack, branch: "No" }
              - { decision: d_busy, branch: "No" }
              - { action: delta, kind: processing }
            next: Connected
        """;

    [Fact]
    public void A_missing_corner_of_two_independent_flags_is_reported_as_a_hole_with_a_witness()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("data-link/connected.sdl.yaml", HolePage);

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("::error::");
        result.Stderr.Should().Contain("totality");
        result.Stderr.Should().Contain("state `Connected`, event `I_received`");
        result.Stderr.Should().Contain("no transition matches when ack_pending=false, own_receiver_busy=false");
        result.Stderr.Should().Contain("witness ack_pending=false, own_receiver_busy=false");
        result.Stderr.Should().NotContain("determinism:");
    }

    [Fact]
    public void Two_transitions_matching_the_same_feasible_valuation_are_reported_as_nondeterminism_naming_both()
    {
        // A syntactic overlap never reaches this lint (LintGuardOverlap fails
        // the run first), so this is an overlap only the resolved guards show:
        // the raw literals are `vs_eq_va` and `not vs_eq_va` (disjoint to the
        // old lint), but the second path assigns V(a) := N(r) before its
        // decision, so the Resolver (ax25sdl#53) emits `not vs_eq_nr`, and
        // V(s) = V(a) with V(s) != N(r) satisfies both.
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("data-link/connected.sdl.yaml", Header + """

            decisions:
              - id: d_all_acked
                question: "V(s) == V(a)?"
                predicate: vs_eq_va
            transitions:
              - id: t01_unchanged
                on: RR_received
                path:
                  - { decision: d_all_acked, branch: "Yes" }
                  - { action: alpha, kind: processing }
                next: Connected
              - id: t02_after_assignment
                on: RR_received
                path:
                  - { action: "V(a) := N(r)", kind: processing }
                  - { decision: d_all_acked, branch: "No" }
                  - { action: beta, kind: processing }
                next: Connected
            """);

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().NotContain("non-disjoint", "the syntactic overlap lint cannot see this one");
        result.Stderr.Should().Contain("determinism: state `Connected`, event `RR_received`: transitions `t01_unchanged`, `t02_after_assignment` all match when vs_eq_va=true, vs_eq_nr=false; witness V(s)=0, V(a)=0, N(r)=1");
        // The same pair also leaves V(s) != V(a), V(s) == N(r) uncovered.
        result.Stderr.Should().Contain("totality: state `Connected`, event `RR_received`: no transition matches when vs_eq_va=false, vs_eq_nr=true");
    }

    [Fact]
    public void A_hole_only_at_an_infeasible_valuation_does_not_fire_and_is_counted_as_classified_infeasible()
    {
        // ns_eq_vr and vr_lt_ns_lt_vr_plus_k can never both hold (N(s) cannot
        // equal V(r) and lie strictly inside the window at once), so the
        // undrawn (true, true) corner is not a hole in the figure.
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("data-link/connected.sdl.yaml", Header + """

            decisions:
              - id: d_in_seq
                question: "N(s) == V(r)?"
                predicate: ns_eq_vr
              - id: d_window
                question: "V(r) < N(s) < V(r)+k?"
                predicate: vr_lt_ns_lt_vr_plus_k
            transitions:
              - id: t01_in_sequence
                on: I_received
                path:
                  - { decision: d_in_seq, branch: "Yes" }
                  - { decision: d_window, branch: "No" }
                  - { action: deliver, kind: processing }
                next: Connected
              - id: t02_in_window
                on: I_received
                path:
                  - { decision: d_in_seq, branch: "No" }
                  - { decision: d_window, branch: "Yes" }
                  - { action: store, kind: processing }
                next: Connected
              - id: t03_out_of_window
                on: I_received
                path:
                  - { decision: d_in_seq, branch: "No" }
                  - { decision: d_window, branch: "No" }
                  - { action: discard, kind: processing }
                next: Connected
            """);

        var result = r.Run();

        result.ExitCode.Should().Be(0, $"stderr: {result.Stderr}\nstdout: {result.Stdout}");
        result.Stderr.Should().NotContain("totality");
        result.Stdout.Should().Contain("0 hole(s), 0 overlap(s); 1 syntactic hole(s) and 0 syntactic overlap(s) were infeasible under the atom domain model");
    }

    [Fact]
    public void A_subroutine_whose_paths_do_not_cover_a_feasible_valuation_is_a_hole()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("data-link/connected.sdl.yaml", TotalPage);
        r.WritePage("data-link/subroutines.sdl.yaml", """
            machine: data_link
            source: { spec: test, figure: f7 }
            subroutines:
              - name: Half_Drawn
                decisions:
                  - id: d_busy
                    question: "Own receiver busy?"
                    predicate: own_receiver_busy
                paths:
                  - id: p01_busy
                    path:
                      - { decision: d_busy, branch: "Yes" }
                      - { action: send_rnr, kind: signal_lower }
            """);

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("totality: subroutine `Half_Drawn`: no path matches when own_receiver_busy=false");
        // The state page in this fixture is total, so the only finding is the subroutine's.
        result.Stderr.Should().NotContain("state `Connected`");
    }

    [Fact]
    public void A_valuation_covered_only_by_an_Undefined_branch_is_not_a_hole()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("data-link/connected.sdl.yaml", Header + """

            decisions:
              - id: d_ack
                question: "Ack pending?"
                predicate: ack_pending
              - id: d_open
                question: "V(s) == V(a)?"
                predicate: vs_eq_va
            transitions:
              - id: t01_ack
                on: I_received
                path:
                  - { decision: d_ack, branch: "Yes" }
                  - { action: alpha, kind: processing }
                next: Connected
              - id: t02_no_ack_spec_undefined
                on: I_received
                path:
                  - { decision: d_ack, branch: "No" }
                  - { decision: d_open, branch: "Undefined" }
                next: Connected
            """);

        var result = r.Run();

        result.ExitCode.Should().Be(0, $"stderr: {result.Stderr}\nstdout: {result.Stdout}");
        result.Stdout.Should().Contain("1 valuation(s) covered only by an Undefined spec branch");
    }

    private const string HoleAllowList = """
        findings:
          - kind: hole
            machine: data_link
            state: Connected
            event: I_received
            when:
              ack_pending: false
              own_receiver_busy: false
            issue: https://github.com/packethacking/ax25spec/issues/999
            note: fixture
        """;

    [Fact]
    public void An_allow_listed_finding_is_downgraded_to_a_warning_and_the_run_succeeds()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("data-link/connected.sdl.yaml", HolePage);
        r.WriteKnownFindings(HoleAllowList);

        var result = r.Run();

        result.ExitCode.Should().Be(0, $"stderr: {result.Stderr}\nstdout: {result.Stdout}");
        result.Stderr.Should().NotContain("::error::");
        result.Stderr.Should().Contain("::warning::");
        result.Stderr.Should().Contain("no transition matches when ack_pending=false, own_receiver_busy=false");
        result.Stderr.Should().Contain("(known: https://github.com/packethacking/ax25spec/issues/999; fixture)");
        result.Stdout.Should().Contain("1 known finding(s) downgraded to warnings");
        r.GeneratedExists("DataLink_Connected.g.cs").Should().BeTrue();
    }

    [Fact]
    public void An_allow_list_entry_that_matches_nothing_fails_the_run()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        // The same page with the missing corner drawn: no finding, so the entry is stale.
        r.WritePage("data-link/connected.sdl.yaml", TotalPage);
        r.WriteKnownFindings(HoleAllowList);

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("::error::");
        result.Stderr.Should().Contain("matches no current finding");
        result.Stderr.Should().Contain("https://github.com/packethacking/ax25spec/issues/999");
        r.GeneratedExists("DataLink_Connected.g.cs").Should().BeFalse();
    }

    [Fact]
    public void An_allow_list_entry_without_an_issue_url_is_rejected()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("data-link/connected.sdl.yaml", HolePage);
        r.WriteKnownFindings(HoleAllowList.Replace("issue: https://github.com/packethacking/ax25spec/issues/999", "issue: TBD", StringComparison.Ordinal));

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("`issue:` is required and must be a URL");
    }

    [Fact]
    public void An_explicit_allow_list_path_that_does_not_exist_is_an_error()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(Events);
        r.WritePage("data-link/connected.sdl.yaml", HolePage);
        r.WriteKnownFindings(HoleAllowList);
        File.Delete(r.KnownFindingsPath!);

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("does not exist");
    }
}
