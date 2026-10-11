using System;
using System.Collections.Generic;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace BetterGhPanel.Runtime
{
    // Participates in the component's own preview so Grasshopper controls hidden
    // objects, preview modes, document switches, and the user's selected colors.
    // Like normal GH preview, this borrows goo on the UI thread; it never bakes,
    // modifies or disposes source geometry.
    internal sealed class PanelGeometryHighlight
    {
        private readonly List<IGH_PreviewData> _normal = new List<IGH_PreviewData>();
        private readonly List<IGH_PreviewData> _selected = new List<IGH_PreviewData>();

        internal bool HasSelection { get; private set; }
        internal int Count => _selected.Count;
        internal int PreviewCount => _normal.Count + _selected.Count;
        internal BoundingBox ClippingBox { get; private set; } = BoundingBox.Empty;

        internal void Set(IReadOnlyList<IGH_Goo?> sourceItems, IReadOnlyList<int> previewPositions, IReadOnlyList<int> selectedPositions)
        {
            Clear();
            var selected = new HashSet<int>();
            foreach (var position in selectedPositions)
                if (position >= 0 && position < sourceItems.Count) selected.Add(position);
            HasSelection = selected.Count > 0;
            var bounds = BoundingBox.Empty;
            foreach (int position in previewPositions)
            {
                if (position < 0 || position >= sourceItems.Count) continue;
                var item = sourceItems[position];
                if (item == null) continue;
                try
                {
                    if (!item.IsValid) continue;
                    // Custom preview goo may supply its own bounds without being
                    // geometric goo. Validate wrappers before converting them so
                    // numbers and vectors cannot become artificial points.
                    var preview = item as IGH_PreviewData;
                    var box = preview?.ClippingBox ?? PanelGeometryActions.GetBounds(item);
                    if (!box.IsValid) box = PanelGeometryActions.GetBounds(item);
                    if (!box.IsValid) continue;
                    preview ??= GH_Convert.ToGeometricGoo(PanelGeometryActions.Unwrap(item)) as IGH_PreviewData;
                    if (preview == null) continue;
                    var clippingBox = preview.ClippingBox;
                    bounds.Union(clippingBox.IsValid ? clippingBox : box);
                    (selected.Contains(position) ? _selected : _normal).Add(preview);
                }
                catch (Exception) { } // One third-party goo cannot disable the other previews.
            }
            ClippingBox = bounds;
        }

        internal void Clear()
        {
            HasSelection = false;
            _normal.Clear();
            _selected.Clear();
            ClippingBox = BoundingBox.Empty;
        }

        internal void DrawWires(IGH_PreviewArgs args, bool componentSelected)
        {
            var normal = new GH_PreviewWireArgs(args.Viewport, args.Display, args.WireColour, args.DefaultCurveThickness);
            int thickness = args.DefaultCurveThickness + (componentSelected ? 0 : CentralSettings.PreviewSelectionThickening);
            var selected = new GH_PreviewWireArgs(args.Viewport, args.Display, args.WireColour_Selected, thickness);
            foreach (var item in _normal)
                try { item.DrawViewportWires(normal); } catch (Exception) { }

            bool pushed = false;
            try
            {
                if (!componentSelected && _selected.Count > 0 && args.Display != null)
                {
                    // Grasshopper pulls selected component wires slightly forward
                    // in clip depth so coincident upstream previews cannot cover
                    // them. Apply the same bias to row selections, keeping normal
                    // depth testing and all source coordinates intact.
                    var clipBias = Transform.Identity;
                    clipBias.M22 = 0.995;
                    clipBias.M23 = 0.01;
                    var bias = args.Viewport.GetTransform(CoordinateSystem.Clip, CoordinateSystem.World) * clipBias *
                        args.Viewport.GetTransform(CoordinateSystem.World, CoordinateSystem.Clip);
                    args.Display.PushModelTransform(bias * args.Display.ModelTransform);
                    pushed = true;
                }
                foreach (var item in _selected)
                    try { item.DrawViewportWires(selected); } catch (Exception) { }
            }
            finally
            {
                if (pushed) args.Display.PopModelTransform();
            }
        }

        internal void DrawMeshes(IGH_PreviewArgs args)
        {
            if (args.MeshingParameters == null) return;
            var normal = new GH_PreviewMeshArgs(args.Viewport, args.Display, args.ShadeMaterial, args.MeshingParameters);
            var selected = new GH_PreviewMeshArgs(args.Viewport, args.Display, args.ShadeMaterial_Selected, args.MeshingParameters);
            foreach (var item in _normal)
                try { item.DrawViewportMeshes(normal); } catch (Exception) { }
            foreach (var item in _selected)
                try { item.DrawViewportMeshes(selected); } catch (Exception) { }
        }
    }
}
