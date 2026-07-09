using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Packet.Sdl.Interpreter;

/// <summary>
/// One golden-trace document (<c>traces/*.trace.yaml</c>). See
/// <c>docs/golden-traces.md</c> for the format specification.
/// </summary>
public sealed class TraceDoc
{
    public string Scenario { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Spec-prose citations (free text, e.g. "ax.25.2.2.4_Oct_25.md §6.3.1").</summary>
    public List<string>? Citations { get; set; }

    /// <summary>
    /// When set, the runner treats this trace as a strict xfail: the trace
    /// MUST fail (because the figure itself is defective), and the value is
    /// the upstream issue URL documenting the defect. An xfail that passes
    /// is an error — it means the defect was fixed and the marker is stale.
    /// </summary>
    public string? ExpectedFailure { get; set; }

    public string Machine { get; set; } = "data_link";
    public InitialDoc Initial { get; set; } = new();
    public List<StepDoc> Steps { get; set; } = new();
}

/// <summary>Initial machine configuration for a trace.</summary>
public sealed class InitialDoc
{
    public string State { get; set; } = "";

    /// <summary>Variable seeds; unlisted variables take the interpreter defaults (all-zero counters, modulo 8, k 4, n2 10, flags false).</summary>
    public Dictionary<string, string>? Variables { get; set; }

    /// <summary>Timer seeds (t1 / t3 → stopped | running | expired); unlisted timers start stopped.</summary>
    public Dictionary<string, string>? Timers { get; set; }
}

/// <summary>One step: an injected stimulus plus the expected outcome.</summary>
public sealed class StepDoc
{
    /// <summary>Optional human label used in failure messages.</summary>
    public string? Name { get; set; }

    public InjectDoc Inject { get; set; } = new();
    public ExpectDoc Expect { get; set; } = new();
}

/// <summary>The injected event and its received-frame fields / environment atoms.</summary>
public sealed class InjectDoc
{
    public string Event { get; set; } = "";
    public bool? Pf { get; set; }
    public bool? Command { get; set; }
    public int? Nr { get; set; }
    public int? Ns { get; set; }
    public string? Data { get; set; }
    public Dictionary<string, bool>? Atoms { get; set; }
}

/// <summary>Per-step expectations. Omitted members are unchecked.</summary>
public sealed class ExpectDoc
{
    /// <summary>Expected transition id (e.g. <c>t03_dl_connect_request</c>).</summary>
    public string? Transition { get; set; }

    /// <summary>Expected state after the step.</summary>
    public string? State { get; set; }

    /// <summary>
    /// Expected effects, in order, matched exhaustively (count and order must
    /// both match). Omit the key to skip effect checking for the step; an
    /// empty list asserts the step emits nothing.
    /// </summary>
    public List<EffectDoc>? Effects { get; set; }

    /// <summary>Variable assertions (subset — only listed keys are checked).</summary>
    public Dictionary<string, string>? Variables { get; set; }

    /// <summary>Timer assertions (subset).</summary>
    public Dictionary<string, string>? Timers { get; set; }

    /// <summary>Full expected I-frame queue contents, head first.</summary>
    public List<string>? Queue { get; set; }
}

/// <summary>
/// One expected effect. Exactly one of <see cref="Frame"/> / <see cref="Dl"/> /
/// <see cref="Lm"/> / <see cref="Internal"/> must be set; the rest of the
/// members refine a <see cref="Frame"/> or <see cref="Internal"/> expectation
/// and are unchecked when omitted.
/// </summary>
public sealed class EffectDoc
{
    public string? Frame { get; set; }
    public string? Dl { get; set; }
    public string? Lm { get; set; }
    public string? Internal { get; set; }

    public bool? Command { get; set; }
    public bool? Pf { get; set; }
    public int? Nr { get; set; }
    public int? Ns { get; set; }
    public bool? Expedited { get; set; }
    public string? Detail { get; set; }
}

/// <summary>YAML loader for trace documents.</summary>
public static class TraceLoader
{
    /// <summary>Load and structurally validate one trace document.</summary>
    /// <exception cref="InvalidDataException">Unparseable YAML, unknown keys, or a structurally invalid document.</exception>
    public static TraceDoc Load(string path)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        TraceDoc doc;
        try
        {
            doc = deserializer.Deserialize<TraceDoc>(File.ReadAllText(path))
                  ?? throw new InvalidDataException($"{path}: empty trace document");
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new InvalidDataException($"{path}: {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(doc.Scenario))
            throw new InvalidDataException($"{path}: missing `scenario:`");
        if (string.IsNullOrWhiteSpace(doc.Initial.State))
            throw new InvalidDataException($"{path}: missing `initial.state:`");
        if (doc.Steps.Count == 0)
            throw new InvalidDataException($"{path}: trace has no steps");

        for (var i = 0; i < doc.Steps.Count; i++)
        {
            var step = doc.Steps[i];
            if (string.IsNullOrWhiteSpace(step.Inject.Event))
                throw new InvalidDataException($"{path}: step {i + 1} has no `inject.event:`");
            foreach (var effect in step.Expect.Effects ?? Enumerable.Empty<EffectDoc>())
            {
                var kinds = new[] { effect.Frame, effect.Dl, effect.Lm, effect.Internal }.Count(k => k is not null);
                if (kinds != 1)
                    throw new InvalidDataException(
                        $"{path}: step {i + 1} has an effect expectation with {kinds} of frame/dl/lm/internal set — exactly one is required");
            }
        }

        return doc;
    }
}
