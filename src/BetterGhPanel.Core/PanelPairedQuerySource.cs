using System;
using System.Collections.Generic;

namespace BetterGhPanel.Core
{
    // Maps transformed D positions to the immutable snapshot of the F input.
    internal sealed class PanelPairedQuerySource
    {
        private readonly Func<int, int?> _partner;
        internal PanelPairedQuerySource(PanelSnapshot snapshot, Func<int, int?> partner)
        { Snapshot = snapshot; _partner = partner; }

        internal PanelSnapshot Snapshot { get; }
        internal IReadOnlyDictionary<int, PanelGeometryInfo?>? Measurements { get; set; }
        internal int? PositionAt(int position) => _partner(position);
        internal PanelRow? RowAt(int position) => PositionAt(position) is int item ? Snapshot.Rows[item] : null;
        internal PanelGeometryInfo? GeometryAt(int position) =>
            PositionAt(position) is int item && Measurements != null && Measurements.TryGetValue(item, out var value)
                ? value ?? Snapshot.Rows[item].GeometryInfo?.WithUnavailableMeasurement() : null;
    }
}
