using static PanelTestAdapter;
using static SmokeAssertions;
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
                Check(ReflectProperty403875335<bool>(frame, "Success") && ReflectProperty403875335<int>(frame, "Skipped") == 0,
                    "additional geometry " + i + " frames without a skip");
            }
            Set(highlight, new IGH_Goo?[] { group, wrappedGroup }, new[] { 0, 1 }, new[] { 0 });
            Check(Count(highlight) == 2 && Box(highlight).Contains(expected[0].Min) && Box(highlight).Contains(expected[1].Max),
                "mixed and wrapped groups preview terminal cloud, extrusion and point leaves");
            object groupCopy = Copy(assembly, new IGH_Goo?[] { group });
            Check(ReflectProperty403875335<bool>(groupCopy, "Success") && ReflectProperty403875335<int>(groupCopy, "Count") == 3 && ReflectProperty403875335<int>(groupCopy, "Skipped") == 0 &&
                ReflectProperty403875335<bool>(groupCopy, "UnverifiedMetadata"), "a mixed group copies all leaves with the custom-metadata warning");
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
            Check(ReflectProperty403875335<bool>(frameGroup, "Success") && ReflectProperty403875335<int>(frameGroup, "Skipped") == 0,
                "a wrapped mixed group frames without recursive wrapper dispatch");
            object copy = Copy(assembly, values);
            Check(ReflectProperty403875335<bool>(copy, "Success") && ReflectProperty403875335<int>(copy, "Count") == 5 && ReflectProperty403875335<int>(copy, "Skipped") == 0,
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
            Check(!ReflectProperty403875335<bool>(staleFrame, "Success") && rhino.Views.ActiveView.ActiveViewport.CameraTarget == beforeFrame,
                "a later bounds callback changing an earlier wrapped polyline cancels the camera action");
            Forms.Clipboard.SetText("keep polyline freshness clipboard");
            CopyRoundTripUserData.DuplicateAt = 1; CopyRoundTripUserData.TriggeredGeneration = 0;
            CopyRoundTripUserData.OnDuplicateAction = () => polyline[1] = new Point3d(12, 30, 0);
            copy = Copy(assembly, new IGH_Goo?[] { polylineWrapper, cloudHolder });
            Check(!ReflectProperty403875335<bool>(copy, "Success") && CopyRoundTripUserData.TriggeredGeneration > 0 &&
                Forms.Clipboard.GetText() == "keep polyline freshness clipboard",
                "a later duplicate callback changing an earlier wrapped polyline cancels clipboard publication");
            CopyRoundTripUserData.TriggeredGeneration = 0;
            CopyRoundTripUserData.OnDuplicateAction = () => polylineWrapper.Value = new Polyline(polyline);
            copy = Copy(assembly, new IGH_Goo?[] { polylineWrapper, cloudHolder });
            Check(!ReflectProperty403875335<bool>(copy, "Success") && CopyRoundTripUserData.TriggeredGeneration > 0 &&
                Forms.Clipboard.GetText() == "keep polyline freshness clipboard",
                "retargeting a wrapped polyline to equal points during duplication cancels clipboard publication");
            polylineWrapper.Value = polyline;
            CopyRoundTripUserData.TriggeredGeneration = 0;
            CopyRoundTripUserData.OnDuplicateAction = () => group.Objects[1] = new GH_Extrusion { Value = extrusion };
            copy = Copy(assembly, new IGH_Goo?[] { group });
            Check(!ReflectProperty403875335<bool>(copy, "Success") && CopyRoundTripUserData.TriggeredGeneration > 0 &&
                Forms.Clipboard.GetText() == "keep polyline freshness clipboard",
                "replacing a mixed group's extrusion holder during cloud duplication cancels publication");
            group.Objects[1] = extrusionHolder;
            CopyRoundTripUserData.OnDuplicateAction = null;

            beforeFrame = rhino.Views.ActiveView.ActiveViewport.CameraTarget;
            custom.OnBounds = () => cloud[1].Hidden = true;
            staleFrame = Frame(assembly, new IGH_Goo?[] { cloudHolder, custom });
            Check(!ReflectProperty403875335<bool>(staleFrame, "Success") && cloud[1].Hidden &&
                rhino.Views.ActiveView.ActiveViewport.CameraTarget == beforeFrame,
                "a later bounds callback adding cloud hidden flags cancels frame before camera motion");
            cloud.ClearHiddenFlags();
            CopyRoundTripUserData.TriggeredGeneration = 0;
            CopyRoundTripUserData.OnDuplicateAction = () => cloud[1].Hidden = true;
            copy = Copy(assembly, new IGH_Goo?[] { cloudHolder });
            Check(!ReflectProperty403875335<bool>(copy, "Success") && CopyRoundTripUserData.TriggeredGeneration > 0 && cloud[1].Hidden &&
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
            var observerField = highlight.GetType().GetField("_admissionObserver", FlagsInstanceNonPublicPublic)!;
            observerField.SetValue(highlight, (Action<int, object>)((_, __) =>
            {
                stagedBytes = ReflectProperty403875335<long>(highlight, "RetainedBytes");
                polyline[1] = new Point3d(12, 40, 0);
            }));
            Set(highlight, new IGH_Goo?[] { polylineWrapper }, new[] { 0 }, new[] { 0 });
            observerField.SetValue(highlight, null);
            Check(stagedBytes > 128 && Count(highlight) == 0 && Status(highlight).Contains("partial") &&
                ReflectProperty403875335<long>(highlight, "RetainedBytes") == 0,
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
            Check(ReflectProperty403875335<bool>(Frame(assembly, new IGH_Goo?[] { hiddenGroup }), "Success"),
                "a cloud with hidden points remains frameable through a group");
            Forms.Clipboard.SetText("keep hidden cloud clipboard");
            copy = Copy(assembly, new IGH_Goo?[] { hiddenHolder });
            Check(!ReflectProperty403875335<bool>(copy, "Success") && ReflectProperty403875335<int>(copy, "Skipped") == 1 &&
                ReflectProperty403875335<string>(copy, "Error").Contains("hidden flags") && Forms.Clipboard.GetText() == "keep hidden cloud clipboard" &&
                hiddenCloud[1].Hidden, "a cloud with non-roundtrippable hidden flags is visibly skipped without changing the clipboard or source");
            copy = Copy(assembly, new IGH_Goo?[] { hiddenGroup, new GH_Point(Point3d.Origin) });
            Check(ReflectProperty403875335<bool>(copy, "Success") && ReflectProperty403875335<int>(copy, "Count") == 1 && ReflectProperty403875335<int>(copy, "Skipped") == 1 &&
                ReflectProperty403875335<string>(copy, "Detail").Contains("hidden flags") && hiddenCloud[1].Hidden,
                "a mixed action visibly reports a skipped hidden-cloud group while copying the supported point");
            using (var partialArchive = ClipboardArchive())
                Check(ArchiveGeometries(partialArchive).Single() is Rhino.Geometry.Point,
                    "the partial clipboard contains only the supported geometry");

            using var orderedCloud = new PointCloud();
            orderedCloud.Add(new Point3d(1, 0, 0), Color.Aqua);
            orderedCloud.Add(new Point3d(2, 0, 0), Color.Orange);
            orderedCloud.Add(new Point3d(3, 0, 0), Color.Aqua);
            Set(highlight, new IGH_Goo?[] { new GH_PointCloud { Value = orderedCloud } }, new[] { 0 }, new[] { 0 });
            var pairs = ((IEnumerable)ReflectField2106128985<object>(highlight, "_entries")).Cast<object>().ToArray();
            var entry = pairs.Single().GetType().GetProperty("Value")!.GetValue(pairs.Single())!;
            var leaf = ((IEnumerable)ReflectField2106128985<object>(entry, "Leaves")).Cast<object>().Single();
            var snapshot = ReflectField2106128985<object>(leaf, "_value");
            var batches = ((IEnumerable)ReflectProperty403875335<object>(snapshot, "Batches")).Cast<object>().ToArray();
            Check(batches.Select(batch => ReflectProperty403875335<int>(batch, "Argb")).SequenceEqual(new[] { Color.Aqua.ToArgb(), Color.Orange.ToArgb(), Color.Aqua.ToArgb() }) &&
                batches.SelectMany(batch => ReflectProperty403875335<Point3d[]>(batch, "Points")).SequenceEqual(new[] { new Point3d(1, 0, 0), new Point3d(2, 0, 0), new Point3d(3, 0, 0) }) &&
                !ReferenceEquals(ReflectField2106128985<GeometryBase>(leaf, "_owned"), orderedCloud) && ReflectProperty403875335<long>(highlight, "RetainedBytes") <= 16L * 1024 * 1024,
                "owned cloud batches preserve point/color order and stay inside the preview budget");

            using var mediumCloud = new PointCloud();
            for (int i = 0; i < 8193; i++) mediumCloud.Add(new Point3d(i, 0, 0));
            var mediumHolder = new GH_PointCloud { Value = mediumCloud };
            var mediumGroup = new GH_GeometryGroup(); mediumGroup.Objects.Add(mediumHolder);
            Set(highlight, new IGH_Goo?[] { mediumHolder, mediumGroup }, new[] { 0, 1 }, new[] { 0, 1 });
            copy = Copy(assembly, new IGH_Goo?[] { mediumGroup });
            Check(Count(highlight) == 2 && Box(highlight).Contains(new Point3d(8192, 0, 0)) &&
                ReflectProperty403875335<bool>(copy, "Success") && ReflectProperty403875335<int>(copy, "Count") == 1 && ReflectProperty403875335<int>(copy, "Skipped") == 0,
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
            Check(ReflectProperty403875335<bool>(copy, "Success") && ReflectProperty403875335<int>(copy, "Skipped") == 1 && ReflectProperty403875335<bool>(copy, "Exhausted") &&
                PolylineSafetyUserData.Callbacks == 0 && largeCloud.HasUserData,
                "oversized point-cloud preflight reports a partial action before invoking its metadata callback");
            PolylineSafetyUserData.Reset(false);

            await VerifyAdditionalTypedOutput(assembly, values, group);
        }
        finally
        {
            CopyRoundTripUserData.OnDuplicateAction = null;
            PolylineSafetyUserData.Reset(false);
            highlight.GetType().GetField("_admissionObserver", FlagsInstanceNonPublicPublic)!.SetValue(highlight, null);
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
            ReflectProperty403875335<bool>(Frame(assembly, new IGH_Goo?[] { group }), "Success"),
            "loaded references preview and frame both independently and inside a group");
        object copy = Copy(assembly, new IGH_Goo?[] { group });
        Check(ReflectProperty403875335<bool>(copy, "Success") && ReflectProperty403875335<int>(copy, "Count") == 2 && ReflectProperty403875335<int>(copy, "Skipped") == 0,
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
        Check(!ReflectProperty403875335<bool>(copy, "Success") && CopyRoundTripUserData.TriggeredGeneration > 0 &&
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
        Check(snapshot.Rows[0].DisplayValue == "2 points" && snapshot.Rows[1].DisplayValue == "Extr · 1 profile" &&
            PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { TypeName = "Point Cloud" }).RowPositions.SequenceEqual(new[] { 0 }) &&
            PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { TypeName = "Extrusion" }).RowPositions.SequenceEqual(new[] { 1 }) &&
            PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "2 points" }).RowPositions.Contains(0) &&
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

}
