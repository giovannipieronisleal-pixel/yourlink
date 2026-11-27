using System.Net;
using System.Text;
using System.Text.Json;
using ClothingStore.Application.Services;
using ClothingStore.Infrastructure.Persistence;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClothingStore.UnitTests;

[TestClass]
public sealed class PlatformEnhancementsTests
{
    private sealed class CapturingHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await handler(request);
        }
    }

    private static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "appmaui.blazor.csproj")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Não foi possível localizar a raiz do repositório.");
    }

    [TestMethod]
    public async Task CloudflareTurnstileService_ShouldAllowWhenUnconfigured_AndEnforceActionHostnameAndFailClosedWhenConfigured()
    {
        var unconfigured = new CloudflareTurnstileService(
            new HttpClient(new CapturingHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))),
            () => (null, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        Assert.IsFalse(unconfigured.IsConfigured);
        var (okUnconf, _) = await unconfigured.VerifyTokenAsync(null, "checkout_pix");
        Assert.IsTrue(okUnconf);

        var okHandler = new CapturingHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"success":true,"action":"checkout_pix","hostname":"youlink-loja.onrender.com"}""",
                Encoding.UTF8,
                "application/json")
        }));
        var configured = new CloudflareTurnstileService(
            new HttpClient(okHandler),
            () => ("site-key-123", "secret-key-xyz", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "youlink-loja.onrender.com" }));

        Assert.IsTrue(configured.IsConfigured);
        Assert.AreEqual("site-key-123", configured.GetPublicConfig().TurnstileSiteKey);

        // Missing token fails closed
        var (missingOk, _) = await configured.VerifyTokenAsync("", "checkout_pix");
        Assert.IsFalse(missingOk);

        // Matching action & hostname succeeds
        var (validOk, validErr) = await configured.VerifyTokenAsync("valid-token-abc", "checkout_pix", "203.0.113.10");
        Assert.IsTrue(validOk, validErr);
        Assert.AreEqual("https://challenges.cloudflare.com/turnstile/v0/siteverify", okHandler.LastRequest?.RequestUri?.ToString());

        // Mismatched action rejected
        var (wrongActionOk, _) = await configured.VerifyTokenAsync("valid-token-abc", "signup");
        Assert.IsFalse(wrongActionOk);

        // Unauthorized hostname rejected
        var badHostHandler = new CapturingHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"success":true,"action":"checkout_pix","hostname":"evil.example.com"}""",
                Encoding.UTF8,
                "application/json")
        }));
        var badHostSvc = new CloudflareTurnstileService(
            new HttpClient(badHostHandler),
            () => ("site-key-123", "secret-key-xyz", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "youlink-loja.onrender.com" }));
        var (badHostOk, _) = await badHostSvc.VerifyTokenAsync("valid-token-abc", "checkout_pix");
        Assert.IsFalse(badHostOk);

        // Network/500 failure fails closed
        var failingSvc = new CloudflareTurnstileService(
            new HttpClient(new CapturingHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))),
            () => ("site-key-123", "secret-key-xyz", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "youlink-loja.onrender.com" }));
        var (failClosedOk, _) = await failingSvc.VerifyTokenAsync("valid-token-abc", "checkout_pix");
        Assert.IsFalse(failClosedOk);
    }

    [TestMethod]
    public async Task GeminiProductAssistantService_ShouldCallV1InteractionsWithGemini4Hydrogen_AndFallbackGracefullyOffline()
    {
        var mockJson = """
        {
          "steps": [
            {
              "type": "model_output",
              "content": [
                {
                  "text": "{\"suggestedTitle\":\"Vestido Midi Seda Champagne\",\"persuasiveDescription\":\"Elegância atemporal em seda fluida para ocasiões especiais. Envio rápido com confirmação no Pix.\",\"whatsappPromoMessage\":\"✨ Vestido Midi Seda Champagne disponível! Peça agora no WhatsApp.\",\"instagramCaption\":\"Sofisticação em cada detalhe. #moda #luxo #youlink\",\"seoTags\":[\"vestido midi\",\"seda champagne\",\"moda festa\"],\"bannerHeadline\":\"Seda Champagne em Edição Limitada\"}"
                }
              ]
            }
          ]
        }
        """;

        var handler = new CapturingHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(mockJson, Encoding.UTF8, "application/json")
        }));

        var liveService = new GeminiProductAssistantService(new HttpClient(handler), () => "AIza-test-key");
        var live = await liveService.GenerateProductCopyAsync(new GenerateProductCopyRequest(
            ProductName: "Vestido Midi Seda",
            CategoryName: "Vestidos",
            Price: 349.90m,
            StoreName: "Atelier Modelo",
            Tone: "Luxo"));

        Assert.IsTrue(live.UsedLiveGemini);
        Assert.AreEqual("Vestido Midi Seda Champagne", live.SuggestedTitle);
        Assert.IsTrue(live.ProviderUsed.Contains(GeminiProductAssistantService.DefaultGeminiModel, StringComparison.Ordinal));
        Assert.AreEqual("https://generativelanguage.googleapis.com/v1/interactions", handler.LastRequest?.RequestUri?.ToString());
        Assert.IsTrue(handler.LastRequest!.Headers.Contains("x-goog-api-key"));
        Assert.IsTrue(handler.LastRequest.Headers.TryGetValues("Api-Revision", out var rev) && rev.Contains("2026-05-20"));
        Assert.IsTrue(handler.LastBody!.Contains("gemini-4-hydrogen", StringComparison.Ordinal));

        // Offline fallback when no API key is configured
        var offlineService = new GeminiProductAssistantService(new HttpClient(handler), () => null);
        var offline = await offlineService.GenerateProductCopyAsync(new GenerateProductCopyRequest(
            ProductName: "Fone Bluetooth Pro",
            CategoryName: "Eletrônicos",
            Price: 159.90m,
            StoreName: "Loja do Gio",
            Tone: "Promocional"));

        Assert.IsFalse(offline.UsedLiveGemini);
        Assert.IsTrue(offline.PersuasiveDescription.Contains("Fone Bluetooth Pro", StringComparison.Ordinal));
        Assert.IsTrue(offline.WhatsappPromoMessage.Contains("Loja do Gio", StringComparison.Ordinal));
        Assert.IsTrue(offline.SeoTags.Count >= 3);
    }

    [TestMethod]
    public async Task RemoteConfigAndTelemetryStore_ShouldPersistFlags_SanitizeSecrets_AndEnforceRingBuffer()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink_remote_ops_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new RemoteConfigAndTelemetryStore(tempDir);
            var saved = await store.SaveRemoteConfigAsync(new RemoteFeatureConfigDto(
                AiStudioEnabled: true,
                TurnstileCheckoutEnabled: true,
                CrashTelemetryEnabled: true,
                GlobalAnnouncementBanner: "Nova versão com Estúdio IA Gemini!",
                GlobalAnnouncementKind: "success",
                EdgeProxyBaseUrl: "https://youlink-edge-accelerator.youlink-edge.workers.dev"));

            Assert.AreEqual("Nova versão com Estúdio IA Gemini!", saved.GlobalAnnouncementBanner);
            Assert.AreEqual("success", saved.GlobalAnnouncementKind);

            var loaded = await store.GetRemoteConfigAsync();
            Assert.AreEqual(saved.GlobalAnnouncementBanner, loaded.GlobalAnnouncementBanner);
            Assert.AreEqual("https://youlink-edge-accelerator.youlink-edge.workers.dev", loaded.EdgeProxyBaseUrl);

            var entry = await store.RecordClientErrorAsync(new ClientCrashReportRequest(
                AppName: "YouLink",
                AppVersion: "1.0.50",
                Platform: "Windows",
                Context: "SyncTest",
                ErrorType: "HttpRequestException",
                ErrorMessage: "Header Bearer super-secret-jwt and x-goog-api-key leaked",
                StackTrace: "at MauiAdminGateway.Test()",
                AccountHint: "loja-modelo"));

            Assert.IsFalse(entry.ErrorMessage.Contains("Bearer super-secret-jwt", StringComparison.Ordinal));
            Assert.IsTrue(entry.ErrorMessage.Contains("[REDACTED]", StringComparison.Ordinal));

            var recent = await store.GetRecentErrorsAsync(10);
            Assert.AreEqual(1, recent.Count);
            Assert.AreEqual("loja-modelo", recent[0].AccountHint);

            // Chave central de IA fica apenas no servidor (nunca exposta no RemoteConfig público)
            var savedAiStatus = await store.SaveServerGeminiApiKeyAsync("AIza-central-owner-key");
            Assert.IsTrue(savedAiStatus.Configured);
            Assert.AreEqual("AIza-central-owner-key", store.GetServerGeminiApiKey());

            var publicRemote = await store.GetRemoteConfigAsync();
            var publicJson = JsonSerializer.Serialize(publicRemote);
            Assert.IsFalse(publicJson.Contains("AIza-central-owner-key", StringComparison.Ordinal),
                "A chave Gemini do servidor nunca deve vazar no RemoteConfig público.");

            var clearedAiStatus = await store.SaveServerGeminiApiKeyAsync(null);
            Assert.IsFalse(clearedAiStatus.Source.Contains("Painel Admin", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public void CloudflareWorker_FirebaseTemplate_LcpWebPerf_AiUi_And_Manual_ShouldBeProperlyConfigured()
    {
        var root = ResolveRepoRoot();

        // 1. Cloudflare Edge Worker & keep-alive cron
        var workerConfig = File.ReadAllText(Path.Combine(root, "infra", "cloudflare-edge-worker", "wrangler.jsonc"));
        var workerScript = File.ReadAllText(Path.Combine(root, "infra", "cloudflare-edge-worker", "src", "index.js"));
        Assert.IsTrue(workerConfig.Contains("*/10 * * * *", StringComparison.Ordinal));
        Assert.IsTrue(workerConfig.Contains("youlink-loja.onrender.com", StringComparison.Ordinal));
        Assert.IsTrue(workerScript.Contains("/api/cloud/", StringComparison.Ordinal));
        Assert.IsTrue(workerScript.Contains("scheduled(", StringComparison.Ordinal));

        // 2. Firebase Remote Config template
        var fbConfig = File.ReadAllText(Path.Combine(root, "firebase", "remote_config.json"));
        using var fbDoc = JsonDocument.Parse(fbConfig);
        Assert.IsTrue(fbDoc.RootElement.TryGetProperty("parameters", out var fbParams));
        Assert.IsTrue(fbParams.TryGetProperty("ai_studio_enabled", out _));
        Assert.IsTrue(fbParams.TryGetProperty("edge_proxy_base_url", out _));

        // 3. LCP & Web Perf in ClothingStore.Web
        var productCard = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Web", "Components", "Shared", "ProductCard.razor"));
        Assert.IsTrue(productCard.Contains("IsAboveFold", StringComparison.Ordinal));
        Assert.IsTrue(productCard.Contains("fetchpriority=", StringComparison.Ordinal));
        Assert.IsTrue(productCard.Contains("decoding=", StringComparison.Ordinal));

        var productDetail = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Web", "Components", "Pages", "ProductDetail.razor"));
        Assert.IsTrue(productDetail.Contains("fetchpriority=\"high\"", StringComparison.Ordinal));
        Assert.IsTrue(productDetail.Contains("ylk-turnstile-checkout", StringComparison.Ordinal));

        // 4. Clean Product & Store Settings UI (no AI key or AI Studio buttons exposed)
        var productEditor = File.ReadAllText(Path.Combine(root, "Components", "Pages", "ProductEditor.razor"));
        Assert.IsFalse(productEditor.Contains("GenerateAiMarketingKitAsync", StringComparison.Ordinal),
            "O editor de produto não deve exibir o bloco Estúdio IA Gemini.");

        var productsPage = File.ReadAllText(Path.Combine(root, "Components", "Pages", "Products.razor"));
        Assert.IsFalse(productsPage.Contains("GenerateAiDescriptionAsync", StringComparison.Ordinal),
            "A tela de produtos não deve exibir botões de geração com IA.");

        var storeSettings = File.ReadAllText(Path.Combine(root, "Components", "Pages", "StoreSettingsPage.razor"));
        Assert.IsFalse(storeSettings.Contains("SaveGeminiKeyFromSettingsAsync", StringComparison.Ordinal),
            "O app do usuário comum não deve pedir chave API do Gemini.");
        Assert.IsFalse(storeSettings.Contains("aistudio.google.com/apikey", StringComparison.Ordinal),
            "O app do usuário comum não deve exibir links técnicos de API key.");
        Assert.IsFalse(storeSettings.Contains("SearchFreeDomainsInSettingsAsync", StringComparison.Ordinal),
            "O app do usuário comum não deve exibir buscador/verificador de domínios externos.");

        var plansPage = File.ReadAllText(Path.Combine(root, "Components", "Pages", "PlansPage.razor"));
        Assert.IsFalse(plansPage.Contains("SearchFreeDomainsAsync", StringComparison.Ordinal),
            "A tela de planos não deve exibir buscador de domínios gratuitos externos.");

        var loginPage = File.ReadAllText(Path.Combine(root, "Components", "Pages", "Login.razor"));
        Assert.IsFalse(loginPage.Contains("CheckDomainInSetupAsync", StringComparison.Ordinal),
            "O cadastro inicial não deve exibir verificador de domínio.");

        var checkoutPage = File.ReadAllText(Path.Combine(root, "Components", "Pages", "PlanCheckoutPage.razor"));
        Assert.IsFalse(checkoutPage.Contains("CheckCheckoutDomainAsync", StringComparison.Ordinal),
            "O checkout de planos não deve exibir verificador de domínio externo.");
        Assert.IsTrue(checkoutPage.Contains("CheckRenderLinkInCheckoutAsync", StringComparison.Ordinal),
            "O checkout deve permitir verificar a disponibilidade do endereço no servidor Render.");
        Assert.IsTrue(storeSettings.Contains("CheckRenderStoreLinkAsync", StringComparison.Ordinal),
            "As configurações da loja devem permitir verificar a disponibilidade do link no servidor Render.");
        Assert.IsTrue(storeSettings.Contains("_activeSettingsTab", StringComparison.Ordinal),
            "As configurações da loja devem separar os blocos em abas claras (_activeSettingsTab) com opcionais recolhidos.");
        Assert.IsTrue(storeSettings.Contains("SendWhatsappVerificationCodeAsync", StringComparison.Ordinal),
            "As configurações da loja devem oferecer autenticação gratuita do número do WhatsApp.");
        Assert.IsTrue(storeSettings.Contains("ConfirmWhatsappVerificationCodeAsync", StringComparison.Ordinal),
            "As configurações da loja devem confirmar o código de 4 dígitos do WhatsApp.");
        Assert.IsFalse(CatalogFeatureFlags.PhoneFeaturesEnabled,
            "As funções de telefone/WhatsApp devem permanecer desativadas.");
        Assert.IsTrue(CatalogFeatureFlags.SalesAndOrdersEnabled,
            "As funções de vendas/pedidos via Pix e Mercado Pago devem estar ativas com chat interno.");

        var validWa = WhatsAppLinkBuilder.ValidateBrazilianWhatsAppNumber("(41) 98877-6655");
        Assert.IsTrue(validWa.IsValid);
        Assert.AreEqual("5541988776655", validWa.NormalizedNumber);
        Assert.AreEqual("(41) 98877-6655", validWa.FormattedDisplay);

        var invalidDddWa = WhatsAppLinkBuilder.ValidateBrazilianWhatsAppNumber("00988776655");
        Assert.IsFalse(invalidDddWa.IsValid);

        var dummyWa = WhatsAppLinkBuilder.ValidateBrazilianWhatsAppNumber("11999999999");
        Assert.IsFalse(dummyWa.IsValid);

        var otpCode = WhatsAppLinkBuilder.GenerateWhatsAppVerificationCode();
        Assert.AreEqual(4, otpCode.Length);
        var waVerifyLink = WhatsAppLinkBuilder.BuildWhatsAppVerificationLink("41988776655", otpCode, "Loja Modelo");
        Assert.IsTrue(waVerifyLink.StartsWith("https://wa.me/5541988776655?text=", StringComparison.Ordinal));
        Assert.IsTrue(loginPage.Contains("CheckRenderLinkInSetupAsync", StringComparison.Ordinal),
            "O cadastro inicial da loja deve verificar se o link no servidor Render está disponível.");

        var webProgram = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Web", "Program.cs"));
        Assert.IsTrue(webProgram.Contains("/api/cloud/check-render-store-link", StringComparison.Ordinal),
            "O servidor Render (ClothingStore.Web) deve expor /api/cloud/check-render-store-link.");

        var ownerSettingsPath = Path.Combine(root, "src", "ClothingStore.AdminApp", "Components", "Pages", "OwnerSettingsPage.razor");
        if (File.Exists(ownerSettingsPath))
        {
            var ownerSettings = File.ReadAllText(ownerSettingsPath);
            Assert.IsFalse(ownerSettings.Contains("SaveServerGeminiKeyAsync", StringComparison.Ordinal),
                "A configuração de chave IA Gemini deve estar removida do painel do dono.");
        }

        // 5. Merchant Manual synced to Google Docs
        var manualPath = Path.Combine(root, "docs", "Manual-Oficial-Lojista-YouLink.md");
        Assert.IsTrue(File.Exists(manualPath));
        var manual = File.ReadAllText(manualPath);
        Assert.IsTrue(manual.Contains("Estúdio IA Gemini", StringComparison.Ordinal));
        Assert.IsTrue(manual.Contains("Cloudflare Turnstile", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ProductVariantMerge_SupportWhatsappCode_And_CloudPullStore_ShouldWorkEndToEnd()
    {
        // 1. Support-issued WhatsApp verification code (deterministic 4 digits, request goes to Support number without leaking code)
        var code1 = WhatsAppLinkBuilder.GenerateSupportWhatsAppVerificationCode("(41) 98877-6655");
        var code2 = WhatsAppLinkBuilder.GenerateSupportWhatsAppVerificationCode("5541988776655");
        Assert.AreEqual(4, code1.Length);
        Assert.AreEqual(code1, code2);

        var reqLink = WhatsAppLinkBuilder.BuildSupportVerificationRequestLink("5511977776666", "41988776655", "Loja Modelo");
        Assert.IsNotNull(reqLink);
        Assert.IsTrue(reqLink!.StartsWith("https://wa.me/5511977776666?text=", StringComparison.Ordinal));
        Assert.IsFalse(reqLink.Contains(code1, StringComparison.Ordinal), "A solicitação ao suporte não deve vazar o código para o usuário.");

        var replyLink = WhatsAppLinkBuilder.BuildSupportVerificationReplyLink("41988776655", code1, "Loja Modelo");
        Assert.IsNotNull(replyLink);
        Assert.IsTrue(replyLink!.StartsWith("https://wa.me/5541988776655?text=", StringComparison.Ordinal));
        Assert.IsTrue(replyLink.Contains(code1, StringComparison.Ordinal));

        // 2. Product creation with multiple variants + same-name merge (no duplicate product!)
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink_variant_merge_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var hasher = new ClothingStore.Infrastructure.Security.Pbkdf2PasswordHasher();
            var repo = new ClothingStoreRepository(hasher, Path.Combine(tempDir, "db.json"));
            var storage = new ClothingStore.Infrastructure.Storage.SupabaseImageStorageService(
                new HttpClient(),
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                Path.Combine(tempDir, "wwwroot"));
            var auth = new AuthService(repo, hasher, new ClothingStore.Infrastructure.Security.JwtTokenService(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));
            var reg = await auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("Lojista Modelo", "lojista@modelo.com", "Senha@123", "lojista"));

            var productService = new ProductManagementService(repo, storage);
            var cat = await productService.CreateCategoryAsync(new SaveCategoryRequest { Name = "Camisas" });

            var created = await productService.CreateProductAsync(new SaveProductRequest
            {
                CategoryId = cat.Id,
                Name = "Camisa social malha fina",
                BasePrice = 60.00m,
                InitialVariants =
                [
                    new SaveProductVariantRequest { Color = "Preta", Size = "M", InitialStockQuantity = 5 },
                    new SaveProductVariantRequest { Color = "Preta", Size = "GG", InitialStockQuantity = 2 }
                ]
            }, reg.Employee.EmployeeId);

            Assert.AreEqual(2, created.Variants.Count);
            Assert.AreEqual(7, created.TotalStockQuantity);

            // Adding another model/color with same product name must MERGE into the same product
            var merged = await productService.CreateProductAsync(new SaveProductRequest
            {
                CategoryId = cat.Id,
                Name = "Camisa social malha fina",
                BasePrice = 60.00m,
                InitialVariants =
                [
                    new SaveProductVariantRequest { Color = "Branca", Size = "M", InitialStockQuantity = 4 }
                ]
            }, reg.Employee.EmployeeId);

            Assert.AreEqual(created.Id, merged.Id, "Não deve criar produto duplicado ao adicionar nova variante com o mesmo nome.");
            Assert.AreEqual(3, merged.Variants.Count);
            Assert.AreEqual(11, merged.TotalStockQuantity);

            var allProducts = await productService.GetAdminProductsAsync();
            Assert.AreEqual(1, allProducts.Count);

            // 3. Cross-device Cloud Pull Store endpoint & export/import roundtrip
            var settingsSvc = new EmployeeAndSettingsService(repo, hasher, storage);
            var syncSvc = new CloudSyncStateService(repo, settingsSvc);
            var exported = await syncSvc.ExportSyncPayloadAsync();
            Assert.AreEqual(1, exported.CatalogSnapshot!.Count);
            Assert.AreEqual(3, exported.CatalogSnapshot[0].Variants.Count);

            var secondDeviceRepo = new ClothingStoreRepository(hasher, Path.Combine(tempDir, "device2.json"));
            var secondDeviceSettingsSvc = new EmployeeAndSettingsService(secondDeviceRepo, hasher, storage);
            var secondDeviceSyncSvc = new CloudSyncStateService(secondDeviceRepo, secondDeviceSettingsSvc);
            var applied = await secondDeviceSyncSvc.ApplySyncPayloadAsync(exported);
            Assert.AreEqual(1, applied.ProductsCount);

            var pulledProducts = await secondDeviceRepo.GetProductsAsync();
            Assert.AreEqual(1, pulledProducts.Count);
            Assert.AreEqual(3, pulledProducts[0].Variants.Count);

            var root = ResolveRepoRoot();
            var webProgram = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Web", "Program.cs"));
            Assert.IsTrue(webProgram.Contains("/api/cloud/pull-store", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
