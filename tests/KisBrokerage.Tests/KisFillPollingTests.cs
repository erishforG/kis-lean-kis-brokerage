using System.Net;
using System.Text;
using QuantConnect.Brokerages;
using QuantConnect.KisBrokerage.Client;
using QuantConnect.Orders;
using Xunit;

namespace QuantConnect.KisBrokerage.Tests;

public sealed class KisFillPollingTests
{
    [Theory]
    [InlineData("2026-09-14T14:59:59Z", "20260914")]
    [InlineData("2026-09-14T15:00:00Z", "20260915")]
    [InlineData("2026-09-14T17:45:18Z", "20260915")]
    public void UsFillHistoryUsesKisKoreanBusinessDate(string instant, string expected)
    {
        Assert.Equal(expected, KisUsTradingClient.KisBusinessDate(DateTimeOffset.Parse(instant)));
    }

    [Fact]
    public async Task UsFillHistoryMatchesOriginalOrderNumberAfterModification()
    {
        Uri? fillRequest = null;
        using var fixture = new Fixture((request, _) =>
        {
            if (IsToken(request)) return Task.FromResult(Token());
            fillRequest = request.RequestUri;
            return Task.FromResult(UsFills(UsRow("US-NEW", "US-OLD", 1, 1, 100m)));
        });

        var fill = Assert.Single(await fixture.Us.GetFilledOrdersAsync(odno: "US-OLD"));

        Assert.Equal("US-NEW", fill.OrderNo);
        Assert.Equal("US-OLD", fill.OriginalOrderNo);
        var query = System.Web.HttpUtility.ParseQueryString(fillRequest!.Query);
        Assert.False(string.IsNullOrWhiteSpace(query["ORD_DT"]));
        Assert.Equal(query["ORD_STRT_DT"], query["ORD_DT"]);
        Assert.Equal(query["ORD_END_DT"], query["ORD_DT"]);
    }

    [Fact]
    public async Task UsFillHistoryMatchesZeroPaddedSubmitOrderNumber()
    {
        using var fixture = new Fixture((request, _) =>
        {
            if (IsToken(request)) return Task.FromResult(Token());
            return Task.FromResult(UsFills(UsRow("35674", "0", 64, 64, 765.65m)));
        });

        var fill = Assert.Single(await fixture.Us.GetFilledOrdersAsync(odno: "0000035674"));

        Assert.Equal("35674", fill.OrderNo);
        Assert.Equal(64m, fill.FilledQty);
    }

    [Fact]
    public async Task UsFillPollingJoinsUnpaddedLedgerOrderToSubmittedOrder()
    {
        using var fixture = new Fixture((request, _) =>
        {
            if (IsToken(request)) return Task.FromResult(Token());
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/trading/order", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"rt_cd":"0","output":{"ODNO":"0000035674"}}"""));
            if (path.EndsWith("/trading/inquire-ccnl", StringComparison.Ordinal))
                return Task.FromResult(UsFills(UsRow("35674", "0", 64, 64, 765.65m)));
            throw new InvalidOperationException(path);
        });
        using var brokerage = fixture.Brokerage(KisMarketScope.Us);
        brokerage.EnableFillPolling();
        var events = new List<OrderEvent>();
        brokerage.OrdersStatusChanged += (_, batch) => events.AddRange(batch);
        var order = Limit("SPY", Market.USA, 64, 765.69m);

        Assert.True(brokerage.PlaceOrder(order));
        await brokerage.PollFillsOnceAsync();

        var fill = Assert.Single(events, x => x.Status == OrderStatus.Filled);
        Assert.Equal(64m, fill.FillQuantity);
        Assert.Equal(765.65m, fill.FillPrice);
    }

    [Fact]
    public async Task KrCumulativeFillsEmitDeltaOnlyAndPartialThenFull()
    {
        var fillPoll = 0;
        using var fixture = new Fixture((request, _) =>
        {
            if (IsToken(request)) return Task.FromResult(Token());
            if (request.RequestUri!.AbsolutePath.EndsWith("/order-cash", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"rt_cd":"0","output":{"ODNO":"KR-1","KRX_FWDG_ORD_ORGNO":"ORG"}}"""));
            if (request.RequestUri.AbsolutePath.EndsWith("/inquire-daily-ccld", StringComparison.Ordinal))
            {
                var n = Interlocked.Increment(ref fillPoll);
                var qty = n < 3 ? 2 : 5;
                var average = n < 3 ? "100" : "102";
                return Task.FromResult(KrFills($$"""
                    {"odno":"KR-1","pdno":"069500","ord_qty":"5","tot_ccld_qty":"{{qty}}","ord_unpr":"99","avg_prvs":"{{average}}","sll_buy_dvsn_cd":"02"}
                    """));
            }
            throw new InvalidOperationException(request.RequestUri.AbsolutePath);
        });
        using var brokerage = fixture.Brokerage(KisMarketScope.Kr);
        brokerage.EnableFillPolling();
        var events = new List<OrderEvent>();
        brokerage.OrdersStatusChanged += (_, batch) => events.AddRange(batch);
        var order = Limit("069500", Market.KRX, 5, 99m);

        Assert.True(brokerage.PlaceOrder(order));
        await brokerage.PollFillsOnceAsync();
        await brokerage.PollFillsOnceAsync();
        await brokerage.PollFillsOnceAsync();

        var fills = events.Where(x => x.FillQuantity != 0m).ToArray();
        Assert.Equal(2, fills.Length);
        Assert.Equal((OrderStatus.PartiallyFilled, 2m, 100m),
            (fills[0].Status, fills[0].FillQuantity, fills[0].FillPrice));
        Assert.Equal(OrderStatus.Filled, fills[1].Status);
        Assert.Equal(3m, fills[1].FillQuantity);
        Assert.Equal(310m / 3m, fills[1].FillPrice);
    }

    [Fact]
    public async Task UsModifyRekeysOneLeanOrderAndIgnoresOldBrokerRow()
    {
        var fillQuery = 0;
        string? modifyBody = null;
        using var fixture = new Fixture(async (request, _) =>
        {
            if (IsToken(request)) return Token();
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/trading/order", StringComparison.Ordinal))
                return Json("""{"rt_cd":"0","output":{"ODNO":"US-OLD"}}""");
            if (path.EndsWith("/trading/order-rvsecncl", StringComparison.Ordinal))
            {
                modifyBody = await request.Content!.ReadAsStringAsync();
                return Json("""{"rt_cd":"0","output":{"ODNO":"US-NEW"}}""");
            }
            if (path.EndsWith("/trading/inquire-ccnl", StringComparison.Ordinal))
            {
                var n = Interlocked.Increment(ref fillQuery);
                return n switch
                {
                    1 or 2 => UsFills(UsRow("US-OLD", "", 10, 2, 100m)),
                    3 => UsFills(
                        UsRow("US-OLD", "", 10, 2, 100m) + "," +
                        UsRow("US-NEW", "US-OLD", 8, 3, 99m)),
                    _ => UsFills(UsRow("US-NEW", "US-OLD", 8, 8, 98m))
                };
            }
            throw new InvalidOperationException(path);
        });
        using var brokerage = fixture.Brokerage(KisMarketScope.Us);
        brokerage.EnableFillPolling();
        var events = new List<OrderEvent>();
        brokerage.OrdersStatusChanged += (_, batch) => events.AddRange(batch);
        var order = Limit("AAPL", Market.USA, 10, 101m);

        Assert.True(brokerage.PlaceOrder(order));
        await brokerage.PollFillsOnceAsync(); // old id: 2/10
        var replacement = Replacement(order, 99m);
        Assert.True(brokerage.UpdateOrder(replacement)); // final old-id sync repeats 2/10
        Assert.Contains("\"ORD_QTY\":\"8\"", modifyBody, StringComparison.Ordinal);
        Assert.Equal("US-NEW", replacement.BrokerId[^1]);
        await brokerage.PollFillsOnceAsync(); // old row ignored; new id adds 3
        await brokerage.PollFillsOnceAsync(); // new id adds remaining 5

        var fills = events.Where(x => x.FillQuantity != 0m).ToArray();
        Assert.Equal([2m, 3m, 5m], fills.Select(x => x.FillQuantity));
        Assert.Equal([OrderStatus.PartiallyFilled, OrderStatus.PartiallyFilled, OrderStatus.Filled],
            fills.Select(x => x.Status));
        Assert.All(fills, fill => Assert.Equal(order.Id, fill.OrderId));
    }

    [Fact]
    public void CancelSynchronizesRaceFillBeforeCanceled()
    {
        using var fixture = new Fixture((request, _) =>
        {
            if (IsToken(request)) return Task.FromResult(Token());
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/order-cash", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"rt_cd":"0","output":{"ODNO":"KR-RACE","KRX_FWDG_ORD_ORGNO":"ORG"}}"""));
            if (path.EndsWith("/order-rvsecncl", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"rt_cd":"0","output":{"ODNO":"KR-CANCEL"}}"""));
            if (path.EndsWith("/inquire-daily-ccld", StringComparison.Ordinal))
                return Task.FromResult(KrFills("""{"odno":"KR-RACE","pdno":"069500","ord_qty":"5","tot_ccld_qty":"2","ord_unpr":"99","avg_prvs":"99","sll_buy_dvsn_cd":"02"}"""));
            throw new InvalidOperationException(path);
        });
        using var brokerage = fixture.Brokerage(KisMarketScope.Kr);
        brokerage.EnableFillPolling();
        var events = new List<OrderEvent>();
        brokerage.OrdersStatusChanged += (_, batch) => events.AddRange(batch);
        var order = Limit("069500", Market.KRX, 5, 99m);

        Assert.True(brokerage.PlaceOrder(order));
        Assert.True(brokerage.CancelOrder(order));

        Assert.Equal([OrderStatus.Submitted, OrderStatus.PartiallyFilled, OrderStatus.Canceled],
            events.Select(x => x.Status));
        Assert.Equal(2m, events[1].FillQuantity);
    }

    [Fact]
    public async Task ConcurrentPollsDoNotOverlap()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fillCalls = 0;
        using var fixture = new Fixture(async (request, _) =>
        {
            if (IsToken(request)) return Token();
            if (request.RequestUri!.AbsolutePath.EndsWith("/order-cash", StringComparison.Ordinal))
                return Json("""{"rt_cd":"0","output":{"ODNO":"KR-OVERLAP","KRX_FWDG_ORD_ORGNO":"ORG"}}""");
            if (request.RequestUri.AbsolutePath.EndsWith("/inquire-daily-ccld", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref fillCalls);
                entered.TrySetResult();
                await release.Task;
                return KrFills("""{"odno":"KR-OVERLAP","pdno":"069500","ord_qty":"1","tot_ccld_qty":"0","ord_unpr":"99","avg_prvs":"0","sll_buy_dvsn_cd":"02"}""");
            }
            throw new InvalidOperationException(request.RequestUri.AbsolutePath);
        });
        using var brokerage = fixture.Brokerage(KisMarketScope.Kr);
        brokerage.EnableFillPolling();
        Assert.True(brokerage.PlaceOrder(Limit("069500", Market.KRX, 1, 99m)));

        var first = brokerage.PollFillsOnceAsync();
        await entered.Task;
        await brokerage.PollFillsOnceAsync();
        Assert.Equal(1, Volatile.Read(ref fillCalls));
        release.TrySetResult();
        await first;
    }

    [Fact]
    public async Task TransientPeriodicFillPollFailureIsWarningAndCanRetry()
    {
        var calls = 0;
        using var fixture = new Fixture((request, _) =>
        {
            if (IsToken(request)) return Task.FromResult(Token());
            if (request.RequestUri!.AbsolutePath.EndsWith("/order-cash", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"rt_cd":"0","output":{"ODNO":"KR-RETRY","KRX_FWDG_ORD_ORGNO":"ORG"}}"""));
            if (request.RequestUri.AbsolutePath.EndsWith("/inquire-daily-ccld", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("temporary");
                return Task.FromResult(KrFills("""{"odno":"KR-RETRY","pdno":"069500","ord_qty":"1","tot_ccld_qty":"1","ord_unpr":"99","avg_prvs":"99","sll_buy_dvsn_cd":"02"}"""));
            }
            throw new InvalidOperationException(request.RequestUri.AbsolutePath);
        });
        using var brokerage = fixture.Brokerage(KisMarketScope.Kr);
        brokerage.EnableFillPolling();
        var messages = new List<BrokerageMessageEvent>();
        var events = new List<OrderEvent>();
        brokerage.Message += (_, message) => messages.Add(message);
        brokerage.OrdersStatusChanged += (_, batch) => events.AddRange(batch);
        Assert.True(brokerage.PlaceOrder(Limit("069500", Market.KRX, 1, 99m)));

        await brokerage.PollFillsOnceAsync();
        await brokerage.PollFillsOnceAsync();

        Assert.Contains(messages, x => x.Code == "KIS_FILL_POLL_ERROR" && x.Type == BrokerageMessageType.Warning);
        Assert.Contains(events, x => x.Status == OrderStatus.Filled && x.FillQuantity == 1m);
    }

    [Fact]
    public void UsTimedOutCancelIsReconciledWithoutReplayingPost()
    {
        var cancelPosts = 0;
        using var fixture = new Fixture(async (request, ct) =>
        {
            if (IsToken(request)) return Token();
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/trading/order", StringComparison.Ordinal))
                return Json("""{"rt_cd":"0","output":{"ODNO":"US-CANCEL"}}""");
            if (path.EndsWith("/trading/order-rvsecncl", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref cancelPosts);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                return Json("""{"rt_cd":"0","output":{"ODNO":"US-CANCEL-ACK"}}""");
            }
            if (path.EndsWith("/trading/inquire-nccs", StringComparison.Ordinal))
                return Json("""{"rt_cd":"0","output":[]}""");
            if (path.EndsWith("/trading/inquire-ccnl", StringComparison.Ordinal))
                return UsFills("");
            throw new InvalidOperationException(path);
        });
        using var brokerage = fixture.Brokerage(KisMarketScope.Us);
        brokerage.EnableFillPolling();
        var events = new List<OrderEvent>();
        brokerage.OrdersStatusChanged += (_, batch) => events.AddRange(batch);
        var order = Limit("AAPL", Market.USA, 1, 99m);

        Assert.True(brokerage.PlaceOrder(order));
        Assert.True(brokerage.CancelOrder(order));

        Assert.Equal(1, cancelPosts);
        Assert.Equal([OrderStatus.Submitted, OrderStatus.Canceled], events.Select(x => x.Status));
    }

    [Theory]
    [InlineData(KisMarketScope.Kr)]
    [InlineData(KisMarketScope.Us)]
    public async Task FillPollingNeverCallsOppositeMarket(KisMarketScope scope)
    {
        var paths = new List<string>();
        using var fixture = new Fixture((request, _) =>
        {
            if (IsToken(request)) return Task.FromResult(Token());
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            if (path.EndsWith("/order-cash", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"rt_cd":"0","output":{"ODNO":"KR-SCOPE","KRX_FWDG_ORD_ORGNO":"ORG"}}"""));
            if (path.EndsWith("/trading/order", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"rt_cd":"0","output":{"ODNO":"US-SCOPE"}}"""));
            return Task.FromResult(path.EndsWith("/inquire-daily-ccld", StringComparison.Ordinal)
                ? KrFills("")
                : UsFills(""));
        });
        using var brokerage = fixture.Brokerage(scope);
        brokerage.EnableFillPolling();
        var market = scope == KisMarketScope.Kr ? Market.KRX : Market.USA;
        var ticker = scope == KisMarketScope.Kr ? "069500" : "AAPL";
        Assert.True(brokerage.PlaceOrder(Limit(ticker, market, 1, 99m)));
        paths.Clear();

        await brokerage.PollFillsOnceAsync();

        Assert.Single(paths);
        Assert.Equal(scope == KisMarketScope.Kr, paths[0].Contains("domestic-stock", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FillPollingWithNoTrackedOrdersMakesNoHttpCalls()
    {
        var calls = 0;
        using var fixture = new Fixture((_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("No request was expected.");
        });
        using var brokerage = fixture.Brokerage(KisMarketScope.Us);
        brokerage.EnableFillPolling();

        await brokerage.PollFillsOnceAsync();

        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Theory]
    [InlineData(KisMarketScope.Kr)]
    [InlineData(KisMarketScope.Us)]
    public async Task RestartedPartialOrderSeedsBaselineAndEmitsOnlyNewDelta(KisMarketScope scope)
    {
        var fillQuery = 0;
        using var fixture = new Fixture((request, _) =>
        {
            if (IsToken(request)) return Task.FromResult(Token());
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/inquire-psbl-rvsecncl", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"rt_cd":"0","output":[{"odno":"KR-IMPORT","pdno":"069500","ord_qty":"10","tot_ccld_qty":"2","psbl_qty":"8","ord_unpr":"99","ord_gno_brno":"ORG","sll_buy_dvsn_cd":"02"}]}"""));
            if (path.EndsWith("/inquire-nccs", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"rt_cd":"0","output":[{"odno":"US-IMPORT","pdno":"AAPL","ft_ord_qty":"10","ft_ccld_qty":"2","ft_ord_unpr3":"99","ovrs_excg_cd":"NASD","sll_buy_dvsn_cd":"02"}]}"""));
            if (path.EndsWith("/inquire-daily-ccld", StringComparison.Ordinal))
            {
                var n = Interlocked.Increment(ref fillQuery);
                var qty = n <= 2 ? 2 : n == 3 ? 3 : 10;
                return Task.FromResult(KrFills($$"""{"odno":"KR-IMPORT","pdno":"069500","ord_qty":"10","tot_ccld_qty":"{{qty}}","ord_unpr":"99","avg_prvs":"100","sll_buy_dvsn_cd":"02"}"""));
            }
            if (path.EndsWith("/inquire-ccnl", StringComparison.Ordinal))
            {
                var n = Interlocked.Increment(ref fillQuery);
                var qty = n <= 2 ? 2 : n == 3 ? 3 : 10;
                return Task.FromResult(UsFills(UsRow("US-IMPORT", "", 10, qty, 100m)));
            }
            throw new InvalidOperationException(path);
        });
        using var brokerage = fixture.Brokerage(scope);
        brokerage.EnableFillPolling();
        var events = new List<OrderEvent>();
        brokerage.OrdersStatusChanged += (_, batch) => events.AddRange(batch);

        var imported = Assert.Single(brokerage.GetOpenOrders()); // baseline query sees 2/10
        Assert.Equal(8m, Math.Abs(imported.Quantity));
        await brokerage.PollFillsOnceAsync(); // unchanged 2/10: no historical replay
        await brokerage.PollFillsOnceAsync(); // cumulative 3: delta 1
        await brokerage.PollFillsOnceAsync(); // cumulative 10: remaining delta 7

        Assert.Equal([1m, 7m], events.Select(x => Math.Abs(x.FillQuantity)));
        Assert.Equal([OrderStatus.PartiallyFilled, OrderStatus.Filled], events.Select(x => x.Status));
    }

    [Fact]
    public async Task FullFillRacingRestartBaselineIsEmittedOnFirstPostSetupPoll()
    {
        var fillQuery = 0;
        using var fixture = new Fixture((request, _) =>
        {
            if (IsToken(request)) return Task.FromResult(Token());
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/inquire-psbl-rvsecncl", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"rt_cd":"0","output":[{"odno":"KR-RACE-IMPORT","pdno":"069500","ord_qty":"10","tot_ccld_qty":"2","psbl_qty":"8","ord_unpr":"99","ord_gno_brno":"ORG","sll_buy_dvsn_cd":"02"}]}"""));
            if (path.EndsWith("/inquire-daily-ccld", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref fillQuery);
                return Task.FromResult(KrFills("""{"odno":"KR-RACE-IMPORT","pdno":"069500","ord_qty":"10","tot_ccld_qty":"10","ord_unpr":"99","avg_prvs":"100","sll_buy_dvsn_cd":"02"}"""));
            }
            throw new InvalidOperationException(path);
        });
        using var brokerage = fixture.Brokerage(KisMarketScope.Kr);
        brokerage.EnableFillPolling();
        var events = new List<OrderEvent>();
        brokerage.OrdersStatusChanged += (_, batch) => events.AddRange(batch);

        brokerage.GetOpenOrders(); // baseline response advanced 2 -> 10; defer until post-setup poll
        Assert.Empty(events);
        await brokerage.PollFillsOnceAsync();

        var fill = Assert.Single(events);
        Assert.Equal(OrderStatus.Filled, fill.Status);
        Assert.Equal(8m, fill.FillQuantity);
        Assert.Equal(2, fillQuery);
    }

    [Fact]
    public void DisposeStopsFillTimer()
    {
        var fillCalls = 0;
        using var fixture = new Fixture((request, _) =>
        {
            if (IsToken(request)) return Task.FromResult(Token());
            Interlocked.Increment(ref fillCalls);
            return Task.FromResult(KrFills(""));
        });
        var brokerage = fixture.Brokerage(KisMarketScope.Kr);
        brokerage.EnableFillPolling(TimeSpan.FromHours(1));
        brokerage.Connect();
        Assert.True(brokerage.IsFillPollTimerActive);
        brokerage.Dispose();
        Assert.False(brokerage.IsFillPollTimerActive);
        Assert.Equal(0, Volatile.Read(ref fillCalls));
        Assert.False(brokerage.IsConnected);
    }

    [Fact]
    public async Task DisposeDrainsInFlightFillPollBeforeDisposingClients()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (request, _) =>
        {
            if (IsToken(request)) return Token();
            if (request.RequestUri!.AbsolutePath.EndsWith("/order-cash", StringComparison.Ordinal))
                return Json("""{"rt_cd":"0","output":{"ODNO":"KR-DISPOSE","KRX_FWDG_ORD_ORGNO":"ORG"}}""");
            entered.TrySetResult();
            await release.Task;
            return KrFills("");
        });
        var brokerage = fixture.Brokerage(KisMarketScope.Kr);
        brokerage.EnableFillPolling();
        BrokerageMessageEvent? error = null;
        brokerage.Message += (_, message) => error = message.Type == BrokerageMessageType.Error ? message : error;
        Assert.True(brokerage.PlaceOrder(Limit("069500", Market.KRX, 1, 99m)));

        var poll = brokerage.PollFillsOnceAsync();
        await entered.Task;
        var dispose = Task.Run(brokerage.Dispose);
        await Task.Delay(30);
        Assert.False(dispose.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(poll, dispose).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(error);
        Assert.False(brokerage.IsFillPollTimerActive);
    }

    [Fact]
    public async Task FillQueryFailurePublishesRetryableWarning()
    {
        using var fixture = new Fixture((request, _) => Task.FromResult(IsToken(request)
            ? Token()
            : request.RequestUri!.AbsolutePath.EndsWith("/order-cash", StringComparison.Ordinal)
                ? Json("""{"rt_cd":"0","output":{"ODNO":"KR-FAIL","KRX_FWDG_ORD_ORGNO":"ORG"}}""")
                : Json("""{"rt_cd":"1","msg1":"failed"}""")));
        using var brokerage = fixture.Brokerage(KisMarketScope.Kr);
        brokerage.EnableFillPolling();
        BrokerageMessageEvent? message = null;
        brokerage.Message += (_, value) => message = value;
        Assert.True(brokerage.PlaceOrder(Limit("069500", Market.KRX, 1, 99m)));

        await brokerage.PollFillsOnceAsync();

        Assert.NotNull(message);
        Assert.Equal(BrokerageMessageType.Warning, message.Type);
        Assert.Equal("KIS_FILL_POLL_ERROR", message.Code);
    }

    [Fact]
    public async Task FillPollPausesCompetingQuotesAndResumesAfterQuery()
    {
        KisQuotePollingControl.ResetForTests();
        var pausedDuringFill = false;
        using var fixture = new Fixture((request, _) =>
        {
            if (IsToken(request)) return Task.FromResult(Token());
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/order-cash", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"rt_cd":"0","output":{"ODNO":"KR-PRIORITY","KRX_FWDG_ORD_ORGNO":"ORG"}}"""));
            if (path.EndsWith("/inquire-daily-ccld", StringComparison.Ordinal))
            {
                pausedDuringFill = !KisQuotePollingControl.IsEnabled(KisMarketScope.Kr);
                return Task.FromResult(KrFills(""));
            }
            throw new InvalidOperationException(path);
        });
        using var brokerage = fixture.Brokerage(KisMarketScope.Kr);
        brokerage.EnableFillPolling();
        Assert.True(brokerage.PlaceOrder(Limit("069500", Market.KRX, 1, 99m)));

        await brokerage.PollFillsOnceAsync();

        Assert.True(pausedDuringFill);
        Assert.True(KisQuotePollingControl.IsEnabled(KisMarketScope.Kr));
        KisQuotePollingControl.ResetForTests();
    }

    private static LimitOrder Limit(string ticker, string market, decimal quantity, decimal price) =>
        new(new Symbol(SecurityIdentifier.GenerateEquity(ticker, market, false, null, null), ticker),
            quantity, price, DateTime.UtcNow);

    private static LimitOrder Replacement(LimitOrder existing, decimal price)
    {
        var replacement = new LimitOrder(existing.Symbol, existing.Quantity, price, DateTime.UtcNow);
        foreach (var id in existing.BrokerId) replacement.BrokerId.Add(id);
        return replacement;
    }

    private static bool IsToken(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.EndsWith("/oauth2/tokenP", StringComparison.Ordinal);

    private static HttpResponseMessage Token() =>
        Json("""{"access_token":"token","token_type":"Bearer","expires_in":86400}""");

    private static HttpResponseMessage KrFills(string rows) =>
        Json("{\"rt_cd\":\"0\",\"output1\":[" + rows + "]}");

    private static HttpResponseMessage UsFills(string rows) =>
        Json("{\"rt_cd\":\"0\",\"output\":[" + rows + "],\"ctx_area_nk200\":\"\",\"ctx_area_fk200\":\"\"}");

    private static string UsRow(string orderNo, string original, int quantity, int filled, decimal average) =>
        "{\"odno\":\"" + orderNo + "\",\"orgn_odno\":\"" + original +
        "\",\"pdno\":\"AAPL\",\"ovrs_excg_cd\":\"NASD\",\"sll_buy_dvsn_cd\":\"02\",\"ft_ord_qty\":\"" +
        quantity + "\",\"ft_ccld_qty\":\"" + filled + "\",\"ft_ord_unpr3\":\"99\",\"ft_ccld_unpr3\":\"" +
        average.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"}";

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _http;
        private readonly KisKrOAuthClient _oauth;
        private readonly KisKrTradingClient _kr;
        private readonly KisUsTradingClient _us;
        private readonly IKisCallGate _gate;

        public Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            var config = new KisKrOAuthConfig(
                "https://fill.test", $"key-{Guid.NewGuid():N}", "secret", "00000000", "01");
            _http = new HttpClient(new Handler(send)) { BaseAddress = new Uri(config.BaseUrl) };
            _oauth = new KisKrOAuthClient(config, _http, isPaper: false);
            var gate = new KisCallGate(TimeSpan.Zero);
            _gate = gate;
            var options = new KisOrderRequestOptions(
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.Zero);
            _kr = new KisKrTradingClient(config, _oauth, _http, true, gate, options);
            _us = new KisUsTradingClient(config, _oauth, _http, true, gate, options);
        }

        public KisBrokerage Brokerage(KisMarketScope scope) =>
            new(_oauth, _kr, _us, ensureKrxMarketData: false, scope, _gate);

        public KisUsTradingClient Us => _us;

        public void Dispose()
        {
            _kr.Dispose();
            _us.Dispose();
            _oauth.Dispose();
            _http.Dispose();
        }
    }

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
