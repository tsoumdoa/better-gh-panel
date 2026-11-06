using Xunit;
using System;
using System.Linq;
using BetterGhPanel.Core;

public class PanelActionCommitTests
{
    [Fact]
    public void DeferredCommitsRevalidateOwnershipAndSelectionRevision()
    {
        var gate = new PanelActionCommit();
        int published = 0, status = 0;
        var pre = gate.Run(() => false, () => published = -1);
        Assert.True(pre == PanelCommitOutcome.Stale && published == 0, "pre-commit cancellation cannot publish");
        var outer = gate.Run(() => true, () =>
        {
            var nested = gate.Run(() => true, () => published = 2, result => status = result == PanelCommitOutcome.Committed ? 2 : -2);
            Assert.True(nested == PanelCommitOutcome.Deferred, "a nested publication returns without blocking");
            published = 1; // the old native call finishes after the callback
        });
        Assert.True(outer == PanelCommitOutcome.Committed && published == 2 && status == 2 && gate.IsIdle,
            "the newer deferred publication and status win after the old call returns");
        published = 0; status = 0;
        var failed = gate.Run(() => true, () =>
        {
            gate.Run(() => true, () => published = 3, result => status = result == PanelCommitOutcome.Committed ? 3 : -3);
            published = 1;
            throw new InvalidOperationException("injected native commit failure");
        });
        Assert.True(failed == PanelCommitOutcome.Uncertain && published == 3 && status == 3,
            "a failed native call is uncertain and never restores over a newer deferred owner");
        bool stillCurrent = true; published = 0; status = 0;
        gate.Run(() => true, () =>
        {
            gate.Run(() => stillCurrent, () => published = 5, result => status = result == PanelCommitOutcome.Stale ? 5 : -5);
            stillCurrent = false; // source, selection, document, target or ticket changed while queued
            published = 1;
        });
        Assert.True(published == 1 && status == 5, "a deferred action revalidates its context when it is dispatched");
        published = 0; status = 0;
        gate.Run(() => true, () =>
        {
            gate.Run(() => true, () => published = 4, result => status = result == PanelCommitOutcome.Stale ? 4 : -4);
            gate.Run(() => false, () => published = -1, result => status += result == PanelCommitOutcome.Stale ? 5 : -5);
        });
        Assert.True(published == 0 && status == 9, "a superseded pending request and a stale successor never publish");

        var snapshot = new PanelSnapshot(Enumerable.Range(0, 3)
            .Select(i => new PanelRow(i, "{0}", "Number", i.ToString(), i)).ToArray(),
            new[] { new PanelBranch("{0}", 3) });
        var result = PanelQueryEngine.Execute(snapshot, new PanelQueryOptions());
        var selection = new PanelRowSelection();
        selection.SelectAt(result, 0, 0);
        var positions = selection.OrderedPositions(result);
        long version = selection.Version;
        bool SelectionCurrent() => PanelActionPositions.IsCurrent(result, 3, selection, positions, version);
        Assert.True(SelectionCurrent(), "an unchanged selection remains eligible for a value-copy commit");
        published = 0;
        var changedInsideSourceCheck = gate.Run(() => SelectionCurrent() && ChangeSelection() && SelectionCurrent(), () => published++);
        Assert.True(changedInsideSourceCheck == PanelCommitOutcome.Stale && published == 0 &&
            PanelActionPositions.TryGet(result, 3, selection, positions, out _),
            "a source callback changing selection rejects old positions even when those positions still exist");
        bool ChangeSelection() { selection.SelectAt(result, 1, 1); return true; }
        selection.SelectAt(result, 0, 0);
        Assert.True(!SelectionCurrent(), "changing selection away and back still invalidates an in-flight copy");
        positions = selection.OrderedPositions(result); version = selection.Version;
        status = 0; published = 0;
        gate.Run(() => true, () =>
        {
            gate.Run(SelectionCurrent, () => published = -1, outcome => status = outcome == PanelCommitOutcome.Stale ? 1 : -1);
            selection.SelectAt(result, 1, 1);
            published = 2;
        });
        Assert.True(published == 2 && status == 1, "a deferred value-copy commit rechecks its selection revision");
    }

}
