using System;
using System.Drawing;
using System.Reflection;
using System.Threading.Tasks;
using Rhino;


internal static partial class PreviewSafetyChecks
{
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
            VerifyDenseSelectionGestures(highlight);
            await VerifyCrossComponentActions(assembly, rhino);
        }
        finally
        {
            if (previous != null) rhino.Views.ActiveView = previous;
            view.Close();
            rhino.Modified = modified;
        }
    }

}
