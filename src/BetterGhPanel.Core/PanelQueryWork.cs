using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace BetterGhPanel.Core
{
    /// <summary>A query that can yield between operations and resume on another thread.
    /// Callers must never advance the same work concurrently.</summary>
    public sealed class PanelQueryWork : IDisposable
    {
        private IEnumerator<PanelQueryResult?>? _steps;
        private Func<bool>? _shouldYield;

        internal PanelQueryWork(Func<PanelQueryWork, IEnumerator<PanelQueryResult?>> createSteps) => _steps = createSteps(this);

        public PanelQueryResult? Result { get; private set; }

        /// <summary>Uses a monotonic, cooperative time budget. An individual operation
        /// already in progress finishes before yielding; no partial result is published.</summary>
        public bool TryComplete(TimeSpan budget)
        {
            if (budget < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(budget));
            var clock = Stopwatch.StartNew();
            return TryComplete(() => clock.Elapsed >= budget);
        }

        // A deterministic yield policy lets tests exercise handoff at every phase
        // without depending on machine speed or sleeping to exhaust a wall-clock budget.
        internal bool TryComplete(Func<bool> shouldYield)
        {
            if (Result != null) return true;
            if (_steps == null) throw new ObjectDisposedException(nameof(PanelQueryWork));
            if (shouldYield()) return false;
            _shouldYield = shouldYield;
            try { return Advance(); }
            finally { _shouldYield = null; }
        }

        // Background completion bypasses clocks and iterator handoffs, retaining
        // the throughput of the ordinary scan and merge loops.
        internal bool ShouldYield() => _shouldYield != null && _shouldYield();

        public PanelQueryResult Complete()
        {
            while (Result == null) Advance();
            return Result;
        }

        private bool Advance()
        {
            var steps = _steps ?? throw new ObjectDisposedException(nameof(PanelQueryWork));
            if (!steps.MoveNext()) throw new InvalidOperationException("Query ended without a result.");
            Result = steps.Current;
            if (Result == null) return false;
            Dispose();
            return true;
        }

        public void Dispose()
        {
            _steps?.Dispose();
            _steps = null;
        }
    }
}
