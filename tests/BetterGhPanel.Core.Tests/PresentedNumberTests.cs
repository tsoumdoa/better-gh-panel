using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BetterGhPanel.Core;
using Xunit;

public sealed class PresentedNumberTests
{
    [Fact]
    public void AutomaticGeometryFormatting()
    {
        var row = new PanelRow(0, "{0}", "Curve", "Curve", null,
            geometryInfo: new PanelGeometryInfo("Curve", 1.23456789, units: "mm"));
        var snapshot = new PanelSnapshot(new[] { row }, new[] { new PanelBranch("{0}", 1) });
        Assert.Equal("1.23457 mm", PanelFilterValue.Format(snapshot, row, PanelSearchMode.Length, -1));
    }

    [Fact]
    public void SeededFactorOracleAcrossDecimalPlaces()
    {
        var random = new System.Random(50421);
        foreach (double factor in new[] { 1e-20, 1e-12, 1e-6, .001, .05, .07, .1, .25, .3, 5 })
        for (int places = -1; places <= 12; places++)
        for (int i = 0; i < 160; i++)
        {
            double input = (random.NextDouble() * 2 - 1) * System.Math.Pow(10, i % 22 - 6);
            decimal original = OracleDecimal(input), f = OracleDecimal(factor);
            decimal expectedDecimal = original;
            decimal rem;
            try { rem = expectedDecimal % f; }
            catch (System.OverflowException)
            {
                // An overflowing remainder here has a sub-resolution factor;
                // the documented base fallback leaves the factor step alone.
                Assert.Equal(PanelUnits.Transform(input, PanelUnitConversion.None, 0, places),
                    PanelUnits.Transform(input, PanelUnitConversion.None, factor, places));
                continue;
            }
            if (rem != 0) expectedDecimal = expectedDecimal - rem + (expectedDecimal > 0 ? f : 0);
            decimal scale = (decimal)System.Math.Pow(10, System.Math.Max(0, places));
            if (places >= 0) expectedDecimal = decimal.Ceiling(expectedDecimal * scale) / scale;
            double expected = PanelNumber.ToDouble(expectedDecimal);
            double result = PanelUnits.Transform(input, PanelUnitConversion.None, factor, places);
            Assert.Equal(expected, result);
            Assert.True(OracleDecimal(result) >= original);
            long positiveBits = System.BitConverter.DoubleToInt64Bits(System.Math.Abs(result));
            double ulp = System.BitConverter.Int64BitsToDouble(positiveBits + 1) - System.Math.Abs(result);
            if ((places < 0 || f * scale == decimal.Truncate(f * scale)) && factor >= 1000 * ulp)
                Assert.True(result == PanelUnits.Transform(result, PanelUnitConversion.None, factor, places), $"input={input:R} factor={factor:R} dp={places} result={result:R} next={PanelUnits.Transform(result, PanelUnitConversion.None, factor, places):R}");
            else if (places >= 0 && 1d / (double)scale >= 1000 * ulp)
                Assert.Equal(decimal.Truncate(OracleDecimal(result) * scale), OracleDecimal(result) * scale);
            double plain = PanelUnits.Transform(input, PanelUnitConversion.None, 0, places);
            Assert.Equal(plain, PanelUnits.Transform(plain, PanelUnitConversion.None, 0, places));
        }
    }

    private static decimal OracleDecimal(double value) => decimal.Parse(
        System.Environment.Version.Major < 5 ? PanelUnits.FormatRoundTrip(value) : value.ToString("R", CultureInfo.InvariantCulture),
        NumberStyles.Float, CultureInfo.InvariantCulture);

    [Fact]
    public void IncompatibleGridsCanAdvanceOnReapplication()
    {
        Assert.Equal(12.2, PanelUnits.Transform(12.1, PanelUnitConversion.None, .07, 1));
        Assert.Equal(12.3, PanelUnits.Transform(12.2, PanelUnitConversion.None, .07, 1));
    }

    [Fact]
    public void LargeMagnitudeRetainsLiteralDecimalAlgorithmAtDoublePrecisionLimit()
    {
        double input = -91525711674022.359;
        double first = PanelUnits.Transform(input, PanelUnitConversion.None, .05);
        double second = PanelUnits.Transform(first, PanelUnitConversion.None, .05);
        Assert.Equal(-91525711674022.34m, OracleDecimal(first));
        Assert.Equal(-91525711674022.3m, OracleDecimal(second));
        Assert.True(OracleDecimal(first) >= OracleDecimal(input));
    }

    [Theory]
    [InlineData(1.00000000000003e-15, 1e-20)]
    [InlineData(5.00000000000004e-16, 1e-18)]
    [InlineData(-4.556411977185128e-21, 1e-27)]
    public void InexactDecimalInputsUseBaseFactorPath(double input, double factor)
    {
        double expected = System.Math.Max(input, System.Math.Ceiling(input / factor) * factor);
        double result = PanelUnits.Transform(input, PanelUnitConversion.None, factor);
        Assert.Equal(expected, result);
        Assert.True(result >= input);
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
    [InlineData(1, 1d / 3, -1, 1.3333333333333333)]
    [InlineData(2 * System.Math.PI, System.Math.PI, -1, 2 * System.Math.PI)]
    [InlineData(1000000000000001, 1, -1, 1000000000000001)]
    [InlineData(9007199254740991, 1, -1, 9007199254740991)]

    [InlineData(79595447042768.58, .25, 8, 79595447042768.75)]
    [InlineData(3e-30, 1e-30, 2, 0)] // Tiny-value guard retains base rounding.
    [InlineData(.6424555553321054, .1, 1, .7)]
    [InlineData(135.63838467730133, .01, 2, 135.64)]
    [InlineData(8680.175, .07, 2, 8680.21)]
    [InlineData(8680.175, .07, 11, 8680.21)]
    [InlineData(12345678901234.56, .07, 2, 12345678901234.56)]
    [InlineData(1000000.3, 1e-12, 2, 1000000.3)]
    [InlineData(820337036301.54, 1e-6, 2, 820337036301.54)]
    [InlineData(45920837976001.6, .001, 1, 45920837976001.6)]
    [InlineData(651429.77, 1e-20, 3, 651429.77)]
    [InlineData(9623.300000000001, .05, 2, 9623.35)]
    [InlineData(.30000000000000004, .1, -1, .4)]
    [InlineData(12.1, .25, 0, 13)]
    [InlineData(-12.1, .25, 0, -12)]
    [InlineData(2.67, .01, 2, 2.67)]
    [InlineData(2.675, .001, 2, 2.68)]
    public void FactorDecimalPlacesCeil(double value, double factor, int places, double expected) =>
        Assert.Equal(expected, PanelUnits.Transform(value, PanelUnitConversion.None, factor, places));

    [Fact]
    public void PresentedMetricsAgreeWithOutputStatisticsAndFormatting()
    {
        var rows = new[] {
            new PanelRow(0, "{0}", "Curve", "Curve", null, geometryInfo: new PanelGeometryInfo("Curve", 4.4999999, units: "mm")),
            new PanelRow(1, "{0}", "Curve", "Curve", null, geometryInfo: new PanelGeometryInfo("Curve", 4.501, units: "mm"))
        };
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", 2) });
        var options = new PanelQueryOptions { SearchMode = PanelSearchMode.Length, DisplayFilterValue = true, OutputValues = true, DecimalPlaces = 2 };
        var result = PanelQueryEngine.Execute(snapshot, options);
        Assert.Equal(4.5, result.OutputValues![0]!.Number);

        Assert.Equal("4.50 mm", PanelFilterValue.Format(snapshot, rows[0], options.SearchMode, 2));
    }

    [Fact]
    public void MetricComparisonsUsePresentedRoundingAndRoundUpFactors()
    {
        var rows = new[] {
            new PanelRow(0, "{0}", "Curve", "Curve", null, geometryInfo: new PanelGeometryInfo("Curve", 4.4999999, units: "mm")),
            new PanelRow(1, "{0}", "Curve", "Curve", null, geometryInfo: new PanelGeometryInfo("Curve", 4.501, units: "mm"))
        };
        var snapshot = new PanelSnapshot(rows, new[] { new PanelBranch("{0}", 2) });
        var options = new PanelQueryOptions { SearchMode = PanelSearchMode.Length, DisplayFilterValue = true, OutputValues = true, DecimalPlaces = 2 };
        options.DisplayFilterValue = false; options.Search = "= 4.5";
        Assert.Equal(new[] { 0, 1 }, PanelQueryEngine.Execute(snapshot, options).RowPositions);
        options.RoundUpFactor = 5;
        Assert.Empty(PanelQueryEngine.Execute(snapshot, options).RowPositions);
        Assert.Equal("5.00 mm", PanelFilterValue.Format(snapshot, rows[0], options.SearchMode, 2, roundUpFactor: 5));
    }

    [Theory]
    [InlineData(PanelSearchMode.Radius)]
    [InlineData(PanelSearchMode.X)]
    public void PresentedPropertiesAndComponentsAgreeWithOutputComparisons(PanelSearchMode mode)
    {
        var options = new PanelQueryOptions { DisplayFilterValue = false, OutputValues = true, DecimalPlaces = 2, Search = "= 4.5" };
        var property = new PanelRow(0, "{0}", "Example", "", null, PanelValidity.VerifiedValid,
        components: new PanelComponents(4.4999999, 0, 0, "mm"),
        properties: new Dictionary<PanelSearchMode, PanelPropertyValue?> { [PanelSearchMode.Radius] = PanelPropertyValue.Numeric(4.4999999) });
        var snapshot = new PanelSnapshot(new[] { property }, new[] { new PanelBranch("{0}", 1) });
        options.SearchMode = mode; options.RoundUpFactor = 0;
        Assert.Single(PanelQueryEngine.Execute(snapshot, options).RowPositions);
        Assert.Equal(4.5, PanelFilterValue.OutputValue(snapshot, property, mode, null, 2)!.Number);
    }

    [Fact]
    public void PresentedScalarsAgreeWithComparisonsAndStatistics()
    {
        var options = new PanelQueryOptions { DisplayFilterValue = false, OutputValues = true, DecimalPlaces = 2, Search = "= 4.5" };
        var scalar = new PanelRow(0, "{0}", "Number", "captured", 4.4999999);
        var snapshot = new PanelSnapshot(new[] { scalar }, new[] { new PanelBranch("{0}", 1) });
        options.SearchMode = PanelSearchMode.Value;
        Assert.Single(PanelQueryEngine.Execute(snapshot, options).RowPositions);
    }

    [Fact]
    public void ScalarDisplayUsesInvariantCulture()
    {
        var scalar = new PanelRow(0, "{0}", "Number", "captured", 4.4999999);
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("4.50", PanelValuePresentation.FormatDisplay(scalar, PanelUnitConversion.None, 2));
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    [Fact]
    public void RoundingDescriptionIsEmptyForAutomaticPrecision()
    {
        var options = new PanelQueryOptions { DisplayFilterValue = false, OutputValues = true, DecimalPlaces = 2, Search = "= 4.5" };
        var scalar = new PanelRow(0, "{0}", "Number", "captured", 4.4999999);
        var snapshot = new PanelSnapshot(new[] { scalar }, new[] { new PanelBranch("{0}", 1) });
        options.SearchMode = PanelSearchMode.Value;
        Assert.Contains("2 decimal places", PanelFilterValue.RoundingDescription(snapshot, scalar, options));
        options.DecimalPlaces = -1;
        Assert.Equal("", PanelFilterValue.RoundingDescription(snapshot, scalar, options));
    }

    [Fact]
    public void CircleLengthIsRoundedAfterComputingCircumference()
    {
        var feet = new PanelRow(0, "{0}", "Curve", "", null, geometryInfo: new PanelGeometryInfo("Curve", 12.345, units: "ft"));
        var snapshot = new PanelSnapshot(new[] { feet }, new[] { new PanelBranch("{0}", 1) }, PanelUnitConversion.MillimetersToMeters);
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
