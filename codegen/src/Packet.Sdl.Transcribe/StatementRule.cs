using System.Text.RegularExpressions;

namespace Packet.Sdl.Transcribe;

/// <summary>
/// The rule a transcriber uses to decide which printed lines of a figure's
/// action box are one statement (one line, so one action, in the graphml).
/// It is written down here, closed, and tested against every committed page
/// (StatementRuleTests), so two transcribers can't read a box differently.
/// docs/sdl-transcription-runbook.md, "One statement, one line".
/// </summary>
/// <remarks>
/// A printed line starts a new statement only when the statement so far is
/// complete and the line opens a statement. Any other line continues the
/// statement above, because the figure is only wrapping it.
/// <list type="bullet">
/// <item>Complete: ends with ".", or is an assignment with a right-hand side
/// ("R &lt;- Random"), or starts with a listed verb (on its own or in
/// brackets).</item>
/// <item>Opens a statement: is an assignment, or starts with a listed verb,
/// or is a bracketed line starting with a listed verb ("(Set Digipeating)").
/// A bracketed line that doesn't ("(F = 1)") is an argument and
/// continues.</item>
/// </list>
/// </remarks>
public static class StatementRule
{
    /// <summary>
    /// The verbs that start a statement. Closed: these are exactly the verbs
    /// the committed pages need. A page that needs another adds it here, and
    /// StatementRuleTests proves every committed page still reads the same.
    /// </summary>
    public static IReadOnlySet<string> Verbs { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "Accumulate", "Add", "Allocate", "Assemble", "Backtrack", "Clear", "Decrement", "Discard", "Divide",
        "Free", "Insert", "Restart", "Resume", "Select", "Set", "Start", "Stop", "Suspend",
    };

    private static readonly Regex Arrow = new(@"<-|←|:=");

    private static string FirstWord(string text)
    {
        var words = text.Trim().TrimStart('(').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? "" : Regex.Replace(words[0], "[^A-Za-z]", "");
    }

    /// <summary>True when <paramref name="statement"/> is complete on its own.</summary>
    public static bool IsComplete(string statement)
    {
        var t = statement.Trim();
        if (t.EndsWith('.')) return true;
        var m = Arrow.Match(t);
        if (m.Success) return t[(m.Index + m.Length)..].Trim().Length > 0;
        return Verbs.Contains(FirstWord(t));
    }

    /// <summary>True when the printed line <paramref name="line"/> can open a new statement.</summary>
    public static bool OpensStatement(string line)
    {
        var t = line.Trim();
        if (t.StartsWith('(')) return Verbs.Contains(FirstWord(t));
        return Arrow.IsMatch(t) || Verbs.Contains(FirstWord(t));
    }

    /// <summary>The statements a box's printed lines make, joined with single spaces.</summary>
    public static IReadOnlyList<string> Split(IEnumerable<string> printedLines)
    {
        var statements = new List<string>();
        foreach (var raw in printedLines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (statements.Count == 0 || !(IsComplete(statements[^1]) && OpensStatement(line)))
            {
                if (statements.Count == 0) statements.Add(line);
                else statements[^1] += " " + line;
            }
            else
            {
                statements.Add(line);
            }
        }
        return statements;
    }
}
