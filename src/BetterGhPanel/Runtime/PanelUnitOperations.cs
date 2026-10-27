using System;
using BetterGhPanel.Core;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace BetterGhPanel.Runtime
{
    internal static class PanelUnitOperations
    {
        internal static GH_Structure<IGH_Goo> Apply(GH_Structure<IGH_Goo> source, PanelUnitConversion conversion)
            => conversion == PanelUnitConversion.None ? source : ApplyCaptured(source, conversion, CapturedScalar.Capture(source));

        internal static GH_Structure<IGH_Goo> ApplyCaptured(GH_Structure<IGH_Goo> source, PanelUnitConversion conversion,
            CapturedScalar[] captured)
        {
            if (conversion == PanelUnitConversion.None) return source;
            var result = new GH_Structure<IGH_Goo>();
            int position = 0;
            for (int b = 0; b < source.PathCount; b++)
            {
                var path = new GH_Path(source.Paths[b]);
                result.EnsurePath(path);
                foreach (var item in source.Branches[b])
                {
                    IGH_Goo converted = item;
                    // Invalid scalar goo can contain finite unset sentinels. Scaling
                    // those would turn an invalid input into an ordinary valid number.
                    var value = captured[position++];
                    if (value.IsNumeric && value.IsValid) converted = new GH_Number(PanelUnits.Convert(value.NumericValue, conversion));
                    result.Append(converted, path);
                }
            }
            return result;
        }

        internal static bool InputsStillMatch(GH_Structure<IGH_Goo>? source, GH_Structure<IGH_Goo>? converted, PanelUnitConversion conversion)
            => InputsStillMatchWithVisits(source, converted, conversion, out _);

        internal static bool InputsStillMatchWithVisits(GH_Structure<IGH_Goo>? source, GH_Structure<IGH_Goo>? converted,
            PanelUnitConversion conversion, out int visits)
        {
            visits = 0;
            if (source == null || converted == null || source.PathCount != converted.PathCount) return false;
            for (int b = 0; b < source.PathCount; b++)
            {
                if (source.Paths[b].CompareTo(converted.Paths[b]) != 0 || source.Branches[b].Count != converted.Branches[b].Count) return false;
                for (int i = 0; i < source.Branches[b].Count; i++)
                {
                    visits++;
                    var original = source.Branches[b][i]; var result = converted.Branches[b][i];
                    if (conversion == PanelUnitConversion.None) { if (!ReferenceEquals(original, result)) return false; continue; }
                    if (original?.GetType() == typeof(GH_Number))
                    {
                        var number = (GH_Number)original;
                        if (!number.IsValid) { if (!ReferenceEquals(original, result)) return false; continue; }
                        if (result?.GetType() != typeof(GH_Number) ||
                            BitConverter.DoubleToInt64Bits(((GH_Number)result).Value) != BitConverter.DoubleToInt64Bits(PanelUnits.Convert(number.Value, conversion))) return false;
                    }
                    else if (original?.GetType() == typeof(GH_Integer))
                    {
                        var integer = (GH_Integer)original;
                        if (!integer.IsValid) { if (!ReferenceEquals(original, result)) return false; continue; }
                        if (result?.GetType() != typeof(GH_Number) ||
                            BitConverter.DoubleToInt64Bits(((GH_Number)result).Value) != BitConverter.DoubleToInt64Bits(PanelUnits.Convert(integer.Value, conversion))) return false;
                    }
                    else if (!ReferenceEquals(original, result)) return false;
                }
            }
            return true;
        }
    }
}
