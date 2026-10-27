using System;

namespace BetterGhPanel.UI
{
    internal enum PanelCommitOutcome { Committed, Deferred, Stale, Uncertain }

    // UI-thread-only serialization of a single native side effect. A nested
    // request is retained rather than blocking its reentrant caller. Only the
    // latest pending request survives, and it is revalidated before publication.
    internal sealed class PanelActionCommit
    {
        private bool _active;
        private Request? _pending;
        internal bool IsIdle => !_active && _pending == null;

        internal PanelCommitOutcome Run(Func<bool> current, Action publish, Action<PanelCommitOutcome>? completed = null)
        {
            var request = new Request(current, publish, completed);
            if (_active)
            {
                var previous = _pending;
                _pending = request;
                if (previous != null) Complete(previous, PanelCommitOutcome.Stale);
                return PanelCommitOutcome.Deferred;
            }
            _active = true;
            try
            {
                var result = Execute(request);
                while (_pending != null)
                {
                    var next = _pending; _pending = null;
                    Complete(next, Execute(next));
                }
                return result;
            }
            finally { _active = false; }
        }

        private static PanelCommitOutcome Execute(Request request)
        {
            try { if (!request.Current()) return PanelCommitOutcome.Stale; }
            catch (Exception) { return PanelCommitOutcome.Stale; }
            try { request.Publish(); return PanelCommitOutcome.Committed; }
            catch (Exception) { return PanelCommitOutcome.Uncertain; }
        }

        private static void Complete(Request request, PanelCommitOutcome result)
        { try { request.Completed?.Invoke(result); } catch (Exception) { } }

        private sealed class Request
        {
            internal Request(Func<bool> current, Action publish, Action<PanelCommitOutcome>? completed)
            { Current = current; Publish = publish; Completed = completed; }
            internal Func<bool> Current { get; }
            internal Action Publish { get; }
            internal Action<PanelCommitOutcome>? Completed { get; }
        }
    }
}
