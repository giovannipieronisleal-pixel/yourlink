# =============================================================================
# SCRIPT DE PUBLICAÇÃO AUTOMATIZADA NO GOOGLE CLOUD RUN (ZERO COLD-START)
# Projeto: YouLink Portal Central & Vitrines Multi-Loja (ClothingStore.Web)
# =============================================================================

param (
    [string]$ServiceName = "youlink-loja",
    [string]$Region = "southamerica-east1",
    [string]$Account = "seu-email@gmail.com",
    [int]$MinInstances = 1,
    [string]$Memory = "1Gi",
    [string]$SyncSecret = "",
    [string]$DatabaseUrl = "",
    [string]$GeminiApiKey = "",
    [string]$TurnstileSiteKey = "",
    [string]$TurnstileSecretKey = ""
)

Write-Host "==============================================================" -ForegroundColor Cyan
Write-Host "🚀 PUBLICAÇÃO NO GOOGLE CLOUD RUN (YOULINK 24H SEM HIBERNAÇÃO)" -ForegroundColor Green
Write-Host "☁️ Conta Google Cloud: $Account | Região: $Region | MinInstances: $MinInstances" -ForegroundColor Yellow
Write-Host "==============================================================" -ForegroundColor Cyan

$gcloudInstalled = Get-Command gcloud -ErrorAction SilentlyContinue
if (-not $gcloudInstalled) {
    Write-Host ""
    Write-Host "⚠️ Google Cloud CLI (gcloud) não foi encontrado no PATH do Windows." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "👉 OPÇÃO A (Google Cloud Shell — direto no navegador, sem instalar nada):" -ForegroundColor Green
    Write-Host "  1. Acesse: https://shell.cloud.google.com/?show=terminal com $Account" -ForegroundColor Cyan
    Write-Host "  2. Execute na raiz do repositório:" -ForegroundColor Gray
    Write-Host "     bash ./scripts/deploy-google-cloud-run.sh" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "👉 OPÇÃO B (Instalar o Google Cloud SDK no Windows):" -ForegroundColor Green
    Write-Host "  https://cloud.google.com/sdk/docs/install" -ForegroundColor Cyan
    exit 0
}

Write-Host "`n1. Verificando conta Google Cloud..." -ForegroundColor Cyan
if ($Account -and $Account -ne "seu-email@gmail.com") {
    gcloud config set account $Account --quiet
}

$envPairs = @(
    "ASPNETCORE_ENVIRONMENT=Production",
    "YOULINK_PORTAL_HOME=true",
    "YOULINK_EDGE_PROXY_URL=https://youlink-edge-accelerator.youlink-edge.workers.dev"
)
if ($SyncSecret) { $envPairs += "YOULINK_SYNC_SECRET=$SyncSecret" }
if ($DatabaseUrl) { $envPairs += "DATABASE_URL=$DatabaseUrl" }
if ($GeminiApiKey) { $envPairs += "GEMINI_API_KEY=$GeminiApiKey" }
if ($TurnstileSiteKey) { $envPairs += "CLOUDFLARE_TURNSTILE_SITE_KEY=$TurnstileSiteKey" }
if ($TurnstileSecretKey) { $envPairs += "CLOUDFLARE_TURNSTILE_SECRET_KEY=$TurnstileSecretKey" }

$envJoined = ($envPairs -join ",")

Write-Host "`n2. Publicando container Docker a partir da raiz do repositório no Cloud Run..." -ForegroundColor Cyan
$createdTempDockerfile = -not (Test-Path "./Dockerfile")
if ($createdTempDockerfile) {
    Copy-Item -Path "./src/ClothingStore.Web/Dockerfile" -Destination "./Dockerfile" -Force
}
try {
    gcloud run deploy $ServiceName `
        --source . `
        --region $Region `
        --allow-unauthenticated `
        --port 8080 `
        --memory $Memory `
        --cpu 1 `
        --min-instances $MinInstances `
        --max-instances 4 `
        --set-env-vars "$envJoined" `
        --quiet
}
finally {
    if ($createdTempDockerfile -and (Test-Path "./Dockerfile")) {
        Remove-Item "./Dockerfile" -Force -ErrorAction SilentlyContinue
    }
}

if ($LASTEXITCODE -eq 0) {
    $serviceUrl = gcloud run services describe $ServiceName --region $Region --format 'value(status.url)'
    Write-Host ""
    Write-Host "==============================================================" -ForegroundColor Green
    Write-Host "🎉 SUCESSO! YOULINK ONLINE NO GOOGLE CLOUD RUN!" -ForegroundColor Green
    Write-Host "🌐 Link Oficial 24h: $serviceUrl" -ForegroundColor Yellow
    Write-Host "==============================================================" -ForegroundColor Green
} else {
    Write-Host "`n❌ Falha na publicação. Verifique se o projeto e o faturamento do Cloud Run estão ativos." -ForegroundColor Red
}
