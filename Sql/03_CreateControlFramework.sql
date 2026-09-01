/*
    03_CreateControlFramework.sql
    -----------------------------
    The "ctl" control framework: the only SSIS-aware SQL in the system.

    Everything an application needs in order to run packages is exposed as
    stored procedures. The caller passes logical package keys and a flat
    bag of name/value parameters as JSON, and never references SSISDB, the
    catalog schema, @object_type codes, or execution status numbers.

        ctl.usp_StartPackage    -- start ONE package, fire-and-forget
        ctl.usp_StartRun        -- start N packages in ONE round trip
        ctl.usp_PollRun         -- status of one run, ONE round trip
        ctl.usp_PollCorrelation -- status of every run sharing a CorrelationId

    There is no separate "retry" procedure: calling usp_StartPackage or
    usp_StartRun again IS the retry. Pass @RetryOfRunId so the new run
    records what it is a retry of, and a higher @LoggingLevel if you want
    verbose detail this time.

    usp_DispatchRunItems and usp_RefreshRunItems are internal helpers shared
    by the procedures above; applications do not call them directly.
    usp_StartPackage is itself a thin wrapper: it builds a one-item manifest
    and calls usp_StartRun, so validation, dispatch, and the concurrency
    guard exist in exactly one place.

    Usage:
      sqlcmd -S ".\SQLFORPOC_2022" -E -i Sql\03_CreateControlFramework.sql

    Prerequisites: Sql\01_CreateTarget.sql, project deployed to the catalog,
    and Sql\02_SetupCatalogEnvironment.sql.

    ------------------------------------------------------------------
    WHY A WRAPPER AT ALL
    ------------------------------------------------------------------
    SSISDB has no bulk parameter API: every parameter costs a separate
    catalog.set_execution_parameter_value call. Fifty packages with eight
    parameters each is ~450 statements. Those statements still happen -- but
    inside the server, where they cost microseconds, instead of across the
    network from a pod. Starting 50 packages costs the caller ONE round trip.

    ------------------------------------------------------------------
    PoC SHORTCUT -- NOT PRODUCTION SHAPE
    ------------------------------------------------------------------
    These procedures live in SsisPoC and call across the database boundary
    into SSISDB. Ownership chaining does not cross databases, so this only
    works because the caller is a local sysadmin. A real deployment must
    sign these procedures with a certificate whose user holds the SSISDB
    permissions, so the application login needs no rights in SSISDB at all.
    Do NOT reach for TRUSTWORTHY ON as the shortcut -- it is a documented
    privilege-escalation path. See CLAUDE.md.

    Also omitted deliberately for the PoC: a maxParallel dispatcher and
    per-package timeout enforcement. SSISDB provides neither natively.
    The seam for both is marked in ctl.usp_RefreshRunItems.
*/

USE SsisPoC;
GO

-- Required for the filtered index below (IX_Run_CorrelationId); sqlcmd does
-- not set this ON by default.
SET QUOTED_IDENTIFIER ON;
GO

IF SCHEMA_ID('ctl') IS NULL
    EXEC('CREATE SCHEMA ctl');
GO

/* =================================================================
   TABLES
   ================================================================= */

/* Logical package name -> physical catalog location. The application refers
   to packages only by PackageKey, so moving a package to another project
   later is a row update here rather than an application change. */
IF OBJECT_ID('ctl.PackageRegistry') IS NULL
CREATE TABLE ctl.PackageRegistry
(
    PackageKey       varchar(60)   NOT NULL CONSTRAINT PK_PackageRegistry PRIMARY KEY,
    FolderName       nvarchar(128) NOT NULL,
    ProjectName      nvarchar(128) NOT NULL,
    PackageName      nvarchar(260) NOT NULL,
    EnvironmentName  nvarchar(128) NULL,        -- NULL = run without an environment reference
    Use32BitRuntime  bit           NOT NULL CONSTRAINT DF_PackageRegistry_Use32 DEFAULT (0),
    TimeoutSeconds   int           NULL,        -- reserved: enforced by the dispatcher (not in PoC)
    IsEnabled        bit           NOT NULL CONSTRAINT DF_PackageRegistry_Enabled DEFAULT (1)
);
GO

IF OBJECT_ID('ctl.Run') IS NULL
CREATE TABLE ctl.Run
(
    RunId          bigint        NOT NULL IDENTITY(1,1) CONSTRAINT PK_Run PRIMARY KEY,
    CorrelationId  nvarchar(100) NULL,          -- caller's own id, for log correlation
    RequestedBy    sysname       NOT NULL CONSTRAINT DF_Run_By  DEFAULT (SUSER_SNAME()),
    RequestedAtUtc datetime2(3)  NOT NULL CONSTRAINT DF_Run_At  DEFAULT (SYSUTCDATETIME()),
    State          varchar(20)   NOT NULL CONSTRAINT DF_Run_State DEFAULT ('Running'),
    RetryOfRunId   bigint        NULL CONSTRAINT FK_Run_Retry REFERENCES ctl.Run(RunId)
);
GO

IF OBJECT_ID('ctl.RunItem') IS NULL
CREATE TABLE ctl.RunItem
(
    RunItemId       bigint         NOT NULL IDENTITY(1,1) CONSTRAINT PK_RunItem PRIMARY KEY,
    RunId           bigint         NOT NULL CONSTRAINT FK_RunItem_Run REFERENCES ctl.Run(RunId),
    PackageKey      varchar(60)    NOT NULL,
    ParametersJson  nvarchar(max)  NULL,         -- the exact overrides used: audit trail
    EnvironmentName nvarchar(128)  NULL,         -- this run's override; NULL = use registry default
    ExecutionId     bigint         NULL,         -- SSISDB execution_id once started
    State           varchar(20)    NOT NULL CONSTRAINT DF_RunItem_State DEFAULT ('Pending'),
    Attempt         int            NOT NULL CONSTRAINT DF_RunItem_Attempt DEFAULT (1),
    StartedAtUtc    datetime2(3)   NULL,
    EndedAtUtc      datetime2(3)   NULL,
    ErrorMessage    nvarchar(max)  NULL
);
GO

-- Migration guard: ctl.RunItem already exists on instances built before this
-- column was added. EnvironmentName replaces the old behaviour of writing an
-- item's environment override back onto ctl.PackageRegistry (see usp_StartRun).
IF COL_LENGTH('ctl.RunItem', 'EnvironmentName') IS NULL
    ALTER TABLE ctl.RunItem ADD EnvironmentName nvarchar(128) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RunItem_RunId')
    CREATE INDEX IX_RunItem_RunId ON ctl.RunItem(RunId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Run_CorrelationId')
    CREATE INDEX IX_Run_CorrelationId ON ctl.Run(CorrelationId) WHERE CorrelationId IS NOT NULL;
GO

/* ----------------------------------------------------------------
   Registry seed. LOAD_EMPLOYEES uses only project parameters;
   LOAD_REFERENCE_DATA uses package parameters as well. The framework
   resolves which is which from catalog metadata, so the caller does
   not need to know.
   ---------------------------------------------------------------- */
MERGE ctl.PackageRegistry AS tgt
USING (VALUES
    ('LOAD_EMPLOYEES',      N'SSIS PoC', N'SSIS', N'LoadEmployees.dtsx',     N'Dev'),
    ('LOAD_REFERENCE_DATA', N'SSIS PoC', N'SSIS', N'LoadReferenceData.dtsx', N'Dev')
) AS src (PackageKey, FolderName, ProjectName, PackageName, EnvironmentName)
ON tgt.PackageKey = src.PackageKey
WHEN MATCHED THEN UPDATE SET
    FolderName      = src.FolderName,
    ProjectName     = src.ProjectName,
    PackageName     = src.PackageName,
    EnvironmentName = src.EnvironmentName
WHEN NOT MATCHED THEN
    INSERT (PackageKey, FolderName, ProjectName, PackageName, EnvironmentName)
    VALUES (src.PackageKey, src.FolderName, src.ProjectName, src.PackageName, src.EnvironmentName);
GO

/* =================================================================
   ctl.usp_DispatchRunItems  (internal)

   Creates and starts an SSISDB execution for every Pending item of a run.

   This is where all the SSIS-specific vocabulary is concentrated:

     * A parameter's @object_type (20 = project, 30 = package) and its
       declared data type are both looked up from catalog.object_parameters.
       That is why the caller can pass a flat name/value bag: the framework
       discovers the scope and the type from the catalog itself. A package
       parameter wins over a project parameter of the same name, matching
       how $Package:: shadows $Project::.

     * set_execution_parameter_value takes sql_variant, so each JSON string
       is cast to the parameter's real type first. The casts are in IF/ELSE
       rather than a CASE expression on purpose -- SQL Server may evaluate
       every branch of a CASE, which would throw converting "abc" to int
       on a branch that is not even selected.

     * start_execution runs ASYNCHRONOUSLY (no SYNCHRONIZED setting). The
       caller's connection is not held for the package duration, and a pod
       that dies mid-run loses nothing: the package keeps running in SQL
       Server and any caller can resume by polling the RunId.

   Each item is wrapped individually, so one package failing to start does
   not prevent the rest of the batch from starting.
   ================================================================= */
CREATE OR ALTER PROCEDURE ctl.usp_DispatchRunItems
    @RunId        bigint,
    @LoggingLevel int = 1        -- 0=None 1=Basic 2=Performance 3=Verbose 4=RuntimeLineage
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @RunItemId  bigint,
            @PackageKey varchar(60),
            @ParamsJson nvarchar(max),
            @ItemEnv    nvarchar(128);

    DECLARE item_cur CURSOR LOCAL FAST_FORWARD FOR
        SELECT RunItemId, PackageKey, ParametersJson, EnvironmentName
        FROM ctl.RunItem
        WHERE RunId = @RunId AND State = 'Pending'
        ORDER BY RunItemId;

    OPEN item_cur;
    FETCH NEXT FROM item_cur INTO @RunItemId, @PackageKey, @ParamsJson, @ItemEnv;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        BEGIN TRY
            DECLARE @Folder    nvarchar(128),
                    @Project   nvarchar(128),
                    @Package   nvarchar(260),
                    @EnvName   nvarchar(128),
                    @Use32     bit;

            -- This run's own override wins over the registry default; neither
            -- is stored back onto ctl.PackageRegistry, so it never leaks into
            -- a later, unrelated call for the same PackageKey.
            SELECT @Folder  = FolderName,
                   @Project = ProjectName,
                   @Package = PackageName,
                   @EnvName = COALESCE(@ItemEnv, EnvironmentName),
                   @Use32   = Use32BitRuntime
            FROM ctl.PackageRegistry
            WHERE PackageKey = @PackageKey;

            /* Environment reference, if the registry names one. */
            DECLARE @RefId bigint = NULL;
            IF @EnvName IS NOT NULL
            BEGIN
                SELECT @RefId = er.reference_id
                FROM SSISDB.catalog.environment_references er
                JOIN SSISDB.catalog.projects p ON p.project_id = er.project_id
                JOIN SSISDB.catalog.folders  f ON f.folder_id  = p.folder_id
                WHERE f.name = @Folder
                  AND p.name = @Project
                  AND er.environment_name = @EnvName;

                IF @RefId IS NULL
                    THROW 50010, 'Environment reference not found for this project.', 1;
            END;

            DECLARE @ExecId bigint;

            EXEC SSISDB.catalog.create_execution
                 @folder_name     = @Folder,
                 @project_name    = @Project,
                 @package_name    = @Package,
                 @reference_id    = @RefId,
                 @use32bitruntime = @Use32,
                 @execution_id    = @ExecId OUTPUT;

            /* Logging level is an execution SETTING: object_type 50. */
            EXEC SSISDB.catalog.set_execution_parameter_value
                 @ExecId, @object_type = 50,
                 @parameter_name = N'LOGGING_LEVEL', @parameter_value = @LoggingLevel;

            /* ---- per-run parameter overrides ---- */
            IF @ParamsJson IS NOT NULL AND @ParamsJson <> N'{}'
            BEGIN
                DECLARE @pName nvarchar(128), @pRaw nvarchar(4000);

                DECLARE p_cur CURSOR LOCAL FAST_FORWARD FOR
                    SELECT [key], [value] FROM OPENJSON(@ParamsJson);

                OPEN p_cur;
                FETCH NEXT FROM p_cur INTO @pName, @pRaw;

                WHILE @@FETCH_STATUS = 0
                BEGIN
                    DECLARE @objType int, @dataType nvarchar(128);

                    SELECT TOP 1 @objType  = op.object_type,
                                 @dataType = op.data_type
                    FROM SSISDB.catalog.object_parameters op
                    JOIN SSISDB.catalog.projects p ON p.project_id = op.project_id
                    JOIN SSISDB.catalog.folders  f ON f.folder_id  = p.folder_id
                    WHERE f.name = @Folder
                      AND p.name = @Project
                      AND op.parameter_name = @pName
                      AND (op.object_type = 20
                           OR (op.object_type = 30 AND op.object_name = @Package))
                    ORDER BY op.object_type DESC;   -- package (30) shadows project (20)

                    IF @objType IS NULL
                        THROW 50011, 'Unknown parameter name for this package.', 1;

                    DECLARE @pVal sql_variant;
                    IF      @dataType = 'Int32'    SET @pVal = CAST(CAST(@pRaw AS int)            AS sql_variant);
                    ELSE IF @dataType = 'Int16'    SET @pVal = CAST(CAST(@pRaw AS smallint)       AS sql_variant);
                    ELSE IF @dataType = 'Int64'    SET @pVal = CAST(CAST(@pRaw AS bigint)         AS sql_variant);
                    ELSE IF @dataType = 'Byte'     SET @pVal = CAST(CAST(@pRaw AS tinyint)        AS sql_variant);
                    ELSE IF @dataType = 'Boolean'  SET @pVal = CAST(CAST(@pRaw AS bit)            AS sql_variant);
                    ELSE IF @dataType = 'DateTime' SET @pVal = CAST(CAST(@pRaw AS datetime2(3))   AS sql_variant);
                    ELSE IF @dataType = 'Decimal'  SET @pVal = CAST(CAST(@pRaw AS decimal(28,10)) AS sql_variant);
                    ELSE IF @dataType = 'Double'   SET @pVal = CAST(CAST(@pRaw AS float)          AS sql_variant);
                    ELSE IF @dataType = 'Single'   SET @pVal = CAST(CAST(@pRaw AS real)           AS sql_variant);
                    ELSE                           SET @pVal = CAST(@pRaw AS sql_variant);

                    EXEC SSISDB.catalog.set_execution_parameter_value
                         @ExecId,
                         @object_type     = @objType,
                         @parameter_name  = @pName,
                         @parameter_value = @pVal;

                    SET @objType = NULL; SET @dataType = NULL;
                    FETCH NEXT FROM p_cur INTO @pName, @pRaw;
                END;

                CLOSE p_cur;
                DEALLOCATE p_cur;
            END;

            EXEC SSISDB.catalog.start_execution @ExecId;   -- asynchronous

            UPDATE ctl.RunItem
            SET ExecutionId  = @ExecId,
                State        = 'Running',
                StartedAtUtc = SYSUTCDATETIME()
            WHERE RunItemId = @RunItemId;
        END TRY
        BEGIN CATCH
            IF CURSOR_STATUS('local','p_cur') >= 0
            BEGIN
                CLOSE p_cur;
                DEALLOCATE p_cur;
            END;

            UPDATE ctl.RunItem
            SET State        = 'Failed',
                EndedAtUtc   = SYSUTCDATETIME(),
                ErrorMessage = CONCAT('Failed to start: ', ERROR_MESSAGE())
            WHERE RunItemId = @RunItemId;
        END CATCH;

        FETCH NEXT FROM item_cur INTO @RunItemId, @PackageKey, @ParamsJson, @ItemEnv;
    END;

    CLOSE item_cur;
    DEALLOCATE item_cur;
END;
GO

/* =================================================================
   ctl.usp_RefreshRunItems  (internal)

   Pulls current status from SSISDB for one run's in-flight items and rolls
   the result up onto ctl.Run. The single source of truth for the SSISDB
   status-code mapping (1=created 2=running 3=cancelled 4=failed 5=pending
   6=ended unexpectedly 7=succeeded 8=stopping 9=completed) -- usp_PollRun,
   usp_PollCorrelation, and usp_StartRun's concurrency guard all call this
   instead of each keeping their own copy of the mapping.
   ================================================================= */
CREATE OR ALTER PROCEDURE ctl.usp_RefreshRunItems
    @RunId bigint
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE ri
    SET State = CASE
                    WHEN e.status IN (7, 9) THEN 'Succeeded'
                    WHEN e.status = 3       THEN 'Cancelled'
                    WHEN e.status IN (4, 6) THEN 'Failed'
                    ELSE 'Running'
                END,
        EndedAtUtc = CASE WHEN e.status IN (3, 4, 6, 7, 9)
                          THEN COALESCE(ri.EndedAtUtc, SYSUTCDATETIME()) END
    FROM ctl.RunItem ri
    JOIN SSISDB.catalog.executions e ON e.execution_id = ri.ExecutionId
    WHERE ri.RunId = @RunId
      AND ri.State NOT IN ('Succeeded', 'Failed', 'Cancelled');

    /* First error message for anything that failed and has none recorded. */
    UPDATE ri
    SET ErrorMessage = x.message
    FROM ctl.RunItem ri
    CROSS APPLY (
        SELECT TOP 1 om.message
        FROM SSISDB.catalog.operation_messages om
        WHERE om.operation_id = ri.ExecutionId
          AND om.message_type = 120          -- 120 = error, 110 = warning
        ORDER BY om.message_time
    ) x
    WHERE ri.RunId = @RunId
      AND ri.State IN ('Failed', 'Cancelled')
      AND ri.ErrorMessage IS NULL;

    /* ------------------------------------------------------------------
       SEAM: a production dispatcher belongs here. Having refreshed state,
       this is the point at which you would (a) start Pending items while
       fewer than maxParallel are Running, and (b) call
       catalog.stop_operation on anything past PackageRegistry.TimeoutSeconds.
       SSISDB provides neither a concurrency limit nor a timeout natively.
       Omitted from the PoC because two packages cannot exercise it.
       ------------------------------------------------------------------ */

    /* Roll the item states up to the run. */
    UPDATE ctl.Run
    SET State = CASE
                    WHEN EXISTS (SELECT 1 FROM ctl.RunItem
                                 WHERE RunId = @RunId
                                   AND State NOT IN ('Succeeded','Failed','Cancelled'))
                         THEN 'Running'
                    WHEN EXISTS (SELECT 1 FROM ctl.RunItem
                                 WHERE RunId = @RunId AND State <> 'Succeeded')
                         THEN 'Failed'
                    ELSE 'Succeeded'
                END
    WHERE RunId = @RunId;
END;
GO

/* =================================================================
   ctl.usp_StartRun  (public)

   ONE call starts every package in the manifest.

   @Manifest shape:
     {
       "items": [
         { "packageKey": "LOAD_EMPLOYEES",
           "environment": "Dev",                       -- optional, overrides registry
           "parameters": { "SourceFileName": "Employees.csv" } }
       ]
     }

   All validations below happen BEFORE anything starts. An unknown package
   key, a misspelled parameter name, or (unless @AllowConcurrent = 1) a
   package already running otherwise surfaces as a package failure minutes
   into the run, which is a miserable way to learn about a typo.
   ================================================================= */
CREATE OR ALTER PROCEDURE ctl.usp_StartRun
    @Manifest        nvarchar(max),
    @CorrelationId   nvarchar(100) = NULL,
    @LoggingLevel    int           = 1,     -- 1=Basic (normal), 3=Verbose (retries)
    @RetryOfRunId    bigint        = NULL,  -- lineage only; does not fetch old params
    @AllowConcurrent bit           = 0,     -- 0 = reject if any item's PackageKey is already Running
    @RunId           bigint        = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    IF ISJSON(@Manifest) <> 1
        THROW 50001, 'Manifest is not valid JSON.', 1;

    DECLARE @items TABLE
    (
        Ordinal     int IDENTITY(1,1),
        PackageKey  varchar(60),
        Environment nvarchar(128),
        ParamsJson  nvarchar(max)
    );

    INSERT INTO @items (PackageKey, Environment, ParamsJson)
    SELECT j.packageKey, j.environment, j.parameters
    FROM OPENJSON(@Manifest, '$.items')
    WITH (
        packageKey  varchar(60)   '$.packageKey',
        environment nvarchar(128) '$.environment',
        parameters  nvarchar(max) '$.parameters' AS JSON
    ) j;

    IF NOT EXISTS (SELECT 1 FROM @items)
        THROW 50002, 'Manifest contains no items.', 1;

    /* --- validation 1: every package key is known and enabled --- */
    DECLARE @badKeys nvarchar(max);
    SELECT @badKeys = STRING_AGG(i.PackageKey, ', ')
    FROM @items i
    LEFT JOIN ctl.PackageRegistry r
           ON r.PackageKey = i.PackageKey AND r.IsEnabled = 1
    WHERE r.PackageKey IS NULL;

    IF @badKeys IS NOT NULL
    BEGIN
        DECLARE @m1 nvarchar(2048) = CONCAT('Unknown or disabled package key(s): ', @badKeys);
        THROW 50003, @m1, 1;
    END;

    /* --- validation 2: every parameter name exists on its package ---

       SSISDB is created with collation Latin1_General_BIN2, while this
       database uses the server default, so every COLUMN-to-COLUMN comparison
       across the two needs an explicit COLLATE or it fails with
       "Cannot resolve the collation conflict". Comparisons against a
       variable do not need it -- a variable is coercible and simply adopts
       the column's collation, which is why the cursor in
       usp_DispatchRunItems has no COLLATE clauses. */
    DECLARE @badParams nvarchar(max);
    SELECT @badParams = STRING_AGG(CONCAT(x.PackageKey, '.', x.ParamName), ', ')
    FROM (
        /* OPENJSON emits its key column as Latin1_General_BIN2 as well --
           JSON keys are case-sensitive by spec -- so it needs collating back
           to the local default before being concatenated with PackageKey. */
        SELECT i.PackageKey, p.[key] COLLATE DATABASE_DEFAULT AS ParamName
        FROM @items i
        JOIN ctl.PackageRegistry r ON r.PackageKey = i.PackageKey
        CROSS APPLY OPENJSON(i.ParamsJson) p
        WHERE i.ParamsJson IS NOT NULL
          AND NOT EXISTS (
              SELECT 1
              FROM SSISDB.catalog.object_parameters op
              JOIN SSISDB.catalog.projects pr ON pr.project_id = op.project_id
              JOIN SSISDB.catalog.folders  f  ON f.folder_id   = pr.folder_id
              WHERE f.name  COLLATE DATABASE_DEFAULT = r.FolderName
                AND pr.name COLLATE DATABASE_DEFAULT = r.ProjectName
                AND op.parameter_name COLLATE DATABASE_DEFAULT = p.[key]
                AND (op.object_type = 20
                     OR (op.object_type = 30
                         AND op.object_name COLLATE DATABASE_DEFAULT = r.PackageName))
          )
    ) x;

    IF @badParams IS NOT NULL
    BEGIN
        DECLARE @m2 nvarchar(2048) = CONCAT('Unknown parameter(s): ', @badParams);
        THROW 50004, @m2, 1;
    END;

    /* --- validation 3: none of these package keys already has a Running
       item, unless the caller opted into concurrency. A RunItem only ever
       reaches a terminal state via usp_RefreshRunItems, so a run nobody has
       polled since it finished would still read 'Running' here -- refresh
       first, or an unpolled-but-finished run would permanently block reuse
       of that PackageKey. --- */
    IF @AllowConcurrent = 0
    BEGIN
        DECLARE @busyRunId bigint;
        DECLARE busy_cur CURSOR LOCAL FAST_FORWARD FOR
            SELECT DISTINCT ri.RunId
            FROM ctl.RunItem ri
            JOIN @items i ON i.PackageKey = ri.PackageKey
            WHERE ri.State = 'Running';

        OPEN busy_cur;
        FETCH NEXT FROM busy_cur INTO @busyRunId;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            EXEC ctl.usp_RefreshRunItems @RunId = @busyRunId;
            FETCH NEXT FROM busy_cur INTO @busyRunId;
        END;
        CLOSE busy_cur;
        DEALLOCATE busy_cur;

        DECLARE @busyKeys nvarchar(max);
        SELECT @busyKeys = STRING_AGG(PackageKey, ', ')
        FROM (
            SELECT DISTINCT ri.PackageKey
            FROM ctl.RunItem ri
            JOIN @items i ON i.PackageKey = ri.PackageKey
            WHERE ri.State = 'Running'
        ) x;

        IF @busyKeys IS NOT NULL
        BEGIN
            DECLARE @m3 nvarchar(2048) = CONCAT('Package(s) already running: ', @busyKeys);
            THROW 50012, @m3, 1;
        END;
    END;

    /* --- record the run --- */
    INSERT INTO ctl.Run (CorrelationId, RetryOfRunId) VALUES (@CorrelationId, @RetryOfRunId);
    SET @RunId = SCOPE_IDENTITY();

    /* Each item's own environment override (if any) lives on the RunItem,
       not on ctl.PackageRegistry -- a one-off override must never leak into
       a later, unrelated call for the same PackageKey. */
    INSERT INTO ctl.RunItem (RunId, PackageKey, ParametersJson, EnvironmentName)
    SELECT @RunId, i.PackageKey, i.ParamsJson, i.Environment
    FROM @items i
    ORDER BY i.Ordinal;

    EXEC ctl.usp_DispatchRunItems @RunId = @RunId, @LoggingLevel = @LoggingLevel;

    SELECT RunItemId, PackageKey, ExecutionId, State, ErrorMessage
    FROM ctl.RunItem
    WHERE RunId = @RunId
    ORDER BY RunItemId;
END;
GO

/* =================================================================
   ctl.usp_PollRun  (public)

   ONE call returns the status of every package in the run, whether that is
   2 packages or 50. Safe to call repeatedly, and safe to call from a
   different process than the one that started the run.

   Failure detail is pulled from catalog.operation_messages, which joins to
   executions on operation_id = execution_id -- not an obvious relationship
   from the column names.
   ================================================================= */
CREATE OR ALTER PROCEDURE ctl.usp_PollRun
    @RunId    bigint,
    @RunState varchar(20) = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM ctl.Run WHERE RunId = @RunId)
        THROW 50005, 'No such RunId.', 1;

    EXEC ctl.usp_RefreshRunItems @RunId = @RunId;

    SELECT @RunState = State FROM ctl.Run WHERE RunId = @RunId;

    SELECT
        ri.RunItemId,
        ri.PackageKey,
        ri.ExecutionId,
        ri.State,
        ri.Attempt,
        ri.StartedAtUtc,
        ri.EndedAtUtc,
        DATEDIFF(second, ri.StartedAtUtc, COALESCE(ri.EndedAtUtc, SYSUTCDATETIME())) AS ElapsedSeconds,
        ri.ErrorMessage
    FROM ctl.RunItem ri
    WHERE ri.RunId = @RunId
    ORDER BY ri.RunItemId;
END;
GO

/* =================================================================
   ctl.usp_StartPackage  (public)

   Starts ONE package, fire-and-forget, and returns its RunId immediately --
   the single-package counterpart to usp_StartRun. It is a thin wrapper: it
   builds a one-item manifest and calls usp_StartRun, so validation,
   dispatch, and the concurrency guard all live in exactly one place.

   There is no separate retry procedure. To retry, call this again with the
   same (or corrected) parameters, pass the earlier RunId as @RetryOfRunId
   for lineage, and raise @LoggingLevel to 3 if you want verbose detail.
   ================================================================= */
CREATE OR ALTER PROCEDURE ctl.usp_StartPackage
    @PackageKey      varchar(60),
    @Parameters      nvarchar(max) = NULL,   -- flat {"Name":"Value"}, same as a manifest item
    @CorrelationId   nvarchar(100) = NULL,
    @Environment     nvarchar(128) = NULL,   -- optional, this call only -- see usp_StartRun
    @LoggingLevel    int           = 1,
    @RetryOfRunId    bigint        = NULL,
    @AllowConcurrent bit           = 0,
    @RunId           bigint        = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    IF @Parameters IS NOT NULL AND ISJSON(@Parameters) <> 1
        THROW 50013, 'Parameters is not valid JSON.', 1;

    DECLARE @Manifest nvarchar(max) =
    (
        SELECT @PackageKey AS packageKey,
               @Environment AS environment,
               JSON_QUERY(COALESCE(@Parameters, N'{}')) AS parameters
        FOR JSON PATH, ROOT('items')
    );

    EXEC ctl.usp_StartRun
         @Manifest        = @Manifest,
         @CorrelationId   = @CorrelationId,
         @LoggingLevel    = @LoggingLevel,
         @RetryOfRunId    = @RetryOfRunId,
         @AllowConcurrent = @AllowConcurrent,
         @RunId           = @RunId OUTPUT;
END;
GO

/* =================================================================
   ctl.usp_PollCorrelation  (public)

   Status of every run sharing a CorrelationId, in one round trip. This is
   what replaces the manifest's one-RunId batch view now that independent
   usp_StartPackage calls have no RunId in common -- callers that still want
   "did last night's load succeed" pass the same @CorrelationId to every
   usp_StartPackage call and poll it here instead of tracking N RunIds
   themselves.
   ================================================================= */
CREATE OR ALTER PROCEDURE ctl.usp_PollCorrelation
    @CorrelationId nvarchar(100)
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM ctl.Run WHERE CorrelationId = @CorrelationId)
        THROW 50014, 'No runs found for that CorrelationId.', 1;

    DECLARE @rid bigint;
    DECLARE run_cur CURSOR LOCAL FAST_FORWARD FOR
        SELECT RunId FROM ctl.Run WHERE CorrelationId = @CorrelationId;

    OPEN run_cur;
    FETCH NEXT FROM run_cur INTO @rid;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC ctl.usp_RefreshRunItems @RunId = @rid;
        FETCH NEXT FROM run_cur INTO @rid;
    END;
    CLOSE run_cur;
    DEALLOCATE run_cur;

    SELECT
        r.RunId,
        r.State AS RunState,
        ri.PackageKey,
        ri.ExecutionId,
        ri.State,
        ri.Attempt,
        ri.StartedAtUtc,
        ri.EndedAtUtc,
        DATEDIFF(second, ri.StartedAtUtc, COALESCE(ri.EndedAtUtc, SYSUTCDATETIME())) AS ElapsedSeconds,
        ri.ErrorMessage
    FROM ctl.Run r
    JOIN ctl.RunItem ri ON ri.RunId = r.RunId
    WHERE r.CorrelationId = @CorrelationId
    ORDER BY r.RunId, ri.RunItemId;
END;
GO

PRINT 'ctl framework created: PackageRegistry, Run, RunItem + usp_StartPackage / usp_StartRun / usp_PollRun / usp_PollCorrelation';
GO
