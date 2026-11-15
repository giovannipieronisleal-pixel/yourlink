# YouLink — Catálogo Digital, Loja Online Multi-Tenant & Gestão (.NET 10)

O **YouLink** é um aplicativo para **Android e Windows** integrado a uma **plataforma web pública (`ClothingStore.Web`)** com **Portal Central (`/`)**, **vitrines multi-loja (`/loja/{slug}`)** instaláveis como **PWA**, integração com **WhatsApp**, **Pix EMV** e **Mercado Pago**, controle de estoque visual por variante e **publicação online em 1 clique**.

---

## Baixar o App Pronto (Windows e Android)

- 👉 **[Baixar a Última Versão (Latest Release)](https://github.com/giovannipieronisleal-pixel/yourlink/releases/latest)** (`YouLink-Setup-x64.exe`, `YouLink-Windows-x64.zip` e `YouLink-Android.apk`)
- 👉 **[Ver Aba Releases](https://github.com/giovannipieronisleal-pixel/yourlink/releases)** | **[GitHub Actions (Builds)](https://github.com/giovannipieronisleal-pixel/yourlink/actions)**

### Instalação e Atualização Automática no App
- **Windows — Instalador (`YouLink-Setup-x64.exe` · Recomendado):** executável único que instala o aplicativo no Windows, cria o atalho **YouLink** na Área de Trabalho e Menu Iniciar e não exige .NET pré-instalado.
- **Windows — Portável (`YouLink-Windows-x64.zip`):** pacote com a pasta `YouLink/` contendo `YouLink.exe` e dependências isoladas na subpasta `App/`. Extraia tudo antes de executar `YouLink.exe`.
- **Android (`YouLink-Android.apk`):** ao instalar fora da Play Store, habilite *"Instalar apps desconhecidos"*.
- **Portal Web (`/`):** quando publicado com `YOULINK_PORTAL_HOME=true`, a página inicial do servidor web também centraliza os links diretos de download para Android e Windows.
- **Atualização automática integrada:** ao abrir ou retomar o app, o YouLink verifica o release mais recente no GitHub (`AppUpdateCatalog` + fallback por redirect) e exibe o aviso para atualizar direto no app preservando os dados locais (`App_Data`).

---

## Publicar Sua Loja Online em 1 Clique (Direto no App)

Em **Loja & Visual**, o lojista coloca o catálogo no ar em segundos sem terminal, DNS manual nem criação de servidor próprio:

1. **Modo Principal — Servidor Central YouLink (1 Clique):** clique em **🚀 Publicar Minha Loja Online em 1 Clique** no assistente (`CloudPublishWizard`). O app sincroniza produtos, fotos, estoque e identidade visual no servidor central e ativa imediatamente sua vitrine pública em **`/loja/{slug}`**, o link do **WhatsApp** e o **QR Code**.
2. **Modo Opcional / Avançado — Meu Próprio Servidor no Render:** caso prefira um servidor dedicado próprio, expanda **⚙️ Avançado: usar meu próprio servidor no Render (Passo a Passo)** para abrir o Blueprint (`https://dashboard.render.com/blueprint/new?repo=https://github.com/giovannipieronisleal-pixel/yourlink`), informar a URL e a chave `YOULINK_SYNC_SECRET` e ativar sua instância isolada.

---

## Principais Recursos

### Gestão de Loja, Categorias & Estoque Visual
- **Início limpo sem produtos de exemplo:** crie categorias e cadastre produtos com fotos, variações (cor/tamanho/opção) e SKU.
- **Estoque com identificação por foto (`/estoque`):** os cards de movimentação rápida exibem a foto do produto para conferência visual antes de tocar em **−1 Retirar**, **+1 Entrar**, **+5 Repor**, **+10 Lote** ou **✎ Mais**, além de prévia fotográfica na movimentação personalizada e miniatura no histórico de entradas e saídas.
- **QR Code & WhatsApp prontos:** link direto `https://wa.me/...` e QR Code gerados automaticamente com o endereço oficial da loja.

### Planos, Mercado Pago & Vendas via Pix
Todos os níveis incluem endereço da loja (`/loja/{slug}`), HTTPS, PWA e publicação online:

- **Plano Básico (`Basico`):** visual *Minimalista Luxo* + vitrine pública + pedidos direto no WhatsApp.
- **Meia Versão (`Intermediario`):** **5 versões de site pré-prontas**, painel financeiro **Vendas & Quanto Vendeu** (`/vendas`) e vendas diretas pelo app/site via **Pix**.
- **Premium Sob Medida (`Premium`):** **Estúdio Visual completo** (cores, tipografia, cantos, densidade e grade personalizáveis), painel `/vendas`, vendas via Pix e suporte a domínio próprio.
- **Mercado Pago Integrado:** conexão rápida de conta via **OAuth 1-clique** (`MercadoPagoPixService`, `PlatformMercadoPagoTokenStore`) para lojistas receberem com split/credenciais próprias, além de checkout de assinatura de planos via **Pix** ou **Checkout Pro** (`PlatformSubscriptionService`) com confirmação automática por webhook/polling e **Gift Card** (`/planos`).

### Pix EMV, Raio de Entrega, Correios & PWA
- **Pix Copia e Cola:** payload padrão BACEN EMV (`CRC-16/CCITT-FALSE`) via `PixPayloadBuilder`.
- **Frete e Área de Entrega:** filtro por **Todo o Brasil**, **Mesmo Estado (UF)**, **Mesma Cidade** ou **Raio em km** a partir do CEP de origem, com cálculo **PAC / SEDEX** (`BrazilShippingCalculator`) e gestão de pedidos (`/pedidos`).
- **Vitrine Instalável (PWA):** manifests dinâmicos em `/manifest.webmanifest` e `/loja/{storeSlug}/manifest.webmanifest` (`PwaManifestBuilder`) para os clientes adicionarem a loja à tela inicial do celular.

### Domínios Gratuitos & Verificação Google OAuth
- **Buscador de domínios gratuitos (`FreeDomainFinder`):** consulta DNS ao vivo (`dns.google` com fallback `cloudflare-dns.com`) para `*.duckdns.org`, `*.dpdns.org`, `*.pages.dev`, `*.workers.dev` e `*.fly.dev`, com suporte a atualização DuckDNS.
- **Verificação OAuth Google (`GoogleAccountTierVerifier`):** leitura opcional da cota do Google Drive (`drive/v3/about`) ou domínio Google Workspace em `/configuracoes-loja` para liberar selo Pro e desconto mensal.

---

## Estrutura da Solução (`appmaui.blazor.slnx`)

| Projeto | Caminho | Responsabilidade |
| :--- | :--- | :--- |
| **YouLink App** | [appmaui.blazor.csproj](appmaui.blazor.csproj) | Aplicativo `.NET MAUI Blazor Hybrid` para Windows x64 e Android (catálogo, estoque visual, equipe, pedidos, planos, estúdio visual, atualização in-app e publicação em 1 clique). |
| **ClothingStore.Web** | [ClothingStore.Web.csproj](src/ClothingStore.Web/ClothingStore.Web.csproj) | Portal Central (`/`), downloads do app e vitrines públicas multi-loja PWA (`/loja/{slug}`, `/loja/{slug}/catalogo`, `/loja/{slug}/produto/{slug}`) em Blazor Web App com [Dockerfile](src/ClothingStore.Web/Dockerfile). |
| **ClothingStore.Api** | [ClothingStore.Api.csproj](src/ClothingStore.Api/ClothingStore.Api.csproj) | ASP.NET Core Web API multi-tenant com autenticação JWT + 2FA, sincronização segura, webhooks e endpoints públicos/administrativos ([Dockerfile](src/ClothingStore.Api/Dockerfile)). |
| **ClothingStore.Application** | [ClothingStore.Application.csproj](src/ClothingStore.Application/ClothingStore.Application.csproj) | Casos de uso (`AuthService`, `CatalogService`, `ProductManagementService`, `StockManagementService`, `EmployeeAndSettingsService`, `CloudSyncStateService`, `OrderAndShippingService`, `MercadoPagoPixService`, `MultiTenantGatewayService`, `PlatformSubscriptionService`, `StoreDesignMapper`). |
| **ClothingStore.Domain** | [ClothingStore.Domain.csproj](src/ClothingStore.Domain/ClothingStore.Domain.csproj) | Entidades e regras de domínio (`User`, `Employee`, `Category`, `Product`, `ProductImage`, `ProductVariant`, `StockMovement`, `StoreSettings`, `StoreOrder`, `PlanActivationCode`, `PlatformSubscriptionOrder`). |
| **ClothingStore.Infrastructure** | [ClothingStore.Infrastructure.csproj](src/ClothingStore.Infrastructure/ClothingStore.Infrastructure.csproj) | Persistência transacional (`ClothingStoreRepository`), gerenciador multi-loja (`MultiTenantStoreManager`), espelhamento em nuvem PostgreSQL/Supabase (`CloudAppDataMirror`, `SupabasePostgresClient`), criptografia OAuth (`TokenProtectionHelper`), segurança PBKDF2/JWT e armazenamento de imagens (`SupabaseImageStorageService`). |
| **ClothingStore.Shared** | [ClothingStore.Shared.csproj](src/ClothingStore.Shared/ClothingStore.Shared.csproj) | DTOs, clientes HTTP (`CatalogApiClient`, `AdminApiClient`), `AppUpdateCatalog`, `PixPayloadBuilder`, `BrazilShippingCalculator`, `FreeDomainFinder`, `GoogleAccountTierVerifier`, `PwaManifestBuilder`, `RenderProvisioningHelper` e `WhatsAppLinkBuilder`. |
| **ClothingStore.UnitTests** | [ClothingStore.UnitTests.csproj](tests/ClothingStore.UnitTests/ClothingStore.UnitTests.csproj) | Suíte com **154 testes automatizados** cobrindo domínio, segurança/2FA, multi-tenant, Mercado Pago (OAuth/Pix/Checkout Pro/assinaturas), portal web/PWA, publicação 1-clique, auto-update, layout/responsividade e validações Windows/NTFS. |

---

## Como Compilar e Testar Localmente

### 1. API (`http://localhost:5180`) e Vitrine Web
```powershell
dotnet run --project src/ClothingStore.Api/ClothingStore.Api.csproj
dotnet run --project src/ClothingStore.Web/ClothingStore.Web.csproj
```

### 2. Gerar Pacotes Windows x64 e Android APK
```powershell
# Windows x64 Self-Contained (unpackaged)
dotnet publish appmaui.blazor.csproj -f net10.0-windows10.0.19041.0 -c Release -p:WindowsPackageType=None -o dist/YouLink-Windows -m:1

# Android APK
dotnet publish appmaui.blazor.csproj -f net10.0-android -c Release -p:AndroidPackageFormat=apk
```

### 3. Executar a Suíte de Testes (154 testes)
```powershell
dotnet run --project tests/ClothingStore.UnitTests/ClothingStore.UnitTests.csproj -c Release
```

---

## Persistência em Nuvem (Neon PostgreSQL / Supabase) & Deploy

1. **Banco PostgreSQL (Recomendado: Neon.tech):** configure `YOULINK_DATABASE_URL` (ou `DATABASE_URL`) para que o servidor central persista automaticamente lojas, catálogo, contas e tokens criptografados (`YOULINK_TOKEN_ENCRYPTION_KEY`) entre redeploys no Render (`render.yaml`).
2. **Supabase (Banco / Storage opcional):** caso utilize Supabase Storage, execute [supabase_schema.sql](database/supabase_schema.sql) no **SQL Editor** do projeto Supabase e preencha `SUPABASE_URL`, `SUPABASE_SERVICE_ROLE_KEY` e `SUPABASE_STORAGE_BUCKET`.
3. **Variáveis de ambiente e Deploy:** consulte [.env.example](.env.example), [render.yaml](render.yaml) (deploy Web no Render) e [deploy-google-cloud.ps1](deploy-google-cloud.ps1) (Google Cloud Run).
