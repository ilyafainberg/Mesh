#!/usr/bin/env bash
set -uo pipefail
RG=rg-mesh
APP=mesh-relay
ENVN=mesh-ne-env
SFX=$(tr -dc a-z0-9 </dev/urandom | head -c6)
LOC=$(az containerapp env show -n "$ENVN" -g "$RG" --query location -o tsv)
LOC=${LOC// /}
[ -z "$LOC" ] && LOC=northeurope
echo "== region: $LOC  suffix: $SFX =="

COSMOS=mesh-cosmos-$SFX
REDIS=mesh-redis-$SFX

echo "== Redis (Basic C0), async =="
az redis create -n "$REDIS" -g "$RG" -l "$LOC" --sku Basic --vm-size c0 --no-wait

echo "== Cosmos (serverless) =="
az cosmosdb create -n "$COSMOS" -g "$RG" --locations regionName="$LOC" \
  --capabilities EnableServerless --default-consistency-level Session -o none
COSMOS_CONN=$(az cosmosdb keys list -n "$COSMOS" -g "$RG" --type connection-strings \
  --query "connectionStrings[0].connectionString" -o tsv)
echo "cosmos conn len: ${#COSMOS_CONN}"

echo "== Build + deploy relay image from source =="
az containerapp up -n "$APP" -g "$RG" --environment "$ENVN" \
  --source ./build --target-port 8080 --ingress external -o none
echo "container build/deploy done"

echo "== Wait for Redis to finish provisioning =="
az redis show -n "$REDIS" -g "$RG" --query provisioningState -o tsv
for i in $(seq 1 40); do
  ST=$(az redis show -n "$REDIS" -g "$RG" --query provisioningState -o tsv 2>/dev/null)
  echo "redis[$i]: $ST"
  [ "$ST" = "Succeeded" ] && break
  sleep 30
done
REDIS_KEY=$(az redis list-keys -n "$REDIS" -g "$RG" --query primaryKey -o tsv)
REDIS_HOST=$(az redis show -n "$REDIS" -g "$RG" --query hostName -o tsv)
REDIS_CONN="$REDIS_HOST:6380,password=$REDIS_KEY,ssl=True,abortConnect=False"
echo "redis conn host: $REDIS_HOST"

echo "== Wire secrets + env vars on the relay =="
az containerapp secret set -n "$APP" -g "$RG" \
  --secrets "cosmos=$COSMOS_CONN" "redis=$REDIS_CONN" -o none
az containerapp update -n "$APP" -g "$RG" \
  --set-env-vars \
    "ASPNETCORE_URLS=http://+:8080" \
    "COSMOS_CONNECTION=secretref:cosmos" \
    "REDIS_CONNECTION=secretref:redis" \
    "MODEL_DAILY_TOKEN_LIMIT=100000" \
  --min-replicas 1 --max-replicas 5 -o none

echo "== sticky sessions =="
az containerapp ingress sticky-sessions set -n "$APP" -g "$RG" --affinity sticky -o none || true

FQDN=$(az containerapp show -n "$APP" -g "$RG" --query "properties.configuration.ingress.fqdn" -o tsv)
echo "DEPLOY_DONE"
echo "RELAY_FQDN=$FQDN"
echo "COSMOS=$COSMOS"
echo "REDIS=$REDIS"
