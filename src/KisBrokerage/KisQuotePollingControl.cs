using System.Collections.Concurrent;

namespace QuantConnect.KisBrokerage;

/// <summary>
/// Process-local coordination between REST quote polling and higher-priority broker calls.
/// A pause cancels the current quote generation so requests already waiting behind the shared
/// KIS rate-limit gate do not starve account reconciliation or order HTTP. Scoped leases are
/// reference counted; a permanent startup/shutdown pause remains in force after a lease ends.
/// </summary>
public static class KisQuotePollingControl
{
    private sealed class ScopeState
    {
        public object Sync { get; } = new();
        public bool ManuallyPaused { get; set; }
        public int LeaseCount { get; set; }
        public CancellationTokenSource Generation { get; set; } = new();
    }

    private sealed class PauseLease(KisMarketScope scope) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Release(scope);
        }
    }

    private static readonly ConcurrentDictionary<KisMarketScope, ScopeState> States = new();

    public static bool IsEnabled(KisMarketScope scope)
    {
        var state = GetState(scope);
        lock (state.Sync)
            return !state.ManuallyPaused && state.LeaseCount == 0;
    }

    /// <summary>
    /// Token for the current polling generation. A startup/shutdown pause or the first active
    /// critical lease cancels it, including quote calls already queued on the KIS gate.
    /// </summary>
    public static CancellationToken CurrentToken(KisMarketScope scope)
    {
        var state = GetState(scope);
        lock (state.Sync)
            return state.Generation.Token;
    }

    public static void Pause(KisMarketScope scope)
    {
        var state = GetState(scope);
        lock (state.Sync)
        {
            if (state.ManuallyPaused) return;
            state.ManuallyPaused = true;
            CancelGeneration(state);
        }
    }

    public static void Resume(KisMarketScope scope)
    {
        var state = GetState(scope);
        lock (state.Sync)
        {
            state.ManuallyPaused = false;
            StartGenerationIfEnabled(state);
        }
    }

    /// <summary>
    /// Temporarily gives reconciliation/order HTTP priority over quote polling. Disposing the
    /// lease resumes quotes unless another lease or an explicit startup/shutdown pause remains.
    /// </summary>
    public static IDisposable AcquirePause(KisMarketScope scope)
    {
        var state = GetState(scope);
        lock (state.Sync)
        {
            state.LeaseCount++;
            if (state.LeaseCount == 1)
                CancelGeneration(state);
        }
        return new PauseLease(scope);
    }

    internal static void Reset(KisMarketScope scope)
    {
        ValidateScope(scope);
        if (!States.TryRemove(scope, out var state)) return;
        lock (state.Sync)
        {
            state.ManuallyPaused = true;
            state.LeaseCount = 0;
            CancelGeneration(state);
        }
    }

    internal static void ResetForTests()
    {
        foreach (var scope in States.Keys)
            Reset(scope);
        States.Clear();
    }

    private static void Release(KisMarketScope scope)
    {
        var state = GetState(scope);
        lock (state.Sync)
        {
            if (state.LeaseCount == 0) return;
            state.LeaseCount--;
            StartGenerationIfEnabled(state);
        }
    }

    private static ScopeState GetState(KisMarketScope scope)
    {
        ValidateScope(scope);
        return States.GetOrAdd(scope, static _ => new ScopeState());
    }

    private static void ValidateScope(KisMarketScope scope)
    {
        if (scope is not (KisMarketScope.Kr or KisMarketScope.Us))
            throw new ArgumentOutOfRangeException(nameof(scope), "Quote polling requires an exact KR or US scope.");
    }

    private static void CancelGeneration(ScopeState state)
    {
        if (!state.Generation.IsCancellationRequested)
            state.Generation.Cancel();
    }

    private static void StartGenerationIfEnabled(ScopeState state)
    {
        if (state.ManuallyPaused || state.LeaseCount != 0 || !state.Generation.IsCancellationRequested) return;
        state.Generation = new CancellationTokenSource();
    }
}
