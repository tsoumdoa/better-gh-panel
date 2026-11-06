using static PanelTestAdapter;
using static SmokeAssertions;
using System;
using System.Reflection;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Forms = System.Windows.Forms;


internal static partial class PreviewSafetyChecks
{
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
            Check(PropertyValue<bool>(copied, "Success") && PropertyValue<int>(copied, "Count") == 1 && PropertyValue<int>(copied, "Skipped") == 1 &&
                source.HasUserData && PolylineSafetyUserData.Callbacks > 0 && PropertyValue<string>(copied, "Detail").Contains("callback failed"),
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
            Check(Count(highlight) == 0 && Status(highlight).Contains("partial") && !PropertyValue<bool>(polyCopy, "Success") &&
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
            Check(Count(highlight) == 0 && Status(highlight).Contains("partial") && frame.IsValid && !PropertyValue<bool>(result, "Success") &&
                PropertyValue<int>(result, "Skipped") == 2 && brep.UserData.Count == 0 && brep.Curves3D[0].HasUserData &&
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

}
