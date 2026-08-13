using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PkgRunConsole
{
    // Standalone C# equivalent of Scripts/Run-Package.ps1: runs
    // LoadEmployees.dtsx in-process via the SSIS managed API
    // (Microsoft.SqlServer.ManagedDTS), overriding $Project:: parameters
    // for this run only. See Scripts/PkgRun.cs for why this exists instead
    // of dtexec: dtexec's local /Project mode can't override project
    // parameters at all, and /ISServer needs a deployed SSISDB catalog.
    //
    // Usage:
    //   PkgRun.Console.exe [--ispac <path>] [--package <name>] [--set Name=Value ...]
    //
    // Examples:
    //   PkgRun.Console.exe
    //   PkgRun.Console.exe --set SourceFolder=C:\Drop\NewHires --set SourceFileName=NewHires.csv
    //   PkgRun.Console.exe --ispac D:\PoC\SSIS\SSIS\bin\Development\SSIS.ispac --package LoadEmployees.dtsx
    public static class Program
    {
        public static int Main(string[] args)
        {
            string ispacPath = null;
            string packageName = "LoadEmployees.dtsx";
            var overrides = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--ispac":
                        ispacPath = RequireValue(args, ref i, "--ispac");
                        break;
                    case "--package":
                        packageName = RequireValue(args, ref i, "--package");
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

            return PkgRun.Run(ispacPath, packageName, overrides.ToArray());
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
            Console.WriteLine("Usage: PkgRun.Console.exe [--ispac <path>] [--package <name>] [--set Name=Value ...]");
            Console.WriteLine();
            Console.WriteLine("  --ispac    Path to the built .ispac (default: <repo>\\SSIS\\bin\\Development\\SSIS.ispac)");
            Console.WriteLine("  --package  Package to run inside the project (default: LoadEmployees.dtsx)");
            Console.WriteLine("  --set      Override a $Project:: parameter for this run, e.g. --set SourceFolder=C:\\Drop\\NewHires");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  PkgRun.Console.exe");
            Console.WriteLine("  PkgRun.Console.exe --set SourceFolder=C:\\Drop\\NewHires --set SourceFileName=NewHires.csv");
        }
    }
}
