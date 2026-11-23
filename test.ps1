$ErrorActionPreference = 'Stop'

dotnet run --project "$PSScriptRoot/tests/GitGardener.Regression/GitGardener.Regression.csproj" --configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project "$PSScriptRoot/tests/GitGardener.PullRequests.Regression/GitGardener.PullRequests.Regression.csproj" --configuration Release
exit $LASTEXITCODE
