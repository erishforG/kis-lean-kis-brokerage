using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using QuantConnect.KisBrokerage.Client;
using QuantConnect.Brokerages;
using QuantConnect.Orders;
using Xunit;

namespace QuantConnect.KisBrokerage.Tests;

public sealed class KisOrderLifecycleTests
{
    private static readonly KisOrderRequestOptions FastOptions = new(
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromMilliseconds(10));

    private static readonly KisOrderRequestOptions TimeoutOptions = new(
        TimeSpan.FromMilliseconds(75),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromMilliseconds(10));

    [Fact]
    public void KrSuccessfulPlacementPublishesSubmittedAndSupportsModifyCancel()
    {
        var modifyCount = 0;
        using var fixture = new ClientFixture((request, _) =>
        {
            if (IsToken(request)) return Task.FromResult(TokenResponse());
            if (request.RequestUri!.AbsolutePath.EndsWith("/order-cash"))
            {
                return Task.FromResult(JsonResponse(
                    """{"rt_cd":"0","msg_cd":"0","output":{"ODNO":"KR-100","ORD_TMD":"101500","KRX_FWDG_ORD_ORGNO":"ORG-1"}}"""));
            }

            modifyCount++;
            return Task.FromResult(JsonResponse(
                "{\"rt_cd\":\"0\",\"msg_cd\":\"0\",\"output\":{\"ODNO\":\"KR-" +
                (100 + modifyCount) + "\",\"ORD_TMD\":\"101501\"}}"));
        });

        using var brokerage = fixture.CreateBrokerage();
        var events = new List<OrderEvent>();
        brokerage.OrdersStatusChanged += (_, batch) => events.AddRange(batch);
        var order = NewLimitOrder("067290", Market.KRX, 1, 2860m);

        Assert.True(brokerage.PlaceOrder(order));
        Assert.Equal("KR-100", Assert.Single(order.BrokerId));
        Assert.Equal(OrderStatus.Submitted, Assert.Single(events).Status);

        order = ReplacementOrder(order, 2800m);
        Assert.True(brokerage.UpdateOrder(order));
        Assert.Equal("KR-101", order.BrokerId[^1]);
        Assert.Equal(OrderStatus.UpdateSubmitted, events[^1].Status);

        Assert.True(brokerage.CancelOrder(order));
        Assert.Equal(OrderStatus.Canceled, events[^1].Status);
        Assert.Equal(3, fixture.Gate.ExecutionCount);
    }

    [Fact]
    public void UsSuccessfulPlacementHasKrLifecycleParity()
    {
        var modifyCount = 0;
        using var fixture = new ClientFixture((request, _) =>
        {
            if (IsToken(request)) return Task.FromResult(TokenResponse());
            if (request.RequestUri!.AbsolutePath.EndsWith("/trading/order"))
            {
                return Task.FromResult(JsonResponse(
                    """{"rt_cd":"0","msg_cd":"0","output":{"ODNO":"US-100","ORD_TMD":"101500"}}"""));
            }

            modifyCount++;
            return Task.FromResult(JsonResponse(
                "{\"rt_cd\":\"0\",\"msg_cd\":\"0\",\"output\":{\"ODNO\":\"US-" +
                (100 + modifyCount) + "\",\"ORD_TMD\":\"101501\"}}"));
        });

        using var brokerage = fixture.CreateBrokerage();
        var events = new List<OrderEvent>();
        brokerage.OrdersStatusChanged += (_, batch) => events.AddRange(batch);
        var order = NewLimitOrder("AAPL", Market.USA, 1, 150.25m);

        Assert.True(brokerage.PlaceOrder(order));
        Assert.Equal("US-100", Assert.Single(order.BrokerId));
        Assert.Equal(OrderStatus.Submitted, Assert.Single(events).Status);

        order = ReplacementOrder(order, 149.75m);
        Assert.True(brokerage.UpdateOrder(order));
        Assert.Equal("US-101", order.BrokerId[^1]);
        Assert.Equal(OrderStatus.UpdateSubmitted, events[^1].Status);

        Assert.True(brokerage.CancelOrder(order));
        Assert.Equal(OrderStatus.Canceled, events[^1].Status);
        Assert.Equal(3, fixture.Gate.ExecutionCount);
    }

    [Fact]
    public void ModifySuccessWithoutReplacementIdKeepsOldTrackingAndFailsClosed()
    {
        var modifyAttempted = false;
        string? cancelBody = null;
        using var fixture = new ClientFixture(async (request, _) =>
        {
            if (IsToken(request)) return TokenResponse();
            if (request.RequestUri!.AbsolutePath.EndsWith("/trading/order", StringComparison.Ordinal))
                return JsonResponse("""{"rt_cd":"0","output":{"ODNO":"US-OLD"}}""");

            var body = await request.Content!.ReadAsStringAsync();
            if (!modifyAttempted)
            {
                modifyAttempted = true;
                return JsonResponse("""{"rt_cd":"0","output":{}}""");
            }
            cancelBody = body;
            return JsonResponse("""{"rt_cd":"0","output":{"ODNO":"US-CANCELED"}}""");
        });
        using var brokerage = fixture.CreateBrokerage();
        BrokerageMessageEvent? guardMessage = null;
        brokerage.Message += (_, message) => guardMessage = message;
        var order = NewLimitOrder("AAPL", Market.USA, 1, 150m);
        Assert.True(brokerage.PlaceOrder(order));

        var replacement = ReplacementOrder(order, 149m);
        Assert.False(brokerage.UpdateOrder(replacement));
        Assert.Equal(BrokerageMessageType.Error, guardMessage?.Type);
        Assert.Equal("KIS_ORDER_ID_MISSING", guardMessage?.Code);
        Assert.Equal(["US-OLD"], replacement.BrokerId);
        Assert.True(brokerage.CancelOrder(replacement));
        Assert.Contains("\"ORGN_ODNO\":\"US-OLD\"", cancelBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KrAndUsPlacementShareOneRateLimitGate()
    {
        var orderStarts = new ConcurrentQueue<long>();
        var sharedGate = new KisCallGate(TimeSpan.FromMilliseconds(60));
        using var fixture = new ClientFixture(async (request, _) =>
        {
            if (IsToken(request)) return TokenResponse();
            orderStarts.Enqueue(Stopwatch.GetTimestamp());
            await Task.Yield();
            var orderNo = request.RequestUri!.AbsolutePath.Contains("domestic", StringComparison.Ordinal)
                ? "KR-GATED"
                : "US-GATED";
            return JsonResponse(
                "{\"rt_cd\":\"0\",\"output\":{\"ODNO\":\"" + orderNo +
                "\",\"ORD_TMD\":\"101500\",\"KRX_FWDG_ORD_ORGNO\":\"ORG\"}}");
        }, sharedGate);

        await fixture.OAuth.GetAccessTokenAsync();
        var kr = fixture.Kr.PlaceLimitOrderAsync("buy", "067290", 1, 2860m);
        var us = fixture.Us.PlaceLimitOrderAsync("buy", "AAPL", 1, 150.25m);
        var results = await Task.WhenAll(kr.ContinueWith(t => t.Result.Ok), us.ContinueWith(t => t.Result.Ok));

        Assert.All(results, Assert.True);
        var starts = orderStarts.Order().ToArray();
        Assert.Equal(2, starts.Length);
        Assert.True(Stopwatch.GetElapsedTime(starts[0], starts[1]) >= TimeSpan.FromMilliseconds(45));
    }

    [Fact]
    public async Task KrTimeoutRecoversAcceptedOrderWithoutReposting()
    {
        var postCount = 0;
        var nowKst = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Asia/Seoul");
        using var fixture = new ClientFixture(async (request, ct) =>
        {
            if (IsToken(request)) return TokenResponse();
            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref postCount);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return JsonResponse($$"""
                {"rt_cd":"0","output":[{"odno":"KR-RECOVERED","pdno":"067290","prdt_name":"test","ord_qty":"1","tot_ccld_qty":"0","psbl_qty":"1","ord_unpr":"2860","ord_tmd":"{{nowKst:HHmmss}}","ord_gno_brno":"ORG-RECOVERED","sll_buy_dvsn_cd":"02"}]}
                """);
        }, options: TimeoutOptions);

        var result = await fixture.Kr.PlaceLimitOrderAsync("buy", "067290", 1, 2860m);

        Assert.True(result.Ok);
        Assert.True(result.Reconciled);
        Assert.Equal("KR-RECOVERED", result.OrderNo);
        Assert.Equal("ORG-RECOVERED", result.OrgNo);
        Assert.Equal(1, postCount);
    }

    [Fact]
    public void KrIncompleteSuccessRecoversFromDailyHistoryAndUsesOrgNoForModifyCancel()
    {
        var nowKst = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Asia/Seoul");
        var modifyCancelBodies = new List<string>();
        using var fixture = new ClientFixture(async (request, _) =>
        {
            if (IsToken(request)) return TokenResponse();

            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/order-cash", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """{"rt_cd":"0","msg_cd":"0","output":{"ODNO":"KR-INCOMPLETE","ORD_TMD":"101500"}}""");
            }

            if (path.EndsWith("/inquire-psbl-rvsecncl", StringComparison.Ordinal))
            {
                return JsonResponse("""{"rt_cd":"0","output":[]}""");
            }

            if (path.EndsWith("/inquire-daily-ccld", StringComparison.Ordinal))
            {
                return JsonResponse($$"""
                    {"rt_cd":"0","output1":[{"odno":"KR-DAILY","pdno":"067290","prdt_name":"test","ord_qty":"1","tot_ccld_qty":"0","rmn_qty":"1","ord_unpr":"2860","avg_prvs":"0","sll_buy_dvsn_cd":"02","ord_dt":"{{nowKst:yyyyMMdd}}","ord_tmd":"{{nowKst:HHmmss}}","ord_gno_brno":"ORG-DAILY"}]}
                    """);
            }

            var body = await request.Content!.ReadAsStringAsync();
            modifyCancelBodies.Add(body);
            var replacementOrderNo = body.Contains("\"RVSE_CNCL_DVSN_CD\":\"01\"", StringComparison.Ordinal)
                ? "KR-MODIFIED"
                : "KR-CANCELED";
            return JsonResponse(
                "{\"rt_cd\":\"0\",\"msg_cd\":\"0\",\"output\":{\"ODNO\":\"" +
                replacementOrderNo + "\",\"ORD_TMD\":\"" + nowKst.ToString("HHmmss") + "\"}}");
        });

        using var brokerage = fixture.CreateBrokerage();
        var order = NewLimitOrder("067290", Market.KRX, 1, 2860m);

        Assert.True(brokerage.PlaceOrder(order));
        Assert.Equal("KR-DAILY", Assert.Single(order.BrokerId));

        order = ReplacementOrder(order, 2800m);
        Assert.True(brokerage.UpdateOrder(order));
        Assert.True(brokerage.CancelOrder(order));

        Assert.Equal(2, modifyCancelBodies.Count);
        Assert.All(modifyCancelBodies, body =>
            Assert.Contains("\"KRX_FWDG_ORD_ORGNO\":\"ORG-DAILY\"", body, StringComparison.Ordinal));
        Assert.Contains("\"ORGN_ODNO\":\"KR-DAILY\"", modifyCancelBodies[0], StringComparison.Ordinal);
        Assert.Contains("\"ORGN_ODNO\":\"KR-MODIFIED\"", modifyCancelBodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsTimeoutRecoversAcceptedOrderWithoutReposting()
    {
        var postCount = 0;
        var nowKst = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Asia/Seoul");
        using var fixture = new ClientFixture(async (request, ct) =>
        {
            if (IsToken(request)) return TokenResponse();
            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref postCount);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return JsonResponse($$"""
                {"rt_cd":"0","output":[{"odno":"US-RECOVERED","pdno":"AAPL","prdt_name":"test","sll_buy_dvsn_cd":"02","ft_ord_qty":"1","ft_ccld_qty":"0","ft_ord_unpr3":"150.25","ord_dt":"{{nowKst:yyyyMMdd}}","ord_tmd":"{{nowKst:HHmmss}}","ovrs_excg_cd":"NASD"}]}
                """);
        }, options: TimeoutOptions);

        var result = await fixture.Us.PlaceLimitOrderAsync("buy", "AAPL", 1, 150.25m);

        Assert.True(result.Ok);
        Assert.True(result.Reconciled);
        Assert.Equal("US-RECOVERED", result.OrderNo);
        Assert.Equal(1, postCount);
    }

    private static LimitOrder NewLimitOrder(string ticker, string market, decimal quantity, decimal price) =>
        new(
            new Symbol(SecurityIdentifier.GenerateEquity(ticker, market, mapSymbol: false, null, null), ticker),
            quantity, price, DateTime.UtcNow);

    private static LimitOrder ReplacementOrder(LimitOrder existing, decimal price)
    {
        var replacement = new LimitOrder(existing.Symbol, existing.Quantity, price, DateTime.UtcNow);
        foreach (var brokerId in existing.BrokerId)
        {
            replacement.BrokerId.Add(brokerId);
        }
        return replacement;
    }

    private static bool IsToken(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.EndsWith("/oauth2/tokenP", StringComparison.Ordinal);

    private static HttpResponseMessage TokenResponse() =>
        JsonResponse("""{"access_token":"test-token","token_type":"Bearer","expires_in":86400}""");

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class ClientFixture : IDisposable
    {
        private readonly HttpClient _http;

        public ClientFixture(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
            IKisCallGate? gate = null,
            KisOrderRequestOptions? options = null)
        {
            Gate = gate as RecordingCallGate ?? new RecordingCallGate(gate ?? new KisCallGate(TimeSpan.Zero));
            var config = new KisKrOAuthConfig(
                "https://kis.test", $"test-key-{Guid.NewGuid():N}", "test-secret", "00000000", "01");
            _http = new HttpClient(new StubHttpMessageHandler(send)) { BaseAddress = new Uri(config.BaseUrl) };
            OAuth = new KisKrOAuthClient(config, _http, isPaper: false);
            Kr = new KisKrTradingClient(config, OAuth, _http, true, Gate, options ?? FastOptions);
            Us = new KisUsTradingClient(config, OAuth, _http, true, Gate, options ?? FastOptions);
        }

        public RecordingCallGate Gate { get; }
        public KisKrOAuthClient OAuth { get; }
        public KisKrTradingClient Kr { get; }
        public KisUsTradingClient Us { get; }

        public KisBrokerage CreateBrokerage() => new(OAuth, Kr, Us, ensureKrxMarketData: false);

        public void Dispose()
        {
            Kr.Dispose();
            Us.Dispose();
            OAuth.Dispose();
            _http.Dispose();
        }
    }

    private sealed class RecordingCallGate(IKisCallGate inner) : IKisCallGate
    {
        private int _executionCount;
        public int ExecutionCount => _executionCount;

        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _executionCount);
            return inner.ExecuteAsync(call, ct);
        }
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
