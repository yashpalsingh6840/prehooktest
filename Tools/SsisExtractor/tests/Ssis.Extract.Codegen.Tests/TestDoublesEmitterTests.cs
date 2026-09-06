namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// Direct unit coverage for <see cref="TestDoublesEmitter.Emit"/>'s own <c>PackageHarness.cs</c>
/// output -- specifically <c>NewRealUnitOfWorkAsync</c>, added 2026-09-06 after a real, previously
/// unexercised finding: <c>ComponentTestEmitter.EmitSqlSourceTest</c>'s own
/// <c>[Trait("Category", "Integration")]</c> test used to pass a <c>FakeUnitOfWork</c> (from
/// <c>NewUnitOfWork()</c>) into a source method that unconditionally attempts
/// <c>sp_bindsession</c> when given a non-null <c>uow</c> -- and a <c>FakeUnitOfWork</c>'s own
/// bind token is always the same hardcoded placeholder string, which any real server rejects
/// regardless of what server <c>PackageHarness</c>'s own <c>DatabaseOptions</c> points at. Proven
/// beyond this file by actually editing a generated <c>PackageHarness.cs</c>'s own
/// <c>_databaseOptions</c> line and running the resulting Integration test against
/// <c>.\SQLFORPOC_2022</c> -- it now genuinely passes, where it used to fail with "Session binding
/// token is invalid" even after that same edit.
/// </summary>
public class TestDoublesEmitterTests
{
    [Fact]
    public void Emit_ProducesAPackageHarness_WithARealUnitOfWorkFactory()
    {
        var result = TestDoublesEmitter.Emit("MyPackage", "MyPackagePackage", "MyPackageDbContext");

        var harness = Assert.Single(result.Files, f => f.RelativePath == "TestDoubles/PackageHarness.cs");
        Assert.Contains("private readonly DatabaseOptions _databaseOptions;", harness.Content);
        Assert.Contains("_databaseOptions = new DatabaseOptions { Server = \"(fake)\", Database = \"Fake\", ConnectTimeoutSeconds = 1 };", harness.Content);
        Assert.Contains("public async Task<UnitOfWork> NewRealUnitOfWorkAsync(CancellationToken ct = default)", harness.Content);
        // Reads the SAME options field a human edits for a local run, not a second literal copy.
        Assert.Contains(".UseSqlServer(SqlConnectionStringFactory.Build(_databaseOptions))", harness.Content);
        Assert.Contains("new UnitOfWork(new MyPackageDbContext(options), new SqlBulkCopyFactory(), NullLogger<UnitOfWork>.Instance);", harness.Content);
        // Begun before being handed back -- GetBindTokenAsync throws unless a transaction is
        // already active (see UnitOfWork's own precondition), matching production's own ordering.
        Assert.Contains("await uow.BeginAsync(IsolationLevel.ReadCommitted, ct);", harness.Content);
        Assert.Contains("using System.Data;", harness.Content);
        CodeAssertions.AssertNoSyntaxErrors(harness.Content);
    }
}
