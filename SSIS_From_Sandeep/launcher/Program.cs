using System;
using System.IO;

namespace RBC.Demo.Launcher
{
    /// <summary>
    /// Runs one package two ways, side by side:
    ///   managed  - Application().LoadPackage() ... the .NET 8 blocker
    ///   dtexec   - child process ................. the Option B replacement
    ///
    ///   Launcher.exe managed  &lt;package.dtsx&gt;
    ///   Launcher.exe dtexec   &lt;package.dtsx&gt;
    ///   Launcher.exe both     &lt;package.dtsx&gt;
    /// </summary>
    public static class Program
    {
        const string OleDb =
            "Data Source=localhost,1433;Initial Catalog=SSISDemo;Provider=MSOLEDBSQL.1;" +
            "User ID=sa;Password=YourStrong!Passw0rd123;Persist Security Info=True;";

        public static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: Launcher.exe <managed|dtexec|both> <package.dtsx>");
                return 2;
            }

            string mode = args[0].ToLowerInvariant();
            string pkg  = Path.GetFullPath(args[1]);
            if (!File.Exists(pkg))
            {
                Console.WriteLine("package not found: " + pkg);
                return 4;
            }

            int rc = 0;

            if (mode == "managed" || mode == "both")
            {
                Console.WriteLine();
                Console.WriteLine("=== managed host: Application().LoadPackage()  [.NET 8 BLOCKER] ===");
                var managed = new RbcDtsPackage();
                managed.AddConnection("CM_SQL_SSISDemo", OleDb);
                rc = managed.Run(pkg);
            }

            if (mode == "dtexec" || mode == "both")
            {
                Console.WriteLine();
                Console.WriteLine("=== dtexec child process  [Option B - ports to .NET 8] ===");
                rc = new DtexecLauncher().Run(pkg);
            }

            return rc;
        }
    }
}
