using System;
using System.Globalization;
using System.Numerics;
using BetterGhPanel.Core;
using Xunit;

public sealed class DeterministicNumberTests
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

}
