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

// Every assertion in this fixture is plugin-controlled except the separately
// identified real-solve/document-cache probe at the end.
internal static partial class PreviewSafetyChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    internal static async Task Run(Assembly assembly)
    {
        Rhino.Runtime.HostUtils.InitializeRhinoCommon();
        var rhino = RhinoDoc.ActiveDoc ?? RhinoDoc.Create(null);
        bool previousModified = rhino.Modified;
        var view = rhino.Views.ActiveView ?? rhino.Views.Add("Preview safety", Rhino.Display.DefinedViewportProjection.Top,
            new Rectangle(0, 0, 640, 480), false);
        rhino.Views.ActiveView = view;
        try
        {
            object highlight = Activator.CreateInstance(assembly.GetType("BetterGhPanel.Runtime.PanelGeometryHighlight", true)!, true)!;
            VerifyMutation(assembly, highlight);
            VerifyBudgets(highlight);
            VerifyStagedAdmission(highlight);
            VerifyActions(assembly);
            VerifyResources(highlight);
            await VerifyDocumentCacheAndDrawing(assembly, rhino);
        }
        finally { rhino.Modified = previousModified; }
    }

    internal static async Task RunMetadata(Assembly assembly)
    {
        var rhino = RhinoDoc.ActiveDoc ?? RhinoDoc.Create(null);
        bool modified = rhino.Modified;
        var previous = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Preview metadata", Rhino.Display.DefinedViewportProjection.Top,
            new Rectangle(0, 0, 640, 480), false);
        try
        {
            rhino.Views.ActiveView = view;
            object highlight = Activator.CreateInstance(assembly.GetType("BetterGhPanel.Runtime.PanelGeometryHighlight", true)!, true)!;
            VerifyMetadataAndActions(assembly, highlight);
            VerifyReentrancy(assembly, highlight);
            VerifyLatePublicationContext(assembly, rhino);
            VerifyReferencedPointFreshness(assembly, highlight, rhino);
            await VerifyUnsupportedGeometry(assembly, highlight, rhino);
            VerifyDenseSelectionGestures(assembly, highlight);
            await VerifyCrossComponentActions(assembly, rhino);
        }
        finally
        {
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close();
            rhino.Modified = modified;
        }
    }

    internal static Task RunAdditionalGeometry(Assembly assembly) => PanelHostFixture.RunWithEditor(assembly, RunAdditionalGeometryCore);

    private static async Task RunAdditionalGeometryCore(Assembly assembly)
    {
        FixtureUserData.EnsureRegistered();
        var rhino = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("Additional geometry needs a Rhino document.");
        bool modified = rhino.Modified;
        var clipboard = Forms.Clipboard.GetDataObject();
        var originalIds = rhino.Objects.Select(item => item.Id).ToHashSet();
        using var cloud = new PointCloud();
        cloud.Add(new Point3d(-18, 0, 0), Vector3d.ZAxis, Color.Aqua, 7.5);
        cloud.Add(new Point3d(-17, 1, 1), Vector3d.ZAxis, Color.Orange, 2.5);
        using var profile = new Rectangle3d(new Plane(new Point3d(-10, 0, 0), Vector3d.ZAxis), 2, 3).ToNurbsCurve();
        using var extrusion = Extrusion.Create(profile, 4, true) ?? throw new InvalidOperationException("Extrusion fixture failed.");
        var polyline = new Polyline(new[] { new Point3d(10, 0, 0), new Point3d(12, 1, 0), new Point3d(13, 0, 0) });
        var cloudHolder = new GH_PointCloud { Value = cloud };
        var extrusionHolder = new GH_Extrusion { Value = extrusion };
        var bbox = new BoundingBox(new Point3d(-3, 0, 0), new Point3d(-1, 2, 2));
        var sphere = new Sphere(new Plane(new Point3d(5, 0, 0), Vector3d.ZAxis), 1);
        var polylineWrapper = new GH_ObjectWrapper(polyline);
        var values = new IGH_Goo?[] { cloudHolder, extrusionHolder, new GH_ObjectWrapper(bbox), new GH_ObjectWrapper(sphere), polylineWrapper };
        var group = new GH_GeometryGroup { Name = "mixed" };
        group.Objects.Add(cloudHolder); group.Objects.Add(extrusionHolder); group.Objects.Add(new GH_Point(new Point3d(-14, 0, 0)));
        var wrappedGroup = new GH_GeometricGooWrapper(group);
        object highlight = Activator.CreateInstance(assembly.GetType("BetterGhPanel.Runtime.PanelGeometryHighlight", true)!, true)!;
        try
        {
            Check(cloud.SetUserString("source", "cloud") && extrusion.SetUserString("source", "extrusion") &&
                cloud.UserData.Add(new CopyRoundTripUserData { Value = 301 }) && extrusion.UserData.Add(new CopyRoundTripUserData { Value = 302 }),
                "point cloud and extrusion carry serializable source metadata");
            var expected = new[] { cloud.GetBoundingBox(false), extrusion.GetBoundingBox(false), bbox, sphere.BoundingBox,
                new BoundingBox(new Point3d(10, 0, 0), new Point3d(13, 1, 0)) };
            for (int i = 0; i < values.Length; i++)
            {
                Set(highlight, new[] { values[i] }, new[] { 0 }, new[] { 0 });
                Check(Count(highlight) == 1 && Box(highlight).Contains(expected[i].Min) && Box(highlight).Contains(expected[i].Max) &&
                    !Status(highlight).Contains("partial") && Bounds(assembly, values[i]!).Contains(expected[i].Center),
                    "additional geometry " + i + " receives a coherent standalone preview and frame bounds");
                object frame = Frame(assembly, new[] { values[i] });
                Check(Property<bool>(frame, "Success") && Property<int>(frame, "Skipped") == 0,
                    "additional geometry " + i + " frames without a skip");
            }
            Set(highlight, new IGH_Goo?[] { group, wrappedGroup }, new[] { 0, 1 }, new[] { 0 });
            Check(Count(highlight) == 2 && Box(highlight).Contains(expected[0].Min) && Box(highlight).Contains(expected[1].Max),
                "mixed and wrapped groups preview terminal cloud, extrusion and point leaves");
            object groupCopy = Copy(assembly, new IGH_Goo?[] { group });
            Check(Property<bool>(groupCopy, "Success") && Property<int>(groupCopy, "Count") == 3 && Property<int>(groupCopy, "Skipped") == 0 &&
                Property<bool>(groupCopy, "UnverifiedMetadata"), "a mixed group copies all leaves with the custom-metadata warning");
            using (var archive = ClipboardArchive())
            {
                var copied = ArchiveGeometries(archive);
                Console.WriteLine("Additional source geometry: " + DescribeAdditional(new GeometryBase[] { cloud, extrusion }));
                Console.WriteLine("Mixed-group clipboard geometry: " + DescribeAdditional(copied));
                Check(copied.OfType<PointCloud>().Any(value => value.Count == 2 && value[0].Color.ToArgb() == Color.Aqua.ToArgb() &&
                    value[0].Normal == Vector3d.ZAxis && value[0].PointValue == 7.5 && !value[1].Hidden &&
                    value.GetUserString("source") == "cloud" && (value.UserData.Find(typeof(CopyRoundTripUserData)) as CopyRoundTripUserData)?.Value == 301) &&
                    copied.OfType<Extrusion>().Any(value => value.GetUserString("source") == "extrusion" &&
                    (value.UserData.Find(typeof(CopyRoundTripUserData)) as CopyRoundTripUserData)?.Value == 302),
                    "mixed-group clipboard preserves cloud attributes and root custom metadata on both native types");
            }
            Check(RhinoApp.RunScript(rhino.RuntimeSerialNumber, "_Paste", false), "Rhino accepts the mixed-group clipboard");
            var groupDeadline = DateTime.UtcNow.AddSeconds(20);
            while (rhino.Objects.Count(item => !originalIds.Contains(item.Id)) < 3)
            { if (DateTime.UtcNow >= groupDeadline) throw new TimeoutException("Mixed-group Paste did not add three objects."); await Task.Delay(25); }
            var groupPasted = rhino.Objects.Where(item => !originalIds.Contains(item.Id)).ToArray();
            Check(groupPasted.Length == 3 && groupPasted.Select(item => item.Geometry).OfType<PointCloud>().Any(value =>
                    value.Count == 2 && value[0].Color.ToArgb() == Color.Aqua.ToArgb() && value[0].Normal == Vector3d.ZAxis &&
                    value[0].PointValue == 7.5 && (value.UserData.Find(typeof(CopyRoundTripUserData)) as CopyRoundTripUserData)?.Value == 301) &&
                groupPasted.Select(item => item.Geometry).Any(value => value.GetUserString("source") == "extrusion" &&
                    (value.UserData.Find(typeof(CopyRoundTripUserData)) as CopyRoundTripUserData)?.Value == 302) &&
                groupPasted.Select(item => item.Geometry).OfType<Rhino.Geometry.Point>().Any(value => value.Location == new Point3d(-14, 0, 0)) &&
                cloud.GetUserString("source") == "cloud" && extrusion.GetUserString("source") == "extrusion",
                "real Paste of a mixed group preserves all three leaves and cloud/extrusion metadata");
            foreach (var item in groupPasted) rhino.Objects.Delete(item.Id, true);
            object frameGroup = Frame(assembly, new IGH_Goo?[] { wrappedGroup });
            Check(Property<bool>(frameGroup, "Success") && Property<int>(frameGroup, "Skipped") == 0,
                "a wrapped mixed group frames without recursive wrapper dispatch");
            object copy = Copy(assembly, values);
            Check(Property<bool>(copy, "Success") && Property<int>(copy, "Count") == 5 && Property<int>(copy, "Skipped") == 0,
                "all five additional geometry values copy without skips");
            using (var archive = ClipboardArchive())
            {
                var copied = ArchiveGeometries(archive);
                Check(copied.Length == 5 && copied.OfType<PointCloud>().Count() == 1 && copied.OfType<Extrusion>().Count() == 1 &&
                    copied.OfType<Brep>().Count() == 2 && copied.OfType<Brep>().Any(value => value.GetBoundingBox(false).Contains(bbox.Center)) &&
                    copied.OfType<Brep>().Any(value => value.GetBoundingBox(false).Contains(sphere.Center)) &&
                    copied.OfType<PolylineCurve>().Any(value => value.PointCount == 3 && value.Point(1) == polyline[1]),
                    "the clipboard archive contains cloud, extrusion, box, sphere and polyline geometry at their original locations");
            }
            Check(RhinoApp.RunScript(rhino.RuntimeSerialNumber, "_Paste", false), "Rhino accepts the additional geometry clipboard");
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (rhino.Objects.Count(item => !originalIds.Contains(item.Id)) < 5)
            { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Additional geometry Paste did not add five objects."); await Task.Delay(25); }
            var pasted = rhino.Objects.Where(item => !originalIds.Contains(item.Id)).Select(item => item.Geometry).ToArray();
            Console.WriteLine("Pasted additional geometry: " + DescribeAdditional(pasted));
            Check(pasted.Length == 5 && pasted.OfType<PointCloud>().Any(value => value.Count == 2 && value.GetUserString("source") == "cloud" &&
                    value[0].Color.ToArgb() == Color.Aqua.ToArgb() && value[0].Normal == Vector3d.ZAxis && value[0].PointValue == 7.5 && !value[1].Hidden &&
                    (value.UserData.Find(typeof(CopyRoundTripUserData)) as CopyRoundTripUserData)?.Value == 301) &&
                pasted.Any(value => value.GetUserString("source") == "extrusion" && value.GetBoundingBox(false).Contains(expected[1].Center) &&
                    (value.UserData.Find(typeof(CopyRoundTripUserData)) as CopyRoundTripUserData)?.Value == 302) &&
                pasted.OfType<PolylineCurve>().Any(value => value.PointCount == 3 && value.Point(2) == polyline[2]) &&
                pasted.Any(value => value.GetBoundingBox(false).Contains(bbox.Center)) &&
                pasted.Any(value => value.GetBoundingBox(false).Contains(sphere.Center)) &&
                cloud.GetUserString("source") == "cloud" && extrusion.GetUserString("source") == "extrusion" &&
                (cloud.UserData.Find(typeof(CopyRoundTripUserData)) as CopyRoundTripUserData)?.Value == 301 &&
                (extrusion.UserData.Find(typeof(CopyRoundTripUserData)) as CopyRoundTripUserData)?.Value == 302,
                "real Paste preserves additional geometry, cloud attributes and source metadata");

            await VerifyAdditionalReferences(assembly, highlight, rhino, cloud, extrusion);

            Set(highlight, new IGH_Goo?[] { cloudHolder, group }, new[] { 0, 1 }, new[] { 0, 1 });
            var cloudGenerationBounds = Box(highlight);
            cloud.Add(new Point3d(50, 4, 0), Vector3d.ZAxis, Color.Aqua, 1);
            Check(Box(highlight).Equals(cloudGenerationBounds) && !Box(highlight).Contains(new Point3d(50, 4, 0)),
                "standalone and grouped clouds keep their original bounds after in-place source mutation without expiration");
            Set(highlight, new IGH_Goo?[] { cloudHolder, group }, new[] { 0, 1 }, new[] { 0, 1 });
            Check(Count(highlight) == 2 && Box(highlight).Contains(new Point3d(50, 4, 0)),
                "a fresh preview generation captures the changed standalone and grouped cloud");
            cloud.RemoveAt(2);
            using var shiftedProfile = new Rectangle3d(new Plane(new Point3d(70, 0, 0), Vector3d.ZAxis), 2, 3).ToNurbsCurve();
            using var shiftedExtrusion = Extrusion.Create(shiftedProfile, 4, true) ?? throw new InvalidOperationException("Replacement extrusion fixture failed.");
            Set(highlight, new IGH_Goo?[] { extrusionHolder, group }, new[] { 0, 1 }, new[] { 0, 1 });
            var extrusionGenerationBounds = Box(highlight);
            extrusionHolder.Value = shiftedExtrusion;
            Check(Box(highlight).Equals(extrusionGenerationBounds) && !Box(highlight).Contains(new Point3d(70, 0, 0)),
                "standalone and grouped extrusion previews retain their old generation after holder replacement");
            Set(highlight, new IGH_Goo?[] { extrusionHolder, group }, new[] { 0, 1 }, new[] { 0, 1 });
            Check(Count(highlight) == 2 && Box(highlight).Contains(new Point3d(70, 0, 0)),
                "a fresh preview generation captures the replacement standalone and grouped extrusion");
            extrusionHolder.Value = extrusion;

            var oldBox = Bounds(assembly, polylineWrapper);
            Set(highlight, new IGH_Goo?[] { polylineWrapper }, new[] { 0 }, new[] { 0 });
            polyline[1] = new Point3d(12, 20, 0);
            Check(Box(highlight).Equals(oldBox), "a wrapped mutable polyline retains the old preview bounds until refresh");
            Set(highlight, new IGH_Goo?[] { polylineWrapper }, new[] { 0 }, new[] { 0 });
            Check(Box(highlight).Max.Y == 20, "a new preview generation captures the changed polyline");
            var custom = new StandalonePointPreview();
            var beforeFrame = rhino.Views.ActiveView.ActiveViewport.CameraTarget;
            custom.OnBounds = () => polyline[1] = new Point3d(12, 25, 0);
            object staleFrame = Frame(assembly, new IGH_Goo?[] { polylineWrapper, custom });
            Check(!Property<bool>(staleFrame, "Success") && rhino.Views.ActiveView.ActiveViewport.CameraTarget == beforeFrame,
                "a later bounds callback changing an earlier wrapped polyline cancels the camera action");
            Forms.Clipboard.SetText("keep polyline freshness clipboard");
            CopyRoundTripUserData.DuplicateAt = 1; CopyRoundTripUserData.TriggeredGeneration = 0;
            CopyRoundTripUserData.OnDuplicateAction = () => polyline[1] = new Point3d(12, 30, 0);
            copy = Copy(assembly, new IGH_Goo?[] { polylineWrapper, cloudHolder });
            Check(!Property<bool>(copy, "Success") && CopyRoundTripUserData.TriggeredGeneration > 0 &&
                Forms.Clipboard.GetText() == "keep polyline freshness clipboard",
                "a later duplicate callback changing an earlier wrapped polyline cancels clipboard publication");
            CopyRoundTripUserData.TriggeredGeneration = 0;
            CopyRoundTripUserData.OnDuplicateAction = () => polylineWrapper.Value = new Polyline(polyline);
            copy = Copy(assembly, new IGH_Goo?[] { polylineWrapper, cloudHolder });
            Check(!Property<bool>(copy, "Success") && CopyRoundTripUserData.TriggeredGeneration > 0 &&
                Forms.Clipboard.GetText() == "keep polyline freshness clipboard",
                "retargeting a wrapped polyline to equal points during duplication cancels clipboard publication");
            polylineWrapper.Value = polyline;
            CopyRoundTripUserData.TriggeredGeneration = 0;
            CopyRoundTripUserData.OnDuplicateAction = () => group.Objects[1] = new GH_Extrusion { Value = extrusion };
            copy = Copy(assembly, new IGH_Goo?[] { group });
            Check(!Property<bool>(copy, "Success") && CopyRoundTripUserData.TriggeredGeneration > 0 &&
                Forms.Clipboard.GetText() == "keep polyline freshness clipboard",
                "replacing a mixed group's extrusion holder during cloud duplication cancels publication");
            group.Objects[1] = extrusionHolder;
            CopyRoundTripUserData.OnDuplicateAction = null;

            beforeFrame = rhino.Views.ActiveView.ActiveViewport.CameraTarget;
            custom.OnBounds = () => cloud[1].Hidden = true;
            staleFrame = Frame(assembly, new IGH_Goo?[] { cloudHolder, custom });
            Check(!Property<bool>(staleFrame, "Success") && cloud[1].Hidden &&
                rhino.Views.ActiveView.ActiveViewport.CameraTarget == beforeFrame,
                "a later bounds callback adding cloud hidden flags cancels frame before camera motion");
            cloud.ClearHiddenFlags();
            CopyRoundTripUserData.TriggeredGeneration = 0;
            CopyRoundTripUserData.OnDuplicateAction = () => cloud[1].Hidden = true;
            copy = Copy(assembly, new IGH_Goo?[] { cloudHolder });
            Check(!Property<bool>(copy, "Success") && CopyRoundTripUserData.TriggeredGeneration > 0 && cloud[1].Hidden &&
                Forms.Clipboard.GetText() == "keep polyline freshness clipboard",
                "hidden flags added by a duplicate callback cancel copy before clipboard publication");
            cloud.ClearHiddenFlags();
            CopyRoundTripUserData.TriggeredGeneration = 0;
            CopyRoundTripUserData.OnDuplicateAction = () => cloud[1].Hidden = true;
            Set(highlight, new IGH_Goo?[] { cloudHolder }, new[] { 0 }, new[] { 0 });
            Check(Count(highlight) == 0 && Status(highlight).Contains("partial") && cloud[1].Hidden &&
                CopyRoundTripUserData.TriggeredGeneration > 0,
                "hidden flags added during preview construction reject the stale generation");
            CopyRoundTripUserData.OnDuplicateAction = null;
            cloud.ClearHiddenFlags();

            long stagedBytes = 0;
            var observerField = highlight.GetType().GetField("_admissionObserver", Instance)!;
            observerField.SetValue(highlight, (Action<int, object>)((_, __) =>
            {
                stagedBytes = Property<long>(highlight, "RetainedBytes");
                polyline[1] = new Point3d(12, 40, 0);
            }));
            Set(highlight, new IGH_Goo?[] { polylineWrapper }, new[] { 0 }, new[] { 0 });
            observerField.SetValue(highlight, null);
            Check(stagedBytes > 128 && Count(highlight) == 0 && Status(highlight).Contains("partial") &&
                Property<long>(highlight, "RetainedBytes") == 0,
                "wrapped polyline staging is accounted and a mid-build mutation rejects and releases the generation");

            using var hiddenCloud = new PointCloud();
            hiddenCloud.Add(new Point3d(0, 0, 0)); hiddenCloud.Add(new Point3d(1, 0, 0)); hiddenCloud[1].Hidden = true;
            using (var file = new File3dm())
            {
                Guid id = file.Objects.Add(hiddenCloud, null);
                using var roundtrip = File3dm.FromByteArray(file.ToByteArray(new File3dmWriteOptions { Version = 8, SaveUserData = true }));
                var archived = roundtrip.Objects.FindId(id)?.Geometry as PointCloud;
                Console.WriteLine($"Hidden-cloud archive diagnostic: source hidden={hiddenCloud[1].Hidden}, " +
                    $"archive type={archived?.GetType().Name}, archive hidden={archived?[1].Hidden}");
                Check(id != Guid.Empty && hiddenCloud.ContainsHiddenFlags && hiddenCloud[1].Hidden && archived != null &&
                    archived.Count == 2 && !archived[1].Hidden,
                    "Rhino's 3DM roundtrip demonstrably drops a cloud's hidden flag while leaving its source unchanged");
            }
            var hiddenHolder = new GH_PointCloud { Value = hiddenCloud };
            var hiddenGroup = new GH_GeometryGroup(); hiddenGroup.Objects.Add(hiddenHolder);
            Check(Property<bool>(Frame(assembly, new IGH_Goo?[] { hiddenGroup }), "Success"),
                "a cloud with hidden points remains frameable through a group");
            Forms.Clipboard.SetText("keep hidden cloud clipboard");
            copy = Copy(assembly, new IGH_Goo?[] { hiddenHolder });
            Check(!Property<bool>(copy, "Success") && Property<int>(copy, "Skipped") == 1 &&
                Property<string>(copy, "Error").Contains("hidden flags") && Forms.Clipboard.GetText() == "keep hidden cloud clipboard" &&
                hiddenCloud[1].Hidden, "a cloud with non-roundtrippable hidden flags is visibly skipped without changing the clipboard or source");
            copy = Copy(assembly, new IGH_Goo?[] { hiddenGroup, new GH_Point(Point3d.Origin) });
            Check(Property<bool>(copy, "Success") && Property<int>(copy, "Count") == 1 && Property<int>(copy, "Skipped") == 1 &&
                Property<string>(copy, "Detail").Contains("hidden flags") && hiddenCloud[1].Hidden,
                "a mixed action visibly reports a skipped hidden-cloud group while copying the supported point");
            using (var partialArchive = ClipboardArchive())
                Check(ArchiveGeometries(partialArchive).Single() is Rhino.Geometry.Point,
                    "the partial clipboard contains only the supported geometry");

            using var orderedCloud = new PointCloud();
            orderedCloud.Add(new Point3d(1, 0, 0), Color.Aqua);
            orderedCloud.Add(new Point3d(2, 0, 0), Color.Orange);
            orderedCloud.Add(new Point3d(3, 0, 0), Color.Aqua);
            Set(highlight, new IGH_Goo?[] { new GH_PointCloud { Value = orderedCloud } }, new[] { 0 }, new[] { 0 });
            var pairs = ((IEnumerable)Field<object>(highlight, "_entries")).Cast<object>().ToArray();
            var entry = pairs.Single().GetType().GetProperty("Value")!.GetValue(pairs.Single())!;
            var leaf = ((IEnumerable)Field<object>(entry, "Leaves")).Cast<object>().Single();
            var snapshot = Field<object>(leaf, "_value");
            var batches = ((IEnumerable)Property<object>(snapshot, "Batches")).Cast<object>().ToArray();
            Check(batches.Select(batch => Property<int>(batch, "Argb")).SequenceEqual(new[] { Color.Aqua.ToArgb(), Color.Orange.ToArgb(), Color.Aqua.ToArgb() }) &&
                batches.SelectMany(batch => Property<Point3d[]>(batch, "Points")).SequenceEqual(new[] { new Point3d(1, 0, 0), new Point3d(2, 0, 0), new Point3d(3, 0, 0) }) &&
                !ReferenceEquals(Field<GeometryBase>(leaf, "_owned"), orderedCloud) && Property<long>(highlight, "RetainedBytes") <= 16L * 1024 * 1024,
                "owned cloud batches preserve point/color order and stay inside the preview budget");

            using var mediumCloud = new PointCloud();
            for (int i = 0; i < 8193; i++) mediumCloud.Add(new Point3d(i, 0, 0));
            var mediumHolder = new GH_PointCloud { Value = mediumCloud };
            var mediumGroup = new GH_GeometryGroup(); mediumGroup.Objects.Add(mediumHolder);
            Set(highlight, new IGH_Goo?[] { mediumHolder, mediumGroup }, new[] { 0, 1 }, new[] { 0, 1 });
            copy = Copy(assembly, new IGH_Goo?[] { mediumGroup });
            Check(Count(highlight) == 2 && Box(highlight).Contains(new Point3d(8192, 0, 0)) &&
                Property<bool>(copy, "Success") && Property<int>(copy, "Count") == 1 && Property<int>(copy, "Skipped") == 0,
                "an 8,193-point cloud remains previewable and copyable both standalone and in a group");
            using (var mediumArchive = ClipboardArchive())
                Check(ArchiveGeometries(mediumArchive).OfType<PointCloud>().Single().Count == 8193,
                    "the medium cloud clipboard archive retains every point");
            using var largeCloud = new PointCloud();
            for (int i = 0; i < 32769; i++) largeCloud.Add(new Point3d(i, 0, 0));
            Check(largeCloud.UserData.Add(new PolylineSafetyUserData()), "oversized point cloud has callback metadata before preflight");
            PolylineSafetyUserData.Reset(true);
            Forms.Clipboard.SetText("keep cloud limit clipboard");
            copy = Copy(assembly, new IGH_Goo?[] { new GH_PointCloud { Value = largeCloud }, new GH_Point(Point3d.Origin) });
            Check(Property<bool>(copy, "Success") && Property<int>(copy, "Skipped") == 1 && Property<bool>(copy, "Exhausted") &&
                PolylineSafetyUserData.Callbacks == 0 && largeCloud.HasUserData,
                "oversized point-cloud preflight reports a partial action before invoking its metadata callback");
            PolylineSafetyUserData.Reset(false);

            await VerifyAdditionalTypedOutput(assembly, values, group);
        }
        finally
        {
            CopyRoundTripUserData.OnDuplicateAction = null;
            PolylineSafetyUserData.Reset(false);
            highlight.GetType().GetField("_admissionObserver", Instance)!.SetValue(highlight, null);
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            foreach (var item in rhino.Objects.Where(item => !originalIds.Contains(item.Id)).ToArray()) rhino.Objects.Delete(item.Id, true);
            rhino.Modified = modified;
            if (clipboard == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(clipboard, true);
        }
    }

    private static GeometryBase[] ArchiveGeometries(File3dm archive)
    {
        var values = new List<GeometryBase>();
        foreach (var item in archive.Objects) values.Add(item?.Geometry ?? throw new InvalidOperationException("Archive object is unavailable."));
        return values.ToArray();
    }

    private static string DescribeAdditional(IEnumerable<GeometryBase> geometries) => string.Join("; ", geometries.Select(value =>
    {
        string detail = value.GetType().Name + " source=" + value.GetUserString("source") + " custom=" +
            ((value.UserData.Find(typeof(CopyRoundTripUserData)) as CopyRoundTripUserData)?.Value.ToString() ?? "absent");
        if (value is PointCloud cloud)
            detail += " count=" + cloud.Count + " points=[" + string.Join(",", Enumerable.Range(0, cloud.Count).Select(i =>
                cloud[i].Location + "/argb=" + cloud[i].Color.ToArgb().ToString("X8") + "/normal=" + cloud[i].Normal +
                "/value=" + cloud[i].PointValue + "/hidden=" + cloud[i].Hidden)) + "]";
        return detail;
    }));

    private static Task VerifyAdditionalReferences(Assembly assembly, object highlight, RhinoDoc rhino, PointCloud cloud, Extrusion extrusion)
    {
        Guid cloudId = rhino.Objects.AddPointCloud(cloud);
        Guid extrusionId = rhino.Objects.AddExtrusion(extrusion);
        var loadedCloud = new GH_PointCloud(cloudId);
        var loadedExtrusion = new GH_Extrusion(extrusionId);
        Check(cloudId != Guid.Empty && extrusionId != Guid.Empty && loadedCloud.LoadGeometry(rhino) && loadedExtrusion.LoadGeometry(rhino) &&
            loadedCloud.IsReferencedGeometry && loadedCloud.IsGeometryLoaded && loadedExtrusion.IsReferencedGeometry && loadedExtrusion.IsGeometryLoaded,
            "point-cloud and extrusion reference controls load from the Rhino document");
        var group = new GH_GeometryGroup(); group.Objects.Add(loadedCloud); group.Objects.Add(loadedExtrusion);
        Set(highlight, new IGH_Goo?[] { loadedCloud, loadedExtrusion, group }, new[] { 0, 1, 2 }, new[] { 0, 1, 2 });
        Check(Count(highlight) == 3 && Box(highlight).Contains(cloud.GetBoundingBox(false).Center) &&
            Box(highlight).Contains(extrusion.GetBoundingBox(false).Center) &&
            Property<bool>(Frame(assembly, new IGH_Goo?[] { group }), "Success"),
            "loaded references preview and frame both independently and inside a group");
        object copy = Copy(assembly, new IGH_Goo?[] { group });
        Check(Property<bool>(copy, "Success") && Property<int>(copy, "Count") == 2 && Property<int>(copy, "Skipped") == 0,
            "a group of loaded references copies both terminal geometries");
        using (var archive = ClipboardArchive())
        {
            var values = ArchiveGeometries(archive);
            Check(values.OfType<PointCloud>().Any(value => value.GetUserString("source") == "cloud" &&
                    (value.UserData.Find(typeof(CopyRoundTripUserData)) as CopyRoundTripUserData)?.Value == 301) &&
                values.Any(value => value.GetUserString("source") == "extrusion" &&
                    (value.UserData.Find(typeof(CopyRoundTripUserData)) as CopyRoundTripUserData)?.Value == 302),
                "loaded-reference clipboard geometry retains both source metadata payloads");
        }
        Forms.Clipboard.SetText("keep retargeted cloud clipboard");
        CopyRoundTripUserData.DuplicateAt = 1; CopyRoundTripUserData.TriggeredGeneration = 0;
        CopyRoundTripUserData.OnDuplicateAction = () => loadedCloud.ReferenceID = extrusionId;
        copy = Copy(assembly, new IGH_Goo?[] { loadedCloud });
        Check(!Property<bool>(copy, "Success") && CopyRoundTripUserData.TriggeredGeneration > 0 &&
            Forms.Clipboard.GetText() == "keep retargeted cloud clipboard",
            "retargeting a loaded cloud reference during duplication cancels clipboard publication");
        CopyRoundTripUserData.OnDuplicateAction = null;
        return Task.CompletedTask;
    }

    private static async Task VerifyAdditionalTypedOutput(Assembly assembly, IGH_Goo?[] values, GH_GeometryGroup group)
    {
        using var fixture = new PanelHostFixture.Fixture(assembly);
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        var all = values.Concat(new IGH_Goo?[] { group }).ToArray();
        fixture.Source.PersistentData.AppendRange(all!, new GH_Path(0));
        fixture.Paired.PersistentData.AppendRange(Enumerable.Range(0, all.Length).Select(i => (IGH_Goo)new GH_String("pair-" + i)), new GH_Path(0));
        fixture.Solve();
        await fixture.Ready(() => fixture.Sinks[0].VolatileData.DataCount == all.Length && fixture.Sinks[3].VolatileData.DataCount == all.Length);
        var delivered = fixture.Component.Params.Input[0].VolatileData.AllData(false).ToArray();
        var output = fixture.Sinks[0].VolatileData.AllData(false).ToArray();
        var snapshot = fixture.Result.Snapshot;
        Console.WriteLine("Additional captured rows: " + string.Join("; ", snapshot.Rows.Select(row =>
            $"type={row.TypeName} known={row.TypeIdentityAvailable} validity={row.Validity} display={row.DisplayValue} capabilities={row.SearchCapabilities}")));
        Check(snapshot.Rows[0].TypeName == "Point Cloud" && snapshot.Rows[1].TypeName == "Extrusion" &&
            snapshot.Rows[5].TypeName == "Group" && snapshot.Rows[0].TypeIdentityAvailable && snapshot.Rows[1].TypeIdentityAvailable &&
            snapshot.Rows[5].TypeIdentityAvailable && new[] { 0, 1, 5 }.All(i => snapshot.Rows[i].Validity == PanelValidity.VerifiedValid),
            "capture retains exact cloud/extrusion/group identities and verifies their valid contents");
        Check(snapshot.Rows[0].DisplayValue == "<Point Cloud>" && snapshot.Rows[1].DisplayValue == "<Extrusion>" &&
            PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { TypeName = "Point Cloud" }).RowPositions.SequenceEqual(new[] { 0 }) &&
            PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { TypeName = "Extrusion" }).RowPositions.SequenceEqual(new[] { 1 }) &&
            PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "<Point Cloud>" }).RowPositions.Contains(0) &&
            PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = snapshot.Rows[5].DisplayValue }).RowPositions.Contains(5),
            "type and text searches use the known stock identities and captured group summary");
        Check(snapshot.SupportsSearchMode(PanelSearchMode.Text) && snapshot.SupportsSearchMode(PanelSearchMode.Regex) &&
            snapshot.SupportsSearchMode(PanelSearchMode.ListLength) && !snapshot.SupportsSearchMode(PanelSearchMode.Length) &&
            !snapshot.SupportsSearchMode(PanelSearchMode.Area) && !snapshot.SupportsSearchMode(PanelSearchMode.Volume) &&
            !PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { OnlyInvalid = true }).RowPositions.Any(i => i == 0 || i == 1 || i == 5),
            "valid cloud/extrusion/group rows have no metric capability and are excluded by invalid-only filtering");
        Check(delivered.Length == all.Length && output.Select(value => value?.GetType()).SequenceEqual(delivered.Select(value => value?.GetType())) &&
            delivered[0] is GH_PointCloud && delivered[1] is GH_Extrusion && delivered[5] is GH_GeometryGroup,
            "the real solve preserves delivered cloud, extrusion, wrapper and group types in D");
        fixture.SelectAt(0); fixture.SelectAt(5, toggle: true);
        fixture.Scope(PanelOutputScope.Selected);
        await fixture.Ready(() => fixture.Sinks[1].VolatileData.DataCount == 2 && fixture.Sinks[3].VolatileData.DataCount == 2);
        var selected = fixture.Sinks[1].VolatileData.AllData(false).ToArray();
        var mask = fixture.Sinks[2].VolatileData.AllData(false).Cast<GH_Boolean>().Select(value => value.Value).ToArray();
        Check(selected.Length == 2 && selected[0]?.GetType() == delivered[0]?.GetType() && selected[1]?.GetType() == delivered[5]?.GetType() &&
            mask.SequenceEqual(new[] { true, false, false, false, false, true }) && fixture.Values(3) == "pair-0,pair-5",
            "selected S and paired B/F preserve typed membership and source order");
    }

    internal static Task RunAdditionalGeometryDraw(Assembly assembly)
    {
        var rhino = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("Additional geometry drawing needs a Rhino document.");
        bool modified = rhino.Modified; var previous = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Additional geometry preview", Rhino.Display.DefinedViewportProjection.Top,
            new Rectangle(0, 0, 640, 480), false);
        using var document = new GH_Document { Enabled = true };
        using var cloud = new PointCloud(); cloud.Add(new Point3d(-15, 0, 0)); cloud.Add(new Point3d(-14.5, 0.5, 0));
        using var groupedCloud = new PointCloud(); groupedCloud.Add(new Point3d(-10, 0, 0)); groupedCloud.Add(new Point3d(-9.5, 0.5, 0));
        using var coloredCloud = new PointCloud();
        coloredCloud.Add(new Point3d(20, 0, 0), Color.Cyan); coloredCloud.Add(new Point3d(23, 0, 0), Color.Cyan);
        coloredCloud[1].Hidden = true;
        using var extrusion = HollowPreviewExtrusion(new Point3d(-5, -1, 0));
        using var groupExtrusion = HollowPreviewExtrusion(new Point3d(0, -1, 0));
        using var normalExtrusion = HollowPreviewExtrusion(new Point3d(-5, 5, 0));
        using var selectedGroupExtrusion = HollowPreviewExtrusion(new Point3d(0, 5, 0));
        var cloudGroup = new GH_GeometryGroup(); cloudGroup.Objects.Add(new GH_PointCloud { Value = groupedCloud });
        var extrusionGroup = new GH_GeometryGroup(); extrusionGroup.Objects.Add(new GH_Extrusion { Value = groupExtrusion });
        var selectedExtrusionGroup = new GH_GeometryGroup(); selectedExtrusionGroup.Objects.Add(new GH_Extrusion { Value = selectedGroupExtrusion });
        var bbox = new BoundingBox(new Point3d(5, -1, 0), new Point3d(7, 1, 2));
        var sphere = new Sphere(new Plane(new Point3d(10, 0, 0), Vector3d.ZAxis), 1);
        var polyline = new Polyline(new[] { new Point3d(14, -1, 0), new Point3d(15, 1, 0), new Point3d(16, -1, 0) });
        var source = new IGH_Goo?[] { new GH_PointCloud { Value = cloud }, cloudGroup, new GH_Extrusion { Value = extrusion },
            extrusionGroup, new GH_ObjectWrapper(bbox), new GH_ObjectWrapper(sphere), new GH_ObjectWrapper(polyline),
            new GH_PointCloud { Value = coloredCloud }, new GH_Extrusion { Value = normalExtrusion }, selectedExtrusionGroup };
        var snapshot = new PanelSnapshot(Enumerable.Range(0, source.Length).Select(i => new PanelRow(i, "{0}", "Geometry", "geometry", null)).ToArray(),
            new[] { new PanelBranch("{0}", source.Length) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        using var conduit = new PreviewConduit(component, document, Color.Blue, Color.Yellow);
        try
        {
            rhino.Views.ActiveView = view;
            view.ActiveViewport.ConstructionGridVisible = view.ActiveViewport.ConstructionAxesVisible = view.ActiveViewport.WorldAxesVisible = false;
            view.ActiveViewport.ZoomBoundingBox(new BoundingBox(new Point3d(-18, -4, -1), new Point3d(26, 11, 3)));
            var shaded = Rhino.Display.DisplayModeDescription.FindByName("Shaded");
            if (shaded != null) view.ActiveViewport.DisplayMode = shaded;
            document.Properties.KeepOpen = true; document.PreviewMode = GH_PreviewMode.Shaded; document.AddObject(component, false);
            object highlight = Field<object>(component, "_highlight");
            Scope(highlight, source, result, new[] { 0, 2, 4, 6, 7, 9 });
            component.ExpirePreview(false);
            Method(document, "CreatePreviewCaches").Invoke(document, Array.Empty<object>());
            var bounds = Box(highlight); var hostBounds = document.PreviewBoundingBox;
            Check(Count(highlight) == 10 && Property<int>(highlight, "Count") == 6 && hostBounds.Contains(bounds.Min) && hostBounds.Contains(bounds.Max),
                "all additional standalone and group previews enter coherent component/document bounds");
            bool called = false;
            conduit.OnDraw = args => { called = true; component.DrawViewportMeshes(args); component.DrawViewportWires(args); };
            conduit.Enabled = true;
            using var bitmap = CapturePreview(view, highlight, "additional geometry", conduit);
            Check(called && Property<int>(highlight, "DrawWireCalls") > 0 && Property<int>(highlight, "DrawMeshCalls") > 0,
                "an actual native capture executes additional geometry wire and shaded callbacks");
            var regions = new[] {
                new BoundingBox(new Point3d(-15.5,-1,0),new Point3d(-14,1,0)),
                new BoundingBox(new Point3d(-10.5,-1,0),new Point3d(-9,1,0)),
                extrusion.GetBoundingBox(false), groupExtrusion.GetBoundingBox(false), bbox, sphere.BoundingBox,
                new BoundingBox(new Point3d(14,-1,0),new Point3d(16,1,0)) };
            for (int i = 0; i < regions.Length; i++)
                Check(RegionHasColor(bitmap, view.ActiveViewport, regions[i], i % 2 == 0 ? Color.Lime : Color.Red),
                    "additional preview " + i + " produces actual selected or filtered viewport pixels");
            var extrusions = new[] { extrusion, groupExtrusion, normalExtrusion, selectedGroupExtrusion };
            for (int i = 0; i < extrusions.Length; i++)
            {
                bool selected = i == 0 || i == 3;
                Color shade = selected ? Color.Yellow : Color.Blue;
                Color wire = selected ? Color.Lime : Color.Red;
                var ext = extrusions[i]; var box = ext.GetBoundingBox(false);
                Check(ext.IsValid && ext.IsCappedAtBottom && ext.IsCappedAtTop && ext.ProfileCount == 2 &&
                    RegionColors(bitmap, view.ActiveViewport, box, shade, wire),
                    "extrusion " + i + " has distinct shaded and wire pixels for selected/normal standalone/group geometry");
                var cap = new BoundingBox(new Point3d(box.Min.X + 0.2, box.Min.Y + 0.2, box.Max.Z),
                    new Point3d(box.Min.X + 0.8, box.Min.Y + 2.8, box.Max.Z));
                var hole = new BoundingBox(new Point3d(box.Min.X + 1.2, box.Min.Y + 1.2, box.Max.Z),
                    new Point3d(box.Min.X + 1.8, box.Min.Y + 1.8, box.Max.Z));
                Check(InteriorShadePixels(bitmap, view.ActiveViewport, cap, shade) > 10 &&
                    InteriorShadePixels(bitmap, view.ActiveViewport, hole, shade) == 0,
                    "extrusion " + i + " shades its cap interior while preserving its inner-profile hole");
            }
            Check(RegionHasColor(bitmap, view.ActiveViewport, new BoundingBox(new Point3d(19.5, -0.5, 0), new Point3d(20.5, 0.5, 0)), Color.Cyan) &&
                !RegionHasColor(bitmap, view.ActiveViewport, new BoundingBox(new Point3d(22.5, -0.5, 0), new Point3d(23.5, 0.5, 0)), Color.Cyan) &&
                coloredCloud[1].Hidden,
                "selected colored clouds retain source colors and omit hidden-point pixels without changing the source");
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            Method(highlight, "ReleaseOnIdle").Invoke(highlight, new object?[] { null, EventArgs.Empty });
            Check(Property<long>(highlight, "RetainedBytes") == 0, "explicit post-capture cleanup releases additional preview resources");
        }
        finally
        {
            conduit.Enabled = false; document.Properties.KeepOpen = false; document.PreviewMode = GH_PreviewMode.Disabled;
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close(); rhino.Modified = modified;
        }
        return Task.CompletedTask;
    }

    private static Extrusion HollowPreviewExtrusion(Point3d origin)
    {
        using var outer = new Rectangle3d(Plane.WorldXY, 3, 3).ToNurbsCurve();
        using var inner = new Rectangle3d(Plane.WorldXY, new Interval(1, 2), new Interval(1, 2)).ToNurbsCurve();
        inner.Reverse();
        var extrusion = Extrusion.Create(outer, 2, true) ?? throw new InvalidOperationException("Extrusion drawing fixture failed.");
        if (!extrusion.AddInnerProfile(inner) || !extrusion.Translate((Vector3d)origin))
        { extrusion.Dispose(); throw new InvalidOperationException("Extrusion inner-profile fixture failed."); }
        return extrusion;
    }

    private static int InteriorShadePixels(Bitmap bitmap, Rhino.Display.RhinoViewport viewport, BoundingBox region, Color shade)
    {
        var p = viewport.WorldToClient(region.Min); var q = viewport.WorldToClient(region.Max);
        double sx = bitmap.Width / (double)Math.Max(1, viewport.Size.Width), sy = bitmap.Height / (double)Math.Max(1, viewport.Size.Height);
        int x0 = Math.Max(0, (int)Math.Ceiling(Math.Min(p.X, q.X) * sx)), x1 = Math.Min(bitmap.Width, (int)Math.Floor(Math.Max(p.X, q.X) * sx));
        int y0 = Math.Max(0, (int)Math.Ceiling(Math.Min(p.Y, q.Y) * sy)), y1 = Math.Min(bitmap.Height, (int)Math.Floor(Math.Max(p.Y, q.Y) * sy));
        int count = 0;
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
        {
            var color = bitmap.GetPixel(x, y);
            if (shade == Color.Blue ? color.B > color.R + 15 && color.B > color.G + 15 : color.R > color.B + 15 && color.G > color.B + 15) count++;
        }
        Console.WriteLine($"Extrusion interior {region}: {shade.Name}={count}, pixels={x0},{y0}-{x1},{y1}");
        return count;
    }

    private static async Task VerifyCrossComponentActions(Assembly assembly, RhinoDoc rhino)
    {
        var prior = Forms.Clipboard.GetDataObject();
        using var document = new GH_Document { Enabled = true };
        try
        {
            document.Properties.KeepOpen = true;
            GH_Document.EnableSolutions = true;
            var componentType = assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!;
            var first = (GH_Component)Activator.CreateInstance(componentType)!;
            var second = (GH_Component)Activator.CreateInstance(componentType)!;
            first.CreateAttributes(); second.CreateAttributes();
            var source = new Param_GenericObject { Hidden = true }; source.CreateAttributes();
            source.PersistentData.Append(new GH_Point(Point3d.Origin), new GH_Path(0));
            source.PersistentData.Append(new GH_Point(new Point3d(70, 0, 0)), new GH_Path(0));
            document.AddObject(source, false); document.AddObject(first, false); document.AddObject(second, false);
            first.Params.Input[0].AddSource(source); second.Params.Input[0].AddSource(source);
            document.NewSolution(false, GH_SolutionMode.Silent);
            var firstSession = PanelTestAdapter.Session(first); var secondSession = PanelTestAdapter.Session(second);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!(bool)firstSession.GetType().GetProperty("HasCurrentResult")!.GetValue(firstSession)! ||
                   !(bool)secondSession.GetType().GetProperty("HasCurrentResult")!.GetValue(secondSession)!)
            { if (DateTime.UtcNow > deadline) throw new TimeoutException("Cross-component action queries did not complete"); await Task.Delay(25); }
            var firstSnapshot = (PanelSnapshot)firstSession.GetType().GetProperty("Snapshot")!.GetValue(firstSession)!;
            var secondSnapshot = (PanelSnapshot)secondSession.GetType().GetProperty("Snapshot")!.GetValue(secondSession)!;
            var firstSource = Field<IGH_Goo?[]>(first, "_sourceItems");
            var custom = new StandalonePointPreview(); firstSource[0] = custom;
            string? newerFrame = null;
            custom.OnBounds = () => newerFrame = (string?)Method(second, "FrameRows").Invoke(second, new object[] { secondSnapshot, new[] { 1 } });
            string? olderFrame = (string?)Method(first, "FrameRows").Invoke(first, new object[] { firstSnapshot, new[] { 0 } });
            Check(olderFrame != null && olderFrame.Contains("cancelled") && newerFrame == null &&
                rhino.Views.ActiveView.ActiveViewport.CameraTarget.DistanceTo(new Point3d(70, 0, 0)) < 1e-8,
                "a newer frame from another component supersedes the reentrant older frame and retains its camera target");

            using var curve = new LineCurve(Point3d.Origin, new Point3d(2, 0, 0));
            Check(curve.UserData.Add(new CopyRoundTripUserData { Value = 19 }), "cross-component copy callback metadata is attached");
            firstSource[0] = new GH_Curve { Value = curve };
            Forms.Clipboard.SetText("old cross-component clipboard");
            bool newerCopied = false;
            CopyRoundTripUserData.OnDuplicateAction = () =>
            {
                object?[] nested = { secondSnapshot, new[] { 1 }, null };
                newerCopied = (bool)Method(second, "TryCopyGeometry").Invoke(second, nested)!;
            };
            object?[] outer = { firstSnapshot, new[] { 0 }, null };
            bool olderCopied = (bool)Method(first, "TryCopyGeometry").Invoke(first, outer)!;
            using var archive = ClipboardArchive();
            var copiedPoint = archive.Objects.Select(item => item.Geometry).OfType<Rhino.Geometry.Point>().Single();
            Check(newerCopied && !olderCopied && ((string)outer[2]!).Contains("cancelled") && copiedPoint.Location == new Point3d(70, 0, 0),
                "a newer copy from another component remains on the clipboard after an older duplicate callback returns");

            var actionType = assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!;
            var clipboardGate = actionType.GetMethod("CommitClipboard", Static)!;
            string? deferredStatus = null, initialStatus = null;
            Action olderWriter = () =>
            {
                object?[] nested = { secondSnapshot, new[] { 1 }, (Action<string>)(value => deferredStatus = value), null };
                Method(second, "TryCopyGeometryWithCompletion").Invoke(second, nested);
                initialStatus = (string?)nested[3];
                Forms.Clipboard.SetText("older writer returned last");
            };
            clipboardGate.Invoke(null, new object?[] { (Func<bool>)(() => true), olderWriter, null });
            using (var currentArchive = ClipboardArchive())
            {
                var point = currentArchive.Objects.Select(item => item.Geometry).OfType<Rhino.Geometry.Point>().Single();
                Check(initialStatus == "Copy queued." && deferredStatus != null && deferredStatus.Contains("geometries copied") &&
                    point.Location == new Point3d(70, 0, 0),
                    "a copy reentered inside the older clipboard writer is deferred and its data and status ultimately win");
            }
            string? firstDeferred = null, secondDeferred = null;
            olderWriter = () =>
            {
                object?[] firstQueued = { secondSnapshot, new[] { 1 }, (Action<string>)(value => firstDeferred = value), null };
                object?[] secondQueued = { secondSnapshot, new[] { 1 }, (Action<string>)(value => secondDeferred = value), null };
                Method(second, "TryCopyGeometryWithCompletion").Invoke(second, firstQueued);
                Method(second, "TryCopyGeometryWithCompletion").Invoke(second, secondQueued);
                Forms.Clipboard.SetText("older generation writer");
            };
            clipboardGate.Invoke(null, new object?[] { (Func<bool>)(() => true), olderWriter, null });
            using (var currentArchive = ClipboardArchive())
            {
                Check(firstDeferred != null && firstDeferred.Contains("cancelled") && secondDeferred != null &&
                    secondDeferred.Contains("geometries copied") && currentArchive.Objects.Count == 1,
                    "a newer queued clipboard generation cancels the prior queued status and publishes last");
            }

            var secondSource = Field<IGH_Goo?[]>(second, "_sourceItems");
            var mutablePoint = (GH_Point)secondSource[1]!;
            Point3d original = mutablePoint.Value;
            deferredStatus = null;
            olderWriter = () =>
            {
                object?[] nested = { secondSnapshot, new[] { 1 }, (Action<string>)(value => deferredStatus = value), null };
                Method(second, "TryCopyGeometryWithCompletion").Invoke(second, nested);
                mutablePoint.Value = new Point3d(90, 0, 0);
                Forms.Clipboard.SetText("older source mutation");
            };
            clipboardGate.Invoke(null, new object?[] { (Func<bool>)(() => true), olderWriter, null });
            Check(deferredStatus != null && deferredStatus.Contains("cancelled") && Forms.Clipboard.GetText() == "older source mutation",
                "a queued copy rechecks the captured source graph when dispatched");
            mutablePoint.Value = original;

            var secondSelection = Property<object>(second, "RowSelection");
            var secondResult = (PanelQueryResult)secondSession.GetType().GetProperty("Result")!.GetValue(secondSession)!;
            deferredStatus = null;
            olderWriter = () =>
            {
                object?[] nested = { secondSnapshot, new[] { 1 }, (Action<string>)(value => deferredStatus = value), null };
                Method(second, "TryCopyGeometryWithCompletion").Invoke(second, nested);
                Method(secondSelection, "SelectAt").Invoke(secondSelection, new object[] { secondResult, 1, 1, true, false, false });
                Forms.Clipboard.SetText("older selection mutation");
            };
            clipboardGate.Invoke(null, new object?[] { (Func<bool>)(() => true), olderWriter, null });
            Check(deferredStatus != null && deferredStatus.Contains("cancelled") && Forms.Clipboard.GetText() == "older selection mutation",
                "a queued copy rechecks the selection generation when dispatched");

            double tolerance = rhino.ModelAbsoluteTolerance;
            deferredStatus = null;
            try
            {
                olderWriter = () =>
                {
                    object?[] nested = { secondSnapshot, new[] { 1 }, (Action<string>)(value => deferredStatus = value), null };
                    Method(second, "TryCopyGeometryWithCompletion").Invoke(second, nested);
                    rhino.ModelAbsoluteTolerance = tolerance * 2;
                    Forms.Clipboard.SetText("older document mutation");
                };
                clipboardGate.Invoke(null, new object?[] { (Func<bool>)(() => true), olderWriter, null });
                Check(deferredStatus != null && deferredStatus.Contains("cancelled") && Forms.Clipboard.GetText() == "older document mutation",
                    "a queued copy rechecks Rhino document settings when dispatched");
            }
            finally { rhino.ModelAbsoluteTolerance = tolerance; }

            var viewport = rhino.Views.ActiveView.ActiveViewport;
            var frameGate = actionType.GetMethod("CommitFrame", Static)!;
            string? frameStatus = null, queuedFrame = null;
            Action olderCamera = () =>
            {
                queuedFrame = (string?)Method(second, "FrameRowsWithCompletion").Invoke(second,
                    new object?[] { secondSnapshot, new[] { 1 }, (Action<string>)(value => frameStatus = value) });
                viewport.SetCameraTarget(new Point3d(-50, 0, 0), true);
            };
            frameGate.Invoke(null, new object?[] { viewport.Id, (Func<bool>)(() => true), olderCamera, null });
            Check(queuedFrame == "Frame queued." && frameStatus == "Selection framed" &&
                viewport.CameraTarget.DistanceTo(new Point3d(70, 0, 0)) < 1e-8,
                "a frame reentered inside the older camera writer ultimately wins with its completion status");
            frameStatus = null;
            try
            {
                olderCamera = () =>
                {
                    Method(second, "FrameRowsWithCompletion").Invoke(second,
                        new object?[] { secondSnapshot, new[] { 1 }, (Action<string>)(value => frameStatus = value) });
                    viewport.LockedProjection = true;
                };
                frameGate.Invoke(null, new object?[] { viewport.Id, (Func<bool>)(() => true), olderCamera, null });
                Check(frameStatus != null && frameStatus.Contains("cancelled") && viewport.CameraTarget.DistanceTo(new Point3d(70, 0, 0)) < 1e-8,
                    "a queued frame rechecks the viewport lock before its deferred publication");
            }
            finally { viewport.LockedProjection = false; }
        }
        finally
        {
            CopyRoundTripUserData.OnDuplicateAction = null;
            document.Properties.KeepOpen = false; document.PreviewMode = GH_PreviewMode.Disabled;
            if (prior == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(prior, true);
        }
    }

    private static void VerifyReferencedPointFreshness(Assembly assembly, object highlight, RhinoDoc rhino)
    {
        var prior = Forms.Clipboard.GetDataObject(); bool modified = rhino.Modified;
        Point3d value = new Point3d(4, 5, 6);
        Guid first = rhino.Objects.AddPoint(value), second = rhino.Objects.AddPoint(value);
        var point = new GH_Point(value) { ReferenceID = first };
        point.ReferenceType(GH_PointRefType.point_object);
        var observerField = highlight.GetType().GetField("_admissionObserver", Instance)!;
        try
        {
            Check(point.IsReferencedGeometry && point.IsGeometryLoaded && point.ReferenceID == first,
                "the point reference fixture starts with a loaded same-value source");
            var witnessType = assembly.GetType("BetterGhPanel.Runtime.CaptureGraphWitness", true)!;
            object?[] captureArgs = { point, 20000, null };
            bool captured = (bool)witnessType.GetMethod("TryCapture", Static)!.Invoke(null, captureArgs)!;
            var witness = captureArgs[2]!;
            Method(witness, "ResolveContexts").Invoke(witness, Array.Empty<object>());
            var isCurrent = witnessType.GetMethod("IsCurrent", Instance, null, Type.EmptyTypes, null)!;
            Check(captured && (bool)isCurrent.Invoke(witness, Array.Empty<object>())!,
                "the captured point witness accepts the original reference");
            point.ReferenceID = second;
            Check(point.Value == value && !(bool)isCurrent.Invoke(witness, Array.Empty<object>())!,
                "the captured point witness rejects a same-value reference retarget");
            point.ReferenceID = first;
            observerField.SetValue(highlight, (Action<int, object>)((_, __) => point.ReferenceID = second));
            Set(highlight, new IGH_Goo?[] { point }, new[] { 0 }, new[] { 0 });
            observerField.SetValue(highlight, null);
            Check(Count(highlight) == 0 && Status(highlight).Contains("partial") && point.Value == value,
                "preview admission rejects a same-value point retarget during construction");
            point.ReferenceID = first; Set(highlight, new IGH_Goo?[] { point }, new[] { 0 }, new[] { 0 });
            Check(Count(highlight) == 1 && Box(highlight).Contains(value), "an unchanged referenced point previews normally");

            var viewport = rhino.Views.ActiveView.ActiveViewport;
            var target = viewport.CameraTarget;
            var custom = new StandalonePointPreview();
            custom.OnBounds = () => point.ReferenceID = second;
            object framed = Frame(assembly, new IGH_Goo?[] { point, custom });
            Check(!Property<bool>(framed, "Success") && viewport.CameraTarget == target && point.Value == value,
                "a same-value point retarget during a later bounds callback cancels frame");
            point.ReferenceID = first;
            using var curve = new LineCurve(Point3d.Origin, new Point3d(3, 0, 0));
            Check(curve.UserData.Add(new CopyRoundTripUserData { Value = 11 }), "point-reference copy callback data is attached");
            Forms.Clipboard.SetText("keep point reference clipboard");
            CopyRoundTripUserData.DuplicateAt = 1; CopyRoundTripUserData.TriggeredGeneration = 0;
            CopyRoundTripUserData.OnDuplicateAction = () => point.ReferenceID = second;
            object copied = Copy(assembly, new IGH_Goo?[] { point, new GH_Curve { Value = curve } });
            Check(!Property<bool>(copied, "Success") && Forms.Clipboard.GetText() == "keep point reference clipboard" &&
                point.Value == value && CopyRoundTripUserData.TriggeredGeneration > 0,
                "a same-value point retarget during duplication cancels copy before publication");

            point.ReferenceID = first;
            uint serial = rhino.Objects.FindId(first).RuntimeSerialNumber;
            bool replaced = false;
            custom.OnBounds = () => replaced = rhino.Objects.Replace(first, value);
            target = viewport.CameraTarget;
            framed = Frame(assembly, new IGH_Goo?[] { point, custom });
            Check(replaced && rhino.Objects.FindId(first).RuntimeSerialNumber != serial && !Property<bool>(framed, "Success") &&
                viewport.CameraTarget == target && point.ReferenceID == first && point.Value == value,
                "replacing the referenced Rhino object under the same ID cancels frame");
            serial = rhino.Objects.FindId(first).RuntimeSerialNumber; replaced = false;
            CopyRoundTripUserData.TriggeredGeneration = 0;
            CopyRoundTripUserData.OnDuplicateAction = () => replaced = rhino.Objects.Replace(first, value);
            copied = Copy(assembly, new IGH_Goo?[] { point, new GH_Curve { Value = curve } });
            Check(replaced && rhino.Objects.FindId(first).RuntimeSerialNumber != serial && !Property<bool>(copied, "Success") &&
                Forms.Clipboard.GetText() == "keep point reference clipboard" && point.ReferenceID == first && point.Value == value,
                "replacing the referenced Rhino object under the same ID cancels copy");
        }
        finally
        {
            observerField.SetValue(highlight, null); CopyRoundTripUserData.OnDuplicateAction = null;
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            rhino.Objects.Delete(first, true); rhino.Objects.Delete(second, true); rhino.Modified = modified;
            if (prior == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(prior, true);
        }
    }

    private static void VerifyLatePublicationContext(Assembly assembly, RhinoDoc rhino)
    {
        var previous = Forms.Clipboard.GetDataObject();
        var actionType = assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!;
        var resultType = assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActionResult", true)!;
        var completionType = typeof(Action<>).MakeGenericType(resultType);
        var viewport = rhino.Views.ActiveView.ActiveViewport;
        bool originalLock = viewport.LockedProjection;
        double tolerance = rhino.ModelAbsoluteTolerance;
        bool modified = rhino.Modified;
        try
        {
            viewport.LockedProjection = false;
            var item = new IGH_Goo?[] { new GH_Point(new Point3d(19, 0, 0)) };
            var normalFrame = Frame(assembly, item);
            Check(Property<bool>(normalFrame, "Success"), "unchanged frame publication context remains valid");
            var target = viewport.CameraTarget;
            bool dispatching = false, lockChanged = false;
            int checks = 0;
            object? frameCompletion = null, frameStarted = null;
            Action<object> frameDone = value => frameCompletion = value;
            var typedFrameDone = Delegate.CreateDelegate(completionType, frameDone.Target, frameDone.Method);
            Func<bool> frameCurrent = () =>
            {
                if (dispatching && ++checks == 4) { viewport.LockedProjection = true; lockChanged = true; }
                return true;
            };
            Action frameWriter = () =>
            {
                frameStarted = actionType.GetMethod("FrameWithCompletion", Static)!.Invoke(null,
                    new object?[] { new IGH_Goo?[] { new GH_Point(new Point3d(90, 0, 0)) }, frameCurrent, typedFrameDone });
                dispatching = true;
            };
            actionType.GetMethod("CommitFrame", Static)!.Invoke(null,
                new object?[] { viewport.Id, (Func<bool>)(() => true), frameWriter, null });
            Check(frameStarted != null && Property<bool>(frameStarted, "Deferred") && lockChanged && frameCompletion != null &&
                !Property<bool>(frameCompletion, "Success") && Property<string>(frameCompletion, "Error").Contains("cancelled") && viewport.CameraTarget == target,
                $"a deferred frame rechecks a viewport lock changed by its final source predicate (checks={checks})");
            viewport.LockedProjection = false;

            var normalCopy = Copy(assembly, item);
            Check(Property<bool>(normalCopy, "Success"), "unchanged clipboard publication context remains valid");
            Forms.Clipboard.SetText("keep late-context clipboard");
            dispatching = false; checks = 0;
            bool toleranceChanged = false;
            object? copyCompletion = null, copyStarted = null;
            Action<object> copyDone = value => copyCompletion = value;
            var typedCopyDone = Delegate.CreateDelegate(completionType, copyDone.Target, copyDone.Method);
            Func<bool> copyCurrent = () =>
            {
                if (dispatching && ++checks == 4) { rhino.ModelAbsoluteTolerance = tolerance * 2; toleranceChanged = true; }
                return true;
            };
            Action clipboardWriter = () =>
            {
                copyStarted = actionType.GetMethod("CopyGeometryWithCompletion", Static)!.Invoke(null,
                    new object?[] { item, rhino.ModelUnitSystem, tolerance, copyCurrent, typedCopyDone });
                dispatching = true;
            };
            actionType.GetMethod("CommitClipboard", Static)!.Invoke(null,
                new object?[] { (Func<bool>)(() => true), clipboardWriter, null });
            Check(copyStarted != null && Property<bool>(copyStarted, "Deferred") && toleranceChanged && copyCompletion != null &&
                !Property<bool>(copyCompletion, "Success") && Property<string>(copyCompletion, "Error").Contains("cancelled") && Forms.Clipboard.GetText() == "keep late-context clipboard",
                $"a deferred copy rechecks tolerance changed by its final source predicate (checks={checks})");
        }
        finally
        {
            viewport.LockedProjection = originalLock;
            rhino.ModelAbsoluteTolerance = tolerance; rhino.Modified = modified;
            if (previous == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(previous, true);
        }
    }

    private static async Task VerifyUnsupportedGeometry(Assembly assembly, object highlight, RhinoDoc rhino)
    {
        var prior = Forms.Clipboard.GetDataObject();
        bool modified = rhino.Modified;
        var originalIds = rhino.Objects.Select(item => item.Id).ToHashSet();
        var invalid = new GH_ObjectWrapper(new Polyline(new[] { Point3d.Origin, Point3d.Origin }));
        var unknown = new GH_ObjectWrapper(new object());
        var values = new IGH_Goo?[] { invalid, unknown };
        try
        {
            for (int i = 0; i < values.Length; i++)
            {
                Set(highlight, new[] { values[i] }, new[] { 0 }, new[] { 0 });
                Check(Count(highlight) == 0 && Status(highlight).Contains("partial") && !Bounds(assembly, values[i]!).IsValid,
                    "unsupported standalone geometry " + i + " reports partial preview and unavailable bounds");
                object framed = Frame(assembly, new IGH_Goo?[] { values[i], new GH_Point(new Point3d(10, 0, 0)) });
                Check(Property<bool>(framed, "Success") && Property<int>(framed, "Skipped") == 1,
                    "frame visibly skips unsupported standalone geometry " + i);
                object copied = Copy(assembly, new IGH_Goo?[] { values[i], new GH_Point(new Point3d(10, 0, 0)) });
                Check(Property<bool>(copied, "Success") && Property<int>(copied, "Count") == 1 && Property<int>(copied, "Skipped") == 1,
                    "copy visibly skips unsupported standalone geometry " + i);
            }
            var group = new GH_GeometryGroup(); group.Objects.Add(new GH_Point(Point3d.Origin)); group.Objects.Add(new HostileCurveHolder());
            HostileCurveHolder.Calls = 0;
            Set(highlight, new IGH_Goo?[] { group }, new[] { 0 }, new[] { 0 });
            Check(Count(highlight) == 0 && Status(highlight).Contains("partial") && !Bounds(assembly, group).IsValid && HostileCurveHolder.Calls == 0,
                "a group with an unsupported hostile child is not silently admitted or dispatched");
            object groupFrame = Frame(assembly, new IGH_Goo?[] { group, new GH_Point(new Point3d(10, 0, 0)) });
            object groupCopy = Copy(assembly, new IGH_Goo?[] { group, new GH_Point(new Point3d(10, 0, 0)) });
            Check(Property<bool>(groupFrame, "Success") && Property<int>(groupFrame, "Skipped") == 1 &&
                Property<bool>(groupCopy, "Success") && Property<int>(groupCopy, "Skipped") == 1 && HostileCurveHolder.Calls == 0,
                "frame and copy report the skipped group and preserve the separate supported point");
            Check(RhinoApp.RunScript(rhino.RuntimeSerialNumber, "_Paste", false), "Rhino accepts the partial-action clipboard");
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!rhino.Objects.Any(item => !originalIds.Contains(item.Id)))
            { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Partial-action Paste did not add its point."); await Task.Delay(25); }
            var pasted = rhino.Objects.Where(item => !originalIds.Contains(item.Id)).ToArray();
            Check(pasted.Length == 1 && pasted[0].Geometry is Rhino.Geometry.Point point && point.Location == new Point3d(10, 0, 0),
                "a partial action pastes only the explicitly supported point");
            var tooLong = new GH_ObjectWrapper(new Polyline(Enumerable.Range(0, 32769).Select(i => new Point3d(i, 0, 0))));
            Forms.Clipboard.SetText("keep oversized clipboard");
            Set(highlight, new IGH_Goo?[] { tooLong }, new[] { 0 }, new[] { 0 });
            object oversized = Copy(assembly, new IGH_Goo?[] { tooLong, new GH_Point(Point3d.Origin) });
            Check(Count(highlight) == 0 && Status(highlight).Contains("partial") && !Property<bool>(oversized, "Success") &&
                Property<bool>(oversized, "Exhausted") && Forms.Clipboard.GetText() == "keep oversized clipboard",
                "an oversized wrapped polyline exhausts the action visibly without replacing the clipboard");
        }
        finally
        {
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            foreach (var item in rhino.Objects.Where(item => !originalIds.Contains(item.Id)).ToArray()) rhino.Objects.Delete(item.Id, true);
            rhino.Modified = modified;
            if (prior == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(prior, true);
        }
    }

    private static void VerifyDenseSelectionGestures(Assembly assembly, object highlight)
    {
        const int count = 5000;
        var snapshot = new PanelSnapshot(Enumerable.Range(0, count)
            .Select(i => new PanelRow(i, "{0}", "Point", i.ToString(), null)).ToArray(), new[] { new PanelBranch("{0}", count) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        var source = Enumerable.Range(0, count).Select(i => (IGH_Goo?)new GH_Point(new Point3d(i, 0, 0))).ToArray();
        var selectionType = assembly.GetType("BetterGhPanel.UI.PanelRowSelection", true)!;
        foreach (bool filtered in new[] { false, true })
        foreach (bool labels in new[] { false, true })
        {
            int visits = 0;
            var selection = Activator.CreateInstance(selectionType, Instance, null, new object[] { (Action)(() => visits++) }, null)!;
            Method(selection, "SelectAll").Invoke(selection, new object?[] { result, null, false });
            var ordered = (IReadOnlyList<int>)Method(selection, "OrderedPositions").Invoke(selection, new object[] { result })!;
            Method(highlight, "SetOrderResult").Invoke(highlight, new object?[] { labels ? result : null });
            Method(highlight, "SetScopeChecked").Invoke(highlight, new object[] { source, result, filtered, ordered, (Func<bool>)(() => true), selection });
            visits = 0;
            Method(selection, "SelectAt").Invoke(selection, new object[] { result, 4000, 4000, true, false, false });
            ordered = (IReadOnlyList<int>)Method(selection, "OrderedPositions").Invoke(selection, new object[] { result })!;
            Method(highlight, "SetScopeChecked").Invoke(highlight, new object[] { source, result, filtered, ordered, (Func<bool>)(() => true), selection });
            Check(visits <= 2 && Property<int>(highlight, "LastVisitedRows") <= 1 && ordered.Count == count - 1,
                "dense deselection avoids full-result ordering and preview visits in both preview modes and label settings");
            visits = 0;
            Method(selection, "SelectAt").Invoke(selection, new object[] { result, 4000, 4000, true, false, false });
            ordered = (IReadOnlyList<int>)Method(selection, "OrderedPositions").Invoke(selection, new object[] { result })!;
            Method(highlight, "SetScopeChecked").Invoke(highlight, new object[] { source, result, filtered, ordered, (Func<bool>)(() => true), selection });
            Check(visits <= 2 && Property<int>(highlight, "LastVisitedRows") <= 1 && ordered.Count == count,
                "reselecting one dense member updates only that preview classification");
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
        }
    }

    internal static async Task RunReentrantDraw(Assembly assembly)
    {
        var rhino = RhinoDoc.ActiveDoc ?? RhinoDoc.Create(null);
        bool modified = rhino.Modified;
        var previous = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Reentrant preview fixture", Rhino.Display.DefinedViewportProjection.Top,
            new Rectangle(0, 0, 640, 480), false);
        using var document = new GH_Document { Enabled = true };
        try
        {
            rhino.Views.ActiveView = view;
            document.Properties.KeepOpen = true;
            document.PreviewMeshType = GH_PreviewMesh.LowQuality;
            var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
            component.CreateAttributes(); document.AddObject(component, false);
            object highlight = Field<object>(component, "_highlight");
            using var conduit = new PreviewConduit(component, document) { Enabled = true };
            using var polyline = new PolylineCurve(new[] { Point3d.Origin, new Point3d(2, 0, 0), new Point3d(2, 3, 0) });
            var group = new GH_GeometryGroup(); group.Objects.Add(new GH_Curve { Value = polyline });
            var custom = new StandalonePointPreview();
            Set(highlight, new IGH_Goo?[] { group, custom }, new[] { 0, 1 }, new[] { 0, 1 });
            var ownedCurve = OwnedGeometry(highlight).OfType<PolylineCurve>().Single();
            bool wireCalled = false, wireHeld = false;
            custom.OnWire = () =>
            {
                wireCalled = true;
                Method(component, "ClearHighlight").Invoke(component, Array.Empty<object>());
                wireHeld = !ownedCurve.Disposed;
            };
            view.Redraw();
            using (var bitmap = CapturePreview(view, highlight, "reentrant wire", conduit)) { }
            await WaitForPreviewRetirement(highlight);
            Check(wireCalled && wireHeld && ownedCurve.Disposed && Count(highlight) == 0,
                $"clearing during a standalone wire callback keeps grouped geometry alive until the draw exits " +
                $"(called={wireCalled}, held={wireHeld}, disposed={ownedCurve.Disposed}, count={Count(highlight)}, draws={conduit.Draws})");

            using var brep = new Box(new BoundingBox(Point3d.Origin, new Point3d(2, 3, 4))).ToBrep();
            group = new GH_GeometryGroup(); group.Objects.Add(new GH_Brep { Value = brep });
            Set(highlight, new IGH_Goo?[] { group, custom }, new[] { 0, 1 }, new[] { 0, 1 });
            var ownedBrep = OwnedGeometry(highlight).OfType<Brep>().Single();
            var live = assembly.GetType("BetterGhPanel.Runtime.PanelPreviewLeaf", true)!.GetProperty("LiveGeneratedMeshes", Static)!;
            bool meshCalled = false, meshHeld = false;
            custom.OnMesh = () =>
            {
                meshCalled = true;
                document.PreviewMeshType = GH_PreviewMesh.HighQuality;
                Set(highlight, new IGH_Goo?[] { new GH_Point(new Point3d(50, 0, 0)) }, new[] { 0 }, new[] { 0 });
                component.ExpirePreview(false);
                meshHeld = !ownedBrep.Disposed && (int)live.GetValue(null)! > 0;
            };
            view.Redraw();
            using (var bitmap = CapturePreview(view, highlight, "reentrant mesh", conduit)) { }
            await WaitForPreviewRetirement(highlight);
            Check(meshCalled && meshHeld && ownedBrep.Disposed && (int)live.GetValue(null)! == 0 &&
                Count(highlight) == 1 && Box(highlight).Contains(new Point3d(50, 0, 0)),
                $"meshing reset and replacement defer grouped Brep and generated mesh disposal until shading exits " +
                $"(called={meshCalled}, held={meshHeld}, disposed={ownedBrep.Disposed}, live={(int)live.GetValue(null)!}, " +
                $"count={Count(highlight)}, bounds={Box(highlight)}, draws={conduit.Draws})");
            Method(component, "ClearHighlight").Invoke(component, Array.Empty<object>());

            var originalPoint = new GH_Point(new Point3d(5, 0, 0));
            var wrapper = new GH_GeometricGooWrapper(originalPoint);
            var wrappedValue = typeof(GH_GeometricGooWrapper).GetField("m_goo", Instance)!;
            var capturedGroup = new GH_GeometryGroup(); capturedGroup.Objects.Add(new GH_Curve { Value = polyline }); capturedGroup.Objects.Add(wrapper);
            GeometryBase? capturedOwned = null;
            bool replacedAndRestored = false;
            Action<int, object> observer = (index, leaf) =>
            {
                if (index != 0) return;
                capturedOwned = Field<GeometryBase?>(leaf, "_owned");
                wrappedValue.SetValue(wrapper, new GH_Point(new Point3d(300, 0, 0)));
                capturedGroup.Objects[1] = new GH_Point(new Point3d(400, 0, 0));
                capturedGroup.Objects[1] = wrapper; wrappedValue.SetValue(wrapper, originalPoint);
                replacedAndRestored = true;
            };
            highlight.GetType().GetField("_admissionObserver", Instance)!.SetValue(highlight, observer);
            Set(highlight, new IGH_Goo?[] { capturedGroup }, new[] { 0 }, new[] { 0 });
            highlight.GetType().GetField("_admissionObserver", Instance)!.SetValue(highlight, null);
            Check(replacedAndRestored && capturedOwned != null && !capturedOwned.Disposed && Count(highlight) == 1 &&
                Box(highlight).Contains(new Point3d(5, 0, 0)) && !Box(highlight).Contains(new Point3d(300, 0, 0)),
                "preview construction uses captured leaves after a group replacement and wrapper retarget are restored");
            var entries = (IEnumerable)Field<object>(highlight, "_entries");
            var pair = entries.Cast<object>().Single();
            var entry = pair.GetType().GetProperty("Value")!.GetValue(pair)!;
            var leaves = ((IEnumerable)Field<object>(entry, "Leaves")).Cast<object>().ToArray();
            Check(Field<object>(leaves[1], "_value") is Point3d point && point == new Point3d(5, 0, 0),
                "the drawable point and captured bounds come from the same graph generation");
            component.ExpirePreview(false);
            Method(document, "CreatePreviewCaches").Invoke(document, Array.Empty<object>());
            Check(document.PreviewBoundingBox.Contains(new Point3d(5, 0, 0)) &&
                !document.PreviewBoundingBox.Contains(new Point3d(300, 0, 0)), "host bounds refresh to the captured generation");
            int previousDraws = conduit.Draws; view.Redraw();
            using (var bitmap = CapturePreview(view, highlight, "reentrant captured generation", conduit)) { }
            Check(conduit.Draws > previousDraws, "the captured generation reaches the actual viewport draw callback");
            Method(component, "ClearHighlight").Invoke(component, Array.Empty<object>());
            await WaitForPreviewRetirement(highlight);
            Check(capturedOwned!.Disposed && !Box(highlight).IsValid, "clearing the captured generation disposes its owned geometry");
        }
        finally
        {
            document.Properties.KeepOpen = false; document.PreviewMode = GH_PreviewMode.Disabled;
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close(); rhino.Modified = modified;
        }
    }

    private static IEnumerable<GeometryBase> OwnedGeometry(object highlight)
    {
        foreach (object pair in (IEnumerable)Field<object>(highlight, "_entries"))
        {
            object entry = pair.GetType().GetProperty("Value")!.GetValue(pair)!;
            foreach (object leaf in (IEnumerable)Field<object>(entry, "Leaves"))
                if (Field<object?>(leaf, "_owned") is GeometryBase owned) yield return owned;
        }
    }

    private static IEnumerable<Mesh> OwnedBoxMeshes(object highlight)
    {
        foreach (object pair in (IEnumerable)Field<object>(highlight, "_entries"))
        {
            object entry = pair.GetType().GetProperty("Value")!.GetValue(pair)!;
            foreach (object leaf in (IEnumerable)Field<object>(entry, "Leaves"))
                if (Field<object?>(leaf, "_boxMesh") is Mesh mesh) yield return mesh;
        }
    }

    private static async Task WaitForPreviewRetirement(object highlight)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (Property<bool>(highlight, "RetirementPending"))
        { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Preview resources did not retire on Rhino idle."); await Task.Delay(25); }
    }

    private static Bitmap CapturePreview(Rhino.Display.RhinoView view, object highlight, string phase, PreviewConduit? conduit)
    {
        string State() => $"{phase}: entries={Count(highlight)}, bounds={Box(highlight)}, bytes={Property<long>(highlight, "RetainedBytes")}, " +
            $"wire={Property<int>(highlight, "DrawWireCalls")}, mesh={Property<int>(highlight, "DrawMeshCalls")}, " +
            $"retirementPending={Property<bool>(highlight, "RetirementPending")}, idleReleases={Property<int>(highlight, "IdleReleases")}, " +
            $"conduit={conduit?.Draws ?? 0}, mesh callbacks={conduit?.MeshesCompleted ?? 0}/{conduit?.MeshesStarted ?? 0}, " +
            $"wire callbacks={conduit?.WiresCompleted ?? 0}/{conduit?.WiresStarted ?? 0}, status={Status(highlight)}";
        Console.WriteLine("Before preview capture: " + State());
        Console.Out.Flush();
        try { return Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480); }
        catch (Exception ex) { Console.WriteLine("Preview capture failed: " + State() + "; " + ex); Console.Out.Flush(); throw; }
    }

    // This is an explicit handler seam, not evidence that Rhino's real idle
    // event is an end-of-frame notification. The separate idle counter reports
    // what this embedded WinForms host actually delivered.
    internal static async Task RunRetirementSeam(Assembly assembly)
    {
        var rhino = RhinoDoc.ActiveDoc ?? RhinoDoc.Create(null);
        bool modified = rhino.Modified;
        bool meshEdges = Grasshopper.CentralSettings.PreviewMeshEdges;
        var previous = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Preview retirement seam", Rhino.Display.DefinedViewportProjection.Top,
            new Rectangle(0, 0, 640, 480), false);
        using var document = new GH_Document { Enabled = true };
        using var brep = new Box(new BoundingBox(new Point3d(-2, -2, -1), new Point3d(2, 2, 1))).ToBrep();
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        using var conduit = new PreviewConduit(component, document);
        int idleInCapture = 0, idleAfterCapture = 0;
        bool captureActive = false;
        Rhino.Display.RhinoViewport? capturedViewport = null;
        EventHandler observeIdle = (_, _) => { if (captureActive) idleInCapture++; else idleAfterCapture++; };
        RhinoApp.Idle += observeIdle;
        try
        {
            Grasshopper.CentralSettings.PreviewMeshEdges = true;
            rhino.Views.ActiveView = view;
            view.ActiveViewport.ZoomBoundingBox(new BoundingBox(new Point3d(-4, -4, -2), new Point3d(4, 4, 2)));
            document.Properties.KeepOpen = true; document.PreviewMode = GH_PreviewMode.Shaded;
            document.AddObject(component, false);
            object highlight = Field<object>(component, "_highlight");
            var leafType = assembly.GetType("BetterGhPanel.Runtime.PanelPreviewLeaf", true)!;
            var live = leafType.GetProperty("LiveGeneratedMeshes", Static)!;
            var holder = new GH_Brep { Value = brep };
            var group = new GH_GeometryGroup(); group.Objects.Add(holder);
            GeometryBase? wireOwned = null, meshOwned = null, resetOwned = null;
            Mesh[] resetMeshes = Array.Empty<Mesh>();
            bool wireCalled = false, wireGuarded = false, wireFrameHeld = false;
            bool meshCalled = false, meshGuarded = false, meshFrameHeld = false, resetHeld = false;
            bool postCallbackChecks = false;
            long heldBytes = 0;
            string? error = null;
            conduit.OnDraw = args =>
            {
                try
                {
                    capturedViewport = new Rhino.Display.RhinoViewport(args.Viewport);
                    VerifyPostCallbackRetirement(component, highlight, args);
                    VerifyRejectedMeshing(component, highlight, args);
                    VerifyFailedDisplayFlush(component, highlight, args);
                    postCallbackChecks = true;
                    var customWire = new StandalonePointPreview();
                    Set(highlight, new IGH_Goo?[] { group, customWire }, new[] { 0, 1 }, new[] { 0, 1 });
                    wireOwned = OwnedGeometry(highlight).OfType<Brep>().Single();
                    customWire.OnWire = () =>
                    {
                        wireCalled = true;
                        Method(component, "ClearHighlight").Invoke(component, Array.Empty<object>());
                        int calls = Property<int>(highlight, "DrawWireCalls");
                        Method(highlight, "ReleaseOnIdle").Invoke(highlight, new object?[] { null, EventArgs.Empty });
                        component.DrawViewportWires(args);
                        wireGuarded = !wireOwned.Disposed && Property<bool>(highlight, "RetirementPending") &&
                            Property<int>(highlight, "DrawWireCalls") == calls;
                    };
                    component.DrawViewportWires(args);
                    wireFrameHeld = !wireOwned.Disposed;

                    Set(highlight, new IGH_Goo?[] { group }, new[] { 0 }, new[] { 0 });
                    resetOwned = OwnedGeometry(highlight).OfType<Brep>().Single();
                    document.PreviewMeshType = GH_PreviewMesh.LowQuality;
                    component.DrawViewportMeshes(args);
                    var firstEntry = ((IEnumerable)Field<object>(highlight, "_entries")).Cast<object>().Single();
                    object firstValue = firstEntry.GetType().GetProperty("Value")!.GetValue(firstEntry)!;
                    object firstLeaf = ((IEnumerable)Field<object>(firstValue, "Leaves")).Cast<object>().Single();
                    resetMeshes = Field<Mesh[]?>(firstLeaf, "_generatedMeshes") ?? Array.Empty<Mesh>();
                    document.PreviewMeshType = GH_PreviewMesh.HighQuality;
                    component.DrawViewportMeshes(args);
                    resetHeld = resetMeshes.Length > 0 && resetMeshes.All(mesh => !mesh.Disposed) &&
                        !resetOwned.Disposed && Property<bool>(highlight, "RetirementPending");

                    var customMesh = new StandalonePointPreview();
                    Set(highlight, new IGH_Goo?[] { group, customMesh }, new[] { 0, 1 }, new[] { 0, 1 });
                    meshOwned = OwnedGeometry(highlight).OfType<Brep>().Single();
                    customMesh.OnMesh = () =>
                    {
                        meshCalled = true;
                        int calls = Property<int>(highlight, "DrawMeshCalls");
                        component.DocumentContextChanged(document, GH_DocumentContext.Close);
                        Method(highlight, "ReleaseOnIdle").Invoke(highlight, new object?[] { null, EventArgs.Empty });
                        component.DrawViewportMeshes(args);
                        heldBytes = Property<long>(highlight, "RetainedBytes");
                        meshGuarded = !wireOwned.Disposed && !resetOwned.Disposed && !meshOwned.Disposed &&
                            resetMeshes.All(mesh => !mesh.Disposed) && (int)live.GetValue(null)! > 0 &&
                            Property<bool>(highlight, "RetirementPending") && Property<int>(highlight, "DrawMeshCalls") == calls &&
                            heldBytes > 0 && heldBytes <= 16L * 1024 * 1024;
                    };
                    component.DrawViewportMeshes(args);
                    meshFrameHeld = !wireOwned.Disposed && !resetOwned.Disposed && !meshOwned.Disposed &&
                        resetMeshes.All(mesh => !mesh.Disposed);
                }
                catch (Exception ex) { error = ex.ToString(); }
            };
            conduit.Enabled = true;
            captureActive = true;
            using var bitmap = CapturePreview(view, highlight, "retirement seam", conduit);
            captureActive = false;
            Check(error == null && bitmap != null && GreenPixels(bitmap) > 0,
                "retirement seam executes an actual shaded draw: " + error);
            Check(postCallbackChecks, "post-component retirement, negative meshing cache and failed-flush regressions execute inside the enclosing display callback");
            int regionIndex = 0;
            foreach (var region in ConsumptionRegions())
                Check(RegionColors(bitmap!, capturedViewport!, region, Color.Lime, Color.Cyan),
                    "consumption region " + regionIndex++ + " retains shading and wires after in-callback retirement and native capture return");
            Check(wireCalled && wireGuarded && wireFrameHeld && wireOwned != null && !wireOwned.Disposed,
                "simulated idle and nested wire draw cannot release a cleared generation during its active callback");
            Check(resetHeld && meshCalled && meshGuarded && meshFrameHeld && meshOwned != null && !meshOwned.Disposed,
                "mesh reset and document close retain drawn geometry through callback and capture return, including simulated reentrant idle");
            int releases = Property<int>(highlight, "IdleReleases");
            Method(highlight, "ReleaseOnIdle").Invoke(highlight, new object?[] { null, EventArgs.Empty });
            Check(wireOwned!.Disposed && resetOwned!.Disposed && meshOwned!.Disposed && resetMeshes.All(mesh => mesh.Disposed) &&
                (int)live.GetValue(null)! == 0 && Property<long>(highlight, "RetainedBytes") == 0 &&
                !Property<bool>(highlight, "RetirementPending") && Property<int>(highlight, "IdleReleases") == releases + 1,
                "the explicit post-capture retirement seam releases all retired resources and clears its pending subscription");
            Method(highlight, "ReleaseOnIdle").Invoke(highlight, new object?[] { null, EventArgs.Empty });
            Check((int)live.GetValue(null)! == 0 && Property<long>(highlight, "RetainedBytes") == 0,
                "repeated retirement does not dispose or account for the same resources twice");
            await Task.Delay(1000);
            Console.WriteLine($"RhinoApp.Idle observer: during capture={idleInCapture}, after capture={idleAfterCapture}; " +
                "this is host evidence only, not an end-of-frame guarantee.");
        }
        finally
        {
            RhinoApp.Idle -= observeIdle;
            capturedViewport?.Dispose();
            conduit.Enabled = false;
            Grasshopper.CentralSettings.PreviewMeshEdges = meshEdges;
            document.Properties.KeepOpen = false; document.PreviewMode = GH_PreviewMode.Disabled;
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close(); rhino.Modified = modified;
        }
    }

    private static void VerifyReentrancy(Assembly assembly, object highlight)
    {
        var previous = Forms.Clipboard.GetDataObject();
        try
        {
            var replacement = new GH_Point(new Point3d(80, 0, 0));
            var custom = new StandalonePointPreview();
            custom.OnBounds = () => Set(highlight, new IGH_Goo?[] { replacement }, new[] { 0 }, new[] { 0 });
            Set(highlight, new IGH_Goo?[] { custom }, new[] { 0 }, new[] { 0 });
            Check(Count(highlight) == 1 && Box(highlight).Contains(new Point3d(80, 0, 0)) &&
                !Box(highlight).Contains(new Point3d(7, 0, 0)),
                "a reentrant preview callback cannot publish the superseded generation");

            bool current = true;
            custom.OnBounds = () => current = false;
            var viewport = RhinoDoc.ActiveDoc!.Views.ActiveView.ActiveViewport;
            var target = viewport.CameraTarget;
            object frame = assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!.GetMethod("FrameChecked", Static)!
                .Invoke(null, new object[] { new IGH_Goo?[] { custom }, (Func<bool>)(() => current) })!;
            Check(!Property<bool>(frame, "Success") && Property<string>(frame, "Error").Contains("cancelled") &&
                viewport.CameraTarget == target, "a reentrant bounds callback cancels framing before camera publication");

            var doc = RhinoDoc.ActiveDoc!;
            var firstView = doc.Views.ActiveView;
            var secondView = doc.Views.Add("Reentrant frame target", Rhino.Display.DefinedViewportProjection.Top,
                new Rectangle(0, 0, 320, 240), false);
            try
            {
                doc.Views.ActiveView = firstView;
                target = viewport.CameraTarget;
                var otherTarget = secondView.ActiveViewport.CameraTarget;
                custom.OnBounds = () => doc.Views.ActiveView = secondView;
                frame = Frame(assembly, new IGH_Goo?[] { custom });
                Check(!Property<bool>(frame, "Success") && Property<string>(frame, "Error").Contains("cancelled") &&
                    viewport.CameraTarget == target && secondView.ActiveViewport.CameraTarget == otherTarget,
                    "a callback switching the active view cancels without moving either camera");

                doc.Views.ActiveView = firstView;
                custom.OnBounds = () => viewport.LockedProjection = true;
                frame = Frame(assembly, new IGH_Goo?[] { custom });
                Check(!Property<bool>(frame, "Success") && Property<string>(frame, "Error").Contains("cancelled") &&
                    viewport.CameraTarget == target, "a callback locking the target viewport cancels before camera motion");
                viewport.LockedProjection = false;
                frame = Frame(assembly, new IGH_Goo?[] { new GH_Point(new Point3d(120, 0, 0)) });
                Check(Property<bool>(frame, "Success") && viewport.CameraTarget.DistanceTo(new Point3d(120, 0, 0)) < 1e-8,
                    "an unchanged unlocked target still frames normally");
            }
            finally
            {
                viewport.LockedProjection = false;
                doc.Views.ActiveView = firstView;
                secondView.Close();
            }

            Forms.Clipboard.SetText("preserve reentrant clipboard");
            int checks = 0;
            using var curve = new LineCurve(Point3d.Origin, new Point3d(2, 0, 0));
            var holder = new GH_Curve { Value = curve };
            object copy = assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!.GetMethod("CopyGeometryChecked", Static)!
                .Invoke(null, new object[] { new IGH_Goo?[] { holder }, doc.ModelUnitSystem, doc.ModelAbsoluteTolerance,
                    (Func<bool>)(() => ++checks < 5) })!;
            Check(!Property<bool>(copy, "Success") && Property<string>(copy, "Error").Contains("cancelled") &&
                Forms.Clipboard.GetText() == "preserve reentrant clipboard",
                "a changed generation during native copy/serialization leaves the clipboard unchanged");

            var group = new GH_GeometryGroup(); group.Objects.Add(new GH_Point(Point3d.Origin));
            copy = Copy(assembly, RetargetAfterPreparation(group));
            Check(!Property<bool>(copy, "Success") && Property<string>(copy, "Error").Contains("cancelled") &&
                Forms.Clipboard.GetText() == "preserve reentrant clipboard",
                "a group retargeted after preparation is reinspected before clipboard publication");

            var equalGroup = new GH_GeometryGroup(); equalGroup.Objects.Add(new GH_Point(Point3d.Origin));
            target = viewport.CameraTarget;
            frame = Frame(assembly, RetargetAfterPreparation(equalGroup, true));
            Check(!Property<bool>(frame, "Success") && viewport.CameraTarget == target,
                "replacing a group member with an equal-valued holder cancels frame before camera publication");
            var wrapper = new GH_ObjectWrapper(new GH_Point(Point3d.Origin));
            copy = Copy(assembly, RetargetWrapperAfterPreparation(wrapper));
            Check(!Property<bool>(copy, "Success") && Forms.Clipboard.GetText() == "preserve reentrant clipboard",
                "retargeting a wrapper to an equal-valued holder cancels before clipboard publication");
            var mutable = new GH_Point(Point3d.Origin);
            target = viewport.CameraTarget;
            frame = Frame(assembly, ChangePointAfterPreparation(mutable));
            Check(!Property<bool>(frame, "Success") && viewport.CameraTarget == target,
                "changing a captured point value on the same holder cancels frame before publication");
            mutable.Value = Point3d.Origin;
            var pointGroup = new GH_GeometryGroup(); pointGroup.Objects.Add(mutable);
            custom.OnBounds = () => mutable.Value = new Point3d(39, 0, 0);
            frame = Frame(assembly, new IGH_Goo?[] { pointGroup, custom });
            Check(!Property<bool>(frame, "Success") && viewport.CameraTarget == target,
                "a reentrant bounds callback changing a grouped point cancels frame");
            mutable.Value = Point3d.Origin;
            copy = Copy(assembly, ChangePointAfterPreparation(mutable));
            Check(!Property<bool>(copy, "Success") && Forms.Clipboard.GetText() == "preserve reentrant clipboard",
                "changing a captured point value on the same holder cancels copy before publication");

            var empty = new GH_GeometryGroup();
            frame = Frame(assembly, new IGH_Goo?[] { empty, new GH_Point(new Point3d(12, 0, 0)) });
            Check(Property<bool>(frame, "Success") && Property<int>(frame, "Skipped") == 1,
                "an unchanged empty group is reported as skipped while a point frames");
            copy = Copy(assembly, new IGH_Goo?[] { empty, new GH_Point(new Point3d(12, 0, 0)) });
            Check(Property<bool>(copy, "Success") && Property<int>(copy, "Skipped") == 1,
                "an unchanged empty group is reported as skipped while a point copies");
            target = viewport.CameraTarget;
            custom.OnBounds = () => empty.Objects.Add(new GH_Point(new Point3d(80, 0, 0)));
            frame = Frame(assembly, new IGH_Goo?[] { empty, custom });
            Check(!Property<bool>(frame, "Success") && viewport.CameraTarget == target && empty.Objects.Count == 1,
                "a callback changing an earlier skipped empty group cancels frame without moving the camera");
            empty.Objects.Clear(); Forms.Clipboard.SetText("keep skipped group clipboard");
            using var withData = new LineCurve(Point3d.Origin, new Point3d(2, 0, 0));
            Check(withData.UserData.Add(new CopyRoundTripUserData { Value = 9 }), "skipped-group copy callback data is attached");
            CopyRoundTripUserData.DuplicateCalls = 0; CopyRoundTripUserData.TriggeredGeneration = 0; CopyRoundTripUserData.DuplicateAt = 1;
            CopyRoundTripUserData.OnDuplicateAction = () => empty.Objects.Add(new GH_Point(new Point3d(80, 0, 0)));
            copy = Copy(assembly, new IGH_Goo?[] { empty, new GH_Curve { Value = withData } });
            Check(!Property<bool>(copy, "Success") && Forms.Clipboard.GetText() == "keep skipped group clipboard" &&
                empty.Objects.Count == 1 && CopyRoundTripUserData.TriggeredGeneration > 0,
                "a duplicate callback changing an earlier skipped empty group cancels copy before replacing the clipboard");
        }
        finally
        {
            CopyRoundTripUserData.OnDuplicateAction = null;
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            if (previous == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(previous, true);
        }
    }

    private static IEnumerable<IGH_Goo?> RetargetAfterPreparation(GH_GeometryGroup group, bool equal = false)
    {
        yield return group;
        group.Objects.Clear();
        group.Objects.Add(new GH_Point(equal ? Point3d.Origin : new Point3d(90, 0, 0)));
    }

    private static IEnumerable<IGH_Goo?> RetargetWrapperAfterPreparation(GH_ObjectWrapper wrapper)
    {
        yield return wrapper;
        wrapper.Value = new GH_Point(Point3d.Origin);
    }

    private static IEnumerable<IGH_Goo?> ChangePointAfterPreparation(GH_Point point)
    {
        yield return point;
        point.Value = new Point3d(37, 0, 0);
    }

    internal static async Task RunNativeResources(Assembly assembly)
    {
        var rhino = RhinoDoc.ActiveDoc ?? RhinoDoc.Create(null);
        bool modified = rhino.Modified;
        var previous = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Preview resources fixture", Rhino.Display.DefinedViewportProjection.Top, new Rectangle(0, 0, 640, 480), false);
        using var document = new GH_Document { Enabled = true };
        try
        {
            rhino.Views.ActiveView = view;
            view.ActiveViewport.ConstructionGridVisible = view.ActiveViewport.ConstructionAxesVisible = view.ActiveViewport.WorldAxesVisible = false;
            document.Properties.KeepOpen = true; document.PreviewColourSelected = Color.FromArgb(255, 0, 230, 40);
            var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
            component.CreateAttributes(); document.AddObject(component, false);
            var highlight = Field<object>(component, "_highlight");
            var wrappedPoint = new GH_ObjectWrapper(new Point3d(0, 0, 0));
            Set(highlight, new IGH_Goo?[] { wrappedPoint }, new[] { 0 }, new[] { 0 });
            component.ExpirePreview(false);
            view.ActiveViewport.ZoomBoundingBox(new BoundingBox(new Point3d(-4, -4, -1), new Point3d(4, 4, 1)));
            view.Redraw();
            using var pointBitmap = Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480);
            int pointGreen = 0;
            for (int y = 0; y < pointBitmap.Height; y++) for (int x = 0; x < pointBitmap.Width; x++)
            { var c = pointBitmap.GetPixel(x, y); if (c.G > c.R + 40 && c.G > c.B + 40) pointGreen++; }
            Check(pointGreen > 0, "an object-wrapped point actually draws in the Rhino viewport");

            var box = new GH_Box(new Box(new BoundingBox(new Point3d(-2, -2, -1), new Point3d(2, 2, 1))));
            var boxGroup = new GH_GeometryGroup(); boxGroup.Objects.Add(box);
            Set(highlight, new IGH_Goo?[] { box, boxGroup }, new[] { 0, 1 }, new[] { 0 });
            long bytes = Property<long>(highlight, "RetainedBytes");
            foreach (var mode in new[] { GH_PreviewMesh.LowQuality, GH_PreviewMesh.HighQuality, GH_PreviewMesh.Custom })
            {
                if (mode == GH_PreviewMesh.Custom) document.PreviewCustomMeshParameters = new MeshingParameters(0.4);
                document.PreviewMeshType = mode;
                component.ExpirePreview(false);
                view.Redraw();
                using var meshBitmap = CapturePreview(view, highlight, "native boxes " + mode, null);
                Check(meshBitmap != null && Count(highlight) == 2 && Property<long>(highlight, "RetainedBytes") == bytes &&
                    Box(highlight).Contains(new Point3d(2, 2, 1)),
                    "mesh-setting changes retain bounded standalone and grouped box snapshots without regenerating caches");
            }
            using var nativeBrep = new Box(new BoundingBox(new Point3d(-2, -2, -1), new Point3d(2, 2, 1))).ToBrep();
            var brep = new GH_Brep { Value = nativeBrep };
            var brepGroup = new GH_GeometryGroup(); brepGroup.Objects.Add(brep);
            Set(highlight, new IGH_Goo?[] { brep, brepGroup }, new[] { 0, 1 }, new[] { 0 });
            var leafType = assembly.GetType("BetterGhPanel.Runtime.PanelPreviewLeaf", true)!;
            var live = leafType.GetProperty("LiveGeneratedMeshes", Static)!;
            var builds = leafType.GetProperty("GeneratedMeshBuilds", Static)!;
            var shaded = Rhino.Display.DisplayModeDescription.FindByName("Shaded");
            if (shaded != null) view.ActiveViewport.DisplayMode = shaded;
            document.PreviewMode = GH_PreviewMode.Shaded;
            foreach (var mode in new[] { GH_PreviewMesh.LowQuality, GH_PreviewMesh.HighQuality, GH_PreviewMesh.Custom })
            {
                if (mode == GH_PreviewMesh.Custom) document.PreviewCustomMeshParameters = new MeshingParameters(0.35);
                document.PreviewMeshType = mode;
                component.ExpirePreview(false);
                int before = (int)builds.GetValue(null)!;
                view.Redraw();
                using var bitmap = CapturePreview(view, highlight, "native Breps " + mode, null);
                Check(bitmap != null && Count(highlight) == 2 && (int)builds.GetValue(null)! > before &&
                    (int)live.GetValue(null)! > 0 && Property<long>(highlight, "RetainedBytes") <= 16L * 1024 * 1024,
                    "standalone and grouped Breps regenerate bounded shaded meshes after a meshing setting change");
                int cachedBuilds = (int)builds.GetValue(null)!;
                int cachedMeshes = (int)live.GetValue(null)!;
                view.Redraw();
                using var repeat = Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480);
                Check(repeat != null && (int)builds.GetValue(null)! == cachedBuilds && (int)live.GetValue(null)! == cachedMeshes,
                    "unchanged meshing settings reuse owned meshes instead of regenerating them every frame");
            }
            var oldBounds = Box(highlight);
            Check(nativeBrep.Transform(Transform.Translation(30, 0, 0)), "the live Brep mutates after preview admission");
            Check(Box(highlight).Equals(oldBounds), "standalone and grouped Brep bounds remain on the old generation without expiration");
            component.ExpirePreview(false); view.Redraw();
            using (var oldImage = Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480))
                Check(oldImage != null && GreenPixels(oldImage) > 0 && Count(highlight) == 2 && Box(highlight).Equals(oldBounds),
                    "the old Brep generation still draws with its original bounds after source mutation");
            Set(highlight, new IGH_Goo?[] { brep, brepGroup }, new[] { 0, 1 }, new[] { 0 });
            Check(Box(highlight).Min.X > oldBounds.Max.X, "a rebuilt Brep generation uses the new source geometry and bounds");
            view.ActiveViewport.ZoomBoundingBox(Box(highlight));
            component.ExpirePreview(false); view.Redraw();
            using (var newImage = Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480))
                Check(newImage != null && GreenPixels(newImage) > 0 && Count(highlight) == 2,
                    "the rebuilt Brep generation draws after expiration");
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            Check(Count(highlight) == 0 && !Box(highlight).IsValid, "clearing a preview immediately detaches its geometry and bounds");
            await WaitForPreviewRetirement(highlight);
            Check((int)live.GetValue(null)! == 0 && Property<long>(highlight, "RetainedBytes") == 0,
                "clearing a preview eventually disposes every generated Brep mesh and releases its accounting");
            component.DocumentContextChanged(document, GH_DocumentContext.Close);
        }
        finally
        {
            document.Properties.KeepOpen = false; document.PreviewMode = GH_PreviewMode.Disabled;
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close(); rhino.Modified = modified;
        }
    }

    // Directly exercise the plugin's preview callbacks in a Rhino display
    // conduit as well as retaining the separate GH-document draw fixture above.
    internal static Task RunConduitResources(Assembly assembly)
    {
        var rhino = RhinoDoc.ActiveDoc ?? RhinoDoc.Create(null);
        bool modified = rhino.Modified;
        var previous = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Preview conduit fixture", Rhino.Display.DefinedViewportProjection.Top, new Rectangle(0, 0, 640, 480), false);
        using var document = new GH_Document();
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes(); component.Attributes.Selected = true;
        using var conduit = new PreviewConduit(component, document);
        try
        {
            rhino.Views.ActiveView = view;
            view.ActiveViewport.ConstructionGridVisible = view.ActiveViewport.ConstructionAxesVisible = view.ActiveViewport.WorldAxesVisible = false;
            view.ActiveViewport.ZoomBoundingBox(new BoundingBox(new Point3d(-4, -4, -1), new Point3d(4, 4, 1)));
            var highlight = Field<object>(component, "_highlight");
            var wrapper = new GH_ObjectWrapper(new Point3d(0, 0, 0));
            Set(highlight, new IGH_Goo?[] { wrapper }, new[] { 0 }, new[] { 0 });
            wrapper.Value = wrapper;
            conduit.Enabled = true;
            using (var bitmap = Rhino.Display.DisplayPipeline.DrawToBitmap(view.ActiveViewport, 640, 480))
            {
                int green = 0;
                for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
                { var c = bitmap.GetPixel(x, y); if (c.G > c.R + 40 && c.G > c.B + 40) green++; }
                Check(green > 0 && conduit.Draws > 0 && Box(highlight).Contains(Point3d.Origin),
                    "a wrapped point actually draws from its old coherent generation after its wrapper becomes cyclic");
            }
            var box = new GH_Box(new Box(new BoundingBox(new Point3d(-2, -2, -1), new Point3d(2, 2, 1))));
            var group = new GH_GeometryGroup(); group.Objects.Add(box);
            Set(highlight, new IGH_Goo?[] { box, group }, new[] { 0, 1 }, new[] { 0 });
            long bytes = Property<long>(highlight, "RetainedBytes");
            foreach (var mode in new[] { GH_PreviewMesh.LowQuality, GH_PreviewMesh.HighQuality, GH_PreviewMesh.Custom })
            {
                if (mode == GH_PreviewMesh.Custom) document.PreviewCustomMeshParameters = new MeshingParameters(0.4);
                document.PreviewMeshType = mode;
                int before = conduit.Draws;
                using var bitmap = CapturePreview(view, highlight, "conduit boxes " + mode, conduit);
                Check(bitmap != null && conduit.Draws > before && Count(highlight) == 2 &&
                    Property<long>(highlight, "RetainedBytes") == bytes && Box(highlight).Contains(new Point3d(2, 2, 1)),
                    "wire and shaded callbacks retain bounded standalone and grouped boxes across mesh resets");
            }
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
        }
        finally
        {
            conduit.Enabled = false;
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close(); rhino.Modified = modified;
        }
        return Task.CompletedTask;
    }

    internal static async Task RunCompatibilityDraw(Assembly assembly)
    {
        var rhino = RhinoDoc.ActiveDoc ?? RhinoDoc.Create(null);
        bool modified = rhino.Modified; var previous = rhino.Views.ActiveView;
        var view = rhino.Views.Add("Compatibility preview drawing", Rhino.Display.DefinedViewportProjection.Top,
            new Rectangle(0, 0, 640, 480), false);
        using var document = new GH_Document { Enabled = true };
        using var first = LongNurbs(-6); using var second = LongNurbs(-2);
        using var third = LongPolyline(2); using var fourth = LongPolyline(6);
        using var rebuiltMaterial = new Rhino.Display.DisplayMaterial(Color.Cyan);
        var component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
        component.CreateAttributes();
        using var conduit = new PreviewConduit(component, document);
        Rhino.Display.RhinoViewport? capturedViewport = null;
        try
        {
            rhino.Views.ActiveView = view;
            view.ActiveViewport.ConstructionGridVisible = view.ActiveViewport.ConstructionAxesVisible = view.ActiveViewport.WorldAxesVisible = false;
            view.ActiveViewport.ZoomBoundingBox(new BoundingBox(new Point3d(-10, -9, -1), new Point3d(10, 9, 1)));
            document.Properties.KeepOpen = true;
            document.AddObject(component, false);
            var highlight = Field<object>(component, "_highlight");
            var nurbsGroup = new GH_GeometryGroup(); nurbsGroup.Objects.Add(new GH_Curve { Value = second });
            var polyGroup = new GH_GeometryGroup(); polyGroup.Objects.Add(new GH_Curve { Value = fourth });
            var source = new IGH_Goo?[] { new GH_Curve { Value = first }, nurbsGroup, new GH_Curve { Value = third }, polyGroup };
            var snapshot = new PanelSnapshot(Enumerable.Range(0, 4).Select(i => new PanelRow(i, "{0}", "Curve", "curve", null)).ToArray(),
                new[] { new PanelBranch("{0}", 4) });
            var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
            Scope(highlight, source, result, new[] { 0, 2 });
            var oldBounds = Box(highlight);
            bool initial = Count(highlight) == 4 && Property<int>(highlight, "Count") == 2;
            bool changed = false, oldCoherent = false, rebuilt = false;
            string? error = null;
            conduit.OnDraw = args =>
            {
                try
                {
                    // DrawToBitmap uses its own projection. Pixel regions must
                    // use the viewport from this draw, not the restored live view.
                    capturedViewport = new Rhino.Display.RhinoViewport(args.Viewport);
                    changed = first.Transform(Transform.Translation(0, 1, 0)) && second.Transform(Transform.Translation(0, 1, 0)) &&
                        third.Transform(Transform.Translation(0, 1, 0)) && fourth.Transform(Transform.Translation(0, 1, 0));
                    oldCoherent = changed && Box(highlight).Equals(oldBounds) && Count(highlight) == 4;
                    component.DrawViewportWires(args);
                    Method(component, "ClearHighlight").Invoke(component, Array.Empty<object>());
                    Scope(highlight, source, result, new[] { 0, 2 });
                    component.ExpirePreview(false);
                    rebuilt = Count(highlight) == 4 && Property<int>(highlight, "Count") == 2 && Box(highlight).Min.Y > oldBounds.Min.Y &&
                        Property<long>(highlight, "RetainedBytes") <= 16L * 1024 * 1024;
                    component.DrawViewportWires(new ColoredPreviewArgs(args, rebuiltMaterial, Color.Cyan));
                }
                catch (Exception ex) { error = ex.ToString(); }
            };
            conduit.Enabled = true;
            using var bitmap = CapturePreview(view, highlight, "compatibility curves", conduit);
            Check(error == null && initial && oldCoherent && rebuilt,
                "selected and filtered compatibility curves retain old bounds until source expiration and rebuild: " + error);
            double[] ys = { -6, -2, 2, 6 };
            for (int i = 0; i < ys.Length; i++)
            {
                var old = new BoundingBox(new Point3d(-8, ys[i] - 0.2, 0), new Point3d(8, ys[i] + 0.3, 0));
                var current = new BoundingBox(new Point3d(-8, ys[i] + 0.8, 0), new Point3d(8, ys[i] + 1.3, 0));
                Check(RegionHasColor(bitmap, capturedViewport!, old, i % 2 == 0 ? Color.Lime : Color.Red) &&
                    RegionHasColor(bitmap, capturedViewport!, current, Color.Cyan),
                    "compatibility curve " + i + " actually draws with selected/filtered colors before and after rebuild");
            }
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            await WaitForPreviewRetirement(highlight);
            Check(Property<long>(highlight, "RetainedBytes") == 0, "compatibility preview snapshots are released after drawing");
        }
        finally
        {
            capturedViewport?.Dispose();
            conduit.Enabled = false; document.Properties.KeepOpen = false; document.PreviewMode = GH_PreviewMode.Disabled;
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close(); rhino.Modified = modified;
        }
    }

    private static NurbsCurve LongNurbs(double y)
    {
        var curve = new NurbsCurve(3, false, 2, 1025);
        for (int i = 0; i < curve.Knots.Count; i++) curve.Knots[i] = i;
        for (int i = 0; i < curve.Points.Count; i++) curve.Points.SetPoint(i, new Point3d(-8 + 16.0 * i / 1024, y + 0.1 * (i % 2), 0));
        return curve;
    }
    private static PolylineCurve LongPolyline(double y) => new PolylineCurve(Enumerable.Range(0, 2049)
        .Select(i => new Point3d(-8 + 16.0 * i / 2048, y + 0.1 * (i % 2), 0)));

    private static void VerifyMutation(Assembly assembly, object highlight)
    {
        var point = new GH_Point(new Point3d(1, 2, 3));
        var group = new GH_GeometryGroup { Name = "safe" };
        group.Objects.Add(point);
        var wrapper = new GH_GeometricGooWrapper(group);
        IGH_Goo?[] source = { wrapper };
        Set(highlight, source, new[] { 0 }, new[] { 0 });
        Check(Box(highlight).Contains(new Point3d(1, 2, 3)) && Count(highlight) == 1,
            "wrapped group snapshots one bounded point and one group label target");
        point.Value = new Point3d(100, 0, 0);
        group.Objects.Clear(); group.Objects.Add(group);
        Check(Box(highlight).Contains(new Point3d(1, 2, 3)) && !Box(highlight).Contains(new Point3d(100, 0, 0)),
            "mutating a leaf and making the source group cyclic leaves the admitted bounds unchanged");
        Check(!Bounds(assembly, wrapper).IsValid, "frame inspection rejects the group's current cycle");
        Set(highlight, source, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 0 && Status(highlight).Contains("partial"),
            "a new generation detects the cycle without dispatching to the group");
        group.Objects.Clear(); group.Objects.Add(new GH_Point(new Point3d(25, 0, 0)));
        Set(highlight, source, new[] { 0 }, new[] { 0 });
        Check(Box(highlight).Contains(new Point3d(25, 0, 0)), "source expiration admits the replacement child");

        var objectWrapper = new GH_ObjectWrapper(new Point3d(4, 0, 0));
        source[0] = objectWrapper;
        Set(highlight, source, new[] { 0 }, new[] { 0 });
        objectWrapper.Value = objectWrapper;
        Check(Box(highlight).Contains(new Point3d(4, 0, 0)) && !Bounds(assembly, objectWrapper).IsValid,
            "retargeting a wrapper into itself cannot retarget its preview and current frame rejects it");
        Set(highlight, source, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 0 && Status(highlight).Contains("partial"), "self-wrapped objects are unavailable after refresh");
        var geometricSelf = new GH_GeometricGooWrapper();
        typeof(GH_GeometricGooWrapper).GetField("m_goo", Instance)!.SetValue(geometricSelf, geometricSelf);
        source[0] = geometricSelf; Set(highlight, source, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 0 && !Bounds(assembly, geometricSelf).IsValid, "geometric self-wrapper is bounded before any delegated call");
        var deep = new GH_GeometryGroup(); var current = deep;
        for (int i = 0; i < 80; i++) { var child = new GH_GeometryGroup(); current.Objects.Add(child); current = child; }
        current.Objects.Add(new GH_Point(Point3d.Origin));
        source[0] = deep; Set(highlight, source, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 0 && Status(highlight).Contains("partial"), "deep acyclic groups stop at the depth budget");
        var hostileHolder = new HostileCurveHolder();
        source[0] = hostileHolder; HostileCurveHolder.Calls = 0;
        Set(highlight, source, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 0 && !Bounds(assembly, hostileHolder).IsValid && HostileCurveHolder.Calls == 0,
            "a hostile holder subclass is not treated as an independent standalone preview");

        using var native = new LineCurve(new Point3d(0, 0, 0), new Point3d(3, 0, 0));
        var holder = new GH_Curve(native);
        source[0] = holder; Set(highlight, source, new[] { 0 }, new[] { 0 });
        holder.Value.SetEndPoint(new Point3d(30, 0, 0));
        Check(Box(highlight).Max.X == 3, "in-place curve mutation does not change the old preview generation");
        Set(highlight, source, new[] { 0 }, new[] { 0 });
        Check(Box(highlight).Max.X == 30, "refresh reads the newly mutated exact inner object");
        var curveGroup = new GH_GeometryGroup(); curveGroup.Objects.Add(holder);
        source[0] = curveGroup; Set(highlight, source, new[] { 0 }, new[] { 0 });
        holder.Value.SetEndPoint(new Point3d(60, 0, 0));
        Check(Box(highlight).Max.X == 30, "group-derived in-place curve mutation retains coherent admitted bounds");
        Set(highlight, source, new[] { 0 }, new[] { 0 });
        Check(Box(highlight).Max.X == 60, "group refresh rebuilds its curve snapshot and bounds together");
        using var hostile = new HostileLineCurve();
        holder.Value = hostile;
        HostileLineCurve.Calls = 0;
        Check(Box(highlight).Max.X == 60 && !Bounds(assembly, holder).IsValid && HostileLineCurve.Calls == 0,
            "replacement with an untrusted inner subclass never reaches its validity callback");
        Set(highlight, source, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 0 && HostileLineCurve.Calls == 0 && Status(highlight).Contains("partial"),
            "refresh visibly skips the hostile inner object");
        Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
    }

    private static void VerifyBudgets(object highlight)
    {
        const int total = 1100;
        var values = Enumerable.Range(0, total).Select(i => (IGH_Goo?)new GH_Point(new Point3d(i, 0, 0))).ToArray();
        var snapshot = new PanelSnapshot(Enumerable.Range(0, total).Select(i => new PanelRow(i, "{0}", "Point", "", null)).ToArray(),
            new[] { new PanelBranch("{0}", total) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        foreach (bool labels in new[] { false, true })
        {
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            Method(highlight, "SetOrderResult").Invoke(highlight, new object?[] { labels ? result : null });
            Scope(highlight, values, result, Array.Empty<int>());
            Check(Count(highlight) == 1024 && Status(highlight).Contains("partial") && Property<long>(highlight, "RetainedBytes") <= 16L * 1024 * 1024,
                "filtered preview enforces bounded admission and reports partial results");
            for (int i = 1024; i < total; i++)
            {
                Scope(highlight, values, result, new[] { i });
                Check(Box(highlight).Contains(new Point3d(i, 0, 0)) && Property<int>(highlight, "LastVisitedRows") <= 1 &&
                    Property<int>(highlight, "LastVisitedNodes") <= 1 && Property<long>(highlight, "RetainedBytes") <= 16L * 1024 * 1024,
                    $"successive sparse selections evict and recover without walking filtered rows " +
                    $"(position={i}, labels={labels}, bounds={Box(highlight)}, rows={Property<int>(highlight, "LastVisitedRows")}, " +
                    $"nodes={Property<int>(highlight, "LastVisitedNodes")}, bytes={Property<long>(highlight, "RetainedBytes")}, status={Status(highlight)})");
            }
        }
        var huge = new GH_GeometryGroup { Name = "many" };
        for (int i = 0; i < 5000; i++) huge.Objects.Add(new GH_Point(new Point3d(i, 0, 0)));
        Set(highlight, new IGH_Goo?[] { huge }, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 0 && Property<int>(highlight, "LastVisitedNodes") <= 4096 && Status(highlight).Contains("partial"),
            "a group exceeding the per-root budget is unavailable rather than partially dispatched");
        var shared = new GH_GeometryGroup();
        for (int i = 0; i < 100; i++) shared.Objects.Add(new GH_Point(new Point3d(i, 0, 0)));
        var repeated = Enumerable.Repeat<IGH_Goo?>(shared, 1000).ToArray();
        Set(highlight, repeated, Enumerable.Range(0, repeated.Length).ToArray(), Array.Empty<int>());
        Check(Property<int>(highlight, "LastVisitedNodes") <= 20000 && Property<long>(highlight, "RetainedBytes") <= 16L * 1024 * 1024 &&
            Status(highlight).Contains("partial"), "repeated groups share an aggregate visit and retained-byte limit");
        using var beyondNarrow = new PolylineCurve(Enumerable.Range(0, 2049).Select(i => new Point3d(i, i % 2, 0)));
        Set(highlight, new IGH_Goo?[] { new GH_Curve { Value = beyondNarrow } }, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 1 && Box(highlight).Max.X == 2048 && Property<long>(highlight, "RetainedBytes") <= 16L * 1024 * 1024,
            "a 2049-point polyline outside the narrow reconstruction uses the bounded compatibility provider");
        using var oversized = new PolylineCurve(Enumerable.Range(0, 32769).Select(i => new Point3d(i, i % 2, 0)));
        Set(highlight, new IGH_Goo?[] { new GH_Curve { Value = oversized } }, new[] { 0 }, new[] { 0 });
        Check(Count(highlight) == 0 && Property<long>(highlight, "RetainedBytes") == 0 && Status(highlight).Contains("partial"),
            "geometry beyond the compatibility provider limit is rejected before constructing a preview copy");
        Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
    }

    private static void VerifyStagedAdmission(object highlight)
    {
        using var brep = new Box(new BoundingBox(Point3d.Origin, new Point3d(2, 3, 4))).ToBrep();
        var holder = new GH_Brep { Value = brep };
        var outer = new GH_GeometryGroup(); var nested = new GH_GeometryGroup();
        for (int i = 0; i < 5; i++) { outer.Objects.Add(holder); nested.Objects.Add(holder); }
        object? stagedLeaf = null; GeometryBase? stagedGeometry = null;
        bool aliveDuringCallback = false, cappedDuringCallback = false, nestedRejected = false;
        var observerField = highlight.GetType().GetField("_admissionObserver", Instance)!;
        observerField.SetValue(highlight, (Action<int, object>)((index, leaf) =>
        {
            if (index != 0) return;
            observerField.SetValue(highlight, null);
            stagedLeaf = leaf; stagedGeometry = Field<GeometryBase?>(leaf, "_owned");
            long reserved = Property<long>(highlight, "RetainedBytes");
            Set(highlight, new IGH_Goo?[] { nested }, new[] { 0 }, new[] { 0 });
            aliveDuringCallback = stagedGeometry != null && !stagedGeometry.Disposed;
            cappedDuringCallback = reserved > 0 && Property<long>(highlight, "RetainedBytes") <= 16L * 1024 * 1024;
            nestedRejected = Count(highlight) == 0 && Status(highlight).Contains("partial");
        }));
        try
        {
            Set(highlight, new IGH_Goo?[] { outer }, new[] { 0 }, new[] { 0 });
            Check(aliveDuringCallback && cappedDuringCallback && nestedRejected,
                "reentrant admission counts staged geometry and rejects a nested build over the live-memory cap");
            Check(stagedLeaf != null && stagedGeometry != null && stagedGeometry.Disposed && Property<int>(stagedLeaf, "DisposeCount") == 1 &&
                Property<long>(highlight, "RetainedBytes") == 0, "the abandoned staged leaf is disposed exactly once after reentry");
            Set(highlight, new IGH_Goo?[] { nested }, new[] { 0 }, new[] { 0 });
            var recovered = OwnedGeometry(highlight).ToArray();
            Check(Count(highlight) == 1 && recovered.Length == 5 && recovered.All(item => !item.Disposed) &&
                Property<long>(highlight, "RetainedBytes") <= 16L * 1024 * 1024, "a later build recovers after the staged reservation is released");
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            Check(recovered.All(item => item.Disposed) && Property<long>(highlight, "RetainedBytes") == 0,
                "clearing the recovered generation releases every owned geometry");
        }
        finally { observerField.SetValue(highlight, null); Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>()); }
    }

    private static void VerifyMetadataAndActions(Assembly assembly, object highlight)
    {
        var prior = Forms.Clipboard.GetDataObject();
        try
        {
            using var poly = new PolylineCurve(new[] { Point3d.Origin, new Point3d(2, 0, 0), new Point3d(2, 3, 0) });
            var holder = new GH_Curve(poly);
            var source = (PolylineCurve)holder.Value;
            Check(source.UserData.Add(new PolylineSafetyUserData()) && source.HasUserData, "polyline UserData is attached before plugin work");
            PolylineSafetyUserData.Reset(true);
            Set(highlight, new IGH_Goo?[] { holder }, new[] { 0 }, new[] { 0 });
            Check(Count(highlight) == 1 && Box(highlight).Max.Y == 3 && source.HasUserData && PolylineSafetyUserData.Callbacks == 0,
                "value-only polyline preview preserves source UserData without invoking it");
            object copied = Copy(assembly, new IGH_Goo?[] { new GH_Point(new Point3d(9, 0, 0)), holder });
            Check(Property<bool>(copied, "Success") && Property<int>(copied, "Count") == 1 && Property<int>(copied, "Skipped") == 1 &&
                source.HasUserData && PolylineSafetyUserData.Callbacks > 0 && Property<string>(copied, "Detail").Contains("callback failed"),
                "explicit copy reports a throwing custom callback and preserves the skipped source metadata");
            PolylineSafetyUserData.Reset(false);

            using var polyCurve = new PolyCurve();
            Check(polyCurve.Append(new Line(Point3d.Origin, new Point3d(1, 0, 0))) &&
                polyCurve.Append(new Line(new Point3d(1, 0, 0), new Point3d(2, 1, 0))), "nested polycurve fixture is constructed");
            var polyHolder = new GH_Curve(polyCurve);
            var segment = ((PolyCurve)polyHolder.Value).SegmentCurve(0);
            Check(!polyHolder.Value.HasUserData && segment.UserData.Add(new PolylineSafetyUserData()) && segment.HasUserData,
                "nested segment UserData exists while the outer curve has none");
            PolylineSafetyUserData.Reset(true);
            Set(highlight, new IGH_Goo?[] { polyHolder }, new[] { 0 }, new[] { 0 });
            Check(Count(highlight) == 0 && PolylineSafetyUserData.Callbacks > 0 && Status(highlight).Contains("partial") && segment.HasUserData,
                "compatibility preview visibly rejects a throwing nested callback and preserves its metadata");
            object polyCopy = Copy(assembly, new IGH_Goo?[] { polyHolder });
            Check(Count(highlight) == 0 && Status(highlight).Contains("partial") && !Property<bool>(polyCopy, "Success") &&
                segment.HasUserData && !polyHolder.Value.HasUserData && PolylineSafetyUserData.Callbacks > 0,
                "throwing nested custom data is visibly skipped for explicit copy without source metadata loss");
            PolylineSafetyUserData.Reset(false);

            using var brep = new Box(new BoundingBox(Point3d.Origin, new Point3d(2, 3, 4))).ToBrep();
            Check(brep.UserData.Count == 0 && brep.Curves3D[0].UserData.Add(new CaptureCountingUserData()) &&
                brep.Surfaces[0].UserData.Add(new CaptureBenignUserData()) && brep.Surfaces[0].UserData.Add(new CaptureNonSerializableUserData()) &&
                brep.Curves3D[0].HasUserData && brep.Surfaces[0].UserData.Count == 2,
                "nested hostile, benign and non-serializable Brep data exists without outer data");
            var brepHolder = new GH_Brep { Value = brep };
            var group = new GH_GeometryGroup(); group.Objects.Add(brepHolder);
            CaptureCountingUserData.Callbacks = CaptureBenignUserData.Callbacks = CaptureNonSerializableUserData.Callbacks = 0;
            Set(highlight, new IGH_Goo?[] { brepHolder, group }, new[] { 0, 1 }, new[] { 0, 1 });
            int beforeFrame = CaptureCountingUserData.Callbacks + CaptureBenignUserData.Callbacks + CaptureNonSerializableUserData.Callbacks;
            var frame = Bounds(assembly, group);
            Check(Count(highlight) == 0 && Status(highlight).Contains("partial") && frame.IsValid && beforeFrame > 0 &&
                CaptureCountingUserData.Callbacks + CaptureBenignUserData.Callbacks + CaptureNonSerializableUserData.Callbacks == beforeFrame,
                "unserializable nested metadata is visibly unavailable for preview and framing does not invoke callbacks");
            object result = Copy(assembly, new IGH_Goo?[] { brepHolder, group });
            Check(Count(highlight) == 0 && Status(highlight).Contains("partial") && frame.IsValid && !Property<bool>(result, "Success") &&
                Property<int>(result, "Skipped") == 2 && brep.UserData.Count == 0 && brep.Curves3D[0].HasUserData &&
                brep.Surfaces[0].UserData.Count == 2 && CaptureNonSerializableUserData.Callbacks > 0,
                "explicit copy reports non-serializable nested UserData and preserves the source; current bounds remain frameable");

        }
        finally
        {
            PolylineSafetyUserData.Reset(false);
            Method(highlight, "Clear").Invoke(highlight, Array.Empty<object>());
            if (prior == null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(prior, true);
        }
    }

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

    private static object Copy(Assembly assembly, IEnumerable<IGH_Goo?> values) => assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!
        .GetMethod("CopyGeometry", Static)!.Invoke(null, new object[] { values, RhinoDoc.ActiveDoc.ModelUnitSystem, RhinoDoc.ActiveDoc.ModelAbsoluteTolerance })!;
    private static File3dm ClipboardArchive()
    {
        object data = Forms.Clipboard.GetData("Rhino 8 3DM Geometry")!;
        byte[] bytes;
        if (data is byte[] array) bytes = array;
        else if (data is Stream input)
        {
            using var output = new MemoryStream(); input.Position = 0; input.CopyTo(output); bytes = output.ToArray();
        }
        else throw new InvalidOperationException("Native geometry clipboard has no byte payload.");
        return File3dm.FromByteArray(bytes) ?? throw new InvalidOperationException("Invalid geometry clipboard archive.");
    }
    private static object Frame(Assembly assembly, IEnumerable<IGH_Goo?> values) => assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!
        .GetMethod("Frame", Static)!.Invoke(null, new object[] { values })!;
    private static BoundingBox Bounds(Assembly assembly, IGH_Goo value) => (BoundingBox)assembly.GetType("BetterGhPanel.Runtime.PanelGeometryActions", true)!
        .GetMethod("GetBounds", Static)!.Invoke(null, new object[] { value })!;
    private static void Set(object highlight, IGH_Goo?[] values, int[] positions, int[] selected) =>
        Method(highlight, "Set").Invoke(highlight, new object[] { values, positions, selected });
    private static void Scope(object highlight, IGH_Goo?[] values, PanelQueryResult result, int[] selected) =>
        Method(highlight, "SetScope").Invoke(highlight, new object[] { values, result, true, selected });
    private static BoundingBox Box(object highlight) => Property<BoundingBox>(highlight, "ClippingBox");
    private static int Count(object highlight) => Property<int>(highlight, "PreviewCount");
    private static string Status(object highlight) => Property<string>(highlight, "Status");
    private static int GreenPixels(Bitmap bitmap)
    {
        int count = 0;
        for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
        { var color = bitmap.GetPixel(x, y); if (color.G > color.R + 40 && color.G > color.B + 40) count++; }
        return count;
    }
    private static bool RegionColors(Bitmap bitmap, Rhino.Display.RhinoViewport viewport, BoundingBox bounds, Color shade, Color wire)
    {
        var p = viewport.WorldToClient(new Point3d(bounds.Min.X, bounds.Min.Y, bounds.Max.Z));
        var q = viewport.WorldToClient(new Point3d(bounds.Max.X, bounds.Max.Y, bounds.Max.Z));
        var size = viewport.Size;
        double sx = bitmap.Width / (double)Math.Max(1, size.Width), sy = bitmap.Height / (double)Math.Max(1, size.Height);
        int x0 = Math.Max(0, (int)(Math.Min(p.X, q.X) * sx) - 5), x1 = Math.Min(bitmap.Width, (int)(Math.Max(p.X, q.X) * sx) + 6);
        int y0 = Math.Max(0, (int)(Math.Min(p.Y, q.Y) * sy) - 5), y1 = Math.Min(bitmap.Height, (int)(Math.Max(p.Y, q.Y) * sy) + 6);
        bool Matches(Color c, Color target) => target == Color.Blue ? c.B > c.R + 15 && c.B > c.G + 15 :
            target == Color.Yellow ? c.R > c.B + 15 && c.G > c.B + 15 :
            target == Color.Magenta ? c.R > c.G + 15 && c.B > c.G + 15 :
            target == Color.Red ? c.R > c.G + 25 && c.R > c.B + 25 :
            target == Color.Lime ? c.G > c.R + 25 && c.G > c.B + 25 : c.G > c.R + 15 && c.B > c.R + 15;
        int shadePixels = 0, wirePixels = 0;
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
        { var color = bitmap.GetPixel(x, y); if (Matches(color, shade)) shadePixels++; if (Matches(color, wire)) wirePixels++; }
        Console.WriteLine($"Preview region {bounds}: {shade.Name}={shadePixels}, {wire.Name}={wirePixels}, pixels={x0},{y0}-{x1},{y1}");
        return shadePixels > 20 && wirePixels > 2;
    }
    private static bool RegionHasColor(Bitmap bitmap, Rhino.Display.RhinoViewport viewport, BoundingBox bounds, Color target)
    {
        var p = viewport.WorldToClient(bounds.Min); var q = viewport.WorldToClient(bounds.Max); var size = viewport.Size;
        double sx = bitmap.Width / (double)Math.Max(1, size.Width), sy = bitmap.Height / (double)Math.Max(1, size.Height);
        int x0 = Math.Max(0, (int)(Math.Min(p.X, q.X) * sx) - 4), x1 = Math.Min(bitmap.Width, (int)(Math.Max(p.X, q.X) * sx) + 5);
        int y0 = Math.Max(0, (int)(Math.Min(p.Y, q.Y) * sy) - 4), y1 = Math.Min(bitmap.Height, (int)(Math.Max(p.Y, q.Y) * sy) + 5);
        int count = 0;
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
        {
            var c = bitmap.GetPixel(x, y);
            bool match = target == Color.Lime ? c.G > c.R + 25 && c.G > c.B + 25 :
                target == Color.Red ? c.R > c.G + 25 && c.R > c.B + 25 : c.G > c.R + 25 && c.B > c.R + 25;
            if (match) count++;
        }
        Console.WriteLine($"Compatibility region {bounds}: {target.Name}={count}, pixels={x0},{y0}-{x1},{y1}");
        return count > 10;
    }
    private static MethodInfo Method(object value, string name) => value.GetType().GetMethod(name, Instance)!;
    private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name, Instance)!.GetValue(value)!;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Instance)!.GetValue(value)!;
    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);

    private sealed class HostileLineCurve : LineCurve
    {
        internal static int Calls;
        public override bool IsValid { get { Calls++; throw new InvalidOperationException("Unexpected hostile validity"); } }
    }

    private sealed class HostileCurveHolder : GH_Curve
    {
        internal static int Calls;
        public override Curve Value { get { Calls++; throw new InvalidOperationException(); } set { Calls++; throw new InvalidOperationException(); } }
        public override bool IsValid { get { Calls++; throw new InvalidOperationException(); } }
        public override object ScriptVariable() { Calls++; throw new InvalidOperationException(); }
    }

    private sealed class StandalonePointPreview : GH_Point, IGH_PreviewData
    {
        internal int BoundsCalls;
        internal Action? OnBounds;
        internal Action? OnWire, OnMesh;
        BoundingBox IGH_PreviewData.ClippingBox
        {
            get
            {
                BoundsCalls++;
                var callback = OnBounds; OnBounds = null; callback?.Invoke();
                return new BoundingBox(new Point3d(7, 0, 0), new Point3d(7, 0, 0));
            }
        }
        void IGH_PreviewData.DrawViewportWires(GH_PreviewWireArgs args)
        { var callback = OnWire; OnWire = null; callback?.Invoke(); }
        void IGH_PreviewData.DrawViewportMeshes(GH_PreviewMeshArgs args)
        { var callback = OnMesh; OnMesh = null; callback?.Invoke(); }
    }

    private sealed class ColoredPreviewArgs : IGH_PreviewArgs
    {
        private readonly IGH_PreviewArgs _inner;
        internal ColoredPreviewArgs(IGH_PreviewArgs inner, Rhino.Display.DisplayMaterial material, Color wire)
        { _inner = inner; ShadeMaterial = ShadeMaterial_Selected = material; WireColour = WireColour_Selected = wire; }
        public int DefaultCurveThickness => _inner.DefaultCurveThickness;
        public Color WireColour { get; }
        public Color WireColour_Selected { get; }
        public Rhino.Display.DisplayMaterial ShadeMaterial { get; }
        public Rhino.Display.DisplayMaterial ShadeMaterial_Selected { get; }
        public Rhino.Display.RhinoViewport Viewport => _inner.Viewport;
        public Rhino.Display.DisplayPipeline Display => _inner.Display;
        public GH_Document Document => _inner.Document;
        public MeshingParameters MeshingParameters => _inner.MeshingParameters;
    }

    private sealed class PreviewConduit : Rhino.Display.DisplayConduit, IDisposable
    {
        private readonly GH_Component _component;
        private readonly GH_Document _document;
        private readonly Rhino.Display.DisplayMaterial _normal;
        private readonly Rhino.Display.DisplayMaterial _selected;
        internal PreviewConduit(GH_Component component, GH_Document document, Color? normalShade = null, Color? selectedShade = null)
        {
            _component = component; _document = document;
            _normal = new Rhino.Display.DisplayMaterial(normalShade ?? Color.Red);
            _selected = new Rhino.Display.DisplayMaterial(selectedShade ?? Color.Lime);
        }
        internal int Draws;
        internal int MeshesStarted, MeshesCompleted, WiresStarted, WiresCompleted;
        internal Action<IGH_PreviewArgs>? OnDraw;
        protected override void DrawForeground(Rhino.Display.DrawEventArgs e)
        {
            Draws++;
            var args = new Arguments(_document, e, _normal, _selected);
            if (OnDraw != null) { var callback = OnDraw; OnDraw = null; callback(args); return; }
            MeshesStarted++; _component.DrawViewportMeshes(args); MeshesCompleted++;
            WiresStarted++; _component.DrawViewportWires(args); WiresCompleted++;
        }
        public void Dispose() { Enabled = false; _normal.Dispose(); _selected.Dispose(); }

        private sealed class Arguments : IGH_PreviewArgs
        {
            private readonly Rhino.Display.DrawEventArgs _args;
            internal Arguments(GH_Document document, Rhino.Display.DrawEventArgs args,
                Rhino.Display.DisplayMaterial normal, Rhino.Display.DisplayMaterial selected)
            { Document = document; _args = args; ShadeMaterial = normal; ShadeMaterial_Selected = selected; }
            public int DefaultCurveThickness => 3;
            public Color WireColour => Color.Red;
            public Color WireColour_Selected => Color.Lime;
            public Rhino.Display.DisplayMaterial ShadeMaterial { get; }
            public Rhino.Display.DisplayMaterial ShadeMaterial_Selected { get; }
            public Rhino.Display.RhinoViewport Viewport => _args.Viewport;
            public Rhino.Display.DisplayPipeline Display => _args.Display;
            public GH_Document Document { get; }
            public MeshingParameters MeshingParameters => Document.PreviewCurrentMeshParameters();
        }
    }

    private sealed class StockCurveConduit : Rhino.Display.DisplayConduit, IDisposable
    {
        internal Action<Rhino.Display.DisplayPipeline>? OnDraw;
        protected override void DrawForeground(Rhino.Display.DrawEventArgs e)
        { var callback = OnDraw; OnDraw = null; callback?.Invoke(e.Display); }
        public void Dispose() { Enabled = false; }
    }
}
