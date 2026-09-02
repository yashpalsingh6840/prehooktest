using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Dtsx;

/// <summary>
/// Reads a single <c>.dtsx</c> file into a <see cref="PackageSpec"/> by parsing the raw
/// XML directly (plan §2.1 -- deliberately not the SSIS object model: this must run on
/// any <c>PackageFormatVersion</c>, without the matching SSIS runtime/GAC installed, on
/// any OS). Slice 1 scope: identity/provenance/versioning/protection, execution
/// semantics, checkpoints, logging, package-level property expressions, package
/// parameters, connection managers (incl. full flat-file format), and variables. Slice 2
/// adds: the recursive control-flow tree, Execute SQL Task payloads, precedence DAGs,
/// event handlers, and the coverage percentage. Slice 3 adds: the full Data Flow Task
/// pipeline model (<see cref="PipelineReader"/>, every component generically plus bespoke
/// semantics for evidenced types) and derived column-level lineage
/// (<see cref="LineageBuilder"/>).
/// </summary>
public static partial class DtsxPackageReader
{
    private static readonly XNamespace Dts = "www.microsoft.com/SqlServer/Dts";
    private static readonly XNamespace SqlTask = "www.microsoft.com/sqlserver/dts/tasks/sqltask";

    public static PackageSpec Read(string path, bool noRedact)
    {
        var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new InvalidDataException($"{path}: no root element");

        var fileInfo = new FileInfo(path);
        var sha256 = ComputeSha256(path);

        var protectionLevelRaw = root.AttrInt(Dts + "ProtectionLevel") ?? 0;

        string? PackageProperty(string name) => root.Elements(Dts + "Property")
            .FirstOrDefault(p => p.Attr(Dts + "Name") == name)?.Value;
        var packageFormatVersion = int.TryParse(PackageProperty("PackageFormatVersion"), out var pfv) ? pfv : (int?)null;

        var unmapped = new List<UnmappedFragment>();
        var coverage = new CoverageAccumulator();

        var connectionManagers = (root.Element(Dts + "ConnectionManagers")?.Elements(Dts + "ConnectionManager") ?? [])
            .Select(cmEl => ReadConnectionManager(cmEl, noRedact))
            .ToList();
        var cmDtsIdToName = connectionManagers
            .Where(cm => cm.DtsId is not null)
            .ToDictionary(cm => cm.DtsId!, cm => cm.ObjectName);
        // The pipeline's own connection references use the refId path shape (e.g.
        // "Package.ConnectionManagers[CM_EmployeesCsv]"), not the DTSID-GUID shape Execute
        // SQL Task uses -- a second, separate lookup, not a normalization of the same one.
        var cmRefIdToName = connectionManagers
            .Where(cm => cm.RefId is not null)
            .ToDictionary(cm => cm.RefId!, cm => cm.ObjectName);

        var variables = (root.Element(Dts + "Variables")?.Elements(Dts + "Variable") ?? [])
            .Select(varEl => ReadVariable(varEl, owningRefId: "Package"))
            .ToList();

        var parameters = (root.Element(Dts + "PackageParameters")?.Elements(Dts + "PackageParameter") ?? [])
            .Select(ReadPackageParameter)
            .ToList();

        var propertyExpressions = root.Elements(Dts + "PropertyExpression")
            .Select(el => new PropertyExpressionSpec { PropertyName = el.Attr(Dts + "Name") ?? "", Expression = el.Value })
            .ToList();

        var (executables, precedenceConstraints, dag) = ReadContainerBody(root, cmDtsIdToName, cmRefIdToName, coverage);
        var eventHandlers = ReadEventHandlers(root, cmDtsIdToName, cmRefIdToName, coverage);

        AddUnmappedIfPresent(root, Dts + "Configurations", "Package/Configurations", "legacy Package-Deployment-Model configurations not yet modeled (plan §4.2)", unmapped, coverage, excluded: false);
        AddDesignTimePropertiesPlaceholder(root, unmapped, coverage);

        var executionSemantics = new ExecutionSemanticsSpec
        {
            MaxConcurrentExecutables = root.AttrInt(Dts + "MaxConcurrentExecutables"),
            MaximumErrorCount = root.AttrInt(Dts + "MaximumErrorCount"),
            FailPackageOnFailure = root.AttrBool(Dts + "FailPackageOnFailure"),
            DelayValidation = root.AttrBool(Dts + "DelayValidation"),
            TransactionOption = root.Attr(Dts + "TransactionOption"),
            IsolationLevel = root.Attr(Dts + "IsolationLevel"),
            ForceExecutionResult = root.Attr(Dts + "ForceExecutionResult"),
            Disable = root.AttrBool(Dts + "Disable"),
            DisableEventHandlers = root.AttrBool(Dts + "DisableEventHandlers"),
        };

        var checkpoints = new CheckpointSpec
        {
            CheckpointUsage = root.Attr(Dts + "CheckpointUsage"),
            CheckpointFileName = root.Attr(Dts + "CheckpointFileName"),
            SaveCheckpoints = root.AttrBool(Dts + "SaveCheckpoints"),
        };

        var logging = new LoggingSpec
        {
            LoggingMode = root.Attr(Dts + "LoggingMode"),
            Providers = ReadLogProviders(root),
        };

        var totalElements = root.DescendantsAndSelf().Count();
        var denominator = totalElements - coverage.Excluded;
        var coveragePercent = denominator <= 0
            ? 100.0
            : Math.Round(100.0 * (denominator - coverage.Unmapped) / denominator, 2);

        return new PackageSpec
        {
            ObjectName = root.Attr(Dts + "ObjectName") ?? Path.GetFileNameWithoutExtension(path),
            Description = root.Attr(Dts + "Description"),
            DtsId = root.Attr(Dts + "DTSID"),
            RefId = root.Attr(Dts + "refId") ?? "Package",
            SourceDtsxPath = path,
            Sha256 = sha256,
            FileSizeBytes = fileInfo.Length,
            LastWriteTimeUtc = fileInfo.LastWriteTimeUtc,
            CreationDate = root.Attr(Dts + "CreationDate"),
            CreatorName = root.Attr(Dts + "CreatorName"),
            CreatorComputerName = root.Attr(Dts + "CreatorComputerName"),
            PackageFormatVersion = packageFormatVersion,
            LastModifiedProductVersion = root.Attr(Dts + "LastModifiedProductVersion"),
            VersionBuild = root.Attr(Dts + "VersionBuild"),
            VersionGuid = root.Attr(Dts + "VersionGUID"),
            VersionComment = root.Attr(Dts + "VersionComment"),
            ProtectionLevelRaw = protectionLevelRaw,
            ProtectionLevelName = SsisTypeCodeMaps.ProtectionLevelName(protectionLevelRaw),
            PackageType = root.Attr(Dts + "PackageType"),
            LocaleId = root.Attr(Dts + "LocaleID"),
            ExecutionSemantics = executionSemantics,
            Checkpoints = checkpoints,
            Logging = logging,
            PropertyExpressions = propertyExpressions,
            Parameters = parameters,
            ConnectionManagers = connectionManagers,
            Variables = variables,
            Configurations = [],
            EnableConfigurations = root.AttrBoolOrFalse(Dts + "EnableConfigurations"),
            Executables = executables,
            PrecedenceConstraints = precedenceConstraints,
            Dag = dag,
            EventHandlers = eventHandlers,
            Coverage = new CoverageStats
            {
                TotalElements = totalElements,
                UnmappedElements = coverage.Unmapped,
                ExcludedElements = coverage.Excluded,
                CoveragePercent = coveragePercent,
            },
            Unmapped = unmapped,
        };
    }

    // ---- Control flow (plan §4.5-§4.6) ----

    internal sealed class CoverageAccumulator
    {
        public int Unmapped;
        public int Excluded;
    }

    /// <summary>
    /// Reads one container's direct children, its own precedence constraints, and the
    /// derived DAG over them. Used identically for the package root, any nested container
    /// (Sequence/ForEach/For Loop -- none in this PoC), and any event handler -- they all
    /// share the same "Executables + PrecedenceConstraints" shape.
    /// </summary>
    private static (List<ExecutableSpec> Children, List<PrecedenceConstraintSpec> Constraints, ControlFlowDagSpec Dag) ReadContainerBody(
        XElement containerEl, Dictionary<string, string> cmDtsIdToName, Dictionary<string, string> cmRefIdToName, CoverageAccumulator coverage)
    {
        var children = (containerEl.Element(Dts + "Executables")?.Elements(Dts + "Executable") ?? [])
            .Select(exEl => ReadExecutable(exEl, cmDtsIdToName, cmRefIdToName, coverage))
            .ToList();

        var constraints = (containerEl.Element(Dts + "PrecedenceConstraints")?.Elements(Dts + "PrecedenceConstraint") ?? [])
            .Select(ReadPrecedenceConstraint)
            .ToList();

        var dag = BuildDag(children.Select(c => c.RefId).ToList(), constraints);

        return (children, constraints, dag);
    }

    private static ExecutableSpec ReadExecutable(XElement exEl, Dictionary<string, string> cmDtsIdToName, Dictionary<string, string> cmRefIdToName, CoverageAccumulator coverage)
    {
        var executableType = exEl.Attr(Dts + "ExecutableType") ?? exEl.Attr(Dts + "CreationName") ?? "Unknown";
        var refId = exEl.Attr(Dts + "refId") ?? "";

        var propertyExpressions = exEl.Elements(Dts + "PropertyExpression")
            .Select(el => new PropertyExpressionSpec { PropertyName = el.Attr(Dts + "Name") ?? "", Expression = el.Value })
            .ToList();

        var variables = (exEl.Element(Dts + "Variables")?.Elements(Dts + "Variable") ?? [])
            .Select(v => ReadVariable(v, owningRefId: refId))
            .ToList();

        var (children, constraints, dag) = ReadContainerBody(exEl, cmDtsIdToName, cmRefIdToName, coverage);
        var eventHandlers = ReadEventHandlers(exEl, cmDtsIdToName, cmRefIdToName, coverage);

        ExecuteSqlTaskPayload? sqlPayload = null;
        DataFlowTaskPayload? dataFlowPayload = null;
        UnmappedTaskPayload? unmappedPayload = null;
        ScriptTaskPayload? scriptTaskPayload = null;
        FileSystemTaskPayload? fileSystemTaskPayload = null;
        ExecutePackageTaskPayload? executePackageTaskPayload = null;

        var objectData = exEl.Element(Dts + "ObjectData");
        if (objectData is not null)
        {
            if (executableType == "Microsoft.ExecuteSQLTask")
            {
                sqlPayload = ReadExecuteSqlTask(objectData, cmDtsIdToName);
            }
            else if (executableType == "Microsoft.FileSystemTask")
            {
                fileSystemTaskPayload = ReadFileSystemTask(objectData, cmDtsIdToName);
            }
            else if (executableType == "Microsoft.ExecutePackageTask")
            {
                executePackageTaskPayload = ReadExecutePackageTask(objectData, cmDtsIdToName, coverage);
            }
            else if (executableType == "Microsoft.Pipeline")
            {
                var pipelineEl = objectData.Element("pipeline") ?? objectData;
                var pipeline = PipelineReader.Read(pipelineEl, cmRefIdToName);
                var lineage = LineageBuilder.Build(pipeline);
                dataFlowPayload = new DataFlowTaskPayload { Pipeline = pipeline, Lineage = lineage };
                // Deliberately no coverage.Unmapped increment here (unlike the ExecuteSQLTask/
                // unmapped-task branches, which capture raw XML text) -- every element inside
                // <pipeline> is now structurally modeled by PipelineReader, generically, even
                // for a component type without bespoke semantics (see PipelineComponentSpec's
                // doc comment). Confirmed by enumerating every distinct element name under
                // <pipeline> in both PoC packages against what PipelineReader actually reads.
            }
            else if (executableType == "Microsoft.ScriptTask")
            {
                scriptTaskPayload = ReadScriptTask(objectData);
                // Not counted as unmapped: every child of <ScriptProject> is now read --
                // <ProjectItem>/<BinaryItem> included. Worth recording that this was NOT
                // always true: before ReadScriptTask read those two element types, they were
                // still silently swept into "covered" by this very comment (coverage.Unmapped
                // was never incremented for them either), the same silent-overstatement shape
                // as the pre-fix ForEachLoop/pipeline-output-property gaps documented
                // elsewhere in this file and in CLAUDE.md's synthetic-coverage-pass note --
                // just never caught until a real SSDT-authored Script Task (source text, not
                // just declared surface) was actually examined.
            }
            else
            {
                coverage.Unmapped += objectData.DescendantsAndSelf().Count();
                unmappedPayload = new UnmappedTaskPayload { RawObjectDataXml = objectData.ToString(SaveOptions.DisableFormatting) };
            }
        }

        // A ForEach Loop's enumerator config and variable mappings live in two sibling
        // elements alongside <Executables>, not inside <ObjectData> (it doesn't have one) --
        // see ForEachLoopPayload's own comment for why this was invisible to both the model
        // AND the coverage accounting before this branch existed.
        var forEachLoopPayload = executableType == "STOCK:FOREACHLOOP"
            ? ReadForEachLoop(exEl, coverage)
            : null;

        return new ExecutableSpec
        {
            RefId = refId,
            ObjectName = exEl.Attr(Dts + "ObjectName"),
            Description = exEl.Attr(Dts + "Description"),
            DtsId = exEl.Attr(Dts + "DTSID"),
            ExecutableType = executableType,
            Disabled = exEl.AttrBool(Dts + "Disabled"),
            DelayValidation = exEl.AttrBool(Dts + "DelayValidation"),
            FailParentOnFailure = exEl.AttrBool(Dts + "FailParentOnFailure"),
            FailPackageOnFailure = exEl.AttrBool(Dts + "FailPackageOnFailure"),
            MaximumErrorCount = exEl.AttrInt(Dts + "MaximumErrorCount"),
            TransactionOption = exEl.Attr(Dts + "TransactionOption"),
            IsolationLevel = exEl.Attr(Dts + "IsolationLevel"),
            ForcedExecutionValue = exEl.Attr(Dts + "ForcedExecutionValue"),
            ThreadHint = exEl.Attr(Dts + "ThreadHint"),
            PropertyExpressions = propertyExpressions,
            Variables = variables,
            Children = children,
            PrecedenceConstraints = constraints,
            Dag = dag,
            EventHandlers = eventHandlers,
            ExecuteSqlTask = sqlPayload,
            DataFlowTask = dataFlowPayload,
            UnmappedTask = unmappedPayload,
            ScriptTask = scriptTaskPayload,
            ForEachLoop = forEachLoopPayload,
            FileSystemTask = fileSystemTaskPayload,
            ExecutePackageTask = executePackageTaskPayload,
        };
    }

    /// <summary>
    /// <c>Microsoft.FileSystemTask</c>'s own <c>&lt;FileSystemData&gt;</c> -- no namespace
    /// prefix on the element or its attributes, same as <c>&lt;ScriptProject&gt;</c> (confirmed
    /// against a real object-model-built probe package, not assumed from the one real example
    /// this was first evidenced on). <see cref="FileSystemTaskPayload.OperationRaw"/> is
    /// genuinely absent from the XML for the schema's default (CopyFile) -- see that type's own
    /// doc comment.
    /// </summary>
    internal static FileSystemTaskPayload ReadFileSystemTask(XElement objectData, Dictionary<string, string> cmDtsIdToName)
    {
        var el = objectData.Element("FileSystemData");
        if (el is null) return new FileSystemTaskPayload();

        var sourceRaw = el.Attr("TaskSourcePath");
        var sourceIsVariable = el.AttrBool("TaskIsSourceVariable");
        var sourceConnectionName = sourceIsVariable != true && sourceRaw is not null && cmDtsIdToName.TryGetValue(sourceRaw, out var srcName)
            ? srcName : null;

        var destRaw = el.Attr("TaskDestinationPath");
        var destIsVariable = el.AttrBool("TaskIsDestinationVariable");
        var destConnectionName = destIsVariable != true && destRaw is not null && cmDtsIdToName.TryGetValue(destRaw, out var destName)
            ? destName : null;

        return new FileSystemTaskPayload
        {
            OperationRaw = el.Attr("TaskOperationType"),
            SourcePathRaw = sourceRaw,
            SourceIsVariable = sourceIsVariable,
            SourceConnectionName = sourceConnectionName,
            DestinationPathRaw = destRaw,
            DestinationIsVariable = destIsVariable,
            DestinationConnectionName = destConnectionName,
            OverwriteDestination = el.AttrBool("TaskOverwriteDestFile"),
        };
    }

    /// <summary>
    /// <c>Microsoft.ExecutePackageTask</c>'s own unqualified <c>&lt;ExecutePackageTask&gt;</c> --
    /// unlike <c>&lt;FileSystemData&gt;</c>/<c>&lt;ScriptProject&gt;</c>, its own children are
    /// ELEMENTS (<c>&lt;PackageName&gt;value&lt;/PackageName&gt;</c>), not attributes -- confirmed
    /// via a live object-model round trip, see <see cref="ExecutePackageTaskPayload"/>'s own doc
    /// comment. Any <c>&lt;ParameterAssignments&gt;</c> content (project-reference parameter
    /// bindings) is deliberately not modeled -- bumps <paramref name="coverage"/> instead of being
    /// silently swept into "covered", the same discipline the ForEach Loop enumerator/pipeline
    /// output-property fixes established for exactly this failure shape.
    /// </summary>
    internal static ExecutePackageTaskPayload ReadExecutePackageTask(XElement objectData, Dictionary<string, string> cmDtsIdToName, CoverageAccumulator coverage)
    {
        var el = objectData.Element("ExecutePackageTask");
        if (el is null) return new ExecutePackageTaskPayload();

        var paramAssignments = el.Element("ParameterAssignments");
        if (paramAssignments is not null)
        {
            coverage.Unmapped += paramAssignments.DescendantsAndSelf().Count();
        }

        var connectionRaw = el.Element("Connection")?.Value;
        var connectionName = connectionRaw is not null && cmDtsIdToName.TryGetValue(connectionRaw, out var name) ? name : null;

        return new ExecutePackageTaskPayload
        {
            ExecuteOutOfProcess = ParseElementBool(el.Element("ExecuteOutOfProcess")?.Value),
            UseProjectReference = ParseElementBool(el.Element("UseProjectReference")?.Value),
            PackageName = el.Element("PackageName")?.Value,
            PackageIdRaw = el.Element("PackageID")?.Value,
            VersionIdRaw = el.Element("VersionID")?.Value,
            ConnectionRefRaw = connectionRaw,
            ConnectionName = connectionName,
        };
    }

    /// <summary>Same "True"/"False" (and "1"/"0", for parity with <see cref="XmlHelpers.AttrBool"/>) tolerance as attribute-based bools, but for an element's own text VALUE -- <c>&lt;ExecutePackageTask&gt;</c>'s children are elements, not attributes.</summary>
    private static bool? ParseElementBool(string? raw)
    {
        if (raw is null) return null;
        if (raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1") return true;
        if (raw.Equals("false", StringComparison.OrdinalIgnoreCase) || raw == "0") return false;
        return null;
    }

    internal static ScriptTaskPayload ReadScriptTask(XElement objectData)
    {
        var el = objectData.Element("ScriptProject");
        if (el is null) return new ScriptTaskPayload();

        var projectItems = el.Elements("ProjectItem")
            .Select(pi => new ScriptProjectItemSpec
            {
                Name = pi.Attr("Name") ?? "",
                Encoding = pi.Attr("Encoding"),
                Content = pi.Value,
            })
            .ToList();

        var binaryItemNames = el.Elements("BinaryItem")
            .Select(bi => bi.Attr("Name") ?? "")
            .ToList();

        return new ScriptTaskPayload
        {
            Language = el.Attr("Language"),
            ProjectName = el.Attr("Name"),
            VstaMajorVersion = el.Attr("VSTAMajorVersion"),
            VstaMinorVersion = el.Attr("VSTAMinorVersion"),
            ReadOnlyVariables = SplitVariableList(el.Attr("ReadOnlyVariables")),
            ReadWriteVariables = SplitVariableList(el.Attr("ReadWriteVariables")),
            ProjectItems = projectItems,
            BinaryItemNames = binaryItemNames,
            SourceStripped = projectItems.Count == 0 && binaryItemNames.Count > 0,
        };
    }

    private static List<string> SplitVariableList(string? raw) =>
        string.IsNullOrEmpty(raw) ? [] : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static ForEachLoopPayload ReadForEachLoop(XElement exEl, CoverageAccumulator coverage)
    {
        var enumeratorEl = exEl.Element(Dts + "ForEachEnumerator");
        var enumeratorCreationName = enumeratorEl?.Attr(Dts + "CreationName");
        var enumeratorObjectData = enumeratorEl?.Element(Dts + "ObjectData");

        ForEachFileEnumeratorSpec? fileEnumerator = null;
        string? rawEnumeratorObjectDataXml = null;

        if (enumeratorCreationName == "Microsoft.ForEachFileEnumerator")
        {
            var props = enumeratorObjectData?.Element("ForEachFileEnumeratorProperties");
            string? Fefe(string name) => props?.Elements("FEFEProperty").FirstOrDefault(p => p.Attribute(name) is not null)?.Attr(name);
            fileEnumerator = new ForEachFileEnumeratorSpec
            {
                Folder = Fefe("Folder"),
                FileSpec = Fefe("FileSpec"),
                FileNameRetrievalTypeRaw = int.TryParse(Fefe("FileNameRetrievalType"), out var frt) ? frt : null,
                Recurse = Fefe("Recurse") switch { "1" => true, "0" => false, _ => null },
            };
        }
        else if (enumeratorObjectData is not null)
        {
            // Not modeled -- an enumerator type this build slice has no real evidence for
            // (Database/Item/Event/ADO/etc.). Captured verbatim, same honesty pattern as
            // UnmappedTaskPayload, and counted as unmapped since it genuinely isn't.
            coverage.Unmapped += enumeratorObjectData.DescendantsAndSelf().Count();
            rawEnumeratorObjectDataXml = enumeratorObjectData.ToString(SaveOptions.DisableFormatting);
        }

        var variableMappings = (exEl.Element(Dts + "ForEachVariableMappings")?.Elements(Dts + "ForEachVariableMapping") ?? [])
            .Select(m => new ForEachVariableMappingSpec
            {
                VariableName = m.Attr(Dts + "VariableName") ?? "",
                ValueIndex = m.AttrInt(Dts + "ValueIndex"),
            })
            .ToList();

        return new ForEachLoopPayload
        {
            EnumeratorCreationName = enumeratorCreationName,
            FileEnumerator = fileEnumerator,
            RawEnumeratorObjectDataXml = rawEnumeratorObjectDataXml,
            VariableMappings = variableMappings,
        };
    }

    private static List<EventHandlerSpec> ReadEventHandlers(XElement containerEl, Dictionary<string, string> cmDtsIdToName, Dictionary<string, string> cmRefIdToName, CoverageAccumulator coverage)
    {
        var container = containerEl.Element(Dts + "EventHandlers");
        if (container is null) return [];

        return container.Elements(Dts + "EventHandler").Select(ehEl =>
        {
            var refId = ehEl.Attr(Dts + "refId") ?? "";
            var (children, constraints, dag) = ReadContainerBody(ehEl, cmDtsIdToName, cmRefIdToName, coverage);
            var variables = (ehEl.Element(Dts + "Variables")?.Elements(Dts + "Variable") ?? [])
                .Select(v => ReadVariable(v, owningRefId: refId))
                .ToList();

            return new EventHandlerSpec
            {
                EventName = ehEl.Attr(Dts + "ObjectName") ?? ehEl.Attr(Dts + "EventName") ?? "",
                RefId = refId,
                ObjectName = ehEl.Attr(Dts + "ObjectName"),
                DtsId = ehEl.Attr(Dts + "DTSID"),
                Description = ehEl.Attr(Dts + "Description"),
                Disabled = ehEl.AttrBool(Dts + "Disabled"),
                Variables = variables,
                Children = children,
                PrecedenceConstraints = constraints,
                Dag = dag,
            };
        }).ToList();
    }

    private static PrecedenceConstraintSpec ReadPrecedenceConstraint(XElement el) => new()
    {
        RefId = el.Attr(Dts + "refId"),
        DtsId = el.Attr(Dts + "DTSID"),
        ObjectName = el.Attr(Dts + "ObjectName"),
        From = el.Attr(Dts + "From") ?? "",
        To = el.Attr(Dts + "To") ?? "",
        Value = el.Attr(Dts + "Value"),
        EvalOp = el.Attr(Dts + "EvalOp"),
        Expression = el.Attr(Dts + "Expression"),
        LogicalAnd = el.AttrBool(Dts + "LogicalAnd"),
    };

    /// <summary>
    /// Kahn's algorithm for topological order, plus a longest-path "level" for each node
    /// (see <see cref="ControlFlowDagSpec.ParallelLevels"/> for what that grouping does and
    /// doesn't guarantee). Constraints referencing a refId outside <paramref name="nodeRefIds"/>
    /// are silently excluded from the graph rather than treated as an error -- shouldn't
    /// happen for a well-formed .dtsx, but a hand-edited or corrupted file shouldn't crash
    /// the whole extraction over one dangling reference.
    /// </summary>
    internal static ControlFlowDagSpec BuildDag(List<string> nodeRefIds, List<PrecedenceConstraintSpec> constraints)
    {
        if (nodeRefIds.Count == 0)
        {
            return new ControlFlowDagSpec();
        }

        var nodes = new HashSet<string>(nodeRefIds);
        var adjacency = nodeRefIds.ToDictionary(n => n, _ => new List<string>());
        var inDegree = nodeRefIds.ToDictionary(n => n, _ => 0);

        foreach (var c in constraints)
        {
            if (!nodes.Contains(c.From) || !nodes.Contains(c.To))
            {
                continue;
            }
            adjacency[c.From].Add(c.To);
            inDegree[c.To]++;
        }

        var remainingInDegree = new Dictionary<string, int>(inDegree);
        var levels = new Dictionary<string, int>();
        var queue = new Queue<string>();
        foreach (var n in nodeRefIds)
        {
            if (inDegree[n] == 0)
            {
                levels[n] = 0;
                queue.Enqueue(n);
            }
        }

        var topoOrder = new List<string>();
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            topoOrder.Add(n);
            foreach (var next in adjacency[n])
            {
                levels[next] = Math.Max(levels.GetValueOrDefault(next, 0), levels[n] + 1);
                if (--remainingInDegree[next] == 0)
                {
                    queue.Enqueue(next);
                }
            }
        }

        var hasCycle = topoOrder.Count != nodeRefIds.Count;
        if (hasCycle)
        {
            return new ControlFlowDagSpec { HasCycle = true };
        }

        var parallelLevels = levels
            .GroupBy(kv => kv.Value)
            .OrderBy(g => g.Key)
            .Select(g => g.Select(kv => kv.Key).ToList())
            .ToList();

        return new ControlFlowDagSpec
        {
            TopologicalOrder = topoOrder,
            ParallelLevels = parallelLevels,
            HasCycle = false,
        };
    }

    private static ExecuteSqlTaskPayload ReadExecuteSqlTask(XElement objectData, Dictionary<string, string> cmDtsIdToName)
    {
        var el = objectData.Element(SqlTask + "SqlTaskData");
        if (el is null)
        {
            // Shouldn't happen for a well-formed Microsoft.ExecuteSQLTask, but an empty
            // payload beats throwing away the whole extraction over one odd task.
            return new ExecuteSqlTaskPayload();
        }

        var connectionRaw = el.Attr(SqlTask + "Connection");
        string? connectionName = connectionRaw is not null && cmDtsIdToName.TryGetValue(connectionRaw, out var name) ? name : null;

        var parameterBindings = el.Elements(SqlTask + "ParameterBinding").Select(p => new SqlParameterBindingSpec
        {
            DtsVariableName = p.Attr(SqlTask + "DtsVariableName"),
            ParameterName = p.Attr(SqlTask + "ParameterName"),
            Direction = p.Attr(SqlTask + "ParameterDirection"),
            DataTypeRaw = p.AttrInt(SqlTask + "DataType"),
            ParameterSize = p.AttrInt(SqlTask + "ParameterSize"),
        }).ToList();

        var resultBindings = el.Elements(SqlTask + "ResultBinding").Select(r => new SqlResultBindingSpec
        {
            ResultName = r.Attr(SqlTask + "ResultName"),
            DtsVariableName = r.Attr(SqlTask + "DtsVariableName"),
        }).ToList();

        return new ExecuteSqlTaskPayload
        {
            SqlStatementSource = el.Attr(SqlTask + "SqlStatementSource"),
            SqlStatementSourceType = el.Attr(SqlTask + "SqlStatementSourceType"),
            ConnectionRefRaw = connectionRaw,
            ConnectionName = connectionName,
            IsStoredProc = el.AttrBool(SqlTask + "IsStoredProc"),
            ResultSetType = el.Attr(SqlTask + "ResultType") ?? el.Attr(SqlTask + "ResultSetType"),
            TimeOut = el.AttrInt(SqlTask + "TimeOut"),
            CodePage = el.AttrInt(SqlTask + "CodePage"),
            BypassPrepare = el.AttrBool(SqlTask + "BypassPrepare"),
            ParameterBindings = parameterBindings,
            ResultBindings = resultBindings,
        };
    }

    // ---- Connection managers / flat-file format (slice 1) ----

    // internal (not private), same reasoning as ReadFileSystemTask/ReadScriptTask above: a
    // direct unit test via InternalsVisibleTo, for a shape (DPAPI ciphertext) no synthetic
    // fixture builder can easily produce.
    internal static ConnectionManagerSpec ReadConnectionManager(XElement cmEl, bool noRedact)
    {
        var creationName = cmEl.Attr(Dts + "CreationName") ?? "";
        var objectData = cmEl.Element(Dts + "ObjectData")?.Element(Dts + "ConnectionManager");
        var rawConnString = objectData?.Attr(Dts + "ConnectionString");

        var hadPassword = rawConnString is not null && PasswordPattern().IsMatch(rawConnString);
        var redactedConnString = rawConnString is not null ? RedactConnectionString(rawConnString) : null;

        var isFlatFileLike = creationName is "FLATFILE" or "MULTIFLATFILE";
        var isExcel = creationName == "EXCEL";
        var parsed = rawConnString is not null ? ParseConnectionString(rawConnString, isFlatFileLike, isExcel) : null;

        var propertyExpressions = cmEl.Elements(Dts + "PropertyExpression")
            .Select(el => new PropertyExpressionSpec { PropertyName = el.Attr(Dts + "Name") ?? "", Expression = el.Value })
            .ToList();

        // A ProtectionLevel of EncryptSensitiveWithUserKey (or EncryptSensitiveWithPassword)
        // DPAPI-encrypts a sensitive property IN PLACE rather than stripping it at deploy time
        // -- e.g. <DTS:Password DTS:Name="Password" Sensitive="1" Encrypted="1">ciphertext...
        // </DTS:Password> as a child of the inner <DTS:ConnectionManager> element. Confirmed
        // real against SSIS_From_Sandeep/RBC_Demo_ETL/RBC_Demo_ETL/Package_Advanced.dtsx.
        // Searched generically (not just <DTS:Password>) since any sensitive child property
        // could in principle use the same shape -- only the ciphertext itself is never read,
        // matching the redaction discipline already applied to a plaintext Password= segment.
        var encryptedProperties = objectData?
            .Descendants()
            .Where(el => el.AttrBool("Encrypted") == true)
            .Select(el => el.Attr(Dts + "Name") ?? el.Name.LocalName)
            .ToList() ?? [];

        return new ConnectionManagerSpec
        {
            ObjectName = cmEl.Attr(Dts + "ObjectName") ?? "",
            RefId = cmEl.Attr(Dts + "refId"),
            DtsId = cmEl.Attr(Dts + "DTSID"),
            Description = cmEl.Attr(Dts + "Description"),
            CreationName = creationName,
            Scope = "Package", // every connection manager inside a .dtsx is package-scoped by definition; project-scoped ones live in .conmgr files referenced from .dtproj (none in this PoC)
            ConnectionString = redactedConnString,
            UnredactedConnectionString = noRedact ? rawConnString : null,
            WasRedacted = hadPassword,
            Parsed = parsed,
            DelayValidation = cmEl.AttrBool(Dts + "DelayValidation"),
            RetainSameConnection = cmEl.AttrBool(Dts + "RetainSameConnection"),
            PropertyExpressions = propertyExpressions,
            FlatFileFormat = isFlatFileLike && objectData is not null ? ReadFlatFileFormat(objectData) : null,
            EncryptedProperties = encryptedProperties,
        };
    }

    private static FlatFileFormatSpec ReadFlatFileFormat(XElement objectData)
    {
        var headerRaw = objectData.Attr(Dts + "HeaderRowDelimiter");
        var headerDecoded = XmlHexEscape.Decode(headerRaw);
        var rowRaw = objectData.Attr(Dts + "RowDelimiter");
        var rowDecoded = XmlHexEscape.Decode(rowRaw);
        var qualifierRaw = objectData.Attr(Dts + "TextQualifier");
        var qualifierDecoded = XmlHexEscape.Decode(qualifierRaw);

        var columns = (objectData.Element(Dts + "FlatFileColumns")?.Elements(Dts + "FlatFileColumn") ?? [])
            .Select(colEl =>
            {
                var colDelimRaw = colEl.Attr(Dts + "ColumnDelimiter");
                var colDelimDecoded = XmlHexEscape.Decode(colDelimRaw);
                var dataTypeRaw = colEl.AttrInt(Dts + "DataType") ?? 0;
                return new FlatFileColumnSpec
                {
                    ObjectName = colEl.Attr(Dts + "ObjectName") ?? "",
                    DtsId = colEl.Attr(Dts + "DTSID"),
                    ColumnType = colEl.Attr(Dts + "ColumnType"),
                    ColumnDelimiterRaw = colDelimRaw,
                    ColumnDelimiterDecoded = colDelimRaw is null ? null : colDelimDecoded,
                    ColumnDelimiterDisplay = colDelimRaw is null ? null : XmlHexEscape.ToDisplayForm(colDelimDecoded),
                    DataTypeRaw = dataTypeRaw,
                    DataTypeName = SsisTypeCodeMaps.PipelineDataTypeName(dataTypeRaw),
                    MaximumWidth = colEl.AttrInt(Dts + "MaximumWidth"),
                    DataPrecision = colEl.AttrInt(Dts + "DataPrecision"),
                    DataScale = colEl.AttrInt(Dts + "DataScale"),
                    TextQualified = colEl.AttrBool(Dts + "TextQualified"),
                };
            })
            .ToList();

        return new FlatFileFormatSpec
        {
            Format = objectData.Attr(Dts + "Format"),
            LocaleId = objectData.Attr(Dts + "LocaleID"),
            CodePage = objectData.AttrInt(Dts + "CodePage"),
            Unicode = objectData.AttrBool(Dts + "Unicode"),
            HeaderRowsToSkip = objectData.AttrInt(Dts + "HeaderRowsToSkip"),
            HeaderRowDelimiterRaw = headerRaw,
            HeaderRowDelimiterDecoded = headerRaw is null ? null : headerDecoded,
            HeaderRowDelimiterDisplay = headerRaw is null ? null : XmlHexEscape.ToDisplayForm(headerDecoded),
            RowDelimiterRaw = rowRaw,
            RowDelimiterDecoded = rowRaw is null ? null : rowDecoded,
            RowDelimiterDisplay = rowRaw is null ? null : XmlHexEscape.ToDisplayForm(rowDecoded),
            ColumnNamesInFirstDataRow = objectData.AttrBool(Dts + "ColumnNamesInFirstDataRow"),
            TextQualifierRaw = qualifierRaw,
            TextQualifierDecoded = qualifierRaw is null ? null : qualifierDecoded,
            Columns = columns,
        };
    }

    private static VariableSpec ReadVariable(XElement varEl, string owningRefId)
    {
        var valueEl = varEl.Element(Dts + "VariableValue");
        var declaredRaw = valueEl?.AttrInt(Dts + "DataType");
        return new VariableSpec
        {
            Namespace = varEl.Attr(Dts + "Namespace") ?? "",
            ObjectName = varEl.Attr(Dts + "ObjectName") ?? "",
            DtsId = varEl.Attr(Dts + "DTSID"),
            CreationName = NullIfEmpty(varEl.Attr(Dts + "CreationName")),
            DeclaredDataTypeRaw = declaredRaw,
            DeclaredDataTypeName = declaredRaw.HasValue ? SsisTypeCodeMaps.VariantDeclaredTypeName(declaredRaw.Value) : null,
            Value = valueEl?.Value,
            EvaluateAsExpression = varEl.AttrBoolOrFalse(Dts + "EvaluateAsExpression"),
            Expression = varEl.Attr(Dts + "Expression"),
            ReadOnly = varEl.AttrBoolOrFalse(Dts + "ReadOnly"),
            IncludeInDebugDump = varEl.AttrInt(Dts + "IncludeInDebugDump"),
            OwningContainerRefId = owningRefId,
        };
    }

    private static SsisParameter ReadPackageParameter(XElement paramEl)
    {
        var valueProp = paramEl.Elements(Dts + "Property").FirstOrDefault(p => p.Attr(Dts + "Name") == "ParameterValue");
        var dataTypeRaw = paramEl.AttrInt(Dts + "DataType") ?? 0;
        return new SsisParameter
        {
            Name = paramEl.Attr(Dts + "ObjectName") ?? "",
            Id = paramEl.Attr(Dts + "DTSID"),
            CreationName = NullIfEmpty(paramEl.Attr(Dts + "CreationName")),
            Description = paramEl.Attr(Dts + "Description"),
            IncludeInDebugDump = paramEl.AttrInt(Dts + "IncludeInDebugDump"),
            Required = paramEl.AttrBoolOrFalse(Dts + "Required"),
            Sensitive = paramEl.AttrBoolOrFalse(Dts + "Sensitive"),
            Value = valueProp?.Value,
            DataTypeRaw = dataTypeRaw,
            DataTypeName = SsisTypeCodeMaps.VariantDeclaredTypeName(dataTypeRaw),
            Scope = "Package",
        };
    }

    private static List<LogProviderSpec> ReadLogProviders(XElement root)
    {
        var container = root.Element(Dts + "LogProviders");
        if (container is null) return [];
        return container.Elements(Dts + "LogProvider").Select(el => new LogProviderSpec
        {
            DtsId = el.Attr(Dts + "DTSID"),
            ObjectName = el.Attr(Dts + "ObjectName"),
            CreationName = el.Attr(Dts + "CreationName"),
            ConfigString = el.Attr(Dts + "ConfigString") ?? el.Element(Dts + "ObjectData")?.Value,
        }).ToList();
    }

    private static void AddUnmappedIfPresent(XElement parent, XName childName, string location, string reason, List<UnmappedFragment> sink, CoverageAccumulator coverage, bool excluded)
    {
        var el = parent.Element(childName);
        if (el is not null && el.HasElements)
        {
            sink.Add(new UnmappedFragment { Location = location, Reason = reason, RawXml = el.ToString(SaveOptions.DisableFormatting) });
            var count = el.DescendantsAndSelf().Count();
            if (excluded) coverage.Excluded += count; else coverage.Unmapped += count;
        }
    }

    private static void AddDesignTimePropertiesPlaceholder(XElement root, List<UnmappedFragment> sink, CoverageAccumulator coverage)
    {
        var el = root.Element(Dts + "DesignTimeProperties");
        if (el is null) return;
        coverage.Excluded += el.DescendantsAndSelf().Count();
        var raw = el.ToString(SaveOptions.DisableFormatting);
        sink.Add(new UnmappedFragment
        {
            Location = "Package/DesignTimeProperties",
            Reason = "GUI layout only (diagram X/Y, sizes) -- the file's own embedded comment documents this has no runtime effect. Elided to a length note so designer rearrangement doesn't masquerade as a package change in git diff. Excluded from the coverage percentage (not just deferred) since it will never be modeled.",
            RawXml = $"<!-- elided, {raw.Length} chars -->",
        });
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    [GeneratedRegex(@"(Password|Pwd)\s*=\s*[^;]*", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordPattern();

    private static string RedactConnectionString(string raw) => PasswordPattern().Replace(raw, "$1=***");

    /// <summary><paramref name="isExcel"/>: an EXCEL connection manager's own "Data Source" key
    /// is always the workbook file path (confirmed real from RBC_Demo_ETL's own CM_EXCEL_Drip:
    /// "Provider=Microsoft.ACE.OLEDB.12.0;Data Source=...\DripEligibility.xlsx;Extended
    /// Properties=..."), unlike an ordinary OLEDB connection string where "Data Source" names a
    /// SQL server host -- so this is parsed via the same key=value path as any OLEDB connection
    /// string (Extended Properties/Provider are real, if currently unused, signals worth keeping),
    /// with <see cref="ParsedConnectionString.FilePath"/> additionally populated from the same
    /// "Data Source" value codegen's file-source machinery (<c>BuildFileSourceEntry</c>) already
    /// expects, so no separate Excel-specific path-resolution code was needed there.</summary>
    private static ParsedConnectionString ParseConnectionString(string raw, bool isFlatFileLike, bool isExcel = false)
    {
        if (isFlatFileLike || !raw.Contains('='))
        {
            // FLATFILE/FILE connection managers store a bare path, not key=value pairs.
            return new ParsedConnectionString { FilePath = raw };
        }

        var extras = new Dictionary<string, string>();
        string? server = null, database = null, provider = null, userId = null, authMode = null;

        foreach (var segment in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = segment.IndexOf('=');
            if (eq < 0) continue;
            var key = segment[..eq].Trim();
            var value = segment[(eq + 1)..].Trim();

            switch (key.ToLowerInvariant())
            {
                case "data source": server = value; break;
                case "initial catalog": database = value; break;
                case "provider": provider = value; break;
                case "user id": userId = value; authMode = "SqlLogin"; break;
                case "integrated security": authMode = "Integrated"; break;
                default: extras[key] = value; break;
            }
        }

        return new ParsedConnectionString
        {
            Server = server,
            Database = database,
            Provider = provider,
            AuthMode = authMode,
            UserId = userId,
            Extras = extras,
            FilePath = isExcel ? server : null,
        };
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
