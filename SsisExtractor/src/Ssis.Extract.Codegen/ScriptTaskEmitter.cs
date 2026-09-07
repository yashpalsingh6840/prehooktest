using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits the generated HALF of a ported SSIS Script Task -- e.g.
/// <c>Package.ScriptTasks.ScrValidateAndLogStartScriptTask</c>: an <c>ILoadTask</c> whose
/// interface method, name, and timing are fully generated, and whose actual logic is a single
/// unimplemented <c>private partial Task RunScriptAsync(ScriptTaskContext ctx, CancellationToken ct)</c>.
///
/// **Why a private partial helper rather than a partial interface method.** The plan for this
/// feature flagged "can a partial method implement an interface member under net10?" as an open
/// question to resolve rather than assume. It is moot with this shape, which is strictly better
/// anyway: the interface method is fully implemented in generated code, so the Name/StepResult/
/// timing plumbing stays consistent across every package and a fill cannot get it subtly wrong.
/// Verified against Roslyn before designing against it (same discipline as the Script Component
/// seams): an unimplemented <c>private partial</c> with a return type is exactly one
/// <c>CS8795</c> naming the method, and a second part supplying
/// <c>private partial async Task RunScriptAsync(...)</c> compiles clean under
/// <c>Nullable</c> + <c>TreatWarningsAsErrors</c>, static fields included.
///
/// **What the seam is NOT.** No attempt is made to translate the script's own source. It is C# or
/// VB written against the SSIS object model (<c>Dts.Connections</c>, <c>Dts.Variables</c>,
/// <c>Dts.Events</c>, <c>Dts.TaskResult</c>) with no mechanical mapping to these abstractions --
/// that is precisely what makes it Tier 2 (missing LOGIC, source present) rather than a missing
/// datum or missing tool support. The work packet carries the verbatim source; a human ports it.
/// </summary>
public static class ScriptTaskEmitter
{
    /// <summary>The C# class name for a Script Task, e.g. "SCR_ValidateAndLogStart" ->
    /// "ScrValidateAndLogStartScriptTask". Shared with <see cref="ProgramEmitter"/> and with
    /// <c>AiPacketEmitter</c>, which both have to name the very same type -- a work packet telling
    /// someone to write a partial part of the wrong class name would be worse than no packet.</summary>
    public static string ClassName(ExecutableSpec task) => ClassName(task.ObjectName ?? task.RefId);

    /// <summary>As <see cref="ClassName(ExecutableSpec)"/>, from the raw task name alone -- what
    /// <c>ssisx apply-fills</c> needs, since it works from <c>gaps.json</c> and never has the
    /// ExecutableSpec. Both callers must agree exactly or a fill would bind to nothing.</summary>
    public static string ClassName(string raw)
    {
        var chars = raw.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray();
        var cleaned = new string(chars);
        if (cleaned.Length == 0) cleaned = "ScriptTask";
        if (char.IsDigit(cleaned[0])) cleaned = "_" + cleaned;

        // "SCR_VALIDATE" would otherwise stay shouting; "SCR_ValidateAndLogStart" keeps its own
        // casing. Only the first character is forced, so an author's intent survives.
        cleaned = char.ToUpperInvariant(cleaned[0]) + cleaned[1..];
        return cleaned.EndsWith("ScriptTask", StringComparison.Ordinal) ? cleaned : cleaned + "ScriptTask";
    }

    /// <summary>The seam's own method name -- constant, because there is exactly one seam per
    /// Script Task class and a stable name is what lets a fill written months ago still bind.</summary>
    public const string SeamMethodName = "RunScriptAsync";

    public static EmitResult Emit(string ns, ExecutableSpec task)
    {
        var className = ClassName(task);
        var taskName = task.ObjectName ?? task.RefId;
        var script = task.ScriptTask;

        var lines = new List<string>
        {
            "using System.Diagnostics;",
            "using Etl.Core.Abstractions;",
            "",
            $"namespace {ns};",
            "",
            "/// <summary>",
            $"/// Ported SSIS Script Task '{taskName}'"
                + (script?.Language is { Length: > 0 } lang ? $" (original language: {lang})." : "."),
            "///",
            $"/// The logic is NOT generated -- it lives in a hand-written second part of this class,",
            $"/// implementing <see cref=\"{SeamMethodName}\"/>. Until one exists the project does not",
            "/// compile (CS8795), which is deliberate: a Script Task silently omitted would leave a",
            "/// package that looks complete and does less than the original.",
        };

        if (script is { ReadOnlyVariables.Count: > 0 })
            lines.Add($"/// Declared ReadOnlyVariables: {string.Join(", ", script.ReadOnlyVariables)}");
        if (script is { ReadWriteVariables.Count: > 0 })
            lines.Add($"/// Declared ReadWriteVariables: {string.Join(", ", script.ReadWriteVariables)}");

        lines.AddRange([
            "/// </summary>",
            $"public sealed partial class {className}(PackageVariables variables, IServiceProvider services) : ILoadTask",
            "{",
            $"    public string Name => \"{taskName.Replace("\\", "\\\\").Replace("\"", "\\\"")}\";",
            "",
            "    public async Task<StepResult> RunAsync(IUnitOfWork uow, LoadContext load, CancellationToken ct)",
            "    {",
            "        var started = Stopwatch.GetTimestamp();",
            $"        await {SeamMethodName}(new ScriptTaskContext(uow, load, variables, services), ct);",
            "",
            "        // A Script Task moves no rows through a pipeline, so the row counts are 0 rather",
            "        // than invented. Elapsed time is real.",
            "        return new StepResult(Name, 0, 0, Stopwatch.GetElapsedTime(started));",
            "    }",
            "",
            "    /// <summary>",
            $"    /// Port of '{taskName}'. Implement in a second part of this class -- see the",
            $"    /// SCRIPT-TASK work packet for this task, which carries its verbatim original source.",
            "    /// </summary>",
            $"    private partial Task {SeamMethodName}(ScriptTaskContext ctx, CancellationToken ct);",
            "}",
        ]);

        var gap = new GenerationGap($"{taskName}.ScriptTask",
            $"Script Task '{taskName}' is emitted as a `private partial Task {SeamMethodName}(ScriptTaskContext ctx, CancellationToken ct)` " +
            $"seam on '{ns}.{className}'. The project will not compile (CS8795) until a second part of that class " +
            "implements it; port the source carried in this gap's own work packet and apply it with `ssisx apply-fills`.",
            Kind: GapKind.ScriptTask,
            EvidenceRefId: task.RefId);

        // Companion "Script Task seam" taxonomy row (Docs/Generated-Tests-Plan.md): a filled seam
        // is human logic, and a test for it is a test-oracle packet, not something the
        // deterministic emitter can derive -- a separate work item from the port itself, sharing
        // its own Location so both land under the same task in `gaps.json` (the different KIND
        // prefix already makes the two GapIds distinct). Non-blocking: a missing test for a
        // seam does not stop the package building.
        var testGap = new GenerationGap($"{taskName}.ScriptTask",
            $"Script Task '{taskName}' has no generated test at all (a filled seam is human logic -- " +
            "see the TEST-ORACLE work packet for this task to write one, exercising the ported " +
            $"RunScriptAsync via a real PackageHarness and ctx.Variables/ctx.Uow).",
            IsBlocking: false, Kind: GapKind.TestOracle, EvidenceRefId: task.RefId);

        return new EmitResult(
            [new GeneratedFile($"ScriptTasks/{className}.cs", Rendering.JoinLines(lines))],
            [gap, testGap]);
    }
}
