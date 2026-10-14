# KIS Brokerage for QuantConnect LEAN

An open-source QuantConnect LEAN brokerage adapter for Korea Investment & Securities (KIS),
with support for KIS overseas stock trading (including US equities) and Korean equities.

This repository contains only the broker integration layer. It does not include a trading
strategy, account configuration, OpenClaw automation, credentials, or personal operating data.

## Scope

- KIS OAuth token caching and paper-account authentication
- LEAN `BrokerageFactory` integration
- US/overseas and KRX equity balances, holdings, open orders, fills, and daily history
- Limit-order submission, modification, cancellation, and fill synchronization
- REST quote polling through LEAN's `IDataQueueHandler`
- Shared KIS rate limiting and bounded request/reconciliation retries
- Unit tests using fake HTTP responses; no brokerage credentials are required

Market orders are intentionally unsupported. The adapter is paper-trading oriented and should
be validated against a KIS paper account before any live-account use.

## Requirements

- .NET 10 SDK
- QuantConnect LEAN 2.5-compatible packages
- KIS Open API paper-account credentials

## Build and test

```sh
dotnet test tests/KisBrokerage.Tests/QuantConnect.KisBrokerage.Tests.csproj
dotnet build src/KisBrokerage/QuantConnect.KisBrokerage.csproj -c Release
```

The plugin is loaded by the LEAN Launcher as `QuantConnect.KisBrokerage.KisBrokerageFactory`.
See [`src/KisBrokerage/config.json`](src/KisBrokerage/config.json) for a credential-free sample.
Credentials must be supplied through the operator's private runtime configuration; never commit
app keys, app secrets, account numbers, access tokens, journals, or runtime binaries.

## Safety and limitations

This software sends brokerage requests and can lose money if misconfigured. It is provided for
education and research, with no performance guarantee. Use KIS paper trading first, keep live
credentials out of source control, and independently verify all order and fee behavior before
live use. KIS API limits, endpoint behavior, market rules, and applicable laws or terms may
change.

The quote feed is REST polling, not a tick-accurate websocket feed. Fees and taxes are not
modeled by the paper brokerage model.

## License

MIT. See [LICENSE](LICENSE).

