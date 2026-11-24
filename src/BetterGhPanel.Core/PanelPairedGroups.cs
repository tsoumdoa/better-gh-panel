using System;
using System.Collections.Generic;
using System.Globalization;

namespace BetterGhPanel.Core
{
    internal sealed class PanelPairedGroups
    {
        private readonly PanelSnapshot _data;
        private readonly PanelQueryOptions _query;
        private readonly Dictionary<string, Dictionary<(PanelText Text, bool NullKey), Group>> _branches =
            new Dictionary<string, Dictionary<(PanelText, bool), Group>>(StringComparer.Ordinal);

        internal PanelPairedGroups(PanelSnapshot data, PanelQueryOptions query)
        { _data = data; _query = query; }

        internal Group Add(int position)
        {
            string path = _data.Rows[position].Path;
            var source = _query.PairedSource;
            var row = source?.RowAt(position);
            var key = PanelDisplayedValue.CapturePaired(row, _data.UnitConversion, _query, source?.GeometryAt(position));
            if (!_branches.TryGetValue(path, out var groups))
                _branches.Add(path, groups = new Dictionary<(PanelText, bool), Group>());
            var identity = (key.SemanticText, row == null || row.IsNull);
            if (!groups.TryGetValue(identity, out var group))
            {
                int index = groups.Count;
                string suffix = index.ToString(CultureInfo.InvariantCulture) + "}";
                string destination = _query.Flattened ? "{" + suffix : path.Substring(0, path.Length - 1) + ";" + suffix;
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
