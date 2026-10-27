using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using BetterGhPanel.Core;
using Grasshopper.GUI.Canvas;
using Grasshopper.GUI.StringDisplay;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

// These comparisons run a real document solution with both panels connected to
// the same source. The stock panel's collected GH_String values are its content;
// never use the original group's name as the oracle after host collection.
internal static class GeometryGroupChecks
{
    private const int ItemCount = 22;
    private const int GroupCount = 11;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    internal static Task RunPersistent(Assembly assembly) => OutputChecks.RunWithEditor(assembly, value => Run(value, volatileSource: false));
    internal static Task RunVolatile(Assembly assembly) => OutputChecks.RunWithEditor(assembly, value => Run(value, volatileSource: true));

    private static async Task Run(Assembly assembly, bool volatileSource)
    {
        var original = Values();
        var originalNames = original.Take(GroupCount).Cast<GH_GeometryGroup>().Select(group => group.Name).ToArray();
        Check(original.Length == ItemCount && original.Take(GroupCount).All(value => value is GH_GeometryGroup) &&
            ((GH_GeometryGroup)original[10]).Objects.Count == 3 && ((GH_GeometryGroup)original[10]).Objects[1] == null,
            "The fixture contains eleven groups and a three-slot nested group with a null slot");

        using var fixture = new OutputChecks.Fixture(assembly);
        fixture.Source.PersistentData.Clear(); fixture.Paired.PersistentData.Clear();
        fixture.Paired.PersistentData.AppendRange(Enumerable.Range(0, ItemCount).Select(i => (IGH_Goo)new GH_Integer(1000 + i)), new GH_Path(0));
        IGH_Param source;
        if (volatileSource)
        {
            var producer = new VolatileSource(original);
            producer.CreateAttributes(); fixture.Document.AddObject(producer, false);
            fixture.Component.Params.Input[0].RemoveAllSources();
            fixture.Component.Params.Input[0].AddSource(producer);
            source = producer;
        }
        else
        {
            fixture.Source.PersistentData.AppendRange(original, new GH_Path(0));
            source = fixture.Source;
        }
        var stock = new GH_Panel();
        stock.CreateAttributes(); fixture.Document.AddObject(stock, false); stock.AddSource(source);
        fixture.Solve();
        await fixture.Ready(() => fixture.Result.Snapshot.Rows.Count == ItemCount &&
            fixture.Sinks[0].VolatileData.DataCount == ItemCount && stock.VolatileData.DataCount == ItemCount);

        var delivered = fixture.Component.Params.Input[0].VolatileData.get_Branch(0).Cast<IGH_Goo>().ToArray();
        var produced = source.VolatileData.get_Branch(0).Cast<IGH_Goo>().ToArray();
        var stockValues = stock.VolatileData.get_Branch(0).Cast<GH_String>().ToArray();
        var rendered = RenderStock(stock, fixture.Document);
        Check(delivered.Length == ItemCount && produced.Length == ItemCount && stockValues.Length == ItemCount && rendered.Length == ItemCount,
            "The source, Panel+ input, rendered stock panel and downstream output all receive twenty-two items in one branch");
        for (int i = 0; i < ItemCount; i++)
        {
            if (i < GroupCount)
            {
                var inputGroup = (GH_GeometryGroup)delivered[i];
                var sourceGroup = (GH_GeometryGroup)produced[i];
                Console.WriteLine($"GROUP_DELIVERY source={(volatileSource ? "volatile" : "persistent")} index={i} " +
                    $"originalName={JsonSerializer.Serialize(originalNames[i])} originalCount={((GH_GeometryGroup)original[i]).Objects.Count} " +
                    $"sourceName={JsonSerializer.Serialize(sourceGroup.Name)} deliveredName={JsonSerializer.Serialize(inputGroup.Name)} " +
                    $"deliveredCount={inputGroup.Objects.Count} stock={JsonSerializer.Serialize(((GH_String)stockValues[i]).Value)}");
            }
            string stockText = ((GH_String)stockValues[i]).Value;
            string deliveredText = ((IGH_Goo)delivered[i]).ToString()!;
            var snapshot = fixture.Result.Snapshot;
            string panelText = PanelTestAdapter.DisplayValue(fixture.Component, snapshot, i);
            Check(stockText == deliveredText && panelText == stockText && rendered[i] == PanelValuePresentation.Visualize(panelText) &&
                PanelTestAdapter.CopyValue(fixture.Component, snapshot, i) == stockText,
                $"Item {i}: stock rendered text, the delivered input, Panel+ presentation and clipboard-free value copy agree");
        }
        Check(originalNames.SequenceEqual(original.Take(GroupCount).Cast<GH_GeometryGroup>().Select(group => group.Name)),
            "Collection does not rewrite the original group names");
        var nested = (GH_GeometryGroup)delivered[10];
        Check(nested.Objects.Count == 3 && nested.Objects[0] is GH_GeometryGroup inner && inner.Objects.Count == 2 && nested.Objects[1] == null,
            "The delivered nested group counts its direct children including the null slot, not recursive descendants");
        Check(((GH_String)stockValues[13]).Value == "12,34,56" && ((GH_String)stockValues[14]).Value == "12,34,56 (128)" &&
            ((GH_String)stockValues[15]).Value == "12,34,56 (0)" && ((GH_String)stockValues[20]).Value == "Red" &&
            ((GH_String)stockValues[21]).Value == "Transparent",
            "The actual stock panel renders opaque, alpha and named colours using Grasshopper's conventions");
        Check(((GH_String)stockValues[11]).Value == "" && ((GH_String)stockValues[12]).Value == "<empty>" &&
            PanelValuePresentation.Visualize(PanelTestAdapter.DisplayValue(fixture.Component, fixture.Result.Snapshot, 11)) == "<empty>",
            "Empty text and literal <empty> share a visual marker but retain different semantic stock and copy values");

        if (volatileSource)
        {
            Check(((GH_GeometryGroup)delivered[5]).Name == "Facade A" && ((GH_GeometryGroup)delivered[6]).Name == "Facade B" &&
                ((GH_GeometryGroup)delivered[7]).Name == "Facade A" && ((GH_GeometryGroup)delivered[9]).Name == "  ",
                "The volatile source delivers distinct names and whitespace without a persistent-data duplicate");
            Check(((GH_String)stockValues[0]).Value == "Empty Group" && ((GH_String)stockValues[1]).Value == "Empty Group (Atrium zero)" &&
                ((GH_String)stockValues[2]).Value == "Group with 1 object" && ((GH_String)stockValues[3]).Value == "Group (Atrium one) with 1 object" &&
                ((GH_String)stockValues[4]).Value == "Group with 3 objects", "Named and unnamed groups cover zero, one and many direct members");
            Check(((GH_String)stockValues[5]).Value == "Group (Facade A) with 2 objects" && ((GH_String)stockValues[6]).Value == "Group (Facade B) with 2 objects" &&
                ((GH_String)stockValues[7]).Value == "Group (Facade A) with 1 object" && ((GH_String)stockValues[9]).Value == "Empty Group (  )",
                "Stock summaries distinguish name, direct count and a whitespace-only name");
            Check(fixture.Sinks[0].VolatileData.get_Branch(0).Cast<IGH_Goo>().Take(GroupCount).All(value => value is GH_GeometryGroup) &&
                fixture.Sinks[0].VolatileData.get_Branch(0)[13] is GH_Colour,
                "Initial solved D output preserves delivered geometry-group and colour types");
            await VerifyVolatileQueries(fixture);
            Check(SmokeAssertions.Count == 46, "The volatile geometry-group fixture executed its 46 comparison and behavior assertions");
        }
        else
        {
            // Some Rhino versions duplicate persistent groups and drop Name. Search
            // the delivered stock text, and never assert an upstream lost name.
            int[] expected = Enumerable.Range(0, GroupCount)
                .Where(i => ((GH_String)stockValues[i]).Value.Contains("Facade A", StringComparison.OrdinalIgnoreCase)).ToArray();
            fixture.Query(new PanelQueryOptions { Search = "Facade A" });
            await fixture.Ready(() => fixture.Result.RowPositions.SequenceEqual(expected) && fixture.Sinks[0].VolatileData.DataCount == expected.Length);
            Check(fixture.Result.RowPositions.SequenceEqual(expected) &&
                fixture.Sinks[0].VolatileData.AllData(true).All(item => item is GH_GeometryGroup),
                "Persistent-source name search and typed D membership follow only names delivered to the stock panel");
            fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = @"^Group.*with 2 objects$" });
            int[] two = Enumerable.Range(0, GroupCount).Where(i => System.Text.RegularExpressions.Regex.IsMatch(((GH_String)stockValues[i]).Value, @"^Group.*with 2 objects$")).ToArray();
            await fixture.Ready(() => fixture.Result.RowPositions.SequenceEqual(two) && fixture.Sinks[0].VolatileData.DataCount == two.Length);
            Check(two.Length > 0 && fixture.Sinks[0].VolatileData.AllData(true).All(item => item is GH_GeometryGroup),
                "Persistent-source regex and typed D membership follow the actual delivered two-child groups");
            Check(SmokeAssertions.Count == 30, "The persistent geometry-group fixture executed its 30 comparison and behavior assertions");
        }
    }

    private static async Task VerifyVolatileQueries(OutputChecks.Fixture fixture)
    {
        var component = fixture.Component;
        fixture.Query(new PanelQueryOptions { Search = "Facade A" });
        await fixture.Ready(() => fixture.Result.RowPositions.SequenceEqual(new[] { 5, 7, 8 }) && fixture.Sinks[0].VolatileData.DataCount == 3);
        Check(fixture.Result.RowPositions.SequenceEqual(new[] { 5, 7, 8 }), "Text search finds the delivered group name, including equal-name groups of different counts");
        fixture.Scope(PanelOutputScope.Filtered);
        await fixture.Ready(() => fixture.Values(3) == "1005,1007,1008");
        Check(Types(fixture, 0, typeof(GH_GeometryGroup)) && Types(fixture, 2, typeof(GH_Boolean)) && Types(fixture, 3, typeof(GH_Integer)) &&
            Flags(fixture).SequenceEqual(Enumerable.Range(0, ItemCount).Select(i => i == 5 || i == 7 || i == 8)),
            "With Create set off, filtered D stays geometry, B is a typed source mask and F carries matching integer partners");
        fixture.Select(5);
        fixture.Scope(PanelOutputScope.Selected);
        await fixture.Ready(() => fixture.Sinks[1].VolatileData.DataCount == 1 && fixture.Values(3) == "1005");
        Check(Types(fixture, 1, typeof(GH_GeometryGroup)) && Flags(fixture).SequenceEqual(Enumerable.Range(0, ItemCount).Select(i => i == 5)),
            "Selected scope publishes typed S and aligns B/F to the selected original group");

        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = @"^Group \(Facade A\) with 2 objects$" });
        await fixture.Ready(() => fixture.Result.RowPositions.SequenceEqual(new[] { 5, 8 }) && fixture.Sinks[0].VolatileData.DataCount == 2);
        Check(fixture.Values(3) == "1005" && Types(fixture, 0, typeof(GH_GeometryGroup)) && Types(fixture, 1, typeof(GH_GeometryGroup)),
            "Regex finds both equal named groups while selected D/S remain typed and F remains aligned");
        PanelTestAdapter.TreeTools(component, 0, false, false, false, true);
        await fixture.Ready(() => fixture.Result.RowPositions.SequenceEqual(new[] { 5 }) && fixture.Sinks[0].VolatileData.DataCount == 1);
        // Changing tree tools expires the input and clears transient selection.
        fixture.Select(5);
        await fixture.Ready(() => fixture.Sinks[1].VolatileData.DataCount == 1 && fixture.Values(3) == "1005");
        Check(Types(fixture, 0, typeof(GH_String)) && Types(fixture, 1, typeof(GH_String)) && Types(fixture, 2, typeof(GH_Boolean)) &&
            Types(fixture, 3, typeof(GH_Integer)) && Flags(fixture).SequenceEqual(Enumerable.Range(0, ItemCount).Select(i => i == 5)),
            "With Create set on, D/S contain displayed strings and typed B/F retain the representative's membership");
        Check(fixture.Result.SetOccurrencesAt(5)!.Count == 2 && ((GH_String)fixture.Sinks[0].VolatileData.get_Branch(0)[0]!).Value == "Group (Facade A) with 2 objects" &&
            PanelTestAdapter.CopyValue(component, fixture.Result.Snapshot, 5) == "Group (Facade A) with 2 objects",
            "The equal-summary set records both occurrences and copies the displayed group value without the system clipboard");

        fixture.Scope(PanelOutputScope.Filtered);
        fixture.Query(new PanelQueryOptions());
        await fixture.Ready(() => fixture.Result.RowPositions.Count == 20 && fixture.Sinks[0].VolatileData.DataCount == 20);
        Check(fixture.Result.RowPositions.Contains(5) && fixture.Result.RowPositions.Contains(6) && fixture.Result.RowPositions.Contains(7) &&
            !fixture.Result.RowPositions.Contains(8) && fixture.Result.SetOccurrencesAt(5)!.Count == 2,
            "Create set distinguishes equal-count different-name and equal-name different-count groups, but combines equal summaries");
        Check(fixture.Result.RowPositions.Contains(13) && !fixture.Result.RowPositions.Contains(16) &&
            fixture.Result.RowPositions.Contains(14) && fixture.Result.RowPositions.Contains(15) && fixture.Result.SetOccurrencesAt(13)!.Count == 2,
            "Opaque colour and identical text share a stock-value key, while both alpha colours remain distinct");
        Check(fixture.Result.RowPositions.Contains(11) && fixture.Result.RowPositions.Contains(12) &&
            PanelTestAdapter.CopyValue(component, fixture.Result.Snapshot, 11) == "" &&
            PanelTestAdapter.CopyValue(component, fixture.Result.Snapshot, 12) == "<empty>",
            "Create set and value copy distinguish empty text from the literal visual marker");

        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = "^$" });
        await fixture.Ready(() => fixture.Result.RowPositions.SequenceEqual(new[] { 11 }) && fixture.Sinks[0].VolatileData.DataCount == 1 && fixture.Values(3) == "1011");
        Check(((GH_String)fixture.Sinks[0].VolatileData.get_Branch(0)[0]!).Value == "" && fixture.Values(3) == "1011",
            "Exact-empty regex and downstream F identify only the semantic empty string");
        fixture.Query(new PanelQueryOptions { Search = "<empty>" });
        await fixture.Ready(() => fixture.Result.RowPositions.SequenceEqual(new[] { 12 }) && fixture.Sinks[0].VolatileData.DataCount == 1 && fixture.Values(3) == "1012");
        Check(((GH_String)fixture.Sinks[0].VolatileData.get_Branch(0)[0]!).Value == "<empty>" && fixture.Values(3) == "1012",
            "Literal-marker search and downstream F exclude the semantic empty string");
        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = @"^12,34,56$" });
        await fixture.Ready(() => fixture.Result.RowPositions.SequenceEqual(new[] { 13 }) && fixture.Sinks[0].VolatileData.DataCount == 1 && fixture.Values(3) == "1013");
        Check(fixture.Result.SetOccurrencesAt(13)!.Count == 2 && fixture.Values(3) == "1013" &&
            ((GH_String)fixture.Sinks[0].VolatileData.get_Branch(0)[0]!).Value == "12,34,56",
            "Opaque-colour regex and set group the colour with identical text and retain the colour's partner");
        PanelTestAdapter.TreeTools(component, 0, false, false, false, false);
        await fixture.Ready(() => fixture.Result.RowPositions.SequenceEqual(new[] { 13, 16 }) && fixture.Sinks[0].VolatileData.DataCount == 2 && fixture.Values(3) == "1013,1016");
        Check(fixture.Sinks[0].VolatileData.get_Branch(0)[0] is GH_Colour && fixture.Sinks[0].VolatileData.get_Branch(0)[1] is GH_String &&
            fixture.Values(3) == "1013,1016", "Disabling Create set restores both typed opaque colour and matching text");
        fixture.Query(new PanelQueryOptions { SearchMode = PanelSearchMode.Regex, Search = @"^12,34,56 \(128\)$" });
        await fixture.Ready(() => fixture.Result.RowPositions.SequenceEqual(new[] { 14 }) && fixture.Sinks[0].VolatileData.DataCount == 1 && fixture.Values(3) == "1014");
        Check(fixture.Sinks[0].VolatileData.get_Branch(0)[0] is GH_Colour && fixture.Values(3) == "1014",
            "Alpha-colour regex remains distinct from the opaque stock summary");
    }

    private static bool Types(OutputChecks.Fixture fixture, int output, Type type) =>
        fixture.Sinks[output].VolatileData.DataCount > 0 && fixture.Sinks[output].VolatileData.AllData(true).All(value => value?.GetType() == type);
    private static bool[] Flags(OutputChecks.Fixture fixture) => fixture.Sinks[2].VolatileData.AllData(true).Cast<GH_Boolean>().Select(value => value.Value).ToArray();

    internal static void VerifyStockRenderWithoutHost()
    {
        // Exercise the stock painter without a Rhino process or a GH solution.
        // The native fixtures separately compare these rendered entries after solving.
        var stock = new GH_Panel(); stock.CreateAttributes();
        var source = new VolatileSource(Array.Empty<IGH_Goo>()); source.CreateAttributes(); stock.AddSource(source);
        var values = Enumerable.Range(0, ItemCount).Select(i => "Group (Facade A) with " + i + " objects").ToArray();
        values[11] = ""; values[12] = "<empty>"; values[13] = "12,34,56"; values[14] = "12,34,56 (128)";
        values[15] = "12,34,56 (0)"; values[20] = "Red"; values[21] = "Transparent";
        stock.AddVolatileDataList(new GH_Path(0), values.Select(value => new GH_String(value)));
        var previousPalette = GH_Skin.palette_hidden_standard;
        try
        {
            GH_Skin.palette_hidden_standard = null; // reproduce a fresh hidden host
            var rendered = RenderStock(stock, null);
            Check(rendered.Length == ItemCount && rendered.SequenceEqual(values.Select(PanelValuePresentation.Visualize)),
                "Stock rendering initializes the hidden palette and paints all 22 prepared entries without starting Rhino");
        }
        finally { GH_Skin.palette_hidden_standard = previousPalette; }
    }

    private static string[] RenderStock(GH_Panel stock, GH_Document? document)
    {
        // The hidden editor does not necessarily load the canvas skin. The stock
        // panel's full renderer needs its hidden palette for the capsule outline.
        if (GH_Skin.palette_hidden_standard == null) GH_Skin.LoadSkin();
        if (GH_Skin.palette_hidden_standard == null)
            throw new InvalidOperationException("Grasshopper did not initialize the stock panel palette.");
        using var canvas = new GH_Canvas { Size = new Size(800, 1000), Document = document };
        canvas.Viewport.Size = canvas.Size; canvas.Viewport.Target = System.Drawing.Point.Empty; canvas.Viewport.Zoom = 1;
        canvas.Viewport.ComputeProjection();
        stock.Attributes.Pivot = new PointF(40, 40);
        stock.Attributes.Bounds = new RectangleF(40, 40, 500, 900);
        stock.Attributes.PerformLayout();
        using var bitmap = new Bitmap(canvas.Width, canvas.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White); canvas.Viewport.ApplyProjection(graphics);
        var attributes = (GH_PanelAttributes)stock.Attributes;
        attributes.GetType().GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(attributes, new object[] { canvas, graphics, GH_CanvasChannel.Objects });
        var stack = (GH_StringList<GH_FormattedListItem>?)attributes.GetType()
            .GetField("m_stack", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(attributes)
            ?? throw new InvalidOperationException("Stock panel did not render its content stack.");

        // Observe the actual stock entries after their RenderItem calls, using
        // the same destination, clipping and scroll position as the panel.
        var destination = Rectangle.FromLTRB(attributes.PanelBounds.Left, attributes.ContentBounds.Top,
            attributes.PanelBounds.Right, attributes.PanelBounds.Bottom);
        var clip = destination; clip.Inflate(-1, -1);
        var rendered = new List<string>();
        graphics.SetClip(clip);
        try
        {
            stack.RenderItems(graphics, destination, stock.ScrollRatio, null, (item, bounds) =>
            {
                if (item.GetType().Name == "PanelEntryItem" && bounds.IntersectsWith(clip)) rendered.Add(item.Text);
            });
        }
        finally { graphics.ResetClip(); }
        return rendered.ToArray();
    }

    private static IGH_Goo[] Values()
    {
        GH_GeometryGroup Group(string name, int points)
        {
            var result = new GH_GeometryGroup { Name = name };
            for (int i = 0; i < points; i++) result.Objects.Add(new GH_Point(new Point3d(i, 1, 2)));
            return result;
        }
        var nested = new GH_GeometryGroup { Name = "Nested" };
        nested.Objects.Add(Group("Inner", 2)); nested.Objects.Add(null!); nested.Objects.Add(new GH_Point(Point3d.Origin));
        return new IGH_Goo[] { Group("", 0), Group("Atrium zero", 0), Group("", 1), Group("Atrium one", 1), Group("", 3),
            Group("Facade A", 2), Group("Facade B", 2), Group("Facade A", 1), Group("Facade A", 2), Group("  ", 0), nested,
            new GH_String(""), new GH_String("<empty>"), new GH_Colour(Color.FromArgb(255, 12, 34, 56)),
            new GH_Colour(Color.FromArgb(128, 12, 34, 56)), new GH_Colour(Color.FromArgb(0, 12, 34, 56)),
            new GH_String("12,34,56"), new GH_Interval(new Interval(-2.5, 5.75)),
            new GH_Interval2D(new UVInterval(new Interval(1, 2), new Interval(3, 4))),
            new GH_Guid(Guid.Parse("7d55d72e-8547-43c4-a231-cb78e82900cc")), new GH_Colour(Color.Red), new GH_Colour(Color.Transparent) };
    }

    // Grasshopper's persistent collection duplicates geometry and can drop Name.
    // This real source creates volatile data during the document solution instead.
    internal sealed class VolatileSource : GH_Param<IGH_Goo>
    {
        private readonly IGH_Goo[] _items;
        internal VolatileSource(IGH_Goo[] items) : base("Fixture volatile source", "V", "Delivers original values", "Test", "Test", GH_ParamAccess.tree) => _items = items;
        public override Guid ComponentGuid => new Guid("5231cd6c-3fb5-4523-a728-2fb17f5456ef");
        protected override void CollectVolatileData_Custom() => AddVolatileDataList(new GH_Path(0), _items);
    }

    internal static Task RunCycles(Assembly assembly)
    {
        VerifyCycles(assembly);
        Check(SmokeAssertions.Count == 4, "The independent plugin-controlled cycle fixture executed its four assertions");
        return Task.CompletedTask;
    }

    internal static void VerifyCycles(Assembly assembly)
    {
        // Do not feed cycles to the stock panel, a persistent param, or the solver:
        // native graph traversal/duplication may recurse before our code is called.
        var first = new GH_GeometryGroup { Name = "Cycle A" };
        var second = new GH_GeometryGroup { Name = "Cycle B" };
        first.Objects.Add(second); second.Objects.Add(first);
        var wrapper = new GH_GeometricGooWrapper();
        typeof(GH_GeometricGooWrapper).GetField("m_goo", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(wrapper, wrapper);
        var tree = new GH_Structure<IGH_Goo>(); tree.AppendRange(new IGH_Goo[] { first, second, wrapper }, new GH_Path(0));
        var args = new object?[] { tree, null, PanelUnitConversion.None, "mm" };
        var capture = assembly.GetType("BetterGhPanel.Runtime.SnapshotCapture", true)!.GetMethod("Capture", Static)!;
        var snapshot = (PanelSnapshot)capture.Invoke(null, args)!;
        Check(snapshot.Rows.Count == 3 && ((IGH_Goo?[])args[1]!).SequenceEqual(tree.Branches[0]), "Cycle capture retains the three supplied source identities");
        Check(snapshot.Rows.All(row => row.Validity == PanelValidity.Unverified) && snapshot.InspectionVisitCount < 100,
            "Both cyclic roots and a self-wrapped value terminate as unverified within the inspection budget");
        Check(snapshot.Rows[0].DisplayValue == "Group (Cycle A) with 1 object" && snapshot.Rows[1].DisplayValue == "Group (Cycle B) with 1 object",
            "Cyclic groups retain their direct names and counts without recursive host formatting");
        Check(PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "Cycle A" }).RowPositions.SequenceEqual(new[] { 0 }),
            "Plugin-controlled search remains usable after bounded cycle inspection");
    }

    private static void Check(bool condition, string message) => SmokeAssertions.Check(condition, message);
}
