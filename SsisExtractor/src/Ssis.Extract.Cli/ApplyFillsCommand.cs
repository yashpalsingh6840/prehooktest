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
/// <c>Fill_&lt;Column&gt;</c> seams <c>ssisx generate --seams</c> emitted for columns produced by a
/// Script Component. It is human work product -- authored by a person, usually with AI help from
/// the SCRIPT-COLUMN work packet -- so this command only ever READS <c>fills/</c>, never writes to
/// it, exactly like <c>conformance</c>'s claims files. Fills are copied INTO
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

    /// <summary>Matches an implementing part of a seam, e.g.
    /// <c>private partial string Fill_FullName(StagingCustomersCsvRow row, in RowContext ctx) =&gt; ...</c>.
    /// Deliberately loose about the return type (it can be nullable, generic, qualified) and about
    /// modifier order -- the compiler is the authority on whether the part actually matches its
    /// declaration; all this needs is the method NAME, to tell a real fill from an orphan.</summary>
    private static readonly Regex FillMethodPattern =
        new(@"partial\s+(?:async\s+)?[^\s(]+\s+(Fill_\w+)\s*\(", RegexOptions.Compiled);

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

        // Expected seams, per package: one per ScriptComponentColumn gap. GapSpec.Location is
        // "{Entity}.{Column}", and TransformEmitter names the seam Fill_{Column} -- so the column
        // is the part after the last '.'.
        var expected = new Dictionary<string, Dictionary<string, GapSpec>>(StringComparer.Ordinal);
        foreach (var gap in gaps.Where(g => g.Kind == GapKind.ScriptComponentColumn))
        {
            var column = gap.Location[(gap.Location.LastIndexOf('.') + 1)..];
            if (column.Length == 0) continue;
            if (!expected.TryGetValue(gap.Package, out var byMethod))
                expected[gap.Package] = byMethod = new Dictionary<string, GapSpec>(StringComparer.Ordinal);
            byMethod[$"Fill_{column}"] = gap;
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

        fillsDir = Path.GetFullPath(fillsDir ?? Path.Combine(outDir, "fills"));
        // Same default `ssisx conformance` itself uses -- read-only here, and silently absent
        // (never an error) when nobody has run `conformance` for this --out at all.
        claimsDir = Path.GetFullPath(claimsDir ?? Path.Combine(outDir, "conformance", "claims"));
        var generateDir = Path.Combine(outDir, "generate");

        var applied = new List<string>();
        var orphanedFiles = new List<string>();
        var orphanedMethods = new List<string>();
        var staleSeams = new List<string>();
        var unknownPackages = new List<string>();
        var filledMethods = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var staleMethods = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var fillRecords = new List<FillRecordSpec>();

        if (Directory.Exists(fillsDir))
        {
            foreach (var packageDir in Directory.EnumerateDirectories(fillsDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var packageName = Path.GetFileName(packageDir);
                if (!Directory.Exists(Path.Combine(generateDir, packageName)))
                {
                    unknownPackages.Add(packageName);
                    continue;
                }

                foreach (var fillFile in Directory.EnumerateFiles(packageDir, "*.cs").OrderBy(f => f, StringComparer.Ordinal))
                {
                    var fileName = Path.GetFileName(fillFile);
                    var content = File.ReadAllText(fillFile);
                    var known = expected.TryGetValue(packageName, out var byMethod) ? byMethod : [];
                    var knownClasses = expectedScriptTasks.TryGetValue(packageName, out var byClass) ? byClass : [];

                    // One row per seam actually found in the file, each carrying its own
                    // provenance comment (if any) -- unlike the pre-chunk-4 version, this is no
                    // longer a plain distinct-method-names list, because staleness is a per-seam
                    // fact, not a per-file one.
                    var seams = new List<(string Seam, GapSpec? Gap)>();
                    foreach (Match m in FillMethodPattern.Matches(content))
                    {
                        var method = m.Groups[1].Value;
                        if (seams.Any(s => s.Seam == method)) continue;
                        known.TryGetValue(method, out var gap);
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

                    var recognized = new List<string>();
                    var staleInThisFile = new List<string>();
                    foreach (var (seam, gap) in seams)
                    {
                        if (gap is null)
                        {
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
                            staleSet.Add(seam);
                            staleInThisFile.Add(seam);
                            continue;
                        }

                        recognized.Add(seam);
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
                        if (staleInThisFile.Count == 0 && (seams.Count == 0 || seams.All(s => s.Gap is null)))
                            orphanedFiles.Add($"{packageName}/{fileName}");
                        continue;
                    }

                    var targetDir = Path.Combine(generateDir, packageName, "Fills");
                    Directory.CreateDirectory(targetDir);
                    File.Copy(fillFile, Path.Combine(targetDir, fileName), overwrite: true);
                    applied.Add($"{packageName}/Fills/{fileName} ({string.Join(", ", recognized)})");

                    if (!filledMethods.TryGetValue(packageName, out var set))
                        filledMethods[packageName] = set = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var method in recognized) set.Add(method);
                }
            }
        }

        var stillUnfilled = new List<string>();
        foreach (var (packageName, byMethod) in expected.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            filledMethods.TryGetValue(packageName, out var filled);
            staleMethods.TryGetValue(packageName, out var stale);
            foreach (var (method, gap) in byMethod.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (filled?.Contains(method) == true) continue;
                // A stale seam is reported in its own, more informative section above -- it HAS a
                // fill, just an out-of-date one, which "no fill provided at all" would misstate.
                if (stale?.Contains(method) == true) continue;
                stillUnfilled.Add($"{gap.GapId}  ->  {method}");
            }
        }

        foreach (var (packageName, byClass) in expectedScriptTasks.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            filledMethods.TryGetValue(packageName, out var filled);
            staleMethods.TryGetValue(packageName, out var stale);
            foreach (var (cls, gap) in byClass.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var seam = $"{cls}.RunScriptAsync";
                if (filled?.Contains(seam) == true) continue;
                if (stale?.Contains(seam) == true) continue;
                stillUnfilled.Add($"{gap.GapId}  ->  {seam}");
            }
        }

        // Group every EXPECTED seam (not just the ones a fill answered) by the ScriptCode RuleId
        // its own gap points at -- several ScriptComponentColumn gaps can share one RuleId (same
        // Script Component, several columns), and the underlying obligation is "port the whole
        // component", not one per column, so it is only satisfied once every seam in the group has
        // genuinely landed in `generate/` (i.e. is in filledMethods, never stale/orphaned).
        var ruleGroups = new Dictionary<(string Package, string RuleId), List<string>>();
        foreach (var (packageName, byMethod) in expected)
            foreach (var (method, gap) in byMethod)
                if (ConformanceRuleId(gap) is { } ruleId)
                {
                    var key = (packageName, ruleId);
                    if (!ruleGroups.TryGetValue(key, out var seamsForRule))
                        ruleGroups[key] = seamsForRule = [];
                    seamsForRule.Add(method);
                }
        foreach (var (packageName, byClass) in expectedScriptTasks)
            foreach (var (cls, gap) in byClass)
                if (ConformanceRuleId(gap) is { } ruleId)
                {
                    var key = (packageName, ruleId);
                    if (!ruleGroups.TryGetValue(key, out var seamsForRule))
                        ruleGroups[key] = seamsForRule = [];
                    seamsForRule.Add($"{cls}.RunScriptAsync");
                }

        var conformanceLinks = new List<string>();
        var claimsCache = new Dictionary<string, List<ImplementationClaimSpec>?>(StringComparer.Ordinal);
        foreach (var ((packageName, ruleId), seamsForRule) in ruleGroups.OrderBy(g => g.Key.Package, StringComparer.Ordinal)
                                                                          .ThenBy(g => g.Key.RuleId, StringComparer.Ordinal))
        {
            filledMethods.TryGetValue(packageName, out var filled);
            if (!seamsForRule.All(s => filled?.Contains(s) == true)) continue; // partial -- not yet a candidate

            if (!claimsCache.TryGetValue(packageName, out var claims))
                claimsCache[packageName] = claims = ReadClaims(claimsDir, packageName);
            var claim = claims?.FirstOrDefault(c => string.Equals(c.RuleId, ruleId, StringComparison.Ordinal));

            var status = claim?.Status ?? (claims is null ? "(no claims file found)" : "Unclaimed");
            var note = string.Equals(status, "Implemented", StringComparison.OrdinalIgnoreCase)
                ? "already marked Implemented"
                : "update the claims file by hand if this port is trustworthy";
            conformanceLinks.Add(
                $"{packageName}: {ruleId}  --  all {seamsForRule.Count} seam(s) filled ({string.Join(", ", seamsForRule.OrderBy(s => s, StringComparer.Ordinal))}); claim status: {status} -- {note}");
        }

        var manifestPath = Path.Combine(outDir, "fills-applied.json");
        StableJsonWriter.WriteToFile(
            fillRecords.OrderBy(r => r.Package, StringComparer.Ordinal)
                       .ThenBy(r => r.FileName, StringComparer.Ordinal)
                       .ThenBy(r => r.Seam, StringComparer.Ordinal)
                       .ToList(),
            manifestPath);

        Report(fillsDir, manifestPath, applied, stillUnfilled, staleSeams, orphanedFiles, orphanedMethods, unknownPackages, conformanceLinks);

        if (orphanedFiles.Count > 0 || orphanedMethods.Count > 0 || unknownPackages.Count > 0) return 1;
        return stillUnfilled.Count > 0 || staleSeams.Count > 0 ? 3 : 0;
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
            : FillMethodPattern.Matches(content)
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
        List<string> staleSeams, List<string> orphanedFiles, List<string> orphanedMethods,
        List<string> unknownPackages, List<string> conformanceLinks)
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
            sb.AppendLine("Orphaned -- implements a Fill_* no gap asked for, so NOT copied. Usually the package");
            sb.AppendLine("changed and that seam is gone; re-read the current work packet before reusing it.");
            foreach (var line in orphanedMethods) sb.AppendLine($"  ? {line}");
            foreach (var line in orphanedFiles) sb.AppendLine($"  ? {line} (skipped entirely -- nothing in it matched a seam)");
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
