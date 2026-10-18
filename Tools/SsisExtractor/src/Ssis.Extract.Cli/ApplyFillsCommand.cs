using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ssis.Extract.Codegen;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Serialization;

namespace Ssis.Extract.Cli;

/// <summary>
/// <c>ssisx apply-fills</c> -- copies hand-maintained Tier-2 fills into the generated projects
/// and reports exactly what is still outstanding.
///
/// A "fill" is a second part of a generated <c>partial</c> transform class, implementing the
/// combined seam <c>ssisx generate --seams</c> emitted for one Script Component -- one method,
/// named after the component itself (e.g. <c>SCR_CleanseCustomerRow</c>), returning ALL of that
/// component's own produced columns together in one small record (not one independent
/// <c>Fill_&lt;Column&gt;</c> seam per column, a retired earlier convention -- see
/// <c>TransformEmitter.ScriptComponentGroup</c>'s own doc comment for why one seam per component,
/// not per column, is the correct unit of work). It is human work product -- authored by a person,
/// usually with AI help from the SCRIPT-COLUMN work packet -- so this command only ever READS
/// <c>fills/</c>, never writes to it, exactly like <c>conformance</c>'s claims files. Fills are copied INTO
/// <c>generate/&lt;Package&gt;/Fills/</c> (their own folder, not mixed in with <c>Mapping/</c>) so a
/// reviewer can tell at a glance which files in the project are not machine-generated.
///
/// Validation is the real point, not the copy. A fill implementing a <c>Fill_*</c> that no gap
/// asked for is reported as ORPHANED and deliberately NOT copied -- an orphan usually means the
/// package changed and a seam disappeared, and copying it anyway would only produce a confusing
/// "no defining declaration" compile error instead of a clear message here. Seams with no fill at
/// all are reported too: they are the CS8795 errors the next build will produce.
///
/// <b>Provenance and staleness (chunk 4).</b> A fill MAY carry a one-line comment immediately
/// above the seam it answers:
/// <code>// ssisx-fill: GapId=... Author=... Date=... EvidenceSha256=...</code>
/// <c>EvidenceSha256</c> is copied from the work packet at authoring time -- it freezes what the
/// fill was actually written against. If the package's CURRENT evidence hash (from
/// <c>gaps.json</c>, recomputed fresh every run) no longer matches, the .dtsx changed underneath
/// the translation and the fill is reported <see cref="FillStatus.Stale"/> and NOT copied, the
/// same "decided against evidence that has since moved" refusal <c>GapDecisions</c> already
/// applies to Tier-1 decisions. A fill with no such comment is not refused -- most fills predate
/// this feature -- it is copied and reported <see cref="FillStatus.Unattributed"/>, which is
/// itself useful: it is what tells a reviewer where the practice has not caught on yet.
///
/// This command deliberately does NOT inject a synthetic header into an unattributed fill's own
/// COPY. It would be easy to fabricate one from `gaps.json`'s current hash, but that value was
/// never actually checked against by a human, and stamping it in would make an unattributed fill
/// LOOK reviewed rather than actually being reviewed -- the exact failure this whole design exists
/// to prevent. The full, honest picture instead goes into <c>fills-applied.json</c>, written fresh
/// every run: one row per seam, its status, and (when present) who wrote it, when, and against
/// which hash.
///
/// <b>Linking a fill to the gate-1 conformance obligation it answers.</b> A Script Task/Component
/// gap and its own gate-1 <c>ScriptCode</c> conformance rule (<c>ssisx conformance</c>) are about
/// the exact same real-world obligation -- "read this script and port it deliberately" -- but they
/// live in two commands that share no code and, until now, no data either. `GapSpec` now carries
/// the same `EvidenceRefId` <c>ConformanceRulesBuilder</c> uses to build that rule's own `RuleId`
/// (<c>ConformanceRulesBuilder.ScriptCodeRuleId</c>), so once every seam belonging to one Script
/// Task/Component is genuinely filled, this command can name the exact <c>RuleId</c> that
/// translation answers and -- when a claims file already exists -- say what it currently claims.
/// **It never writes to the claims file.** Only a human puts a `ConfirmedBy`-equivalent sign-off on
/// a conformance claim; this command's whole job here is making the connection VISIBLE, the same
/// division of labor as everywhere else in this feature.
/// </summary>
internal static class ApplyFillsCommand
{
    /// <summary>Matches a provenance comment, e.g.
    /// <c>// ssisx-fill: GapId=SCRIPT-COLUMN:Package:X.Y Author=me Date=2026-08-31 EvidenceSha256=abc123</c>.
    /// Whitespace-delimited <c>Key=Value</c> tokens rather than JSON or a stricter grammar -- this
    /// is a hand-typed annotation copied out of a work packet, and the values involved (a GapId, an
    /// email/username, an ISO date, a hex hash) never contain spaces.</summary>
    private static readonly Regex ProvenanceCommentPattern =
        new(@"^\s*//\s*ssisx-fill:\s*(?<fields>.+)$", RegexOptions.Compiled);

    private sealed record FillProvenance(string? GapId, string? Author, string? Date, string? EvidenceSha256);

    /// <summary>Matches an implementing part of a Script-Component combined seam, e.g.
    /// <c>private partial SCR_CleanseCustomerRowResult SCR_CleanseCustomerRow(Row row, in RowContext ctx) =&gt; ...</c>.
    /// Deliberately loose about the return type/modifier order (the compiler is the authority on
    /// whether the part actually matches its declaration) AND deliberately generic on the method
    /// NAME -- unlike the retired <c>Fill_&lt;Column&gt;</c> convention, a combined seam is named
    /// after the Script Component's own <c>SanitizeIdentifier</c>'d name, which can be anything.
    /// <c>RunScriptAsync</c> is excluded explicitly wherever this pattern is matched: that name is
    /// permanently reserved for a Script TASK's own seam (matched by its own, separate pass below,
    /// keyed by class name, unaffected by this pattern at all) -- a Script Component's own name can
    /// never legitimately collide with it, so excluding it here is what stops a Script Task fill
    /// from being double-counted (once correctly by the class-keyed pass, once as a spurious
    /// unresolved "orphan" picked up by this broader one).</summary>
    private static readonly Regex ScriptComponentSeamPattern =
        new(@"partial\s+(?:async\s+)?[^\s(]+\s+(\w+)\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// A Script Task fill is matched by the CLASS it is a part of, not by its method name -- every
    /// Script Task seam is called <c>RunScriptAsync</c>, so a package with two of them
    /// (RBC_Demo_ETL's own Package has exactly two) would otherwise have a single fill silently
    /// appear to answer both gaps, and one task's logic would go missing with nothing reported.
    /// </summary>
    private static readonly Regex ScriptTaskFillClassPattern =
        new(@"partial\s+class\s+(\w+)", RegexOptions.Compiled);

    /// <summary><c>async</c> is optional in both patterns, and it is not a hypothetical: the very
    /// first real Script Task fill written against this seam was
    /// <c>private partial async Task RunScriptAsync(...)</c>, which a pattern expecting exactly one
    /// token between <c>partial</c> and the method name silently failed to recognize -- reported
    /// (correctly) as an orphan, which is how the omission surfaced at all.</summary>
    private static readonly Regex ScriptTaskSeamPattern =
        new(@"partial\s+(?:async\s+)?[^\s(]+\s+RunScriptAsync\s*\(", RegexOptions.Compiled);

    public static int Run(string[] args)
    {
        string? outDir = null;
        string? fillsDir = null;
        string? claimsDir = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out": outDir = RequireValue(args, ref i, "--out"); break;
                case "--fills": fillsDir = RequireValue(args, ref i, "--fills"); break;
                case "--claims": claimsDir = RequireValue(args, ref i, "--claims"); break;
                default:
                    Console.Error.WriteLine($"error: unknown option '{args[i]}'. Run 'ssisx --help'.");
                    return 2;
            }
        }

        if (outDir is null)
        {
            Console.Error.WriteLine("error: apply-fills requires --out <dir> (the same --out 'ssisx generate' wrote).");
            return 2;
        }

        outDir = Path.GetFullPath(outDir);

        // A durable, step-by-step trace of every decision this run makes -- see RunLog's own
        // doc comment. Every `return` below restores Console.Out/Error via this `using`.
        using var runLog = RunLog.Start(Path.Combine(outDir, "apply-fills.log"));

        var gapsPath = Path.Combine(outDir, "gaps.json");
        if (!File.Exists(gapsPath))
        {
            Console.Error.WriteLine($"error: {gapsPath} not found -- run 'ssisx generate --out {outDir} --seams' first.");
            return 2;
        }

        List<GapSpec>? gaps;
        try
        {
            gaps = JsonSerializer.Deserialize<List<GapSpec>>(File.ReadAllText(gapsPath));
        }
        catch (JsonException ex)
        {
            // A malformed index is a hard error, not something to work around: silently treating
            // every seam as unfilled would be indistinguishable from a genuinely empty gap list.
            Console.Error.WriteLine($"error: {gapsPath} is not valid gaps.json ({ex.Message}).");
            return 2;
        }

        if (gaps is null)
        {
            Console.Error.WriteLine($"error: {gapsPath} deserialized to nothing.");
            return 2;
        }

        // Expected seams, per package: one per ScriptComponentColumn gap (one gap per COMPONENT
        // since the combined-seam round -- see TransformEmitter.ScriptComponentGroup). GapSpec.
        // Location is "{Entity}.{SanitizedComponentName}", and TransformEmitter names the seam
        // method IDENTICALLY to that same sanitized name -- so the expected seam name is read
        // directly off Location's own tail, never synthesized (unlike the retired "Fill_" + column
        // convention, which assumed a naming rule rather than reading back what was emitted).
        //
        // TWO maps, not one -- a real, confirmed bug this closes. A package with more than one
        // Script Component left at SSIS's own unrenamed default display name ("Script Component")
        // produces more than one gap whose seam method name is IDENTICAL ("ScriptComponent"). The
        // bare-method-keyed map alone used to let only the LAST such gap survive, silently
        // checking every OTHER same-named fill against the wrong gap's evidence hash (reported
        // Stale despite being correct). `expectedPrecise`, keyed by (the generated TRANSFORM CLASS
        // name read off the file's own `partial class` declaration, seam method name), disambiguates
        // correctly whenever GapSpec.TransformClassName is present (every gap from a current
        // `ssisx generate` run). `expected` (bare method name -> every gap sharing it) is kept as
        // the fallback for an OLDER gaps.json with no TransformClassName recorded -- used only when
        // there is exactly ONE candidate for that method name; a genuine ambiguity with no precise
        // match available is reported explicitly rather than silently guessing (see below).
        var expected = new Dictionary<string, Dictionary<string, List<GapSpec>>>(StringComparer.Ordinal);
        var expectedPrecise = new Dictionary<string, Dictionary<(string ClassName, string Method), GapSpec>>(StringComparer.Ordinal);
        foreach (var gap in gaps.Where(g => g.Kind == GapKind.ScriptComponentColumn))
        {
            var seamName = gap.Location[(gap.Location.LastIndexOf('.') + 1)..];
            if (seamName.Length == 0) continue;
            if (!expected.TryGetValue(gap.Package, out var byMethod))
                expected[gap.Package] = byMethod = new Dictionary<string, List<GapSpec>>(StringComparer.Ordinal);
            if (!byMethod.TryGetValue(seamName, out var candidates))
                byMethod[seamName] = candidates = [];
            candidates.Add(gap);

            if (gap.TransformClassName is { Length: > 0 } className)
            {
                if (!expectedPrecise.TryGetValue(gap.Package, out var byClassAndMethod))
                    expectedPrecise[gap.Package] = byClassAndMethod = new Dictionary<(string, string), GapSpec>();
                byClassAndMethod[(className, seamName)] = gap;
            }
        }

        // Expected Script Task seams, per package, keyed by the generated partial CLASS name.
        // GapSpec.Location is "{TaskName}.ScriptTask" and ScriptTaskEmitter derives its class name
        // from that same task name, so both sides agree without parsing the gap's own prose.
        var expectedScriptTasks = new Dictionary<string, Dictionary<string, GapSpec>>(StringComparer.Ordinal);
        foreach (var gap in gaps.Where(g => g.Kind == GapKind.ScriptTask
                                         && g.Location.EndsWith(".ScriptTask", StringComparison.Ordinal)))
        {
            var taskName = gap.Location[..^".ScriptTask".Length];
            if (taskName.Length == 0) continue;
            if (!expectedScriptTasks.TryGetValue(gap.Package, out var byClass))
                expectedScriptTasks[gap.Package] = byClass = new Dictionary<string, GapSpec>(StringComparer.Ordinal);
            byClass[ScriptTaskEmitter.ClassName(taskName)] = gap;
        }

        // Expected TEST-ORACLE fills, per package, keyed by GapId -- there is no seam name to
        // match a whole new test FILE against (unlike ScriptComponentColumn/ScriptTask, this gap
        // kind has nothing already generated to splice into), so its own `// ssisx-fill:` comment
        // is the ONLY way to attribute one at all. See <c>Tests/*.cs</c> handling below.
        var expectedTestOracles = new Dictionary<string, Dictionary<string, GapSpec>>(StringComparer.Ordinal);
        foreach (var gap in gaps.Where(g => g.Kind == GapKind.TestOracle))
        {
            if (!expectedTestOracles.TryGetValue(gap.Package, out var byGapId))
                expectedTestOracles[gap.Package] = byGapId = new Dictionary<string, GapSpec>(StringComparer.Ordinal);
            byGapId[gap.GapId] = gap;
        }

        // Expected LOCAL-DATA fills, per package, keyed by the exact TestData/ file name a fill
        // must use (GapSpec.ExpectedFileName -- see its own doc comment for why this is NOT the
        // gap's Location/FileSourceKey). A gap with no derivable file name (an expression-driven
        // connection manager with no design-time default path) is skipped here -- there is no name
        // to match a fill against, so it can never be auto-applied by this command at all.
        //
        // Phase 4.6 (P5/a+b) of the independent-review fix plan, two real bugs fixed together:
        // (a) this used to be keyed OrdinalIgnoreCase while the "still open" check below compared
        // Ordinal, so a fill saved as e.g. "customers.CSV" against an expected "Customers.csv"
        // matched here (silently applied, wrong case preserved on disk) but then ALSO reported as
        // "still open" -- both true at once, neither trustworthy. Now Ordinal throughout, with a
        // dedicated case-mismatch diagnosis at apply time instead of a silent case-insensitive
        // match. (b) the value used to be a single GapSpec, so two-or-more gaps legitimately
        // sharing one expected file name (a raw source read by more than one flow) silently
        // collapsed to whichever gap was enumerated last -- the other gap(s) never got marked
        // applied by the one file that in fact answers all of them.
        var expectedLocalData = new Dictionary<string, Dictionary<string, List<GapSpec>>>(StringComparer.Ordinal);
        foreach (var gap in gaps.Where(g => g.Kind == GapKind.LocalFileSourceData && g.ExpectedFileName is { Length: > 0 }))
        {
            if (!expectedLocalData.TryGetValue(gap.Package, out var byFileName))
                expectedLocalData[gap.Package] = byFileName = new Dictionary<string, List<GapSpec>>(StringComparer.Ordinal);
            if (!byFileName.TryGetValue(gap.ExpectedFileName!, out var gapsForFile))
                byFileName[gap.ExpectedFileName!] = gapsForFile = [];
            gapsForFile.Add(gap);
        }

        // Phase 1.5 (R6): an EXPLICITLY given --fills that doesn't exist is a typo -- a hard
        // usage error (exit 2), not a silent "nothing to apply" (exit 3 with no explanation at
        // all, as it used to be). The default location (no --fills given) is deliberately NOT
        // held to this: it's entirely normal for fills/ to not exist yet on a fresh --out.
        if (fillsDir is not null && !Directory.Exists(fillsDir))
        {
            Console.Error.WriteLine($"error: --fills '{fillsDir}' does not exist.");
            return ExitCodes.UsageOrInputError;
        }
        fillsDir = Path.GetFullPath(fillsDir ?? Path.Combine(outDir, "fills"));
        // Same default `ssisx conformance` itself uses -- read-only here, and silently absent
        // (never an error) when nobody has run `conformance` for this --out at all.
        claimsDir = Path.GetFullPath(claimsDir ?? Path.Combine(outDir, "metadata", "conformance", "claims"));
        var generateDir = Path.Combine(outDir, "generate");

        var applied = new List<string>();
        var orphanedFiles = new List<string>();
        // Phase 4.6 (P5/d): a file with NO recognizable partial method/class at all (a missing
        // `partial` keyword, a renamed seam) is a genuinely different problem from a file that
        // DOES declare a real seam but one no current gap asks for -- the latter alone is what
        // "implements a seam no gap asked for" describes. Conflating the two under one label
        // used to tell a human whose fill has a typo that their (nonexistent) seam is orphaned,
        // when the real problem is that apply-fills never recognized it as a seam at all.
        var noSeamFiles = new List<string>();
        var collidingFiles = new List<string>();
        var orphanedMethods = new List<string>();
        var staleSeams = new List<string>();
        var unknownPackages = new List<string>();
        // Keyed by GapId, NOT by the bare seam/method name -- a real, confirmed bug: two or more
        // Script Components left at SSIS's own unrenamed default display name ("Script Component")
        // produce DIFFERENT gaps whose seam method name is IDENTICAL ("ScriptComponent"). Tracking
        // "is this seam filled/stale" by that bare string meant filling just ONE of them marked
        // every other same-named, still-unfilled gap as already filled too (and the mirror failure
        // for staleness) -- GapId is always unique regardless of how many components share a name.
        var filledMethods = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var staleMethods = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var fillRecords = new List<FillRecordSpec>();

        // TEST-ORACLE/LOCAL-DATA (Docs/Generated-Tests-Plan.md phase 3) -- tracked separately from
        // the seam lists above: neither kind is BLOCKING (no CS8795 waits on either), so a gap left
        // open here is informational, not a build failure, and never affects the exit code the way
        // stillUnfilled/staleSeams do. An orphan still does, though -- applying a fill that answers
        // nothing is worth flagging loudly regardless of kind.
        var testOracleApplied = new List<string>();
        var testOracleOrphaned = new List<string>();
        var testOracleStale = new List<string>();
        var testOracleFilled = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var testOracleStaleGapIds = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var localDataApplied = new List<string>();
        var localDataOrphaned = new List<string>();
        var localDataFilled = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        Console.WriteLine($"scanning fills under {fillsDir}...");
        if (Directory.Exists(fillsDir))
        {
            foreach (var packageDir in Directory.EnumerateDirectories(fillsDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var packageName = Path.GetFileName(packageDir);
                if (!Directory.Exists(Path.Combine(generateDir, packageName)))
                {
                    Console.WriteLine($"  {packageName}: no matching folder under {generateDir} -- treating as unknown package");
                    unknownPackages.Add(packageName);
                    continue;
                }
                Console.WriteLine($"  {packageName}:");

                // Recursive -- NOT top-level only. A fill placed one level deeper than the
                // documented flat convention (e.g. mirroring the generated project's own
                // Mapping\ folder) used to be completely invisible here: not applied, not
                // reported as orphaned, nothing. `Tests\` is excluded because it has its own
                // separate TEST-ORACLE handling below with different provenance rules -- a file
                // there must not also be considered by this seam scan.
                var testsSubdir = Path.Combine(packageDir, "Tests") + Path.DirectorySeparatorChar;
                var seamFiles = Directory.EnumerateFiles(packageDir, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !f.StartsWith(testsSubdir, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .ToList();

                // Phase 4.6 (P5/e): every seam fill is copied into ONE FLAT `Fills/` directory,
                // keyed only by its own bare file name -- so two fills that differ only by their
                // NESTED source path (e.g. mirroring the generated project's own Mapping\ and
                // Sql\ folders, both containing a file named the same) used to silently overwrite
                // each other, with whichever sorted last winning and the other's own work simply
                // vanishing with no error at all. Detected up front, for the whole package, before
                // anything is copied -- a collision blocks every one of the colliding files, not
                // just all-but-one of them, since there's no way to know which one a human meant.
                foreach (var group in seamFiles.GroupBy(f => Path.GetFileName(f), StringComparer.Ordinal))
                {
                    if (group.Count() <= 1) continue;
                    var relativePaths = group.Select(f => Path.GetRelativePath(packageDir, f));
                    Console.WriteLine($"    {group.Key}: COLLISION -- {group.Count()} fills under different subfolders flatten to the same name ({string.Join(", ", relativePaths)}); NONE copied. Rename one or the other.");
                    foreach (var f in group)
                    {
                        collidingFiles.Add($"{packageName}/{Path.GetRelativePath(packageDir, f)}");
                        fillRecords.Add(new FillRecordSpec
                        {
                            Package = packageName, FileName = Path.GetRelativePath(packageDir, f), Seam = "(name collision)",
                            GapId = null, Status = FillStatus.Orphaned,
                        });
                    }
                }
                var collidingNames = new HashSet<string>(
                    seamFiles.GroupBy(f => Path.GetFileName(f), StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key),
                    StringComparer.Ordinal);

                foreach (var fillFile in seamFiles.Where(f => !collidingNames.Contains(Path.GetFileName(f))))
                {
                    var fileName = Path.GetFileName(fillFile);
                    var relativePath = Path.GetRelativePath(packageDir, fillFile);
                    Console.WriteLine($"    {relativePath}");
                    var content = File.ReadAllText(fillFile);
                    var known = expected.TryGetValue(packageName, out var byMethod) ? byMethod : [];
                    var knownPrecise = expectedPrecise.TryGetValue(packageName, out var byClassAndMethod) ? byClassAndMethod : [];
                    var knownClasses = expectedScriptTasks.TryGetValue(packageName, out var byClass) ? byClass : [];

                    // The enclosing `partial class` this fill's own seam(s) live on -- read directly
                    // off the file (same generic regex the Script Task pass already uses; it is not
                    // specific to RunScriptAsync), so a Script Component seam can be matched
                    // precisely by (class, method) instead of by method name alone. Null when the
                    // file has no recognizable class at all, which only matters for the bare-method
                    // fallback below.
                    var fileClassMatch = ScriptTaskFillClassPattern.Match(content);
                    var fileClassName = fileClassMatch.Success ? fileClassMatch.Groups[1].Value : null;

                    // One row per seam actually found in the file, each carrying its own
                    // provenance comment (if any) -- unlike the pre-chunk-4 version, this is no
                    // longer a plain distinct-method-names list, because staleness is a per-seam
                    // fact, not a per-file one.
                    var seams = new List<(string Seam, GapSpec? Gap)>();
                    foreach (Match m in ScriptComponentSeamPattern.Matches(content))
                    {
                        var method = m.Groups[1].Value;
                        if (method == "RunScriptAsync") continue; // reserved for the Script Task pass below
                        if (seams.Any(s => s.Seam == method)) continue;

                        GapSpec? gap = null;
                        if (fileClassName is not null && knownPrecise.TryGetValue((fileClassName, method), out var preciseGap))
                        {
                            gap = preciseGap;
                        }
                        else if (known.TryGetValue(method, out var candidates))
                        {
                            if (candidates.Count == 1)
                            {
                                // Safe fallback: no TransformClassName recorded (an older gaps.json)
                                // or no class could be read from the file, but only ONE gap in the
                                // whole package shares this seam method name, so there is nothing
                                // to disambiguate.
                                gap = candidates[0];
                            }
                            else
                            {
                                // The real, confirmed bug this round closes: more than one gap
                                // shares this bare seam method name (two+ Script Components left at
                                // SSIS's own default display name) and no precise (class, method)
                                // match resolved it -- never silently pick one. Regenerating with a
                                // current ssisx build records TransformClassName on every gap, which
                                // is what the precise path above needs.
                                Console.WriteLine(
                                    $"      {method}: AMBIGUOUS -- {candidates.Count} current gaps share this seam method name and this file's own class " +
                                    $"({fileClassName ?? "(not found)"}) did not precisely match any of them; " +
                                    $"re-run 'ssisx generate' with a current build to record which gap this file answers, then retry.");
                            }
                        }
                        seams.Add((method, gap));
                    }

                    // The Script Task half: recognized by class name, and only when the file
                    // genuinely supplies the seam -- a partial part that declares the class but no
                    // RunScriptAsync answers nothing and should not count as a fill.
                    if (ScriptTaskSeamPattern.IsMatch(content))
                    {
                        foreach (Match m in ScriptTaskFillClassPattern.Matches(content))
                        {
                            var cls = m.Groups[1].Value;
                            var seam = $"{cls}.RunScriptAsync";
                            if (seams.Any(s => s.Seam == seam)) continue;
                            knownClasses.TryGetValue(cls, out var gap);
                            seams.Add((seam, gap));
                        }
                    }

                    if (seams.Count == 0)
                    {
                        Console.WriteLine(
                            $"      no 'partial' method/class found in this file -- check it still declares " +
                            $"'partial <ReturnType> <Method>(...)' (a Script Component seam) or 'partial class " +
                            $"<Name>' plus 'partial ... RunScriptAsync(...)' (a Script Task seam), exactly as the " +
                            $"work packet's own seam signature specifies. A missing 'partial' keyword, or a " +
                            $"renamed method/class, both look like this.");
                    }

                    var recognized = new List<string>();
                    var recognizedGapIds = new List<string>();
                    var staleInThisFile = new List<string>();
                    foreach (var (seam, gap) in seams)
                    {
                        if (gap is null)
                        {
                            Console.WriteLine($"      {seam}: ORPHANED -- no current gap asks for this seam");
                            orphanedMethods.Add($"{packageName}/{fileName}: {seam}");
                            fillRecords.Add(new FillRecordSpec
                            {
                                Package = packageName, FileName = fileName, Seam = seam,
                                GapId = null, Status = FillStatus.Orphaned,
                            });
                            continue;
                        }

                        var provenance = ExtractProvenance(content, seam);

                        if (provenance?.EvidenceSha256 is { Length: > 0 } recorded
                            && gap.EvidenceSha256 is { Length: > 0 } current
                            && !string.Equals(recorded, current, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"      {seam}: STALE ({gap.GapId}) -- recorded {Short(recorded)}... != current {Short(current)}...");
                            staleSeams.Add(
                                $"{gap.GapId}  ->  {seam}  (written against {Short(recorded)}..., package now hashes {Short(current)}...)");
                            fillRecords.Add(new FillRecordSpec
                            {
                                Package = packageName, FileName = fileName, Seam = seam, GapId = gap.GapId,
                                Status = FillStatus.Stale, Author = provenance.Author, Date = provenance.Date,
                                RecordedEvidenceSha256 = recorded, CurrentEvidenceSha256 = current,
                                ConformanceRuleId = ConformanceRuleId(gap),
                            });
                            if (!staleMethods.TryGetValue(packageName, out var staleSet))
                                staleMethods[packageName] = staleSet = new HashSet<string>(StringComparer.Ordinal);
                            staleSet.Add(gap.GapId); // keyed by GapId -- see filledMethods/staleMethods' own doc comment above
                            staleInThisFile.Add(seam);
                            continue;
                        }

                        Console.WriteLine($"      {seam}: {(provenance is null ? "UNATTRIBUTED" : "APPLIED")} ({gap.GapId})");
                        recognized.Add(seam);
                        recognizedGapIds.Add(gap.GapId);
                        fillRecords.Add(new FillRecordSpec
                        {
                            Package = packageName, FileName = fileName, Seam = seam, GapId = gap.GapId,
                            Status = provenance is null ? FillStatus.Unattributed : FillStatus.Applied,
                            Author = provenance?.Author, Date = provenance?.Date,
                            RecordedEvidenceSha256 = provenance?.EvidenceSha256, CurrentEvidenceSha256 = gap.EvidenceSha256,
                            ConformanceRuleId = ConformanceRuleId(gap),
                        });
                    }

                    // A file is copied WHOLE or not at all -- there is no surgical way to strip just
                    // the stale seam's own method body out of an otherwise-fine file without risking
                    // a subtly wrong edit, and copying the whole file anyway would ship the stale
                    // seam's still-present, still-working-looking implementation straight into the
                    // build, exactly the outcome FillStatus.Stale exists to prevent. So ANY stale
                    // seam blocks the whole file -- its other, genuinely fine seams stay reported as
                    // still unfilled too, which is the honest state of the actual generated output.
                    if (recognized.Count == 0 || staleInThisFile.Count > 0)
                    {
                        if (staleInThisFile.Count == 0 && seams.Count == 0)
                        {
                            Console.WriteLine($"      -> file NOT copied (no seam declaration recognized at all)");
                            noSeamFiles.Add($"{packageName}/{fileName}");
                        }
                        else if (staleInThisFile.Count == 0 && seams.All(s => s.Gap is null))
                        {
                            Console.WriteLine($"      -> file NOT copied (nothing in it matched a current gap)");
                            orphanedFiles.Add($"{packageName}/{fileName}");
                        }
                        else
                        {
                            Console.WriteLine($"      -> file NOT copied (a stale seam blocks the whole file)");
                        }
                        continue;
                    }

                    var targetDir = Path.Combine(generateDir, packageName, "Fills");
                    Directory.CreateDirectory(targetDir);
                    File.Copy(fillFile, Path.Combine(targetDir, fileName), overwrite: true);
                    Console.WriteLine($"      -> copied to {targetDir}\\{fileName}");
                    applied.Add($"{packageName}/Fills/{fileName} ({string.Join(", ", recognized)})");

                    if (!filledMethods.TryGetValue(packageName, out var set))
                        filledMethods[packageName] = set = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var gapId in recognizedGapIds) set.Add(gapId); // keyed by GapId, not the bare seam name -- see the doc comment above
                }

                // TEST-ORACLE: a whole test file per gap, attributed ONLY by its own
                // `// ssisx-fill:` comment -- there is no seam name to fall back on the way
                // ScriptComponentColumn/ScriptTask fills have, so a file with no comment, or one
                // naming a GapId this package does not currently have, is Orphaned rather than
                // Unattributed. That is a real, deliberate asymmetry from the seam mechanism above,
                // not an oversight -- see TestOracleResponseFormat's own doc comment in AiPacketEmitter.
                var testsDir = Path.Combine(packageDir, "Tests");
                var knownOracles = expectedTestOracles.TryGetValue(packageName, out var byGapId) ? byGapId : [];
                if (Directory.Exists(testsDir))
                {
                    foreach (var fillFile in Directory.EnumerateFiles(testsDir, "*.cs").OrderBy(f => f, StringComparer.Ordinal))
                    {
                        var fileName = Path.GetFileName(fillFile);
                        Console.WriteLine($"    Tests\\{fileName}");
                        var provenance = ExtractLeadingProvenance(File.ReadAllText(fillFile));
                        var gap = provenance?.GapId is { Length: > 0 } gapId && knownOracles.TryGetValue(gapId, out var g) ? g : null;

                        if (gap is null)
                        {
                            // Phase 4.6 (P5/d): the reason is carried in the SAME string the summary
                            // prints, not a blanket "GapId did not match" suffix that used to
                            // misdescribe the "no comment at all" case as a GapId mismatch.
                            var reason = provenance?.GapId is null
                                ? "no leading '// ssisx-fill: GapId=...' comment found"
                                : $"GapId '{provenance.GapId}' does not match any current TEST-ORACLE gap in this package";
                            Console.WriteLine($"      ORPHANED -- {reason}");
                            testOracleOrphaned.Add($"{packageName}/Tests/{fileName} ({reason})");
                            fillRecords.Add(new FillRecordSpec
                            {
                                Package = packageName, FileName = $"Tests/{fileName}", Seam = "(whole file)",
                                GapId = provenance?.GapId, Status = FillStatus.Orphaned,
                                Author = provenance?.Author, Date = provenance?.Date,
                            });
                            continue;
                        }

                        if (gap.EvidenceSha256 is { Length: > 0 } current
                            && provenance!.EvidenceSha256 is { Length: > 0 } recorded
                            && !string.Equals(recorded, current, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"      STALE ({gap.GapId}) -- recorded {Short(recorded)}... != current {Short(current)}...");
                            testOracleStale.Add($"{gap.GapId}  ->  Tests/{fileName}  (written against {Short(recorded)}..., package now hashes {Short(current)}...)");
                            fillRecords.Add(new FillRecordSpec
                            {
                                Package = packageName, FileName = $"Tests/{fileName}", Seam = "(whole file)", GapId = gap.GapId,
                                Status = FillStatus.Stale, Author = provenance.Author, Date = provenance.Date,
                                RecordedEvidenceSha256 = recorded, CurrentEvidenceSha256 = current,
                            });
                            if (!testOracleStaleGapIds.TryGetValue(packageName, out var staleSet))
                                testOracleStaleGapIds[packageName] = staleSet = new HashSet<string>(StringComparer.Ordinal);
                            staleSet.Add(gap.GapId);
                            continue;
                        }

                        var testsTargetDir = Path.Combine(generateDir, $"{packageName}.Tests", "Fills");
                        Directory.CreateDirectory(testsTargetDir);
                        File.Copy(fillFile, Path.Combine(testsTargetDir, fileName), overwrite: true);
                        Console.WriteLine($"      APPLIED ({gap.GapId}) -> {testsTargetDir}\\{fileName}");
                        testOracleApplied.Add($"{packageName}.Tests/Fills/{fileName} ({gap.GapId})");
                        fillRecords.Add(new FillRecordSpec
                        {
                            Package = packageName, FileName = $"Tests/{fileName}", Seam = "(whole file)", GapId = gap.GapId,
                            Status = FillStatus.Applied, Author = provenance!.Author, Date = provenance.Date,
                            RecordedEvidenceSha256 = provenance.EvidenceSha256, CurrentEvidenceSha256 = gap.EvidenceSha256,
                        });
                        if (!testOracleFilled.TryGetValue(packageName, out var oracleSet))
                            testOracleFilled[packageName] = oracleSet = new HashSet<string>(StringComparer.Ordinal);
                        oracleSet.Add(gap.GapId);
                    }
                }

                // LOCAL-DATA: any file type, matched purely by NAME against the exact TestData/
                // filename its own gap expects (GapSpec.ExpectedFileName) -- a raw data file has no
                // universal comment syntax to carry provenance in, so there is genuinely no
                // staleness check possible here, only Applied/Orphaned. Stated plainly in the work
                // packet's own contract, not silently missing.
                var testDataDir = Path.Combine(packageDir, "TestData");
                var knownLocalData = expectedLocalData.TryGetValue(packageName, out var byFileName) ? byFileName : [];
                if (Directory.Exists(testDataDir))
                {
                    // Phase 5.2 (G1): `svk sampledata --for-gaps` marks a file it auto-closed with
                    // a sidecar `<file>.provenance` -- itself never a data file to apply, so it is
                    // excluded from the scan up front rather than showing up as its own, nonsense
                    // "orphaned" entry.
                    foreach (var fillFile in Directory.EnumerateFiles(testDataDir)
                                 .Where(f => !f.EndsWith(".provenance", StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(f => f, StringComparer.Ordinal))
                    {
                        var fileName = Path.GetFileName(fillFile);
                        var synthetic = File.Exists(fillFile + ".provenance");
                        Console.WriteLine($"    TestData\\{fileName}" + (synthetic ? " (synthetic -- svk sampledata --for-gaps)" : ""));
                        if (!knownLocalData.TryGetValue(fileName, out var gapsForFile))
                        {
                            // Phase 4.6 (P5/a): distinguish "genuinely unknown" from "right file,
                            // wrong case" -- the latter used to match silently (OrdinalIgnoreCase)
                            // and get copied with its own wrong casing preserved, then ALSO show
                            // up under "still open" below (Ordinal there), reporting both Applied
                            // and not-yet-filled for the identical gap. Neither is copied now.
                            var caseMismatch = knownLocalData.Keys
                                .FirstOrDefault(k => string.Equals(k, fileName, StringComparison.OrdinalIgnoreCase));
                            if (caseMismatch is not null)
                            {
                                Console.WriteLine($"      ORPHANED -- file name differs only by CASE from the expected 'TestData\\{caseMismatch}' -- rename it to match exactly; not copied");
                            }
                            else
                            {
                                Console.WriteLine("      ORPHANED -- file name does not match any current gap's own expected TestData\\ name");
                            }
                            localDataOrphaned.Add($"{packageName}/TestData/{fileName}");
                            fillRecords.Add(new FillRecordSpec
                            {
                                Package = packageName, FileName = $"TestData/{fileName}", Seam = "(data file)",
                                GapId = null, Status = FillStatus.Orphaned, Synthetic = synthetic,
                            });
                            continue;
                        }

                        var dataTargetDir = Path.Combine(generateDir, packageName, "TestData");
                        Directory.CreateDirectory(dataTargetDir);
                        File.Copy(fillFile, Path.Combine(dataTargetDir, fileName), overwrite: true);
                        // One file can genuinely answer several gaps at once (Phase 4.6, P5/b) --
                        // e.g. the same raw source read by more than one flow -- so every gap this
                        // file's own name satisfies is recorded, not just the first/last one seen.
                        Console.WriteLine($"      APPLIED ({string.Join(", ", gapsForFile.Select(g => g.GapId))}) -> {dataTargetDir}\\{fileName}");
                        foreach (var gap in gapsForFile)
                        {
                            localDataApplied.Add($"{packageName}/TestData/{fileName} ({gap.GapId})" + (synthetic ? " [synthetic]" : ""));
                            fillRecords.Add(new FillRecordSpec
                            {
                                Package = packageName, FileName = $"TestData/{fileName}", Seam = "(data file)", GapId = gap.GapId,
                                Status = FillStatus.Applied, Synthetic = synthetic,
                            });
                        }
                        if (!localDataFilled.TryGetValue(packageName, out var dataSet))
                            localDataFilled[packageName] = dataSet = new HashSet<string>(StringComparer.Ordinal);
                        dataSet.Add(fileName);
                    }
                }
            }
        }

        var stillUnfilled = new List<string>();
        foreach (var (packageName, byMethod) in expected.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            filledMethods.TryGetValue(packageName, out var filled);
            staleMethods.TryGetValue(packageName, out var stale);
            foreach (var (method, candidates) in byMethod.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                // More than one gap can share this bare seam method name (two+ Script Components
                // left at SSIS's own default display name) -- each is tracked and reported
                // independently by its own GapId, never assumed filled/stale just because another
                // gap with the same method name is.
                foreach (var gap in candidates.OrderBy(g => g.GapId, StringComparer.Ordinal))
                {
                    if (filled?.Contains(gap.GapId) == true) continue;
                    // A stale seam is reported in its own, more informative section above -- it HAS
                    // a fill, just an out-of-date one, which "no fill provided at all" would misstate.
                    if (stale?.Contains(gap.GapId) == true) continue;
                    stillUnfilled.Add($"{gap.GapId}  ->  {method}");
                }
            }
        }

        foreach (var (packageName, byClass) in expectedScriptTasks.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            filledMethods.TryGetValue(packageName, out var filled);
            staleMethods.TryGetValue(packageName, out var stale);
            foreach (var (cls, gap) in byClass.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var seam = $"{cls}.RunScriptAsync";
                if (filled?.Contains(gap.GapId) == true) continue;
                if (stale?.Contains(gap.GapId) == true) continue;
                stillUnfilled.Add($"{gap.GapId}  ->  {seam}");
            }
        }

        // TEST-ORACLE/LOCAL-DATA still open -- informational only (see the tracking lists' own
        // doc comment above for why neither affects the exit code the way stillUnfilled does).
        var testOracleOpen = new List<string>();
        foreach (var (packageName, byGapId) in expectedTestOracles.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            testOracleFilled.TryGetValue(packageName, out var filled);
            testOracleStaleGapIds.TryGetValue(packageName, out var stale);
            foreach (var gap in byGapId.Values.OrderBy(g => g.GapId, StringComparer.Ordinal))
            {
                if (filled?.Contains(gap.GapId) == true) continue;
                // A stale fill is reported in its own, more informative section above -- it HAS a
                // fill, just an out-of-date one, which "still open" would misstate (same
                // discipline as stillUnfilled's own staleMethods exclusion above).
                if (stale?.Contains(gap.GapId) == true) continue;
                testOracleOpen.Add(gap.GapId);
            }
        }

        var localDataOpen = new List<string>();
        foreach (var (packageName, byFileName) in expectedLocalData.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            localDataFilled.TryGetValue(packageName, out var filled);
            foreach (var (fileName, gapsForFile) in byFileName.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (filled?.Contains(fileName) == true) continue;
                foreach (var gap in gapsForFile) localDataOpen.Add($"{gap.GapId}  ->  TestData/{fileName}");
            }
        }

        // Group every EXPECTED seam (not just the ones a fill answered) by the ScriptCode RuleId
        // its own gap points at -- several ScriptComponentColumn gaps can share one RuleId (same
        // Script Component, several columns), and the underlying obligation is "port the whole
        // component", not one per column, so it is only satisfied once every seam in the group has
        // genuinely landed in `generate/` (i.e. is in filledMethods, never stale/orphaned).
        // Each entry is (the seam's readable method name for display, the specific gap's own
        // GapId for the filled-check) -- the method name alone is NOT a safe key here either, for
        // the identical reason filledMethods/staleMethods moved to GapId above: a Script Component
        // seam name can repeat across several different gaps/components.
        var ruleGroups = new Dictionary<(string Package, string RuleId), List<(string Method, string GapId)>>();
        foreach (var (packageName, byMethod) in expected)
            foreach (var (method, candidates) in byMethod)
                foreach (var gap in candidates)
                    if (ConformanceRuleId(gap) is { } ruleId)
                    {
                        var key = (packageName, ruleId);
                        if (!ruleGroups.TryGetValue(key, out var seamsForRule))
                            ruleGroups[key] = seamsForRule = [];
                        seamsForRule.Add((method, gap.GapId));
                    }
        foreach (var (packageName, byClass) in expectedScriptTasks)
            foreach (var (cls, gap) in byClass)
                if (ConformanceRuleId(gap) is { } ruleId)
                {
                    var key = (packageName, ruleId);
                    if (!ruleGroups.TryGetValue(key, out var seamsForRule))
                        ruleGroups[key] = seamsForRule = [];
                    seamsForRule.Add(($"{cls}.RunScriptAsync", gap.GapId));
                }

        var conformanceLinks = new List<string>();
        var claimsCache = new Dictionary<string, List<ImplementationClaimSpec>?>(StringComparer.Ordinal);
        foreach (var ((packageName, ruleId), seamsForRule) in ruleGroups.OrderBy(g => g.Key.Package, StringComparer.Ordinal)
                                                                          .ThenBy(g => g.Key.RuleId, StringComparer.Ordinal))
        {
            filledMethods.TryGetValue(packageName, out var filled);
            if (!seamsForRule.All(s => filled?.Contains(s.GapId) == true)) continue; // partial -- not yet a candidate

            if (!claimsCache.TryGetValue(packageName, out var claims))
                claimsCache[packageName] = claims = ReadClaims(claimsDir, packageName);
            var claim = claims?.FirstOrDefault(c => string.Equals(c.RuleId, ruleId, StringComparison.Ordinal));

            var status = claim?.Status ?? (claims is null ? "(no claims file found)" : "Unclaimed");
            var note = string.Equals(status, "Implemented", StringComparison.OrdinalIgnoreCase)
                ? "already marked Implemented"
                : "update the claims file by hand if this port is trustworthy";
            conformanceLinks.Add(
                $"{packageName}: {ruleId}  --  all {seamsForRule.Count} seam(s) filled ({string.Join(", ", seamsForRule.Select(s => s.Method).OrderBy(s => s, StringComparer.Ordinal))}); claim status: {status} -- {note}");
        }

        var manifestPath = Path.Combine(outDir, "fills-applied.json");
        StableJsonWriter.WriteToFile(
            fillRecords.OrderBy(r => r.Package, StringComparer.Ordinal)
                       .ThenBy(r => r.FileName, StringComparer.Ordinal)
                       .ThenBy(r => r.Seam, StringComparer.Ordinal)
                       .ToList(),
            manifestPath);

        Report(
            fillsDir, manifestPath, applied, stillUnfilled, staleSeams, orphanedFiles, noSeamFiles, collidingFiles, orphanedMethods, unknownPackages, conformanceLinks,
            testOracleApplied, testOracleOpen, testOracleStale, testOracleOrphaned,
            localDataApplied, localDataOpen, localDataOrphaned);

        if (orphanedFiles.Count > 0 || noSeamFiles.Count > 0 || collidingFiles.Count > 0 || orphanedMethods.Count > 0 || unknownPackages.Count > 0
            || testOracleOrphaned.Count > 0 || localDataOrphaned.Count > 0) return 1;
        return stillUnfilled.Count > 0 || staleSeams.Count > 0 || testOracleStale.Count > 0 ? 3 : 0;
    }

    private static string Short(string hash) => hash.Length > 8 ? hash[..8] : hash;

    /// <summary>The gate-1 <c>ScriptCode</c> RuleId this gap's own translation contributes to, or
    /// null when its kind carries no such counterpart (only ScriptTask/ScriptComponentColumn do).</summary>
    private static string? ConformanceRuleId(GapSpec gap) =>
        gap.Kind is GapKind.ScriptTask or GapKind.ScriptComponentColumn && gap.EvidenceRefId is { Length: > 0 } refId
            ? ConformanceRulesBuilder.ScriptCodeRuleId(refId)
            : null;

    /// <summary>
    /// Looks for a <c>// ssisx-fill:</c> comment on the nearest non-blank line ABOVE this seam's
    /// own declaration. Deliberately strict about adjacency (only blank lines may separate the two)
    /// rather than searching the whole file for any header mentioning this seam's name -- a loose
    /// search could attribute one seam's provenance comment to a different, unrelated seam sharing
    /// a similar name.
    /// </summary>
    private static FillProvenance? ExtractProvenance(string content, string seam)
    {
        // The seam's own declaration line -- reuse the same patterns that found it in the first
        // place, so "where is this seam declared" is answered identically everywhere in this file.
        var declaration = seam.EndsWith(".RunScriptAsync", StringComparison.Ordinal)
            ? ScriptTaskFillClassPattern.Matches(content)
                .FirstOrDefault(m => $"{m.Groups[1].Value}.RunScriptAsync" == seam)
            : ScriptComponentSeamPattern.Matches(content)
                .FirstOrDefault(m => m.Groups[1].Value == seam);
        if (declaration is null || !declaration.Success) return null;

        // Find which whole LINE the declaration starts on -- content[..declaration.Index] would
        // include a trailing fragment of that same line (e.g. "    private " before "partial..."),
        // which is neither blank nor a provenance comment and would wrongly stop the search one
        // line too early, on the declaration's own line instead of the one above it.
        var allLines = content.Split('\n');
        var offset = 0;
        var declarationLine = allLines.Length - 1;
        for (var i = 0; i < allLines.Length; i++)
        {
            var lineLength = allLines[i].Length + 1; // +1 for the '\n' Split consumed
            if (offset + lineLength > declaration.Index) { declarationLine = i; break; }
            offset += lineLength;
        }

        for (var i = declarationLine - 1; i >= 0; i--)
        {
            var line = allLines[i].TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            var m = ProvenanceCommentPattern.Match(line);
            return m.Success ? ParseProvenance(m.Groups["fields"].Value) : null;
        }
        return null;
    }

    /// <summary>
    /// A TEST-ORACLE fill's own provenance comment, expected as the FIRST non-blank line of the
    /// whole file (see <c>AiPacketEmitter.TestOracleResponseFormat</c>) -- there is no seam
    /// declaration to search relative to, since this is a brand-new file, not a splice into one
    /// that already exists.
    /// </summary>
    private static FillProvenance? ExtractLeadingProvenance(string content)
    {
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            var m = ProvenanceCommentPattern.Match(line);
            return m.Success ? ParseProvenance(m.Groups["fields"].Value) : null;
        }
        return null;
    }

    private static readonly JsonSerializerOptions ClaimsReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Reads a package's own claims file, read-only, exactly like <c>ConformanceCommand</c> writes
    /// and reads it -- <c>&lt;claimsDir&gt;/&lt;SanitizedPackageName&gt;.claims.json</c>. Returns
    /// null (not empty) when the file is absent, so a caller can tell "nobody has run `ssisx
    /// conformance` for this package yet" apart from "it ran and found nothing to claim".
    /// </summary>
    private static List<ImplementationClaimSpec>? ReadClaims(string claimsDir, string packageName)
    {
        var path = Path.Combine(claimsDir, $"{SanitizeFileName(packageName)}.claims.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<ImplementationClaimSpec>>(File.ReadAllText(path), ClaimsReadOptions) ?? [];
        }
        catch (JsonException)
        {
            // A malformed claims file is `conformance`'s own problem to report; here it just means
            // "nothing to cross-reference", not a reason to fail an otherwise-successful apply-fills.
            return null;
        }
    }

    /// <summary>Must match <c>ConformanceCommand.SanitizeFileName</c> exactly -- both sides need to
    /// agree on the same claims file name without sharing the method.</summary>
    private static string SanitizeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static FillProvenance ParseProvenance(string fields)
    {
        string? gapId = null, author = null, date = null, hash = null;
        foreach (var token in fields.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = token.IndexOf('=');
            if (eq <= 0) continue;
            var value = token[(eq + 1)..];
            switch (token[..eq])
            {
                case "GapId": gapId = value; break;
                case "Author": author = value; break;
                case "Date": date = value; break;
                case "EvidenceSha256": hash = value; break;
            }
        }
        return new FillProvenance(gapId, author, date, hash);
    }

    private static void Report(
        string fillsDir, string manifestPath, List<string> applied, List<string> stillUnfilled,
        List<string> staleSeams, List<string> orphanedFiles, List<string> noSeamFiles, List<string> collidingFiles, List<string> orphanedMethods,
        List<string> unknownPackages, List<string> conformanceLinks,
        List<string> testOracleApplied, List<string> testOracleOpen, List<string> testOracleStale, List<string> testOracleOrphaned,
        List<string> localDataApplied, List<string> localDataOpen, List<string> localDataOrphaned)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"apply-fills: {applied.Count} fill file(s) applied from {fillsDir}");
        foreach (var line in applied) sb.AppendLine($"  + {line}");

        if (staleSeams.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Stale -- the fill's own recorded EvidenceSha256 no longer matches this package, so its");
            sb.AppendLine("WHOLE FILE was NOT copied (a stale seam blocks every other seam in the same file too --");
            sb.AppendLine("there is no safe way to strip out just the stale one). The .dtsx changed underneath this");
            sb.AppendLine("translation; re-read the current work packet and re-port rather than trusting the old one.");
            foreach (var line in staleSeams) sb.AppendLine($"  ~ {line}");
        }

        if (stillUnfilled.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{stillUnfilled.Count} seam(s) still unfilled -- the next build will report one CS8795 each:");
            foreach (var line in stillUnfilled) sb.AppendLine($"  ! {line}");
        }

        if (orphanedFiles.Count > 0 || orphanedMethods.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Orphaned -- implements a seam no gap asked for, so NOT copied. Usually the package");
            sb.AppendLine("changed and that seam is gone; re-read the current work packet before reusing it.");
            foreach (var line in orphanedMethods) sb.AppendLine($"  ? {line}");
            foreach (var line in orphanedFiles) sb.AppendLine($"  ? {line} (skipped entirely -- its own seam(s) don't match a current gap)");
        }

        // Phase 4.6 (P5/e): two fills under different subfolders that flatten to the SAME bare
        // file name in the one flat Fills/ target -- neither is copied, since there's no way to
        // know which one a human meant to keep.
        if (collidingFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Name collision -- two or more fills flatten to the same Fills\\ file name, so NONE of");
            sb.AppendLine("them were copied. Rename one or the other so they no longer collide.");
            foreach (var line in collidingFiles) sb.AppendLine($"  ! {line}");
        }

        // Phase 4.6 (P5/d): kept separate from "Orphaned" above -- this file never declared a
        // recognizable seam at all (a missing `partial` keyword, a renamed method/class), which
        // is a different mistake than implementing a real seam nothing asks for any more.
        if (noSeamFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("No seam found -- this file has no 'partial <ReturnType> <Method>(...)' or 'partial class");
            sb.AppendLine("<Name>' declaration ssisx recognizes as a seam, so NOT copied. Check for a missing");
            sb.AppendLine("'partial' keyword or a renamed method/class against the work packet's own signature.");
            foreach (var line in noSeamFiles) sb.AppendLine($"  ? {line} (skipped entirely -- no seam declaration recognized)");
        }

        if (unknownPackages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Fills for a package that was not generated into --out:");
            foreach (var line in unknownPackages) sb.AppendLine($"  ? {line}");
        }

        if (conformanceLinks.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Gate-1 conformance rules now fully answered by these fills (never written here --");
            sb.AppendLine("update the claims file yourself if you agree the port is trustworthy):");
            foreach (var line in conformanceLinks) sb.AppendLine($"  = {line}");
        }

        // TEST-ORACLE/LOCAL-DATA -- a separate section, deliberately: neither is blocking, so this
        // never contributes to the "no Tier-2 seams" fallback message below, which is specifically
        // about the CS8795-producing seam mechanism.
        if (testOracleApplied.Count > 0 || testOracleOpen.Count > 0 || testOracleStale.Count > 0 || testOracleOrphaned.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"TEST-ORACLE: {testOracleApplied.Count} applied, {testOracleOpen.Count} still open (informational, not a build blocker):");
            foreach (var line in testOracleApplied) sb.AppendLine($"  + {line}");
            foreach (var line in testOracleStale) sb.AppendLine($"  ~ {line}");
            foreach (var line in testOracleOpen) sb.AppendLine($"  . {line}");
            foreach (var line in testOracleOrphaned) sb.AppendLine($"  ? {line}");
        }

        if (localDataApplied.Count > 0 || localDataOpen.Count > 0 || localDataOrphaned.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"LOCAL-DATA: {localDataApplied.Count} applied, {localDataOpen.Count} still open (informational, not a build blocker; no staleness check possible for a raw data file):");
            foreach (var line in localDataApplied) sb.AppendLine($"  + {line}");
            foreach (var line in localDataOpen) sb.AppendLine($"  . {line}");
            foreach (var line in localDataOrphaned) sb.AppendLine($"  ? {line} (file name did not match any current gap's own expected TestData/ name)");
        }

        if (applied.Count == 0 && stillUnfilled.Count == 0 && staleSeams.Count == 0
            && orphanedFiles.Count == 0 && orphanedMethods.Count == 0)
            sb.AppendLine("  (no Tier-2 seams in this --out, and no fills to apply)");

        sb.AppendLine();
        sb.AppendLine($"Full per-seam audit trail (provenance, status, evidence hashes): {manifestPath}");

        Console.Write(sb.ToString());
    }

    private static string RequireValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{flag} requires a value");
        return args[++i];
    }
}
