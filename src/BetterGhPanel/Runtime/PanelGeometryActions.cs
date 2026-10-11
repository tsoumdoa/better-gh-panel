using System;
using System.Collections.Generic;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Display;
using Rhino.FileIO;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    internal sealed class PanelGeometryActionResult
    {
        internal PanelGeometryActionResult(bool success, int count, int skipped, string? error = null)
        {
            Success = success;
            Count = count;
            Skipped = skipped;
            Error = error;
        }

        internal bool Success { get; }
        internal int Count { get; }
        internal int Skipped { get; }
        internal string? Error { get; }
    }

    // These actions run on Rhino's UI thread against the component's current goo.
    // Neither action bakes, selects, deletes, or transforms any source objects.
    internal static class PanelGeometryActions
    {
        internal static PanelGeometryActionResult Frame(IEnumerable<IGH_Goo?> items)
        {
            int count = 0, skipped = 0;
            try
            {
                var document = RhinoDoc.ActiveDoc;
                var view = document?.Views.ActiveView;
                if (view == null) return Failure("No active Rhino viewport is available.");
                if (view is RhinoPageView page)
                {
                    if (page.PageIsActive)
                        return Failure("Activate a model viewport or a layout detail to frame geometry.");
                    var details = page.GetDetailViews();
                    if (details != null)
                        foreach (var detail in details)
                            if (detail.IsActive && detail.DetailGeometry.IsProjectionLocked)
                                return Failure("The active layout detail is locked.");
                }

                var viewport = view.ActiveViewport;
                if (viewport.LockedProjection)
                    return Failure("The active Rhino viewport is locked.");

                var bounds = BoundingBox.Empty;
                foreach (var item in items)
                {
                    var box = GetBounds(item);
                    if (!box.IsValid) { skipped++; continue; }
                    bounds.Union(box);
                    count++;
                }
                if (count == 0 || !bounds.IsValid)
                    return Failure("No valid geometry to frame in the selected rows.", skipped);

                double span = Math.Max(bounds.Max.X - bounds.Min.X,
                    Math.Max(bounds.Max.Y - bounds.Min.Y, bounds.Max.Z - bounds.Min.Z));
                if (!RhinoMath.IsValidDouble(span) || !bounds.Center.IsValid)
                    return Failure("The selected geometry is too large to frame.", skipped);

                if (span == 0)
                {
                    // A point has no useful zoom extent. Pan to it at the current scale.
                    viewport.SetCameraTarget(bounds.Center, true);
                }
                else
                {
                    // Also give planar and linear geometry nonzero depth.
                    bounds.Inflate(span * 0.08);
                    if (!bounds.IsValid || !viewport.ZoomBoundingBox(bounds))
                        return Failure("Rhino could not frame the selected geometry.", skipped);
                }
                view.Redraw();
                return new PanelGeometryActionResult(true, count, skipped);
            }
            catch (Exception ex)
            {
                return Failure("Could not frame geometry: " + ex.Message, skipped);
            }
        }

        internal static PanelGeometryActionResult CopyGeometry(IEnumerable<IGH_Goo?> items, UnitSystem units, double tolerance)
        {
            // Rhino uses a different, undocumented pasteboard format on macOS.
            // Do not overwrite the clipboard with data that native Paste cannot read.
            if (!Rhino.Runtime.HostUtils.RunningOnWindows)
                return Failure("Copy geometry into Rhino is currently supported on Windows only.");
            if (units == UnitSystem.CustomUnits)
                return Failure("Copy geometry does not yet support custom Rhino model units.");

            int count = 0, skipped = 0;
            try
            {
                byte[] archive;
                using (var file = new File3dm())
                {
                    file.Settings.ModelUnitSystem = units;
                    if (RhinoMath.IsValidDouble(tolerance) && tolerance > 0)
                        file.Settings.ModelAbsoluteTolerance = tolerance;

                    foreach (var item in items)
                    {
                        using (var geometry = DuplicateGeometry(item))
                        {
                            if (geometry == null) { skipped++; continue; }
                            if (file.Objects.Add(geometry, null) == Guid.Empty)
                                return Failure("Rhino could not prepare one of the selected geometries for copying.", skipped);
                            count++;
                        }
                    }
                    if (count == 0)
                        return Failure("No supported valid geometry to copy in the selected rows.", skipped);

                    archive = file.ToByteArray(new File3dmWriteOptions { Version = 8 });
                }
                if (archive == null || archive.Length == 0)
                    return Failure("Rhino could not create the geometry clipboard archive.", skipped);

                // Rhino 8's native Windows clipboard uses a raw version-8 3DM
                // archive in this format (verified against CopyToClipboard/Paste).
                // Prepare everything first; an empty/failed selection leaves the
                // clipboard intact. No temporary files need to survive for Paste.
                // A fresh Eto object also excludes stale text/older Rhino formats
                // cached by Clipboard.Instance. SetData performs a single replacement.
                using (var clipboard = new Eto.Forms.Clipboard())
                    clipboard.SetData(archive, "Rhino 8 3DM Geometry");
                return new PanelGeometryActionResult(true, count, skipped);
            }
            catch (Exception ex)
            {
                return Failure("Could not copy geometry: " + ex.Message, skipped);
            }
        }

        private static PanelGeometryActionResult Failure(string error, int skipped = 0)
            => new PanelGeometryActionResult(false, 0, skipped, error);

        internal static BoundingBox GetBounds(IGH_Goo? item)
        {
            try
            {
                if (item == null || !item.IsValid) return BoundingBox.Empty;
                if (item is IGH_GeometricGoo geometric) return geometric.Boundingbox;
                var value = Unwrap(item);
                if (value is GeometryBase geometry) return geometry.IsValid ? geometry.GetBoundingBox(false) : BoundingBox.Empty;
                // Wrappers can contain lightweight Rhino geometry as well as goo.
                using (var owned = DuplicateValue(value))
                    return owned != null && owned.IsValid ? owned.GetBoundingBox(false) : BoundingBox.Empty;
            }
            catch (Exception) { return BoundingBox.Empty; }
        }

        private static GeometryBase? DuplicateGeometry(IGH_Goo? item)
        {
            GeometryBase? owned = null;
            try
            {
                if (item == null || !item.IsValid) return null;
                owned = DuplicateValue(Unwrap(item));
                if (owned != null && owned.IsValid) return owned;
            }
            catch (Exception) { }
            owned?.Dispose();
            return null;
        }

        internal static object? Unwrap(IGH_Goo item)
        {
            object? value = item;
            // Avoid cycles in third-party wrappers. Do not cast numbers, vectors,
            // planes, or text to points simply because Grasshopper permits it.
            for (int depth = 0; value is IGH_Goo goo && depth < 8; depth++)
            {
                if (!goo.IsValid) return null;
                value = goo is GH_ObjectWrapper wrapper ? wrapper.Value : goo.ScriptVariable();
                if (ReferenceEquals(value, goo)) return null;
            }
            return value is IGH_Goo ? null : value;
        }

        private static GeometryBase? DuplicateValue(object? value)
        {
            switch (value)
            {
                case Point3d point when point.IsValid: return new Point(point);
                case Line line when line.IsValid: return new LineCurve(line);
                case Arc arc when arc.IsValid: return new ArcCurve(arc);
                case Circle circle when circle.IsValid: return new ArcCurve(circle);
                case Rectangle3d rectangle when rectangle.IsValid: return rectangle.ToNurbsCurve();
                case Box box when box.IsValid: return box.ToBrep();
                case BoundingBox box when box.IsValid: return box.ToBrep();
                case Sphere sphere when sphere.IsValid: return sphere.ToBrep();
                case Polyline polyline when polyline.IsValid: return new PolylineCurve(polyline);
                case GeometryBase geometry when IsStandaloneGeometry(geometry) && geometry.IsValid:
                    return geometry.Duplicate();
                default: return null;
            }
        }

        // Blocks and annotations need document definition/style tables. Excluding
        // them is safer than advertising copies that silently lose their content.
        private static bool IsStandaloneGeometry(GeometryBase geometry) =>
            geometry is Point || geometry is PointCloud || geometry is Curve ||
            geometry is Surface || geometry is Brep || geometry is Mesh || geometry is SubD;
    }
}
