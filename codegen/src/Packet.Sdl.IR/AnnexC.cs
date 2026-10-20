namespace Packet.Sdl.IR;

/// <summary>
/// One Annex C state machine: its <c>machine:</c> id in the yaml, the
/// graphml filename prefix that selects it, and the directory its pages
/// live in under <c>spec-sdl/&lt;revision&gt;/</c>.
/// </summary>
/// <param name="Id">The yaml <c>machine:</c> value (must be in the sdl-machine schema's enum).</param>
/// <param name="FilePrefix">The graphml filename prefix before the first underscore, e.g. <c>LinkMux</c> in <c>LinkMux_Idle.graphml</c>.</param>
/// <param name="Directory">The machine directory under <c>spec-sdl/&lt;revision&gt;/</c>, e.g. <c>link-multiplexer</c>.</param>
/// <param name="Title">The machine's name as Annex C titles it.</param>
public sealed record AnnexCMachine(string Id, string FilePrefix, string Directory, string Title);

/// <summary>
/// One Annex C page (a state, or a subroutines page) and the figure that
/// draws it.
/// </summary>
/// <param name="Machine">The owning machine's <see cref="AnnexCMachine.Id"/>.</param>
/// <param name="Key">The part of the graphml filename after the machine prefix, e.g. <c>Idle</c> in <c>LinkMux_Idle.graphml</c>. For a state page this is the yaml <c>state:</c> value.</param>
/// <param name="Figure">The yaml <c>source.figure:</c> value, e.g. <c>figc3.1</c>. A state drawn over several figures (C4.4a to C4.4c) has one id without the letter.</param>
/// <param name="PdfPages">The page(s) of the rev 4 Oct 2025 PDF the figure is on.</param>
/// <param name="IsSubroutines">True for a subroutines page (no state, walked by the subroutines walker).</param>
public sealed record AnnexCPage(string Machine, string Key, string Figure, string PdfPages, bool IsSubroutines = false);

/// <summary>
/// The table of every AX.25 v2.2 Annex C machine and page, taken from the
/// figure captions in <c>spec-sdl/v2.2-errata/ax.25 v2.2 rev 4 Oct 2025.pdf</c>.
/// The transcriber uses it to name the machine and figure of a graphml it
/// walks; codegen uses it to tell a state that is drawn in Annex C but not
/// transcribed yet from a typo.
/// </summary>
/// <remarks>
/// <para>
/// State keys are the PascalCase state name as the figures draw it in their
/// state symbols, keeping the figures' spelling (the Link Multiplexer's
/// "Sieze Pending", like the SIEZE in its primitives). Where a page's own
/// symbols disagree, the caption decides: Figure C3.3's entry symbol reads
/// "1 Sieze Pending" but its title and caption say Seized.
/// </para>
/// <para>
/// Subroutines pages are keyed by their caption with the machine name taken
/// off, ending in <c>Subroutines</c>. Data Link's figure C4.7a and C4.7b are
/// one page, <c>DataLink_Subroutines.graphml</c>, as already committed.
/// </para>
/// </remarks>
public static class AnnexC
{
    /// <summary>The <c>source.spec:</c> value every page carries.</summary>
    public const string Spec = "ax.25.2.2.4_Oct_25";

    public static IReadOnlyList<AnnexCMachine> Machines { get; } =
    [
        new("simplex_phy",          "SimplexPhy",         "simplex-physical",      "Simplex Physical"),
        new("duplex_phy",           "DuplexPhy",          "duplex-physical",       "Duplex Physical"),
        new("link_mux",             "LinkMux",            "link-multiplexer",      "Link Multiplexer"),
        new("data_link",            "DataLink",           "data-link",             "Data Link"),
        new("management_data_link", "ManagementDataLink", "management-data-link",  "Management Data Link"),
        // Annex C6 draws the segmenter and the reassembler as two machines on
        // one appendix. Each gets its own directory: both have a Ready state,
        // and the yaml is named by state.
        new("segmenter",            "Segmenter",          "segmenter",             "Segmenter"),
        new("reassembler",          "Reassembler",        "reassembler",           "Reassembler"),
    ];

    public static IReadOnlyList<AnnexCPage> Pages { get; } =
    [
        // C2a Simplex Physical
        new("simplex_phy", "Ready",                  "figc2a.1", "61"),
        new("simplex_phy", "Receiving",              "figc2a.2", "62"),
        new("simplex_phy", "TransmitterSuppression", "figc2a.3", "63"),
        new("simplex_phy", "TransmitterStart",       "figc2a.4", "64"),
        new("simplex_phy", "Transmitting",           "figc2a.5", "65"),
        new("simplex_phy", "Digipeating",            "figc2a.6", "66"),
        new("simplex_phy", "ReceiverStart",          "figc2a.7", "67"),
        new("simplex_phy", "Subroutines",            "figc2a.s", "68", IsSubroutines: true),

        // C2b Duplex Physical (a receiver and a transmitter, both numbered from 0)
        new("duplex_phy", "ReceiverReady",    "figc2b.1", "72"),
        new("duplex_phy", "Receiving",        "figc2b.2", "73"),
        new("duplex_phy", "TransmitterReady", "figc2b.3", "74"),
        new("duplex_phy", "TransmitterStart", "figc2b.4", "75"),
        new("duplex_phy", "Transmitting",     "figc2b.5", "76"),

        // C3 Link Multiplexer
        new("link_mux", "Idle",         "figc3.1", "81"),
        new("link_mux", "SiezePending", "figc3.2", "82"),
        new("link_mux", "Seized",       "figc3.3", "83"),
        new("link_mux", "Subroutines",  "figc3.4", "84", IsSubroutines: true),

        // C4 Data Link
        new("data_link", "Disconnected",          "figc4.1", "90"),
        new("data_link", "AwaitingConnection",    "figc4.2", "91"),
        new("data_link", "AwaitingRelease",       "figc4.3", "92"),
        new("data_link", "Connected",             "figc4.4", "93-95"),
        new("data_link", "TimerRecovery",         "figc4.5", "96-100"),
        new("data_link", "AwaitingV22Connection", "figc4.6", "101-102"),
        new("data_link", "Subroutines",           "figc4.7", "103-104", IsSubroutines: true),

        // C5 Management Data Link
        new("management_data_link", "Ready",                                     "figc5.1", "107"),
        new("management_data_link", "Negotiating",                               "figc5.2", "108"),
        new("management_data_link", "ClassesOfProceduresNegotiationSubroutines", "figc5.3", "109", IsSubroutines: true),
        new("management_data_link", "OptionalFunctionsSubroutines",              "figc5.4", "110", IsSubroutines: true),
        new("management_data_link", "N1NegotiationSubroutines",                  "figc5.5", "111", IsSubroutines: true),
        new("management_data_link", "WindowNegotiationSubroutines",              "figc5.6", "112", IsSubroutines: true),
        new("management_data_link", "RetryNotificationSubroutines",              "figc5.7", "113", IsSubroutines: true),
        new("management_data_link", "T1NegotiationSubroutines",                  "figc5.8", "114", IsSubroutines: true),

        // C6 Segmenter / Reassembler (captions written "Figure C-6.x")
        new("segmenter",   "Ready",                "figc6.1", "119"),
        new("reassembler", "Ready",                "figc6.2", "120"),
        new("reassembler", "ReassemblingData",     "figc6.3", "121"),
        new("reassembler", "ReassemblingUnitData", "figc6.4", "122"),
    ];

    /// <summary>
    /// Machines already published in the generated packages. Codegen never
    /// withholds them: a transition leading to a state with no page is an
    /// error for these machines, not a sign of work in progress, so a
    /// deleted yaml can't quietly remove generated code. Their generated
    /// files also keep the
    /// bare yaml stem (<c>connected.g.go</c>, <c>ready.g.json</c>), because
    /// consumers import them by those names.
    /// </summary>
    public static IReadOnlySet<string> PublishedMachines { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "data_link", "management_data_link" };

    /// <summary>The stem of a <c>*.sdl.yaml</c> path: <c>.../yaml/sieze_pending.sdl.yaml</c> gives <c>sieze_pending</c>.</summary>
    public static string YamlStem(string sourcePath) =>
        Path.GetFileNameWithoutExtension(sourcePath).Replace(".sdl", string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// The stem every backend names a page's generated files with. State
    /// names recur across machines (Ready is a state of four of them, and
    /// several machines have a Subroutines page), so every machine but those
    /// in <see cref="PublishedMachines"/> is qualified:
    /// <c>link_mux_idle.g.go</c>, <c>simplex_phy_ready.g.json</c>.
    /// Codegen also fails outright if two pages still end up with one stem.
    /// </summary>
    public static string OutputStem(string machine, string sourcePath)
    {
        var stem = YamlStem(sourcePath);
        return PublishedMachines.Contains(machine) ? stem : machine + "_" + stem;
    }

    /// <summary>The machine whose graphml filename prefix is <paramref name="prefix"/>, or null.</summary>
    public static AnnexCMachine? MachineByPrefix(string prefix) =>
        Machines.FirstOrDefault(m => string.Equals(m.FilePrefix, prefix, StringComparison.Ordinal));

    /// <summary>The machine whose yaml id is <paramref name="id"/>, or null.</summary>
    public static AnnexCMachine? MachineById(string id) =>
        Machines.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));

    /// <summary>The page <paramref name="key"/> of machine <paramref name="machineId"/>, or null.</summary>
    public static AnnexCPage? Page(string machineId, string key) =>
        Pages.FirstOrDefault(p => string.Equals(p.Machine, machineId, StringComparison.Ordinal)
                               && string.Equals(p.Key, key, StringComparison.Ordinal));

    /// <summary>Every state (not subroutines page) Annex C draws for machine <paramref name="machineId"/>.</summary>
    public static IReadOnlyList<string> StatesOf(string machineId) =>
        Pages.Where(p => !p.IsSubroutines && string.Equals(p.Machine, machineId, StringComparison.Ordinal))
             .Select(p => p.Key)
             .ToList();

    /// <summary>
    /// Splits a graphml filename (<c>LinkMux_Idle.graphml</c>) into its
    /// machine and page, looked up in the table. Throws an
    /// <see cref="InvalidDataException"/> naming the valid choices when the
    /// prefix or the page key is not in Annex C.
    /// </summary>
    public static (AnnexCMachine Machine, AnnexCPage Page) ResolveGraphmlFilename(string filename)
    {
        var bare = Path.GetFileNameWithoutExtension(filename);
        var underscoreAt = bare.IndexOf('_');
        if (underscoreAt < 0)
            throw new InvalidDataException(
                $"graphml filename '{filename}' doesn't follow the <Machine>_<Page>.graphml convention.");
        var prefix = bare[..underscoreAt];
        var key = bare[(underscoreAt + 1)..];

        var machine = MachineByPrefix(prefix)
            ?? throw new InvalidDataException(
                $"graphml filename '{filename}': machine prefix '{prefix}' is not an Annex C machine. " +
                $"Use one of: {string.Join(", ", Machines.Select(m => m.FilePrefix))}.");
        var page = Page(machine.Id, key)
            ?? throw new InvalidDataException(
                $"graphml filename '{filename}': '{key}' is not a page of {machine.Title} ({machine.Id}) in Annex C. " +
                $"Use one of: {string.Join(", ", Pages.Where(p => p.Machine == machine.Id).Select(p => p.Key))}.");
        return (machine, page);
    }
}
