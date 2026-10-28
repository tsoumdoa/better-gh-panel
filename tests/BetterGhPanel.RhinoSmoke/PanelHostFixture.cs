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

// Shared setup for the native ownership and clipboard regressions.
internal static class PanelHostFixture
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static async Task RunWithEditor(Assembly assembly, Func<Assembly, Task> check)
    {
        // Scheduled solutions need the host editor's native handle, even while
        // the test's Rhino window remains hidden.
        var previous = Grasshopper.Instances.DocumentEditor;
        using var owned = previous == null ? new Grasshopper.GUI.GH_DocumentEditor() : null;
        var editor = previous ?? owned!;
        _ = editor.Handle;
        var setEditor = typeof(Grasshopper.Instances).GetMethod("SetDocumentEditor", BindingFlags.Static | BindingFlags.NonPublic)!;
        setEditor.Invoke(null, new object?[] { editor });
        try
        {
            await check(assembly);
            Console.WriteLine("PASS: Output fixture completed.");
        }
        finally { setEditor.Invoke(null, new object?[] { previous }); }
    }

    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name, Instance)!.GetValue(owner)!;
    private static async Task Wait(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed.TotalSeconds > 20) throw new TimeoutException("Output check did not finish");
            await Task.Delay(10);
        }
    }
    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);

    internal sealed class Fixture : IDisposable
    {
        internal readonly GH_Document Document = new GH_Document { Enabled = true };
        internal readonly Param_GenericObject Source = new Param_GenericObject(), Paired = new Param_GenericObject();
        internal readonly Param_GenericObject[] Sinks = Enumerable.Range(0, 4).Select(_ => new Param_GenericObject()).ToArray();
        internal readonly GH_Component Component;
        internal int Solutions { get; private set; }
        internal object Session => PanelTestAdapter.Session(Component);
        internal PanelQueryResult Result => Property<PanelQueryResult>(Session, "Result");
        private object Selection => Property<object>(Component, "RowSelection");
        internal int SelectedCount => Property<int>(Selection, "Count");
        internal Fixture(Assembly assembly)
        {
            GH_Document.EnableSolutions = true;
            Component = (GH_Component)Activator.CreateInstance(assembly.GetType("BetterGhPanel.BetterPanelComponent", true)!)!;
            Source.CreateAttributes(); Paired.CreateAttributes(); Component.CreateAttributes();
            Component.Attributes.Pivot = new PointF(40, 40);
            Document.AddObject(Source, false); Document.AddObject(Paired, false); Document.AddObject(Component, false);
            Document.SolutionEnd += (_, __) => Solutions++;
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
        internal void Scope(PanelOutputScope scope) => Component.GetType().GetMethod("SetOutputScope", Instance)!.Invoke(Component, new object[] { scope });
        internal void Select(int position, bool toggle = false, bool preserveSelected = false) => Selection.GetType().GetMethod("Select", Instance)!
            .Invoke(Selection, new object[] { Result, position, toggle, false, preserveSelected });
        internal void SelectAt(int position, bool toggle = false, bool preserveSelected = false)
        {
            Check(Result.TryGetViewIndex(position, out int index), "Native selected row is visible in the current view");
            Selection.GetType().GetMethod("SelectAt", Instance)!.Invoke(Selection, new object[] { Result, position, index, toggle, false, preserveSelected });
        }
        internal void ClearSelection() => Selection.GetType().GetMethod("Clear", Instance)!.Invoke(Selection, Array.Empty<object>());
        internal void SelectBranch(string path) => Selection.GetType().GetMethod("SelectAll", Instance)!
            .Invoke(Selection, new object[] { Result, path, true });
        internal async Task Ready(Func<bool> condition, bool allowError = false)
        {
            try
            {
                await Wait(() => !Property<bool>(Session, "IsBusy") && (allowError || Property<bool>(Session, "HasCurrentResult")) && condition());
            }
            catch (TimeoutException)
            {
                Console.Error.WriteLine("Output state: " + string.Join("; ", Enumerable.Range(0, 4).Select(i => i + "=" + Values(i))) +
                    "; busy=" + Property<bool>(Session, "IsBusy") + "; current=" + Property<bool>(Session, "HasCurrentResult") +
                    "; error=" + Property<string?>(Session, "Error") + "; matches=" + Result.RowPositions.Count);
                throw;
            }
        }
        public void Dispose() { Component.DocumentContextChanged(Document, GH_DocumentContext.Close); Document.Dispose(); }
    }
}
