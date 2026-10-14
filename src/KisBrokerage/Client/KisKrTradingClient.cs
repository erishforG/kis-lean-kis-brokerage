using System.Globalization;
using System.Text.Json.Serialization;

namespace QuantConnect.KisBrokerage.Client;

// NOTE: ported unchanged (only the namespace moved) from poc/KisKrTradingClient.cs during the
// LEAN-plugin-shape rewrite in feat/kr-oauth-client — see src/KisBrokerage/README.md.

/// <summary>Result of placing a KR limit order via <see cref="KisKrTradingClient.PlaceLimitOrderAsync"/>.</summary>
public sealed record KisOrderPlacementResult(
    bool Ok,
    string? OrderNo,
    string? OrderTime,
    string? ErrorMessage,
    string? ReturnCode,
    string? MessageCode,
    // KRX-assigned "전송 조직번호" for this order — required as-is by KIS's order-rvsecncl
    // (cancel/modify) endpoint's KRX_FWDG_ORD_ORGNO field alongside the order number itself.
    // KIS returns this in the placement response but the reference Node implementation never
    // captured it (KR domestic cancel/modify was never built there either — see
    // KisKrTradingClient.CancelOrderAsync's doc comment), so callers must persist OrderNo AND
    // OrgNo together from this result if they intend to cancel/modify the order later.
    string? OrgNo = null,
    bool Reconciled = false);

/// <summary>
/// Result of <see cref="KisKrTradingClient.CancelOrderAsync"/> or
/// <see cref="KisKrTradingClient.ModifyOrderAsync"/>. Like the US order-rvsecncl equivalent,
/// KIS issues a *new* order number for the cancel/modify action itself — this is not the
/// original order's number.
/// </summary>
public sealed record KisCancelModifyResult(
    bool Ok,
    string? OrderNo,
    string? ErrorMessage,
    string? ReturnCode,
    string? MessageCode);

/// <summary>One row of <see cref="KisKrTradingClient.GetBalanceAsync"/>'s holdings list.</summary>
public sealed record KisHolding(
    string Ticker,
    string Name,
    decimal Quantity,
    decimal AveragePrice,
    decimal CurrentPrice,
    decimal PnlPercent,
    decimal PnlAmount);

/// <summary>Result of <see cref="KisKrTradingClient.GetBalanceAsync"/>.</summary>
public sealed record KisBalanceResult(
    bool Ok,
    IReadOnlyList<KisHolding> Holdings,
    decimal TotalEval,
    decimal StockEval,
    decimal TotalPnl,
    decimal Cash,
    string? ErrorMessage);

/// <summary>One row of <see cref="KisKrTradingClient.GetDailyFilledOrdersAsync"/>.</summary>
public sealed record KisFilledOrder(
    string OrderNo,
    string Ticker,
    string Name,
    decimal OrderQty,
    decimal FilledQty,
    decimal RemainingQty,
    decimal OrderPrice,
    decimal AverageFilledPrice,
    string Side,
    string? OrderDate,
    string? OrderTime,
    string? OrgNo = null);

/// <summary>Result of <see cref="KisKrTradingClient.GetQuoteAsync"/> — real-time-ish (REST-polled) KR quote.</summary>
public sealed record KisQuoteResult(
    bool Ok,
    string Ticker,
    decimal Price,
    decimal Volume,
    string? ErrorMessage);

/// <summary>One row of <see cref="KisKrTradingClient.GetUnfilledOrdersAsync"/>.</summary>
public sealed record KisUnfilledOrder(
    string OrderNo,
    string Ticker,
    string Name,
    decimal OrderQty,
    decimal FilledQty,
    decimal RemainingQty,
    decimal OrderPrice,
    string? OrderType,
    string? OrderTime,
    string? OrgNo = null,
    string? Side = null);

// ─── Wire DTOs — field names/casing mirror the raw KIS JSON exactly (some endpoints
// return upper-case keys inside "output", others lower-case keys inside "output1"/
// "output2" — this is a genuine KIS API quirk, not an inconsistency introduced here). ───

internal sealed record KisOrderCashRequestBody(
    [property: JsonPropertyName("CANO")] string Cano,
    [property: JsonPropertyName("ACNT_PRDT_CD")] string AcntPrdtCd,
    [property: JsonPropertyName("PDNO")] string Pdno,
    [property: JsonPropertyName("ORD_DVSN")] string OrdDvsn,
    [property: JsonPropertyName("ORD_QTY")] string OrdQty,
    [property: JsonPropertyName("ORD_UNPR")] string OrdUnpr,
    [property: JsonPropertyName("EXCG_ID_DVSN_CD")] string ExchangeId,
    [property: JsonPropertyName("SLL_TYPE")] string SellType,
    [property: JsonPropertyName("CNDT_PRIC")] string ConditionPrice);

internal sealed record KisOrderCashOutput(
    [property: JsonPropertyName("ODNO")] string? OrderNo,
    [property: JsonPropertyName("ORD_TMD")] string? OrderTime,
    [property: JsonPropertyName("KRX_FWDG_ORD_ORGNO")] string? OrgNo);

// KIS official domestic order-rvsecncl request body. QTY_ALL_ORD_YN "Y" tells KIS to cancel/
// modify the entire remaining quantity regardless of ORD_QTY — this client always sends "Y" and
// the caller's requested qty, matching how KisUsTradingClient's overseas equivalent is used from
// KisBrokerage.cs (a partial-quantity cancel/modify is not exposed at the LEAN Order level either).
internal sealed record KisRvseCnclRequestBody(
    [property: JsonPropertyName("CANO")] string Cano,
    [property: JsonPropertyName("ACNT_PRDT_CD")] string AcntPrdtCd,
    [property: JsonPropertyName("KRX_FWDG_ORD_ORGNO")] string KrxFwdgOrdOrgno,
    [property: JsonPropertyName("ORGN_ODNO")] string OrgnOdno,
    [property: JsonPropertyName("ORD_DVSN")] string OrdDvsn,
    [property: JsonPropertyName("RVSE_CNCL_DVSN_CD")] string RvseCnclDvsnCd,
    [property: JsonPropertyName("ORD_QTY")] string OrdQty,
    [property: JsonPropertyName("ORD_UNPR")] string OrdUnpr,
    [property: JsonPropertyName("QTY_ALL_ORD_YN")] string QtyAllOrdYn,
    [property: JsonPropertyName("EXCG_ID_DVSN_CD")] string ExchangeId,
    [property: JsonPropertyName("CNDT_PRIC")] string ConditionPrice);

internal sealed record KisOrderCashResponse(
    [property: JsonPropertyName("rt_cd")] string? ReturnCode,
    [property: JsonPropertyName("msg_cd")] string? MessageCode,
    [property: JsonPropertyName("msg1")] string? Message,
    [property: JsonPropertyName("output")] KisOrderCashOutput? Output);

internal sealed record KisBalanceHoldingRow(
    [property: JsonPropertyName("pdno")] string? Pdno,
    [property: JsonPropertyName("prdt_name")] string? PrdtName,
    [property: JsonPropertyName("hldg_qty")] string? HldgQty,
    [property: JsonPropertyName("pchs_avg_pric")] string? PchsAvgPric,
    [property: JsonPropertyName("prpr")] string? Prpr,
    [property: JsonPropertyName("evlu_pfls_rt")] string? EvluPflsRt,
    [property: JsonPropertyName("evlu_pfls_amt")] string? EvluPflsAmt);

internal sealed record KisBalanceSummaryRow(
    [property: JsonPropertyName("tot_evlu_amt")] string? TotEvluAmt,
    [property: JsonPropertyName("scts_evlu_amt")] string? SctsEvluAmt,
    [property: JsonPropertyName("evlu_amt_smtl_amt")] string? EvluAmtSmtlAmt,
    [property: JsonPropertyName("evlu_pfls_smtl_amt")] string? EvluPflsSmtlAmt,
    [property: JsonPropertyName("dnca_tot_amt")] string? DncaTotAmt);

internal sealed record KisBalanceResponse(
    [property: JsonPropertyName("rt_cd")] string? ReturnCode,
    [property: JsonPropertyName("msg1")] string? Message,
    [property: JsonPropertyName("output1")] List<KisBalanceHoldingRow>? Output1,
    [property: JsonPropertyName("output2")] List<KisBalanceSummaryRow>? Output2);

internal sealed record KisDailyCcldRow(
    [property: JsonPropertyName("odno")] string? Odno,
    [property: JsonPropertyName("pdno")] string? Pdno,
    [property: JsonPropertyName("prdt_name")] string? PrdtName,
    [property: JsonPropertyName("ord_qty")] string? OrdQty,
    [property: JsonPropertyName("tot_ccld_qty")] string? TotCcldQty,
    [property: JsonPropertyName("ord_unpr")] string? OrdUnpr,
    [property: JsonPropertyName("avg_prvs")] string? AvgPrvs,
    [property: JsonPropertyName("sll_buy_dvsn_cd")] string? SllBuyDvsnCd,
    [property: JsonPropertyName("ord_dt")] string? OrdDt,
    [property: JsonPropertyName("ord_tmd")] string? OrdTmd,
    [property: JsonPropertyName("ord_gno_brno")] string? OrdGnoBrno);

internal sealed record KisDailyCcldResponse(
    [property: JsonPropertyName("rt_cd")] string? ReturnCode,
    [property: JsonPropertyName("msg1")] string? Message,
    [property: JsonPropertyName("output1")] List<KisDailyCcldRow>? Output1);

internal sealed record KisUnfilledRow(
    [property: JsonPropertyName("odno")] string? Odno,
    [property: JsonPropertyName("pdno")] string? Pdno,
    [property: JsonPropertyName("prdt_name")] string? PrdtName,
    [property: JsonPropertyName("ord_qty")] string? OrdQty,
    [property: JsonPropertyName("tot_ccld_qty")] string? TotCcldQty,
    [property: JsonPropertyName("psbl_qty")] string? PsblQty,
    [property: JsonPropertyName("ord_unpr")] string? OrdUnpr,
    [property: JsonPropertyName("ord_dvsn_cd")] string? OrdDvsnCd,
    [property: JsonPropertyName("ord_tmd")] string? OrdTmd,
    // Same KRX_FWDG_ORD_ORGNO concept as the order-placement response (see
    // KisOrderCashOutput.OrgNo), lower-cased here like every other field this query returns.
    // Needed to cancel/modify an order this process didn't place itself (e.g. after a restart).
    [property: JsonPropertyName("ord_gno_brno")] string? OrdGnoBrno,
    [property: JsonPropertyName("sll_buy_dvsn_cd")] string? SllBuyDvsnCd);

internal sealed record KisUnfilledResponse(
    [property: JsonPropertyName("rt_cd")] string? ReturnCode,
    [property: JsonPropertyName("msg1")] string? Message,
    [property: JsonPropertyName("output")] List<KisUnfilledRow>? Output);

internal sealed record KisPriceOutput(
    [property: JsonPropertyName("stck_prpr")] string? StckPrpr,
    [property: JsonPropertyName("acml_vol")] string? AcmlVol);

internal sealed record KisPriceResponse(
    [property: JsonPropertyName("rt_cd")] string? ReturnCode,
    [property: JsonPropertyName("msg1")] string? Message,
    [property: JsonPropertyName("output")] KisPriceOutput? Output);

internal sealed record KisDailyPriceRow(
    [property: JsonPropertyName("stck_bsop_date")] string? Date,
    [property: JsonPropertyName("stck_oprc")] string? Open,
    [property: JsonPropertyName("stck_hgpr")] string? High,
    [property: JsonPropertyName("stck_lwpr")] string? Low,
    [property: JsonPropertyName("stck_clpr")] string? Close,
    [property: JsonPropertyName("acml_vol")] string? Volume);

internal sealed record KisDailyPriceResponse(
    [property: JsonPropertyName("rt_cd")] string? ReturnCode,
    [property: JsonPropertyName("msg1")] string? Message,
    [property: JsonPropertyName("output2")] List<KisDailyPriceRow>? Output2);

/// <summary>
/// KIS KR-market trading client — limit order placement, order status/fill query, and
/// balance/holdings query. Paper-account ("모의투자") oriented only, same env-var
/// configuration pattern as <see cref="KisKrOAuthClient"/> (no hardcoded secrets, never
/// reads kis/.env.kis).
///
/// Ports the behavior of the reference Node implementation
/// (kis/kis_client.mjs, as delegated to by kis/lib/market_adapter_kr.mjs):
///   - orderCashWithRetry / buyEtf / sellEtf → PlaceLimitOrderAsync
///     POST {baseUrl}/uapi/domestic-stock/v1/trading/order-cash, tr_id VTTC0012U (buy,
///     paper) / VTTC0011U (sell, paper); retries up to 4 attempts with exponential backoff
///     (350ms base) on KIS's "초당 거래건수를 초과" rate-limit rejection, matching
///     isRateLimitError/orderCashWithRetry.
///   - getKrFilledOrders → GetDailyFilledOrdersAsync
///     GET {baseUrl}/uapi/domestic-stock/v1/trading/inquire-daily-ccld, tr_id VTTC0081R
///     (paper).
///   - getUnfilledOrders → GetUnfilledOrdersAsync
///     GET {baseUrl}/uapi/domestic-stock/v1/trading/inquire-psbl-rvsecncl, tr_id
///     VTTC8001R (paper).
///   - getBalance → GetBalanceAsync
///     GET {baseUrl}/uapi/domestic-stock/v1/trading/inquire-balance, tr_id VTTC8434R
///     (paper).
///
/// Only KR limit orders and REST-polled quotes were in scope for the original round — see
/// <see cref="CancelOrderAsync"/>/<see cref="ModifyOrderAsync"/> for the order-rvsecncl support
/// added afterward (which, unlike everything else in this class, has no Node reference to port —
/// kis_client.mjs itself never implemented KR domestic cancel/modify, see
/// market_adapter_kr.mjs::cancelOrder's own comment — so this was built directly against KIS's
/// official domestic order-rvsecncl API docs instead).
/// </summary>
public sealed class KisKrTradingClient : IDisposable
{
    private const string OrderCashPath = "/uapi/domestic-stock/v1/trading/order-cash";
    private const string RvseCnclPath = "/uapi/domestic-stock/v1/trading/order-rvsecncl";
    private const string InquireBalancePath = "/uapi/domestic-stock/v1/trading/inquire-balance";
    private const string InquireUnfilledPath = "/uapi/domestic-stock/v1/trading/inquire-psbl-rvsecncl";
    private const string InquireDailyCcldPath = "/uapi/domestic-stock/v1/trading/inquire-daily-ccld";
    private const string InquirePricePath = "/uapi/domestic-stock/v1/quotations/inquire-price";
    private const string InquireDailyPricePath =
        "/uapi/domestic-stock/v1/quotations/inquire-daily-itemchartprice";

    // Matches kis_client.mjs's TR_IDS.price — same tr_id for both paper and real accounts
    // (public market-data quotes are not account-scoped the way orders/balance are).
    private const string PriceTrId = "FHKST01010100";
    private const string DailyPriceTrId = "FHKST03010100";
    private const int DailyPriceMaxPages = 10;

    // ORD_DVSN "00" = 지정가 (limit). This client only places limit orders.
    private const string LimitOrderDvsn = "00";

    private const string BuyTrIdPaper = "VTTC0012U";
    private const string BuyTrIdReal = "TTTC0012U";
    private const string SellTrIdPaper = "VTTC0011U";
    private const string SellTrIdReal = "TTTC0011U";
    // KIS official domestic order-rvsecncl tr_id — same for both cancel and modify (the request
    // body's RVSE_CNCL_DVSN_CD distinguishes the two, not the tr_id), matching the pattern
    // KisUsTradingClient already uses for the overseas equivalent.
    private const string RvseCnclTrIdPaper = "VTTC0013U";
    private const string RvseCnclTrIdReal = "TTTC0013U";
    private const string BalanceTrIdPaper = "VTTC8434R";
    private const string BalanceTrIdReal = "TTTC8434R";
    private const string UnfilledTrIdPaper = "VTTC8001R";
    private const string UnfilledTrIdReal = "TTTC8001R";
    private const string DailyCcldTrIdPaper = "VTTC0081R";
    private const string DailyCcldTrIdReal = "TTTC0081R";

    // Matches kis_client.mjs::orderCashWithRetry.
    private const int OrderMaxAttempts = 4;
    private const int OrderBaseDelayMs = 350;

    private readonly KisKrOAuthConfig _config;
    private readonly KisKrOAuthClient _oauth;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly bool _isPaper;
    private readonly IKisCallGate _callGate;
    private readonly KisOrderRequestOptions _orderOptions;

    public KisKrTradingClient(
        KisKrOAuthConfig config,
        KisKrOAuthClient oauthClient,
        HttpClient? httpClient = null,
        bool isPaper = true)
        : this(config, oauthClient, httpClient, isPaper, KisRateLimiter.Shared, KisOrderRequestOptions.Default)
    {
    }

    internal KisKrTradingClient(
        KisKrOAuthConfig config,
        KisKrOAuthClient oauthClient,
        HttpClient? httpClient,
        bool isPaper,
        IKisCallGate callGate,
        KisOrderRequestOptions orderOptions)
    {
        _config = config;
        _oauth = oauthClient;
        _isPaper = isPaper;
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient { BaseAddress = new Uri(config.BaseUrl) };
        _callGate = callGate;
        _orderOptions = orderOptions;
    }

    /// <summary>
    /// Places a KR limit order (buy or sell). Matches kis_client.mjs::orderCashWithRetry —
    /// retries on rate-limit rejection ("초당 거래건수를 초과") up to <see cref="OrderMaxAttempts"/>
    /// times with exponential backoff, otherwise returns immediately with the KIS error.
    /// </summary>
    public async Task<KisOrderPlacementResult> PlaceLimitOrderAsync(
        string side, string ticker, int qty, decimal price, CancellationToken ct = default)
    {
        if (side != "buy" && side != "sell")
        {
            throw new ArgumentException("side must be \"buy\" or \"sell\"", nameof(side));
        }
        if (string.IsNullOrWhiteSpace(ticker))
        {
            throw new ArgumentException("ticker is required", nameof(ticker));
        }
        if (qty <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(qty), "qty must be positive");
        }
        if (price <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(price), "limit orders require price > 0");
        }

        var trId = side == "buy"
            ? (_isPaper ? BuyTrIdPaper : BuyTrIdReal)
            : (_isPaper ? SellTrIdPaper : SellTrIdReal);

        var body = new KisOrderCashRequestBody(
            Cano: _config.AccountNo,
            AcntPrdtCd: _config.ProductCode,
            Pdno: ticker,
            OrdDvsn: LimitOrderDvsn,
            OrdQty: qty.ToString(CultureInfo.InvariantCulture),
            OrdUnpr: price.ToString("0", CultureInfo.InvariantCulture),
            ExchangeId: "KRX",
            SellType: side == "sell" ? "01" : "",
            ConditionPrice: "");

        KisOrderCashResponse? last = null;
        var submittedAt = DateTimeOffset.UtcNow;

        for (var attempt = 1; attempt <= OrderMaxAttempts; attempt++)
        {
            try
            {
                last = await SendOrderAsync(body, trId, ct).ConfigureAwait(false);
            }
            catch (KisAmbiguousOrderSubmissionException ex)
            {
                return await ReconcilePlacementAsync(
                    side, ticker, qty, price, submittedAt, ex.Message).ConfigureAwait(false);
            }

            if (last?.ReturnCode == "0")
            {
                if (!string.IsNullOrWhiteSpace(last.Output?.OrderNo) &&
                    !string.IsNullOrWhiteSpace(last.Output.OrgNo))
                {
                    return new KisOrderPlacementResult(
                        Ok: true,
                        OrderNo: last.Output.OrderNo,
                        OrderTime: last.Output.OrderTime,
                        ErrorMessage: null,
                        ReturnCode: last.ReturnCode,
                        MessageCode: last.MessageCode,
                        OrgNo: last.Output.OrgNo);
                }

                // KR modify/cancel needs both the order number and forwarding org number. Treat
                // an incomplete success payload like a lost response and recover both values
                // from KIS history instead of returning a lifecycle-unusable success.
                return await ReconcilePlacementAsync(
                    side, ticker, qty, price, submittedAt,
                    "KIS returned success without a usable order number and forwarding org number")
                    .ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(last?.ReturnCode))
            {
                return await ReconcilePlacementAsync(
                    side, ticker, qty, price, submittedAt,
                    "KIS returned a response without a protocol result code").ConfigureAwait(false);
            }

            if (!IsRateLimitError(last?.Message) || attempt == OrderMaxAttempts)
            {
                break;
            }

            var delayMs = OrderBaseDelayMs * (1 << (attempt - 1));
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
        }

        return new KisOrderPlacementResult(
            Ok: false,
            OrderNo: null,
            OrderTime: null,
            ErrorMessage: last?.Message ?? "Unknown error",
            ReturnCode: last?.ReturnCode,
            MessageCode: last?.MessageCode);
    }

    private async Task<KisOrderCashResponse?> SendOrderAsync(
        KisOrderCashRequestBody body, string trId, CancellationToken callerToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeout.CancelAfter(_orderOptions.HttpTimeout);
        var requestStarted = false;

        try
        {
            return await _callGate.ExecuteAsync(async ct =>
            {
                var token = await _oauth.GetAccessTokenAsync(ct).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Post, OrderCashPath)
                {
                    // KIS's gateway rejects chunked order POSTs with EGW00202. StringContent
                    // computes Content-Length up front; JsonContent may stream without it.
                    Content = new StringContent(
                        System.Text.Json.JsonSerializer.Serialize(body),
                        System.Text.Encoding.UTF8,
                        "application/json"),
                };
                AddAuthHeaders(request, token, trId);
                requestStarted = true;
                using var response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return System.Text.Json.JsonSerializer.Deserialize<KisOrderCashResponse>(raw);
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (requestStarted && IsAmbiguousTransportFailure(ex))
        {
            throw new KisAmbiguousOrderSubmissionException(
                "KIS KR order response was not fully observed after the request started", ex);
        }
    }

    private async Task<KisOrderPlacementResult> ReconcilePlacementAsync(
        string side, string ticker, int qty, decimal price, DateTimeOffset submittedAt, string cause)
    {
        using var reconciliation = new CancellationTokenSource(_orderOptions.ReconciliationTimeout);

        while (!reconciliation.IsCancellationRequested)
        {
            try
            {
                var unfilled = await _callGate.ExecuteAsync(
                    innerCt => GetUnfilledOrdersAsync(innerCt), reconciliation.Token).ConfigureAwait(false);
                var candidates = unfilled
                    .Where(o => !string.IsNullOrWhiteSpace(o.OrderNo) &&
                        !string.IsNullOrWhiteSpace(o.OrgNo) && MatchesPlacement(
                        o.Ticker, o.Side, o.OrderQty, o.OrderPrice, o.OrderTime,
                        side, ticker, qty, price, submittedAt))
                    .ToList();

                if (candidates.Count == 1)
                {
                    var recovered = candidates[0];
                    return new KisOrderPlacementResult(
                        true, recovered.OrderNo, recovered.OrderTime, null, "0", null,
                        recovered.OrgNo, Reconciled: true);
                }

                var daily = await _callGate.ExecuteAsync(
                    innerCt => GetDailyFilledOrdersAsync(ct: innerCt), reconciliation.Token).ConfigureAwait(false);
                var historyCandidates = daily
                    .Where(o => !string.IsNullOrWhiteSpace(o.OrderNo) &&
                        !string.IsNullOrWhiteSpace(o.OrgNo) && MatchesPlacement(
                        o.Ticker, o.Side, o.OrderQty, o.OrderPrice, o.OrderTime,
                        side, ticker, qty, price, submittedAt))
                    .ToList();

                if (historyCandidates.Count == 1)
                {
                    var recovered = historyCandidates[0];
                    return new KisOrderPlacementResult(
                        true, recovered.OrderNo, recovered.OrderTime, null, "0", null,
                        recovered.OrgNo, Reconciled: true);
                }
            }
            catch (OperationCanceledException) when (reconciliation.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
            {
                // A read-only reconciliation lookup may be retried within the bounded window.
            }

            try
            {
                await Task.Delay(_orderOptions.ReconciliationPollDelay, reconciliation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return new KisOrderPlacementResult(
            false, null, null,
            $"Ambiguous KIS KR order submission; no unique matching order was found. " +
            $"The POST was not retried to prevent a duplicate accepted order. Cause: {cause}",
            null, "KIS_AMBIGUOUS_SUBMIT");
    }

    private static bool MatchesPlacement(
        string actualTicker, string? actualSide, decimal actualQty, decimal actualPrice, string? actualTime,
        string expectedSide, string expectedTicker, int expectedQty, decimal expectedPrice,
        DateTimeOffset submittedAt)
    {
        if (actualTicker != expectedTicker || actualSide != expectedSide || actualQty != expectedQty || actualPrice != expectedPrice)
        {
            return false;
        }

        return IsRecentOrderTime(actualTime, submittedAt, TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul"));
    }

    private static bool IsRecentOrderTime(string? orderTime, DateTimeOffset submittedAt, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(orderTime) || orderTime.Length < 6 ||
            !DateTime.TryParseExact(
                orderTime[..6], "HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsedTime))
        {
            return false;
        }

        var localAttempt = TimeZoneInfo.ConvertTime(submittedAt, zone);
        var localOrder = localAttempt.Date + parsedTime.TimeOfDay;
        return localOrder >= localAttempt.DateTime.AddSeconds(-10) &&
            localOrder <= localAttempt.DateTime.Add(OrderReconciliationMatchWindow);
    }

    private static readonly TimeSpan OrderReconciliationMatchWindow = TimeSpan.FromMinutes(2);

    private static bool IsAmbiguousTransportFailure(Exception ex) =>
        ex is OperationCanceledException or HttpRequestException or IOException or System.Text.Json.JsonException;

    /// <summary>
    /// Cancels a previously-placed KR limit order in full (QTY_ALL_ORD_YN "Y"). No Node
    /// reference exists for this — see the class doc comment — so this is built directly
    /// against KIS's official domestic order-rvsecncl API docs, not ported/verified against an
    /// existing working implementation the way every other method in this file is. Requires the
    /// KRX_FWDG_ORD_ORGNO ("전송 조직번호") returned alongside the order number, either from
    /// <see cref="PlaceLimitOrderAsync"/>'s result, <see cref="GetUnfilledOrdersAsync"/>, or
    /// <see cref="GetDailyFilledOrdersAsync"/>.
    /// </summary>
    public async Task<KisCancelModifyResult> CancelOrderAsync(
        string originalOrderNo, string orgNo, int qty, CancellationToken ct = default) =>
        await RvseCnclAsync(originalOrderNo, orgNo, qty, price: 0, rvseCnclDvsnCd: "02", ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Modifies (지정가 정정) a previously-placed KR limit order's quantity/price. Same
    /// no-Node-reference caveat as <see cref="CancelOrderAsync"/>.
    /// </summary>
    public async Task<KisCancelModifyResult> ModifyOrderAsync(
        string originalOrderNo, string orgNo, int qty, decimal price, CancellationToken ct = default) =>
        await RvseCnclAsync(originalOrderNo, orgNo, qty, price, rvseCnclDvsnCd: "01", ct)
            .ConfigureAwait(false);

    private async Task<KisCancelModifyResult> RvseCnclAsync(
        string originalOrderNo, string orgNo, int qty, decimal price, string rvseCnclDvsnCd,
        CancellationToken ct)
    {
        var trId = _isPaper ? RvseCnclTrIdPaper : RvseCnclTrIdReal;
        var body = new KisRvseCnclRequestBody(
            Cano: _config.AccountNo,
            AcntPrdtCd: _config.ProductCode,
            KrxFwdgOrdOrgno: orgNo,
            OrgnOdno: originalOrderNo,
            OrdDvsn: LimitOrderDvsn,
            RvseCnclDvsnCd: rvseCnclDvsnCd,
            OrdQty: qty.ToString(CultureInfo.InvariantCulture),
            OrdUnpr: price.ToString("0", CultureInfo.InvariantCulture),
            QtyAllOrdYn: "Y",
            ExchangeId: "KRX",
            ConditionPrice: "");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_orderOptions.HttpTimeout);

        KisOrderCashResponse? parsed;
        try
        {
            parsed = await _callGate.ExecuteAsync(async innerCt =>
            {
                var token = await _oauth.GetAccessTokenAsync(innerCt).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Post, RvseCnclPath)
                {
                    Content = new StringContent(
                        System.Text.Json.JsonSerializer.Serialize(body),
                        System.Text.Encoding.UTF8,
                        "application/json"),
                };
                AddAuthHeaders(request, token, trId);
                using var response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, innerCt).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var raw = await response.Content.ReadAsStringAsync(innerCt).ConfigureAwait(false);
                return System.Text.Json.JsonSerializer.Deserialize<KisOrderCashResponse>(raw);
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new KisCancelModifyResult(
                false, null, "KIS KR modify/cancel request timed out; outcome may be ambiguous",
                "KIS_ORDER_TIMEOUT", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            return new KisCancelModifyResult(
                false, null, $"KIS KR modify/cancel response failed: {ex.Message}",
                "KIS_ORDER_TRANSPORT", null);
        }

        if (parsed?.ReturnCode != "0")
        {
            return new KisCancelModifyResult(
                Ok: false, OrderNo: null, ErrorMessage: parsed?.Message ?? "Unknown error",
                ReturnCode: parsed?.ReturnCode, MessageCode: parsed?.MessageCode);
        }

        if (rvseCnclDvsnCd == "01" && string.IsNullOrWhiteSpace(parsed.Output?.OrderNo))
        {
            return new KisCancelModifyResult(
                false, null, "KIS KR modify succeeded without a replacement order number",
                "KIS_ORDER_ID_MISSING", parsed.MessageCode);
        }

        return new KisCancelModifyResult(
            Ok: true, OrderNo: parsed.Output?.OrderNo, ErrorMessage: null,
            ReturnCode: parsed.ReturnCode, MessageCode: parsed.MessageCode);
    }

    /// <summary>
    /// Order status/fill query for today (KST) by default. Matches
    /// kis_client.mjs::getKrFilledOrders — one row per order with both order and
    /// filled/remaining quantities, i.e. this single endpoint doubles as order status.
    /// </summary>
    public async Task<IReadOnlyList<KisFilledOrder>> GetDailyFilledOrdersAsync(
        string? startDate = null, string? endDate = null, CancellationToken ct = default)
    {
        var token = await _oauth.GetAccessTokenAsync(ct).ConfigureAwait(false);
        var trId = _isPaper ? DailyCcldTrIdPaper : DailyCcldTrIdReal;
        var today = TodayKst();
        startDate ??= today;
        endDate ??= today;

        // 모의(paper) 계좌는 필터 파라미터를 무시하고 전체조회만 허용 — kis_client.mjs 동일 주석.
        var query = new Dictionary<string, string>
        {
            ["CANO"] = _config.AccountNo,
            ["ACNT_PRDT_CD"] = _config.ProductCode,
            ["INQR_STRT_DT"] = startDate,
            ["INQR_END_DT"] = endDate,
            ["SLL_BUY_DVSN_CD"] = "00",
            ["INQR_DVSN"] = "00",
            ["PDNO"] = "",
            ["CCLD_DVSN"] = "00",
            ["ORD_GNO_BRNO"] = "",
            ["ODNO"] = "",
            ["INQR_DVSN_3"] = "00",
            ["INQR_DVSN_1"] = "",
            ["CTX_AREA_FK100"] = "",
            ["CTX_AREA_NK100"] = "",
        };

        using var request = BuildGetRequest(InquireDailyCcldPath, query);
        AddAuthHeaders(request, token, trId);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<KisDailyCcldResponse>(raw);

        if (parsed?.ReturnCode != "0")
        {
            throw new InvalidOperationException(
                $"KIS daily filled-orders query failed: {parsed?.Message ?? "Unknown error"}");
        }

        return (parsed.Output1 ?? [])
            .Select(o =>
            {
                var orderQty = ParseDecimal(o.OrdQty);
                var filledQty = ParseDecimal(o.TotCcldQty);
                return new KisFilledOrder(
                    OrderNo: o.Odno ?? "",
                    Ticker: o.Pdno ?? "",
                    Name: o.PrdtName ?? "",
                    OrderQty: orderQty,
                    FilledQty: filledQty,
                    RemainingQty: orderQty - filledQty,
                    OrderPrice: ParseDecimal(o.OrdUnpr),
                    AverageFilledPrice: ParseDecimal(o.AvgPrvs),
                    Side: o.SllBuyDvsnCd == "02" ? "buy" : "sell",
                    OrderDate: o.OrdDt,
                    OrderTime: o.OrdTmd,
                    OrgNo: o.OrdGnoBrno);
            })
            .ToList();
    }

    /// <summary>
    /// Unfilled/partially-filled order query for today (KST). Matches
    /// kis_client.mjs::getUnfilledOrders (inquire-psbl-rvsecncl), keeping only rows with
    /// psbl_qty (remaining cancellable qty) &gt; 0.
    /// </summary>
    public async Task<IReadOnlyList<KisUnfilledOrder>> GetUnfilledOrdersAsync(CancellationToken ct = default)
    {
        var token = await _oauth.GetAccessTokenAsync(ct).ConfigureAwait(false);
        var trId = _isPaper ? UnfilledTrIdPaper : UnfilledTrIdReal;
        var today = TodayKst();

        var query = new Dictionary<string, string>
        {
            ["CANO"] = _config.AccountNo,
            ["ACNT_PRDT_CD"] = _config.ProductCode,
            ["INQR_STRT_DT"] = today,
            ["INQR_END_DT"] = today,
            ["SLL_BUY_DVSN_CD"] = "00",
            ["INQR_DVSN"] = "00",
            ["PDNO"] = "",
            ["CCLD_DVSN"] = "00",
            ["ORD_GNO_BRNO"] = "",
            ["ODNO"] = "",
            ["INQR_DVSN_3"] = "00",
            ["INQR_DVSN_1"] = "0",
            ["INQR_DVSN_2"] = "0",
            ["CTX_AREA_FK100"] = "",
            ["CTX_AREA_NK100"] = "",
        };

        using var request = BuildGetRequest(InquireUnfilledPath, query);
        AddAuthHeaders(request, token, trId);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<KisUnfilledResponse>(raw);

        if (parsed?.ReturnCode != "0")
        {
            throw new InvalidOperationException(
                $"KIS unfilled-orders query failed: {parsed?.Message ?? "Unknown error"}");
        }

        return (parsed.Output ?? [])
            .Where(o => ParseDecimal(o.PsblQty) > 0)
            .Select(o => new KisUnfilledOrder(
                OrderNo: o.Odno ?? "",
                Ticker: o.Pdno ?? "",
                Name: o.PrdtName ?? "",
                OrderQty: ParseDecimal(o.OrdQty),
                FilledQty: ParseDecimal(o.TotCcldQty),
                RemainingQty: ParseDecimal(o.PsblQty),
                OrderPrice: ParseDecimal(o.OrdUnpr),
                OrderType: o.OrdDvsnCd,
                OrderTime: o.OrdTmd,
                OrgNo: o.OrdGnoBrno,
                // sll_buy_dvsn_cd: "01"=매도(sell), "02"=매수(buy) — same convention as
                // GetDailyFilledOrdersAsync's mapping below.
                Side: o.SllBuyDvsnCd == "02" ? "buy" : "sell"))
            .ToList();
    }

    /// <summary>
    /// Balance/holdings query. Matches kis_client.mjs::getBalance — <c>TotalEval</c> is
    /// the account's total evaluation amount (stock + cash, i.e. NAV; KIS field
    /// tot_evlu_amt), <c>StockEval</c> is stock-only evaluation (scts_evlu_amt, falling
    /// back to evlu_amt_smtl_amt when zero, mirroring the Node `||` fallback). <c>Cash</c>
    /// is current NAV less stock evaluation. KIS's dnca_tot_amt is settlement-date deposit
    /// cash and can remain unchanged immediately after a paper fill, so it is not suitable
    /// for live portfolio reconciliation.
    /// </summary>
    public async Task<KisBalanceResult> GetBalanceAsync(CancellationToken ct = default)
    {
        var token = await _oauth.GetAccessTokenAsync(ct).ConfigureAwait(false);
        var trId = _isPaper ? BalanceTrIdPaper : BalanceTrIdReal;

        var query = new Dictionary<string, string>
        {
            ["CANO"] = _config.AccountNo,
            ["ACNT_PRDT_CD"] = _config.ProductCode,
            ["AFHR_FLPR_YN"] = "N",
            ["OFL_YN"] = "",
            ["INQR_DVSN"] = _isPaper ? "01" : "02",
            ["UNPR_DVSN"] = "01",
            ["FUND_STTL_ICLD_YN"] = "N",
            ["FNCG_AMT_AUTO_RDPT_YN"] = "N",
            ["PRCS_DVSN"] = _isPaper ? "00" : "01",
            ["CTX_AREA_FK100"] = "",
            ["CTX_AREA_NK100"] = "",
        };

        using var request = BuildGetRequest(InquireBalancePath, query);
        AddAuthHeaders(request, token, trId);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<KisBalanceResponse>(raw);

        if (parsed?.ReturnCode != "0")
        {
            return new KisBalanceResult(false, [], 0, 0, 0, 0, parsed?.Message ?? "Unknown error");
        }

        var holdings = (parsed.Output1 ?? [])
            .Where(i => ParseDecimal(i.HldgQty) > 0)
            .Select(i => new KisHolding(
                Ticker: i.Pdno ?? "",
                Name: i.PrdtName ?? "",
                Quantity: ParseDecimal(i.HldgQty),
                AveragePrice: ParseDecimal(i.PchsAvgPric),
                CurrentPrice: ParseDecimal(i.Prpr),
                PnlPercent: ParseDecimal(i.EvluPflsRt),
                PnlAmount: ParseDecimal(i.EvluPflsAmt)))
            .ToList();

        var summary = parsed.Output2?.FirstOrDefault();
        var sctsEval = ParseDecimal(summary?.SctsEvluAmt);
        var stockEval = sctsEval != 0 ? sctsEval : ParseDecimal(summary?.EvluAmtSmtlAmt);
        var totalEval = ParseDecimal(summary?.TotEvluAmt);
        var currentCash = totalEval > 0m && totalEval >= stockEval
            ? totalEval - stockEval
            : ParseDecimal(summary?.DncaTotAmt);

        return new KisBalanceResult(
            Ok: true,
            Holdings: holdings,
            TotalEval: totalEval,
            StockEval: stockEval,
            TotalPnl: ParseDecimal(summary?.EvluPflsSmtlAmt),
            Cash: currentCash,
            ErrorMessage: null);
    }

    /// <summary>
    /// Current-price quote for a single KR ticker. Matches kis_client.mjs::getEtfPrice —
    /// GET inquire-price, tr_id FHKST01010100, FID_COND_MRKT_DIV_CODE=J (KRX/KOSDAQ combined).
    /// Used by <see cref="KisBrokerage"/>'s <c>IDataQueueHandler</c> REST-polling implementation
    /// (see KisBrokerage.DataQueueHandler.cs) — this is a plain read-only GET, no order side
    /// effects, safe to poll on an interval.
    /// </summary>
    public async Task<KisQuoteResult> GetQuoteAsync(string ticker, CancellationToken ct = default)
    {
        var token = await _oauth.GetAccessTokenAsync(ct).ConfigureAwait(false);

        var query = new Dictionary<string, string>
        {
            ["FID_COND_MRKT_DIV_CODE"] = "J",
            ["FID_INPUT_ISCD"] = ticker,
        };

        using var request = BuildGetRequest(InquirePricePath, query);
        AddAuthHeaders(request, token, PriceTrId);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<KisPriceResponse>(raw);

        if (parsed?.ReturnCode != "0")
        {
            return new KisQuoteResult(false, ticker, 0, 0, parsed?.Message ?? "Unknown error");
        }

        return new KisQuoteResult(
            Ok: true,
            Ticker: ticker,
            Price: ParseDecimal(parsed.Output?.StckPrpr),
            Volume: ParseDecimal(parsed.Output?.AcmlVol),
            ErrorMessage: null);
    }

    /// <summary>
    /// Returns adjusted KR daily OHLCV rows in ascending date order. KIS exposes the same
    /// public quotation TR for paper and real endpoints; unlike account-scoped order TRs,
    /// <c>FHKST03010100</c> therefore has no V/T variant.
    ///
    /// KIS commonly caps one response near 100 rows. The client moves the inclusive end-date
    /// cursor to one calendar day before the oldest returned row, de-duplicates dates, and
    /// stops on the requested start date, no progress, an empty page, or a bounded page cap.
    /// Every page acquires the injected/shared KIS call gate.
    /// </summary>
    public async Task<IReadOnlyList<KisDailyBar>> GetDailyBarsAsync(
        string ticker, DateOnly startDate, DateOnly endDate, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ticker))
        {
            throw new ArgumentException("ticker is required", nameof(ticker));
        }
        if (endDate < startDate)
        {
            throw new ArgumentException("endDate must be on or after startDate", nameof(endDate));
        }

        var bars = new Dictionary<DateOnly, KisDailyBar>();
        var cursor = endDate;

        for (var page = 0; page < DailyPriceMaxPages && cursor >= startDate; page++)
        {
            var parsed = await _callGate.ExecuteAsync(async innerCt =>
            {
                var token = await _oauth.GetAccessTokenAsync(innerCt).ConfigureAwait(false);
                var query = new Dictionary<string, string>
                {
                    ["FID_COND_MRKT_DIV_CODE"] = "J",
                    ["FID_INPUT_ISCD"] = ticker,
                    ["FID_INPUT_DATE_1"] = startDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                    ["FID_INPUT_DATE_2"] = cursor.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                    ["FID_PERIOD_DIV_CODE"] = "D",
                    ["FID_ORG_ADJ_PRC"] = "0",
                };

                using var request = BuildGetRequest(InquireDailyPricePath, query);
                AddAuthHeaders(request, token, DailyPriceTrId);
                using var response = await _http.SendAsync(request, innerCt).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var raw = await response.Content.ReadAsStringAsync(innerCt).ConfigureAwait(false);
                return System.Text.Json.JsonSerializer.Deserialize<KisDailyPriceResponse>(raw);
            }, ct).ConfigureAwait(false);

            if (parsed?.ReturnCode != "0")
            {
                throw new InvalidOperationException(
                    $"KIS KR daily-price query failed: {parsed?.Message ?? "Unknown error"}");
            }

            var rows = parsed.Output2 ?? [];
            if (rows.Count == 0)
            {
                break;
            }

            DateOnly? oldest = null;
            foreach (var row in rows)
            {
                if (!TryParseDate(row.Date, out var date))
                {
                    continue;
                }

                oldest = oldest is null || date < oldest ? date : oldest;
                var bar = ParseDailyBar(date, row.Open, row.High, row.Low, row.Close, row.Volume);
                if (bar is not null && date >= startDate && date <= endDate)
                {
                    bars[date] = bar;
                }
            }

            if (oldest is null || oldest <= startDate)
            {
                break;
            }

            var next = oldest.Value.AddDays(-1);
            if (next >= cursor)
            {
                break;
            }
            cursor = next;
        }

        return bars.Values.OrderBy(bar => bar.Date).ToList();
    }

    private static KisDailyBar? ParseDailyBar(
        DateOnly date, string? open, string? high, string? low, string? close, string? volume)
    {
        var parsedOpen = ParseDecimal(open);
        var parsedHigh = ParseDecimal(high);
        var parsedLow = ParseDecimal(low);
        var parsedClose = ParseDecimal(close);
        if (parsedOpen <= 0 || parsedHigh <= 0 || parsedLow <= 0 || parsedClose <= 0)
        {
            return null;
        }

        return new KisDailyBar(date, parsedOpen, parsedHigh, parsedLow, parsedClose, ParseDecimal(volume));
    }

    private static bool TryParseDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(
            value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private void AddAuthHeaders(HttpRequestMessage request, string token, string trId)
    {
        request.Headers.Add("authorization", $"Bearer {token}");
        request.Headers.Add("appkey", _config.AppKey);
        request.Headers.Add("appsecret", _config.AppSecret);
        request.Headers.Add("tr_id", trId);
        request.Headers.Add("custtype", "P");
        request.Headers.Add("tr_cont", "");
    }

    private static HttpRequestMessage BuildGetRequest(string path, IReadOnlyDictionary<string, string> query)
    {
        var qs = string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return new HttpRequestMessage(HttpMethod.Get, $"{path}?{qs}");
    }

    private static bool IsRateLimitError(string? message)
    {
        var msg = message ?? "";
        return msg.Contains("초당 거래건수를 초과", StringComparison.Ordinal)
            || msg.Contains("rate", StringComparison.OrdinalIgnoreCase);
    }

    private static decimal ParseDecimal(string? s) =>
        decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    private static string TodayKst()
    {
        var kst = DateTime.UtcNow.AddHours(9);
        return kst.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
