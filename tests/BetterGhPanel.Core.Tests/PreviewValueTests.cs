using System;
using System.Collections.Generic;
using System.Drawing;
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
        var row = Geometry(new PanelGeometryInfo("Surface", power: 2, metricName: "Area", canMeasure: true));
        var snapshot = Snapshot(row);
        var pending = PanelPreviewValues.Sample(snapshot, row, PanelSearchMode.Text, null, 2, 0);
        Assert.Null(pending.Number); Assert.True(pending.Pending); Assert.Equal("…", pending.Label(null, true));
        Assert.Equal("2 · …", pending.Label(2, true));
        var failed = PanelPreviewValues.Sample(snapshot, row, PanelSearchMode.Text, row.GeometryInfo!.WithUnavailableMeasurement(), 2, 0);
        Assert.False(failed.Pending); Assert.Equal("", failed.Label(null, true));
        var missing = PanelPreviewValues.Sample(snapshot, row, PanelSearchMode.Length, null, 2, 0);
        Assert.False(missing.Pending); Assert.Null(missing.Number);
        var point = new PanelRow(0, "{0}", "Point", "", null, PanelValidity.VerifiedValid, components: new PanelComponents(1, 2, 3, "m"));
        Assert.Equal("", PanelPreviewValues.Sample(Snapshot(point), point, PanelSearchMode.Text, null, 2, 0).Label(null, true));
        Assert.Equal("1.00 m", PanelPreviewValues.Sample(Snapshot(point), point, PanelSearchMode.X, null, 2, 0).Text);
    }

    [Fact]
    public void UnmeasuredCurveWithUnknownMainMetricStillHasPendingDot()
    {
        var row = Geometry(new PanelGeometryInfo("Curve", metricName: "", metricLabel: "", canMeasure: true));
        var pending = PanelPreviewValues.Sample(Snapshot(row), row, PanelSearchMode.Text, null, 2, 0);
        Assert.True(pending.Pending);
        Assert.Null(pending.Number);
        Assert.Equal("2 · …", pending.Label(2, true));
        Assert.Equal("", PanelPreviewValues.Sample(Snapshot(row), row, PanelSearchMode.Text,
            row.GeometryInfo!.WithUnavailableMeasurement(), 2, 0).Label(null, true));
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

    [Theory]
    [InlineData(PanelValuePalette.Viridis, 0x440154, 0x21918c, 0xfde725)]
    [InlineData(PanelValuePalette.Heat, 0x000004, 0xb73779, 0xfcfdbf)]
    [InlineData(PanelValuePalette.BlueWhiteRed, 0x2166ac, 0xf7f7f7, 0xb2182b)]
    public void PaletteStopsReverseAndContrastMatchMock(PanelValuePalette palette, int first, int middle, int last)
    {
        Assert.Equal(first, PanelPreviewValues.Colour(palette, 0, false).ToArgb() & 0xffffff);
        Assert.Equal(middle, PanelPreviewValues.Colour(palette, 32, false).ToArgb() & 0xffffff);
        Assert.Equal(last, PanelPreviewValues.Colour(palette, 64, false).ToArgb() & 0xffffff);
        Assert.Equal(PanelPreviewValues.Colour(palette, 0, false), PanelPreviewValues.Colour(palette, 64, true));
        Assert.Equal(Color.White, PanelPreviewValues.TextColour(Color.Black));
        Assert.Equal(Color.Black, PanelPreviewValues.TextColour(Color.White));
    }

    [Fact]
    public void PreviewPagesStayBelowRowLimitAndDisableOnlyDependentChoices()
    {
        var menu = new CanvasMenu(); var options = new PanelQueryOptions();
        menu.Toggle(CanvasMenuKind.Preview, PanelSnapshot.Empty, options);
        Assert.Equal(7, menu.Count); Assert.Equal("Show values", menu.Label(4));
        Assert.False(menu.RowEnabled(5, false, false, PanelValuePalette.Off));
        Assert.True(menu.RowEnabled(5, false, true, PanelValuePalette.Off));
        Assert.Equal("Centre…", menu.Trailing(5, PanelDotPlacement.Centre, PanelValuePalette.Off));
        menu.Toggle(CanvasMenuKind.DotPlacement, PanelSnapshot.Empty, options);
        Assert.Equal(4, menu.Count); Assert.Equal("‹ Preview", menu.Label(0)); Assert.Equal("Box bottom", menu.Label(3));
        menu.Toggle(CanvasMenuKind.ValuePalette, PanelSnapshot.Empty, options);
        Assert.Equal(6, menu.Count); Assert.Equal("Blue–white–red", menu.Label(4)); Assert.Equal("Reverse", menu.Label(5));
        Assert.False(menu.RowEnabled(5, false, false, PanelValuePalette.Off));
        Assert.True(menu.RowEnabled(5, false, false, PanelValuePalette.Heat));
        bool Enabled(int index) => menu.RowEnabled(index, false, false, PanelValuePalette.Off);
        menu.MoveHover(-1, Enabled, fromEdge: true);
        Assert.Equal(4, menu.HoveredIndex);
        menu.MoveHover(1, Enabled);
        Assert.Equal(0, menu.HoveredIndex);
    }
}
