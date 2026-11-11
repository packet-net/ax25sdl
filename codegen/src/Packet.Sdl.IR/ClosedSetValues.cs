using YamlDotNet.Serialization;

namespace Packet.Sdl.IR;

/// <summary>
/// Stable numeric values for the generated closed-set enums (Ax25Event,
/// Ax25ActionVerb, Ax25Guard) in the backends that number them (C#, Rust).
/// </summary>
/// <remarks>
/// <para>
/// The members are emitted in alphabetical order, so numbering them by
/// position renumbered existing members every time one was added: a silent
/// break for anyone who stores or sends the numeric value. Instead each
/// canonical name keeps the number it was first given. The registry
/// (<c>codegen/closed-set-values.yaml</c>), keyed by the emitted member name,
/// is committed; codegen adds new names at the next free number (in alphabetical order among themselves)
/// and writes the file back. A name that disappears keeps its number,
/// which is never reused, so the numbering is append-only.
/// </para>
/// <para>
/// The registry was seeded from the numbering published before stable
/// values existed (the positional order of the last release), so no
/// existing value changes.
/// </para>
/// </remarks>
public sealed class ClosedSetValues
{
    private readonly Dictionary<string, SortedDictionary<string, int>> _sets = new(StringComparer.Ordinal);

    /// <summary>Path the registry was loaded from, or null for an in-memory registry (tests, sandboxes).</summary>
    public string? Path { get; private init; }

    /// <summary>True once <see cref="Assign"/> has added a name.</summary>
    public bool Changed { get; private set; }

    public static ClosedSetValues Load(string path)
    {
        var values = new ClosedSetValues { Path = File.Exists(path) ? path : null };
        if (values.Path is null) return values;
        var raw = new DeserializerBuilder().Build()
            .Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(path))
            ?? new Dictionary<string, Dictionary<string, int>>();
        foreach (var (set, members) in raw)
        {
            var dict = new SortedDictionary<string, int>(members ?? new Dictionary<string, int>(), StringComparer.Ordinal);
            if (dict.Values.Distinct().Count() != dict.Count)
                throw new InvalidDataException($"{path}: two names in `{set}` share a value.");
            values._sets[set] = dict;
        }
        return values;
    }

    /// <summary>
    /// The value of every name in <paramref name="names"/> for closed set
    /// <paramref name="set"/>: the registered value, or the next free number
    /// for a name seen for the first time.
    /// </summary>
    public IReadOnlyDictionary<string, int> Assign(string set, IEnumerable<string> names)
    {
        if (!_sets.TryGetValue(set, out var dict))
        {
            dict = new SortedDictionary<string, int>(StringComparer.Ordinal);
            _sets[set] = dict;
        }
        var next = dict.Count == 0 ? 0 : dict.Values.Max() + 1;
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!dict.TryGetValue(name, out var value))
            {
                value = next++;
                dict[name] = value;
                Changed = true;
            }
            result[name] = value;
        }
        return result;
    }

    /// <summary>The registry as yaml: one map per closed set, names in alphabetical order.</summary>
    public string ToYaml()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# Stable numeric values for the generated closed-set enums (C# and Rust),\n");
        sb.Append("# keyed by member name. Append-only: codegen gives a new member the next\n");
        sb.Append("# free number and never reuses or changes one. Members no longer generated\n");
        sb.Append("# keep their number. Maintained by codegen; commit it with the generated code.\n");
        foreach (var (set, members) in _sets.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append('\n').Append(set).Append(":\n");
            foreach (var (name, value) in members)
                sb.Append("  \"").Append(name.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\": ").Append(value).Append('\n');
        }
        return sb.ToString();
    }
}
