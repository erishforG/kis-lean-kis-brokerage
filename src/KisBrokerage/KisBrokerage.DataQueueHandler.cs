using System.Collections.Concurrent;
using NodaTime;
using QuantConnect;
using QuantConnect.Brokerages;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.KisBrokerage;

/// <summary>
/// <see cref="IDataQueueHandler"/> half of <see cref="KisBrokerage"/> — lets LEAN's
/// config.json point <c>data-queue-handler</c> at this same type (the standard
/// "brokerage doubles as its own data source" pattern LEAN's Launcher expects when no
/// separate data provider is configured).
///
/// SCOPE: this is deliberately a REST-polling implementation, not a real-time/websocket feed —
/// KIS websocket streaming is explicitly out of scope for this round (see project scope notes).
/// On <see cref="Subscribe"/>, a single shared round-robin <see cref="Timer"/> polls the real
/// KIS current-price/quote REST endpoint (KR: <c>KisKrTradingClient.GetQuoteAsync</c>, matching
/// kis_client.mjs::getEtfPrice / inquire-price; US: <c>KisUsTradingClient.GetQuoteAsync</c>,
/// matching kis_client.mjs::getOverseasPrice) for exactly one subscribed symbol per tick (the
/// tick interval is configurable via the <c>kis-quote-poll-interval-seconds</c> config key,
/// default 5s) and republishes the response as a LEAN <see cref="Tick"/> through the standard
/// <see cref="IDataAggregator"/>/<see cref="EventBasedDataQueueHandlerSubscriptionManager"/>
/// plumbing (the same pattern used by LEAN's own community brokerage plugins).
///
/// One shared timer instead of one timer per symbol is deliberate: a per-symbol timer design
/// dispatches all subscribed symbols' first polls at once (dueTime 0) and then every
/// <c>QuotePollTickInterval</c> thereafter regardless of how many symbols are subscribed, so
/// once a portfolio holds more symbols than <see cref="Client.KisRateLimiter"/>'s gap allows to
/// clear per tick interval, requests permanently outpace the shared
/// <see cref="KisBrokerage.ExecuteKisCallAsync{T}"/> throttle gate and pile up (this is exactly
/// what happened in the first real smoke test against a paper account holding 9 symbols across
/// KR+US — see smoke-fixed4.log). Round-robin makes exactly one quote request per tick
/// regardless of portfolio size, so per-symbol refresh cadence is
/// <c>QuotePollTickInterval * subscribed symbol count</c> — slower per symbol as the portfolio
/// grows, but it can never exceed the KIS rate limit no matter how many symbols are held. This
/// means data is read-only and has multi-tick-interval lag — not tick-accurate, not suitable for
/// anything latency-sensitive. No order-related endpoints are called from this file.
/// </summary>
public sealed partial class KisBrokerage : IDataQueueHandler
{
    // Not real-time: this is a REST-polling stand-in, not a websocket feed. Gap between
    // successive round-robin quote polls (see class doc comment) — must stay >= the shared
    // KisRateLimiter gap or every tick would just wait out the shared throttle gate anyway.
    private static readonly TimeSpan QuotePollTickInterval =
        TimeSpan.FromSeconds(Config.GetDouble("kis-quote-poll-interval-seconds", 5));

    private readonly ConcurrentDictionary<Symbol, DateTimeZone> _dataQueueExchangeTimeZones = new();
    private readonly ConcurrentDictionary<Symbol, int> _dataQueueSubscriberCounts = new();
    private readonly ConcurrentDictionary<Symbol, byte> _dataQueuePollsInFlight = new();
    private readonly object _dataQueuePollTimerLock = new();
    private Timer? _dataQueuePollTimer;
    private int _dataQueuePollCursor = -1;
    private int _activeQuotePolls;

    private IDataAggregator? _dataAggregator;
    private EventBasedDataQueueHandlerSubscriptionManager? _dataQueueSubscriptionManager;

    /// <inheritdoc />
    public void SetJob(LiveNodePacket job)
    {
        _dataQueueSubscriptionManager = new EventBasedDataQueueHandlerSubscriptionManager();
        _dataQueueSubscriptionManager.SubscribeImpl = (symbols, _) =>
        {
            foreach (var symbol in symbols)
            {
                StartPollingSymbol(symbol);
            }
            return true;
        };
        _dataQueueSubscriptionManager.UnsubscribeImpl = (symbols, _) =>
        {
            foreach (var symbol in symbols)
            {
                StopPollingSymbol(symbol);
            }
            return true;
        };

        // AggregationManager itself lives in QuantConnect.Lean.Engine (the Launcher process),
        // not in any package this project references — resolved by type name through the same
        // Composer/MEF mechanism the Launcher uses to find KisBrokerageFactory in the first
        // place. This is the standard pattern LEAN's own brokerage plugins use so they don't
        // need a project reference to the engine.
        _dataAggregator = Composer.Instance.GetExportedValueByTypeName<IDataAggregator>(
            Config.Get("data-aggregator", "QuantConnect.Lean.Engine.DataFeeds.AggregationManager"));
        _dataAggregator.Initialize(new DataAggregatorInitializeParameters
        {
            AlgorithmSettings = new AlgorithmSettings()
        });
    }

    /// <inheritdoc />
    public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
    {
        if (_dataAggregator == null || _dataQueueSubscriptionManager == null)
        {
            // LEAN's Launcher always calls SetJob before Subscribe for a live data queue
            // handler; if this fires, config.json's wiring is broken, not this brokerage.
            throw new InvalidOperationException(
                "KisBrokerage: Subscribe() called before SetJob() — data-queue-handler wiring issue.");
        }

        if ((dataConfig.Symbol.ID.Market != Market.KRX && dataConfig.Symbol.ID.Market != Market.USA) ||
            !_marketScope.Allows(dataConfig.Symbol.ID.Market))
        {
            // Only the two markets KisBrokerage actually trades have a quote client wired up.
            return null;
        }

        _dataQueueExchangeTimeZones[dataConfig.Symbol] = dataConfig.ExchangeTimeZone;
        var enumerator = _dataAggregator.Add(dataConfig, newDataAvailableHandler);
        _dataQueueSubscriptionManager.Subscribe(dataConfig);
        return enumerator;
    }

    /// <inheritdoc />
    public void Unsubscribe(SubscriptionDataConfig dataConfig)
    {
        _dataQueueSubscriptionManager?.Unsubscribe(dataConfig);
        _dataAggregator?.Remove(dataConfig);
    }

    /// <summary>
    /// Ref-counts <paramref name="symbol"/> into the round-robin polling set (one entry
    /// regardless of how many tick-type channels subscribe to it —
    /// <see cref="DataQueueHandlerSubscriptionManager"/> ref-counts per (symbol, tick type)
    /// channel) and lazily starts the single shared polling timer on first subscriber.
    /// </summary>
    private void StartPollingSymbol(Symbol symbol)
    {
        _dataQueueSubscriberCounts.AddOrUpdate(symbol, 1, (_, count) => count + 1);
        lock (_dataQueuePollTimerLock)
        {
            _dataQueuePollTimer ??= new Timer(
                _ => PollNextSymbol(),
                null,
                QuotePollTickInterval,
                QuotePollTickInterval);
        }
    }

    private void StopPollingSymbol(Symbol symbol)
    {
        var remaining = _dataQueueSubscriberCounts.AddOrUpdate(symbol, 0, (_, count) => Math.Max(0, count - 1));
        if (remaining > 0)
        {
            return;
        }

        _dataQueueSubscriberCounts.TryRemove(symbol, out _);
        _dataQueueExchangeTimeZones.TryRemove(symbol, out _);

        lock (_dataQueuePollTimerLock)
        {
            if (_dataQueueSubscriberCounts.IsEmpty && _dataQueuePollTimer != null)
            {
                _dataQueuePollTimer.Dispose();
                _dataQueuePollTimer = null;
            }
        }
    }

    /// <summary>
    /// Timer callback: advances the round-robin cursor and polls exactly one currently
    /// subscribed symbol per tick, so the KIS request rate this class generates never scales
    /// with portfolio size — see the class doc comment for why per-symbol timers don't have
    /// this property.
    /// </summary>
    private void PollNextSymbol()
    {
        var symbols = _dataQueueSubscriberCounts.Keys.ToArray();
        if (symbols.Length == 0)
        {
            return;
        }

        var cursor = Interlocked.Increment(ref _dataQueuePollCursor);
        var symbol = symbols[(int)((uint)cursor % (uint)symbols.Length)];
        var scope = ExactScope(symbol);
        if (!KisQuotePollingControl.IsEnabled(scope))
        {
            return;
        }
        var quoteCancellation = KisQuotePollingControl.CurrentToken(scope);

        // System.Threading.Timer does not wait for an async callback. A slow in-flight call for
        // this same symbol from a previous cycle (rare — one round trip per subscribed symbol
        // count ticks) is skipped rather than queued a second time.
        if (_dataQueuePollsInFlight.TryAdd(symbol, 0))
        {
            Interlocked.Increment(ref _activeQuotePolls);
            _ = PollAndPublishAsync(symbol, quoteCancellation);
        }
    }

    private async Task PollAndPublishAsync(Symbol symbol, CancellationToken cancellationToken)
    {
        try
        {
            Tick? tick = symbol.ID.Market switch
            {
                var market when market == Market.KRX =>
                    await PollKrTickAsync(symbol, cancellationToken).ConfigureAwait(false),
                var market when market == Market.USA =>
                    await PollUsTickAsync(symbol, cancellationToken).ConfigureAwait(false),
                _ => null,
            };

            if (tick != null)
            {
                _dataAggregator?.Update(tick);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when reconciliation/order HTTP takes priority or the queue shuts down.
        }
        catch (Exception exception)
        {
            OnMessage(new BrokerageMessageEvent(
                BrokerageMessageType.Warning, "KIS_QUOTE_POLL_ERROR",
                $"KisBrokerage: quote poll failed for {symbol.Value}: {exception.Message}"));
        }
        finally
        {
            _dataQueuePollsInFlight.TryRemove(symbol, out _);
            Interlocked.Decrement(ref _activeQuotePolls);
        }
    }

    private async Task<Tick?> PollKrTickAsync(Symbol symbol, CancellationToken cancellationToken)
    {
        var quote = await ExecuteKisCallAsync(
            () => _tradingClient.GetQuoteAsync(symbol.Value, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (!quote.Ok)
        {
            OnMessage(new BrokerageMessageEvent(
                BrokerageMessageType.Warning, "KIS_QUOTE_ERROR",
                quote.ErrorMessage ?? $"Unknown KIS error polling quote for {symbol.Value}"));
            return null;
        }

        return new Tick(ExchangeLocalNow(symbol), symbol, string.Empty, string.Empty, quote.Volume, quote.Price);
    }

    private async Task<Tick?> PollUsTickAsync(Symbol symbol, CancellationToken cancellationToken)
    {
        // Not wrapped in ExecuteKisCallAsync: GetQuoteAsync may itself make up to 3 internal KIS
        // HTTP calls (one per known price-exchange code — see that method's doc comment), each
        // already throttled individually via the same shared KisRateLimiter gate. Wrapping the
        // whole call here too would try to re-acquire the same non-reentrant SemaphoreSlim(1,1)
        // from inside itself and deadlock (same reasoning as KisBrokerage.cs's US GetBalanceAsync
        // call sites).
        var quote = await _usTradingClient.GetQuoteAsync(symbol.Value, ct: cancellationToken).ConfigureAwait(false);
        if (!quote.Ok)
        {
            OnMessage(new BrokerageMessageEvent(
                BrokerageMessageType.Warning, "KIS_US_QUOTE_ERROR",
                quote.ErrorMessage ?? $"Unknown KIS error polling quote for {symbol.Value}"));
            return null;
        }

        return new Tick(ExchangeLocalNow(symbol), symbol, string.Empty, string.Empty, quote.Volume, quote.Last);
    }

    /// <summary>
    /// Current wall-clock time in <paramref name="symbol"/>'s subscribed exchange time zone —
    /// matches what <see cref="IDataAggregator"/>'s consolidators expect
    /// (<c>SubscriptionDataConfig.ExchangeTimeZone</c>-stamped raw data), falling back to UTC if
    /// this symbol's timezone was somehow never recorded by <see cref="Subscribe"/>.
    /// </summary>
    private DateTime ExchangeLocalNow(Symbol symbol)
    {
        var zone = _dataQueueExchangeTimeZones.TryGetValue(symbol, out var tz) ? tz : DateTimeZone.Utc;
        return Instant.FromDateTimeUtc(DateTime.UtcNow).InZone(zone).ToDateTimeUnspecified();
    }

    /// <summary>Called from <see cref="Dispose"/> (KisBrokerage.cs) — stops the shared polling timer.</summary>
    private void DisposeDataQueueHandler()
    {
        // The trading brokerage and data-queue handler are separate KisBrokerage instances.
        // Only the instance initialized through SetJob owns quote polling and may clear the
        // process-local pause at shutdown; otherwise disposal of the trading instance could
        // re-enable the still-live data queue while its HttpClient is being torn down.
        var ownsDataQueue = _dataAggregator is not null || _dataQueueSubscriptionManager is not null;
        lock (_dataQueuePollTimerLock)
        {
            _dataQueuePollTimer?.Dispose();
            _dataQueuePollTimer = null;
        }
        if (ownsDataQueue)
        {
            if (_marketScope is KisMarketScope.Kr or KisMarketScope.Both)
                KisQuotePollingControl.Reset(KisMarketScope.Kr);
            if (_marketScope is KisMarketScope.Us or KisMarketScope.Both)
                KisQuotePollingControl.Reset(KisMarketScope.Us);
            SpinWait.SpinUntil(
                () => Volatile.Read(ref _activeQuotePolls) == 0,
                TimeSpan.FromSeconds(5));
        }
        _dataQueueSubscriberCounts.Clear();
        _dataQueueExchangeTimeZones.Clear();
        _dataQueuePollsInFlight.Clear();
        _dataAggregator?.Dispose();
    }

    private static KisMarketScope ExactScope(Symbol symbol) => symbol.ID.Market switch
    {
        var market when market == Market.KRX => KisMarketScope.Kr,
        var market when market == Market.USA => KisMarketScope.Us,
        _ => throw new ArgumentOutOfRangeException(nameof(symbol), "Unsupported KIS quote market."),
    };
}
