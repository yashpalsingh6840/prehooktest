using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.Dts.Runtime;

// Executes one or more packages from a local .ispac using the SSIS object
// model directly. This exists because dtexec's /Project (local, non-catalog)
// mode does NOT support overriding $Project:: parameters via /Par or /Set --
// that is only available once a project is deployed to an SSISDB catalog and
// run with /ISServer. This gives the same override capability without that
// setup.
//
// Four independent SSIS logging channels exist; this file wires up three:
//   1. IDTSEvents sink (CaptureEvents below)  -- real-time console output.
//   2. Package.Errors / Package.Warnings      -- post-execute safety net;
//      normally redundant with #1 but catches anything raised outside the
//      event pump (e.g. some validation-time failures).
//   3. Native log provider (DTS.LogProviderTextFile.3), cascaded to every
//      container -- the ONLY channel that carries pipeline-level events
//      (OnPipelineRowsSent, Diagnostic), i.e. per-component row counts.
// A 4th channel (custom IDTSLogging, for a structured JSON/stdout sink) is
// not wired here -- it matters for a future containerized runtime, not this
// local script. See CLAUDE.md "retrieving logs" notes.

public class CaptureEvents : DefaultEvents
{
    private readonly string _tag;
    public List<string> Messages = new List<string>();

    public CaptureEvents(string packageName)
    {
        _tag = "[" + packageName + "] ";
    }

    public override bool OnError(DtsObject source, int errorCode, string subComponent,
        string description, string helpFile, int helpContext, string idofInterfaceWithError)
    {
        Messages.Add(_tag + "ERROR  [" + subComponent + "] " + description);
        return false;
    }

    public override void OnWarning(DtsObject source, int warningCode, string subComponent,
        string description, string helpFile, int helpContext, string idofInterfaceWithError)
    {
        Messages.Add(_tag + "WARN   [" + subComponent + "] " + description);
    }

    public override void OnInformation(DtsObject source, int informationCode, string subComponent,
        string description, string helpFile, int helpContext, string idofInterfaceWithError,
        ref bool fireAgain)
    {
        Messages.Add(_tag + "INFO   [" + subComponent + "] " + description);
        // Without this, SSIS stops raising repeated OnInformation events --
        // which is where per-task progress and row-count chatter live.
        fireAgain = true;
    }
}

// Outcome of running one package, kept even on total failure so the batch
// summary can report every package regardless of where it broke.
public class PackageRunOutcome
{
    public string PackageName;
    public bool Started;
    public DTSExecResult? Result;
    public string LogFilePath;
    public string FailureReason;
}

public static class PkgRun
{
    private static readonly string[] LogEvents =
    {
        "OnError", "OnWarning", "OnInformation", "OnTaskFailed",
        "OnPreExecute", "OnPostExecute", "OnPipelineRowsSent", "Diagnostic"
    };

    // Back-compat single-package entry point.
    public static int Run(string ispacPath, string packageName, string[] overrides)
    {
        return RunAll(ispacPath, new[] { packageName }, overrides);
    }

    // Executes every named package in one project load, in order, continuing
    // to the next package if one fails to start or fails to run -- a single
    // package that can't launch (e.g. a credential that only exists inside
    // SSISDB, see CLAUDE.md "Sensitive credential") must not strand the rest
    // of the batch, same principle as ctl.usp_DispatchRunItems's per-item
    // TRY/CATCH.
    public static int RunAll(string ispacPath, string[] packageNames, string[] overrides)
    {
        Project proj = Project.OpenProject(ispacPath);

        foreach (Parameter p in proj.Parameters)
            Console.WriteLine("param: $Project::" + p.Name + " = " + p.Value);

        string logDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ispacPath)) ?? ".", "logs");
        Directory.CreateDirectory(logDir);

        var outcomes = new List<PackageRunOutcome>();

        foreach (string packageName in packageNames)
        {
            var outcome = new PackageRunOutcome { PackageName = packageName };
            outcomes.Add(outcome);

            Console.WriteLine("\n--- executing " + packageName + " ---");
            try
            {
                if (proj.PackageItems.IndexOf(packageName) < 0)
                {
                    outcome.FailureReason = "Package not found in project.";
                    Console.WriteLine("ERROR  " + outcome.FailureReason);
                    continue;
                }

                Package pkg = proj.PackageItems[packageName].Package;

                ApplyOverrides(proj, pkg, overrides);

                string logFile = Path.Combine(logDir, packageName + ".log");
                outcome.LogFilePath = logFile;
                ConfigureTextFileLogging(pkg, logFile);

                CaptureEvents ev = new CaptureEvents(packageName);
                outcome.Started = true;

                DTSExecResult result = pkg.Execute(null, null, ev, null, null);
                outcome.Result = result;

                foreach (string m in ev.Messages)
                    if (m.Contains("ERROR") || m.Contains("WARN") || m.Contains("rows"))
                        Console.WriteLine(m);

                // Channel 2 safety net -- see file header.
                foreach (DtsError e in pkg.Errors)
                    Console.WriteLine("[" + packageName + "] ERROR  [" + e.SubComponent + "] " + e.Description);
                foreach (DtsWarning w in pkg.Warnings)
                    Console.WriteLine("[" + packageName + "] WARN   [" + w.SubComponent + "] " + w.Description);

                if (result != DTSExecResult.Success)
                {
                    string firstError = ev.Messages.FirstOrDefault(m => m.Contains("ERROR"));
                    outcome.FailureReason = firstError ?? result.ToString();
                }

                Console.WriteLine("--- " + packageName + " result: " + result + "  (log: " + logFile + ") ---");
            }
            catch (Exception ex)
            {
                outcome.FailureReason = ex.GetType().Name + ": " + ex.Message;
                Console.WriteLine("EXCEPTION running " + packageName + ": " + ex.Message);
            }
        }

        Console.WriteLine("\n=== summary ===");
        foreach (var o in outcomes)
        {
            string status = !o.Started ? "NOT STARTED" : o.Result.ToString();
            Console.WriteLine(string.Format("{0,-20} {1,-12} {2}", o.PackageName, status, o.FailureReason ?? ""));
        }

        bool allOk = outcomes.All(o => o.Started && o.Result == DTSExecResult.Success);
        return allOk ? 0 : 1;
    }

    // Overrides arrive as "Name=Value" and mimic what an SSISDB Environment
    // would supply. Package-scoped parameters shadow project-scoped ones of
    // the same name, matching how ctl resolves object_type in the catalog
    // (see CLAUDE.md). An override matching neither scope is a warning, not
    // a throw, so one override list can be reused across packages even
    // though LoadEmployees and LoadReferenceData don't share every
    // parameter name.
    //
    // A second form, "CM.<ConnectionManagerName>.<PropertyName>=Value" (same
    // naming SSISDB itself uses for its auto-split connection-manager
    // parameters -- see CLAUDE.md "Sensitive credential"), sets a property
    // directly on a connection manager instead of a project/package
    // parameter. This is what makes running LoadEmployees.dtsx locally
    // possible again: CM_TargetDb.Password is a write-only property that was
    // deliberately never persisted to the .ispac (see trap 13), so there is
    // no default to fall back on the way there is for an ordinary parameter
    // -- the caller must supply the real password explicitly, every run,
    // exactly as SSISDB's bound environment variable does at execution time.
    // Nothing here weakens that: no password is stored by this file, and a
    // run without the override still fails exactly as documented.
    private static void ApplyOverrides(Project proj, Package pkg, string[] overrides)
    {
        if (overrides == null) return;

        foreach (string o in overrides)
        {
            int eq = o.IndexOf('=');
            string n = o.Substring(0, eq);
            string val = o.Substring(eq + 1);

            if (n.StartsWith("CM."))
            {
                ApplyConnectionManagerOverride(pkg, n, val);
            }
            else if (pkg.Parameters.Contains(n))
            {
                pkg.Parameters[n].Value = val;
                Console.WriteLine("override: $Package::" + n + " = " + val);
            }
            else if (proj.Parameters.Contains(n))
            {
                proj.Parameters[n].Value = val;
                Console.WriteLine("override: $Project::" + n + " = " + val);
            }
            else
            {
                Console.WriteLine("WARN   override '" + n + "' matches no parameter on " +
                    pkg.Name + " (package or project scope); ignored.");
            }
        }
    }

    // Handles "CM.<ConnectionManagerName>.<PropertyName>=Value". Split into
    // at most 3 parts so a property name containing a literal '.' (none do
    // today) wouldn't silently truncate the connection manager name.
    private static void ApplyConnectionManagerOverride(Package pkg, string overrideName, string val)
    {
        string[] parts = overrideName.Split(new[] { '.' }, 3);
        if (parts.Length != 3)
        {
            Console.WriteLine("WARN   override '" + overrideName +
                "' is not in CM.<ConnectionManagerName>.<PropertyName> form; ignored.");
            return;
        }

        string cmName = parts[1];
        string propName = parts[2];

        if (!pkg.Connections.Contains(cmName))
        {
            Console.WriteLine("WARN   override '" + overrideName + "' names connection manager '" +
                cmName + "', which does not exist on " + pkg.Name + "; ignored.");
            return;
        }

        ConnectionManager cm = pkg.Connections[cmName];
        if (!cm.Properties.Contains(propName))
        {
            Console.WriteLine("WARN   override '" + overrideName + "': connection manager '" +
                cmName + "' has no property '" + propName + "'; ignored.");
            return;
        }

        cm.Properties[propName].SetValue(cm, val);

        // Never echo the actual value for a property literally named
        // Password -- it's already sensitive by SSIS's own convention (see
        // trap 13), regardless of where the caller sourced it from.
        string shown = propName.Equals("Password", StringComparison.OrdinalIgnoreCase) ? "***" : val;
        Console.WriteLine("override: CM." + cmName + "." + propName + " = " + shown);
    }

    // Channel 3: native SSIS log provider. LoggingOptions do NOT cascade from
    // a container to its children (confirmed against the live object model),
    // so the same provider + filter must be applied to every container in
    // the tree, or the Data Flow -- the one place row counts live -- stays
    // silent.
    private static void ConfigureTextFileLogging(Package pkg, string logFile)
    {
        // The text-file log provider's ConfigString is NOT the file path --
        // it's the NAME of a FILE connection manager, which in turn holds
        // the path. Passing a raw path directly fails at runtime with "The
        // connection '<path>' is not found" (it's parsed as a connection
        // manager name), and -- worse -- that failure itself counts toward
        // the package's MaximumErrorCount, which can flip an otherwise-
        // successful run to Failure. Confirmed by reproducing exactly that
        // false failure against LoadReferenceData before adding this
        // connection manager.
        ConnectionManager logCm = pkg.Connections.Add("FILE");
        logCm.Name = "CM_PkgRunLog_" + pkg.Name;
        logCm.ConnectionString = logFile;

        // ".9" is this SSIS version's (17.0) log-provider suffix -- confirmed
        // via Application.LogProviderInfos, NOT the ".3"/".4" seen in older
        // docs/blog posts; using the wrong suffix fails at runtime with
        // "not recognized as a valid log provider type", not at compile time.
        LogProvider lp = pkg.LogProviders.Add("DTS.LogProviderTextFile.9");
        lp.ConfigString = logCm.Name;

        ApplyLogging(pkg, lp);
    }

    private static void ApplyLogging(DtsContainer container, LogProvider lp)
    {
        container.LoggingMode = DTSLoggingMode.Enabled;
        container.LoggingOptions.SelectedLogProviders.Add(lp);
        container.LoggingOptions.EventFilterKind = DTSEventFilterKind.Inclusion;
        container.LoggingOptions.EventFilter = LogEvents;

        IDTSSequence seq = container as IDTSSequence;
        if (seq == null) return;

        foreach (Executable child in seq.Executables)
        {
            DtsContainer childContainer = child as DtsContainer;
            if (childContainer != null)
                ApplyLogging(childContainer, lp);
        }
    }
}
