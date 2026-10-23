namespace Packet.Sdl.Transcribe;

/// <summary>
/// Resolves a figure-verbatim label (with its shape class) to the canonical
/// event name defined in spec-sdl/events.yaml.
/// </summary>
/// <remarks>
/// The resolver uses the label text as its primary signal and consults
/// d5 only to disambiguate the two "All Other Primitives" catch-alls
/// (which share a label but differ in shape class).
/// </remarks>
public static class EventResolver
{
    /// <summary>
    /// Set of frame names that, when used as a Signal reception trigger,
    /// always resolve to "&lt;NAME&gt;_received" — see events.yaml § frames_received.
    /// </summary>
    private static readonly HashSet<string> FrameNames = new(StringComparer.Ordinal)
    {
        "I", "RR", "RNR", "REJ", "SREJ", "UI",
        "SABM", "SABME", "DISC", "UA", "DM",
        "FRMR", "XID", "TEST",
    };

    /// <summary>
    /// Explicit mappings for labels that aren't a clean transformation
    /// of their figure spelling. Keyed by (Label, ShapeClass).
    /// </summary>
    private static readonly Dictionary<(string Label, string ShapeClass), string> ExplicitMap = new()
    {
        // The "All Other Primitives" catch-all has two flavours, distinguished
        // by which shape class drew them. Encoded verbatim per events.yaml.
        [("All Other Primitives", "Signal reception from lower layer")] = "all_other_primitives__from_lower_layer",
        [("All Other Primitives", "Signal reception from upper layer")] = "all_other_primitives__from_upper_layer",

        // Composite "I, RR, RNR, REJ or SREJ Commands" — figc4.3.
        [("I, RR, RNR, REJ or SREJ Commands", "Signal reception from lower layer")] = "i_or_s_command_received",
        [("I, RR, RNR, REJ or SREJ Commands", "Signal reception from upper layer")] = "i_or_s_command_received",

        // "I Frame Pops Off I Frame Queue" — the I-frame send queue producing a
        // frame to transmit. Drawn as an Internal Signal Reception on every page
        // (figc4.4 Connected, figc4.5 Timer Recovery, and the Awaiting pages)
        // after the wording + symbol harmonisation in packethacking/ax25spec#36.
        // The canonical event id keeps its capital I because it is an I-frame
        // name (events.yaml § frames); the others are fully lowercase.
        [("I Frame Pops Off I Frame Queue", "Internal Signal Reception")] = "I_frame_pops_off_queue",

        // figc4.5 has an "I Frame" trigger (bare, no qualifier) meaning
        // "received an I-frame from the peer". Sibling of the other bare
        // frame receptions (RR_received, REJ_received, etc.) — canonical
        // event name follows that pattern. events.yaml needs an entry.
        [("I Frame", "Signal reception from lower layer")] = "I_received",

        // Figure C2a.7 (Simplex Physical Receiver Start) draws one internal
        // input labelled "T106 Expiry Or T108?". The "?" is the figure's own
        // doubt about which timer it means (C2a.5 also draws "Start T106
        // (T108?)", C2a.6 "(Start T108)", and no other page has a T108
        // input), so the id names both rather than picking T106. on_label
        // keeps the drawn text.
        [("T106 Expiry Or T108?", "Internal Signal Reception")] = "T106_or_T108_expiry",
    };

    /// <summary>
    /// Service-primitive families, by the prefix the figures draw before the
    /// first dash: DL- (Data Link), MDL- (Management Data Link), LM- (Link
    /// Multiplexer), PH- (Physical) and HW- (the radio hardware below the
    /// physical machines, Annex C2). Some figures draw an underscore where
    /// the dash belongs ("PH_SIEZE Confirm" on C2a.4), so either is accepted.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex PrimitivePrefix = new(
        "^(DL|MDL|LM|PH|HW)[-_]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// A timer expiry: "Timer T1 Expiry" (Data Link), "T102 Expiry" (C2a),
    /// "TM 201 Expiry" (C5.2, drawn with a space) and "TR210 Expiry" (C6).
    /// The timer name keeps its case and loses any space.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex TimerExpiry = new(
        "^(?:Timer )?(T[A-Z]* ?[0-9]+) Expiry$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>A frame drawn with its command/response role ("XID Command", "FRMR Response", C5).</summary>
    private static readonly System.Text.RegularExpressions.Regex FrameWithRole = new(
        "^([A-Z]+) (Command|Response)$");

    /// <summary>An event id must be usable as an identifier in every backend and in a transition id.</summary>
    private static readonly System.Text.RegularExpressions.Regex EventIdShape = new("^[A-Za-z][A-Za-z0-9_]*$");

    /// <summary>
    /// The trigger label as one line, used for both the event id and
    /// <c>on_label</c>. Line breaks and runs of spaces count as a single
    /// space. In a primitive label, a break or space straight after a "-" or
    /// "_" is joined up, because the figures wrap long names there: C2b.5
    /// draws "PH-" / "EXPEDITED_" / "DATA" / "Request", which reads
    /// "PH-EXPEDITED_DATA Request" whether it is typed with the breaks, with
    /// spaces, or joined.
    /// </summary>
    public static string NormaliseTriggerLabel(string label)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(label, @"\s+", " ").Trim();
        if (PrimitivePrefix.IsMatch(s))
            s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=[-_]) ", "");
        return s;
    }

    public static string ResolveTriggerEvent(string label, string shapeClass)
    {
        var resolved = ResolveUnchecked(label, shapeClass);
        if (!EventIdShape.IsMatch(resolved))
            throw new InvalidDataException(
                $"trigger label '{label.Trim()}' ({shapeClass}) resolves to '{resolved}', which is not a valid event id. " +
                "Add a rule or an ExplicitMap entry for it in codegen/src/Packet.Sdl.Transcribe/EventResolver.cs.");
        return resolved;
    }

    private static string ResolveUnchecked(string label, string shapeClass)
    {
        var trimmed = NormaliseTriggerLabel(label);
        if (ExplicitMap.TryGetValue((trimmed, shapeClass), out var explicitName))
            return explicitName;

        // "All Other Commands"
        if (trimmed.Equals("All Other Commands", StringComparison.OrdinalIgnoreCase))
            return "all_other_commands";

        // Any other "All Other ... Primitives" catch-all ("All Other DL
        // Primitives", C6) records which side it was drawn on, the same way
        // the plain "All Other Primitives" does in ExplicitMap above.
        if (trimmed.StartsWith("All Other ", StringComparison.OrdinalIgnoreCase)
         && trimmed.EndsWith(" Primitives", StringComparison.OrdinalIgnoreCase))
        {
            var side = shapeClass switch
            {
                "Signal reception from upper layer" => "__from_upper_layer",
                "Signal reception from lower layer" => "__from_lower_layer",
                _ => "",
            };
            return SnakeLower(trimmed) + side;
        }

        // "Timer T1 Expiry" → "T1_expiry"; "TM 201 Expiry" → "TM201_expiry".
        var timerMatch = TimerExpiry.Match(trimmed);
        if (timerMatch.Success)
            return timerMatch.Groups[1].Value.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant() + "_expiry";

        // "DL-X Y" → "DL_X_y", likewise MDL-, LM-, PH- and HW-
        // (request/confirm/indication lowercased).
        if (PrimitivePrefix.IsMatch(trimmed))
            return NormaliseDashPrefixedPrimitive(trimmed);

        // Bare frame name → "<NAME>_received"
        if (FrameNames.Contains(trimmed))
            return trimmed + "_received";

        // "XID Response" → "XID_response_received" (events.yaml § frames_received).
        var roleMatch = FrameWithRole.Match(trimmed);
        if (roleMatch.Success && FrameNames.Contains(roleMatch.Groups[1].Value))
            return $"{roleMatch.Groups[1].Value}_{roleMatch.Groups[2].Value.ToLowerInvariant()}_received";

        // "Control Field Error" / "Info Not Permitted In Frame" / "U or S
        // Frame Length Error" / "Queue Empty": title case to fully
        // snake_case lowercase to match events.yaml § internal entries.
        // (I_frame_pops_off_queue is the lone exception, handled via
        // ExplicitMap above.)
        return SnakeLower(trimmed);
    }

    private static string SnakeLower(string s) =>
        string.Join('_', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                          .Select(p => p.ToLowerInvariant()));

    /// <summary>
    /// The only shape a primitive label may have: a prefix and a name joined
    /// by dashes (or underscores) with no spaces, then one kind word, then
    /// optionally a parenthesised note such as an error code.
    /// "LM-" then a line break then "DATA Request", or
    /// "PH-EXPEDITED DATA Request", would otherwise give ids that differ from
    /// the dashed spelling (<c>LM__data_request</c>,
    /// <c>PH_EXPEDITED_data_request</c>), so they are rejected.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex PrimitiveShape = new(
        @"^((?:DL|MDL|LM|PH|HW)(?:[-_][A-Za-z0-9]+)+) (Request|Indication|Indicate|Confirm)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Figure spellings of one primitive that differ between pages, mapped
    /// to the canonical name so the primitive has one event id on every
    /// machine. The label stays recorded as drawn in <c>on_label</c>. Each
    /// entry cites the ax25spec issue that records the misspelling; add one
    /// only when a figure being transcribed needs it.
    /// </summary>
    private static readonly Dictionary<string, (string Canonical, string Issue)> PrimitiveNameAliases =
        new(StringComparer.Ordinal)
        {
            // C3.1 and C3.2 draw SIEZE; the prose and the Data Link pages'
            // LM-SEIZE Confirm spell SEIZE.
            ["LM_SIEZE"] = ("LM_SEIZE", "packethacking/ax25spec#61"),
            ["PH_SIEZE"] = ("PH_SEIZE", "packethacking/ax25spec#61"),
        };

    private static string NormaliseDashPrefixedPrimitive(string label)
    {
        // "DL-DISCONNECT Request" → "DL_DISCONNECT_request"
        // "DL-UNIT-DATA Request" → "DL_UNIT_DATA_request"
        // "DL-ERROR Indication (L)" → "DL_ERROR_indication"  (drop parenthetical)
        // "LM-SEIZE Confirm" → "LM_SEIZE_confirm"
        // "MDL-NEGOTIATE Request" → "MDL_NEGOTIATE_request"
        // "PH-SIEZE Confirm" → "PH_SEIZE_confirm"  (alias, #61)
        // "PH-EXPEDITED_DATA Request" → "PH_EXPEDITED_DATA_request"
        var stripped = StripTrailingParenthetical(label);
        var m = PrimitiveShape.Match(stripped);
        if (!m.Success)
            throw new InvalidDataException(
                $"primitive label '{label}' is not '<PREFIX>-<NAME> <Request|Indication|Indicate|Confirm>' with no spaces " +
                "inside the name. Type the label on one line as the figure spells it; if the figure itself draws it " +
                "another way, add a rule in codegen/src/Packet.Sdl.Transcribe/EventResolver.cs.");
        // The primitive name: upper-cased, dashes to underscores, so
        // "PH-Quiet Indication" (C3.2) and "PH-QUIET Indication" (C3.1) are
        // one event. A known misspelling maps to its canonical name.
        var name = m.Groups[1].Value.Replace('-', '_').ToUpperInvariant();
        if (PrimitiveNameAliases.TryGetValue(name, out var alias))
            name = alias.Canonical;
        // The kind word lower case: request / confirm / indication / indicate.
        return $"{name}_{m.Groups[2].Value.ToLowerInvariant()}";
    }

    private static string ToSnakeCasePreservingSpecVars(string label)
    {
        var parts = label.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('_', parts.Select(PreserveOrLower));
    }

    private static string PreserveOrLower(string token)
    {
        // Keep single-letter or all-caps tokens as-is (I, U, S, F, V — spec
        // variable names). Lowercase everything else.
        return System.Text.RegularExpressions.Regex.IsMatch(token, "^[A-Z]+[0-9]*$")
            ? token
            : token.ToLowerInvariant();
    }

    private static string StripTrailingParenthetical(string s)
    {
        var openParen = s.IndexOf('(');
        return openParen < 0 ? s.Trim() : s[..openParen].Trim();
    }


    /// <summary>
    /// Maps a d5 shape class to the canonical "kind" enum value in the
    /// yaml schema, for use in path steps.
    /// </summary>
    public static string? ShapeClassToActionKind(string shapeClass) => shapeClass switch
    {
        "Signal generation to upper layer" => "signal_upper",
        "Signal generation to lower layer" => "signal_lower",
        "Processing description"           => "processing",
        "Subroutine call"                  => "subroutine",
        "Internal Signal Generation"       => "internal_out",
        _ => null,
    };

    /// <summary>
    /// Cleans up a node label for use as an action verb in a path step.
    /// yEd's label contains line-breaks for multi-statement blocks (e.g.
    /// "V(s) ← 0\nV(a) ← 0\nV(r) ← 0") and uses HTML entity escaping for
    /// left-arrow assignments. Verbatim per CLAUDE.md "Trust the figure"
    /// — we don't normalise verb spelling here; actions.yaml does that
    /// downstream at codegen time.
    /// </summary>
    public static string NormaliseActionLabel(string raw)
    {
        // yEd writes "←" as the literal arrow character or as the HTML
        // entity &lt;- (which appears in d6 as "&lt;-" pre-unescaping or
        // "<-" post-unescape). The transcription convention used to date
        // is ":=" — see existing yamls.
        return raw.Replace("\n", "; ")
                  .Replace("<-", ":=")
                  .Replace("←", ":=")
                  .Trim();
    }
}
