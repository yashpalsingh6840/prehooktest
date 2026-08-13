using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PkgRunConsole
{
    // Standalone C# equivalent of Scripts/Run-Package.ps1: runs
    // LoadEmployees.dtsx and LoadReferenceData.dtsx (both, by default)
    // in-process via the SSIS managed API (Microsoft.SqlServer.ManagedDTS),
    // overriding $Project::/$Package:: parameters for this run only. See
    // Scripts/PkgRun.cs for why this exists instead of dtexec: dtexec's
    // local /Project mode can't override project parameters at all, and
    // /ISServer needs a deployed SSISDB catalog. See PkgRun.cs's header for
    // the logging channels wired into each run (console + per-package text
    // log under SSIS/bin/Development/logs/).
    //
    // Packages run in sequence and one failing to launch does not stop the
    // rest of the batch -- as of 2026-08-13, LoadEmployees is EXPECTED to
    // fail here (its CM_TargetDb password only exists inside SSISDB, see
    // CLAUDE.md "Sensitive credential"); LoadReferenceData still runs and
    // the exit code reflects the batch, not just the first package.
    //
    // Usage:
    //   PkgRun.Console.exe [--ispac <path>] [--package <name> ...] [--set Name=Value ...]
    //
    // Examples:
    //   PkgRun.Console.exe
    //   PkgRun.Console.exe --package LoadReferenceData.dtsx
    //   PkgRun.Console.exe --set TargetConnectionString="Data Source=...;"
    //   PkgRun.Console.exe --ispac D:\PoC\SSIS\SSIS\bin\Development\SSIS.ispac --package LoadEmployees.dtsx
    public static class Program
    {
        private static readonly string[] DefaultPackages =
        {
            "LoadEmployees.dtsx", "LoadReferenceData.dtsx"
        };

        public static int Main(string[] args)
        {
            string ispacPath = null;
            var packageNames = new List<string>();
            var overrides = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--ispac":
                        ispacPath = RequireValue(args, ref i, "--ispac");
                        break;
                    case "--package":
                        packageNames.Add(RequireValue(args, ref i, "--package"));
                        break;
                    case "--set":
                        string kv = RequireValue(args, ref i, "--set");
                        if (kv.IndexOf('=') < 0)
                        {
                            Console.Error.WriteLine("--set expects Name=Value, got: " + kv);
                            return 2;
                        }
                        overrides.Add(kv);
                        break;
                    case "-h":
                    case "--help":
                        PrintUsage();
                        return 0;
                    default:
                        Console.Error.WriteLine("Unrecognized argument: " + args[i]);
                        PrintUsage();
                        return 2;
                }
            }

            // No --package given at all => run the full default set (both
            // packages), matching Run-Package.ps1's default.
            if (packageNames.Count == 0)
                packageNames.AddRange(DefaultPackages);

            if (ispacPath == null)
            {
                string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
                ispacPath = Path.Combine(repoRoot, "SSIS", "bin", "Development", "SSIS.ispac");
            }
            ispacPath = Path.GetFullPath(ispacPath);

            if (!File.Exists(ispacPath))
            {
                Console.Error.WriteLine("ispac not found at '" + ispacPath + "'.");
                Console.Error.WriteLine("Build the project first (devenv SSIS.sln /Build Development), or pass --ispac.");
                return 1;
            }

            return PkgRun.RunAll(ispacPath, packageNames.ToArray(), overrides.ToArray());
        }

        private static string RequireValue(string[] args, ref int i, string flag)
        {
            if (i + 1 >= args.Length)
                throw new ArgumentException(flag + " requires a value");
            i++;
            return args[i];
        }

        // Walks up from the built exe's location (which varies by
        // Debug/Release output path) to find the repo root, identified by
        // SSIS.sln, so --ispac doesn't have to be passed on every run.
        private static string FindRepoRoot(string start)
        {
            var dir = new DirectoryInfo(start);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SSIS.sln")))
                dir = dir.Parent;

            if (dir == null)
                throw new InvalidOperationException(
                    "Could not locate SSIS.sln above '" + start + "'. Pass --ispac explicitly.");

            return dir.FullName;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: PkgRun.Console.exe [--ispac <path>] [--package <name> ...] [--set Name=Value ...]");
            Console.WriteLine();
            Console.WriteLine("  --ispac    Path to the built .ispac (default: <repo>\\SSIS\\bin\\Development\\SSIS.ispac)");
            Console.WriteLine("  --package  Package to run; repeatable. Default (if omitted entirely): both");
            Console.WriteLine("             LoadEmployees.dtsx and LoadReferenceData.dtsx.");
            Console.WriteLine("  --set      Override a parameter for this run, applied across all packages in");
            Console.WriteLine("             the batch, e.g. --set TargetConnectionString=\"Data Source=...;\"");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  PkgRun.Console.exe");
            Console.WriteLine("  PkgRun.Console.exe --package LoadReferenceData.dtsx");
            Console.WriteLine("  PkgRun.Console.exe --package LoadEmployees.dtsx --package LoadReferenceData.dtsx");
        }
    }
}
