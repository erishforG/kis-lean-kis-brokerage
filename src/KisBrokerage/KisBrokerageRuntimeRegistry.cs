namespace QuantConnect.KisBrokerage;

public sealed record KisScopedHolding(string Market, string Symbol, decimal Quantity);
public sealed record KisScopedOpenOrder(string Market, string Symbol, string BrokerOrderId);
public sealed record KisScopedCash(string Currency, decimal Amount);
public sealed record KisBrokerSnapshot(
    KisMarketScope Scope,
    IReadOnlyList<KisScopedHolding> Holdings,
    IReadOnlyList<KisScopedOpenOrder> OpenOrders,
    IReadOnlyList<KisScopedCash> Cash,
    decimal? TotalValue = null,
    decimal? CumulativePnl = null);

public interface IKisBrokerSnapshotProvider
{
    KisMarketScope Scope { get; }
    KisBrokerSnapshot ReadBrokerSnapshot();
}

/// <summary>
/// Same-process bridge from the algorithm to the trading brokerage instance created by LEAN.
/// It prevents a separate audit client/process from issuing another OAuth token.
/// </summary>
public static class KisBrokerageRuntimeRegistry
{
    private static readonly object Sync = new();
    private static IKisBrokerSnapshotProvider? _tradingBrokerage;

    public static void RegisterTradingBrokerage(IKisBrokerSnapshotProvider brokerage)
    {
        ArgumentNullException.ThrowIfNull(brokerage);
        lock (Sync) _tradingBrokerage = brokerage;
    }

    public static IKisBrokerSnapshotProvider? Resolve(KisMarketScope scope)
    {
        lock (Sync) return _tradingBrokerage?.Scope == scope ? _tradingBrokerage : null;
    }

    public static void UnregisterTradingBrokerage(IKisBrokerSnapshotProvider brokerage)
    {
        ArgumentNullException.ThrowIfNull(brokerage);
        lock (Sync)
        {
            if (ReferenceEquals(_tradingBrokerage, brokerage)) _tradingBrokerage = null;
        }
    }

    internal static void ClearForTests()
    {
        lock (Sync) _tradingBrokerage = null;
    }
}
