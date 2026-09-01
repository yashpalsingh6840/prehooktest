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

/// <summary>One Execute SQL Task's own connection manager, when it resolves to a DIFFERENT
/// server/database than the package's primary <see cref="DatabaseAuthRequest"/> -- e.g.
/// RBC_Demo_ETL's own <c>CM_SQL_SSISDemoCache</c>, a second database the primary
/// TargetDatabase/appsettings.Shared.json config has no way to describe. Unlike
/// <see cref="DatabaseAuthRequest"/>, <see cref="Server"/>/<see cref="Database"/> are written
/// directly into THIS package's own appsettings.json rather than the portfolio-wide shared
/// file -- a secondary connection is inherently package-specific, so there is no shared file
/// for it to belong to. Never a password here either, same rule as everywhere else: User
/// Secrets/an environment variable only.</summary>
public sealed record SecondaryConnectionRequest(
    string ConnectionManagerName, string AuthMode, string? UserId, string Server, string Database);

public sealed record ProjectRequest(
    string PackageName,
    List<FileSourceEntryRequest> FileSourceEntries,
    DatabaseAuthRequest DatabaseAuth,
    List<string> OnSuccessRecipients,
    List<string> OnFailureRecipients,
    List<SecondaryConnectionRequest>? SecondaryConnections = null)
{
    /// <summary>Defaults to empty so every pre-existing call site (none of which knows about
    /// secondary connections) keeps compiling unchanged.</summary>
    public List<SecondaryConnectionRequest> SecondaryConnections { get; init; } = SecondaryConnections ?? [];
}

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

        // A secondary connection can need its own SQL-auth secret even when the PRIMARY one is
        // Windows-auth (not evidenced anywhere yet, but a real possible shape) -- so this checks
        // every connection this package uses, not just DatabaseAuth.
        var needsUserSecrets = request.DatabaseAuth.AuthMode == "SqlServer"
            || request.SecondaryConnections.Any(s => s.AuthMode == "SqlServer");

        if (needsUserSecrets)
        {
            lines.Add("  <ItemGroup>");
            lines.Add("    <PackageReference Include=\"Microsoft.Extensions.Configuration.UserSecrets\" />");
            lines.Add("  </ItemGroup>");
            lines.Add("");
        }

        lines.Add("  <PropertyGroup>");
        lines.Add("    <OutputType>Exe</OutputType>");
        if (needsUserSecrets)
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

        // Server/Database live HERE, unlike TargetDatabase's own (Shared/appsettings.Shared.json) --
        // a secondary connection is package-specific, so there is no portfolio-wide shared file
        // for it to belong to. Same shape as that shared file's own TargetDatabase block
        // (Encrypt/TrustServerCertificate/ConnectTimeoutSeconds), minus Password -- never written,
        // same rule as everywhere else: User Secrets ("SecondaryConnections:<CM name>:Password")
        // or an environment variable only.
        if (request.SecondaryConnections.Count > 0)
        {
            var secondaryConnections = new JsonObject();
            foreach (var secondary in request.SecondaryConnections)
            {
                var entry = new JsonObject
                {
                    ["Server"] = secondary.Server,
                    ["Database"] = secondary.Database,
                    ["AuthMode"] = secondary.AuthMode,
                };
                if (secondary.UserId is not null)
                    entry["UserId"] = secondary.UserId;
                entry["Encrypt"] = true;
                entry["TrustServerCertificate"] = true;
                entry["ConnectTimeoutSeconds"] = 15;
                secondaryConnections[secondary.ConnectionManagerName] = entry;
            }
            root["SecondaryConnections"] = secondaryConnections;
        }

        // JsonNode.ToJsonString's line endings/indentation are a serializer implementation
        // detail, not something to trust blindly against the LF-only rule every other emitter
        // in this project follows -- normalize explicitly rather than assume.
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var normalized = json.Replace("\r\n", "\n").TrimEnd('\n') + "\n";

        return new GeneratedFile("appsettings.json", normalized);
    }
}
