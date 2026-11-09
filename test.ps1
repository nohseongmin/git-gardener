$ErrorActionPreference = 'Stop'

dotnet run --project "$PSScriptRoot/tests/GitGardener.Regression/GitGardener.Regression.csproj" --configuration Release
exit $LASTEXITCODE
