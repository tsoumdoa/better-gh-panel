using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

internal static partial class PreviewSafetyChecks
{
    private static IEnumerable<object> PreviewLeaves(object highlight)
    {
        foreach (object pair in (IEnumerable)Field<object>(highlight, "_entries"))
        {
            object entry = pair.GetType().GetProperty("Value")!.GetValue(pair)!;
            foreach (object leaf in (IEnumerable)Field<object>(entry, "Leaves")) yield return leaf;
        }
    }

    private static void RetireNow(object highlight) =>
        Method(highlight, "ReleaseOnIdle").Invoke(highlight, new object?[] { null, EventArgs.Empty });

    private static void VerifyPostCallbackRetirement(GH_Component component, object highlight, IGH_PreviewArgs args)
    {
        var regions = ConsumptionRegions();
        using var mesh = Mesh.CreateFromBox(regions[0], 1, 1, 1);
        using var brep = new Box(regions[2]).ToBrep();
        using var control = Mesh.CreateFromBox(regions[3], 1, 1, 1);
        using var subd = SubD.CreateFromMesh(control) ?? throw new InvalidOperationException("SubD consumption fixture failed");
        Set(highlight, new IGH_Goo?[] { new GH_Mesh(mesh), new GH_Box(new Box(regions[1])), new GH_Brep(brep), new GH_SubD(subd) },
            new[] { 0, 1, 2, 3 }, new[] { 0, 1, 2, 3 });
        Check(Count(highlight) == 4, "fixed mesh, box, Brep and SubD are admitted at the consumption seam");
        var leaves = PreviewLeaves(highlight).ToArray();
        var wires = leaves.SelectMany(leaf => Field<NurbsCurve[]?>(leaf, "_subdWires") ?? Array.Empty<NurbsCurve>()).ToArray();
        Check(wires.Length > 0 && wires.All(curve => !curve.Disposed && curve.IsValid),
            "SubD retains independent bounded limit-edge curves before display");
        // Clear again to prove that drawing no longer needs SubDDisplay's pointer.
        foreach (var ownedSubd in OwnedGeometry(highlight).OfType<SubD>()) ownedSubd.ClearEvaluationCache();
        int flushes = Property<int>(highlight, "CompletedDisplayFlushes");
        var coloredArgs = new ColoredPreviewArgs(args, args.ShadeMaterial_Selected, System.Drawing.Color.Cyan);
        component.DrawViewportMeshes(coloredArgs);
        component.DrawViewportWires(coloredArgs);
        Check(Property<int>(highlight, "CompletedDisplayFlushes") == flushes + 2 && Field<int>(highlight, "_activeDraws") == 0,
            "both component callbacks explicitly flush before releasing their draw guards");
        // Installed RhinoCommon implementation check, separate from native proof.
        var cache = Field<object>(args.Display, "m_cache");
        Check(((ICollection)Field<object>(cache, "m_meshes")).Count == 0,
            "RhinoCommon's managed mesh cache is empty after the component returns");
        Check(leaves.Where(leaf => Field<object?>(leaf, "_owned") is Brep || Field<object?>(leaf, "_owned") is SubD)
                .All(leaf => Field<Mesh[]?>(leaf, "_generatedMeshes")?.Length > 0),
            "both Brep and SubD submit independent shaded meshes");
        var resources = OwnedGeometry(highlight).Concat(OwnedBoxMeshes(highlight))
            .Concat(wires).Concat(leaves.SelectMany(leaf => Field<Mesh[]?>(leaf, "_generatedMeshes") ?? Array.Empty<Mesh>())).ToArray();
        Check(resources.OfType<Mesh>().Count() >= 4, "fixed, box, Brep and SubD meshes reached the retirement seam");
        Method(component, "ClearHighlight").Invoke(component, Array.Empty<object>());
        Check(resources.All(value => !value.Disposed), "cleared resources survive until the explicit retirement injection");
        // Crucial ordering: component callbacks have RETURNED, but the enclosing
        // DisplayConduit callback has NOT. This is deliberately not custom.OnMesh.
        RetireNow(highlight);
        Check(resources.All(value => value.Disposed) && Property<long>(highlight, "RetainedBytes") == 0,
            "retirement after component return releases the flushed resources inside the enclosing callback");
        args.Display.Flush();
        // The containing fixture must still capture pixels and return normally.
        // This seam is regression evidence, not a universal native/GPU lifetime proof.
    }

    private static BoundingBox[] ConsumptionRegions() => new[]
    {
        new BoundingBox(new Point3d(-3.6, -3.6, -1), new Point3d(-2.6, -2.6, 1)),
        new BoundingBox(new Point3d(2.6, -3.6, -1), new Point3d(3.6, -2.6, 1)),
        new BoundingBox(new Point3d(-3.6, 2.6, -1), new Point3d(-2.6, 3.6, 1)),
        new BoundingBox(new Point3d(2.6, 2.6, -1), new Point3d(3.6, 3.6, 1))
    };

    private static void VerifyRejectedMeshing(GH_Component component, object highlight, IGH_PreviewArgs args)
    {
        using var brep = new Box(new BoundingBox(new Point3d(-1, -1, -1), new Point3d(1, 1, 1))).ToBrep();
        Set(highlight, new IGH_Goo?[] { new GH_Brep(brep) }, new[] { 0 }, new[] { 0 });
        var leafType = PreviewLeaves(highlight).Single().GetType();
        var attempts = leafType.GetProperty("GeneratedMeshAttempts", Static)!;
        int Attempts() => (int)attempts.GetValue(null)!;
        using var rejected = new MeshingParameters(args.MeshingParameters) { Tolerance = 1e-12 };
        var rejectedArgs = new MeshingPreviewArgs(args, rejected);
        int before = Attempts();
        component.DrawViewportMeshes(rejectedArgs);
        component.DrawViewportMeshes(rejectedArgs);
        component.DrawViewportMeshes(rejectedArgs);
        Check(Attempts() == before + 1 && !string.IsNullOrEmpty(Status(highlight)),
            "unchanged deterministic meshing rejection is cached and remains visibly partial");
        component.DrawViewportMeshes(args);
        var leaf = PreviewLeaves(highlight).Single();
        Check(Attempts() == before + 2 && Field<Mesh[]?>(leaf, "_generatedMeshes")?.Length > 0,
            "changed settings retry a previously rejected mesh and admit an ordinary result");
        using var changed = new MeshingParameters(args.MeshingParameters) { GridMinCount = 64, GridMaxCount = 64 };
        var changedArgs = new MeshingPreviewArgs(args, changed);
        long staged = Field<long>(highlight, "_stagedBytes");
        try
        {
            // Reserve the remaining capacity without allocating native geometry.
            highlight.GetType().GetField("_stagedBytes", Instance)!.SetValue(highlight,
                staged + 16L * 1024 * 1024 - Property<long>(highlight, "RetainedBytes"));
            component.DrawViewportMeshes(changedArgs);
            Check(Attempts() == before + 2, "retirement capacity failure performs no new meshing");
        }
        finally { highlight.GetType().GetField("_stagedBytes", Instance)!.SetValue(highlight, staged); }
        component.DrawViewportMeshes(changedArgs);
        Check(Attempts() == before + 3, "the same settings retry when retirement capacity becomes available");
        Method(component, "ClearHighlight").Invoke(component, Array.Empty<object>());
        RetireNow(highlight);
    }

    private static void VerifyFailedDisplayFlush(GH_Component component, object highlight, IGH_PreviewArgs args)
    {
        using var mesh = Mesh.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(1, 1, 1)), 1, 1, 1);
        Set(highlight, new IGH_Goo?[] { new GH_Mesh(mesh) }, new[] { 0 }, new[] { 0 });
        var owned = OwnedGeometry(highlight).Single();
        var seam = highlight.GetType().GetProperty("BeforeDisplayFlush", Instance)!;
        bool guarded = false;
        seam.SetValue(highlight, (Action)(() =>
        {
            Method(component, "ClearHighlight").Invoke(component, Array.Empty<object>());
            RetireNow(highlight);
            guarded = Field<int>(highlight, "_activeDraws") > 0 && !owned.Disposed;
            throw new InvalidOperationException("Injected display flush failure");
        }));
        try
        {
            component.DrawViewportMeshes(args);
            RetireNow(highlight);
            int calls = Property<int>(highlight, "DrawMeshCalls");
            component.DrawViewportMeshes(args);
            Check(guarded && !owned.Disposed && Property<long>(highlight, "RetainedBytes") > 0 &&
                Property<bool>(highlight, "RetirementPending") && Property<int>(highlight, "DrawMeshCalls") == calls &&
                Status(highlight).Contains("flush failed"),
                "failed flush retains accounted resources across idle and suppresses subsequent draws");
        }
        finally
        {
            seam.SetValue(highlight, null);
            // Only the fixture can undo its artificial failure, after flushing
            // the SAME live pipeline. Production deliberately has no reset API.
            args.Display.Flush();
            highlight.GetType().GetField("_displayFlushFailed", Instance)!.SetValue(highlight, false);
            RetireNow(highlight);
        }
        Check(owned.Disposed, "the failed-flush fixture releases its own resources after an explicit successful flush");
    }

    private sealed class MeshingPreviewArgs : IGH_PreviewArgs
    {
        private readonly IGH_PreviewArgs _inner;
        internal MeshingPreviewArgs(IGH_PreviewArgs inner, MeshingParameters parameters)
        { _inner = inner; MeshingParameters = parameters; }
        public int DefaultCurveThickness => _inner.DefaultCurveThickness;
        public System.Drawing.Color WireColour => _inner.WireColour;
        public System.Drawing.Color WireColour_Selected => _inner.WireColour_Selected;
        public Rhino.Display.DisplayMaterial ShadeMaterial => _inner.ShadeMaterial;
        public Rhino.Display.DisplayMaterial ShadeMaterial_Selected => _inner.ShadeMaterial_Selected;
        public Rhino.Display.RhinoViewport Viewport => _inner.Viewport;
        public Rhino.Display.DisplayPipeline Display => _inner.Display;
        public GH_Document Document => _inner.Document;
        public MeshingParameters MeshingParameters { get; }
    }
}
