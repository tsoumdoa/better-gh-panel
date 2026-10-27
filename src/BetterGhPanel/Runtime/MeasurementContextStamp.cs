using System;

namespace BetterGhPanel.Runtime
{
    // A managed comparison of the Rhino context used to capture and measure.
    internal readonly struct MeasurementContextStamp : IEquatable<MeasurementContextStamp>
    {
        internal MeasurementContextStamp(uint document, int unitSystem, double tolerance, string units)
        { Document = document; UnitSystem = unitSystem; Tolerance = BitConverter.DoubleToInt64Bits(tolerance); Units = units; }
        internal uint Document { get; }
        internal int UnitSystem { get; }
        private long Tolerance { get; }
        private string Units { get; }
        public bool Equals(MeasurementContextStamp other) => Document == other.Document && UnitSystem == other.UnitSystem &&
            Tolerance == other.Tolerance && string.Equals(Units, other.Units, StringComparison.Ordinal);
        public override bool Equals(object? value) => value is MeasurementContextStamp other && Equals(other);
        public override int GetHashCode() => unchecked((int)Document * 397 ^ UnitSystem);
    }
}
