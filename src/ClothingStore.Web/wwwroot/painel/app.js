(() => {
  const SESSION_KEY = "youlink_pwa_session_v1";
  const THEME_KEY = "youlink_pwa_theme_v1";
  const DEFAULT_API_ORIGIN = "https://youlink-loja.onrender.com";

  function resolveApiOrigin() {
    const loc = window.location;
    if (loc.hostname === "localhost" || loc.hostname === "127.0.0.1") return loc.origin;
    if (loc.hostname.endsWith(".onrender.com") || loc.hostname.endsWith(".workers.dev")) return loc.origin;
    return DEFAULT_API_ORIGIN;
  }

  const API_BASE = resolveApiOrigin();

  const STORE_THEMES = [
    { key: "minimalista-luxo", name: "Minimalista Luxo", tier: "Basico", desc: "Alabastro quente, sépia e ouro editorial (incluso desde o Básico)", design: { presetKey: "minimalista-luxo", backgroundColor: "#FAF8F5", surfaceColor: "#FFFFFF", surfaceSubtleColor: "#F3EFE8", primaryColor: "#18181B", primaryTextColor: "#FAF8F5", accentColor: "#B69158", textColor: "#18181B", mutedTextColor: "#57534E", borderColor: "#E7E2DA", sidebarColor: "#141416", displayFont: "Playfair Display", bodyFont: "Plus Jakarta Sans", cornerRadiusMode: "ROUND_FOUR", densityMode: "balanced", heroLayout: "split", gridColumns: 3, showLookbookBadge: true } },
    { key: "editorial-vogue", name: "Editorial Vogue", tier: "Intermediario", desc: "Branco puro, contraste alto e tipografia editorial", design: { presetKey: "editorial-vogue", backgroundColor: "#FFFFFF", surfaceColor: "#F9FAFB", surfaceSubtleColor: "#F3F4F6", primaryColor: "#111827", primaryTextColor: "#FFFFFF", accentColor: "#9333EA", textColor: "#111827", mutedTextColor: "#4B5563", borderColor: "#E5E7EB", sidebarColor: "#111827", displayFont: "Bodoni Moda", bodyFont: "Inter", cornerRadiusMode: "SHARP", densityMode: "airy", heroLayout: "editorial", gridColumns: 3, showLookbookBadge: true } },
    { key: "urban-sartorial", name: "Urban Sartorial", tier: "Intermediario", desc: "Urbano limpo com azul moderno focado em conversão", design: { presetKey: "urban-sartorial", backgroundColor: "#F8FAFC", surfaceColor: "#FFFFFF", surfaceSubtleColor: "#EEF2F6", primaryColor: "#0F172A", primaryTextColor: "#FFFFFF", accentColor: "#2563EB", textColor: "#0F172A", mutedTextColor: "#475569", borderColor: "#E2E8F0", sidebarColor: "#0F172A", displayFont: "Space Grotesk", bodyFont: "Plus Jakarta Sans", cornerRadiusMode: "ROUND_EIGHT", densityMode: "balanced", heroLayout: "split", gridColumns: 3, showLookbookBadge: true } },
    { key: "esmeralda-imperial", name: "Esmeralda Imperial", tier: "Premium", desc: "Verde esmeralda luxuoso com detalhes em ouro", design: { presetKey: "esmeralda-imperial", backgroundColor: "#F4FBF7", surfaceColor: "#FFFFFF", surfaceSubtleColor: "#E6F4EC", primaryColor: "#064E3B", primaryTextColor: "#FFFFFF", accentColor: "#10B981", textColor: "#064E3B", mutedTextColor: "#3F6258", borderColor: "#D1E7DC", sidebarColor: "#064E3B", displayFont: "Cormorant Garamond", bodyFont: "Plus Jakarta Sans", cornerRadiusMode: "ROUND_EIGHT", densityMode: "balanced", heroLayout: "split", gridColumns: 3, showLookbookBadge: true } },
    { key: "grafite-studio", name: "Grafite Studio", tier: "Premium", desc: "Vitrine escura contemporânea de alto impacto", design: { presetKey: "grafite-studio", backgroundColor: "#0F172A", surfaceColor: "#1E293B", surfaceSubtleColor: "#273549", primaryColor: "#F8FAFC", primaryTextColor: "#0F172A", accentColor: "#D4A853", textColor: "#F8FAFC", mutedTextColor: "#94A3B8", borderColor: "#334155", sidebarColor: "#0B1120", displayFont: "Montserrat", bodyFont: "Inter", cornerRadiusMode: "ROUND_EIGHT", densityMode: "balanced", heroLayout: "centered", gridColumns: 3, showLookbookBadge: true } },
    { key: "terracota-solar", name: "Terracota Solar", tier: "Premium", desc: "Quente, solar e marcante para moda e acessórios", design: { presetKey: "terracota-solar", backgroundColor: "#FDF8F4", surfaceColor: "#FFFFFF", surfaceSubtleColor: "#F7EAE0", primaryColor: "#7C2D12", primaryTextColor: "#FFFFFF", accentColor: "#EA580C", textColor: "#431407", mutedTextColor: "#785343", borderColor: "#EBD5C5", sidebarColor: "#5A1F0C", displayFont: "Playfair Display", bodyFont: "DM Sans", cornerRadiusMode: "ROUND_TWELVE", densityMode: "balanced", heroLayout: "split", gridColumns: 3, showLookbookBadge: true } },
    { key: "noir-noturno", name: "Noir Noturno", tier: "Premium", desc: "Preto profundo com dourado refinado", design: { presetKey: "noir-noturno", backgroundColor: "#09090B", surfaceColor: "#18181B", surfaceSubtleColor: "#27272A", primaryColor: "#FAFAFA", primaryTextColor: "#09090B", accentColor: "#EAB308", textColor: "#FAFAFA", mutedTextColor: "#A1A1AA", borderColor: "#27272A", sidebarColor: "#09090B", displayFont: "Bodoni Moda", bodyFont: "Inter", cornerRadiusMode: "SHARP", densityMode: "airy", heroLayout: "centered", gridColumns: 3, showLookbookBadge: true } },
    { key: "boutique-rose", name: "Boutique Rosé", tier: "Premium", desc: "Delicado, editorial e feminino", design: { presetKey: "boutique-rose", backgroundColor: "#FFF5F7", surfaceColor: "#FFFFFF", surfaceSubtleColor: "#FCE7F3", primaryColor: "#831843", primaryTextColor: "#FFFFFF", accentColor: "#EC4899", textColor: "#500724", mutedTextColor: "#9D174D", borderColor: "#FBCFE8", sidebarColor: "#6B1237", displayFont: "Cormorant Garamond", bodyFont: "Plus Jakarta Sans", cornerRadiusMode: "ROUND_TWELVE", densityMode: "balanced", heroLayout: "split", gridColumns: 3, showLookbookBadge: true } },
    { key: "safira-flagship", name: "Safira Flagship", tier: "Premium", desc: "Azul marinho executivo para marca consolidada", design: { presetKey: "safira-flagship", backgroundColor: "#F0F4FF", surfaceColor: "#FFFFFF", surfaceSubtleColor: "#E0E7FF", primaryColor: "#1E3A8A", primaryTextColor: "#FFFFFF", accentColor: "#3B82F6", textColor: "#172554", mutedTextColor: "#475569", borderColor: "#C7D2FE", sidebarColor: "#172554", displayFont: "Montserrat", bodyFont: "Plus Jakarta Sans", cornerRadiusMode: "ROUND_EIGHT", densityMode: "balanced", heroLayout: "split", gridColumns: 3, showLookbookBadge: true } },
    { key: "galeria-marfim", name: "Galeria Marfim", tier: "Premium", desc: "Estética de galeria de arte com respiro amplo", design: { presetKey: "galeria-marfim", backgroundColor: "#FBFBF9", surfaceColor: "#FFFFFF", surfaceSubtleColor: "#F2F0EB", primaryColor: "#292524", primaryTextColor: "#FAFAF9", accentColor: "#A8A29E", textColor: "#1C1917", mutedTextColor: "#57534E", borderColor: "#E7E5E4", sidebarColor: "#1C1917", displayFont: "Playfair Display", bodyFont: "Work Sans", cornerRadiusMode: "ROUND_FOUR", densityMode: "airy", heroLayout: "editorial", gridColumns: 3, showLookbookBadge: true } }
  ];

  if ("serviceWorker" in navigator) {
    navigator.serviceWorker.register("/painel/sw.js", { scope: "/painel/" }).catch(() => {});
  }

  const state = {
    session: loadSession(),
    theme: localStorage.getItem(THEME_KEY) || "light",
    authTab: "login",
    activeTab: "home",
    busy: false,
    msgOk: null,
    msgErr: null,
    copiedLink: false,
    copiedPix: false,
    planCatalog: {
      basicPlanMonthlyPrice: 49.90,
      intermediaryPlanMonthlyPrice: 89.90,
      premiumPlanMonthlyPrice: 129.90,
      hasOfficialPixKey: false,
      hasPlatformMercadoPagoConnected: false,
      supportWhatsappNumber: null
    },
    planExpiresAt: null,
    isCourtesyVip: false,
    giftCardInput: "",
    activePlanOrder: null,
    orders: [],
    ordersLoaded: false,
    mpStatus: null,
    showMoreSheet: false,
    showProfileModal: false,
    store: createDefaultStore(),
    categories: [],
    products: [],
    publishToken: null,
    search: "",
    filterCategory: "",
    newCategoryName: "",
    editingProductIndex: null,
    showProductForm: false,
    storeLoaded: false,
    productDraft: null,
    requiresRegisterEmailOtp: false,
    regDraft: { fullName: "", username: "", email: "", password: "", emailOtpCode: "" }
  };

  state.productDraft = createEmptyProductDraft();
  applyTheme(state.theme);

  function createDefaultStore() {
    return {
      storeName: "Minha Loja",
      storeSlug: "minha-loja",
      whatsappNumber: "",
      storeDescription: "Catálogo oficial com nossos produtos e novidades.",
      logoPublicUrl: null,
      defaultLowStockThreshold: 3,
      isSiteActive: true,
      autoSyncOnlineOnSave: true,
      planTier: "Basico",
      designPreset: "minimalista-luxo",
      manualSalesEnabled: true,
      directPixSalesEnabled: false,
      pixGatewayMode: "DirectPix",
      storePixKey: "",
      storePixReceiverName: "",
      originCep: "",
      originCity: "",
      originState: "",
      deliveryScopeMode: "TodoBrasil",
      maxDeliveryDistanceKm: 0,
      defaultPackageWeightGrams: 450
    };
  }

  function createEmptyProductDraft() {
    return {
      sourceId: cryptoRandomUuid(),
      name: "",
      categoryName: (state && state.categories && state.categories[0]?.name) || "Geral",
      basePrice: "",
      description: "",
      isVisibleOnSite: true,
      images: [],
      variants: [
        { color: "", colorHex: "#18181b", size: "", stockQuantity: "", lowStockThreshold: 3, isActive: true }
      ]
    };
  }

  function cryptoRandomUuid() {
    if (window.crypto && crypto.randomUUID) return crypto.randomUUID();
    return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, (c) => {
      const r = (Math.random() * 16) | 0;
      return (c === "x" ? r : (r & 0x3) | 0x8).toString(16);
    });
  }

  function loadSession() {
    try {
      const raw = localStorage.getItem(SESSION_KEY);
      return raw ? JSON.parse(raw) : null;
    } catch { return null; }
  }

  function saveSession(session) {
    state.session = session;
    try {
      if (session) localStorage.setItem(SESSION_KEY, JSON.stringify(session));
      else localStorage.removeItem(SESSION_KEY);
    } catch {}
  }

  function applyTheme(theme) {
    state.theme = theme === "light" ? "light" : "dark";
    document.body.classList.toggle("theme-light", state.theme === "light");
    try { localStorage.setItem(THEME_KEY, state.theme); } catch {}
  }

  function slugify(text) {
    return (text || "minha-loja")
      .toString()
      .normalize("NFD")
      .replace(/[\u0300-\u036f]/g, "")
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, "-")
      .replace(/^-+|-+$/g, "") || "minha-loja";
  }

  function esc(val) {
    return String(val ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function formatMoney(v) {
    return Number(v || 0).toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
  }

  function formatDateBr(iso) {
    if (!iso) return "";
    try { return new Date(iso).toLocaleDateString("pt-BR"); } catch { return ""; }
  }

  function parsePrice(raw) {
    const clean = String(raw ?? "0").replace(/[^\d,.-]/g, "").replace(",", ".");
    const num = Number(clean);
    return Number.isFinite(num) && num >= 0 ? Math.round(num * 100) / 100 : 0;
  }

  function getPublicStoreUrl() {
    const slug = slugify(state.store.storeSlug || state.session?.storeSlug || state.store.storeName);
    return `${DEFAULT_API_ORIGIN}/loja/${slug}`;
  }

  function getUserInitials() {
    const name = (state.session?.fullName || state.session?.username || "YL").trim();
    const parts = name.split(/\s+/).filter(Boolean);
    if (parts.length >= 2) return (parts[0][0] + parts[1][0]).toUpperCase();
    return name.slice(0, 2).toUpperCase();
  }

  function getGreeting() {
    const h = new Date().getHours();
    const prefix = h < 12 ? "Bom dia" : h < 18 ? "Boa tarde" : "Boa noite";
    const first = (state.session?.fullName || state.session?.username || "Lojista").trim().split(/\s+/)[0];
    return `${prefix}, ${first}!`;
  }

  function getPlanRank(tier) {
    const raw = (tier || "Basico").toLowerCase();
    if (raw.includes("prem")) return 2;
    if (raw.includes("inter")) return 1;
    return 0;
  }

  function getPlanMeta() {
    const rank = getPlanRank(state.store.planTier || state.session?.planTier);
    if (rank === 2) return { rank: 2, key: "Premium", label: "✦ Premium", cls: "pill-gold", name: "Plano Premium" };
    if (rank === 1) return { rank: 1, key: "Intermediario", label: "◈ Meia Versão", cls: "pill-green", name: "Plano Intermediário" };
    return { rank: 0, key: "Basico", label: "● Plano Básico", cls: "pill-slate", name: "Plano Básico" };
  }

  function isThemeAllowed(themeObj) {
    return getPlanRank(state.store.planTier || state.session?.planTier) >= getPlanRank(themeObj.tier);
  }

  function setStatus(ok, err) {
    state.msgOk = ok || null;
    state.msgErr = err || null;
    render();
  }

  async function apiJson(path, options = {}) {
    const res = await fetch(`${API_BASE}${path}`, {
      ...options,
      headers: {
        "Content-Type": "application/json",
        ...(options.headers || {})
      }
    });
    const text = await res.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch { data = { raw: text }; }
    if (!res.ok) {
      const err = new Error(data?.error || data?.message || `Erro HTTP ${res.status}`);
      err.status = res.status;
      err.requiresEmailOtp = !!data?.requiresEmailOtp;
      throw err;
    }
    return data;
  }

  async function loadPublicCatalogMeta() {
    try {
      const data = await apiJson("/api/platform-sync/plan-catalog");
      if (data) {
        state.planCatalog = {
          basicPlanMonthlyPrice: Number(data.basicPlanMonthlyPrice || 49.90),
          intermediaryPlanMonthlyPrice: Number(data.intermediaryPlanMonthlyPrice || 89.90),
          premiumPlanMonthlyPrice: Number(data.premiumPlanMonthlyPrice || 129.90),
          hasOfficialPixKey: !!data.hasOfficialPixKey,
          hasPlatformMercadoPagoConnected: !!data.hasPlatformMercadoPagoConnected,
          supportWhatsappNumber: data.supportWhatsappNumber || null
        };
        render();
      }
    } catch {}
  }

  function buildStoreAuthHeaders(slug) {
    const headers = { "X-YouLink-Store-Slug": slug };
    if (state.session?.cloudSessionToken) headers["X-YouLink-Cloud-Token"] = state.session.cloudSessionToken;
    if (state.publishToken) headers["X-YouLink-Publish-Token"] = state.publishToken;
    return headers;
  }

  async function loadCloudOrders(silent = true) {
    if (!state.session) return;
    const slug = slugify(state.store.storeSlug || state.session.storeSlug || state.store.storeName);
    if (!slug) return;
    try {
      const headers = buildStoreAuthHeaders(slug);
      const res = await apiJson(`/api/cloud/orders?storeSlug=${encodeURIComponent(slug)}`, { headers });
      state.orders = Array.isArray(res?.orders) ? res.orders : [];
      state.ordersLoaded = true;
      render();
    } catch (err) {
      if (!silent) setStatus(null, `Pedidos: ${err.message}`);
    }
  }

  async function loadMercadoPagoStatus() {
    const slug = slugify(state.store.storeSlug || state.session?.storeSlug || state.store.storeName);
    if (!slug) return;
    try {
      state.mpStatus = await apiJson(`/api/payments/mercadopago/status?storeSlug=${encodeURIComponent(slug)}`);
      render();
    } catch {}
  }

  async function pullStoreViaPublicFallback(showMessage = false) {
    const email = encodeURIComponent(state.session?.email || "");
    const username = encodeURIComponent(state.session?.username || "");
    let planInfo = null;
    try {
      planInfo = await apiJson(`/api/cloud/plan-status?email=${email}&username=${username}`);
    } catch {}

    if (planInfo) {
      state.planExpiresAt = planInfo.planExpiresAt || null;
      state.isCourtesyVip = !!planInfo.isCourtesyVip;
    }

    const slug = (planInfo?.storeSlug || state.session?.storeSlug || "").trim();
    const fallbackName = planInfo?.storeName || state.session?.storeName || "Minha Loja";
    const fallbackPlan = planInfo?.planTier || state.session?.planTier || "Basico";

    if (!slug) {
      state.store = { ...createDefaultStore(), storeName: fallbackName, planTier: fallbackPlan };
      state.categories = [];
      state.products = [];
      state.storeLoaded = true;
      if (showMessage) setStatus("Sua conta está pronta! Configure o nome da loja e adicione seu primeiro produto.", null);
      return;
    }

    let info = null;
    try {
      info = await apiJson(`/api/public/stores/${encodeURIComponent(slug)}/store-info`);
    } catch (e) {
      if (e.status === 404) {
        state.store = { ...createDefaultStore(), storeName: fallbackName, storeSlug: slug, planTier: fallbackPlan };
        state.categories = [];
        state.products = [];
        state.storeLoaded = true;
        if (showMessage) setStatus("Sua loja está pronta para a primeira publicação!", null);
        return;
      }
      throw e;
    }

    const [cats, cards] = await Promise.all([
      apiJson(`/api/public/stores/${encodeURIComponent(slug)}/categories`).catch(() => []),
      apiJson(`/api/public/stores/${encodeURIComponent(slug)}/products`).catch(() => [])
    ]);

    const details = await Promise.all(
      (Array.isArray(cards) ? cards : []).map((card) =>
        apiJson(`/api/public/stores/${encodeURIComponent(slug)}/products/${encodeURIComponent(card.slug || card.id)}`).catch(() => card)
      )
    );

    state.store = {
      ...createDefaultStore(),
      storeName: info?.storeName || fallbackName,
      storeSlug: info?.storeSlug || slug,
      whatsappNumber: info?.whatsappNumber || "",
      storeDescription: info?.storeDescription || "Catálogo oficial da loja.",
      logoPublicUrl: info?.logoPublicUrl || null,
      isSiteActive: info?.isSiteActive !== false,
      autoSyncOnlineOnSave: true,
      planTier: info?.planTier || fallbackPlan,
      designPreset: info?.design?.presetKey || "minimalista-luxo",
      directPixSalesEnabled: !!info?.directPixSalesReady,
      pixGatewayMode: info?.pixGatewayMode || "DirectPix",
      originCep: info?.originCep || "",
      originCity: info?.originCity || "",
      originState: info?.originState || "",
      deliveryScopeMode: info?.deliveryScopeMode || "TodoBrasil",
      maxDeliveryDistanceKm: Number(info?.maxDeliveryDistanceKm || 0),
      defaultPackageWeightGrams: Number(info?.defaultPackageWeightGrams || 450)
    };
    state.categories = (Array.isArray(cats) ? cats : []).map((c, idx) => ({
      name: c.name || "Geral",
      description: c.description || "",
      displayOrder: c.displayOrder || idx + 1,
      isActive: c.isActive !== false
    }));
    state.products = details.map((prod) => ({
      sourceId: prod.id || cryptoRandomUuid(),
      name: prod.name || "",
      slug: prod.slug || slugify(prod.name),
      categoryName: prod.categoryName || "Geral",
      description: prod.description || "",
      basePrice: Number(prod.basePrice || 0),
      isVisibleOnSite: true,
      variants: (prod.variants || []).map((v) => ({
        color: v.color || "Padrão",
        colorHex: v.colorHex || "#18181b",
        size: v.size || "Único",
        sku: v.sku || null,
        stockQuantity: Number(v.stockQuantity || 0),
        lowStockThreshold: Number(v.lowStockThreshold || 3),
        isActive: v.isActive !== false
      })),
      images: (prod.images || (prod.primaryImageUrl ? [{ publicUrl: prod.primaryImageUrl, isPrimary: true }] : [])).map((img, i) => ({
        publicUrl: img.publicUrl,
        storagePath: img.storagePath || img.publicUrl,
        altText: img.altText || prod.name,
        displayOrder: i,
        isPrimary: i === 0 || !!img.isPrimary
      }))
    }));
    state.storeLoaded = true;
    saveSession({ ...state.session, storeSlug: state.store.storeSlug, storeName: state.store.storeName, planTier: state.store.planTier });
    if (showMessage) setStatus(`☁️ Loja '${state.store.storeName}' carregada (${state.products.length} produtos)!`, null);
    loadCloudOrders(true);
    loadMercadoPagoStatus();
  }

  async function pullStoreFromCloud(showMessage = false) {
    if (!state.session?.cloudSessionToken && !state.publishToken) {
      state.storeLoaded = true;
      return;
    }
    state.busy = true;
    state.msgErr = null;
    render();
    try {
      const headers = {};
      if (state.session?.cloudSessionToken) headers["X-YouLink-Cloud-Token"] = state.session.cloudSessionToken;
      if (state.publishToken) headers["X-YouLink-Publish-Token"] = state.publishToken;

      const res = await apiJson("/api/cloud/pull-store", {
        method: "POST",
        headers,
        body: JSON.stringify({
          email: state.session.email || null,
          username: state.session.username || null,
          cloudSessionToken: state.session.cloudSessionToken || null,
          publishToken: state.publishToken || null,
          storeSlug: state.session.storeSlug || null
        })
      });

      if (res?.found && res.payload) {
        applyPulledPayload(res);
        if (showMessage) setStatus(`☁️ Loja '${state.store.storeName}' sincronizada (${state.products.length} produtos)!`, null);
      } else if (showMessage) {
        setStatus("Sua conta está pronta! Cadastre seu primeiro produto.", null);
      }
      state.storeLoaded = true;
      loadCloudOrders(true);
      loadMercadoPagoStatus();
    } catch (err) {
      if (err && err.status === 404) {
        try {
          await pullStoreViaPublicFallback(showMessage);
          return;
        } catch (fallbackErr) {
          state.storeLoaded = false;
          setStatus(null, `Não foi possível carregar sua loja da nuvem: ${fallbackErr.message}.`);
          return;
        }
      }
      state.storeLoaded = false;
      setStatus(null, `Não foi possível carregar sua loja da nuvem: ${err.message}. Toque em "🔄 Atualizar da Nuvem".`);
    } finally {
      state.busy = false;
      render();
    }
  }

  function applyPulledPayload(res) {
    const p = res.payload || {};
    const s = p.settings || {};
    state.store = {
      ...createDefaultStore(),
      storeName: s.storeName || state.session?.storeName || "Minha Loja",
      storeSlug: res.storeSlug || s.storeSlug || slugify(s.storeName),
      whatsappNumber: s.whatsappNumber || "",
      storeDescription: s.storeDescription || "Catálogo oficial da loja.",
      logoPublicUrl: s.logoPublicUrl || null,
      defaultLowStockThreshold: Number(s.defaultLowStockThreshold ?? 3),
      isSiteActive: s.isSiteActive !== false,
      autoSyncOnlineOnSave: s.autoSyncOnlineOnSave !== false,
      planTier: s.planTier || state.session?.planTier || "Basico",
      designPreset: s.design?.presetKey || "minimalista-luxo",
      manualSalesEnabled: s.manualSalesEnabled !== false,
      directPixSalesEnabled: !!s.directPixSalesEnabled,
      pixGatewayMode: s.pixGatewayMode || "DirectPix",
      storePixKey: s.storePixKey || "",
      storePixReceiverName: s.storePixReceiverName || "",
      originCep: s.originCep || "",
      originCity: s.originCity || "",
      originState: s.originState || "",
      deliveryScopeMode: s.deliveryScopeMode || "TodoBrasil",
      maxDeliveryDistanceKm: Number(s.maxDeliveryDistanceKm || 0),
      defaultPackageWeightGrams: Number(s.defaultPackageWeightGrams || 450)
    };
    state.categories = (p.categories || []).map((c, idx) => ({
      name: c.name || "Geral",
      description: c.description || "",
      displayOrder: c.displayOrder || idx + 1,
      isActive: c.isActive !== false
    }));
    state.products = (p.catalogSnapshot || []).map((prod) => ({
      sourceId: prod.sourceId || cryptoRandomUuid(),
      name: prod.name || "",
      slug: prod.slug || slugify(prod.name),
      categoryName: prod.categoryName || "Geral",
      description: prod.description || "",
      basePrice: Number(prod.basePrice || 0),
      isVisibleOnSite: prod.isVisibleOnSite !== false,
      variants: (prod.variants || []).map((v) => ({
        color: v.color || "Padrão",
        colorHex: v.colorHex || "#18181b",
        size: v.size || "Único",
        sku: v.sku || null,
        stockQuantity: Number(v.stockQuantity || 0),
        lowStockThreshold: Number(v.lowStockThreshold || 3),
        isActive: v.isActive !== false
      })),
      images: (prod.images || []).map((img, i) => ({
        publicUrl: img.publicUrl,
        storagePath: img.storagePath || img.publicUrl,
        altText: img.altText || prod.name,
        displayOrder: i,
        isPrimary: i === 0 || !!img.isPrimary
      }))
    }));
    if (res.publishToken) state.publishToken = res.publishToken;
    saveSession({ ...state.session, storeSlug: state.store.storeSlug, storeName: state.store.storeName, planTier: state.store.planTier });
  }

  function buildPublishPayload(slug) {
    const catNames = new Set(state.categories.map((c) => (c.name || "").trim()).filter(Boolean));
    for (const p of state.products) {
      if (p.categoryName && p.categoryName.trim()) catNames.add(p.categoryName.trim());
    }
    if (catNames.size === 0) catNames.add("Geral");

    const selectedTheme = STORE_THEMES.find((t) => t.key === state.store.designPreset) || STORE_THEMES[0];

    return {
      settings: {
        storeName: (state.store.storeName || "Minha Loja").trim(),
        storeSlug: slug,
        whatsappNumber: (state.store.whatsappNumber || "").trim() || "5511900000000",
        storeDescription: (state.store.storeDescription || "Catálogo digital oficial da loja.").trim(),
        logoPublicUrl: state.store.logoPublicUrl || null,
        defaultLowStockThreshold: Math.max(0, parseInt(state.store.defaultLowStockThreshold ?? 3, 10) || 3),
        onlineBaseUrl: DEFAULT_API_ORIGIN,
        autoSyncOnlineOnSave: state.store.autoSyncOnlineOnSave !== false,
        isSiteActive: state.store.isSiteActive !== false,
        planTier: state.store.planTier || "Basico",
        design: selectedTheme.design,
        manualSalesEnabled: state.store.manualSalesEnabled !== false,
        directPixSalesEnabled: !!state.store.directPixSalesEnabled,
        pixGatewayMode: state.store.pixGatewayMode || "DirectPix",
        storePixKey: (state.store.storePixKey || "").trim() || null,
        storePixReceiverName: (state.store.storePixReceiverName || "").trim() || null,
        originCep: (state.store.originCep || "").trim() || null,
        originCity: (state.store.originCity || "").trim() || null,
        originState: (state.store.originState || "").trim().toUpperCase().slice(0, 2) || null,
        deliveryScopeMode: state.store.deliveryScopeMode || "TodoBrasil",
        maxDeliveryDistanceKm: Math.max(0, parseInt(state.store.maxDeliveryDistanceKm ?? 0, 10) || 0),
        defaultPackageWeightGrams: Math.max(50, parseInt(state.store.defaultPackageWeightGrams ?? 450, 10) || 450),
        serverHostingMode: "PlatformShared",
        centralCatalogBaseUrl: DEFAULT_API_ORIGIN
      },
      categories: Array.from(catNames).map((name, idx) => ({
        name,
        description: "",
        displayOrder: idx + 1,
        isActive: true
      })),
      products: state.products.map((p) => ({
        categoryName: p.categoryName || "Geral",
        name: p.name,
        description: p.description || "",
        basePrice: Number(p.basePrice || 0),
        isVisibleOnSite: p.isVisibleOnSite !== false
      })),
      catalogSnapshot: state.products.map((p) => ({
        sourceId: p.sourceId || cryptoRandomUuid(),
        categoryName: p.categoryName || "Geral",
        categorySlug: slugify(p.categoryName || "Geral"),
        name: p.name,
        slug: p.slug || slugify(p.name),
        description: p.description || "",
        basePrice: Number(p.basePrice || 0),
        isVisibleOnSite: p.isVisibleOnSite !== false,
        variants: (() => {
          const mapped = (p.variants || [])
            .filter((v) => (v.color || "").trim() || (v.size || "").trim() || Number(v.stockQuantity) > 0)
            .map((v) => ({
              color: (v.color || "Padrão").trim() || "Padrão",
              colorHex: v.colorHex || "#18181b",
              size: (v.size || "Único").trim().toUpperCase() || "Único",
              sku: v.sku || null,
              stockQuantity: Math.max(0, Number(v.stockQuantity || 0)),
              lowStockThreshold: Number(v.lowStockThreshold || 3),
              isActive: true
            }));
          return mapped.length > 0
            ? mapped
            : [{ color: "Padrão", colorHex: "#18181b", size: "Único", sku: null, stockQuantity: 0, lowStockThreshold: 3, isActive: true }];
        })(),
        images: (p.images || []).filter((img) => img.publicUrl).map((img, i) => ({
          publicUrl: img.publicUrl,
          storagePath: img.storagePath || img.publicUrl,
          altText: img.altText || p.name,
          displayOrder: i,
          isPrimary: i === 0
        }))
      }))
    };
  }

  async function publishStoreToCloud(successMessage = "☁️ Alterações salvas e publicadas no site!") {
    if (!state.session) return;
    if (!state.storeLoaded) {
      setStatus(null, "Toque primeiro em \"🔄 Atualizar da Nuvem\" antes de salvar para proteger seus produtos.");
      return;
    }
    state.busy = true;
    state.msgErr = null;
    render();
    try {
      const slug = slugify(state.store.storeSlug || state.store.storeName);
      state.store.storeSlug = slug;

      const headers = buildStoreAuthHeaders(slug);

      const res = await apiJson(`/api/cloud/publish-store?storeSlug=${encodeURIComponent(slug)}`, {
        method: "POST",
        headers,
        body: JSON.stringify(buildPublishPayload(slug))
      });

      if (res?.publishToken) state.publishToken = res.publishToken;
      if (res?.storeSlug) state.store.storeSlug = res.storeSlug;
      saveSession({ ...state.session, storeSlug: state.store.storeSlug, storeName: state.store.storeName });
      setStatus(successMessage, null);
    } catch (err) {
      setStatus(null, `Não foi possível publicar agora: ${err.message}`);
    } finally {
      state.busy = false;
      render();
    }
  }

  function findDuplicateGroups() {
    const groups = new Map();
    state.products.forEach((p, idx) => {
      const key = `${slugify(p.name)}::${slugify(p.categoryName || "Geral")}`;
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(idx);
    });
    return Array.from(groups.values()).filter((g) => g.length > 1);
  }

  async function mergeDuplicates() {
    const dups = findDuplicateGroups();
    if (dups.length === 0) return;

    const merged = [];
    const seen = new Map();
    for (const p of state.products) {
      const key = `${slugify(p.name)}::${slugify(p.categoryName || "Geral")}`;
      if (!seen.has(key)) {
        const clone = JSON.parse(JSON.stringify(p));
        seen.set(key, clone);
        merged.push(clone);
      } else {
        const target = seen.get(key);
        for (const v of (p.variants || [])) {
          const existing = (target.variants || []).find(
            (ev) => slugify(ev.color) === slugify(v.color) && slugify(ev.size) === slugify(v.size)
          );
          if (existing) existing.stockQuantity = (Number(existing.stockQuantity) || 0) + (Number(v.stockQuantity) || 0);
          else target.variants.push(v);
        }
        for (const img of (p.images || [])) {
          if (!(target.images || []).some((ti) => ti.publicUrl === img.publicUrl)) target.images.push(img);
        }
      }
    }

    state.products = merged;
    await publishStoreToCloud("🔗 Produtos duplicados unidos com sucesso!");
  }

  async function compressImageToDataUrl(file, maxDim = 900) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onerror = () => reject(new Error("Não foi possível ler a imagem."));
      reader.onload = () => {
        const img = new Image();
        img.onerror = () => reject(new Error("Formato de imagem inválido."));
        img.onload = () => {
          let w = img.width;
          let h = img.height;
          if (w > maxDim || h > maxDim) {
            if (w >= h) { h = Math.round((h * maxDim) / w); w = maxDim; }
            else { w = Math.round((w * maxDim) / h); h = maxDim; }
          }
          const canvas = document.createElement("canvas");
          canvas.width = w;
          canvas.height = h;
          canvas.getContext("2d").drawImage(img, 0, 0, w, h);
          resolve(canvas.toDataURL("image/jpeg", 0.78));
        };
        img.src = reader.result;
      };
      reader.readAsDataURL(file);
    });
  }

  function totalStock(product) {
    return (product.variants || []).reduce((acc, v) => acc + Math.max(0, Number(v.stockQuantity) || 0), 0);
  }

  function lowStockCount() {
    let count = 0;
    const defThreshold = Number(state.store.defaultLowStockThreshold ?? 3);
    for (const p of state.products) {
      for (const v of (p.variants || [])) {
        if ((Number(v.stockQuantity) || 0) <= (Number(v.lowStockThreshold ?? defThreshold) || defThreshold)) count++;
      }
    }
    return count;
  }

  /* ================= RENDERIZAÇÃO ================= */

  function render() {
    const root = document.getElementById("app");
    if (!root) return;

    if (!state.session) {
      root.innerHTML = renderAuthScreen();
      bindAuthEvents();
      return;
    }

    const plan = getPlanMeta();
    const storeUrl = getPublicStoreUrl();

    root.innerHTML = `
      <div class="adm-shell">
        <aside class="adm-sidebar">
          <div class="adm-brand">
            <div class="adm-brand-top">
              <div class="adm-brand-mark">YL</div>
              <span class="adm-brand-wordmark">YouLink</span>
            </div>
            <div class="adm-brand-store-name">${esc((state.store.storeName || "Minha Loja").toUpperCase())}</div>
            <div class="adm-brand-slug">/loja/${esc(slugify(state.store.storeSlug || state.store.storeName))}</div>
          </div>

          <div class="adm-nav-group">
            <div class="adm-nav-label">Gestão da Loja</div>
            <button type="button" class="adm-nav-item ${state.activeTab === "home" ? "active" : ""}" data-tab="home">🏠 Início</button>
            <button type="button" class="adm-nav-item ${state.activeTab === "products" ? "active" : ""}" data-tab="products">🛍️ Produtos</button>
            <button type="button" class="adm-nav-item ${state.activeTab === "stock" ? "active" : ""}" data-tab="stock">📦 Estoque</button>
            <button type="button" class="adm-nav-item ${state.activeTab === "orders" ? "active" : ""}" data-tab="orders">🧾 Pedidos</button>
          </div>

          <div class="adm-nav-group">
            <div class="adm-nav-label">Personalização &amp; Plano</div>
            <button type="button" class="adm-nav-item ${state.activeTab === "design" ? "active" : ""}" data-tab="design">🎨 Aparência da Loja</button>
            <button type="button" class="adm-nav-item ${state.activeTab === "plans" ? "active" : ""}" data-tab="plans">⭐ Planos &amp; VIP</button>
            <button type="button" class="adm-nav-item ${state.activeTab === "settings" ? "active" : ""}" data-tab="settings">⚙️ Configurações</button>
          </div>

          <div class="adm-nav-group" style="margin-top:auto;">
            <button type="button" class="adm-nav-item" id="sidebar-theme-btn">${state.theme === "dark" ? "☀️ Tema Claro" : "🌙 Tema Escuro"}</button>
            <button type="button" class="adm-nav-item" id="sidebar-logout-btn" style="color:var(--adm-danger);">🚪 Sair</button>
          </div>
        </aside>

        <div class="adm-main">
          <header class="adm-topbar">
            <div class="adm-topbar-meta">
              <span class="pill-badge ${state.store.isSiteActive ? "pill-green" : "pill-red"}">
                ${state.store.isSiteActive ? "● Loja Aberta" : "● Loja Pausada"}
              </span>
              <button type="button" class="pill-badge ${plan.cls}" data-tab="plans" style="cursor:pointer;">
                ${plan.label}
              </button>
            </div>

            <div class="adm-topbar-actions">
              <button type="button" class="adm-btn adm-btn-outline adm-btn-sm mobile-only" id="topbar-theme-btn" title="Alternar tema">
                ${state.theme === "dark" ? "☀️" : "🌙"}
              </button>
              <a href="${esc(storeUrl)}" target="_blank" rel="noopener" class="adm-btn adm-btn-outline adm-btn-sm">
                🌐 <span class="hide-xs">Ver </span>Loja
              </a>
              <button type="button" class="adm-btn adm-btn-outline adm-btn-sm" id="topbar-profile-btn">
                <span class="adm-avatar">${esc(getUserInitials())}</span>
                <span class="hide-xs">${esc((state.session.fullName || state.session.username || "Conta").split(" ")[0])}</span>
              </button>
            </div>
          </header>

          <main class="adm-content">
            ${state.msgOk ? `<div class="banner banner-ok">${esc(state.msgOk)}</div>` : ""}
            ${state.msgErr ? `<div class="banner banner-err">${esc(state.msgErr)}</div>` : ""}
            ${renderActiveTab()}
          </main>
        </div>

        <nav class="bottom-nav">
          <button type="button" class="${state.activeTab === "home" ? "active" : ""}" data-tab="home">
            <span class="nav-ic">🏠</span><span>Início</span>
          </button>
          <button type="button" class="${state.activeTab === "products" ? "active" : ""}" data-tab="products">
            <span class="nav-ic">🛍️</span><span>Produtos</span>
          </button>
          <button type="button" class="${state.activeTab === "orders" ? "active" : ""}" data-tab="orders">
            <span class="nav-ic">🧾</span><span>Pedidos</span>
          </button>
          <button type="button" class="${["stock","design","plans","settings"].includes(state.activeTab) || state.showMoreSheet ? "active" : ""}" id="open-more-btn">
            <span class="nav-ic">⋯</span><span>Mais</span>
          </button>
        </nav>

        ${state.showMoreSheet ? renderMoreSheet() : ""}
        ${state.showProfileModal ? renderProfileModal() : ""}
      </div>
    `;

    bindShellEvents();
  }

  function renderAuthScreen() {
    return `
      <div class="auth-wrap">
        <div class="auth-card">
          <div style="display:flex;align-items:center;gap:0.7rem;margin-bottom:0.4rem;">
            <div class="adm-brand-mark">YL</div>
            <div>
              <div style="font-weight:800;font-size:1.2rem;">YouLink</div>
              <div style="font-size:0.82rem;color:var(--adm-muted);">Painel oficial da sua loja</div>
            </div>
          </div>

          <div class="auth-tabs">
            <button type="button" class="${state.authTab === "login" ? "active" : ""}" id="tab-auth-login">Entrar</button>
            <button type="button" class="${state.authTab === "register" ? "active" : ""}" id="tab-auth-register">Criar Conta</button>
          </div>

          ${state.msgOk ? `<div class="banner banner-ok">${esc(state.msgOk)}</div>` : ""}
          ${state.msgErr ? `<div class="banner banner-err">${esc(state.msgErr)}</div>` : ""}

          ${state.authTab === "login" ? `
            <form id="login-form">
              <div class="field">
                <label>E-mail ou usuário</label>
                <input class="input" name="login" required autocomplete="username" placeholder="seu@email.com" />
              </div>
              <div class="field">
                <label>Senha</label>
                <input class="input" name="password" type="password" required autocomplete="current-password" placeholder="Sua senha" />
              </div>
              <button type="submit" class="adm-btn adm-btn-primary adm-btn-block" ${state.busy ? "disabled" : ""}>
                ${state.busy ? "Entrando..." : "Entrar na minha loja"}
              </button>
            </form>
          ` : `
            <form id="register-form">
              <div class="field">
                <label>Seu nome</label>
                <input class="input" name="fullName" required value="${esc(state.regDraft?.fullName || "")}" placeholder="Ex.: Lojista Modelo" />
              </div>
              <div class="field">
                <label>Usuário</label>
                <input class="input" name="username" required value="${esc(state.regDraft?.username || "")}" placeholder="ex: minhaloja" />
              </div>
              <div class="field">
                <label>E-mail</label>
                <input class="input" name="email" type="email" required value="${esc(state.regDraft?.email || "")}" placeholder="seu@email.com" />
              </div>
              <div class="field">
                <label>Senha</label>
                <input class="input" name="password" type="password" minlength="8" required value="${esc(state.regDraft?.password || "")}" placeholder="Mínimo 8 caracteres" />
              </div>
              ${state.requiresRegisterEmailOtp ? `
              <div class="field">
                <label>Código enviado para seu e-mail (6 dígitos)</label>
                <input class="input" name="emailOtpCode" maxlength="6" required value="${esc(state.regDraft?.emailOtpCode || "")}" placeholder="Ex.: 123456" />
              </div>` : ""}
              <button type="submit" class="adm-btn adm-btn-success adm-btn-block" ${state.busy ? "disabled" : ""}>
                ${state.busy ? "Validando..." : (state.requiresRegisterEmailOtp ? "Confirmar código e criar loja" : "Criar minha loja grátis")}
              </button>
            </form>
          `}
        </div>
      </div>
    `;
  }

  function renderActiveTab() {
    switch (state.activeTab) {
      case "products": return renderProductsTab();
      case "stock": return renderStockTab();
      case "orders": return renderOrdersTab();
      case "design": return renderDesignTab();
      case "plans": return renderPlansTab();
      case "settings": return renderSettingsTab();
      default: return renderHomeTab();
    }
  }

  function renderHomeTab() {
    const storeUrl = getPublicStoreUrl();
    const visibleProducts = state.products.filter((p) => p.isVisibleOnSite !== false).length;
    const totalUnits = state.products.reduce((s, p) => s + totalStock(p), 0);
    const lowStock = lowStockCount();
    const pendingOrders = state.orders.filter((o) => (o.status || "").toLowerCase().includes("aguard") || (o.status || "").toLowerCase().includes("pend")).length;

    return `
      <header class="yl-home-header">
        <div>
          <h1 class="yl-home-title">${esc(getGreeting())}</h1>
          <div class="yl-home-status-line">
            <span class="yl-status-dot ${state.store.isSiteActive ? "online" : "offline"}"></span>
            <span>${esc(state.store.storeName)}</span>
            <span>&bull;</span>
            <span>${state.store.isSiteActive ? "Loja aberta" : "Pausada"}</span>
          </div>
        </div>
      </header>

      <section class="adm-card yl-share-card">
        <div style="min-width:0;flex:1 1 240px;">
          <div class="yl-share-label">Link da sua loja</div>
          <div class="yl-share-url">${esc(storeUrl.replace(/^https?:\/\//, ""))}</div>
        </div>
        <div class="yl-share-actions">
          <button type="button" class="adm-btn adm-btn-primary" id="copy-link-btn">
            ${state.copiedLink ? "✓ Link Copiado!" : "📋 Copiar Link"}
          </button>
          <button type="button" class="adm-btn adm-btn-success" id="share-wa-btn">
            💬 WhatsApp
          </button>
        </div>
      </section>

      <button type="button" class="adm-btn adm-btn-primary" id="home-new-product-btn" style="width:100%;min-height:52px;font-size:1rem;font-weight:800;border-radius:14px;">+ Cadastrar novo produto</button>

      <section class="yl-summary-grid" style="grid-template-columns:repeat(2,minmax(0,1fr));">
        <button type="button" class="yl-summary-card" data-tab="orders">
          <span class="yl-summary-label">Pedidos novos</span>
          <strong class="yl-summary-value" style="color:var(--yl-success-text);">${pendingOrders || state.orders.length}</strong>
        </button>

        <button type="button" class="yl-summary-card" data-tab="products">
          <span class="yl-summary-label">Produtos ativos</span>
          <strong class="yl-summary-value">${visibleProducts}</strong>
        </button>

        <button type="button" class="yl-summary-card" data-tab="stock">
          <span class="yl-summary-label">Estoque total</span>
          <strong class="yl-summary-value">${totalUnits}</strong>
        </button>

        <button type="button" class="yl-summary-card" data-tab="stock">
          <span class="yl-summary-label">Ajustar estoque</span>
          <strong class="yl-summary-value" style="color:${lowStock > 0 ? "var(--adm-warning)" : "var(--yl-success-text)"};">${lowStock > 0 ? `${lowStock} alerta(s)` : "Em dia"}</strong>
        </button>
      </section>

      <section>
        <h2 class="yl-mobile-quick-heading">O que você quer fazer agora?</h2>
        <div class="yl-mobile-quick-grid">
          <button type="button" class="yl-mobile-quick-tile" id="quick-new-prod">
            <span class="yl-mobile-quick-icon yl-icon-blue">➕</span>
            <strong>Cadastrar Produto</strong>
            <span>Foto, cor, tamanho e preço</span>
          </button>

          <button type="button" class="yl-mobile-quick-tile" data-tab="stock">
            <span class="yl-mobile-quick-icon yl-icon-green">📦</span>
            <strong>Controlar Estoque</strong>
            <span>Ajustar quantidades em 1 toque</span>
          </button>

          <button type="button" class="yl-mobile-quick-tile" data-tab="plans">
            <span class="yl-mobile-quick-icon yl-icon-gold">⭐</span>
            <strong>Planos &amp; VIP</strong>
            <span>Temas, Gift Card e assinatura</span>
          </button>

          <button type="button" class="yl-mobile-quick-tile" data-tab="settings">
            <span class="yl-mobile-quick-icon yl-icon-slate">⚙️</span>
            <strong>Configurações</strong>
            <span>Logo, WhatsApp, Pix e Frete</span>
          </button>
        </div>
      </section>

      <div class="adm-card" style="display:flex;align-items:center;justify-content:space-between;gap:0.8rem;flex-wrap:wrap;">
        <div>
          <div style="font-weight:800;font-size:0.96rem;">☁️ Sincronização em Nuvem</div>
          <div style="font-size:0.84rem;color:var(--adm-muted);">Puxe atualizações de outro aparelho ou publique agora.</div>
        </div>
        <div style="display:flex;gap:0.55rem;flex-wrap:wrap;">
          <button type="button" class="adm-btn adm-btn-outline adm-btn-sm" id="sync-pull-btn" ${state.busy ? "disabled" : ""}>🔄 Atualizar da Nuvem</button>
          <button type="button" class="adm-btn adm-btn-primary adm-btn-sm" id="sync-push-btn" ${state.busy ? "disabled" : ""}>☁️ Publicar Agora</button>
        </div>
      </div>
    `;
  }

  function renderProductsTab() {
    const dups = findDuplicateGroups();
    const filtered = state.products
      .map((p, idx) => ({ p, idx }))
      .filter(({ p }) => {
        const q = state.search.trim().toLowerCase();
        if (q && !(p.name || "").toLowerCase().includes(q) && !(p.categoryName || "").toLowerCase().includes(q)) return false;
        if (state.filterCategory && (p.categoryName || "Geral") !== state.filterCategory) return false;
        return true;
      });

    const catNames = Array.from(new Set([
      ...state.categories.map((c) => c.name).filter(Boolean),
      ...state.products.map((p) => p.categoryName || "Geral")
    ])).sort();

    return `
      <div class="yl-home-header">
        <div>
          <h1 class="yl-home-title">Produtos</h1>
          <div class="yl-home-status-line">${state.products.length} item(ns) no catálogo</div>
        </div>
        <button type="button" class="adm-btn adm-btn-primary" id="open-new-product-btn">+ Novo Produto</button>
      </div>

      ${dups.length > 0 ? `
        <div class="banner banner-warn" style="display:flex;align-items:center;justify-content:space-between;gap:0.75rem;flex-wrap:wrap;">
          <span>⚠️ Encontramos ${dups.length} produto(s) com nome repetido.</span>
          <button type="button" class="adm-btn adm-btn-primary adm-btn-sm" id="merge-dups-btn" ${state.busy ? "disabled" : ""}>🔗 Unir Duplicados</button>
        </div>
      ` : ""}

      ${state.showProductForm ? renderProductForm(catNames) : ""}

      <div class="adm-card" style="padding:0.9rem;">
        <div class="form-grid" style="gap:0.65rem;">
          <input class="input" id="search-products-input" placeholder="🔍 Buscar produto..." value="${esc(state.search)}" />
          <select id="filter-category-select">
            <option value="">Todas as categorias (${catNames.length})</option>
            ${catNames.map((c) => `<option value="${esc(c)}" ${state.filterCategory === c ? "selected" : ""}>${esc(c)}</option>`).join("")}
          </select>
        </div>
        <div style="display:flex;gap:0.5rem;margin-top:0.65rem;flex-wrap:wrap;">
          <input class="input" id="new-category-input" style="flex:1 1 180px;" placeholder="Nova categoria (ex.: Camisas, Calças...)" value="${esc(state.newCategoryName)}" />
          <button type="button" class="adm-btn adm-btn-outline adm-btn-sm" id="add-category-btn">+ Criar Categoria</button>
        </div>
      </div>

      <div class="product-list">
        ${filtered.length === 0 ? `
          <div class="adm-card" style="text-align:center;padding:2.1rem 1rem;">
            <div style="font-size:2.2rem;margin-bottom:0.4rem;">🛍️</div>
            <div style="font-weight:800;font-size:1.05rem;margin-bottom:0.3rem;">Nenhum produto encontrado</div>
            <div style="font-size:0.88rem;color:var(--adm-muted);margin-bottom:1.1rem;">Toque em "+ Novo Produto" para cadastrar com cores, tamanhos e fotos.</div>
            <button type="button" class="adm-btn adm-btn-primary" id="empty-new-prod-btn">+ Cadastrar primeiro produto</button>
          </div>
        ` : filtered.map(({ p, idx }) => {
          const thumb = p.images?.[0]?.publicUrl || "";
          const stock = totalStock(p);
          return `
            <div class="product-item">
              ${thumb ? `<img class="product-thumb" src="${esc(thumb)}" alt="${esc(p.name)}" />` : `<div class="product-thumb">Sem foto</div>`}
              <div style="min-width:0;">
                <div class="product-title">${esc(p.name)}</div>
                <div class="product-meta">
                  ${esc(p.categoryName || "Geral")} &bull; <strong>${formatMoney(p.basePrice)}</strong> &bull; ${stock} un. em estoque
                </div>
                <div class="variant-chips">
                  ${(p.variants || []).slice(0, 10).map((v) => `
                    <span class="vchip">
                      <span class="vdot" style="background:${esc(v.colorHex || "#18181b")}"></span>
                      ${esc(v.color || "Padrão")} / ${esc(v.size || "Único")} &middot; ${Number(v.stockQuantity) || 0} un.
                    </span>
                  `).join("")}
                </div>
                <div class="product-actions">
                  <button type="button" class="adm-btn adm-btn-outline adm-btn-sm" data-edit-prod="${idx}">✏️ Editar</button>
                  <button type="button" class="adm-btn adm-btn-danger adm-btn-sm" data-del-prod="${idx}">🗑️ Excluir</button>
                </div>
              </div>
            </div>
          `;
        }).join("")}
      </div>
    `;
  }

  function renderProductForm(catNames) {
    const d = state.productDraft;
    const isEdit = state.editingProductIndex !== null;
    return `
      <div class="adm-card" style="border-left:4px solid var(--adm-success);">
        <h3 style="margin:0 0 0.9rem;font-size:1.12rem;">${isEdit ? "✏️ Editar Produto" : "➕ Novo Produto"}</h3>
        <div class="form-grid">
          <div class="field">
            <label>Nome do Produto *</label>
            <input class="input" data-draft="name" value="${esc(d.name)}" placeholder="Ex.: Camisa de Linho" />
          </div>
          <div class="field">
            <label>Categoria *</label>
            <input class="input" list="yl-cat-datalist" data-draft="categoryName" value="${esc(d.categoryName)}" placeholder="Ex.: Camisas, Calças..." />
            <datalist id="yl-cat-datalist">
              ${catNames.map((c) => `<option value="${esc(c)}"></option>`).join("")}
            </datalist>
          </div>
          <div class="field">
            <label>Preço de Venda (R$) *</label>
            <input class="input" data-draft="basePrice" value="${esc(d.basePrice)}" inputmode="decimal" placeholder="149,90" />
          </div>
        </div>

        <div class="variant-box">
          <div style="display:flex;align-items:center;justify-content:space-between;gap:0.5rem;flex-wrap:wrap;margin-bottom:0.75rem;">
            <div>
              <strong style="font-size:0.94rem;">🎨 Modelos / Cores, Tamanhos e Estoque</strong>
              <div style="font-size:0.8rem;color:var(--adm-muted);">Adicione quantas combinações de cor e tamanho quiser.</div>
            </div>
            <button type="button" class="adm-btn adm-btn-outline adm-btn-sm" id="add-variant-btn">+ Adicionar Cor / Tamanho</button>
          </div>

          ${(d.variants || []).map((v, i) => `
            <div class="variant-row">
              <div class="field" style="margin:0;">
                <label>Cor / Modelo</label>
                <div class="color-pair">
                  <input class="input" data-vidx="${i}" data-vfield="color" value="${esc(v.color)}" placeholder="Ex.: Preta" />
                  <input type="color" class="color-swatch" data-vidx="${i}" data-vfield="colorHex" value="${esc(v.colorHex || "#18181b")}" />
                </div>
              </div>
              <div class="field" style="margin:0;">
                <label>Tamanho / Opção</label>
                <input class="input" data-vidx="${i}" data-vfield="size" value="${esc(v.size)}" placeholder="Ex.: P, M, G" />
              </div>
              <div class="field" style="margin:0;">
                <label>Qtd. (un.)</label>
                <input type="number" min="0" class="input" data-vidx="${i}" data-vfield="stockQuantity" value="${esc(v.stockQuantity)}" />
              </div>
              ${d.variants.length > 1 ? `
                <button type="button" class="adm-btn adm-btn-danger adm-btn-sm" data-del-variant="${i}">Remover</button>
              ` : ""}
            </div>
          `).join("")}
        </div>

        <div class="field">
          <label>📸 Fotos do Produto</label>
          <input type="file" id="product-photo-input" accept="image/*" class="input" />
          ${(d.images || []).length > 0 ? `
            <div class="photo-strip">
              ${d.images.map((img, i) => `
                <div class="photo-tile">
                  <img src="${esc(img.publicUrl)}" alt="Foto" />
                  <button type="button" class="photo-del" data-del-photo="${i}">×</button>
                </div>
              `).join("")}
            </div>
          ` : ""}
        </div>

        <div class="field">
          <label>Descrição</label>
          <textarea data-draft="description" placeholder="Tecido, medidas, detalhes...">${esc(d.description)}</textarea>
        </div>

        <label class="toggle-card ${d.isVisibleOnSite !== false ? "active" : ""}">
          <input type="checkbox" id="draft-visible-chk" ${d.isVisibleOnSite !== false ? "checked" : ""} />
          <span><strong>Exibir imediatamente no site público</strong></span>
        </label>

        <div style="display:flex;gap:0.6rem;flex-wrap:wrap;">
          <button type="button" class="adm-btn adm-btn-success" id="save-product-btn" ${state.busy ? "disabled" : ""}>
            ${state.busy ? "Salvando..." : "💾 Salvar Produto"}
          </button>
          <button type="button" class="adm-btn adm-btn-outline" id="cancel-product-btn">Cancelar</button>
        </div>
      </div>
    `;
  }

  function renderStockTab() {
    return `
      <div class="yl-home-header">
        <div>
          <h1 class="yl-home-title">Controle de Estoque</h1>
          <div class="yl-home-status-line">Ajuste as unidades por cor e tamanho em 1 toque</div>
        </div>
        <button type="button" class="adm-btn adm-btn-primary" id="stock-save-all-btn" ${state.busy ? "disabled" : ""}>
          ${state.busy ? "Publicando..." : "☁️ Salvar no Site"}
        </button>
      </div>

      ${state.products.length === 0 ? `
        <div class="adm-card" style="text-align:center;padding:2rem;">
          Cadastre produtos primeiro para controlar o estoque.
        </div>
      ` : state.products.map((p, pIdx) => `
        <div class="adm-card">
          <div style="font-weight:800;font-size:1.02rem;margin-bottom:0.55rem;">${esc(p.name)} <span style="font-weight:600;color:var(--adm-muted);font-size:0.86rem;">(${totalStock(p)} un.)</span></div>
          ${(p.variants || []).map((v, vIdx) => `
            <div class="stock-item-row">
              <div style="display:flex;align-items:center;gap:0.5rem;">
                <span class="vdot" style="width:12px;height:12px;background:${esc(v.colorHex || "#18181b")}"></span>
                <strong>${esc(v.color || "Padrão")}</strong>
                <span style="color:var(--adm-muted);">/ ${esc(v.size || "Único")}</span>
              </div>
              <div class="stock-stepper">
                <button type="button" data-stock-delta="-1" data-pidx="${pIdx}" data-vidx="${vIdx}">−</button>
                <span>${Number(v.stockQuantity) || 0}</span>
                <button type="button" data-stock-delta="1" data-pidx="${pIdx}" data-vidx="${vIdx}">+</button>
              </div>
            </div>
          `).join("")}
        </div>
      `).join("")}
    `;
  }

  function renderOrdersTab() {
    return `
      <div class="yl-home-header">
        <div>
          <h1 class="yl-home-title">Pedidos</h1>
          <div class="yl-home-status-line">${state.orders.length} pedido(s) sincronizado(s) da loja online</div>
        </div>
        <button type="button" class="adm-btn adm-btn-outline adm-btn-sm" id="refresh-orders-btn" ${state.busy ? "disabled" : ""}>🔄 Atualizar Pedidos</button>
      </div>

      ${state.orders.length === 0 ? `
        <div class="adm-card" style="text-align:center;padding:2.2rem 1.2rem;">
          <div style="font-size:2.2rem;margin-bottom:0.5rem;">🧾</div>
          <h3 style="margin:0 0 0.4rem;">Nenhum pedido online registrado ainda</h3>
          <p style="color:var(--adm-muted);font-size:0.9rem;max-width:460px;margin:0 auto 1.2rem;">
            Quando seus clientes comprarem pelo Pix da vitrine, os pedidos aparecerão aqui automaticamente (e pedidos pelo WhatsApp chegam direto no seu número).
          </p>
          <div style="display:flex;gap:0.6rem;justify-content:center;flex-wrap:wrap;">
            <button type="button" class="adm-btn adm-btn-primary" data-tab="stock">📦 Controlar Estoque</button>
            <button type="button" class="adm-btn adm-btn-outline" data-tab="settings">⚙️ Configurar WhatsApp / Pix</button>
          </div>
        </div>
      ` : state.orders.map((o) => {
        const waDigits = (o.customerWhatsapp || "").replace(/\D/g, "");
        return `
          <div class="order-card">
            <div style="display:flex;justify-content:space-between;align-items:center;gap:0.5rem;flex-wrap:wrap;margin-bottom:0.5rem;">
              <strong>Pedido #${esc(o.orderNumber || o.id?.slice(0, 8))}</strong>
              <span class="pill-badge ${(o.status || "").toLowerCase().includes("pago") || (o.status || "").toLowerCase().includes("confirm") ? "pill-green" : "pill-blue"}">${esc(o.status || "Recebido")}</span>
            </div>
            <div style="font-size:0.9rem;margin-bottom:0.35rem;">
              <strong>Cliente:</strong> ${esc(o.customerName || "Cliente")} &bull; <strong>Total:</strong> ${formatMoney(o.totalPixAmount || o.itemsTotal)}
            </div>
            ${o.deliveryCity ? `<div style="font-size:0.84rem;color:var(--adm-muted);margin-bottom:0.55rem;">📍 ${esc(o.deliveryStreet || "")}, ${esc(o.deliveryNumber || "")} — ${esc(o.deliveryCity)}/${esc(o.deliveryState || "")}</div>` : ""}
            ${waDigits ? `<a class="adm-btn adm-btn-success adm-btn-sm" target="_blank" rel="noopener" href="https://wa.me/${waDigits}">💬 Chamar Cliente no WhatsApp</a>` : ""}
          </div>
        `;
      }).join("")}
    `;
  }

  function renderDesignTab() {
    const currentKey = state.store.designPreset || "minimalista-luxo";
    const plan = getPlanMeta();

    return `
      <div class="yl-home-header">
        <div>
          <h1 class="yl-home-title">Aparência da Loja</h1>
          <div class="yl-home-status-line">10 temas oficiais &bull; Seu plano atual: <strong>${plan.name}</strong></div>
        </div>
        <button type="button" class="adm-btn adm-btn-success" id="save-design-btn" ${state.busy ? "disabled" : ""}>
          ${state.busy ? "Salvando..." : "💾 Aplicar no Site"}
        </button>
      </div>

      <div class="adm-card">
        <div class="theme-cards-grid">
          ${STORE_THEMES.map((t) => {
            const allowed = isThemeAllowed(t);
            const selected = currentKey === t.key;
            const tierBadge = t.tier === "Premium" ? "✦ Premium" : t.tier === "Intermediario" ? "◈ Meia Versão" : "● Básico";
            return `
              <button type="button" class="theme-card ${selected ? "selected" : ""} ${!allowed ? "locked" : ""}" data-select-theme="${t.key}">
                <div class="theme-preview-bar">
                  <div style="flex:2;background:${t.design.backgroundColor};"></div>
                  <div style="flex:1;background:${t.design.primaryColor};"></div>
                  <div style="flex:1;background:${t.design.accentColor};"></div>
                </div>
                <div style="display:flex;justify-content:space-between;align-items:center;gap:0.4rem;">
                  <strong style="font-size:0.94rem;">${t.name}${selected ? " ✓" : ""}</strong>
                  <span style="font-size:0.7rem;color:var(--adm-muted);">${allowed ? tierBadge : "🔒 " + tierBadge}</span>
                </div>
                <div style="font-size:0.78rem;color:var(--adm-muted);">${t.desc}</div>
              </button>
            `;
          }).join("")}
        </div>
      </div>
    `;
  }

  function renderPlansTab() {
    const plan = getPlanMeta();
    const c = state.planCatalog;
    const waSupport = (c.supportWhatsappNumber || "").replace(/\D/g, "");

    const plans = [
      {
        key: "Basico",
        badge: "● Essencial",
        title: "Plano Básico",
        price: c.basicPlanMonthlyPrice,
        features: [
          "Vitrine online no ar (/loja/sua-loja)",
          "Produtos ilimitados com cores, tamanhos e estoque",
          "Pedidos direto no seu WhatsApp",
          "Tema Minimalista Luxo incluso"
        ]
      },
      {
        key: "Intermediario",
        badge: "◈ Mais Escolhido",
        title: "Meia Versão (Intermediário)",
        price: c.intermediaryPlanMonthlyPrice,
        features: [
          "Tudo do Plano Básico",
          "3 Temas prontos (Minimalista, Editorial Vogue, Urban)",
          "Vendas por Pix Direto e Mercado Pago no site",
          "Relatório de vendas e baixa automática"
        ]
      },
      {
        key: "Premium",
        badge: "✦ Completo",
        title: "Plano Premium",
        price: c.premiumPlanMonthlyPrice,
        highlight: true,
        features: [
          "Todos os 10 Temas exclusivos liberados",
          "Personalização completa de cores e fontes",
          "Pix Direto, Mercado Pago e cálculo de frete",
          "Prioridade total e recursos avançados VIP"
        ]
      }
    ];

    return `
      <div class="yl-home-header">
        <div>
          <h1 class="yl-home-title">Planos &amp; VIP</h1>
          <div class="yl-home-status-line">
            Plano ativo: <strong>${plan.name}</strong>
            ${state.isCourtesyVip ? " &bull; 🎁 VIP Cortesia" : ""}
            ${state.planExpiresAt ? ` &bull; Válido até ${formatDateBr(state.planExpiresAt)}` : ""}
          </div>
        </div>
        ${waSupport ? `
          <a class="adm-btn adm-btn-success adm-btn-sm" target="_blank" rel="noopener" href="https://wa.me/${waSupport}?text=${encodeURIComponent("Olá! Preciso de ajuda com os planos da YouLink.")}">
            💬 Falar com Suporte
          </a>
        ` : ""}
      </div>

      ${state.activePlanOrder ? renderPlanCheckoutCard() : ""}

      <div class="plans-grid">
        ${plans.map((p) => {
          const isCurrent = plan.key === p.key;
          return `
            <div class="plan-card ${isCurrent ? "active-plan" : p.highlight ? "highlight-plan" : ""}">
              <div style="display:flex;justify-content:space-between;align-items:center;gap:0.5rem;">
                <span class="pill-badge ${p.key === "Premium" ? "pill-gold" : p.key === "Intermediario" ? "pill-green" : "pill-slate"}">${p.badge}</span>
                ${isCurrent ? `<span class="pill-badge pill-green">✓ Ativo</span>` : ""}
              </div>
              <h3 style="margin:0.2rem 0 0;font-size:1.15rem;">${p.title}</h3>
              <div class="plan-price">${formatMoney(p.price)} <small>/mês</small></div>
              <ul class="plan-features">
                ${p.features.map((f) => `<li>✓ ${f}</li>`).join("")}
              </ul>
              <button type="button" class="adm-btn ${isCurrent ? "adm-btn-outline" : "adm-btn-primary"} adm-btn-block" style="margin-top:auto;" data-buy-plan="${p.key}" data-plan-price="${p.price}" ${state.busy ? "disabled" : ""}>
                ${isCurrent ? "🔄 Renovar +30 dias" : "⚡ Assinar " + p.title}
              </button>
            </div>
          `;
        }).join("")}
      </div>

      <div class="adm-card">
        <h2 class="section-title">🎁 Resgatar Código Gift Card VIP</h2>
        <p class="section-sub">Recebeu um código promocional ou cortesia? Digite abaixo para ativar na hora.</p>
        <div style="display:flex;gap:0.6rem;flex-wrap:wrap;">
          <input class="input" id="gift-card-input" style="flex:1 1 220px;text-transform:uppercase;" placeholder="Ex.: YOULINK-VIP-XXXX" value="${esc(state.giftCardInput)}" />
          <button type="button" class="adm-btn adm-btn-success" id="redeem-gift-btn" ${state.busy ? "disabled" : ""}>
            ${state.busy ? "Validando..." : "🎁 Ativar Código"}
          </button>
        </div>
      </div>
    `;
  }

  function renderPlanCheckoutCard() {
    const o = state.activePlanOrder;
    return `
      <div class="adm-card" style="border:2px solid var(--adm-gold);">
        <div style="display:flex;justify-content:space-between;align-items:center;gap:0.5rem;flex-wrap:wrap;margin-bottom:0.6rem;">
          <h3 style="margin:0;font-size:1.1rem;">💳 Pagamento do Plano ${esc(o.planTier)} — ${formatMoney(o.amount)}</h3>
          <button type="button" class="adm-btn adm-btn-outline adm-btn-sm" id="close-plan-order-btn">Fechar</button>
        </div>
        <div style="font-size:0.86rem;color:var(--adm-muted);margin-bottom:0.8rem;">
          Pedido <strong>#${esc(o.orderNumber)}</strong> &bull; Status: <strong>${esc(o.status)}</strong>
        </div>
        ${o.mercadoPagoQrCodeBase64 ? `
          <div style="text-align:center;margin-bottom:0.85rem;">
            <img src="data:image/png;base64,${esc(o.mercadoPagoQrCodeBase64)}" alt="QR Code Pix" style="width:190px;height:190px;border-radius:12px;background:#fff;padding:8px;" />
          </div>
        ` : ""}
        ${o.pixBrCode ? `
          <div class="field">
            <label>Pix Copia e Cola</label>
            <textarea readonly rows="3" style="font-size:0.82rem;">${esc(o.pixBrCode)}</textarea>
          </div>
        ` : ""}
        <div style="display:flex;gap:0.6rem;flex-wrap:wrap;">
          ${o.pixBrCode ? `<button type="button" class="adm-btn adm-btn-primary" id="copy-plan-pix-btn">${state.copiedPix ? "✓ Pix Copiado!" : "📋 Copiar Código Pix"}</button>` : ""}
          ${o.mercadoPagoCheckoutUrl ? `<a class="adm-btn adm-btn-primary" target="_blank" rel="noopener" href="${esc(o.mercadoPagoCheckoutUrl)}">💳 Pagar no Mercado Pago</a>` : ""}
          <button type="button" class="adm-btn adm-btn-success" id="check-plan-order-btn" ${state.busy ? "disabled" : ""}>🔄 Já Paguei — Verificar</button>
        </div>
      </div>
    `;
  }

  function renderSettingsTab() {
    const s = state.store;
    const waDigits = (s.whatsappNumber || "").replace(/\D/g, "");
    const waSupport = (state.planCatalog.supportWhatsappNumber || "").replace(/\D/g, "");

    return `
      <div class="yl-home-header">
        <div>
          <h1 class="yl-home-title">Configurações da Loja</h1>
          <div class="yl-home-status-line">Identidade, logotipo, WhatsApp, Pix, Mercado Pago e Frete</div>
        </div>
        <button type="button" class="adm-btn adm-btn-success" id="save-settings-btn" ${state.busy ? "disabled" : ""}>
          ${state.busy ? "Salvando..." : "💾 Salvar e Publicar"}
        </button>
      </div>

      <!-- SEÇÃO 1: DADOS & IDENTIDADE -->
      <div class="adm-card">
        <h2 class="section-title">🏪 Dados &amp; Identidade da Loja</h2>
        <p class="section-sub">Informações principais exibidas no cabeçalho e no link da sua vitrine.</p>

        <div class="form-grid">
          <div class="field">
            <label>Nome da Loja *</label>
            <input class="input" data-store="storeName" value="${esc(s.storeName)}" placeholder="Ex.: Minha Loja" />
          </div>
          <div class="field">
            <label>Endereço do Link (/loja/...) *</label>
            <input class="input" data-store="storeSlug" value="${esc(s.storeSlug)}" placeholder="minha-loja" />
          </div>
          <div class="field">
            <label>Número do WhatsApp (DDD + Celular) *</label>
            <input class="input" data-store="whatsappNumber" value="${esc(s.whatsappNumber)}" placeholder="5511999999999" inputmode="tel" />
            ${waDigits ? `<span style="font-size:0.78rem;color:var(--adm-success);">Link ativo: wa.me/${esc(waDigits)}</span>` : ""}
          </div>
          <div class="field">
            <label>Alerta de Estoque Baixo (unidades)</label>
            <input type="number" min="0" class="input" data-store="defaultLowStockThreshold" value="${esc(s.defaultLowStockThreshold ?? 3)}" />
          </div>
        </div>

        <div class="field">
          <label>Descrição e Bio da Loja *</label>
          <textarea data-store="storeDescription" placeholder="Apresentação exibida para os clientes no catálogo online...">${esc(s.storeDescription || "")}</textarea>
        </div>

        <div class="field">
          <label>Logotipo da Loja (Opcional)</label>
          <input type="file" id="store-logo-input" accept="image/*" class="input" />
          ${s.logoPublicUrl ? `
            <div style="display:flex;align-items:center;gap:0.75rem;margin-top:0.5rem;">
              <img src="${esc(s.logoPublicUrl)}" alt="Logo" style="width:64px;height:64px;border-radius:12px;object-fit:cover;border:1px solid var(--adm-border);" />
              <button type="button" class="adm-btn adm-btn-danger adm-btn-sm" id="remove-logo-btn">Remover Logo</button>
            </div>
          ` : ""}
        </div>

        <label class="toggle-card ${s.isSiteActive !== false ? "active" : ""}">
          <input type="checkbox" id="store-active-chk" ${s.isSiteActive !== false ? "checked" : ""} />
          <span><strong>Loja aberta e visível para clientes</strong><br/><span style="font-size:0.82rem;color:var(--adm-muted);">Desmarque para pausar temporariamente a vitrine online.</span></span>
        </label>

        <label class="toggle-card ${s.autoSyncOnlineOnSave !== false ? "active" : ""}">
          <input type="checkbox" id="store-autosync-chk" ${s.autoSyncOnlineOnSave !== false ? "checked" : ""} />
          <span><strong>Sincronizar automaticamente ao salvar</strong><br/><span style="font-size:0.82rem;color:var(--adm-muted);">Atualiza o site público imediatamente sempre que você salvar alterações.</span></span>
        </label>
      </div>

      <!-- SEÇÃO 2: VENDAS, PIX & MERCADO PAGO -->
      <div class="adm-card">
        <h2 class="section-title">💳 Vendas, Pix &amp; Mercado Pago</h2>
        <p class="section-sub">Configure como seus clientes pagam no catálogo online e no balcão.</p>

        <label class="toggle-card ${s.manualSalesEnabled !== false ? "active" : ""}">
          <input type="checkbox" id="store-manual-sales-chk" ${s.manualSalesEnabled !== false ? "checked" : ""} />
          <span><strong>Vendas Manuais / WhatsApp / Balcão</strong><br/><span style="font-size:0.82rem;color:var(--adm-muted);">Permite receber pedidos pelo WhatsApp e registrar vendas em dinheiro/cartão.</span></span>
        </label>

        <label class="toggle-card ${s.directPixSalesEnabled ? "active" : ""}">
          <input type="checkbox" id="store-pix-enabled-chk" ${s.directPixSalesEnabled ? "checked" : ""} />
          <span><strong>Ativar Pagamento por Pix no Site</strong><br/><span style="font-size:0.82rem;color:var(--adm-muted);">Exibe checkout Pix direto no catálogo público (Intermediário e Premium).</span></span>
        </label>

        <div class="form-grid" style="margin-top:0.5rem;">
          <div class="field">
            <label>Modo de Recebimento Pix</label>
            <select data-store="pixGatewayMode">
              <option value="DirectPix" ${(s.pixGatewayMode || "DirectPix") === "DirectPix" ? "selected" : ""}>Pix Direto (Sua Chave Pix)</option>
              <option value="MercadoPago" ${s.pixGatewayMode === "MercadoPago" ? "selected" : ""}>Mercado Pago (Confirmação Automática)</option>
            </select>
          </div>
          <div class="field">
            <label>Chave Pix da Loja</label>
            <input class="input" data-store="storePixKey" value="${esc(s.storePixKey || "")}" placeholder="CPF, CNPJ, e-mail, celular ou chave aleatória" />
          </div>
          <div class="field">
            <label>Nome do Recebedor no Pix</label>
            <input class="input" data-store="storePixReceiverName" value="${esc(s.storePixReceiverName || "")}" placeholder="Nome que aparece para o cliente no banco" />
          </div>
        </div>

        <div style="display:flex;align-items:center;justify-content:space-between;gap:0.75rem;flex-wrap:wrap;padding:0.85rem;border-radius:12px;background:var(--adm-input-bg);border:1px solid var(--adm-border);margin-top:0.4rem;">
          <div>
            <strong style="font-size:0.9rem;">Mercado Pago da Loja</strong>
            <div style="font-size:0.82rem;color:var(--adm-muted);">
              ${state.mpStatus?.isConnected ? `✅ Conectado (${esc(state.mpStatus.accountLabel || "Conta ativa")})` : "Não conectado — conecte em 1 clique para confirmar Pix automaticamente."}
            </div>
          </div>
          <button type="button" class="adm-btn adm-btn-primary adm-btn-sm" id="connect-mp-btn" ${state.busy ? "disabled" : ""}>
            ${state.mpStatus?.isConnected ? "🔄 Reconectar Mercado Pago" : "⚡ Conectar Mercado Pago"}
          </button>
        </div>
      </div>

      <!-- SEÇÃO 3: ENTREGA & FRETE (CORREIOS) -->
      <div class="adm-card">
        <h2 class="section-title">🚚 Entrega &amp; Cálculo de Frete</h2>
        <p class="section-sub">Usado para calcular prazo e frete a partir da sua loja.</p>

        <div class="form-grid">
          <div class="field">
            <label>CEP de Origem *</label>
            <div style="display:flex;gap:0.45rem;">
              <input class="input" data-store="originCep" value="${esc(s.originCep || "")}" placeholder="00000-000" inputmode="numeric" />
              <button type="button" class="adm-btn adm-btn-outline adm-btn-sm" id="lookup-cep-btn">🔍 CEP</button>
            </div>
          </div>
          <div class="field">
            <label>Sua Cidade</label>
            <input class="input" data-store="originCity" value="${esc(s.originCity || "")}" placeholder="Ex.: São Paulo" />
          </div>
          <div class="field">
            <label>Seu Estado (UF)</label>
            <input class="input" data-store="originState" maxlength="2" value="${esc(s.originState || "")}" placeholder="SP" style="text-transform:uppercase;" />
          </div>
          <div class="field">
            <label>Área de Entrega</label>
            <select data-store="deliveryScopeMode">
              <option value="TodoBrasil" ${(s.deliveryScopeMode || "TodoBrasil") === "TodoBrasil" ? "selected" : ""}>Todo o Brasil (Correios)</option>
              <option value="RaioKm" ${s.deliveryScopeMode === "RaioKm" ? "selected" : ""}>Raio Máximo em KM</option>
              <option value="MesmaCidade" ${s.deliveryScopeMode === "MesmaCidade" ? "selected" : ""}>Apenas Mesma Cidade</option>
            </select>
          </div>
          <div class="field">
            <label>Distância Máxima em KM (0 = sem limite)</label>
            <input type="number" min="0" class="input" data-store="maxDeliveryDistanceKm" value="${esc(s.maxDeliveryDistanceKm ?? 0)}" />
          </div>
          <div class="field">
            <label>Peso Médio por Produto (gramas)</label>
            <input type="number" min="50" class="input" data-store="defaultPackageWeightGrams" value="${esc(s.defaultPackageWeightGrams ?? 450)}" />
          </div>
        </div>

        <button type="button" class="adm-btn adm-btn-success adm-btn-block" id="save-settings-btn-bottom" ${state.busy ? "disabled" : ""}>
          ${state.busy ? "Salvando..." : "💾 Salvar Todas as Configurações e Publicar"}
        </button>
      </div>

      ${waSupport ? `
        <div class="adm-card" style="display:flex;align-items:center;justify-content:space-between;gap:0.75rem;flex-wrap:wrap;">
          <div>
            <strong style="font-size:0.95rem;">💬 Suporte Oficial YouLink</strong>
            <div style="font-size:0.84rem;color:var(--adm-muted);">Precisa de ajuda com sua loja ou plano?</div>
          </div>
          <a class="adm-btn adm-btn-success adm-btn-sm" target="_blank" rel="noopener" href="https://wa.me/${waSupport}?text=${encodeURIComponent("Olá! Preciso de suporte na minha loja YouLink.")}">Chamar no WhatsApp</a>
        </div>
      ` : ""}
    `;
  }

  function renderMoreSheet() {
    return `
      <div class="modal-backdrop" id="more-backdrop">
        <div class="modal-sheet">
          <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:0.9rem;">
            <strong style="font-size:1.1rem;">Menu da Loja</strong>
            <button type="button" class="adm-btn adm-btn-outline adm-btn-sm" id="close-more-btn">Fechar</button>
          </div>
          <button type="button" class="sheet-item" data-sheet-tab="stock"><span>📦 Controle de Estoque</span><span>&rarr;</span></button>
          <button type="button" class="sheet-item" data-sheet-tab="design"><span>🎨 Aparência da Loja (10 Temas)</span><span>&rarr;</span></button>
          <button type="button" class="sheet-item" data-sheet-tab="plans"><span>⭐ Planos, Assinatura &amp; Gift Card</span><span>&rarr;</span></button>
          <button type="button" class="sheet-item" data-sheet-tab="settings"><span>⚙️ Configurações Completas</span><span>&rarr;</span></button>
          <button type="button" class="sheet-item" id="sheet-theme-btn"><span>${state.theme === "dark" ? "☀️ Mudar para Tema Claro" : "🌙 Mudar para Tema Escuro"}</span><span></span></button>
          <button type="button" class="sheet-item" id="sheet-logout-btn" style="color:var(--adm-danger);"><span>🚪 Sair da Conta</span><span></span></button>
        </div>
      </div>
    `;
  }

  function renderProfileModal() {
    return `
      <div class="modal-backdrop" id="profile-backdrop">
        <div class="modal-sheet">
          <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:0.9rem;">
            <strong style="font-size:1.1rem;">Minha Conta</strong>
            <button type="button" class="adm-btn adm-btn-outline adm-btn-sm" id="close-profile-btn">Fechar</button>
          </div>
          <div style="margin-bottom:1.1rem;">
            <div style="font-weight:800;font-size:1.05rem;">${esc(state.session?.fullName || state.session?.username)}</div>
            <div style="font-size:0.88rem;color:var(--adm-muted);">${esc(state.session?.email || "")}</div>
          </div>
          <button type="button" class="adm-btn adm-btn-danger adm-btn-block" id="profile-logout-btn">🚪 Sair da Conta</button>
        </div>
      </div>
    `;
  }

  /* ================= EVENTOS ================= */

  function bindAuthEvents() {
    const tabLogin = document.getElementById("tab-auth-login");
    const tabReg = document.getElementById("tab-auth-register");
    if (tabLogin) tabLogin.onclick = () => { state.authTab = "login"; state.msgErr = null; render(); };
    if (tabReg) tabReg.onclick = () => { state.authTab = "register"; state.msgErr = null; render(); };

    const loginForm = document.getElementById("login-form");
    if (loginForm) {
      loginForm.onsubmit = async (e) => {
        e.preventDefault();
        const fd = new FormData(loginForm);
        state.busy = true;
        state.msgErr = null;
        render();
        try {
          const res = await apiJson("/api/cloud/accounts/verify-login", {
            method: "POST",
            body: JSON.stringify({ email: String(fd.get("login") || "").trim(), password: fd.get("password") })
          });
          if (!res?.verified || !res.account) {
            throw new Error(res?.message || "Usuário ou senha inválidos.");
          }
          saveSession({ ...res.account, cloudSessionToken: res.cloudSessionToken || res.account.cloudSessionToken || null });
          state.activeTab = "home";
          await pullStoreFromCloud(false);
        } catch (err) {
          setStatus(null, err.message);
        } finally {
          state.busy = false;
          render();
        }
      };
    }

    const regForm = document.getElementById("register-form");
    if (regForm) {
      regForm.onsubmit = async (e) => {
        e.preventDefault();
        const fd = new FormData(regForm);
        state.regDraft = {
          fullName: String(fd.get("fullName") || ""),
          username: String(fd.get("username") || ""),
          email: String(fd.get("email") || ""),
          password: String(fd.get("password") || ""),
          emailOtpCode: String(fd.get("emailOtpCode") || "").trim()
        };
        state.busy = true;
        state.msgErr = null;
        render();
        try {
          const res = await apiJson("/api/cloud/accounts/register", {
            method: "POST",
            body: JSON.stringify({
              fullName: state.regDraft.fullName,
              username: state.regDraft.username,
              email: state.regDraft.email,
              password: state.regDraft.password,
              emailOtpCode: state.regDraft.emailOtpCode || null
            })
          });
          if (!res?.verified || !res.account) {
            throw new Error(res?.message || "Não foi possível criar a conta.");
          }
          state.requiresRegisterEmailOtp = false;
          state.regDraft = { fullName: "", username: "", email: "", password: "", emailOtpCode: "" };
          saveSession({ ...res.account, cloudSessionToken: res.cloudSessionToken || res.account.cloudSessionToken || null });
          state.storeLoaded = true;
          state.products = [];
          state.categories = [];
          state.store = { ...createDefaultStore(), storeName: res.account.storeName || "Minha Loja" };
          state.activeTab = "settings";
          setStatus("🎉 Conta criada! Preencha os dados da sua loja abaixo e salve.", null);
        } catch (err) {
          if (err.requiresEmailOtp) {
            state.requiresRegisterEmailOtp = true;
          }
          setStatus(null, err.status === 404 ? "Atualize o servidor do Render para liberar cadastro web, ou entre com uma conta já criada." : err.message);
        } finally {
          state.busy = false;
          render();
        }
      };
    }
  }

  function openNewProduct() {
    state.activeTab = "products";
    state.editingProductIndex = null;
    state.productDraft = createEmptyProductDraft();
    state.showProductForm = true;
    state.showMoreSheet = false;
    state.msgOk = null;
    state.msgErr = null;
    render();
  }

  function logout() {
    saveSession(null);
    state.storeLoaded = false;
    state.products = [];
    state.categories = [];
    state.orders = [];
    state.activePlanOrder = null;
    state.store = createDefaultStore();
    state.publishToken = null;
    state.activeTab = "home";
    state.showProductForm = false;
    state.showMoreSheet = false;
    state.showProfileModal = false;
    state.msgOk = null;
    state.msgErr = null;
    render();
  }

  function bindShellEvents() {
    document.querySelectorAll("[data-tab]").forEach((el) => {
      el.onclick = () => {
        const nextTab = el.getAttribute("data-tab");
        state.activeTab = nextTab;
        state.showMoreSheet = false;
        state.msgOk = null;
        state.msgErr = null;
        render();
        if (nextTab === "orders") loadCloudOrders(true);
        if (nextTab === "settings") loadMercadoPagoStatus();
      };
    });

    document.querySelectorAll("[data-sheet-tab]").forEach((el) => {
      el.onclick = () => {
        const nextTab = el.getAttribute("data-sheet-tab");
        state.activeTab = nextTab;
        state.showMoreSheet = false;
        render();
        if (nextTab === "settings") loadMercadoPagoStatus();
      };
    });

    const toggleTheme = () => { applyTheme(state.theme === "dark" ? "light" : "dark"); render(); };
    const sbTheme = document.getElementById("sidebar-theme-btn");
    const tbTheme = document.getElementById("topbar-theme-btn");
    const shTheme = document.getElementById("sheet-theme-btn");
    if (sbTheme) sbTheme.onclick = toggleTheme;
    if (tbTheme) tbTheme.onclick = toggleTheme;
    if (shTheme) shTheme.onclick = () => { toggleTheme(); state.showMoreSheet = false; render(); };

    const sbLogout = document.getElementById("sidebar-logout-btn");
    const shLogout = document.getElementById("sheet-logout-btn");
    const prLogout = document.getElementById("profile-logout-btn");
    if (sbLogout) sbLogout.onclick = logout;
    if (shLogout) shLogout.onclick = logout;
    if (prLogout) prLogout.onclick = logout;

    const openMore = document.getElementById("open-more-btn");
    const closeMore = document.getElementById("close-more-btn");
    const moreBg = document.getElementById("more-backdrop");
    if (openMore) openMore.onclick = () => { state.showMoreSheet = true; render(); };
    if (closeMore) closeMore.onclick = () => { state.showMoreSheet = false; render(); };
    if (moreBg) moreBg.onclick = (e) => { if (e.target === moreBg) { state.showMoreSheet = false; render(); } };

    const openProfile = document.getElementById("topbar-profile-btn");
    const closeProfile = document.getElementById("close-profile-btn");
    const profileBg = document.getElementById("profile-backdrop");
    if (openProfile) openProfile.onclick = () => { state.showProfileModal = true; render(); };
    if (closeProfile) closeProfile.onclick = () => { state.showProfileModal = false; render(); };
    if (profileBg) profileBg.onclick = (e) => { if (e.target === profileBg) { state.showProfileModal = false; render(); } };

    const homeNewProd = document.getElementById("home-new-product-btn");
    const quickNewProd = document.getElementById("quick-new-prod");
    const openNewProd = document.getElementById("open-new-product-btn");
    const emptyNewProd = document.getElementById("empty-new-prod-btn");
    if (homeNewProd) homeNewProd.onclick = openNewProduct;
    if (quickNewProd) quickNewProd.onclick = openNewProduct;
    if (openNewProd) openNewProd.onclick = openNewProduct;
    if (emptyNewProd) emptyNewProd.onclick = openNewProduct;

    const copyBtn = document.getElementById("copy-link-btn");
    if (copyBtn) {
      copyBtn.onclick = async () => {
        try { await navigator.clipboard.writeText(getPublicStoreUrl()); } catch {}
        state.copiedLink = true;
        render();
        setTimeout(() => { state.copiedLink = false; render(); }, 2000);
      };
    }

    const shareWa = document.getElementById("share-wa-btn");
    if (shareWa) {
      shareWa.onclick = () => {
        const text = `Confira os produtos da ${state.store.storeName}: ${getPublicStoreUrl()}`;
        window.open(`https://wa.me/?text=${encodeURIComponent(text)}`, "_blank", "noopener");
      };
    }

    const pullBtn = document.getElementById("sync-pull-btn");
    const pushBtn = document.getElementById("sync-push-btn");
    if (pullBtn) pullBtn.onclick = () => pullStoreFromCloud(true);
    if (pushBtn) pushBtn.onclick = () => publishStoreToCloud("☁️ Loja publicada com sucesso!");

    const refreshOrdersBtn = document.getElementById("refresh-orders-btn");
    if (refreshOrdersBtn) refreshOrdersBtn.onclick = () => loadCloudOrders(false);

    const mergeBtn = document.getElementById("merge-dups-btn");
    if (mergeBtn) mergeBtn.onclick = mergeDuplicates;

    const searchInput = document.getElementById("search-products-input");
    if (searchInput) {
      searchInput.oninput = (e) => {
        state.search = e.target.value;
        render();
        const next = document.getElementById("search-products-input");
        if (next) { next.focus(); next.setSelectionRange(next.value.length, next.value.length); }
      };
    }

    const filterCat = document.getElementById("filter-category-select");
    if (filterCat) filterCat.onchange = (e) => { state.filterCategory = e.target.value; render(); };

    const newCatInp = document.getElementById("new-category-input");
    if (newCatInp) newCatInp.oninput = (e) => { state.newCategoryName = e.target.value; };
    const addCatBtn = document.getElementById("add-category-btn");
    if (addCatBtn) {
      addCatBtn.onclick = () => {
        const name = (state.newCategoryName || "").trim();
        if (!name) return;
        if (!state.categories.some((c) => slugify(c.name) === slugify(name))) {
          state.categories.push({ name, description: "", displayOrder: state.categories.length + 1, isActive: true });
        }
        state.newCategoryName = "";
        setStatus(`✓ Categoria "${name}" adicionada!`, null);
      };
    }

    document.querySelectorAll("[data-edit-prod]").forEach((el) => {
      el.onclick = () => {
        const idx = Number(el.getAttribute("data-edit-prod"));
        state.editingProductIndex = idx;
        state.productDraft = JSON.parse(JSON.stringify(state.products[idx]));
        if (!state.productDraft.variants || state.productDraft.variants.length === 0) {
          state.productDraft.variants = [{ color: "", colorHex: "#18181b", size: "", stockQuantity: 5, lowStockThreshold: 3, isActive: true }];
        }
        state.showProductForm = true;
        render();
      };
    });

    document.querySelectorAll("[data-del-prod]").forEach((el) => {
      el.onclick = async () => {
        const idx = Number(el.getAttribute("data-del-prod"));
        if (!confirm(`Excluir "${state.products[idx]?.name}"?`)) return;
        state.products.splice(idx, 1);
        await publishStoreToCloud("🗑️ Produto excluído e atualizado no site!");
      };
    });

    document.querySelectorAll("[data-draft]").forEach((el) => {
      el.oninput = (e) => { state.productDraft[el.getAttribute("data-draft")] = e.target.value; };
    });

    document.querySelectorAll("[data-vidx]").forEach((el) => {
      el.oninput = (e) => {
        const idx = Number(el.getAttribute("data-vidx"));
        const field = el.getAttribute("data-vfield");
        if (state.productDraft.variants[idx]) {
          state.productDraft.variants[idx][field] = field === "stockQuantity" ? Number(e.target.value || 0) : e.target.value;
        }
      };
    });

    const addVarBtn = document.getElementById("add-variant-btn");
    if (addVarBtn) {
      addVarBtn.onclick = () => {
        state.productDraft.variants.push({ color: "", colorHex: "#18181b", size: "", stockQuantity: "", lowStockThreshold: 3, isActive: true });
        render();
      };
    }

    document.querySelectorAll("[data-del-variant]").forEach((el) => {
      el.onclick = () => {
        state.productDraft.variants.splice(Number(el.getAttribute("data-del-variant")), 1);
        render();
      };
    });

    document.querySelectorAll("[data-del-photo]").forEach((el) => {
      el.onclick = () => {
        state.productDraft.images.splice(Number(el.getAttribute("data-del-photo")), 1);
        render();
      };
    });

    const photoInput = document.getElementById("product-photo-input");
    if (photoInput) {
      photoInput.onchange = async (e) => {
        const file = e.target.files?.[0];
        if (!file) return;
        try {
          const dataUrl = await compressImageToDataUrl(file);
          state.productDraft.images.push({ publicUrl: dataUrl, storagePath: dataUrl, altText: state.productDraft.name || "Produto", isPrimary: state.productDraft.images.length === 0 });
          render();
        } catch (err) { setStatus(null, err.message); }
      };
    }

    const visChk = document.getElementById("draft-visible-chk");
    if (visChk) visChk.onchange = (e) => { state.productDraft.isVisibleOnSite = e.target.checked; render(); };

    const cancelProd = document.getElementById("cancel-product-btn");
    if (cancelProd) cancelProd.onclick = () => { state.showProductForm = false; state.editingProductIndex = null; render(); };

    const saveProd = document.getElementById("save-product-btn");
    if (saveProd) {
      saveProd.onclick = async () => {
        if (state.busy) return;
        if (!(state.productDraft.name || "").trim()) {
          setStatus(null, "Informe o nome do produto.");
          return;
        }
        const item = JSON.parse(JSON.stringify(state.productDraft));
        item.basePrice = parsePrice(item.basePrice);
        if (state.editingProductIndex !== null) {
          state.products[state.editingProductIndex] = item;
        } else {
          const existingIdx = state.products.findIndex(
            (p) => slugify(p.name) === slugify(item.name) && slugify(p.categoryName || "Geral") === slugify(item.categoryName || "Geral")
          );
          if (existingIdx >= 0) {
            const target = state.products[existingIdx];
            for (const v of (item.variants || [])) {
              const ev = (target.variants || []).find((x) => slugify(x.color) === slugify(v.color) && slugify(x.size) === slugify(v.size));
              if (ev) ev.stockQuantity = (Number(ev.stockQuantity) || 0) + (Number(v.stockQuantity) || 0);
              else target.variants.push(v);
            }
            for (const img of (item.images || [])) {
              if (!(target.images || []).some((x) => x.publicUrl === img.publicUrl)) target.images.push(img);
            }
          } else {
            state.products.unshift(item);
          }
        }
        state.showProductForm = false;
        state.editingProductIndex = null;
        await publishStoreToCloud("✅ Produto salvo e publicado no site!");
      };
    }

    document.querySelectorAll("[data-stock-delta]").forEach((el) => {
      el.onclick = () => {
        const pIdx = Number(el.getAttribute("data-pidx"));
        const vIdx = Number(el.getAttribute("data-vidx"));
        const delta = Number(el.getAttribute("data-stock-delta"));
        const v = state.products[pIdx]?.variants?.[vIdx];
        if (v) { v.stockQuantity = Math.max(0, (Number(v.stockQuantity) || 0) + delta); render(); }
      };
    });

    const stockSaveBtn = document.getElementById("stock-save-all-btn");
    if (stockSaveBtn) stockSaveBtn.onclick = () => publishStoreToCloud("📦 Estoque atualizado no site!");

    // Aparência (10 temas reais)
    document.querySelectorAll("[data-select-theme]").forEach((el) => {
      el.onclick = () => {
        const key = el.getAttribute("data-select-theme");
        const found = STORE_THEMES.find((t) => t.key === key);
        if (!found) return;
        if (!isThemeAllowed(found)) {
          setStatus(null, `O tema "${found.name}" faz parte do plano ${found.tier}. Veja os planos na aba ⭐ Planos & VIP!`);
          return;
        }
        state.store.designPreset = key;
        state.msgErr = null;
        render();
      };
    });

    const saveDesignBtn = document.getElementById("save-design-btn");
    if (saveDesignBtn) saveDesignBtn.onclick = () => publishStoreToCloud("🎨 Aparência da loja aplicada no site!");

    // Planos & VIP + Gift Card
    document.querySelectorAll("[data-buy-plan]").forEach((el) => {
      el.onclick = async () => {
        const planTier = el.getAttribute("data-buy-plan");
        const amount = Number(el.getAttribute("data-plan-price") || 49.90);
        state.busy = true;
        state.msgErr = null;
        render();
        try {
          const order = await apiJson("/api/platform-sync/plan-orders", {
            method: "POST",
            body: JSON.stringify({
              planTier,
              durationMonths: 1,
              amount,
              storeName: state.store.storeName || "Minha Loja",
              storeSlug: slugify(state.store.storeSlug || state.store.storeName),
              buyerUsername: state.session?.username || null,
              buyerEmail: state.session?.email || null,
              buyerFullName: state.session?.fullName || null,
              paymentMethod: state.planCatalog.hasPlatformMercadoPagoConnected ? "MercadoPagoPix" : "DirectPix"
            })
          });
          state.activePlanOrder = order;
          setStatus("💳 Cobrança gerada! Pague o Pix abaixo e clique em \"Já Paguei — Verificar\".", null);
        } catch (err) {
          setStatus(null, err.message);
        } finally {
          state.busy = false;
          render();
        }
      };
    });

    const closeOrderBtn = document.getElementById("close-plan-order-btn");
    if (closeOrderBtn) closeOrderBtn.onclick = () => { state.activePlanOrder = null; render(); };

    const copyPlanPix = document.getElementById("copy-plan-pix-btn");
    if (copyPlanPix && state.activePlanOrder?.pixBrCode) {
      copyPlanPix.onclick = async () => {
        try { await navigator.clipboard.writeText(state.activePlanOrder.pixBrCode); } catch {}
        state.copiedPix = true;
        render();
        setTimeout(() => { state.copiedPix = false; render(); }, 2000);
      };
    }

    const checkOrderBtn = document.getElementById("check-plan-order-btn");
    if (checkOrderBtn && state.activePlanOrder) {
      checkOrderBtn.onclick = async () => {
        state.busy = true;
        render();
        try {
          const st = await apiJson(`/api/platform-sync/plan-orders/status?orderNumber=${encodeURIComponent(state.activePlanOrder.orderNumber)}&claimToken=${encodeURIComponent(state.activePlanOrder.claimToken)}`);
          if (st?.isApproved) {
            state.store.planTier = st.planTier || state.activePlanOrder.planTier;
            state.planExpiresAt = st.appliedPlanExpiresAt || null;
            state.activePlanOrder = null;
            saveSession({ ...state.session, planTier: state.store.planTier });
            setStatus(`🎉 Pagamento aprovado! Plano ${state.store.planTier} ativado!`, null);
          } else {
            state.activePlanOrder.status = st?.status || state.activePlanOrder.status;
            setStatus(null, `Status atual: ${st?.status || "Aguardando pagamento"}. Assim que o Pix compensar, toque novamente.`);
          }
        } catch (err) {
          setStatus(null, err.message);
        } finally {
          state.busy = false;
          render();
        }
      };
    }

    const giftInp = document.getElementById("gift-card-input");
    if (giftInp) giftInp.oninput = (e) => { state.giftCardInput = e.target.value.toUpperCase(); };
    const redeemBtn = document.getElementById("redeem-gift-btn");
    if (redeemBtn) {
      redeemBtn.onclick = async () => {
        const code = (state.giftCardInput || "").trim().toUpperCase();
        if (!code) { setStatus(null, "Digite o código do Gift Card."); return; }
        state.busy = true;
        render();
        try {
          const res = await apiJson("/api/platform-sync/gift-cards/redeem", {
            method: "POST",
            body: JSON.stringify({ code, storeName: state.store.storeName || "Minha Loja" })
          });
          state.store.planTier = res.targetPlanTier || "Premium";
          state.planExpiresAt = res.planExpiresAt || null;
          state.giftCardInput = "";
          saveSession({ ...state.session, planTier: state.store.planTier });
          await publishStoreToCloud(`🎁 ${res.message || "Gift Card ativado com sucesso!"}`);
        } catch (err) {
          setStatus(null, err.message);
        } finally {
          state.busy = false;
          render();
        }
      };
    }

    // Configurações completas
    document.querySelectorAll("[data-store]").forEach((el) => {
      const handler = (e) => { state.store[el.getAttribute("data-store")] = e.target.value; };
      el.oninput = handler;
      el.onchange = handler;
    });

    const logoInput = document.getElementById("store-logo-input");
    if (logoInput) {
      logoInput.onchange = async (e) => {
        const file = e.target.files?.[0];
        if (!file) return;
        try {
          state.store.logoPublicUrl = await compressImageToDataUrl(file, 420);
          render();
        } catch (err) { setStatus(null, err.message); }
      };
    }
    const rmLogoBtn = document.getElementById("remove-logo-btn");
    if (rmLogoBtn) rmLogoBtn.onclick = () => { state.store.logoPublicUrl = null; render(); };

    const activeChk = document.getElementById("store-active-chk");
    if (activeChk) activeChk.onchange = (e) => { state.store.isSiteActive = e.target.checked; render(); };
    const autoSyncChk = document.getElementById("store-autosync-chk");
    if (autoSyncChk) autoSyncChk.onchange = (e) => { state.store.autoSyncOnlineOnSave = e.target.checked; render(); };
    const manualSalesChk = document.getElementById("store-manual-sales-chk");
    if (manualSalesChk) manualSalesChk.onchange = (e) => { state.store.manualSalesEnabled = e.target.checked; render(); };
    const pixChk = document.getElementById("store-pix-enabled-chk");
    if (pixChk) pixChk.onchange = (e) => { state.store.directPixSalesEnabled = e.target.checked; render(); };

    const lookupCepBtn = document.getElementById("lookup-cep-btn");
    if (lookupCepBtn) {
      lookupCepBtn.onclick = async () => {
        const digits = (state.store.originCep || "").replace(/\D/g, "");
        if (digits.length !== 8) { setStatus(null, "Digite um CEP válido com 8 números."); return; }
        state.busy = true;
        render();
        try {
          const cepData = await apiJson(`/api/public/cep/${digits}`);
          if (cepData?.city || cepData?.localidade) state.store.originCity = cepData.city || cepData.localidade;
          if (cepData?.state || cepData?.uf) state.store.originState = cepData.state || cepData.uf;
          setStatus(`📍 CEP encontrado: ${state.store.originCity}/${state.store.originState}`, null);
        } catch (err) {
          setStatus(null, `CEP não encontrado: ${err.message}`);
        } finally {
          state.busy = false;
          render();
        }
      };
    }

    const connectMpBtn = document.getElementById("connect-mp-btn");
    if (connectMpBtn) {
      connectMpBtn.onclick = async () => {
        const slug = slugify(state.store.storeSlug || state.store.storeName);
        state.busy = true;
        render();
        try {
          const headers = buildStoreAuthHeaders(slug);
          const res = await apiJson(`/api/payments/mercadopago/oauth/start?storeSlug=${encodeURIComponent(slug)}`, { method: "POST", headers });
          if (res?.authorizationUrl) {
            window.open(res.authorizationUrl, "_blank", "noopener");
            setStatus("⚡ Abrimos o Mercado Pago para autorizar sua loja!", null);
          } else {
            setStatus(null, res?.message || "Mercado Pago ainda não configurado no servidor.");
          }
        } catch (err) {
          setStatus(null, err.message);
        } finally {
          state.busy = false;
          render();
        }
      };
    }

    const saveSet1 = document.getElementById("save-settings-btn");
    const saveSet2 = document.getElementById("save-settings-btn-bottom");
    if (saveSet1) saveSet1.onclick = () => publishStoreToCloud("⚙️ Configurações salvas e publicadas!");
    if (saveSet2) saveSet2.onclick = () => publishStoreToCloud("⚙️ Configurações salvas e publicadas!");
  }

  loadPublicCatalogMeta();
  render();
  if (state.session) {
    pullStoreFromCloud(false);
  }
})();
