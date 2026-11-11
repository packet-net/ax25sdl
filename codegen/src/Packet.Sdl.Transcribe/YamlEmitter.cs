using System.Globalization;
using System.Text;

namespace Packet.Sdl.Transcribe;

/// <summary>
/// Emits an SdlPage as YAML, matching the existing format conventions in
/// spec-sdl/v2.2-errata/data-link/yaml/*.sdl.yaml.
/// </summary>
/// <remarks>
/// We hand-write YAML instead of using YamlDotNet's serializer so that
/// flow-style maps for path steps (`{ action: ..., kind: ... }`) come out
/// the way the existing yamls have them — YamlDotNet defaults to block
/// style and we'd have to fight it for every collection.
/// </remarks>
public static class YamlEmitter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Emit(SdlPage page)
    {
        var sb = new StringBuilder();

        var src = page.SourceGraphmlPath is null ? "(unknown)" : Path.GetFileName(page.SourceGraphmlPath);
        sb.AppendLine(Inv, $"# Auto-generated from {src}. DO NOT EDIT.");
        sb.AppendLine("# Regenerate via `dotnet run --project codegen/src/Packet.Sdl.Transcribe`.");
        sb.AppendLine();

        sb.AppendLine(Inv, $"machine: {page.Machine}");
        sb.AppendLine(Inv, $"state: {page.State}");
        if (page.Coverage != "complete")
            sb.AppendLine(Inv, $"coverage: {page.Coverage}");

        if (!string.IsNullOrEmpty(page.Source.Spec) || !string.IsNullOrEmpty(page.Source.Figure))
        {
            sb.AppendLine("source:");
            if (!string.IsNullOrEmpty(page.Source.Spec))
                sb.AppendLine(Inv, $"  spec: {page.Source.Spec}");
            if (!string.IsNullOrEmpty(page.Source.Figure))
                sb.AppendLine(Inv, $"  figure: {page.Source.Figure}");
            if (!string.IsNullOrEmpty(page.Source.Url))
                sb.AppendLine(Inv, $"  url: {page.Source.Url}");
        }

        if (page.Save.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("save:");
            foreach (var ev in page.Save)
                sb.AppendLine(Inv, $"  - {ev}");
        }

        if (page.Decisions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("decisions:");
            foreach (var d in page.Decisions)
            {
                sb.AppendLine(Inv, $"  - id: {d.Id}");
                // Always quote questions — they contain '?' and conventionally
                // appear quoted in the existing yamls for readability.
                sb.AppendLine(Inv, $"    question: \"{d.Question}\"");
                sb.AppendLine(Inv, $"    predicate: {d.Predicate}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("transitions:");
        foreach (var t in page.Transitions)
        {
            sb.AppendLine();
            sb.AppendLine(Inv, $"  - id: {t.Id}");
            sb.AppendLine(Inv, $"    on: {t.On}");
            if (!string.IsNullOrEmpty(t.OnLabel))
                sb.AppendLine(Inv, $"    on_label: {YamlQuote(t.OnLabel)}");
            if (t.Path.Count == 0)
            {
                sb.AppendLine("    path: []");
            }
            else
            {
                sb.AppendLine("    path:");
                foreach (var step in t.Path)
                    AppendStep(sb, "      ", step);
            }
            sb.AppendLine(Inv, $"    next: {t.Next}");
        }

        return sb.ToString();
    }

    public static string EmitSubroutines(SubroutinePage page)
    {
        var sb = new StringBuilder();
        var src = page.SourceGraphmlPath is null ? "(unknown)" : Path.GetFileName(page.SourceGraphmlPath);
        sb.AppendLine(Inv, $"# Auto-generated from {src}. DO NOT EDIT.");
        sb.AppendLine("# Regenerate via `dotnet run --project codegen/src/Packet.Sdl.Transcribe`.");
        sb.AppendLine();
        sb.AppendLine(Inv, $"machine: {page.Machine}");

        if (!string.IsNullOrEmpty(page.Source.Spec) || !string.IsNullOrEmpty(page.Source.Figure))
        {
            sb.AppendLine("source:");
            if (!string.IsNullOrEmpty(page.Source.Spec))
                sb.AppendLine(Inv, $"  spec: {page.Source.Spec}");
            if (!string.IsNullOrEmpty(page.Source.Figure))
                sb.AppendLine(Inv, $"  figure: {page.Source.Figure}");
            if (!string.IsNullOrEmpty(page.Source.Url))
                sb.AppendLine(Inv, $"  url: {page.Source.Url}");
        }

        sb.AppendLine();
        sb.AppendLine("subroutines:");
        foreach (var sub in page.Subroutines)
        {
            sb.AppendLine();
            sb.AppendLine(Inv, $"  - name: {sub.Name}");
            if (sub.Decisions.Count > 0)
            {
                sb.AppendLine("    decisions:");
                foreach (var d in sub.Decisions)
                {
                    sb.AppendLine(Inv, $"      - id: {d.Id}");
                    sb.AppendLine(Inv, $"        question: \"{d.Question}\"");
                    sb.AppendLine(Inv, $"        predicate: {d.Predicate}");
                }
            }
            sb.AppendLine("    paths:");
            foreach (var p in sub.Paths)
            {
                sb.AppendLine(Inv, $"      - id: {p.Id}");
                if (p.Steps.Count == 0)
                {
                    sb.AppendLine("        path: []");
                }
                else
                {
                    sb.AppendLine("        path:");
                    foreach (var step in p.Steps)
                        AppendStep(sb, "          ", step);
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Appends one path step at the given indent. Action and decision steps are
    /// single-line flow maps; a <see cref="LoopWhileStep"/> is a block map with
    /// an optional <c>branch:</c> (omitted when the continuing edge is Yes) and
    /// <c>test:</c> (omitted for the default head-test while), and a nested
    /// <c>body:</c> list of action steps.
    /// </summary>
    private static void AppendStep(StringBuilder sb, string indent, PathStep step)
    {
        if (step is LoopWhileStep l)
        {
            sb.AppendLine(Inv, $"{indent}- loop_while: {l.DecisionId}");
            var c = indent + "  ";
            if (l.Branch != "Yes")
                sb.AppendLine(Inv, $"{c}branch: {l.Branch}");
            if (l.TestAtEnd)
                sb.AppendLine(Inv, $"{c}test: tail");
            sb.AppendLine(Inv, $"{c}body:");
            foreach (var b in l.Body)
                AppendStep(sb, c + "  ", b);
            return;
        }
        sb.AppendLine(Inv, $"{indent}- {FormatStep(step)}");
    }

    private static string FormatStep(PathStep step) => step switch
    {
        ActionStep a       => string.Create(Inv, $"{{ action: {YamlQuote(a.Action)}, kind: {a.Kind} }}"),
        DecisionBranch d   => string.Create(Inv, $"{{ decision: {d.DecisionId}, branch: {YamlQuote(d.Branch)} }}"),
        _ => throw new InvalidOperationException($"unknown PathStep variant {step.GetType().Name}"),
    };

    private static readonly System.Buffers.SearchValues<char> QuoteForcing =
        System.Buffers.SearchValues.Create("\"\\");
    private static readonly System.Buffers.SearchValues<char> PlainScalarTroublemakers =
        // '?' too: inside a flow map ({ action: Start T106 (T108?), ... }) a
        // strict YAML parser reads it as a mapping-key indicator (C2a.5).
        System.Buffers.SearchValues.Create(":#,[]{}&*!|>%@`?");

    private static string YamlQuote(string s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";
        if (s.AsSpan().ContainsAny(QuoteForcing))
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var needsQuote = s.AsSpan().ContainsAny(PlainScalarTroublemakers)
                         || s.StartsWith(' ') || s.EndsWith(' ')
                         || s.StartsWith('-')
                         || double.TryParse(s, NumberStyles.Any, Inv, out _)
                         || s is "true" or "false" or "yes" or "no" or "null" or "~";
        return needsQuote ? "\"" + s + "\"" : s;
    }
}
