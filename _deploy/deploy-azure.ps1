<#
  Mesh relay: one-shot Azure provisioning + deploy.

  Prereq: `az login` into a subscription where you have Contributor/Owner
  (the current corp tenant sub "COSMIC AKS PlayGround" denied resource-group writes,
  so run this against an Azure subscription you can write to:
     az login --tenant <your-tenant-id>
     az account set --subscription "<sub name or id>"

  Then:  ./deploy-azure.ps1

  Provisions: Resource group, Cosmos DB (serverless), Azure Cache for Redis (Basic C0),
  ACR, Azure OpenAI + gpt-4o-mini (for the hosted free model), Container Apps env + the
  mesh-relay app. Builds the relay image in ACR and wires every secret as an env var.
#>

param(
  [string]$ResourceGroup = "mesh-prod-rg",
  [string]$Location      = "northeurope",
  [string]$Suffix        = (-join ((48..57) + (97..122) | Get-Random -Count 6 | ForEach-Object {[char]$_}))
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot   # .../Mesh
$cosmos = "mesh-cosmos-$Suffix"
$redis  = "mesh-redis-$Suffix"
$acr    = "meshacr$Suffix"
$aoai   = "mesh-openai-$Suffix"
$env    = "mesh-env-$Suffix"
$appName= "mesh-relay"

Write-Host "== Resource group =="
az group create -n $ResourceGroup -l $Location -o none

Write-Host "== Cosmos DB (serverless) =="
az cosmosdb create -n $cosmos -g $ResourceGroup --locations regionName=$Location `
  --capabilities EnableServerless -o none
$cosmosConn = az cosmosdb keys list -n $cosmos -g $ResourceGroup --type connection-strings `
  --query "connectionStrings[0].connectionString" -o tsv
# Containers (handles/invites/inbox) are created by the app on first use (CosmosRelayStore.EnsureInit).

Write-Host "== Redis (Basic C0) =="
az redis create -n $redis -g $ResourceGroup -l $Location --sku Basic --vm-size c0 -o none
$redisKey  = az redis list-keys -n $redis -g $ResourceGroup --query primaryKey -o tsv
$redisHost = az redis show -n $redis -g $ResourceGroup --query hostName -o tsv
$redisConn = "$redisHost:6380,password=$redisKey,ssl=True,abortConnect=False"

Write-Host "== Azure OpenAI + gpt-4o-mini (hosted free model) =="
az cognitiveservices account create -n $aoai -g $ResourceGroup -l $Location `
  --kind OpenAI --sku S0 --custom-domain $aoai -o none
az cognitiveservices account deployment create -n $aoai -g $ResourceGroup `
  --deployment-name gpt-4o-mini --model-name gpt-4o-mini --model-version "2024-07-18" `
  --model-format OpenAI --sku-capacity 20 --sku-name Standard -o none
$aoaiEndpoint = az cognitiveservices account show -n $aoai -g $ResourceGroup --query "properties.endpoint" -o tsv
$aoaiKey      = az cognitiveservices account keys list -n $aoai -g $ResourceGroup --query key1 -o tsv

Write-Host "== ACR + build relay image =="
az acr create -n $acr -g $ResourceGroup --sku Basic --admin-enabled true -o none
# Build from the flattened deploy context (single Dockerfile, all sources copied in).
& "$PSScriptRoot/sync-deploy.ps1"
az acr build -r $acr -t "mesh-relay:latest" "$repo/_deploy/relay" -o none
$acrServer = az acr show -n $acr -g $ResourceGroup --query loginServer -o tsv
$acrUser   = az acr credential show -n $acr -g $ResourceGroup --query username -o tsv
$acrPass   = az acr credential show -n $acr -g $ResourceGroup --query "passwords[0].value" -o tsv

Write-Host "== Container Apps environment + relay app =="
az containerapp env create -n $env -g $ResourceGroup -l $Location -o none
az containerapp create -n $appName -g $ResourceGroup --environment $env `
  --image "$acrServer/mesh-relay:latest" `
  --registry-server $acrServer --registry-username $acrUser --registry-password $acrPass `
  --target-port 8080 --ingress external --min-replicas 1 --max-replicas 5 `
  --secrets "cosmos=$cosmosConn" "redis=$redisConn" "modelkey=$aoaiKey" `
  --env-vars `
    "ASPNETCORE_URLS=http://+:8080" `
    "COSMOS_CONNECTION=secretref:cosmos" `
    "REDIS_CONNECTION=secretref:redis" `
    "MODEL_KIND=azure" `
    "MODEL_ENDPOINT=$aoaiEndpoint" `
    "MODEL_API_KEY=secretref:modelkey" `
    "MODEL_NAME=gpt-4o-mini" `
    "MODEL_DAILY_TOKEN_LIMIT=100000" `
  -o none

# Session affinity so a connection's negotiate + socket land on the same replica.
az containerapp ingress sticky-sessions set -n $appName -g $ResourceGroup --affinity sticky -o none

$fqdn = az containerapp show -n $appName -g $ResourceGroup --query "properties.configuration.ingress.fqdn" -o tsv
Write-Host ""
Write-Host "Relay deployed: https://$fqdn"
Write-Host "Set this as the client default RelayUrl (Domain/Models.cs MeshProfile.RelayUrl)."
Write-Host "Health: https://$fqdn/health"
