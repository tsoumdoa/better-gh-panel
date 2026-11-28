using static PanelTestAdapter;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

// Shared real-solve setup for the native fixtures.
internal static class PanelHostFixture
{
    private static async Task Wait(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed.TotalSeconds > 20) throw new TimeoutException("Output check did not finish");
            await Task.Delay(10);
        }
    }

    internal sealed class Fixture : IDisposable
    {
        internal readonly GH_Document Document = new GH_Document { Enabled = true };
        internal readonly Param_GenericObject Source = new Param_GenericObject(), Paired = new Param_GenericObject();
        internal readonly Param_GenericObject[] Sinks = Enumerable.Range(0, 4).Select(_ => new Param_GenericObject()).ToArray();
        internal readonly GH_Component Component;
        internal object Session => PanelTestAdapter.Session(Component);
        internal PanelQueryResult Result => PropertyValue<PanelQueryResult>(Session, "Result");
        private object Selection => PropertyValue<object>(Component, "RowSelection");
        internal Fixture(Assembly assembly)
        {
            GH_Document.EnableSolutions = true;
            Component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
            Source.CreateAttributes(); Paired.CreateAttributes(); Component.CreateAttributes();
            Component.Attributes.Pivot = new PointF(40, 40);
            Document.AddObject(Source, false); Document.AddObject(Paired, false); Document.AddObject(Component, false);
            var primary = new[] { new double[] { 12, 4, 18, 12, 7 }, new double[] { 3, 20 }, Array.Empty<double>() };
            int letter = 0;
            for (int b = 0; b < primary.Length; b++)
            {
                var path = new GH_Path(7, b);
                Source.PersistentData.EnsurePath(path); Paired.PersistentData.EnsurePath(path);
                foreach (double value in primary[b])
                {
                    Source.PersistentData.Append(new GH_Number(value), path);
                    Paired.PersistentData.Append(new GH_String(((char)('A' + letter++)).ToString()), path);
                }
            }
            Component.Params.Input[0].AddSource(Source); Component.Params.Input[1].AddSource(Paired);
            for (int i = 0; i < Sinks.Length; i++)
            {
                Sinks[i].CreateAttributes(); Document.AddObject(Sinks[i], false); Sinks[i].AddSource(Component.Params.Output[i]);
            }
        }
        internal void Solve() { Source.ExpireSolution(false); Paired.ExpireSolution(false); Document.NewSolution(false, GH_SolutionMode.Silent); }
        internal string Values(int index) => string.Join(",", Sinks[index].VolatileData.AllData(false).Select(value => value?.ToString() ?? "<null>"));
        internal void Query(PanelQueryOptions options) => Session.GetType().GetMethod("UpdateQuery")!.Invoke(Session, new object[] { options });
        internal void Select(int position, bool toggle = false, bool preserveSelected = false) => Selection.GetType().GetMethod("Select", AnyInstance)!
            .Invoke(Selection, new object[] { Result, position, toggle, false, preserveSelected });
        internal async Task Ready(Func<bool> condition, bool allowError = false)
        {
            try
            {
                await Wait(() => !PropertyValue<bool>(Session, "IsBusy") && (allowError || PropertyValue<bool>(Session, "HasCurrentResult")) && condition());
            }
            catch (TimeoutException)
            {
                Console.Error.WriteLine("Output state: " + string.Join("; ", Enumerable.Range(0, 4).Select(i => i + "=" + Values(i))) +
                    "; busy=" + PropertyValue<bool>(Session, "IsBusy") + "; current=" + PropertyValue<bool>(Session, "HasCurrentResult") +
                    "; error=" + PropertyValue<string?>(Session, "Error") + "; matches=" + Result.RowPositions.Count);
                throw;
            }
        }
        public void Dispose() { Component.DocumentContextChanged(Document, GH_DocumentContext.Close); Document.Dispose(); }
    }
}
