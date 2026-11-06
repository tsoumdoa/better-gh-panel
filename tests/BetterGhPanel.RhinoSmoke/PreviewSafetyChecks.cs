using System;
using System.Drawing;
using System.Reflection;
using System.Threading.Tasks;
using Rhino;

// Every assertion in this fixture is plugin-controlled except the separately
// identified real-solve/document-cache probe at the end.
internal static partial class PreviewSafetyChecks
{
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

}
