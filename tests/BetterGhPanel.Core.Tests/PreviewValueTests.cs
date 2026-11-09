using System;
using System.Collections.Generic;
using BetterGhPanel.Core;
using BetterGhPanel.UI;
using Xunit;

public class PreviewValueTests
{
    private static PanelSnapshot Snapshot(PanelRow row, PanelUnitConversion conversion = PanelUnitConversion.None) =>
        new PanelSnapshot(new[] { row }, Array.Empty<PanelBranch>(), conversion);
    private static PanelRow Geometry(PanelGeometryInfo info) => new PanelRow(2, "{0}", "Geometry", "", null, geometryInfo: info);

    [Fact]
    public void MainMetricAndExplicitAreaShareRowFormatting()
    {
        var solid = new PanelGeometryInfo("Solid", 12.345, 3, "m", "Volume", "V", hasArea: true, areaValue: 45.678);
        var row = Geometry(solid); var snapshot = Snapshot(row);
        var volume = PanelPreviewValues.Sample(snapshot, row, PanelSearchMode.Text, null, 2, .25);
        Assert.Equal("Volume", volume.Metric);
        Assert.Equal("12.50 m³", volume.Text);
        Assert.Equal(12.5, volume.Number);
        Assert.Equal("12.50", new PanelPreviewRange(volume).MinText);
        Assert.Equal("2 · 12.50 m³", volume.Label(2, true));
        Assert.Equal("2", volume.Label(2, false));
        var area = PanelPreviewValues.Sample(snapshot, row, PanelSearchMode.Area, null, 2, 0);
        Assert.Equal("Area", area.Metric);
        Assert.Equal("45.68 m²", area.Text);
        Assert.Equal(45.68, area.Number);
        var length = Geometry(new PanelGeometryInfo("Curve", 12400, units: "mm"));
        Assert.Equal("12.40 m", PanelPreviewValues.Sample(Snapshot(length, PanelUnitConversion.MillimetersToMeters), length, PanelSearchMode.Text, null, 2, 0).Text);
    }

    [Fact]
    public void MissingPendingAndFailedMetricsAreDistinct()
    {
        foreach (var info in new[] {
            new PanelGeometryInfo("Surface", power: 2, metricName: "Area", canMeasure: true),
            new PanelGeometryInfo("Curve", metricName: "", metricLabel: "", canMeasure: true) })
        {
            var row = Geometry(info); var snapshot = Snapshot(row);
            var pending = PanelPreviewValues.Sample(snapshot, row, PanelSearchMode.Text, null, 2, 0);
            Assert.Null(pending.Number); Assert.True(pending.Pending); Assert.Equal("…", pending.Label(null, true));
            Assert.Equal("2 · …", pending.Label(2, true));
            var failed = PanelPreviewValues.Sample(snapshot, row, PanelSearchMode.Text, info.WithUnavailableMeasurement(), 2, 0);
            Assert.False(failed.Pending); Assert.Equal("", failed.Label(null, true));
            var missing = PanelPreviewValues.Sample(snapshot, row, PanelSearchMode.Length, null, 2, 0);
            Assert.False(missing.Pending); Assert.Null(missing.Number);
        }
        var point = new PanelRow(0, "{0}", "Point", "", null, PanelValidity.VerifiedValid, components: new PanelComponents(1, 2, 3, "m"));
        Assert.Equal("", PanelPreviewValues.Sample(Snapshot(point), point, PanelSearchMode.Text, null, 2, 0).Label(null, true));
        Assert.Equal("1.00 m", PanelPreviewValues.Sample(Snapshot(point), point, PanelSearchMode.X, null, 2, 0).Text);
    }

    [Fact]
    public void CircleKeepsRadiusUnlessLengthIsExplicitlyRequested()
    {
        var row = Geometry(new PanelGeometryInfo("Circle", 2000, units: "mm", metricName: "Radius", metricLabel: "R"));
        var snapshot = Snapshot(row, PanelUnitConversion.MillimetersToMeters);
        var radius = PanelPreviewValues.Sample(snapshot, row, PanelSearchMode.Text, null, 2, 0);
        Assert.Equal("2.00 m", radius.Text);
        Assert.Equal(2, radius.Number);
        Assert.Equal("Radius", radius.Metric);
        var circumference = PanelPreviewValues.Sample(snapshot, row, PanelSearchMode.Length, null, 2, 0);
        Assert.Equal("12.57 m", circumference.Text);
        Assert.Equal("Length", circumference.Metric);
        Assert.NotEqual(radius.Key, circumference.Key);
    }

    [Fact]
    public void VectorAxesAreUnitlessAndNumericPropertiesOverrideMainMetric()
    {
        var vector = new PanelRow(0, "{0}", "Vector", "", null, PanelValidity.VerifiedValid, components: new PanelComponents(2, 3, 4, "mm"));
        var value = PanelPreviewValues.Sample(Snapshot(vector, PanelUnitConversion.MillimetersToMeters), vector, PanelSearchMode.X, null, 2, 1);
        Assert.Equal(2, value.Number); Assert.Equal("2.00", value.Text); Assert.Equal("Vector X", value.Metric); Assert.Equal("", value.Unit);
        var row = Geometry(new PanelGeometryInfo("Curve", 9).WithProperties(new Dictionary<PanelSearchMode, PanelPropertyValue?> { [PanelSearchMode.Degree] = PanelPropertyValue.Integer(3) }));
        var degree = PanelPreviewValues.Sample(Snapshot(row), row, PanelSearchMode.Degree, null, 2, 0);
        Assert.Equal("3", degree.Text);
        Assert.Equal("3", new PanelPreviewRange(degree).MinText);
        Assert.Equal("Length", PanelPreviewValues.Sample(Snapshot(row), row, PanelSearchMode.Validity, null, 2, 0).Metric);
    }

    [Fact]
    public void RangesSeparateMetricsAndUnitsAndUseMidpointForOneDistinctValue()
    {
        var range = new PanelPreviewRange(new PanelPreviewValue("Length", "m", 5, "5"));
        Assert.Equal(32, range.Bucket(5)); range.Add(5); Assert.Equal(32, range.Bucket(5));
        range.Add(15); Assert.Equal(0, range.Bucket(5)); Assert.Equal(64, range.Bucket(15)); Assert.Equal(32, range.Bucket(10));
        Assert.NotEqual(new PanelPreviewValue("Length", "m", 5, "").Key, new PanelPreviewValue("Length", "mm", 5, "").Key);
        Assert.NotEqual(new PanelPreviewValue("Length", "m", 5, "").Key, new PanelPreviewValue("X", "m", 5, "").Key);
        var extremes = new PanelPreviewRange(new PanelPreviewValue("X", "m", -1e308, ""));
        extremes.Add(1e308);
        Assert.Equal(0, extremes.Bucket(-1e308)); Assert.Equal(32, extremes.Bucket(0)); Assert.Equal(64, extremes.Bucket(1e308));
    }

    [Fact]
    public void PreviewMenuDisablesDependentChoicesAndSkipsThemByKeyboard()
    {
        var menu = new CanvasMenu(); var options = new PanelQueryOptions();
        menu.Toggle(CanvasMenuKind.Preview, PanelSnapshot.Empty, options);
        Assert.False(menu.RowEnabled(CanvasPreviewRows.Placement, false, false, PanelValuePalette.Off));
        Assert.True(menu.RowEnabled(CanvasPreviewRows.Placement, false, true, PanelValuePalette.Off));
        menu.Toggle(CanvasMenuKind.ValuePalette, PanelSnapshot.Empty, options);
        Assert.False(menu.RowEnabled(CanvasPreviewRows.Reverse, false, false, PanelValuePalette.Off));
        Assert.True(menu.RowEnabled(CanvasPreviewRows.Reverse, false, false, PanelValuePalette.Heat));
        bool Enabled(int index) => menu.RowEnabled(index, false, false, PanelValuePalette.Off);
        menu.MoveHover(-1, Enabled, fromEdge: true);
        Assert.Equal(CanvasPreviewRows.PaletteLast, menu.HoveredIndex);
        menu.MoveHover(1, Enabled);
        Assert.Equal(CanvasPreviewRows.Back, menu.HoveredIndex);
    }
}
