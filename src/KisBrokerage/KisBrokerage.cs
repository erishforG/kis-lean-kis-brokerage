using System.Collections.Concurrent;
using QuantConnect.Brokerages;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.KisBrokerage.Client;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.KisBrokerage;

/// <summary>
/// LEAN <see cref="Brokerage"/> implementation for Korea Investment &amp; Securities (KIS).
///
/// This is a port of the KRX/USA routing logic proven out in the poc/ console PoC
/// (feat/kr-oauth-client, commits 995037e..b1bb5eb) into the shape LEAN's Launcher actually
/// loads plugins in — see <see cref="KisBrokerageFactory"/> and config.json.
///
/// Both <see cref="Market.KRX"/> and <see cref="Market.USA"/> orders are wired to real KIS
/// clients now (<see cref="KisKrTradingClient"/> / <see cref="KisUsTradingClient"/>,
/// paper-account "모의투자" only). Both share the same <see cref="KisKrOAuthClient"/>/
/// <see cref="KisKrOAuthConfig"/> instance — KIS scopes OAuth tokens/app keys to an account,
/// not a market, so there is no separate "US OAuth client" (see KisUsTradingClient.cs's
/// file-level NOTE for the reference-code evidence).
///
/// This class is <c>partial</c> — the <see cref="IDataQueueHandler"/> implementation (a
/// REST-polling "self-hosted data queue" so LEAN's config.json can point
/// <c>data-queue-handler</c> at this same type, the standard "brokerage doubles as its own data
/// source" pattern) lives in KisBrokerage.DataQueueHandler.cs. See that file's doc comment for
/// what is/isn't in scope (no websocket/real-time streaming this round).
///
/// IMPORTANT — two separate <see cref="KisBrokerage"/> instances exist at runtime when
/// config.json's <c>data-queue-handler</c> points at this same type name:
/// <see cref="KisBrokerageFactory.CreateBrokerage"/> constructs the one used for orders/
/// balance/holdings (the "trading" instance held by LEAN's transaction handler); separately,
/// <c>QuantConnect.Util.Composer</c> resolves the data-queue-handler entry by type name via
/// reflection (<c>Composer.GetExportedValueByTypeName&lt;IDataQueueHandler&gt;(...)</c>) and,
/// finding no already-registered <see cref="IDataQueueHandler"/> instance under that name,
/// falls back to <c>Activator.CreateInstance</c> — which requires the parameterless
/// constructor below. That second instance is used *only* for quote polling
/// (Subscribe/Unsubscribe/SetJob/IsConnected/Dispose) — LEAN never routes order calls through
/// it, so this does not create any order-placement risk; it does mean a second OAuth token gets
/// issued against the same paper account.
/// </summary>
public sealed partial class KisBrokerage : Brokerage, IDataQueueHandler, IKisBrokerSnapshotProvider
{
    private static readonly TimeSpan HistoryRequestTimeout = TimeSpan.FromSeconds(
        Math.Clamp(Config.GetInt("kis-history-request-timeout-seconds", 90), 5, 120));
    private static readonly TimeSpan SnapshotRequestTimeout = TimeSpan.FromSeconds(
        Math.Clamp(Config.GetInt("kis-snapshot-request-timeout-seconds", 30), 1, 120));
    private readonly KisKrOAuthClient _oauthClient;
    private readonly KisKrTradingClient _tradingClient;
    private readonly KisUsTradingClient _usTradingClient;
    private readonly KisMarketScope _marketScope;
    private readonly IKisCallGate _callGate;
    private readonly TimeSpan _snapshotRequestTimeout;
    private bool _connected;
    private Timer? _fillPollTimer;
    private readonly SemaphoreSlim _fillSyncGate = new(1, 1);
    private bool _fillPollingEnabled;
    private TimeSpan _fillPollInterval = TimeSpan.FromSeconds(5);
    private int _fillPollInFlight;
    private int _disposing;

    // Order-id cross-reference table: KIS order number -> enough context to cancel/modify it
    // later (KIS's cancel/modify endpoints need the original ticker/quantity/price plus, for KR,
    // the KRX_FWDG_ORD_ORGNO returned alongside the order number — see KisKrTradingClient's
    // CancelOrderAsync doc comment). Populated by PlaceOrder/PlaceUsOrder and refreshed by
    // GetOpenOrders (so an order this process didn't itself place — e.g. after a restart — can
    // still be canceled/modified once discovered via the unfilled-orders query).
    private sealed record KisOrderTracking(
        string Market,
        string Ticker,
        string? OrgNo,
        string? Exchange,
        decimal Quantity,
        decimal LimitPrice,
        Order LeanOrder,
        decimal BrokerReportedFilled = 0m,
        decimal BrokerReportedNotional = 0m,
        bool BrokerReportedNotionalKnown = true,
        decimal LeanReportedFilled = 0m);

    private readonly ConcurrentDictionary<string, KisOrderTracking> _orderTracking = new();

    // Discovered against the real KIS paper API while live-testing this brokerage in a real LEAN
    // Launcher run: BrokerageSetupHandler.Setup() calls GetCashBalance() then GetOpenOrders()
    // back-to-back at startup, each of which itself makes two sequential KR+US calls — four KIS
    // HTTP calls within well under a second, deterministically tripping KIS's per-second rate
    // limit ("초당 거래건수를 초과하였습니다") on the 2nd-4th call every time. Neither
    // KisKrTradingClient.GetBalanceAsync/GetUnfilledOrdersAsync nor their US counterparts have
    // rate-limit backoff (unlike PlaceLimitOrderAsync, which already retries on this same error).
    // This delegates to <see cref="KisRateLimiter"/> — a process-wide spacer shared with the
    // KR/US order clients and
    // <see cref="Client.KisUsTradingClient.GetBalanceAsync"/>'s internal per-exchange call loop,
    // which needs the exact same gate for the reason documented on that type: KIS's limit is
    // scoped to the app key, not to any one call site, so every KIS HTTP call this process makes
    // — orders, balance/holdings, quote polling (KisBrokerage.DataQueueHandler.cs) — must share
    // one clock, or two call sites can each think they're alone and interleave within a second.
    private Task<T> ExecuteKisCallAsync<T>(Func<Task<T>> call, CancellationToken ct = default) =>
        _callGate.ExecuteAsync(_ => call(), ct);

    /// <summary>
    /// Parameterless constructor — required so <c>Composer.GetExportedValueByTypeName&lt;
    /// IDataQueueHandler&gt;("QuantConnect.KisBrokerage.KisBrokerage")</c> can construct this
    /// class via reflection when config.json's <c>data-queue-handler</c> points at this same
    /// type name (see the class doc comment's "IMPORTANT" note). Reads the same
    /// <c>kis-paper-*</c> LEAN config keys as <see cref="KisBrokerageFactory.CreateBrokerage"/>
    /// — paper account only, same as everywhere else in this repo.
    /// </summary>
    public KisBrokerage()
        : this(
            new KisKrOAuthConfig(
                BaseUrl: Config.Get("kis-paper-base-url", "https://openapivts.koreainvestment.com:29443"),
                AppKey: Config.Get("kis-paper-app-key", ""),
                AppSecret: Config.Get("kis-paper-app-secret", ""),
                AccountNo: Config.Get("kis-paper-acct-no", ""),
                ProductCode: Config.Get("kis-paper-prod-code", "")),
            isPaper: true,
            marketScope: KisMarketScopes.Parse(Config.Get("kis-market-scope", "Both")))
    {
    }

    public KisBrokerage(
        KisKrOAuthConfig config,
        bool isPaper = true,
        KisMarketScope marketScope = KisMarketScope.Both)
        : base("Korea Investment & Securities")
    {
        // Must happen before Connect()/GetAccountHoldings() can ever run — see
        // KisKrxMarketData's doc comment for the exact ArgumentException/NullReferenceException
        // this prevents when LEAN auto-adds an existing KR holding.
        KisKrxMarketData.EnsureRegistered();

        _oauthClient = new KisKrOAuthClient(config, isPaper: isPaper);
        _tradingClient = new KisKrTradingClient(config, _oauthClient, isPaper: isPaper);
        _usTradingClient = new KisUsTradingClient(config, _oauthClient, isPaper: isPaper);
        _marketScope = marketScope;
        _callGate = KisRateLimiter.Shared;
        _snapshotRequestTimeout = SnapshotRequestTimeout;
    }

    /// <summary>Test/DI seam — lets a caller (or future test project) inject clients directly.</summary>
    public KisBrokerage(
        KisKrOAuthClient oauthClient,
        KisKrTradingClient tradingClient,
        KisUsTradingClient usTradingClient,
        KisMarketScope marketScope = KisMarketScope.Both)
        : this(oauthClient, tradingClient, usTradingClient, ensureKrxMarketData: true, marketScope)
    {
    }

    internal KisBrokerage(
        KisKrOAuthClient oauthClient,
        KisKrTradingClient tradingClient,
        KisUsTradingClient usTradingClient,
        bool ensureKrxMarketData,
        KisMarketScope marketScope = KisMarketScope.Both,
        IKisCallGate? callGate = null,
        TimeSpan? snapshotRequestTimeout = null)
        : base("Korea Investment & Securities")
    {
        if (ensureKrxMarketData)
        {
            KisKrxMarketData.EnsureRegistered();
        }

        _oauthClient = oauthClient;
        _tradingClient = tradingClient;
        _usTradingClient = usTradingClient;
        _marketScope = marketScope;
        _callGate = callGate ?? KisRateLimiter.Shared;
        _snapshotRequestTimeout = snapshotRequestTimeout ?? SnapshotRequestTimeout;
        if (_snapshotRequestTimeout <= TimeSpan.Zero || _snapshotRequestTimeout > TimeSpan.FromSeconds(120))
            throw new ArgumentOutOfRangeException(
                nameof(snapshotRequestTimeout), "Snapshot timeout must be greater than zero and at most 120 seconds.");
    }

    public override bool IsConnected => _connected;
    public KisMarketScope Scope => _marketScope;

    /// <summary>
    /// Enables broker fill synchronization for the trading instance. The reflected data-queue
    /// instance deliberately never enables it, preventing duplicate fill polling/events.
    /// </summary>
    internal void EnableFillPolling(TimeSpan? interval = null)
    {
        _fillPollingEnabled = true;
        _fillPollInterval = interval ?? TimeSpan.FromSeconds(5);
        if (_fillPollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
    }

    internal bool IsFillPollTimerActive => _fillPollTimer is not null;

    public KisBrokerSnapshot ReadBrokerSnapshot()
    {
        using var krPause = _marketScope is KisMarketScope.Kr or KisMarketScope.Both
            ? KisQuotePollingControl.AcquirePause(KisMarketScope.Kr)
            : null;
        using var usPause = _marketScope is KisMarketScope.Us or KisMarketScope.Both
            ? KisQuotePollingControl.AcquirePause(KisMarketScope.Us)
            : null;
        using var timeout = new CancellationTokenSource(_snapshotRequestTimeout);
        try
        {
            var holdings = new List<KisScopedHolding>();
            var cash = new List<KisScopedCash>();
            decimal? totalValue = null;
            decimal? cumulativePnl = null;

            // A reconciliation snapshot obtains each market's balance exactly once and derives
            // both holdings and cash from that response. Calling GetAccountHoldings followed by
            // GetCashBalance used to repeat all four US balance TRs and starve the LEAN callback.
            if (_marketScope is KisMarketScope.Kr or KisMarketScope.Both)
            {
                var balance = ExecuteKisCallAsync(
                    () => _tradingClient.GetBalanceAsync(timeout.Token), timeout.Token).GetAwaiter().GetResult();
                if (!balance.Ok)
                    throw new InvalidOperationException(balance.ErrorMessage ?? "KIS KR balance snapshot failed.");
                holdings.AddRange(balance.Holdings.Select(x => new KisScopedHolding("KR", x.Ticker, x.Quantity)));
                cash.Add(new KisScopedCash("KRW", balance.Cash));
                totalValue = balance.TotalEval;
            }

            if (_marketScope is KisMarketScope.Us or KisMarketScope.Both)
            {
                var balance = _usTradingClient.GetBalanceAsync(timeout.Token).GetAwaiter().GetResult();
                if (!balance.Ok)
                    throw new InvalidOperationException(balance.ErrorMessage ?? "KIS US balance snapshot failed.");
                holdings.AddRange(balance.Holdings.Select(x => new KisScopedHolding("US", x.Symbol, x.Quantity)));
                cash.Add(new KisScopedCash("USD", balance.UsdCash));
                totalValue = balance.UsdEval;
                cumulativePnl = balance.CumulativePnlUsd;
            }

            var orders = GetOpenOrders(timeout.Token).Select(x => new KisScopedOpenOrder(
                x.Symbol.ID.Market == Market.KRX ? "KR" : x.Symbol.ID.Market == Market.USA ? "US" : "OUTSIDE",
                x.Symbol.Value,
                x.BrokerId.LastOrDefault() ?? $"missing:{x.Id}")).ToArray();
            return new KisBrokerSnapshot(_marketScope, holdings, orders, cash, totalValue, cumulativePnl);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"KIS {_marketScope} broker snapshot exceeded {_snapshotRequestTimeout.TotalSeconds:0}s.", exception);
        }
    }

    public override void Connect()
    {
        // TODO: Brokerage.Connect() is synchronous/void, so this cannot simply await
        // KisKrOAuthClient.IssueTokenAsync() the way a from-scratch design might. A real
        // implementation should pre-warm the OAuth token here (blocking on the async call, or
        // restructuring KisKrOAuthClient to expose a sync-friendly warmup) and only flip
        // _connected once that succeeds, surfacing failures via OnMessage(BrokerageMessageType.Error).
        // For this round, Connect() is inert/optimistic and the first real API call
        // (PlaceOrder/GetCashBalance/etc.) is what actually proves connectivity.
        _connected = true;
        if (_fillPollingEnabled)
        {
            _fillPollTimer ??= new Timer(
                _ => _ = PollFillsOnceAsync(), null, _fillPollInterval, _fillPollInterval);
        }
    }

    public override void Disconnect()
    {
        _fillPollTimer?.Dispose();
        _fillPollTimer = null;
        _connected = false;
    }

    public override List<Order> GetOpenOrders() => GetOpenOrders(CancellationToken.None);

    private List<Order> GetOpenOrders(CancellationToken cancellationToken)
    {
        var orders = new List<Order>();
        var importedKr = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var importedUs = new Dictionary<string, decimal>(StringComparer.Ordinal);

        if (_marketScope is KisMarketScope.Kr or KisMarketScope.Both)
        {
            var krUnfilled = ExecuteKisCallAsync(
                () => _tradingClient.GetUnfilledOrdersAsync(cancellationToken), cancellationToken)
                .GetAwaiter().GetResult();
            foreach (var o in krUnfilled)
            {
                if (string.IsNullOrWhiteSpace(o.OrderNo))
                {
                    continue;
                }

                var signedQty = o.Side == "sell" ? -o.RemainingQty : o.RemainingQty;
                var symbol = CreateUnmappedEquitySymbol(o.Ticker, Market.KRX);
                var leanOrder = new LimitOrder(symbol, signedQty, o.OrderPrice, DateTime.UtcNow)
                {
                    Status = OrderStatus.Submitted,
                };
                leanOrder.BrokerId.Add(o.OrderNo);
                if (_orderTracking.TryAdd(o.OrderNo, new KisOrderTracking(
                    Market.KRX, o.Ticker, o.OrgNo, Exchange: null, o.OrderQty, o.OrderPrice, leanOrder,
                    BrokerReportedFilled: o.FilledQty,
                    BrokerReportedNotional: 0m,
                    BrokerReportedNotionalKnown: o.FilledQty == 0m)))
                    importedKr[o.OrderNo] = o.FilledQty;
                orders.Add(leanOrder);
            }

            if (_fillPollingEnabled && importedKr.Count != 0)
            {
                var rows = ExecuteKisCallAsync(
                        () => _tradingClient.GetDailyFilledOrdersAsync(ct: cancellationToken), cancellationToken)
                    .GetAwaiter().GetResult();
                foreach (var row in rows.Where(row => importedKr.ContainsKey(row.OrderNo)))
                    SeedImportedFillBaseline(row.OrderNo, importedKr[row.OrderNo], row.FilledQty,
                        row.AverageFilledPrice);
            }
        }

        // US GetUnfilledOrdersAsync makes a single HTTP call (unlike GetBalanceAsync's four), so
        // this is safe to wrap in the outer throttle acquisition like the KR call above — see
        // KisUsTradingClient's DefaultExchange doc comment: "NASD" is a generic order-routing
        // code that covers orders regardless of the security's actual listing exchange (unlike
        // quote polling's DefaultPriceExchange, which needs the real listing exchange), so this
        // single default-exchange query is expected to surface every open US order this
        // brokerage itself could have placed.
        if (_marketScope is KisMarketScope.Us or KisMarketScope.Both)
        {
            var usUnfilled = ExecuteKisCallAsync(
                () => _usTradingClient.GetUnfilledOrdersAsync(ct: cancellationToken), cancellationToken)
                .GetAwaiter().GetResult();
            foreach (var o in usUnfilled)
            {
                if (string.IsNullOrWhiteSpace(o.OrderNo))
                {
                    continue;
                }

                var remainingQty = o.OrderQty - o.FilledQty;
                var signedQty = o.Side == "sell" ? -remainingQty : remainingQty;
                var symbol = CreateUnmappedEquitySymbol(o.Ticker, Market.USA);
                var leanOrder = new LimitOrder(symbol, signedQty, o.OrderPrice, DateTime.UtcNow)
                {
                    Status = OrderStatus.Submitted,
                };
                leanOrder.BrokerId.Add(o.OrderNo);
                if (_orderTracking.TryAdd(o.OrderNo, new KisOrderTracking(
                    Market.USA, o.Ticker, OrgNo: null, o.Exchange ?? KisUsTradingClient.DefaultExchange,
                    o.OrderQty, o.OrderPrice, leanOrder,
                    BrokerReportedFilled: o.FilledQty,
                    BrokerReportedNotional: 0m,
                    BrokerReportedNotionalKnown: o.FilledQty == 0m)))
                    importedUs[o.OrderNo] = o.FilledQty;
                orders.Add(leanOrder);
            }

            if (_fillPollingEnabled && importedUs.Count != 0)
            {
                var rows = ExecuteKisCallAsync(
                        () => _usTradingClient.GetFilledOrdersAsync(ct: cancellationToken), cancellationToken)
                    .GetAwaiter().GetResult();
                foreach (var row in rows)
                {
                    var id = FindMatchingOrderNumber(importedUs.Keys, row.OrderNo, row.OriginalOrderNo);
                    if (id is not null && importedUs.TryGetValue(id, out var baseline))
                        SeedImportedFillBaseline(id, baseline, row.FilledQty, row.AverageFilledPrice);
                }
            }
        }

        return orders;
    }

    public override List<Holding> GetAccountHoldings()
    {
        var holdings = new List<Holding>();

        if (_marketScope is KisMarketScope.Kr or KisMarketScope.Both)
        {
            var krBalance = ExecuteKisCallAsync(() => _tradingClient.GetBalanceAsync()).GetAwaiter().GetResult();
            if (!krBalance.Ok)
            {
                OnMessage(new BrokerageMessageEvent(
                    BrokerageMessageType.Warning, "KIS_BALANCE_ERROR", krBalance.ErrorMessage ?? "Unknown KIS error"));
            }
            else
            {
                holdings.AddRange(krBalance.Holdings.Select(h => new Holding
                {
                    Symbol = Symbol.Create(h.Ticker, SecurityType.Equity, Market.KRX),
                    AveragePrice = h.AveragePrice,
                    Quantity = h.Quantity,
                    MarketPrice = h.CurrentPrice,
                    CurrencySymbol = "₩",
                }));
            }
        }

        // Not wrapped in ExecuteKisCallAsync: unlike the KR balance call (one HTTP request),
        // KisUsTradingClient.GetBalanceAsync makes four internal calls, each already throttled
        // individually via the same shared KisRateLimiter gate (see that method). Wrapping the
        // whole call here too would try to re-acquire the same non-reentrant SemaphoreSlim(1,1)
        // from inside itself and deadlock.
        if (_marketScope is KisMarketScope.Us or KisMarketScope.Both)
        {
            var usBalance = GetUsBalanceWithRetry();
            if (!usBalance.Ok)
            {
                OnMessage(new BrokerageMessageEvent(
                    BrokerageMessageType.Warning, "KIS_US_BALANCE_ERROR", usBalance.ErrorMessage ?? "Unknown KIS error"));
            }
            else
            {
                holdings.AddRange(usBalance.Holdings.Select(h => new Holding
                {
                    Symbol = Symbol.Create(h.Symbol, SecurityType.Equity, Market.USA),
                    AveragePrice = h.AveragePrice,
                    Quantity = h.Quantity,
                    MarketPrice = h.CurrentPrice,
                    CurrencySymbol = "$",
                }));
            }
        }

        return holdings;
    }

    public override List<CashAmount> GetCashBalance()
    {
        var cash = new List<CashAmount>();

        if (_marketScope is KisMarketScope.Kr or KisMarketScope.Both)
        {
            var krBalance = ExecuteKisCallAsync(() => _tradingClient.GetBalanceAsync()).GetAwaiter().GetResult();
            if (!krBalance.Ok)
            {
                OnMessage(new BrokerageMessageEvent(
                    BrokerageMessageType.Warning, "KIS_BALANCE_ERROR", krBalance.ErrorMessage ?? "Unknown KIS error"));
            }
            else
            {
                cash.Add(new CashAmount(krBalance.Cash, "KRW"));
            }
        }

        // Not wrapped in ExecuteKisCallAsync: unlike the KR balance call (one HTTP request),
        // KisUsTradingClient.GetBalanceAsync makes four internal calls, each already throttled
        // individually via the same shared KisRateLimiter gate (see that method). Wrapping the
        // whole call here too would try to re-acquire the same non-reentrant SemaphoreSlim(1,1)
        // from inside itself and deadlock.
        if (_marketScope is KisMarketScope.Us or KisMarketScope.Both)
        {
            var usBalance = GetUsBalanceWithRetry();
            if (!usBalance.Ok)
            {
                OnMessage(new BrokerageMessageEvent(
                    BrokerageMessageType.Warning, "KIS_US_BALANCE_ERROR", usBalance.ErrorMessage ?? "Unknown KIS error"));
            }
            else
            {
                cash.Add(new CashAmount(usBalance.UsdCash, "USD"));
            }
        }

        return cash;
    }

    private KisUsBalanceResult GetUsBalanceWithRetry()
    {
        KisUsBalanceResult? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            last = _usTradingClient.GetBalanceAsync().GetAwaiter().GetResult();
            if (last.Ok) return last;
            if (attempt < 2) Thread.Sleep(TimeSpan.FromSeconds(1));
        }
        return last!;
    }

    public override bool PlaceOrder(Order order)
    {
        var market = order.Symbol.ID.Market;
        if (!_marketScope.Allows(market))
        {
            throw new InvalidOperationException(
                $"KisBrokerage market scope {_marketScope} rejects order market {market}.");
        }
        using var quotePause = KisQuotePollingControl.AcquirePause(ExactScope(market));

        if (market == Market.USA)
        {
            return PlaceUsOrder(order);
        }

        if (market != Market.KRX)
        {
            throw new NotSupportedException($"KisBrokerage: unsupported LEAN market '{market}'.");
        }

        if (order.Symbol.SecurityType != SecurityType.Equity)
        {
            throw new NotSupportedException(
                $"KisBrokerage: only equities are supported, got {order.Symbol.SecurityType}.");
        }

        // KisKrTradingClient.PlaceLimitOrderAsync only supports limit orders (matching the KIS
        // Node reference implementation's scope this round) — see its doc comment.
        if (order is not LimitOrder limitOrder)
        {
            throw new NotSupportedException(
                $"KisBrokerage: KIS KR trading client currently supports limit orders only " +
                $"(see KisKrTradingClient.PlaceLimitOrderAsync); got order type '{order.Type}'.");
        }

        var side = order.Quantity > 0 ? "buy" : "sell";
        var quantity = (int)Math.Abs(order.Quantity);

        var result = _tradingClient
            .PlaceLimitOrderAsync(side, order.Symbol.Value, quantity, limitOrder.LimitPrice)
            .GetAwaiter().GetResult();

        if (!result.Ok)
        {
            OnMessage(new BrokerageMessageEvent(
                BrokerageMessageType.Warning,
                result.MessageCode ?? result.ReturnCode ?? "KIS_ORDER_ERROR",
                result.ErrorMessage ?? "Unknown KIS error"));
            return false;
        }

        if (string.IsNullOrWhiteSpace(result.OrderNo) || string.IsNullOrWhiteSpace(result.OrgNo))
        {
            OnMessage(new BrokerageMessageEvent(
                BrokerageMessageType.Error, "KIS_ORDER_ID_MISSING",
                "KIS accepted the KR order but no usable broker order id/org number pair could be recovered."));
            return false;
        }

        order.BrokerId.Add(result.OrderNo);
        _orderTracking[result.OrderNo] = new KisOrderTracking(
            Market.KRX, order.Symbol.Value, result.OrgNo, Exchange: null, quantity, limitOrder.LimitPrice, order);

        // BrokerageTransactionHandler does not turn a PendingSubmit into Submitted from the
        // PlaceOrder() bool. LEAN only performs that transition when the brokerage publishes an
        // OrderEvent, so omitting this event leaves an accepted KIS order permanently pending.
        OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero)
        {
            Status = OrderStatus.Submitted,
        });
        return true;
    }

    private bool PlaceUsOrder(Order order)
    {
        if (order.Symbol.SecurityType != SecurityType.Equity)
        {
            throw new NotSupportedException(
                $"KisBrokerage: only equities are supported, got {order.Symbol.SecurityType}.");
        }

        // KIS rejects overseas market orders client-side — KisUsTradingClient has no market-order
        // method at all (see its class doc comment), and this check makes the rejection explicit
        // and clear at the LEAN Order layer too, rather than letting a non-limit order silently
        // reach (and be mis-sent by) the trading client.
        if (order is not LimitOrder limitOrder)
        {
            throw new NotSupportedException(
                $"KisBrokerage: KIS does not support US market orders (overseas market orders are " +
                $"rejected client-side by KIS) — only limit orders are supported for Market.USA " +
                $"(see KisUsTradingClient.PlaceLimitOrderAsync); got order type '{order.Type}'.");
        }

        var side = order.Quantity > 0 ? "buy" : "sell";
        var quantity = (int)Math.Abs(order.Quantity);

        var result = _usTradingClient
            .PlaceLimitOrderAsync(side, order.Symbol.Value, quantity, limitOrder.LimitPrice)
            .GetAwaiter().GetResult();

        if (!result.Ok)
        {
            OnMessage(new BrokerageMessageEvent(
                BrokerageMessageType.Warning,
                result.MessageCode ?? result.ReturnCode ?? "KIS_US_ORDER_ERROR",
                result.ErrorMessage ?? "Unknown KIS error"));
            return false;
        }

        if (string.IsNullOrWhiteSpace(result.OrderNo))
        {
            OnMessage(new BrokerageMessageEvent(
                BrokerageMessageType.Error, "KIS_US_ORDER_ID_MISSING",
                "KIS accepted the US order but no broker order id could be recovered."));
            return false;
        }

        order.BrokerId.Add(result.OrderNo);
        _orderTracking[result.OrderNo] = new KisOrderTracking(
            Market.USA, order.Symbol.Value, OrgNo: null, KisUsTradingClient.DefaultExchange,
            quantity, limitOrder.LimitPrice, order);
        OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero)
        {
            Status = OrderStatus.Submitted,
        });
        return true;
    }

    /// <summary>
    /// Resolves the tracking entry + current KIS order number for <paramref name="order"/>.
    /// Tries the in-memory table first (populated by PlaceOrder/PlaceUsOrder or a prior
    /// GetOpenOrders call); if that misses — e.g. this process didn't place the order itself, or
    /// was restarted — falls back to a live GetOpenOrders() call, which re-seeds the table from
    /// KIS's own unfilled-orders query before trying once more.
    /// </summary>
    private (string OrderNo, KisOrderTracking Tracking)? ResolveOrderTracking(Order order)
    {
        var orderNo = order.BrokerId.LastOrDefault();
        if (orderNo != null && _orderTracking.TryGetValue(orderNo, out var tracking))
        {
            return (orderNo, tracking);
        }

        GetOpenOrders(); // refreshes _orderTracking as a side effect

        orderNo = order.BrokerId.LastOrDefault();
        if (orderNo != null && _orderTracking.TryGetValue(orderNo, out tracking))
        {
            return (orderNo, tracking);
        }

        return null;
    }

    public override bool UpdateOrder(Order order)
    {
        if (!_marketScope.Allows(order.Symbol.ID.Market))
            throw new InvalidOperationException("KisBrokerage market scope rejects order modification.");
        using var quotePause = KisQuotePollingControl.AcquirePause(ExactScope(order.Symbol.ID.Market));
        if (order is not LimitOrder limitOrder)
        {
            throw new NotSupportedException(
                $"KisBrokerage: only limit order modification is supported; got order type '{order.Type}'.");
        }

        var resolved = ResolveOrderTracking(order);
        if (resolved is not { } r)
        {
            OnMessage(new BrokerageMessageEvent(
                BrokerageMessageType.Warning, "KIS_ORDER_NOT_FOUND",
                $"KisBrokerage: cannot modify order — no KIS order number/tracking found for LEAN order {order.Id}."));
            return false;
        }

        if (_fillPollingEnabled && !SynchronizeTrackedOrder(r.OrderNo, r.Tracking))
        {
            return false;
        }
        if (!_orderTracking.TryGetValue(r.OrderNo, out var currentTracking))
        {
            // The final pre-modify synchronization observed a complete fill. No broker amend is
            // needed; the emitted Filled event is the authoritative LEAN transition.
            return true;
        }
        r = (r.OrderNo, currentTracking);

        var newQty = (int)Math.Max(0m, Math.Abs(order.Quantity) - r.Tracking.LeanReportedFilled);
        if (newQty == 0) return true;
        var result = r.Tracking.Market == Market.KRX
            ? ToResult(_tradingClient.ModifyOrderAsync(r.OrderNo, r.Tracking.OrgNo ?? "", newQty, limitOrder.LimitPrice)
                .GetAwaiter().GetResult())
            : ToResult(_usTradingClient.ModifyOrderAsync(
                r.OrderNo, r.Tracking.Ticker, newQty, limitOrder.LimitPrice,
                r.Tracking.Exchange ?? KisUsTradingClient.DefaultExchange).GetAwaiter().GetResult());

        if (!result.Ok)
        {
            if (_fillPollingEnabled) SynchronizeTrackedOrder(r.OrderNo, r.Tracking);
            if (!_orderTracking.ContainsKey(r.OrderNo)) return true;
            OnMessage(new BrokerageMessageEvent(
                result.ReturnCode == "KIS_ORDER_ID_MISSING"
                    ? BrokerageMessageType.Error
                    : BrokerageMessageType.Warning,
                result.ReturnCode ?? "KIS_MODIFY_ERROR",
                result.ErrorMessage ?? "Unknown KIS error modifying order"));
            return false;
        }

        // KIS's order-rvsecncl issues a *new* order number for the modify action itself (see
        // KisCancelModifyResult/KisUsCancelModifyResult doc comments) — re-key the tracking table
        // and reflect the new id on the LEAN order so a subsequent cancel/modify targets it.
        _orderTracking.TryRemove(r.OrderNo, out _);
        if (!string.IsNullOrWhiteSpace(result.OrderNo))
        {
            order.BrokerId.Add(result.OrderNo);
            _orderTracking[result.OrderNo] = r.Tracking with
            {
                Quantity = newQty,
                LimitPrice = limitOrder.LimitPrice,
                LeanOrder = order,
                BrokerReportedFilled = 0m,
                BrokerReportedNotional = 0m,
                BrokerReportedNotionalKnown = true
            };
        }

        OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero) { Status = OrderStatus.UpdateSubmitted });
        return true;
    }

    public override bool CancelOrder(Order order)
    {
        if (!_marketScope.Allows(order.Symbol.ID.Market))
            throw new InvalidOperationException("KisBrokerage market scope rejects order cancellation.");
        using var quotePause = KisQuotePollingControl.AcquirePause(ExactScope(order.Symbol.ID.Market));
        var resolved = ResolveOrderTracking(order);
        if (resolved is not { } r)
        {
            OnMessage(new BrokerageMessageEvent(
                BrokerageMessageType.Warning, "KIS_ORDER_NOT_FOUND",
                $"KisBrokerage: cannot cancel order — no KIS order number/tracking found for LEAN order {order.Id}."));
            return false;
        }

        if (_fillPollingEnabled && !SynchronizeTrackedOrder(r.OrderNo, r.Tracking))
            return false;
        if (!_orderTracking.TryGetValue(r.OrderNo, out var currentTracking))
            return true;
        r = (r.OrderNo, currentTracking);

        var qty = (int)Math.Max(0m, r.Tracking.Quantity - r.Tracking.BrokerReportedFilled);
        if (qty == 0) return true;
        var result = r.Tracking.Market == Market.KRX
            ? ToResult(_tradingClient.CancelOrderAsync(r.OrderNo, r.Tracking.OrgNo ?? "", qty).GetAwaiter().GetResult())
            : ToResult(_usTradingClient.CancelOrderAsync(
                r.OrderNo, r.Tracking.Ticker, qty, r.Tracking.Exchange ?? KisUsTradingClient.DefaultExchange)
                .GetAwaiter().GetResult());

        if (!result.Ok)
        {
            if (_fillPollingEnabled) SynchronizeTrackedOrder(r.OrderNo, r.Tracking);
            if (!_orderTracking.ContainsKey(r.OrderNo)) return true;
            if (r.Tracking.Market == Market.USA &&
                result.ReturnCode is "KIS_ORDER_TIMEOUT" or "KIS_ORDER_TRANSPORT" &&
                ReconcileAmbiguousUsCancel(r.OrderNo, r.Tracking))
                return true;
            OnMessage(new BrokerageMessageEvent(
                BrokerageMessageType.Warning, result.ReturnCode ?? "KIS_CANCEL_ERROR",
                result.ErrorMessage ?? "Unknown KIS error canceling order"));
            return false;
        }

        // KIS may fill part or all of the order between the last periodic poll and the cancel.
        // Synchronize the accepted cancel's final cumulative fill before publishing Canceled.
        if (_fillPollingEnabled && !SynchronizeTrackedOrder(r.OrderNo, r.Tracking))
        {
            return false;
        }

        if (!_orderTracking.ContainsKey(r.OrderNo))
        {
            // A full fill won the race. The Filled event already removed the tracking entry and
            // must remain the terminal state; publishing Canceled afterwards would corrupt it.
            return true;
        }

        _orderTracking.TryRemove(r.OrderNo, out _);
        OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero) { Status = OrderStatus.Canceled });
        return true;
    }

    /// <summary>
    /// Polls cumulative KIS fills once and emits only positive deltas. Exposed internally for
    /// deterministic fake-HTTP tests; production calls are driven by the single trading-instance
    /// timer created in <see cref="Connect"/>.
    /// </summary>
    internal async Task PollFillsOnceAsync()
    {
        if (Volatile.Read(ref _disposing) != 0) return;
        if (Interlocked.Exchange(ref _fillPollInFlight, 1) != 0) return;
        try
        {
            await _fillSyncGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposing) != 0) return;
                if (_orderTracking.IsEmpty) return;
                await SynchronizeAllFillsUnsafeAsync().ConfigureAwait(false);
            }
            finally
            {
                _fillSyncGate.Release();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            OnMessage(new BrokerageMessageEvent(
                BrokerageMessageType.Warning, "KIS_FILL_POLL_ERROR",
                $"KIS fill synchronization failed: {exception.GetType().Name}: {exception.Message}"));
        }
        finally
        {
            Volatile.Write(ref _fillPollInFlight, 0);
        }
    }

    private bool SynchronizeTrackedOrder(string orderNo, KisOrderTracking tracking)
    {
        try
        {
            _fillSyncGate.Wait();
            try
            {
                if (tracking.Market == Market.KRX)
                {
                    var rows = ExecuteKisCallAsync(() => _tradingClient.GetDailyFilledOrdersAsync())
                        .GetAwaiter().GetResult();
                    var row = rows.FirstOrDefault(item => item.OrderNo == orderNo);
                    ApplyFill(orderNo, row?.FilledQty, row?.AverageFilledPrice);
                }
                else
                {
                    var rows = ExecuteKisCallAsync(() => _usTradingClient.GetFilledOrdersAsync())
                        .GetAwaiter().GetResult();
                    var row = rows.FirstOrDefault(item =>
                        KisUsTradingClient.OrderNumbersEqual(item.OrderNo, orderNo) ||
                        KisUsTradingClient.OrderNumbersEqual(item.OriginalOrderNo, orderNo));
                    ApplyFill(orderNo, row?.FilledQty, row?.AverageFilledPrice);
                }
            }
            finally
            {
                _fillSyncGate.Release();
            }
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            OnMessage(new BrokerageMessageEvent(
                BrokerageMessageType.Error, "KIS_FILL_SYNC_ERROR",
                $"KIS final fill synchronization failed: {exception.GetType().Name}"));
            return false;
        }
    }

    private async Task SynchronizeAllFillsUnsafeAsync()
    {
        if ((_marketScope is KisMarketScope.Kr or KisMarketScope.Both) &&
            _orderTracking.Values.Any(x => x.Market == Market.KRX))
        {
            using var quotePause = KisQuotePollingControl.AcquirePause(KisMarketScope.Kr);
            var rows = await ExecuteKisCallAsync(() => _tradingClient.GetDailyFilledOrdersAsync())
                .ConfigureAwait(false);
            foreach (var row in rows)
                ApplyFill(row.OrderNo, row.FilledQty, row.AverageFilledPrice);
        }

        if ((_marketScope is KisMarketScope.Us or KisMarketScope.Both) &&
            _orderTracking.Values.Any(x => x.Market == Market.USA))
        {
            using var quotePause = KisQuotePollingControl.AcquirePause(KisMarketScope.Us);
            var rows = await ExecuteKisCallAsync(() => _usTradingClient.GetFilledOrdersAsync())
                .ConfigureAwait(false);
            foreach (var row in rows)
            {
                var orderNo = FindMatchingOrderNumber(_orderTracking.Keys, row.OrderNo, row.OriginalOrderNo);
                if (!string.IsNullOrWhiteSpace(orderNo))
                    ApplyFill(orderNo, row.FilledQty, row.AverageFilledPrice);
            }
        }
    }

    private bool ReconcileAmbiguousUsCancel(string orderNo, KisOrderTracking tracking)
    {
        try
        {
            // A timed-out cancel response is ambiguous: KIS may have accepted the request.
            // Never replay the POST. Confirm the order disappeared from the read-only open-order
            // ledger and give the fill ledger several chances to expose a racing execution.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var open = ExecuteKisCallAsync(() => _usTradingClient.GetUnfilledOrdersAsync())
                    .GetAwaiter().GetResult();
                if (open.Any(x => KisUsTradingClient.OrderNumbersEqual(x.OrderNo, orderNo))) return false;

                var fills = ExecuteKisCallAsync(() => _usTradingClient.GetFilledOrdersAsync(odno: orderNo))
                    .GetAwaiter().GetResult();
                foreach (var fill in fills)
                {
                    var id = FindMatchingOrderNumber(_orderTracking.Keys, fill.OrderNo, fill.OriginalOrderNo);
                    if (!string.IsNullOrWhiteSpace(id))
                        ApplyFill(id, fill.FilledQty, fill.AverageFilledPrice);
                }
                if (!_orderTracking.ContainsKey(orderNo)) return true;
                if (attempt < 2) Thread.Sleep(TimeSpan.FromSeconds(1));
            }

            if (!_orderTracking.TryRemove(orderNo, out _)) return true;
            OnOrderEvent(new OrderEvent(tracking.LeanOrder, DateTime.UtcNow, OrderFee.Zero)
            {
                Status = OrderStatus.Canceled
            });
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            OnMessage(new BrokerageMessageEvent(
                BrokerageMessageType.Warning, "KIS_CANCEL_RECONCILIATION_ERROR",
                $"KIS ambiguous cancel reconciliation failed: {exception.GetType().Name}: {exception.Message}"));
            return false;
        }
    }

    private void ApplyFill(string orderNo, decimal? cumulativeFilled, decimal? averageFilledPrice)
    {
        if (cumulativeFilled is null || cumulativeFilled <= 0m ||
            !_orderTracking.TryGetValue(orderNo, out var tracking)) return;
        if (cumulativeFilled < tracking.BrokerReportedFilled || cumulativeFilled > tracking.Quantity ||
            averageFilledPrice is null || averageFilledPrice <= 0m)
            throw new InvalidOperationException("KIS returned an invalid cumulative fill.");

        var delta = cumulativeFilled.Value - tracking.BrokerReportedFilled;
        if (delta == 0m)
        {
            if (!tracking.BrokerReportedNotionalKnown && averageFilledPrice is > 0m)
                _orderTracking.TryUpdate(orderNo, tracking with
                {
                    BrokerReportedNotional = cumulativeFilled.Value * averageFilledPrice.Value,
                    BrokerReportedNotionalKnown = true
                }, tracking);
            return;
        }
        if (delta < 0m) throw new InvalidOperationException("KIS cumulative fill moved backwards.");
        var cumulativeNotional = cumulativeFilled.Value * averageFilledPrice.Value;
        // Imported partially-filled orders do not expose their historical average fill price on
        // KIS's open-order response. Normally the first status poll seeds that baseline without
        // emitting an event. If another fill races that first poll, use the latest cumulative
        // average for that one delta (conservative approximation), then future deltas are exact.
        var deltaPrice = tracking.BrokerReportedNotionalKnown
            ? (cumulativeNotional - tracking.BrokerReportedNotional) / delta
            : averageFilledPrice.Value;
        if (deltaPrice <= 0m) throw new InvalidOperationException("KIS returned an invalid fill price.");

        var totalLeanFilled = tracking.LeanReportedFilled + delta;
        var totalRequested = Math.Abs(tracking.LeanOrder.Quantity);
        if (totalLeanFilled > totalRequested)
            throw new InvalidOperationException("KIS cumulative fill exceeds the LEAN order quantity.");

        var updated = tracking with
        {
            BrokerReportedFilled = cumulativeFilled.Value,
            BrokerReportedNotional = cumulativeNotional,
            BrokerReportedNotionalKnown = true,
            LeanReportedFilled = totalLeanFilled
        };
        if (!_orderTracking.TryUpdate(orderNo, updated, tracking)) return;

        var isFilled = totalLeanFilled == totalRequested;
        OnOrderEvent(new OrderEvent(tracking.LeanOrder, DateTime.UtcNow, OrderFee.Zero)
        {
            Status = isFilled ? OrderStatus.Filled : OrderStatus.PartiallyFilled,
            FillQuantity = tracking.LeanOrder.Quantity < 0m ? -delta : delta,
            FillPrice = deltaPrice
        });
        if (isFilled)
            _orderTracking.TryRemove(new KeyValuePair<string, KisOrderTracking>(orderNo, updated));
    }

    private static string? FindMatchingOrderNumber(
        IEnumerable<string> candidates, params string?[] reportedOrderNumbers)
    {
        foreach (var candidate in candidates)
        {
            if (reportedOrderNumbers.Any(reported =>
                    KisUsTradingClient.OrderNumbersEqual(candidate, reported)))
                return candidate;
        }
        return null;
    }

    private void SeedImportedFillBaseline(
        string orderNo, decimal openOrderFilled, decimal currentFilled, decimal averageFilledPrice)
    {
        if (!_orderTracking.TryGetValue(orderNo, out var tracking)) return;
        if (currentFilled < openOrderFilled || currentFilled > tracking.Quantity)
            throw new InvalidOperationException("KIS imported fill baseline is invalid.");
        // If no fill raced the open-order query, seed exact notional and the first periodic poll
        // becomes a zero-delta no-op. If currentFilled advanced, preserve the earlier baseline so
        // the first post-setup poll emits that race delta after LEAN has imported the open order.
        if (currentFilled == openOrderFilled && (currentFilled == 0m || averageFilledPrice > 0m))
            _orderTracking.TryUpdate(orderNo, tracking with
            {
                BrokerReportedNotional = currentFilled * averageFilledPrice,
                BrokerReportedNotionalKnown = true
            }, tracking);
    }

    // KisCancelModifyResult (KR) and KisUsCancelModifyResult (US) carry the same fields but
    // aren't a shared type across the two client namespaces — this local tuple shape lets
    // UpdateOrder/CancelOrder above handle both markets with one code path.
    private static (bool Ok, string? OrderNo, string? ErrorMessage, string? ReturnCode) ToResult(
        KisCancelModifyResult r) => (r.Ok, r.OrderNo, r.ErrorMessage, r.ReturnCode);

    private static (bool Ok, string? OrderNo, string? ErrorMessage, string? ReturnCode) ToResult(
        KisUsCancelModifyResult r) => (r.Ok, r.OrderNo, r.ErrorMessage, r.ReturnCode);

    /// <summary>
    /// Retrieves completed daily trade bars from KIS's public quotation APIs. This deliberately
    /// supports only KRX/USA equities, daily resolution, trade ticks, and <see cref="TradeBar"/>.
    /// LEAN can chain this brokerage provider with its file provider for every other request.
    /// </summary>
    public override IEnumerable<BaseData> GetHistory(HistoryRequest request)
    {
        if (!_marketScope.Allows(request.Symbol.ID.Market))
        {
            OnMessage(new BrokerageMessageEvent(
                BrokerageMessageType.Warning, "KIS_HISTORY_SCOPE_REJECTED",
                $"KisBrokerage history request market {request.Symbol.ID.Market} is outside {_marketScope} scope."));
            return Enumerable.Empty<BaseData>();
        }

        if (!IsSupportedHistoryRequest(request, out var unsupportedReason))
        {
            OnMessage(new BrokerageMessageEvent(
                BrokerageMessageType.Warning, "KIS_HISTORY_UNSUPPORTED",
                $"KisBrokerage history request ignored: {unsupportedReason}."));
            return Enumerable.Empty<BaseData>();
        }

        var nowUtc = DateTime.UtcNow;
        var effectiveEndUtc = request.EndTimeUtc < nowUtc ? request.EndTimeUtc : nowUtc;
        if (effectiveEndUtc <= request.StartTimeUtc)
        {
            return Enumerable.Empty<BaseData>();
        }

        var effectiveEndLocal = ConvertUtcToLocal(effectiveEndUtc, request.ExchangeHours.TimeZone);
        var startDate = DateOnly.FromDateTime(request.StartTimeLocal);
        var endDate = DateOnly.FromDateTime(effectiveEndLocal);

        try
        {
            using var timeout = new CancellationTokenSource(HistoryRequestTimeout);
            IReadOnlyList<KisDailyBar> rows = request.Symbol.ID.Market == Market.KRX
                ? _tradingClient.GetDailyBarsAsync(request.Symbol.Value, startDate, endDate, timeout.Token)
                    .GetAwaiter().GetResult()
                : _usTradingClient.GetDailyBarsAsync(
                    request.Symbol.Value, startDate, endDate, ct: timeout.Token)
                    .GetAwaiter().GetResult();

            return rows
                .Where(row => IsCompletedWithinRequest(row.Date, request, effectiveEndLocal))
                .Select(row => (BaseData)new TradeBar(
                    row.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified),
                    request.Symbol,
                    row.Open,
                    row.High,
                    row.Low,
                    row.Close,
                    row.Volume,
                    Time.OneDay))
                .OrderBy(bar => bar.Time)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or
            System.Text.Json.JsonException or OperationCanceledException)
        {
            OnMessage(new BrokerageMessageEvent(
                BrokerageMessageType.Warning, "KIS_HISTORY_ERROR",
                $"KIS history query failed for {request.Symbol.Value}: {ex.Message}"));
            return Enumerable.Empty<BaseData>();
        }
    }

    private static bool IsSupportedHistoryRequest(HistoryRequest request, out string reason)
    {
        if (request.Symbol.SecurityType != SecurityType.Equity)
        {
            reason = $"security type {request.Symbol.SecurityType} is not Equity";
            return false;
        }
        if (request.Symbol.ID.Market is not (Market.KRX or Market.USA))
        {
            reason = $"market {request.Symbol.ID.Market} is not KRX or USA";
            return false;
        }
        if (request.Resolution != Resolution.Daily)
        {
            reason = $"resolution {request.Resolution} is not Daily";
            return false;
        }
        if (request.DataType != typeof(TradeBar) || request.TickType != TickType.Trade)
        {
            reason = $"only TradeBar/Trade data is supported (got {request.DataType.Name}/{request.TickType})";
            return false;
        }

        reason = "";
        return true;
    }

    private static bool IsCompletedWithinRequest(
        DateOnly date, HistoryRequest request, DateTime effectiveEndLocal)
    {
        var localDate = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (!request.ExchangeHours.IsDateOpen(localDate, extendedMarketHours: false))
        {
            return false;
        }

        var completionTime = request.ExchangeHours.IsMarketAlwaysOpen
            ? localDate.AddDays(1)
            : request.ExchangeHours.GetNextMarketClose(localDate, extendedMarketHours: false);
        return completionTime > request.StartTimeLocal && completionTime <= effectiveEndLocal;
    }

    private static DateTime ConvertUtcToLocal(DateTime utc, NodaTime.DateTimeZone zone) =>
        NodaTime.Instant.FromDateTimeUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc))
            .InZone(zone)
            .ToDateTimeUnspecified();

    private static Symbol CreateUnmappedEquitySymbol(string ticker, string market) =>
        new(SecurityIdentifier.GenerateEquity(ticker, market, mapSymbol: false, null, null), ticker);

    private static KisMarketScope ExactScope(string market) => market switch
    {
        var value when value == Market.KRX => KisMarketScope.Kr,
        var value when value == Market.USA => KisMarketScope.Us,
        _ => throw new NotSupportedException($"KisBrokerage: unsupported LEAN market '{market}'."),
    };

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0) return;
        Disconnect();
        _fillSyncGate.Wait();
        try
        {
            KisBrokerageRuntimeRegistry.UnregisterTradingBrokerage(this);
            DisposeDataQueueHandler(); // KisBrokerage.DataQueueHandler.cs — stops polling timers.
            _tradingClient.Dispose();
            _usTradingClient.Dispose();
            _oauthClient.Dispose();
            base.Dispose();
        }
        finally
        {
            _fillSyncGate.Release();
        }
    }
}
