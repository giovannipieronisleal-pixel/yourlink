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

  let deferredInstallPrompt = null;
  window.addEventListener("beforeinstallprompt", (e) => {
    e.preventDefault();
    deferredInstallPrompt = e;
    render();
  });

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
    supportWhatsapp: null,
    store: createDefaultStore(),
    categories: [],
    products: [],
    publishToken: null,
    search: "",
    filterCategory: "",
    editingProductIndex: null,
    showProductForm: false,
    productDraft: null,
    linkCheck: null
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
      directPixSalesEnabled: false,
      storePixKey: "",
      storePixReceiverName: ""
    };
  }

  function createEmptyProductDraft() {
    return {
      sourceId: cryptoRandomUuid(),
      name: "",
      categoryName: state.categories[0]?.name || "Geral",
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

  function esc(str) {
    return String(str ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function formatMoney(v) {
    const n = Number(v || 0);
    return n.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
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
    try { data = text ? JSON.parse(text) : null; } catch {}
    if (!res.ok) {
      throw new Error(data?.error || data?.message || `Erro HTTP ${res.status}`);
    }
    return data;
  }

  async function loadPublicCatalogMeta() {
    try {
      const cat = await apiJson("/api/platform-sync/plan-catalog", { method: "GET" });
      if (cat?.supportWhatsappNumber) {
        state.supportWhatsapp = cat.supportWhatsappNumber;
      }
    } catch {}
  }

  async function pullStoreFromCloud(showToast = false) {
    if (!state.session?.passwordHash) return;
    state.busy = true;
    render();
    try {
      const res = await apiJson("/api/cloud/pull-store", {
        method: "POST",
        body: JSON.stringify({
          email: state.session.email,
          username: state.session.username,
          passwordHash: state.session.passwordHash,
          storeSlug: state.session.storeSlug || state.store.storeSlug
        })
      });

      if (res?.found && res.payload) {
        const s = res.payload.settings || {};
        state.store = {
          ...createDefaultStore(),
          storeName: s.storeName || state.session.storeName || "Minha Loja",
          storeSlug: res.storeSlug || s.storeSlug || slugify(s.storeName),
          whatsappNumber: s.whatsappNumber || "",
          storeDescription: s.storeDescription || "Catálogo oficial da loja.",
          logoPublicUrl: s.logoPublicUrl || null,
          isSiteActive: s.isSiteActive !== false,
          autoSyncOnlineOnSave: true,
          planTier: s.planTier || state.session.planTier || "Basico",
          directPixSalesEnabled: !!s.directPixSalesEnabled,
          storePixKey: s.storePixKey || "",
          storePixReceiverName: s.storePixReceiverName || ""
        };
        state.categories = (res.payload.categories || []).map((c, idx) => ({
          name: c.name || "Geral",
          description: c.description || "",
          displayOrder: c.displayOrder || idx + 1,
          isActive: c.isActive !== false
        }));
        state.products = (res.payload.catalogSnapshot || []).map((p) => ({
          sourceId: p.sourceId || cryptoRandomUuid(),
          name: p.name || "",
          slug: p.slug || slugify(p.name),
          categoryName: p.categoryName || "Geral",
          description: p.description || "",
          basePrice: Number(p.basePrice || 0),
          isVisibleOnSite: p.isVisibleOnSite !== false,
          variants: (p.variants || []).map((v) => ({
            color: v.color || "Padrão",
            colorHex: v.colorHex || "#18181b",
            size: v.size || "Único",
            sku: v.sku || null,
            stockQuantity: Number(v.stockQuantity || 0),
            lowStockThreshold: Number(v.lowStockThreshold || 3),
            isActive: v.isActive !== false
          })),
          images: (p.images || []).map((img, i) => ({
            publicUrl: img.publicUrl,
            storagePath: img.storagePath || img.publicUrl,
            altText: img.altText || p.name,
            displayOrder: i,
            isPrimary: i === 0 || !!img.isPrimary
          }))
        }));
        if (res.publishToken) state.publishToken = res.publishToken;
        if (state.store.storeSlug) {
          saveSession({ ...state.session, storeSlug: state.store.storeSlug, storeName: state.store.storeName, planTier: state.store.planTier });
        }
        if (showToast) setStatus(`✓ Loja '${state.store.storeName}' sincronizada (${state.products.length} produtos)!`, null);
      } else if (showToast) {
        setStatus("Sua conta está conectada! Configure o nome da loja e adicione seu primeiro produto.", null);
      }
    } catch (err) {
      if (showToast) setStatus(null, err.message);
    } finally {
      state.busy = false;
      render();
    }
  }

  async function publishStoreToCloud(successLabel = "✓ Alterações salvas e publicadas na sua loja online!") {
    if (!state.session) return;
    state.busy = true;
    state.msgErr = null;
    render();
    try {
      const slug = slugify(state.store.storeSlug || state.store.storeName);
      state.store.storeSlug = slug;

      const catNames = new Set(state.categories.map((c) => c.name.trim()).filter(Boolean));
      for (const p of state.products) {
        if (p.categoryName && p.categoryName.trim()) catNames.add(p.categoryName.trim());
      }
      if (catNames.size === 0) catNames.add("Geral");

      const categoriesPayload = Array.from(catNames).map((name, idx) => ({
        name,
        description: "",
        displayOrder: idx + 1,
        isActive: true
      }));

      const payload = {
        settings: {
          storeName: (state.store.storeName || "Minha Loja").trim(),
          storeSlug: slug,
          whatsappNumber: (state.store.whatsappNumber || "5511900000000").trim(),
          storeDescription: (state.store.storeDescription || "Catálogo digital oficial da loja.").trim(),
          logoPublicUrl: state.store.logoPublicUrl || null,
          defaultLowStockThreshold: 3,
          onlineBaseUrl: DEFAULT_API_ORIGIN,
          autoSyncOnlineOnSave: true,
          isSiteActive: state.store.isSiteActive !== false,
          planTier: state.store.planTier || "Basico",
          directPixSalesEnabled: !!state.store.directPixSalesEnabled,
          storePixKey: state.store.storePixKey || null,
          storePixReceiverName: state.store.storePixReceiverName || null,
          serverHostingMode: "PlatformShared",
          centralCatalogBaseUrl: DEFAULT_API_ORIGIN
        },
        categories: categoriesPayload,
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
          variants: (p.variants || []).map((v) => ({
            color: (v.color || "Padrão").trim() || "Padrão",
            colorHex: v.colorHex || "#18181b",
            size: (v.size || "Único").trim().toUpperCase() || "Único",
            sku: v.sku || null,
            stockQuantity: Math.max(0, Number(v.stockQuantity || 0)),
            lowStockThreshold: Number(v.lowStockThreshold || 3),
            isActive: true
          })),
          images: (p.images || []).map((img, i) => ({
            publicUrl: img.publicUrl,
            storagePath: img.storagePath || img.publicUrl,
            altText: img.altText || p.name,
            displayOrder: i,
            isPrimary: i === 0
          }))
        }))
      };

      const headers = {
        "X-YouLink-Store-Slug": slug,
        "X-YouLink-Owner-Email": state.session.email || ""
      };
      if (state.publishToken) headers["X-YouLink-Publish-Token"] = state.publishToken;

      const res = await apiJson(`/api/cloud/publish-store?storeSlug=${encodeURIComponent(slug)}`, {
        method: "POST",
        headers,
        body: JSON.stringify(payload)
      });

      if (res?.publishToken) state.publishToken = res.publishToken;
      if (res?.storeSlug) state.store.storeSlug = res.storeSlug;
      saveSession({ ...state.session, storeSlug: state.store.storeSlug, storeName: state.store.storeName });
      setStatus(successLabel, null);
    } catch (err) {
      setStatus(null, `Não foi possível publicar agora: ${err.message}`);
    } finally {
      state.busy = false;
      render();
    }
  }

  function compressImageToDataUrl(file) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onerror = () => reject(new Error("Falha ao ler a imagem."));
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
          resolve(canvas.toDataURL("image/jpeg", 0.82));
        };
        img.src = reader.result;
      };
      reader.readAsDataURL(file);
    });
  }

  function getStoreUrl() {
    return `${DEFAULT_API_ORIGIN}/loja/${encodeURIComponent(slugify(state.store.storeSlug || state.store.storeName))}`;
  }

  async function handleInstallPwa() {
    if (deferredInstallPrompt) {
      deferredInstallPrompt.prompt();
      await deferredInstallPrompt.userChoice;
      deferredInstallPrompt = null;
      render();
    } else {
      setStatus("📲 No Chrome/Edge: toque nos 3 pontinhos (⋮) → 'Instalar aplicativo' ou 'Adicionar à tela inicial'. No iPhone (Safari): toque em Compartilhar (⬆️) → 'Adicionar à Tela de Início'.", null);
    }
  }

  function renderAuth() {
    const isReg = state.authTab === "register";
    return `
      <div class="auth-wrap">
        <div class="auth-card">
          <div class="brand-mark">
            <div class="brand-dot">YL</div>
            <div>
              <div>YouLink Painel</div>
              <div class="muted" style="font-size:0.75rem;">App Web Instantâneo • Sem APK e Sem Verificações</div>
            </div>
          </div>

          <div class="tab-switch">
            <button type="button" class="${!isReg ? "active" : ""}" data-action="auth-tab" data-tab="login">Entrar</button>
            <button type="button" class="${isReg ? "active" : ""}" data-action="auth-tab" data-tab="register">Criar Conta</button>
          </div>

          ${state.msgErr ? `<div class="alert alert-err">${esc(state.msgErr)}</div>` : ""}
          ${state.msgOk ? `<div class="alert alert-ok">${esc(state.msgOk)}</div>` : ""}

          ${isReg ? `
            <form id="register-form" style="display:flex;flex-direction:column;gap:0.75rem;">
              <div class="field">
                <label class="label">Nome Completo *</label>
                <input class="input" name="fullName" required placeholder="Seu nome" />
              </div>
              <div class="field">
                <label class="label">Usuário de Acesso *</label>
                <input class="input" name="username" required placeholder="ex.: minha.loja" />
              </div>
              <div class="field">
                <label class="label">E-mail / Gmail *</label>
                <input class="input" type="email" name="email" required placeholder="seu.email@gmail.com" />
              </div>
              <div class="field">
                <label class="label">Senha (mín. 4 caracteres) *</label>
                <input class="input" type="password" name="password" minlength="4" required placeholder="••••" />
              </div>
              <button type="submit" class="btn btn-success" ${state.busy ? "disabled" : ""}>
                ${state.busy ? "⏳ Criando conta..." : "✨ Criar Conta e Abrir Painel"}
              </button>
            </form>
          ` : `
            <form id="login-form" style="display:flex;flex-direction:column;gap:0.75rem;">
              <div class="field">
                <label class="label">Usuário ou E-mail *</label>
                <input class="input" name="login" required placeholder="seu.usuario ou email@gmail.com" />
              </div>
              <div class="field">
                <label class="label">Senha *</label>
                <input class="input" type="password" name="password" required placeholder="Sua senha" />
              </div>
              <button type="submit" class="btn btn-primary" ${state.busy ? "disabled" : ""}>
                ${state.busy ? "⏳ Entrando..." : "🔐 Entrar na Minha Loja"}
              </button>
            </form>
          `}

          <button type="button" class="btn btn-outline" data-action="install-pwa">
            📲 Instalar YouLink na Tela Inicial (Sem APK)
          </button>
        </div>
      </div>
    `;
  }

  function renderHomeTab() {
    const totalStock = state.products.reduce((acc, p) => acc + (p.variants || []).reduce((s, v) => s + Number(v.stockQuantity || 0), 0), 0);
    const storeUrl = getStoreUrl();
    return `
      <div class="card">
        <div class="row" style="justify-content:space-between;margin-bottom:0.5rem;">
          <div>
            <div style="font-size:1.15rem;font-weight:900;">${esc(state.store.storeName)}</div>
            <div class="muted">${esc(state.store.storeDescription)}</div>
          </div>
          <span class="pill pill-gold">Plano ${esc(state.store.planTier || "Basico")}</span>
        </div>

        <div class="kpi-grid" style="margin-top:0.75rem;">
          <div class="kpi-box">
            <div class="kpi-label">Produtos</div>
            <div class="kpi-val">${state.products.length}</div>
          </div>
          <div class="kpi-box">
            <div class="kpi-label">Estoque Total</div>
            <div class="kpi-val">${totalStock} un.</div>
          </div>
        </div>
      </div>

      <div class="card">
        <h2 class="card-title">🌐 Link Oficial da Sua Vitrine</h2>
        <div style="background:var(--surface-2);border:1px solid var(--border);border-radius:10px;padding:0.7rem;font-family:monospace;font-size:0.84rem;word-break:break-all;margin-bottom:0.75rem;">
          ${esc(storeUrl)}
        </div>
        <div class="row">
          <button type="button" class="btn btn-outline btn-sm" data-action="copy-url">📋 Copiar Link</button>
          <a class="btn btn-success btn-sm" target="_blank" rel="noopener" href="https://wa.me/?text=${encodeURIComponent("Confira nossa loja online: " + storeUrl)}">💬 WhatsApp</a>
          <a class="btn btn-primary btn-sm" target="_blank" rel="noopener" href="${esc(storeUrl)}">🌐 Abrir Vitrine</a>
        </div>
      </div>

      <div class="card">
        <h2 class="card-title">⚡ App Instalável (Sempre Atualizado)</h2>
        <p class="muted" style="margin:0 0 0.75rem;">
          Instale este painel na tela inicial do seu celular ou PC: ele atualiza automaticamente sem baixar APK e sem avisos do Play Protect / SmartScreen.
        </p>
        <div class="row">
          <button type="button" class="btn btn-primary" data-action="install-pwa">📲 Instalar na Tela Inicial</button>
          <button type="button" class="btn btn-outline" data-action="go-tab" data-tab="products">+ Cadastrar Produto</button>
        </div>
      </div>
    `;
  }

  function renderProductForm() {
    const d = state.productDraft;
    const isEdit = state.editingProductIndex !== null;
    return `
      <div class="card" style="border-left:4px solid var(--success);">
        <div class="row" style="justify-content:space-between;margin-bottom:0.75rem;">
          <h2 class="card-title" style="margin:0;">${isEdit ? "✏️ Editar Produto & Variantes" : "+ Novo Produto"}</h2>
          <button type="button" class="btn btn-outline btn-sm" data-action="close-product-form">Fechar</button>
        </div>

        <div class="grid-2" style="margin-bottom:0.85rem;">
          <div class="field">
            <label class="label">Nome do Produto *</label>
            <input class="input" data-draft="name" value="${esc(d.name)}" placeholder="Ex.: Camisa de linho" />
          </div>
          <div class="field">
            <label class="label">Categoria *</label>
            <input class="input" data-draft="categoryName" value="${esc(d.categoryName)}" placeholder="Ex.: Camisas" />
          </div>
          <div class="field">
            <label class="label">Preço de Venda (R$) *</label>
            <input class="input" type="number" step="0.01" data-draft="basePrice" value="${esc(d.basePrice)}" />
          </div>
          <div class="field">
            <label class="label">Foto do Produto (Celular/PC)</label>
            <input class="input" type="file" accept="image/*" id="product-photo-input" style="padding:0.45rem;" />
          </div>
        </div>

        ${d.images.length > 0 ? `
          <div class="row" style="margin-bottom:0.85rem;">
            ${d.images.map((img, i) => `
              <div style="position:relative;">
                <img src="${esc(img.publicUrl)}" alt="" style="width:62px;height:62px;object-fit:cover;border-radius:8px;border:1px solid var(--border);" />
                <button type="button" class="btn btn-danger btn-sm" data-action="remove-draft-img" data-idx="${i}" style="position:absolute;top:-6px;right:-6px;padding:0.1rem 0.35rem;border-radius:999px;font-size:0.7rem;">✕</button>
              </div>
            `).join("")}
          </div>
        ` : ""}

        <div style="margin-bottom:0.85rem;display:flex;flex-direction:column;gap:0.6rem;">
          <div class="row" style="justify-content:space-between;">
            <strong style="font-size:0.88rem;">🎨 Cores / Modelos, Tamanhos e Estoque</strong>
            <button type="button" class="btn btn-outline btn-sm" data-action="add-variant-row">+ Outra Cor / Tamanho</button>
          </div>
          ${d.variants.map((v, idx) => `
            <div class="variant-card">
              ${d.variants.length > 1 ? `
                <div class="row" style="justify-content:space-between;">
                  <span class="muted" style="font-size:0.75rem;font-weight:700;">Opção #${idx + 1}</span>
                  <button type="button" class="btn btn-danger btn-sm" data-action="remove-variant-row" data-idx="${idx}">✕ Remover</button>
                </div>
              ` : ""}
              <div class="variant-grid">
                <div class="field">
                  <label class="label">Cor / Modelo</label>
                  <div style="display:flex;gap:0.4rem;align-items:center;">
                    <input type="color" class="input" data-vidx="${idx}" data-vfield="colorHex" value="${esc(v.colorHex || "#18181b")}" style="width:42px;min-width:42px;height:38px;padding:0.15rem;flex:0 0 42px;" />
                    <input type="text" class="input" data-vidx="${idx}" data-vfield="color" value="${esc(v.color)}" placeholder="Preta, Branca..." />
                  </div>
                </div>
                <div class="field">
                  <label class="label">Tamanho / Opção</label>
                  <input type="text" class="input" data-vidx="${idx}" data-vfield="size" value="${esc(v.size)}" placeholder="P, M, G, GG..." />
                </div>
                <div class="field">
                  <label class="label">Qtd. (un.)</label>
                  <input type="number" min="0" class="input" data-vidx="${idx}" data-vfield="stockQuantity" value="${esc(v.stockQuantity)}" />
                </div>
              </div>
            </div>
          `).join("")}
        </div>

        <div class="field" style="margin-bottom:0.85rem;">
          <label class="label">Descrição</label>
          <textarea class="textarea" rows="2" data-draft="description" placeholder="Detalhes do produto...">${esc(d.description)}</textarea>
        </div>

        <div class="row">
          <button type="button" class="btn btn-success" data-action="save-product-draft" ${state.busy ? "disabled" : ""}>
            ${state.busy ? "⏳ Salvando..." : "💾 Salvar e Publicar Produto"}
          </button>
          <button type="button" class="btn btn-outline" data-action="close-product-form">Cancelar</button>
        </div>
      </div>
    `;
  }

  function renderProductsTab() {
    const term = state.search.trim().toLowerCase();
    const filtered = state.products
      .map((p, idx) => ({ p, idx }))
      .filter(({ p }) => !term || (p.name || "").toLowerCase().includes(term) || (p.categoryName || "").toLowerCase().includes(term));

    return `
      <div class="row" style="justify-content:space-between;">
        <h2 style="margin:0;font-size:1.2rem;font-weight:900;">Produtos (${state.products.length})</h2>
        <button type="button" class="btn btn-primary" data-action="open-new-product">+ Novo Produto</button>
      </div>

      ${state.showProductForm ? renderProductForm() : ""}

      <div class="card" style="padding:0.75rem;">
        <input class="input" id="search-products-input" value="${esc(state.search)}" placeholder="🔍 Buscar produto ou categoria..." />
      </div>

      <div style="display:flex;flex-direction:column;gap:0.7rem;">
        ${filtered.length === 0 ? `<div class="card muted" style="text-align:center;">Nenhum produto cadastrado ainda. Clique em <strong>+ Novo Produto</strong> acima.</div>` : ""}
        ${filtered.map(({ p, idx }) => {
          const totalQty = (p.variants || []).reduce((s, v) => s + Number(v.stockQuantity || 0), 0);
          const cover = p.images?.[0]?.publicUrl;
          return `
            <div class="product-item">
              ${cover ? `<img class="product-thumb" src="${esc(cover)}" alt="${esc(p.name)}" />` : `<div class="product-thumb">Sem foto</div>`}
              <div style="flex:1;min-width:0;display:flex;flex-direction:column;gap:0.35rem;">
                <div class="row" style="justify-content:space-between;">
                  <strong style="font-size:0.98rem;">${esc(p.name)}</strong>
                  <strong>${formatMoney(p.basePrice)}</strong>
                </div>
                <div class="row">
                  <span class="pill">${esc(p.categoryName || "Geral")}</span>
                  <span class="pill ${totalQty > 0 ? "pill-green" : "pill-red"}">${totalQty} un.</span>
                  <span class="pill">${(p.variants || []).length} var.</span>
                </div>
                <div class="row" style="margin-top:0.25rem;">
                  <button type="button" class="btn btn-outline btn-sm" data-action="edit-product" data-idx="${idx}">✏️ Editar / Variantes</button>
                  <button type="button" class="btn btn-danger btn-sm" data-action="delete-product" data-idx="${idx}">🗑️</button>
                </div>
              </div>
            </div>
          `;
        }).join("")}
      </div>
    `;
  }

  function renderSettingsTab() {
    const s = state.store;
    return `
      <div class="card" style="display:flex;flex-direction:column;gap:0.85rem;">
        <h2 class="card-title" style="margin:0;">🏪 Dados & Link da Loja</h2>

        <div class="field">
          <label class="label">Nome da Loja *</label>
          <input class="input" data-store="storeName" value="${esc(s.storeName)}" placeholder="Ex.: Minha Loja" />
        </div>

        <div class="field">
          <label class="label">Endereço da Loja (/loja/...)</label>
          <div class="row">
            <input class="input" style="flex:1 1 180px;" data-store="storeSlug" value="${esc(s.storeSlug)}" placeholder="minha-loja" />
            <button type="button" class="btn btn-outline btn-sm" data-action="check-render-slug" ${state.busy ? "disabled" : ""}>🔍 Verificar no Render</button>
          </div>
          ${state.linkCheck ? `
            <div class="alert ${state.linkCheck.isAvailable ? "alert-ok" : "alert-err"}" style="margin-top:0.3rem;">
              ${esc(state.linkCheck.message)}
            </div>
          ` : ""}
        </div>

        <div class="field">
          <label class="label">WhatsApp de Atendimento *</label>
          <input class="input" data-store="whatsappNumber" value="${esc(s.whatsappNumber)}" placeholder="Ex.: 11999990000" />
        </div>

        <div class="field">
          <label class="label">Descrição / Bio da Loja</label>
          <textarea class="textarea" rows="3" data-store="storeDescription">${esc(s.storeDescription)}</textarea>
        </div>

        <div class="field">
          <label class="label">Chave Pix da Loja (Opcional)</label>
          <input class="input" data-store="storePixKey" value="${esc(s.storePixKey)}" placeholder="CPF, E-mail, Celular ou Chave Aleatória" />
        </div>

        <div class="row">
          <button type="button" class="btn btn-success" data-action="save-store-settings" ${state.busy ? "disabled" : ""}>
            ${state.busy ? "⏳ Publicando..." : "💾 Salvar e Publicar na Hora"}
          </button>
        </div>
      </div>

      <div class="card">
        <h2 class="card-title">🎨 Aparência do Painel</h2>
        <div class="row">
          <button type="button" class="btn ${state.theme === "dark" ? "btn-primary" : "btn-outline"} btn-sm" data-action="set-theme" data-theme="dark">🌙 Modo Escuro</button>
          <button type="button" class="btn ${state.theme === "light" ? "btn-primary" : "btn-outline"} btn-sm" data-action="set-theme" data-theme="light">☀️ Modo Claro</button>
        </div>
      </div>
    `;
  }

  function renderAccountTab() {
    const supUrl = state.supportWhatsapp ? `https://wa.me/${state.supportWhatsapp}?text=${encodeURIComponent("Olá Suporte YouLink!")}` : null;
    return `
      <div class="card" style="display:flex;flex-direction:column;gap:0.75rem;">
        <h2 class="card-title" style="margin:0;">👤 Minha Conta</h2>
        <div class="muted">
          <strong>${esc(state.session.fullName || state.session.username)}</strong><br/>
          Usuário: <code>${esc(state.session.username)}</code><br/>
          E-mail: <code>${esc(state.session.email)}</code>
        </div>
        <div class="row">
          <button type="button" class="btn btn-outline" data-action="sync-pull" ${state.busy ? "disabled" : ""}>🔄 Atualizar Dados da Nuvem</button>
          <button type="button" class="btn btn-primary" data-action="install-pwa">📲 Instalar App (Sem APK)</button>
          ${supUrl ? `<a class="btn btn-success" target="_blank" rel="noopener" href="${esc(supUrl)}">💬 Suporte no WhatsApp</a>` : ""}
          <button type="button" class="btn btn-danger" data-action="logout">Sair da Conta</button>
        </div>
      </div>
    `;
  }

  function render() {
    const root = document.getElementById("app");
    if (!state.session) {
      root.innerHTML = renderAuth();
      bindEvents();
      return;
    }

    let body = "";
    if (state.activeTab === "home") body = renderHomeTab();
    else if (state.activeTab === "products") body = renderProductsTab();
    else if (state.activeTab === "settings") body = renderSettingsTab();
    else body = renderAccountTab();

    root.innerHTML = `
      <div class="app-shell">
        <header class="topbar">
          <div class="brand-mark">
            <div class="brand-dot">YL</div>
            <span>${esc(state.store.storeName || "YouLink")}</span>
          </div>
          <div class="row">
            <button type="button" class="btn btn-outline btn-sm" data-action="sync-pull" ${state.busy ? "disabled" : ""}>🔄</button>
          </div>
        </header>

        <main class="container">
          ${state.msgErr ? `<div class="alert alert-err">${esc(state.msgErr)}</div>` : ""}
          ${state.msgOk ? `<div class="alert alert-ok">${esc(state.msgOk)}</div>` : ""}
          ${body}
        </main>

        <nav class="bottom-nav">
          <button type="button" class="nav-btn ${state.activeTab === "home" ? "active" : ""}" data-action="go-tab" data-tab="home"><span class="nav-ico">🏠</span>Início</button>
          <button type="button" class="nav-btn ${state.activeTab === "products" ? "active" : ""}" data-action="go-tab" data-tab="products"><span class="nav-ico">🛍️</span>Produtos</button>
          <button type="button" class="nav-btn ${state.activeTab === "settings" ? "active" : ""}" data-action="go-tab" data-tab="settings"><span class="nav-ico">🏪</span>Loja</button>
          <button type="button" class="nav-btn ${state.activeTab === "account" ? "active" : ""}" data-action="go-tab" data-tab="account"><span class="nav-ico">👤</span>Conta</button>
        </nav>
      </div>
    `;

    bindEvents();
  }

  function bindEvents() {
    document.querySelectorAll("[data-action]").forEach((el) => {
      el.onclick = async () => {
        const act = el.getAttribute("data-action");
        if (act === "auth-tab") {
          state.authTab = el.getAttribute("data-tab");
          setStatus(null, null);
        } else if (act === "go-tab") {
          state.activeTab = el.getAttribute("data-tab");
          state.msgOk = null;
          state.msgErr = null;
          render();
        } else if (act === "install-pwa") {
          await handleInstallPwa();
        } else if (act === "copy-url") {
          try {
            await navigator.clipboard.writeText(getStoreUrl());
            setStatus("✓ Link da sua loja copiado!", null);
          } catch {
            setStatus("Copie o link exibido no quadro acima.", null);
          }
        } else if (act === "sync-pull") {
          await pullStoreFromCloud(true);
        } else if (act === "logout") {
          saveSession(null);
          state.store = createDefaultStore();
          state.products = [];
          state.categories = [];
          setStatus(null, null);
        } else if (act === "set-theme") {
          applyTheme(el.getAttribute("data-theme"));
          render();
        } else if (act === "open-new-product") {
          state.editingProductIndex = null;
          state.productDraft = createEmptyProductDraft();
          state.showProductForm = true;
          render();
        } else if (act === "close-product-form") {
          state.showProductForm = false;
          state.editingProductIndex = null;
          render();
        } else if (act === "add-variant-row") {
          const last = state.productDraft.variants[state.productDraft.variants.length - 1];
          state.productDraft.variants.push({
            color: last?.color || "",
            colorHex: last?.colorHex || "#18181b",
            size: "",
            stockQuantity: last?.stockQuantity || 5,
            lowStockThreshold: 3,
            isActive: true
          });
          render();
        } else if (act === "remove-variant-row") {
          const idx = Number(el.getAttribute("data-idx"));
          if (state.productDraft.variants.length > 1) {
            state.productDraft.variants.splice(idx, 1);
            render();
          }
        } else if (act === "remove-draft-img") {
          const idx = Number(el.getAttribute("data-idx"));
          state.productDraft.images.splice(idx, 1);
          render();
        } else if (act === "edit-product") {
          const idx = Number(el.getAttribute("data-idx"));
          const p = state.products[idx];
          if (p) {
            state.editingProductIndex = idx;
            state.productDraft = JSON.parse(JSON.stringify(p));
            if (!state.productDraft.variants || state.productDraft.variants.length === 0) {
              state.productDraft.variants = [{ color: "", colorHex: "#18181b", size: "", stockQuantity: 5, lowStockThreshold: 3, isActive: true }];
            }
            state.showProductForm = true;
            render();
          }
        } else if (act === "delete-product") {
          const idx = Number(el.getAttribute("data-idx"));
          if (confirm("Remover este produto da loja?")) {
            state.products.splice(idx, 1);
            await publishStoreToCloud("✓ Produto removido e loja atualizada!");
          }
        } else if (act === "save-product-draft") {
          const d = state.productDraft;
          if (!d.name || !d.name.trim()) {
            setStatus(null, "Informe o nome do produto.");
            return;
          }
          const cleanProduct = {
            sourceId: d.sourceId || cryptoRandomUuid(),
            name: d.name.trim(),
            slug: slugify(d.name.trim()),
            categoryName: (d.categoryName || "Geral").trim() || "Geral",
            description: (d.description || "").trim(),
            basePrice: Math.max(0.01, Number(d.basePrice || 0)),
            isVisibleOnSite: d.isVisibleOnSite !== false,
            images: d.images || [],
            variants: (d.variants || []).map((v) => ({
              color: (v.color || "Padrão").trim() || "Padrão",
              colorHex: v.colorHex || "#18181b",
              size: (v.size || "Único").trim().toUpperCase() || "Único",
              stockQuantity: Math.max(0, Number(v.stockQuantity || 0)),
              lowStockThreshold: 3,
              isActive: true
            }))
          };

          if (state.editingProductIndex !== null) {
            state.products[state.editingProductIndex] = cleanProduct;
          } else {
            const existingIdx = state.products.findIndex(
              (p) => slugify(p.name) === slugify(cleanProduct.name) && slugify(p.categoryName) === slugify(cleanProduct.categoryName)
            );
            if (existingIdx >= 0) {
              const target = state.products[existingIdx];
              for (const nv of cleanProduct.variants) {
                const mv = target.variants.find(
                  (ev) => ev.color.toLowerCase() === nv.color.toLowerCase() && ev.size.toLowerCase() === nv.size.toLowerCase()
                );
                if (mv) mv.stockQuantity += nv.stockQuantity;
                else target.variants.push(nv);
              }
              if (cleanProduct.images.length > 0) target.images.push(...cleanProduct.images);
            } else {
              state.products.unshift(cleanProduct);
            }
          }

          state.showProductForm = false;
          state.editingProductIndex = null;
          await publishStoreToCloud(`✓ Produto '${cleanProduct.name}' salvo e publicado na sua vitrine!`);
        } else if (act === "check-render-slug") {
          state.busy = true;
          render();
          try {
            const slug = slugify(state.store.storeSlug || state.store.storeName);
            state.linkCheck = await apiJson(
              `/api/cloud/check-render-store-link?slug=${encodeURIComponent(slug)}&ownerEmail=${encodeURIComponent(state.session.email || "")}&ownerUsername=${encodeURIComponent(state.session.username || "")}`,
              { method: "GET" }
            );
            if (state.linkCheck?.isAvailable && state.linkCheck.normalizedSlug) {
              state.store.storeSlug = state.linkCheck.normalizedSlug;
            }
          } catch (err) {
            setStatus(null, err.message);
          } finally {
            state.busy = false;
            render();
          }
        } else if (act === "save-store-settings") {
          await publishStoreToCloud("✓ Configurações da loja salvas e publicadas online!");
        }
      };
    });

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
            body: JSON.stringify({ email: fd.get("login"), password: fd.get("password") })
          });
          if (!res?.verified || !res.account) {
            throw new Error(res?.message || "Usuário ou senha incorretos.");
          }
          saveSession(res.account);
          await pullStoreFromCloud(false);
          setStatus(`✓ Bem-vindo(a), ${res.account.fullName || res.account.username}!`, null);
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
          setStatus("🎉 Conta criada! Preencha o nome e WhatsApp da sua loja abaixo e salve.", null);
        } catch (err) {
          setStatus(null, err.message);
        } finally {
          state.busy = false;
          render();
        }
      };
    }

    const searchInput = document.getElementById("search-products-input");
    if (searchInput) {
      searchInput.oninput = (e) => {
        state.search = e.target.value;
        render();
        const next = document.getElementById("search-products-input");
        if (next) { next.focus(); next.setSelectionRange(next.value.length, next.value.length); }
      };
    }

    document.querySelectorAll("[data-draft]").forEach((el) => {
      el.oninput = (e) => {
        state.productDraft[el.getAttribute("data-draft")] = e.target.value;
      };
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

    document.querySelectorAll("[data-store]").forEach((el) => {
      el.oninput = (e) => {
        state.store[el.getAttribute("data-store")] = e.target.value;
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
  }

  loadPublicCatalogMeta();
  render();
  if (state.session) {
    pullStoreFromCloud(false);
  }
})();
