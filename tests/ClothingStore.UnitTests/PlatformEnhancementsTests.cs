using System.Net;
using System.Text;
using System.Text.Json;
using ClothingStore.Application.Services;
using ClothingStore.Infrastructure.Persistence;
using ClothingStore.Shared.DTOs;
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
            StoreName: "Atelier Flavia",
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

        // 4. Gemini AI Studio UI in Client (zero API key setup for users) & Central Key in AdminApp
        var productEditor = File.ReadAllText(Path.Combine(root, "Components", "Pages", "ProductEditor.razor"));
        Assert.IsTrue(productEditor.Contains("GenerateAiMarketingKitAsync", StringComparison.Ordinal));

        var productsPage = File.ReadAllText(Path.Combine(root, "Components", "Pages", "Products.razor"));
        Assert.IsTrue(productsPage.Contains("GenerateAiDescriptionAsync", StringComparison.Ordinal));

        var storeSettings = File.ReadAllText(Path.Combine(root, "Components", "Pages", "StoreSettingsPage.razor"));
        Assert.IsFalse(storeSettings.Contains("SaveGeminiKeyFromSettingsAsync", StringComparison.Ordinal),
            "O app do usuário comum não deve pedir chave API do Gemini.");
        Assert.IsFalse(storeSettings.Contains("aistudio.google.com/apikey", StringComparison.Ordinal),
            "O app do usuário comum não deve exibir links técnicos de API key.");

        var ownerSettings = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.AdminApp", "Components", "Pages", "OwnerSettingsPage.razor"));
        Assert.IsTrue(ownerSettings.Contains("SaveServerGeminiKeyAsync", StringComparison.Ordinal),
            "A configuração central da chave Gemini deve ficar no YouLink Admin do dono.");

        // 5. Merchant Manual synced to Google Docs
        var manualPath = Path.Combine(root, "docs", "Manual-Oficial-Lojista-YouLink.md");
        Assert.IsTrue(File.Exists(manualPath));
        var manual = File.ReadAllText(manualPath);
        Assert.IsTrue(manual.Contains("Estúdio IA Gemini", StringComparison.Ordinal));
        Assert.IsTrue(manual.Contains("Cloudflare Turnstile", StringComparison.Ordinal));
    }
}
