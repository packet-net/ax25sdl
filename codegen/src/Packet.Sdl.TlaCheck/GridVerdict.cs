namespace Packet.Sdl.TlaCheck;

/// <summary>
/// How a grid cell is judged once both tools have run. The explorer stops at
/// its state cap or depth limit; TLC has neither. A cell whose explorer search
/// was cut short by either is "capped": its state count is not compared, and
/// neither is a counterexample's length or path, since the explorer's search
/// was not the whole graph. The verdict kind is still compared where both
/// tools found a violation (the explorer finds a deadlock in the part it
/// explored even when the cap cuts the rest off).
/// </summary>
internal static class GridVerdict
{
    /// <summary>
    /// Which limit cut the explorer's search short: "depth", "states" or ""
    /// (none, or not a grid cell, or a row that relies on the depth bound).
    /// Set whatever the explorer's verdict, so a violation found after the
    /// cap was hit (a deadlock, a reset or give-up loss) is capped too.
    /// </summary>
    public static string Cap(bool gridCell, bool depthBoundRow, bool depthBoundHit, bool stateBoundHit) =>
        !gridCell || depthBoundRow ? "" : depthBoundHit ? "depth" : stateBoundHit ? "states" : "";

    /// <summary>A verdict kind that names a violation (not clean, a bound, a timeout or a failure).</summary>
    public static bool IsViolation(string kind) => kind is not ("clean" or "bound" or "error" or "timeout" or "");

    /// <summary>
    /// The agreement and its reason. An uncapped cell agrees when the full
    /// comparison found no difference (<paramref name="differences"/>). A
    /// capped cell never counts as agreeing; its reason is set only when both
    /// tools found a violation and the kinds differ, which makes it a
    /// disagreement.
    /// </summary>
    public static (bool Agree, string Why) Judge(string capped, string explorerKind, string tlcKind, IReadOnlyList<string> differences)
    {
        if (capped.Length == 0) return (differences.Count == 0, string.Join("; ", differences));
        return IsViolation(explorerKind) && IsViolation(tlcKind) && explorerKind != tlcKind
            ? (false, $"verdict {explorerKind} vs {tlcKind}")
            : (false, "");
    }

    /// <summary>The label a grid cell's log line starts with.</summary>
    public static string Label(Row r) =>
        r.Capped.Length > 0
            ? r.Why.Length > 0 ? "CAPPED, VERDICTS DIFFER:"
            : r.CappedFinding ? "CAPPED, TLC FOUND A VIOLATION (look at it):"
            : IsViolation(r.ExplorerKind) && r.ExplorerKind == r.TlcKind ? "capped, same verdict:"
            : "capped, not compared:"
        : r.Agree ? "agree   "
        : r.TlcKind == "timeout" ? "TIMEOUT "
        : r.TlcKind == "error" || r.ExplorerKind.Length == 0 ? "ERROR   "
        : "DISAGREE";

    /// <summary>A cell that passes: it agrees, or it is capped with no verdict difference.</summary>
    public static bool Passes(Row r) => r.Agree || (r.Capped.Length > 0 && r.Why.Length == 0);
}
