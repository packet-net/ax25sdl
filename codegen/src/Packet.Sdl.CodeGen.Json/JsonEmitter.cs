using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Packet.Sdl.IR;

namespace Packet.Sdl.CodeGen.Json;

/// <summary>
/// JSON emitter for the resolved SDL IR. Produces one <c>.g.json</c>
/// file per page, a single <c>schema.json</c> describing the shape, and
/// an <c>index.json</c> manifest listing every page. The emitter
/// validates every emitted <c>.g.json</c> against the schema before it
/// is written; validation failure throws.
/// </summary>
/// <remarks>
/// <para>
/// Output uses <see cref="System.Text.Json"/> with deterministic key
/// ordering (we hand-roll the field order in code), 2-space indentation,
/// and camelCase property names — matching the conventions used by the
/// TypeScript emitter so consumers can interchange the two.
/// </para>
/// <para>
/// Action kinds serialise as lowercase snake_case strings
/// (<c>signal_upper</c> / <c>signal_lower</c> / <c>processing</c> /
/// <c>subroutine</c> / <c>internal_out</c>) — the same identifiers used
/// by <c>spec-sdl/actions.yaml</c> group names.
/// </para>
/// </remarks>
public static class JsonEmitter
{
    public sealed record Emission(string FileName, string Content);

    /// <summary>
    /// Shared writer options. <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>
    /// keeps non-ASCII glyphs (and characters like <c>=</c>) un-escaped so
    /// the output stays human-readable; the resulting bytes are still
    /// strictly RFC 8259 JSON.
    /// </summary>
    private static readonly JsonSerializerOptions WriterOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ─── Page emission ────────────────────────────────────────────────

    public static Emission EmitStatePage(ResolvedPage page)
    {
        var stem = AnnexC.OutputStem(page.Machine, page.SourcePath);
        var fileName = stem + ".g.json";

        var node = new JsonObject
        {
            ["$schema"] = "./schema.json",
            ["kind"] = "state",
            ["machine"] = page.Machine,
            ["state"] = page.State,
            ["source"] = BuildSource(page.SourceSpec, page.SourceFigure, page.SourceUrl),
            ["transitions"] = BuildTransitions(page),
        };

        return new Emission(fileName, Serialise(node));
    }

    public static Emission EmitSubroutinePage(ResolvedSubroutinesPage page)
    {
        var stem = AnnexC.OutputStem(page.Machine, page.SourcePath);
        var fileName = stem + ".g.json";

        var node = new JsonObject
        {
            ["$schema"] = "./schema.json",
            ["kind"] = "subroutines",
            ["machine"] = page.Machine,
            ["source"] = BuildSource(page.SourceSpec, page.SourceFigure, page.SourceUrl),
            ["subroutines"] = BuildSubroutines(page.Subroutines),
        };

        return new Emission(fileName, Serialise(node));
    }

    /// <summary>
    /// Build the <c>index.json</c> manifest listing every state page and
    /// subroutine page. Sorted by file name (Ordinal) for determinism.
    /// </summary>
    public static string EmitIndex(IEnumerable<ResolvedPage> pages, IEnumerable<ResolvedSubroutinesPage> subPages)
    {
        var entries = new JsonArray();
        var combined = new List<(string File, JsonObject Entry)>();

        foreach (var p in pages)
        {
            var stem = AnnexC.OutputStem(p.Machine, p.SourcePath);
            var file = stem + ".g.json";
            combined.Add((file, new JsonObject
            {
                ["file"] = file,
                ["kind"] = "state",
                ["machine"] = p.Machine,
                ["state"] = p.State,
                ["figure"] = p.SourceFigure,
            }));
        }
        foreach (var p in subPages)
        {
            var stem = AnnexC.OutputStem(p.Machine, p.SourcePath);
            var file = stem + ".g.json";
            combined.Add((file, new JsonObject
            {
                ["file"] = file,
                ["kind"] = "subroutines",
                ["machine"] = p.Machine,
                ["figure"] = p.SourceFigure,
            }));
        }

        foreach (var (_, entry) in combined.OrderBy(t => t.File, StringComparer.Ordinal))
        {
            entries.Add(entry);
        }

        var node = new JsonObject
        {
            ["$schema"] = "./schema.json",
            ["kind"] = "index",
            ["pages"] = entries,
        };

        return Serialise(node);
    }

    // ─── Schema ───────────────────────────────────────────────────────

    /// <summary>
    /// Build the JSON Schema (draft-2020-12) describing every shape this
    /// emitter produces. Hand-written rather than reflected so we can
    /// keep <c>additionalProperties: false</c> across the board and have
    /// fine-grained control over <c>$defs</c> reuse.
    /// </summary>
    public static string EmitSchema()
    {
        var actionKind = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray("signal_upper", "signal_lower", "processing", "subroutine", "internal_out"),
        };

        var actionStep = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("verb", "kind"),
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["verb"] = new JsonObject { ["type"] = "string" },
                ["kind"] = new JsonObject { ["$ref"] = "#/$defs/actionKind" },
            },
        };

        var loopRange = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("start", "length", "predicate", "test_at_end"),
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["start"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
                ["length"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
                ["predicate"] = new JsonObject { ["type"] = "string" },
                ["test_at_end"] = new JsonObject { ["type"] = "boolean" },
            },
        };

        var implementationReference = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("source", "cite", "quote", "path", "function", "line", "note"),
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["source"] = new JsonObject { ["type"] = "string" },
                ["cite"] = new JsonObject { ["type"] = "string" },
                ["quote"] = new JsonObject { ["type"] = "string" },
                ["path"] = new JsonObject { ["type"] = "string" },
                ["function"] = new JsonObject { ["type"] = "string" },
                ["line"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
                ["note"] = new JsonObject { ["type"] = "string" },
            },
        };

        var source = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("spec", "figure", "url"),
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["spec"] = new JsonObject { ["type"] = "string" },
                ["figure"] = new JsonObject { ["type"] = "string" },
                ["url"] = new JsonObject { ["type"] = "string" },
            },
        };

        var transitionSpec = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("id", "from", "on", "guard", "actions", "next", "notes", "references", "loops"),
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string" },
                ["from"] = new JsonObject { ["type"] = "string" },
                ["on"] = new JsonObject { ["type"] = "string" },
                ["guard"] = new JsonObject { ["type"] = "string" },
                ["actions"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["$ref"] = "#/$defs/actionStep" },
                },
                ["next"] = new JsonObject { ["type"] = "string" },
                ["notes"] = new JsonObject { ["type"] = "string" },
                ["references"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["$ref"] = "#/$defs/implementationReference" },
                },
                ["loops"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["$ref"] = "#/$defs/loopRange" },
                },
            },
        };

        var subroutinePath = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("id", "guard", "actions", "notes", "references", "loops"),
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string" },
                ["guard"] = new JsonObject { ["type"] = "string" },
                ["actions"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["$ref"] = "#/$defs/actionStep" },
                },
                ["notes"] = new JsonObject { ["type"] = "string" },
                ["references"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["$ref"] = "#/$defs/implementationReference" },
                },
                ["loops"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["$ref"] = "#/$defs/loopRange" },
                },
            },
        };

        var subroutineSpec = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("name", "paths", "notes", "references"),
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject { ["type"] = "string" },
                ["paths"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["$ref"] = "#/$defs/subroutinePath" },
                },
                ["notes"] = new JsonObject { ["type"] = "string" },
                ["references"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["$ref"] = "#/$defs/implementationReference" },
                },
            },
        };

        var statePage = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("kind", "machine", "state", "source", "transitions"),
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["$schema"] = new JsonObject { ["type"] = "string" },
                ["kind"] = new JsonObject { ["const"] = "state" },
                ["machine"] = new JsonObject { ["type"] = "string" },
                ["state"] = new JsonObject { ["type"] = "string" },
                ["source"] = new JsonObject { ["$ref"] = "#/$defs/source" },
                ["transitions"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["$ref"] = "#/$defs/transitionSpec" },
                },
            },
        };

        var subroutinesPage = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("kind", "machine", "source", "subroutines"),
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["$schema"] = new JsonObject { ["type"] = "string" },
                ["kind"] = new JsonObject { ["const"] = "subroutines" },
                ["machine"] = new JsonObject { ["type"] = "string" },
                ["source"] = new JsonObject { ["$ref"] = "#/$defs/source" },
                ["subroutines"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["$ref"] = "#/$defs/subroutineSpec" },
                },
            },
        };

        var indexEntry = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("file", "kind", "machine", "figure"),
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["file"] = new JsonObject { ["type"] = "string" },
                ["kind"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("state", "subroutines"),
                },
                ["machine"] = new JsonObject { ["type"] = "string" },
                ["state"] = new JsonObject { ["type"] = "string" },
                ["figure"] = new JsonObject { ["type"] = "string" },
            },
        };

        var indexPage = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("kind", "pages"),
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["$schema"] = new JsonObject { ["type"] = "string" },
                ["kind"] = new JsonObject { ["const"] = "index" },
                ["pages"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["$ref"] = "#/$defs/indexEntry" },
                },
            },
        };

        var schema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = "https://packet.net/sdl/schema.json",
            ["title"] = "ax25sdl",
            ["description"] = "Resolved SDL IR for the AX.25 v2.2 specification. Each *.g.json conforms to statePage, subroutinesPage, or indexPage.",
            ["$defs"] = new JsonObject
            {
                ["actionKind"] = actionKind,
                ["actionStep"] = actionStep,
                ["loopRange"] = loopRange,
                ["implementationReference"] = implementationReference,
                ["source"] = source,
                ["transitionSpec"] = transitionSpec,
                ["subroutinePath"] = subroutinePath,
                ["subroutineSpec"] = subroutineSpec,
                ["statePage"] = statePage,
                ["subroutinesPage"] = subroutinesPage,
                ["indexEntry"] = indexEntry,
                ["indexPage"] = indexPage,
            },
            ["oneOf"] = new JsonArray(
                new JsonObject { ["$ref"] = "#/$defs/statePage" },
                new JsonObject { ["$ref"] = "#/$defs/subroutinesPage" },
                new JsonObject { ["$ref"] = "#/$defs/indexPage" }),
        };

        return Serialise(schema);
    }

    // ─── Validation ───────────────────────────────────────────────────

    /// <summary>
    /// Validate <paramref name="emissionText"/> against
    /// <paramref name="schemaText"/>. Throws
    /// <see cref="InvalidOperationException"/> on validation failure with
    /// a message listing every failing pointer; throws nothing on success.
    /// </summary>
    public static void ValidateAgainstSchema(string schemaText, string emissionText, string fileForErrorMessage)
    {
        JsonSchema schema;
        try
        {
            schema = JsonSchema.FromText(schemaText)
                ?? throw new InvalidOperationException("JsonSchema.FromText returned null");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse the generated JSON Schema (this is a codegen bug, not a YAML bug): {ex.Message}", ex);
        }

        JsonNode? doc;
        try
        {
            doc = JsonNode.Parse(emissionText);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Generated JSON for {fileForErrorMessage} failed to parse (codegen bug): {ex.Message}", ex);
        }

        var options = new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        };
        var result = schema.Evaluate(doc, options);
        if (result.IsValid) return;

        var sb = new StringBuilder();
        sb.Append("Generated JSON for ").Append(fileForErrorMessage).Append(" failed schema validation:\n");
        foreach (var detail in Flatten(result))
        {
            if (detail.IsValid) continue;
            if (detail.HasErrors && detail.Errors is not null)
            {
                foreach (var err in detail.Errors)
                {
                    sb.Append("  at ").Append(detail.InstanceLocation)
                      .Append(" (schema ").Append(detail.EvaluationPath).Append("): ")
                      .Append(err.Key).Append(": ").Append(err.Value).Append('\n');
                }
            }
        }
        throw new InvalidOperationException(sb.ToString());
    }

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults r)
    {
        yield return r;
        if (r.Details is null) yield break;
        foreach (var d in r.Details)
            foreach (var inner in Flatten(d))
                yield return inner;
    }

    // ─── Builders ─────────────────────────────────────────────────────

    private static JsonObject BuildSource(string spec, string figure, string? url) => new()
    {
        ["spec"] = spec,
        ["figure"] = figure,
        ["url"] = url ?? "",
    };

    private static JsonArray BuildTransitions(ResolvedPage page)
    {
        var arr = new JsonArray();
        foreach (var t in page.Transitions)
        {
            arr.Add(new JsonObject
            {
                ["id"] = t.Id,
                ["from"] = page.State,
                ["on"] = t.On,
                ["guard"] = t.Guard ?? "",
                ["actions"] = BuildActions(t.Actions),
                ["next"] = t.Next,
                ["notes"] = t.Notes ?? "",
                ["references"] = BuildReferences(t.References),
                ["loops"] = BuildLoops(t.Loops),
            });
        }
        return arr;
    }

    private static JsonArray BuildSubroutines(IReadOnlyList<ResolvedSubroutine> subs)
    {
        var arr = new JsonArray();
        foreach (var s in subs)
        {
            var paths = new JsonArray();
            foreach (var p in s.Paths)
            {
                paths.Add(new JsonObject
                {
                    ["id"] = p.Id,
                    ["guard"] = p.Guard ?? "",
                    ["actions"] = BuildActions(p.Actions),
                    ["notes"] = p.Notes ?? "",
                    ["references"] = BuildReferences(p.References),
                    ["loops"] = BuildLoops(p.Loops),
                });
            }
            arr.Add(new JsonObject
            {
                ["name"] = s.Name,
                ["paths"] = paths,
                ["notes"] = s.Notes ?? "",
                ["references"] = BuildReferences(s.References),
            });
        }
        return arr;
    }

    private static JsonArray BuildActions(IReadOnlyList<ResolvedAction> actions)
    {
        var arr = new JsonArray();
        foreach (var a in actions)
        {
            arr.Add(new JsonObject
            {
                ["verb"] = a.Verb,
                ["kind"] = KindString(a.Kind),
            });
        }
        return arr;
    }

    private static JsonArray BuildReferences(IReadOnlyList<ResolvedReference> refs)
    {
        var arr = new JsonArray();
        foreach (var r in refs)
        {
            arr.Add(new JsonObject
            {
                ["source"] = r.Source,
                ["cite"] = r.Cite ?? "",
                ["quote"] = r.Quote ?? "",
                ["path"] = r.Path ?? "",
                ["function"] = r.Function ?? "",
                ["line"] = r.Line ?? 0,
                ["note"] = r.Note ?? "",
            });
        }
        return arr;
    }

    private static JsonArray BuildLoops(IReadOnlyList<ResolvedLoop> loops)
    {
        var arr = new JsonArray();
        foreach (var l in loops)
        {
            arr.Add(new JsonObject
            {
                ["start"] = l.Start,
                ["length"] = l.Length,
                ["predicate"] = l.Predicate,
                ["test_at_end"] = l.TestAtEnd,
            });
        }
        return arr;
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static string KindString(ResolvedActionKind kind) => kind switch
    {
        ResolvedActionKind.SignalUpper => "signal_upper",
        ResolvedActionKind.SignalLower => "signal_lower",
        ResolvedActionKind.Processing => "processing",
        ResolvedActionKind.Subroutine => "subroutine",
        ResolvedActionKind.InternalOut => "internal_out",
        _ => throw new InvalidOperationException($"unknown action kind '{kind}'"),
    };

    /// <summary>
    /// Serialise <paramref name="node"/> with our shared
    /// <see cref="WriterOptions"/> and a trailing newline (matches
    /// <c>WriteIfChanged</c>'s expectations and standard POSIX text-file
    /// convention).
    /// </summary>
    private static string Serialise(JsonNode node)
    {
        var text = node.ToJsonString(WriterOptions);
        if (!text.EndsWith('\n')) text += "\n";
        return text;
    }

    // CultureInfo reference kept to satisfy analyzers if someone later
    // re-adds formatted-int conversions; harmless otherwise.
    private static readonly CultureInfo _invariant = CultureInfo.InvariantCulture;
}
