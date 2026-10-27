using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using GH_IO.Serialization;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Runtime.InProcess;

internal static class Program
{
    private static string _system = string.Empty;
    private static string _grasshopper = string.Empty;
    private static string _plugin = string.Empty;
    private static string _resultPath = string.Empty;
    private static bool _canvasDrawingVerified;
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
    private static string? _failure;

    // This is also checked against fixtures.json, the coordinator's explicit inventory.
    private static Dictionary<string, Func<Assembly, Task>> _fixtures = null!;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Dictionary<string, Func<Assembly, Task>> CreateFixtures() => new(StringComparer.Ordinal)
    {
        ["panel-handle"] = assembly => { PanelHandleChecks.Run(assembly); return Task.CompletedTask; },
        ["output"] = OutputChecks.Run,
        ["output-pending"] = OutputChecks.RunPending,
        ["row-numbering"] = RowNumberingChecks.Run,
        ["index-selection"] = IndexSelectionChecks.Run,
        ["capsule"] = CapsuleChecks.Run,
        ["capsule-double-click"] = CapsuleChecks.NativeDoubleClickChecks,
        ["recompute-presentation"] = RecomputeChecks.VerifyPresentation,
        ["recompute-output"] = RecomputeChecks.RunOutput,
        ["recompute-regex"] = RecomputeChecks.RunWhitespaceRegex,
        ["recompute-local-preview"] = RecomputeChecks.RunLocalPreview,
        ["recompute-disabled"] = RecomputeChecks.RunDisabled,
        ["geometry-hint"] = GeometryHintChecks.Run,
        ["analytic-curve-details"] = assembly => { FixtureUserData.EnsureRegistered(); return AnalyticCurveDetailsChecks.Run(assembly); },
        ["capture-safety"] = assembly => { FixtureUserData.EnsureRegistered(); CaptureSafetyChecks.VerifyManaged(assembly); CaptureSafetyChecks.VerifyNativeCapabilities(assembly); CaptureSafetyChecks.VerifyNativeUserData(assembly); return Task.CompletedTask; },
        ["polyline-safety"] = assembly => { FixtureUserData.EnsureRegistered(); return GeometryPolylineSafetyChecks.Run(assembly); },
        ["nurbs-value-candidate"] = assembly => { FixtureUserData.EnsureRegistered(); return GeometryNurbsCandidateChecks.Run(assembly); },
        ["production-curve-measurement"] = assembly => { FixtureUserData.EnsureRegistered(); return ProductionCurveMeasurementChecks.Run(assembly); },
        ["geometry-canvas-viewport"] = GeometryHintChecks.VerifyCanvasViewport,
        ["measurement-priority"] = GeometryMeasurementBatchChecks.VerifyPriority,
        ["measurement-cancellation"] = GeometryMeasurementBatchChecks.VerifyCancellation,
        ["measurement-invalidation"] = GeometryMeasurementBatchChecks.VerifyInvalidation,
        ["measurement-failures"] = GeometryMeasurementBatchChecks.VerifyFailures,
        ["row-actions"] = RowActionChecks.Run,
        ["highlight-overlap"] = RowActionChecks.RunOverlap,
        ["filter-metadata"] = assembly => { FilterChecks.VerifyMetadata(assembly); return Task.CompletedTask; },
        ["filter-measurements"] = FilterChecks.VerifyMeasurements,
        ["filter-cancellation"] = FilterChecks.VerifyCancellation,
        ["filter-snapshot-replacement"] = FilterChecks.VerifySnapshotReplacement,
        ["filter-lifecycle-paths"] = FilterChecks.VerifyLifecycleAndPaths,
        ["filter-displayed-output"] = FilterChecks.VerifyDisplayedOutput,
        ["filter-displayed-geometry"] = FilterChecks.RunDisplayedGeometryOutput,
        ["filter-ui"] = FilterUiChecks.Run,
        ["filter-ui-choices"] = FilterUiChecks.VerifyTypedChoicesAndStaleEditors,
        ["full-value"] = FullValueChecks.Run,
        ["view-freshness"] = ViewFreshnessChecks.Run,
        ["filter-footer-statistics"] = FilterUiChecks.VerifyFooterStatistics,
        ["filter-preview-undo"] = FilterUiChecks.VerifyPreviewUndo,
        ["filter-display-values"] = FilterUiChecks.VerifyDisplayValues,
        ["statistics-ui"] = StatisticsUiChecks.Run,
        ["create-set"] = CreateSetChecks.Run,
        ["set-details"] = SetDetailsChecks.Run,
        ["display-set"] = DisplaySetChecks.Run,
        ["item-order-ui"] = ItemOrderUiChecks.Run,
        ["item-order-capture"] = ItemOrderUiChecks.CaptureViewport,
        ["integration"] = async assembly => { await ExecuteIntegration(assembly); },
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string path);
    private delegate bool WindowCallback(IntPtr handle, IntPtr argument);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr argument);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, WindowCallback callback, IntPtr argument);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr handle, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, System.Text.StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

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
                VerifyTreeOperations(_loadedPlugin);
                CreateSetChecks.VerifyTree(_loadedPlugin);
                OutputChecks.VerifyTree(_loadedPlugin);
                VerifyUnitOperations(_loadedPlugin);
                CaptureSafetyChecks.VerifyManaged(_loadedPlugin);
                FullValueChecks.VerifyPaintClipping(_loadedPlugin);
                Console.WriteLine("PASS: Managed Grasshopper tree and unit operation checks completed (Rhino host not started).");
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
        Console.WriteLine("Starting hidden Rhino 8 host; no plugin installation will be performed.");
        using var host = new RhinoCore(new[] { "/nosplash", "/notemplate" }, WindowStyle.Hidden);
        Console.WriteLine("Rhino initialized: " + Rhino.RhinoApp.Version);
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
            finally { Console.Out.Flush(); Console.Error.Flush(); context.ExitThread(); }
        };
        timer.Start();
        System.Windows.Forms.Application.Run(context);
        return exitCode;
    }

    private static async Task<int> ExecuteIntegration(Assembly assembly)
    {
        var componentType = assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!;
        var component = (GH_Component)Activator.CreateInstance(componentType)!;
        var componentIcon = (Bitmap)componentType.GetProperty("Icon", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!;
        Assert(componentIcon.Width == 24 && componentIcon.Height == 24, "Embedded component icon is valid 24 × 24 bitmap");
        var libraryInfo = (GH_AssemblyInfo)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterGhPanelInfo", true)!)!;
        var libraryIcon = libraryInfo.Icon;
        Assert(libraryIcon != null && libraryIcon.Width == 24 && libraryIcon.Height == 24, "Assembly metadata resolves a valid embedded icon");
        var expectedVersion = typeof(PanelSnapshot).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        Assert(libraryInfo.Name == "better-gh-panel" && libraryInfo.AssemblyVersion == expectedVersion, "Plugin metadata matches Yak package name and semantic version");
        component.CreateAttributes();
        using var document = new GH_Document();
        GH_Document.EnableSolutions = true;
        document.Enabled = true;
        var source = new Param_GenericObject();
        source.CreateAttributes();
        source.PersistentData.Append(new GH_Number(10), new GH_Path(0, 1));
        source.PersistentData.Append(new GH_Number(100), new GH_Path(0, 1));
        source.PersistentData.Append(null!, new GH_Path(0, 1));
        source.PersistentData.Append(new GH_String("alpha\nbeta"), new GH_Path(0, 2));
        source.PersistentData.Append(new GH_Point(new Rhino.Geometry.Point3d(1, 2, 3)), new GH_Path(0, 2));
        source.PersistentData.EnsurePath(new GH_Path(0, 3));
        document.AddObject(source, false);
        document.AddObject(component, false);
        component.Params.Input[0].AddSource(source);
        document.NewSolution(false, GH_SolutionMode.Silent);
        object session = PanelTestAdapter.Session(component);
        Console.WriteLine("GH solved: " + component.Params.Output[0].VolatileData.DataCount + " output items.");
        Assert(PanelTestAdapter.Size(component) == new SizeF(360, 360), "New canvas panels default to a 1:1 ratio");
        var input = component.Params.Input[0].VolatileData;
        var output = component.Params.Output[0].VolatileData;
        Assert(input.DataCount == 5, "Native Grasshopper solver collected all source items");
        AssertEqualTrees(input, output);
        await WaitForQuery(session);
        var snapshot = Get<PanelSnapshot>(session, "Snapshot");
        Assert(snapshot.Rows.Count == input.DataCount, "Snapshot preserves input item positions");
        Assert(snapshot.Branches.Count == input.PathCount, "Snapshot preserves input branch paths");

        var query = new PanelQueryOptions { Search = "> 42", SortMode = PanelSortMode.ValueDescending };
        document.IsModified = false;
        UpdateQuery(session, query);
        Assert(document.IsModified, "Changed query settings mark the Grasshopper document modified");
        await WaitForQuery(session);
        var result = Get<PanelQueryResult>(session, "Result");
        Assert(result.RowPositions.Count == 1 && result.Snapshot.Rows[result.RowPositions[0]].NumericValue == 100,
            "Native query filters and sorts without mutating source");
        AssertEqualTrees(input, component.Params.Output[0].VolatileData);
        document.IsModified = false;
        UpdateQuery(session, query.Clone());
        await WaitForQuery(session);
        Assert(!document.IsModified, "Unchanged query and worker notifications do not mark the document modified");
        Assert(ReferenceEquals(result, Get<PanelQueryResult>(session, "Result")), "Equal query submissions reuse the published result");
        session.GetType().GetMethod("AttachSnapshot")!.Invoke(session, new object[] { snapshot });
        await WaitForQuery(session);
        Assert(!document.IsModified, "New snapshot notifications do not mark view settings modified");

        for (int i = 0; i < 100; i++) UpdateQuery(session, new PanelQueryOptions { Search = "> " + i });
        UpdateQuery(session, new PanelQueryOptions { Search = "alpha", BranchPath = "{0;2}", SortMode = PanelSortMode.ValueDescending });
        await WaitForQuery(session);
        result = Get<PanelQueryResult>(session, "Result");
        Assert(result.RowPositions.Count == 1 && result.Snapshot.Rows[result.RowPositions[0]].TypeName == "Text",
            "Rapid cancellation publishes the latest query only");
        session.GetType().GetMethod("ResetView")!.Invoke(session, Array.Empty<object>());
        await WaitForQuery(session);

        var independentRows = snapshot.Rows.Select(row => new PanelRow(
            row.OriginalIndex, row.Path, row.TypeName, "cached summary", row.NumericValue, row.IsNull, row.IsValid)).ToArray();
        var independentSnapshot = new PanelSnapshot(independentRows, snapshot.Branches);
        session.GetType().GetMethod("AttachSnapshot")!.Invoke(session, new object[] { independentSnapshot });
        await WaitForQuery(session);
        Assert(PanelTestAdapter.DisplayValue(component, independentSnapshot, 0) == "cached summary",
            "Canvas presentation uses captured summaries instead of live goo formatting");
        Assert(PanelTestAdapter.CopyValue(component, independentSnapshot, 0) == "10",
            "Value copying uses captured full-precision numeric data");
        session.GetType().GetMethod("AttachSnapshot")!.Invoke(session, new object[] { snapshot });
        Assert(PanelTestAdapter.DisplayValue(component, independentSnapshot, 0) == "cached summary",
            "Obsolete snapshots never resolve display values from a newer solution");
        session.GetType().GetMethod("ResetView")!.Invoke(session, Array.Empty<object>());
        await WaitForQuery(session);
        UpdateQuery(session, new PanelQueryOptions { Search = "alpha", BranchPath = "{0;2}", SortMode = PanelSortMode.ValueDescending });
        await WaitForQuery(session);

        VerifyTreeOperations(assembly);
        VerifyUnitOperations(assembly);
        PanelTestAdapter.TreeTools(component, 1, true, true, true);
        await WaitForQuery(session);
        Assert(PanelTestAdapter.TrimDepth(component) == 1 && component.Params.Output[0].VolatileData.PathCount == 1,
            "Tree settings recompute the Grasshopper output");
        PanelTestAdapter.Resize(component, 500, 420);
        UpdateQuery(session, new PanelQueryOptions { Search = "alpha", BranchPath = "{0}", SortMode = PanelSortMode.ValueDescending });
        var archive = new GH_Archive();
        Assert(archive.AppendObject(component, "Panel"), "Component settings serialize");
        var restored = (GH_Component)Activator.CreateInstance(componentType)!;
        restored.CreateAttributes();
        Assert(archive.ExtractObject(restored, "Panel"), "Component settings deserialize");
        object restoredSession = PanelTestAdapter.Session(restored);
        var restoredOptions = Get<PanelQueryOptions>(restoredSession, "Options");
        Assert(restoredOptions.Search == "alpha" && restoredOptions.BranchPath == "{0}" &&
            restoredOptions.SortMode == PanelSortMode.ValueDescending, "Search, branch and sort survive serialization");
        Assert(PanelTestAdapter.Size(restored).Width == 500 && PanelTestAdapter.Size(restored).Height == 420,
            "Canvas width and height survive serialization");
        Assert(PanelTestAdapter.Preset(restored) == -1, "Custom dimensions do not retain an unrelated preset check mark");
        Assert(PanelTestAdapter.TrimDepth(restored) == 1 && PanelTestAdapter.TreeFlag(restored, "Flatten") &&
            PanelTestAdapter.TreeFlag(restored, "Reverse") && PanelTestAdapter.TreeFlag(restored, "Simplify"),
            "All tree operations survive serialization");
        var legacy = new GH_LooseChunk("Panel");
        Assert(restored.Write(legacy), "Legacy fixture serializes the component");
        legacy.RemoveItem("PanelWidth");
        legacy.RemoveItem("PanelHeight");
        legacy.RemoveItem("PanelSize");
        legacy.SetInt32("PanelSize", 2);
        Assert(restored.Read(legacy), "Legacy preset-only settings deserialize");
        Assert(PanelTestAdapter.Size(restored).Width == 440 && PanelTestAdapter.Size(restored).Height == 400 &&
            PanelTestAdapter.Preset(restored) == 2, "Legacy size presets supply dimensions when explicit dimensions are absent");
        foreach (var key in new[] { "PanelTrimDepth", "PanelFlatten", "PanelReverse", "PanelSimplify", "PanelSize" }) legacy.RemoveItem(key);
        Assert(restored.Read(legacy) && PanelTestAdapter.TrimDepth(restored) == 0 &&
            !PanelTestAdapter.TreeFlag(restored, "Flatten") && !PanelTestAdapter.TreeFlag(restored, "Reverse") &&
            !PanelTestAdapter.TreeFlag(restored, "Simplify") && PanelTestAdapter.Size(restored) == new SizeF(360, 360),
            "Older definitions without tree settings default to unchanged output and square size");
        restored.RemovedFromDocument(document);
        PanelTestAdapter.ResetView(component);
        PanelTestAdapter.TreeTools(component, 0, false, false, false);
        await WaitForQuery(session);
        AssertEqualTrees(input, component.Params.Output[0].VolatileData);

        component.DocumentContextChanged(document, GH_DocumentContext.Unloaded);
        component.DocumentContextChanged(document, GH_DocumentContext.Loaded);
        object reloadedSession = PanelTestAdapter.Session(component);
        await WaitForQuery(reloadedSession);
        Assert(Get<PanelSnapshot>(reloadedSession, "Snapshot").Rows.Count == input.DataCount,
            "Switching document tabs retains the cached tree without recomputing");

        component.Attributes.Pivot = new PointF(80, 50);
        component.Attributes.ExpireLayout();
        component.Attributes.PerformLayout();
        using var canvas = new GH_Canvas { Size = new Size(1200, 900) };
        await VerifyCanvasTreeTools(component, canvas, assembly, session, input);
        await VerifyCanvasReset(component, canvas, assembly, session, input, document);
        var sampleRows = Enumerable.Range(0, 10000).Select(i => new PanelRow(i, "{0;0}", "Number", i.ToString(), i)).ToArray();
        session.GetType().GetMethod("AttachSnapshot")!.Invoke(session, new object[] { new PanelSnapshot(sampleRows, new[] { new PanelBranch("{0;0}", 10000) }) });
        session.GetType().GetMethod("ResetView")!.Invoke(session, Array.Empty<object>());
        await WaitForQuery(session);
        PanelTestAdapter.Resize(component, 360, 360);
        component.Attributes.PerformLayout();
        DrawCanvasPreview(component, canvas);
        _canvasDrawingVerified = true;
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Jump));
        CanvasEditorAdapter.Active(assembly).Submit("5000");
        DrawCanvasPreview(component, canvas);
        var list = PanelTestAdapter.Bounds(component, CanvasControl.List);
        component.Attributes.RespondToMouseDown(canvas, Mouse(new PointF(list.X + 40, list.Y + 24)));
        Assert(PanelTestAdapter.Offset(component) == 4999 && PanelTestAdapter.SelectedPosition(component) >= 0,
            "Reset regression starts with scrolled, selected rows and default query options");
        var unchangedResult = Get<PanelQueryResult>(session, "Result");
        document.IsModified = false;
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Reset));
        Assert(PanelTestAdapter.Offset(component) == 0 && PanelTestAdapter.SelectedPosition(component) == -1 &&
            ReferenceEquals(unchangedResult, Get<PanelQueryResult>(session, "Result")) && !document.IsModified,
            "Reset clears scroll and selection even when no query scan or document change is needed");
        float originalZoom = canvas.Viewport.Zoom;
        canvas.Viewport.Zoom = 0.5f;
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Jump));
        DrawCanvasPreview(component, canvas);
        Assert(CanvasEditorAdapter.Active(assembly).TextAreaWidth >= Grasshopper.Global_Proc.UiAdjust(100),
            "Compact Jump editor retains a readable text area after rendering at 50% zoom");
        CanvasEditorAdapter.Active(assembly).Submit("1");
        canvas.Viewport.Zoom = originalZoom;
        Assert(component.Params.Input[0].Attributes.InputGrip.X == PanelTestAdapter.Bounds(component, CanvasControl.Panel).Left &&
            component.Params.Output[0].Attributes.OutputGrip.X == PanelTestAdapter.Bounds(component, CanvasControl.Panel).Right,
            "Native wire grips sit on the panel edges");

        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Type));
        ClickMenuRow(component, canvas, 2);
        await WaitForQuery(session);
        Assert(Get<PanelQueryOptions>(session, "Options").TypeName == "Number", "Canvas type menu updates the query");
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Sort));
        ClickMenuRow(component, canvas, 2);
        await WaitForQuery(session);
        result = Get<PanelQueryResult>(session, "Result");
        Assert(result.Snapshot.Rows[result.RowPositions[0]].NumericValue == 9999, "Canvas sort menu orders values descending");

        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Search));
        var editor = CanvasEditorAdapter.Active(assembly);
        editor.Submit("> 42");
        await WaitForQuery(session);
        Assert(Get<PanelQueryOptions>(session, "Options").Search == "> 42" && Get<PanelQueryResult>(session, "Result").RowPositions.Count == 9957,
            "Host canvas textbox accepts inline search and filters the view");
        editor.AcceptAgain("second commit");
        Assert(Get<PanelQueryOptions>(session, "Options").Search == "> 42", "Inline textbox commits its callback once");
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Search));
        editor = CanvasEditorAdapter.Active(assembly);
        component.DocumentContextChanged(document, GH_DocumentContext.Unloaded);
        editor.AcceptAgain("canceled commit");
        Assert(Get<PanelQueryOptions>(session, "Options").Search == "> 42", "Document switching cancels inline text without committing");
        component.DocumentContextChanged(document, GH_DocumentContext.Loaded);
        session.GetType().GetMethod("ResetView")!.Invoke(session, Array.Empty<object>());
        await WaitForQuery(session);
        DrawCanvasPreview(component, canvas);

        var resize = PanelTestAdapter.Bounds(component, CanvasControl.Resize);
        var start = new PointF(resize.X + 5, resize.Y + 5);
        Assert(component.Attributes.RespondToMouseDown(canvas, Mouse(start)) == GH_ObjectResponse.Capture, "Canvas resize captures the mouse");
        component.Attributes.RespondToMouseMove(canvas, Mouse(new PointF(start.X + 140, start.Y + 80)));
        Assert(PanelTestAdapter.Size(component).Width == 500 && PanelTestAdapter.Size(component).Height == 440,
            "Dragging the resize corner updates panel dimensions");
        Assert(component.Params.Output[0].Attributes.OutputGrip.X == PanelTestAdapter.Bounds(component, CanvasControl.Panel).Right,
            "Native output grip follows resized panel bounds");
        Assert(component.Attributes.RespondToMouseUp(canvas, Mouse(start)) == GH_ObjectResponse.Release, "Canvas resize releases the mouse");
        PanelTestAdapter.Resize(component, 360, 360);
        component.Attributes.PerformLayout();
        DrawCanvasPreview(component, canvas);
        DrawCanvasPreview(component, canvas, 192);
        AssertCanvasDpiInvariant();
        await VerifyCanvasUnits(component, canvas, assembly, session, input);
        await VerifyUndoRedo(component, document, session, input);
        // Review screenshot uses a real solved mixed tree with enough rows to show the compact layout.
        source.PersistentData.Append(new GH_Surface(new Rhino.Geometry.PlaneSurface(Rhino.Geometry.Plane.WorldXY,
            new Rhino.Geometry.Interval(0, 10), new Rhino.Geometry.Interval(0, 10)).ToBrep()), new GH_Path(0, 3));
        source.PersistentData.Append(new GH_Mesh(Rhino.Geometry.Mesh.CreateFromBox(new Rhino.Geometry.BoundingBox(
            Rhino.Geometry.Point3d.Origin, new Rhino.Geometry.Point3d(2, 2, 2)), 1, 1, 1)), new GH_Path(0, 3));
        source.PersistentData.Append(new GH_Curve(new Rhino.Geometry.LineCurve(Rhino.Geometry.Point3d.Origin,
            new Rhino.Geometry.Point3d(10, 0, 0))), new GH_Path(0, 3));
        source.PersistentData.Append(new GH_Line(new Rhino.Geometry.Line(Rhino.Geometry.Point3d.Origin,
            new Rhino.Geometry.Point3d(3, 4, 0))), new GH_Path(0, 3));
        source.PersistentData.Append(new GH_Brep(new Rhino.Geometry.Box(new Rhino.Geometry.BoundingBox(
            Rhino.Geometry.Point3d.Origin, new Rhino.Geometry.Point3d(2, 3, 4))).ToBrep()), new GH_Path(0, 3));
        source.PersistentData.Append(new GH_Curve(), new GH_Path(0, 3));
        for (int i = 0; i < 24; i++)
            source.PersistentData.Append(new GH_Number(1200.5 + i * 17.35), new GH_Path(0, 3));
        source.ExpireSolution(false);
        document.NewSolution(false, GH_SolutionMode.Silent);
        PanelTestAdapter.TreeTools(component, 0, false, false, true);
        PanelTestAdapter.Units(component, PanelUnitConversion.None, 2);
        await WaitForQuery(session);
        Assert(Get<PanelSnapshot>(session, "Snapshot").TypeCounts.ContainsKey("Surface") &&
            Get<PanelSnapshot>(session, "Snapshot").TypeCounts.ContainsKey("Mesh"), "Geometry hints use real Grasshopper surface and mesh types");
        UpdateQuery(session, new PanelQueryOptions { TypeName = "Surface" });
        await WaitForQuery(session);
        document.NewSolution(false, GH_SolutionMode.Silent);
        Assert(Get<PanelQueryResult>(session, "Result").RowPositions.Count == 1 && component.Params.Output[0].VolatileData.DataCount == 1 &&
            component.Params.Output[0].VolatileData.AllData(true).Single() is GH_Surface,
            "Abbreviated geometry hints retain full-name filtering and publish matching typed output");
        DrawCanvasPreview(component, canvas, artifactName: "rhino-smoke-surface-hint.png");
        var surfacePosition = Get<PanelQueryResult>(session, "Result").RowPositions[0];
        var surfaceTooltip = PanelTestAdapter.Tooltip(component, canvas, surfacePosition);
        Assert(!surfaceTooltip.Description.Contains("Area:") && surfaceTooltip.Description.Contains("Calculating measurement") &&
            surfaceTooltip.Description.Contains("Trimming: Untrimmed") && surfaceTooltip.Description.Contains("Degree (U/V): 1/1"),
            "Pending tooltips show captured metadata while visible geometry is queued without inline calculation");
        var surfaceSnapshot = Get<PanelSnapshot>(session, "Snapshot");
        Click(component, canvas, PanelTestAdapter.RowBounds(component, surfacePosition));
        Assert(!GetGeometryMeasurement(session, surfaceSnapshot, surfacePosition).HasValue,
            "Selecting a row does not calculate geometry");
        DrawCanvasPreview(component, canvas, artifactName: "rhino-smoke-measure-pending.png");
        var beforeMeasure = Get<PanelQueryResult>(session, "Result");
        await WaitForMeasurement(session, surfaceSnapshot, surfacePosition);
        Assert(GetGeometryMeasurement(session, surfaceSnapshot, surfacePosition) == 100 &&
            ReferenceEquals(beforeMeasure, Get<PanelQueryResult>(session, "Result")) && !Get<bool>(session, "IsBusy"),
            "Visible surface area appears asynchronously without a click, query or solution");
        surfaceTooltip = PanelTestAdapter.Tooltip(component, canvas, surfacePosition);
        DrawCanvasPreview(component, canvas, artifactName: "rhino-smoke-measured-surface.png");
        Assert(surfaceTooltip.Text == "Surface" && surfaceTooltip.Description.StartsWith("Area: 100.00 ", StringComparison.Ordinal) &&
            surfaceTooltip.Description.Contains("Trimming: Untrimmed") && surfaceTooltip.Description.Contains("Planarity: Planar") &&
            !surfaceTooltip.Description.Contains("Srf ·"), "Surface tooltip shows one metric followed by labeled metadata without repeating the compact row");
        Assert(PanelTestAdapter.DisplayValue(component, surfaceSnapshot, surfacePosition).StartsWith("Srf · untrimmed · planar · A 100.00 ", StringComparison.Ordinal),
            "Measured rows keep trimming and planarity beside their area");
        PanelTestAdapter.Units(component, PanelUnitConversion.SquareMillimetersToSquareMeters, -1);
        await WaitForQuery(session);
        var convertedSurface = Get<PanelSnapshot>(session, "Snapshot");
        Assert(GetGeometryMeasurement(session, convertedSurface, surfacePosition) == 100 &&
            PanelTestAdapter.DisplayValue(component, convertedSurface, surfacePosition).Contains("A 0.0001 m²") &&
            PanelTestAdapter.DisplayValue(component, convertedSurface, surfacePosition).Contains("untrimmed · planar"),
            "Units converts the cached metric while preserving surface metadata and its raw area");
        PanelTestAdapter.Units(component, PanelUnitConversion.SquareMillimetersToSquareMeters, 6);
        Assert(ReferenceEquals(convertedSurface, Get<PanelSnapshot>(session, "Snapshot")) &&
            PanelTestAdapter.DisplayValue(component, convertedSurface, surfacePosition).Contains("0.000100 m²"),
            "Decimals reformat geometry metrics without solving or measuring again");
        PanelTestAdapter.Units(component, PanelUnitConversion.SquareMillimetersToSquareMeters, -1);
        Assert(PanelTestAdapter.CopyValue(component, Get<PanelSnapshot>(session, "Snapshot"), surfacePosition) == "0.0001",
            "Copy value uses the converted measured area without labels or units");
        UpdateQuery(session, new PanelQueryOptions { TypeName = "Brep" });
        await WaitForQuery(session);
        DrawCanvasPreview(component, canvas, artifactName: "rhino-smoke-brep-hint.png");
        var brepPosition = Get<PanelQueryResult>(session, "Result").RowPositions[0];
        var brepTooltip = PanelTestAdapter.Tooltip(component, canvas, brepPosition);
        Assert(!brepTooltip.Description.Contains("Volume:"), "Brep paint and hover return before its volume is calculated");
        await WaitForMeasurement(session, Get<PanelSnapshot>(session, "Snapshot"), brepPosition);
        brepTooltip = PanelTestAdapter.Tooltip(component, canvas, brepPosition);
        Assert(brepTooltip.Text == "Brep" && brepTooltip.Description.Contains("Volume: 24 ") &&
            brepTooltip.Description.Contains("Topology: Closed solid") && brepTooltip.Description.Contains("Faces: 6") &&
            PanelTestAdapter.DisplayValue(component, Get<PanelSnapshot>(session, "Snapshot"), brepPosition).Contains("closed · 6 faces · V"),
            "Brep volume augments topology and face count in both the compact row and labeled tooltip");
        PanelTestAdapter.Units(component, PanelUnitConversion.CubicMillimetersToCubicMeters, -1);
        await WaitForQuery(session);
        var convertedBrep = Get<PanelSnapshot>(session, "Snapshot");
        Assert(Math.Abs((GetGeometryMeasurement(session, convertedBrep, brepPosition) ?? double.NaN) - 24) < 1e-9 &&
            PanelTestAdapter.DisplayValue(component, convertedBrep, brepPosition).Contains("2.4E-08 m³"),
            "Units offers cubic conversion and retains the measured solid volume: " + PanelTestAdapter.DisplayValue(component, convertedBrep, brepPosition));
        UpdateQuery(session, new PanelQueryOptions { TypeName = "Line" });
        PanelTestAdapter.Units(component, PanelUnitConversion.MillimetersToMeters, -1);
        await WaitForQuery(session);
        var linePosition = Get<PanelQueryResult>(session, "Result").RowPositions[0];
        var lineSnapshot = Get<PanelSnapshot>(session, "Snapshot");
        Assert(PanelTestAdapter.DisplayValue(component, lineSnapshot, linePosition) == "Ln · L 0.005 m",
            "Units converts analytic line metrics as well as asynchronous measurements");
        var geometryOutput = component.Params.Output[0].VolatileData.AllData(true).OfType<GH_Line>().Single();
        Assert(geometryOutput.Value.Length == 5, "Geometry unit display never scales output geometry");
        PanelTestAdapter.Units(component, PanelUnitConversion.None, 2);
        UpdateQuery(session, new PanelQueryOptions { OnlyInvalid = true });
        await WaitForQuery(session);
        DrawCanvasPreview(component, canvas, artifactName: "rhino-smoke-invalid-geometry.png");
        var invalidPosition = Get<PanelQueryResult>(session, "Result").RowPositions.Single();
        var invalidSnapshot = Get<PanelSnapshot>(session, "Snapshot");
        var invalidTooltip = PanelTestAdapter.Tooltip(component, canvas, invalidPosition);
        Assert(PanelTestAdapter.DisplayValue(component, invalidSnapshot, invalidPosition).StartsWith("Invalid ·", StringComparison.Ordinal) &&
            invalidTooltip.Text == "Invalid Curve" && invalidTooltip.Description.Contains(invalidSnapshot.Rows[invalidPosition].InvalidReason),
            "Invalid geometry has a visible text label and its reason in the tooltip");
        Click(component, canvas, PanelTestAdapter.RowBounds(component, invalidPosition));
        DrawCanvasPreview(component, canvas, artifactName: "rhino-smoke-invalid-selected.png");
        Assert(!GetGeometryMeasurement(session, invalidSnapshot, invalidPosition).HasValue,
            "Invalid geometry never requests a measurement");
        session.GetType().GetMethod("ResetView")!.Invoke(session, Array.Empty<object>());
        await WaitForQuery(session);
        DrawCanvasPreview(component, canvas);
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Units));
        DrawCanvasPreview(component, canvas, artifactName: "rhino-smoke-units-menu.png");
        VerifyCanvasDisplay(component, canvas, document);
        component.DocumentContextChanged(document, GH_DocumentContext.Close);
        Assert(!PanelTestAdapter.HasSession(component),
            "Document closure disposes the component session");
        Console.WriteLine("PASS: Windows .NET Rhino host smoke checks completed.");
        return 0;
    }

    private static void VerifyCanvasDisplay(GH_Component component, GH_Canvas canvas, GH_Document document)
    {
        var inputGrip = component.Params.Input[0].Attributes.InputGrip;
        var outputGrip = component.Params.Output[0].Attributes.OutputGrip;
        var result = Get<PanelQueryResult>(PanelTestAdapter.Session(component), "Result");
        var output = component.Params.Output[0].VolatileData;
        document.UndoServer.Clear();
        foreach (var state in new[] { "half", "full" })
        {
            Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Display));
            float expectedHeight = state == "full" ? 360 : 180;
            Assert(PanelTestAdapter.Bounds(component, CanvasControl.Panel).Height == expectedHeight,
                "Single native display control cycles to " + state);
            Assert(component.Params.Input[0].Attributes.InputGrip == inputGrip &&
                component.Params.Output[0].Attributes.OutputGrip == outputGrip,
                "Folding keeps both native wire connections fixed");
        }
        Assert(PanelTestAdapter.Size(component) == new SizeF(360, 360) &&
            ReferenceEquals(result, Get<PanelQueryResult>(PanelTestAdapter.Session(component), "Result")) &&
            ReferenceEquals(output, component.Params.Output[0].VolatileData), "Folding preserves saved size, query and typed output");
        document.UndoServer.PerformUndo();
        component.Attributes.PerformLayout();
        Assert(PanelTestAdapter.Bounds(component, CanvasControl.Panel).Height == 180, "Native undo restores half display");
        var archive = new GH_Archive();
        var restored = (GH_Component)Activator.CreateInstance(component.GetType())!;
        restored.CreateAttributes();
        Assert(archive.AppendObject(component, "Display") && archive.ExtractObject(restored, "Display"), "Folded display serializes");
        restored.Attributes.PerformLayout();
        Assert(PanelTestAdapter.Bounds(restored, CanvasControl.Panel).Height == 180 &&
            PanelTestAdapter.Size(restored) == new SizeF(360, 360), "Saved half view retains the full panel size");
        var legacy = new GH_LooseChunk("Display");
        restored.Write(legacy);
        legacy.RemoveItem("PanelDisplay");
        legacy.SetInt32("PanelDisplay", 2);
        Assert(restored.Read(legacy), "Previously collapsed display settings load");
        restored.Attributes.PerformLayout();
        Assert(PanelTestAdapter.Bounds(restored, CanvasControl.Panel).Height == 360, "Previously collapsed panels reopen in Full mode");
        legacy.RemoveItem("PanelDisplay");
        Assert(restored.Read(legacy), "Legacy display settings load");
        restored.Attributes.PerformLayout();
        Assert(PanelTestAdapter.Bounds(restored, CanvasControl.Panel).Height == 360, "Older definitions open in Full mode");
        restored.RemovedFromDocument(document);
        document.UndoServer.PerformRedo();
        component.Attributes.PerformLayout();
        Assert(PanelTestAdapter.Bounds(component, CanvasControl.Panel).Height == 360, "Native redo restores full display");
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Display));
        var corner = PanelTestAdapter.Bounds(component, CanvasControl.Resize);
        var start = new PointF(corner.X + 5, corner.Y + 5);
        float bottom = PanelTestAdapter.Bounds(component, CanvasControl.Panel).Bottom;
        component.Attributes.RespondToMouseDown(canvas, Mouse(start));
        component.Attributes.RespondToMouseMove(canvas, Mouse(new PointF(start.X, start.Y + 24)));
        Assert(Math.Abs(PanelTestAdapter.Bounds(component, CanvasControl.Panel).Bottom - bottom - 24) < 0.01f,
            "Half panel resize corner follows the cursor");
        component.Attributes.RespondToMouseUp(canvas, Mouse(start));
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Display));
        PanelTestAdapter.Resize(component, 360, 360);
        component.Attributes.PerformLayout();
        document.UndoServer.Clear();
    }

    private static double? GetGeometryMeasurement(object session, PanelSnapshot snapshot, int position) =>
        ((PanelGeometryInfo?)session.GetType().GetMethod("GetGeometryDetails", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, new object[] { snapshot, position }))?.Value;

    private static async Task WaitForMeasurement(object session, PanelSnapshot snapshot, int position)
    {
        var has = session.GetType().GetMethod("HasGeometryMeasurement", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var timeout = Stopwatch.StartNew();
        while (!(bool)has.Invoke(session, new object[] { snapshot, position })!)
        {
            if (timeout.Elapsed.TotalSeconds > 15) throw new TimeoutException("Visible geometry measurement did not reach the UI");
            await Task.Delay(10);
        }
    }

    private static void VerifyTreeOperations(Assembly assembly)
    {
        var source = new GH_Structure<IGH_Goo>();
        var number = new GH_Number(10);
        var text = new GH_String("full\nvalue");
        source.Append(number, new GH_Path(7, 0, 0));
        source.Append(null!, new GH_Path(7, 0, 0));
        source.Append(text, new GH_Path(7, 0, 1));
        source.EnsurePath(new GH_Path(7, 1, 0));
        var apply = assembly.GetType("BetterGhPanel.Runtime.PanelTreeOperations", true)!
            .GetMethod("ApplyWithSources", BindingFlags.Static | BindingFlags.NonPublic)!;
        GH_Structure<IGH_Goo> Transform(int depth = 0, bool simplify = false, bool flatten = false, bool reverse = false) =>
            (GH_Structure<IGH_Goo>)apply.Invoke(null, new object?[] { source, depth, simplify, flatten, reverse, null })!;

        Assert(ReferenceEquals(source, Transform()), "Disabled tools pass through the original typed tree");
        var trimmed = Transform(1);
        Assert(trimmed.PathCount == 2 && trimmed.Paths[0].ToString() == "{7;0}" &&
            trimmed.Branches[0].Count == 3 && trimmed.Branches[1].Count == 0,
            "Trim merges trailing path levels in source order and preserves empty branches");
        Assert(ReferenceEquals(trimmed.Branches[0][0], number) && ReferenceEquals(trimmed.Branches[0][2], text) &&
            trimmed.Branches[0][1] == null, "Tree transformations preserve typed goo references and null items");
        Assert(Transform(int.MaxValue).Paths.Single().ToString() == "{0}", "Trim beyond path depth collapses to {0}");
        var simplified = Transform(simplify: true);
        Assert(simplified.Paths.Select(p => p.ToString()).SequenceEqual(new[] { "{0;0}", "{0;1}", "{1;0}" }),
            "Simplify removes path indices shared by all branches");
        var simplifiedWithEmptyBranch = Transform(1, simplify: true);
        Assert(simplifiedWithEmptyBranch.Paths.Select(p => p.ToString()).SequenceEqual(new[] { "{0}", "{1}" }) &&
            simplifiedWithEmptyBranch.Branches[0].SequenceEqual(new IGH_Goo[] { number, null!, text }) &&
            simplifiedWithEmptyBranch.Branches[1].Count == 0,
            "Simplify followed by Trim preserves separate empty output branches");
        var sharedSuffix = new GH_Structure<IGH_Goo>();
        sharedSuffix.Append(number, new GH_Path(7, 0, 9));
        sharedSuffix.Append(null!, new GH_Path(7, 0, 9));
        sharedSuffix.Append(text, new GH_Path(7, 1, 9));
        sharedSuffix.EnsurePath(new GH_Path(7, 2, 9));
        var simplifiedThenTrimmed = (GH_Structure<IGH_Goo>)apply.Invoke(null,
            new object?[] { sharedSuffix, 1, true, false, false, null })!;
        Assert(simplifiedThenTrimmed.PathCount == 1 && simplifiedThenTrimmed.Paths[0].ToString() == "{0}" &&
            simplifiedThenTrimmed.Branches.Single().SequenceEqual(new IGH_Goo[] { number, null!, text }),
            "Simplify removes shared prefix and suffix before Trim merges the remaining path levels");
        Assert(sharedSuffix.Paths.Select(p => p.ToString()).SequenceEqual(new[] { "{7;0;9}", "{7;1;9}", "{7;2;9}" }) &&
            sharedSuffix.Branches[0].SequenceEqual(new IGH_Goo[] { number, null! }) &&
            sharedSuffix.Branches[1].Single() == text && sharedSuffix.Branches[2].Count == 0,
            "Simplify followed by Trim preserves upstream paths, typed values, nulls and empty branches");
        var reversed = Transform(reverse: true);
        Assert(reversed.Branches[0][0] == null && ReferenceEquals(reversed.Branches[0][1], number) &&
            reversed.Branches[2].Count == 0, "Reverse operates within branches and preserves empty branches");
        var flattened = Transform(flatten: true);
        Assert(flattened.PathCount == 1 && flattened.Paths[0].ToString() == "{0}" &&
            flattened.Branches[0].SequenceEqual(new IGH_Goo[] { number, null!, text }),
            "Flatten merges every item into {0} in source order");
        var combined = Transform(1, simplify: true, flatten: true, reverse: true);
        Assert(combined.Branches.Single().SequenceEqual(new IGH_Goo[] { text, null!, number }),
            "Combined tools use Simplify, Trim, Flatten, then Reverse");
        Assert(source.Paths.Select(p => p.ToString()).SequenceEqual(new[] { "{7;0;0}", "{7;0;1}", "{7;1;0}" }) &&
            source.Branches[0].SequenceEqual(new IGH_Goo[] { number, null! }) && source.Branches[2].Count == 0,
            "Every tool leaves upstream paths, branch lists and values untouched");
        var empty = new GH_Structure<IGH_Goo>();
        var emptyResult = (GH_Structure<IGH_Goo>)apply.Invoke(null, new object?[] { empty, 3, true, true, true, null })!;
        Assert(emptyResult.DataCount == 0 && empty.DataCount == 0, "All tools safely handle empty input");
    }

    private static async Task VerifyCanvasTreeTools(GH_Component component, GH_Canvas canvas,
        Assembly assembly, object session, IGH_Structure input)
    {
        PanelTestAdapter.Resize(component, 360, 360);
        component.Attributes.PerformLayout();
        UpdateQuery(session, new PanelQueryOptions { BranchPath = "{0;2}", BranchPrefix = true });
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Reverse));
        await WaitForQuery(session);
        var reversedBranch = component.Params.Output[0].VolatileData.get_Branch(new GH_Path(0, 2));
        Assert(Get<PanelQueryOptions>(session, "Options").BranchPath == "{0;2}" &&
            Get<PanelQueryOptions>(session, "Options").BranchPrefix && reversedBranch[0] is GH_Point,
            "Reverse changes item order without clearing exact or prefix branch selection");
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Reverse));
        await WaitForQuery(session);
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Flatten));
        await WaitForQuery(session);
        var output = component.Params.Output[0].VolatileData;
        Assert(PanelTestAdapter.TreeFlag(component, "Flatten") && output.PathCount == 1 && output.DataCount == input.DataCount,
            "Canvas Flatten changes the real output tree");
        Assert(Get<PanelQueryOptions>(session, "Options").BranchPath == null, "Structural changes clear stale branch selections");
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Reverse));
        await WaitForQuery(session);
        output = component.Params.Output[0].VolatileData;
        var first = output.get_Branch(output.Paths[0])[0];
        var lastInput = input.get_Branch(input.Paths[1]);
        Assert(first?.ToString() == lastInput[lastInput.Count - 1]?.ToString(), "Canvas Reverse reverses the flattened output");
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Simplify));
        await WaitForQuery(session);
        Assert(PanelTestAdapter.TreeFlag(component, "Simplify"), "Canvas Simplify toggles persistently");
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Trim));
        DrawCanvasPreview(component, canvas);
        Assert(CanvasEditorAdapter.Active(assembly).TextAreaWidth >= Grasshopper.Global_Proc.UiAdjust(100),
            "Compact Trim editor retains a readable text area after rendering");
        CanvasEditorAdapter.Active(assembly).Submit("1");
        await WaitForQuery(session);
        Assert(PanelTestAdapter.TrimDepth(component) == 1, "Canvas Trim accepts a depth through the inline editor");
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Trim));
        CanvasEditorAdapter.Active(assembly).Submit("-1");
        Assert(PanelTestAdapter.TrimDepth(component) == 1, "Invalid trim depths leave the last setting unchanged");
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Reset));
        await WaitForQuery(session);
        Assert(PanelTestAdapter.TrimDepth(component) == 0 && !PanelTestAdapter.TreeFlag(component, "Flatten"),
            "Reset button disables output tree tools");
        PanelTestAdapter.TreeTools(component, 0, false, false, false);
        await WaitForQuery(session);
        AssertEqualTrees(input, component.Params.Output[0].VolatileData);
        Assert(Get<PanelSnapshot>(session, "Snapshot").Branches.Select(b => b.Path).SequenceEqual(input.Paths.Select(p => p.ToString())),
            "Canvas snapshot follows the solved output tree");
    }

    private static async Task VerifyCanvasReset(GH_Component component, GH_Canvas canvas,
        Assembly assembly, object session, IGH_Structure input, GH_Document document)
    {
        PanelTestAdapter.TreeTools(component, 1, true, true, true);
        PanelTestAdapter.Units(component, PanelUnitConversion.MillimetersToMeters, 2);
        UpdateQuery(session, new PanelQueryOptions { Search = "> 0", TypeName = "Number", BranchPath = "{0}",
            BranchPrefix = true, OnlyInvalid = true, SortMode = PanelSortMode.ValueDescending });
        await WaitForQuery(session);
        var transformed = Get<PanelSnapshot>(session, "Snapshot");
        document.UndoServer.Clear();
        var size = PanelTestAdapter.Size(component);
        var inputGrip = component.Params.Input[0].Attributes.InputGrip;
        var outputGrip = component.Params.Output[0].Attributes.OutputGrip;
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Units));
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Reset));
        await WaitForQuery(session);
        var options = Get<PanelQueryOptions>(session, "Options");
        Assert(PanelTestAdapter.TrimDepth(component) == 0 && !PanelTestAdapter.TreeFlag(component, "Flatten") &&
            !PanelTestAdapter.TreeFlag(component, "Reverse") && !PanelTestAdapter.TreeFlag(component, "Simplify") &&
            PanelTestAdapter.Conversion(component) == PanelUnitConversion.None && PanelTestAdapter.Decimals(component) == -1,
            "Reset disables every tree tool, conversion and fixed decimal formatting in one click");
        Assert(options.Search == "" && options.TypeName == null && options.BranchPath == null &&
            !options.BranchPrefix && !options.OnlyInvalid && options.SortMode == PanelSortMode.SourceOrder &&
            !PanelTestAdapter.MenuOpen(component), "Reset bypasses an open dropdown and clears every query setting");
        AssertEqualTrees(input, component.Params.Output[0].VolatileData);
        Assert(PanelTestAdapter.Size(component) == size && component.Params.Input[0].Attributes.InputGrip == inputGrip &&
            component.Params.Output[0].Attributes.OutputGrip == outputGrip, "Reset retains panel dimensions and wire grips");
        document.UndoServer.PerformUndo();
        await WaitForQuery(session);
        options = Get<PanelQueryOptions>(session, "Options");
        Assert(PanelTestAdapter.TrimDepth(component) == 1 && PanelTestAdapter.TreeFlag(component, "Flatten") &&
            PanelTestAdapter.TreeFlag(component, "Reverse") && PanelTestAdapter.TreeFlag(component, "Simplify") &&
            PanelTestAdapter.Conversion(component) == PanelUnitConversion.MillimetersToMeters && PanelTestAdapter.Decimals(component) == 2 &&
            options.Search == "> 0" && options.TypeName == "Number" && options.BranchPath == "{0}" && options.BranchPrefix &&
            options.OnlyInvalid && options.SortMode == PanelSortMode.ValueDescending &&
            Get<PanelSnapshot>(session, "Snapshot").Rows.Select(r => r.DisplayValue).SequenceEqual(transformed.Rows.Select(r => r.DisplayValue)),
            "One Undo restores every setting and transformed output before Reset");
        document.UndoServer.PerformRedo();
        await WaitForQuery(session);
        AssertEqualTrees(input, component.Params.Output[0].VolatileData);
        Assert(PanelTestAdapter.Decimals(component) == -1 && Get<PanelQueryOptions>(session, "Options").Search == "",
            "Redo restores the fully reset panel");
        foreach (var control in new[] { CanvasControl.Type, CanvasControl.Sort, CanvasControl.Branch, CanvasControl.Units })
        {
            Click(component, canvas, PanelTestAdapter.Bounds(component, control));
            Assert(PanelTestAdapter.MenuOpen(component), "Reset regression opens each dropdown");
            Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Reset));
            Assert(!PanelTestAdapter.MenuOpen(component), "Reset closes every dropdown on the first click");
        }
        foreach (var control in new[] { CanvasControl.Search, CanvasControl.Jump, CanvasControl.Trim })
        {
            Click(component, canvas, PanelTestAdapter.Bounds(component, control));
            var editor = CanvasEditorAdapter.Active(assembly);
            Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Reset));
            editor.AcceptAgain("2");
            Assert(Get<PanelQueryOptions>(session, "Options").Search == "" && PanelTestAdapter.TrimDepth(component) == 0 &&
                PanelTestAdapter.Offset(component) == 0, "Reset cancels stale search, jump and tree-tool editor callbacks");
        }
        PanelTestAdapter.Units(component, PanelUnitConversion.MillimetersToMeters, 2);
        PanelTestAdapter.TreeTools(component, 1, true, true, true);
        await WaitForQuery(session);
        PanelTestAdapter.ResetView(component);
        Assert(PanelTestAdapter.Conversion(component) == PanelUnitConversion.MillimetersToMeters && PanelTestAdapter.Decimals(component) == 2 &&
            PanelTestAdapter.TrimDepth(component) == 1, "Explicit Reset panel view retains output tools and units");
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Reset));
        await WaitForQuery(session);
        document.UndoServer.Clear();
    }

    private static void VerifyUnitOperations(Assembly assembly)
    {
        var source = new GH_Structure<IGH_Goo>();
        var number = new GH_Number(1234.56789);
        var integer = new GH_Integer(2000);
        var text = new GH_String("1234.5");
        var point = new GH_Point(new Rhino.Geometry.Point3d(1000, 2000, 3000));
        source.AppendRange(new IGH_Goo[] { number, integer, null!, text, point }, new GH_Path(0, 1));
        source.EnsurePath(new GH_Path(0, 2));
        var method = assembly.GetType("BetterGhPanel.Runtime.PanelUnitOperations", true)!
            .GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!;
        var converted = (GH_Structure<IGH_Goo>)method.Invoke(null, new object[] { source, PanelUnitConversion.MillimetersToMeters })!;
        Assert(Math.Abs(((GH_Number)converted.Branches[0][0]).Value - 1.23456789) < 1e-12 &&
            ((GH_Number)converted.Branches[0][1]).Value == 2, "Unit conversion converts Number and Integer items to full-precision numbers");
        Assert(converted.PathCount == 2 && converted.Branches[1].Count == 0 && converted.Branches[0][2] == null &&
            ReferenceEquals(converted.Branches[0][3], text) && ReferenceEquals(converted.Branches[0][4], point),
            "Unit conversion preserves branches, nulls, text and geometry");
        Assert(number.Value == 1234.56789 && integer.Value == 2000 && source.Branches[0][0] == number,
            "Unit conversion leaves upstream numeric values untouched");
        Assert(ReferenceEquals(source, method.Invoke(null, new object[] { source, PanelUnitConversion.None })),
            "Disabled conversion preserves original types and tree identity");

        var unset = new GH_Number(Rhino.RhinoMath.UnsetValue);
        var invalidSource = new GH_Structure<IGH_Goo>();
        invalidSource.AppendRange(new IGH_Goo[] { unset, new GH_Number(10) }, new GH_Path(0));
        Assert(!unset.IsValid && !double.IsNaN(unset.Value) && !double.IsInfinity(unset.Value),
            "Native unset numbers are invalid despite carrying a finite double");
        foreach (PanelUnitConversion conversion in Enum.GetValues(typeof(PanelUnitConversion)))
        {
            var result = (GH_Structure<IGH_Goo>)method.Invoke(null, new object[] { invalidSource, conversion })!;
            Assert(ReferenceEquals(result.Branches[0][0], unset) && !result.Branches[0][0].IsValid &&
                unset.Value == Rhino.RhinoMath.UnsetValue,
                "Every unit mode preserves invalid numeric goo without turning unset into a valid scalar");
            Assert(((GH_Number)result.Branches[0][1]).Value == PanelUnits.Convert(10, conversion),
                "Valid numeric values still convert alongside invalid items");
        }

        var convertedInvalid = (GH_Structure<IGH_Goo>)method.Invoke(null,
            new object[] { invalidSource, PanelUnitConversion.MillimetersToMeters })!;
        var capture = assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!
            .GetMethod("Capture", BindingFlags.Public | BindingFlags.Static)!;
        var snapshot = (PanelSnapshot)capture.Invoke(null,
            new object?[] { convertedInvalid, null, PanelUnitConversion.MillimetersToMeters, "mm" })!;
        var query = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        Assert(query.Statistics.InvalidCount == 1 && query.Statistics.NumericCount == 1 &&
            query.Statistics.Minimum == 0.01 && query.Statistics.Maximum == 0.01 && query.Statistics.Mean == 0.01,
            "Converted native unset values remain invalid and never distort numeric statistics");
        Assert(PanelQueryEngine.Execute(snapshot,
            new PanelQueryOptions { SearchMode = PanelSearchMode.Value, Search = "< 0" }).RowPositions.Count == 0 &&
            PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { OnlyInvalid = true }).RowPositions.SequenceEqual(new[] { 0 }),
            "Converted unset values remain available to invalid-only inspection while numeric filters exclude them");
    }

    private static async Task VerifyCanvasUnits(GH_Component component, GH_Canvas canvas,
        Assembly assembly, object session, IGH_Structure input)
    {
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Units));
        var menu = PanelTestAdapter.MenuBounds(component);
        Assert(menu.Bottom < PanelTestAdapter.Bounds(component, CanvasControl.Units).Top,
            "Compact footer unit menu opens upward inside the panel");
        ClickMenuRow(component, canvas, 1);
        await WaitForQuery(session);
        var snapshot = Get<PanelSnapshot>(session, "Snapshot");
        Assert(PanelTestAdapter.Conversion(component) == PanelUnitConversion.MillimetersToMeters &&
            snapshot.UnitConversion == PanelUnitConversion.MillimetersToMeters && snapshot.Rows[0].NumericValue == 0.01,
            "Footer unit choice converts real output and snapshot values");
        var output = component.Params.Output[0].VolatileData;
        Assert(((GH_Number)output.get_Branch(output.Paths[0])[0]!).Value == 0.01 &&
            input.get_Branch(input.Paths[0])[0]!.ToString() == "10", "Canvas conversion does not mutate upstream data");
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Units));
        ClickMenuRow(component, canvas, PanelUnits.ConversionCount + 1);
        CanvasEditorAdapter.Active(assembly).Submit("0");
        Assert(PanelTestAdapter.Decimals(component) == 0 && PanelTestAdapter.CopyValue(component, snapshot, 0) == "0" &&
            ReferenceEquals(snapshot, Get<PanelSnapshot>(session, "Snapshot")) &&
            ((GH_Number)output.get_Branch(output.Paths[0])[0]!).Value == 0.01,
            "Decimal formatting and copying preserve full output precision without another solution");
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Units));
        ClickMenuRow(component, canvas, PanelUnits.ConversionCount + 1);
        CanvasEditorAdapter.Active(assembly).Submit("13");
        Assert(PanelTestAdapter.Decimals(component) == 0, "Invalid decimal counts retain the previous setting");
        PanelTestAdapter.Units(component, PanelUnitConversion.MillimetersToMeters, 2);
        var archive = new GH_Archive();
        var restored = (GH_Component)Activator.CreateInstance(component.GetType())!;
        restored.CreateAttributes();
        Assert(archive.AppendObject(component, "Units") && archive.ExtractObject(restored, "Units") &&
            PanelTestAdapter.Conversion(restored) == PanelUnitConversion.MillimetersToMeters && PanelTestAdapter.Decimals(restored) == 2,
            "Unit conversion and decimal settings survive save/load");
        var chunk = new GH_LooseChunk("Units");
        restored.Write(chunk);
        chunk.RemoveItem("PanelUnitConversion"); chunk.RemoveItem("PanelDecimalPlaces");
        Assert(restored.Read(chunk) && PanelTestAdapter.Conversion(restored) == PanelUnitConversion.None && PanelTestAdapter.Decimals(restored) == -1,
            "Older definitions default to no conversion and automatic decimals");
        restored.RemovedFromDocument(component.OnPingDocument());
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Units));
        ClickMenuRow(component, canvas, PanelUnits.ConversionCount);
        Assert(PanelTestAdapter.Decimals(component) == -1, "Unit menu restores automatic decimal formatting");
        PanelTestAdapter.Units(component, PanelUnitConversion.None, -1);
        await WaitForQuery(session);
        AssertEqualTrees(input, component.Params.Output[0].VolatileData);
        bool rejectedStaleCopy = false;
        try { PanelTestAdapter.CopyValue(component, snapshot, 0); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { rejectedStaleCopy = true; }
        Assert(rejectedStaleCopy, "Obsolete snapshots cannot copy values from a newer solution");
    }

    private static async Task VerifyUndoRedo(GH_Component component, GH_Document document,
        object session, IGH_Structure originalInput)
    {
        var sourceId = component.Params.Input[0].Sources.Single().InstanceGuid;
        document.UndoServer.Clear();
        PanelTestAdapter.Units(component, PanelUnitConversion.MillimetersToMeters, 2);
        await WaitForQuery(session);
        PanelTestAdapter.Units(component, PanelUnitConversion.MillimetersToMeters, 0);
        document.UndoServer.PerformUndo();
        await WaitForQuery(session);
        Assert(PanelTestAdapter.Decimals(component) == 2 && Get<PanelSnapshot>(session, "Snapshot").Rows[0].NumericValue == 0.01,
            "Native undo restores decimals while retaining full-precision converted output");
        document.UndoServer.PerformRedo();
        await WaitForQuery(session);
        Assert(PanelTestAdapter.Decimals(component) == 0, "Native redo restores decimal formatting");
        document.UndoServer.PerformUndo();
        document.UndoServer.PerformUndo();
        await WaitForQuery(session);
        Assert(PanelTestAdapter.Conversion(component) == PanelUnitConversion.None && PanelTestAdapter.Decimals(component) == -1,
            "Native undo restores disabled conversion and automatic decimals");
        AssertEqualTrees(originalInput, component.Params.Output[0].VolatileData);
        document.UndoServer.PerformRedo();
        await WaitForQuery(session);
        Assert(PanelTestAdapter.Conversion(component) == PanelUnitConversion.MillimetersToMeters &&
            Get<PanelSnapshot>(session, "Snapshot").Rows[0].NumericValue == 0.01, "Native redo recomputes converted output");
        PanelTestAdapter.Units(component, PanelUnitConversion.None, -1);
        await WaitForQuery(session);
        document.UndoServer.Clear();
        UpdateQuery(session, new PanelQueryOptions { BranchPath = "{0;2}" });
        PanelTestAdapter.TreeTools(component, 1, true, true, true);
        await WaitForQuery(session);
        document.UndoServer.PerformUndo();
        await WaitForQuery(session);
        Assert(PanelTestAdapter.TrimDepth(component) == 0 && !PanelTestAdapter.TreeFlag(component, "Flatten") &&
            !PanelTestAdapter.TreeFlag(component, "Reverse") && !PanelTestAdapter.TreeFlag(component, "Simplify") &&
            Get<PanelQueryOptions>(session, "Options").BranchPath == "{0;2}", "Native undo restores all tree settings and branch selection");
        var branchOutput = new GH_Structure<IGH_Goo>();
        branchOutput.AppendRange(originalInput.get_Branch(new GH_Path(0, 2)).Cast<IGH_Goo>(), new GH_Path(0, 2));
        AssertEqualTrees(branchOutput, component.Params.Output[0].VolatileData);
        document.UndoServer.PerformRedo();
        await WaitForQuery(session);
        Assert(PanelTestAdapter.TrimDepth(component) == 1 && PanelTestAdapter.TreeFlag(component, "Flatten") &&
            PanelTestAdapter.TreeFlag(component, "Reverse") && PanelTestAdapter.TreeFlag(component, "Simplify") &&
            component.Params.Output[0].VolatileData.PathCount == 1 && Get<PanelQueryOptions>(session, "Options").BranchPath == null,
            "Native redo restores tree tools, cleared branch selection and transformed output");
        Assert(component.Params.Input[0].Sources.Single().InstanceGuid == sourceId, "Undo and redo retain the upstream wire");
        AssertEqualTrees(originalInput, component.Params.Input[0].VolatileData);
        PanelTestAdapter.TreeTools(component, 0, false, false, false);
        session.GetType().GetMethod("ResetView")!.Invoke(session, Array.Empty<object>());
        await WaitForQuery(session);
    }

    private static T Get<T>(object value, string property) => (T)value.GetType().GetProperty(property)!.GetValue(value)!;
    private static void UpdateQuery(object session, PanelQueryOptions options) => session.GetType().GetMethod("UpdateQuery")!.Invoke(session, new object[] { options });

    private static GH_CanvasMouseEvent Mouse(PointF point) => new GH_CanvasMouseEvent(Point.Empty, point, System.Windows.Forms.MouseButtons.Left, 1, 0);
    private static void Click(GH_Component component, GH_Canvas canvas, RectangleF region) => Assert(component.Attributes.RespondToMouseDown(canvas, Mouse(new PointF(region.X + region.Width / 2, region.Y + region.Height / 2))) == GH_ObjectResponse.Handled, "Canvas control responds to native mouse events");
    private static void ClickMenuRow(GH_Component component, GH_Canvas canvas, int index)
    {
        var state = component.Attributes.GetType().GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component.Attributes)!;
        int Value(string name) => (int)state.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
        int count = Value("Count");
        if (index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index));
        for (int attempts = 0; attempts <= count; attempts++)
        {
            // Page choices are actionable only after that page has been painted.
            using (var bitmap = new Bitmap(canvas.Width, canvas.Height))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                canvas.Viewport.ApplyProjection(graphics);
                PanelTestAdapter.Render(component, canvas, graphics);
            }
            var menu = PanelTestAdapter.MenuBounds(component);
            int offset = Value("Offset"), visible = Value("Visible");
            if (index >= offset && index < offset + visible)
            {
                component.Attributes.RespondToMouseDown(canvas, Mouse(new PointF(menu.X + 10, menu.Y + (index - offset) * 20 + 10)));
                return;
            }
            float x = index < offset ? menu.X + 10 : menu.Right - 10;
            component.Attributes.RespondToMouseDown(canvas, Mouse(new PointF(x, menu.Bottom - 10)));
            if (Value("Offset") == offset) break;
        }
        throw new InvalidOperationException("Could not reach the requested painted menu row.");
    }
    private static void DrawCanvasPreview(GH_Component component, GH_Canvas canvas, int dpi = 96, string? artifactName = null)
    {
        var bounds = component.Attributes.Bounds;
        const int scale = 2, marginX = 90, marginY = 25;
        using var bitmap = new Bitmap((int)(bounds.Width + marginX * 2) * scale, (int)(bounds.Height + marginY * 2) * scale);
        bitmap.SetResolution(dpi, dpi);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(239, 240, 237));
        using (var grid = new Pen(Color.FromArgb(221, 224, 217)))
        {
            for (int x = 0; x < bitmap.Width; x += 40) graphics.DrawLine(grid, x, 0, x, bitmap.Height);
            for (int y = 0; y < bitmap.Height; y += 40) graphics.DrawLine(grid, 0, y, bitmap.Width, y);
        }
        graphics.ScaleTransform(scale, scale);
        graphics.TranslateTransform(marginX - bounds.X, marginY - bounds.Y);
        using (var wire = new Pen(Color.FromArgb(144, 151, 145), 1.2f))
        {
            var input = component.Params.Input[0].Attributes.InputGrip;
            var output = component.Params.Output[0].Attributes.OutputGrip;
            graphics.DrawBezier(wire, new PointF(input.X - 100, input.Y - 20), new PointF(input.X - 55, input.Y - 20), new PointF(input.X - 45, input.Y), input);
            graphics.DrawBezier(wire, output, new PointF(output.X + 45, output.Y), new PointF(output.X + 55, output.Y + 20), new PointF(output.X + 100, output.Y + 20));
        }
        PanelTestAdapter.Render(component, canvas, graphics);
        string artifact = Path.Combine(Path.GetDirectoryName(_plugin)!, artifactName ?? (dpi == 96 ? "rhino-smoke-canvas.png" : "rhino-smoke-canvas-dpi192.png"));
        bitmap.Save(artifact);
        Console.WriteLine("Actual canvas Objects renderer preview (sample data and illustrative grid/wires): " + artifact);
    }
    private static void AssertCanvasDpiInvariant()
    {
        using var normal = new Bitmap(Path.Combine(Path.GetDirectoryName(_plugin)!, "rhino-smoke-canvas.png"));
        using var high = new Bitmap(Path.Combine(Path.GetDirectoryName(_plugin)!, "rhino-smoke-canvas-dpi192.png"));
        var rectangle = new Rectangle(0, 0, normal.Width, normal.Height);
        var first = normal.LockBits(rectangle, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var second = high.LockBits(rectangle, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var left = new byte[first.Stride * first.Height];
            var right = new byte[second.Stride * second.Height];
            Marshal.Copy(first.Scan0, left, 0, left.Length);
            Marshal.Copy(second.Scan0, right, 0, right.Length);
            Assert(left.SequenceEqual(right), "Canvas pixel geometry and text render identically at 96 and 192 DPI");
        }
        finally { normal.UnlockBits(first); high.UnlockBits(second); }
    }

    private static async Task WaitForQuery(object session)
    {
        var timeout = Stopwatch.StartNew();
        while (Get<bool>(session, "IsBusy"))
        {
            if (timeout.Elapsed.TotalSeconds > 15) throw new TimeoutException("Background query did not reach the Rhino UI thread.");
            await Task.Delay(10);
        }
        Assert(session.GetType().GetProperty("Error")!.GetValue(session) == null, "Query completed without runtime errors");
    }

    private static void AssertEqualTrees(IGH_Structure input, IGH_Structure output)
    {
        Assert(input.PathCount == output.PathCount && input.DataCount == output.DataCount, "Output preserves tree shape and item count");
        for (int branch = 0; branch < input.PathCount; branch++)
        {
            Assert(input.Paths[branch].ToString() == output.Paths[branch].ToString(), "Output preserves original path " + input.Paths[branch]);
            var left = input.get_Branch(input.Paths[branch]);
            var right = output.get_Branch(output.Paths[branch]);
            Assert(left.Count == right.Count, "Output preserves branch length");
            for (int i = 0; i < left.Count; i++)
                Assert(left[i]?.GetType() == right[i]?.GetType() && left[i]?.ToString() == right[i]?.ToString(), "Output preserves original typed value at " + input.Paths[branch] + " [" + i + "]");
        }
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
            phase = _phase, failure = _failure, canvasDrawingVerified = _canvasDrawingVerified,
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
