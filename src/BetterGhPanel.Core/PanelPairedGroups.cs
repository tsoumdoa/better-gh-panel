using System;
using System.Collections.Generic;
using System.Globalization;

namespace BetterGhPanel.Core
{
    internal sealed class PanelPairedGroups
    {
        private readonly PanelSnapshot _data;
        private readonly PanelQueryOptions _query;
        private enum KeyKind { Number, Text, Other }
        private readonly Dictionary<string, Dictionary<(PanelText Text, KeyKind Kind, bool NullKey), Group>> _branches =
            new Dictionary<string, Dictionary<(PanelText, KeyKind, bool), Group>>(StringComparer.Ordinal);

        internal PanelPairedGroups(PanelSnapshot data, PanelQueryOptions query)
        { _data = data; _query = query; }

        internal Group Add(int position)
        {
            string path = _data.Rows[position].Path;
            var source = _query.PairedSource;
            int? item = source?.PositionAt(position);
            var row = item is int indexOfKey ? source!.Snapshot.Rows[indexOfKey] : null;
            var key = PanelDisplayedValue.CapturePaired(source?.Snapshot, item, _data.UnitConversion, _query, source?.GeometryAt(position));
            if (!_branches.TryGetValue(path, out var groups))
                _branches.Add(path, groups = new Dictionary<(PanelText, KeyKind, bool), Group>());
            var kind = row?.NumericValue.HasValue == true ? KeyKind.Number
                : row?.StringValue != null || row?.TypeName == "Text" ? KeyKind.Text : KeyKind.Other;
            var identity = (key.SemanticText, kind, row == null || row.IsNull);
            if (!groups.TryGetValue(identity, out var group))
            {
                int index = groups.Count;
                string suffix = index.ToString(CultureInfo.InvariantCulture) + "}";
                string destination = source?.Flattened == true ? "{" + suffix : path.Substring(0, path.Length - 1) + ";" + suffix;
                group = new Group(position, index, destination, key);
                groups.Add(identity, group);
            }
            group.Count++;
            return group;
        }

        internal sealed class Group
        {
            internal Group(int representative, int index, string path, PanelDisplayedValue key)
            { Representative = representative; Index = index; Path = path; Key = key; }
            internal int Representative { get; }
            internal int Index { get; }
            internal string Path { get; }
            internal PanelDisplayedValue Key { get; }
            internal int Count { get; set; }
        }
    }
}
