#!/usr/bin/env bash
set -uo pipefail
RG=rg-mesh
APP=mesh-relay
ENVN=mesh-ne-env
SFX=$(tr -dc a-z0-9 </dev/urandom | head -c6)
LOC=northeurope
echo "SUFFIX=$SFX"

REDIS=mesh-redis-$SFX
echo "== creating redis $REDIS in background =="
( az redis create -n "$REDIS" -g "$RG" -l "$LOC" --sku Basic --vm-size c0 -o none && echo REDIS_CREATE_DONE || echo REDIS_CREATE_FAIL ) >redis-create.log 2>&1 &
REDIS_PID=$!

COSMOS=mesh-cosmos-$SFX
COSMOS_OK=""
for R in northeurope westeurope swedencentral uksouth francecentral; do
  echo "== trying cosmos in $R =="
  if az cosmosdb create -n "$COSMOS" -g "$RG" --locations regionName=$R failoverPriority=0 isZoneRedundant=False --capabilities EnableServerless --default-consistency-level Session -o none 2>cosmos-err.log; then
    COSMOS_OK=$R; echo "COSMOS_OK_IN=$R"; break
  else
    echo "cosmos failed in $R:"; tail -3 cosmos-err.log
  fi
done
[ -z "$COSMOS_OK" ] && echo "COSMOS_ALL_FAILED"
COSMOS_CONN=$(az cosmosdb keys list -n "$COSMOS" -g "$RG" --type connection-strings --query "connectionStrings[0].connectionString" -o tsv 2>/dev/null)
echo "cosmos_conn_len=${#COSMOS_CONN}"

echo "== containerapp up (build from ./build) =="
az containerapp up -n "$APP" -g "$RG" --environment "$ENVN" --source ./build --ingress external --target-port 8080
echo "CONTAINERAPP_UP_EXIT=$?"

echo "== waiting on redis create =="
wait $REDIS_PID
cat redis-create.log
REDIS_ST=""
for i in $(seq 1 40); do
  REDIS_ST=$(az redis show -n "$REDIS" -g "$RG" --query provisioningState -o tsv 2>/dev/null)
  echo "redis[$i]=$REDIS_ST"
  [ "$REDIS_ST" = "Succeeded" ] && break
  [ -z "$REDIS_ST" ] && { echo "redis missing, stop polling"; break; }
  sleep 30
done

REDIS_CONN=""
if [ "$REDIS_ST" = "Succeeded" ]; then
  RK=$(az redis list-keys -n "$REDIS" -g "$RG" --query primaryKey -o tsv)
  RH=$(az redis show -n "$REDIS" -g "$RG" --query hostName -o tsv)
  REDIS_CONN="$RH:6380,password=$RK,ssl=True,abortConnect=False"
  echo "redis_host=$RH"
fi

echo "== wiring secrets + env =="
if [ -n "$COSMOS_CONN" ]; then az containerapp secret set -n "$APP" -g "$RG" --secrets cosmos="$COSMOS_CONN" -o none; fi
if [ -n "$REDIS_CONN" ]; then az containerapp secret set -n "$APP" -g "$RG" --secrets redis="$REDIS_CONN" -o none; fi

ENVSET="ASPNETCORE_URLS=http://+:8080 MODEL_DAILY_TOKEN_LIMIT=100000"
[ -n "$COSMOS_CONN" ] && ENVSET="$ENVSET COSMOS_CONNECTION=secretref:cosmos"
[ -n "$REDIS_CONN" ] && ENVSET="$ENVSET REDIS_CONNECTION=secretref:redis"
az containerapp update -n "$APP" -g "$RG" --set-env-vars $ENVSET --min-replicas 1 --max-replicas 5 -o none
az containerapp ingress sticky-sessions set -n "$APP" -g "$RG" --affinity sticky -o none || true

FQDN=$(az containerapp show -n "$APP" -g "$RG" --query "properties.configuration.ingress.fqdn" -o tsv)
echo "DEPLOY_DONE"
echo "RELAY_FQDN=$FQDN"
echo "COSMOS=$COSMOS OK=$COSMOS_OK"
echo "REDIS=$REDIS ST=$REDIS_ST"
