using CommandLine;
using Packet.Sdl.CodeGen.C;
using Packet.Sdl.CodeGen.Csharp;
using Packet.Sdl.CodeGen.Go;
using Packet.Sdl.CodeGen.Json;
using Packet.Sdl.CodeGen.Python;
using Packet.Sdl.CodeGen.Rust;
using Packet.Sdl.CodeGen.Ts;
using Packet.Sdl.IR;

namespace Packet.Sdl.CodeGen;

/// <summary>
/// Driver for the SDL codegen pipeline. Loads YAML pages from
/// <c>spec-sdl/</c>, validates them, resolves into the language-neutral
/// IR (<see cref="ResolvedPage"/> / <see cref="ResolvedSubroutinesPage"/>),
/// and hands them to one or more language backends
/// (C# / Go / TS / JSON / Rust / C / Python).
/// </summary>
/// <remarks>
/// <para>
/// Backend selection is opt-in by presence: pass no language flags and
/// every backend runs with its default output path; pass any language
/// flag and only the explicitly-enabled backends run. A backend is
/// "enabled" when its bare flag (<c>--csharp</c>, <c>--go</c>,
/// <c>--ts</c>, <c>--json</c>, <c>--rust</c>, <c>--emit-c</c>,
/// <c>--python</c>) is set OR when one of its path options
/// (<c>--csharp-out</c>, <c>--csharp-tests</c>, <c>--go-out</c>,
/// <c>--ts-out</c>, <c>--json-out</c>, <c>--rust-out</c>,
/// <c>--c-out</c>, <c>--python-out</c>) is set.
/// </para>
/// </remarks>
internal static class Program
{
    public static int Main(string[] args)
    {
        var parsed = Parser.Default.ParseArguments<CodegenOptions>(args);
        if (parsed.Errors.Any())
        {
            // CommandLineParser already wrote the help text to stderr.
            return 1;
        }

        try
        {
            return Run(CodegenPlan.From(parsed.Value));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::{ex.Message}");
            return 1;
        }
    }

    private static int Run(CodegenPlan plan)
    {
        if (!Directory.Exists(plan.InDir))
        {
            // A generator that was asked to emit, and found no input, has failed. It must not
            // exit 0. Returning success here let CI's `git diff --exit-code` drift guard pass
            // while generating nothing, silently disabling the guard for every language target
            // whenever the ax25spec submodule was not checked out.
            Console.Error.WriteLine($"::error::SDL input directory '{plan.InDir}' does not exist. "
                + "It is a symlink into the ax25spec submodule; run `git submodule update --init "
                + "--recursive` locally, or check out with `submodules: true` in CI.");
            return 1;
        }

        if (plan.EmitCsharp)
        {
            Directory.CreateDirectory(plan.CsharpOut);
            Directory.CreateDirectory(plan.CsharpTests);
        }
        if (plan.EmitGo)
        {
            Directory.CreateDirectory(plan.GoOut);
        }
        if (plan.EmitTs)
        {
            Directory.CreateDirectory(plan.TsOut);
        }
        if (plan.EmitJson)
        {
            Directory.CreateDirectory(plan.JsonOut);
        }
        if (plan.EmitRust)
        {
            Directory.CreateDirectory(plan.RustOut);
        }
        if (plan.EmitC)
        {
            Directory.CreateDirectory(plan.COut);
            // Test files land in a sibling `test/` directory of the C
            // source output. CMake's CTest scans that directory by glob.
            Directory.CreateDirectory(CTestDir(plan.COut));
        }
        if (plan.EmitPython)
        {
            Directory.CreateDirectory(plan.PythonOut);
        }

        var events     = EventCatalog.Load(Path.Combine(plan.InDir, "events.yaml"));
        var actions    = ActionCatalog.Load(Path.Combine(plan.InDir, "actions.yaml"));
        var predicates = PredicateCatalog.Load(Path.Combine(plan.InDir, "predicates.yaml"));

        // Split YAML files into state-machine pages (sdl-machine schema)
        // and subroutine pages (sdl-subroutines schema). Both share the
        // *.sdl.yaml extension; Loader.IsSubroutinePage routes by content.
        var yamlFiles = Directory.EnumerateFiles(plan.InDir, "*.sdl.yaml", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        var pages = new List<SdlPage>(yamlFiles.Count);
        var subroutinePages = new List<SubroutinePage>();
        foreach (var path in yamlFiles)
        {
            if (Loader.IsSubroutinePage(path))
            {
                subroutinePages.Add(Loader.LoadSubroutinePage(path));
                continue;
            }
            pages.Add(Loader.LoadPage(path));
        }

        var errors = new List<string>();
        foreach (var page in pages)
        {
            Validation.NormaliseActionVerbs(page, actions, errors);
            Validation.NormaliseDecisionPredicates(page, predicates, errors);
            Validation.ValidatePage(page, events, errors);
        }
        foreach (var subPage in subroutinePages)
        {
            Validation.NormaliseSubroutineActionVerbs(subPage, actions, errors);
            Validation.NormaliseSubroutineDecisionPredicates(subPage, predicates, errors);
            Validation.ValidateSubroutinePage(subPage, errors);
        }

        // Unused-alias lint: every alias declared in actions.yaml should
        // be referenced by at least one *.sdl.yaml verb. Otherwise the
        // alias is dead weight — file an error so the catalog stays tidy.
        foreach (var alias in actions.DeclaredAliases)
        {
            if (!actions.SeenAliases.Contains(alias))
            {
                errors.Add(
                    $"spec-sdl/actions.yaml: alias `{alias}` (→ `{actions.CanonicalLookup[alias]}`) " +
                    "is declared but never referenced by any *.sdl.yaml verb. Remove the alias or update " +
                    "a YAML page to use it.");
            }
        }

        // Unused-predicate-alias lint — the guard analogue of the above.
        // Every alias declared in predicates.yaml must be referenced by at
        // least one *.sdl.yaml decision predicate, or it's dead weight.
        foreach (var alias in predicates.DeclaredAliases)
        {
            if (!predicates.SeenAliases.Contains(alias))
            {
                errors.Add(
                    $"spec-sdl/predicates.yaml: alias `{alias}` (→ `{predicates.CanonicalLookup[alias]}`) " +
                    "is declared but never referenced by any *.sdl.yaml decision predicate. Remove the " +
                    "alias or update a YAML page to use it.");
            }
        }

        // Predicate-catalogue-completeness lint: every predicate atom the
        // YAML's decisions reference must resolve to a canonical entry in
        // spec-sdl/predicates.yaml. That keeps the emitted Ax25Guard closed
        // set authoritative, and it is the codegen's whole remaining stake in
        // guard coverage. Whether a *consumer* binds every atom is enforced
        // inside that consumer by its own compiler, not from here; see
        // docs/sdl-guard-and-event-catalogue.md, "Where the binding gate lives".
        LintPredicateCatalogue(pages, subroutinePages, predicates, errors);

        // State-target lint: every transition's `next:` must name a state
        // that exists somewhere in the same machine. Catches transcription
        // typos like `next: connecteed` that would otherwise wedge the
        // session in a state the runtime can't dispatch on. Runtime-
        // agnostic, operating on SDL pages directly rather than against any
        // runtime file.
        LintStateTargets(pages, errors);

        // Per-state catchall-coverage lint: every state should have at
        // least one transition triggered by a `catchalls:` event (e.g.
        // all_other_primitives__from_lower_layer). Without a catchall,
        // any event not explicitly handled silently no-ops, which can
        // mask real transcription gaps. The lint is intentionally
        // tolerant: it only requires SOME catchall, not full event
        // coverage; deciding which events a state should handle is the
        // spec author's call, not codegen's.
        LintCatchallCoverage(pages, errors);

        if (errors.Count > 0)
        {
            foreach (var e in errors) Console.Error.WriteLine($"::error::{e}");
            return 1;
        }

        var writtenCsharpCode    = new HashSet<string>(StringComparer.Ordinal);
        var writtenCsharpTests   = new HashSet<string>(StringComparer.Ordinal);
        var writtenMermaid       = new HashSet<string>(StringComparer.Ordinal);
        var writtenGo            = new HashSet<string>(StringComparer.Ordinal);
        var writtenTs            = new HashSet<string>(StringComparer.Ordinal);
        var writtenJson          = new HashSet<string>(StringComparer.Ordinal);
        var writtenRust          = new HashSet<string>(StringComparer.Ordinal);
        var writtenCSrc          = new HashSet<string>(StringComparer.Ordinal);
        var writtenCTest         = new HashSet<string>(StringComparer.Ordinal);
        var writtenPython        = new HashSet<string>(StringComparer.Ordinal);

        // Emit the JSON Schema once up front. State + subroutine pages
        // reference it via "$schema": "./schema.json" and validate against
        // it before they're written, so we need the schema text in hand
        // before the per-page loop starts.
        string? jsonSchemaText = null;
        if (plan.EmitJson)
        {
            jsonSchemaText = JsonEmitter.EmitSchema();
            var schemaPath = Path.Combine(plan.JsonOut, "schema.json");
            WriteIfChanged(schemaPath, jsonSchemaText);
            writtenJson.Add(Path.GetFullPath(schemaPath));
        }

        // Collect resolved IR so TS index emission sees the full set in
        // deterministic order at the end of the run.
        var resolvedPages = new List<ResolvedPage>(pages.Count);
        var resolvedSubPages = new List<ResolvedSubroutinesPage>(subroutinePages.Count);

        foreach (var page in pages)
        {
            var resolved = Resolver.Resolve(page);
            resolvedPages.Add(resolved);

            var label = "";
            if (plan.EmitCsharp)
            {
                var model = CsharpStateModel.From(resolved);
                var codePath  = Path.Combine(plan.CsharpOut,   model.ClassName + ".g.cs");
                var testsPath = Path.Combine(plan.CsharpTests, model.ClassName + ".g.Tests.cs");
                var mermaidPath = plan.MermaidOut is null
                    ? Path.Combine(
                        // Default: emit to a sibling `mmd/` directory when the
                        // source lives under a `yaml/` folder (the layout used
                        // under spec-sdl/<rev>/<machine>/{sdl,yaml,mmd}/); fall
                        // back to alongside the source for in-memory test
                        // fixtures, which don't use the three-way split.
                        SiblingMermaidDir(page.SourcePath),
                        Path.GetFileNameWithoutExtension(page.SourcePath).Replace(".sdl", string.Empty, StringComparison.Ordinal) + ".g.mmd")
                    : Path.Combine(plan.MermaidOut, model.ClassName + ".g.mmd");

                var emission = CsharpEmitter.EmitStatePage(resolved, codePath, testsPath);
                WriteIfChanged(codePath,    emission.Code);
                WriteIfChanged(testsPath,   emission.Tests);
                WriteIfChanged(mermaidPath, emission.Mermaid);
                writtenCsharpCode.Add(Path.GetFullPath(codePath));
                writtenCsharpTests.Add(Path.GetFullPath(testsPath));
                writtenMermaid.Add(Path.GetFullPath(mermaidPath));
                label = $"  →  {model.ClassName}.{{g.cs,g.Tests.cs,g.mmd}}";
            }

            if (plan.EmitGo)
            {
                var go = GoEmitter.EmitStatePage(resolved);
                WriteIfChanged(Path.Combine(plan.GoOut, go.FileName), go.Content);
                writtenGo.Add(Path.GetFullPath(Path.Combine(plan.GoOut, go.FileName)));

                // Per-transition tests file alongside the data file —
                // matches the C# .g.Tests.cs scope (state-machine pages
                // only, not subroutine pages).
                var goTests = GoEmitter.EmitStatePageTests(resolved);
                WriteIfChanged(Path.Combine(plan.GoOut, goTests.FileName), goTests.Content);
                writtenGo.Add(Path.GetFullPath(Path.Combine(plan.GoOut, goTests.FileName)));
                label += " + .g{,_test}.go";
            }

            if (plan.EmitTs)
            {
                var ts = TsEmitter.EmitStatePage(resolved);
                WriteIfChanged(Path.Combine(plan.TsOut, ts.FileName), ts.Content);
                writtenTs.Add(Path.GetFullPath(Path.Combine(plan.TsOut, ts.FileName)));

                var tsTests = TsEmitter.EmitStatePageTests(resolved);
                WriteIfChanged(Path.Combine(plan.TsOut, tsTests.FileName), tsTests.Content);
                writtenTs.Add(Path.GetFullPath(Path.Combine(plan.TsOut, tsTests.FileName)));
                label += " + .g{,.test}.ts";
            }

            if (plan.EmitJson)
            {
                var json = JsonEmitter.EmitStatePage(resolved);
                var outPath = Path.Combine(plan.JsonOut, json.FileName);
                // Validate before writing — a drift between the IR types
                // and the hand-written schema must fail codegen, not
                // produce broken consumer files.
                JsonEmitter.ValidateAgainstSchema(jsonSchemaText!, json.Content, outPath);
                WriteIfChanged(outPath, json.Content);
                writtenJson.Add(Path.GetFullPath(outPath));
                label += " + .g.json";
            }

            if (plan.EmitRust)
            {
                // Rust co-locates data + per-transition tests in the
                // same .g.rs file (idiomatic #[cfg(test)] mod tests).
                var rs = RustEmitter.EmitStatePage(resolved);
                WriteIfChanged(Path.Combine(plan.RustOut, rs.FileName), rs.Content);
                writtenRust.Add(Path.GetFullPath(Path.Combine(plan.RustOut, rs.FileName)));
                label += " + .g.rs";
            }

            if (plan.EmitC)
            {
                var c = CEmitter.EmitStatePage(resolved);
                WriteIfChanged(Path.Combine(plan.COut, c.FileName), c.Content);
                writtenCSrc.Add(Path.GetFullPath(Path.Combine(plan.COut, c.FileName)));

                // Per-page test executable in the sibling test/ dir;
                // CMake's CTest picks them up via a glob.
                var cTestDir = CTestDir(plan.COut);
                var cTests = CEmitter.EmitStatePageTests(resolved);
                WriteIfChanged(Path.Combine(cTestDir, cTests.FileName), cTests.Content);
                writtenCTest.Add(Path.GetFullPath(Path.Combine(cTestDir, cTests.FileName)));
                label += " + .g{,.test}.c";
            }

            if (plan.EmitPython)
            {
                var py = PythonEmitter.EmitStatePage(resolved);
                WriteIfChanged(Path.Combine(plan.PythonOut, py.FileName), py.Content);
                writtenPython.Add(Path.GetFullPath(Path.Combine(plan.PythonOut, py.FileName)));

                var pyTests = PythonEmitter.EmitStatePageTests(resolved);
                WriteIfChanged(Path.Combine(plan.PythonOut, pyTests.FileName), pyTests.Content);
                writtenPython.Add(Path.GetFullPath(Path.Combine(plan.PythonOut, pyTests.FileName)));
                label += " + .g.py + _g_test.py";
            }

            Console.WriteLine($"  ok  {page.SourcePath}{label}");
        }

        // Subroutine pages: one .g.cs / .g.go / .g.ts per page (no
        // generated tests on this side — matches the C# emitter's
        // historical scope).
        foreach (var subPage in subroutinePages)
        {
            var resolved = Resolver.Resolve(subPage);
            resolvedSubPages.Add(resolved);

            string className = "";
            if (plan.EmitCsharp)
            {
                var model = CsharpSubroutinesModel.From(resolved);
                className = model.ClassName;
                var codePath = Path.Combine(plan.CsharpOut, model.ClassName + ".g.cs");
                var emission = CsharpEmitter.EmitSubroutinePage(resolved, codePath);
                WriteIfChanged(codePath, emission.Code);
                writtenCsharpCode.Add(Path.GetFullPath(codePath));
            }

            if (plan.EmitGo)
            {
                var go = GoEmitter.EmitSubroutinePage(resolved);
                WriteIfChanged(Path.Combine(plan.GoOut, go.FileName), go.Content);
                writtenGo.Add(Path.GetFullPath(Path.Combine(plan.GoOut, go.FileName)));
            }

            if (plan.EmitTs)
            {
                var ts = TsEmitter.EmitSubroutinePage(resolved);
                WriteIfChanged(Path.Combine(plan.TsOut, ts.FileName), ts.Content);
                writtenTs.Add(Path.GetFullPath(Path.Combine(plan.TsOut, ts.FileName)));
            }

            if (plan.EmitJson)
            {
                var json = JsonEmitter.EmitSubroutinePage(resolved);
                var outPath = Path.Combine(plan.JsonOut, json.FileName);
                JsonEmitter.ValidateAgainstSchema(jsonSchemaText!, json.Content, outPath);
                WriteIfChanged(outPath, json.Content);
                writtenJson.Add(Path.GetFullPath(outPath));
            }

            if (plan.EmitRust)
            {
                var rs = RustEmitter.EmitSubroutinePage(resolved);
                WriteIfChanged(Path.Combine(plan.RustOut, rs.FileName), rs.Content);
                writtenRust.Add(Path.GetFullPath(Path.Combine(plan.RustOut, rs.FileName)));
            }

            if (plan.EmitC)
            {
                var c = CEmitter.EmitSubroutinePage(resolved);
                WriteIfChanged(Path.Combine(plan.COut, c.FileName), c.Content);
                writtenCSrc.Add(Path.GetFullPath(Path.Combine(plan.COut, c.FileName)));
            }

            if (plan.EmitPython)
            {
                var py = PythonEmitter.EmitSubroutinePage(resolved);
                WriteIfChanged(Path.Combine(plan.PythonOut, py.FileName), py.Content);
                writtenPython.Add(Path.GetFullPath(Path.Combine(plan.PythonOut, py.FileName)));
            }

            Console.WriteLine($"  ok  {subPage.SourcePath}  (subroutines){(className.Length > 0 ? "  →  " + className + ".g.cs" : "")}");
        }

        // Generated closed verb set (SP-010 / packet.net#260): every canonical
        // action verb across all pages + subroutines, so a runtime dispatcher can
        // switch exhaustively (a new/renamed verb becomes a compile error rather
        // than an "unknown SDL action" thrown at runtime). Emitted as a C# enum,
        // a TS string-literal union, and a Rust enum (ADR-0002 extended to Rust
        // so a no_std embedded consumer can `match` exhaustively); Go / C /
        // Python / JSON keep the string verb.
        if (plan.EmitCsharp || plan.EmitTs || plan.EmitRust)
        {
            var allVerbs = resolvedPages
                .SelectMany(p => p.Transitions).SelectMany(t => t.Actions).Select(a => a.Verb)
                .Concat(resolvedSubPages
                    .SelectMany(p => p.Subroutines).SelectMany(s => s.Paths).SelectMany(path => path.Actions).Select(a => a.Verb))
                .ToList();

            // Generated closed guard set (SP-010 step for guards): every
            // canonical guard atom across all pages + subroutines, gathered from
            // the resolved IR so it includes atoms the Resolver synthesises that
            // never appear as a raw decision `predicate:` (e.g. the stale-read
            // substitution's `vs_eq_nr`; ax25sdl#53). A transition guard is a
            // conjunction of these atoms, so we parse each composed guard /
            // predicate string back into its atoms. Lets a guard evaluator bind
            // every atom exhaustively (a new/renamed atom is a compile error, not
            // an "unbound identifier" thrown at runtime).
            var allGuardAtoms = resolvedPages
                .SelectMany(p => p.Transitions)
                .SelectMany(t => GuardExpression.Atoms(t.Guard)
                    .Concat(t.Loops.Select(l => GuardExpression.ParseSingle(l.Predicate).Atom))
                    .Concat(t.UndefinedBranches.Select(u => u.Predicate)))
                .Concat(resolvedSubPages
                    .SelectMany(p => p.Subroutines).SelectMany(s => s.Paths)
                    .SelectMany(path => GuardExpression.Atoms(path.Guard)
                        .Concat(path.Loops.Select(l => GuardExpression.ParseSingle(l.Predicate).Atom))))
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .ToList();

            if (plan.EmitCsharp)
            {
                var verbEnumPath = Path.Combine(plan.CsharpOut, "Ax25ActionVerb.g.cs");
                WriteIfChanged(verbEnumPath, CsharpEmitter.EmitActionVerbEnum(allVerbs));
                writtenCsharpCode.Add(Path.GetFullPath(verbEnumPath));
                Console.WriteLine("  ok  (all pages + subroutines)  →  Ax25ActionVerb.g.cs");

                var guardEnumPath = Path.Combine(plan.CsharpOut, "Ax25Guard.g.cs");
                WriteIfChanged(guardEnumPath, CsharpEmitter.EmitGuardEnum(allGuardAtoms));
                writtenCsharpCode.Add(Path.GetFullPath(guardEnumPath));
                Console.WriteLine("  ok  (all pages + subroutines)  →  Ax25Guard.g.cs");

                var eventEnumPath = Path.Combine(plan.CsharpOut, "Ax25Event.g.cs");
                WriteIfChanged(eventEnumPath, CsharpEmitter.EmitEventEnum(events));
                writtenCsharpCode.Add(Path.GetFullPath(eventEnumPath));
                Console.WriteLine("  ok  (events.yaml)  →  Ax25Event.g.cs");
            }
            if (plan.EmitTs)
            {
                var verbUnionPath = Path.Combine(plan.TsOut, "ax25-action-verb.g.ts");
                WriteIfChanged(verbUnionPath, TsEmitter.EmitActionVerbUnion(allVerbs));
                writtenTs.Add(Path.GetFullPath(verbUnionPath));
                Console.WriteLine("  ok  (all pages + subroutines)  →  ax25-action-verb.g.ts");

                var guardUnionPath = Path.Combine(plan.TsOut, "ax25-guard.g.ts");
                WriteIfChanged(guardUnionPath, TsEmitter.EmitGuardUnion(allGuardAtoms));
                writtenTs.Add(Path.GetFullPath(guardUnionPath));
                Console.WriteLine("  ok  (all pages + subroutines)  →  ax25-guard.g.ts");

                var eventUnionPath = Path.Combine(plan.TsOut, "ax25-event.g.ts");
                WriteIfChanged(eventUnionPath, TsEmitter.EmitEventUnion(events));
                writtenTs.Add(Path.GetFullPath(eventUnionPath));
                Console.WriteLine("  ok  (events.yaml)  →  ax25-event.g.ts");
            }
            if (plan.EmitRust)
            {
                var verbEnumPath = Path.Combine(plan.RustOut, "ax25_action_verb.g.rs");
                WriteIfChanged(verbEnumPath, RustEmitter.EmitActionVerbEnum(allVerbs));
                writtenRust.Add(Path.GetFullPath(verbEnumPath));
                Console.WriteLine("  ok  (all pages + subroutines)  →  ax25_action_verb.g.rs");

                var guardEnumPath = Path.Combine(plan.RustOut, "ax25_guard.g.rs");
                WriteIfChanged(guardEnumPath, RustEmitter.EmitGuardEnum(allGuardAtoms));
                writtenRust.Add(Path.GetFullPath(guardEnumPath));
                Console.WriteLine("  ok  (all pages + subroutines)  →  ax25_guard.g.rs");

                var eventEnumPath = Path.Combine(plan.RustOut, "ax25_event.g.rs");
                WriteIfChanged(eventEnumPath, RustEmitter.EmitEventEnum(events));
                writtenRust.Add(Path.GetFullPath(eventEnumPath));
                Console.WriteLine("  ok  (events.yaml)  →  ax25_event.g.rs");
            }
        }

        // TS package needs an index.ts that re-exports every page so
        // consumers can `import { DataLinkConnected } from "ax25sdl"`.
        // Go doesn't need this — every var declared in a package is
        // visible from the package namespace already.
        if (plan.EmitTs)
        {
            var indexPath = Path.Combine(plan.TsOut, "index.ts");
            WriteIfChanged(indexPath, TsEmitter.EmitIndex(resolvedPages, resolvedSubPages));
            writtenTs.Add(Path.GetFullPath(indexPath));
        }

        // JSON consumers get an index.json manifest mapping every emitted
        // .g.json to its page identity (machine / state / figure). Lets a
        // jq pipeline or Python script enumerate the spec without reading
        // every file's contents to discover what's there.
        if (plan.EmitJson)
        {
            var indexPath = Path.Combine(plan.JsonOut, "index.json");
            var indexText = JsonEmitter.EmitIndex(resolvedPages, resolvedSubPages);
            JsonEmitter.ValidateAgainstSchema(jsonSchemaText!, indexText, indexPath);
            WriteIfChanged(indexPath, indexText);
            writtenJson.Add(Path.GetFullPath(indexPath));
        }

        // Rust crate needs a lib.rs that declares each generated module
        // and re-exports them — without it `cargo build` doesn't see the
        // .g.rs files (they're not auto-discovered by file name).
        if (plan.EmitRust)
        {
            var libPath = Path.Combine(plan.RustOut, "lib.rs");
            WriteIfChanged(libPath, RustEmitter.EmitLib(resolvedPages, resolvedSubPages));
            writtenRust.Add(Path.GetFullPath(libPath));
        }

        // C library needs a master generated header so test sources
        // (and any other consumer) can `#include "ax25sdl.g.h"` and pick
        // up every `extern const StatePage ...` declaration in one go.
        if (plan.EmitC)
        {
            var headerPath = Path.Combine(plan.COut, "ax25sdl.g.h");
            WriteIfChanged(headerPath, CEmitter.EmitHeader(resolvedPages, resolvedSubPages));
            writtenCSrc.Add(Path.GetFullPath(headerPath));
        }

        // Python package needs an __init__.py that re-exports every page
        // constant. The .g.py filenames embed a literal dot which makes
        // `from .<stem>.g import …` invalid; __init__.py uses importlib
        // at module-init time to surface the constants at package scope.
        if (plan.EmitPython)
        {
            var initPath = Path.Combine(plan.PythonOut, "__init__.py");
            WriteIfChanged(initPath, PythonEmitter.EmitInit(resolvedPages, resolvedSubPages));
            writtenPython.Add(Path.GetFullPath(initPath));
        }

        // Tidy stale generated files (someone deleted a *.sdl.yaml).
        // Cleanups are scoped to "the files this run produced" so we
        // never sweep across runs that omit a backend — e.g. running
        // with --csharp doesn't delete spec/go/ax25sdl/*.g.go.
        if (plan.EmitCsharp)
        {
            CleanStaleFiles(plan.CsharpOut,   "*.g.cs",       writtenCsharpCode);
            CleanStaleFiles(plan.CsharpTests, "*.g.Tests.cs", writtenCsharpTests);
            if (plan.MermaidOut is not null)
            {
                CleanStaleFiles(plan.MermaidOut, "*.g.mmd", writtenMermaid);
            }
            else
            {
                foreach (var dir in Directory.EnumerateDirectories(plan.InDir, "*", SearchOption.AllDirectories))
                {
                    CleanStaleFiles(dir, "*.g.mmd", writtenMermaid);
                }
            }
        }
        if (plan.EmitGo)
        {
            CleanStaleFiles(plan.GoOut, "*.g.go",      writtenGo);
            CleanStaleFiles(plan.GoOut, "*.g_test.go", writtenGo);
            RunGofmt(plan.GoOut);
        }
        if (plan.EmitTs)
        {
            // index.ts and types.ts are intentionally outside both
            // patterns (the cleanup is scoped) so they survive across
            // codegen runs.
            CleanStaleFiles(plan.TsOut, "*.g.ts",      writtenTs);
            CleanStaleFiles(plan.TsOut, "*.g.test.ts", writtenTs);
        }
        if (plan.EmitJson)
        {
            // schema.json + index.json are outside the *.g.json glob and
            // were added to writtenJson at emission time, so they survive
            // the cleanup naturally.
            CleanStaleFiles(plan.JsonOut, "*.g.json", writtenJson);
        }
        if (plan.EmitRust)
        {
            // lib.rs is added to writtenRust at emission time, so it
            // survives the *.g.rs cleanup. types.rs is hand-written and
            // outside the glob. rustfmt canonicalises the per-page files.
            CleanStaleFiles(plan.RustOut, "*.g.rs", writtenRust);
            RunRustfmt(plan.RustOut);
        }
        if (plan.EmitC)
        {
            // Hand-written sources (ax25sdl.h, smoke.test.c, CMakeLists,
            // README) live outside the *.g.{c,h} globs and survive
            // cleanup. clang-format runs over src/ + test/ to wrap
            // long struct-initialiser strings into readable multi-line
            // form. The CI clang-format check enforces this canonical
            // shape; if clang-format isn't installed, the emitter's
            // raw single-line output gets written instead and CI's
            // dry-run check fails loudly — by design, since clang-format
            // is part of the documented runner toolchain.
            CleanStaleFiles(plan.COut,           "*.g.c",      writtenCSrc);
            CleanStaleFiles(plan.COut,           "*.g.h",      writtenCSrc);
            CleanStaleFiles(CTestDir(plan.COut), "*.g.test.c", writtenCTest);
            RunClangFormat(plan.COut);
            RunClangFormat(CTestDir(plan.COut));
        }
        if (plan.EmitPython)
        {
            // types.py and __init__.py both live outside the *.g.py /
            // *_g_test.py patterns. types.py is hand-written; __init__.py
            // is in writtenPython explicitly so the cleanup spares it.
            CleanStaleFiles(plan.PythonOut, "*.g.py",      writtenPython);
            CleanStaleFiles(plan.PythonOut, "*_g_test.py", writtenPython);
        }

        var which = string.Join(" + ", new[] {
            plan.EmitCsharp ? "C#"     : null,
            plan.EmitGo     ? "Go"     : null,
            plan.EmitTs     ? "TS"     : null,
            plan.EmitJson   ? "JSON"   : null,
            plan.EmitRust   ? "Rust"   : null,
            plan.EmitC      ? "C"      : null,
            plan.EmitPython ? "Python" : null,
        }.Where(s => s is not null));
        Console.WriteLine($"generated {pages.Count} state machine page(s), {subroutinePages.Count} subroutine page(s) [{which}]");
        return 0;
    }

    /// <summary>
    /// Shell out to <c>gofmt -w</c> on the Go output directory. gofmt's
    /// struct-field alignment rules are subtle (different alignment
    /// "runs" form around multi-line literal fields) and it's much
    /// simpler to delegate canonicalisation to gofmt itself than to
    /// re-implement it in the emitter. If gofmt isn't on PATH we emit
    /// a warning rather than fail — the codegen-idempotent CI check
    /// runs gofmt separately, so a missing gofmt locally is visible
    /// but non-blocking.
    /// </summary>
    private static void RunGofmt(string goDir)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "gofmt",
                ArgumentList = { "-w", goDir },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (p is null)
            {
                throw new InvalidOperationException(
                    "gofmt is not available, so the emitted Go files would not be canonically "
                    + "formatted. Continuing would leave the CI drift check comparing unformatted "
                    + "output against a formatted corpus, which reports a large and entirely "
                    + "misleading diff; that is what kept the Rust drift job red from 2026-06-14. "
                    + "Fix the toolchain rather than the corpus: install Go; gofmt ships with the toolchain.");
            }
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                Console.Error.WriteLine($"::warning::gofmt exited {p.ExitCode}: {p.StandardError.ReadToEnd()}");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                "gofmt is not available, so the emitted Go files would not be canonically "
                + "formatted. Continuing would leave the CI drift check comparing unformatted "
                + "output against a formatted corpus, which reports a large and entirely "
                + "misleading diff; that is what kept the Rust drift job red from 2026-06-14. "
                + "Fix the toolchain rather than the corpus: install Go; gofmt ships with the toolchain.");
        }
    }

    /// <summary>
    /// Shell out to <c>rustfmt</c> on every <c>*.rs</c> file in the Rust
    /// output directory. Mirrors <see cref="RunGofmt"/>: the emitter
    /// aims for output that's close to canonical and rustfmt handles
    /// the last-mile alignment. Missing rustfmt produces a warning,
    /// not a failure — the CI discipline job runs <c>cargo fmt --check</c>
    /// separately and will catch any drift.
    /// </summary>
    /// <summary>
    /// Verify a formatter is not merely on PATH but actually runnable, and abort if not.
    /// rustup installs a shim at ~/.cargo/bin/rustfmt that exists even when the rustfmt
    /// component is not installed for the active toolchain, so a `command -v` style check
    /// is a false positive: the process starts and then exits non-zero. That is exactly
    /// how the Rust drift job came to be red from 2026-06-14. Without a usable formatter
    /// the emitted corpus is not canonical, and the CI drift check reports a large and
    /// entirely misleading diff, so this is fatal rather than a warning.
    /// A non-zero exit from the formatting run itself is NOT fatal: it happens
    /// legitimately when formatting an incomplete tree, such as a fresh output directory
    /// that has no hand-written types.rs for rustfmt to resolve `mod types` against.
    /// </summary>
    private static void EnsureFormatterRunnable(string tool, string language, string hint)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = tool,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");
            using var probe = System.Diagnostics.Process.Start(psi);
            if (probe is null)
            {
                throw new InvalidOperationException(
                    $"{tool} could not be started, so the emitted {language} files would not be "
                    + $"canonically formatted and the CI drift check would report a misleading "
                    + $"diff. Fix the toolchain rather than the corpus: {hint}.");
            }
            probe.WaitForExit();
            if (probe.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"{tool} is on PATH but not runnable (`{tool} --version` exited "
                    + $"{probe.ExitCode}: {probe.StandardError.ReadToEnd().Trim()}). The emitted "
                    + $"{language} files would not be canonically formatted and the CI drift check "
                    + $"would report a misleading diff. Fix the toolchain: {hint}.");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                $"{tool} was not found on PATH, so the emitted {language} files would not be "
                + $"canonically formatted and the CI drift check would report a misleading diff. "
                + $"Fix the toolchain rather than the corpus: {hint}.");
        }
    }

    private static void RunRustfmt(string rustDir)
    {
        if (!Directory.Exists(rustDir)) return;
        var rsFiles = Directory.EnumerateFiles(rustDir, "*.rs", SearchOption.TopDirectoryOnly).ToList();
        if (rsFiles.Count == 0) return;

        EnsureFormatterRunnable("rustfmt", "Rust", "rustup component add rustfmt");

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "rustfmt",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--edition");
            psi.ArgumentList.Add("2021");
            foreach (var f in rsFiles) psi.ArgumentList.Add(f);

            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                throw new InvalidOperationException(
                    "rustfmt is not available, so the emitted Rust files would not be canonically "
                    + "formatted. Continuing would leave the CI drift check comparing unformatted "
                    + "output against a formatted corpus, which reports a large and entirely "
                    + "misleading diff; that is what kept the Rust drift job red from 2026-06-14. "
                    + "Fix the toolchain rather than the corpus: rustup component add rustfmt.");
            }
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                Console.Error.WriteLine($"::warning::rustfmt exited {p.ExitCode}: {p.StandardError.ReadToEnd()}");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                "rustfmt is not available, so the emitted Rust files would not be canonically "
                + "formatted. Continuing would leave the CI drift check comparing unformatted "
                + "output against a formatted corpus, which reports a large and entirely "
                + "misleading diff; that is what kept the Rust drift job red from 2026-06-14. "
                + "Fix the toolchain rather than the corpus: rustup component add rustfmt.");
        }
    }

    /// <summary>
    /// Sibling <c>test/</c> directory derived from a C source-output
    /// directory. <c>spec/c/src</c> → <c>spec/c/test</c>. CMake's CTest
    /// pulls in <c>test/*.c</c> via a glob, so we keep this convention
    /// rather than expose a separate flag.
    /// </summary>
    private static string CTestDir(string cOut)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(cOut));
        return parent is null ? "test" : Path.Combine(parent, "test");
    }

    /// <summary>Run <c>clang-format -i</c> over all generated C files in a directory. Same warning-on-missing semantics as <see cref="RunGofmt"/> / <see cref="RunRustfmt"/>.</summary>
    private static void RunClangFormat(string dir)
    {
        if (!Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir, "*.g.c")
            .Concat(Directory.GetFiles(dir, "*.g.h"))
            .Concat(Directory.GetFiles(dir, "*.g.test.c"))
            .ToArray();
        if (files.Length == 0) return;

        EnsureFormatterRunnable("clang-format", "C", "pip install clang-format==22.1.5");

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "clang-format",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-i");
            foreach (var f in files) psi.ArgumentList.Add(f);

            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                throw new InvalidOperationException(
                    "clang-format is not available, so the emitted C files would not be canonically "
                    + "formatted. Continuing would leave the CI drift check comparing unformatted "
                    + "output against a formatted corpus, which reports a large and entirely "
                    + "misleading diff; that is what kept the Rust drift job red from 2026-06-14. "
                    + "Fix the toolchain rather than the corpus: pip install clang-format==22.1.5.");
            }
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                Console.Error.WriteLine($"::warning::clang-format exited {p.ExitCode}: {p.StandardError.ReadToEnd()}");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                "clang-format is not available, so the emitted C files would not be canonically "
                + "formatted. Continuing would leave the CI drift check comparing unformatted "
                + "output against a formatted corpus, which reports a large and entirely "
                + "misleading diff; that is what kept the Rust drift job red from 2026-06-14. "
                + "Fix the toolchain rather than the corpus: pip install clang-format==22.1.5.");
        }
    }

    private static readonly HashSet<string> GuardOperators =
        new(new[] { "and", "or", "not" }, StringComparer.Ordinal);

    private static readonly char[] PredicateTokenSeparators = { ' ', '\t' };

    /// <summary>
    /// Catalogue completeness for predicate identifiers: every atom a YAML
    /// decision references must resolve to a <c>spec-sdl/predicates.yaml</c>
    /// canonical, the guard analogue of how every action verb must resolve
    /// to an <c>actions.yaml</c> entry. Catches a typo'd or uncatalogued
    /// predicate at codegen time, and is what lets the emitted
    /// <c>Ax25Guard</c> closed set stay authoritative.
    /// </summary>
    /// <remarks>
    /// Runtime-agnostic: it reads the SDL pages and the catalogue, and no
    /// path outside this repo. Decision predicates have already been
    /// canonicalised against <c>predicates.yaml</c> before this runs, so the
    /// atoms checked here are the canonical spellings the generated tables
    /// carry, and a surviving unrecognised atom is genuinely uncatalogued.
    /// </remarks>
    private static void LintPredicateCatalogue(
        List<SdlPage> pages,
        List<SubroutinePage> subroutinePages,
        PredicateCatalog predicates,
        List<string> errors)
    {
        // Walk every decision in every page, tokenize its predicate,
        // and remember the first YAML location each identifier appears
        // at so the error message can point at it.
        var firstSeen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            foreach (var d in page.Decisions)
            {
                CollectIdents(d.Predicate, $"{page.SourcePath}: decision `{d.Id}`", firstSeen);
            }
        }
        foreach (var page in subroutinePages)
        {
            foreach (var sub in page.Subroutines)
            {
                foreach (var d in sub.Decisions)
                {
                    CollectIdents(d.Predicate, $"{page.SourcePath}: subroutine `{sub.Name}` decision `{d.Id}`", firstSeen);
                }
            }
        }

        // Catalog-completeness: when a predicates.yaml is present, every atom
        // must be one of its canonical names. (Aliases were already rewritten
        // to canonical by NormaliseDecisionPredicates, so a surviving
        // unrecognised atom is genuinely uncatalogued.) Mirrors the way verbs
        // are validated against actions.yaml; without it an emitted guard atom
        // could drift out of the Ax25Guard closed set the consumers bind to.
        if (predicates.Canonicals.Count > 0)
        {
            foreach (var (ident, loc) in firstSeen.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                if (!predicates.Canonicals.Contains(ident))
                {
                    errors.Add(
                        $"{loc}: predicate `{ident}` is not in spec-sdl/predicates.yaml. Add it as a canonical " +
                        "atom (or as an alias of an existing canonical if it's an alternate figure spelling). " +
                        "Every decision predicate must resolve to a catalog entry so the generated Ax25Guard " +
                        "closed set stays authoritative.");
                }
            }
        }

    }

    private static void CollectIdents(string predicate, string location, Dictionary<string, string> firstSeen)
    {
        if (string.IsNullOrWhiteSpace(predicate)) return;
        var tokens = predicate.Split(PredicateTokenSeparators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var tok in tokens)
        {
            if (GuardOperators.Contains(tok)) continue;
            firstSeen.TryAdd(tok, location);
        }
    }

    // ─── State-target lint ──────────────────────────────────────────────

    /// <summary>
    /// Every transition's `next:` must name a state that exists somewhere
    /// in the same machine. Catches transcription typos before the runtime
    /// silently wedges in a state for which no transitions are dispatched.
    /// </summary>
    /// <remarks>
    /// "Same machine" is taken from the page's <c>machine:</c> field. We
    /// collect every declared state per machine from <c>state:</c> fields
    /// across all pages and check `next:` against that set.
    /// </remarks>
    /// <summary>
    /// Allow-list of <c>next:</c> state targets that don't have a
    /// corresponding <c>*.sdl.yaml</c> page yet but ARE registered at
    /// runtime (typically with an empty transition list — see
    /// <c>TransitionMap</c> wiring in the rig builders). Add an entry
    /// here with a one-line reason rather than letting the lint shrug.
    /// </summary>
    private static readonly HashSet<string> StateTargetAllowList = new(StringComparer.Ordinal)
    {
        // figc4.5 not yet transcribed. figc4.4 t38 / t39 (T1 / T3
        // expiry from Connected) target TimerRecovery, which is a real
        // state in the v2.2 spec but its page hasn't been transcribed
        // yet. Runtime registers TransitionMap[TimerRecovery] = empty
        // so the dispatcher dictionary lookup succeeds; semantically a
        // gap until figc4.5 lands. See plan.md §6.4.
        "TimerRecovery",
    };

    private static void LintStateTargets(List<SdlPage> pages, List<string> errors)
    {
        var statesByMachine = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            if (string.IsNullOrWhiteSpace(page.Machine) || string.IsNullOrWhiteSpace(page.State)) continue;
            if (!statesByMachine.TryGetValue(page.Machine, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                statesByMachine[page.Machine] = set;
            }
            set.Add(page.State);
        }

        foreach (var page in pages)
        {
            // `coverage: partial` signals "this transcription is a
            // work-in-progress" — cross-page consistency is intentionally
            // incomplete. Same escape hatch the catchall lint uses.
            if (string.Equals(page.Coverage, "partial", StringComparison.OrdinalIgnoreCase)) continue;
            if (!statesByMachine.TryGetValue(page.Machine, out var validStates)) continue;
            foreach (var t in page.Transitions)
            {
                if (string.IsNullOrWhiteSpace(t.Next)) continue;
                if (validStates.Contains(t.Next)) continue;
                if (StateTargetAllowList.Contains(t.Next)) continue;
                errors.Add(
                    $"{page.SourcePath}: transition `{t.Id}` targets state `{t.Next}` which is not " +
                    $"a known state in machine `{page.Machine}` (known: {string.Join(", ", validStates.OrderBy(x => x, StringComparer.Ordinal))}). " +
                    "Typo, OR add a new *.sdl.yaml page declaring the target state, OR add an entry to " +
                    "StateTargetAllowList in codegen/src/Packet.Sdl.CodeGen/Program.cs with a one-line reason.");
            }
        }
    }

    // ─── Per-state catchall coverage lint ───────────────────────────────

    /// <summary>
    /// Every state should have at least one transition triggered by a
    /// <c>catchalls:</c> event (<c>all_other_primitives__from_lower_layer</c>
    /// / <c>all_other_primitives__from_upper_layer</c> / <c>all_other_commands</c>).
    /// Without a catchall, events the state doesn't explicitly handle
    /// silently no-op — which can mask real transcription gaps. The
    /// lint is intentionally tolerant: it requires SOME catchall, not
    /// full event coverage (deciding which events a state should handle
    /// is the spec author's call).
    /// </summary>
    private static readonly string[] CatchallEvents =
    {
        "all_other_primitives__from_lower_layer",
        "all_other_primitives__from_upper_layer",
        "all_other_commands",
    };

    private static void LintCatchallCoverage(List<SdlPage> pages, List<string> errors)
    {
        foreach (var page in pages)
        {
            // Skip pages explicitly marked partial. `coverage: partial`
            // signals "this is a work-in-progress transcription"; the
            // catchall requirement applies once the page is complete.
            if (string.Equals(page.Coverage, "partial", StringComparison.OrdinalIgnoreCase)) continue;

            bool hasCatchall = page.Transitions.Any(t => CatchallEvents.Contains(t.On, StringComparer.Ordinal));
            if (!hasCatchall)
            {
                errors.Add(
                    $"{page.SourcePath}: state `{page.State}` has no transition triggered by any of " +
                    $"`{string.Join("` / `", CatchallEvents)}`. Add a catchall transition (or mark the page " +
                    "`coverage: partial` if this is a work-in-progress transcription) so events not " +
                    "explicitly handled don't silently no-op at runtime.");
            }
        }
    }

    // When the SDL source lives under a `yaml/` folder (the layout used under
    // spec-sdl/<rev>/<machine>/{sdl,yaml,mmd}/), redirect the default mermaid
    // output to the sibling `mmd/` folder so artefacts stay grouped by kind.
    // Source paths that don't match this pattern (test fixtures, ad-hoc one-off
    // yamls) fall back to writing alongside the source.
    private static string SiblingMermaidDir(string sourcePath)
    {
        var dir = Path.GetDirectoryName(sourcePath)!;
        if (string.Equals(Path.GetFileName(dir), "yaml", StringComparison.Ordinal))
        {
            return Path.Combine(Path.GetDirectoryName(dir)!, "mmd");
        }
        return dir;
    }

    private static void WriteIfChanged(string path, string contents)
    {
        if (File.Exists(path) && File.ReadAllText(path) == contents) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static void CleanStaleFiles(string dir, string pattern, HashSet<string> keep)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, pattern))
        {
            if (!keep.Contains(Path.GetFullPath(f)))
            {
                File.Delete(f);
                Console.WriteLine($"  removed stale {f}");
            }
        }
    }
}

/// <summary>
/// CLI surface for <see cref="Program"/>. Parsed by CommandLineParser.
/// </summary>
/// <remarks>
/// Empty-string sentinel means "not specified". CommandLineParser's
/// nullable-reference-type support pre-dates NRTs cleanly, so we use
/// <c>Default = ""</c> + length check rather than <c>string?</c>.
/// </remarks>
internal sealed class CodegenOptions
{
    [Option("in", Default = "spec-sdl", HelpText = "Directory containing *.sdl.yaml inputs.")]
    public string InDir { get; set; } = "spec-sdl";

    // ─── C# ────────────────────────────────────────────────────────────
    [Option("csharp", Default = false, HelpText = "Emit C# backend (defaults to spec/csharp + spec/csharp/tests).")]
    public bool Csharp { get; set; }

    [Option("csharp-out", Default = "", HelpText = "C# source output directory. Implies --csharp. Defaults to spec/csharp.")]
    public string CsharpOut { get; set; } = "";

    [Option("csharp-tests", Default = "", HelpText = "C# generated-tests output directory. Implies --csharp. Defaults to spec/csharp/tests.")]
    public string CsharpTests { get; set; } = "";

    [Option("mermaid-out", Default = "", HelpText = "Mermaid output directory (gated on --csharp). When unset and the *.sdl.yaml lives under a `yaml/` folder, emits .g.mmd to the sibling `mmd/` folder; otherwise emits alongside the *.sdl.yaml.")]
    public string MermaidOut { get; set; } = "";

    // ─── Go ────────────────────────────────────────────────────────────
    [Option("go", Default = false, HelpText = "Emit Go backend (defaults to spec/go/ax25sdl).")]
    public bool Go { get; set; }

    [Option("go-out", Default = "", HelpText = "Go output directory. Implies --go. Defaults to spec/go/ax25sdl.")]
    public string GoOut { get; set; } = "";

    // ─── TypeScript ────────────────────────────────────────────────────
    [Option("ts", Default = false, HelpText = "Emit TypeScript backend (defaults to spec/ts/src/ax25sdl).")]
    public bool Ts { get; set; }

    [Option("ts-out", Default = "", HelpText = "TypeScript output directory. Implies --ts. Defaults to spec/ts/src/ax25sdl.")]
    public string TsOut { get; set; } = "";

    // ─── JSON ──────────────────────────────────────────────────────────
    [Option("json", Default = false, HelpText = "Emit JSON backend (defaults to spec/json/).")]
    public bool Json { get; set; }

    [Option("json-out", Default = "", HelpText = "JSON output directory. Implies --json. Defaults to spec/json.")]
    public string JsonOut { get; set; } = "";

    // ─── Rust ──────────────────────────────────────────────────────────
    [Option("rust", Default = false, HelpText = "Emit Rust backend (defaults to spec/rust/src).")]
    public bool Rust { get; set; }

    [Option("rust-out", Default = "", HelpText = "Rust source output directory. Implies --rust. Defaults to spec/rust/src.")]
    public string RustOut { get; set; } = "";

    // ─── C ─────────────────────────────────────────────────────────────
    //
    // CommandLineParser rejects single-character long names ("--c" would
    // throw at startup), so we expose the C backend as `--emit-c` long
    // form / `-c` short form. The path option uses `--c-out` to stay
    // consistent with the other backends' `<lang>-out` pattern.
    [Option('c', "emit-c", Default = false, HelpText = "Emit C backend (defaults to spec/c/src + sibling spec/c/test).")]
    public bool C { get; set; }

    [Option("c-out", Default = "", HelpText = "C source output directory. Implies --emit-c. Defaults to spec/c/src. The test/ sibling of this directory receives the .g.test.c files.")]
    public string COut { get; set; } = "";

    // ─── Python ────────────────────────────────────────────────────────
    [Option("python", Default = false, HelpText = "Emit Python backend (defaults to spec/python/ax25sdl).")]
    public bool Python { get; set; }

    [Option("python-out", Default = "", HelpText = "Python output directory. Implies --python. Defaults to spec/python/ax25sdl.")]
    public string PythonOut { get; set; } = "";
}

/// <summary>
/// Resolved plan derived from <see cref="CodegenOptions"/>. Encapsulates
/// "which backends to emit, with which paths". Built by
/// <see cref="From"/>, which applies the default-all-when-nothing-specified
/// rule and resolves blank-sentinel paths to their conventional defaults.
/// </summary>
internal sealed class CodegenPlan
{
    public required string InDir { get; init; }
    public required bool EmitCsharp { get; init; }
    public required bool EmitGo { get; init; }
    public required bool EmitTs { get; init; }
    public required bool EmitJson { get; init; }
    public required bool EmitRust { get; init; }
    public required bool EmitC { get; init; }
    public required bool EmitPython { get; init; }
    public required string CsharpOut { get; init; }
    public required string CsharpTests { get; init; }
    public required string GoOut { get; init; }
    public required string TsOut { get; init; }
    public required string JsonOut { get; init; }
    public required string RustOut { get; init; }
    public required string COut { get; init; }
    public required string PythonOut { get; init; }
    public required string? MermaidOut { get; init; }

    private const string DefaultCsharpOut   = "spec/csharp";
    private const string DefaultCsharpTests = "spec/csharp/tests";
    private const string DefaultGoOut       = "spec/go/ax25sdl";
    private const string DefaultTsOut       = "spec/ts/src/ax25sdl";
    private const string DefaultJsonOut     = "spec/json";
    private const string DefaultRustOut     = "spec/rust/src";
    private const string DefaultCOut        = "spec/c/src";
    private const string DefaultPythonOut   = "spec/python/ax25sdl";

    public static CodegenPlan From(CodegenOptions opt)
    {
        // A backend is "explicitly enabled" when either its bare flag or
        // any of its path options is set. Per-backend path options
        // therefore both *select* the backend and *configure* its output
        // — there's no way to express "emit Go but I don't care where".
        bool csharpExplicit = opt.Csharp || opt.CsharpOut.Length > 0 || opt.CsharpTests.Length > 0 || opt.MermaidOut.Length > 0;
        bool goExplicit     = opt.Go     || opt.GoOut.Length > 0;
        bool tsExplicit     = opt.Ts     || opt.TsOut.Length > 0;
        bool jsonExplicit   = opt.Json   || opt.JsonOut.Length > 0;
        bool rustExplicit   = opt.Rust   || opt.RustOut.Length > 0;
        bool cExplicit      = opt.C      || opt.COut.Length > 0;
        bool pythonExplicit = opt.Python || opt.PythonOut.Length > 0;
        bool anyExplicit    = csharpExplicit || goExplicit || tsExplicit || jsonExplicit
                              || rustExplicit || cExplicit || pythonExplicit;

        // Default rule: no language flags at all → emit every backend.
        bool emitCsharp = anyExplicit ? csharpExplicit : true;
        bool emitGo     = anyExplicit ? goExplicit     : true;
        bool emitTs     = anyExplicit ? tsExplicit     : true;
        bool emitJson   = anyExplicit ? jsonExplicit   : true;
        bool emitRust   = anyExplicit ? rustExplicit   : true;
        bool emitC      = anyExplicit ? cExplicit      : true;
        bool emitPython = anyExplicit ? pythonExplicit : true;

        return new CodegenPlan
        {
            InDir       = opt.InDir,
            EmitCsharp  = emitCsharp,
            EmitGo      = emitGo,
            EmitTs      = emitTs,
            EmitJson    = emitJson,
            EmitRust    = emitRust,
            EmitC       = emitC,
            EmitPython  = emitPython,
            CsharpOut   = opt.CsharpOut.Length > 0 ? opt.CsharpOut : DefaultCsharpOut,
            CsharpTests = opt.CsharpTests.Length > 0 ? opt.CsharpTests : DefaultCsharpTests,
            GoOut       = opt.GoOut.Length > 0 ? opt.GoOut : DefaultGoOut,
            TsOut       = opt.TsOut.Length > 0 ? opt.TsOut : DefaultTsOut,
            JsonOut     = opt.JsonOut.Length > 0 ? opt.JsonOut : DefaultJsonOut,
            RustOut     = opt.RustOut.Length > 0 ? opt.RustOut : DefaultRustOut,
            COut        = opt.COut.Length > 0 ? opt.COut : DefaultCOut,
            PythonOut   = opt.PythonOut.Length > 0 ? opt.PythonOut : DefaultPythonOut,
            MermaidOut  = opt.MermaidOut.Length > 0 ? opt.MermaidOut : null,
        };
    }
}
