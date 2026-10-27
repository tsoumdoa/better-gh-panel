using System;
using BetterGhPanel.UI;

internal static class PanelActionCommitChecks
{
    internal static void Run()
    {
        var gate = new PanelActionCommit();
        int published = 0, status = 0;
        var pre = gate.Run(() => false, () => published = -1);
        Check(pre == PanelCommitOutcome.Stale && published == 0, "pre-commit cancellation cannot publish");
        var outer = gate.Run(() => true, () =>
        {
            var nested = gate.Run(() => true, () => published = 2, result => status = result == PanelCommitOutcome.Committed ? 2 : -2);
            Check(nested == PanelCommitOutcome.Deferred, "a nested publication returns without blocking");
            published = 1; // the old native call finishes after the callback
        });
        Check(outer == PanelCommitOutcome.Committed && published == 2 && status == 2 && gate.IsIdle,
            "the newer deferred publication and status win after the old call returns");
        published = 0; status = 0;
        var failed = gate.Run(() => true, () =>
        {
            gate.Run(() => true, () => published = 3, result => status = result == PanelCommitOutcome.Committed ? 3 : -3);
            published = 1;
            throw new InvalidOperationException("injected native commit failure");
        });
        Check(failed == PanelCommitOutcome.Uncertain && published == 3 && status == 3,
            "a failed native call is uncertain and never restores over a newer deferred owner");
        bool stillCurrent = true; published = 0; status = 0;
        gate.Run(() => true, () =>
        {
            gate.Run(() => stillCurrent, () => published = 5, result => status = result == PanelCommitOutcome.Stale ? 5 : -5);
            stillCurrent = false; // source, selection, document, target or ticket changed while queued
            published = 1;
        });
        Check(published == 1 && status == 5, "a deferred action revalidates its context when it is dispatched");
        published = 0; status = 0;
        gate.Run(() => true, () =>
        {
            gate.Run(() => true, () => published = 4, result => status = result == PanelCommitOutcome.Stale ? 4 : -4);
            gate.Run(() => false, () => published = -1, result => status += result == PanelCommitOutcome.Stale ? 5 : -5);
        });
        Check(published == 0 && status == 9, "a superseded pending request and a stale successor never publish");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
