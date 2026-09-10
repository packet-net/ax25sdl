using System.Globalization;
using System.Text;
using Packet.Sdl.Interpreter;

namespace Packet.Sdl.Explorer;

/// <summary>
/// One concrete frame in flight on the modelled channel: exactly the fields
/// the receiving machine's guards and actions can consult, plus the payload
/// label an I frame carries so delivery order is observable.
/// </summary>
public sealed record Frame(string Type, bool Command, bool Pf, int? Nr, int? Ns, string? Data)
{
    public static Frame FromEffect(FrameEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return new Frame(effect.Frame, effect.Command, effect.Pf, effect.Nr, effect.Ns, effect.Data);
    }

    /// <summary>REJ or SREJ.</summary>
    public bool IsReject => Type is "REJ" or "SREJ";

    /// <summary>I frame or a supervisory frame (RR / RNR / REJ / SREJ).</summary>
    public bool IsIOrSupervisory => Type is "I" or "RR" or "RNR" or "REJ" or "SREJ";

    /// <summary>Compact one-line rendering, e.g. <c>I cmd P=0 ns=1 nr=0 [a1]</c>; also the canonical key used in state fingerprints.</summary>
    public string Render()
    {
        var sb = new StringBuilder(Type)
            .Append(Command ? " cmd " : " rsp ")
            .Append(Command ? "P=" : "F=").Append(Pf ? '1' : '0');
        if (Ns is not null) sb.Append(" ns=").Append(Ns.Value.ToString(CultureInfo.InvariantCulture));
        if (Nr is not null) sb.Append(" nr=").Append(Nr.Value.ToString(CultureInfo.InvariantCulture));
        if (Data is not null) sb.Append(" [").Append(Data).Append(']');
        return sb.ToString();
    }
}
