using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Dtsx;

/// <summary>
/// Enumerates everything a package changes in the outside world -- see
/// <see cref="ObservableEffectSpec"/> for why this is deliberately pessimistic rather than
/// a description of only what the tooling understands.
///
/// <para><b>The catch-all is the point.</b> Tables and files are derived from analysis that
/// already existed. What is new here is the last pass: every executable whose type is not on
/// a short allowlist of "effects already accounted for elsewhere" is emitted as an
/// <c>UncharacterizedTask</c>. A Send Mail Task, an FTP Task, a File System Task, a Web
/// Service Task -- none of which this extractor models -- therefore each produce a declared,
/// explicitly-unverifiable effect instead of producing nothing at all. Adding a new task type
/// to SSIS's world cannot silently widen the blind spot, because unrecognised is the default.</para>
/// </summary>
public static class ObservableEffectsBuilder
{
    /// <summary>
    /// Task types whose observable effects are fully accounted for by other analysis, so they
    /// need no uncharacterized-effect entry of their own.
    ///
    /// <para><b>Script Task is deliberately NOT on this list.</b> Its declared surface (language,
    /// variables read/written) is modelled, but the script body can do anything at all --
    /// send mail, write a file, call a web service -- and no static analysis here reads it.
    /// Treating it as inert because its wrapper is modelled would reintroduce exactly the
    /// blind spot this class exists to close.</para>
    /// </summary>
    private static readonly HashSet<string> EffectsAccountedForElsewhere = new(StringComparer.OrdinalIgnoreCase)
    {
        // Its components' effects surface as tables/files via DataTouchBuilder.
        "Microsoft.Pipeline",
        // Its SQL's effects surface as tables/procedures via SqlAnalyzer.
        "Microsoft.ExecuteSQLTask",
        // Containers: no effect of their own; their children are walked independently.
        // SSIS's own stock-container naming, confirmed against real fixture XML rather than
        // guessed (SyntheticForEachScript.dtsx's ForEach Loop is DTS:ExecutableType=
        // "STOCK:FOREACHLOOP", not the "Microsoft.ForEachLoop" an earlier draft assumed).
        "STOCK:SEQUENCE",
        "STOCK:FOREACHLOOP",
        "STOCK:FORLOOP",
    };

    public static List<ObservableEffectSpec> Build(PackageSpec package, DataTouchSpec dataTouch)
    {
        var effects = new List<ObservableEffectSpec>();
        var name = package.ObjectName;

        foreach (var table in dataTouch.TablesWritten)
        {
            effects.Add(new ObservableEffectSpec
            {
                PackageName = name,
                Kind = "SqlTable",
                Target = table,
                Origin = "data-flow destination or SQL statement",
                Verifiability = "Verifiable",
                Note = "Row-level comparison against the golden corpus covers this effect.",
            });
        }

        foreach (var path in dataTouch.FilePathsWritten)
        {
            effects.Add(new ObservableEffectSpec
            {
                PackageName = name,
                Kind = "File",
                Target = path,
                Origin = "flat-file/file connection manager used by a destination component",
                Verifiability = "NeedsChecker",
                Note = "The gate-3 harness compares SQL rows only; no file-output checker exists yet.",
            });
        }

        // An expression-driven connection manager has no statically-knowable path, but its
        // DIRECTION is still knowable from the component using it -- so a dynamic destination
        // file is reported as a real effect (with its expression standing in for the path)
        // while a dynamic SOURCE file is correctly left out, being an input rather than an effect.
        var (_, writeConnections) = DataTouchBuilder.ResolveConnectionDirections(package);
        foreach (var entry in dataTouch.FilePathsFromExpression)
        {
            var cmName = entry.Split('=', 2)[0].Trim();
            if (!writeConnections.Contains(cmName)) continue;

            effects.Add(new ObservableEffectSpec
            {
                PackageName = name,
                Kind = "File",
                Target = entry,
                Origin = cmName,
                Verifiability = "NeedsChecker",
                Note = "Destination file whose path is computed at run time; no file-output checker exists yet, and the path itself needs environment parameter values to resolve.",
            });
        }

        foreach (var proc in dataTouch.ProceduresExecuted)
        {
            effects.Add(new ObservableEffectSpec
            {
                PackageName = name,
                Kind = "StoredProcedure",
                Target = proc,
                Origin = "Execute SQL Task",
                Verifiability = "NeedsHumanReview",
                Note = "What this procedure writes is not visible to static analysis -- its own targets may be entirely unlisted. Confirm which tables it touches and declare them, or the gate can pass while missing the procedure's real output.",
            });
        }

        foreach (var ex in PackageTree.AllExecutables(package))
        {
            if (!EffectsAccountedForElsewhere.Contains(ex.ExecutableType))
            {
                effects.Add(new ObservableEffectSpec
                {
                    PackageName = name,
                    Kind = "UncharacterizedTask",
                    Target = ex.ObjectName ?? ex.RefId,
                    Origin = ex.ExecutableType,
                    Verifiability = "NeedsHumanReview",
                    Note = $"'{ex.ExecutableType}' has no semantic model here, so whether it has a side effect -- and what that effect is -- is unknown. A person must classify it before this package can be called fully verified.",
                });
            }

            // A Script Component sits INSIDE a Microsoft.Pipeline (on the allowlist above,
            // since its ordinary components' effects are already covered by the tables/files
            // loops), so it would otherwise slip through entirely -- yet its script body can
            // do anything a Script Task's can (send mail, call a web service), and no static
            // analysis here reads it, same reasoning as Script Task's own exclusion above.
            if (ex.DataFlowTask is null) continue;
            foreach (var comp in ex.DataFlowTask.Pipeline.Components)
            {
                if (comp.ScriptComponent is null) continue;

                effects.Add(new ObservableEffectSpec
                {
                    PackageName = name,
                    Kind = "UncharacterizedTask",
                    Target = comp.Name,
                    Origin = $"{ex.ObjectName ?? ex.RefId}/{comp.Name} (Script Component)",
                    Verifiability = "NeedsHumanReview",
                    Note = "A Script Component's transform body can have any side effect at all; nothing here reads it. A person must classify it before this package can be called fully verified.",
                });
            }
        }

        return effects
            .OrderBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.Target, StringComparer.Ordinal)
            .ToList();
    }
}
