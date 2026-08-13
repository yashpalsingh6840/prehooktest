using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Pipeline;
using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Tests;

/// <summary>Unit tests for <see cref="RulesEngine"/> against synthetic packages -- one per rule shape not exercised (or not exercised in this specific way) by the two real PoC packages, e.g. neither has a disabled task, an unused variable, or a duplicate sibling.</summary>
public class RulesEngineTests
{
    private static PackageSpec WithExecutables(PackageSpec package, List<ExecutableSpec> executables) => new()
    {
        ObjectName = package.ObjectName,
        SourceDtsxPath = package.SourceDtsxPath,
        Sha256 = package.Sha256,
        FileSizeBytes = package.FileSizeBytes,
        LastWriteTimeUtc = package.LastWriteTimeUtc,
        ProtectionLevelRaw = package.ProtectionLevelRaw,
        ProtectionLevelName = package.ProtectionLevelName,
        Coverage = package.Coverage,
        Executables = executables,
        Variables = package.Variables,
        ConnectionManagers = package.ConnectionManagers,
    };

    [Fact]
    public void DisabledTask_ProducesFinding()
    {
        var task = new ExecutableSpec { RefId = "T1", ExecutableType = "Microsoft.ExecuteSQLTask", Disabled = true };
        var package = WithExecutables(TestFixtures.MinimalPackage("P"), [task]);

        var findings = RulesEngine.Evaluate(package);

        Assert.Contains(findings, f => f.RuleId == "disabled-task" && f.Location == "T1");
    }

    [Fact]
    public void EnabledTask_ProducesNoDisabledFinding()
    {
        var task = new ExecutableSpec { RefId = "T1", ExecutableType = "Microsoft.ExecuteSQLTask", Disabled = false };
        var package = WithExecutables(TestFixtures.MinimalPackage("P"), [task]);

        var findings = RulesEngine.Evaluate(package);

        Assert.DoesNotContain(findings, f => f.RuleId == "disabled-task");
    }

    [Fact]
    public void VariableNeverReferencedInAnyExpression_IsFlaggedUnused()
    {
        var package = TestFixtures.MinimalPackage("P");
        var withVar = new PackageSpec
        {
            ObjectName = package.ObjectName,
            SourceDtsxPath = package.SourceDtsxPath,
            Sha256 = package.Sha256,
            FileSizeBytes = package.FileSizeBytes,
            LastWriteTimeUtc = package.LastWriteTimeUtc,
            ProtectionLevelRaw = package.ProtectionLevelRaw,
            ProtectionLevelName = package.ProtectionLevelName,
            Coverage = package.Coverage,
            Variables = [new VariableSpec { Namespace = "User", ObjectName = "Unused", OwningContainerRefId = "Package" }],
        };

        var findings = RulesEngine.Evaluate(withVar);

        Assert.Contains(findings, f => f.RuleId == "unused-variable" && f.Message.Contains("User::Unused"));
    }

    [Fact]
    public void VariableReferencedInPropertyExpression_IsNotFlaggedUnused()
    {
        var package = TestFixtures.MinimalPackage("P");
        var withVar = new PackageSpec
        {
            ObjectName = package.ObjectName,
            SourceDtsxPath = package.SourceDtsxPath,
            Sha256 = package.Sha256,
            FileSizeBytes = package.FileSizeBytes,
            LastWriteTimeUtc = package.LastWriteTimeUtc,
            ProtectionLevelRaw = package.ProtectionLevelRaw,
            ProtectionLevelName = package.ProtectionLevelName,
            Coverage = package.Coverage,
            Variables = [new VariableSpec { Namespace = "User", ObjectName = "Used", OwningContainerRefId = "Package" }],
            PropertyExpressions = [new PropertyExpressionSpec { PropertyName = "ConnectionString", Expression = "@[User::Used]" }],
        };

        var findings = RulesEngine.Evaluate(withVar);

        Assert.DoesNotContain(findings, f => f.RuleId == "unused-variable");
    }

    [Fact]
    public void UnroutedErrorOutput_IsFlagged()
    {
        var comp = new PipelineComponentSpec
        {
            RefId = "C1",
            Name = "Comp",
            ComponentClassId = "Microsoft.FlatFileSource",
            Outputs =
            [
                new PipelineOutputSpec { RefId = "C1.Out", Name = "Out", IsErrorOut = false },
                new PipelineOutputSpec { RefId = "C1.Err", Name = "Err", IsErrorOut = true },
            ],
        };
        var pipeline = new PipelineSpec { Components = [comp], Paths = [] }; // no path from C1.Err anywhere
        var dft = new ExecutableSpec
        {
            RefId = "DFT1",
            ExecutableType = "Microsoft.Pipeline",
            DataFlowTask = new DataFlowTaskPayload { Pipeline = pipeline, Lineage = LineageBuilder.Build(pipeline) },
        };
        var package = WithExecutables(TestFixtures.MinimalPackage("P"), [dft]);

        var findings = RulesEngine.Evaluate(package);

        Assert.Contains(findings, f => f.RuleId == "error-output-unrouted" && f.Location == "DFT1/Comp");
    }

    [Fact]
    public void ThirdPartyComponent_IsFlagged()
    {
        var comp = new PipelineComponentSpec { RefId = "C1", Name = "Comp", ComponentClassId = "Acme.CustomTransform" };
        var pipeline = new PipelineSpec { Components = [comp] };
        var dft = new ExecutableSpec
        {
            RefId = "DFT1",
            ExecutableType = "Microsoft.Pipeline",
            DataFlowTask = new DataFlowTaskPayload { Pipeline = pipeline, Lineage = LineageBuilder.Build(pipeline) },
        };
        var package = WithExecutables(TestFixtures.MinimalPackage("P"), [dft]);

        var findings = RulesEngine.Evaluate(package);

        Assert.Contains(findings, f => f.RuleId == "third-party-component" && f.Category == "RewriteEffort");
    }

    [Fact]
    public void DuplicatePackages_AreFlaggedByPortfolioRule_ButNotBySinglePackageEvaluate()
    {
        var a = TestFixtures.MinimalPackage("A", sha256: "same");
        var b = TestFixtures.MinimalPackage("B", sha256: "same");
        var c = TestFixtures.MinimalPackage("C", sha256: "different");

        Assert.DoesNotContain(RulesEngine.Evaluate(a), f => f.RuleId == "duplicate-package");

        var portfolioFindings = RulesEngine.EvaluatePortfolio([a, b, c]);

        Assert.Contains(portfolioFindings, f => f.PackageName == "A" && f.RuleId == "duplicate-package");
        Assert.Contains(portfolioFindings, f => f.PackageName == "B" && f.RuleId == "duplicate-package");
        Assert.DoesNotContain(portfolioFindings, f => f.PackageName == "C");
    }
}
