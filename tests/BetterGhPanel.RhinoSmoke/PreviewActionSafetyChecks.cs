using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.FileIO;
using Rhino.Geometry;
using Forms = System.Windows.Forms;


internal static partial class PreviewSafetyChecks
{
    private static void VerifyActions(Assembly assembly)
    {
        var prior = Forms.Clipboard.GetDataObject();
        try
        {
            var cycle = new GH_GeometryGroup(); cycle.Objects.Add(cycle);
            object framed = Frame(assembly, new IGH_Goo?[] { new GH_Point(new Point3d(1, 2, 3)), cycle });
            Check(Property<bool>(framed, "Success") && Property<int>(framed, "Skipped") == 1,
                "frame reports a partial action when a safe point accompanies an unsafe cycle");
            var standalone = new StandalonePointPreview();
            Check(Bounds(assembly, standalone).Contains(new Point3d(7, 0, 0)) &&
                Property<bool>(Frame(assembly, new IGH_Goo?[] { standalone }), "Success") && standalone.BoundsCalls > 0,
                "standalone custom point preview and frame retain their best-effort behavior");
            var unsafeGroup = new GH_GeometryGroup(); unsafeGroup.Objects.Add(standalone);
            standalone.BoundsCalls = 0;
            Check(!Bounds(assembly, unsafeGroup).IsValid && standalone.BoundsCalls == 0,
                "recursive group inspection never dispatches a custom preview child");
            var wrappedStandalone = new GH_GeometricGooWrapper(standalone);
            Check(!Bounds(assembly, wrappedStandalone).IsValid && standalone.BoundsCalls == 0,
                "a wrapper cannot turn its custom child into standalone preview dispatch");
            framed = Frame(assembly, Enumerable.Range(0, 2200).Select(i => (IGH_Goo?)new GH_Point(new Point3d(i, 0, 0))));
            Check(Property<bool>(framed, "Success") && Property<bool>(framed, "Exhausted"), "frame reports action-wide exhaustion even after success");
            var toCopy = Enumerable.Range(0, 2200).Select(i => (IGH_Goo?)new GH_Point(new Point3d(i, 0, 0)));
            object limited = Copy(assembly, toCopy);
            Check(Property<bool>(limited, "Success") && Property<bool>(limited, "Exhausted"), "copy reports action-wide exhaustion even after success");

            using var withDictionary = new LineCurve(Point3d.Origin, new Point3d(3, 0, 0));
            var dictionaryHolder = new GH_Curve(withDictionary);
            dictionaryHolder.Value.UserDictionary.Set("panel-test", "preserve");
            Check(dictionaryHolder.Value.HasUserData, "copy fixture has real source metadata");
            object highlight = Activator.CreateInstance(assembly.GetType("BetterGhPanel.Runtime.PanelGeometryHighlight", true)!, true)!;
            Set(highlight, new IGH_Goo?[] { dictionaryHolder }, new[] { 0 }, new[] { 0 });
            Check(Count(highlight) == 1 && dictionaryHolder.Value.HasUserData,
                "value-only line preview preserves the source SDK dictionary");
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            object metadataResult = Copy(assembly, new IGH_Goo?[] { new GH_Point(new Point3d(5, 0, 0)), dictionaryHolder });
            Check(Property<bool>(metadataResult, "Success") && Property<int>(metadataResult, "Count") == 2 &&
                Property<int>(metadataResult, "Skipped") == 0 && dictionaryHolder.Value.UserDictionary.GetString("panel-test") == "preserve",
                "geometry copy admits a bounded SDK dictionary without changing the source");
            using var archive = ClipboardArchive();
            var copiedLine = archive.Objects.Select(item => item.Geometry).OfType<LineCurve>().Single();
            Check(copiedLine.UserDictionary.GetString("panel-test") == "preserve",
                "the native clipboard archive preserves the SDK dictionary value");
        }
        finally { if (prior == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(prior, true); }
    }

    private static void VerifyResources(object highlight)
    {
        using var poly = new PolylineCurve(new[] { Point3d.Origin, new Point3d(1, 0, 0), new Point3d(1, 1, 0) });
        using var mesh = Mesh.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(1, 1, 1)), 1, 1, 1);
        var meshHolder = new GH_Mesh(mesh);
        meshHolder.Value.VertexColors.CreateMonotoneMesh(Color.Aqua);
        var source = new IGH_Goo?[] { new GH_Curve(poly), meshHolder, new GH_Box(new Box(new BoundingBox(Point3d.Origin, new Point3d(2, 2, 2)))) };
        Set(highlight, source, new[] { 0, 1, 2 }, new[] { 0 });
        var entries = (IEnumerable)Field<object>(highlight, "_entries");
        var resources = new List<GeometryBase>();
        foreach (object pair in entries)
        {
            object entry = pair.GetType().GetProperty("Value")!.GetValue(pair)!;
            foreach (object leaf in (IEnumerable)Field<object>(entry, "Leaves"))
            {
                if (Field<object?>(leaf, "_owned") is GeometryBase owned) resources.Add(owned);
                if (Field<object?>(leaf, "_boxMesh") is GeometryBase boxMesh) resources.Add(boxMesh);
            }
        }
        Check(resources.Count == 3 && resources.All(value => !value.Disposed), "bounded curve, mesh and box resources are owned during preview");
        var copiedMesh = resources.OfType<Mesh>().Single(value => value.VertexColors.Count > 0);
        Check(copiedMesh.VertexColors.Count == meshHolder.Value.VertexColors.Count &&
            copiedMesh.VertexColors[0].ToArgb() == meshHolder.Value.VertexColors[0].ToArgb(),
            "owned mesh preview preserves the source vertex colors");
        Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
        Check(resources.All(value => value.Disposed) && Property<long>(highlight, "RetainedBytes") == 0 && !Box(highlight).IsValid,
            "clearing a generation disposes each native resource and its bounds");
    }

    private static async Task VerifyDocumentCacheAndDrawing(Assembly assembly, RhinoDoc rhino)
    {
        var previous = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Preview cache fixture", Rhino.Display.DefinedViewportProjection.Top, new Rectangle(0, 0, 640, 480), false);
        using var document = new GH_Document { Enabled = true };
        var priorClipboard = Forms.Clipboard.GetDataObject();
        try
        {
            rhino.Views.ActiveView = view;
            view.ActiveViewport.ConstructionGridVisible = view.ActiveViewport.ConstructionAxesVisible = view.ActiveViewport.WorldAxesVisible = false;
            document.Properties.KeepOpen = true; document.PreviewColourSelected = Color.FromArgb(255, 0, 230, 40);
            GH_Document.EnableSolutions = true;
            var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
            component.CreateAttributes();
            var source = new Param_GenericObject { Hidden = true };
            source.CreateAttributes();
            source.PersistentData.Append(new GH_Point(new Point3d(-20, 0, 0)), new GH_Path(0));
            source.PersistentData.Append(new GH_Line(new Line(new Point3d(20, -5, 0), new Point3d(20, 5, 0))), new GH_Path(0));
            source.PersistentData.Append(new GH_Number(42), new GH_Path(0));
            document.AddObject(source, false); document.AddObject(component, false);
            component.Params.Input[0].AddSource(source);
            document.NewSolution(false, GH_SolutionMode.Silent);
            object session = PanelTestAdapter.Session(component);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!(bool)session.GetType().GetProperty("HasCurrentResult")!.GetValue(session)!)
            { if (DateTime.UtcNow > deadline) throw new TimeoutException("Preview fixture query did not complete"); await Task.Delay(25); }
            var snapshot = (PanelSnapshot)session.GetType().GetProperty("Snapshot")!.GetValue(session)!;
            var beforeActions = component.Params.Output[0].VolatileData.AllData(true).ToArray();
            string frameStatus = (string)Method(component, "FrameRows").Invoke(component, new object[] { snapshot, new[] { 0, 2 } })!;
            Check(frameStatus.Contains("1 skipped"), "actual frame row action reports partial success");
            object?[] copyArgs = { snapshot, new[] { 0, 2 }, null };
            bool copied = (bool)Method(component, "TryCopyGeometry").Invoke(component, copyArgs)!;
            Check(copied && ((string)copyArgs[2]!).Contains("1 skipped"), "actual geometry-copy row action reports partial success");
            Check(beforeActions.SequenceEqual(component.Params.Output[0].VolatileData.AllData(true)),
                "framing and geometry copy leave the typed output unchanged");
            Method(component, "HighlightRows").Invoke(component, new object[] { snapshot, new[] { 0 } });
            Method(document, "CreatePreviewCaches").Invoke(document, Array.Empty<object>());
            var first = document.PreviewBoundingBox;
            Check(first.Contains(new Point3d(-20, 0, 0)), "host preview cache is primed with the first selected bounds");
            document.IsModified = false;
            Method(component, "HighlightRows").Invoke(component, new object[] { snapshot, new[] { 1 } });
            Check(!document.IsModified, "selection-only preview cache expiration does not mark the document modified");
            bool hasCaches = (bool)Method(document, "HasPreviewCaches").Invoke(document, Array.Empty<object>())!;
            Check(!hasCaches || document.PreviewBoundingBox.Contains(new Point3d(20, 5, 0)),
                "selection invalidates the old host cache or the redraw has already rebuilt it");
            Method(document, "CreatePreviewCaches").Invoke(document, Array.Empty<object>());
            var second = document.PreviewBoundingBox;
            Check(second.Contains(new Point3d(20, 5, 0)) && !second.Contains(new Point3d(-20, 0, 0)),
                "selection expires component and document preview bounds without a source solution");
            view.ActiveViewport.ZoomBoundingBox(new BoundingBox(new Point3d(15, -8, -1), new Point3d(25, 8, 1)));
            view.Redraw();
            using var bitmap = Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480);
            int green = 0;
            for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
            { var c = bitmap.GetPixel(x, y); if (c.G > c.R + 40 && c.G > c.B + 40) green++; }
            Check(green > 25, "actual viewport drawing uses the newly admitted selected geometry outside the old bounds");

            component.DocumentContextChanged(document, GH_DocumentContext.Close);
        }
        finally
        {
            document.Properties.KeepOpen = false; document.PreviewMode = GH_PreviewMode.Disabled;
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close();
            if (priorClipboard == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(priorClipboard, true);
        }
    }

}
