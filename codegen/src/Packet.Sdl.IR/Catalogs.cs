using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Packet.Sdl.IR;

/// <summary>
/// Resolved action-verb catalog. Built from <c>spec-sdl/actions.yaml</c>;
/// empty when the file is absent (soft passthrough mode — every verb
/// passes through verbatim).
/// </summary>
public sealed class ActionCatalog
{
    /// <summary>Map from any known spelling (canonical or alias) to canonical name.</summary>
    public Dictionary<string, string> CanonicalLookup { get; } = new(StringComparer.Ordinal);

    /// <summary>Map from canonical name to its declared SDL kind (signal_upper, signal_lower, etc.).</summary>
    public Dictionary<string, string> CanonicalKind { get; } = new(StringComparer.Ordinal);

    /// <summary>Every alias declared in the catalog (i.e. non-canonical spellings).</summary>
    public HashSet<string> DeclaredAliases { get; } = new(StringComparer.Ordinal);

    /// <summary>Aliases that any <see cref="Validation"/>-driven path-step normalisation actually substituted on.</summary>
    public HashSet<string> SeenAliases { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Load and resolve <c>actions.yaml</c>. Returns an empty catalog
    /// (passthrough mode) when the file is absent.
    /// </summary>
    /// <exception cref="InvalidDataException">Malformed catalog: unknown kind group, duplicate canonical, alias claimed twice, etc.</exception>
    public static ActionCatalog Load(string path)
    {
        var catalog = new ActionCatalog();
        if (!File.Exists(path)) return catalog;

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(LowerCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var raw = deserializer.Deserialize<Dictionary<string, List<ActionCatalogEntry>>>(File.ReadAllText(path))
                  ?? new Dictionary<string, List<ActionCatalogEntry>>(StringComparer.Ordinal);

        foreach (var (kind, entries) in raw)
        {
            if (!ValidActionKinds.Contains(kind))
                throw new InvalidDataException($"{path}: unknown action kind group `{kind}`. Valid: {string.Join(", ", ValidActionKinds)}.");

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Name))
                    throw new InvalidDataException($"{path}: entry under `{kind}:` is missing `name:`");

                if (!catalog.CanonicalKind.TryAdd(entry.Name, kind))
                    throw new InvalidDataException($"{path}: canonical name `{entry.Name}` declared twice");

                if (!catalog.CanonicalLookup.TryAdd(entry.Name, entry.Name))
                    throw new InvalidDataException($"{path}: canonical name `{entry.Name}` collides with an alias declared earlier");

                foreach (var alias in entry.Aliases ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(alias))
                        throw new InvalidDataException($"{path}: empty alias under canonical name `{entry.Name}`");
                    if (!catalog.CanonicalLookup.TryAdd(alias, entry.Name))
                        throw new InvalidDataException($"{path}: alias `{alias}` is claimed by two canonical names");
                    catalog.DeclaredAliases.Add(alias);
                }
            }
        }

        return catalog;
    }

    private static readonly HashSet<string> ValidActionKinds = new(
        new[] { "signal_upper", "signal_lower", "processing", "subroutine", "internal_out" },
        StringComparer.Ordinal);
}

/// <summary>One entry under a kind group in <c>actions.yaml</c>.</summary>
public sealed class ActionCatalogEntry
{
    public string Name { get; set; } = "";
    public List<string>? Aliases { get; set; }
}

/// <summary>
/// Resolved guard-predicate catalog. Built from <c>spec-sdl/predicates.yaml</c>;
/// empty when the file is absent (soft passthrough mode — every predicate
/// atom passes through verbatim). The guard analogue of <see cref="ActionCatalog"/>.
/// </summary>
/// <remarks>
/// An SDL guard expression is a conjunction of (optionally negated) decision
/// predicates; this catalog is the vocabulary of the bare atoms. Decision
/// <c>predicate:</c> fields are always single atoms (no <c>not</c> / <c>and</c>
/// / <c>or</c> — the codegen composes the boolean expression), so the catalog
/// is a flat namespace with no kind grouping (the YAML group keys are
/// documentation only and are not surfaced here).
/// </remarks>
public sealed class PredicateCatalog
{
    /// <summary>Map from any known atom spelling (canonical or alias) to canonical atom.</summary>
    public Dictionary<string, string> CanonicalLookup { get; } = new(StringComparer.Ordinal);

    /// <summary>Every canonical atom declared in the catalog.</summary>
    public HashSet<string> Canonicals { get; } = new(StringComparer.Ordinal);

    /// <summary>Every alias declared in the catalog (i.e. non-canonical spellings).</summary>
    public HashSet<string> DeclaredAliases { get; } = new(StringComparer.Ordinal);

    /// <summary>Aliases that decision-predicate normalisation actually substituted on.</summary>
    public HashSet<string> SeenAliases { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Load and resolve <c>predicates.yaml</c>. Returns an empty catalog
    /// (passthrough mode) when the file is absent.
    /// </summary>
    /// <exception cref="InvalidDataException">Malformed catalog: duplicate canonical, alias claimed twice, etc.</exception>
    public static PredicateCatalog Load(string path)
    {
        var catalog = new PredicateCatalog();
        if (!File.Exists(path)) return catalog;

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(LowerCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        // predicates.yaml mirrors actions.yaml's shape (group → list of
        // { name, aliases }), but the group key is documentation only —
        // predicates have no kind. Flatten every group into one namespace.
        var raw = deserializer.Deserialize<Dictionary<string, List<PredicateCatalogEntry>>>(File.ReadAllText(path))
                  ?? new Dictionary<string, List<PredicateCatalogEntry>>(StringComparer.Ordinal);

        foreach (var (group, entries) in raw)
        {
            foreach (var entry in entries ?? new List<PredicateCatalogEntry>())
            {
                if (string.IsNullOrWhiteSpace(entry.Name))
                    throw new InvalidDataException($"{path}: entry under `{group}:` is missing `name:`");

                if (!catalog.Canonicals.Add(entry.Name))
                    throw new InvalidDataException($"{path}: canonical atom `{entry.Name}` declared twice");

                if (!catalog.CanonicalLookup.TryAdd(entry.Name, entry.Name))
                    throw new InvalidDataException($"{path}: canonical atom `{entry.Name}` collides with an alias declared earlier");

                foreach (var alias in entry.Aliases ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(alias))
                        throw new InvalidDataException($"{path}: empty alias under canonical atom `{entry.Name}`");
                    if (!catalog.CanonicalLookup.TryAdd(alias, entry.Name))
                        throw new InvalidDataException($"{path}: alias `{alias}` is claimed by two canonical atoms");
                    catalog.DeclaredAliases.Add(alias);
                }
            }
        }

        return catalog;
    }
}

/// <summary>One entry under a group in <c>predicates.yaml</c>.</summary>
public sealed class PredicateCatalogEntry
{
    public string Name { get; set; } = "";
    public List<string>? Aliases { get; set; }
}

/// <summary>Helpers around the events catalog.</summary>
public static class EventCatalog
{
    /// <summary>
    /// Load the flat set of event names from <c>events.yaml</c>. Returns
    /// empty when absent (events.yaml is documentation-only; the codegen
    /// only consults it for transcription-typo detection).
    /// </summary>
    public static HashSet<string> Load(string path)
    {
        var events = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return events;

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(LowerCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var raw = deserializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path))
                  ?? new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (_, group) in raw)
        {
            foreach (var name in group)
            {
                if (!string.IsNullOrWhiteSpace(name)) events.Add(name);
            }
        }
        return events;
    }
}

/// <summary>
/// The catch-all event family. The transcriber gives every catch-all input
/// ("All Other Primitives", "All Other Commands", "All Other DL Primitives")
/// an id starting <c>all_other_</c>, with a <c>__from_upper_layer</c> or
/// <c>__from_lower_layer</c> suffix where the figures draw both sides, so the
/// family is defined by that prefix rather than a fixed list. Each id must
/// still be in <c>events.yaml</c> (the catalogue lint checks that).
/// </summary>
public static class CatchallEvents
{
    public const string Prefix = "all_other_";

    public static bool IsCatchall(string? eventId) =>
        eventId is not null && eventId.StartsWith(Prefix, StringComparison.Ordinal);
}
