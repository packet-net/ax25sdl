using CommandLine;

namespace Packet.Sdl.Transcribe;

public static class Program
{
    public static int Main(string[] args)
    {
        return Parser.Default.ParseArguments<Options>(args)
            .MapResult(Run, _ => 1);
    }

    private static int Run(Options opt)
    {
        var inputs = ResolveInputs(opt).ToList();
        if (inputs.Count == 0)
        {
            Console.Error.WriteLine($"::error::No graphml files found at {opt.Input}.");
            return 1;
        }

        // Hard errors before anything is written: two graphmls that would
        // write one yaml (the second would silently replace the first), and a
        // graphml filed under another machine's directory.
        var errors = 0;
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            var outPath = Path.GetFullPath(ResolveOutputPath(input, opt));
            if (outputs.TryGetValue(outPath, out var other))
            {
                Console.Error.WriteLine($"::error::{input} and {other} would both write {outPath}.");
                errors++;
            }
            else
            {
                outputs[outPath] = input;
            }

            var misfiled = MisfiledMachineDirectory(input);
            if (misfiled is not null)
            {
                Console.Error.WriteLine($"::error::{input}: {misfiled}");
                errors++;
            }
        }
        if (errors > 0) return 1;

        foreach (var input in inputs)
        {
            try
            {
                var graph = GraphmlReader.Load(input);
                string yaml;
                string summary;
                if (SubroutinesWalker.IsSubroutinesPage(graph))
                {
                    var page = SubroutinesWalker.Walk(graph, input);
                    page.SourceGraphmlPath = input;
                    yaml = YamlEmitter.EmitSubroutines(page);
                    var totalDecisions = page.Subroutines.Sum(s => s.Decisions.Count);
                    summary = $"{page.Subroutines.Count} subroutines, {totalDecisions} decisions";
                }
                else
                {
                    var page = Walker.Walk(graph, input);
                    page.SourceGraphmlPath = input;
                    yaml = YamlEmitter.Emit(page);
                    summary = $"{page.Transitions.Count} transitions, {page.Decisions.Count} decisions";
                }

                var outPath = ResolveOutputPath(input, opt);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                File.WriteAllText(outPath, yaml);
                Console.WriteLine($"  {input}  →  {outPath}  ({summary})");
            }
            catch (Exception ex)
            {
                // The page was not written. That is an error, never a warning:
                // CI must not pass with a page (or a subroutine) missing.
                Console.Error.WriteLine($"::error::{input}: {ex.Message}");
                errors++;
            }
        }

        // A whole sdl/ directory was regenerated: any *.sdl.yaml left in the
        // output directory that no graphml produced is an orphan (its graphml
        // was deleted or renamed), and is an error.
        if (Directory.Exists(opt.Input) && Directory.Exists(opt.OutputDir))
        {
            foreach (var yaml in Directory.EnumerateFiles(opt.OutputDir, "*.sdl.yaml").Order(StringComparer.Ordinal))
            {
                if (outputs.ContainsKey(Path.GetFullPath(yaml))) continue;
                Console.Error.WriteLine($"::error::{yaml} has no graphml in {opt.Input}. Delete it, or restore its graphml.");
                errors++;
            }
        }

        if (errors > 0)
        {
            Console.Error.WriteLine($"failed with {errors} error(s).");
            return 1;
        }
        return 0;
    }

    /// <summary>
    /// A graphml in <c>.../&lt;machine-dir&gt;/sdl/</c> must belong to that
    /// machine: <c>LinkMux_Idle.graphml</c> under <c>simplex-physical/sdl/</c>
    /// is misfiled. Returns the problem, or null when the file is where it
    /// belongs, its prefix is unknown (the walker reports that), or it is not
    /// under an <c>sdl/</c> directory at all.
    /// </summary>
    public static string? MisfiledMachineDirectory(string graphmlPath)
    {
        var full = Path.GetFullPath(graphmlPath);
        var sdlDir = Path.GetDirectoryName(full);
        if (sdlDir is null || Path.GetFileName(sdlDir) != "sdl") return null;
        var machineDir = Path.GetFileName(Path.GetDirectoryName(sdlDir));
        var bare = Path.GetFileNameWithoutExtension(full);
        var underscore = bare.IndexOf('_');
        if (underscore < 0) return null;
        var machine = Packet.Sdl.IR.AnnexC.MachineByPrefix(bare[..underscore]);
        if (machine is null || machine.Directory == machineDir) return null;
        return $"a {machine.Title} page must be under {machine.Directory}/sdl/, not {machineDir}/sdl/.";
    }

    private static IEnumerable<string> ResolveInputs(Options opt)
    {
        if (File.Exists(opt.Input)) return [opt.Input];
        if (Directory.Exists(opt.Input))
            return Directory.EnumerateFiles(opt.Input, "*.graphml", SearchOption.AllDirectories);
        return [];
    }

    private static string ResolveOutputPath(string graphmlPath, Options opt)
    {
        // disconnected.graphml  →  disconnected.sdl.yaml
        // DataLink_Disconnected.graphml  →  disconnected.sdl.yaml  (PascalCase → snake_case)
        var filename = Path.GetFileNameWithoutExtension(graphmlPath);
        var underscore = filename.IndexOf('_');
        var statePart = underscore < 0 ? filename : filename[(underscore + 1)..];
        // PascalCase → snake_case for the yaml filename.
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < statePart.Length; i++)
        {
            if (i > 0 && char.IsUpper(statePart[i])) sb.Append('_');
            sb.Append(char.ToLowerInvariant(statePart[i]));
        }
        var yamlName = sb + ".sdl.yaml";
        return Path.Combine(opt.OutputDir, yamlName);
    }
}

public class Options
{
    [Option('i', "input", Required = true,
        HelpText = "Path to a single graphml file or a directory containing graphml files (recursive).")]
    public string Input { get; init; } = "";

    [Option('o', "output-dir", Required = true,
        HelpText = "Directory to write the generated *.sdl.yaml files into.")]
    public string OutputDir { get; init; } = "";
}
