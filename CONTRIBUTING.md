# Contributing

1. Keep credentials, account identifiers, runtime journals, and personal infrastructure out
   of commits and issue reports.
2. Add or update unit tests for changes to authentication, order lifecycle, reconciliation,
   history, or market-data behavior.
3. Run the test project locally before opening a pull request:

   ```sh
   dotnet test tests/KisBrokerage.Tests/QuantConnect.KisBrokerage.Tests.csproj --configuration Release
   ```

4. Describe the KIS endpoint, market scope, and paper-account conditions used to validate a
   brokerage-facing change. Do not use real-account credentials in automated tests.
