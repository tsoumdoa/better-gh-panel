using System;
using System.Drawing;
using BetterGhPanel.Core;

namespace BetterGhPanel.UI
{
    // Presentation state only: paging and expansion never change a query or output.
    internal sealed class CanvasSetDetails
    {
        internal const int PageSize = 4;
        internal const string Scope = "Locations refer to the original input tree, before Simplify, Trim, Flatten and Reverse. " +
            "Item indices start at 0. Counts include matching displayed values after filters, units and decimal formatting. " +
            "Expanding and paging do not change selection or output.";
        private PanelSnapshot? _snapshot;
        private PanelQueryResult? _checkedResult;
        internal int SourcePosition { get; private set; } = -1;
        internal int Offset { get; private set; }
        internal PanelSetOccurrences? Occurrences { get; private set; }
        internal bool IsOpen => _snapshot != null && SourcePosition >= 0;
        internal int Visible => Math.Min(PageSize, (Occurrences?.Count ?? 0) - Offset);
        internal bool HasPages => Occurrences?.Count > PageSize;
        internal bool CanPrevious => Offset > 0;
        internal bool CanNext => Offset + Visible < (Occurrences?.Count ?? 0);
        internal RectangleF Bounds { get; private set; }
        internal RectangleF Collapse { get; private set; }
        internal RectangleF Previous { get; private set; }
        internal RectangleF Next { get; private set; }
        internal RectangleF Table { get; private set; }
        internal static string Summary(PanelSetOccurrences occurrences) =>
            $"{occurrences.Count:N0} " + (occurrences.Count == 1 ? "occurrence" : "occurrences") +
            $" · {occurrences.BranchCount:N0} input " + (occurrences.BranchCount == 1 ? "branch" : "branches");

        internal void Toggle(PanelQueryResult result, int position)
        {
            var snapshot = result.Snapshot;
            if (position < 0 || position >= snapshot.Rows.Count || result.SetOccurrencesAt(position) == null) return;
            if (ReferenceEquals(_snapshot, snapshot) && SourcePosition == position) { Close(); return; }
            _snapshot = snapshot;
            Occurrences = result.SetOccurrencesAt(position);
            SourcePosition = position;
            Offset = 0;
            _checkedResult = null;
        }

        internal void Synchronize(PanelSnapshot snapshot, PanelQueryResult? result)
        {
            if (!IsOpen) return;
            if (!ReferenceEquals(_snapshot, snapshot)) { Close(); return; }
            if (result == null || ReferenceEquals(result, _checkedResult)) return;
            _checkedResult = result;
            if (!result.TryGetViewIndex(SourcePosition, out _) || result.SetOccurrencesAt(SourcePosition) == null) { Close(); return; }
            Occurrences = result.SetOccurrencesAt(SourcePosition);
            Offset = Math.Min(Offset, (Occurrences!.Count - 1) / PageSize * PageSize);
        }

        internal void Close()
        {
            _snapshot = null;
            Occurrences = null;
            _checkedResult = null;
            SourcePosition = -1;
            Offset = 0;
            Bounds = Collapse = Previous = Next = Table = RectangleF.Empty;
        }

        internal void Page(int direction)
        {
            int lastPage = Math.Max(0, ((Occurrences?.Count ?? 1) - 1) / PageSize * PageSize);
            Offset = Math.Max(0, Math.Min(lastPage, Offset + direction * PageSize));
        }

        internal void Layout(RectangleF panel, bool visible)
        {
            if (!visible || !IsOpen)
            {
                Bounds = Collapse = Previous = Next = Table = RectangleF.Empty;
                return;
            }
            // Keep the drawer's height stable while paging, even on its last page.
            int rows = Math.Min(PageSize, Occurrences!.Count);
            Bounds = new RectangleF(panel.X, panel.Bottom + 4, panel.Width, 57 + rows * 17 + (HasPages ? 19 : 0));
            Collapse = new RectangleF(Bounds.Right - 24, Bounds.Y + 2, 20, 20);
            Table = new RectangleF(Bounds.X + 6, Bounds.Y + 53, Bounds.Width - 12, rows * 17);
            Previous = HasPages ? new RectangleF(Bounds.Right - 113, Bounds.Bottom - 21, 52, 18) : RectangleF.Empty;
            Next = HasPages ? new RectangleF(Bounds.Right - 59, Bounds.Bottom - 21, 52, 18) : RectangleF.Empty;
        }

        internal PanelSourceAddress? LocationAt(PointF point)
        {
            if (Table.IsEmpty || !Table.Contains(point) || Occurrences == null) return null;
            int index = (int)((point.Y - Table.Y) / 17);
            return index < Visible ? Occurrences.Locations[Offset + index] : (PanelSourceAddress?)null;
        }
    }
}
