using BetterGhPanel.Core;

internal static class PanelQueryTestData
{
    internal static PanelSnapshot Sample() => new PanelSnapshot(
        new[]
        {
            new PanelRow(0, "{0;1}", "Number", "10", 10),
            new PanelRow(1, "{0;1}", "Number", "2", 2),
            new PanelRow(0, "{0;1;2}", "Text", "Alpha", null),
            new PanelRow(0, "{0;10}", "Number", "10", 10),
            new PanelRow(1, "{0;10}", "Null", "<null>", null, true, false),
            new PanelRow(2, "{0;10}", "Text", "broken", null, false, false)
        },
        new[]
        {
            new PanelBranch("{0;1}", 2), new PanelBranch("{0;1;2}", 1),
            new PanelBranch("{0;10}", 3), new PanelBranch("{9}", 0)
        });

    internal static PanelQueryResult Query(PanelQueryOptions? options = null) =>
        PanelQueryEngine.Execute(Sample(), options ?? new PanelQueryOptions());

}
