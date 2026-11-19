#!/usr/bin/env bash
# =============================================================================
# YouLink - Deploy no Google Cloud Run (Linux / Google Cloud Shell)
# =============================================================================
set -euo pipefail

SERVICE_NAME="${YOULINK_SERVICE_NAME:-youlink-loja}"
REGION="${YOULINK_REGION:-southamerica-east1}"
MIN_INSTANCES="${YOULINK_MIN_INSTANCES:-1}"
MEMORY="${YOULINK_MEMORY:-1Gi}"

echo "============================================================================="
echo "  YOULINK - DEPLOY NO GOOGLE CLOUD RUN ($REGION | min-instances=$MIN_INSTANCES)"
echo "============================================================================="

cp -f src/ClothingStore.Web/Dockerfile ./Dockerfile
trap 'rm -f ./Dockerfile' EXIT

ENV_VARS="ASPNETCORE_ENVIRONMENT=Production,YOULINK_PORTAL_HOME=true,YOULINK_EDGE_PROXY_URL=https://youlink-edge-accelerator.youlink-edge.workers.dev"
[[ -n "${YOULINK_SYNC_SECRET:-}" ]] && ENV_VARS="${ENV_VARS},YOULINK_SYNC_SECRET=${YOULINK_SYNC_SECRET}"
[[ -n "${DATABASE_URL:-}" ]] && ENV_VARS="${ENV_VARS},DATABASE_URL=${DATABASE_URL}"
[[ -n "${GEMINI_API_KEY:-}" ]] && ENV_VARS="${ENV_VARS},GEMINI_API_KEY=${GEMINI_API_KEY}"
[[ -n "${CLOUDFLARE_TURNSTILE_SITE_KEY:-}" ]] && ENV_VARS="${ENV_VARS},CLOUDFLARE_TURNSTILE_SITE_KEY=${CLOUDFLARE_TURNSTILE_SITE_KEY}"
[[ -n "${CLOUDFLARE_TURNSTILE_SECRET_KEY:-}" ]] && ENV_VARS="${ENV_VARS},CLOUDFLARE_TURNSTILE_SECRET_KEY=${CLOUDFLARE_TURNSTILE_SECRET_KEY}"

gcloud run deploy "$SERVICE_NAME" \
    --source . \
    --platform managed \
    --region "$REGION" \
    --allow-unauthenticated \
    --port 8080 \
    --cpu 1 \
    --memory "$MEMORY" \
    --min-instances "$MIN_INSTANCES" \
    --max-instances 4 \
    --set-env-vars "$ENV_VARS"

echo ""
echo "Deploy finalizado! URL gerada acima pronta para configurar no YouLink Admin e no Cloudflare Edge Worker."
