using System.Net;
using System.Text;
using System.Text.Json;
using NodaTime;
using QuantConnect.Brokerages;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.KisBrokerage.Client;
using QuantConnect.Securities;
using Xunit;

namespace QuantConnect.KisBrokerage.Tests;

public sealed class KisDailyHistoryTests
{
    [Fact]
    public async Task KrHistoryCanCollectMoreThanOneHundredBars()
    {
        var end = new DateOnly(2026, 6, 30);
        var allDates = Enumerable.Range(0, 130).Select(index => end.AddDays(-index)).ToList();
        var requestCount = 0;
        using var fixture = new HistoryFixture((request, _) =>
        {
            if (IsToken(request)) return Task.FromResult(TokenResponse());
            var dates = requestCount++ == 0 ? allDates.Take(100) : allDates.Skip(100);
            return Task.FromResult(DailyKrResponse(dates));
        });

        var bars = await fixture.Kr.GetDailyBarsAsync("069500", allDates[^1], end);

        Assert.Equal(130, bars.Count);
        Assert.Equal(allDates[^1], bars[0].Date);
        Assert.Equal(end, bars[^1].Date);
        Assert.Equal(2, fixture.Gate.ExecutionCount);
    }

    [Fact]
    public async Task KrHistoryPaginatesParsesFiltersDeduplicatesAndShapesRequests()
    {
        var requests = new List<CapturedRequest>();
        using var fixture = new HistoryFixture((request, _) =>
        {
            if (IsToken(request)) return Task.FromResult(TokenResponse());
            requests.Add(Capture(request));
            var query = ParseQuery(request.RequestUri!);
            return Task.FromResult(query["FID_INPUT_DATE_2"] == "20260110"
                ? JsonResponse("""
                    {"rt_cd":"0","output2":[
                      {"stck_bsop_date":"20260110","stck_oprc":"10","stck_hgpr":"12","stck_lwpr":"9","stck_clpr":"11","acml_vol":"100"},
                      {"stck_bsop_date":"20260109","stck_oprc":"9","stck_hgpr":"11","stck_lwpr":"8","stck_clpr":"10","acml_vol":"90"},
                      {"stck_bsop_date":"20260108","stck_oprc":"8","stck_hgpr":"10","stck_lwpr":"7","stck_clpr":"9","acml_vol":"80"}
                    ]}
                    """)
                : JsonResponse("""
                    {"rt_cd":"0","output2":[
                      {"stck_bsop_date":"20260108","stck_oprc":"8","stck_hgpr":"10","stck_lwpr":"7","stck_clpr":"9","acml_vol":"80"},
                      {"stck_bsop_date":"20260107","stck_oprc":"7","stck_hgpr":"9","stck_lwpr":"6","stck_clpr":"8","acml_vol":"70"},
                      {"stck_bsop_date":"20260106","stck_oprc":"0","stck_hgpr":"8","stck_lwpr":"5","stck_clpr":"7","acml_vol":"60"},
                      {"stck_bsop_date":"20260105","stck_oprc":"5","stck_hgpr":"7","stck_lwpr":"4","stck_clpr":"6","acml_vol":"50"}
                    ]}
                    """));
        });

        var bars = await fixture.Kr.GetDailyBarsAsync(
            "069500", new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 10));

        Assert.Equal(
            [new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 7), new DateOnly(2026, 1, 8),
                new DateOnly(2026, 1, 9), new DateOnly(2026, 1, 10)],
            bars.Select(bar => bar.Date));
        Assert.Equal(2, requests.Count);
        Assert.Equal(2, fixture.Gate.ExecutionCount);
        Assert.All(requests, request =>
        {
            Assert.Equal("/uapi/domestic-stock/v1/quotations/inquire-daily-itemchartprice", request.Path);
            Assert.Equal("FHKST03010100", request.TrId);
            Assert.Equal("Bearer test-token", request.Authorization);
            Assert.Equal("J", request.Query["FID_COND_MRKT_DIV_CODE"]);
            Assert.Equal("069500", request.Query["FID_INPUT_ISCD"]);
            Assert.Equal("20260105", request.Query["FID_INPUT_DATE_1"]);
            Assert.Equal("D", request.Query["FID_PERIOD_DIV_CODE"]);
            Assert.Equal("0", request.Query["FID_ORG_ADJ_PRC"]);
        });
        Assert.Equal("20260107", requests[1].Query["FID_INPUT_DATE_2"]);
    }

    [Fact]
    public async Task KrHistorySurfacesApiError()
    {
        using var fixture = new HistoryFixture((request, _) => Task.FromResult(
            IsToken(request) ? TokenResponse() : JsonResponse("""{"rt_cd":"1","msg1":"bad request"}""")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Kr.GetDailyBarsAsync(
                "069500", new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 10)));

        Assert.Contains("bad request", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KrHistoryStopsAtBoundedPageCap()
    {
        using var fixture = new HistoryFixture((request, _) =>
        {
            if (IsToken(request)) return Task.FromResult(TokenResponse());
            var cursor = ParseQuery(request.RequestUri!)["FID_INPUT_DATE_2"];
            return Task.FromResult(DailyKrResponse(
                [DateOnly.ParseExact(cursor, "yyyyMMdd")]));
        });

        var bars = await fixture.Kr.GetDailyBarsAsync(
            "069500", new DateOnly(2020, 1, 1), new DateOnly(2026, 1, 10));

        Assert.Equal(10, bars.Count);
        Assert.Equal(10, fixture.Gate.ExecutionCount);
    }

    [Fact]
    public async Task UsHistoryFallsBackExchangeAndPaginatesWithExpectedShape()
    {
        var requests = new List<CapturedRequest>();
        using var fixture = new HistoryFixture((request, _) =>
        {
            if (IsToken(request)) return Task.FromResult(TokenResponse());
            var captured = Capture(request);
            requests.Add(captured);
            var exchange = captured.Query["EXCD"];
            if (exchange == "NAS")
            {
                return Task.FromResult(JsonResponse("""{"rt_cd":"0","output2":[]}"""));
            }

            return Task.FromResult(captured.Query["BYMD"] == "20260110"
                ? JsonResponse("""
                    {"rt_cd":"0","output2":[
                      {"xymd":"20260110","open":"100","high":"105","low":"99","clos":"104","tvol":"1000"},
                      {"xymd":"20260108","open":"98","high":"101","low":"97","clos":"100","tvol":"800"}
                    ]}
                    """)
                : JsonResponse("""
                    {"rt_cd":"0","output2":[
                      {"xymd":"20260108","open":"98","high":"101","low":"97","clos":"100","tvol":"800"},
                      {"xymd":"20260107","open":"0","high":"99","low":"96","clos":"98","tvol":"700"},
                      {"xymd":"20260105","open":"95","high":"98","low":"94","clos":"97","tvol":"500"}
                    ]}
                    """));
        });

        var bars = await fixture.Us.GetDailyBarsAsync(
            "SPY", new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 10));

        Assert.Equal(
            [new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 8), new DateOnly(2026, 1, 10)],
            bars.Select(bar => bar.Date));
        Assert.Equal(["NAS", "NYS", "NYS"], requests.Select(request => request.Query["EXCD"]));
        Assert.Equal(3, fixture.Gate.ExecutionCount);
        Assert.All(requests, request =>
        {
            Assert.Equal("/uapi/overseas-price/v1/quotations/dailyprice", request.Path);
            Assert.Equal("HHDFS76240000", request.TrId);
            Assert.Equal("", request.Query["AUTH"]);
            Assert.Equal("SPY", request.Query["SYMB"]);
            Assert.Equal("0", request.Query["GUBN"]);
            Assert.Equal("0", request.Query["MODP"]);
        });
        Assert.Equal("20260107", requests[2].Query["BYMD"]);
    }

    [Fact]
    public async Task UsHistoryStopsWhenEndpointMakesNoCursorProgress()
    {
        using var fixture = new HistoryFixture((request, _) => Task.FromResult(
            IsToken(request)
                ? TokenResponse()
                : JsonResponse("""
                    {"rt_cd":"0","output2":[
                      {"xymd":"20260110","open":"100","high":"105","low":"99","clos":"104","tvol":"1000"},
                      {"xymd":"20260108","open":"98","high":"101","low":"97","clos":"100","tvol":"800"}
                    ]}
                    """)));

        var bars = await fixture.Us.GetDailyBarsAsync(
            "AAPL", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 10), "NAS");

        Assert.Equal(2, bars.Count);
        Assert.Equal(2, fixture.Gate.ExecutionCount);
    }

    [Fact]
    public void BrokerageHistoryReturnsOldestFirstWithLocalDailyEndTime()
    {
        using var fixture = new HistoryFixture((request, _) => Task.FromResult(
            IsToken(request)
                ? TokenResponse()
                : JsonResponse("""
                    {"rt_cd":"0","output2":[
                      {"stck_bsop_date":"20260106","stck_oprc":"6","stck_hgpr":"8","stck_lwpr":"5","stck_clpr":"7","acml_vol":"60"},
                      {"stck_bsop_date":"20260105","stck_oprc":"5","stck_hgpr":"7","stck_lwpr":"4","stck_clpr":"6","acml_vol":"50"}
                    ]}
                    """)));
        using var brokerage = fixture.CreateBrokerage();
        var seoul = DateTimeZoneProviders.Tzdb["Asia/Seoul"];
        var request = NewHistoryRequest(
            "069500", Market.KRX, seoul,
            new DateTime(2026, 1, 4, 15, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 6, 15, 0, 0, DateTimeKind.Utc));

        var bars = brokerage.GetHistory(request).Cast<TradeBar>().ToList();

        Assert.Equal(2, bars.Count);
        Assert.Equal(new DateTime(2026, 1, 5), bars[0].Time);
        Assert.Equal(new DateTime(2026, 1, 6), bars[0].EndTime);
        Assert.Equal(new DateTime(2026, 1, 6), bars[1].Time);
        Assert.Equal(new DateTime(2026, 1, 7), bars[1].EndTime);
    }

    [Fact]
    public void BrokerageHistoryWarnsAndReturnsEmptyForUnsupportedResolution()
    {
        using var fixture = new HistoryFixture((_, _) => throw new InvalidOperationException("HTTP should not run"));
        using var brokerage = fixture.CreateBrokerage();
        BrokerageMessageEvent? warning = null;
        brokerage.Message += (_, message) => warning = message;
        var seoul = DateTimeZoneProviders.Tzdb["Asia/Seoul"];
        var request = NewHistoryRequest(
            "069500", Market.KRX, seoul, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow,
            Resolution.Minute);

        Assert.Empty(brokerage.GetHistory(request));
        Assert.NotNull(warning);
        Assert.Equal("KIS_HISTORY_UNSUPPORTED", warning.Code);
        Assert.Contains("Minute", warning.Message, StringComparison.Ordinal);
    }

    private static HistoryRequest NewHistoryRequest(
        string ticker, string market, DateTimeZone zone, DateTime startUtc, DateTime endUtc,
        Resolution resolution = Resolution.Daily)
    {
        var symbol = new Symbol(
            SecurityIdentifier.GenerateEquity(ticker, market, mapSymbol: false, null, null), ticker);
        return new HistoryRequest(
            startUtc, endUtc, typeof(TradeBar), symbol, resolution,
            SecurityExchangeHours.AlwaysOpen(zone), zone, null,
            includeExtendedMarketHours: false, isCustomData: false,
            DataNormalizationMode.Adjusted, TickType.Trade, DataMappingMode.OpenInterest, 0);
    }

    private static bool IsToken(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.EndsWith("/oauth2/tokenP", StringComparison.Ordinal);

    private static HttpResponseMessage TokenResponse() =>
        JsonResponse("""{"access_token":"test-token","token_type":"Bearer","expires_in":86400}""");

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage DailyKrResponse(IEnumerable<DateOnly> dates)
    {
        var output2 = dates.Select(date => new
        {
            stck_bsop_date = date.ToString("yyyyMMdd"),
            stck_oprc = "10",
            stck_hgpr = "12",
            stck_lwpr = "9",
            stck_clpr = "11",
            acml_vol = "100",
        });
        return JsonResponse(JsonSerializer.Serialize(new { rt_cd = "0", output2 }));
    }

    private static CapturedRequest Capture(HttpRequestMessage request) => new(
        request.RequestUri!.AbsolutePath,
        request.Headers.GetValues("tr_id").Single(),
        request.Headers.GetValues("authorization").Single(),
        ParseQuery(request.RequestUri));

    private static Dictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split('=', 2))
            .ToDictionary(
                item => Uri.UnescapeDataString(item[0]),
                item => Uri.UnescapeDataString(item.Length > 1 ? item[1] : ""));

    private sealed record CapturedRequest(
        string Path, string TrId, string Authorization, Dictionary<string, string> Query);

    private sealed class HistoryFixture : IDisposable
    {
        private readonly HttpClient _http;

        public HistoryFixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            Gate = new RecordingCallGate(new KisCallGate(TimeSpan.Zero));
            var config = new KisKrOAuthConfig(
                "https://kis.test", $"test-key-{Guid.NewGuid():N}", "test-secret", "00000000", "01");
            _http = new HttpClient(new StubHttpMessageHandler(send)) { BaseAddress = new Uri(config.BaseUrl) };
            OAuth = new KisKrOAuthClient(config, _http, isPaper: true);
            Kr = new KisKrTradingClient(
                config, OAuth, _http, true, Gate, KisOrderRequestOptions.Default);
            Us = new KisUsTradingClient(
                config, OAuth, _http, true, Gate, KisOrderRequestOptions.Default);
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
