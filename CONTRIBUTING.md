# Contributing

## Requirements

- Windows 10 or Windows 11 x64
- .NET 8 SDK

## Validation

Run these commands before submitting a change:

```powershell
dotnet format src\TeardownBoundaryRemover\TeardownBoundaryRemover.csproj --verify-no-changes
dotnet build src\TeardownBoundaryRemover\TeardownBoundaryRemover.csproj -c Release
dotnet run --project src\TeardownBoundaryRemover\TeardownBoundaryRemover.csproj -c Release --no-build -- --self-test
```

Tests must operate on generated fixtures or temporary copies. Do not make test
code modify an installed Teardown or Workshop file.

Changes to XML or TDBIN write behavior must include a regression test covering
the accepted input and the nearest rejected cases.
