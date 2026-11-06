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

}
