using Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using Microsoft.SqlServer.Dts.Runtime;

namespace Ssis.Extract.ObjectModel;

/// <summary>
/// Test oracle (plan §7.3): loads a package via <see cref="Application.LoadPackage"/> --
/// the real SSIS object model, never the production path -- and derives
/// <see cref="PackageOracleFacts"/> independently of <c>Ssis.Extract.Dtsx.DtsxPackageReader</c>'s
/// own raw-XML parse. This project exists purely so the two can be cross-checked; keeping
/// the Windows/GAC dependency (and its version-upgrade side effects, plan §2.1) confined
/// here is why it isn't referenced by anything under `src/Ssis.Extract.Cli` or the main
/// `SsisExtractor.slnx`.
/// </summary>
public static class PackageOracle
{
    public static PackageOracleFacts Load(string dtsxPath)
    {
        var app = new Application();
        var package = app.LoadPackage(dtsxPath, null);

        var executableNames = new List<string>();
        var precedenceConstraintCount = package.PrecedenceConstraints.Count;
        var variableCount = CountOwnDeclaredVariables(package.Variables, package);
        var pipelineComponentCount = 0;
        var pipelinePathCount = 0;

        Walk(package.Executables, executableNames, ref precedenceConstraintCount, ref variableCount, ref pipelineComponentCount, ref pipelinePathCount);

        var connectionManagerNames = new List<string>();
        foreach (ConnectionManager cm in package.Connections)
        {
            connectionManagerNames.Add(cm.CreationName);
        }
        connectionManagerNames.Sort(StringComparer.Ordinal);

        var parameterDataTypeNames = new List<string>();
        foreach (Parameter p in package.Parameters)
        {
            parameterDataTypeNames.Add(p.DataType.ToString());
        }
        parameterDataTypeNames.Sort(StringComparer.Ordinal);

        return new PackageOracleFacts
        {
            ExecutableCount = executableNames.Count,
            ExecutableNames = executableNames,
            ConnectionManagerCount = connectionManagerNames.Count,
            ConnectionManagerCreationNames = connectionManagerNames,
            VariableCount = variableCount,
            ParameterCount = parameterDataTypeNames.Count,
            ParameterDataTypeNames = parameterDataTypeNames,
            PipelineComponentCount = pipelineComponentCount,
            PipelinePathCount = pipelinePathCount,
            PrecedenceConstraintCount = precedenceConstraintCount,
        };
    }

    /// <summary>
    /// Recurses into every container generically via <see cref="IDTSSequence"/> (implemented
    /// by <c>ForEachLoop</c>/<c>Sequence</c>/<c>ForLoop</c>, none present in this PoC but real
    /// container types nonetheless). <c>Executable</c> itself is an empty abstract base
    /// (confirmed via reflection, not assumed) -- every concrete container/task type actually
    /// derives from <c>DtsContainer</c>, which is where <c>Name</c> and <c>Variables</c>
    /// genuinely live, so that's the one cast target used for both, rather than a separate
    /// <see cref="IDTSName"/> cast plus a <c>TaskHost</c>-only variable count.
    /// </summary>
    private static void Walk(Executables executables, List<string> names, ref int precedenceConstraintCount, ref int variableCount, ref int pipelineComponentCount, ref int pipelinePathCount)
    {
        foreach (Executable exec in executables)
        {
            if (exec is not DtsContainer container)
            {
                names.Add(exec.GetType().Name);
                continue;
            }

            names.Add(container.Name);
            variableCount += CountOwnDeclaredVariables(container.Variables, container);

            if (container is TaskHost { InnerObject: MainPipe pipeline })
            {
                pipelineComponentCount += pipeline.ComponentMetaDataCollection.Count;
                pipelinePathCount += pipeline.PathCollection.Count;
            }

            if (container is IDTSSequence seq)
            {
                precedenceConstraintCount += seq.PrecedenceConstraints.Count;
                Walk(seq.Executables, names, ref precedenceConstraintCount, ref variableCount, ref pipelineComponentCount, ref pipelinePathCount);
            }
        }
    }

    /// <summary>
    /// <c>DtsContainer.Variables</c> returns every variable *visible* at that scope -- every
    /// <c>System::</c> variable at every level, PLUS every ancestor's own declared variables
    /// inherited into view -- not "declared directly on this container." Confirmed
    /// empirically in four stages, not assumed:
    /// 1. An unfiltered count came back 100+ for a package whose own &lt;Variables&gt;
    ///    element declares exactly one -- <see cref="Variable.SystemVariable"/> filters the
    ///    System:: ones.
    /// 2. Filtering those out still over-counted 3x for that one variable, because it showed
    ///    up in every child container's own <c>.Variables</c> enumeration too, inherited from
    ///    the package -- <c>ReferenceEquals(v.Parent, container)</c> was tried and
    ///    under-counted to zero instead, because COM interop hands back a fresh RCW on each
    ///    <c>.Parent</c>/container access (same underlying object, never the same .NET
    ///    reference); comparing <c>.ID</c> (a stable GUID string on every <see cref="IDTSName"/>)
    ///    fixed that.
    /// 3. Still over-counted on a package using project/package PARAMETERS: the object model
    ///    exposes every parameter as a synthetic variable too, namespaced <c>$Package::</c>/
    ///    <c>$Project::</c> -- these correspond to <c>&lt;DTS:PackageParameters&gt;</c> in the
    ///    XML, a completely different element from &lt;DTS:Variables&gt;, so they must be
    ///    excluded here (they're covered by <see cref="PackageOracleFacts.ParameterCount"/>
    ///    instead). Only <c>Namespace == "User"</c> is a real declared variable.
    /// </summary>
    private static int CountOwnDeclaredVariables(Variables variables, DtsContainer container)
    {
        var count = 0;
        foreach (Variable v in variables)
        {
            if (!v.SystemVariable && v.Namespace == "User" && v.Parent.ID == container.ID) count++;
        }
        return count;
    }
}
