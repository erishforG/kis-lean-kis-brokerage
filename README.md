# KIS Brokerage for QuantConnect LEAN

An open-source QuantConnect LEAN brokerage adapter for Korea Investment & Securities (KIS),
with support for KIS overseas stock trading (including US equities) and Korean equities.

This repository contains only the broker integration layer. It does not include a trading
strategy, account configuration, OpenClaw automation, credentials, or personal operating data.

## Scope

- KIS OAuth token caching and configurable paper/real-account authentication
- LEAN `BrokerageFactory` integration
- US/overseas and KRX equity balances, holdings, open orders, fills, and daily history
- Limit-order submission, modification, cancellation, and fill synchronization
- REST quote polling through LEAN's `IDataQueueHandler`
- Shared KIS rate limiting and bounded request/reconciliation retries
- Unit tests using fake HTTP responses; no brokerage credentials are required

Market orders are intentionally unsupported. The launcher defaults to the KIS paper endpoint;
set `kis-account-mode` to `real` and provide the real-account credentials/base URL to select
KIS's real-account TR IDs. Validate independently before any live use.

## Requirements

- .NET 10 SDK
- QuantConnect LEAN 2.5-compatible packages
- KIS Open API credentials for the selected account mode

## Build and test

```sh
dotnet test tests/KisBrokerage.Tests/QuantConnect.KisBrokerage.Tests.csproj
dotnet build src/KisBrokerage/QuantConnect.KisBrokerage.csproj -c Release
```

The plugin is loaded by the LEAN Launcher as `QuantConnect.KisBrokerage.KisBrokerageFactory`.
See [`src/KisBrokerage/config.json`](src/KisBrokerage/config.json) for a credential-free sample.
Set `kis-account-mode` to `paper` (default) or `real`. Credentials must be supplied through the
operator's private runtime configuration; never commit
app keys, app secrets, account numbers, access tokens, journals, or runtime binaries.

For a real account, the minimal launcher settings are:

```json
{
  "kis-account-mode": "real",
  "kis-app-key": "<real-account-app-key>",
  "kis-app-secret": "<real-account-app-secret>",
  "kis-acct-no": "<account-number>",
  "kis-prod-code": "<product-code>",
  "kis-base-url": "https://openapi.koreainvestment.com:9443"
}
```

The default remains paper mode. Switching to `real` selects KIS real-account transaction IDs;
test the complete order lifecycle with a controlled account before unattended operation.

## Safety and limitations

This software sends brokerage requests and can lose money if misconfigured. It is provided for
education and research, with no performance guarantee. Use KIS paper trading first, keep live
credentials out of source control, and independently verify all order and fee behavior before
live use. KIS API limits, endpoint behavior, market rules, and applicable laws or terms may
change.

The quote feed is REST polling, not a tick-accurate websocket feed. Fees and taxes are not
modeled by the brokerage model.

## License

MIT. See [LICENSE](LICENSE).
