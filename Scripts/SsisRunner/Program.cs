using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

// SsisRunner - runs SSIS packages through the ctl framework.
//
// The point of this program is what it does NOT contain. There is no
// reference to SSISDB, catalog.*, @object_type, execution status codes, or
// any other SSIS concept. It calls stored procedures. Adding another 48
// packages means 48 more registry rows -- not a line of change here.
//
//   SsisRunner --package LOAD_EMPLOYEES --set SourceFileName=Employees.csv
//   SsisRunner --manifest manifests\nightly.json
//   SsisRunner --status 42 [--wait]
//
// --package and --manifest start work and return immediately by default --
// they print the RunId and exit. Add --wait to poll to completion instead.
// There is no separate retry command: call --package or --manifest again,
// optionally with --retry-of <runId> for lineage and a higher
// --logging-level for verbose detail.
//
// Exit codes: 0 = every package succeeded (or the run was merely started,
//                 when not waiting)
//             1 = at least one package failed
//             2 = the runner itself could not do its job
//             3 = --status without --wait, and the run is still in progress

const string DefaultConnectionString =
    // TrustServerCertificate is required: Microsoft.Data.SqlClient defaults to
    // Encrypt=True with strict validation, and this instance has a self-signed cert.
    "Server=.\\SQLFORPOC;Database=SsisPoC;Integrated Security=true;TrustServerCertificate=true;";

var options = ParseArgs(args);
if (options is null)
{
    Console.Error.WriteLine("""
        usage:
          SsisRunner --package <key> [--set NAME=VALUE ...] [--environment <name>]
                                      [--correlation <id>] [--logging-level <n>] [--retry-of <runId>]
                                      [--allow-concurrent] [--wait]
          SsisRunner --manifest <path> [--correlation <id>] [--logging-level <n>] [--retry-of <runId>]
                                        [--allow-concurrent] [--wait]
          SsisRunner --status <runId> [--wait]

          (any form) [--connection <connection-string>] [--poll-seconds <n>]

        There is no --retry: call --package or --manifest again, optionally with
        --retry-of <runId> for lineage.
        """);
    return 2;
}

try
{
    await using var conn = new SqlConnection(options.ConnectionString);
    await conn.OpenAsync();

    if (options.Mode == Mode.Status)
        return await HandleStatusAsync(conn, options.StatusOfRunId!.Value, options);

    var (runId, correlationId) = options.Mode == Mode.Package
        ? await StartPackageAsync(conn, options)
        : await StartRunAsync(conn, options);

    Console.WriteLine($"RunId {runId} started (correlation: {correlationId}).");

    if (!options.Wait)
    {
        Console.WriteLine($"Not waiting. Check later with:  SsisRunner --status {runId} [--wait]");
        return 0;
    }

    return await WaitForCompletionAsync(conn, runId, options.PollSeconds);
}
catch (SqlException ex)
{
    // Validation failures from usp_StartRun/usp_StartPackage (unknown package
    // key, unknown parameter, already running) arrive here as THROW 5000x
    // before anything has started.
    Console.Error.WriteLine($"SQL error {ex.Number}: {ex.Message}");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 2;
}

// ---------------------------------------------------------------------------

static async Task<(long RunId, string CorrelationId)> StartPackageAsync(SqlConnection conn, Options o)
{
    string correlationId = o.CorrelationId ?? GenerateCorrelationId();
    string? paramsJson = o.ParameterOverrides.Count > 0 ? JsonSerializer.Serialize(o.ParameterOverrides) : null;

    using var cmd = new SqlCommand("ctl.usp_StartPackage", conn) { CommandType = CommandType.StoredProcedure };
    cmd.Parameters.Add("@PackageKey", SqlDbType.VarChar, 60).Value = o.PackageKey!;
    cmd.Parameters.Add("@Parameters", SqlDbType.NVarChar, -1).Value = (object?)paramsJson ?? DBNull.Value;
    cmd.Parameters.Add("@CorrelationId", SqlDbType.NVarChar, 100).Value = correlationId;
    cmd.Parameters.Add("@Environment", SqlDbType.NVarChar, 128).Value = (object?)o.Environment ?? DBNull.Value;
    cmd.Parameters.Add("@LoggingLevel", SqlDbType.Int).Value = o.LoggingLevel;
    cmd.Parameters.Add("@RetryOfRunId", SqlDbType.BigInt).Value = (object?)o.RetryOfRunId ?? DBNull.Value;
    cmd.Parameters.Add("@AllowConcurrent", SqlDbType.Bit).Value = o.AllowConcurrent;
    var runIdParam = cmd.Parameters.Add("@RunId", SqlDbType.BigInt);
    runIdParam.Direction = ParameterDirection.Output;

    await cmd.ExecuteNonQueryAsync();
    return ((long)runIdParam.Value, correlationId);
}

static async Task<(long RunId, string CorrelationId)> StartRunAsync(SqlConnection conn, Options o)
{
    var manifestJson = await File.ReadAllTextAsync(o.ManifestPath!);

    // A CLI --correlation wins; otherwise fall back to the manifest's own
    // "correlationId" field, then to an auto-generated one.
    string? correlationId = o.CorrelationId;
    if (correlationId is null)
    {
        using var doc = JsonDocument.Parse(manifestJson);
        if (doc.RootElement.TryGetProperty("correlationId", out var c))
            correlationId = c.GetString();
    }
    correlationId ??= GenerateCorrelationId();

    using var cmd = new SqlCommand("ctl.usp_StartRun", conn) { CommandType = CommandType.StoredProcedure };
    cmd.Parameters.Add("@Manifest", SqlDbType.NVarChar, -1).Value = manifestJson;
    cmd.Parameters.Add("@CorrelationId", SqlDbType.NVarChar, 100).Value = correlationId;
    cmd.Parameters.Add("@LoggingLevel", SqlDbType.Int).Value = o.LoggingLevel;
    cmd.Parameters.Add("@RetryOfRunId", SqlDbType.BigInt).Value = (object?)o.RetryOfRunId ?? DBNull.Value;
    cmd.Parameters.Add("@AllowConcurrent", SqlDbType.Bit).Value = o.AllowConcurrent;
    var runIdParam = cmd.Parameters.Add("@RunId", SqlDbType.BigInt);
    runIdParam.Direction = ParameterDirection.Output;

    // One round trip starts every package in the manifest.
    await cmd.ExecuteNonQueryAsync();
    return ((long)runIdParam.Value, correlationId);
}

static string GenerateCorrelationId() =>
    $"ssisrunner-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Random.Shared.Next(0x10000):x4}";

static async Task<int> HandleStatusAsync(SqlConnection conn, long runId, Options o)
{
    if (o.Wait)
        return await WaitForCompletionAsync(conn, runId, o.PollSeconds);

    var (state, items) = await PollRunAsync(conn, runId);
    Render(runId, state, items);

    if (string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase))
        return 3;

    return items.Any(i => i.State is "Failed" or "Cancelled") ? 1 : 0;
}

static async Task<int> WaitForCompletionAsync(SqlConnection conn, long runId, int pollSeconds)
{
    Console.WriteLine($"Polling every {pollSeconds}s...");
    Console.WriteLine();

    string state;
    List<RunItem> items;
    while (true)
    {
        (state, items) = await PollRunAsync(conn, runId);
        Render(runId, state, items);

        if (!string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase))
            break;

        await Task.Delay(TimeSpan.FromSeconds(pollSeconds));
    }

    var failed = items.Count(i => i.State is "Failed" or "Cancelled");
    if (failed > 0)
    {
        Console.WriteLine($"{failed} of {items.Count} package(s) failed.");
        Console.WriteLine($"Retry with:  SsisRunner --package <key> --retry-of {runId}  (or --manifest ... --retry-of {runId})");
        return 1;
    }

    Console.WriteLine($"All {items.Count} package(s) succeeded.");
    return 0;
}

static async Task<(string State, List<RunItem> Items)> PollRunAsync(SqlConnection conn, long runId)
{
    using var cmd = new SqlCommand("ctl.usp_PollRun", conn) { CommandType = CommandType.StoredProcedure };
    cmd.Parameters.Add("@RunId", SqlDbType.BigInt).Value = runId;
    var stateParam = cmd.Parameters.Add("@RunState", SqlDbType.VarChar, 20);
    stateParam.Direction = ParameterDirection.Output;

    var items = new List<RunItem>();

    // One round trip returns every package's status, whether 1 or 50.
    using (var reader = await cmd.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync())
        {
            items.Add(new RunItem(
                PackageKey:  reader.GetString(reader.GetOrdinal("PackageKey")),
                ExecutionId: reader.IsDBNull(reader.GetOrdinal("ExecutionId"))
                                 ? null : reader.GetInt64(reader.GetOrdinal("ExecutionId")),
                State:       reader.GetString(reader.GetOrdinal("State")),
                Elapsed:     reader.IsDBNull(reader.GetOrdinal("ElapsedSeconds"))
                                 ? null : reader.GetInt32(reader.GetOrdinal("ElapsedSeconds")),
                Error:       reader.IsDBNull(reader.GetOrdinal("ErrorMessage"))
                                 ? null : reader.GetString(reader.GetOrdinal("ErrorMessage"))));
        }
    }
    // Output parameters are only populated once the reader is closed.
    return ((string)stateParam.Value, items);
}

static void Render(long runId, string state, List<RunItem> items)
{
    Console.WriteLine($"run {runId}  [{state}]  {DateTime.Now:HH:mm:ss}");
    Console.WriteLine($"  {"PACKAGE",-22} {"EXEC",-8} {"STATE",-10} {"SECS",5}");
    foreach (var i in items)
    {
        Console.WriteLine($"  {i.PackageKey,-22} {i.ExecutionId?.ToString() ?? "-",-8} {i.State,-10} {i.Elapsed?.ToString() ?? "-",5}");
        if (i.Error is not null)
            Console.WriteLine($"      -> {Truncate(i.Error, 160)}");
    }
    Console.WriteLine();
}

static string Truncate(string s, int max)
{
    s = s.Replace('\r', ' ').Replace('\n', ' ');
    return s.Length <= max ? s : s[..max] + "...";
}

static Options? ParseArgs(string[] args)
{
    string? packageKey = null, manifest = null, connection = null, correlation = null, environment = null;
    long? statusOf = null, retryOf = null;
    int pollSeconds = 2, loggingLevel = 1;
    bool allowConcurrent = false, wait = false;
    var parameters = new Dictionary<string, string>();

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--package" when i + 1 < args.Length:
                packageKey = args[++i];
                break;
            case "--manifest" when i + 1 < args.Length:
                manifest = args[++i];
                break;
            case "--status" when i + 1 < args.Length && long.TryParse(args[i + 1], out var s):
                statusOf = s; i++;
                break;
            case "--set" when i + 1 < args.Length:
                var kv = args[++i];
                var eq = kv.IndexOf('=');
                if (eq <= 0)
                    return Fail($"invalid --set value (expected NAME=VALUE): {kv}");
                parameters[kv[..eq]] = kv[(eq + 1)..];
                break;
            case "--correlation" when i + 1 < args.Length:
                correlation = args[++i];
                break;
            case "--environment" when i + 1 < args.Length:
                environment = args[++i];
                break;
            case "--logging-level" when i + 1 < args.Length && int.TryParse(args[i + 1], out var ll):
                loggingLevel = ll; i++;
                break;
            case "--retry-of" when i + 1 < args.Length && long.TryParse(args[i + 1], out var ro):
                retryOf = ro; i++;
                break;
            case "--allow-concurrent":
                allowConcurrent = true;
                break;
            case "--wait":
                wait = true;
                break;
            case "--connection" when i + 1 < args.Length:
                connection = args[++i];
                break;
            case "--poll-seconds" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p):
                pollSeconds = p; i++;
                break;
            default:
                return Fail($"unrecognized argument: {args[i]}");
        }
    }

    var modeCount = (packageKey is not null ? 1 : 0) + (manifest is not null ? 1 : 0) + (statusOf is not null ? 1 : 0);
    if (modeCount != 1)
        return Fail("exactly one of --package, --manifest, or --status is required.");

    if (manifest is not null && !File.Exists(manifest))
        return Fail($"manifest not found: {manifest}");

    var mode = packageKey is not null ? Mode.Package : manifest is not null ? Mode.Manifest : Mode.Status;

    return new Options(mode, packageKey, manifest, statusOf, parameters, correlation, environment,
        loggingLevel, retryOf, allowConcurrent, wait, connection ?? DefaultConnectionString, pollSeconds);
}

static Options? Fail(string message)
{
    Console.Error.WriteLine(message);
    return null;
}

internal enum Mode { Package, Manifest, Status }

internal sealed record Options(
    Mode Mode,
    string? PackageKey,
    string? ManifestPath,
    long? StatusOfRunId,
    Dictionary<string, string> ParameterOverrides,
    string? CorrelationId,
    string? Environment,
    int LoggingLevel,
    long? RetryOfRunId,
    bool AllowConcurrent,
    bool Wait,
    string ConnectionString,
    int PollSeconds);

internal sealed record RunItem(string PackageKey, long? ExecutionId, string State, int? Elapsed, string? Error);
