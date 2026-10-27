using BetterGhPanel.Core;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace BetterGhPanel.Runtime
{
    internal static class PanelUnitOperations
    {
        internal static GH_Structure<IGH_Goo> Apply(GH_Structure<IGH_Goo> source, PanelUnitConversion conversion)
        {
            if (conversion == PanelUnitConversion.None) return source;
            var result = new GH_Structure<IGH_Goo>();
            for (int b = 0; b < source.PathCount; b++)
            {
                var path = new GH_Path(source.Paths[b]);
                result.EnsurePath(path);
                foreach (var item in source.Branches[b])
                {
                    IGH_Goo converted = item;
                    // Invalid scalar goo can contain finite unset sentinels. Scaling
                    // those would turn an invalid input into an ordinary valid number.
                    if (item?.GetType() == typeof(GH_Number))
                    {
                        var number = (GH_Number)item;
                        if (number.IsValid) converted = new GH_Number(PanelUnits.Convert(number.Value, conversion));
                    }
                    else if (item?.GetType() == typeof(GH_Integer))
                    {
                        var integer = (GH_Integer)item;
                        if (integer.IsValid) converted = new GH_Number(PanelUnits.Convert(integer.Value, conversion));
                    }
                    result.Append(converted, path);
                }
            }
            return result;
        }
    }
}
