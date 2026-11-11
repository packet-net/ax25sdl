namespace Packet.Sdl.CodeGen.Tests;

/// <summary>
/// Black-box tests of the Rust emitter. Each test sets up a fixture
/// YAML, runs the codegen with <c>--rust --rust-out X</c> as a
/// subprocess, and asserts on the generated <c>.g.rs</c> shape +
/// <c>lib.rs</c> + idempotency.
/// </summary>
public class RustEmitterTests
{
    private const string MinimalEvents = """
        primitives_upper:
          - DL_DISCONNECT_request
        frames_received:
          - I_received
        catchalls: []
        internal: []
        timers:
          - T1_expiry
        """;

    private const string ValidMinimalPage = """
        machine: data_link
        state: Connected
        coverage: partial
        source:
          spec: test_spec
          figure: figc.test
        decisions: []
        transitions:
          - id: t01_dl_disconnect_request
            on: DL_DISCONNECT_request
            path:
              - { action: send_disc, kind: signal_lower }
            next: AwaitingRelease
        """;

    [Fact]
    public void Rust_only_emits_rust_files_and_skips_other_backends()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(MinimalEvents);
        r.WritePage("data-link/connected.sdl.yaml", ValidMinimalPage);

        var result = r.RunRust();

        result.ExitCode.Should().Be(0, $"stderr: {result.Stderr}\nstdout: {result.Stdout}");

        // .g.rs exists in the Rust output dir.
        r.RustExists("connected.g.rs").Should().BeTrue();

        // lib.rs exists alongside.
        r.RustExists("lib.rs").Should().BeTrue();

        // C# / Go / TS dirs untouched — CodegenRunner.OutDir +
        // TestsDir are the C# default sandbox; we should see no files
        // produced there when only --rust was passed.
        Directory.EnumerateFiles(r.OutDir, "*.g.cs").Should().BeEmpty(
            "no C# output should appear when only --rust is selected");
        Directory.EnumerateFiles(r.TestsDir, "*.g.Tests.cs").Should().BeEmpty(
            "no C# test output should appear when only --rust is selected");
    }

    [Fact]
    public void Generated_rust_file_contains_screaming_snake_static_and_combined_test_module()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(MinimalEvents);
        r.WritePage("data-link/connected.sdl.yaml", ValidMinimalPage);

        var result = r.RunRust();

        result.ExitCode.Should().Be(0, $"stderr: {result.Stderr}");
        var gen = r.ReadRust("connected.g.rs");

        // The page emits a SCREAMING_SNAKE static name derived from
        // machine + state (data_link + Connected → DATA_LINK_CONNECTED).
        gen.Should().Contain("pub static DATA_LINK_CONNECTED: StatePage = StatePage {");

        // The crate-relative types import is present (.g.rs depends on
        // the hand-written types.rs).
        gen.Should().Contain("use crate::types::*;");

        // The combined-file convention: data + tests in one .g.rs
        // wrapped in #[cfg(test)] mod tests { ... }.
        gen.Should().Contain("#[cfg(test)]");
        gen.Should().Contain("mod tests {");

        // The transition's id round-trips verbatim; on + verb are the typed
        // closed-set members (SP-010 / ADR-0002 ported to Rust), not raw
        // strings. on: DL_DISCONNECT_request → Ax25Event::DLDISCONNECTRequest;
        // action send_disc → Ax25ActionVerb::SendDisc.
        gen.Should().Contain("id: \"t01_dl_disconnect_request\"");
        gen.Should().Contain("on: Ax25Event::DLDISCONNECTRequest");
        gen.Should().Contain("verb: Ax25ActionVerb::SendDisc");
        gen.Should().Contain("kind: ActionKind::SignalLower");
        // The raw string spellings must NOT leak into the typed fields.
        gen.Should().NotContain("on: \"DL_DISCONNECT_request\"");
        gen.Should().NotContain("verb: \"send_disc\"");
    }

    [Fact]
    public void Generated_lib_rs_declares_module_and_re_exports()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(MinimalEvents);
        r.WritePage("data-link/connected.sdl.yaml", ValidMinimalPage);

        var result = r.RunRust();

        result.ExitCode.Should().Be(0, $"stderr: {result.Stderr}");
        var lib = r.ReadRust("lib.rs");

        // lib.rs always exposes the hand-written types module.
        lib.Should().Contain("pub mod types;");
        lib.Should().Contain("pub use types::*;");

        // Each generated .g.rs is wired in by #[path = "stem.g.rs"]
        // pub mod stem; plus a wildcard re-export for ergonomic
        // single-import access.
        lib.Should().Contain("#[path = \"connected.g.rs\"]");
        lib.Should().Contain("pub mod connected;");
        lib.Should().Contain("pub use connected::*;");
    }

    [Fact]
    public void Second_run_is_idempotent()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(MinimalEvents);
        r.WritePage("data-link/connected.sdl.yaml", ValidMinimalPage);

        var first = r.RunRust();
        first.ExitCode.Should().Be(0, $"first run stderr: {first.Stderr}");

        // Snapshot the generated content.
        var firstGen = r.ReadRust("connected.g.rs");
        var firstLib = r.ReadRust("lib.rs");

        var second = r.RunRust();
        second.ExitCode.Should().Be(0, $"second run stderr: {second.Stderr}");

        var secondGen = r.ReadRust("connected.g.rs");
        var secondLib = r.ReadRust("lib.rs");

        secondGen.Should().Be(firstGen, ".g.rs should be byte-identical between runs");
        secondLib.Should().Be(firstLib, "lib.rs should be byte-identical between runs");
    }

    [Fact]
    public void Per_transition_tests_are_emitted_inside_mod_tests()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(MinimalEvents);
        r.WritePage("data-link/connected.sdl.yaml", ValidMinimalPage);

        var result = r.RunRust();

        result.ExitCode.Should().Be(0, $"stderr: {result.Stderr}");
        var gen = r.ReadRust("connected.g.rs");

        // Page-level assertions live in mod tests.
        gen.Should().Contain("fn source_figure()");
        gen.Should().Contain("fn transitions_are_present()");

        // Per-transition test function name matches the transition's
        // id verbatim (IDs are already valid Rust identifiers — the
        // schema enforces snake_case starting with a letter).
        gen.Should().Contain("fn t01_dl_disconnect_request()");
    }

    // ─── Typed closed sets (SP-010 / ADR-0002 ported to Rust) ────────────

    [Fact]
    public void Rust_run_emits_the_three_typed_closed_set_enums()
    {
        // Mirrors how the C#/TS runs emit Ax25ActionVerb / Ax25Guard /
        // Ax25Event; the Rust backend now does too (so a no_std embedded
        // consumer can `match` exhaustively). Emitted under a Rust-only
        // invocation — not gated behind the C#/TS backends.
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(MinimalEvents);
        r.WritePage("data-link/connected.sdl.yaml", ValidMinimalPage);

        var result = r.RunRust();
        result.ExitCode.Should().Be(0, $"stderr: {result.Stderr}");

        r.RustExists("ax25_action_verb.g.rs").Should().BeTrue();
        r.RustExists("ax25_guard.g.rs").Should().BeTrue();
        r.RustExists("ax25_event.g.rs").Should().BeTrue();

        var verb = r.ReadRust("ax25_action_verb.g.rs");
        verb.Should().Contain("pub enum Ax25ActionVerb {");
        // send_disc → SendDisc (same fold as the C# emitter).
        verb.Should().MatchRegex(@"SendDisc = \d+,");

        var evt = r.ReadRust("ax25_event.g.rs");
        evt.Should().Contain("pub enum Ax25Event {");
        evt.Should().MatchRegex(@"DLDISCONNECTRequest = \d+,");

        // lib.rs wires the three enum modules in + re-exports them.
        var lib = r.ReadRust("lib.rs");
        lib.Should().Contain("#[path = \"ax25_action_verb.g.rs\"]");
        lib.Should().Contain("pub use ax25_action_verb::*;");
        lib.Should().Contain("pub use ax25_event::*;");
        lib.Should().Contain("pub use ax25_guard::*;");
    }

    [Fact]
    public void Generated_lib_rs_is_no_std_by_default_with_std_feature_escape()
    {
        // The crate is #![no_std] unless the default-on `std` feature is set.
        // The generated lib.rs carries the cfg_attr; the hand-written
        // Cargo.toml (outside codegen) declares the feature.
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(MinimalEvents);
        r.WritePage("data-link/connected.sdl.yaml", ValidMinimalPage);

        var result = r.RunRust();
        result.ExitCode.Should().Be(0, $"stderr: {result.Stderr}");

        var lib = r.ReadRust("lib.rs");
        lib.Should().Contain("#![cfg_attr(not(feature = \"std\"), no_std)]");
    }

    [Fact]
    public void Guard_is_emitted_as_typed_guard_term_slice_not_raw_string()
    {
        // A transition guard renders as an &[GuardTerm] conjunction whose
        // atom is a typed Ax25Guard member — the Rust analogue of the C#
        // GuardTerm[] / TS GuardTerm[] emission. The alias spelling never
        // appears; the canonical atom never appears as a raw string.
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(MinimalEvents);
        r.WritePredicatesCatalog("""
            flags:
              - name: peer_receiver_busy
                aliases:
                  - peer_busy
            """);
        r.WritePage("data-link/connected.sdl.yaml", """
            machine: data_link
            state: Connected
            coverage: partial
            source: { spec: test, figure: f }
            decisions:
              - id: peer_busy_q
                question: "Peer busy?"
                predicate: peer_busy
            transitions:
              - id: t01_yes
                on: I_received
                path:
                  - { decision: peer_busy_q, branch: "Yes" }
                  - { action: cleanup, kind: processing }
                next: Connected
              - id: t02_no
                on: I_received
                path:
                  - { decision: peer_busy_q, branch: "No" }
                  - { action: cleanup, kind: processing }
                next: Connected
            """);

        var result = r.RunRust();
        result.ExitCode.Should().Be(0, $"stderr: {result.Stderr}");

        var guardEnum = r.ReadRust("ax25_guard.g.rs");
        guardEnum.Should().Contain("pub enum Ax25Guard {");
        guardEnum.Should().MatchRegex(@"PeerReceiverBusy = \d+,");

        // rustfmt may wrap a GuardTerm across lines depending on the
        // surrounding indent, so assert on whitespace-collapsed source —
        // we care that the typed (atom, negate) literal is present, not
        // about rustfmt's line-wrapping choice.
        var gen = r.ReadRust("connected.g.rs");
        var flat = System.Text.RegularExpressions.Regex.Replace(gen, @"\s+", " ");
        flat.Should().Contain("GuardTerm { atom: Ax25Guard::PeerReceiverBusy, negate: false }");
        flat.Should().Contain("GuardTerm { atom: Ax25Guard::PeerReceiverBusy, negate: true }");
        // Neither the alias nor the canonical leaks as a raw guard string.
        gen.Should().NotContain("peer_busy");
        gen.Should().NotContain("guard: \"peer_receiver_busy\"");
    }
}
