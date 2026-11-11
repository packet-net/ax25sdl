using System.Text.RegularExpressions;
using Packet.Sdl.Transcribe;

namespace Packet.Sdl.CodeGen.Tests;

/// <summary>
/// The statement rule must read every committed page the way it was
/// transcribed: every boundary between two statements in a box is one the
/// rule makes, and no committed statement could be split by the rule
/// wherever the figure happened to wrap it.
/// </summary>
public class StatementRuleTests
{
    private static readonly HashSet<string> ActionShapes = new(StringComparer.Ordinal)
    {
        "Processing description", "Signal generation to upper layer", "Signal generation to lower layer",
        "Subroutine call", "Internal Signal Generation",
    };

    public static IEnumerable<object[]> CommittedBoxes()
    {
        var root = FindSpecSdl();
        foreach (var file in Directory.EnumerateFiles(root, "*.graphml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (!file.Contains($"{Path.DirectorySeparatorChar}sdl{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            var graph = GraphmlReader.Load(file);
            foreach (var node in graph.Nodes.Where(n => ActionShapes.Contains(n.ShapeClass)))
            {
                var lines = node.Label.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())
                                      .Where(l => l.Length > 0).ToArray();
                if (lines.Length > 0)
                    yield return [Path.GetFileName(file) + " " + node.Id, lines];
            }
        }
    }

    [Fact]
    public void Every_committed_box_reads_back_as_transcribed()
    {
        var boxes = CommittedBoxes().ToList();
        boxes.Should().HaveCountGreaterThan(500, "the submodule should carry every transcribed machine");
        var problems = new List<string>();
        foreach (var box in boxes)
        {
            var where = (string)box[0];
            var statements = (string[])box[1];

            // Each statement boundary is one the rule makes.
            for (var i = 1; i < statements.Length; i++)
                if (!(StatementRule.IsComplete(statements[i - 1]) && StatementRule.OpensStatement(statements[i])))
                    problems.Add($"{where}: the rule would join \"{statements[i - 1]}\" and \"{statements[i]}\"");

            // No statement can be split, wherever the figure wraps it.
            foreach (var st in statements)
            {
                var words = st.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (var k = 1; k < words.Length; k++)
                {
                    var head = string.Join(' ', words[..k]);
                    var rest = string.Join(' ', words[k..]);
                    if (StatementRule.IsComplete(head) && StatementRule.OpensStatement(rest))
                        problems.Add($"{where}: a wrap after \"{head}\" would split \"{st}\"");
                }
            }
        }
        problems.Should().BeEmpty();
    }

    [Theory]
    // Wrapped single statements (the review's examples) stay one action.
    [InlineData("Suspend All|Queue Processing", "Suspend All Queue Processing")]                    // C2a.5
    [InlineData("R <- Random|Number in Range|0 to 255", "R <- Random Number in Range 0 to 255")]   // C2a.3
    [InlineData("Set Selective|Reject", "Set Selective Reject")]                                    // C4.7b
    [InlineData("Clear|Acknowledge|Pending", "Clear Acknowledge Pending")]                          // C4.4a, C4.7a
    [InlineData("Enquiry|Response|(F = 1)", "Enquiry Response (F = 1)")]                            // C4.4b
    [InlineData("Discard:|Erroneous|Primitive", "Discard: Erroneous Primitive")]                    // C2b.1
    // Separate statements split.
    [InlineData("Resume|Processing Priority|Queue|(Set Digipeating)", "Resume Processing Priority Queue/(Set Digipeating)")]   // C2a.5
    [InlineData("Clear Digipeating|(Start T100)|(Start T108)|Suspend Processing Priority Queue",
                "Clear Digipeating/(Start T100)/(Start T108)/Suspend Processing Priority Queue")]                            // C2a.6
    [InlineData("V(s) <- 0|V(a) <- 0", "V(s) <- 0/V(a) <- 0")]
    public void Printed_lines_read_as_the_runbook_says(string printed, string expected)
    {
        StatementRule.Split(printed.Split('|')).Should().Equal(expected.Split('/'));
    }

    private static string FindSpecSdl()
    {
        var d = new DirectoryInfo(Path.GetDirectoryName(typeof(StatementRuleTests).Assembly.Location)!);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "spec-sdl"))) d = d.Parent;
        return Path.Combine(d?.FullName ?? throw new InvalidOperationException("spec-sdl not found"), "spec-sdl");
    }
}
