#!/usr/bin/env bash
# Builds the React UI into wwwroot, publishes the .NET app, and deploys it to
# Azure App Service (Linux, Free F1 by default; set SKU=B1 for Always On). Requires `az login`.
#
# Usage: ./deploy.sh [app-name]   (app name must be globally unique)
set -euo pipefail

APP="${1:-supply-chain-globe-$RANDOM}"
RG="${RG:-supply-chain-globe-rg}"
LOCATION="${LOCATION:-northcentralus}"
PLAN="${PLAN:-supply-chain-globe-plan}"
SKU="${SKU:-F1}"
RUNTIME="${RUNTIME:-DOTNETCORE:10.0}"

cd "$(dirname "$0")"

echo "==> Building UI"
(cd web && npm ci && npm run build)

echo "==> Publishing API"
rm -rf publish publish.zip
dotnet publish src/SupplyChain.Api -c Release -o publish
(cd publish && zip -qr ../publish.zip .)

echo "==> Provisioning Azure resources ($RG / $PLAN / $APP)"
az group create -n "$RG" -l "$LOCATION" -o none
az appservice plan create -g "$RG" -n "$PLAN" --is-linux --sku "$SKU" -o none
az webapp create -g "$RG" -p "$PLAN" -n "$APP" --runtime "$RUNTIME" -o none
az webapp update -g "$RG" -n "$APP" --https-only true -o none
# Always On is unavailable on F1/D1; on paid tiers it keeps the app loaded so visitors never hit a cold start.
if [[ "$SKU" != F1 && "$SKU" != D1 ]]; then az webapp config set -g "$RG" -n "$APP" --always-on true -o none; fi

echo "==> Deploying"
az webapp deploy -g "$RG" -n "$APP" --src-path publish.zip --type zip

echo "==> Live at https://$APP.azurewebsites.net"
