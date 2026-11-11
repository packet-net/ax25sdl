using System.Globalization;
using System.Text;
using Packet.Sdl.IR;

namespace Packet.Sdl.CodeGen.Python;

/// <summary>
/// Python emitter for the resolved SDL IR. Produces one
/// <c>.g.py</c> file per page in the <c>ax25sdl</c> Python package,
/// plus a <c>__init__.py</c> that re-exports the page constants.
/// Hand-rolled string emission (no template engine); the output is
/// designed to pass <c>ruff check</c> + <c>ruff format --check</c>
/// at <c>line-length=100</c> with <c>E501</c> ignored.
/// </summary>
/// <remarks>
/// <para>
/// File-naming convention: state-page data files are
/// <c>&lt;stem&gt;.g.py</c>, but the matching pytest file is
/// <c>&lt;stem&gt;_g_test.py</c> (underscore, not dot) because
/// Python's import system requires module names to be valid
/// identifiers — and pytest discovers <c>*_test.py</c>. The
/// orchestrator cleans <c>*_g_test.py</c> as a separate sweep.
/// </para>
/// <para>
/// Empty-string sentinels mirror the TS emitter: a missing guard /
/// note / URL becomes <c>""</c>; a missing line number becomes
/// <c>0</c>. Consumers treat empty as absence.
/// </para>
/// </remarks>
public static class PythonEmitter
{
    public sealed record Emission(string FileName, string Content);

    public static Emission EmitStatePage(ResolvedPage page)
    {
        var stem = AnnexC.OutputStem(page.Machine, page.SourcePath);
        var constName = ScreamingSnake(page.Machine) + "_" + ScreamingSnake(SnakeFromState(page.State));
        var fileName = stem + ".g.py";

        bool hasActions = page.Transitions.Any(t => t.Actions.Count > 0);
        bool hasRefs    = page.Transitions.Any(t => t.References.Count > 0);
        bool hasLoops   = page.Transitions.Any(t => t.Loops.Count > 0);

        var sb = new StringBuilder();
        EmitHeader(sb, page.SourcePath);
        // Imports are conditional on actual use to satisfy ruff F401.
        // StatePage / SdlSource / TransitionSpec are always present;
        // ActionKind / ActionStep / ImplementationReference / LoopRange
        // only appear if at least one transition has the relevant
        // sub-record.
        EmitTypeImports(sb,
            "ActionKind", hasActions,
            "ActionStep", hasActions,
            "ImplementationReference", hasRefs,
            "LoopRange", hasLoops,
            "SdlSource", true,
            "StatePage", true,
            "TransitionSpec", true);
        sb.Append("\n");
        sb.Append(constName).Append(" = StatePage(\n");
        sb.Append("    machine=").Append(PyStringLiteral(page.Machine)).Append(",\n");
        sb.Append("    state=").Append(PyStringLiteral(page.State)).Append(",\n");
        sb.Append("    source=SdlSource(\n");
        sb.Append("        spec=").Append(PyStringLiteral(page.SourceSpec)).Append(",\n");
        sb.Append("        figure=").Append(PyStringLiteral(page.SourceFigure)).Append(",\n");
        sb.Append("        url=").Append(PyStringLiteral(page.SourceUrl ?? "")).Append(",\n");
        sb.Append("    ),\n");
        sb.Append("    transitions=(\n");
        foreach (var t in page.Transitions)
        {
            sb.Append("        TransitionSpec(\n");
            sb.Append("            id=").Append(PyStringLiteral(t.Id)).Append(",\n");
            sb.Append("            from_=").Append(PyStringLiteral(page.State)).Append(",\n");
            sb.Append("            on=").Append(PyStringLiteral(t.On)).Append(",\n");
            sb.Append("            guard=").Append(PyStringLiteral(t.Guard ?? "")).Append(",\n");
            sb.Append("            actions=").Append(FormatActions(t.Actions, 3)).Append(",\n");
            sb.Append("            next=").Append(PyStringLiteral(t.Next)).Append(",\n");
            sb.Append("            notes=").Append(PyStringLiteral(t.Notes ?? "")).Append(",\n");
            sb.Append("            references=").Append(FormatReferences(t.References, 3)).Append(",\n");
            sb.Append("            loops=").Append(FormatLoops(t.Loops, 3)).Append(",\n");
            sb.Append("        ),\n");
        }
        sb.Append("    ),\n");
        sb.Append(")\n");

        return new Emission(fileName, sb.ToString());
    }

    /// <summary>
    /// Emit a pytest test file asserting every transition's shape
    /// against the YAML transcription. Mirrors the TS emitter's
    /// <c>describe</c>/<c>it</c> per-transition pattern as
    /// individual <c>test_*</c> functions: one for the page-level
    /// source figure, one for the transition count, one per
    /// transition checking id/on/next/guard plus every action's
    /// verb + kind.
    /// </summary>
    public static Emission EmitStatePageTests(ResolvedPage page)
    {
        var stem = AnnexC.OutputStem(page.Machine, page.SourcePath);
        var constName = ScreamingSnake(page.Machine) + "_" + ScreamingSnake(SnakeFromState(page.State));
        var fileName = stem + "_g_test.py";

        bool needsActionKind = page.Transitions.Any(t => t.Actions.Count > 0);

        var sb = new StringBuilder();
        EmitHeader(sb, page.SourcePath);
        // The data file is `<stem>.g.py` whose would-be Python module
        // name (`<stem>.g`) is not a valid identifier. The package
        // `__init__.py` resolves this via importlib and re-exports the
        // constant at the package level — so tests import the
        // constant from the package, not from a sibling module.
        sb.Append("from . import ").Append(constName).Append("\n");
        if (needsActionKind)
        {
            sb.Append("from .types import ActionKind\n");
        }
        sb.Append("\n\n");

        // Page-level: source figure + transition count.
        sb.Append("def test_source_figure() -> None:\n");
        sb.Append("    assert ").Append(constName).Append(".source.figure == ")
          .Append(PyStringLiteral(page.SourceFigure)).Append("\n\n\n");

        sb.Append("def test_transitions_are_present() -> None:\n");
        sb.Append("    assert len(").Append(constName).Append(".transitions) == ")
          .Append(page.Transitions.Count.ToString(CultureInfo.InvariantCulture)).Append("\n");

        foreach (var t in page.Transitions)
        {
            sb.Append("\n\n");
            sb.Append("def test_").Append(SanitiseFunctionName(t.Id)).Append("() -> None:\n");
            sb.Append("    t = next(\n");
            sb.Append("        (x for x in ").Append(constName).Append(".transitions if x.id == ")
              .Append(PyStringLiteral(t.Id)).Append("),\n");
            sb.Append("        None,\n");
            sb.Append("    )\n");
            sb.Append("    assert t is not None, ").Append(PyStringLiteral("transition " + t.Id + " not found")).Append("\n");
            sb.Append("    assert t.on == ").Append(PyStringLiteral(t.On)).Append("\n");
            sb.Append("    assert t.next == ").Append(PyStringLiteral(t.Next)).Append("\n");
            if (!string.IsNullOrEmpty(t.Guard))
            {
                sb.Append("    assert t.guard == ").Append(PyStringLiteral(t.Guard!)).Append("\n");
            }
            sb.Append("    assert len(t.actions) == ")
              .Append(t.Actions.Count.ToString(CultureInfo.InvariantCulture)).Append("\n");
            for (int i = 0; i < t.Actions.Count; i++)
            {
                var a = t.Actions[i];
                var idx = i.ToString(CultureInfo.InvariantCulture);
                sb.Append("    assert t.actions[").Append(idx).Append("].verb == ")
                  .Append(PyStringLiteral(a.Verb)).Append("\n");
                sb.Append("    assert t.actions[").Append(idx).Append("].kind == ")
                  .Append(PyKindLiteral(a.Kind)).Append("\n");
            }
        }
        sb.Append("\n");

        return new Emission(fileName, sb.ToString());
    }

    public static Emission EmitSubroutinePage(ResolvedSubroutinesPage page)
    {
        var stem = PageStem(page.SourcePath);
        var constName = ScreamingSnake(page.Machine) + "_" + ScreamingSnake(stem);
        var fileName = AnnexC.OutputStem(page.Machine, page.SourcePath) + ".g.py";

        bool hasActions = page.Subroutines.Any(s => s.Paths.Any(p => p.Actions.Count > 0));
        bool hasRefs = page.Subroutines.Any(s =>
            s.References.Count > 0 || s.Paths.Any(p => p.References.Count > 0));
        bool hasLoops = page.Subroutines.Any(s => s.Paths.Any(p => p.Loops.Count > 0));

        var sb = new StringBuilder();
        EmitHeader(sb, page.SourcePath);
        EmitTypeImports(sb,
            "ActionKind", hasActions,
            "ActionStep", hasActions,
            "ImplementationReference", hasRefs,
            "LoopRange", hasLoops,
            "SdlSource", true,
            "SubroutinePath", true,
            // Case-insensitive, to match ruff's isort (I001): SubroutinesPage
            // sorts before SubroutineSpec. EmitTypeImports emits in the order
            // given, so the caller is responsible for that ordering.
            "SubroutinesPage", true,
            "SubroutineSpec", true);
        sb.Append("\n");
        sb.Append(constName).Append(" = SubroutinesPage(\n");
        sb.Append("    machine=").Append(PyStringLiteral(page.Machine)).Append(",\n");
        sb.Append("    source=SdlSource(\n");
        sb.Append("        spec=").Append(PyStringLiteral(page.SourceSpec)).Append(",\n");
        sb.Append("        figure=").Append(PyStringLiteral(page.SourceFigure)).Append(",\n");
        sb.Append("        url=").Append(PyStringLiteral(page.SourceUrl ?? "")).Append(",\n");
        sb.Append("    ),\n");
        sb.Append("    subroutines=(\n");
        foreach (var s in page.Subroutines)
        {
            sb.Append("        SubroutineSpec(\n");
            sb.Append("            name=").Append(PyStringLiteral(s.Name)).Append(",\n");
            sb.Append("            paths=(\n");
            foreach (var p in s.Paths)
            {
                sb.Append("                SubroutinePath(\n");
                sb.Append("                    id=").Append(PyStringLiteral(p.Id)).Append(",\n");
                sb.Append("                    guard=").Append(PyStringLiteral(p.Guard ?? "")).Append(",\n");
                sb.Append("                    actions=").Append(FormatActions(p.Actions, 5)).Append(",\n");
                sb.Append("                    notes=").Append(PyStringLiteral(p.Notes ?? "")).Append(",\n");
                sb.Append("                    references=").Append(FormatReferences(p.References, 5)).Append(",\n");
                sb.Append("                    loops=").Append(FormatLoops(p.Loops, 5)).Append(",\n");
                sb.Append("                ),\n");
            }
            sb.Append("            ),\n");
            sb.Append("            notes=").Append(PyStringLiteral(s.Notes ?? "")).Append(",\n");
            sb.Append("            references=").Append(FormatReferences(s.References, 3)).Append(",\n");
            sb.Append("        ),\n");
        }
        sb.Append("    ),\n");
        sb.Append(")\n");

        return new Emission(fileName, sb.ToString());
    }

    /// <summary>
    /// Build the package <c>__init__.py</c> that re-exports the runtime
    /// types plus every generated page constant. Lets consumers
    /// <c>from ax25sdl import DATA_LINK_CONNECTED</c> without knowing
    /// which module the constant lives in.
    /// </summary>
    /// <remarks>
    /// Generated <c>.g.py</c> files end in a literal ".g" stem that is
    /// not a valid Python identifier — we use <c>importlib</c> at
    /// module-init time to load them by file-relative path, then
    /// re-export the constant under its canonical name.
    /// </remarks>
    public static string EmitInit(
        IEnumerable<ResolvedPage> pages,
        IEnumerable<ResolvedSubroutinesPage> subPages)
    {
        var orderedPages = pages.OrderBy(p => p.SourcePath, StringComparer.Ordinal).ToList();
        var orderedSubPages = subPages.OrderBy(p => p.SourcePath, StringComparer.Ordinal).ToList();

        var entries = new List<(string Stem, string ConstName)>();
        foreach (var p in orderedPages)
        {
            entries.Add((AnnexC.OutputStem(p.Machine, p.SourcePath),
                ScreamingSnake(p.Machine) + "_" + ScreamingSnake(SnakeFromState(p.State))));
        }
        foreach (var p in orderedSubPages)
        {
            entries.Add((AnnexC.OutputStem(p.Machine, p.SourcePath),
                ScreamingSnake(p.Machine) + "_" + ScreamingSnake(PageStem(p.SourcePath))));
        }

        var sb = new StringBuilder();
        sb.Append("# Code generated by codegen/src/Packet.Sdl.CodeGen. DO NOT EDIT.\n");
        sb.Append("# Re-exports every SDL page so consumers can `from ax25sdl import DATA_LINK_CONNECTED`.\n");
        sb.Append("\n");
        // The .g.py filenames embed a literal dot in the stem
        // (e.g. connected.g.py → would-be-module-name "connected.g"),
        // which is not a valid Python identifier. Load each generated
        // module via importlib's spec-from-file-location, then surface
        // its constant at the package level.
        sb.Append("import importlib.util as _importlib_util\n");
        sb.Append("from pathlib import Path as _Path\n");
        sb.Append("\n");
        sb.Append("from .types import (\n");
        sb.Append("    ActionKind,\n");
        sb.Append("    ActionStep,\n");
        sb.Append("    ImplementationReference,\n");
        sb.Append("    LoopRange,\n");
        sb.Append("    SdlSource,\n");
        sb.Append("    StatePage,\n");
        sb.Append("    SubroutinePath,\n");
        // ruff's isort (I001) orders import members case-insensitively, which puts
        // SubroutinesPage before SubroutineSpec. RUF022 sorts __all__ ordinally, which
        // orders those two the other way round. The difference below is deliberate.
        sb.Append("    SubroutinesPage,\n");
        sb.Append("    SubroutineSpec,\n");
        sb.Append("    TransitionSpec,\n");
        sb.Append(")\n");
        sb.Append("\n");
        sb.Append("\n");
        sb.Append("def _load(stem: str, attr: str) -> object:\n");
        sb.Append("    path = _Path(__file__).parent / f\"{stem}.g.py\"\n");
        sb.Append("    spec = _importlib_util.spec_from_file_location(f\"ax25sdl._gen_{stem}\", path)\n");
        sb.Append("    assert spec is not None and spec.loader is not None\n");
        sb.Append("    module = _importlib_util.module_from_spec(spec)\n");
        sb.Append("    spec.loader.exec_module(module)\n");
        sb.Append("    return getattr(module, attr)\n");
        sb.Append("\n");
        sb.Append("\n");
        foreach (var (stem, name) in entries)
        {
            sb.Append(name).Append(" = _load(").Append(PyStringLiteral(stem))
              .Append(", ").Append(PyStringLiteral(name)).Append(")\n");
        }
        sb.Append("\n");
        sb.Append("__all__ = [\n");
        // RUF022 wants __all__ in isort-style order: SCREAMING_SNAKE_CASE names first,
        // then CamelCase, each group sorted ordinally. The page constants are emitted
        // above in spec order because that order is meaningful for _load, so sort a
        // copy for __all__ rather than reordering the statements themselves.
        var exported = new List<string>();
        foreach (var (_, constName) in entries)
        {
            exported.Add(constName);
        }
        exported.Sort(StringComparer.Ordinal);
        foreach (var constName in exported)
        {
            sb.Append("    \"").Append(constName).Append("\",\n");
        }
        sb.Append("    \"ActionKind\",\n");
        sb.Append("    \"ActionStep\",\n");
        sb.Append("    \"ImplementationReference\",\n");
        sb.Append("    \"LoopRange\",\n");
        sb.Append("    \"SdlSource\",\n");
        sb.Append("    \"StatePage\",\n");
        sb.Append("    \"SubroutinePath\",\n");
        sb.Append("    \"SubroutineSpec\",\n");
        sb.Append("    \"SubroutinesPage\",\n");
        sb.Append("    \"TransitionSpec\",\n");
        sb.Append("]\n");
        return sb.ToString();
    }

    // ─── Formatting helpers ───────────────────────────────────────────

    /// <summary>
    /// Emit a single <c>from .types import (...)</c> statement
    /// containing only the names whose corresponding <c>include</c>
    /// flag is <c>true</c>. Names are emitted in the order passed in;
    /// the caller supplies them alphabetised so the output matches
    /// ruff's isort-style sort. If every flag is false this emits
    /// nothing (no empty import block).
    /// </summary>
    private static void EmitTypeImports(StringBuilder sb, params object[] pairs)
    {
        // pairs is laid out as [name, include, name, include, ...]
        var names = new List<string>(pairs.Length / 2);
        for (int i = 0; i + 1 < pairs.Length; i += 2)
        {
            var name = (string)pairs[i];
            var include = (bool)pairs[i + 1];
            if (include) names.Add(name);
        }
        if (names.Count == 0) return;
        if (names.Count == 1)
        {
            sb.Append("from .types import ").Append(names[0]).Append("\n");
            return;
        }
        sb.Append("from .types import (\n");
        foreach (var n in names)
        {
            sb.Append("    ").Append(n).Append(",\n");
        }
        sb.Append(")\n");
    }

    private static void EmitHeader(StringBuilder sb, string sourcePath)
    {
        sb.Append("# Code generated by codegen/src/Packet.Sdl.CodeGen from ")
          .Append(sourcePath.Replace('\\', '/'))
          .Append(".\n");
        sb.Append("# DO NOT EDIT. Run `dotnet run --project codegen/src/Packet.Sdl.CodeGen` to regenerate.\n\n");
    }

    /// <summary>
    /// Strip the <c>.sdl</c> + extension from a YAML page source path to
    /// produce the stem used as the Python module name root.
    /// </summary>
    internal static string PageStem(string sourcePath)
        => Path.GetFileNameWithoutExtension(sourcePath)
            .Replace(".sdl", string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// <paramref name="parentIndent"/> is the indentation depth (in
    /// 4-space units) of the surrounding <c>field=(</c> line. Entries
    /// land at parentIndent+1; the closing paren matches parentIndent.
    /// Empty tuple → <c>()</c> as a single-line literal.
    /// </summary>
    private static string FormatActions(IReadOnlyList<ResolvedAction> actions, int parentIndent)
    {
        if (actions.Count == 0) return "()";
        var indent = new string(' ', (parentIndent + 1) * 4);
        var closer = new string(' ', parentIndent * 4);
        var sb = new StringBuilder();
        sb.Append("(\n");
        foreach (var a in actions)
        {
            sb.Append(indent).Append("ActionStep(verb=").Append(PyStringLiteral(a.Verb))
              .Append(", kind=").Append(PyKindLiteral(a.Kind)).Append("),\n");
        }
        sb.Append(closer).Append(')');
        return sb.ToString();
    }

    private static string FormatReferences(IReadOnlyList<ResolvedReference> refs, int parentIndent)
    {
        if (refs.Count == 0) return "()";
        var indent = new string(' ', (parentIndent + 1) * 4);
        var inner = new string(' ', (parentIndent + 2) * 4);
        var closer = new string(' ', parentIndent * 4);
        var sb = new StringBuilder();
        sb.Append("(\n");
        foreach (var r in refs)
        {
            sb.Append(indent).Append("ImplementationReference(\n");
            sb.Append(inner).Append("source=").Append(PyStringLiteral(r.Source)).Append(",\n");
            sb.Append(inner).Append("cite=").Append(PyStringLiteral(r.Cite ?? "")).Append(",\n");
            sb.Append(inner).Append("quote=").Append(PyStringLiteral(r.Quote ?? "")).Append(",\n");
            sb.Append(inner).Append("path=").Append(PyStringLiteral(r.Path ?? "")).Append(",\n");
            sb.Append(inner).Append("function=").Append(PyStringLiteral(r.Function ?? "")).Append(",\n");
            sb.Append(inner).Append("line=")
              .Append((r.Line ?? 0).ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append(inner).Append("note=").Append(PyStringLiteral(r.Note ?? "")).Append(",\n");
            sb.Append(indent).Append("),\n");
        }
        sb.Append(closer).Append(')');
        return sb.ToString();
    }

    private static string FormatLoops(IReadOnlyList<ResolvedLoop> loops, int parentIndent)
    {
        if (loops.Count == 0) return "()";
        var indent = new string(' ', (parentIndent + 1) * 4);
        var closer = new string(' ', parentIndent * 4);
        var sb = new StringBuilder();
        sb.Append("(\n");
        foreach (var l in loops)
        {
            sb.Append(indent).Append("LoopRange(start=")
              .Append(l.Start.ToString(CultureInfo.InvariantCulture))
              .Append(", length=").Append(l.Length.ToString(CultureInfo.InvariantCulture))
              .Append(", predicate=").Append(PyStringLiteral(l.Predicate))
              .Append(", test_at_end=").Append(l.TestAtEnd ? "True" : "False").Append("),\n");
        }
        sb.Append(closer).Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// Python string literal — double-quoted with backslash escapes.
    /// Mirrors ts emitter's TsStringLiteral but uses Python's
    /// <c>\xNN</c> form for sub-0x20 control characters instead of
    /// JS's <c>\uNNNN</c> (both are accepted by Python; <c>\x</c>
    /// is canonical for ASCII control chars).
    /// </summary>
    internal static string PyStringLiteral(string s)
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
                case '\b': sb.Append("\\b");  break;
                case '\f': sb.Append("\\f");  break;
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

    /// <summary>
    /// Emit the kind as a fully-qualified <c>ActionKind</c> enum
    /// member access. The Python <c>ActionKind</c> is a str-enum
    /// whose values match the spec-sdl/actions.yaml kind names; tests
    /// compare against enum members rather than strings so type
    /// checkers can spot typos.
    /// </summary>
    internal static string PyKindLiteral(ResolvedActionKind kind) => kind switch
    {
        ResolvedActionKind.SignalUpper => "ActionKind.SIGNAL_UPPER",
        ResolvedActionKind.SignalLower => "ActionKind.SIGNAL_LOWER",
        ResolvedActionKind.Processing  => "ActionKind.PROCESSING",
        ResolvedActionKind.Subroutine  => "ActionKind.SUBROUTINE",
        ResolvedActionKind.InternalOut => "ActionKind.INTERNAL_OUT",
        _ => throw new InvalidOperationException($"unknown action kind '{kind}'"),
    };

    /// <summary>snake_case → PascalCase, preserving non-alphanumeric runs.</summary>
    internal static string Pascal(string snake)
    {
        var parts = snake.Split('_');
        return string.Concat(parts.Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
    }

    /// <summary>
    /// snake_case/PascalCase/Mixed → SCREAMING_SNAKE_CASE. Inserts
    /// underscores at letter→digit and lower→upper boundaries; upper-
    /// cases everything; collapses runs of non-alphanumeric to a
    /// single underscore. Used for module-level page constants like
    /// <c>DATA_LINK_DISCONNECTED</c> and
    /// <c>DATA_LINK_AWAITING_V22_CONNECTION</c>.
    /// </summary>
    internal static string ScreamingSnake(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length + 4);
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '_' || c == '-' || c == ' ' || c == '.')
            {
                if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
                continue;
            }
            if (!char.IsLetterOrDigit(c)) continue;
            if (i > 0)
            {
                var prev = s[i - 1];
                bool boundaryLowerToUpper = char.IsLower(prev) && char.IsUpper(c);
                bool boundaryLetterToDigit = char.IsLetter(prev) && char.IsDigit(c);
                bool boundaryDigitToLetter = char.IsDigit(prev) && char.IsLetter(c);
                if ((boundaryLowerToUpper || boundaryLetterToDigit || boundaryDigitToLetter)
                    && sb.Length > 0 && sb[^1] != '_')
                {
                    sb.Append('_');
                }
            }
            sb.Append(char.ToUpperInvariant(c));
        }
        // Strip any leading/trailing underscore that survived collapsing.
        var result = sb.ToString().Trim('_');
        return result;
    }

    /// <summary>PascalCase → snake_case (no-op for snake input).</summary>
    private static string SnakeFromState(string state)
    {
        if (string.IsNullOrEmpty(state)) return state;
        var sb = new StringBuilder(state.Length + 4);
        for (int i = 0; i < state.Length; i++)
        {
            var c = state[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(state[i - 1]))
            {
                sb.Append('_');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Make a string safe to use as the suffix of a pytest function
    /// name. Transition ids are already <c>tNN_snake_case</c> in
    /// practice, but pinch any stray non-identifier characters to
    /// underscores to be safe.
    /// </summary>
    private static string SanitiseFunctionName(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (var c in id)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }
        return sb.ToString();
    }
}
