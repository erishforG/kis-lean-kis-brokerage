using QuantConnect.Brokerages;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.KisBrokerage;

/// <summary>
/// Minimal paper-smoke brokerage model. A zero fee avoids LEAN's default Interactive Brokers
/// equity fee model, which does not support KRX. This is not a production fee/tax model.
/// </summary>
public sealed class KisBrokerageModel : DefaultBrokerageModel
{
    public KisBrokerageModel()
        : base(AccountType.Cash)
    {
    }

    public override IFeeModel GetFeeModel(Security security) => new ConstantFeeModel(0m);

    // KIS exposes domestic sale proceeds as immediately reusable buying power.  LEAN's default
    // cash-account equity model settles sales later, which left the KR CashBook unchanged after
    // sells and rejected later buys in the same rebalance even though KIS reported the cash.
    // Keep the default delayed settlement behavior for US equities, whose paper cash is known to
    // move asynchronously around settlement.
    public override ISettlementModel GetSettlementModel(Security security) =>
        UsesImmediateSettlement(security.Symbol.ID.Market)
            ? new ImmediateSettlementModel()
            : base.GetSettlementModel(security);

    internal static bool UsesImmediateSettlement(string market) => market == Market.KRX;
}
