using System.Globalization;
using System.Text;
using Packet.Sdl.IR;

namespace Packet.Sdl.CodeGen.Go;

/// <summary>
/// Go emitter for the resolved SDL IR. Produces one <c>.g.go</c> file
/// per page in the <c>ax25sdl</c> package. Hand-rolled string emission
/// (no template engine) — Go's strict gofmt rules make a generator
/// without a templating layer significantly simpler than the C# /
/// Scriban path.
/// </summary>
public static class GoEmitter
{
    public sealed record Emission(string FileName, string Content);

    public static Emission EmitStatePage(ResolvedPage page)
    {
        // Variable name: the YAML's `state:` is already PascalCase (e.g.
        // AwaitingConnection22), so concatenating with a Pascal-of-machine
        // produces a clean Go identifier without re-casing.
        var varName = Pascal(page.Machine) + page.State;

        // File name: derive from the source file stem so the on-disk
        // layout stays snake_case regardless of how the state is named
        // in the YAML.
        var stem = AnnexC.OutputStem(page.Machine, page.SourcePath);
        var fileName = stem + ".g.go";

        var sb = new StringBuilder();
        EmitHeader(sb, page.SourcePath);
        sb.Append("package ax25sdl\n\n");
        sb.Append("// ").Append(varName).Append(" holds the SDL transitions for the\n");
        sb.Append("// ").Append(page.State).Append(" state of the ").Append(page.Machine).Append(" machine. Source: ");
        sb.Append(page.SourceSpec).Append(", figure ").Append(page.SourceFigure).Append(".\n");
        sb.Append("var ").Append(varName).Append(" = StatePage{\n");
        sb.Append("\tMachine: ").Append(GoStringLiteral(page.Machine)).Append(",\n");
        sb.Append("\tState:   ").Append(GoStringLiteral(page.State)).Append(",\n");
        sb.Append("\tSource:  SdlSource{Spec: ").Append(GoStringLiteral(page.SourceSpec))
          .Append(", Figure: ").Append(GoStringLiteral(page.SourceFigure))
          .Append(", URL: ").Append(GoStringLiteral(page.SourceUrl ?? "")).Append("},\n");
        sb.Append("\tTransitions: []TransitionSpec{\n");
        foreach (var t in page.Transitions)
        {
            sb.Append("\t\t{\n");
            sb.Append("\t\t\tID:    ").Append(GoStringLiteral(t.Id)).Append(",\n");
            sb.Append("\t\t\tFrom:  ").Append(GoStringLiteral(page.State)).Append(",\n");
            sb.Append("\t\t\tOn:    ").Append(GoStringLiteral(t.On)).Append(",\n");
            sb.Append("\t\t\tGuard: ").Append(GoStringLiteral(t.Guard ?? "")).Append(",\n");
            sb.Append("\t\t\tActions: ").Append(FormatActions(t.Actions, 3)).Append(",\n");
            sb.Append("\t\t\tNext:  ").Append(GoStringLiteral(t.Next)).Append(",\n");
            sb.Append("\t\t\tNotes: ").Append(GoStringLiteral(t.Notes ?? "")).Append(",\n");
            sb.Append("\t\t\tReferences: ").Append(FormatReferences(t.References, 3)).Append(",\n");
            sb.Append("\t\t\tLoops: ").Append(FormatLoops(t.Loops, 3)).Append(",\n");
            sb.Append("\t\t},\n");
        }
        sb.Append("\t},\n");
        sb.Append("}\n");

        return new Emission(fileName, sb.ToString());
    }

    /// <summary>
    /// Emit a Go test file asserting every transition's shape against
    /// the YAML transcription. Mirrors the C# <c>tests.scriban-cs</c>
    /// template: one test per transition checking id/on/next/guard plus
    /// every action's verb + kind. Catches emitter regressions the
    /// hand-written smoke tests would miss (e.g. a guard silently
    /// dropped, or an action routed through the wrong kind).
    /// </summary>
    public static Emission EmitStatePageTests(ResolvedPage page)
    {
        var varName = Pascal(page.Machine) + page.State;
        var stem = AnnexC.OutputStem(page.Machine, page.SourcePath);
        var fileName = stem + ".g_test.go";

        var sb = new StringBuilder();
        EmitHeader(sb, page.SourcePath);
        sb.Append("package ax25sdl\n\n");
        sb.Append("import \"testing\"\n\n");

        // Page-level: source figure + transition count.
        sb.Append("func Test").Append(varName).Append("_SourceFigure(t *testing.T) {\n");
        sb.Append("\tif got := ").Append(varName).Append(".Source.Figure; got != ")
          .Append(GoStringLiteral(page.SourceFigure)).Append(" {\n");
        sb.Append("\t\tt.Errorf(\"Source.Figure = %q, want ").Append(page.SourceFigure).Append("\", got)\n");
        sb.Append("\t}\n");
        sb.Append("}\n\n");

        sb.Append("func Test").Append(varName).Append("_TransitionsArePresent(t *testing.T) {\n");
        sb.Append("\tif got := len(").Append(varName).Append(".Transitions); got != ")
          .Append(page.Transitions.Count.ToString(CultureInfo.InvariantCulture)).Append(" {\n");
        sb.Append("\t\tt.Errorf(\"len(Transitions) = %d, want ")
          .Append(page.Transitions.Count.ToString(CultureInfo.InvariantCulture)).Append("\", got)\n");
        sb.Append("\t}\n");
        sb.Append("}\n\n");

        // One test per transition. Transition IDs from the YAML are
        // already valid Go identifier suffixes (letter-prefixed alnum
        // / underscore — the schema enforces it).
        foreach (var t in page.Transitions)
        {
            sb.Append("func Test").Append(varName).Append('_').Append(t.Id).Append("(t *testing.T) {\n");
            sb.Append("\tvar tx *TransitionSpec\n");
            sb.Append("\tfor i := range ").Append(varName).Append(".Transitions {\n");
            sb.Append("\t\tif ").Append(varName).Append(".Transitions[i].ID == ")
              .Append(GoStringLiteral(t.Id)).Append(" {\n");
            sb.Append("\t\t\ttx = &").Append(varName).Append(".Transitions[i]\n");
            sb.Append("\t\t\tbreak\n");
            sb.Append("\t\t}\n");
            sb.Append("\t}\n");
            sb.Append("\tif tx == nil {\n");
            sb.Append("\t\tt.Fatalf(\"transition ").Append(t.Id).Append(" not found\")\n");
            sb.Append("\t}\n");

            sb.Append("\tif tx.On != ").Append(GoStringLiteral(t.On)).Append(" {\n");
            sb.Append("\t\tt.Errorf(\"On = %q, want ").Append(t.On).Append("\", tx.On)\n");
            sb.Append("\t}\n");
            sb.Append("\tif tx.Next != ").Append(GoStringLiteral(t.Next)).Append(" {\n");
            sb.Append("\t\tt.Errorf(\"Next = %q, want ").Append(t.Next).Append("\", tx.Next)\n");
            sb.Append("\t}\n");
            if (!string.IsNullOrEmpty(t.Guard))
            {
                sb.Append("\tif tx.Guard != ").Append(GoStringLiteral(t.Guard!)).Append(" {\n");
                sb.Append("\t\tt.Errorf(\"Guard = %q, want %q\", tx.Guard, ").Append(GoStringLiteral(t.Guard!)).Append(")\n");
                sb.Append("\t}\n");
            }
            sb.Append("\tif got := len(tx.Actions); got != ")
              .Append(t.Actions.Count.ToString(CultureInfo.InvariantCulture)).Append(" {\n");
            sb.Append("\t\tt.Fatalf(\"len(Actions) = %d, want ")
              .Append(t.Actions.Count.ToString(CultureInfo.InvariantCulture)).Append("\", got)\n");
            sb.Append("\t}\n");
            for (int i = 0; i < t.Actions.Count; i++)
            {
                var a = t.Actions[i];
                var idx = i.ToString(CultureInfo.InvariantCulture);
                sb.Append("\tif tx.Actions[").Append(idx).Append("].Verb != ")
                  .Append(GoStringLiteral(a.Verb)).Append(" {\n");
                sb.Append("\t\tt.Errorf(\"Actions[").Append(idx).Append("].Verb = %q, want %q\", tx.Actions[")
                  .Append(idx).Append("].Verb, ").Append(GoStringLiteral(a.Verb)).Append(")\n");
                sb.Append("\t}\n");
                sb.Append("\tif tx.Actions[").Append(idx).Append("].Kind != ")
                  .Append(GoKindLiteral(a.Kind)).Append(" {\n");
                sb.Append("\t\tt.Errorf(\"Actions[").Append(idx).Append("].Kind = %v, want ")
                  .Append(GoKindLiteral(a.Kind)).Append("\", tx.Actions[").Append(idx).Append("].Kind)\n");
                sb.Append("\t}\n");
            }
            sb.Append("}\n\n");
        }

        return new Emission(fileName, sb.ToString());
    }

    public static Emission EmitSubroutinePage(ResolvedSubroutinesPage page)
    {
        var fileStem = Path.GetFileNameWithoutExtension(page.SourcePath)
            .Replace(".sdl", string.Empty, StringComparison.Ordinal);
        var varName = Pascal(page.Machine) + Pascal(fileStem);
        var fileName = AnnexC.OutputStem(page.Machine, page.SourcePath) + ".g.go";

        var sb = new StringBuilder();
        EmitHeader(sb, page.SourcePath);
        sb.Append("package ax25sdl\n\n");
        sb.Append("// ").Append(varName).Append(" holds the SDL subroutines for the\n");
        sb.Append("// ").Append(page.Machine).Append(" machine. Source: ");
        sb.Append(page.SourceSpec).Append(", figure ").Append(page.SourceFigure).Append(".\n");
        sb.Append("var ").Append(varName).Append(" = SubroutinesPage{\n");
        sb.Append("\tMachine: ").Append(GoStringLiteral(page.Machine)).Append(",\n");
        sb.Append("\tSource:  SdlSource{Spec: ").Append(GoStringLiteral(page.SourceSpec))
          .Append(", Figure: ").Append(GoStringLiteral(page.SourceFigure))
          .Append(", URL: ").Append(GoStringLiteral(page.SourceUrl ?? "")).Append("},\n");
        sb.Append("\tSubroutines: []SubroutineSpec{\n");
        foreach (var s in page.Subroutines)
        {
            sb.Append("\t\t{\n");
            sb.Append("\t\t\tName: ").Append(GoStringLiteral(s.Name)).Append(",\n");
            sb.Append("\t\t\tPaths: []SubroutinePath{\n");
            foreach (var p in s.Paths)
            {
                sb.Append("\t\t\t\t{\n");
                sb.Append("\t\t\t\t\tID:    ").Append(GoStringLiteral(p.Id)).Append(",\n");
                sb.Append("\t\t\t\t\tGuard: ").Append(GoStringLiteral(p.Guard ?? "")).Append(",\n");
                sb.Append("\t\t\t\t\tActions: ").Append(FormatActions(p.Actions, 5)).Append(",\n");
                sb.Append("\t\t\t\t\tNotes: ").Append(GoStringLiteral(p.Notes ?? "")).Append(",\n");
                sb.Append("\t\t\t\t\tReferences: ").Append(FormatReferences(p.References, 5)).Append(",\n");
                sb.Append("\t\t\t\t\tLoops: ").Append(FormatLoops(p.Loops, 5)).Append(",\n");
                sb.Append("\t\t\t\t},\n");
            }
            sb.Append("\t\t\t},\n");
            sb.Append("\t\t\tNotes: ").Append(GoStringLiteral(s.Notes ?? "")).Append(",\n");
            sb.Append("\t\t\tReferences: ").Append(FormatReferences(s.References, 3)).Append(",\n");
            sb.Append("\t\t},\n");
        }
        sb.Append("\t},\n");
        sb.Append("}\n");

        return new Emission(fileName, sb.ToString());
    }

    // ─── Formatting helpers ───────────────────────────────────────────

    private static void EmitHeader(StringBuilder sb, string sourcePath)
    {
        sb.Append("// Code generated by codegen/src/Packet.Sdl.CodeGen from ").Append(sourcePath.Replace('\\', '/')).Append(".\n");
        sb.Append("// DO NOT EDIT. Run `dotnet run --project codegen/src/Packet.Sdl.CodeGen` to regenerate.\n\n");
    }

    /// <summary>
    /// <paramref name="parentTabs"/> is the indentation depth of the
    /// surrounding <c>Field: []ActionStep{</c> line. Entries land at
    /// <c>parentTabs + 1</c>; the closing brace matches
    /// <paramref name="parentTabs"/>. Matches gofmt's expectation that
    /// closing braces align with the line that opened them.
    /// </summary>
    private static string FormatActions(IReadOnlyList<ResolvedAction> actions, int parentTabs)
    {
        if (actions.Count == 0) return "[]ActionStep{}";
        var indent = new string('\t', parentTabs + 1);
        var closer = new string('\t', parentTabs);
        var sb = new StringBuilder();
        sb.Append("[]ActionStep{\n");
        foreach (var a in actions)
        {
            sb.Append(indent).Append("{Verb: ").Append(GoStringLiteral(a.Verb))
              .Append(", Kind: ").Append(GoKindLiteral(a.Kind)).Append("},\n");
        }
        sb.Append(closer).Append('}');
        return sb.ToString();
    }

    private static string FormatReferences(IReadOnlyList<ResolvedReference> refs, int parentTabs)
    {
        if (refs.Count == 0) return "[]ImplementationReference{}";
        var indent = new string('\t', parentTabs + 1);
        var closer = new string('\t', parentTabs);
        var sb = new StringBuilder();
        sb.Append("[]ImplementationReference{\n");
        foreach (var r in refs)
        {
            sb.Append(indent).Append("{Source: ").Append(GoStringLiteral(r.Source))
              .Append(", Cite: ").Append(GoStringLiteral(r.Cite ?? ""))
              .Append(", Quote: ").Append(GoStringLiteral(r.Quote ?? ""))
              .Append(", Path: ").Append(GoStringLiteral(r.Path ?? ""))
              .Append(", Function: ").Append(GoStringLiteral(r.Function ?? ""))
              .Append(", Line: ").Append((r.Line ?? 0).ToString(CultureInfo.InvariantCulture))
              .Append(", Note: ").Append(GoStringLiteral(r.Note ?? ""))
              .Append("},\n");
        }
        sb.Append(closer).Append('}');
        return sb.ToString();
    }

    private static string FormatLoops(IReadOnlyList<ResolvedLoop> loops, int parentTabs)
    {
        if (loops.Count == 0) return "[]LoopRange{}";
        var indent = new string('\t', parentTabs + 1);
        var closer = new string('\t', parentTabs);
        var sb = new StringBuilder();
        sb.Append("[]LoopRange{\n");
        foreach (var l in loops)
        {
            sb.Append(indent).Append("{Start: ").Append(l.Start.ToString(CultureInfo.InvariantCulture))
              .Append(", Length: ").Append(l.Length.ToString(CultureInfo.InvariantCulture))
              .Append(", Predicate: ").Append(GoStringLiteral(l.Predicate))
              .Append(", TestAtEnd: ").Append(l.TestAtEnd ? "true" : "false")
              .Append("},\n");
        }
        sb.Append(closer).Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// Go string literal — double-quoted with escaping for the small set
    /// of characters Go interprets specially. We deliberately don't use
    /// raw (backtick) strings because predicates and notes can contain
    /// backticks themselves in principle.
    /// </summary>
    internal static string GoStringLiteral(string s)
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
                        sb.AppendFormat(CultureInfo.InvariantCulture, "\\x{0:x2}", (int)c);
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    internal static string GoKindLiteral(ResolvedActionKind kind) => kind switch
    {
        ResolvedActionKind.SignalUpper => "SignalUpper",
        ResolvedActionKind.SignalLower => "SignalLower",
        ResolvedActionKind.Processing  => "Processing",
        ResolvedActionKind.Subroutine  => "Subroutine",
        ResolvedActionKind.InternalOut => "InternalOut",
        _ => throw new InvalidOperationException($"unknown action kind '{kind}'"),
    };

    /// <summary>snake_case → PascalCase, preserving non-alphanumeric runs.</summary>
    internal static string Pascal(string snake)
    {
        var parts = snake.Split('_');
        return string.Concat(parts.Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
