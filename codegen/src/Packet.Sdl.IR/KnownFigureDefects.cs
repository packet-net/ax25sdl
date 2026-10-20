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
    /// <summary>The page's <c>machine:</c>.</summary>
    public string Machine { get; set; } = "";
    /// <summary>The page's <c>state:</c>.</summary>
    public string State { get; set; } = "";
    /// <summary>The transition id, exactly as generated.</summary>
    public string Transition { get; set; } = "";
    /// <summary>The <c>next:</c> value the figure draws.</summary>
    public string Next { get; set; } = "";
    /// <summary>Required. The ax25spec issue that records the defect.</summary>
    public string Issue { get; set; } = "";
    public string? Note { get; set; }

    public string Describe() =>
        $"{Machine} {State} transition `{Transition}` leads to `{Next}` ({Issue})";
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
            if (string.IsNullOrWhiteSpace(d.Machine) || string.IsNullOrWhiteSpace(d.State)
                || string.IsNullOrWhiteSpace(d.Transition) || string.IsNullOrWhiteSpace(d.Next))
            {
                errors.Add($"{path}: every entry needs machine, state, transition and next.");
                continue;
            }
            if (!d.Issue.StartsWith("https://github.com/", StringComparison.Ordinal))
            {
                errors.Add($"{path}: {d.Describe()} needs `issue:` set to the URL of the issue that records it.");
                continue;
            }
            if (!seen.Add((d.Machine, d.State, d.Transition)))
            {
                errors.Add($"{path}: {d.Describe()} is listed twice.");
                continue;
            }
            valid.Add(d);
        }
        return valid;
    }
}
