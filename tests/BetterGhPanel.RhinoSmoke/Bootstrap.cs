using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

// The executable also references Core. Bind the requested payload before the host
// type is loaded or any of its Core-using methods are compiled.
internal static class Bootstrap
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool treeOnly = args.Length is 1 or 2 && args[0] == "--tree-only";
        bool fixture = args.Length is 2 or 3 && (args[0] == "--fixture" || args[0] == "--identity-only");
        if (!treeOnly && !fixture)
        {
            Console.Error.WriteLine("Usage: --tree-only [plugin path], --fixture <fixture> [plugin path], or --identity-only <fixture> [plugin path]. Use run.ps1 for all isolated fixtures.");
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
