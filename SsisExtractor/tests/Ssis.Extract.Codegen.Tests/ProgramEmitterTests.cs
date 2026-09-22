namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// ProgramEmitter now emits ONLY the bootstrap -- everything about how a package actually runs
/// (constructing every step, running each in order, the transaction/rollback wrapper) lives in
/// PackageClassEmitter's own <c>{Package}.cs</c> instead (see that class's own doc comment, and
/// PackageClassEmitterTests for its own coverage). What used to be asserted here against
/// Program.cs's own flat-script content moved there.
/// </summary>
public class ProgramEmitterTests
{
    [Fact]
    public void Emit_ProducesASlimBootstrap_ThatConstructsAndRunsThePackageClass()
    {
        var request = new ProgramRequest(
            PackageName: "LoadEmployees",
            RootNamespace: "LoadEmployees",
            DbContextTypeName: "EmployeeDbContext",
            PreLoadStatements: [],
            PreLoadFileActions: [],
            Steps: []);

        var result = ProgramEmitter.Emit(request);

        var file = Assert.Single(result.Files);
        Assert.Equal("Program.cs", file.RelativePath);

        Assert.Contains("using LoadEmployees;", file.Content);
        Assert.Contains("using LoadEmployees.Model;", file.Content);
        Assert.Contains("const string PackageName = \"LoadEmployees\";", file.Content);
        Assert.Contains("var builder = EtlHost.Create(args, PackageName);", file.Content);
        Assert.Contains("builder.Services.AddEtlDbContext<EmployeeDbContext>();", file.Content);
        Assert.Contains("using var host = builder.Build();", file.Content);
        Assert.Contains("return await new LoadEmployeesPackage(host.Services).RunAsync(CancellationToken.None);", file.Content);

        // Off by default (IncludeNotifications defaults false) -- a .dtsx carries no
        // notification-recipient info at all, so wiring this unconditionally would add a
        // capability the original SSIS package never had. See EmitsAddEmailNotifications_...
        // below for the opted-in shape.
        Assert.DoesNotContain("AddEmailNotifications", file.Content);
        Assert.DoesNotContain("using Etl.Core.Notifications;", file.Content);

        // Deliberately gone: this is the whole point of the emitter rewrite -- no
        // DI-registration-lambda construction of any component lives in Program.cs any more.
        Assert.DoesNotContain("AddScoped<IRowSource", file.Content);
        Assert.DoesNotContain("AddBulkSink", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_EmitsAddEmailNotifications_WhenIncludeNotificationsIsSet()
    {
        var request = new ProgramRequest(
            PackageName: "LoadEmployees",
            RootNamespace: "LoadEmployees",
            DbContextTypeName: "EmployeeDbContext",
            PreLoadStatements: [],
            PreLoadFileActions: [],
            Steps: [])
        {
            IncludeNotifications = true,
        };

        var result = ProgramEmitter.Emit(request);
        var file = Assert.Single(result.Files);

        Assert.Contains("using Etl.Core.Notifications;", file.Content);
        Assert.Contains("builder.Services.AddEmailNotifications(builder.Configuration);", file.Content);
        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_NamesTheClassAfterThePackage_ForADifferentPackageName()
    {
        var request = new ProgramRequest("SyntheticParallelShapes", "SyntheticParallelShapes", "SyntheticParallelShapesDbContext", [], [], []);

        var result = ProgramEmitter.Emit(request);
        var file = Assert.Single(result.Files);

        Assert.Contains("new SyntheticParallelShapesPackage(host.Services)", file.Content);
        Assert.Equal("SyntheticParallelShapesPackage", PackageClassEmitter.ClassName("SyntheticParallelShapes"));
    }
}
