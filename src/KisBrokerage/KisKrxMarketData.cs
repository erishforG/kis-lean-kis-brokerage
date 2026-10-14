using NodaTime;
using QuantConnect.Securities;

namespace QuantConnect.KisBrokerage;

/// <summary>
/// Registers a <see cref="Market.KRX"/> (Korea Exchange) equity entry into LEAN's runtime
/// <see cref="MarketHoursDatabase"/> and <see cref="SymbolPropertiesDatabase"/> singletons.
///
/// <b>Why this exists</b> — confirmed against a real LEAN Launcher run (source-built engine,
/// real KIS paper credentials, KisBrokerage.Connect()/balance query succeeding for real):
/// vanilla/stock LEAN's open-source Data folder ships no "krx" entry in either
/// market-hours-database.json or symbol-properties-database.csv (only "usa", "fxcm", etc. have
/// the generic "[*]" wildcard fallback rows). When LEAN's BrokerageSetupHandler auto-adds the
/// real paper account's existing KR holding (e.g. ticker 067290) via
/// <c>QCAlgorithm.AddSecurity</c>, that throws:
/// <c>System.ArgumentException: Unable to locate exchange hours for Equity-krx-067290</c>
/// out of <see cref="MarketHoursDatabase.GetEntry(string, string, SecurityType)"/>. LEAN's own
/// <c>Extensions.GetOrAddUnrequestedSecurity</c> helper swallows that exception's caller-visible
/// effect (AddSecurity logs the error and returns a null Security) but still reports success,
/// so <c>BrokerageSetupHandler.LoadExistingHoldingsAndOrders</c> goes on to dereference the null
/// Security a few lines later — turning the ArgumentException into a masked
/// NullReferenceException ("Object reference not set to an instance of an object.") instead.
/// Either way, the algorithm never reaches a connected state and no real tick data ever flows.
///
/// <b>Why a runtime API instead of a Data-folder patch</b> — both
/// <see cref="MarketHoursDatabase.SetEntry"/> and <see cref="SymbolPropertiesDatabase.SetEntry"/>
/// are public APIs LEAN itself documents as "intended to be used by custom data and other data
/// sources that don't have explicit entries" in the shipped Data folder. Registering here makes
/// this plugin self-contained: an operator who drops this DLL into a LEAN deployment does not
/// need to hand-edit LEAN's Data/market-hours or Data/symbol-properties files. This mirrors the
/// wildcard-symbol pattern LEAN's own Data folder uses for e.g. "usa" equities (see
/// Data/symbol-properties/symbol-properties-database.csv's "usa,[*],equity,,USD,1,0.01,1" row) —
/// passing a null/empty symbol to SetEntry maps to <see cref="SecurityDatabaseKey.Wildcard"/>
/// ("[*]"), so this entry matches any KR equity ticker that doesn't already have a more specific
/// one.
///
/// <b>Known simplifications</b> (documented here rather than silently assumed):
/// <list type="bullet">
/// <item>Hours model Mon-Fri 09:00-15:30 KST with no holiday calendar. Real KRX regular-session
/// hours are exactly this (KRX dropped its midday break in 2000), but this entry does not know
/// about KRX-specific market holidays (e.g. Lunar New Year, Chuseok) — LEAN will treat those as
/// open trading days. Fine for a live-data smoke test; would need a maintained holiday list for
/// accurate backtesting/scheduling around KRX holidays.</item>
/// <item><see cref="SymbolProperties.MinimumPriceVariation"/> is set to a flat 1 KRW. Real KRX
/// tick sizes step through several price bands (1 KRW up to ~1,000 KRW depending on price), which
/// LEAN's flat-per-market SymbolProperties model cannot express; this is the same simplification
/// LEAN's own generic "[*]" wildcard rows for other markets make.</item>
/// </list>
/// </summary>
internal static class KisKrxMarketData
{
    // A static field initializer runs at most once per AppDomain, the first time any static
    // member of this type is touched, and the CLR guarantees that is thread-safe — so this is a
    // simple, allocation-free "run exactly once" guard without needing a lock/volatile flag of
    // our own. See EnsureRegistered() below for the actual call site convention.
    private static readonly bool s_registered = Register();

    /// <summary>
    /// No-op touch point that guarantees <see cref="Register"/> has already run. Call this from
    /// every <see cref="KisBrokerage"/> constructor, before any code path that might resolve a
    /// "krx" market Equity security — in particular, before LEAN's BrokerageSetupHandler
    /// auto-adds the real account's existing KR holdings during Setup().
    /// </summary>
    public static void EnsureRegistered() => _ = s_registered;

    private static bool Register()
    {
        var seoul = DateTimeZoneProviders.Tzdb["Asia/Seoul"];
        var marketOpen = new TimeSpan(9, 0, 0);
        var marketClose = new TimeSpan(15, 30, 0);

        var hoursByDay = new Dictionary<DayOfWeek, LocalMarketHours>
        {
            { DayOfWeek.Sunday, LocalMarketHours.ClosedAllDay(DayOfWeek.Sunday) },
            { DayOfWeek.Monday, new LocalMarketHours(DayOfWeek.Monday, marketOpen, marketClose) },
            { DayOfWeek.Tuesday, new LocalMarketHours(DayOfWeek.Tuesday, marketOpen, marketClose) },
            { DayOfWeek.Wednesday, new LocalMarketHours(DayOfWeek.Wednesday, marketOpen, marketClose) },
            { DayOfWeek.Thursday, new LocalMarketHours(DayOfWeek.Thursday, marketOpen, marketClose) },
            { DayOfWeek.Friday, new LocalMarketHours(DayOfWeek.Friday, marketOpen, marketClose) },
            { DayOfWeek.Saturday, LocalMarketHours.ClosedAllDay(DayOfWeek.Saturday) },
        };

        var exchangeHours = new SecurityExchangeHours(
            seoul,
            holidayDates: Enumerable.Empty<DateTime>(),
            marketHoursForEachDayOfWeek: hoursByDay,
            earlyCloses: new Dictionary<DateTime, TimeSpan>(),
            lateOpens: new Dictionary<DateTime, TimeSpan>());

        // symbol: null => SecurityDatabaseKey.Wildcard ("[*]") — see the class doc comment.
        MarketHoursDatabase.FromDataFolder().SetEntry(Market.KRX, null, SecurityType.Equity, exchangeHours, seoul);

        var symbolProperties = new SymbolProperties(
            description: "Korea Exchange (KRX) equity",
            quoteCurrency: "KRW",
            contractMultiplier: 1,
            minimumPriceVariation: 1,
            lotSize: 1,
            marketTicker: "KRX");

        SymbolPropertiesDatabase.FromDataFolder().SetEntry(Market.KRX, null, SecurityType.Equity, symbolProperties);

        return true;
    }
}
