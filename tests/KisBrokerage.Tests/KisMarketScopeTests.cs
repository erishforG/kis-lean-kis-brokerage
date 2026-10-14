using System.Collections.Concurrent;
using System.Net;
using System.Text;
using QuantConnect.KisBrokerage.Client;
using QuantConnect.Data.Market;
using QuantConnect.Data;
using QuantConnect.Orders;
using QuantConnect.Securities;
using NodaTime;
using Xunit;

namespace QuantConnect.KisBrokerage.Tests;

public sealed class KisMarketScopeTests
{
    [Fact]
    public void SettlementPolicyReusesKrSaleProceedsButPreservesUsDelay()
    {
        Assert.True(KisBrokerageModel.UsesImmediateSettlement(Market.KRX));
        Assert.False(KisBrokerageModel.UsesImmediateSettlement(Market.USA));
    }

    [Fact]
    public void QuotePollingDefaultsEnabledAndPauseIsExactScope()
    {
        KisQuotePollingControl.ResetForTests();
        try
        {
            Assert.True(KisQuotePollingControl.IsEnabled(KisMarketScope.Kr));
            Assert.True(KisQuotePollingControl.IsEnabled(KisMarketScope.Us));

            KisQuotePollingControl.Pause(KisMarketScope.Us);

            Assert.True(KisQuotePollingControl.IsEnabled(KisMarketScope.Kr));
            Assert.False(KisQuotePollingControl.IsEnabled(KisMarketScope.Us));
            KisQuotePollingControl.Resume(KisMarketScope.Us);
            Assert.True(KisQuotePollingControl.IsEnabled(KisMarketScope.Us));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                KisQuotePollingControl.Pause(KisMarketScope.Both));
        }
        finally
        {
            KisQuotePollingControl.ResetForTests();
        }
    }

    [Fact]
    public void QuotePollingCriticalLeaseCancelsGenerationAndRespectsShutdownPause()
    {
        KisQuotePollingControl.ResetForTests();
        try
        {
            var firstGeneration = KisQuotePollingControl.CurrentToken(KisMarketScope.Us);
            using (KisQuotePollingControl.AcquirePause(KisMarketScope.Us))
            {
                Assert.False(KisQuotePollingControl.IsEnabled(KisMarketScope.Us));
                Assert.True(firstGeneration.IsCancellationRequested);
                KisQuotePollingControl.Pause(KisMarketScope.Us);
            }

            Assert.False(KisQuotePollingControl.IsEnabled(KisMarketScope.Us));
            KisQuotePollingControl.Resume(KisMarketScope.Us);
            Assert.True(KisQuotePollingControl.IsEnabled(KisMarketScope.Us));
            Assert.False(KisQuotePollingControl.CurrentToken(KisMarketScope.Us).IsCancellationRequested);
        }
        finally
        {
            KisQuotePollingControl.ResetForTests();
        }
    }

    [Fact]
    public void RegistryConditionalUnregisterCannotClearNewerBrokerage()
    {
        KisBrokerageRuntimeRegistry.ClearForTests();
        var old = new SnapshotProvider(KisMarketScope.Us);
        var current = new SnapshotProvider(KisMarketScope.Us);
        KisBrokerageRuntimeRegistry.RegisterTradingBrokerage(old);
        KisBrokerageRuntimeRegistry.RegisterTradingBrokerage(current);

        KisBrokerageRuntimeRegistry.UnregisterTradingBrokerage(old);
        Assert.Same(current, KisBrokerageRuntimeRegistry.Resolve(KisMarketScope.Us));
        KisBrokerageRuntimeRegistry.UnregisterTradingBrokerage(current);
        Assert.Null(KisBrokerageRuntimeRegistry.Resolve(KisMarketScope.Us));
    }

    [Fact]
    public void KrScopeAccountMethodsNeverCallOverseasEndpoints()
    {
        using var fixture = new ScopeFixture(KisMarketScope.Kr);
        fixture.Brokerage.GetCashBalance();
        fixture.Brokerage.GetAccountHoldings();
        fixture.Brokerage.GetOpenOrders();
        Assert.Contains(fixture.Paths, x => x.Contains("/domestic-stock/", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Paths, x => x.Contains("/overseas-stock/", StringComparison.Ordinal));
    }

    [Fact]
    public void UsScopeAccountMethodsNeverCallDomesticEndpoints()
    {
        using var fixture = new ScopeFixture(KisMarketScope.Us);
        fixture.Brokerage.GetCashBalance();
        fixture.Brokerage.GetAccountHoldings();
        fixture.Brokerage.GetOpenOrders();
        Assert.Contains(fixture.Paths, x => x.Contains("/overseas-stock/", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Paths, x => x.Contains("/domestic-stock/", StringComparison.Ordinal));
    }

    [Fact]
    public void UsCompositeSnapshotReadsBalanceOnlyOnce()
    {
        using var fixture = new ScopeFixture(KisMarketScope.Us);

        var snapshot = fixture.Brokerage.ReadBrokerSnapshot();

        Assert.Equal(KisMarketScope.Us, snapshot.Scope);
        Assert.Equal(3, fixture.Paths.Count(x =>
            x == "/uapi/overseas-stock/v1/trading/inquire-balance"));
        Assert.Equal(1, fixture.Paths.Count(x =>
            x == "/uapi/overseas-stock/v1/trading/inquire-present-balance"));
        Assert.Equal(1, fixture.Paths.Count(x =>
            x == "/uapi/overseas-stock/v1/trading/inquire-nccs"));
    }

    [Fact]
    public void UsPaperBalanceReconstructsZeroUsdCashFromKrwNavWithExistingHolding()
    {
        using var fixture = new ScopeFixture(KisMarketScope.Us, (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/oauth2/tokenP", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(
                    "{\"access_token\":\"scope-token\",\"token_type\":\"Bearer\",\"expires_in\":86400}"));
            if (path.EndsWith("/inquire-balance", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(
                    "{\"rt_cd\":\"0\",\"output1\":[{\"ovrs_pdno\":\"SPY\",\"ovrs_cblc_qty\":\"1\",\"ovrs_stck_evlu_amt\":\"760\"}]," +
                    "\"output2\":{\"tot_evlu_pfls_amt\":\"-375000\"}}"));
            if (path.EndsWith("/inquire-present-balance", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(
                    "{\"rt_cd\":\"0\",\"output2\":[{\"crcy_cd\":\"USD\",\"frcr_dncl_amt_2\":\"0\",\"frst_bltn_exrt\":\"1500\"}]," +
                    "\"output3\":{\"crcy_cd\":\"USD\",\"frcr_use_psbl_amt\":\"0\",\"tot_asst_amt\":\"375000000\"}}"));
            return Task.FromResult(JsonResponse("{\"rt_cd\":\"0\",\"output\":[]}"));
        });

        var snapshot = fixture.Brokerage.ReadBrokerSnapshot();

        Assert.Equal(249_240m, Assert.Single(snapshot.Cash).Amount);
        Assert.Equal(1m, Assert.Single(snapshot.Holdings).Quantity);
        Assert.Equal(250_000m, snapshot.TotalValue);
    }

    [Fact]
    public void UsSnapshotUsesReportedNavInsteadOfSettlementCashPlusHoldings()
    {
        using var fixture = new ScopeFixture(KisMarketScope.Us, (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/oauth2/tokenP", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(
                    "{\"access_token\":\"scope-token\",\"token_type\":\"Bearer\",\"expires_in\":86400}"));
            if (path.EndsWith("/inquire-balance", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(
                    "{\"rt_cd\":\"0\",\"output1\":[{\"ovrs_pdno\":\"SPY\",\"ovrs_cblc_qty\":\"1\",\"ovrs_stck_evlu_amt\":\"760\"}]," +
                    "\"output2\":{\"tot_evlu_pfls_amt\":\"-375000\"}}"));
            if (path.EndsWith("/inquire-present-balance", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(
                    "{\"rt_cd\":\"0\",\"output2\":[{\"crcy_cd\":\"USD\",\"frcr_dncl_amt_2\":\"168000\",\"frst_bltn_exrt\":\"1500\"}]," +
                    "\"output3\":{\"crcy_cd\":\"USD\",\"frcr_use_psbl_amt\":\"168000\",\"tot_asst_amt\":\"375000000\"}}"));
            return Task.FromResult(JsonResponse("{\"rt_cd\":\"0\",\"output\":[]}"));
        });

        var snapshot = fixture.Brokerage.ReadBrokerSnapshot();

        Assert.Equal(168_000m, Assert.Single(snapshot.Cash).Amount);
        Assert.Equal(250_000m, snapshot.TotalValue);
        Assert.Equal(-250m, snapshot.CumulativePnl);
    }

    [Fact]
    public void UsPaperBalanceFallsBackToBuyingPowerWhenPresentBalanceGatewayFails()
    {
        using var fixture = new ScopeFixture(KisMarketScope.Us, (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/oauth2/tokenP", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(
                    "{\"access_token\":\"scope-token\",\"token_type\":\"Bearer\",\"expires_in\":86400}"));
            if (path.EndsWith("/inquire-balance", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(
                    "{\"rt_cd\":\"0\",\"output1\":[{\"ovrs_pdno\":\"SPY\",\"ovrs_cblc_qty\":\"1\",\"ovrs_stck_evlu_amt\":\"760\"}]}"));
            if (path.EndsWith("/inquire-present-balance", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse("{\"rt_cd\":\"1\",\"msg1\":\"Gateway routing error\"}"));
            if (path.EndsWith("/inquire-psamount", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(
                    "{\"rt_cd\":\"0\",\"output\":{\"ord_psbl_frcr_amt\":\"99229.63\",\"frcr_ord_psbl_amt1\":\"210264.43\",\"exrt\":\"1345.3\"}}"));
            return Task.FromResult(JsonResponse("{\"rt_cd\":\"0\",\"output\":[]}"));
        });

        var snapshot = fixture.Brokerage.ReadBrokerSnapshot();

        Assert.Equal(210_264.43m, Assert.Single(snapshot.Cash).Amount);
        Assert.Equal(1m, Assert.Single(snapshot.Holdings).Quantity);
    }

    [Fact]
    public void UsLeanStartupCashRetriesAFailedCompositeBalance()
    {
        var presentCalls = 0;
        using var fixture = new ScopeFixture(KisMarketScope.Us, (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/oauth2/tokenP", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(
                    "{\"access_token\":\"scope-token\",\"token_type\":\"Bearer\",\"expires_in\":86400}"));
            if (path.EndsWith("/inquire-balance", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse("{\"rt_cd\":\"0\",\"output1\":[]}"));
            if (path.EndsWith("/inquire-present-balance", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref presentCalls) == 1)
                    return Task.FromResult(JsonResponse("{\"rt_cd\":\"1\",\"msg1\":\"Gateway routing error\"}"));
                return Task.FromResult(JsonResponse(
                    "{\"rt_cd\":\"0\",\"output2\":[{\"crcy_cd\":\"USD\",\"frcr_dncl_amt_2\":\"1234.56\",\"frst_bltn_exrt\":\"1500\"}]," +
                    "\"output3\":{\"crcy_cd\":\"USD\",\"frcr_use_psbl_amt\":\"1234.56\",\"tot_asst_amt\":\"1851840\"}}"));
            }
            if (path.EndsWith("/inquire-psamount", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse("{\"rt_cd\":\"1\",\"msg1\":\"Gateway routing error\"}"));
            return Task.FromResult(JsonResponse("{\"rt_cd\":\"0\",\"output\":[]}"));
        });

        var cash = Assert.Single(fixture.Brokerage.GetCashBalance());

        Assert.Equal(1234.56m, cash.Amount);
        Assert.Equal(2, presentCalls);
    }

    [Fact]
    public void KrCompositeSnapshotReadsBalanceOnlyOnce()
    {
        using var fixture = new ScopeFixture(KisMarketScope.Kr);

        fixture.Brokerage.ReadBrokerSnapshot();

        Assert.Equal(1, fixture.Paths.Count(x =>
            x == "/uapi/domestic-stock/v1/trading/inquire-balance"));
        Assert.Equal(1, fixture.Paths.Count(x =>
            x == "/uapi/domestic-stock/v1/trading/inquire-psbl-rvsecncl"));
    }

    [Fact]
    public void CompositeSnapshotTimeoutFailsClosedAndResumesQuotes()
    {
        using var fixture = new ScopeFixture(
            KisMarketScope.Kr,
            async (request, cancellationToken) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/oauth2/tokenP", StringComparison.Ordinal))
                    return JsonResponse("{\"access_token\":\"scope-token\",\"token_type\":\"Bearer\",\"expires_in\":86400}");
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            },
            TimeSpan.FromMilliseconds(100));

        Assert.Throws<TimeoutException>(() => fixture.Brokerage.ReadBrokerSnapshot());
        Assert.True(KisQuotePollingControl.IsEnabled(KisMarketScope.Kr));
    }

    [Theory]
    [InlineData("Both", KisMarketScope.Both)]
    [InlineData("KR", KisMarketScope.Kr)]
    [InlineData("US", KisMarketScope.Us)]
    public void ParserPreservesBackwardCompatibleBothDefault(string raw, KisMarketScope expected) =>
        Assert.Equal(expected, KisMarketScopes.Parse(raw));

    [Fact]
    public void CrossScopePlaceAndHistoryRejectBeforeAnyHttp()
    {
        using var kr = new ScopeFixture(KisMarketScope.Kr);
        var usSymbol = new Symbol(
            SecurityIdentifier.GenerateEquity("AAPL", Market.USA, mapSymbol: false, null, null), "AAPL");
        Assert.Throws<InvalidOperationException>(() =>
            kr.Brokerage.PlaceOrder(new LimitOrder(usSymbol, 1, 100m, DateTime.UtcNow)));
        var history = NewHistoryRequest(usSymbol, DateTimeZoneProviders.Tzdb["America/New_York"]);
        Assert.Empty(kr.Brokerage.GetHistory(history));
        Assert.Empty(kr.Paths);

        using var us = new ScopeFixture(KisMarketScope.Us);
        var krSymbol = new Symbol(
            SecurityIdentifier.GenerateEquity("069500", Market.KRX, mapSymbol: false, null, null), "069500");
        Assert.Throws<InvalidOperationException>(() =>
            us.Brokerage.PlaceOrder(new LimitOrder(krSymbol, 1, 10_000m, DateTime.UtcNow)));
        Assert.Empty(us.Brokerage.GetHistory(NewHistoryRequest(
            krSymbol, DateTimeZoneProviders.Tzdb["Asia/Seoul"])));
        Assert.Empty(us.Paths);
    }

    [Fact]
    public void ScopePredicateRejectsOppositeMarketForDataQueueGuard()
    {
        Assert.True(KisMarketScope.Kr.Allows(Market.KRX));
        Assert.False(KisMarketScope.Kr.Allows(Market.USA));
        Assert.True(KisMarketScope.Us.Allows(Market.USA));
        Assert.False(KisMarketScope.Us.Allows(Market.KRX));
    }

    private static HistoryRequest NewHistoryRequest(Symbol symbol, DateTimeZone zone) => new(
        DateTime.UtcNow.AddDays(-10), DateTime.UtcNow, typeof(TradeBar), symbol, Resolution.Daily,
        SecurityExchangeHours.AlwaysOpen(zone), zone, null, false, false,
        DataNormalizationMode.Adjusted, TickType.Trade, DataMappingMode.OpenInterest, 0);

    private sealed class ScopeFixture : IDisposable
    {
        private readonly HttpClient _http;
        private readonly KisKrOAuthClient _oauth;
        private readonly KisKrTradingClient _kr;
        private readonly KisUsTradingClient _us;

        public ScopeFixture(
            KisMarketScope scope,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? responder = null,
            TimeSpan? snapshotRequestTimeout = null)
        {
            var config = new KisKrOAuthConfig(
                "https://kis.test", $"scope-key-{Guid.NewGuid():N}", "secret", "00000000", "01");
            _http = new HttpClient(new StubHandler(responder ?? ((request, _) =>
            {
                Paths.Enqueue(request.RequestUri!.AbsolutePath);
                var json = request.RequestUri.AbsolutePath.EndsWith("/oauth2/tokenP", StringComparison.Ordinal)
                    ? "{\"access_token\":\"scope-token\",\"token_type\":\"Bearer\",\"expires_in\":86400}"
                    : "{\"rt_cd\":\"0\",\"output\":[],\"output1\":[],\"output2\":[],\"output3\":[]}";
                return Task.FromResult(JsonResponse(json));
            })))
            { BaseAddress = new Uri(config.BaseUrl) };
            _oauth = new KisKrOAuthClient(config, _http, isPaper: true);
            var gate = new KisCallGate(TimeSpan.Zero);
            var options = new KisOrderRequestOptions(
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.Zero);
            _kr = new KisKrTradingClient(config, _oauth, _http, true, gate, options);
            _us = new KisUsTradingClient(config, _oauth, _http, true, gate, options);
            Brokerage = new KisBrokerage(
                _oauth, _kr, _us, ensureKrxMarketData: false, scope,
                callGate: gate,
                snapshotRequestTimeout: snapshotRequestTimeout);
        }

        public ConcurrentQueue<string> Paths { get; } = new();
        public KisBrokerage Brokerage { get; }

        public void Dispose()
        {
            Brokerage.Dispose();
            _kr.Dispose();
            _us.Dispose();
            _oauth.Dispose();
            _http.Dispose();
        }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class SnapshotProvider(KisMarketScope scope) : IKisBrokerSnapshotProvider
    {
        public KisMarketScope Scope => scope;
        public KisBrokerSnapshot ReadBrokerSnapshot() => new(scope, [], [], []);
    }
}
