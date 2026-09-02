using System;
using System.Collections.Generic;
using Microsoft.SqlServer.Dts.Runtime;

namespace RBC.Demo.Launcher
{
    /// <summary>
    /// Reproduction of the production launcher pattern documented in
    /// docs/SSISAnalysis.md section 0.2 - the classes RBCDtsPackage / RbcSSISPackage /
    /// ExecuteSSIS, used by all 29 launcher projects across 44 packages.
    ///
    /// THIS IS THE .NET 8 BLOCKER (Blocker 1 / section 3.1).
    ///
    ///   Microsoft.SqlServer.ManagedDTS.dll ships only with SSDT and the SSIS runtime,
    ///   is not on NuGet, and wraps COM (DTSRuntimeWrap / DTSPipelineWrap). There is no
    ///   .NET Core equivalent, so this file compiles on .NET Framework 4.7.2 and cannot
    ///   be ported to net8.0 as-is.
    ///
    /// It is here deliberately: the demo previously showed only the TARGET state
    /// (dtexec), never the problem state. Compare with DtexecLauncher.cs, which is the
    /// same job done the Option B way and does port to .NET 8.
    /// </summary>
    public class RbcDtsPackage
    {
        private readonly Dictionary<string, string> _connections = new Dictionary<string, string>();

        /// <summary>Connection strings injected at run time, exactly as production does.</summary>
        public void AddConnection(string name, string connectionString)
        {
            _connections[name] = connectionString;
        }

        /// <summary>
        /// Loads and executes a .dtsx in-process. Every line touching Application,
        /// Package or DTSExecResult is unavailable on .NET 8.
        /// </summary>
        public int Run(string packagePath)
        {
            var events = new PkgEventListener();

            // ---- the blocking API ------------------------------------------------
            var app = new Application();
            Package package = app.LoadPackage(packagePath, events);

            // production overrides connection strings on the loaded package
            foreach (var kvp in _connections)
            {
                if (package.Connections.Contains(kvp.Key))
                {
                    package.Connections[kvp.Key].ConnectionString = kvp.Value;
                    Console.WriteLine("  connection '{0}' overridden", kvp.Key);
                }
                else
                {
                    Console.WriteLine("  WARNING: no connection manager named '{0}'", kvp.Key);
                }
            }

            DTSExecResult result = package.Execute(null, null, events, null, null);
            // ----------------------------------------------------------------------

            Console.WriteLine("  result: {0}", result);
            if (result == DTSExecResult.Failure)
            {
                foreach (DtsError err in package.Errors)
                    Console.WriteLine("  ERROR {0}: {1}", err.ErrorCode, err.Description);
            }
            return result == DTSExecResult.Success ? 0 : 1;
        }
    }

    /// <summary>
    /// Mirrors the production PkgEventListener : DefaultEvents pattern. Section 3.2
    /// lists this as challenge C12 - roughly 25 packages rely on it for error logging.
    /// </summary>
    public class PkgEventListener : DefaultEvents
    {
        public override bool OnError(DtsObject source, int errorCode, string subComponent,
                                     string description, string helpFile, int helpContext,
                                     string idofInterfaceWithError)
        {
            Console.WriteLine("  [OnError] {0}: {1}", subComponent, description);
            return false;   // false = keep going, matching production behaviour
        }

        public override void OnInformation(DtsObject source, int informationCode, string subComponent,
                                           string description, string helpFile, int helpContext,
                                           string idofInterfaceWithError, ref bool fireAgain)
        {
            Console.WriteLine("  [OnInfo]  {0}: {1}", subComponent, description);
            fireAgain = true;
        }

        public override void OnTaskFailed(TaskHost taskHost)
        {
            Console.WriteLine("  [OnTaskFailed] {0}", taskHost.Name);
        }
    }
}
