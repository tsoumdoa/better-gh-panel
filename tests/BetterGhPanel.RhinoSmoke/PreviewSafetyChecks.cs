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
