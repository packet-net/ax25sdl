using System.Globalization;
using System.Text;
using Packet.Sdl.IR;

namespace Packet.Sdl.CodeGen.Rust;

/// <summary>
/// Rust emitter for the resolved SDL IR. Produces one <c>.g.rs</c> file
/// per page in the <c>ax25sdl</c> crate (under <c>spec/rust/src/</c>).
/// Hand-rolled string emission (no template engine) — mirrors the Go
/// emitter's pattern.
/// </summary>
/// <remarks>
/// <para>
/// Unlike Go (separate <c>.g_test.go</c>) and TypeScript (separate
/// <c>.g.test.ts</c>), Rust idiomatically nests per-module tests under
/// <c>#[cfg(test)] mod tests { ... }</c> at the bottom of the same file.
/// We emit the data table and its corresponding tests into one combined
/// <c>.g.rs</c> file per state page; <see cref="EmitStatePage"/> is the
/// only emission entrypoint for state-machine pages.
/// </para>
/// <para>
/// The output is post-processed by <c>rustfmt</c> in the orchestrator —
/// we aim for output that's already close to canonical, and let
/// <c>rustfmt</c> handle the last-mile whitespace alignment.
/// </para>
/// </remarks>
public static class RustEmitter
{
    public sealed record Emission(string FileName, string Content);

    public static Emission EmitStatePage(ResolvedPage page)
    {
        var stem = AnnexC.OutputStem(page.Machine, page.SourcePath);
        var fileName = stem + ".g.rs";

        // Rust convention: SCREAMING_SNAKE_CASE for `pub static` items.
        // Machine + state: data_link + Disconnected → DATA_LINK_DISCONNECTED.
        var staticName = (ScreamingSnake(page.Machine) + "_" + ScreamingSnake(page.State)).ToUpperInvariant();

        var sb = new StringBuilder();
        EmitHeader(sb, page.SourcePath);
        sb.Append("use crate::types::*;\n\n");
        sb.Append("/// SDL transitions for the ").Append(page.State)
          .Append(" state of the ").Append(page.Machine).Append(" machine.\n");
        sb.Append("/// Source: ").Append(page.SourceSpec).Append(", figure ").Append(page.SourceFigure).Append(".\n");
        sb.Append("pub static ").Append(staticName).Append(": StatePage = StatePage {\n");
        sb.Append("    machine: ").Append(RustStringLiteral(page.Machine)).Append(",\n");
        sb.Append("    state: ").Append(RustStringLiteral(page.State)).Append(",\n");
        sb.Append("    source: SdlSource {\n");
        sb.Append("        spec: ").Append(RustStringLiteral(page.SourceSpec)).Append(",\n");
        sb.Append("        figure: ").Append(RustStringLiteral(page.SourceFigure)).Append(",\n");
        sb.Append("        url: ").Append(RustStringLiteral(page.SourceUrl ?? "")).Append(",\n");
        sb.Append("    },\n");
        sb.Append("    transitions: &[\n");
        foreach (var t in page.Transitions)
        {
            sb.Append("        TransitionSpec {\n");
            sb.Append("            id: ").Append(RustStringLiteral(t.Id)).Append(",\n");
            sb.Append("            from: ").Append(RustStringLiteral(page.State)).Append(",\n");
            sb.Append("            on: ").Append(EventEnumLiteral(t.On)).Append(",\n");
            sb.Append("            guard: ").Append(FormatGuard(t.Guard, 3)).Append(",\n");
            sb.Append("            actions: ").Append(FormatActions(t.Actions, 3)).Append(",\n");
            sb.Append("            next: ").Append(RustStringLiteral(t.Next)).Append(",\n");
            sb.Append("            notes: ").Append(RustStringLiteral(t.Notes ?? "")).Append(",\n");
            sb.Append("            references: ").Append(FormatReferences(t.References, 3)).Append(",\n");
            sb.Append("            loops: ").Append(FormatLoops(t.Loops, 3)).Append(",\n");
            sb.Append("        },\n");
        }
        sb.Append("    ],\n");
        sb.Append("};\n");

        // Per-transition tests (inline mod, idiomatic Rust). Mirrors the
        // C# .g.Tests.cs / Go .g_test.go scope: one test per transition
        // checking id/on/next/guard plus every action's verb + kind.
        sb.Append('\n');
        sb.Append("#[cfg(test)]\n");
        sb.Append("mod tests {\n");
        sb.Append("    use super::*;\n\n");

        sb.Append("    #[test]\n");
        sb.Append("    fn source_figure() {\n");
        sb.Append("        assert_eq!(").Append(staticName).Append(".source.figure, ")
          .Append(RustStringLiteral(page.SourceFigure)).Append(");\n");
        sb.Append("    }\n\n");

        sb.Append("    #[test]\n");
        sb.Append("    fn transitions_are_present() {\n");
        sb.Append("        assert_eq!(").Append(staticName).Append(".transitions.len(), ")
          .Append(page.Transitions.Count.ToString(CultureInfo.InvariantCulture)).Append(");\n");
        sb.Append("    }\n");

        foreach (var t in page.Transitions)
        {
            sb.Append('\n');
            sb.Append("    #[test]\n");
            sb.Append("    fn ").Append(t.Id).Append("() {\n");
            sb.Append("        let tx = ").Append(staticName).Append(".transitions.iter()\n");
            sb.Append("            .find(|x| x.id == ").Append(RustStringLiteral(t.Id)).Append(")\n");
            sb.Append("            .expect(\"transition ").Append(t.Id).Append(" not found\");\n");
            sb.Append("        assert_eq!(tx.on, ").Append(EventEnumLiteral(t.On)).Append(");\n");
            sb.Append("        assert_eq!(tx.next, ").Append(RustStringLiteral(t.Next)).Append(");\n");
            if (!string.IsNullOrEmpty(t.Guard))
            {
                sb.Append("        assert_eq!(tx.guard, ").Append(FormatGuard(t.Guard!, 2)).Append(");\n");
            }
            sb.Append("        assert_eq!(tx.actions.len(), ")
              .Append(t.Actions.Count.ToString(CultureInfo.InvariantCulture)).Append(");\n");
            for (int i = 0; i < t.Actions.Count; i++)
            {
                var a = t.Actions[i];
                var idx = i.ToString(CultureInfo.InvariantCulture);
                sb.Append("        assert_eq!(tx.actions[").Append(idx).Append("].verb, ")
                  .Append(VerbEnumLiteral(a.Verb)).Append(");\n");
                sb.Append("        assert_eq!(tx.actions[").Append(idx).Append("].kind, ")
                  .Append(RustKindLiteral(a.Kind)).Append(");\n");
            }
            sb.Append("    }\n");
        }
        sb.Append("}\n");

        return new Emission(fileName, sb.ToString());
    }

    public static Emission EmitSubroutinePage(ResolvedSubroutinesPage page)
    {
        var fileStem = Path.GetFileNameWithoutExtension(page.SourcePath)
            .Replace(".sdl", string.Empty, StringComparison.Ordinal);
        var staticName = (ScreamingSnake(page.Machine) + "_" + ScreamingSnake(fileStem)).ToUpperInvariant();
        var fileName = AnnexC.OutputStem(page.Machine, page.SourcePath) + ".g.rs";

        var sb = new StringBuilder();
        EmitHeader(sb, page.SourcePath);
        sb.Append("use crate::types::*;\n\n");
        sb.Append("/// SDL subroutines for the ").Append(page.Machine).Append(" machine.\n");
        sb.Append("/// Source: ").Append(page.SourceSpec).Append(", figure ").Append(page.SourceFigure).Append(".\n");
        sb.Append("pub static ").Append(staticName).Append(": SubroutinesPage = SubroutinesPage {\n");
        sb.Append("    machine: ").Append(RustStringLiteral(page.Machine)).Append(",\n");
        sb.Append("    source: SdlSource {\n");
        sb.Append("        spec: ").Append(RustStringLiteral(page.SourceSpec)).Append(",\n");
        sb.Append("        figure: ").Append(RustStringLiteral(page.SourceFigure)).Append(",\n");
        sb.Append("        url: ").Append(RustStringLiteral(page.SourceUrl ?? "")).Append(",\n");
        sb.Append("    },\n");
        sb.Append("    subroutines: &[\n");
        foreach (var s in page.Subroutines)
        {
            sb.Append("        SubroutineSpec {\n");
            sb.Append("            name: ").Append(RustStringLiteral(s.Name)).Append(",\n");
            sb.Append("            paths: &[\n");
            foreach (var p in s.Paths)
            {
                sb.Append("                SubroutinePath {\n");
                sb.Append("                    id: ").Append(RustStringLiteral(p.Id)).Append(",\n");
                sb.Append("                    guard: ").Append(FormatGuard(p.Guard, 5)).Append(",\n");
                sb.Append("                    actions: ").Append(FormatActions(p.Actions, 5)).Append(",\n");
                sb.Append("                    notes: ").Append(RustStringLiteral(p.Notes ?? "")).Append(",\n");
                sb.Append("                    references: ").Append(FormatReferences(p.References, 5)).Append(",\n");
                sb.Append("                    loops: ").Append(FormatLoops(p.Loops, 5)).Append(",\n");
                sb.Append("                },\n");
            }
            sb.Append("            ],\n");
            sb.Append("            notes: ").Append(RustStringLiteral(s.Notes ?? "")).Append(",\n");
            sb.Append("            references: ").Append(FormatReferences(s.References, 3)).Append(",\n");
            sb.Append("        },\n");
        }
        sb.Append("    ],\n");
        sb.Append("};\n");

        return new Emission(fileName, sb.ToString());
    }

    /// <summary>
    /// Build the crate's <c>lib.rs</c> that declares each generated
    /// module + the hand-written <c>types</c> module, and re-exports
    /// every page's statics for ergonomic single-import access
    /// (<c>use ax25sdl::DATA_LINK_DISCONNECTED;</c>).
    /// </summary>
    public static string EmitLib(IEnumerable<ResolvedPage> pages, IEnumerable<ResolvedSubroutinesPage> subPages)
    {
        var sb = new StringBuilder();
        sb.Append("// Code generated by codegen/src/Packet.Sdl.CodeGen. DO NOT EDIT.\n");
        sb.Append("// Re-exports every SDL page so consumers can `use ax25sdl::DATA_LINK_DISCONNECTED`.\n");
        // no_std by default for embedded consumers (e.g. a Pi Pico W node);
        // the default-on `std` feature lights up the host test harness. The
        // generated data tables + types are `&'static` / `Copy` and touch no
        // allocator, so the core path is no_std-clean either way.
        sb.Append("#![cfg_attr(not(feature = \"std\"), no_std)]\n\n");
        sb.Append("pub mod types;\n");
        sb.Append("pub use types::*;\n\n");
        // The closed typed sets (SP-010 / ADR-0002) — one generated module
        // each, re-exported so consumers `use ax25sdl::{Ax25Event, Ax25Guard,
        // Ax25ActionVerb}`.
        sb.Append("#[path = \"ax25_action_verb.g.rs\"]\n");
        sb.Append("pub mod ax25_action_verb;\n");
        sb.Append("pub use ax25_action_verb::*;\n");
        sb.Append("#[path = \"ax25_guard.g.rs\"]\n");
        sb.Append("pub mod ax25_guard;\n");
        sb.Append("pub use ax25_guard::*;\n");
        sb.Append("#[path = \"ax25_event.g.rs\"]\n");
        sb.Append("pub mod ax25_event;\n");
        sb.Append("pub use ax25_event::*;\n\n");

        var stems = new List<string>();
        foreach (var page in pages.OrderBy(p => p.SourcePath, StringComparer.Ordinal))
        {
            stems.Add(AnnexC.OutputStem(page.Machine, page.SourcePath));
        }
        foreach (var page in subPages.OrderBy(p => p.SourcePath, StringComparer.Ordinal))
        {
            stems.Add(AnnexC.OutputStem(page.Machine, page.SourcePath));
        }

        foreach (var stem in stems)
        {
            sb.Append("#[path = \"").Append(stem).Append(".g.rs\"]\n");
            sb.Append("pub mod ").Append(stem).Append(";\n");
        }
        sb.Append('\n');
        foreach (var stem in stems)
        {
            sb.Append("pub use ").Append(stem).Append("::*;\n");
        }
        return sb.ToString();
    }

    // ─── Formatting helpers ───────────────────────────────────────────

    private static void EmitHeader(StringBuilder sb, string sourcePath)
    {
        sb.Append("// Code generated by codegen/src/Packet.Sdl.CodeGen from ").Append(sourcePath.Replace('\\', '/')).Append(".\n");
        sb.Append("// DO NOT EDIT. Run `dotnet run --project codegen/src/Packet.Sdl.CodeGen` to regenerate.\n\n");
    }

    /// <summary>
    /// <paramref name="parentIndent"/> is the indentation depth (in
    /// 4-space units) of the surrounding <c>field: &amp;[</c> line.
    /// Entries land at parentIndent+1; the closing bracket matches
    /// parentIndent. Matches rustfmt's expected nesting for static
    /// slice initialisers.
    /// </summary>
    private static string FormatActions(IReadOnlyList<ResolvedAction> actions, int parentIndent)
    {
        if (actions.Count == 0) return "&[]";
        var indent = new string(' ', (parentIndent + 1) * 4);
        var closer = new string(' ', parentIndent * 4);
        var sb = new StringBuilder();
        sb.Append("&[\n");
        foreach (var a in actions)
        {
            sb.Append(indent).Append("ActionStep { verb: ").Append(VerbEnumLiteral(a.Verb))
              .Append(", kind: ").Append(RustKindLiteral(a.Kind)).Append(" },\n");
        }
        sb.Append(closer).Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// Render a composed guard string as a Rust <c>&amp;[GuardTerm]</c>
    /// conjunction — empty <c>&amp;[]</c> when unguarded. Each conjunct
    /// carries its atom as a typed <see cref="Ax25Guard"/> member (not a raw
    /// string), so a renamed/typo'd atom is a compile error rather than a
    /// runtime "unbound identifier". Mirrors the C# <c>GuardTerm[]</c> /
    /// TS <c>GuardTerm[]</c> emission (ADR-0002).
    /// </summary>
    private static string FormatGuard(string? guard, int parentIndent)
    {
        var terms = GuardExpression.Parse(guard);
        if (terms.Count == 0) return "&[]";
        var indent = new string(' ', (parentIndent + 1) * 4);
        var closer = new string(' ', parentIndent * 4);
        var sb = new StringBuilder();
        sb.Append("&[\n");
        foreach (var term in terms)
        {
            sb.Append(indent).Append(GuardTermLiteral(term)).Append(",\n");
        }
        sb.Append(closer).Append(']');
        return sb.ToString();
    }

    private static string FormatReferences(IReadOnlyList<ResolvedReference> refs, int parentIndent)
    {
        if (refs.Count == 0) return "&[]";
        var indent = new string(' ', (parentIndent + 1) * 4);
        var closer = new string(' ', parentIndent * 4);
        var sb = new StringBuilder();
        sb.Append("&[\n");
        foreach (var r in refs)
        {
            sb.Append(indent).Append("ImplementationReference { source: ").Append(RustStringLiteral(r.Source))
              .Append(", cite: ").Append(RustStringLiteral(r.Cite ?? ""))
              .Append(", quote: ").Append(RustStringLiteral(r.Quote ?? ""))
              .Append(", path: ").Append(RustStringLiteral(r.Path ?? ""))
              .Append(", function: ").Append(RustStringLiteral(r.Function ?? ""))
              .Append(", line: ").Append((r.Line ?? 0).ToString(CultureInfo.InvariantCulture))
              .Append(", note: ").Append(RustStringLiteral(r.Note ?? ""))
              .Append(" },\n");
        }
        sb.Append(closer).Append(']');
        return sb.ToString();
    }

    private static string FormatLoops(IReadOnlyList<ResolvedLoop> loops, int parentIndent)
    {
        if (loops.Count == 0) return "&[]";
        var indent = new string(' ', (parentIndent + 1) * 4);
        var closer = new string(' ', parentIndent * 4);
        var sb = new StringBuilder();
        sb.Append("&[\n");
        foreach (var l in loops)
        {
            sb.Append(indent).Append("LoopRange { start: ").Append(l.Start.ToString(CultureInfo.InvariantCulture))
              .Append(", length: ").Append(l.Length.ToString(CultureInfo.InvariantCulture))
              .Append(", predicate: ").Append(GuardTermLiteral(GuardExpression.ParseSingle(l.Predicate)))
              .Append(", test_at_end: ").Append(l.TestAtEnd ? "true" : "false")
              .Append(" },\n");
        }
        sb.Append(closer).Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// Rust string literal — double-quoted with backslash escapes.
    /// Avoids raw strings because notes / predicates can in principle
    /// contain <c>#"</c> sequences; standard-escaped literals are the
    /// safe default.
    /// </summary>
    internal static string RustStringLiteral(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20 || c == 0x7f)
                        sb.AppendFormat(CultureInfo.InvariantCulture, "\\x{0:x2}", (int)c);
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Emit the kind as a path expression. Resolves against the
    /// hand-written <c>ActionKind</c> enum in <c>spec/rust/src/types.rs</c>.
    /// </summary>
    internal static string RustKindLiteral(ResolvedActionKind kind) => kind switch
    {
        ResolvedActionKind.SignalUpper => "ActionKind::SignalUpper",
        ResolvedActionKind.SignalLower => "ActionKind::SignalLower",
        ResolvedActionKind.Processing  => "ActionKind::Processing",
        ResolvedActionKind.Subroutine  => "ActionKind::Subroutine",
        ResolvedActionKind.InternalOut => "ActionKind::InternalOut",
        _ => throw new InvalidOperationException($"unknown action kind '{kind}'"),
    };

    // ─── Typed closed sets (SP-010 / ADR-0002, ported to Rust) ────────────
    //
    // Mirrors the C#/TS typed-set emission: an `Ax25ActionVerb` /
    // `Ax25Guard` / `Ax25Event` closed enum so a consumer can `match`
    // exhaustively (a renamed/typo'd verb, atom, or event becomes a compile
    // error rather than a runtime "unknown" throw). The enum-member folding
    // is identical to the C# emitter's so the closed-set member names are
    // nominally the same across backends. Rust accepts acronym-run PascalCase
    // variant names (`DLDISCONNECTRequest`, `DMFEq1`) without a
    // non_camel_case_types warning.

    /// <summary>
    /// Map a canonical verb string to a stable, collision-free Rust enum
    /// variant identifier for <c>Ax25ActionVerb</c>. Identical folding to the
    /// C# emitter: recurring operators become words
    /// (<c>:=</c>→Assign, <c>*</c>→Times, <c>+</c>→Plus, <c>=</c>→Eq); every
    /// other run of non-alphanumerics is a PascalCase token boundary.
    /// </summary>
    internal static string VerbEnumMember(string verb)
    {
        var s = verb
            .Replace(":=", " Assign ", StringComparison.Ordinal)
            .Replace("*",  " Times ",  StringComparison.Ordinal)
            .Replace("+",  " Plus ",   StringComparison.Ordinal)
            .Replace("=",  " Eq ",     StringComparison.Ordinal);

        var sb = new StringBuilder(s.Length);
        var atBoundary = true;
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(atBoundary ? char.ToUpperInvariant(ch) : ch);
                atBoundary = false;
            }
            else
            {
                atBoundary = true;
            }
        }

        var id = sb.ToString();
        if (id.Length == 0)
            throw new InvalidOperationException($"verb '{verb}' sanitised to an empty enum identifier");
        if (char.IsDigit(id[0]))
            id = "_" + id;
        return id;
    }

    /// <summary>
    /// Map a canonical guard atom to a stable, collision-free Rust enum
    /// variant identifier for <c>Ax25Guard</c>. Atoms are already valid
    /// identifiers (<c>[A-Za-z0-9_]+</c>) so this is an underscore-boundary
    /// PascalCase fold (e.g. <c>vr_I_frame_stored</c> → <c>VrIFrameStored</c>).
    /// Identical to the C# / event folding.
    /// </summary>
    internal static string GuardEnumMember(string atom)
    {
        var sb = new StringBuilder(atom.Length);
        var atBoundary = true;
        foreach (var ch in atom)
        {
            if (ch == '_')
            {
                atBoundary = true;
                continue;
            }
            sb.Append(atBoundary ? char.ToUpperInvariant(ch) : ch);
            atBoundary = false;
        }

        var id = sb.ToString();
        if (id.Length == 0)
            throw new InvalidOperationException($"guard atom '{atom}' sanitised to an empty enum identifier");
        if (char.IsDigit(id[0]))
            id = "_" + id;
        return id;
    }

    /// <summary>Map an event name to its <c>Ax25Event</c> variant identifier (same fold as <see cref="GuardEnumMember"/>).</summary>
    internal static string EventEnumMember(string evt) => GuardEnumMember(evt);

    /// <summary>Render a verb as its <c>Ax25ActionVerb::&lt;Member&gt;</c> literal.</summary>
    internal static string VerbEnumLiteral(string verb) => "Ax25ActionVerb::" + VerbEnumMember(verb);

    /// <summary>Render a guard atom as its <c>Ax25Guard::&lt;Member&gt;</c> literal.</summary>
    internal static string GuardEnumLiteral(string atom) => "Ax25Guard::" + GuardEnumMember(atom);

    /// <summary>Render an event as its <c>Ax25Event::&lt;Member&gt;</c> literal.</summary>
    internal static string EventEnumLiteral(string evt) => "Ax25Event::" + EventEnumMember(evt);

    /// <summary>Render one typed guard term as a <c>GuardTerm { atom: Ax25Guard::X, negate: false }</c> literal.</summary>
    internal static string GuardTermLiteral(GuardTermIR term)
        => "GuardTerm { atom: " + GuardEnumLiteral(term.Atom) + ", negate: " + (term.Negate ? "true" : "false") + " }";

    private static string EnumDocComment(string canonical)
        // Rust doc comment body — escape `[` / `]` so rustdoc doesn't try to
        // resolve an intra-doc link, and wrap in inline code so operator-laden
        // verbs (`V(s) := V(a) + 1`) render verbatim.
        => "/// `" + canonical.Replace("\\", "\\\\", StringComparison.Ordinal) + "`\n";

    /// <summary>
    /// Render the generated <c>ax25_action_verb.g.rs</c> — the closed set of
    /// every canonical action verb in the spec as a Rust enum. Members are
    /// ordered by canonical string for a stable diff and de-duplicated; the
    /// emission asserts variant-name distinctness so a future collision fails
    /// codegen (mirrors the C# emitter).
    /// </summary>
    public static string EmitActionVerbEnum(IEnumerable<string> canonicalVerbs, IReadOnlyDictionary<string, int>? values = null)
        => EmitEnum(
            "Ax25ActionVerb",
            "spec-sdl/actions.yaml",
            "The closed set of canonical AX.25 SDL action verbs (figc4.x), one variant per\n/// distinct semantic action across every page + subroutine. Carried by\n/// [`ActionStep::verb`](crate::ActionStep); lets a consumer `match` exhaustively.",
            canonicalVerbs,
            VerbEnumMember,
            values);

    /// <summary>
    /// Render the generated <c>ax25_guard.g.rs</c> — the closed set of every
    /// canonical guard atom (gathered from the resolved IR, so it includes
    /// Resolver-synthesised atoms like <c>vs_eq_nr</c>) as a Rust enum.
    /// </summary>
    public static string EmitGuardEnum(IEnumerable<string> canonicalAtoms, IReadOnlyDictionary<string, int>? values = null)
        => EmitEnum(
            "Ax25Guard",
            "spec-sdl/predicates.yaml",
            "The closed set of canonical AX.25 SDL guard atoms (figc4.x decision\n/// predicates), one variant per distinct predicate across every page +\n/// subroutine. A transition's [`TransitionSpec::guard`](crate::TransitionSpec) is a\n/// conjunction of these atoms (each optionally negated; see\n/// [`GuardTerm`](crate::GuardTerm)); enumerating them lets a guard evaluator bind\n/// every atom exhaustively.",
            canonicalAtoms,
            GuardEnumMember,
            values);

    /// <summary>
    /// Render the generated <c>ax25_event.g.rs</c> — the closed set of every
    /// AX.25 SDL event (the <c>spec-sdl/events.yaml</c> catalogue) as a Rust
    /// enum.
    /// </summary>
    public static string EmitEventEnum(IEnumerable<string> events, IReadOnlyDictionary<string, int>? values = null)
        => EmitEnum(
            "Ax25Event",
            "spec-sdl/events.yaml",
            "The closed set of AX.25 SDL events: every primitive, received frame,\n/// timer expiry, internal queue/error signal, and catch-all in\n/// `spec-sdl/events.yaml`. Carried by [`TransitionSpec::on`](crate::TransitionSpec);\n/// lets a consumer dispatch on the incoming event exhaustively.",
            events,
            EventEnumMember,
            values);

    /// <summary>
    /// Shared renderer for the three closed-set enums. De-duplicates +
    /// orders by canonical string, asserts member distinctness, and emits a
    /// <c>#[derive(Debug, PartialEq, Eq, Clone, Copy, Hash)]</c> enum whose
    /// variants each carry the canonical text as a doc comment.
    /// </summary>
    private static string EmitEnum(
        string enumName,
        string sourceFile,
        string summary,
        IEnumerable<string> canonicals,
        Func<string, string> member,
        IReadOnlyDictionary<string, int>? values = null)
    {
        var distinct = canonicals.Distinct(StringComparer.Ordinal)
                                 .OrderBy(v => v, StringComparer.Ordinal)
                                 .ToList();

        var byMember = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in distinct)
        {
            var m = member(v);
            if (byMember.TryGetValue(m, out var clash))
                throw new InvalidOperationException(
                    $"{enumName} identifier collision: '{v}' and '{clash}' both sanitise to '{m}'. " +
                    $"Disambiguate one of the canonical spellings in {sourceFile}.");
            byMember[m] = v;
        }

        var sb = new StringBuilder();
        sb.Append("// Code generated by codegen/src/Packet.Sdl.CodeGen. DO NOT EDIT.\n");
        sb.Append("// The closed set generated from ").Append(sourceFile).Append(".\n\n");
        sb.Append("/// ").Append(summary).Append('\n');
        sb.Append("#[derive(Debug, PartialEq, Eq, Clone, Copy, Hash)]\n");
        sb.Append("pub enum ").Append(enumName).Append(" {\n");
        foreach (var v in distinct)
        {
            sb.Append("    ").Append(EnumDocComment(v));
            sb.Append("    ").Append(member(v));
            if (values is not null) sb.Append(" = ").Append(values[v].ToString(CultureInfo.InvariantCulture));
            sb.Append(",\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>
    /// snake_case → SCREAMING_SNAKE_CASE. Also handles input that's
    /// already PascalCase (e.g. <c>AwaitingConnection22</c> → splits
    /// runs of capitals into <c>AWAITING_CONNECTION_22</c>).
    /// </summary>
    internal static string ScreamingSnake(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var sb = new StringBuilder(input.Length + 4);
        for (int i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '_')
            {
                sb.Append('_');
                continue;
            }
            // Insert an underscore between a lowercase→uppercase
            // boundary and between a letter→digit / digit→letter
            // boundary, but only if the previous character isn't
            // already an underscore.
            if (i > 0)
            {
                var prev = input[i - 1];
                bool boundary =
                    (char.IsLower(prev) && char.IsUpper(c)) ||
                    (char.IsLetter(prev) && char.IsDigit(c)) ||
                    (char.IsDigit(prev) && char.IsLetter(c));
                if (boundary && sb.Length > 0 && sb[sb.Length - 1] != '_')
                {
                    sb.Append('_');
                }
            }
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }
}
