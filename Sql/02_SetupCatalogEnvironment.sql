/*
    02_SetupCatalogEnvironment.sql
    ------------------------------
    Creates an SSISDB catalog Environment ("Dev") in the "SSIS PoC" folder and
    binds the STABLE project parameters of the deployed "SSIS" project to it.

    Prerequisites (see CLAUDE.md):
      - SSISDB catalog exists (created via SMO; "clr enabled" must be 1).
      - Folder "SSIS PoC" exists and project "SSIS" has been deployed into it.

    Re-runnable: drops and recreates the environment each time.

    Usage:
      sqlcmd -S ".\SQLFORPOC" -E -i Sql\02_SetupCatalogEnvironment.sql

    ------------------------------------------------------------------
    WHICH VALUES LIVE HERE, AND WHICH DO NOT
    ------------------------------------------------------------------
    Environment variables hold values that are STABLE for an environment:
    connection strings, base paths. They are global mutable state, so a
    value that changes per run must NOT live here -- two overlapping runs
    would overwrite each other's value and silently load the wrong data,
    with no error raised anywhere.

    Per-run values (file names, business dates, batch ids) are passed as
    EXECUTION PARAMETER OVERRIDES instead. Those are scoped to a single
    execution_id and are safe under concurrency. ctl.usp_StartRun does that
    -- see Sql\03_CreateControlFramework.sql.

    So this script deliberately binds only two of the three project
    parameters. SourceFileName is left on its design default so the caller
    can override it per run. (Binding does not prevent overriding: an
    execution override outranks an environment value.)

    ------------------------------------------------------------------
    SENSITIVE VALUES  (@sensitive = 1)
    ------------------------------------------------------------------
    A variable created with @sensitive = 1 is encrypted by SSISDB:
      - encrypted with that environment's own symmetric key
        (MS_Enckey_Env_<environment_id>, protected by MS_Cert_Env_<id>,
        protected in turn by the SSISDB database master key),
      - ciphertext stored in internal.environment_variables.sensitive_value,
      - the plaintext "value" column left NULL, and
      - NULL returned from the public catalog.environment_variables view.

    You never supply the catalog master-key password to use this. The database
    master key is also encrypted by the instance Service Master Key, so SQL
    Server opens it automatically. That password matters only for disaster
    recovery -- restoring SSISDB onto a DIFFERENT instance, whose Service
    Master Key cannot open the restored DMK:

        OPEN MASTER KEY DECRYPTION BY PASSWORD = '<catalog password>';
        ALTER MASTER KEY ADD ENCRYPTION BY SERVICE MASTER KEY;

    Lose that password and every sensitive value in the catalog is
    unrecoverable. It belongs in a password manager.

    ------------------------------------------------------------------
    WHAT A SENSITIVE VALUE CAN ACTUALLY BE USED FOR  (learned the hard way)
    ------------------------------------------------------------------
    Two SSIS rules combine into a hard constraint. Both were hit empirically
    while building this PoC:

      1. A sensitive VARIABLE may only be mapped to a sensitive PARAMETER.
         Mapping it to a non-sensitive one is accepted by
         set_object_parameter_value but rejected at EXECUTION time:
         "Unable to map an environment variable with the sensitive property
         set to True, to a parameter with the sensitive property set to
         False." Binding-time success proves nothing here.

      2. A sensitive PARAMETER's value cannot be read by package logic --
         at all. Not by a property expression ("The expression will not be
         evaluated because it contains sensitive parameter variable ..."),
         and not by an Execute SQL Task parameter mapping ("Accessing value
         of the parameter variable for the sensitive parameter ... is not
         allowed").

    That is deliberate: the value must never surface anywhere it could be
    logged. The only consumer SSIS permits is the runtime itself applying a
    CONNECTION MANAGER property -- the CM.<name>.Password parameter that a
    project-level connection manager exposes, which never passes through
    package code.

    Consequence for this PoC: TargetConnectionString CANNOT be sensitive,
    because both packages receive it through a property expression on their
    connection manager. It is therefore created non-sensitive below.

    AuditToken is created sensitive purely to demonstrate the encryption and
    masking, and is deliberately left unbound -- there is nothing in these
    packages that SSIS would let consume it.

    To protect a real credential you need the production shape: a project
    connection manager using SQL authentication, with CM.<name>.Password
    bound to a sensitive variable. That needs a SQL login and a restructuring
    of both packages onto a shared project connection manager, which is
    tracked as follow-up work rather than done here.
*/

USE SSISDB;
GO

SET NOCOUNT ON;
GO

DECLARE @folder      sysname = N'SSIS PoC';
DECLARE @project     sysname = N'SSIS';
DECLARE @environment sysname = N'Dev';

/* ---------------------------------------------------------------
   1. Drop the environment if it already exists (makes this re-runnable).
      Deleting the environment also removes its variables and any
      references pointing at it.
   --------------------------------------------------------------- */
IF EXISTS (
    SELECT 1
    FROM catalog.environments e
    JOIN catalog.folders f ON f.folder_id = e.folder_id
    WHERE f.name = @folder AND e.name = @environment
)
BEGIN
    EXEC catalog.delete_environment
         @folder_name      = @folder,
         @environment_name = @environment;
END;

/* ---------------------------------------------------------------
   2. Create the environment.
   --------------------------------------------------------------- */
EXEC catalog.create_environment
     @folder_name             = @folder,
     @environment_name        = @environment,
     @environment_description = N'Local development values for the SSIS PoC.';

/* ---------------------------------------------------------------
   3. Stable values only -- see the header note.
   --------------------------------------------------------------- */
EXEC catalog.create_environment_variable
     @folder_name      = @folder,
     @environment_name = @environment,
     @variable_name    = N'SourceFolder',
     @data_type        = N'String',
     @sensitive        = 0,
     @value            = N'D:\PoC\SSIS\Data\Input',
     @description      = N'Folder that holds the inbound employee CSV file.';

/* NOT sensitive -- and it cannot be. See the "SENSITIVE VALUES" note in the
   header: this value reaches the connection manager through a property
   expression, and SSIS refuses to evaluate an expression that references a
   sensitive parameter. Marking it sensitive fails the package at run time. */
EXEC catalog.create_environment_variable
     @folder_name      = @folder,
     @environment_name = @environment,
     @variable_name    = N'TargetConnectionString',
     @data_type        = N'String',
     @sensitive        = 0,
     @value            = N'Data Source=.\SQLFORPOC;Initial Catalog=SsisPoC;Provider=MSOLEDBSQL.1;Integrated Security=SSPI;Auto Translate=False;',
     @description      = N'OLE DB connection string for the destination SQL Server database.';

/* The sensitive one. AuditToken is consumed by an Execute SQL Task PARAMETER
   MAPPING inside LoadReferenceData, not by a property expression, so it is
   allowed to be sensitive -- and the matching package parameter is declared
   Sensitive="1", which SSISDB requires before it will map a sensitive
   variable to it. */
EXEC catalog.create_environment_variable
     @folder_name      = @folder,
     @environment_name = @environment,
     @variable_name    = N'AuditToken',
     @data_type        = N'String',
     @sensitive        = 1,   -- encrypted at rest; masked in catalog views
     @value            = N'S3cr3t-Token-9f2a4c',
     @description      = N'Secret token recorded against each reference-data load.';

/* ---------------------------------------------------------------
   4. Reference the environment from the project.
      @reference_type = 'R' (relative) => environment lives in the same
      folder as the project, so the reference survives folder renames.

      An environment reference belongs to the PROJECT, not the environment,
      so deleting and recreating the environment in step 1 does NOT remove
      it -- and it also survives a project redeploy. Creating it a second
      time fails with "This project already has an reference to the
      specified environment", so check first.
   --------------------------------------------------------------- */
DECLARE @reference_id bigint;

SELECT @reference_id = er.reference_id
FROM catalog.environment_references er
JOIN catalog.projects p ON p.project_id = er.project_id
JOIN catalog.folders  f ON f.folder_id  = p.folder_id
WHERE f.name = @folder
  AND p.name = @project
  AND er.environment_name = @environment;

IF @reference_id IS NULL
BEGIN
    EXEC catalog.create_environment_reference
         @folder_name           = @folder,
         @project_name          = @project,
         @environment_name      = @environment,
         @reference_type        = 'R',
         @reference_id          = @reference_id OUTPUT;
END;

/* ---------------------------------------------------------------
   5. Bind the two stable project parameters to their variables.
      @object_type = 20 => project-level parameter ($Project::).
      @value_type  = 'R' => the value is a *reference* to an environment
                            variable, not a literal ('V').
      When @value_type = 'R', @parameter_value holds the VARIABLE NAME.
   --------------------------------------------------------------- */
EXEC catalog.set_object_parameter_value
     @object_type     = 20,
     @folder_name     = @folder,
     @project_name    = @project,
     @parameter_name  = N'SourceFolder',
     @parameter_value = N'SourceFolder',
     @value_type      = 'R';

EXEC catalog.set_object_parameter_value
     @object_type     = 20,
     @folder_name     = @folder,
     @project_name    = @project,
     @parameter_name  = N'TargetConnectionString',
     @parameter_value = N'TargetConnectionString',
     @value_type      = 'R';

/* AuditToken is deliberately NOT bound to any parameter -- see the header.
   It exists to demonstrate encryption at rest; SSIS will not let a sensitive
   value be consumed by package logic, only by a connection-manager property. */

/* ---------------------------------------------------------------
   5b. Explicitly UNBIND what this script deliberately does not bind.

   Parameter bindings belong to the PROJECT, so they outlive both
   catalog.delete_environment and a project redeploy. An earlier version of
   this script bound SourceFileName to an environment variable; without this
   clear, that stale binding would survive and point at a variable that no
   longer exists, and every execution would fail at run time with a missing
   value. A re-runnable setup script has to clear what it no longer sets,
   not merely set what it wants.
   --------------------------------------------------------------- */
IF EXISTS (
    SELECT 1
    FROM catalog.object_parameters op
    JOIN catalog.projects p ON p.project_id = op.project_id
    JOIN catalog.folders  f ON f.folder_id  = p.folder_id
    WHERE f.name = @folder AND p.name = @project
      AND op.object_type = 20
      AND op.parameter_name = N'SourceFileName'
      AND op.value_type = 'R'
)
BEGIN
    EXEC catalog.clear_object_parameter_value
         @object_type    = 20,
         @folder_name    = @folder,
         @project_name   = @project,
         @parameter_name = N'SourceFileName';
END;
GO

/* ---------------------------------------------------------------
   6. Report the resulting configuration, and prove the masking.
   --------------------------------------------------------------- */
SELECT
    e.name              AS environment_name,
    ev.name             AS variable_name,
    ev.type             AS data_type,
    ev.sensitive,
    ev.value            AS value_from_public_view  -- NULL when sensitive = 1
FROM catalog.environment_variables ev
JOIN catalog.environments e ON e.environment_id = ev.environment_id
JOIN catalog.folders      f ON f.folder_id      = e.folder_id
WHERE f.name = N'SSIS PoC' AND e.name = N'Dev'
ORDER BY ev.name;

/* The ciphertext itself, straight from the internal table. Proves the value
   is stored encrypted rather than merely hidden by the view. */
SELECT
    ev.name                                   AS variable_name,
    ev.sensitive,
    ev.value                                  AS plaintext_column,
    DATALENGTH(ev.sensitive_value)            AS ciphertext_bytes,
    CONVERT(varchar(48), ev.sensitive_value)  AS ciphertext_preview
FROM internal.environment_variables ev
JOIN internal.environments e ON e.environment_id = ev.environment_id
JOIN internal.folders      f ON f.folder_id      = e.folder_id
WHERE f.name = N'SSIS PoC' AND e.environment_name = N'Dev'   -- internal uses environment_name
ORDER BY ev.name;

SELECT
    op.object_type,             -- 20 = project parameter, 30 = package parameter
    op.object_name,
    op.parameter_name,
    op.value_type,              -- 'R' = bound to environment variable, 'V' = literal
    op.referenced_variable_name,
    op.sensitive,
    op.design_default_value     -- NULL for the sensitive one: stripped at build
FROM catalog.object_parameters op
JOIN catalog.projects p ON p.project_id = op.project_id
JOIN catalog.folders  f ON f.folder_id  = p.folder_id
WHERE f.name = N'SSIS PoC' AND p.name = N'SSIS'
  AND op.parameter_name NOT LIKE 'CM.%'
ORDER BY op.object_type, op.object_name, op.parameter_name;
GO
