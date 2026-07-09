using System.Globalization;
using System.Text;

namespace Packet.Sdl.Interpreter;

/// <summary>
/// One abstract effect produced by dispatching an event: something the
/// machine did that is observable from outside (a frame handed to the
/// lower layer, a primitive handed to the upper layer, an LM primitive,
/// or an internal-queue post). Variable assignments, timer operations and
/// state changes are not effects — traces assert those against the
/// machine's state after the step.
/// </summary>
public abstract record Effect
{
    /// <summary>Stable, human-readable one-line rendering used in trace failure messages.</summary>
    public abstract string Render();
}

/// <summary>A frame handed to the lower layer for transmission.</summary>
/// <param name="Frame">Frame type: SABM, SABME, DISC, UA, DM, UI, I, RR, RNR, REJ, SREJ, XID, TEST, FRMR.</param>
/// <param name="Command">True = command (carries P), false = response (carries F).</param>
/// <param name="Pf">The resolved P/F bit.</param>
/// <param name="Nr">N(R) for frame types that carry one (I / RR / RNR / REJ / SREJ).</param>
/// <param name="Ns">N(S) for I frames.</param>
/// <param name="Expedited">True for the figures' "Expedited" TX-priority variants.</param>
public sealed record FrameEffect(string Frame, bool Command, bool Pf, int? Nr, int? Ns, bool Expedited) : Effect
{
    public override string Render()
    {
        var sb = new StringBuilder("frame ").Append(Frame)
            .Append(Command ? " command" : " response")
            .Append(" pf=").Append(Pf ? '1' : '0');
        if (Ns is not null) sb.Append(" ns=").Append(Ns.Value.ToString(CultureInfo.InvariantCulture));
        if (Nr is not null) sb.Append(" nr=").Append(Nr.Value.ToString(CultureInfo.InvariantCulture));
        if (Expedited) sb.Append(" expedited");
        return sb.ToString();
    }
}

/// <summary>A primitive handed to the upper layer (DL-* indication/confirm, DL-ERROR Indication (X), …). Verbatim canonical verb.</summary>
public sealed record UpperEffect(string Primitive) : Effect
{
    public override string Render() => $"dl {Primitive}";
}

/// <summary>A Link Multiplexer primitive (LM_seize_request / LM_release_request / LM_data_request).</summary>
public sealed record LmEffect(string Primitive) : Effect
{
    public override string Render() => $"lm {Primitive}";
}

/// <summary>An internal signal (kind <c>internal_out</c>): a post to the machine's own queue, e.g. an I-frame (re)queue.</summary>
public sealed record InternalEffect(string Verb, string? Detail) : Effect
{
    public override string Render() => Detail is null ? $"internal {Verb}" : $"internal {Verb} [{Detail}]";
}
