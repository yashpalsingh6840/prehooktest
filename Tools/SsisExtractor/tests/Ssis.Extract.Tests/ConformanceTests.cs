using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;

namespace Ssis.Extract.Tests;

/// <summary>
/// Gate 1 (<c>Migration-Validation-Plan.md</c> §3) -- <see cref="ConformanceRulesBuilder"/>
/// and <see cref="ConformanceChecker"/>. Split into two halves on purpose:
/// <list type="bullet">
/// <item>the builder is tested against the <b>real</b> PoC packages, because the whole
/// value of gate 1 is that it enumerates obligations nobody wrote down -- asserting it
/// against a hand-built <c>PackageSpec</c> would only prove it echoes back what the test
/// author already thought of;</item>
/// <item>the checker is tested against hand-built rules/claims, because every path that
/// matters there (invalid status, unexplained NotApplicable, orphan, duplicate) is a
/// malformed input a real package can't produce.</item>
/// </list>
/// </summary>
public class ConformanceTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    // tests/Ssis.Extract.Tests -> Tools/SsisExtractor -> repo root -> SSIS/
    private static readonly string PoCPackagesDir = Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(ThisFilePath())!, "..", "..", "..", "..", "SSIS"));

    private static readonly string FixturesDir = Path.Combine(Path.GetDirectoryName(ThisFilePath())!, "Fixtures");

    private static List<ConformanceRuleSpec> RulesFor(string dtsxFileName) =>
        ConformanceRulesBuilder.Build(DtsxPackageReader.Read(Path.Combine(PoCPackagesDir, dtsxFileName), noRedact: false));

    private static List<ConformanceRuleSpec> RulesForFixture(string dtsxFileName) =>
        ConformanceRulesBuilder.Build(DtsxPackageReader.Read(Path.Combine(FixturesDir, dtsxFileName), noRedact: false));

    [Fact]
    public void LoadEmployees_ProducesAnObligationForEveryTargetColumn()
    {
        // The plan's own worked example (§11): dbo.Employee has 8 columns, and gate 1's
        // headline job is "every target column has a producing rule in the new code".
        var targetColumns = RulesFor("LoadEmployees.dtsx")
            .Where(r => r.Category == "TargetColumn")
            .Select(r => r.RuleId)
            .ToList();

        Assert.Equal(8, targetColumns.Count);
        Assert.Contains("TARGET-COLUMN:[dbo].[Employee].FullName", targetColumns);
        Assert.Contains("TARGET-COLUMN:[dbo].[Employee].EmployeeKey", targetColumns);
        Assert.Contains("TARGET-COLUMN:[dbo].[Employee].LoadedAtUtc", targetColumns);
    }

    [Fact]
    public void LoadEmployees_CarriesTheDerivedColumnExpressionsAsEvidence()
    {
        // These four are exactly the expressions Migration-Validation-Plan §4 names as the
        // seed for gate 2's generated unit tests -- so gate 1's evidence field is also the
        // handoff to gate 2, not just documentation.
        var evidence = RulesFor("LoadEmployees.dtsx")
            .Where(r => r.Category == "Transformation")
            .Select(r => r.Evidence ?? "")
            .ToList();

        Assert.Contains(evidence, e => e.Contains("(DT_WSTR,101)(FirstName + \" \" + LastName)"));
        Assert.Contains(evidence, e => e.Contains("(DT_WSTR,60)(City + \", \" + State)"));
        Assert.Contains(evidence, e => e.Contains("UPPER(SUBSTRING(Department,1,3))"));
        Assert.Contains(evidence, e => e.Contains("GETUTCDATE()"));
    }

    [Fact]
    public void LoadEmployees_LoadSemanticsRuleCapturesFastLoadDetail()
    {
        // §3's "load semantics match: truncate-then-insert vs append vs upsert, fast-load
        // options, batch/commit size" -- the detail that silently diverges in a rewrite
        // because nobody thinks to look at it.
        var rule = Assert.Single(RulesFor("LoadEmployees.dtsx").Where(r => r.Category == "LoadSemantics"));

        Assert.Contains("MaxInsertCommitSize=2147483647", rule.Evidence);
        Assert.Contains("errorRowDisposition=FailComponent", rule.Evidence);
    }

    [Fact]
    public void LoadEmployees_TargetSchemaRuleCarriesTypesAndLengths()
    {
        var rule = Assert.Single(RulesFor("LoadEmployees.dtsx").Where(r => r.Category == "TargetSchema"));

        Assert.Equal("TARGET-SCHEMA:[dbo].[Employee]", rule.RuleId);
        // The declared length is the whole point -- FullName is wstr(101), and a rewrite
        // that silently uses a wider type passes every row-diff until the day it doesn't.
        Assert.Contains("FullName wstr(101)", rule.Evidence);
        Assert.Contains("Salary numeric(18,2)", rule.Evidence);
    }

    [Fact]
    public void LoadEmployees_HasAnOrderingRuleForItsPrecedenceConstraint()
    {
        var ordering = Assert.Single(RulesFor("LoadEmployees.dtsx").Where(r => r.Category == "Ordering"));

        Assert.Contains("SQL_TruncateTarget", ordering.Evidence);
        Assert.Contains("DFT_LoadEmployees", ordering.Evidence);
        Assert.Contains("Value=Success", ordering.Evidence);
    }

    [Fact]
    public void LoadReferenceData_CoversBothDataFlowsAndBothTargetTables()
    {
        var rules = RulesFor("LoadReferenceData.dtsx");

        var targets = rules.Where(r => r.Category == "TargetSchema").Select(r => r.RuleId).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(["TARGET-SCHEMA:[dbo].[Department]", "TARGET-SCHEMA:[dbo].[Designation]"], targets);

        // ONE SqlStatement rule, not two -- this package truncates both tables from a single
        // batched Execute SQL Task ("TRUNCATE ...Department; TRUNCATE ...Designation;").
        // Worth pinning rather than assuming one-rule-per-statement: rules are per *task*
        // (the addressable unit the SQL harvest already writes to sql/), so a rewrite author
        // reading this rule has to notice the evidence contains two statements. Asserting the
        // evidence rather than a count is what makes that visible.
        var sqlRule = Assert.Single(rules.Where(r => r.Category == "SqlStatement"));
        Assert.Contains("dbo.Department", sqlRule.Evidence);
        Assert.Contains("dbo.Designation", sqlRule.Evidence);
    }

    [Fact]
    public void RuleIds_AreStableAcrossRepeatedBuilds()
    {
        // The claim file is keyed by RuleId and hand-maintained for the life of a migration.
        // If ids moved between runs, every claim would orphan itself -- so this is a
        // correctness requirement of the design, not a tidiness check.
        var first = RulesFor("LoadEmployees.dtsx").Select(r => r.RuleId).ToList();
        var second = RulesFor("LoadEmployees.dtsx").Select(r => r.RuleId).ToList();

        Assert.Equal(first, second);
        Assert.Equal(first, first.OrderBy(x => x, StringComparer.Ordinal).ToList()); // emitted sorted, so the file diffs cleanly
        Assert.Equal(first.Count, first.Distinct(StringComparer.Ordinal).Count());   // and unique, or claims would collide
    }

    [Fact]
    public void ScriptTaskAndScriptComponent_ProduceNonMachineVerifiableRules()
    {
        // Migration-Validation-Plan §13: "Script Tasks remain a manual read." These still
        // have to be claimed, but no generated assertion can ever satisfy them, and a
        // conformance percentage that quietly ignored them would be misleading.
        var scriptTaskRule = Assert.Single(RulesForFixture("SyntheticForEachScript.dtsx").Where(r => r.Category == "ScriptCode"));
        Assert.False(scriptTaskRule.MachineVerifiable);
        Assert.Contains("VisualBasic", scriptTaskRule.Evidence);

        var componentRule = Assert.Single(RulesForFixture("SyntheticScriptComponent.dtsx").Where(r => r.Category == "ScriptCode"));
        Assert.False(componentRule.MachineVerifiable);
        Assert.Contains("CSharp", componentRule.Evidence);
        Assert.Contains("writes User::ProcessedRowCount", componentRule.Evidence);
    }

    [Fact]
    public void UnusedSourceColumn_IsFlaggedAsNeedingAnExplicitDropDecision()
    {
        // A ForEach/Script fixture has no pipeline, so use the lookup fixture: whatever its
        // sources produce, any column never consumed downstream must say so rather than
        // silently reading as ordinary work to do.
        var rules = RulesForFixture("SyntheticLookupSplit.dtsx").Where(r => r.Category == "SourceColumn").ToList();

        Assert.NotEmpty(rules);
        Assert.All(rules, r => Assert.StartsWith("SOURCE-COLUMN:", r.RuleId));
    }

    [Fact]
    public void EveryRuleHasARequirementPhrasedAsAnObligation()
    {
        // Gate 1's output is read by a rewrite author deciding what to build. A rule that
        // describes the old package instead of stating an obligation is useless to them.
        foreach (var rule in RulesFor("LoadEmployees.dtsx").Concat(RulesFor("LoadReferenceData.dtsx")))
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Requirement));
            Assert.Contains("must", rule.Requirement, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- Checker ----------------------------------------------------------------

    private static ConformanceRuleSpec Rule(string id) => new()
    {
        RuleId = id,
        Category = "ControlFlow",
        PackageName = "P",
        Location = id,
        Requirement = "Must do the thing.",
    };

    private static ImplementationClaimSpec Claim(string id, string status, string? note = null) => new()
    {
        RuleId = id,
        Status = status,
        Note = note,
    };

    [Fact]
    public void Checker_CountsImplementedAndExplainedNotApplicableAsAccountedFor()
    {
        var result = ConformanceChecker.Check("P",
            [Rule("A"), Rule("B")],
            [Claim("A", "Implemented"), Claim("B", "NotApplicable", "superseded by an upstream feed")]);

        Assert.Equal(1, result.Report.Implemented);
        Assert.Equal(1, result.Report.NotApplicable);
        Assert.Equal(100, result.Report.AccountedForPercent);
        Assert.True(ConformanceChecker.Passes(result.Report));
    }

    [Fact]
    public void Checker_RejectsNotApplicableWithoutANote()
    {
        // The single most important check here: an unexplained NotApplicable is exactly how
        // a real requirement gets quietly deleted from a migration.
        var result = ConformanceChecker.Check("P", [Rule("A")], [Claim("A", "NotApplicable")]);

        Assert.Equal(1, result.Report.InvalidClaims);
        Assert.Equal(0, result.Report.NotApplicable);
        Assert.False(ConformanceChecker.Passes(result.Report));
        Assert.Contains("requires a Note", Assert.Single(result.Rules).Problem);
    }

    [Fact]
    public void Checker_RejectsAnUnknownStatusRatherThanTreatingItAsDone()
    {
        var result = ConformanceChecker.Check("P", [Rule("A")], [Claim("A", "Done")]);

        Assert.Equal(1, result.Report.InvalidClaims);
        Assert.Equal(0, result.Report.Implemented);
        Assert.False(ConformanceChecker.Passes(result.Report));
    }

    [Fact]
    public void Checker_SeparatesUnclaimedFromPending()
    {
        // Both are "not done", but they mean different things: Pending is known work,
        // Unclaimed usually means the package changed and nobody re-reviewed.
        var result = ConformanceChecker.Check("P",
            [Rule("A"), Rule("B")],
            [Claim("A", "Pending")]);

        Assert.Equal(1, result.Report.Pending);
        Assert.Equal(1, result.Report.Unclaimed);
        Assert.False(ConformanceChecker.Passes(result.Report));
    }

    [Fact]
    public void Checker_ReportsOrphanedClaimsInsteadOfIgnoringThem()
    {
        var result = ConformanceChecker.Check("P",
            [Rule("A")],
            [Claim("A", "Implemented"), Claim("GONE", "Implemented")]);

        Assert.Equal(1, result.Report.OrphanedClaims);
        Assert.Equal("GONE", Assert.Single(result.OrphanedClaims).RuleId);
        // Accounted-for is still 100% -- the orphan isn't an unmet obligation, but it does
        // fail the gate, because it means a claim needs re-reviewing.
        Assert.Equal(100, result.Report.AccountedForPercent);
        Assert.False(ConformanceChecker.Passes(result.Report));
    }

    [Fact]
    public void Checker_TreatsDuplicateClaimsForOneRuleAsInvalid()
    {
        var result = ConformanceChecker.Check("P",
            [Rule("A")],
            [Claim("A", "Implemented"), Claim("A", "Pending")]);

        Assert.Equal(1, result.Report.InvalidClaims);
        Assert.False(ConformanceChecker.Passes(result.Report));
    }

    [Fact]
    public void StubClaims_CoverEveryRuleAsPending()
    {
        var rules = RulesFor("LoadEmployees.dtsx");
        var stub = ConformanceChecker.StubClaims(rules);

        Assert.Equal(rules.Count, stub.Count);
        Assert.All(stub, c => Assert.Equal("Pending", c.Status));

        // A freshly-stubbed package is 0% accounted for with zero unclaimed and zero
        // invalid -- the honest starting state, not a false green.
        var result = ConformanceChecker.Check("LoadEmployees", rules, stub);
        Assert.Equal(0, result.Report.AccountedForPercent);
        Assert.Equal(0, result.Report.Unclaimed);
        Assert.Equal(0, result.Report.InvalidClaims);
        Assert.False(ConformanceChecker.Passes(result.Report));
    }
}
