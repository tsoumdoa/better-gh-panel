using System;
using System.Globalization;
using System.Numerics;

namespace BetterGhPanel.Core
{
    // No double formatting/parsing participates in decimal rounding. A faithful
    // 15-digit decimal cast preserves the original Panel+ / Excel semantics;
    // otherwise round the exact binary rational, including subnormal values.
    internal static class PanelNumber
    {
        internal const double IntegerBoundary = 9007199254740992d;
        private static readonly BigInteger[] Tens = Powers(350);
        private static BigInteger[] Powers(int count)
        {
            var values = new BigInteger[count]; values[0] = BigInteger.One;
            for (int i = 1; i < count; i++) values[i] = values[i - 1] * 10;
            return values;
        }

        internal static bool FaithfulDecimal(double value, out decimal number)
        {
            number = 0;
            if (Math.Abs(value) >= 79228162514264337593543950336d) return false;
            number = (decimal)value;
            return (double)number == value;
        }

        private static void Binary(double value, out BigInteger numerator, out BigInteger denominator)
        {
            long bits = BitConverter.DoubleToInt64Bits(Math.Abs(value));
            int exponent = (int)((bits >> 52) & 2047);
            long mantissa = bits & 0xfffffffffffffL;
            if (exponent != 0) mantissa |= 1L << 52;
            exponent = exponent == 0 ? -1074 : exponent - 1075;
            numerator = new BigInteger(mantissa); denominator = BigInteger.One;
            if (exponent >= 0) numerator <<= exponent; else denominator <<= -exponent;
        }

        private static BigInteger Rounded(BigInteger numerator, BigInteger denominator)
        {
            var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
            return remainder * 2 >= denominator ? quotient + 1 : quotient;
        }

        internal static decimal RoundedDecimal(double value, int places)
        {
            if (FaithfulDecimal(value, out decimal number))
                return Math.Round(number, places, MidpointRounding.AwayFromZero);
            Binary(value, out var numerator, out var denominator);
            var coefficient = Rounded(numerator * Tens[places], denominator);
            // |v| < 2^53 and places <= 12, so the coefficient fits 96 bits.
            return new decimal((int)(uint)(coefficient & uint.MaxValue),
                (int)(uint)((coefficient >> 32) & uint.MaxValue),
                (int)(uint)((coefficient >> 64) & uint.MaxValue), value < 0, (byte)places);
        }

        internal static double ToDouble(decimal number)
        {
            if (number == 0) return 0;
            var bits = decimal.GetBits(number);
            int scale = (bits[3] >> 16) & 255;
            // Both operands of the cast's final division are exact doubles here.
            if (bits[2] == 0 && (uint)bits[1] < 0x200000 && scale <= 22) return (double)number;
            var numerator = (BigInteger)(uint)bits[0] + ((BigInteger)(uint)bits[1] << 32) + ((BigInteger)(uint)bits[2] << 64);
            // The runtime cast is a good estimate, but can double-round depending
            // on decimal's stored scale. Correct its final bit using the rational.
            return RationalDouble(numerator, Tens[scale], bits[3] < 0, Math.Abs((double)number));
        }

        private static double RationalDouble(BigInteger numerator, BigInteger denominator, bool negative, double estimate)
        {
            int binaryExponent = estimate > 0 && !double.IsInfinity(estimate)
                ? (int)((BitConverter.DoubleToInt64Bits(estimate) >> 52) & 2047) - 1023
                : (int)Math.Floor(BigInteger.Log(numerator, 2) - BigInteger.Log(denominator, 2));
            bool BelowBinary(int power) => power >= 0 ? numerator < (denominator << power) : (numerator << -power) < denominator;
            while (BelowBinary(binaryExponent)) binaryExponent--;
            while (!BelowBinary(binaryExponent + 1)) binaryExponent++;
            if (binaryExponent > 1023) return negative ? double.NegativeInfinity : double.PositiveInfinity;
            int shift = 52 - Math.Max(-1022, binaryExponent);
            var divisor = shift < 0 ? denominator << -shift : denominator;
            var significand = BigInteger.DivRem(shift >= 0 ? numerator << shift : numerator, divisor, out var remainder);
            int comparison = (remainder * 2).CompareTo(divisor);
            if (comparison > 0 || comparison == 0 && !significand.IsEven) significand++;
            double result = (double)significand * Math.Pow(2, -shift);
            return negative ? -result : result;
        }

        internal static string Fixed(double value, int places) => Math.Abs(value) >= IntegerBoundary
            ? new BigInteger(value).ToString(CultureInfo.InvariantCulture) + (places == 0 ? "" : "." + new string('0', places))
            : RoundedDecimal(value, places).ToString("F" + places, CultureInfo.InvariantCulture);

        // Compact general formatting also uses integer arithmetic, so it cannot
        // acquire Framework's older double formatter's extra rounding errors.
        internal static string General(double value, int precision)
        {
            if (value == 0) return "0";
            var coefficient = GeneralCoefficient(value, precision, out int exponent);
            string digits = coefficient.ToString(CultureInfo.InvariantCulture);
            if (digits.Length > precision) { exponent++; digits = digits.Substring(0, precision); }
            digits = digits.TrimEnd('0');
            string sign = value < 0 ? "-" : "";
            if (exponent < -4 || exponent >= precision)
                return sign + digits[0] + (digits.Length > 1 ? "." + digits.Substring(1) : "") + "E" +
                    (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString("D2", CultureInfo.InvariantCulture);
            int point = exponent + 1;
            return sign + (point <= 0 ? "0." + new string('0', -point) + digits : point >= digits.Length
                ? digits + new string('0', point - digits.Length) : digits.Insert(point, "."));
        }

        private static BigInteger GeneralCoefficient(double value, int precision, out int exponent)
        {
            Binary(value, out var numerator, out var denominator);
            exponent = (int)Math.Floor(Math.Log10(Math.Abs(value)));
            bool BelowPower(int power) => power >= 0 ? numerator < denominator * Tens[power] : numerator * Tens[-power] < denominator;
            while (BelowPower(exponent)) exponent--;
            while (!BelowPower(exponent + 1)) exponent++;
            int scale = precision - 1 - exponent;
            return scale >= 0 ? Rounded(numerator * Tens[scale], denominator) : Rounded(numerator, denominator * Tens[-scale]);
        }

        internal static double GeneralValue(double value, int precision)
        {
            if (value == 0) return 0;
            var coefficient = GeneralCoefficient(value, precision, out int exponent);
            int scale = precision - 1 - exponent;
            return RationalDouble(scale < 0 ? coefficient * Tens[-scale] : coefficient,
                scale > 0 ? Tens[scale] : BigInteger.One, value < 0, Math.Abs(value));
        }

        internal static string RoundTrip(double value)
        {
            for (int precision = 15; precision <= 17; precision++)
            {
                string text = General(value, precision);
                if (TryParse(text, out double parsed) && BitConverter.DoubleToInt64Bits(parsed) == BitConverter.DoubleToInt64Bits(value))
                    return text;
            }
            return General(value, 17);
        }

        private static bool FitsDecimalExactly(string text)
        {
            int significant = 0, fractional = 0, index = 0;
            bool point = false;
            for (; index < text.Length && text[index] != 'e' && text[index] != 'E'; index++)
            {
                char c = text[index];
                if (c == '.') point = true;
                if (c < '0' || c > '9') continue;
                if (c != '0' || significant > 0) significant++;
                if (point) fractional++;
            }
            int exponent = 0;
            if (index < text.Length && !int.TryParse(text.Substring(index + 1), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out exponent)) return false;
            return significant <= 28 && (long)fractional - exponent <= 28;
        }

        internal static bool TryParse(string text, out double value)
        {
            text = text.Trim();
            if (FitsDecimalExactly(text) && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number))
            {
                value = ToDouble(number); return true;
            }
            // Outside decimal's exact range, parse the integer rational directly. This
            // avoids net48's mis-rounded double.Parse for large integer addresses.
            value = 0;
            if (text.Length == 0) return false;
            int index = 0; bool negative = text[0] == '-';
            if (negative || text[0] == '+') index++;
            BigInteger coefficient = BigInteger.Zero;
            int scale = 0, digits = 0; bool point = false;
            for (; index < text.Length && text[index] != 'e' && text[index] != 'E'; index++)
            {
                char c = text[index];
                if (c == '.' && !point) { point = true; continue; }
                if (c < '0' || c > '9') return false;
                coefficient = coefficient * 10 + (c - '0'); digits++;
                if (point) scale++;
            }
            if (digits == 0) return false;
            if (index < text.Length)
            {
                if (!int.TryParse(text.Substring(index + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int exponent)) return false;
                if (exponent > 10000 || exponent < -10000) return false;
                scale -= exponent;
            }
            if (coefficient.IsZero) return true;
            if (scale > 10000 || scale < -10000) return false;
            var numerator = scale < 0 ? coefficient * BigInteger.Pow(10, -scale) : coefficient;
            var denominator = scale > 0 ? BigInteger.Pow(10, scale) : BigInteger.One;
            value = RationalDouble(numerator, denominator, negative, 0);
            return !double.IsInfinity(value);
        }
    }
}
