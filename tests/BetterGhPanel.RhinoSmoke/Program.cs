using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.Kernel;
using Rhino.Runtime.InProcess;

internal static class Program
{
    private static string _system = string.Empty;
    private static string _grasshopper = string.Empty;
    private static string _plugin = string.Empty;
    private static string _resultPath = string.Empty;
    private static string _fixture = string.Empty;
    private static string _runId = string.Empty;
    private static Assembly? _loadedPlugin;
    private static Assembly? _loadedCore;
    private static string _pluginSha256 = string.Empty;
    private static string _coreSha256 = string.Empty;
    private static DateTimeOffset _startedUtc;
    private static bool _completed;
    private static bool _passed;
    private static string _phase = "identity";
    private static string _hostMode = "hidden";
    private static bool _rhinoIdlePump;
    private static string? _failure;

    // This is also checked against fixtures.json, the coordinator's explicit inventory.
    private static Dictionary<string, Func<Assembly, Task>> _fixtures = null!;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Dictionary<string, Func<Assembly, Task>> CreateFixtures() => new(StringComparer.Ordinal)
    {
        ["capture-safety"] = assembly => { FixtureUserData.EnsureRegistered(); CaptureSafetyChecks.VerifyManaged(assembly); CaptureSafetyChecks.VerifyNativeCapabilities(assembly); CaptureSafetyChecks.VerifyNativeUserData(assembly); return Task.CompletedTask; },
        ["polyline-safety"] = assembly => { FixtureUserData.EnsureRegistered(); return GeometryPolylineSafetyChecks.Run(assembly); },
        ["preview-safety"] = PreviewSafetyChecks.Run,
        ["preview-admission-freshness"] = PreviewSafetyChecks.RunAdmissionFreshness,
        ["preview-geometry"] = PreviewGeometryChecks.Run,
        ["preview-additional-geometry"] = PreviewSafetyChecks.RunAdditionalGeometry,
        ["preview-additional-draw"] = PreviewSafetyChecks.RunAdditionalGeometryDraw,
        ["preview-reentrant-draw"] = PreviewSafetyChecks.RunReentrantDraw,
        ["preview-metadata-safety"] = assembly => { FixtureUserData.EnsureRegistered(); return PreviewSafetyChecks.RunMetadata(assembly); },
        ["preview-native-resources"] = PreviewSafetyChecks.RunNativeResources,
        ["preview-conduit-resources"] = PreviewSafetyChecks.RunConduitResources,
        ["preview-compatibility-draw"] = PreviewSafetyChecks.RunCompatibilityDraw,
        ["preview-retirement-seam"] = PreviewSafetyChecks.RunRetirementSeam,
        ["nurbs-value-candidate"] = assembly => { FixtureUserData.EnsureRegistered(); return GeometryNurbsCandidateChecks.Run(assembly); },
        ["production-curve-measurement"] = assembly => { FixtureUserData.EnsureRegistered(); return ProductionCurveMeasurementChecks.Run(assembly); },
        ["standalone-compatibility"] = assembly => { Rhino.Runtime.HostUtils.InitializeRhinoCommon(); FixtureUserData.EnsureRegistered(); return StandaloneCompatibilityChecks.Run(assembly); },
        ["capture-transaction"] = CaptureTransactionChecks.Run,
        ["capture-stock-geometry"] = CaptureStockGeometryChecks.RunNative,
        ["property-inspection"] = PropertyInspectionChecks.Run,
        ["ui-ux"] = UxInteractionChecks.Run,
        ["row-selection"] = RowSelectionChecks.Run,
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string path);
    private delegate bool WindowCallback(IntPtr handle, IntPtr argument);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr argument);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, WindowCallback callback, IntPtr argument);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr handle, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, System.Text.StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);

    internal static int Run(string[] args)
    {
        if (!((args.Length == 1 || args.Length == 2) && args[0] == "--tree-only") &&
            !((args.Length == 2 || args.Length == 3) && (args[0] == "--fixture" || args[0] == "--identity-only")))
        {
            Console.Error.WriteLine("Usage: --tree-only [plugin path], --fixture <fixture> [plugin path], or --identity-only <fixture> [plugin path]. Use run.ps1 for all isolated fixtures.");
            return 2;
        }
        bool treeOnly = args[0] == "--tree-only";
        _fixture = treeOnly ? string.Empty : args[1];
        try
        {
            string? desktop = Environment.GetEnvironmentVariable(InactiveDesktop.EnvironmentName);
            if (!string.IsNullOrEmpty(desktop))
            {
                if (!InactiveDesktop.IsCurrent(desktop)) throw new InvalidOperationException("The fixture is not on its requested inactive desktop.");
                _hostMode = "inactive-desktop";
            }
            _system = Environment.GetEnvironmentVariable("RHINO_SYSTEM_PATH") ?? "C:/Program Files/Rhino 8/System";
            _grasshopper = Path.GetFullPath(Path.Combine(_system, "../Plug-ins/Grasshopper"));
            int pathIndex = treeOnly ? 1 : 2;
            _plugin = args.Length > pathIndex ? Path.GetFullPath(args[pathIndex]) : Path.GetFullPath("src/BetterGhPanel/bin/Release/net7.0/BetterGhPanel.gha");
            string corePath = Path.Combine(Path.GetDirectoryName(_plugin)!, "BetterGhPanel.Core.dll");
            if (!File.Exists(_plugin) || !File.Exists(corePath) || !File.Exists(Path.Combine(_system, "RhinoCommon.dll")))
                throw new FileNotFoundException("Build the plugin and Core together and install Rhino 8 before running this check.");
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            Environment.SetEnvironmentVariable("PATH", _system + ";" + Environment.GetEnvironmentVariable("PATH"));
            SetDllDirectory(_system);

            // The test executable also references Core. Bind it to the requested payload
            // before any fixture is JIT-compiled; then verify the assembly actually used.
            _loadedPlugin = Assembly.LoadFrom(_plugin);
            _loadedCore = LoadedCoreAssembly();
            _fixtures = CreateFixtures();
            var inventory = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures.json")))!;
            if (inventory.Length != _fixtures.Count || inventory.Distinct(StringComparer.Ordinal).Count() != inventory.Length ||
                !inventory.All(_fixtures.ContainsKey))
                throw new InvalidOperationException("Host fixture dispatch does not match fixtures.json.");
            if (!treeOnly && !_fixtures.ContainsKey(_fixture))
                throw new ArgumentException("Unknown fixture: " + _fixture);
            if (!string.Equals(Path.GetFullPath(_loadedPlugin.Location), _plugin, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(_loadedCore.Location), Path.GetFullPath(corePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The loaded plugin or Core assembly is not from the requested payload: plugin=" + _loadedPlugin.Location + ", Core=" + _loadedCore.Location + ", expected Core=" + corePath);
            if (treeOnly)
            {
                CaptureSafetyChecks.VerifyManaged(_loadedPlugin);
                NativeGroupFixture.VerifyCycles(_loadedPlugin);
                Console.WriteLine("PASS: Managed capture safety checks completed (Rhino host not started).");
                return 0;
            }
            _runId = Environment.GetEnvironmentVariable("PANEL_SMOKE_RUN_ID") ?? Guid.NewGuid().ToString("D");
            if (!Guid.TryParseExact(_runId, "D", out _)) throw new ArgumentException("Invalid PANEL_SMOKE_RUN_ID.");
            _resultPath = Environment.GetEnvironmentVariable("PANEL_SMOKE_RESULT") ??
                Path.Combine(Path.GetDirectoryName(_plugin)!, "rhino-smoke-" + _runId + "-result.json");
            _pluginSha256 = HashFile(_loadedPlugin.Location);
            _coreSha256 = HashFile(_loadedCore.Location);
            _startedUtc = DateTimeOffset.UtcNow;
            WriteReport(); // Durable identity evidence before Rhino startup or any fixture capture.
            Console.WriteLine("SMOKE_IDENTITY " + JsonSerializer.Serialize(new { fixture = _fixture, runId = _runId,
                plugin = new { path = _loadedPlugin.Location, sha256 = _pluginSha256 },
                core = new { path = _loadedCore.Location, sha256 = _coreSha256 } }));
            Console.Out.Flush();
            if (args[0] == "--identity-only") return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        // Rhino's in-process startup can report existing plug-in registrations in a
        // completed informational window. Dismiss only its exact Close button in this
        // process; never answer license, install, or any other startup decisions.
        _ = Task.Run(() =>
        {
            for (int attempt = 0; attempt < 60; attempt++)
            {
                EnumWindows((handle, argument) =>
                {
                    GetWindowThreadProcessId(handle, out int pid);
                    if (pid != Environment.ProcessId) return true;
                    var text = new System.Text.StringBuilder(512);
                    GetWindowText(handle, text, text.Capacity);
                    if (text.ToString() != "Done installing plug-ins, press close to continue") return true;
                    EnumChildWindows(handle, (button, data) =>
                    {
                        var caption = new System.Text.StringBuilder(128);
                        GetWindowText(button, caption, caption.Capacity);
                        if (caption.ToString() == "&Close")
                        {
                            Console.WriteLine("Dismissing completed Rhino startup informational notice.");
                            SendMessage(button, 0x00F5, IntPtr.Zero, IntPtr.Zero);
                        }
                        return true;
                    }, IntPtr.Zero);
                    return true;
                }, IntPtr.Zero);
                Thread.Sleep(1000);
            }
        });
        try { return RunHost(); }
        catch (Exception error)
        {
            _phase = _completed ? "host-shutdown-failed" : "host-startup-failed";
            _failure = error.ToString();
            WriteReport();
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name + ".dll";
        foreach (string directory in new[] { _system, _grasshopper, Path.GetDirectoryName(_plugin)! })
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunHost()
    {
        Console.WriteLine("Starting Rhino 8 host (" + _hostMode + "); no plugin installation will be performed.");
        Console.WriteLine("Host process: " + Environment.ProcessId + "; runtime: " + RuntimeInformation.FrameworkDescription);
        _phase = "host-startup";
        WriteReport();
        using var host = new RhinoCore(new[] { "/nosplash", "/notemplate" },
            _hostMode == "inactive-desktop" ? WindowStyle.Normal : WindowStyle.Hidden);
        Console.WriteLine("Rhino initialized: " + Rhino.RhinoApp.Version);
        _phase = "grasshopper-startup";
        WriteReport();
        using var eto = new Eto.Forms.Application(new Eto.Wpf.Platform());
        var pluginId = Rhino.PlugIns.PlugIn.IdFromName("Grasshopper");
        Assert(pluginId != Guid.Empty && Rhino.PlugIns.PlugIn.LoadPlugIn(pluginId, true, true), "Installed Grasshopper host plug-in starts before UI rendering");
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
        int exitCode = 1;
        using var context = new System.Windows.Forms.ApplicationContext();
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += async (sender, args) =>
        {
            timer.Stop();
            try
            {
                // Some fixtures previously inherited the active document and view
                // created by an earlier check in the combined host.
                var rhino = Rhino.RhinoDoc.ActiveDoc ?? Rhino.RhinoDoc.Create(null);
                if (rhino.Views.ActiveView == null)
                    rhino.Views.ActiveView = rhino.Views.Add("Panel fixture", Rhino.Display.DefinedViewportProjection.Perspective,
                        new Rectangle(0, 0, 640, 480), false);
                if (Rhino.RhinoDoc.ActiveDoc == null || rhino.Views.ActiveView == null)
                    throw new InvalidOperationException("The isolated host could not create an active Rhino document and viewport.");
                GH_Document.EnableSolutions = true;
                _phase = "fixture";
                WriteReport();
                SmokeAssertions.Begin();
                try
                {
                    await _fixtures[_fixture](_loadedPlugin!);
                    _passed = true;
                    exitCode = 0;
                }
                catch (Exception error) { _failure = error.ToString(); Console.Error.WriteLine(error); }
                finally
                {
                    SmokeAssertions.End();
                    _completed = true;
                    _phase = _passed ? "fixture-completed" : "fixture-failed";
                    WriteReport();
                }
            }
            catch (Exception error)
            {
                _failure = error.ToString();
                _phase = "host-preparation-failed";
                Console.Error.WriteLine(error);
                WriteReport();
            }
            finally
            {
                Console.Out.Flush(); Console.Error.Flush();
                if (_hostMode == "inactive-desktop")
                {
                    // This isolated host owns only fixture documents. Request
                    // Rhino shutdown before ending its native message loop;
                    // posting WM_QUIT alone leaves native shutdown incomplete.
                    if (Rhino.RhinoDoc.ActiveDoc is { } document) document.Modified = false;
                    Rhino.RhinoApp.Exit(false);
                    PostQuitMessage(0);
                }
                else context.ExitThread();
            }
        };
        timer.Start();
        if (_hostMode == "inactive-desktop")
        {
            // Viewport fixtures need Rhino's message preprocessing as well as
            // its idle work. Forwarding WinForms Idle to DoIdle is insufficient
            // for repeated native captures. Idle is still not a display fence.
            _rhinoIdlePump = true;
            Console.WriteLine("The inactive-desktop host runs Rhino's native message loop and idle tasks.");
            host.Run();
        }
        else System.Windows.Forms.Application.Run(context);
        return exitCode;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Assembly LoadedCoreAssembly() => typeof(PanelSnapshot).Assembly;

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void WriteReport()
    {
        var report = new
        {
            schemaVersion = 1, fixture = _fixture, runId = _runId, startedUtc = _startedUtc,
            completed = _completed, passed = _passed, assertionCount = SmokeAssertions.Count,
            phase = _phase, failure = _failure, hostMode = _hostMode,
            rhinoIdlePump = _rhinoIdlePump,
            processId = Environment.ProcessId, runtime = RuntimeInformation.FrameworkDescription,
            plugin = new { path = _loadedPlugin!.Location, sha256 = _pluginSha256 },
            core = new { path = _loadedCore!.Location, sha256 = _coreSha256 }
        };
        using var stream = new FileStream(_resultPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 1024, true);
        writer.Write(JsonSerializer.Serialize(report));
        writer.Flush();
        stream.Flush(true);
    }

    private static void Assert(bool condition, string name) => SmokeAssertions.Check(condition, name);
}
