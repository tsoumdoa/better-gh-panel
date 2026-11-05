using BetterGhPanel.UI;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BetterGhPanel.Core;
using Xunit;

public sealed class PresentedNumberChecks
{
    [Fact]
    public void AutomaticGeometryStatisticsMatchRows()
    {
        var row = new PanelRow(0, "{0}", "Curve", "Curve", null,
            geometryInfo: new PanelGeometryInfo("Curve", 1.23456789, units: "mm"));
        var snapshot = new PanelSnapshot(new[] { row }, new[] { new PanelBranch("{0}", 1) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { DisplayFilterValue = true, SearchMode = PanelSearchMode.Length });
        Assert.Equal("1.23457 mm", PanelFilterValue.Format(snapshot, row, PanelSearchMode.Length, -1));
        Assert.Equal("1×  Number: 1.23457 mm", CanvasStatistics.Label(result, 9, -1));
        Assert.Equal("Numbers: 1 · Min: 1.23457 mm", CanvasStatistics.Label(result, 2, -1));
    }

    [Fact]
    public void SeededFactorOracleAcrossDecimalPlaces()
    {
        var random = new System.Random(50421);
        foreach (double factor in new[] { 1e-12, .001, .07, .1, .25, .3, 5 })
        for (int places = 1; places <= 12; places++)
        for (int i = 0; i < 400; i++)
        {
            double input = (random.NextDouble() * 2 - 1) * System.Math.Pow(10, i % 16 - 3);
            double factored = PanelUnits.Transform(input, PanelUnitConversion.None, factor);
            decimal multiple = OracleMultiple(factored, (decimal)factor);
            decimal inputMultiple = OracleMultiple(input, (decimal)factor);
            if (PanelNumber.ToDouble(inputMultiple) == input) multiple = inputMultiple;
            decimal scale = (decimal)System.Math.Pow(10, places);
            double expected = PanelNumber.ToDouble(decimal.Ceiling(multiple * scale) / scale);
            if (factored == input && PanelNumber.ToDouble(inputMultiple) != input)
            {
                if (decimal.Round((decimal)factor, places) == (decimal)factor)
                {
                    decimal grid = OracleMultiple(input, (decimal)factor);
                    if (PanelNumber.ToDouble(grid) < input) grid += (decimal)factor;
                    expected = PanelNumber.ToDouble(grid);
                }
                else expected = ExactBinaryCeiling(input, places);
            }
            double result = PanelUnits.Transform(input, PanelUnitConversion.None, factor, places);
            Assert.True(expected == result, $"oracle input={input:R} f={factor:R} dp={places} expected={expected:R} actual={result:R}");
            var source = ExactFraction(input);
            var output = ExactFraction(result);
            Assert.True(output.Item1 * source.Item2 >= source.Item1 * output.Item2);
            if (decimal.Round((decimal)factor, places) == (decimal)factor)
                Assert.True(result == PanelUnits.Transform(result, PanelUnitConversion.None, factor, places), $"idempotence input={input:R} result={result:R} f={factor:R} dp={places}");
        }
    }

    private static (System.Numerics.BigInteger, System.Numerics.BigInteger) ExactFraction(double value)
    {
        long bits = System.BitConverter.DoubleToInt64Bits(value);
        int exponentBits = (int)((bits >> 52) & 2047);
        int exponent = exponentBits == 0 ? -1074 : exponentBits - 1075;
        var numerator = new System.Numerics.BigInteger((bits & 0xfffffffffffffL) | (exponentBits == 0 ? 0 : 1L << 52));
        if (value < 0) numerator = -numerator;
        var denominator = System.Numerics.BigInteger.One;
        if (exponent >= 0) numerator <<= exponent; else denominator <<= -exponent;
        return (numerator, denominator);
    }

    private static decimal OracleMultiple(double value, decimal factor)
    {
        var fraction = ExactFraction(value);
        // Parse decimal digits independently of the production decimal-bit path.
        string digits = factor.ToString(CultureInfo.InvariantCulture);
        int point = digits.IndexOf('.');
        int places = point < 0 ? 0 : digits.Length - point - 1;
        var numerator = System.Numerics.BigInteger.Parse(digits.Replace(".", ""), CultureInfo.InvariantCulture);
        var scale = System.Numerics.BigInteger.Pow(10, places);
        var dividend = System.Numerics.BigInteger.Abs(fraction.Item1) * scale;
        var divisor = fraction.Item2 * numerator;
        var floor = System.Numerics.BigInteger.DivRem(dividend, divisor, out var remainder);
        if (remainder * 2 >= divisor) floor++;
        return (decimal)(value < 0 ? -floor : floor) * factor;
    }

    private static double ExactBinaryCeiling(double value, int places)
    {
        var fraction = ExactFraction(value);
        var scale = System.Numerics.BigInteger.Pow(10, places);
        var coefficient = System.Numerics.BigInteger.DivRem(fraction.Item1 * scale, fraction.Item2, out var remainder);
        if (remainder > 0) coefficient++;
        return PanelNumber.ToDouble((decimal)coefficient / (decimal)scale);
    }

    [Fact]
    public void FastAutomaticKeysMatchFormatter()
    {
        var random = new System.Random(501);
        foreach (int precision in new[] { 6, 12 })
        {
            for (int exponent = -300; exponent <= 300; exponent += 10)
            foreach (double coefficient in new[] { 1d, 9.999999999995, 1.234565, 1.234567890125 })
            {
                double v = coefficient * System.Math.Pow(10, exponent);
                for (int offset = -4; offset <= 4; offset++)
                {
                    double adjacent = System.BitConverter.Int64BitsToDouble(System.BitConverter.DoubleToInt64Bits(v) + offset);
                    string format = precision == 6 ? "G6" : "G12";
                    Assert.Equal(PanelUnits.Format(adjacent, PanelUnitConversion.None, -1, format),
                        PanelUnits.Format(PanelNumber.GeneralKey(adjacent, precision), PanelUnitConversion.None, -1, format));
                }
            }
            var keys = new Dictionary<string, double>();
            var texts = new Dictionary<double, string>();
            for (int i = 0; i < 20000; i++)
            {
                double value = (random.NextDouble() * 2 - 1) * System.Math.Pow(10, i % 600 - 300);
                double key = PanelNumber.GeneralKey(value, precision);
                string text = PanelUnits.Format(value, PanelUnitConversion.None, -1, precision == 6 ? "G6" : "G12");
                if (keys.TryGetValue(text, out double existing)) Assert.Equal(existing, key);
                if (texts.TryGetValue(key, out string? existingText)) Assert.Equal(existingText, text);
                keys[text] = key; texts[key] = text;
                Assert.Equal(text, PanelUnits.Format(key, PanelUnitConversion.None, -1, precision == 6 ? "G6" : "G12"));
            }
        }
    }

    [Fact]
    public void StatisticsGroupConvertedCoordinatesAndProperties()
    {
        var rows = new[] {
            new PanelRow(0, "{0}", "Point", "", null, PanelValidity.VerifiedValid, components: new PanelComponents(1000, 0, 0, "mm")),
            new PanelRow(1, "{0}", "Point", "", null, PanelValidity.VerifiedValid, components: new PanelComponents(1000, 0, 0, "mm"))
        };
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", 2) }, unitConversion: PanelUnitConversion.MillimetersToMeters);
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { DisplayFilterValue = true, SearchMode = PanelSearchMode.X, DecimalPlaces = 2 });
        Assert.Single(result.Statistics.UniqueValues);
        Assert.Equal(2, result.Statistics.UniqueValues[0].Count);
        Assert.Equal("1.00 m", result.Statistics.UniqueValues[0].PresentedText);
    }

    [Fact]
    public void StatisticsKeepPropertyUnitSymbolsSeparate()
    {
        var rows = new[] {
            new PanelRow(0, "{0}", "Example", "", null, PanelValidity.VerifiedValid,
                properties: new Dictionary<PanelSearchMode, PanelPropertyValue?> { [PanelSearchMode.Radius] = PanelPropertyValue.Numeric(1000, 1, "mm") }),
            new PanelRow(1, "{0}", "Example", "", null, PanelValidity.VerifiedValid,
                properties: new Dictionary<PanelSearchMode, PanelPropertyValue?> { [PanelSearchMode.Radius] = PanelPropertyValue.Numeric(1, 1, "ft") })
        };
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", 2) }, unitConversion: PanelUnitConversion.MillimetersToMeters);
        foreach (int decimals in new[] { -1, 2 })
        {
            var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { DisplayFilterValue = true, SearchMode = PanelSearchMode.Radius, DecimalPlaces = decimals });
            Assert.Equal(2, result.Statistics.UniqueValues.Count);
            Assert.Contains("m", result.Statistics.UniqueValues[0].PresentedText!);
            Assert.Contains("ft", result.Statistics.UniqueValues[1].PresentedText!);
        }
    }

    [Theory]
    [InlineData(-4111711901664.5996, .3, 1)]
    [InlineData(2611424723924.8003, .1, 3)]
    [InlineData(19568874370105.973, .07, 2)]
    [InlineData(502282399452.42303, .001, 4)]
    [InlineData(60128756780237.305, .1, 2)]
    public void GuardedCommensurateResultsRemainOnFactorGrid(double input, double factor, int places)
    {
        double result = PanelUnits.Transform(input, PanelUnitConversion.None, factor, places);
        Assert.True(result >= input);
        Assert.Equal(result, PanelUnits.Transform(result, PanelUnitConversion.None, factor, places));
    }

    [Theory]
    [InlineData(4199.999847542495)]
    [InlineData(7581.197553119248)]
    [InlineData(7875.019851082479)]
    public void EmittedFineFactorGridPointsRemainStable(double value)
    {
        Assert.Equal(value, PanelUnits.Transform(value, PanelUnitConversion.None, 1e-12, 12));
    }

    [Theory]
    [InlineData(1, 1d / 3, -1, 1)]
    [InlineData(2 * System.Math.PI, System.Math.PI, -1, 2 * System.Math.PI)]
    [InlineData(1000000000000001, 1, -1, 1000000000000001)]
    [InlineData(9007199254740991, 1, -1, 9007199254740991)]

    [InlineData(79595447042768.58, .25, 8, 79595447042768.75)]
    [InlineData(3e-30, 1e-30, 2, .01)]
    [InlineData(.6424555553321054, .1, 1, .7)]
    [InlineData(135.63838467730133, .01, 2, 135.64)]
    [InlineData(8680.175, .07, 2, 8680.21)]
    [InlineData(8680.175, .07, 11, 8680.21)]
    [InlineData(12345678901234.56, .07, 2, 12345678901234.56)]
    [InlineData(12.1, .25, 0, 13)]
    [InlineData(-12.1, .25, 0, -12)]
    [InlineData(2.67, .01, 2, 2.67)]
    [InlineData(2.675, .001, 2, 2.68)]
    public void FactorDecimalPlacesCeil(double value, double factor, int places, double expected) =>
        Assert.Equal(expected, PanelUnits.Transform(value, PanelUnitConversion.None, factor, places));

    [Fact]
    public void PresentedMetricsPropertiesComponentsAndScalarsAgreeWithOutputComparisonsAndStats()
    {
        var rows = new[] {
            new PanelRow(0, "{0}", "Curve", "Curve", null, geometryInfo: new PanelGeometryInfo("Curve", 4.4999999, units: "mm")),
            new PanelRow(1, "{0}", "Curve", "Curve", null, geometryInfo: new PanelGeometryInfo("Curve", 4.501, units: "mm"))
        };
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", 2) });
        var options = new PanelQueryOptions { SearchMode = PanelSearchMode.Length, DisplayFilterValue = true, OutputValues = true, DecimalPlaces = 2 };
        var result = PanelQueryEngine.Execute(snapshot, options);
        Assert.Equal(4.5, result.OutputValues![0]!.Number);
        Assert.Equal(4.5, result.Statistics.Minimum);
        Assert.Equal(4.5, result.Statistics.Maximum);
        Assert.Equal(4.5, result.Statistics.Mean);
        Assert.Single(result.Statistics.UniqueValues);
        Assert.Equal(2, result.Statistics.UniqueValues[0].Count);
        Assert.Equal("4.50 mm", PanelFilterValue.Format(snapshot, rows[0], options.SearchMode, 2));
        options.DisplayFilterValue = false; options.Search = "= 4.5";
        Assert.Equal(new[] { 0, 1 }, PanelQueryEngine.Execute(snapshot, options).RowPositions);
        options.RoundUpFactor = 5;
        Assert.Empty(PanelQueryEngine.Execute(snapshot, options).RowPositions);
        Assert.Equal("5.00 mm", PanelFilterValue.Format(snapshot, rows[0], options.SearchMode, 2, roundUpFactor: 5));

        var property = new PanelRow(0, "{0}", "Example", "", null, PanelValidity.VerifiedValid,
            components: new PanelComponents(4.4999999, 0, 0, "mm"),
            properties: new Dictionary<PanelSearchMode, PanelPropertyValue?> { [PanelSearchMode.Radius] = PanelPropertyValue.Numeric(4.4999999) });
        snapshot = new PanelSnapshot(new[] { property }, new[] { new PanelBranch("{0}", 1) });
        foreach (var mode in new[] { PanelSearchMode.Radius, PanelSearchMode.X })
        {
            options.SearchMode = mode; options.RoundUpFactor = 0;
            Assert.Single(PanelQueryEngine.Execute(snapshot, options).RowPositions);
            Assert.Equal(4.5, PanelFilterValue.OutputValue(snapshot, property, mode, null, 2)!.Number);
        }
        var scalar = new PanelRow(0, "{0}", "Number", "captured", 4.4999999);
        snapshot = new PanelSnapshot(new[] { scalar }, new[] { new PanelBranch("{0}", 1) });
        options.SearchMode = PanelSearchMode.Value;
        Assert.Single(PanelQueryEngine.Execute(snapshot, options).RowPositions);
        Assert.Equal(4.5, PanelQueryEngine.Execute(snapshot, options).Statistics.Minimum);
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("4.50", PanelValuePresentation.FormatDisplay(scalar, PanelUnitConversion.None, 2));
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
        Assert.Contains("2 decimal places", PanelFilterValue.RoundingDescription(snapshot, scalar, options));
        options.DecimalPlaces = -1;
        Assert.Equal("", PanelFilterValue.RoundingDescription(snapshot, scalar, options));
        var feet = new PanelRow(0, "{0}", "Curve", "", null, geometryInfo: new PanelGeometryInfo("Curve", 12.345, units: "ft"));
        snapshot = new PanelSnapshot(new[] { feet }, new[] { new PanelBranch("{0}", 1) }, PanelUnitConversion.MillimetersToMeters);
        options.SearchMode = PanelSearchMode.Length; options.Search = ""; options.DisplayFilterValue = true; options.DecimalPlaces = 2;
        result = PanelQueryEngine.Execute(snapshot, options);
        Assert.Equal("ft", result.Statistics.NumericUnits);
        Assert.Contains("12.35 ft", BetterGhPanel.UI.CanvasStatistics.Label(result, 2, 2));
        options.SearchMode = PanelSearchMode.ItemIndex;
        result = PanelQueryEngine.Execute(snapshot, options);
        Assert.Equal("", result.Statistics.NumericUnits);
        Assert.DoesNotContain(" m", BetterGhPanel.UI.CanvasStatistics.Label(result, 2, 2));
        var circle = new PanelRow(0, "{0}", "Circle", "", null, geometryInfo: new PanelGeometryInfo("Circle", 2, metricName: "Radius"));
        Assert.Equal(15, PanelFilterValue.OutputValue(snapshot, circle, PanelSearchMode.Length, null, -1, 5)!.Number);
    }

    [Theory]
    [InlineData(new[] { 12, 12, 12, 0 }, 12, 1)]
    [InlineData(new[] { 12, 12, 12 }, -1, 0)]
    [InlineData(new[] { 12, 12, 11, 0 }, -1, 0)]
    [InlineData(new[] { 12, 12, 12, 12 }, -1, 0)]
    [InlineData(new[] { 0, 0, 0, 1 }, 0, 1)]
    public void WarningsAreConservativeAndIndependentOfFilters(int[] lengths, int majority, int outliers)
    {
        var branches = lengths.Select((count, i) => new PanelBranch("{" + i + "}", count)).ToArray();
        var snapshot = new PanelSnapshot(new[] {
            new PanelRow(0, "{0}", "Null", "null", null, isNull: true, isValid: false),
            new PanelRow(1, "{0}", "Number", "invalid", null, isValid: false),
            new PanelRow(2, "{0}", "Unknown", "unverified", null, PanelValidity.Unverified)
        }, branches);
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { Search = "no matches" });
        Assert.Empty(result.RowPositions);
        Assert.Equal(1, result.Warnings.NullCount);
        Assert.Equal(1, result.Warnings.InvalidCount);
        Assert.Equal(1, result.Warnings.UnverifiedCount);
        Assert.Equal(majority < 0 ? (int?)null : majority, result.Warnings.MajorityLength);
        Assert.Equal(outliers, result.Warnings.OutlierBranches);
        Assert.Equal(outliers != 0, result.Warnings.IsOutlier(snapshot, branches.Last().Path));
        Assert.False(PanelQueryEngine.Execute(PanelSnapshot.Empty, new PanelQueryOptions()).Warnings.Any);
    }
}
