namespace Packet.Sdl.IR;

// ─── Resolved IR ─────────────────────────────────────────────────────
//
// Language-neutral representation of an SDL page after path-walking and
// action-verb canonicalisation. Emitters (C# / Go / …) consume this IR;
// nothing here is C#-specific.
//
// Build via Resolver.Resolve(SdlPage) / Resolver.Resolve(SubroutinePage).

public enum ResolvedActionKind
{
    SignalUpper,
    SignalLower,
    Processing,
    Subroutine,
    InternalOut,
}

/// <summary>One verb in a transition's or subroutine path's action list.</summary>
public sealed record ResolvedAction(string Verb, ResolvedActionKind Kind);

/// <summary>
/// One loop range over the flat <see cref="ResolvedTransition.Actions"/> list.
/// <see cref="Start"/> and <see cref="Length"/> point at the body slice;
/// <see cref="Predicate"/> is the loop's <em>continue</em> condition (already
/// negated where the figure's continuing branch is the decision's No edge, so
/// it reads directly as "keep looping while this holds").
/// <see cref="TestAtEnd"/> distinguishes the two SDL loop topologies: false =
/// test-at-head (while; the figure's decision sits before the body, body runs
/// zero-or-more times); true = test-at-tail (do-while; the decision sits after
/// the body, body runs one-or-more times).
/// </summary>
public sealed record ResolvedLoop(int Start, int Length, string Predicate, bool TestAtEnd);

/// <summary>
/// One cross-reference citation. <see cref="Source"/> is "spec_prose" or
/// the key of a page-level pinned_refs entry. Code citations populate
/// <see cref="Path"/>/<see cref="Function"/>/<see cref="Line"/>;
/// spec_prose citations populate <see cref="Cite"/>/<see cref="Quote"/>.
/// </summary>
public sealed record ResolvedReference(
    string Source,
    string? Cite,
    string? Quote,
    string? Path,
    string? Function,
    int? Line,
    string? Note);

/// <summary>A resolved state-machine page (figc4.1 / 4.2 / 4.3 / 4.4 / 4.6 / etc.).</summary>
public sealed class ResolvedPage
{
    public string Machine { get; init; } = "";
    public string State { get; init; } = "";
    public string Coverage { get; init; } = "complete";
    public string SourcePath { get; init; } = "";
    public string SourceSpec { get; init; } = "";
    public string SourceFigure { get; init; } = "";
    public string? SourceUrl { get; init; }
    public IReadOnlyList<ResolvedTransition> Transitions { get; init; } = Array.Empty<ResolvedTransition>();
    /// <summary>Inputs this state saves (the figure's Save symbol, "Requeue"),
    /// as event ids in the order the page lists them. A saved input is not
    /// consumed in this state: it stays in the input queue, in arrival order,
    /// until the machine enters a state that does not save it (ITU-T Z.100
    /// save semantics; docs/golden-traces.md). Empty for most states.</summary>
    public IReadOnlyList<string> Saves { get; init; } = Array.Empty<string>();
}

/// <summary>One resolved transition on a state-machine page.</summary>
public sealed class ResolvedTransition
{
    public string Id { get; init; } = "";
    public string On { get; init; } = "";
    /// <summary>Verbatim trigger-box NodeLabel text from the source SDL (e.g.
    /// "I Frame Pops Off I Frame Queue"). Empty for transitions transcribed before the
    /// on_label field landed. Emitters include this in error messages so a
    /// runtime trace can quote the figure.</summary>
    public string OnLabel { get; init; } = "";
    public string? Guard { get; init; }
    public string Next { get; init; } = "";
    public string? Notes { get; init; }
    public IReadOnlyList<ResolvedAction> Actions { get; init; } = Array.Empty<ResolvedAction>();
    public IReadOnlyList<ResolvedLoop> Loops { get; init; } = Array.Empty<ResolvedLoop>();
    public IReadOnlyList<ResolvedReference> References { get; init; } = Array.Empty<ResolvedReference>();
    /// <summary>Decisions in the path whose branch label is <c>Undefined</c>
    /// — paths crossing the SDL's "both edges labelled undefined" marker.
    /// Emitters must render the transition body as a runtime throw quoting
    /// these decision questions; the runtime cannot resolve which path to
    /// take because the spec leaves it unspecified. Empty for the normal
    /// (Yes/No-only) case.</summary>
    public IReadOnlyList<ResolvedUndefinedBranch> UndefinedBranches { get; init; } = Array.Empty<ResolvedUndefinedBranch>();
}

/// <summary>Records a single Undefined branch crossed on a path — the
/// decision id, its human-readable question, and the predicate name. Used by
/// emitters to compose runtime-throw error messages that quote the figure.</summary>
public sealed record ResolvedUndefinedBranch(string DecisionId, string Question, string Predicate);

/// <summary>A resolved subroutine page (figc4.7).</summary>
public sealed class ResolvedSubroutinesPage
{
    public string Machine { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string SourceSpec { get; init; } = "";
    public string SourceFigure { get; init; } = "";
    public string? SourceUrl { get; init; }
    public IReadOnlyList<ResolvedSubroutine> Subroutines { get; init; } = Array.Empty<ResolvedSubroutine>();
}

/// <summary>One resolved subroutine on a subroutine page.</summary>
public sealed class ResolvedSubroutine
{
    public string Name { get; init; } = "";
    public string? Notes { get; init; }
    public IReadOnlyList<ResolvedSubroutinePath> Paths { get; init; } = Array.Empty<ResolvedSubroutinePath>();
    public IReadOnlyList<ResolvedReference> References { get; init; } = Array.Empty<ResolvedReference>();
}

/// <summary>One resolved path through a subroutine.</summary>
public sealed class ResolvedSubroutinePath
{
    public string Id { get; init; } = "";
    public string? Guard { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<ResolvedAction> Actions { get; init; } = Array.Empty<ResolvedAction>();
    public IReadOnlyList<ResolvedLoop> Loops { get; init; } = Array.Empty<ResolvedLoop>();
    public IReadOnlyList<ResolvedReference> References { get; init; } = Array.Empty<ResolvedReference>();
}
