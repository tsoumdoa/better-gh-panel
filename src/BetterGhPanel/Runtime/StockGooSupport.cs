using System;
using System.Collections.Generic;
using BetterGhPanel.Core;
using Grasshopper.Kernel.Types;

namespace BetterGhPanel.Runtime
{
    internal static class StockGooSupport
    {
        private static readonly Dictionary<Type, PanelStockType> Types = Build();
        private static Dictionary<Type, PanelStockType> Build()
        {
            var types = new Dictionary<Type, PanelStockType>();
            foreach (var entry in PanelStockTypes.All)
            {
                Type? type = typeof(IGH_Goo).Assembly.GetType("Grasshopper.Kernel.Types." + entry.SdkName);
                if (type != null) types.Add(type, entry);
            }
            return types;
        }
        internal static PanelStockType? Find(Type type) => Types.TryGetValue(type, out var entry) ? entry : null;
        internal static bool Has(Type type, StockTypeFacets facet) => Find(type)?.Has(facet) == true;
    }
}
