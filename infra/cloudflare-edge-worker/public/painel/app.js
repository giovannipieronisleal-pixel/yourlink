(() => {
  const SESSION_KEY = "youlink_pwa_session_v1";
  const THEME_KEY = "youlink_pwa_theme_v1";
  const DEFAULT_API_ORIGIN = "https://youlink-loja.onrender.com";

  function resolveApiOrigin() {
    const loc = window.location;
    if (loc.hostname === "localhost" || loc.hostname === "127.0.0.1") {
      return loc.origin;
    }
    if (loc.hostname.endsWith(".onrender.com") || loc.hostname.endsWith(".workers.dev")) {
      return loc.origin;
    }
    return DEFAULT_API_ORIGIN;
  }

  const API_BASE = resolveApiOrigin();

  const STORE_THEMES = [
    { key: "Minimalista", name: "Minimalista Luxo", tier: "Basico", bg: "#FAF8F5", primary: "#18181B", accent: "#B69158", desc: "Limpo, elegante e atemporal" },
    { key: "UrbanoClean", name: "Urbano Clean", tier: "Intermediario", bg: "#F8FAFC", primary: "#0F172A", accent: "#2563EB", desc: "Moderno com foco em conversão" },
    { key: "TerracotaSolar", name: "Terracota Solar", tier: "Intermediario", bg: "#FDF8F4", primary: "#7C2D12", accent: "#EA580C", desc: "Quente e acolhedor para moda" },
    { key: "EsmeraldaImperial", name: "Esmeralda Imperial", tier: "Intermediario", bg: "#F4FBF7", primary: "#064E3B", accent: "#10B981", desc: "Sofisticado com destaque verde" },
    { key: "GrafiteStudio", name: "Grafite Studio", tier: "Premium", bg: "#111827", primary: "#F8FAFC", accent: "#D4A853", desc: "Vitrine escura de alto impacto" },
    { key: "BoutiqueRose", name: "Boutique Rosé", tier: "Premium", bg: "#FFF5F7", primary: "#831843", accent: "#EC4899", desc: "Editorial delicado e marcante" }
  ];

  if ("serviceWorker" in navigator) {
    navigator.serviceWorker.register("/painel/sw.js", { scope: "/painel/" }).catch(() => {});
  }

  const state = {
    session: loadSession(),
    theme: localStorage.getItem(THEME_KEY) || "dark",
    authTab: "login",
    activeTab: "home",
    busy: false,
    msgOk: null,
    msgErr: null,
    copiedLink: false,
    supportWhatsapp: null,
    showMoreSheet: false,
    showProfileModal: false,
    store: createDefaultStore(),
    categories: [],
    products: [],
    publishToken: null,
    search: "",
    filterCategory: "",
    editingProductIndex: null,
    showProductForm: false,
    productDraft: null
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
      isSiteActive: true,
      autoSyncOnlineOnSave: true,
      planTier: "Basico",
      designPreset: "Minimalista",
      directPixSalesEnabled: false,
      storePixKey: "",
      storePixReceiverName: ""
    };
  }

  function createEmptyProductDraft() {
    return {
      sourceId: cryptoRandomUuid(),
      name: "",
      categoryName: (state && state.categories && state.categories[0]?.name) || "Geral",
      basePrice: "99.90",
      description: "",
      isVisibleOnSite: true,
      images: [],
      variants: [
        { color: "", colorHex: "#18181b", size: "", stockQuantity: 5, lowStockThreshold: 3, isActive: true }
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
    } catch {
      return null;
    }
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
    const num = Number(v || 0);
    return num.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
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

  function getPlanMeta() {
    const raw = (state.store.planTier || state.session?.planTier || "Basico").toLowerCase();
    if (raw.includes("prem")) return { label: "✦ Premium", cls: "pill-gold", name: "Plano Premium" };
    if (raw.includes("inter")) return { label: "◈ Meia Versão", cls: "pill-green", name: "Plano Intermediário" };
    return { label: "● Plano Básico", cls: "pill-slate", name: "Plano Básico" };
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
      throw new Error(data?.error || data?.message || `Erro HTTP ${res.status}`);
    }
    return data;
  }

  async function loadPublicCatalogMeta() {
    try {
      const data = await apiJson("/api/platform/public-catalog");
      if (data?.supportWhatsApp) {
        state.supportWhatsapp = data.supportWhatsApp;
      }
    } catch {}
  }

  async function pullStoreFromCloud(showMessage = false) {
    if (!state.session) return;
    state.busy = true;
    render();
    try {
      const res = await apiJson("/api/cloud/pull-store", {
        method: "POST",
        body: JSON.stringify({
          login: state.session.email || state.session.username,
          passwordHash: state.session.passwordHash,
          storeSlug: state.session.storeSlug || state.store.storeSlug
        })
      });

      if (res?.found && res.snapshot) {
        applySnapshot(res.snapshot);
        if (showMessage) setStatus("☁️ Loja sincronizada da nuvem com sucesso!", null);
      } else if (showMessage) {
        setStatus("Sua conta está pronta! Cadastre seu primeiro produto abaixo.", null);
      }
    } catch (err) {
      if (showMessage) setStatus(null, err.message);
    } finally {
      state.busy = false;
      render();
    }
  }

  function applySnapshot(snap) {
    const s = snap.store || {};
    state.store = {
      ...createDefaultStore(),
      ...s,
      storeName: s.storeName || state.session?.storeName || "Minha Loja",
      storeSlug: s.storeSlug || state.session?.storeSlug || slugify(s.storeName),
      planTier: s.planTier || state.session?.planTier || "Basico",
      designPreset: s.designPreset || "Minimalista"
    };
    state.categories = Array.isArray(snap.categories) ? snap.categories : [];
    state.products = Array.isArray(snap.products) ? snap.products : [];
  }

  function buildPublishPayload() {
    const slug = slugify(state.store.storeSlug || state.store.storeName);
    state.store.storeSlug = slug;

    const catMap = new Map();
    for (const c of state.categories) {
      if (c?.name) catMap.set(c.name.trim().toLowerCase(), {
        sourceId: c.sourceId || cryptoRandomUuid(),
        name: c.name.trim(),
        slug: slugify(c.name),
        description: c.description || "",
        displayOrder: c.displayOrder || 0,
        isActive: true
      });
    }
    for (const p of state.products) {
      const cname = (p.categoryName || "Geral").trim();
      const key = cname.toLowerCase();
      if (!catMap.has(key)) {
        catMap.set(key, {
          sourceId: cryptoRandomUuid(),
          name: cname,
          slug: slugify(cname),
          description: "",
          displayOrder: catMap.size,
          isActive: true
        });
      }
    }
    if (catMap.size === 0) {
      catMap.set("geral", { sourceId: cryptoRandomUuid(), name: "Geral", slug: "geral", description: "", displayOrder: 0, isActive: true });
    }

    const categories = Array.from(catMap.values());
    const products = state.products.map((p) => {
      const cat = catMap.get((p.categoryName || "Geral").trim().toLowerCase()) || categories[0];
      const variants = (p.variants || [])
        .filter((v) => (v.color || "").trim() || (v.size || "").trim() || Number(v.stockQuantity) > 0)
        .map((v) => ({
          sourceId: v.sourceId || cryptoRandomUuid(),
          sku: v.sku || `${slugify(p.name).slice(0, 6).toUpperCase()}-${slugify(v.color || "PAD").slice(0, 4).toUpperCase()}-${slugify(v.size || "U").toUpperCase()}`,
          color: (v.color || "Padrão").trim(),
          colorHex: v.colorHex || "#18181b",
          size: (v.size || "Único").trim(),
          stockQuantity: Math.max(0, parseInt(v.stockQuantity, 10) || 0),
          lowStockThreshold: Math.max(0, parseInt(v.lowStockThreshold ?? 3, 10) || 3),
          priceOverride: null,
          isActive: v.isActive !== false
        }));

      if (variants.length === 0) {
        variants.push({
          sourceId: cryptoRandomUuid(),
          sku: `${slugify(p.name).slice(0, 6).toUpperCase()}-PAD-U`,
          color: "Padrão",
          colorHex: "#18181b",
          size: "Único",
          stockQuantity: 1,
          lowStockThreshold: 3,
          priceOverride: null,
          isActive: true
        });
      }

      return {
        sourceId: p.sourceId || cryptoRandomUuid(),
        categorySourceId: cat.sourceId,
        categoryName: cat.name,
        name: (p.name || "Produto").trim(),
        slug: slugify(p.slug || p.name),
        description: p.description || "",
        basePrice: parsePrice(p.basePrice),
        isVisibleOnSite: p.isVisibleOnSite !== false,
        variants,
        images: (p.images || []).map((img, i) => ({
          sourceId: img.sourceId || cryptoRandomUuid(),
          publicUrl: img.publicUrl || img.storagePath || "",
          storagePath: img.storagePath || img.publicUrl || "",
          altText: img.altText || p.name || "Produto",
          displayOrder: i,
          isPrimary: i === 0
        })).filter((i) => i.publicUrl)
      };
    });

    return {
      storeSlug: slug,
      ownerEmail: state.session?.email || "",
      ownerName: state.session?.fullName || state.session?.username || "Lojista",
      store: {
        ...state.store,
        storeSlug: slug
      },
      categories,
      products
    };
  }

  async function publishStoreToCloud(successMessage = "☁️ Alterações salvas e publicadas no site!") {
    if (!state.session) return;
    state.busy = true;
    render();
    try {
      const payload = buildPublishPayload();
      const res = await apiJson("/api/cloud/publish-store", {
        method: "POST",
        body: JSON.stringify(payload)
      });
      if (res?.storeSlug) {
        state.store.storeSlug = res.storeSlug;
        if (state.session) {
          state.session.storeSlug = res.storeSlug;
          saveSession(state.session);
        }
      }
      setStatus(successMessage, null);
    } catch (err) {
      setStatus(null, err.message);
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
          if (existing) {
            existing.stockQuantity = (Number(existing.stockQuantity) || 0) + (Number(v.stockQuantity) || 0);
          } else {
            target.variants.push(v);
          }
        }
        for (const img of (p.images || [])) {
          if (!(target.images || []).some((ti) => ti.publicUrl === img.publicUrl)) {
            target.images.push(img);
          }
        }
      }
    }

    state.products = merged;
    await publishStoreToCloud("🔗 Produtos duplicados unidos com sucesso!");
  }

  async function compressImageToDataUrl(file) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onerror = () => reject(new Error("Não foi possível ler a imagem."));
      reader.onload = () => {
        const img = new Image();
        img.onerror = () => reject(new Error("Formato de imagem inválido."));
        img.onload = () => {
          const maxDim = 900;
          let w = img.width;
          let h = img.height;
          if (w > maxDim || h > maxDim) {
            if (w >= h) { h = Math.round((h * maxDim) / w); w = maxDim; }
            else { w = Math.round((w * maxDim) / h); h = maxDim; }
          }
          const canvas = document.createElement("canvas");
          canvas.width = w;
          canvas.height = h;
          const ctx = canvas.getContext("2d");
          ctx.drawImage(img, 0, 0, w, h);
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
    for (const p of state.products) {
      for (const v of (p.variants || [])) {
        if ((Number(v.stockQuantity) || 0) <= (Number(v.lowStockThreshold ?? 3) || 3)) count++;
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
            <div class="adm-nav-label">Personalização</div>
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
          <div style="display:flex;align-items:center;gap:0.65rem;margin-bottom:0.4rem;">
            <div class="adm-brand-mark">YL</div>
            <div>
              <div style="font-weight:800;font-size:1.15rem;">YouLink</div>
              <div style="font-size:0.78rem;color:var(--adm-muted);">Painel oficial da sua loja</div>
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
                <input class="input" name="fullName" required placeholder="Ex.: Lojista Modelo" />
              </div>
              <div class="field">
                <label>Usuário</label>
                <input class="input" name="username" required placeholder="ex: minhaloja" />
              </div>
              <div class="field">
                <label>E-mail</label>
                <input class="input" name="email" type="email" required placeholder="seu@email.com" />
              </div>
              <div class="field">
                <label>Senha</label>
                <input class="input" name="password" type="password" minlength="6" required placeholder="Mínimo 6 caracteres" />
              </div>
              <button type="submit" class="adm-btn adm-btn-success adm-btn-block" ${state.busy ? "disabled" : ""}>
                ${state.busy ? "Criando conta..." : "Criar minha loja grátis"}
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

    return `
      <header class="yl-home-header">
        <div>
          <h1 class="yl-home-title">${esc(getGreeting())}</h1>
          <div class="yl-home-status-line">
            <span class="yl-status-dot ${state.store.isSiteActive ? "online" : "offline"}"></span>
            <span>${esc(state.store.storeName)}</span>
            <span>&bull;</span>
            <span>${state.store.isSiteActive ? "No ar" : "Pausada"}</span>
          </div>
        </div>
        <button type="button" class="adm-btn adm-btn-primary" id="home-new-product-btn">+ Novo Produto</button>
      </header>

      <section class="adm-card yl-share-card">
        <div>
          <div class="yl-share-label">Link da sua loja</div>
          <div class="yl-share-url">${esc(storeUrl.replace(/^https?:\/\//, ""))}</div>
        </div>
        <div class="yl-share-actions">
          <button type="button" class="adm-btn adm-btn-primary adm-btn-sm" id="copy-link-btn">
            ${state.copiedLink ? "✓ Link Copiado!" : "📋 Copiar Link"}
          </button>
          <button type="button" class="adm-btn adm-btn-success adm-btn-sm" id="share-wa-btn">
            💬 WhatsApp
          </button>
        </div>
      </section>

      <section class="yl-summary-grid">
        <button type="button" class="yl-summary-card" data-tab="products">
          <span class="yl-summary-label">Produtos ativos</span>
          <strong class="yl-summary-value">${visibleProducts}</strong>
          <span class="yl-summary-link">Abrir &rarr;</span>
        </button>

        <button type="button" class="yl-summary-card" data-tab="stock">
          <span class="yl-summary-label">Unidades em estoque</span>
          <strong class="yl-summary-value">${totalUnits}</strong>
          <span class="yl-summary-link">${lowStock > 0 ? `${lowStock} alerta(s)` : "Controlar"} &rarr;</span>
        </button>

        <button type="button" class="yl-summary-card" data-tab="orders">
          <span class="yl-summary-label">Pedidos &amp; WhatsApp</span>
          <strong class="yl-summary-value" style="font-size:1.05rem;">Ver pedidos</strong>
          <span class="yl-summary-link">Abrir &rarr;</span>
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

          <button type="button" class="yl-mobile-quick-tile" data-tab="design">
            <span class="yl-mobile-quick-icon yl-icon-gold">🎨</span>
            <strong>Aparência da Loja</strong>
            <span>Tema e estilo da vitrine</span>
          </button>

          <button type="button" class="yl-mobile-quick-tile" data-tab="settings">
            <span class="yl-mobile-quick-icon yl-icon-slate">⚙️</span>
            <strong>Configurações</strong>
            <span>WhatsApp, link e Pix</span>
          </button>
        </div>
      </section>

      <div class="adm-card" style="display:flex;align-items:center;justify-content:space-between;gap:0.75rem;flex-wrap:wrap;">
        <div>
          <div style="font-weight:800;font-size:0.94rem;">☁️ Sincronização em Nuvem</div>
          <div style="font-size:0.8rem;color:var(--adm-muted);">Atualize dados de outros aparelhos ou publique agora.</div>
        </div>
        <div style="display:flex;gap:0.5rem;flex-wrap:wrap;">
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

    const catNames = Array.from(new Set(state.products.map((p) => p.categoryName || "Geral"))).sort();

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

      ${state.showProductForm ? renderProductForm() : ""}

      <div class="adm-card" style="padding:0.8rem;">
        <div class="form-grid" style="gap:0.55rem;">
          <input class="input" id="search-products-input" placeholder="🔍 Buscar produto..." value="${esc(state.search)}" />
          <select id="filter-category-select">
            <option value="">Todas as categorias</option>
            ${catNames.map((c) => `<option value="${esc(c)}" ${state.filterCategory === c ? "selected" : ""}>${esc(c)}</option>`).join("")}
          </select>
        </div>
      </div>

      <div class="product-list">
        ${filtered.length === 0 ? `
          <div class="adm-card" style="text-align:center;padding:2rem 1rem;">
            <div style="font-size:2rem;margin-bottom:0.4rem;">🛍️</div>
            <div style="font-weight:800;margin-bottom:0.25rem;">Nenhum produto encontrado</div>
            <div style="font-size:0.84rem;color:var(--adm-muted);margin-bottom:1rem;">Toque em "+ Novo Produto" para cadastrar com cores, tamanhos e fotos.</div>
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
                  ${(p.variants || []).slice(0, 8).map((v) => `
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

  function renderProductForm() {
    const d = state.productDraft;
    const isEdit = state.editingProductIndex !== null;
    return `
      <div class="adm-card" style="border-left:4px solid var(--adm-success);">
        <h3 style="margin:0 0 0.85rem;font-size:1.05rem;">${isEdit ? "✏️ Editar Produto" : "➕ Novo Produto"}</h3>
        <div class="form-grid">
          <div class="field">
            <label>Nome do Produto *</label>
            <input class="input" data-draft="name" value="${esc(d.name)}" placeholder="Ex.: Camisa de Linho" />
          </div>
          <div class="field">
            <label>Categoria *</label>
            <input class="input" data-draft="categoryName" value="${esc(d.categoryName)}" placeholder="Ex.: Camisas, Calças..." />
          </div>
          <div class="field">
            <label>Preço de Venda (R$) *</label>
            <input class="input" data-draft="basePrice" value="${esc(d.basePrice)}" inputmode="decimal" placeholder="149,90" />
          </div>
        </div>

        <div class="variant-box">
          <div style="display:flex;align-items:center;justify-content:space-between;gap:0.5rem;flex-wrap:wrap;margin-bottom:0.65rem;">
            <div>
              <strong style="font-size:0.88rem;">🎨 Modelos / Cores, Tamanhos e Estoque</strong>
              <div style="font-size:0.76rem;color:var(--adm-muted);">Adicione cada cor e tamanho deste produto.</div>
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

        <label style="display:flex;align-items:center;gap:0.5rem;font-size:0.86rem;margin-bottom:0.9rem;cursor:pointer;">
          <input type="checkbox" id="draft-visible-chk" ${d.isVisibleOnSite !== false ? "checked" : ""} />
          Exibir imediatamente no site público
        </label>

        <div style="display:flex;gap:0.55rem;flex-wrap:wrap;">
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
        <button type="button" class="adm-btn adm-btn-primary adm-btn-sm" id="stock-save-all-btn" ${state.busy ? "disabled" : ""}>
          ${state.busy ? "Publicando..." : "☁️ Salvar no Site"}
        </button>
      </div>

      ${state.products.length === 0 ? `
        <div class="adm-card" style="text-align:center;padding:2rem;">
          Cadastre produtos primeiro para controlar o estoque.
        </div>
      ` : state.products.map((p, pIdx) => `
        <div class="adm-card">
          <div style="font-weight:800;font-size:1rem;margin-bottom:0.5rem;">${esc(p.name)} <span style="font-weight:600;color:var(--adm-muted);font-size:0.82rem;">(${totalStock(p)} un.)</span></div>
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
    const waDigits = (state.store.whatsappNumber || "").replace(/\D/g, "");
    return `
      <div class="yl-home-header">
        <div>
          <h1 class="yl-home-title">Pedidos</h1>
          <div class="yl-home-status-line">Acompanhe seus pedidos pelo WhatsApp e vitrine</div>
        </div>
        <a href="${esc(getPublicStoreUrl())}" target="_blank" rel="noopener" class="adm-btn adm-btn-outline adm-btn-sm">🌐 Abrir Vitrine</a>
      </div>

      <div class="adm-card" style="text-align:center;padding:2rem 1.2rem;">
        <div style="font-size:2.2rem;margin-bottom:0.5rem;">🧾</div>
        <h3 style="margin:0 0 0.4rem;">Pedidos via WhatsApp &amp; Vitrine</h3>
        <p style="color:var(--adm-muted);font-size:0.88rem;max-width:460px;margin:0 auto 1.1rem;">
          Seus clientes escolhem cor, tamanho e quantidade direto no site e enviam o pedido pronto no seu WhatsApp${waDigits ? ` (${esc(state.store.whatsappNumber)})` : ""}.
        </p>
        <div style="display:flex;gap:0.6rem;justify-content:center;flex-wrap:wrap;">
          <button type="button" class="adm-btn adm-btn-primary" data-tab="stock">📦 Dar baixa no estoque</button>
          <button type="button" class="adm-btn adm-btn-outline" data-tab="settings">⚙️ Configurar WhatsApp / Pix</button>
        </div>
      </div>
    `;
  }

  function renderDesignTab() {
    return `
      <div class="yl-home-header">
        <div>
          <h1 class="yl-home-title">Aparência da Loja</h1>
          <div class="yl-home-status-line">Escolha o visual do seu catálogo público</div>
        </div>
        <button type="button" class="adm-btn adm-btn-success" id="save-design-btn" ${state.busy ? "disabled" : ""}>
          ${state.busy ? "Salvando..." : "💾 Aplicar no Site"}
        </button>
      </div>

      <div class="adm-card">
        <div class="theme-cards-grid">
          ${STORE_THEMES.map((t) => `
            <button type="button" class="theme-card ${(state.store.designPreset || "Minimalista") === t.key ? "selected" : ""}" data-select-theme="${t.key}">
              <div class="theme-preview-bar">
                <div style="flex:2;background:${t.bg};"></div>
                <div style="flex:1;background:${t.primary};"></div>
                <div style="flex:1;background:${t.accent};"></div>
              </div>
              <div style="font-weight:800;font-size:0.92rem;">${t.name}</div>
              <div style="font-size:0.75rem;color:var(--adm-muted);margin-top:0.2rem;">${t.desc}</div>
            </button>
          `).join("")}
        </div>
      </div>
    `;
  }

  function renderPlansTab() {
    const plan = getPlanMeta();
    const waSupport = (state.supportWhatsapp || "").replace(/\D/g, "");
    return `
      <div class="yl-home-header">
        <div>
          <h1 class="yl-home-title">Planos &amp; VIP</h1>
          <div class="yl-home-status-line">Status atual da sua assinatura YouLink</div>
        </div>
      </div>

      <div class="adm-card">
        <div style="display:flex;align-items:center;justify-content:space-between;gap:0.75rem;flex-wrap:wrap;">
          <div>
            <span class="pill-badge ${plan.cls}">${plan.label}</span>
            <h2 style="margin:0.55rem 0 0.25rem;font-size:1.25rem;">${plan.name}</h2>
            <div style="font-size:0.86rem;color:var(--adm-muted);">
              Sua loja <strong>${esc(state.store.storeName)}</strong> está conectada na nuvem com atualização automática.
            </div>
          </div>
          ${waSupport ? `
            <a class="adm-btn adm-btn-success" target="_blank" rel="noopener" href="https://wa.me/${waSupport}?text=${encodeURIComponent("Olá! Quero saber sobre os planos VIP da YouLink.")}">
              💬 Falar com Suporte
            </a>
          ` : ""}
        </div>
      </div>
    `;
  }

  function renderSettingsTab() {
    const s = state.store;
    return `
      <div class="yl-home-header">
        <div>
          <h1 class="yl-home-title">Configurações da Loja</h1>
          <div class="yl-home-status-line">Dados públicos, WhatsApp e Pix</div>
        </div>
        <button type="button" class="adm-btn adm-btn-success" id="save-settings-btn" ${state.busy ? "disabled" : ""}>
          ${state.busy ? "Salvando..." : "💾 Salvar e Publicar"}
        </button>
      </div>

      <div class="adm-card">
        <div class="form-grid">
          <div class="field">
            <label>Nome da Loja</label>
            <input class="input" data-store="storeName" value="${esc(s.storeName)}" />
          </div>
          <div class="field">
            <label>Link da Loja (/loja/...)</label>
            <input class="input" data-store="storeSlug" value="${esc(s.storeSlug)}" />
          </div>
          <div class="field">
            <label>WhatsApp da Loja (com DDD)</label>
            <input class="input" data-store="whatsappNumber" value="${esc(s.whatsappNumber)}" placeholder="5511999999999" />
          </div>
          <div class="field">
            <label>Chave Pix (opcional)</label>
            <input class="input" data-store="storePixKey" value="${esc(s.storePixKey || "")}" placeholder="CPF, e-mail, telefone ou aleatória" />
          </div>
        </div>

        <div class="field">
          <label>Descrição da Loja</label>
          <textarea data-store="storeDescription">${esc(s.storeDescription || "")}</textarea>
        </div>

        <label style="display:flex;align-items:center;gap:0.5rem;font-size:0.88rem;margin:0.4rem 0 0.8rem;cursor:pointer;">
          <input type="checkbox" id="store-active-chk" ${s.isSiteActive !== false ? "checked" : ""} />
          Loja aberta e visível para clientes
        </label>

        <button type="button" class="adm-btn adm-btn-success" id="save-settings-btn-bottom" ${state.busy ? "disabled" : ""}>
          ${state.busy ? "Salvando..." : "💾 Salvar e Publicar Agora"}
        </button>
      </div>
    `;
  }

  function renderMoreSheet() {
    return `
      <div class="modal-backdrop" id="more-backdrop">
        <div class="modal-sheet">
          <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:0.85rem;">
            <strong style="font-size:1.05rem;">Mais opções</strong>
            <button type="button" class="adm-btn adm-btn-outline adm-btn-sm" id="close-more-btn">Fechar</button>
          </div>
          <button type="button" class="sheet-item" data-sheet-tab="stock"><span>📦 Controle de Estoque</span><span>&rarr;</span></button>
          <button type="button" class="sheet-item" data-sheet-tab="design"><span>🎨 Aparência da Loja</span><span>&rarr;</span></button>
          <button type="button" class="sheet-item" data-sheet-tab="plans"><span>⭐ Planos &amp; VIP</span><span>&rarr;</span></button>
          <button type="button" class="sheet-item" data-sheet-tab="settings"><span>⚙️ Configurações da Loja</span><span>&rarr;</span></button>
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
          <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:0.85rem;">
            <strong style="font-size:1.05rem;">Minha Conta</strong>
            <button type="button" class="adm-btn adm-btn-outline adm-btn-sm" id="close-profile-btn">Fechar</button>
          </div>
          <div style="margin-bottom:1rem;">
            <div style="font-weight:800;font-size:1rem;">${esc(state.session?.fullName || state.session?.username)}</div>
            <div style="font-size:0.84rem;color:var(--adm-muted);">${esc(state.session?.email || "")}</div>
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
            body: JSON.stringify({ login: fd.get("login"), password: fd.get("password") })
          });
          if (!res?.verified || !res.account) {
            throw new Error(res?.message || "Usuário ou senha inválidos.");
          }
          saveSession(res.account);
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
        state.busy = true;
        state.msgErr = null;
        render();
        try {
          const res = await apiJson("/api/cloud/accounts/register", {
            method: "POST",
            body: JSON.stringify({
              fullName: fd.get("fullName"),
              username: fd.get("username"),
              email: fd.get("email"),
              password: fd.get("password")
            })
          });
          if (!res?.verified || !res.account) {
            throw new Error(res?.message || "Não foi possível criar a conta.");
          }
          saveSession(res.account);
          state.activeTab = "settings";
          setStatus("🎉 Conta criada! Preencha o nome e WhatsApp da sua loja abaixo.", null);
        } catch (err) {
          setStatus(null, err.message);
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
    state.showMoreSheet = false;
    state.showProfileModal = false;
    state.msgOk = null;
    state.msgErr = null;
    render();
  }

  function bindShellEvents() {
    document.querySelectorAll("[data-tab]").forEach((el) => {
      el.onclick = () => {
        state.activeTab = el.getAttribute("data-tab");
        state.showMoreSheet = false;
        state.msgOk = null;
        state.msgErr = null;
        render();
      };
    });

    document.querySelectorAll("[data-sheet-tab]").forEach((el) => {
      el.onclick = () => {
        state.activeTab = el.getAttribute("data-sheet-tab");
        state.showMoreSheet = false;
        render();
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
        state.productDraft.variants.push({ color: "", colorHex: "#18181b", size: "", stockQuantity: 5, lowStockThreshold: 3, isActive: true });
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
        } catch (err) {
          setStatus(null, err.message);
        }
      };
    }

    const visChk = document.getElementById("draft-visible-chk");
    if (visChk) visChk.onchange = (e) => { state.productDraft.isVisibleOnSite = e.target.checked; };

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
          // Mescla automaticamente se já existir produto com mesmo nome + categoria
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
        if (v) {
          v.stockQuantity = Math.max(0, (Number(v.stockQuantity) || 0) + delta);
          render();
        }
      };
    });

    const stockSaveBtn = document.getElementById("stock-save-all-btn");
    if (stockSaveBtn) stockSaveBtn.onclick = () => publishStoreToCloud("📦 Estoque atualizado no site!");

    document.querySelectorAll("[data-select-theme]").forEach((el) => {
      el.onclick = () => {
        state.store.designPreset = el.getAttribute("data-select-theme");
        render();
      };
    });

    const saveDesignBtn = document.getElementById("save-design-btn");
    if (saveDesignBtn) saveDesignBtn.onclick = () => publishStoreToCloud("🎨 Aparência da loja atualizada!");

    document.querySelectorAll("[data-store]").forEach((el) => {
      el.oninput = (e) => { state.store[el.getAttribute("data-store")] = e.target.value; };
    });

    const activeChk = document.getElementById("store-active-chk");
    if (activeChk) activeChk.onchange = (e) => { state.store.isSiteActive = e.target.checked; };

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
