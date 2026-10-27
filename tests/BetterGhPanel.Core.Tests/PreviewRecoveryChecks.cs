using System.Collections.Generic;
using System.Linq;
using BetterGhPanel.UI;
using Xunit;

namespace BetterGhPanel.Core.Tests;

public sealed class PreviewRecoveryChecks
{
    [Fact]
    public void DenseSelectionRetainsSuffixAcrossToggleAndRecoversInBoundedTurns()
    {
        var recovery = new PanelPreviewRecovery();
        var selected = Enumerable.Range(0, 300).ToHashSet();
        var admitted = new HashSet<int>();
        recovery.Reset(selected.OrderBy(i => i).ToArray());
        int visits = 0;
        PanelPreviewRecovery.Admission Admit(int position) { visits++; admitted.Add(position); return PanelPreviewRecovery.Admission.Complete; }
        Assert.True(recovery.Pump(128, selected.Contains, admitted.Contains, Admit));
        Assert.Equal(128, visits);
        selected.Remove(250);
        visits = 0;
        Assert.True(recovery.Pump(128, selected.Contains, admitted.Contains, Admit));
        Assert.InRange(visits, 1, 128);
        Assert.True(recovery.Pending);
        visits = 0;
        Assert.False(recovery.Pump(128, selected.Contains, admitted.Contains, Admit));
        Assert.InRange(visits, 1, 128);
        Assert.False(recovery.Pending);
        Assert.True(selected.SetEquals(admitted));
    }

    [Fact]
    public void CapacityDefersWithoutLosingDemandAndRecoversAfterEviction()
    {
        var recovery = new PanelPreviewRecovery();
        var selected = Enumerable.Range(0, 200).ToHashSet();
        var admitted = new HashSet<int>();
        recovery.Reset(selected.OrderBy(i => i).ToArray());
        int visits = 0;
        PanelPreviewRecovery.Admission Admit(int position)
        {
            visits++;
            if (admitted.Count >= 150) return PanelPreviewRecovery.Admission.Blocked;
            admitted.Add(position); return PanelPreviewRecovery.Admission.Complete;
        }
        Assert.True(recovery.Pump(128, selected.Contains, admitted.Contains, Admit));
        Assert.False(recovery.Pump(128, selected.Contains, admitted.Contains, Admit));
        Assert.True(recovery.Pending);
        Assert.Equal(150, admitted.Count);
        foreach (int position in Enumerable.Range(0, 50)) { selected.Remove(position); admitted.Remove(position); }
        visits = 0;
        Assert.False(recovery.Pump(128, selected.Contains, admitted.Contains, Admit));
        Assert.Equal(50, visits);
        Assert.False(recovery.Pending);
        Assert.True(selected.SetEquals(admitted));
    }

    [Fact]
    public void ReentrantReplacementCannotAdvanceOrAttachTheNewBatch()
    {
        var recovery = new PanelPreviewRecovery();
        recovery.Reset(new[] { 1, 2, 3 });
        int calls = 0;
        Assert.False(recovery.Pump(128, _ => true, _ => false, _ =>
        {
            calls++;
            recovery.Reset(new[] { 10, 11 });
            return PanelPreviewRecovery.Admission.Complete;
        }));
        Assert.Equal(1, calls);
        var seen = new List<int>();
        Assert.False(recovery.Pump(128, _ => true, _ => false, position => { seen.Add(position); return PanelPreviewRecovery.Admission.Complete; }));
        Assert.Equal(new[] { 10, 11 }, seen);
        recovery.Reset(new[] { 12 });
        recovery.Clear();
        Assert.False(recovery.Pending);
    }

    [Fact]
    public void AnExpensiveRootDoesNotStarveLaterRootsOrStartAnEndlessRetry()
    {
        var recovery = new PanelPreviewRecovery();
        var selected = Enumerable.Range(0, 200).ToHashSet();
        var admitted = new HashSet<int>();
        recovery.Reset(selected.OrderBy(i => i).ToArray());
        int retries = 0;
        PanelPreviewRecovery.Admission Admit(int position)
        {
            if (position == 0) { retries++; return PanelPreviewRecovery.Admission.Retry; }
            admitted.Add(position); return PanelPreviewRecovery.Admission.Complete;
        }
        Assert.True(recovery.Pump(128, selected.Contains, admitted.Contains, Admit));
        Assert.False(recovery.Pump(128, selected.Contains, admitted.Contains, Admit));
        Assert.Equal(199, admitted.Count);
        Assert.Equal(1, retries);
        Assert.True(recovery.Pending);
        selected.Remove(0);
        Assert.True(recovery.Pump(128, selected.Contains, admitted.Contains, Admit));
        Assert.False(recovery.Pump(128, selected.Contains, admitted.Contains, Admit));
        Assert.False(recovery.Pending);
        Assert.True(selected.SetEquals(admitted));
    }
}
