namespace QuantConnect.KisBrokerage.Client;

/// <summary>
/// Process-wide KIS per-second call throttle, shared by KR/US order clients,
/// <c>KisBrokerage.ExecuteKisCallAsync</c> (balance/holdings/quote-poll calls issued by
/// KisBrokerage.cs and KisBrokerage.DataQueueHandler.cs), and
/// <see cref="KisUsTradingClient.GetBalanceAsync"/>'s internal per-exchange loop. All call sites
/// need the *same* gate/last-call-timestamp because
/// KIS applies its "초당 거래건수를 초과하였습니다" limit to the app key, not to any particular
/// C# call site: a naive per-call-site throttle would let KisBrokerage's outer wrap and this
/// class's inner loop each think they're the only caller and interleave two requests within the
/// same second.
/// </summary>
internal interface IKisCallGate
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct = default);
}

/// <summary>
/// Serializes calls and spaces their completion times. Production uses one process-wide instance;
/// the instance type exists so focused tests can use a short interval without waiting three seconds.
/// </summary>
internal sealed class KisCallGate : IKisCallGate
{
    private readonly TimeSpan _minCallGap;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastCallCompletedUtc = DateTime.MinValue;

    public KisCallGate(TimeSpan minCallGap)
    {
        _minCallGap = minCallGap;
    }

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var elapsed = DateTime.UtcNow - _lastCallCompletedUtc;
            var wait = elapsed < _minCallGap ? _minCallGap - elapsed : TimeSpan.Zero;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            return await call(ct).ConfigureAwait(false);
        }
        finally
        {
            _lastCallCompletedUtc = DateTime.UtcNow;
            _gate.Release();
        }
    }
}

internal static class KisRateLimiter
{
    // See KisBrokerage.cs's file-level NOTE next to the (now-delegating) MinKisCallGap constant
    // for how this gap was tuned against the real KIS paper gateway.
    internal static IKisCallGate Shared { get; } = new KisCallGate(TimeSpan.FromSeconds(3));

    public static Task<T> ThrottledAsync<T>(Func<Task<T>> call, CancellationToken ct = default) =>
        Shared.ExecuteAsync(_ => call(), ct);
}
