using Packet.Sdl.IR;

namespace Packet.Sdl.CodeGen.Csharp;

/// <summary>
/// C# view-model for a state-machine page, surfaced to Scriban templates
/// via snake_case member access (e.g. <c>page.class_name</c>). Built
/// from a <see cref="ResolvedPage"/>; all C#-specific literal escaping
/// and enum mapping happens here.
/// </summary>
public sealed class CsharpStateModel
{
    public string Machine { get; init; } = "";
    public string State { get; init; } = "";
    public string Coverage { get; init; } = "complete";
    public string ClassName { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string SourceSpec { get; init; } = "";
    public string SourceFigure { get; init; } = "";
    public string? SourceUrl { get; init; }
    public string SourceUrlLiteral { get; init; } = "null";
    public List<CsharpTransitionModel> Transitions { get; init; } = new();
    /// <summary>C# initialiser for the state's <c>Saves</c>: a read-only wrapper that cannot be cast back to an array, or the shared empty array.</summary>
    public string SavesInitializer { get; init; } = "";
    /// <summary>C# array expression for the conformance test's expected saves.</summary>
    public string SavesExpected { get; init; } = "";

    public static CsharpStateModel From(ResolvedPage page)
    {
        var className = CsharpEmitter.Pascal(page.Machine) + "_" + page.State;
        return new CsharpStateModel
        {
            Machine          = page.Machine,
            State            = page.State,
            Coverage         = page.Coverage,
            ClassName        = className,
            SourcePath       = page.SourcePath,
            SourceSpec       = page.SourceSpec,
            SourceFigure     = page.SourceFigure,
            SourceUrl        = page.SourceUrl,
            SourceUrlLiteral = page.SourceUrl is null ? "null" : CsharpEmitter.CSharpStringLiteral(page.SourceUrl),
            Transitions      = page.Transitions.Select(CsharpTransitionModel.From).ToList(),
            SavesInitializer = page.Saves.Count == 0
                ? "System.Array.Empty<Ax25Event>()"
                : "System.Array.AsReadOnly(new Ax25Event[] { " + string.Join(", ", page.Saves.Select(CsharpEmitter.EventEnumLiteral)) + " })",
            SavesExpected = page.Saves.Count == 0
                ? "System.Array.Empty<Ax25Event>()"
                : "new Ax25Event[] { " + string.Join(", ", page.Saves.Select(CsharpEmitter.EventEnumLiteral)) + " }",
        };
    }
}

public sealed class CsharpTransitionModel
{
    public string Id { get; init; } = "";
    public string On { get; init; } = "";
    public string OnEnum { get; init; } = "";
    public string OnLabel { get; init; } = "";
    public string OnLabelLiteral { get; init; } = "null";
    public string? Guard { get; init; }
    public string GuardLiteral { get; init; } = "null";
    public string Next { get; init; } = "";
    public string? Notes { get; init; }
    public string NotesLiteral { get; init; } = "null";
    public List<CsharpActionModel> Actions { get; init; } = new();
    public string ActionsCsv { get; init; } = "";
    public string ReferencesCsv { get; init; } = "";
    public string LoopsCsv { get; init; } = "";
    public string EdgeLabel { get; init; } = "";
    /// <summary>C# array-initialiser literal for the transition's
    /// UndefinedBranches argument — emits <c>null</c> when none, or the
    /// constructed <c>UndefinedSpecBranch[]</c> when one or more decisions on
    /// the transition's path cross an `undefined`-labelled edge.</summary>
    public string UndefinedBranchesCsv { get; init; } = "null";

    public static CsharpTransitionModel From(ResolvedTransition t) => new()
    {
        Id                   = t.Id,
        On                   = t.On,
        OnEnum               = CsharpEmitter.EventEnumLiteral(t.On),
        OnLabel              = t.OnLabel,
        OnLabelLiteral       = string.IsNullOrEmpty(t.OnLabel) ? "null" : CsharpEmitter.CSharpStringLiteral(t.OnLabel),
        Guard                = t.Guard,
        GuardLiteral         = CsharpEmitter.GuardExprLiteral(t.Guard),
        Next                 = t.Next,
        Notes                = t.Notes,
        NotesLiteral         = t.Notes is null ? "null" : CsharpEmitter.CSharpStringLiteral(t.Notes),
        Actions              = t.Actions.Select(CsharpActionModel.From).ToList(),
        ActionsCsv           = CsharpEmitter.FormatActionsCsv(t.Actions),
        ReferencesCsv        = CsharpEmitter.FormatReferenceCsv(t.References),
        LoopsCsv             = CsharpEmitter.FormatLoopsCsv(t.Loops),
        EdgeLabel            = CsharpEmitter.BuildMermaidEdgeLabel(t.Id, t.On, t.Guard, t.Actions),
        UndefinedBranchesCsv = FormatUndefinedBranchesCsv(t.UndefinedBranches),
    };

    private static string FormatUndefinedBranchesCsv(IReadOnlyList<ResolvedUndefinedBranch> branches)
    {
        if (branches.Count == 0) return "null";
        var entries = branches.Select(b =>
            $"new UndefinedSpecBranch({CsharpEmitter.CSharpStringLiteral(b.DecisionId)}, " +
            $"{CsharpEmitter.CSharpStringLiteral(b.Question)}, " +
            $"{CsharpEmitter.GuardEnumLiteral(b.Predicate)})");
        return "new UndefinedSpecBranch[] { " + string.Join(", ", entries) + " }";
    }
}

public sealed class CsharpActionModel
{
    public string Verb { get; init; } = "";
    public string VerbLiteral { get; init; } = "";
    public string VerbEnum { get; init; } = "";
    public string KindEnum { get; init; } = "";

    public static CsharpActionModel From(ResolvedAction a) => new()
    {
        Verb        = a.Verb,
        VerbLiteral = CsharpEmitter.CSharpStringLiteral(a.Verb),
        VerbEnum    = CsharpEmitter.VerbEnumLiteral(a.Verb),
        KindEnum    = CsharpEmitter.KindEnumLiteral(a.Kind),
    };
}
