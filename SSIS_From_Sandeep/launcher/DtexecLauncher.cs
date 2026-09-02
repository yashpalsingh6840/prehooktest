using System;
using System.Diagnostics;

namespace RBC.Demo.Launcher
{
    /// <summary>
    /// The Option B migration from docs/SSISAnalysis.md section 4 (Phase 5.1): run the
    /// package as a dtexec.exe CHILD PROCESS instead of hosting the SSIS runtime.
    ///
    /// Why this matters: it uses only System.Diagnostics.Process, so unlike
    /// RbcDtsPackage.cs it compiles and runs unchanged on .NET 8. That is what makes it
    /// the recommended 2-3 week unblock for all 29 launcher projects.
    ///
    /// Trade-offs called out in the analysis, reproduced honestly here:
    ///   * the dtexec path is version-specific (SQL 2022 -> 160, SQL 2019 -> 150)
    ///   * it breaks in containers, where no SSIS runtime is installed
    ///   * connection strings move from an in-process object model to /SET arguments
    /// </summary>
    public class DtexecLauncher
    {
        public string DtexecPath { get; set; }

        public DtexecLauncher()
        {
            // 160 = SQL Server 2022, which is what this demo targets.
            DtexecPath = @"C:\Program Files\Microsoft SQL Server\160\DTS\Binn\DTExec.exe";
        }

        /// <param name="setArguments">
        /// Optional /SET property paths, e.g.
        /// \Package.Connections[CM_SQL_SSISDemo].Properties[ConnectionString];"..."
        /// </param>
        public int Run(string packagePath, params string[] setArguments)
        {
            var args = "/File \"" + packagePath + "\" /Reporting EW";
            foreach (var s in setArguments)
                args += " /SET " + s;

            var psi = new ProcessStartInfo
            {
                FileName = DtexecPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var p = Process.Start(psi))
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine("  " + e.Data); };
                p.ErrorDataReceived  += (s, e) => { if (e.Data != null) Console.WriteLine("  ! " + e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();

                // dtexec exit codes: 0 success, 1 failure, 3 cancelled, 4 cannot find,
                // 5 cannot load, 6 syntax error.
                Console.WriteLine("  dtexec exit code: {0}", p.ExitCode);
                return p.ExitCode;
            }
        }
    }
}
