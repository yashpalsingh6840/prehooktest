using System.Text.Json;

namespace Ssis.Extract.Codegen.Tests;

public class ProjectEmitterTests
{
    [Fact]
    public void Emit_IncludesUserSecretsPackageReference_OnlyForSqlServerAuth()
    {
        var request = new ProjectRequest(
            "LoadEmployees",
            [new FileSourceEntryRequest("Employees", @"D:\Data\Input", "Employees.csv")],
            new DatabaseAuthRequest("SqlServer", "ssis_loader"),
            OnSuccessRecipients: ["reports@example.local"],
            OnFailureRecipients: ["oncall@example.local"]);

        var result = ProjectEmitter.Emit(request);

        var csproj = result.Files.Single(f => f.RelativePath == "LoadEmployees.csproj");
        Assert.Contains("Microsoft.Extensions.Configuration.UserSecrets", csproj.Content);
        Assert.Contains("<InternalsVisibleTo Include=\"LoadEmployees.Tests\" />", csproj.Content);
        Assert.DoesNotContain('\r', csproj.Content);
    }

    [Fact]
    public void Emit_OmitsUserSecretsPackageReference_ForWindowsAuth()
    {
        var request = new ProjectRequest(
            "LoadReferenceData",
            [],
            new DatabaseAuthRequest("Windows", UserId: null),
            OnSuccessRecipients: [],
            OnFailureRecipients: []);

        var result = ProjectEmitter.Emit(request);

        var csproj = result.Files.Single(f => f.RelativePath == "LoadReferenceData.csproj");
        Assert.DoesNotContain("UserSecrets", csproj.Content);
    }

    [Fact]
    public void Emit_ProducesValidJson_WithFileSourceEntriesAndNotificationRecipients()
    {
        var request = new ProjectRequest(
            "LoadReferenceData",
            [
                new FileSourceEntryRequest("Department", @"D:\Data\Department", "Departments.csv"),
                new FileSourceEntryRequest("Designation", @"D:\Data\Designation", "Designations.csv"),
            ],
            new DatabaseAuthRequest("Windows", UserId: null),
            OnSuccessRecipients: ["reports@example.local"],
            OnFailureRecipients: ["oncall@example.local"])
        {
            IncludeNotifications = true,
        };

        var result = ProjectEmitter.Emit(request);
        var appsettings = result.Files.Single(f => f.RelativePath == "appsettings.json");

        using var doc = JsonDocument.Parse(appsettings.Content);
        var root = doc.RootElement;

        Assert.Equal(@"D:\Data\Department", root.GetProperty("FileSource").GetProperty("Files").GetProperty("Department").GetProperty("SourceFolder").GetString());
        Assert.Equal("Departments.csv", root.GetProperty("FileSource").GetProperty("Files").GetProperty("Department").GetProperty("SourceFileName").GetString());
        Assert.Equal("Windows", root.GetProperty("TargetDatabase").GetProperty("AuthMode").GetString());
        Assert.False(root.GetProperty("TargetDatabase").TryGetProperty("UserId", out _));
        Assert.Equal("LoadReferenceData", root.GetProperty("TargetDatabase").GetProperty("ApplicationName").GetString());
        Assert.Equal("oncall@example.local", root.GetProperty("Notification").GetProperty("OnFailureRecipients")[0].GetString());

        Assert.DoesNotContain('\r', appsettings.Content);
        Assert.EndsWith("\n", appsettings.Content);
    }

    [Fact]
    public void Emit_IncludesUserId_OnlyForSqlServerAuth()
    {
        var request = new ProjectRequest(
            "LoadEmployees",
            [],
            new DatabaseAuthRequest("SqlServer", "ssis_loader"),
            OnSuccessRecipients: [],
            OnFailureRecipients: []);

        var appsettings = ProjectEmitter.Emit(request).Files.Single(f => f.RelativePath == "appsettings.json");
        using var doc = JsonDocument.Parse(appsettings.Content);

        Assert.Equal("ssis_loader", doc.RootElement.GetProperty("TargetDatabase").GetProperty("UserId").GetString());
    }
}
