# Builds everything CDK needs: Lambda bundles (linux-arm64, ReadyToRun) and the React bundle.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

if (Test-Path artifacts) { Remove-Item -Recurse -Force artifacts }
dotnet publish src/GridSafety.Api -c Release -r linux-arm64 --self-contained false -o artifacts/api
if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet publish src/GridSafety.Functions -c Release -r linux-arm64 --self-contained false -o artifacts/functions
if ($LASTEXITCODE) { exit $LASTEXITCODE }

Push-Location web
npm ci; if ($LASTEXITCODE) { exit $LASTEXITCODE }
npm run build; if ($LASTEXITCODE) { exit $LASTEXITCODE }
Pop-Location
Write-Host 'Build complete -> artifacts/ and web/dist/'
