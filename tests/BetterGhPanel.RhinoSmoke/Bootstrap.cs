using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;

// The executable also references Core. Bind the requested payload before the host
// type is loaded or any of its Core-using methods are compiled.
internal static class Bootstrap
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Exercise the desktop and job lifetime without starting Rhino.
        if (args.Length == 2 && args[0] == "--desktop-probe" && args[1] is "ok" or "fail" or "wait")
        {
            try { return InactiveDesktop.Run(new[] { "--desktop-probe-child", args[1] }); }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        if (args.Length == 2 && args[0] == "--desktop-probe-child" && args[1] is "ok" or "fail" or "wait")
        {
            string? desktop = Environment.GetEnvironmentVariable(InactiveDesktop.EnvironmentName);
            if (desktop == null || !InactiveDesktop.IsCurrent(desktop)) return 2;
            Console.WriteLine("Inactive desktop probe ready");
            Console.Out.Flush();
            if (args[1] == "wait") Thread.Sleep(30000);
            return args[1] == "fail" ? 7 : 0;
        }
        if (args.Length is 2 or 3 && args[0] == "--desktop-fixture")
        {
            try
            {
                var childArguments = (string[])args.Clone();
                childArguments[0] = "--fixture";
                return InactiveDesktop.Run(childArguments);
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        bool treeOnly = args.Length is 1 or 2 && args[0] == "--tree-only";
        bool fixture = args.Length is 2 or 3 && (args[0] == "--fixture" || args[0] == "--identity-only");
        if (!treeOnly && !fixture)
        {
            Console.Error.WriteLine("Usage: --tree-only [plugin path], --fixture <fixture> [plugin path], --desktop-fixture <fixture> [plugin path], or --identity-only <fixture> [plugin path]. Use run.ps1 for all isolated fixtures.");
            return 2;
        }
        try
        {
            int pathIndex = treeOnly ? 1 : 2;
            string plugin = args.Length > pathIndex ? Path.GetFullPath(args[pathIndex]) :
                Path.GetFullPath("src/BetterGhPanel/bin/Release/net7.0/BetterGhPanel.gha");
            string core = Path.Combine(Path.GetDirectoryName(plugin)!, "BetterGhPanel.Core.dll");
            var loaded = AssemblyLoadContext.Default.LoadFromAssemblyPath(core);
            if (!string.Equals(Path.GetFullPath(loaded.Location), core, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Core was already loaded from an unexpected location: " + loaded.Location);
            return RunHost(args);
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunHost(string[] args) => Program.Run(args);
}
