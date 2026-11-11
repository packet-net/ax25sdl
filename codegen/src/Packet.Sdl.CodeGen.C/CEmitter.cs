using System.Globalization;
using System.Text;
using Packet.Sdl.IR;

namespace Packet.Sdl.CodeGen.C;

/// <summary>
/// C emitter for the resolved SDL IR. Produces one <c>.g.c</c> file
/// per page (state-machine or subroutine) defining a <c>const</c>
/// instance of the corresponding runtime type. State-machine pages
/// also get a matching <c>.g.test.c</c> file containing a standalone
/// <c>main()</c> that asserts every transition's shape. Hand-rolled
/// string emission (no template engine) — mirrors the Go emitter
/// shape so the two stay easy to keep in sync.
/// </summary>
/// <remarks>
/// <para>
/// C lacks slice types, so the runtime in <c>spec/c/include/ax25sdl.h</c>
/// uses paired <c>pointer + size_t length</c> fields. Each
/// transition's per-list arrays (actions, references, loops) get
/// their own named <c>static const</c> array at file scope to
/// avoid the C99 trade-off where compound literals embedded in a
/// braced initialiser would land in automatic storage. Empty arrays
/// are encoded as <c>NULL + 0</c>; no zero-length array static is
/// emitted for them (ISO C forbids zero-sized arrays).
/// </para>
/// <para>
/// Symbol naming uses snake_case throughout (C convention). The
/// page symbol is <c>&lt;machine&gt;_&lt;state-as-snake&gt;</c>;
/// per-transition statics are <c>&lt;symbol&gt;_&lt;tid&gt;_actions</c>
/// / <c>_references</c> / <c>_loops</c>. Transition IDs from the
/// schema are already snake-case valid C identifiers.
/// </para>
/// </remarks>
public static class CEmitter
{
    public sealed record Emission(string FileName, string Content);

    public static Emission EmitStatePage(ResolvedPage page)
    {
        var stem = AnnexC.OutputStem(page.Machine, page.SourcePath);
        var symbol = page.Machine + "_" + Snake(page.State);
        var fileName = stem + ".g.c";

        var sb = new StringBuilder();
        EmitHeader(sb, page.SourcePath);
        sb.Append("#include \"ax25sdl.h\"\n\n");

        // File-scope statics for each transition's variable-length lists.
        // Emitted first so the StatePage initialiser can reference them by
        // name. Skipping empties keeps the file readable (and avoids
        // zero-length array statics, which are non-portable in C99).
        for (int i = 0; i < page.Transitions.Count; i++)
        {
            var t = page.Transitions[i];
            var prefix = symbol + "_" + t.Id;
            EmitActionsStatic(sb, prefix + "_actions", t.Actions);
            EmitReferencesStatic(sb, prefix + "_references", t.References);
            EmitLoopsStatic(sb, prefix + "_loops", t.Loops);
        }

        sb.Append("static const TransitionSpec ").Append(symbol).Append("_transitions[] = {\n");
        foreach (var t in page.Transitions)
        {
            var prefix = symbol + "_" + t.Id;
            sb.Append("    {\n");
            sb.Append("        .id = ").Append(CStringLiteral(t.Id)).Append(",\n");
            sb.Append("        .from = ").Append(CStringLiteral(page.State)).Append(",\n");
            sb.Append("        .on = ").Append(CStringLiteral(t.On)).Append(",\n");
            sb.Append("        .guard = ").Append(CStringLiteral(t.Guard ?? "")).Append(",\n");
            EmitListField(sb, "actions", prefix + "_actions", t.Actions.Count);
            sb.Append("        .next = ").Append(CStringLiteral(t.Next)).Append(",\n");
            sb.Append("        .notes = ").Append(CStringLiteral(t.Notes ?? "")).Append(",\n");
            EmitListField(sb, "references", prefix + "_references", t.References.Count);
            EmitListField(sb, "loops", prefix + "_loops", t.Loops.Count);
            sb.Append("    },\n");
        }
        sb.Append("};\n\n");

        sb.Append("const StatePage ").Append(symbol).Append(" = {\n");
        sb.Append("    .machine = ").Append(CStringLiteral(page.Machine)).Append(",\n");
        sb.Append("    .state = ").Append(CStringLiteral(page.State)).Append(",\n");
        sb.Append("    .source = { .spec = ").Append(CStringLiteral(page.SourceSpec))
          .Append(", .figure = ").Append(CStringLiteral(page.SourceFigure))
          .Append(", .url = ").Append(CStringLiteral(page.SourceUrl ?? "")).Append(" },\n");
        sb.Append("    .transitions = ").Append(symbol).Append("_transitions,\n");
        sb.Append("    .transitions_len = ").Append(page.Transitions.Count.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        sb.Append("};\n");

        return new Emission(fileName, sb.ToString());
    }

    /// <summary>
    /// Emit a standalone C test executable asserting every transition's
    /// shape against the YAML transcription. Mirrors the Go
    /// <c>EmitStatePageTests</c>: page-level source/count check plus
    /// one assertion block per transition checking id/on/next/guard
    /// plus every action's verb + kind. Each block returns 1 on
    /// failure; <c>main</c> ORs them together and returns the
    /// combined rc so ctest sees a failing exit code.
    /// </summary>
    public static Emission EmitStatePageTests(ResolvedPage page)
    {
        var stem = AnnexC.OutputStem(page.Machine, page.SourcePath);
        var symbol = page.Machine + "_" + Snake(page.State);
        var fileName = stem + ".g.test.c";

        var sb = new StringBuilder();
        EmitHeader(sb, page.SourcePath);
        sb.Append("#include \"ax25sdl.h\"\n");
        sb.Append("#include <stdio.h>\n");
        sb.Append("#include <string.h>\n\n");
        sb.Append("extern const StatePage ").Append(symbol).Append(";\n\n");

        sb.Append("#define ASSERT(cond, msg) do { if (!(cond)) { fprintf(stderr, \"FAIL: %s\\n\", msg); return 1; } } while (0)\n");
        sb.Append("#define ASSERT_STREQ(a, b, msg) ASSERT(strcmp((a), (b)) == 0, msg)\n\n");

        // Page-level: source figure + transition count.
        sb.Append("static int test_source_figure(void) {\n");
        sb.Append("    ASSERT_STREQ(").Append(symbol).Append(".source.figure, ")
          .Append(CStringLiteral(page.SourceFigure)).Append(", \"source.figure\");\n");
        sb.Append("    return 0;\n");
        sb.Append("}\n\n");

        sb.Append("static int test_transitions_count(void) {\n");
        sb.Append("    ASSERT(").Append(symbol).Append(".transitions_len == ")
          .Append(page.Transitions.Count.ToString(CultureInfo.InvariantCulture))
          .Append(", \"transitions count\");\n");
        sb.Append("    return 0;\n");
        sb.Append("}\n\n");

        // One static helper per transition. Transition IDs are
        // snake-case identifiers per the schema, so concatenation
        // gives a valid C function name directly.
        foreach (var t in page.Transitions)
        {
            sb.Append("static int test_").Append(t.Id).Append("(void) {\n");
            sb.Append("    const TransitionSpec* t = NULL;\n");
            sb.Append("    for (size_t i = 0; i < ").Append(symbol).Append(".transitions_len; i++) {\n");
            sb.Append("        if (strcmp(").Append(symbol).Append(".transitions[i].id, ")
              .Append(CStringLiteral(t.Id)).Append(") == 0) {\n");
            sb.Append("            t = &").Append(symbol).Append(".transitions[i];\n");
            sb.Append("            break;\n");
            sb.Append("        }\n");
            sb.Append("    }\n");
            sb.Append("    ASSERT(t != NULL, \"").Append(t.Id).Append(" not found\");\n");
            sb.Append("    ASSERT_STREQ(t->on, ").Append(CStringLiteral(t.On)).Append(", \"on\");\n");
            sb.Append("    ASSERT_STREQ(t->next, ").Append(CStringLiteral(t.Next)).Append(", \"next\");\n");
            if (!string.IsNullOrEmpty(t.Guard))
            {
                sb.Append("    ASSERT_STREQ(t->guard, ").Append(CStringLiteral(t.Guard!)).Append(", \"guard\");\n");
            }
            sb.Append("    ASSERT(t->actions_len == ").Append(t.Actions.Count.ToString(CultureInfo.InvariantCulture))
              .Append(", \"actions count\");\n");
            for (int i = 0; i < t.Actions.Count; i++)
            {
                var a = t.Actions[i];
                var idx = i.ToString(CultureInfo.InvariantCulture);
                sb.Append("    ASSERT_STREQ(t->actions[").Append(idx).Append("].verb, ")
                  .Append(CStringLiteral(a.Verb)).Append(", \"actions[").Append(idx).Append("].verb\");\n");
                sb.Append("    ASSERT(t->actions[").Append(idx).Append("].kind == ")
                  .Append(CKindLiteral(a.Kind)).Append(", \"actions[").Append(idx).Append("].kind\");\n");
            }
            sb.Append("    return 0;\n");
            sb.Append("}\n\n");
        }

        sb.Append("int main(void) {\n");
        sb.Append("    int rc = 0;\n");
        sb.Append("    rc |= test_source_figure();\n");
        sb.Append("    rc |= test_transitions_count();\n");
        foreach (var t in page.Transitions)
        {
            sb.Append("    rc |= test_").Append(t.Id).Append("();\n");
        }
        sb.Append("    return rc;\n");
        sb.Append("}\n");

        return new Emission(fileName, sb.ToString());
    }

    public static Emission EmitSubroutinePage(ResolvedSubroutinesPage page)
    {
        var fileStem = Path.GetFileNameWithoutExtension(page.SourcePath)
            .Replace(".sdl", string.Empty, StringComparison.Ordinal);
        var symbol = page.Machine + "_" + Snake(fileStem);
        var fileName = AnnexC.OutputStem(page.Machine, page.SourcePath) + ".g.c";

        var sb = new StringBuilder();
        EmitHeader(sb, page.SourcePath);
        sb.Append("#include \"ax25sdl.h\"\n\n");

        // Per-path statics (actions / references / loops) and
        // per-subroutine references statics. Path symbols are
        // qualified with the subroutine name to keep them globally
        // unique within the file.
        foreach (var s in page.Subroutines)
        {
            var subSymbol = symbol + "_" + Snake(s.Name);
            EmitReferencesStatic(sb, subSymbol + "_references", s.References);
            foreach (var p in s.Paths)
            {
                var pathPrefix = subSymbol + "_" + p.Id;
                EmitActionsStatic(sb, pathPrefix + "_actions", p.Actions);
                EmitReferencesStatic(sb, pathPrefix + "_references", p.References);
                EmitLoopsStatic(sb, pathPrefix + "_loops", p.Loops);
            }
        }

        // Per-subroutine paths array.
        foreach (var s in page.Subroutines)
        {
            var subSymbol = symbol + "_" + Snake(s.Name);
            sb.Append("static const SubroutinePath ").Append(subSymbol).Append("_paths[] = {\n");
            foreach (var p in s.Paths)
            {
                var pathPrefix = subSymbol + "_" + p.Id;
                sb.Append("    {\n");
                sb.Append("        .id = ").Append(CStringLiteral(p.Id)).Append(",\n");
                sb.Append("        .guard = ").Append(CStringLiteral(p.Guard ?? "")).Append(",\n");
                EmitListField(sb, "actions", pathPrefix + "_actions", p.Actions.Count);
                sb.Append("        .notes = ").Append(CStringLiteral(p.Notes ?? "")).Append(",\n");
                EmitListField(sb, "references", pathPrefix + "_references", p.References.Count);
                EmitListField(sb, "loops", pathPrefix + "_loops", p.Loops.Count);
                sb.Append("    },\n");
            }
            sb.Append("};\n\n");
        }

        // Outer SubroutineSpec[] referencing each subroutine's paths array.
        sb.Append("static const SubroutineSpec ").Append(symbol).Append("_subroutines[] = {\n");
        foreach (var s in page.Subroutines)
        {
            var subSymbol = symbol + "_" + Snake(s.Name);
            sb.Append("    {\n");
            sb.Append("        .name = ").Append(CStringLiteral(s.Name)).Append(",\n");
            sb.Append("        .paths = ").Append(subSymbol).Append("_paths,\n");
            sb.Append("        .paths_len = ").Append(s.Paths.Count.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("        .notes = ").Append(CStringLiteral(s.Notes ?? "")).Append(",\n");
            EmitListField(sb, "references", subSymbol + "_references", s.References.Count);
            sb.Append("    },\n");
        }
        sb.Append("};\n\n");

        sb.Append("const SubroutinesPage ").Append(symbol).Append(" = {\n");
        sb.Append("    .machine = ").Append(CStringLiteral(page.Machine)).Append(",\n");
        sb.Append("    .source = { .spec = ").Append(CStringLiteral(page.SourceSpec))
          .Append(", .figure = ").Append(CStringLiteral(page.SourceFigure))
          .Append(", .url = ").Append(CStringLiteral(page.SourceUrl ?? "")).Append(" },\n");
        sb.Append("    .subroutines = ").Append(symbol).Append("_subroutines,\n");
        sb.Append("    .subroutines_len = ").Append(page.Subroutines.Count.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        sb.Append("};\n");

        return new Emission(fileName, sb.ToString());
    }

    /// <summary>
    /// Build the master generated header that <c>extern</c>-declares
    /// every page symbol. Lets test sources and consumers pull in one
    /// header rather than tracking per-file externs. Mirrors the
    /// C equivalent of TS's <c>index.ts</c>.
    /// </summary>
    public static string EmitHeader(IEnumerable<ResolvedPage> pages, IEnumerable<ResolvedSubroutinesPage> subPages)
    {
        var sb = new StringBuilder();
        sb.Append("// Code generated by codegen/src/Packet.Sdl.CodeGen. DO NOT EDIT.\n");
        sb.Append("// Re-exports every SDL page as `extern const` so consumers can\n");
        sb.Append("// `#include \"ax25sdl.g.h\"` and link against the library.\n\n");
        sb.Append("#ifndef AX25SDL_GENERATED_H\n");
        sb.Append("#define AX25SDL_GENERATED_H\n\n");
        sb.Append("#include \"ax25sdl.h\"\n\n");
        foreach (var page in pages.OrderBy(p => p.SourcePath, StringComparer.Ordinal))
        {
            var symbol = page.Machine + "_" + Snake(page.State);
            sb.Append("extern const StatePage ").Append(symbol).Append(";\n");
        }
        foreach (var page in subPages.OrderBy(p => p.SourcePath, StringComparer.Ordinal))
        {
            var fileStem = Path.GetFileNameWithoutExtension(page.SourcePath)
                .Replace(".sdl", string.Empty, StringComparison.Ordinal);
            var symbol = page.Machine + "_" + Snake(fileStem);
            sb.Append("extern const SubroutinesPage ").Append(symbol).Append(";\n");
        }
        sb.Append("\n#endif // AX25SDL_GENERATED_H\n");
        return sb.ToString();
    }

    // ─── Formatting helpers ───────────────────────────────────────────

    private static void EmitHeader(StringBuilder sb, string sourcePath)
    {
        sb.Append("// Code generated by codegen/src/Packet.Sdl.CodeGen from ").Append(sourcePath.Replace('\\', '/')).Append(".\n");
        sb.Append("// DO NOT EDIT. Run `dotnet run --project codegen/src/Packet.Sdl.CodeGen` to regenerate.\n\n");
    }

    private static void EmitActionsStatic(StringBuilder sb, string name, IReadOnlyList<ResolvedAction> actions)
    {
        // ISO C forbids zero-length array statics; skip and let the
        // containing struct point at NULL + 0 instead.
        if (actions.Count == 0) return;
        sb.Append("static const ActionStep ").Append(name).Append("[] = {\n");
        foreach (var a in actions)
        {
            sb.Append("    { .verb = ").Append(CStringLiteral(a.Verb))
              .Append(", .kind = ").Append(CKindLiteral(a.Kind)).Append(" },\n");
        }
        sb.Append("};\n\n");
    }

    private static void EmitReferencesStatic(StringBuilder sb, string name, IReadOnlyList<ResolvedReference> refs)
    {
        if (refs.Count == 0) return;
        sb.Append("static const ImplementationReference ").Append(name).Append("[] = {\n");
        foreach (var r in refs)
        {
            sb.Append("    { .source = ").Append(CStringLiteral(r.Source))
              .Append(", .cite = ").Append(CStringLiteral(r.Cite ?? ""))
              .Append(", .quote = ").Append(CStringLiteral(r.Quote ?? ""))
              .Append(", .path = ").Append(CStringLiteral(r.Path ?? ""))
              .Append(", .function = ").Append(CStringLiteral(r.Function ?? ""))
              .Append(", .line = ").Append((r.Line ?? 0).ToString(CultureInfo.InvariantCulture))
              .Append(", .note = ").Append(CStringLiteral(r.Note ?? ""))
              .Append(" },\n");
        }
        sb.Append("};\n\n");
    }

    private static void EmitLoopsStatic(StringBuilder sb, string name, IReadOnlyList<ResolvedLoop> loops)
    {
        if (loops.Count == 0) return;
        sb.Append("static const LoopRange ").Append(name).Append("[] = {\n");
        foreach (var l in loops)
        {
            sb.Append("    { .start = ").Append(l.Start.ToString(CultureInfo.InvariantCulture))
              .Append(", .length = ").Append(l.Length.ToString(CultureInfo.InvariantCulture))
              .Append(", .predicate = ").Append(CStringLiteral(l.Predicate))
              .Append(", .test_at_end = ").Append(l.TestAtEnd ? "true" : "false")
              .Append(" },\n");
        }
        sb.Append("};\n\n");
    }

    /// <summary>
    /// Emit a paired <c>.field = ptr, .field_len = N,</c> line. When
    /// <paramref name="count"/> is zero we point at <c>NULL</c> instead
    /// of an unnamed static — matches the no-zero-length-array policy
    /// in the EmitXxxStatic helpers above.
    /// </summary>
    private static void EmitListField(StringBuilder sb, string field, string arraySymbol, int count)
    {
        sb.Append("        .").Append(field).Append(" = ");
        sb.Append(count == 0 ? "NULL" : arraySymbol);
        sb.Append(",\n");
        sb.Append("        .").Append(field).Append("_len = ")
          .Append(count.ToString(CultureInfo.InvariantCulture)).Append(",\n");
    }

    /// <summary>
    /// C string literal — double-quoted with backslash escapes. Control
    /// bytes &lt; 0x20 emit as <c>\xHH</c> hex escapes; this is portable
    /// across compilers and avoids the "C parser greedy-consumes hex
    /// digits" trap by limiting the run to two digits.
    /// </summary>
    internal static string CStringLiteral(string s)
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
                    if (c < 0x20)
                        sb.AppendFormat(CultureInfo.InvariantCulture, "\\x{0:x2}\"\"", (int)c);
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// All-caps snake prefix avoids collision with anything else
    /// in the runtime header. Mirrors the kind enum declared in
    /// <c>spec/c/include/ax25sdl.h</c>.
    /// </summary>
    internal static string CKindLiteral(ResolvedActionKind kind) => kind switch
    {
        ResolvedActionKind.SignalUpper => "AX25SDL_KIND_SIGNAL_UPPER",
        ResolvedActionKind.SignalLower => "AX25SDL_KIND_SIGNAL_LOWER",
        ResolvedActionKind.Processing  => "AX25SDL_KIND_PROCESSING",
        ResolvedActionKind.Subroutine  => "AX25SDL_KIND_SUBROUTINE",
        ResolvedActionKind.InternalOut => "AX25SDL_KIND_INTERNAL_OUT",
        _ => throw new InvalidOperationException($"unknown action kind '{kind}'"),
    };

    /// <summary>
    /// PascalCase / mixedCase → snake_case. Idempotent on already-snake
    /// input (no consecutive underscores produced). Used to coin C
    /// symbol names from the YAML's PascalCase state names (e.g.
    /// <c>AwaitingConnection22</c> → <c>awaiting_connection22</c>).
    /// </summary>
    internal static string Snake(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length + 4);
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsUpper(c))
            {
                // Insert separator before an uppercase letter unless we're
                // at the start of the string or the previous character was
                // already a separator (e.g. '_' or '-' or a digit run end).
                if (i > 0 && sb.Length > 0 && sb[sb.Length - 1] != '_')
                {
                    sb.Append('_');
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (c == '-' || c == ' ' || c == '.')
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
