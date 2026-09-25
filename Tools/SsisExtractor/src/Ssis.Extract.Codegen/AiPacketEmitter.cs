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
            ExpectedFileName = gap.Kind == GapKind.LocalFileSourceData ? LocalFileSourceDataExpectedFileName(package, gap) : null,
        };

    /// <summary>The real <c>TestData/</c> file name this gap's fill must use -- see
    /// <see cref="GapSpec.ExpectedFileName"/>'s own doc comment for why this is NOT the Tier-A
    /// sample's own name. Mirrors <see cref="LocalFileSourceDataEvidence"/>'s own three-way
    /// resolution (by connection manager RefId for CSV/fixed-width, by component name for Excel/
    /// XML) rather than sharing code with it -- each resolution is a few lines and returns a
    /// differently-shaped result (evidence text vs. a bare file name).</summary>
    private static string? LocalFileSourceDataExpectedFileName(PackageSpec package, GenerationGap gap)
    {
        if (gap.EvidenceRefId is { Length: > 0 } refId)
        {
            var cm = package.ConnectionManagers.FirstOrDefault(c => c.RefId == refId);
            return cm?.Parsed?.FilePath is { Length: > 0 } path ? Path.GetFileName(path) : null;
        }

        var component = PackageTree.AllExecutables(package)
            .Select(e => e.DataFlowTask?.Pipeline)
            .Where(p => p is not null)
            .SelectMany(p => p!.Components)
            .FirstOrDefault(c => c.Name == gap.Location && (c.ExcelSource is not null || c.XmlSource is not null));

        // An XML Source has no connection manager at all (see XmlSourcePayload's own doc
        // comment) -- its own literal XMLData path IS the file name to resolve, no connection
        // manager lookup needed.
        if (component?.XmlSource is { } xml)
            return xml.XmlDataPath is { Length: > 0 } xmlPath ? Path.GetFileName(xmlPath) : null;

        var cmName = component?.ExcelSource?.ConnectionName;
        var excelCm = cmName is null ? null : package.ConnectionManagers.FirstOrDefault(c => c.ObjectName == cmName);
        return excelCm?.Parsed?.FilePath is { Length: > 0 } excelPath ? Path.GetFileName(excelPath) : null;
    }

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
        GapKind.TestOracle => TestOracleContract,
        GapKind.LocalFileSourceData => LocalFileSourceDataContract,
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
        | `Dts.Events.FireError` + `Dts.TaskResult = Failure` | `throw` -- this is this rewrite's OWN documented equivalent of a Script Task `Failure` result, not merely "seemed reasonable": an unhandled exception from `RunAsync` triggers the same rollback + `FailureHandlers` path a `Value=Failure` precedence constraint or `OnError` handler would, and fails the whole run the same way SSIS's own task-failure semantics do. Treat this as settled unless the packet says otherwise. |
        | `Dts.Events.FireInformation` | an `ILogger` resolved from `ctx.Services` |
        | `DateTime.Now` / `DateTime.UtcNow` | `DateTime.UtcNow` directly -- there is no per-call "now" on `ctx` (`ctx.Load.StartedAtUtc` is fixed at the whole RUN's start, not this task's own execution moment, and would silently collapse a real elapsed-time computation to ~0). Call out the local-to-UTC timezone change; do not assume it is inconsequential. |

        Note the transaction difference and do not try to work around it: this task's SQL joins the
        package's single all-or-nothing transaction, whereas SSIS auto-committed per task.

        **If the task's real logic cannot be reproduced against these abstractions**, say so
        explicitly instead of inventing a substitute. That answer is more useful than a plausible one.

        """;

    private const string ScriptComponentContract = """
        The body of ONE method computing EVERY column this Script Component produces, together, for
        a single row -- not one method per column. It will be spliced into the generated transform as
        a `partial` method returning the combined record `ssisx generate --seams` already declared, so
        it must be a pure function of the row:

        ```csharp
        // ssisx-fill: GapId=<this gap's id> Author=<you> Date=<yyyy-mm-dd> EvidenceSha256=<from the heading above>
        private partial <Component>Result <Component>(<RowType> row, in RowContext ctx)
        {
            // fields/helpers are allowed here -- this is a class part, not a bare method body.
            return new(<Column1>: ..., <Column2>: ..., ...);
        }
        ```

        `<Component>`/`<Component>Result` and the exact record shape (one property per produced
        column, with its own C# type) are both already generated -- read them off the seam declaration
        this gap's own reason text points at, or the "Columns this component produces" table below.
        `<RowType>` is the generated row type, whose properties are the "Input columns available on
        `row`" table. Getting every property's type right matters -- the rest of the method signature
        is filled in for you when the fill is applied.

        The Script Component's full source is below -- port the WHOLE component, computing all of its
        columns from the one method (they came from one script; there is exactly one work packet for
        it, not one per column).

        This method is itself called once PER ROW -- it is exactly where `Input0_ProcessInputRow` ran
        in the original. So if the original script called `DateTime.Now`/`DateTime.UtcNow` inside that
        per-row callback, calling `DateTime.UtcNow` directly inside THIS method is the faithful port:
        it still evaluates fresh per row, only switching local time to UTC (call that timezone change
        out; do not assume it is inconsequential). Reach for `ctx.LoadedAtUtc` instead only when the
        original value was clearly meant to be ONE shared instant for the whole load -- e.g. it
        reproduces an SSIS built-in expression like `GETUTCDATE()`, which this tool's own expression
        translator already makes deterministic per load elsewhere, by design. Using `ctx.LoadedAtUtc`
        as a substitute for a per-row `DateTime.Now` changes the actual VALUES stored on every row, not
        just an implementation detail -- if you choose it anyway, say so explicitly, don't substitute
        silently.

        """;

    /// <summary>
    /// The ONE pinned reference block for writing a generated test, defined once here and reused
    /// VERBATIM by <see cref="TestOracleContract"/>, <c>Tools/.github/prompts/ssisx-test.prompt.md</c>
    /// (a later phase), and <c>Tools/COPILOT_TESTING_GUIDE.md</c> -- so all three cannot silently
    /// drift out of agreement with each other or with what <see cref="TestDoublesEmitter"/> actually
    /// emits. Covers exactly what a TEST-ORACLE packet's own reader needs and nothing else: this is
    /// not a general testing tutorial.
    /// </summary>
    internal const string PinnedTestingApiBlock = """
        **Known-good testing API -- do not read `TestDoubles/` to confirm any of this:**

        - `using var harness = new PackageHarness();` -- one per test, disposed at the end (a `using`
          statement is enough). `harness.Package()` returns a fresh instance of the generated package
          class, wired to fakes (no database, no real file server, no real Script Task connections).
        - `harness.NewUnitOfWork()` -- a standalone `FakeUnitOfWork` for calling one method directly
          (`await package.SomeMethod(uow, CancellationToken.None)`), without going through `RunAsync`.
          Its public surface: `BeginCalled`/`CommitCalled`/`RollbackCalled` (bool), `ExecutedSql`
          (`List<string>`), `ExecutedParameterizedSql` (`List<ParameterizedSqlCall>`),
          `ExecutedSqlWithoutTransaction` (`List<string>`), `BulkInserts` (`List<BulkInsertCall>`,
          each with `DestinationTable`/`ColumnMappings`/`RowsWritten`). Set `ThrowOnGetBindToken`
          before the call to make the very next `GetBindTokenAsync` throw.
        - `harness.CreatedUnitsOfWork` -- every `FakeUnitOfWork` actually resolved via DI so far (one
          per scope), for asserting on a run made through `RunAsync` itself rather than a direct call.
        - `harness.Notifier.Result` -- the captured `PackageResult` after a `RunAsync` call.
        - `harness.SinkFilePath(key)` / `FileSystemTaskPath(key)` / `ForEachLoopFolder(key)` -- the
          real, resolved path a Flat File Destination / File System Task / ForEach Loop method keyed
          by `key` reads from or writes to, matching `FileSourceOptions` exactly.
        - A test needing something the harness fakes for free CANNOT provide (a real database, a
          real `.xlsx` file, a real secondary-connection server) gets
          `[Trait("Category", "Integration")]` -- the always-green baseline is
          `dotnet test --filter Category!=Integration`, so this tag is required, not optional, on
          such a test.
        - **Get every method's exact signature from `<Package>/README.md`'s own component table,
          never by reading `<Package>.cs` top to bottom.** It is generated fresh every run
          specifically so an assistant never has to.
        """;

    private static string ResponseFormat(GenerationGap gap) => gap.Kind switch
    {
        GapKind.LookupJoinKey => LookupResponseFormat,
        GapKind.EncryptedConnectionManagerSecret => EncryptedSecretResponseFormat,
        GapKind.TestOracle => TestOracleResponseFormat,
        GapKind.LocalFileSourceData => LocalFileSourceDataResponseFormat,
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

    private const string TestOracleContract = """
        A whole, self-contained xUnit test FILE -- not a seam spliced into existing generated code
        (there is nothing to splice into here; unlike a Script Task/Component gap, this one has no
        `partial` method waiting for you). It will be copied verbatim into
        `<Package>.Tests/Fills/` by `ssisx apply-fills`, alongside the generated starter tests.

        """ + PinnedTestingApiBlock + """


        The evidence below tells you WHICH of five shapes this is -- read it before writing anything:

        - **A Conditional Split case** this pilot's own oracle-based evaluator could not resolve to a
          boolean for a representative row (see the reason above). Construct your OWN representative
          row (you are not limited to the pilot's single hard-coded row) and assert
          `new <RouterClass>().SelectBranch(row, ctx)` lands in the branch you expect, with a comment
          explaining WHY that branch is correct for that input.
        - **A Script Task seam**, once it is filled. Call the generated task directly through a
          `PackageHarness`-backed `IUnitOfWork` and assert its real, observable effect (a row it wrote
          via `uow.ExecutedSql`, a variable it set via `ctx.Variables` if you construct the context
          yourself, etc.) -- not merely that it does not throw.
        - **A Script Component seam**, once it is filled. The seam is `private`, so it is never
          directly callable from a separate `.Tests` project -- construct a representative row and
          call `new <TransformClass>().Map(row, ctx)` instead, then assert the exact expected value(s)
          on the returned entity for however many of the component's own columns you can verify --
          state your reasoning for what "correct" means for each, since nothing here computes it for
          you the way `TransformTestEmitter`'s own oracle-verified assertions do.
        - **An Aggregate GroupBy/count source**, whose own starter test could not be generated
          because its GroupBy key resolves through a Lookup cache rather than a plain row property.
          Construct a real `AggregateRowSource<TSourceRow,TKey,TRow>` directly (the same class
          production code uses), feeding it a small in-memory fake `IRowSource<TSourceRow>` and a
          key-selector function using a plain dictionary as a stand-in for the Lookup cache (no real
          database needed) -- then assert the grouped/counted output rows.
        - **A ForEach-Loop-over-a-Data-Flow-Task's own loop body**, whose per-iteration source has no
          static Tier-A sample file to point a test at (its path is computed fresh each iteration, not
          a fixed location). Write two small temp files into `harness.ForEachLoopFolder(key)` (the
          evidence below names the real folder/file-spec/destination the real package uses) and assert
          the destination sink receives rows from both.

        **If you cannot determine a correct expected value with confidence, say so instead of
        guessing one** -- a test asserting a wrong value is worse than no test at all, since it looks
        like proof of something that was never actually checked.

        """;

    /// <summary>
    /// Docs/Generated-Tests-Plan.md's own Tier B: the wiring (`appsettings.Development.json`'s
    /// `TestData` override) is ALREADY generated unconditionally -- this packet is asked for only
    /// the DATA, a realistic file matching the exact declared schema below.
    /// </summary>
    private const string LocalFileSourceDataContract = """
        A realistic sample data file matching the EXACT schema in the evidence below (same columns,
        same order, same delimiter/fixed-width positions, same header presence) -- not a redesign of
        the format, just believable VALUES in place of the placeholder shown. If `svk sampledata` is
        available in this environment, running it first produces a schema-correct starting point (see
        `Tools/COPILOT_GUIDE.md`); this packet is asking you to make ITS output realistic, not to
        invent a file from nothing.

        This file will be copied byte-for-byte into `<Package>/TestData/` by `ssisx apply-fills` --
        there is no provenance comment convention for a data file (unlike a code fill), so it is
        applied and reported `Applied` on file name alone, with no staleness check against a later
        schema change. If the connection manager's own schema changes, this file will need replacing
        directly, and nothing will detect that automatically -- state this limitation is understood.

        """;

    private const string EncryptedSecretResponseFormat = """

        ## Respond with

        Confirmation that steps 1-3 above were done (or an explanation of why they can't be, e.g. the
        credential could not be recovered). Do NOT include the actual secret value in your response.
        """;

    private const string TestOracleResponseFormat = """

        ## Respond with

        A single fenced `csharp` block containing the WHOLE test file (usings, namespace, class,
        one or more `[Fact]`s) -- this is a new, self-contained file, not a snippet to splice in.

        As the FIRST line of the file, include:

        ```
        // ssisx-fill: GapId=<this gap's id, from the heading above> Author=<your name or email> Date=<yyyy-mm-dd> EvidenceSha256=<the Evidence SHA-256 from the heading above>
        ```

        This is what lets `ssisx apply-fills` tell this file apart from a stale one on a later run.
        Copy the id and hash verbatim from the heading above; do not compute or guess either.
        """;

    private const string LocalFileSourceDataResponseFormat = """

        ## Respond with

        A single fenced block containing the exact file content (`csv` for a delimited/fixed-width
        file; state the file type explicitly if it is anything else, e.g. an `.xlsx` workbook cannot
        be represented as text at all -- say so and describe what you would need to produce one
        instead). No provenance comment -- see the contract above for why none is possible here.
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
        GapKind.TestOracle => TestOracleEvidence(package, gap),
        GapKind.LocalFileSourceData => LocalFileSourceDataEvidence(package, gap),
        _ => "_(no evidence builder for this gap kind)_\n",
    };

    /// <summary>
    /// Dispatches to one of five shapes by trying, in order, what <see cref="GapSpec.EvidenceRefId"/>
    /// actually resolves to -- a Script Task companion test, a Script Component column companion
    /// test, a Conditional Split router test, an Aggregate GroupBy/count source test, or a ForEach
    /// Data Flow Loop's own loop-body test (see <see cref="TestOracleContract"/>'s own bullets,
    /// which this must stay in lockstep with). The fourth (Aggregate) shape was added 2026-09-06
    /// after a real gap (<c>Package_Transforms</c>'s own <c>RegionSummary</c>, whose GroupBy key
    /// resolves through a Lookup cache rather than a plain row property) fell through to the
    /// generic "could not resolve" fallback below -- <see cref="PipelineComponentSpec.Aggregate"/>
    /// was never checked at all, not a bug in the refId lookup itself (<see cref="FindComponent"/>
    /// already resolved the component correctly). The fifth (ForEach Data Flow Loop) shape closes
    /// the identical failure class for real: its own EvidenceRefId points at the loop body's
    /// DESTINATION component, which has none of ScriptComponent/ConditionalSplit/Aggregate set
    /// either, and <see cref="FindComponent"/> alone can't tell "this is inside a loop" at all --
    /// see <see cref="FindForEachDataFlowLoop"/>'s own doc comment for why that needed a real
    /// ancestor-aware tree walk, not just another field check.
    /// </summary>
    private static string TestOracleEvidence(PackageSpec package, GenerationGap gap)
    {
        if (gap.Location.EndsWith(".ScriptTask", StringComparison.Ordinal)
            && FindExecutable(package, gap.EvidenceRefId) is { ScriptTask: not null })
        {
            return "_This is the companion test for the Script Task ported in its own SCRIPT-TASK work packet --" +
                   " the source below is that SAME script; write a test that exercises the PORTED C# once it" +
                   " exists, not the original SSIS script itself._\n\n" + ScriptTaskEvidence(package, gap);
        }

        var found = FindComponent(package, gap.EvidenceRefId);
        if (found?.Component.ScriptComponent is not null)
        {
            return "_This is the companion test for the column ported in its own SCRIPT-COLUMN work packet --" +
                   " the source below is that SAME Script Component; write a test that exercises the PORTED C#" +
                   " once it exists, not the original SSIS script itself._\n\n" + ScriptComponentEvidence(package, gap);
        }

        if (found?.Component.ConditionalSplit is { } split)
        {
            var (taskName, component) = found.Value;
            var sb = new StringBuilder();
            sb.Append($"- **Data Flow Task:** `{taskName}`\n");
            sb.Append($"- **Component:** `{component.Name}`\n\n");
            sb.Append("### Every case, in evaluation order (the failing one is named in the reason above)\n\n");
            sb.Append("| # | Output | Condition (FriendlyExpression) |\n|---|---|---|\n");
            for (var i = 0; i < split.Cases.Count; i++)
                sb.Append($"| {i} | `{split.Cases[i].OutputName}` | `{split.Cases[i].FriendlyExpression ?? "(none)"}` |\n");
            sb.Append($"| {split.Cases.Count} (default) | `{split.DefaultOutputName}` | _(always matches if nothing above did)_ |\n");

            sb.Append("\n### This case's own input columns\n\n");
            sb.Append(ColumnTable(component.Inputs.SelectMany(i => i.Columns)
                .Select(c => (c.CachedName, c.CachedDataType, c.CachedLength))));
            return sb.ToString();
        }

        // Fifth shape: a ForEach-Loop-over-a-Data-Flow-Task's own loop body (see
        // PackageGenerator's own "no starter test coverage exists for this loop-body shape"
        // comment) -- EvidenceRefId points at the loop body's DESTINATION component, which has
        // none of ScriptComponent/ConditionalSplit/Aggregate set, so it fell through to the
        // generic "could not resolve" message below until this branch was added. Checked AFTER
        // Aggregate (an Aggregate can itself sit inside a loop body's own Data Flow Task in
        // principle, though not evidenced yet -- the Aggregate-specific evidence is more useful
        // when both match).
        if (FindForEachDataFlowLoop(package, gap.EvidenceRefId) is { } loopMatch)
        {
            var (loop, dataFlowExecutable, destination) = loopMatch;
            var fe = loop.ForEachLoop?.FileEnumerator;
            var lsb = new StringBuilder();
            lsb.Append($"- **ForEach Loop:** `{loop.ObjectName ?? loop.RefId}`\n");
            lsb.Append($"- **Folder:** `{fe?.Folder ?? "(unresolved)"}`\n");
            lsb.Append($"- **File spec:** `{fe?.FileSpec ?? "(unresolved)"}`\n");
            lsb.Append($"- **Data Flow Task (runs once per file):** `{dataFlowExecutable.ObjectName ?? dataFlowExecutable.RefId}`\n");
            lsb.Append($"- **Destination component:** `{destination.Name}`\n\n");
            lsb.Append("### Destination's own input columns (the row shape written each iteration)\n\n");
            lsb.Append(ColumnTable(destination.Inputs.SelectMany(i => i.Columns)
                .Select(c => (c.CachedName, c.CachedDataType, c.CachedLength))));
            lsb.Append("\n_There is no static sample file to point a source test at -- the path is computed" +
                        " per iteration from the enumerated file name, not a fixed location. Write two small" +
                        " temp files yourself (matching `harness.ForEachLoopFolder(key)`'s own convention, see" +
                        " the pinned testing API above) and assert the destination sink receives rows from" +
                        " each._\n");
            return lsb.ToString();
        }

        if (found?.Component.Aggregate is { } agg)
        {
            var (taskName, component) = found.Value;
            var pipeline = FindPipeline(package, component.RefId);

            var asb = new StringBuilder();
            asb.Append($"- **Data Flow Task:** `{taskName}`\n");
            asb.Append($"- **Component:** `{component.Name}`\n\n");
            asb.Append("### Every aggregate column, in declared order\n\n");
            asb.Append("| Output | Role | Source column |\n|---|---|---|\n");
            foreach (var col in agg.Columns)
            {
                var role = AggregationTypeName(col.AggregationTypeRaw);
                var sourceName = ResolveLineageColumnName(pipeline, col.SourceColumnLineageId);
                asb.Append($"| `{col.OutputColumnName}` | {role} | {(sourceName is null ? "_(unresolved)_" : $"`{sourceName}`")} |\n");
            }
            asb.Append("\n_A `GroupBy` column resolving through a Lookup's own reference cache" +
                        " (rather than a plain source column) needs a stand-in for that cache in" +
                        " a fakes-only test -- see this project's own `RegionSummary` precedent for" +
                        " the shape (construct the real `AggregateRowSource<TSourceRow,TKey,TRow>`" +
                        " directly with a plain dictionary standing in for the Lookup cache)._\n");
            return asb.ToString();
        }

        return $"_Could not resolve the source of this test-oracle gap (refId `{gap.EvidenceRefId}`) -- report this, it is a bug in AiPacketEmitter._\n";
    }

    private static string AggregationTypeName(int? raw) => raw switch
    {
        0 => "GroupBy",
        1 => "Count",
        2 => "CountAll",
        3 => "CountDistinct",
        4 => "Sum",
        5 => "Average",
        6 => "Minimum",
        7 => "Maximum",
        _ => $"(unrecognized AggregationType {raw?.ToString() ?? "(none)"})",
    };

    /// <summary>Finds the whole pipeline containing the component identified by <paramref name="componentRefId"/>
    /// -- <see cref="FindComponent"/> only ever hands back the one matching component, not the
    /// pipeline it lives in, but resolving an Aggregate column's own source column needs to search
    /// every OTHER component's output columns in that SAME data flow for the matching LineageId.</summary>
    private static PipelineSpec? FindPipeline(PackageSpec package, string componentRefId) =>
        PackageTree.AllExecutables(package)
            .Select(e => e.DataFlowTask?.Pipeline)
            .FirstOrDefault(p => p is not null && p.Components.Any(c => c.RefId == componentRefId));

    /// <summary>Resolves a lineageId to the NAME of the output column that produced it, searching
    /// every component's own outputs in the given pipeline -- the same join key
    /// <c>Ssis.Extract.Dtsx.LineageBuilder</c> uses globally, but scoped to one pipeline and done
    /// locally here rather than pulling in that whole machinery for a single evidence lookup.</summary>
    private static string? ResolveLineageColumnName(PipelineSpec? pipeline, string? lineageId)
    {
        if (pipeline is null || lineageId is null) return null;
        foreach (var component in pipeline.Components)
            foreach (var output in component.Outputs)
            {
                var column = output.Columns.FirstOrDefault(c => c.LineageId == lineageId);
                if (column is not null) return column.Name;
            }
        return null;
    }

    /// <summary>
    /// Resolved differently depending on which source kind reported it: a CSV/fixed-width source
    /// carries its connection manager's own RefId (<see cref="GapSpec.EvidenceRefId"/>), so the
    /// schema comes straight from <c>FlatFileFormatSpec</c>; an Excel or XML source carries none
    /// (neither has a per-package-generation connection-manager thread -- an XML Source has no
    /// connection manager reference AT ALL, see <c>XmlSourcePayload</c>'s own doc comment), so
    /// each is instead resolved by NAME: <c>PackageGenerator.BuildExcelFlowSource</c>/
    /// <c>BuildXmlFlowSource</c> both derive a Flow's own FileSourceKey directly from the
    /// component's own <c>Name</c> (<c>gap.Location</c>), so searching for a component with that
    /// exact name is enough.
    /// </summary>
    private static string LocalFileSourceDataEvidence(PackageSpec package, GenerationGap gap)
    {
        var expectedFileName = LocalFileSourceDataExpectedFileName(package, gap);
        var saveAsLine = expectedFileName is null
            ? "- **Save as:** _(no design-time default path on this connection manager to derive a name from -- name it to match whatever `appsettings.json`'s own SourceFileName ends up being)_\n"
            : $"- **Save as:** `TestData/{expectedFileName}`\n";

        if (gap.EvidenceRefId is { Length: > 0 } refId)
        {
            var cm = package.ConnectionManagers.FirstOrDefault(c => c.RefId == refId);
            if (cm?.FlatFileFormat is not { } format)
                return $"_Could not resolve the flat-file connection manager for refId `{refId}` -- report this, it is a bug in AiPacketEmitter._\n";

            var sb = new StringBuilder();
            sb.Append(saveAsLine);
            sb.Append($"- **Connection manager:** `{cm.ObjectName}`\n");
            sb.Append($"- **Format:** `{format.Format ?? "(not set)"}`\n");
            sb.Append($"- **Column delimiter:** `{format.Columns.FirstOrDefault()?.ColumnDelimiterDisplay ?? "(fixed-width -- see MaximumWidth per column)"}`\n");
            sb.Append($"- **Row delimiter:** `{(string.IsNullOrEmpty(format.RowDelimiterDisplay) ? "(not set -- see the last column's own delimiter, which usually carries it)" : format.RowDelimiterDisplay)}`\n");
            sb.Append($"- **Header row present:** `{format.ColumnNamesInFirstDataRow == true}`\n\n");
            sb.Append("### Columns, in file order\n\n");
            sb.Append("| Column | SSIS type | Width | Delimiter |\n|---|---|---|---|\n");
            foreach (var col in format.Columns)
                sb.Append($"| `{col.ObjectName}` | `{col.DataTypeName}` | {(col.MaximumWidth is > 0 ? col.MaximumWidth.ToString() : "-")} | `{col.ColumnDelimiterDisplay ?? "(row delimiter)"}` |\n");
            return sb.ToString();
        }

        var component = PackageTree.AllExecutables(package)
            .Select(e => e.DataFlowTask?.Pipeline)
            .Where(p => p is not null)
            .SelectMany(p => p!.Components)
            .FirstOrDefault(c => c.Name == gap.Location && (c.ExcelSource is not null || c.XmlSource is not null));

        if (component?.XmlSource is { } xml)
        {
            var xsb = new StringBuilder();
            xsb.Append(saveAsLine);
            xsb.Append($"- **XML Source:** `{component.Name}`\n");
            xsb.Append($"- **Row (repeating) element:** `{component.Outputs.FirstOrDefault(o => o.IsErrorOut != true)?.Name ?? "(unknown)"}`\n");
            xsb.Append($"- **Design-time XMLData path:** `{xml.XmlDataPath ?? "(not set)"}`\n\n");
            xsb.Append("### Columns (direct children of the row element), in order\n\n");
            xsb.Append(ColumnTable(component.Outputs.Where(o => o.IsErrorOut != true).SelectMany(o => o.Columns)
                .Select(c => (c.Name, c.DataType, c.Length))));
            return xsb.ToString();
        }

        if (component?.ExcelSource is not { } excel)
            return $"_Could not resolve the Excel Source or XML Source component named '{gap.Location}' -- report this, it is a bug in AiPacketEmitter._\n";

        var esb = new StringBuilder();
        esb.Append(saveAsLine);
        esb.Append($"- **Excel Source:** `{component.Name}`\n");
        esb.Append($"- **Worksheet:** `{(excel.AccessMode is null or 0 ? excel.OpenRowset : "(see SqlCommand)") ?? "(not set)"}`\n");
        if (excel.AccessMode == 2) esb.Append($"- **SQL command:** `{excel.SqlCommand}`\n");
        esb.Append("\n### Columns, in order\n\n");
        esb.Append(ColumnTable(component.Outputs.Where(o => o.IsErrorOut != true).SelectMany(o => o.Columns)
            .Select(c => (c.Name, c.DataType, c.Length))));
        return esb.ToString();
    }

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
        sb.Append("The gap above is for this WHOLE component -- return every one of these columns together from the one combined method.\n\n");
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

    /// <summary>Finds the nearest ForEach Loop container (if any) whose descendant Data Flow Task
    /// contains the component identified by <paramref name="componentRefId"/> -- the "ForEach
    /// Data Flow Loop" test-oracle shape's own lookup. <see cref="PackageTree.AllExecutables"/>
    /// flattens the whole tree with no parent link, which is fine for every OTHER lookup in this
    /// file (they only ever need the one matching node), but this one genuinely needs the
    /// ANCESTOR relationship, so it walks the tree itself instead, tracking the nearest
    /// ForEachLoop-payload executable seen on the way down.</summary>
    private static (ExecutableSpec Loop, ExecutableSpec DataFlowExecutable, PipelineComponentSpec Component)? FindForEachDataFlowLoop(
        PackageSpec package, string? componentRefId)
    {
        if (componentRefId is null) return null;
        return Walk(package.Executables, null);

        (ExecutableSpec, ExecutableSpec, PipelineComponentSpec)? Walk(List<ExecutableSpec> executables, ExecutableSpec? nearestLoop)
        {
            foreach (var ex in executables)
            {
                var loopHere = ex.ForEachLoop is not null ? ex : nearestLoop;
                if (loopHere is not null && ex.DataFlowTask?.Pipeline is { } pipeline)
                {
                    var component = pipeline.Components.FirstOrDefault(c => c.RefId == componentRefId);
                    if (component is not null) return (loopHere, ex, component);
                }
                var found = Walk(ex.Children, loopHere);
                if (found is not null) return found;
            }
            return null;
        }
    }
}
