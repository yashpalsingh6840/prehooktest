namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits one Percentage Sampling component's routing decision, e.g.
/// LoadX.Mapping.PCTSAMP_TenPercentSamplingRouter -- an <c>IRowRouter&lt;TRow&gt;</c>
/// implementation reused verbatim by <c>ConditionalSplitStep&lt;TRow&gt;</c>
/// (<see cref="PackageClassEmitter"/>'s existing <c>ProgramConditionalSplitStep</c> emission
/// case needs no changes at all for this component type -- see <c>PctSamplingPlan</c>'s own doc
/// comment for why).
///
/// Unlike <see cref="RouterEmitter"/> (Conditional Split), there is no expression to translate
/// here at all -- <c>Microsoft.PctSampling</c> is a pure row router keyed on a single
/// declared percentage (<c>SamplingValue</c>) and a seed (<c>SamplingSeed</c>), confirmed from
/// real evidenced XML (UseCase_89's own Package.dtsx): both of the component's outputs declare
/// empty <c>&lt;externalMetadataColumns/&gt;</c>, i.e. it neither adds, removes, nor transforms
/// any column -- so this emitter needs no reference/column resolution machinery at all, only the
/// two int properties.
///
/// <b>Seed reproducibility, measured via a real dtexec probe before writing this file (never
/// guessed -- see this project's own "ask the runtime, don't guess" discipline):</b> built
/// <c>SyntheticPctSampling.dtsx</c> (200 seeded source rows, <c>SamplingValue=30</c>,
/// <c>SamplingSeed=424242</c>) and ran it via real <c>dtexec</c> TWICE against the identical
/// input, truncating both destination tables between runs. The exact same 49 row IDs landed in
/// the "sampled" destination both times (a byte-for-byte <c>Compare-Object</c> diff came back
/// empty) -- <c>SamplingSeed</c> genuinely makes SSIS's own sampling decision fully
/// reproducible, not just its aggregate percentage. Changing ONLY <c>SamplingSeed</c> (to
/// 999999, same <c>SamplingValue=30</c>, same 200 input rows) produced a DIFFERENT 49-row set
/// (78 differing lines across the two 49-row sets) -- confirming the seed genuinely drives the
/// selection, not some seed-independent, input-derived hash.
///
/// So this emitter seeds a plain <c>System.Random</c> directly from <c>SamplingSeed</c>'s own
/// value (0 is SSIS's own schema default and a perfectly ordinary, valid .NET seed -- no special
/// casing needed) -- reproducible across repeated runs of the SAME generated executable, exactly
/// matching real SSIS's own measured behaviour for a fixed seed. <b>Honestly stated, not
/// guessed:</b> .NET's <c>Random</c> and SSIS's own internal sampling RNG are different, entirely
/// undocumented algorithms -- reproducing SSIS's own EXACT per-row selection (which specific IDs
/// land in which output) was never attempted and is not claimed here. What IS reproduced: (a) the
/// declared percentage split, statistically, over enough rows, and (b) SSIS's own qualitative
/// behaviour that repeated runs of the identical package/seed/input produce identical output --
/// this generated router reproduces (b) for itself, using its own (different, also seeded)
/// algorithm, not SSIS's.
/// </summary>
public static class PctSamplingRouterEmitter
{
    /// <summary>Emits <c>Mapping/{routerClassName}.cs</c> -- branch 0 is always
    /// "sampled"/"Sampling Selected Output", branch 1 is always "not sampled"/"Sampling
    /// Unselected Output", matching <see cref="PackagePlanner.PctSamplingPlan"/>'s own
    /// (Sampled, NotSampled) field order, which in turn matches the real object-model-confirmed
    /// output declaration order (see <c>Ssis.Extract.FixtureBuilder</c>'s own
    /// <c>ProbePctSampling</c> probe: "Sampling Selected Output" is always index 0).</summary>
    public static EmitResult Emit(string ns, string routerClassName, string rowTypeNamespace, string rowTypeName, int samplingValue, int samplingSeed)
    {
        var lines = new List<string>
        {
            $"using {rowTypeNamespace};",
            "using Etl.Core.Abstractions;",
            "",
            $"namespace {ns};",
            "",
            $"/// <summary>Percentage Sampling, translated from SamplingValue={samplingValue}/SamplingSeed={samplingSeed}.",
            "/// Seeded from the SSIS component's own SamplingSeed for a run of THIS generated executable",
            "/// to reproduce the identical split across repeated runs -- matching real SSIS's own measured",
            "/// behaviour for a fixed seed (see this class's own containing file header for the dtexec",
            "/// probe that confirmed this). The exact PER-ROW selection will not match real SSIS's own",
            "/// (a different, undocumented RNG), only the reproducibility property and the aggregate",
            "/// percentage, statistically, over enough rows.</summary>",
            $"public sealed class {routerClassName} : IRowRouter<{rowTypeName}>",
            "{",
            $"    private readonly Random _random = new({samplingSeed});",
            "",
            $"    public int SelectBranch({rowTypeName} row, in RowContext ctx)",
            "    {",
            $"        return _random.Next(100) < {samplingValue} ? 0 : 1; // 0 = sampled (\"Sampling Selected Output\"), 1 = not sampled (\"Sampling Unselected Output\")",
            "    }",
            "}",
        };

        return new EmitResult([new GeneratedFile($"Mapping/{routerClassName}.cs", Rendering.JoinLines(lines))], []);
    }
}
