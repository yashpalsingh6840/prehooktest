using Ssis.Extract.Model.Analysis;

namespace Ssis.Extract.Codegen.Tests;

public class DbContextEmitterTests
{
    [Fact]
    public void Emit_EmitsToTableAndFacets_FromTheRealLoadEmployeesDestination()
    {
        var package = TestFixtures.LoadPackage("LoadEmployees.dtsx");
        var destination = TestFixtures.FindComponent(package, "Microsoft.OLEDBDestination", "OLEDST_Employee");

        var result = DbContextEmitter.Emit("LoadEmployees.Model", "EmployeeDbContext",
            [new DbContextEmitter.TableSpec("Employee", destination, PrimaryKey: null)]);

        Assert.Empty(result.Gaps);
        var file = Assert.Single(result.Files);
        Assert.Equal("Model/EmployeeDbContext.cs", file.RelativePath);

        Assert.Contains("public DbSet<Employee> Employees => Set<Employee>();", file.Content);
        Assert.Contains("entity.ToTable(\"Employee\", \"dbo\");", file.Content);
        // Confirmed against the golden spec.json: EmployeeKey wstr/20, FullName wstr/101,
        // Department wstr/50, Location wstr/60, Salary numeric/18,2, LoadedAtUtc dbTimeStamp2/3.
        Assert.Contains("entity.Property(e => e.EmployeeKey).HasMaxLength(20);", file.Content);
        Assert.Contains("entity.Property(e => e.FullName).HasMaxLength(101);", file.Content);
        Assert.Contains("entity.Property(e => e.Department).HasMaxLength(50);", file.Content);
        Assert.Contains("entity.Property(e => e.Location).HasMaxLength(60);", file.Content);
        Assert.Contains("entity.Property(e => e.Salary).HasColumnType(\"decimal(18,2)\");", file.Content);
        Assert.Contains("entity.Property(e => e.LoadedAtUtc).HasColumnType(\"datetime2(3)\");", file.Content);
        Assert.Contains("entity.Property(e => e.HireDate).HasColumnType(\"date\");", file.Content);

        // No PrimaryKeyCandidateSpec was passed, so no HasKey -- deliberately not a gap either.
        Assert.DoesNotContain("HasKey", file.Content);
        Assert.DoesNotContain(".IsRequired()", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_EmitsHasKey_OnlyWhenConfidenceIsNamingConvention()
    {
        var package = TestFixtures.LoadPackage("LoadEmployees.dtsx");
        var destination = TestFixtures.FindComponent(package, "Microsoft.OLEDBDestination", "OLEDST_Employee");
        var candidate = new PrimaryKeyCandidateSpec
        {
            PackageName = "LoadEmployees",
            DataFlowTaskPath = "DFT_LoadEmployees",
            DestinationComponentName = "OLEDST_Employee",
            DestinationComponentRefId = destination.RefId,
            TargetTable = "[dbo].[Employee]",
            Columns = ["EmployeeID"],
            Confidence = "NamingConvention",
            Reason = "matches '<table>ID' naming convention",
        };

        var result = DbContextEmitter.Emit("LoadEmployees.Model", "EmployeeDbContext",
            [new DbContextEmitter.TableSpec("Employee", destination, candidate)]);

        var file = Assert.Single(result.Files);
        Assert.Contains("entity.HasKey(e => e.EmployeeID);", file.Content);
        Assert.Contains("entity.Property(e => e.EmployeeID).ValueGeneratedNever();", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_SkipsHasKey_WhenConfidenceIsUnknown()
    {
        var package = TestFixtures.LoadPackage("LoadEmployees.dtsx");
        var destination = TestFixtures.FindComponent(package, "Microsoft.OLEDBDestination", "OLEDST_Employee");
        var candidate = new PrimaryKeyCandidateSpec
        {
            PackageName = "LoadEmployees",
            DataFlowTaskPath = "DFT_LoadEmployees",
            DestinationComponentName = "OLEDST_Employee",
            DestinationComponentRefId = destination.RefId,
            TargetTable = "[dbo].[Employee]",
            Columns = [],
            Confidence = "Unknown",
            Reason = "no column matched the naming convention",
        };

        var result = DbContextEmitter.Emit("LoadEmployees.Model", "EmployeeDbContext",
            [new DbContextEmitter.TableSpec("Employee", destination, candidate)]);

        var file = Assert.Single(result.Files);
        Assert.DoesNotContain("HasKey", file.Content);
        Assert.Empty(result.Gaps.Where(g => g.Location == "EmployeeDbContext"));
    }

    [Fact]
    public void Emit_EmitsBothTablesInOneContext_FromTheRealLoadReferenceDataDestinations()
    {
        // Matches how the hand-written rewrite grouped things: one DbContext per package,
        // both destinations' modelBuilder.Entity blocks inside the same OnModelCreating.
        var package = TestFixtures.LoadPackage("LoadReferenceData.dtsx");
        var department = TestFixtures.FindComponent(package, "Microsoft.OLEDBDestination", "OLEDST_Department");
        var designation = TestFixtures.FindComponent(package, "Microsoft.OLEDBDestination", "OLEDST_Designation");

        var result = DbContextEmitter.Emit("LoadReferenceData.Model", "ReferenceDataDbContext",
        [
            new DbContextEmitter.TableSpec("Department", department, null),
            new DbContextEmitter.TableSpec("Designation", designation, null),
        ]);

        Assert.Empty(result.Gaps);
        var file = Assert.Single(result.Files);
        Assert.Contains("public DbSet<Department> Departments => Set<Department>();", file.Content);
        Assert.Contains("public DbSet<Designation> Designations => Set<Designation>();", file.Content);
        Assert.Contains("entity.ToTable(\"Department\", \"dbo\");", file.Content);
        Assert.Contains("entity.ToTable(\"Designation\", \"dbo\");", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }
}
