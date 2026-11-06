using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using BetterGhPanel.Core;
using Xunit;

public sealed class DeterministicNumberChecks
{
    // Shared by both test targets: checks every wire bit and text character.
    // The arithmetic oracle below never formats or parses a double.
    [Fact]
    public void SeededNumbersAcrossMagnitudesAndAllDecimalPlaces()
    {
        ulong seed = 0x46e12fed5UL, digest = 14695981039346656037UL;
        var magnitudes = new[] { 1e-12, 1e-11, 1e-10, 1e-9, 1e-8, 1e-7, 1e-6, 1e-5, 1e-4, 1e-3, 1e-2, 1e-1,
            1d, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15, 1e16, 1e17 };
        void Check(double input, int places)
        {
            double wire = PanelUnits.Transform(input, PanelUnitConversion.None, 0, places);
            string text = PanelUnits.Format(input, PanelUnitConversion.None, places);
            var expected = Oracle(input, places);
            Assert.Equal(expected.Item1, text);
            Assert.True(BitConverter.DoubleToInt64Bits(expected.Item2) == BitConverter.DoubleToInt64Bits(wire), "oracle: " + text + " dp " + places + " input " + input.ToString("R", CultureInfo.InvariantCulture));
            Assert.Equal(text, PanelUnits.Format(wire, PanelUnitConversion.None, places));
            Assert.True(PanelUnits.TryParseOperand(text, out double parsed));
            Assert.True(BitConverter.DoubleToInt64Bits(wire) == BitConverter.DoubleToInt64Bits(parsed), "parse: " + text + " dp " + places + " wire " + wire.ToString("R", CultureInfo.InvariantCulture));
            foreach (char character in text) digest = unchecked((digest ^ character) * 1099511628211UL);
            ulong bits = (ulong)BitConverter.DoubleToInt64Bits(wire);
            for (int i = 0; i < 8; i++) { digest = unchecked((digest ^ (byte)bits) * 1099511628211UL); bits >>= 8; }
        }
        for (int i = 0; i < 100000; i++)
        {
            seed ^= seed << 13; seed ^= seed >> 7; seed ^= seed << 17;
            double value = ((seed >> 11) / 9007199254740992d * 2 - 1) * magnitudes[i % magnitudes.Length];
            for (int places = 0; places <= 12; places++) Check(value, places);
            double factor = new[] { .001, .03, 5, 1e-28, 1e20 }[i % 5];
            if (Math.Abs(value) < 7.9e28) Assert.True((decimal)PanelUnits.Transform(value, PanelUnitConversion.None, factor) >= (decimal)value);
        }
        foreach (double input in new[] { 29205883.00000005, 717.6141121184295, 5400248.0508760773, 1.0000000000000003e29,
            .061657, 1.005, -1.005, 2.675, -2.675, 10000000000000004d, double.Epsilon, -double.Epsilon, 1e-100, -0d, double.MaxValue })
            for (int places = 0; places <= 12; places++) Check(input, places);
        decimal divisor = 1;
        for (int places = 0; places <= 12; places++, divisor *= 10)
            for (int k = 0; k < 100; k++)
            {
                double midpoint = (double)((k + .5m) / divisor);
                foreach (double input in new[] { midpoint, -midpoint,
                    BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(midpoint) - 1),
                    BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(midpoint) + 1) }) Check(input, places);
            }
        Assert.Equal("015d6de49ba1e063", digest.ToString("x16", CultureInfo.InvariantCulture));
    }

    private static Tuple<string, double> Oracle(double value, int places)
    {
        if (Math.Abs(value) >= 9007199254740992d)
            return Tuple.Create(new BigInteger(value).ToString(CultureInfo.InvariantCulture) + (places == 0 ? "" : "." + new string('0', places)), value);
        decimal number = (decimal)value;
        if ((double)number != value)
        {
            long bits = BitConverter.DoubleToInt64Bits(Math.Abs(value));
            int exponent = (int)((bits >> 52) & 2047);
            var mantissa = new BigInteger((bits & 0xfffffffffffffL) | (exponent == 0 ? 0 : 1L << 52));
            exponent = exponent == 0 ? -1074 : exponent - 1075;
            var numerator = mantissa * BigInteger.Pow(10, places);
            var denominator = BigInteger.One;
            if (exponent >= 0) numerator <<= exponent; else denominator <<= -exponent;
            var integer = BigInteger.DivRem(numerator, denominator, out var remainder);
            if (remainder * 2 >= denominator) integer++;
            string digits = integer.ToString(CultureInfo.InvariantCulture).PadLeft(places + 1, '0');
            string exact = places == 0 ? digits : digits.Insert(digits.Length - places, ".");
            number = decimal.Parse(exact, CultureInfo.InvariantCulture);
            if (value < 0) number = -number;
        }
        number = Math.Round(number, places, MidpointRounding.AwayFromZero);
        double wire = NearestDouble(number);
        return Tuple.Create(number.ToString("F" + places, CultureInfo.InvariantCulture), wire == 0 ? 0 : wire);
    }

    // Independent oracle: choose the nearest of the cast's neighbouring doubles
    // by exact rational distances, with even bits winning a tie.
    private static double NearestDouble(decimal number)
    {
        if (number == 0) return 0;
        var parts = decimal.GetBits(number);
        var numerator = (BigInteger)(uint)parts[0] + ((BigInteger)(uint)parts[1] << 32) + ((BigInteger)(uint)parts[2] << 64);
        var denominator = BigInteger.Pow(10, (parts[3] >> 16) & 255);
        long centre = BitConverter.DoubleToInt64Bits(Math.Abs((double)number)), best = centre;
        BigInteger bestError = -1, bestDivisor = 1;
        for (long bits = centre - 2; bits <= centre + 2; bits++)
        {
            int exponent = (int)((bits >> 52) & 2047) - 1075;
            BigInteger significand = (bits & 0xfffffffffffffL) | (1L << 52);
            BigInteger divisor = BigInteger.One;
            if (exponent >= 0) significand <<= exponent; else divisor <<= -exponent;
            var error = BigInteger.Abs(numerator * divisor - significand * denominator);
            int comparison = bestError.Sign < 0 ? -1 : (error * bestDivisor).CompareTo(bestError * divisor);
            if (comparison < 0 || comparison == 0 && (bits & 1) == 0)
            { best = bits; bestError = error; bestDivisor = divisor; }
        }
        double value = BitConverter.Int64BitsToDouble(best);
        return number < 0 ? -value : value;
    }

    [Fact]
    public void ReproductionsFactorGuardsAndComparisonOperands()
    {
        Assert.Equal("2.68", PanelUnits.Format(2.675, PanelUnitConversion.None, 2));
        Assert.Equal("29205883.0000000", PanelUnits.Format(29205883.00000005, PanelUnitConversion.None, 7));
        Assert.Equal(1e17, PanelUnits.Transform(1e17, PanelUnitConversion.None, .01));
        Assert.Equal(.1, PanelUnits.Transform(1e-100, PanelUnitConversion.None, .1));
        Assert.Equal(1.4, PanelUnits.Transform(1.4, PanelUnitConversion.None, .1));
        Assert.Equal(0L, BitConverter.DoubleToInt64Bits(PanelUnits.Transform(-.001, PanelUnitConversion.None, 0, 2)));
        Assert.Equal("unavailable", PanelUnits.Format(double.NaN, PanelUnitConversion.None, 2));
        Assert.True(PanelUnits.TryParseOperand("1e-100", out double tiny));
        Assert.Equal(1e-100, tiny);
        Assert.True(PanelUnits.TryParseOperand("5e-324", out double subnormal));
        Assert.Equal(double.Epsilon, subnormal);
        var row = new PanelRow(0, "{0}", "Number", "", .061657, PanelValidity.VerifiedValid,
            properties: new Dictionary<PanelSearchMode, PanelPropertyValue?> { [PanelSearchMode.Radius] = PanelPropertyValue.Numeric(.061657, 1) });
        var snapshot = new PanelSnapshot(new[] { row }, new[] { new PanelBranch("{0}", 1) });
        foreach (var mode in new[] { PanelSearchMode.Value, PanelSearchMode.Radius })
            Assert.Single(PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SearchMode = mode, Search = "= 0.061657", DecimalPlaces = 6 }).RowPositions);
        var large = new PanelSnapshot(new[] { new PanelRow(0, "{0}", "Number", "", 1e29),
            new PanelRow(1, "{0}", "Number", "", 1.0000000000000003e29) }, new[] { new PanelBranch("{0}", 2) });
        Assert.Equal(2, PanelQueryEngine.Execute(large, new PanelQueryOptions { CreateSet = true, DecimalPlaces = 2 }).RowPositions.Count);
    }

    [Fact]
    public void FoldedFrameworkRunnerRegressions()
    {
        foreach (var c in new[] {
            (10000000000000004d, 0d, 2, 10000000000000004d, "10000000000000004.00"),
            (.84551240822557006, 0d, 12, .845512408226, "0.845512408226"),
            (1.005, 0d, 2, 1.01, "1.01"), (-1.005, 0d, 2, -1.01, "-1.01"),
            (2.675, 0d, 2, 2.68, "2.68"), (-2.675, 0d, 2, -2.68, "-2.68"),
            (-1263.4718, 50d, 2, -1250d, "-1250.00"), (-.001, 0d, 2, 0d, "0.00") })
        {
            double wire = PanelUnits.Transform(c.Item1, PanelUnitConversion.None, c.Item2, c.Item3);
            Assert.Equal(c.Item4, wire);
            Assert.Equal(c.Item5, PanelUnits.Format(wire, PanelUnitConversion.None, c.Item3));
            var row = new PanelRow(0, "{0}", "Number", "", wire);
            var snapshot = new PanelSnapshot(new[] { row }, new[] { new PanelBranch("{0}", 1) });
            var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { DecimalPlaces = c.Item3, CreateSet = true });
            Assert.Equal(wire, result.DisplaySet!.Values[0].Number);
            Assert.Equal(wire, result.Statistics.Minimum);
        }
        const double precise = 1.2345678901234567;
        Assert.Equal(precise, PanelUnits.Transform(precise, PanelUnitConversion.None));
        Assert.Equal("1.23456789012", PanelUnits.Format(precise, PanelUnitConversion.None, -1));
        Assert.Equal("1.23457 ft", PanelUnits.FormatGeometry(precise, 1, "ft", PanelUnitConversion.None, -1));
        var geometry = new PanelRow(0, "{0}", "Curve", "", null,
            geometryInfo: new PanelGeometryInfo("Curve", double.MaxValue, units: "ft"));
        var input = new PanelSnapshot(new[] { geometry }, new[] { new PanelBranch("{0}", 1) });
        var options = new PanelQueryOptions { DisplayFilterValue = true, SearchMode = PanelSearchMode.Length,
            OutputValues = true, RoundUpFactor = 1e308 };
        Assert.Equal("unavailable", PanelFilterValue.Format(input, geometry, options.SearchMode, -1, roundUpFactor: options.RoundUpFactor));
        var unavailable = PanelQueryEngine.Execute(input, options);
        Assert.Null(unavailable.OutputValues![0]); Assert.Equal(0, unavailable.Statistics.NumericCount);
        options.CreateSet = true;
        var set = PanelQueryEngine.Execute(input, options);
        Assert.True(set.DisplaySet!.Values[0].Unavailable); Assert.Equal(0, set.Statistics.ScalarCount);
        options.CreateSet = false; options.DisplayFilterValue = false; options.Search = "> 0";
        Assert.Empty(PanelQueryEngine.Execute(input, options).RowPositions);
    }

    [Fact]
    public void DimensionlessPropertiesIgnoreFactorsAndIntegerPropertiesStayIntegers()
    {
        foreach (double value in new[] { 1d, 2026d, 255d, .123456 })
        {
            var property = PanelPropertyValue.Numeric(value);
            string text = property.Format(PanelUnitConversion.None, 2, true, 50);
            Assert.Equal(value == Math.Truncate(value) ? value.ToString("F2", CultureInfo.InvariantCulture) : "0.12", text);
            var row = new PanelRow(0, "{0}", "Example", "", null, PanelValidity.VerifiedValid,
                properties: new Dictionary<PanelSearchMode, PanelPropertyValue?> { [PanelSearchMode.M00] = property });
            var snapshot = new PanelSnapshot(new[] { row }, new[] { new PanelBranch("{0}", 1) });
            var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { SearchMode = PanelSearchMode.M00,
                DisplayFilterValue = true, OutputValues = true, RoundUpFactor = 50, DecimalPlaces = 2 });
            Assert.Equal(value == Math.Truncate(value) ? value : .12, result.OutputValues![0]!.Number);
        }
        foreach (int value in new[] { 0, 1, 2026, 255 })
            Assert.Equal(value.ToString(CultureInfo.InvariantCulture), PanelPropertyValue.Integer(value).Format(PanelUnitConversion.None, 2, true, 50));
        Assert.Equal("50.00 mm", PanelPropertyValue.Numeric(1, 1, "mm").Format(PanelUnitConversion.None, 2, true, 50));
    }

    [Theory]
    [InlineData("1.23456789012E-18", 1.23456789012e-18)]
    [InlineData("1.23456789012E-19", 1.23456789012e-19)]
    [InlineData("1.23456789012E-20", 1.23456789012e-20)]
    [InlineData("1.23456789012E-21", 1.23456789012e-21)]
    [InlineData("1.23456789012E-22", 1.23456789012e-22)]
    [InlineData("1.23456789012E-23", 1.23456789012e-23)]
    [InlineData("1.23456789012E-24", 1.23456789012e-24)]
    [InlineData("1.23456789012E-25", 1.23456789012e-25)]
    [InlineData("1.23456789012E-26", 1.23456789012e-26)]
    [InlineData("1.23456789012E-27", 1.23456789012e-27)]
    [InlineData("1.23456789012E-28", 1.23456789012e-28)]
    [InlineData("2.5e-28", 2.5e-28)]
    [InlineData("0.12345678901234567890123456789", .12345678901234567890123456789)]
    public void TinyOperandsPreserveEveryBit(string text, double expected)
    {
        Assert.True(PanelUnits.TryParseOperand(text, out double actual));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));
        Assert.True(PanelUnits.TryParseOperand("-" + text, out actual));
        Assert.Equal(BitConverter.DoubleToInt64Bits(-expected), BitConverter.DoubleToInt64Bits(actual));
    }

    [Fact]
    public void AutomaticTinySetValuesFiltersAndRoundingNotesAgree()
    {
        const double first = 1.23456789012e-25, second = 1.23456789013e-25;
        var snapshot = new PanelSnapshot(new[] { new PanelRow(0, "{0}", "Number", "", first),
            new PanelRow(1, "{0}", "Number", "", second) }, new[] { new PanelBranch("{0}", 2) });
        foreach (bool displayFilterValue in new[] { false, true })
        {
            var options = new PanelQueryOptions { CreateSet = true, SearchMode = PanelSearchMode.Value, DisplayFilterValue = displayFilterValue };
            var result = PanelQueryEngine.Execute(snapshot, options);
            Assert.Equal(2, result.RowPositions.Count);
            Assert.Equal(first, result.DisplaySet!.Values[0].Number);
            Assert.Equal(second, result.DisplaySet.Values[1].Number);
            Assert.Equal(first, result.Statistics.Minimum);
            Assert.Equal(2, result.Statistics.UniqueValues.Count);
            Assert.Equal("", PanelFilterValue.RoundingDescription(snapshot, snapshot.Rows[0], options));
            options.DisplayFilterValue = false;
            options.Search = "= 1.23456789012E-25";
            Assert.Equal(0, Assert.Single(PanelQueryEngine.Execute(snapshot, options).RowPositions));
        }
        Assert.Equal(1e-25, PanelNumber.GeneralValue(9.999999999999e-26, 12));
    }

    [Theory]
    [InlineData(.3, "0.3")]
    [InlineData(.30000000000000004, "0.30000000000000004")]
    [InlineData(1.234567890123456, "1.234567890123456")]
    [InlineData(1.23456789012e-25, "1.23456789012E-25")]
    [InlineData(double.MaxValue, "1.7976931348623157E+308")]
    [InlineData(double.Epsilon, "4.94065645841247E-324")]
    public void AutomaticUniqueTextMatchesRowsAndTooltipRoundTrips(double value, string expected)
    {
        Assert.Equal(expected, PanelUnits.FormatRoundTrip(value));
        Assert.True(PanelUnits.TryParseOperand(expected, out double parsed));
        Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(parsed));
        var snapshot = new PanelSnapshot(new[] { new PanelRow(0, "{0}", "Number", "", value) }, new[] { new PanelBranch("{0}", 1) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        string compact = PanelUnits.Format(value, PanelUnitConversion.None, -1);
        Assert.Equal("Numbers: 1 · Min: " + compact, CanvasStatistics.Label(result, 2, -1));
        Assert.Equal("Max: " + compact, CanvasStatistics.Label(result, 3, -1));
        Assert.Equal("Average: " + compact, CanvasStatistics.Label(result, 4, -1));
        Assert.Equal("1×  Number: " + compact, CanvasStatistics.Label(result, CanvasStatistics.OverviewRows, -1));
    }

    [Fact]
    public void AutomaticStatisticsDistinguishAdjacentValuesAndSetAddressesMatchD()
    {
        var snapshot = new PanelSnapshot(new[] { new PanelRow(0, "{0;2}", "Number", "", .3),
            new PanelRow(1, "{0;2}", "Number", "", .1 + .2) }, new[] { new PanelBranch("{0;2}", 2) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        Assert.Single(result.Statistics.UniqueValues);
        Assert.Equal(2, result.Statistics.UniqueValues[0].Count);
        Assert.Contains("Merged from 2 distinct values", CanvasStatistics.Label(result, 9, -1, full: true));
        Assert.Equal("2×  Number: 0.3", CanvasStatistics.Label(result, 9, -1));
        result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions { CreateSet = true, DecimalPlaces = 2 });
        Assert.Equal("{0;2;0}", PanelItemOrder.Address(result, 0, true, false));
        Assert.Equal("{0}", PanelItemOrder.Address(result, 0, true, true));
        Assert.Equal("{0;2}(0)", PanelItemOrder.Address(result, 0, false, false));
    }
}
