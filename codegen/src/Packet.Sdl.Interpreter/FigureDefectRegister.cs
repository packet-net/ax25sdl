using System.Globalization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Packet.Sdl.Interpreter;

/// <summary>
/// The hand-maintained register of open upstream figure-defect issues
/// (<c>traces/figure-defects.yaml</c>): which of them a single-machine
/// golden trace can express as a strict xfail (<c>expected_failure:</c>),
/// and why not for the rest. The Interpreter tests hold it to the traces
/// (offline) and to the upstream issue tracker (online). See
/// <c>docs/golden-traces.md</c>.
/// </summary>
public sealed class FigureDefectRegisterDoc
{
    /// <summary>GitHub <c>owner/name</c> of the repository the issues live in.</summary>
    public string Repository { get; set; } = "";

    /// <summary>The issue label that marks a figure defect there.</summary>
    public string Label { get; set; } = "";

    public List<FigureDefectDoc> Defects { get; set; } = new();
}

/// <summary>One registered figure defect.</summary>
public sealed class FigureDefectDoc
{
    /// <summary>Full issue link, exactly as traces carry it in <c>expected_failure:</c>.</summary>
    public string Issue { get; set; } = "";

    /// <summary>One-line paraphrase of the issue title (not compared with upstream; titles drift).</summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// True when a single-machine trace can fail because of this defect
    /// (and at least one trace must then carry the issue as
    /// <c>expected_failure:</c>); false when it cannot, with <see cref="Reason"/> saying why.
    /// </summary>
    public bool TraceExpressible { get; set; }

    /// <summary>Why no trace can express the defect. Required when <see cref="TraceExpressible"/> is false.</summary>
    public string? Reason { get; set; }
}

/// <summary>YAML loader for the figure-defect register.</summary>
public static class FigureDefectRegister
{
    /// <summary>File name of the register inside <c>traces/</c>.</summary>
    public const string FileName = "figure-defects.yaml";

    /// <summary>Load and structurally validate the register.</summary>
    /// <exception cref="InvalidDataException">Unparseable YAML, unknown keys, or a structurally invalid register.</exception>
    public static FigureDefectRegisterDoc Load(string path)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        FigureDefectRegisterDoc doc;
        try
        {
            doc = deserializer.Deserialize<FigureDefectRegisterDoc>(File.ReadAllText(path))
                  ?? throw new InvalidDataException($"{path}: empty register");
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new InvalidDataException($"{path}: {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(doc.Repository) || doc.Repository.Split('/').Length != 2)
            throw new InvalidDataException($"{path}: `repository:` must be a GitHub owner/name");
        if (string.IsNullOrWhiteSpace(doc.Label))
            throw new InvalidDataException($"{path}: missing `label:`");
        if (doc.Defects.Count == 0)
            throw new InvalidDataException($"{path}: the register lists no defects");

        var seen = new HashSet<int>();
        for (var i = 0; i < doc.Defects.Count; i++)
        {
            var entry = doc.Defects[i];
            var number = IssueNumber(entry.Issue, doc.Repository)
                ?? throw new InvalidDataException(
                    $"{path}: entry {i + 1}: `issue:` must be https://github.com/{doc.Repository}/issues/<n>, got `{entry.Issue}`");
            if (!seen.Add(number))
                throw new InvalidDataException($"{path}: issue #{number} is listed twice");
            if (string.IsNullOrWhiteSpace(entry.Title))
                throw new InvalidDataException($"{path}: issue #{number} has no `title:`");
            if (!entry.TraceExpressible && string.IsNullOrWhiteSpace(entry.Reason))
                throw new InvalidDataException(
                    $"{path}: issue #{number} is `trace_expressible: false` but gives no `reason:`");
        }

        return doc;
    }

    /// <summary>
    /// The issue number carried by an issue link of the form
    /// <c>https://github.com/{repository}/issues/{n}</c>; null when the link
    /// is not one (a different repository, a trailing fragment, and so on).
    /// </summary>
    public static int? IssueNumber(string issue, string repository)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(repository);

        var prefix = $"https://github.com/{repository}/issues/";
        if (!issue.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        return int.TryParse(issue.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : null;
    }
}
