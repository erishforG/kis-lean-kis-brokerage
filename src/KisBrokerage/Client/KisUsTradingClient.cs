using System.Globalization;
using System.Text.Json.Serialization;

namespace QuantConnect.KisBrokerage.Client;

// NOTE: added in feat/kr-oauth-client's US-market follow-up round. Ports the overseas-trading
// surface of the reference Node implementation (kis/lib/market_adapter_us.mjs, delegating to
// kis/kis_client.mjs) the same way KisKrTradingClient ported the KR domestic surface — see
// src/KisBrokerage/README.md.
//
// IMPORTANT — no separate OAuth client/config for US: in the real KIS API, OAuth tokens are
// scoped to an *account* (paper/stock/pension — one app key/secret pair per account), not to a
// market. kis_client.mjs::getAccessToken(type) issues one token per account `type` and that same
// token/appkey/appsecret pair is reused for both domestic (/uapi/domestic-stock/...) and overseas
// (/uapi/overseas-stock/...) endpoint calls — see ACCOUNTS.paper being passed unchanged into both
// market_adapter_kr.mjs and market_adapter_us.mjs. This client therefore takes the *same*
// <see cref="KisKrOAuthConfig"/> and a shared <see cref="KisKrOAuthClient"/> instance as
// KisKrTradingClient — no KIS_PAPER_US_APP_KEY-style env vars, no new OAuth class.

/// <summary>Result of placing a US limit order via <see cref="KisUsTradingClient.PlaceLimitOrderAsync"/>.</summary>
public sealed record KisUsOrderPlacementResult(
    bool Ok,
    string? OrderNo,
    string? OrderTime,
    string? ErrorMessage,
    string? ReturnCode,
    string? MessageCode,
    bool Reconciled = false);

/// <summary>
/// Result of <see cref="KisUsTradingClient.CancelOrderAsync"/> or
/// <see cref="KisUsTradingClient.ModifyOrderAsync"/>. KIS's order-rvsecncl endpoint issues a
/// *new* order number for the cancel/modify action itself (<see cref="OrderNo"/>) — this is not
/// the original order's number, matching kis_client.mjs's `_overseasRvseCncl`.
/// </summary>
public sealed record KisUsCancelModifyResult(
    bool Ok,
    string? OrderNo,
    string? ErrorMessage,
    string? ReturnCode,
    string? MessageCode);

/// <summary>One row of <see cref="KisUsTradingClient.GetBalanceAsync"/>'s holdings list.</summary>
public sealed record KisUsHolding(
    string Symbol,
    string Name,
    string Exchange,
    decimal Quantity,
    decimal AveragePrice,
    decimal CurrentPrice,
    decimal PnlPercent,
    decimal PnlAmount,
    decimal EvalAmount);

/// <summary>Result of <see cref="KisUsTradingClient.GetBalanceAsync"/>.</summary>
public sealed record KisUsBalanceResult(
    bool Ok,
    IReadOnlyList<KisUsHolding> Holdings,
    decimal UsdCash,
    decimal UsdEval,
    decimal UsdUsePsbl,
    decimal UsdToKrw,
    string? ErrorMessage,
    decimal? CumulativePnlUsd = null);

/// <summary>One row of <see cref="KisUsTradingClient.GetFilledOrdersAsync"/>.</summary>
public sealed record KisUsFilledOrder(
    string OrderNo,
    string? OriginalOrderNo,
    string Ticker,
    string? Name,
    string? Exchange,
    string? Side,
    decimal OrderQty,
    decimal FilledQty,
    decimal OrderPrice,
    decimal AverageFilledPrice,
    decimal FilledAmount,
    string? OrderDate,
    string? OrderTime);

/// <summary>Result of <see cref="KisUsTradingClient.GetQuoteAsync"/> — real-time-ish (REST-polled) US quote.</summary>
public sealed record KisUsQuoteResult(
    bool Ok,
    string Symbol,
    string Exchange,
    decimal Last,
    decimal Volume,
    string? ErrorMessage);

/// <summary>One row of <see cref="KisUsTradingClient.GetUnfilledOrdersAsync"/>.</summary>
public sealed record KisUsUnfilledOrder(
    string OrderNo,
    string Ticker,
    string? Name,
    string? Side,
    decimal OrderQty,
    decimal FilledQty,
    decimal OrderPrice,
    string? Exchange,
    string? OrderDate,
    string? OrderTime);

// ─── Wire DTOs — field names/casing mirror the raw KIS JSON exactly, same rationale as
// KisKrTradingClient's DTOs (some endpoints use upper-case "output" keys, others lower-case
// "output1"/"output2"/"output3" keys — a genuine KIS API quirk). ───

internal sealed record KisOverseasOrderRequestBody(
    [property: JsonPropertyName("CANO")] string Cano,
    [property: JsonPropertyName("ACNT_PRDT_CD")] string AcntPrdtCd,
    [property: JsonPropertyName("OVRS_EXCG_CD")] string OvrsExcgCd,
    [property: JsonPropertyName("PDNO")] string Pdno,
    [property: JsonPropertyName("ORD_QTY")] string OrdQty,
    [property: JsonPropertyName("OVRS_ORD_UNPR")] string OvrsOrdUnpr,
    [property: JsonPropertyName("ORD_SVR_DVSN_CD")] string OrdSvrDvsnCd,
    [property: JsonPropertyName("ORD_DVSN")] string OrdDvsn);

internal sealed record KisOverseasOrderOutput(
    [property: JsonPropertyName("ODNO")] string? OrderNo,
    [property: JsonPropertyName("ORD_TMD")] string? OrderTime);

internal sealed record KisOverseasOrderResponse(
    [property: JsonPropertyName("rt_cd")] string? ReturnCode,
    [property: JsonPropertyName("msg_cd")] string? MessageCode,
    [property: JsonPropertyName("msg1")] string? Message,
    [property: JsonPropertyName("output")] KisOverseasOrderOutput? Output);

// Matches kis_client.mjs's `_overseasRvseCncl` request body exactly (field order/casing).
internal sealed record KisOverseasRvseCnclRequestBody(
    [property: JsonPropertyName("CANO")] string Cano,
    [property: JsonPropertyName("ACNT_PRDT_CD")] string AcntPrdtCd,
    [property: JsonPropertyName("OVRS_EXCG_CD")] string OvrsExcgCd,
    [property: JsonPropertyName("PDNO")] string Pdno,
    [property: JsonPropertyName("ORGN_ODNO")] string OrgnOdno,
    [property: JsonPropertyName("RVSE_CNCL_DVSN_CD")] string RvseCnclDvsnCd,
    [property: JsonPropertyName("ORD_QTY")] string OrdQty,
    [property: JsonPropertyName("OVRS_ORD_UNPR")] string OvrsOrdUnpr,
    [property: JsonPropertyName("MGCO_APTM_ODNO")] string MgcoAptmOdno,
    [property: JsonPropertyName("ORD_SVR_DVSN_CD")] string OrdSvrDvsnCd);

internal sealed record KisOverseasUnfilledRow(
    [property: JsonPropertyName("odno")] string? Odno,
    [property: JsonPropertyName("pdno")] string? Pdno,
    [property: JsonPropertyName("prdt_name")] string? PrdtName,
    [property: JsonPropertyName("sll_buy_dvsn_cd")] string? SllBuyDvsnCd,
    [property: JsonPropertyName("ft_ord_qty")] string? FtOrdQty,
    [property: JsonPropertyName("ord_qty")] string? OrdQty,
    [property: JsonPropertyName("ft_ccld_qty")] string? FtCcldQty,
    [property: JsonPropertyName("tot_ccld_qty")] string? TotCcldQty,
    [property: JsonPropertyName("ft_ord_unpr3")] string? FtOrdUnpr3,
    [property: JsonPropertyName("ord_unpr3")] string? OrdUnpr3,
    [property: JsonPropertyName("ord_dt")] string? OrdDt,
    [property: JsonPropertyName("ord_tmd")] string? OrdTmd,
    [property: JsonPropertyName("ovrs_excg_cd")] string? OvrsExcgCd);

internal sealed record KisOverseasUnfilledResponse(
    [property: JsonPropertyName("rt_cd")] string? ReturnCode,
    [property: JsonPropertyName("msg1")] string? Message,
    [property: JsonPropertyName("output")] List<KisOverseasUnfilledRow>? Output);

internal sealed record KisOverseasCcnlRow(
    [property: JsonPropertyName("odno")] string? Odno,
    [property: JsonPropertyName("orgn_odno")] string? OrgnOdno,
    [property: JsonPropertyName("ord_dt")] string? OrdDt,
    [property: JsonPropertyName("ord_tmd")] string? OrdTmd,
    [property: JsonPropertyName("pdno")] string? Pdno,
    [property: JsonPropertyName("prdt_name")] string? PrdtName,
    [property: JsonPropertyName("ovrs_excg_cd")] string? OvrsExcgCd,
    [property: JsonPropertyName("sll_buy_dvsn_cd")] string? SllBuyDvsnCd,
    [property: JsonPropertyName("ft_ord_qty")] string? FtOrdQty,
    [property: JsonPropertyName("ord_qty")] string? OrdQty,
    [property: JsonPropertyName("ft_ccld_qty")] string? FtCcldQty,
    [property: JsonPropertyName("tot_ccld_qty")] string? TotCcldQty,
    [property: JsonPropertyName("ft_ord_unpr3")] string? FtOrdUnpr3,
    [property: JsonPropertyName("ord_unpr3")] string? OrdUnpr3,
    [property: JsonPropertyName("ord_unpr")] string? OrdUnpr,
    [property: JsonPropertyName("ft_ccld_unpr3")] string? FtCcldUnpr3,
    [property: JsonPropertyName("avg_unpr")] string? AvgUnpr,
    [property: JsonPropertyName("ccld_unpr")] string? CcldUnpr,
    [property: JsonPropertyName("ft_ccld_amt3")] string? FtCcldAmt3,
    [property: JsonPropertyName("tot_ccld_amt")] string? TotCcldAmt);

internal sealed record KisOverseasCcnlResponse(
    [property: JsonPropertyName("rt_cd")] string? ReturnCode,
    [property: JsonPropertyName("msg1")] string? Message,
    [property: JsonPropertyName("output")] List<KisOverseasCcnlRow>? Output,
    [property: JsonPropertyName("ctx_area_nk200")] string? CtxAreaNk200,
    [property: JsonPropertyName("ctx_area_fk200")] string? CtxAreaFk200);

internal sealed record KisOverseasBalanceHoldingRow(
    [property: JsonPropertyName("ovrs_pdno")] string? OvrsPdno,
    [property: JsonPropertyName("ovrs_item_name")] string? OvrsItemName,
    [property: JsonPropertyName("ovrs_excg_cd")] string? OvrsExcgCd,
    [property: JsonPropertyName("ovrs_cblc_qty")] string? OvrsCblcQty,
    [property: JsonPropertyName("pchs_avg_pric")] string? PchsAvgPric,
    [property: JsonPropertyName("now_pric2")] string? NowPric2,
    [property: JsonPropertyName("evlu_pfls_rt")] string? EvluPflsRt,
    [property: JsonPropertyName("frcr_evlu_pfls_amt")] string? FrcrEvluPflsAmt,
    [property: JsonPropertyName("ovrs_stck_evlu_amt")] string? OvrsStckEvluAmt);

internal sealed record KisOverseasBalanceResponse(
    [property: JsonPropertyName("rt_cd")] string? ReturnCode,
    [property: JsonPropertyName("msg1")] string? Message,
    [property: JsonPropertyName("output1")] List<KisOverseasBalanceHoldingRow>? Output1,
    [property: JsonPropertyName("output2")] System.Text.Json.JsonElement? Output2);

internal sealed record KisOverseasPsAmountOutput(
    [property: JsonPropertyName("ord_psbl_frcr_amt")] string? OrdPsblFrcrAmt,
    [property: JsonPropertyName("frcr_ord_psbl_amt1")] string? FrcrOrdPsblAmt1,
    [property: JsonPropertyName("exrt")] string? ExchangeRate);

internal sealed record KisOverseasPsAmountResponse(
    [property: JsonPropertyName("rt_cd")] string? ReturnCode,
    [property: JsonPropertyName("msg1")] string? Message,
    [property: JsonPropertyName("output")] KisOverseasPsAmountOutput? Output);

internal sealed record KisOverseasCurrencySummaryRow(
    [property: JsonPropertyName("crcy_cd")] string? CrcyCd,
    [property: JsonPropertyName("frcr_dncl_amt_2")] string? FrcrDnclAmt2,
    [property: JsonPropertyName("frst_bltn_exrt")] string? FrstBltnExrt);

internal sealed record KisOverseasAvailCashRow(
    [property: JsonPropertyName("crcy_cd")] string? CrcyCd,
    [property: JsonPropertyName("frcr_use_psbl_amt")] string? FrcrUsePsblAmt,
    [property: JsonPropertyName("tot_asst_amt")] string? TotAsstAmt,
    [property: JsonPropertyName("frcr_evlu_tota")] string? FrcrEvluTota);

internal sealed record KisOverseasPresentBalanceResponse(
    [property: JsonPropertyName("rt_cd")] string? ReturnCode,
    [property: JsonPropertyName("msg1")] string? Message,
    [property: JsonPropertyName("output2")] List<KisOverseasCurrencySummaryRow>? Output2,
    [property: JsonPropertyName("output3")] System.Text.Json.JsonElement? Output3);

internal sealed record KisOverseasPriceOutput(
    [property: JsonPropertyName("last")] string? Last,
    [property: JsonPropertyName("tvol")] string? Tvol);

internal sealed record KisOverseasPriceResponse(
    [property: JsonPropertyName("rt_cd")] string? ReturnCode,
    [property: JsonPropertyName("msg1")] string? Message,
    [property: JsonPropertyName("output")] KisOverseasPriceOutput? Output);

internal sealed record KisOverseasDailyPriceRow(
    [property: JsonPropertyName("xymd")] string? Date,
    [property: JsonPropertyName("open")] string? Open,
    [property: JsonPropertyName("high")] string? High,
    [property: JsonPropertyName("low")] string? Low,
    [property: JsonPropertyName("clos")] string? Close,
    [property: JsonPropertyName("tvol")] string? Volume);

internal sealed record KisOverseasDailyPriceResponse(
    [property: JsonPropertyName("rt_cd")] string? ReturnCode,
    [property: JsonPropertyName("msg1")] string? Message,
    [property: JsonPropertyName("output2")] List<KisOverseasDailyPriceRow>? Output2);

/// <summary>
/// KIS US-market (해외주식, NASDAQ/NYSE/AMEX) trading client — limit order placement,
/// order status/fill query, and balance/holdings query. Supports both paper and real-account
/// routing, using the same configuration pattern as <see cref="KisKrTradingClient"/> — and in fact
/// the *same* <see cref="KisKrOAuthConfig"/>/<see cref="KisKrOAuthClient"/>, since KIS scopes
/// app key/secret/tokens to an account, not a market (see the file-level NOTE above).
///
/// Ports the behavior of the reference Node implementation
/// (kis/kis_client.mjs, as delegated to by kis/lib/market_adapter_us.mjs):
///   - placeOverseasOrder / buyOverseas / sellOverseas → PlaceLimitOrderAsync
///     POST {baseUrl}/uapi/overseas-stock/v1/trading/order, tr_id VTTT1002U (buy, paper) /
///     TTTT1002U (buy, real) / VTTT1001U (sell, paper) / TTTT1001U (sell, real).
///     ORD_DVSN is hard-coded to "00" (지정가/limit) exactly like the Node reference — KIS
///     rejects overseas market orders client-side, so this client has no market-order method
///     at all (not merely an unused parameter) to make that impossible to call by accident.
///     Includes the same single-retry-on-rate-limit behavior as placeOverseasOrder.
///   - getOverseasUnfilledOrders → GetUnfilledOrdersAsync
///     GET {baseUrl}/uapi/overseas-stock/v1/trading/inquire-nccs, tr_id VTTS3018R (paper) /
///     TTTS3018R (real).
///   - getOverseasFilledOrders → GetFilledOrdersAsync
///     GET {baseUrl}/uapi/overseas-stock/v1/trading/inquire-ccnl, tr_id VTTS3035R (paper) /
///     TTTS3035R (real). Paper accounts ignore filter params and return everything, so (like
///     the Node reference) this method always full-queries and filters client-side; also mirrors
///     the reference's CTX_AREA_NK200-driven pagination (capped at 10 pages).
///   - getOverseasBalance → GetBalanceAsync
///     Combines per-exchange inquire-balance (GET .../inquire-balance, tr_id VTTS3012R/TTTS3012R,
///     looped over NASD/NYSE/AMEX and de-duplicated by symbol — paper accounts return all USD
///     holdings regardless of the OVRS_EXCG_CD filter) for per-symbol holdings, with
///     inquire-present-balance (GET .../inquire-present-balance, tr_id VTRP6504R paper /
///     CTRP6504R real) for the USD cash/available-cash summary. Reproduces the paper-account
///     quirks the Node reference works around: present-balance's USD cash field can come back
///     as a KRW-seed total instead of real USD holdings (falls back to tot_asst_amt / exchange
///     rate), and usd_use_psbl can come back KRW-denominated (detected via a magnitude ratio
///     check against the USD eval estimate and divided by the exchange rate).
///
/// Only US limit orders are in scope — matching the Node reference, which never sends
/// ORD_DVSN other than "00" for overseas orders (KIS's own client-side rule: overseas market
/// orders are rejected). No order cancel/modify (kis_client.mjs has cancelOverseasOrderById /
/// modifyOverseasOrder, but wiring those into this brokerage is left as a follow-up — see
/// KisBrokerage.cs's UpdateOrder/CancelOrder TODOs, same gap already carried over from KR).
/// </summary>
public sealed class KisUsTradingClient : IDisposable
{
    private const string OrderPath = "/uapi/overseas-stock/v1/trading/order";
    private const string RvseCnclPath = "/uapi/overseas-stock/v1/trading/order-rvsecncl";
    private const string InquireUnfilledPath = "/uapi/overseas-stock/v1/trading/inquire-nccs";
    private const string InquireCcnlPath = "/uapi/overseas-stock/v1/trading/inquire-ccnl";
    private const string InquireBalancePath = "/uapi/overseas-stock/v1/trading/inquire-balance";
    private const string InquirePresentBalancePath = "/uapi/overseas-stock/v1/trading/inquire-present-balance";
    private const string InquirePsAmountPath = "/uapi/overseas-stock/v1/trading/inquire-psamount";

    // Note: quote/price lives under a different KIS path prefix ("overseas-price", not
    // "overseas-stock") than every order/balance endpoint above — matches
    // kis_client.mjs::getOverseasPrice.
    private const string InquirePricePath = "/uapi/overseas-price/v1/quotations/price";
    private const string InquireDailyPricePath = "/uapi/overseas-price/v1/quotations/dailyprice";

    // Matches kis_client.mjs's tr_id for getOverseasPrice — same for paper and real accounts
    // (public market-data quotes are not account-scoped the way orders/balance are).
    private const string PriceTrId = "HHDFS00000300";
    private const string DailyPriceTrId = "HHDFS76240000";
    private const int DailyPriceMaxPages = 10;

    /// <summary>Default order/query exchange code, matching market_adapter_us.mjs's DEFAULTS.defaultExchange.</summary>
    public const string DefaultExchange = "NASD";

    /// <summary>
    /// Default quote/price exchange code, matching market_adapter_us.mjs's
    /// DEFAULTS.defaultPriceExchange. KIS uses different codes for order-side ("NASD") vs.
    /// price-side ("NAS") exchange identification — see resolveExchange in market_adapter_us.mjs.
    /// </summary>
    public const string DefaultPriceExchange = "NAS";

    // ORD_DVSN "00" = 지정가 (limit). KIS rejects overseas market orders client-side — this
    // client only ever sends limit orders, matching placeOverseasOrder in kis_client.mjs.
    private const string LimitOrderDvsn = "00";

    private const string BuyTrIdPaper = "VTTT1002U";
    private const string BuyTrIdReal = "TTTT1002U";
    private const string SellTrIdPaper = "VTTT1001U";
    private const string SellTrIdReal = "TTTT1001U";
    // Matches kis_client.mjs's `_overseasRvseCncl` tr_id — same for both cancel and modify
    // (RVSE_CNCL_DVSN_CD in the request body distinguishes the two, not the tr_id).
    private const string RvseCnclTrIdPaper = "VTTT1004U";
    private const string RvseCnclTrIdReal = "TTTT1004U";
    private const string UnfilledTrIdPaper = "VTTS3018R";
    private const string UnfilledTrIdReal = "TTTS3018R";
    private const string CcnlTrIdPaper = "VTTS3035R";
    private const string CcnlTrIdReal = "TTTS3035R";
    private const string BalanceTrIdPaper = "VTTS3012R";
    private const string BalanceTrIdReal = "TTTS3012R";
    private const string PresentBalanceTrIdPaper = "VTRP6504R";
    private const string PresentBalanceTrIdReal = "CTRP6504R";
    private const string PsAmountTrIdPaper = "VTTS3007R";

    private static readonly string[] BalanceExchanges = ["NASD", "NYSE", "AMEX"];

    // Matches kis_client.mjs::placeOverseasOrder's single retry on EGW00201 (초당 거래건수 초과).
    private const int OrderRateLimitRetryMs = 1500;

    // KIS overseas order history is keyed by the broker's Korean business date, even when
    // the underlying US session is still on the previous UTC/local-market calendar date.
    private static readonly TimeZoneInfo KisBusinessTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");

    private readonly KisKrOAuthConfig _config;
    private readonly KisKrOAuthClient _oauth;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly bool _isPaper;
    private readonly IKisCallGate _callGate;
    private readonly KisOrderRequestOptions _orderOptions;
    private readonly TimeProvider _timeProvider;
    private KisUsBalanceResult? _lastGoodBalance;
    private DateTimeOffset _lastGoodBalanceAt;
    private IReadOnlyList<KisUsUnfilledOrder>? _lastGoodUnfilled;
    private DateTimeOffset _lastGoodUnfilledAt;
    private static readonly TimeSpan ReadCacheTtl = TimeSpan.FromMinutes(5);

    public KisUsTradingClient(
        KisKrOAuthConfig config,
        KisKrOAuthClient oauthClient,
        HttpClient? httpClient = null,
        bool isPaper = true)
        : this(config, oauthClient, httpClient, isPaper, KisRateLimiter.Shared, KisOrderRequestOptions.Default)
    {
    }

    internal KisUsTradingClient(
        KisKrOAuthConfig config,
        KisKrOAuthClient oauthClient,
        HttpClient? httpClient,
        bool isPaper,
        IKisCallGate callGate,
        KisOrderRequestOptions orderOptions,
        TimeProvider? timeProvider = null)
    {
        _config = config;
        _oauth = oauthClient;
        _isPaper = isPaper;
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient { BaseAddress = new Uri(config.BaseUrl) };
        _callGate = callGate;
        _orderOptions = orderOptions;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Places a US limit order (buy or sell) on the given exchange (NASD/NYSE/AMEX). There is
    /// intentionally no way to request a market order from this client — see the class doc
    /// comment; KIS itself rejects overseas market orders, so KisBrokerage.PlaceOrder must only
    /// ever reach this method for LEAN <c>LimitOrder</c>s.
    /// </summary>
    public async Task<KisUsOrderPlacementResult> PlaceLimitOrderAsync(
        string side, string ticker, int qty, decimal price, string exchange = DefaultExchange,
        CancellationToken ct = default)
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
            throw new ArgumentOutOfRangeException(
                nameof(price), "KIS US orders must be limit orders and require price > 0 (KIS rejects overseas market orders)");
        }

        var trId = side == "buy"
            ? (_isPaper ? BuyTrIdPaper : BuyTrIdReal)
            : (_isPaper ? SellTrIdPaper : SellTrIdReal);

        var body = new KisOverseasOrderRequestBody(
            Cano: _config.AccountNo,
            AcntPrdtCd: _config.ProductCode,
            OvrsExcgCd: exchange,
            Pdno: ticker,
            OrdQty: qty.ToString(CultureInfo.InvariantCulture),
            OvrsOrdUnpr: price.ToString(CultureInfo.InvariantCulture),
            OrdSvrDvsnCd: "0",
            OrdDvsn: LimitOrderDvsn);

        var submittedAt = _timeProvider.GetUtcNow();
        KisOverseasOrderResponse? parsed;
        try
        {
            parsed = await SendOrderAsync(body, trId, ct).ConfigureAwait(false);
        }
        catch (KisAmbiguousOrderSubmissionException ex)
        {
            return await ReconcilePlacementAsync(
                side, ticker, qty, price, exchange, submittedAt, ex.Message).ConfigureAwait(false);
        }

        if (parsed?.ReturnCode != "0" && IsRateLimitError(parsed?.Message))
        {
            await Task.Delay(OrderRateLimitRetryMs, ct).ConfigureAwait(false);
            try
            {
                parsed = await SendOrderAsync(body, trId, ct).ConfigureAwait(false);
            }
            catch (KisAmbiguousOrderSubmissionException ex)
            {
                return await ReconcilePlacementAsync(
                    side, ticker, qty, price, exchange, submittedAt, ex.Message).ConfigureAwait(false);
            }
        }

        if (parsed?.ReturnCode == "0")
        {
            if (!string.IsNullOrWhiteSpace(parsed.Output?.OrderNo))
            {
                return new KisUsOrderPlacementResult(
                    Ok: true,
                    OrderNo: parsed.Output.OrderNo,
                    OrderTime: parsed.Output.OrderTime,
                    ErrorMessage: null,
                    ReturnCode: parsed.ReturnCode,
                    MessageCode: parsed.MessageCode);
            }

            return await ReconcilePlacementAsync(
                side, ticker, qty, price, exchange, submittedAt,
                "KIS returned success without an order number").ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(parsed?.ReturnCode))
        {
            return await ReconcilePlacementAsync(
                side, ticker, qty, price, exchange, submittedAt,
                "KIS returned a response without a protocol result code").ConfigureAwait(false);
        }

        return new KisUsOrderPlacementResult(
            Ok: false,
            OrderNo: null,
            OrderTime: null,
            ErrorMessage: parsed?.Message ?? "Unknown error",
            ReturnCode: parsed?.ReturnCode,
            MessageCode: parsed?.MessageCode);
    }

    private async Task<KisOverseasOrderResponse?> SendOrderAsync(
        KisOverseasOrderRequestBody body, string trId, CancellationToken callerToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeout.CancelAfter(_orderOptions.HttpTimeout);
        var requestStarted = false;

        try
        {
            return await _callGate.ExecuteAsync(async ct =>
            {
                var token = await _oauth.GetAccessTokenAsync(ct).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Post, OrderPath)
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
                return System.Text.Json.JsonSerializer.Deserialize<KisOverseasOrderResponse>(raw);
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (requestStarted && IsAmbiguousTransportFailure(ex))
        {
            throw new KisAmbiguousOrderSubmissionException(
                "KIS US order response was not fully observed after the request started", ex);
        }
    }

    private async Task<KisUsOrderPlacementResult> ReconcilePlacementAsync(
        string side, string ticker, int qty, decimal price, string exchange,
        DateTimeOffset submittedAt, string cause)
    {
        using var reconciliation = new CancellationTokenSource(_orderOptions.ReconciliationTimeout);

        while (!reconciliation.IsCancellationRequested)
        {
            try
            {
                var unfilled = await _callGate.ExecuteAsync(
                    innerCt => GetUnfilledOrdersAsync(exchange, innerCt), reconciliation.Token).ConfigureAwait(false);
                var candidates = unfilled
                    .Where(o => !string.IsNullOrWhiteSpace(o.OrderNo) && MatchesPlacement(
                        o.Ticker, o.Side, o.OrderQty, o.OrderPrice, o.Exchange,
                        o.OrderDate, o.OrderTime, side, ticker, qty, price, exchange, submittedAt))
                    .ToList();

                if (candidates.Count == 1)
                {
                    var recovered = candidates[0];
                    return new KisUsOrderPlacementResult(
                        true, recovered.OrderNo, recovered.OrderTime, null, "0", null,
                        Reconciled: true);
                }

                var history = await _callGate.ExecuteAsync(
                    innerCt => GetFilledOrdersAsync(
                        ticker: ticker, exchange: exchange, side: side, ct: innerCt),
                    reconciliation.Token).ConfigureAwait(false);
                var historyCandidates = history
                    .Where(o => !string.IsNullOrWhiteSpace(o.OrderNo) && MatchesPlacement(
                        o.Ticker, o.Side, o.OrderQty, o.OrderPrice, o.Exchange,
                        o.OrderDate, o.OrderTime, side, ticker, qty, price, exchange, submittedAt))
                    .ToList();

                if (historyCandidates.Count == 1)
                {
                    var recovered = historyCandidates[0];
                    return new KisUsOrderPlacementResult(
                        true, recovered.OrderNo, recovered.OrderTime, null, "0", null,
                        Reconciled: true);
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

        return new KisUsOrderPlacementResult(
            false, null, null,
            $"Ambiguous KIS US order submission; no unique matching order was found. " +
            $"The POST was not retried to prevent a duplicate accepted order. Cause: {cause}",
            null, "KIS_AMBIGUOUS_SUBMIT");
    }

    private static bool MatchesPlacement(
        string actualTicker, string? actualSide, decimal actualQty, decimal actualPrice,
        string? actualExchange, string? actualDate, string? actualTime,
        string expectedSide, string expectedTicker, int expectedQty, decimal expectedPrice,
        string expectedExchange, DateTimeOffset submittedAt)
    {
        if (actualTicker != expectedTicker || actualSide != expectedSide || actualQty != expectedQty ||
            actualPrice != expectedPrice ||
            (!string.IsNullOrWhiteSpace(actualExchange) && actualExchange != expectedExchange))
        {
            return false;
        }

        return IsRecentOrderTime(actualDate, actualTime, submittedAt);
    }

    private static bool IsRecentOrderTime(string? orderDate, string? orderTime, DateTimeOffset submittedAt)
    {
        if (string.IsNullOrWhiteSpace(orderTime) || orderTime.Length < 6)
        {
            return false;
        }

        var localAttempt = TimeZoneInfo.ConvertTime(
            submittedAt, TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul"));
        var date = string.IsNullOrWhiteSpace(orderDate) ? localAttempt.ToString("yyyyMMdd") : orderDate;
        if (!DateTime.TryParseExact(
            $"{date}{orderTime[..6]}", "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var localOrder))
        {
            return false;
        }

        return localOrder >= localAttempt.DateTime.AddSeconds(-10) &&
            localOrder <= localAttempt.DateTime.Add(OrderReconciliationMatchWindow);
    }

    private static readonly TimeSpan OrderReconciliationMatchWindow = TimeSpan.FromMinutes(2);

    private static bool IsAmbiguousTransportFailure(Exception ex) =>
        ex is OperationCanceledException or HttpRequestException or IOException or System.Text.Json.JsonException;

    /// <summary>
    /// Cancels a previously-placed US limit order in full. Matches
    /// kis_client.mjs::cancelOverseasOrderById (RVSE_CNCL_DVSN_CD "02") — this is a real KIS
    /// order-rvsecncl call, not a client-side no-op; KIS itself issues a new order number for
    /// the cancel action (see <see cref="KisUsCancelModifyResult"/>'s doc comment).
    /// </summary>
    public async Task<KisUsCancelModifyResult> CancelOrderAsync(
        string originalOrderNo, string symbol, int qty, string exchange = DefaultExchange,
        CancellationToken ct = default) =>
        await RvseCnclAsync(originalOrderNo, symbol, qty, price: 0, exchange, rvseCnclDvsnCd: "02", ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Modifies (지정가 정정) a previously-placed US limit order's quantity/price. Matches
    /// kis_client.mjs::modifyOverseasOrder (RVSE_CNCL_DVSN_CD "01") — same real-order-rvsecncl
    /// caveat as <see cref="CancelOrderAsync"/>.
    /// </summary>
    public async Task<KisUsCancelModifyResult> ModifyOrderAsync(
        string originalOrderNo, string symbol, int qty, decimal price, string exchange = DefaultExchange,
        CancellationToken ct = default) =>
        await RvseCnclAsync(originalOrderNo, symbol, qty, price, exchange, rvseCnclDvsnCd: "01", ct)
            .ConfigureAwait(false);

    private async Task<KisUsCancelModifyResult> RvseCnclAsync(
        string originalOrderNo, string symbol, int qty, decimal price, string exchange,
        string rvseCnclDvsnCd, CancellationToken ct)
    {
        var trId = _isPaper ? RvseCnclTrIdPaper : RvseCnclTrIdReal;
        var body = new KisOverseasRvseCnclRequestBody(
            Cano: _config.AccountNo,
            AcntPrdtCd: _config.ProductCode,
            OvrsExcgCd: exchange,
            Pdno: symbol,
            OrgnOdno: originalOrderNo,
            RvseCnclDvsnCd: rvseCnclDvsnCd,
            OrdQty: qty.ToString(CultureInfo.InvariantCulture),
            OvrsOrdUnpr: price.ToString(CultureInfo.InvariantCulture),
            MgcoAptmOdno: "",
            OrdSvrDvsnCd: "0");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_orderOptions.HttpTimeout);

        KisOverseasOrderResponse? parsed;
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
                return System.Text.Json.JsonSerializer.Deserialize<KisOverseasOrderResponse>(raw);
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new KisUsCancelModifyResult(
                false, null, "KIS US modify/cancel request timed out; outcome may be ambiguous",
                "KIS_ORDER_TIMEOUT", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            return new KisUsCancelModifyResult(
                false, null, $"KIS US modify/cancel response failed: {ex.Message}",
                "KIS_ORDER_TRANSPORT", null);
        }

        if (parsed?.ReturnCode != "0")
        {
            return new KisUsCancelModifyResult(
                Ok: false, OrderNo: null, ErrorMessage: parsed?.Message ?? "Unknown error",
                ReturnCode: parsed?.ReturnCode, MessageCode: parsed?.MessageCode);
        }

        if (rvseCnclDvsnCd == "01" && string.IsNullOrWhiteSpace(parsed.Output?.OrderNo))
        {
            return new KisUsCancelModifyResult(
                false, null, "KIS US modify succeeded without a replacement order number",
                "KIS_ORDER_ID_MISSING", parsed.MessageCode);
        }

        return new KisUsCancelModifyResult(
            Ok: true, OrderNo: parsed.Output?.OrderNo, ErrorMessage: null,
            ReturnCode: parsed.ReturnCode, MessageCode: parsed.MessageCode);
    }

    /// <summary>
    /// Unfilled/partially-filled order query. Matches kis_client.mjs::getOverseasUnfilledOrders
    /// (inquire-nccs).
    /// </summary>
    public async Task<IReadOnlyList<KisUsUnfilledOrder>> GetUnfilledOrdersAsync(
        string exchange = DefaultExchange, CancellationToken ct = default)
    {
        if (_lastGoodUnfilled is not null &&
            _timeProvider.GetUtcNow() - _lastGoodUnfilledAt <= ReadCacheTtl)
            return _lastGoodUnfilled;
        var token = await _oauth.GetAccessTokenAsync(ct).ConfigureAwait(false);
        var trId = _isPaper ? UnfilledTrIdPaper : UnfilledTrIdReal;

        var query = new Dictionary<string, string>
        {
            ["CANO"] = _config.AccountNo,
            ["ACNT_PRDT_CD"] = _config.ProductCode,
            ["OVRS_EXCG_CD"] = exchange,
            ["SORT_SQN"] = "DS",
            ["CTX_AREA_FK200"] = "",
            ["CTX_AREA_NK200"] = "",
        };

        KisOverseasUnfilledResponse? parsed = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var request = BuildGetRequest(InquireUnfilledPath, query);
            AddAuthHeaders(request, token, trId);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            parsed = System.Text.Json.JsonSerializer.Deserialize<KisOverseasUnfilledResponse>(raw);
            if (parsed?.ReturnCode == "0") break;
            if (attempt < 2) await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }

        if (parsed?.ReturnCode != "0")
        {
            if (_lastGoodUnfilled is not null &&
                _timeProvider.GetUtcNow() - _lastGoodUnfilledAt <= ReadCacheTtl)
                return _lastGoodUnfilled;
            throw new InvalidOperationException(
                $"KIS US unfilled-orders query failed: {parsed?.Message ?? "Unknown error"}");
        }

        var result = (parsed.Output ?? [])
            .Select(o => new KisUsUnfilledOrder(
                OrderNo: o.Odno ?? "",
                Ticker: o.Pdno ?? "",
                Name: o.PrdtName,
                Side: o.SllBuyDvsnCd == "02" ? "buy" : "sell",
                OrderQty: ParseDecimal(o.FtOrdQty ?? o.OrdQty),
                FilledQty: ParseDecimal(o.FtCcldQty ?? o.TotCcldQty),
                OrderPrice: ParseDecimal(o.FtOrdUnpr3 ?? o.OrdUnpr3),
                Exchange: o.OvrsExcgCd ?? exchange,
                OrderDate: o.OrdDt,
                OrderTime: o.OrdTmd))
            .ToList();
        _lastGoodUnfilled = result;
        _lastGoodUnfilledAt = _timeProvider.GetUtcNow();
        return result;
    }

    /// <summary>
    /// Order status/fill query (also doubles as order status, same as the KR client — one row
    /// per order with both order and filled quantities). Matches
    /// kis_client.mjs::getOverseasFilledOrders (inquire-ccnl), including its client-side
    /// filtering (needed because paper accounts ignore server-side filter params) and its
    /// CTX_AREA_NK200 pagination, capped at 10 pages.
    /// </summary>
    public async Task<IReadOnlyList<KisUsFilledOrder>> GetFilledOrdersAsync(
        string? ticker = null, string? exchange = null, string? odno = null, string? side = null,
        string? status = null, string? startDate = null, string? endDate = null,
        CancellationToken ct = default)
    {
        var trId = _isPaper ? CcnlTrIdPaper : CcnlTrIdReal;
        var today = TodayKisBusinessDate();
        startDate ??= today;
        endDate ??= today;

        var sideCode = side switch { "buy" => "02", "sell" => "01", _ => "00" };
        var statusCode = status switch { "filled" => "01", "unfilled" => "02", _ => "00" };

        var query = new Dictionary<string, string>
        {
            ["CANO"] = _config.AccountNo,
            ["ACNT_PRDT_CD"] = _config.ProductCode,
            // Paper accounts ignore these filter params and full-query regardless — mirrors
            // getOverseasFilledOrders's isPaper branch exactly.
            ["PDNO"] = _isPaper ? "" : (ticker ?? "%"),
            ["ORD_STRT_DT"] = startDate,
            ["ORD_END_DT"] = endDate,
            ["SLL_BUY_DVSN"] = _isPaper ? "00" : sideCode,
            ["CCLD_NCCS_DVSN"] = _isPaper ? "00" : statusCode,
            ["OVRS_EXCG_CD"] = _isPaper ? "" : (exchange ?? ""),
            ["SORT_SQN"] = "DS",
            // KIS paper trading requires ORD_DT even though the real-account examples
            // commonly leave it blank when a start/end range is supplied.  An empty value
            // is rejected during regular US hours with an input-validation error, which in
            // turn prevents LEAN from observing otherwise successful fills.
            ["ORD_DT"] = today,
            ["ORD_GNO_BRNO"] = "",
            ["ODNO"] = "",
            ["CTX_AREA_NK200"] = "",
            ["CTX_AREA_FK200"] = "",
        };

        var all = new List<KisOverseasCcnlRow>();
        for (var page = 0; page < 10; page++)
        {
            var token = await _oauth.GetAccessTokenAsync(ct).ConfigureAwait(false);
            using var request = BuildGetRequest(InquireCcnlPath, query);
            AddAuthHeaders(request, token, trId);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = System.Text.Json.JsonSerializer.Deserialize<KisOverseasCcnlResponse>(raw);

            if (parsed?.ReturnCode != "0")
            {
                throw new InvalidOperationException(
                    $"KIS US filled-orders query failed: {parsed?.Message ?? "Unknown error"}");
            }

            all.AddRange(parsed.Output ?? []);

            var next = parsed.CtxAreaNk200?.Trim();
            if (string.IsNullOrEmpty(next))
            {
                break;
            }
            query["CTX_AREA_NK200"] = next;
            query["CTX_AREA_FK200"] = parsed.CtxAreaFk200 ?? "";
        }

        IEnumerable<KisUsFilledOrder> orders = all.Select(o =>
        {
            var orderQty = ParseDecimal(o.FtOrdQty ?? o.OrdQty);
            var filledQty = ParseDecimal(o.FtCcldQty ?? o.TotCcldQty);
            return new KisUsFilledOrder(
                OrderNo: o.Odno ?? "",
                OriginalOrderNo: o.OrgnOdno,
                Ticker: o.Pdno ?? "",
                Name: o.PrdtName,
                Exchange: o.OvrsExcgCd,
                Side: o.SllBuyDvsnCd == "02" ? "buy" : (o.SllBuyDvsnCd == "01" ? "sell" : null),
                OrderQty: orderQty,
                FilledQty: filledQty,
                OrderPrice: ParseDecimal(o.FtOrdUnpr3 ?? o.OrdUnpr3 ?? o.OrdUnpr),
                AverageFilledPrice: ParseDecimal(o.FtCcldUnpr3 ?? o.AvgUnpr ?? o.CcldUnpr),
                FilledAmount: ParseDecimal(o.FtCcldAmt3 ?? o.TotCcldAmt),
                OrderDate: o.OrdDt,
                OrderTime: o.OrdTmd);
        });

        // Client-side filtering, same as getOverseasFilledOrders (needed for paper accounts,
        // harmless post-filter for real accounts too).
        if (!string.IsNullOrEmpty(ticker)) orders = orders.Where(o => o.Ticker == ticker);
        if (!string.IsNullOrEmpty(exchange) && _isPaper) orders = orders.Where(o => o.Exchange == null || o.Exchange == exchange);
        if (!string.IsNullOrEmpty(side)) orders = orders.Where(o => o.Side == side);
        if (status == "filled") orders = orders.Where(o => o.FilledQty > 0);
        if (status == "unfilled") orders = orders.Where(o => o.FilledQty < o.OrderQty);
        if (!string.IsNullOrEmpty(odno))
        {
            orders = orders.Where(o =>
                OrderNumbersEqual(o.OrderNo, odno) || OrderNumbersEqual(o.OriginalOrderNo, odno));
        }

        return orders.ToList();
    }

    // KIS order-submit responses use a zero-padded ODNO (for example "0000035674"), while
    // overseas paper fill/open-order ledgers can return the same number without leading zeroes
    // ("35674"). Keep the original value for modify/cancel POSTs, but compare canonical forms
    // whenever joining read-only ledgers back to a submitted order.
    internal static bool OrderNumbersEqual(string? left, string? right) =>
        NormalizeOrderNumber(left) == NormalizeOrderNumber(right);

    private static string NormalizeOrderNumber(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return string.Empty;
        var normalized = trimmed.TrimStart('0');
        return normalized.Length == 0 ? "0" : normalized;
    }

    /// <summary>
    /// Balance/holdings query. Matches kis_client.mjs::getOverseasBalance: per-exchange
    /// inquire-balance calls (NASD/NYSE/AMEX, de-duplicated by symbol — paper accounts return
    /// all USD holdings regardless of the exchange filter) for holdings, plus
    /// inquire-present-balance for the USD cash/available-cash summary, including its
    /// paper-account KRW-seed and KRW-denominated-usable-cash quirks.
    /// </summary>
    public async Task<KisUsBalanceResult> GetBalanceAsync(CancellationToken ct = default)
    {
        if (TryGetCachedBalance(out var recent)) return recent;
        var balanceTrId = _isPaper ? BalanceTrIdPaper : BalanceTrIdReal;
        var holdingsBySymbol = new Dictionary<string, KisUsHolding>();
        decimal? cumulativePnlKrw = null;

        foreach (var excg in BalanceExchanges)
        {
            var token = await _oauth.GetAccessTokenAsync(ct).ConfigureAwait(false);
            var query = new Dictionary<string, string>
            {
                ["CANO"] = _config.AccountNo,
                ["ACNT_PRDT_CD"] = _config.ProductCode,
                ["OVRS_EXCG_CD"] = excg,
                ["TR_CRCY_CD"] = "USD",
                ["CTX_AREA_FK200"] = "",
                ["CTX_AREA_NK200"] = "",
            };

            // Each exchange in the loop is its own KIS HTTP call — throttled individually
            // (not just once around the whole GetBalanceAsync call) or all three plus the
            // present-balance call below fire back-to-back well under KIS's per-second cap.
            // The injected gate is the process-wide shared gate in production and a deterministic
            // zero-delay gate in tests; either way it is the same instance used by this client.
            KisOverseasBalanceResponse? parsed = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                parsed = await _callGate.ExecuteAsync(async innerCt =>
                {
                    using var request = BuildGetRequest(InquireBalancePath, query);
                    AddAuthHeaders(request, token, balanceTrId);
                    using var response = await _http.SendAsync(request, innerCt).ConfigureAwait(false);
                    var raw = await response.Content.ReadAsStringAsync(innerCt).ConfigureAwait(false);
                    return System.Text.Json.JsonSerializer.Deserialize<KisOverseasBalanceResponse>(raw);
                }, ct).ConfigureAwait(false);
                if (parsed?.ReturnCode == "0") break;
                if (attempt < 2) await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }

            if (parsed?.ReturnCode != "0")
            {
                if (TryGetCachedBalance(out var cached)) return cached;
                return new KisUsBalanceResult(
                    false, holdingsBySymbol.Values.ToList(), 0, 0, 0, 0,
                    $"{excg} balance error: {parsed?.Message ?? "Unknown error"}");
            }

            foreach (var row in parsed.Output1 ?? [])
            {
                var qty = ParseDecimal(row.OvrsCblcQty);
                if (qty <= 0 || row.OvrsPdno is null || holdingsBySymbol.ContainsKey(row.OvrsPdno))
                {
                    continue;
                }

                holdingsBySymbol[row.OvrsPdno] = new KisUsHolding(
                    Symbol: row.OvrsPdno,
                    Name: row.OvrsItemName ?? "",
                    Exchange: row.OvrsExcgCd ?? excg,
                    Quantity: qty,
                    AveragePrice: ParseDecimal(row.PchsAvgPric),
                    CurrentPrice: ParseDecimal(row.NowPric2),
                    PnlPercent: ParseDecimal(row.EvluPflsRt),
                    PnlAmount: ParseDecimal(row.FrcrEvluPflsAmt),
                    EvalAmount: ParseDecimal(row.OvrsStckEvluAmt));
            }

            if (ParseBalanceCumulativePnl(parsed.Output2) is { } totalPnl)
                cumulativePnlKrw = totalPnl;
        }

        var holdings = holdingsBySymbol.Values.ToList();

        var presentToken = await _oauth.GetAccessTokenAsync(ct).ConfigureAwait(false);
        var presentTrId = _isPaper ? PresentBalanceTrIdPaper : PresentBalanceTrIdReal;
        var presentQuery = new Dictionary<string, string>
        {
            ["CANO"] = _config.AccountNo,
            ["ACNT_PRDT_CD"] = _config.ProductCode,
            ["WCRC_FRCR_DVSN_CD"] = "02",
            ["NATN_CD"] = "840",
            ["TR_MKET_CD"] = "00",
            ["INQR_DVSN_CD"] = "00",
        };

        // Same throttle as the per-exchange loop above — this is the 4th KIS HTTP call this
        // method makes, right after the 3rd exchange, so it needs the shared gate just as much.
        var present = await _callGate.ExecuteAsync(async innerCt =>
        {
            using var presentRequest = BuildGetRequest(InquirePresentBalancePath, presentQuery);
            AddAuthHeaders(presentRequest, presentToken, presentTrId);
            using var presentResponse = await _http.SendAsync(presentRequest, innerCt).ConfigureAwait(false);
            var presentRaw = await presentResponse.Content.ReadAsStringAsync(innerCt).ConfigureAwait(false);
            return System.Text.Json.JsonSerializer.Deserialize<KisOverseasPresentBalanceResponse>(presentRaw);
        }, ct).ConfigureAwait(false);

        if (present?.ReturnCode != "0")
        {
            // KIS paper present-balance intermittently returns a gateway-routing error during
            // the regular US session even though balance and order endpoints remain healthy.
            // The official buying-power endpoint is the authoritative operational fallback.
            if (_isPaper)
            {
                var fallback = await GetPaperBuyingPowerAsync(ct).ConfigureAwait(false);
                if (fallback.Ok)
                {
                    var holdingsEval = holdings.Sum(h => h.EvalAmount);
                    return CacheBalance(new KisUsBalanceResult(
                        true, holdings, fallback.Cash, holdingsEval + fallback.Cash,
                        fallback.Cash, fallback.ExchangeRate, null,
                        fallback.ExchangeRate > 0m && cumulativePnlKrw is { } fallbackPnl
                            ? fallbackPnl / fallback.ExchangeRate
                            : null));
                }
            }
            if (TryGetCachedBalance(out var cached)) return cached;
            return new KisUsBalanceResult(false, holdings, 0, 0, 0, 0, present?.Message ?? "present-balance error");
        }

        var usdRow = present.Output2?.FirstOrDefault(c => c.CrcyCd == "USD");
        var usdCash = ParseDecimal(usdRow?.FrcrDnclAmt2);
        var usdToKrw = ParseDecimal(usdRow?.FrstBltnExrt);

        var (usdUsePsblRaw, totAsstAmt, frcrEvluTota) = ParseAvailCash(present.Output3);

        var holdingsEvalUsd = holdings.Sum(h => h.EvalAmount);
        var krwTotal = totAsstAmt > 0 ? totAsstAmt : frcrEvluTota;
        var reportedNavUsd = usdToKrw > 0 && krwTotal > 0 ? krwTotal / usdToKrw : 0m;
        var usdEval = reportedNavUsd > 0 ? reportedNavUsd : holdingsEvalUsd + usdCash;

        // Paper accounts can keep frcr_dncl_amt_2=0 even after a USD holding exists (the seed
        // remains KRW-denominated). Reconstruct cash from output3's KRW total NAV less the
        // separately queried USD holdings. Testing usdCash, rather than usdEval, matters here:
        // a holding makes usdEval non-zero and previously suppressed this fallback entirely.
        if (_isPaper && usdCash == 0 && usdToKrw > 0)
        {
            if (krwTotal > 0)
            {
                var totalNavUsd = krwTotal / usdToKrw;
                usdCash = Math.Max(0, totalNavUsd - holdingsEvalUsd);
                usdEval = holdingsEvalUsd + usdCash;
            }
        }

        // usd_use_psbl sometimes comes back KRW-denominated (ratio check vs. the USD eval
        // estimate), matching the Node reference's magnitude-heuristic correction.
        var usdUsePsbl = usdUsePsblRaw;
        if (usdToKrw > 0 && usdEval > 0 && usdUsePsblRaw > usdEval * 100)
        {
            usdUsePsbl = usdUsePsblRaw / usdToKrw;
        }
        if (_isPaper && usdUsePsbl == 0 && usdCash > 0)
        {
            usdUsePsbl = usdCash;
        }

        return CacheBalance(new KisUsBalanceResult(
            Ok: true,
            Holdings: holdings,
            UsdCash: usdCash,
            UsdEval: usdEval,
            UsdUsePsbl: usdUsePsbl,
            UsdToKrw: usdToKrw,
            ErrorMessage: null,
            CumulativePnlUsd: usdToKrw > 0m && cumulativePnlKrw is { } finalPnl
                ? finalPnl / usdToKrw
                : null));
    }

    private KisUsBalanceResult CacheBalance(KisUsBalanceResult balance)
    {
        _lastGoodBalance = balance;
        _lastGoodBalanceAt = _timeProvider.GetUtcNow();
        return balance;
    }

    private bool TryGetCachedBalance(out KisUsBalanceResult balance)
    {
        if (_lastGoodBalance is not null &&
            _timeProvider.GetUtcNow() - _lastGoodBalanceAt <= ReadCacheTtl)
        {
            balance = _lastGoodBalance;
            return true;
        }
        balance = null!;
        return false;
    }

    private async Task<(bool Ok, decimal Cash, decimal ExchangeRate)> GetPaperBuyingPowerAsync(
        CancellationToken ct)
    {
        var token = await _oauth.GetAccessTokenAsync(ct).ConfigureAwait(false);
        var query = new Dictionary<string, string>
        {
            ["CANO"] = _config.AccountNo,
            ["ACNT_PRDT_CD"] = _config.ProductCode,
            ["OVRS_EXCG_CD"] = "NASD",
            ["OVRS_ORD_UNPR"] = "100",
            ["ITEM_CD"] = "AAPL",
        };
        var parsed = await _callGate.ExecuteAsync(async innerCt =>
        {
            using var request = BuildGetRequest(InquirePsAmountPath, query);
            AddAuthHeaders(request, token, PsAmountTrIdPaper);
            using var response = await _http.SendAsync(request, innerCt).ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync(innerCt).ConfigureAwait(false);
            return System.Text.Json.JsonSerializer.Deserialize<KisOverseasPsAmountResponse>(raw);
        }, ct).ConfigureAwait(false);

        if (parsed?.ReturnCode != "0") return (false, 0, 0);
        var usdOnly = ParseDecimal(parsed.Output?.OrdPsblFrcrAmt);
        var withConversion = ParseDecimal(parsed.Output?.FrcrOrdPsblAmt1);
        return (true, Math.Max(usdOnly, withConversion), ParseDecimal(parsed.Output?.ExchangeRate));
    }

    // output3 (해외주식 사용가능금액) comes back as either a single object or an array
    // depending on the response — same quirk kis_client.mjs works around.
    private static (decimal UsdUsePsblRaw, decimal TotAsstAmt, decimal FrcrEvluTota) ParseAvailCash(
        System.Text.Json.JsonElement? output3)
    {
        if (output3 is not { } el || el.ValueKind == System.Text.Json.JsonValueKind.Undefined
            || el.ValueKind == System.Text.Json.JsonValueKind.Null)
        {
            return (0, 0, 0);
        }

        System.Text.Json.JsonElement? row = null;
        if (el.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (item.TryGetProperty("crcy_cd", out var crcy) && crcy.GetString() == "USD")
                {
                    row = item;
                    break;
                }
            }
            row ??= el.EnumerateArray().Cast<System.Text.Json.JsonElement?>().FirstOrDefault();
        }
        else if (el.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            row = el;
        }

        if (row is not { } r)
        {
            return (0, 0, 0);
        }

        static decimal Field(System.Text.Json.JsonElement r, string name) =>
            r.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? ParseDecimal(v.GetString())
                : 0m;

        return (Field(r, "frcr_use_psbl_amt"), Field(r, "tot_asst_amt"), Field(r, "frcr_evlu_tota"));
    }

    private static decimal? ParseBalanceCumulativePnl(System.Text.Json.JsonElement? output2)
    {
        if (output2 is not { } element || element.ValueKind is
            System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined)
            return null;

        System.Text.Json.JsonElement? row = element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Object => element,
            System.Text.Json.JsonValueKind.Array when element.GetArrayLength() > 0 => element[0],
            _ => null
        };
        if (row is not { } value ||
            !value.TryGetProperty("tot_evlu_pfls_amt", out var field) ||
            field.ValueKind != System.Text.Json.JsonValueKind.String)
            return null;
        return ParseDecimal(field.GetString());
    }

    // KIS's inquire-price returns ReturnCode "0" (success) with Output.Last "0"/empty rather
    // than an error when SYMB isn't actually listed on the given EXCD — discovered live-testing
    // real held US symbols: NASDAQ-listed ones (e.g. TTWO, CPRT) work fine against the
    // DefaultPriceExchange "NAS", but NYSE-listed ones (e.g. SPY, AMCR, ANET, AME, AEE) silently
    // came back with a real (Ok=true) response and Last=0. There is no per-holding
    // exchange-of-listing metadata available to this LEAN plugin (unlike the Node reference's
    // resolveExchange, which is fed by an operator-maintained watchlist config this repo does
    // not port), so on a Last=0/no-data response this tries the other two known KIS price
    // exchange codes in turn (mirrors resolveExchange's NAS/NYS/AMS mapping in
    // market_adapter_us.mjs) and returns the first one with real (non-zero) data.
    private static readonly string[] KnownPriceExchanges = [DefaultPriceExchange, "NYS", "AMS"];

    /// <summary>
    /// Current-price quote for a single US/overseas symbol. Matches
    /// kis_client.mjs::getOverseasPrice — GET overseas-price/quotations/price, tr_id
    /// HHDFS00000300, AUTH left blank per the reference implementation. Used by
    /// <see cref="KisBrokerage"/>'s <c>IDataQueueHandler</c> REST-polling implementation (see
    /// KisBrokerage.DataQueueHandler.cs) — this is a plain read-only GET, no order side effects,
    /// safe to poll on an interval.
    ///
    /// When <paramref name="exchange"/> is not given, tries each known price-exchange code (see
    /// <see cref="KnownPriceExchanges"/>) until one returns real data — this can make up to 3 KIS
    /// HTTP calls, each individually throttled through <see cref="KisRateLimiter"/> (not wrapped
    /// as a single outer throttle acquisition — see that type's doc comment for why nesting a
    /// non-reentrant gate around a call that also uses it internally would deadlock).
    /// </summary>
    public async Task<KisUsQuoteResult> GetQuoteAsync(
        string symbol, string? exchange = null, CancellationToken ct = default)
    {
        var candidates = exchange != null ? [exchange] : KnownPriceExchanges;
        KisUsQuoteResult? last = null;
        foreach (var excd in candidates)
        {
            var result = await QuerySingleExchangeQuoteAsync(symbol, excd, ct).ConfigureAwait(false);
            if (result.Ok && result.Last > 0)
            {
                return result;
            }
            last = result;
        }

        return last!;
    }

    /// <summary>
    /// Returns adjusted US daily OHLCV rows in ascending date order. The public market-data TR
    /// <c>HHDFS76240000</c> is the same on paper and real endpoints. When the listing exchange
    /// is unknown, NAS/NYS/AMS are tried in turn because KIS can return success with no usable
    /// rows for a symbol queried against the wrong price-exchange code.
    /// </summary>
    public async Task<IReadOnlyList<KisDailyBar>> GetDailyBarsAsync(
        string symbol, DateOnly startDate, DateOnly endDate, string? exchange = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("symbol is required", nameof(symbol));
        }
        if (endDate < startDate)
        {
            throw new ArgumentException("endDate must be on or after startDate", nameof(endDate));
        }

        var candidates = exchange is null ? KnownPriceExchanges : [exchange];
        InvalidOperationException? lastError = null;
        var observedSuccessfulEmptyResponse = false;

        foreach (var excd in candidates)
        {
            try
            {
                var bars = await GetDailyBarsForExchangeAsync(
                    symbol, excd, startDate, endDate, ct).ConfigureAwait(false);
                if (bars.Count > 0)
                {
                    return bars;
                }
                observedSuccessfulEmptyResponse = true;
            }
            catch (InvalidOperationException ex)
            {
                lastError = ex;
            }
        }

        if (!observedSuccessfulEmptyResponse && lastError is not null)
        {
            throw lastError;
        }
        return [];
    }

    private async Task<IReadOnlyList<KisDailyBar>> GetDailyBarsForExchangeAsync(
        string symbol, string exchange, DateOnly startDate, DateOnly endDate, CancellationToken ct)
    {
        var bars = new Dictionary<DateOnly, KisDailyBar>();
        var cursor = endDate;

        for (var page = 0; page < DailyPriceMaxPages && cursor >= startDate; page++)
        {
            var parsed = await _callGate.ExecuteAsync(async innerCt =>
            {
                var token = await _oauth.GetAccessTokenAsync(innerCt).ConfigureAwait(false);
                var query = new Dictionary<string, string>
                {
                    ["AUTH"] = "",
                    ["EXCD"] = exchange,
                    ["SYMB"] = symbol,
                    ["GUBN"] = "0",
                    ["BYMD"] = cursor.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                    ["MODP"] = "0",
                };

                using var request = BuildGetRequest(InquireDailyPricePath, query);
                AddAuthHeaders(request, token, DailyPriceTrId);
                using var response = await _http.SendAsync(request, innerCt).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var raw = await response.Content.ReadAsStringAsync(innerCt).ConfigureAwait(false);
                return System.Text.Json.JsonSerializer.Deserialize<KisOverseasDailyPriceResponse>(raw);
            }, ct).ConfigureAwait(false);

            if (parsed?.ReturnCode != "0")
            {
                throw new InvalidOperationException(
                    $"KIS US daily-price query failed for {exchange}: " +
                    (parsed?.Message ?? "Unknown error"));
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

    private async Task<KisUsQuoteResult> QuerySingleExchangeQuoteAsync(
        string symbol, string exchange, CancellationToken ct)
    {
        var token = await _oauth.GetAccessTokenAsync(ct).ConfigureAwait(false);
        var query = new Dictionary<string, string>
        {
            ["AUTH"] = "",
            ["EXCD"] = exchange,
            ["SYMB"] = symbol,
        };

        var parsed = await _callGate.ExecuteAsync(async innerCt =>
        {
            using var request = BuildGetRequest(InquirePricePath, query);
            AddAuthHeaders(request, token, PriceTrId);
            using var response = await _http.SendAsync(request, innerCt).ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync(innerCt).ConfigureAwait(false);
            return System.Text.Json.JsonSerializer.Deserialize<KisOverseasPriceResponse>(raw);
        }, ct).ConfigureAwait(false);

        if (parsed?.ReturnCode != "0")
        {
            return new KisUsQuoteResult(false, symbol, exchange, 0, 0, parsed?.Message ?? "Unknown error");
        }

        return new KisUsQuoteResult(
            Ok: true,
            Symbol: symbol,
            Exchange: exchange,
            Last: ParseDecimal(parsed.Output?.Last),
            Volume: ParseDecimal(parsed.Output?.Tvol),
            ErrorMessage: null);
    }

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

    private string TodayKisBusinessDate() => KisBusinessDate(_timeProvider.GetUtcNow());

    internal static string KisBusinessDate(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(
        instant, KisBusinessTimeZone).ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
