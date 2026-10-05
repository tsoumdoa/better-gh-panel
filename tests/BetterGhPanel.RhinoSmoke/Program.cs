using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string path);
    private delegate bool WindowCallback(IntPtr handle, IntPtr argument);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr argument);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, WindowCallback callback, IntPtr argument);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr handle, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, System.Text.StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] != "--run")
        {
            Console.WriteLine("Windows Rhino 8 host smoke check (requires a working Rhino license).\nBuild the plugin first, then run with --run [absolute BetterGhPanel.gha path].\nCreates a hidden RhinoCore, loads the workspace assembly directly, and never installs a plugin.");
            return 0;
        }
        _system = Environment.GetEnvironmentVariable("RHINO_SYSTEM_PATH") ?? "C:/Program Files/Rhino 8/System";
        _grasshopper = Path.GetFullPath(Path.Combine(_system, "../Plug-ins/Grasshopper"));
        _plugin = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.GetFullPath("src/BetterGhPanel/bin/Release/net7.0/BetterGhPanel.gha");
        _resultPath = Environment.GetEnvironmentVariable("PANEL_SMOKE_RESULT") ?? Path.Combine(Path.GetDirectoryName(_plugin)!, "rhino-smoke-result.json");
        if (!File.Exists(_plugin) || !File.Exists(Path.Combine(_system, "RhinoCommon.dll")))
            throw new FileNotFoundException("Build the plugin and install Rhino 8 before running this host check.");
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        Environment.SetEnvironmentVariable("PATH", _system + ";" + Environment.GetEnvironmentVariable("PATH"));
        SetDllDirectory(_system);
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
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
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
            try { exitCode = await ExecuteSmoke(host); }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally
            {
                File.WriteAllText(_resultPath, "{\"passed\":" + (exitCode == 0 ? "true" : "false") + ",\"canvasDrawingVerified\":" + (_canvasDrawingVerified ? "true" : "false") + ",\"runtime\":\"Windows .NET 7 / Rhino 8.35\"}");
                Console.Out.Flush(); Console.Error.Flush(); context.ExitThread();
            }
        };
        timer.Start();
        System.Windows.Forms.Application.Run(context);
        return exitCode;
    }

    private static async Task<int> ExecuteSmoke(RhinoCore host)
    {
        _ = Task.Run(() =>
        {
            Console.WriteLine("Worker queues native UI invoke.");
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() => Console.WriteLine("Native UI invoke delivered.")));
            Console.WriteLine("Worker native UI invoke returned.");
        });
        var assembly = Assembly.LoadFrom(_plugin);
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
        var inspector = (Eto.Forms.Form)Activator.CreateInstance(assembly.GetType("BetterGhPanel.UI.PanelInspector", true)!, session)!;
        Console.WriteLine("Eto inspector constructed: " + inspector.GetType().Name);
        inspector.Location = new Eto.Drawing.Point(-1800, -1100);
        inspector.Show();
        var input = component.Params.Input[0].VolatileData;
        var output = component.Params.Output[0].VolatileData;
        Assert(input.DataCount == 5, "Native Grasshopper solver collected all source items");
        AssertEqualTrees(input, output);
        await WaitForQuery(host, session);
        var snapshot = Get<PanelSnapshot>(session, "Snapshot");
        Assert(snapshot.Rows.Count == input.DataCount, "Snapshot preserves input item positions");
        Assert(snapshot.Branches.Count == input.PathCount, "Snapshot preserves input branch paths");

        var query = new PanelQueryOptions { Search = "> 42", SortMode = PanelSortMode.ValueDescending };
        document.IsModified = false;
        UpdateQuery(session, query);
        Assert(document.IsModified, "Changed query settings mark the Grasshopper document modified");
        await WaitForQuery(host, session);
        var result = Get<PanelQueryResult>(session, "Result");
        Assert(result.RowIds.Count == 1 && result.Snapshot.Rows[result.RowIds[0]].NumericValue == 100,
            "Native query filters and sorts without mutating source");
        AssertEqualTrees(input, component.Params.Output[0].VolatileData);
        document.IsModified = false;
        UpdateQuery(session, query.Clone());
        await WaitForQuery(host, session);
        Assert(!document.IsModified, "Unchanged query and worker notifications do not mark the document modified");
        Assert(ReferenceEquals(result, Get<PanelQueryResult>(session, "Result")), "Equal query submissions reuse the published result");
        session.GetType().GetMethod("AttachSnapshot")!.Invoke(session, new object[] { snapshot });
        await WaitForQuery(host, session);
        Assert(!document.IsModified, "New snapshot notifications do not mark view settings modified");

        for (int i = 0; i < 100; i++) UpdateQuery(session, new PanelQueryOptions { Search = "> " + i });
        UpdateQuery(session, new PanelQueryOptions { Search = "alpha", BranchPath = "{0;2}", SortMode = PanelSortMode.ValueDescending });
        await WaitForQuery(host, session);
        result = Get<PanelQueryResult>(session, "Result");
        Assert(result.RowIds.Count == 1 && result.Snapshot.Rows[result.RowIds[0]].TypeName == "Text",
            "Rapid cancellation publishes the latest query only");
        session.GetType().GetMethod("ResetView")!.Invoke(session, Array.Empty<object>());
        await WaitForQuery(host, session);
        var search = PanelTestAdapter.Search(inspector);
        Assert(search.Text == string.Empty, "External reset synchronizes inspector search controls");
        search.Text = "Unsubmitted draft";
        session.GetType().GetMethod("ResetView")!.Invoke(session, Array.Empty<object>());
        await WaitForQuery(host, session);
        Assert(search.Text == string.Empty, "Reset clears inspector drafts even when query options already equal defaults");

        var independentRows = snapshot.Rows.Select(row => new PanelRow(row.Id + 1000, row.BranchIndex,
            row.OriginalIndex, row.Path, row.TypeName, "cached summary", row.NumericValue, row.IsNull, row.IsValid)).ToArray();
        var independentSnapshot = new PanelSnapshot(50, independentRows, snapshot.Branches);
        session.GetType().GetMethod("AttachSnapshot")!.Invoke(session, new object[] { independentSnapshot });
        await WaitForQuery(host, session);
        PanelTestAdapter.SelectRow(inspector, 0);
        Assert(PanelTestAdapter.Detail(inspector) == "10", "Full-value lookup uses source positions even when row IDs differ");
        UpdateQuery(session, new PanelQueryOptions { Search = "> 42" });
        await WaitForQuery(host, session);
        Assert(PanelTestAdapter.Detail(inspector) == string.Empty, "Filtering out the selected row clears the detail and selection");
        session.GetType().GetMethod("AttachSnapshot")!.Invoke(session, new object[] { snapshot });
        var staleValue = (string)session.GetType().GetMethod("GetDisplayValue")!.Invoke(session, new object[] { independentSnapshot, 0 })!;
        Assert(staleValue == "cached summary", "Obsolete snapshots never resolve full values from a newer solution");
        session.GetType().GetMethod("ResetView")!.Invoke(session, Array.Empty<object>());
        await WaitForQuery(host, session);
        await Task.Delay(100);
        var native = (System.Windows.Window)inspector.ControlObject;
        native.UpdateLayout();
        var renderTarget = new System.Windows.Media.Imaging.RenderTargetBitmap((int)native.ActualWidth, (int)native.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        renderTarget.Render(native);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(renderTarget));
        string inspectorArtifact = Path.Combine(Path.GetDirectoryName(_plugin)!, "rhino-smoke-inspector.png");
        using (var stream = File.Create(inspectorArtifact)) encoder.Save(stream);
        Console.WriteLine("Native Eto inspector artifact: " + inspectorArtifact);
        inspector.Close();
        inspector.Dispose();
        UpdateQuery(session, new PanelQueryOptions { Search = "alpha", BranchPath = "{0;2}", SortMode = PanelSortMode.ValueDescending });
        await WaitForQuery(host, session);

        PanelTestAdapter.Resize(component, 500, 420);
        var archive = new GH_Archive();
        Assert(archive.AppendObject(component, "Panel"), "Component settings serialize");
        var restored = (GH_Component)Activator.CreateInstance(componentType)!;
        restored.CreateAttributes();
        Assert(archive.ExtractObject(restored, "Panel"), "Component settings deserialize");
        object restoredSession = PanelTestAdapter.Session(restored);
        var restoredOptions = Get<PanelQueryOptions>(restoredSession, "Options");
        Assert(restoredOptions.Search == "alpha" && restoredOptions.BranchPath == "{0;2}" &&
            restoredOptions.SortMode == PanelSortMode.ValueDescending, "Search, branch and sort survive serialization");
        Assert(PanelTestAdapter.Size(restored).Width == 500 && PanelTestAdapter.Size(restored).Height == 420,
            "Canvas width and height survive serialization");
        Assert(PanelTestAdapter.Preset(restored) == -1, "Custom dimensions do not retain an unrelated preset check mark");
        var legacy = new GH_LooseChunk("Panel");
        Assert(restored.Write(legacy), "Legacy fixture serializes the component");
        legacy.RemoveItem("PanelWidth");
        legacy.RemoveItem("PanelHeight");
        legacy.RemoveItem("PanelSize");
        legacy.SetInt32("PanelSize", 2);
        Assert(restored.Read(legacy), "Legacy preset-only settings deserialize");
        Assert(PanelTestAdapter.Size(restored).Width == 440 && PanelTestAdapter.Size(restored).Height == 400 &&
            PanelTestAdapter.Preset(restored) == 2, "Legacy size presets supply dimensions when explicit dimensions are absent");
        restored.RemovedFromDocument(document);

        component.DocumentContextChanged(document, GH_DocumentContext.Unloaded);
        component.DocumentContextChanged(document, GH_DocumentContext.Loaded);
        object reloadedSession = PanelTestAdapter.Session(component);
        await WaitForQuery(host, reloadedSession);
        Assert(Get<PanelSnapshot>(reloadedSession, "Snapshot").Rows.Count == input.DataCount,
            "Switching document tabs retains the cached tree without recomputing");

        component.Attributes.Pivot = new PointF(80, 50);
        component.Attributes.ExpireLayout();
        component.Attributes.PerformLayout();
        using var canvas = new GH_Canvas { Size = new Size(720, 520) };
        var sampleRows = Enumerable.Range(0, 10000).Select(i => new PanelRow(i, 0, i, "{0;0}", "Number", i.ToString(), i)).ToArray();
        session.GetType().GetMethod("AttachSnapshot")!.Invoke(session, new object[] { new PanelSnapshot(100, sampleRows, new[] { new PanelBranch(0, "{0;0}", 10000) }) });
        session.GetType().GetMethod("ResetView")!.Invoke(session, Array.Empty<object>());
        await WaitForQuery(host, session);
        PanelTestAdapter.Resize(component, 360, 280);
        component.Attributes.PerformLayout();
        DrawCanvasPreview(component, canvas);
        _canvasDrawingVerified = true;
        Assert(component.Params.Input[0].Attributes.InputGrip.X == PanelTestAdapter.Bounds(component, CanvasControl.Panel).Left &&
            component.Params.Output[0].Attributes.OutputGrip.X == PanelTestAdapter.Bounds(component, CanvasControl.Panel).Right,
            "Native wire grips sit on the panel edges");

        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Type));
        ClickMenuRow(component, canvas, 2);
        await WaitForQuery(host, session);
        Assert(Get<PanelQueryOptions>(session, "Options").TypeName == "Number", "Canvas type menu updates the query");
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Sort));
        ClickMenuRow(component, canvas, 2);
        await WaitForQuery(host, session);
        result = Get<PanelQueryResult>(session, "Result");
        Assert(result.Snapshot.Rows[result.RowIds[0]].NumericValue == 9999, "Canvas sort menu orders values descending");

        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Search));
        var editor = CanvasEditorAdapter.Active(assembly);
        editor.Submit("> 42");
        await WaitForQuery(host, session);
        Assert(Get<PanelQueryOptions>(session, "Options").Search == "> 42" && Get<PanelQueryResult>(session, "Result").RowIds.Count == 9957,
            "Host canvas textbox accepts inline search and filters the view");
        long revision = Get<long>(session, "QueryRevision");
        editor.AcceptAgain("second commit");
        Assert(Get<long>(session, "QueryRevision") == revision, "Inline textbox commits its callback once");
        Click(component, canvas, PanelTestAdapter.Bounds(component, CanvasControl.Search));
        editor = CanvasEditorAdapter.Active(assembly);
        component.DocumentContextChanged(document, GH_DocumentContext.Unloaded);
        editor.AcceptAgain("canceled commit");
        Assert(Get<long>(session, "QueryRevision") == revision, "Document switching cancels inline text without committing");
        component.DocumentContextChanged(document, GH_DocumentContext.Loaded);
        session.GetType().GetMethod("ResetView")!.Invoke(session, Array.Empty<object>());
        await WaitForQuery(host, session);
        DrawCanvasPreview(component, canvas);

        var resize = PanelTestAdapter.Bounds(component, CanvasControl.Resize);
        var start = new PointF(resize.X + 5, resize.Y + 5);
        Assert(component.Attributes.RespondToMouseDown(canvas, Mouse(start)) == GH_ObjectResponse.Capture, "Canvas resize captures the mouse");
        component.Attributes.RespondToMouseMove(canvas, Mouse(new PointF(start.X + 140, start.Y + 80)));
        Assert(PanelTestAdapter.Size(component).Width == 500 && PanelTestAdapter.Size(component).Height == 360,
            "Dragging the resize corner updates panel dimensions");
        Assert(component.Params.Output[0].Attributes.OutputGrip.X == PanelTestAdapter.Bounds(component, CanvasControl.Panel).Right,
            "Native output grip follows resized panel bounds");
        Assert(component.Attributes.RespondToMouseUp(canvas, Mouse(start)) == GH_ObjectResponse.Release, "Canvas resize releases the mouse");
        PanelTestAdapter.Resize(component, 360, 280);
        component.Attributes.PerformLayout();
        DrawCanvasPreview(component, canvas);
        DrawCanvasPreview(component, canvas, 192);
        AssertCanvasDpiInvariant();
        component.DocumentContextChanged(document, GH_DocumentContext.Close);
        Assert(!PanelTestAdapter.HasSession(component),
            "Document closure disposes the component session");
        Console.WriteLine("PASS: Windows .NET Rhino host smoke checks completed.");
        return 0;
    }

    private static T Get<T>(object value, string property) => (T)value.GetType().GetProperty(property)!.GetValue(value)!;
    private static void UpdateQuery(object session, PanelQueryOptions options) => session.GetType().GetMethod("UpdateQuery")!.Invoke(session, new object[] { options });

    private static GH_CanvasMouseEvent Mouse(PointF point) => new GH_CanvasMouseEvent(Point.Empty, point, System.Windows.Forms.MouseButtons.Left, 1, 0);
    private static void Click(GH_Component component, GH_Canvas canvas, RectangleF region) => Assert(component.Attributes.RespondToMouseDown(canvas, Mouse(new PointF(region.X + region.Width / 2, region.Y + region.Height / 2))) == GH_ObjectResponse.Handled, "Canvas control responds to native mouse events");
    private static void ClickMenuRow(GH_Component component, GH_Canvas canvas, int index)
    {
        var menu = PanelTestAdapter.MenuBounds(component);
        component.Attributes.RespondToMouseDown(canvas, Mouse(new PointF(menu.X + 10, menu.Y + index * 20 + 10)));
    }
    private static void DrawCanvasPreview(GH_Component component, GH_Canvas canvas, int dpi = 96)
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
        component.Attributes.GetType().GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component.Attributes, new object[] { canvas, graphics, GH_CanvasChannel.Objects });
        string artifact = Path.Combine(Path.GetDirectoryName(_plugin)!, dpi == 96 ? "rhino-smoke-canvas.png" : "rhino-smoke-canvas-dpi192.png");
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

    private static async Task WaitForQuery(RhinoCore host, object session)
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
            Assert(input.Paths[branch].Equals(output.Paths[branch]), "Output preserves original path " + input.Paths[branch]);
            var left = input.get_Branch(input.Paths[branch]);
            var right = output.get_Branch(output.Paths[branch]);
            Assert(left.Count == right.Count, "Output preserves branch length");
            for (int i = 0; i < left.Count; i++)
                Assert(left[i]?.GetType() == right[i]?.GetType() && left[i]?.ToString() == right[i]?.ToString(), "Output preserves original typed value at " + input.Paths[branch] + " [" + i + "]");
        }
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
