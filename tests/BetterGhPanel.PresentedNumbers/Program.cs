using System;
using System.Globalization;
using BetterGhPanel.Core;

internal static class Program
{
    private static void Equal<T>(T expected, T actual)
    {
        if (!Equals(expected, actual)) throw new Exception("Expected " + expected + "; got " + actual);
    }

    private static void Main()
    {
        var cases = new[] {
            (10000000000000004d, 0d, 2, 10000000000000004d, "10000000000000004.00"),
            (0.84551240822557006, 0d, 12, 0.845512408226, "0.845512408226"),
            (1.005, 0d, 2, 1.01, "1.01"), (-1.005, 0d, 2, -1.01, "-1.01"),
            (2.675, 0d, 2, 2.68, "2.68"), (-2.675, 0d, 2, -2.68, "-2.68"),
            (-1263.4718, 50d, 2, -1250d, "-1250.00"), (-0.001, 0d, 2, 0d, "0.00")
        };
        foreach (var c in cases)
        {
            double number = PanelUnits.Transform(c.Item1, PanelUnitConversion.None, c.Item2, c.Item3);
            string text = PanelUnits.Format(number, PanelUnitConversion.None, c.Item3);
            Equal(c.Item4, number);
            Equal(c.Item5, text);
            Equal(number, double.Parse(text, CultureInfo.InvariantCulture));
            Equal(number, PanelUnits.Transform(number, PanelUnitConversion.None, 0, c.Item3));
            // Explicit sets must retain the typed number even for large integers.
            var row = new PanelRow(0, "{0}", "Number", "", number);
            var snapshot = new PanelSnapshot(new[] { row }, new[] { new PanelBranch("{0}", 1) });
            var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { DecimalPlaces = c.Item3, CreateSet = true });
            Equal((double?)number, result.DisplaySet!.Values[0].Number);
            Equal((double?)number, result.Statistics.Minimum);
        }
        Equal(0L, BitConverter.DoubleToInt64Bits(PanelUnits.Transform(-0.001, PanelUnitConversion.None, 0, 2)));
        double precise = 1.2345678901234567;
        Equal(precise, PanelUnits.Transform(precise, PanelUnitConversion.None));
        Equal("1.23456789012", PanelUnits.Format(precise, PanelUnitConversion.None, -1));
        Equal("1.23457 ft", PanelUnits.FormatGeometry(precise, 1, "ft", PanelUnitConversion.None, -1));
        var scalar = new PanelRow(0, "{0}", "Number", "", precise);
        var input = new PanelSnapshot(new[] { scalar }, new[] { new PanelBranch("{0}", 1) });
        Equal(precise, PanelFilterValue.OutputValue(input, scalar, PanelSearchMode.Value, null)!.Number);
        Equal(true, PanelFilterValue.RoundingDescription(input, scalar, new PanelQueryOptions()).StartsWith("Shown 1.23456789012 · raw "));
        Equal((double?)1.23456789012, PanelQueryEngine.Execute(input, new PanelQueryOptions { CreateSet = true }).DisplaySet!.Values[0].Number);

        // A finite metric may overflow only after factor rounding, even without conversion.
        var geometry = new PanelRow(0, "{0}", "Curve", "", null,
            geometryInfo: new PanelGeometryInfo("Curve", double.MaxValue, units: "ft"));
        input = new PanelSnapshot(new[] { geometry }, new[] { new PanelBranch("{0}", 1) });
        var options = new PanelQueryOptions { DisplayFilterValue = true, SearchMode = PanelSearchMode.Length, OutputValues = true, RoundUpFactor = 1e308 };
        Equal("unavailable", PanelFilterValue.Format(input, geometry, options.SearchMode, -1, roundUpFactor: options.RoundUpFactor));
        var unavailable = PanelQueryEngine.Execute(input, options);
        Equal(null, unavailable.OutputValues![0]);
        Equal(0, unavailable.Statistics.NumericCount);
        options.CreateSet = true;
        var set = PanelQueryEngine.Execute(input, options);
        Equal(true, set.DisplaySet!.Values[0].Unavailable);
        Equal(0, set.Statistics.ScalarCount);
        options.CreateSet = false; options.DisplayFilterValue = false; options.Search = "> 0";
        Equal(0, PanelQueryEngine.Execute(input, options).RowPositions.Count);
        Console.WriteLine("PASS: presented-number regression (8 explicit cases, Automatic, sets, tooltip and final overflow)");
    }
}
