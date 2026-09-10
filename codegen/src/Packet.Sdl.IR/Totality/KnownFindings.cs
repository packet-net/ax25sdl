using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Packet.Sdl.IR.Totality;

/// <summary>
/// One entry of <c>codegen/lint-known-findings.yaml</c>: a totality /
/// determinism finding on the real figures that is known, filed upstream,
/// and must not block the pipeline until the ax25spec fix lands and the pin
/// is bumped.
/// </summary>
public sealed class KnownFinding
{
    /// <summary><c>hole</c> or <c>nondeterminism</c>.</summary>
    public string Kind { get; set; } = "";
    public string Machine { get; set; } = "";
    /// <summary>State-page finding: the state. Exactly one of <see cref="State"/> / <see cref="Subroutine"/>.</summary>
    public string? State { get; set; }
    public string? Subroutine { get; set; }
    /// <summary>State-page finding: the <c>on:</c> event.</summary>
    public string? Event { get; set; }
    /// <summary>The cube exactly as the lint reports it: every fixed atom and its value. Live atoms the finding does not depend on are omitted.</summary>
    public Dictionary<string, bool> When { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Nondeterminism only: every transition / path id that matches (order irrelevant).</summary>
    public List<string>? Transitions { get; set; }
    /// <summary>Required. The upstream issue tracking the fix.</summary>
    public string Issue { get; set; } = "";
    public string? Note { get; set; }

    internal string Describe()
        => Subroutine is not null
            ? $"{Kind} in {Machine} subroutine `{Subroutine}`"
            : $"{Kind} in {Machine} state `{State}` on `{Event}`";
}

/// <summary>Top-level shape of the allow-list file.</summary>
public sealed class KnownFindingsFile
{
    public List<KnownFinding> Findings { get; set; } = new();
}

/// <summary>Result of applying the allow-list to a report.</summary>
public sealed class AppliedFindings
{
    /// <summary>Findings that fail the run (not allow-listed), plus every allow-list contract violation (stale, duplicate, malformed).</summary>
    public required IReadOnlyList<string> Errors { get; init; }
    /// <summary>Allow-listed findings, downgraded to warnings, each suffixed with its issue.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// The allow-list contract: each entry identifies exactly one finding; a
/// matching finding downgrades to a warning; an entry that matches nothing
/// fails the run (the list can never go stale silently); two entries for
/// one finding fail the run; every entry needs an <c>issue:</c> URL. Same
/// discipline as the golden-trace suite's <c>expected_failure</c>.
/// </summary>
public static class KnownFindings
{
    /// <summary>
    /// Load the allow-list. A missing file at the default location is an
    /// empty list; a missing file at a path the caller passed explicitly is
    /// an error, since a typo in the path would otherwise silently disable
    /// every entry.
    /// </summary>
    public static IReadOnlyList<KnownFinding> Load(string path, bool explicitPath)
    {
        if (!File.Exists(path))
        {
            if (explicitPath)
                throw new InvalidOperationException($"--known-findings `{path}` does not exist.");
            return Array.Empty<KnownFinding>();
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(LowerCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var file = deserializer.Deserialize<KnownFindingsFile>(File.ReadAllText(path)) ?? new KnownFindingsFile();
        var entries = file.Findings ?? new List<KnownFinding>();

        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var loc = $"{path}: findings[{i}]";
            if (e.Kind is not ("hole" or "nondeterminism"))
                throw new InvalidOperationException($"{loc}: `kind:` must be `hole` or `nondeterminism` (was `{e.Kind}`).");
            if (string.IsNullOrWhiteSpace(e.Machine))
                throw new InvalidOperationException($"{loc}: missing `machine:`.");
            var hasState = !string.IsNullOrWhiteSpace(e.State);
            var hasSub = !string.IsNullOrWhiteSpace(e.Subroutine);
            if (hasState == hasSub)
                throw new InvalidOperationException($"{loc}: exactly one of `state:` / `subroutine:` is required.");
            if (hasState && string.IsNullOrWhiteSpace(e.Event))
                throw new InvalidOperationException($"{loc}: a state-page finding needs `event:`.");
            if (hasSub && !string.IsNullOrWhiteSpace(e.Event))
                throw new InvalidOperationException($"{loc}: a subroutine finding has no `event:`.");
            if (e.Kind == "nondeterminism" && (e.Transitions is null || e.Transitions.Count < 2))
                throw new InvalidOperationException($"{loc}: a nondeterminism finding needs `transitions:` listing the two or more ids that match.");
            if (e.Kind == "hole" && e.Transitions is { Count: > 0 })
                throw new InvalidOperationException($"{loc}: a hole finding has no `transitions:`.");
            if (string.IsNullOrWhiteSpace(e.Issue)
                || !(e.Issue.StartsWith("https://", StringComparison.Ordinal) || e.Issue.StartsWith("http://", StringComparison.Ordinal)))
                throw new InvalidOperationException($"{loc}: `issue:` is required and must be a URL (was `{e.Issue}`).");
            e.When ??= new Dictionary<string, bool>(StringComparer.Ordinal);
        }
        return entries;
    }

    /// <summary>Apply the allow-list to a report: split findings into errors and warnings and enforce the strict-xfail contract.</summary>
    public static AppliedFindings Apply(TotalityReport report, IReadOnlyList<KnownFinding> known, string allowListPath)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var matchedBy = new Dictionary<TotalityFinding, KnownFinding>();
        var hits = new int[known.Count];

        foreach (var f in report.Findings)
        {
            for (int i = 0; i < known.Count; i++)
            {
                if (!Matches(known[i], f)) continue;
                hits[i]++;
                if (matchedBy.TryGetValue(f, out var earlier))
                {
                    errors.Add($"{allowListPath}: two entries match the same finding ({known[i].Describe()}; issues {earlier.Issue} and {known[i].Issue}). Keep one.");
                    continue;
                }
                matchedBy[f] = known[i];
            }
        }

        foreach (var f in report.Findings)
        {
            if (matchedBy.TryGetValue(f, out var k))
                warnings.Add($"{f.Message} (known: {k.Issue}{(string.IsNullOrWhiteSpace(k.Note) ? "" : "; " + k.Note)})");
            else
                errors.Add(f.Message);
        }

        for (int i = 0; i < known.Count; i++)
        {
            if (hits[i] == 0)
            {
                errors.Add(
                    $"{allowListPath}: entry for {known[i].Describe()} ({known[i].Issue}) matches no current finding. " +
                    "The figure was fixed, the arm changed, or the entry is wrong: remove or correct the entry. " +
                    "A stale allow-list entry is an error so the list can never quietly outlive what it excuses.");
            }
            else if (hits[i] > 1)
            {
                errors.Add($"{allowListPath}: entry for {known[i].Describe()} ({known[i].Issue}) matches {hits[i]} findings; an entry must identify exactly one.");
            }
        }

        return new AppliedFindings { Errors = errors, Warnings = warnings };
    }

    private static bool Matches(KnownFinding k, TotalityFinding f)
    {
        var kind = f.Kind == FindingKind.Hole ? "hole" : "nondeterminism";
        if (!string.Equals(k.Kind, kind, StringComparison.Ordinal)) return false;
        if (!string.Equals(k.Machine, f.Machine, StringComparison.Ordinal)) return false;
        if (!string.Equals(k.State ?? "", f.State ?? "", StringComparison.Ordinal)) return false;
        if (!string.Equals(k.Subroutine ?? "", f.Subroutine ?? "", StringComparison.Ordinal)) return false;
        if (!string.Equals(k.Event ?? "", f.Event ?? "", StringComparison.Ordinal)) return false;
        if (k.When.Count != f.When.Count) return false;
        foreach (var (atom, value) in f.When)
        {
            if (!k.When.TryGetValue(atom, out var kv) || kv != value) return false;
        }
        if (f.Kind == FindingKind.Nondeterminism)
        {
            var a = (k.Transitions ?? new List<string>()).OrderBy(s => s, StringComparer.Ordinal);
            if (!a.SequenceEqual(f.Transitions, StringComparer.Ordinal)) return false;
        }
        return true;
    }
}
