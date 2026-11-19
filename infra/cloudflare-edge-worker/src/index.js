/**
 * YouLink Edge Accelerator — Cloudflare Worker
 * 1. Proxy reverso com cache na borda para vitrines, mídias e assets estáticos (LCP/TTFB muito mais rápidos).
 * 2. Bypass obrigatório para rotas autenticadas, webhooks, checkouts POST, Blazor SignalR e status VIP em tempo real.
 * 3. Cron keep-alive a cada 10 minutos em /health para eliminar cold-start/hibernação do servidor central no Render.
 */

const BYPASS_PREFIXES = [
  "/api/auth/",
  "/api/cloud/",
  "/api/platform-sync/",
  "/api/public/orders/",
  "/api/public/webhooks/",
  "/api/ai/",
  "/_blazor",
  "/health"
];

const MEDIA_PREFIXES = ["/media/", "/uploads/", "/icons/"];
const STATIC_EXTENSIONS = [".css", ".js", ".svg", ".png", ".jpg", ".jpeg", ".webp", ".woff2", ".ico"];

function shouldBypassCache(request, pathname) {
  if (request.method !== "GET" && request.method !== "HEAD") return true;
  if (request.headers.get("upgrade")?.toLowerCase() === "websocket") return true;
  if (request.headers.has("authorization") || request.headers.has("x-youlink-sync-key")) return true;
  return BYPASS_PREFIXES.some((prefix) => pathname.startsWith(prefix));
}

function resolveCacheTtl(pathname, env) {
  const mediaTtl = Number.parseInt(env.MEDIA_CACHE_SECONDS || "86400", 10);
  const publicTtl = Number.parseInt(env.PUBLIC_CACHE_SECONDS || "60", 10);

  if (
    MEDIA_PREFIXES.some((p) => pathname.startsWith(p)) ||
    STATIC_EXTENSIONS.some((ext) => pathname.endsWith(ext))
  ) {
    return Number.isFinite(mediaTtl) ? mediaTtl : 86400;
  }

  if (pathname.startsWith("/api/public/") || pathname === "/sitemap.xml" || pathname === "/robots.txt") {
    return Number.isFinite(publicTtl) ? publicTtl : 60;
  }

  return 0;
}

export default {
  async fetch(request, env, ctx) {
    const originBase = (env.ORIGIN_URL || "https://youlink-loja.onrender.com").replace(/\/+$/, "");
    const incomingUrl = new URL(request.url);
    const targetUrl = new URL(incomingUrl.pathname + incomingUrl.search, originBase);

    const bypass = shouldBypassCache(request, incomingUrl.pathname);
    const cacheTtl = bypass ? 0 : resolveCacheTtl(incomingUrl.pathname, env);
    const cache = caches.default;
    const cacheKey = new Request(targetUrl.toString(), { method: "GET" });

    if (cacheTtl > 0) {
      const cached = await cache.match(cacheKey);
      if (cached) {
        const hitHeaders = new Headers(cached.headers);
        hitHeaders.set("X-YouLink-Edge-Cache", "HIT");
        return new Response(cached.body, {
          status: cached.status,
          statusText: cached.statusText,
          headers: hitHeaders
        });
      }
    }

    const proxyHeaders = new Headers(request.headers);
    proxyHeaders.set("X-Forwarded-Host", incomingUrl.host);
    proxyHeaders.set("X-Forwarded-Proto", incomingUrl.protocol.replace(":", ""));
    proxyHeaders.delete("host");

    const originResponse = await fetch(targetUrl.toString(), {
      method: request.method,
      headers: proxyHeaders,
      body: request.method === "GET" || request.method === "HEAD" ? undefined : request.body,
      redirect: "manual"
    });

    const responseHeaders = new Headers(originResponse.headers);
    responseHeaders.set("X-YouLink-Edge-Proxy", "cloudflare-worker");

    if (cacheTtl > 0 && originResponse.status === 200) {
      responseHeaders.set("Cache-Control", `public, max-age=${cacheTtl}, stale-while-revalidate=${cacheTtl * 5}`);
      responseHeaders.set("X-YouLink-Edge-Cache", "MISS");
      const cacheable = new Response(originResponse.body, {
        status: originResponse.status,
        statusText: originResponse.statusText,
        headers: responseHeaders
      });
      ctx.waitUntil(cache.put(cacheKey, cacheable.clone()));
      return cacheable;
    }

    responseHeaders.set("X-YouLink-Edge-Cache", "BYPASS");
    return new Response(originResponse.body, {
      status: originResponse.status,
      statusText: originResponse.statusText,
      headers: responseHeaders
    });
  },

  async scheduled(_controller, env, ctx) {
    const originBase = (env.ORIGIN_URL || "https://youlink-loja.onrender.com").replace(/\/+$/, "");
    ctx.waitUntil(
      fetch(`${originBase}/health`, {
        method: "GET",
        headers: { "User-Agent": "YouLink-Cloudflare-KeepAlive/1.0" }
      }).then((res) => {
        console.log(JSON.stringify({ event: "keepalive_health", origin: originBase, status: res.status }));
      }).catch((err) => {
        console.error(JSON.stringify({ event: "keepalive_error", origin: originBase, error: String(err) }));
      })
    );
  }
};
