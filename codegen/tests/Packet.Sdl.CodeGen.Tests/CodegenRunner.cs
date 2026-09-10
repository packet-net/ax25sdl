using System.Diagnostics;
using System.IO;

namespace Packet.Sdl.CodeGen.Tests;

/// <summary>
/// Invokes the codegen tool as a subprocess against a temporary directory.
/// Each test scenario writes its `*.sdl.yaml` and `events.yaml` fixtures into
/// the temp dir, asks the runner to invoke codegen, then asserts on the
/// exit code, stderr (for ::error:: lines), and the generated `.g.cs`
/// content.
/// </summary>
/// <remarks>
/// <para>
/// Black-box on purpose: tests exercise the actual CLI contract of the
/// tool. Refactoring <c>Program.cs</c> internals doesn't break tests; only
/// CLI behaviour does.
/// </para>
/// <para>
/// The runner uses the codegen project's build output directly via
/// <c>dotnet</c> to invoke <c>Packet.Sdl.CodeGen.dll</c>. It does NOT run
/// <c>dotnet run --project ...</c> per invocation because that path
/// re-builds every time and the test suite would crawl.
/// </para>
/// </remarks>
internal sealed class CodegenRunner : IDisposable
{
    public string RootDir { get; }
    public string SpecDir { get; }
    public string OutDir  { get; }
    public string TestsDir { get; }
    public string JsonOutDir { get; }
    public string RustOutDir { get; }

    public CodegenRunner()
    {
        RootDir    = Directory.CreateTempSubdirectory("sdl-codegen-tests-").FullName;
        SpecDir    = Path.Combine(RootDir, "spec-sdl");
        OutDir     = Path.Combine(RootDir, "spec", "csharp");
        TestsDir   = Path.Combine(RootDir, "spec", "csharp", "tests");
        JsonOutDir = Path.Combine(RootDir, "spec", "json");
        RustOutDir = Path.Combine(RootDir, "spec", "rust", "src");
        Directory.CreateDirectory(SpecDir);
        Directory.CreateDirectory(OutDir);
        Directory.CreateDirectory(TestsDir);
        Directory.CreateDirectory(JsonOutDir);
        Directory.CreateDirectory(RustOutDir);
    }

    /// <summary>Drop a YAML page into the SpecDir at the given sub-path.</summary>
    public void WritePage(string relativePath, string yaml)
    {
        var full = Path.Combine(SpecDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, yaml);
    }

    /// <summary>Drop an events.yaml at the SpecDir root.</summary>
    public void WriteEventsCatalog(string yaml)
        => File.WriteAllText(Path.Combine(SpecDir, "events.yaml"), yaml);

    /// <summary>Drop an actions.yaml at the SpecDir root (optional; soft passthrough when absent).</summary>
    public void WriteActionsCatalog(string yaml)
        => File.WriteAllText(Path.Combine(SpecDir, "actions.yaml"), yaml);

    /// <summary>Drop a predicates.yaml at the SpecDir root (optional; soft passthrough when absent — but once present, every decision predicate must resolve to it).</summary>
    public void WritePredicatesCatalog(string yaml)
        => File.WriteAllText(Path.Combine(SpecDir, "predicates.yaml"), yaml);

    public sealed record RunResult(int ExitCode, string Stdout, string Stderr);

    /// <summary>Run the codegen tool and capture exit + stdout + stderr.</summary>
    public RunResult Run()
    {
        // Locate the codegen DLL next to the test binary. The csproj sets
        // ReferenceOutputAssembly=false so the assembly isn't auto-copied;
        // we resolve it via the well-known relative path from this test
        // assembly's build output. Works in dotnet test and direct runs.
        var dll = LocateCodegenDll();

        // --csharp opts in to the C# backend (only) so the test
        // sandbox doesn't touch Go / TS spec directories. Per-test
        // input + output paths are passed through --in, --csharp-out,
        // --csharp-tests; the CLI's opt-in-by-presence semantics treat
        // setting either path as an explicit selection of the C#
        // backend, with all others off.
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" --in \"{SpecDir}\" --csharp --csharp-out \"{OutDir}\" --csharp-tests \"{TestsDir}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            // Anchor any relative path the codegen reads to the test
            // sandbox rather than the test bin dir.
            WorkingDirectory       = RootDir,
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return new RunResult(proc.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Run codegen with the JSON backend selected (and only the JSON
    /// backend). Output lands in <see cref="JsonOutDir"/>.
    /// </summary>
    public RunResult RunJson()
    {
        var dll = LocateCodegenDll();
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" --in \"{SpecDir}\" --json --json-out \"{JsonOutDir}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            WorkingDirectory       = RootDir,
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return new RunResult(proc.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Run codegen with the Rust backend selected (and only Rust).
    /// Output lands in <see cref="RustOutDir"/>.
    /// </summary>
    public RunResult RunRust()
    {
        var dll = LocateCodegenDll();
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" --in \"{SpecDir}\" --rust --rust-out \"{RustOutDir}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            WorkingDirectory       = RootDir,
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return new RunResult(proc.ExitCode, stdout, stderr);
    }

    /// <summary>Read a generated file from the C# output directory.</summary>
    public string ReadGenerated(string relativePath)
    {
        var full = Path.Combine(OutDir, relativePath);
        return File.ReadAllText(full);
    }

    /// <summary>Read a generated file from the JSON output directory.</summary>
    public string ReadJson(string relativePath)
    {
        var full = Path.Combine(JsonOutDir, relativePath);
        return File.ReadAllText(full);
    }

    /// <summary>Read a generated file from the Rust output directory.</summary>
    public string ReadRust(string relativePath)
        => File.ReadAllText(Path.Combine(RustOutDir, relativePath));

    /// <summary>Does the named file exist in the C# output directory?</summary>
    public bool GeneratedExists(string relativePath)
        => File.Exists(Path.Combine(OutDir, relativePath));

    /// <summary>Does the named file exist in the JSON output directory?</summary>
    public bool JsonExists(string relativePath)
        => File.Exists(Path.Combine(JsonOutDir, relativePath));

    /// <summary>Does the named file exist in the Rust output directory?</summary>
    public bool RustExists(string relativePath)
        => File.Exists(Path.Combine(RustOutDir, relativePath));

    private static string LocateCodegenDll()
    {
        var testAsmDir = Path.GetDirectoryName(typeof(CodegenRunner).Assembly.Location)!;
        var config = new DirectoryInfo(testAsmDir).Parent!.Name;
        var tfm = new DirectoryInfo(testAsmDir).Name;
        var repoRoot = FindRepoRoot(testAsmDir);
        var dll = Path.Combine(repoRoot, "src", "Packet.Sdl.CodeGen", "bin", config, tfm, "Packet.Sdl.CodeGen.dll");
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException(
                $"codegen DLL not found at expected path: {dll}. Ensure the codegen project is built before running tests.");
        }
        return dll;
    }

    public void Dispose()
    {
        try { Directory.Delete(RootDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static string FindRepoRoot(string start)
    {
        var d = new DirectoryInfo(start);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ax25sdl.slnx")))
        {
            d = d.Parent;
        }
        return d?.FullName ?? throw new InvalidOperationException("repo root (ax25sdl.slnx) not found above " + start);
    }
}
