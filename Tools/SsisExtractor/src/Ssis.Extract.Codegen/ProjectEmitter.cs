using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ssis.Extract.Codegen;

public sealed record FileSourceEntryRequest(string Key, string SourceFolder, string SourceFileName);

/// <summary>"Windows" or "SqlServer" -- mirrors Etl.Core.Hosting.SqlAuthMode. UserId is required
/// (and rendered) only for SqlServer; the password itself is never emitted anywhere, matching
/// the skeleton's own "User Secrets/env var only, never appsettings.json" rule.</summary>
public sealed record DatabaseAuthRequest(string AuthMode, string? UserId);

public sealed record ProjectRequest(
    string PackageName,
    List<FileSourceEntryRequest> FileSourceEntries,
    DatabaseAuthRequest DatabaseAuth,
    List<string> OnSuccessRecipients,
    List<string> OnFailureRecipients);

/// <summary>
/// Emits one package's project-level files: the .csproj and its appsettings.json. Does NOT
/// emit the repo-wide files (Directory.Build.props, Directory.Packages.props,
/// appsettings.Shared.json, the .slnx) -- those are written once per `--out` directory
/// regardless of package count, not per package, so they don't fit this type's per-package
/// Emit(ProjectRequest) shape; the future CLI command writes them directly as fixed content.
/// </summary>
public static class ProjectEmitter
{
    public static EmitResult Emit(ProjectRequest request) =>
        new([EmitCsproj(request), EmitAppSettings(request)], []);

    private static GeneratedFile EmitCsproj(ProjectRequest request)
    {
        var lines = new List<string>
        {
            "<Project Sdk=\"Microsoft.NET.Sdk\">",
            "",
            "  <ItemGroup>",
            "    <ProjectReference Include=\"..\\Etl.Core\\Etl.Core.csproj\" />",
            "  </ItemGroup>",
            "",
        };

        if (request.DatabaseAuth.AuthMode == "SqlServer")
        {
            lines.Add("  <ItemGroup>");
            lines.Add("    <PackageReference Include=\"Microsoft.Extensions.Configuration.UserSecrets\" />");
            lines.Add("  </ItemGroup>");
            lines.Add("");
        }

        lines.Add("  <PropertyGroup>");
        lines.Add("    <OutputType>Exe</OutputType>");
        if (request.DatabaseAuth.AuthMode == "SqlServer")
            lines.Add($"    <UserSecretsId>{DeterministicUserSecretsId(request.PackageName)}</UserSecretsId>");
        lines.Add("  </PropertyGroup>");
        lines.Add("");
        lines.Add("  <ItemGroup>");
        lines.Add("    <None Update=\"appsettings.json;appsettings.*.json\" CopyToOutputDirectory=\"PreserveNewest\" />");
        lines.Add("    <Content Include=\"..\\Shared\\appsettings.Shared.json\" Link=\"appsettings.Shared.json\" CopyToOutputDirectory=\"PreserveNewest\" />");
        lines.Add("  </ItemGroup>");
        lines.Add("");
        lines.Add("  <ItemGroup>");
        lines.Add($"    <InternalsVisibleTo Include=\"{request.PackageName}.Tests\" />");
        lines.Add("  </ItemGroup>");
        lines.Add("");
        lines.Add("</Project>");

        return new GeneratedFile($"{request.PackageName}.csproj", Rendering.JoinLines(lines));
    }

    /// <summary>
    /// Real bug found by Phase 4 layer 3 (diffing generated LoadEmployees against the
    /// hand-written D:\PoC\SSIS_Rewrite reference, 2026-08-25): a SqlServer-auth package's
    /// generated .csproj had the UserSecrets package reference but no &lt;UserSecretsId&gt;.
    /// `Etl.Core.Hosting.EtlHost.Create` calls `AddUserSecrets(entryAssembly, ...)` -- the
    /// assembly-instance overload, which reads the `[assembly: UserSecretsId(...)]` attribute
    /// the SDK generates FROM this csproj element at build time. Without it, that call throws
    /// `InvalidOperationException` at startup ("Could not find 'UserSecretsId'..."), so the
    /// generated app never even reached the point of needing the actual secret. Confirmed by
    /// reading EtlHost.cs directly, not guessed.
    ///
    /// The GUID must be DETERMINISTIC, not `Guid.NewGuid()` -- generation has to be idempotent
    /// (same input .dtsx -> byte-identical output, what GenerateGoldenFileTests pins) the same
    /// way every other emitted file already is. Derived from the package name via MD5, which
    /// only needs to be stable and distinct per package, not cryptographically meaningful --
    /// this is a local secrets-store folder name, not a security boundary.
    /// </summary>
    private static Guid DeterministicUserSecretsId(string packageName)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(packageName));
        return new Guid(hash);
    }

    private static GeneratedFile EmitAppSettings(ProjectRequest request)
    {
        var files = new JsonObject();
        foreach (var entry in request.FileSourceEntries)
        {
            files[entry.Key] = new JsonObject
            {
                ["SourceFolder"] = entry.SourceFolder,
                ["SourceFileName"] = entry.SourceFileName,
            };
        }

        var targetDatabase = new JsonObject { ["AuthMode"] = request.DatabaseAuth.AuthMode };
        if (request.DatabaseAuth.UserId is not null)
            targetDatabase["UserId"] = request.DatabaseAuth.UserId;
        targetDatabase["ApplicationName"] = request.PackageName;

        var notification = new JsonObject
        {
            ["OnFailureRecipients"] = new JsonArray(request.OnFailureRecipients.Select(r => (JsonNode)r).ToArray()),
            ["OnSuccessRecipients"] = new JsonArray(request.OnSuccessRecipients.Select(r => (JsonNode)r).ToArray()),
        };

        var root = new JsonObject
        {
            ["FileSource"] = new JsonObject { ["Files"] = files },
            ["TargetDatabase"] = targetDatabase,
            ["Notification"] = notification,
        };

        // JsonNode.ToJsonString's line endings/indentation are a serializer implementation
        // detail, not something to trust blindly against the LF-only rule every other emitter
        // in this project follows -- normalize explicitly rather than assume.
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var normalized = json.Replace("\r\n", "\n").TrimEnd('\n') + "\n";

        return new GeneratedFile("appsettings.json", normalized);
    }
}
