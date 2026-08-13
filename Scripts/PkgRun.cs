using System;
using System.Collections.Generic;
using Microsoft.SqlServer.Dts.Runtime;

// Executes a package from a local .ispac using the SSIS object model directly.
// This exists because dtexec's /Project (local, non-catalog) mode does NOT
// support overriding $Project:: parameters via /Par or /Set -- that is only
// available once a project is deployed to an SSISDB catalog and run with
// /ISServer. This gives the same override capability without that setup.

public class CaptureEvents : DefaultEvents
{
    public List<string> Messages = new List<string>();

    public override bool OnError(DtsObject source, int errorCode, string subComponent,
        string description, string helpFile, int helpContext, string idofInterfaceWithError)
    {
        Messages.Add("ERROR  [" + subComponent + "] " + description);
        return false;
    }

    public override void OnWarning(DtsObject source, int warningCode, string subComponent,
        string description, string helpFile, int helpContext, string idofInterfaceWithError)
    {
        Messages.Add("WARN   [" + subComponent + "] " + description);
    }

    public override void OnInformation(DtsObject source, int informationCode, string subComponent,
        string description, string helpFile, int helpContext, string idofInterfaceWithError,
        ref bool fireAgain)
    {
        Messages.Add("INFO   [" + subComponent + "] " + description);
        fireAgain = true;
    }
}

public static class PkgRun
{
    public static int Run(string ispacPath, string packageName, string[] overrides)
    {
        Project proj = Project.OpenProject(ispacPath);

        // overrides arrive as "Name=Value" and mimic what an SSISDB Environment would supply
        if (overrides != null)
        {
            foreach (string o in overrides)
            {
                int eq = o.IndexOf('=');
                string n = o.Substring(0, eq);
                string val = o.Substring(eq + 1);
                proj.Parameters[n].Value = val;
                Console.WriteLine("override: " + n + " = " + val);
            }
        }

        foreach (Parameter p in proj.Parameters)
            Console.WriteLine("param: $Project::" + p.Name + " = " + p.Value);

        Package pkg = proj.PackageItems[packageName].Package;
        CaptureEvents ev = new CaptureEvents();

        Console.WriteLine("\n--- executing " + packageName + " ---");
        DTSExecResult result = pkg.Execute(null, null, ev, null, null);

        foreach (string m in ev.Messages)
            if (m.StartsWith("ERROR") || m.StartsWith("WARN") || m.Contains("rows"))
                Console.WriteLine(m);

        Console.WriteLine("--- result: " + result + " ---");
        return result == DTSExecResult.Success ? 0 : 1;
    }
}
