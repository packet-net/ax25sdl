using Packet.Sdl.Explorer;
using Packet.Sdl.Interpreter;

namespace Packet.Sdl.Explorer.Tests;

/// <summary>Locates the JSON table sets the suite runs against: the committed current tables and the pre-fix fixtures.</summary>
internal static class Fixtures
{
    private static readonly Lazy<string> RepoRoot = new(FindRepoRoot);

    /// <summary>spec/json on the branch: the current tables (referenced, never copied).</summary>
    public static string CurrentDir => Path.Combine(RepoRoot.Value, "spec", "json");

    /// <summary>packethacking/ax25spec abd46b0: has the #38 fix, lacks the #40 out-of-window guard.</summary>
    public static string Pre40Dir => Path.Combine(RepoRoot.Value, "codegen", "tests", "Packet.Sdl.Explorer.Tests", "fixtures", "tables-pre40");

    /// <summary>packethacking/ax25spec fb727db: SREJ go-back-N in figc4.5 (pre #38) and no #40 guard.</summary>
    public static string Pre38Dir => Path.Combine(RepoRoot.Value, "codegen", "tests", "Packet.Sdl.Explorer.Tests", "fixtures", "tables-pre38");

    /// <summary>The TlaCheck seeded fixture in which Connected answers DISC with UA but stays Connected (codegen/src/Packet.Sdl.TlaCheck/fixtures/README.md).</summary>
    public static string ReleaseStuckDir => Path.Combine(RepoRoot.Value, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-release-stuck");

    /// <summary>The TlaCheck seeded fixture in which Connected ignores a DM and stays up.</summary>
    public static string DmIgnoredDir => Path.Combine(RepoRoot.Value, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-dm-ignored");

    /// <summary>The TlaCheck seeded fixture in which Connected ignores DL-CONNECT Request.</summary>
    /// <summary>The TlaCheck seeded fixture in which Awaiting Connection reports a completed connect as DL-DISCONNECT Indication.</summary>
    public static string ConfirmAsDisconnectDir => Path.Combine(RepoRoot.Value, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-confirm-as-disconnect");

    /// <summary>The TlaCheck seeded fixture in which an RNR in Connected does not set peer receiver busy.</summary>
    public static string RnrIgnoredDir => Path.Combine(RepoRoot.Value, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-rnr-ignored");

    /// <summary>The TlaCheck seeded fixture in which Connected's own-busy test on I frames reads peer receiver busy.</summary>
    public static string BusyDeliversDir => Path.Combine(RepoRoot.Value, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-busy-delivers");

    /// <summary>The TlaCheck seeded fixture in which a busy station answers a poll with RR (Enquiry_Response's busy path).</summary>
    public static string BusySaysRrDir => Path.Combine(RepoRoot.Value, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-busy-says-rr");

    /// <summary>The TlaCheck seeded fixture in which a busy station's own T1 poll is RR (Transmit_Enquiry's busy path).</summary>
    public static string EnquirySaysRrDir => Path.Combine(RepoRoot.Value, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-enquiry-says-rr");

    /// <summary>The TlaCheck seeded fixture in which Connected swallows a UI command with P = 0.</summary>
    public static string UiSwallowedDir => Path.Combine(RepoRoot.Value, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-ui-swallowed");

    /// <summary>The TlaCheck seeded fixture in which DL-UNIT-DATA Request in Connected sends nothing.</summary>
    public static string UiUnsentDir => Path.Combine(RepoRoot.Value, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-ui-unsent");

    /// <summary>The TlaCheck seeded fixture in which Connected does not answer a UI poll.</summary>
    public static string UiPollUnansweredDir => Path.Combine(RepoRoot.Value, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-ui-poll-unanswered");

    /// <summary>The TlaCheck seeded fixture in which UI Check delivers a UI response.</summary>
    public static string UiResponseDeliveredDir => Path.Combine(RepoRoot.Value, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-ui-response-delivered");

    /// <summary>The TlaCheck seeded fixture in which Connected has no T3 expiry arm.</summary>
    public static string T3DeadDir => Path.Combine(RepoRoot.Value, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-t3-dead");

    /// <summary>The TlaCheck seeded fixture in which neither end of a cold connect starts T3.</summary>
    public static string NoKeepaliveDir => Path.Combine(RepoRoot.Value, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-no-keepalive");

    public static string ReconnectSwallowedDir => Path.Combine(RepoRoot.Value, "codegen", "src", "Packet.Sdl.TlaCheck", "fixtures", "tables-reconnect-swallowed");

    private static readonly Dictionary<string, TableSet> Cache = new(StringComparer.Ordinal);

    public static TableSet Tables(string dir)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(dir, out var tables))
            {
                tables = TableLoader.Load(dir);
                Cache[dir] = tables;
            }
            return tables;
        }
    }

    public static ExplorationResult Run(ExplorerOptions options) => Explorer.Run(options, Tables(options.TablesDir));

    private static string FindRepoRoot()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(Fixtures).Assembly.Location)!;
        var d = new DirectoryInfo(assemblyDir);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "spec-sdl")))
            d = d.Parent!;
        if (d is null)
            throw new InvalidOperationException($"can't locate repo root walking up from {assemblyDir}: no spec-sdl/ directory in any ancestor");
        return d.FullName;
    }
}
