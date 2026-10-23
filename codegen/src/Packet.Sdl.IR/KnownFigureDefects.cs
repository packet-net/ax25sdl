using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Packet.Sdl.IR;

/// <summary>
/// One entry of <c>spec-sdl/known-figure-defects.yaml</c> (in ax25spec): a
/// transition whose <c>next:</c> names a state that does not exist, because
/// the figure draws it that way. The transcription records what is drawn;
/// this entry says the defect is known and filed.
/// </summary>
public sealed class KnownFigureDefect
{
    /// <summary>A transition's <c>next:</c> names a state that does not exist. The machine is withheld.</summary>
    public const string UnknownNextState = "unknown_next_state";

    /// <summary>
    /// The figure draws no catch-all input for this state. The page is
    /// exempt from the catch-all lint, as a <c>coverage: partial</c> page
    /// is, and the machine still generates. The only kind allowed on a
    /// published machine, because it changes no transition.
    /// </summary>
    public const string MissingCatchall = "missing_catchall";

    /// <summary>
    /// A subroutine call, as drawn, names no subroutine of its machine. The
    /// machine still generates (the call stays a named verb, as before). It
    /// changes no transition, so it is allowed on a published machine.
    /// </summary>
    public const string UnknownSubroutine = "unknown_subroutine";

    /// <summary><see cref="UnknownNextState"/> (the default), <see cref="MissingCatchall"/> or <see cref="UnknownSubroutine"/>.</summary>
    public string Kind { get; set; } = UnknownNextState;

    /// <summary>unknown_subroutine only: the call's label exactly as drawn (e.g. "Select T1 Value").</summary>
    public string Call { get; set; } = "";

    /// <summary>The page's <c>machine:</c>.</summary>
    public string Machine { get; set; } = "";
    /// <summary>The page's <c>state:</c>.</summary>
    public string State { get; set; } = "";
    /// <summary>unknown_next_state only: the transition id, exactly as generated.</summary>
    public string Transition { get; set; } = "";
    /// <summary>unknown_next_state only: the <c>next:</c> value the figure draws.</summary>
    public string Next { get; set; } = "";
    /// <summary>Required. The ax25spec issue that records the defect.</summary>
    public string Issue { get; set; } = "";
    public string? Note { get; set; }

    public string Describe() => Kind switch
    {
        MissingCatchall => $"{Machine} {State} has no catch-all input ({Issue})",
        UnknownSubroutine => $"{Machine} calls subroutine \"{Call}\", which it does not define ({Issue})",
        _ => $"{Machine} {State} transition `{Transition}` leads to `{Next}` ({Issue})",
    };
}

/// <summary>Top-level shape of the allowlist file.</summary>
public sealed class KnownFigureDefectsFile
{
    public List<KnownFigureDefect> Defects { get; set; } = new();
}

/// <summary>
/// The known-figure-defects allowlist. It works like a strict xfail: a
/// listed defect is reported as a warning instead of failing the run, and
/// its machine stays withheld from generation while the entry is present;
/// an entry whose defect no longer occurs fails the run, so the list can't
/// go stale. The file lives next to the figures in ax25spec, because the
/// defects belong to the figures, not to the tooling.
/// </summary>
public static class KnownFigureDefects
{
    public const string FileName = "known-figure-defects.yaml";

    /// <summary>Figure defects are recorded in ax25spec, so the issue must be one there.</summary>
    private static readonly System.Text.RegularExpressions.Regex IssueUrl =
        new(@"^https://github\.com/packethacking/ax25spec/issues/[0-9]+$");

    /// <summary>
    /// Loads <c>&lt;spec-sdl&gt;/known-figure-defects.yaml</c>. A missing
    /// file is an empty list. Malformed entries (a missing field, or an
    /// unknown key, which is usually a typo) are added to
    /// <paramref name="errors"/> and dropped.
    /// </summary>
    public static List<KnownFigureDefect> Load(string specDir, List<string> errors)
    {
        var path = Path.Combine(specDir, FileName);
        if (!File.Exists(path)) return new List<KnownFigureDefect>();

        KnownFigureDefectsFile file;
        try
        {
            file = new DeserializerBuilder()
                .WithNamingConvention(LowerCaseNamingConvention.Instance)
                .Build()
                .Deserialize<KnownFigureDefectsFile>(File.ReadAllText(path)) ?? new KnownFigureDefectsFile();
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            errors.Add($"{path}: {ex.Message}");
            return new List<KnownFigureDefect>();
        }

        var valid = new List<KnownFigureDefect>();
        var seen = new HashSet<(string, string, string)>();
        foreach (var d in file.Defects ?? new List<KnownFigureDefect>())
        {
            if (d.Kind is not (KnownFigureDefect.UnknownNextState or KnownFigureDefect.MissingCatchall or KnownFigureDefect.UnknownSubroutine))
            {
                errors.Add($"{path}: unknown kind `{d.Kind}`; use {KnownFigureDefect.UnknownNextState}, {KnownFigureDefect.MissingCatchall} or {KnownFigureDefect.UnknownSubroutine}.");
                continue;
            }
            if (d.Kind == KnownFigureDefect.UnknownSubroutine)
            {
                if (string.IsNullOrWhiteSpace(d.Machine) || string.IsNullOrWhiteSpace(d.Call)
                    || !string.IsNullOrWhiteSpace(d.State) || !string.IsNullOrWhiteSpace(d.Transition) || !string.IsNullOrWhiteSpace(d.Next))
                {
                    errors.Add($"{path}: an {KnownFigureDefect.UnknownSubroutine} entry needs machine and call (the label as drawn), and nothing else.");
                    continue;
                }
            }
            else if (string.IsNullOrWhiteSpace(d.Machine) || string.IsNullOrWhiteSpace(d.State))
            {
                errors.Add($"{path}: every entry needs machine and state.");
                continue;
            }
            else if (!string.IsNullOrWhiteSpace(d.Call))
            {
                errors.Add($"{path}: `call` belongs only to {KnownFigureDefect.UnknownSubroutine} entries.");
                continue;
            }
            if (d.Kind == KnownFigureDefect.UnknownNextState
                && (string.IsNullOrWhiteSpace(d.Transition) || string.IsNullOrWhiteSpace(d.Next)))
            {
                errors.Add($"{path}: an {KnownFigureDefect.UnknownNextState} entry needs transition and next.");
                continue;
            }
            if (d.Kind == KnownFigureDefect.MissingCatchall
                && (!string.IsNullOrWhiteSpace(d.Transition) || !string.IsNullOrWhiteSpace(d.Next)))
            {
                errors.Add($"{path}: a {KnownFigureDefect.MissingCatchall} entry names a state, not a transition; remove transition and next.");
                continue;
            }
            if (!IssueUrl.IsMatch(d.Issue))
            {
                errors.Add($"{path}: {d.Describe()} needs `issue:` set to the packethacking/ax25spec issue that records it " +
                           "(https://github.com/packethacking/ax25spec/issues/<number>).");
                continue;
            }
            if (!seen.Add((d.Kind + ":" + d.Machine, d.State + d.Call, d.Transition)))
            {
                errors.Add($"{path}: {d.Describe()} is listed twice.");
                continue;
            }
            valid.Add(d);
        }
        return valid;
    }
}
