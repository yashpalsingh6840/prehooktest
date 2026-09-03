using System.Text;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Pipeline;
using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Codegen;

/// <summary>What <see cref="AiPacketEmitter.Emit"/> produced for one package: the work packets
/// (one per Tier 1/2 gap) and the <see cref="GapSpec"/> index row for EVERY gap, packet or not.</summary>
public sealed record AiPacketResult(List<GeneratedFile> Packets, List<GapSpec> Gaps);

/// <summary>
/// Turns a Tier 1/2 <see cref="GenerationGap"/> into a self-contained markdown work packet: the
/// exact contract to satisfy, the real evidence already extracted from the .dtsx, and the SSIS
/// semantics a correct answer has to preserve.
///
/// <b>Why a file rather than an API call.</b> The transport is deliberately a human pasting this
/// into a chat window: a client site running this tool has no guarantee of outbound network
/// access or an API key, and a human in the loop is the point rather than an inconvenience -- an
/// AI-authored fill is reviewed work product, not a silent guess, which is the whole property
/// this tool exists to protect. Because the contract is "packet in, fill out", swapping the
/// transport later (an API call, a subagent) changes nothing about the format.
///
/// <b>What this deliberately does NOT do.</b> Tier 3 gaps (<see cref="GapTier.MissingToolSupport"/>)
/// get no packet at all. Patching those per-package would hide ONE systemic emitter gap behind N
/// one-off hand-written patches -- the opposite of what the portfolio readiness report exists to
/// surface. They belong in the emitter, with a fixture and a test.
/// </summary>
public static class AiPacketEmitter
{
    public static AiPacketResult Emit(PackageSpec package, IReadOnlyList<GenerationGap> gaps)
    {
        var packets = new List<GeneratedFile>();
        var specs = new List<GapSpec>();

        foreach (var (gapId, gap) in GapIdentity.AssignIds(package.ObjectName, gaps))
        {
            var tier = GapIdentity.TierOf(gap);
            if (!GapIdentity.HasWorkPacket(gap))
            {
                specs.Add(NewSpec(gapId, package, gap, tier, null, null));
                continue;
            }

            var evidence = BuildEvidence(package, gap);
            var fileName = GapIdentity.ToFileName(gapId) + ".md";
            packets.Add(new GeneratedFile(fileName, BuildPacket(package, gapId, gap, tier, evidence)));
            specs.Add(NewSpec(gapId, package, gap, tier, fileName, GapIdentity.Sha256(evidence)));
        }

        return new AiPacketResult(packets, specs);
    }

    private static GapSpec NewSpec(string gapId, PackageSpec package, GenerationGap gap, GapTier tier, string? packetPath, string? evidenceHash) =>
        new()
        {
            GapId = gapId,
            Package = package.ObjectName,
            Kind = gap.Kind,
            Tier = tier,
            Location = gap.Location,
            Reason = gap.Reason,
            IsBlocking = gap.IsBlocking,
            PacketPath = packetPath,
            EvidenceSha256 = evidenceHash,
            EvidenceRefId = gap.EvidenceRefId,
        };

    private static string BuildPacket(PackageSpec package, string gapId, GenerationGap gap, GapTier tier, string evidence)
    {
        var sb = new StringBuilder();
        sb.Append($"# Work packet: `{gapId}`\n\n");
        sb.Append($"- **Package:** `{package.ObjectName}`\n");
        sb.Append($"- **Location:** `{gap.Location}`\n");
        sb.Append($"- **Tier:** {TierDescription(tier)}\n");
        sb.Append($"- **Evidence SHA-256:** `{GapIdentity.Sha256(evidence)}`\n\n");
        sb.Append("## Why `ssisx generate` stopped here\n\n");
        sb.Append(gap.Reason);
        sb.Append("\n\n## What you need to produce\n\n");
        sb.Append(Contract(gap));
        sb.Append("\n## Evidence from the package\n\n");
        sb.Append(evidence);
        sb.Append('\n');

        // Only a Tier-2 answer is CODE, so only Tier 2 needs the semantics appendix. A Tier-1
        // answer is a single fact; handing its reviewer a page of expression-language rules would
        // be noise.
        if (tier == GapTier.MissingLogic) sb.Append(SsisSemanticsAppendix);

        sb.Append(ResponseFormat(gap));
        return sb.ToString();
    }

    private static string TierDescription(GapTier tier) => tier switch
    {
        GapTier.MissingDatum => "1 -- missing datum. Supply one FACT; the generator writes all the code. Do not write code for this.",
        GapTier.MissingLogic => "2 -- missing logic. The real source IS below; port it. Reviewed by a human, then verified by gate 3.",
        _ => tier.ToString(),
    };

    private static string Contract(GenerationGap gap) => gap.Kind switch
    {
        GapKind.LookupJoinKey => LookupContract,
        GapKind.ScriptTask => ScriptTaskContract,
        GapKind.ScriptComponentColumn => ScriptComponentContract,
        GapKind.EncryptedConnectionManagerSecret => EncryptedSecretContract,
        _ => "_(no contract defined for this gap kind)_\n",
    };

    private const string LookupContract = """
        The join key this Lookup uses. SSIS normally records it on the joining input column (a
        `JoinToReferenceColumn` property) and this tool reads it automatically -- you are seeing this
        packet because THIS Lookup carries no such property, which happens when a package was built
        programmatically rather than in SSDT. So it has to be supplied, and it will not be guessed: a
        wrong key compiles, runs, and silently produces wrong joined data on every row. Identify:

        1. the INPUT column the Lookup joins on (see the Lookup's own input columns below -- SSIS only
           keeps a column on a Lookup's input if the component actually references it, so this list is
           normally very short), and
        2. the column in the REFERENCE query's own result set it matches against.

        State a confidence and the reasoning. **A human confirms this before it is used** -- you are
        proposing, not deciding.

        """;

    private const string ScriptTaskContract = """
        A **second `partial` part** of the class `ssisx generate --seams` already emitted for this
        task, implementing its one unimplemented seam. Do NOT write the `ILoadTask` implementation,
        the `Name` property, or the `StepResult` -- all of that is generated already. Write only this:

        ```csharp
        using Etl.Core.Abstractions;

        namespace <Package>.ScriptTasks;

        // ssisx-fill: GapId=<this gap's id> Author=<you> Date=<yyyy-mm-dd> EvidenceSha256=<from the heading above>
        public sealed partial class <TaskName>ScriptTask
        {
            // Fields are allowed here -- this is a class part, not a bare method body. A compiled
            // Regex, a constant, a helper method: all fine.

            private partial async Task RunScriptAsync(ScriptTaskContext ctx, CancellationToken ct)
            {
                // ported logic here
            }
        }
        ```

        The exact namespace and class name are in the gap's own reason text; use them verbatim or the
        part will not bind. `async` is optional -- return `Task.CompletedTask` if nothing awaits.
        Add your own `using` lines: the generated part's usings do not carry over to yours, so
        resolving anything from `ctx.Services` needs `using Microsoft.Extensions.DependencyInjection;`.

        What the original script's `Dts.*` calls map to, all reachable from `ctx`:

        | SSIS | here |
        |---|---|
        | `Dts.Connections[...].AcquireConnection` for SQL | `ctx.Uow` -- the package's one shared connection/transaction (`ExecuteSqlAsync`) |
        | `Dts.Variables["System::PackageName"]` | `ctx.Load.PackageName` (also `RunId`, `StartedAtUtc`) |
        | `Dts.Variables["User::Whatever"]` | `ctx.Variables.Get<T>(name)` / `.Set(name, value)`, shared with every other ported Script Task in this package |
        | `Dts.Connections[...].ConnectionString`, or any other configured value | resolve it from `ctx.Services` (e.g. `IOptions<FileSourceOptions>`) |
        | `Dts.Events.FireError` + `Dts.TaskResult = Failure` | throw -- the run's transaction rolls back |
        | `Dts.Events.FireInformation` | an `ILogger` resolved from `ctx.Services` |

        Note the transaction difference and do not try to work around it: this task's SQL joins the
        package's single all-or-nothing transaction, whereas SSIS auto-committed per task.

        **If the task's real logic cannot be reproduced against these abstractions**, say so
        explicitly instead of inventing a substitute. That answer is more useful than a plausible one.

        """;

    private const string ScriptComponentContract = """
        The body of ONE method computing this column's value for a single row. It will be spliced into
        the generated transform as a `partial` method, so it must be a pure function of the row:

        ```csharp
        // ssisx-fill: GapId=<this gap's id> Author=<you> Date=<yyyy-mm-dd> EvidenceSha256=<from the heading above>
        private partial <ClrType> Fill_<ColumnName>(<RowType> row, in RowContext ctx);
        ```

        `<ClrType>` is this column's own C# type from the "Columns this component produces" table
        below; `<RowType>` is the generated row type, whose properties are the "Input columns
        available on `row`" table. Getting the return type right matters -- the rest of the
        signature is filled in for you when the fill is applied.

        The Script Component's full source is below -- port only the part that produces THIS column.
        Other columns from the same component are separate work packets; do not fold them together.
        `ctx` carries the run identity (`ctx.LoadedAtUtc` is the run's own UTC timestamp, the correct
        stand-in for a script that stamped `DateTime.Now`).

        """;

    private static string ResponseFormat(GenerationGap gap) => gap.Kind switch
    {
        GapKind.LookupJoinKey => LookupResponseFormat,
        GapKind.EncryptedConnectionManagerSecret => EncryptedSecretResponseFormat,
        _ => CodeResponseFormat,
    };

    private const string LookupResponseFormat = """

        ## Respond with

        A JSON object exactly in this shape, and nothing else:

        ```json
        {
          "inputColumn": "...",
          "referenceColumn": "...",
          "confidence": "High | Medium | Low",
          "rationale": "one or two sentences",
          "evidenceSha256": "<the Evidence SHA-256 from the heading above, copied verbatim>"
        }
        ```

        A human copies this into `fills/<Package>.decisions.json` keyed by this gap's id, adding
        `confirmedBy`/`confirmedOn` themselves -- **that confirmation, not your confidence, is what
        makes it used.** `evidenceSha256` is what lets a LATER run tell a decision made against this
        package apart from one made against a version of it that has since changed; include it even
        though it feels redundant with the heading above.
        """;

    private const string EncryptedSecretContract = """
        Nothing to propose or write. There is no fact in this package an AI (or a human reading the
        .dtsx) can recover here -- the value is DPAPI-encrypted and only ever decryptable by the
        original author's own Windows account. What actually closes this:

        1. Obtain the real credential from wherever it lives outside this package (a password vault,
           the DBA, the target server's own connection-manager configuration, an existing deployment's
           SSISDB environment variable, etc.) -- NOT by attempting to decrypt the ciphertext in the
           .dtsx, which is not possible outside the original machine/account.
        2. Set it via User Secrets in the generated package's own project directory, e.g.:
           `dotnet user-secrets set "TargetDatabase:Password" "<the real password>"`
           (or `SecondaryConnections:<connection manager name>:Password` -- see the gap's own reason
           text and the generated appsettings.json for which key applies).
        3. Record an acknowledgment -- NEVER the value itself -- in this package's
           `fills/<Package>.decisions.json`, keyed by this gap's own id, with `ConfirmedBy` set and
           `EvidenceSha256` copied from the heading above. This only silences the report; it changes
           nothing about what gets generated. `EvidenceSha256` is what lets a later run detect that
           the connection manager changed underneath this acknowledgment and ask for a fresh one.

        Do not put the real secret anywhere in a chat response, a work packet answer, or
        `decisions.json`. This packet exists to make the requirement visible, not to collect the value.

        """;

    private const string EncryptedSecretResponseFormat = """

        ## Respond with

        Confirmation that steps 1-3 above were done (or an explanation of why they can't be, e.g. the
        credential could not be recovered). Do NOT include the actual secret value in your response.
        """;

    private const string CodeResponseFormat = """

        ## Respond with

        A single fenced `csharp` block containing only the code asked for above -- no prose around it,
        no `using` directives (the generated file already has them), no explanation inside the block.
        Put any caveats AFTER the block, and say plainly if the port is not faithful.

        Immediately above the method (or `partial class`) declaration, on its own line, include:

        ```
        // ssisx-fill: GapId=<this gap's id, from the heading above> Author=<your name or email> Date=<yyyy-mm-dd> EvidenceSha256=<the Evidence SHA-256 from the heading above>
        ```

        This is what lets `ssisx apply-fills` tell this port apart from a stale one on a later run,
        after the .dtsx has changed underneath it -- without it the fill is still applied (most fills
        predate this), just reported as unattributed rather than checked. Copy the id and hash
        verbatim from the heading above; do not compute or guess either.
        """;

    /// <summary>
    /// The SSIS semantics a ported translation has to preserve, distilled from this project's own
    /// gate-2 oracle corpus (docs/gate2-schema.md) -- every line here was MEASURED against the
    /// real SSIS evaluator, not read from documentation. An AI porting SSIS script/expression
    /// logic into C# will get several of these wrong unless told, because the naive C# equivalent
    /// looks right and behaves differently.
    /// </summary>
    private const string SsisSemanticsAppendix = """

        ## SSIS semantics you must preserve

        These were measured against the real SSIS evaluator by this project's own oracle, not
        assumed. A naive C# port gets several of them wrong:

        - **SSIS never implicitly coerces between types.** `1 + "a"`, `"1" + 1` and `LEN(123)` are
          all errors in SSIS, not silent conversions.
        - **NULL propagates with SQL-style three-valued logic.** `NULL == 1` is NULL, not `false`.
          `FALSE && NULL` is `false`; `TRUE || NULL` is `true`; `TRUE && NULL` is NULL.
        - **String `==`/`!=` are ORDINAL (case-sensitive); `<`/`>`/`<=`/`>=` are CULTURE-AWARE.**
          In C#: `string.Equals(a, b, StringComparison.Ordinal)` versus
          `string.Compare(a, b, StringComparison.InvariantCulture)`. Lowercase sorts before its
          own uppercase.
        - **`(DT_WSTR,n)`/`(DT_STR,n)` casts are a HARD ERROR on overflow**, never a silent truncation.
        - **`(DT_I4)` on a float rounds half-to-EVEN** (2.5 -> 2, 3.5 -> 4), matching .NET's own
          default `Math.Round`. Casting `TRUE` yields **-1**, not 1.
        - **`SUBSTRING` is 1-based and errors when start < 1** (including 0); an out-of-range range
          clamps to `""`.
        - **`TOKEN`/`TOKENCOUNT`'s delimiter argument is a CHARACTER SET** (strtok-style), not a
          substring, and an empty input string is exactly one token, not zero.

        If the script source uses .NET APIs directly (rather than SSIS expressions), keep its .NET
        semantics -- these rules apply to SSIS expression logic being reproduced, not to C# that was
        already C#.

        """;

    private static string BuildEvidence(PackageSpec package, GenerationGap gap) => gap.Kind switch
    {
        GapKind.ScriptTask => ScriptTaskEvidence(package, gap),
        GapKind.ScriptComponentColumn => ScriptComponentEvidence(package, gap),
        GapKind.LookupJoinKey => LookupEvidence(package, gap),
        GapKind.EncryptedConnectionManagerSecret => EncryptedSecretEvidence(package, gap),
        _ => "_(no evidence builder for this gap kind)_\n",
    };

    private static string EncryptedSecretEvidence(PackageSpec package, GenerationGap gap)
    {
        var cm = package.ConnectionManagers.FirstOrDefault(c => c.RefId == gap.EvidenceRefId);
        if (cm is null)
            return $"_Could not resolve the connection manager for refId `{gap.EvidenceRefId}` -- report this, it is a bug in AiPacketEmitter._\n";

        var sb = new StringBuilder();
        sb.Append($"- **Connection manager:** `{cm.ObjectName}`\n");
        sb.Append($"- **Type:** `{cm.CreationName}`\n");
        sb.Append($"- **Encrypted properties:** {FormatList(cm.EncryptedProperties)}\n");
        if (cm.ConnectionString is not null)
            sb.Append($"- **Connection string (redacted):** `{cm.ConnectionString}`\n");
        sb.Append("\n_The ciphertext itself is never extracted -- there is nothing more to show here._\n");
        return sb.ToString();
    }

    private static string ScriptTaskEvidence(PackageSpec package, GenerationGap gap)
    {
        var executable = FindExecutable(package, gap.EvidenceRefId);
        if (executable?.ScriptTask is not { } script)
            return $"_Could not resolve the Script Task for refId `{gap.EvidenceRefId}` -- report this, it is a bug in AiPacketEmitter._\n";

        var sb = new StringBuilder();
        sb.Append($"- **Task:** `{executable.ObjectName ?? executable.RefId}`\n");
        sb.Append($"- **Language:** `{script.Language ?? "(not declared)"}`\n");
        sb.Append($"- **Reads variables:** {FormatList(script.ReadOnlyVariables)}\n");
        sb.Append($"- **Writes variables:** {FormatList(script.ReadWriteVariables)}\n\n");

        if (script.SourceStripped || script.ProjectItems.Count == 0)
        {
            sb.Append("**This task was saved with its source STRIPPED** -- only a compiled binary remains, so\n");
            sb.Append("there is nothing to port from. Say so; do not invent an implementation.\n");
            return sb.ToString();
        }

        // The VSTA entry point plus any other real source file. The .csproj/AssemblyInfo/designer
        // files carry no logic to port and would bury the code that does, so they are listed by
        // name only rather than dumped in full.
        foreach (var item in script.ProjectItems.Where(IsInterestingSource))
            sb.Append($"### `{item.Name}`\n\n```{FenceLanguage(item.Name)}\n{item.Content.TrimEnd()}\n```\n\n");

        var skipped = script.ProjectItems.Where(i => !IsInterestingSource(i)).Select(i => i.Name).ToList();
        if (skipped.Count > 0)
            sb.Append($"_Other VSTA project files, not shown: {string.Join(", ", skipped.Select(s => $"`{s}`"))}._\n");

        return sb.ToString();
    }

    private static string ScriptComponentEvidence(PackageSpec package, GenerationGap gap)
    {
        var found = FindComponent(package, gap.EvidenceRefId);
        if (found is null || found.Value.Component.ScriptComponent is not { } script)
            return $"_Could not resolve the Script Component for refId `{gap.EvidenceRefId}` -- report this, it is a bug in AiPacketEmitter._\n";

        var (taskName, component) = found.Value;
        var sb = new StringBuilder();
        sb.Append($"- **Data Flow Task:** `{taskName}`\n");
        sb.Append($"- **Component:** `{component.Name}`\n");
        sb.Append($"- **Language:** `{script.Language ?? "(not declared)"}`\n");
        sb.Append($"- **Reads variables:** {FormatList(script.ReadOnlyVariables)}\n");
        sb.Append($"- **Writes variables:** {FormatList(script.ReadWriteVariables)}\n\n");

        sb.Append("### Input columns available on `row`\n\n");
        sb.Append(ColumnTable(component.Inputs.SelectMany(i => i.Columns)
            .Select(c => (c.CachedName, c.CachedDataType, c.CachedLength))));

        sb.Append("\n### Columns this component produces\n\n");
        sb.Append("The gap above is for ONE of these. Port only that one.\n\n");
        sb.Append(ColumnTable(component.Outputs.Where(o => o.IsErrorOut != true).SelectMany(o => o.Columns)
            .Select(c => (c.Name, c.DataType, c.Length))));

        if (script.SourceStripped || script.SourceCodeItems.Count == 0)
        {
            sb.Append("\n**This component was saved with its source STRIPPED** -- only compiled binary remains,\n");
            sb.Append("so there is nothing to port from. Say so; do not invent an implementation.\n");
            return sb.ToString();
        }

        // Prefer the PARSED files (name/encoding/content triples -- see SourceFiles' own doc
        // comment). Only the authored entry point matters for a port; the rest of the array is
        // VSTA-generated scaffolding that would bury it. Falling back to the raw array keeps a
        // misaligned/unparseable case usable rather than silently empty.
        if (script.SourceFiles.Count > 0)
        {
            foreach (var file in script.SourceFiles.Where(IsAuthoredComponentSource))
                sb.Append($"\n### `{file.Name}`\n\n```{FenceLanguage(file.Name)}\n{file.Content.TrimEnd()}\n```\n");

            var scaffolding = script.SourceFiles.Where(f => !IsAuthoredComponentSource(f)).Select(f => f.Name).ToList();
            if (scaffolding.Count > 0)
                sb.Append($"\n_VSTA-generated scaffolding, not shown: {string.Join(", ", scaffolding.Select(s => $"`{s}`"))}._\n");

            return sb.ToString();
        }

        for (var i = 0; i < script.SourceCodeItems.Count; i++)
        {
            sb.Append($"\n### Source item {i + 1} of {script.SourceCodeItems.Count}\n\n");
            sb.Append($"```{FenceLanguage(script.Language)}\n{script.SourceCodeItems[i].TrimEnd()}\n```\n");
        }

        return sb.ToString();
    }

    private static string LookupEvidence(PackageSpec package, GenerationGap gap)
    {
        var found = FindComponent(package, gap.EvidenceRefId);
        if (found is null || found.Value.Component.Lookup is not { } lookup)
            return $"_Could not resolve the Lookup for refId `{gap.EvidenceRefId}` -- report this, it is a bug in AiPacketEmitter._\n";

        var (taskName, component) = found.Value;
        var sb = new StringBuilder();
        sb.Append($"- **Data Flow Task:** `{taskName}`\n");
        sb.Append($"- **Component:** `{component.Name}`\n");
        sb.Append($"- **Reference connection:** `{lookup.ConnectionName ?? "(unresolved)"}`\n");
        sb.Append($"- **Cache type (raw):** `{lookup.CacheTypeRaw?.ToString() ?? "(not set)"}`  (0 = full cache)\n\n");

        sb.Append("### The Lookup's own INPUT columns\n\n");
        sb.Append("SSIS only keeps a column on a Lookup's input if the component actually references it, so\n");
        sb.Append("the join key is almost always in this list -- often it is the only entry.\n\n");
        sb.Append(ColumnTable(component.Inputs.SelectMany(i => i.Columns)
            .Select(c => (c.CachedName, c.CachedDataType, c.CachedLength))));

        sb.Append("\n### Columns the Lookup ADDS to matched rows\n\n");
        sb.Append(ColumnTable(component.Outputs.Where(o => o.IsErrorOut != true).SelectMany(o => o.Columns)
            .Select(c => (c.Name, c.DataType, c.Length))));

        if (lookup.SqlCommand is { Length: > 0 } sql)
            sb.Append($"\n### Reference query\n\n```sql\n{sql.TrimEnd()}\n```\n");

        if (lookup.ReferenceColumns.Count > 0)
        {
            sb.Append("\n### Reference table columns\n\n");
            sb.Append(ColumnTable(lookup.ReferenceColumns.Select(c => (c.Name, c.DataType, c.Length))));
        }

        return sb.ToString();
    }

    private static string ColumnTable(IEnumerable<(string Name, string? DataType, int? Length)> columns)
    {
        var rows = columns.ToList();
        if (rows.Count == 0) return "_(none)_\n";

        var sb = new StringBuilder("| Column | SSIS type | Length | C# type |\n|---|---|---|---|\n");
        foreach (var (name, dataType, length) in rows)
        {
            var clr = SsisPipelineTypeMap.Resolve(NormalizePipelineType(dataType))?.ClrTypeName ?? "(unmapped)";
            sb.Append($"| `{name}` | `{dataType ?? "?"}` | {(length is > 0 ? length.ToString() : "-")} | `{clr}` |\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Two different spellings of the same type vocabulary reach this emitter: a pipeline
    /// buffer/output column carries the short form <c>SsisPipelineTypeMap</c> is keyed by
    /// (<c>wstr</c>, <c>i4</c>), while a Lookup's own reference columns -- parsed from its
    /// <c>ReferenceMetadataXml</c> -- carry the <c>DT_</c>-prefixed form (<c>DT_WSTR</c>,
    /// <c>DT_I4</c>). Normalized here at the call site rather than by widening the shared map's
    /// accepted vocabulary, which every other consumer relies on being exactly the buffer form.
    /// </summary>
    private static string? NormalizePipelineType(string? dataType) =>
        dataType?.StartsWith("DT_", StringComparison.OrdinalIgnoreCase) == true ? dataType[3..] : dataType;

    /// <summary>A Script Component's VSTA project is almost entirely generated plumbing --
    /// ComponentWrapper/BufferWrapper (the buffer accessors SSIS regenerates from the component's
    /// own column metadata), Properties\*, the .csproj, and an internal "Project" descriptor. The
    /// only authored file is the entry point, <c>main.cs</c>/<c>main.vb</c> -- note NOT
    /// <c>ScriptMain.*</c>, which is the Script *Task* convention.</summary>
    private static bool IsAuthoredComponentSource(ScriptComponentSourceFileSpec file) =>
        (file.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || file.Name.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
        && !file.Name.Contains('\\')
        && !file.Name.Contains("ComponentWrapper", StringComparison.OrdinalIgnoreCase)
        && !file.Name.Contains("BufferWrapper", StringComparison.OrdinalIgnoreCase);

    private static bool IsInterestingSource(ScriptProjectItemSpec item) =>
        (item.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || item.Name.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
        && !item.Name.Contains("AssemblyInfo", StringComparison.OrdinalIgnoreCase)
        && !item.Name.Contains(".Designer.", StringComparison.OrdinalIgnoreCase);

    private static string FenceLanguage(string? nameOrLanguage) =>
        nameOrLanguage is not null
        && (nameOrLanguage.EndsWith(".vb", StringComparison.OrdinalIgnoreCase)
            || nameOrLanguage.Contains("VisualBasic", StringComparison.OrdinalIgnoreCase))
            ? "vb"
            : "csharp";

    private static string FormatList(List<string> values) =>
        values.Count == 0 ? "_(none)_" : string.Join(", ", values.Select(v => $"`{v}`"));

    private static ExecutableSpec? FindExecutable(PackageSpec package, string? refId) =>
        refId is null ? null : PackageTree.AllExecutables(package).FirstOrDefault(e => e.RefId == refId);

    /// <summary>Resolved by refId, never by display name -- two components in different Data Flow
    /// Tasks can genuinely share a name (RBC_Demo_ETL has two Data Conversions both producing
    /// "CustomerID_i4"), which is exactly why GenerationGap carries EvidenceRefId structurally.</summary>
    private static (string TaskName, PipelineComponentSpec Component)? FindComponent(PackageSpec package, string? refId)
    {
        if (refId is null) return null;
        foreach (var executable in PackageTree.AllExecutables(package))
        {
            var pipeline = executable.DataFlowTask?.Pipeline;
            if (pipeline is null) continue;
            var component = pipeline.Components.FirstOrDefault(c => c.RefId == refId);
            if (component is not null) return (executable.ObjectName ?? executable.RefId, component);
        }
        return null;
    }
}
