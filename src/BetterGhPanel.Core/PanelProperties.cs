using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace BetterGhPanel.Core
{
    public enum PanelPropertyKind { Number, Boolean, Text, DateTime }

    /// <summary>An immutable captured value. Null dictionary entries advertise deferred properties.</summary>
    public sealed class PanelPropertyValue
    {
        private PanelPropertyValue(PanelPropertyKind kind, double number = 0, string text = "", int power = 0, string units = "", DateTime date = default, bool integer = false)
        { Kind = kind; Number = number; Text = text; Power = power; Units = units; Date = date; KeepInteger = integer; }
        public PanelPropertyKind Kind { get; }
        public double Number { get; }
        public string Text { get; }
        /// <summary>Zero is unitless; 1–3 are model length powers; -1 denotes radians.</summary>
        public int Power { get; }
        public string Units { get; }
        public DateTime Date { get; }
        public static PanelPropertyValue Numeric(double value, int power = 0, string units = "") => new PanelPropertyValue(PanelPropertyKind.Number, value, power: power, units: units);
        public static PanelPropertyValue Integer(int value) => new PanelPropertyValue(PanelPropertyKind.Number, value, integer: true);
        public static PanelPropertyValue Boolean(bool value) => new PanelPropertyValue(PanelPropertyKind.Boolean, value ? 1 : 0);
        public static PanelPropertyValue String(string value) => new PanelPropertyValue(PanelPropertyKind.Text, text: value);
        public static PanelPropertyValue Time(DateTime value) => new PanelPropertyValue(PanelPropertyKind.DateTime, date: value);
        public double Converted(PanelUnitConversion conversion) => Power == -1
            ? PanelUnits.Convert(Number, conversion == PanelUnitConversion.RadiansToDegrees ? conversion : PanelUnitConversion.None)
            : Power == 0 ? Number : PanelUnits.ConvertGeometry(Number, Power, Units, conversion);
        internal bool KeepInteger { get; }
        internal double Presented(PanelUnitConversion conversion, int decimals, double roundUpFactor) =>
            PanelUnits.Transform(Converted(conversion), PanelUnitConversion.None, Power == 0 ? 0 : roundUpFactor, KeepInteger ? -1 : decimals);
        public string Format(PanelUnitConversion conversion, int decimals, bool includeUnits, double roundUpFactor = 0)
        {
            if (Kind == PanelPropertyKind.Text) return Text;
            if (Kind == PanelPropertyKind.Boolean) return Number == 0 ? "False" : "True";
            if (Kind == PanelPropertyKind.DateTime) return Date.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            double value = Presented(conversion, decimals, roundUpFactor);
            if (double.IsNaN(value) || double.IsInfinity(value)) return "unavailable";
            string suffix = includeUnits ? UnitSymbol(conversion) : "";
            return PanelUnits.Format(value, PanelUnitConversion.None, KeepInteger ? -1 : decimals) + (suffix.Length == 0 ? "" : " " + suffix);
        }
        public string UnitSymbol(PanelUnitConversion conversion)
        {
            if (Power == 0) return "";
            bool converted = Power == -1 ? conversion == PanelUnitConversion.RadiansToDegrees :
                PanelUnits.ConvertGeometry(1, Power, Units, conversion) != 1;
            return converted ? PanelUnits.Symbol(conversion) : Power == -1 ? "rad" : Units + (Power == 2 ? "²" : Power == 3 ? "³" : "");
        }

    }

    public static class PanelProperties
    {
        public static readonly IReadOnlyDictionary<PanelSearchMode, PanelPropertyValue?> Empty =
            new ReadOnlyDictionary<PanelSearchMode, PanelPropertyValue?>(new Dictionary<PanelSearchMode, PanelPropertyValue?>());
        public static IReadOnlyDictionary<PanelSearchMode, PanelPropertyValue?> Freeze(IReadOnlyDictionary<PanelSearchMode, PanelPropertyValue?>? values)
        {
            if (values == null || values.Count == 0) return Empty;
            var copy = new Dictionary<PanelSearchMode, PanelPropertyValue?>();
            foreach (var pair in values) copy.Add(pair.Key, pair.Value);
            return new ReadOnlyDictionary<PanelSearchMode, PanelPropertyValue?>(copy);
        }
        public static bool IsProperty(PanelSearchMode mode) => mode >= PanelSearchMode.Radius && Enum.IsDefined(typeof(PanelSearchMode), mode);
        public static bool IsMetadata(PanelSearchMode mode) => mode == PanelSearchMode.DataType || mode == PanelSearchMode.Validity || mode == PanelSearchMode.BranchPath || mode == PanelSearchMode.ItemIndex;
        public static PanelPropertyKind Kind(PanelSearchMode mode) => mode switch
        {
            PanelSearchMode.Closed or PanelSearchMode.Solid or PanelSearchMode.Planar or PanelSearchMode.Trimmed or PanelSearchMode.BooleanValue or PanelSearchMode.ParallelProjection or PanelSearchMode.PerspectiveProjection or PanelSearchMode.ProjectionLocked => PanelPropertyKind.Boolean,
            PanelSearchMode.Shape or PanelSearchMode.GroupName or PanelSearchMode.ColourName or PanelSearchMode.TextValue or PanelSearchMode.GuidValue or
                PanelSearchMode.DataType or PanelSearchMode.Validity or PanelSearchMode.BranchPath or PanelSearchMode.FontFace or
                PanelSearchMode.CultureName or PanelSearchMode.EnglishName or PanelSearchMode.DefinitionId => PanelPropertyKind.Text,
            PanelSearchMode.DateTime => PanelPropertyKind.DateTime,
            _ => PanelPropertyKind.Number
        };
        public static string Label(PanelSearchMode mode) => mode switch
        {
            PanelSearchMode.Radius => "Radius", PanelSearchMode.Degree => "Degree", PanelSearchMode.Closed => "Closed",
            PanelSearchMode.Solid => "Solid", PanelSearchMode.Planar => "Planar", PanelSearchMode.Shape => "Shape",
            PanelSearchMode.VertexCount => "Vertex count", PanelSearchMode.EdgeCount => "Edge count", PanelSearchMode.FaceCount => "Face count",
            PanelSearchMode.SegmentCount => "Segment count", PanelSearchMode.Trimmed => "Trimmed", PanelSearchMode.DegreeU => "Surface · U degree", PanelSearchMode.DegreeV => "Surface · V degree",
            PanelSearchMode.GroupName => "Group · name", PanelSearchMode.ObjectCount => "Group · object count",
            PanelSearchMode.Red => "Colour · red", PanelSearchMode.Green => "Colour · green", PanelSearchMode.Blue => "Colour · blue", PanelSearchMode.Alpha => "Colour · alpha", PanelSearchMode.ColourName => "Colour · name",
            PanelSearchMode.DomainStart => "Domain · start", PanelSearchMode.DomainEnd => "Domain · end",
            PanelSearchMode.UStart => "Domain · U start", PanelSearchMode.UEnd => "Domain · U end", PanelSearchMode.VStart => "Domain · V start", PanelSearchMode.VEnd => "Domain · V end",
            PanelSearchMode.BooleanValue => "Boolean value", PanelSearchMode.DateTime => "Date and time", PanelSearchMode.Year => "Date · year", PanelSearchMode.Month => "Date · month", PanelSearchMode.Day => "Date · day",
            PanelSearchMode.Hour => "Time · hour", PanelSearchMode.Minute => "Time · minute", PanelSearchMode.Second => "Time · second",
            PanelSearchMode.Magnitude => "Magnitude", PanelSearchMode.Width => "Width", PanelSearchMode.Height => "Height", PanelSearchMode.Depth => "Depth", PanelSearchMode.Perimeter => "Perimeter", PanelSearchMode.Angle => "Arc · angle",
            PanelSearchMode.OriginX => "Plane · origin X", PanelSearchMode.OriginY => "Plane · origin Y", PanelSearchMode.OriginZ => "Plane · origin Z",
            PanelSearchMode.AxisXX => "Plane · X axis X", PanelSearchMode.AxisXY => "Plane · X axis Y", PanelSearchMode.AxisXZ => "Plane · X axis Z",
            PanelSearchMode.AxisYX => "Plane · Y axis X", PanelSearchMode.AxisYY => "Plane · Y axis Y", PanelSearchMode.AxisYZ => "Plane · Y axis Z",
            PanelSearchMode.AxisZX => "Plane · Z axis X", PanelSearchMode.AxisZY => "Plane · Z axis Y", PanelSearchMode.AxisZZ => "Plane · Z axis Z",
            PanelSearchMode.TextValue => "Text value", PanelSearchMode.CharacterCount => "Text · character count", PanelSearchMode.GuidValue => "GUID value",
            PanelSearchMode.Real => "Complex · real", PanelSearchMode.Imaginary => "Complex · imaginary", PanelSearchMode.Phase => "Complex · phase",
            PanelSearchMode.Determinant => "Transform · determinant", PanelSearchMode.ProfileCount => "Extrusion · profile count", PanelSearchMode.PointCount => "Point count",
            PanelSearchMode.DefinitionId => "Block · definition ID", PanelSearchMode.ParallelProjection => "Detail · parallel projection",
            PanelSearchMode.PerspectiveProjection => "Detail · perspective projection", PanelSearchMode.ProjectionLocked => "Detail · projection locked",
            PanelSearchMode.PageToModelRatio => "Detail · page to model ratio", PanelSearchMode.DimensionValue => "Dimension · value",
            PanelSearchMode.LocationX => "Location · X", PanelSearchMode.LocationY => "Location · Y", PanelSearchMode.LocationZ => "Location · Z",
            PanelSearchMode.FontHeight => "Font height", PanelSearchMode.FontFace => "Font face",
            PanelSearchMode.Latitude => "Latitude (degrees)", PanelSearchMode.Longitude => "Longitude (degrees)",
            PanelSearchMode.CultureName => "Culture · name", PanelSearchMode.EnglishName => "Culture · English name",
            PanelSearchMode.RowCount => "Matrix · rows", PanelSearchMode.ColumnCount => "Matrix · columns", PanelSearchMode.PathDepth => "Path · depth",
            PanelSearchMode.PatternIndex => "Hatch · pattern index", PanelSearchMode.PatternScale => "Hatch · scale", PanelSearchMode.PatternRotation => "Hatch · rotation",
            PanelSearchMode.DataType => "Data type", PanelSearchMode.Validity => "Validity", PanelSearchMode.BranchPath => "Branch path", PanelSearchMode.ItemIndex => "Item index",
            _ => mode >= PanelSearchMode.M00 && mode <= PanelSearchMode.M33 ? "Transform · " + mode : mode.ToString()
        };
        public static bool TryGet(PanelRow row, PanelSearchMode mode, PanelGeometryInfo? measured, out PanelPropertyValue? value)
        {
            value = mode switch
            {
                PanelSearchMode.DataType => PanelPropertyValue.String(row.TypeName),
                PanelSearchMode.Validity => PanelPropertyValue.String(row.IsNull ? "Null" : row.Validity == PanelValidity.VerifiedValid ? "Valid" : row.Validity == PanelValidity.VerifiedInvalid ? "Invalid" : "Unverified"),
                PanelSearchMode.BranchPath => PanelPropertyValue.String(row.Path),
                PanelSearchMode.ItemIndex => PanelPropertyValue.Integer(row.OriginalIndex),
                _ => null
            };
            if (IsMetadata(mode)) return true;
            if (!row.IsValid || row.IsNull) return false;
            var geometry = measured ?? row.GeometryInfo;
            return row.Properties.TryGetValue(mode, out value) || geometry != null && geometry.Properties.TryGetValue(mode, out value);
        }
    }
}
