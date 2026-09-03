namespace Ssis.Extract.Codegen.Tests;

public class EntityEmitterTests
{
    [Fact]
    public void Emit_ProducesEveryColumn_FromTheRealLoadEmployeesDestination()
    {
        var package = TestFixtures.LoadPackage("LoadEmployees.dtsx");
        var destination = TestFixtures.FindComponent(package, "Microsoft.OLEDBDestination", "OLEDST_Employee");

        var result = EntityEmitter.Emit("LoadEmployees.Model", "Employee", destination);

        Assert.Empty(result.Gaps);
        var file = Assert.Single(result.Files);
        Assert.Equal("Model/Employee.cs", file.RelativePath);

        // All 8 real dbo.Employee columns, with the facts confirmed against the golden
        // spec.json: EmployeeID int, EmployeeKey/FullName/Department/Location string,
        // HireDate DateOnly, Salary decimal, LoadedAtUtc DateTime.
        Assert.Contains("public int EmployeeID { get; set; }", file.Content);
        Assert.Contains("public string EmployeeKey { get; set; } = \"\";", file.Content);
        Assert.Contains("public string FullName { get; set; } = \"\";", file.Content);
        Assert.Contains("public string Department { get; set; } = \"\";", file.Content);
        Assert.Contains("public DateOnly HireDate { get; set; }", file.Content);
        Assert.Contains("public decimal Salary { get; set; }", file.Content);
        Assert.Contains("public string Location { get; set; } = \"\";", file.Content);
        Assert.Contains("public DateTime LoadedAtUtc { get; set; }", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_EndsInASingleTrailingNewline_NoCarriageReturnsAnywhere()
    {
        var package = TestFixtures.LoadPackage("LoadEmployees.dtsx");
        var destination = TestFixtures.FindComponent(package, "Microsoft.OLEDBDestination", "OLEDST_Employee");

        var file = Assert.Single(EntityEmitter.Emit("LoadEmployees.Model", "Employee", destination).Files);

        Assert.DoesNotContain('\r', file.Content);
        Assert.EndsWith("\n", file.Content);
        Assert.False(file.Content.EndsWith("\n\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Emit_MakesAStringPropertyNullable_WhenItsOwnColumnIsNullableInferred()
    {
        // Mirrors SqlRowEmitter's own identical fix (2026-08-28): this project generates with
        // <Nullable>enable</Nullable> + TreatWarningsAsErrors, so a `string` property assigned a
        // genuinely-null value is a build ERROR (CS8601), not just a runtime risk -- string must
        // NOT be excluded from nullable-column treatment the way an earlier version of this
        // emitter excluded it.
        var package = TestFixtures.LoadPackage("LoadEmployees.dtsx");
        var destination = TestFixtures.FindComponent(package, "Microsoft.OLEDBDestination", "OLEDST_Employee");

        var result = EntityEmitter.Emit("LoadEmployees.Model", "Employee", destination,
            nullableColumnNames: new HashSet<string> { "EmployeeKey" });

        Assert.Empty(result.Gaps);
        var file = Assert.Single(result.Files);
        Assert.Contains("public string? EmployeeKey { get; set; }", file.Content);
        Assert.DoesNotContain("EmployeeKey { get; set; } = \"\";", file.Content);
        // Every other string column is unaffected -- not blanket-nullable, only the named one.
        Assert.Contains("public string FullName { get; set; } = \"\";", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_UsesTheNamespaceAndEntityNamePassedIn()
    {
        var package = TestFixtures.LoadPackage("LoadReferenceData.dtsx");
        var destination = TestFixtures.FindComponent(package, "Microsoft.OLEDBDestination", "OLEDST_Department");

        var file = Assert.Single(EntityEmitter.Emit("LoadReferenceData.Model", "Department", destination).Files);

        Assert.Contains("namespace LoadReferenceData.Model;", file.Content);
        Assert.Contains("public sealed class Department", file.Content);
        Assert.Equal("Model/Department.cs", file.RelativePath);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }
}
