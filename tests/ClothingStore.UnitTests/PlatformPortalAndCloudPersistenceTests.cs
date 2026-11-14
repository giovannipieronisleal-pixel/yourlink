using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClothingStore.Application.Interfaces;
using ClothingStore.Application.Services;
using ClothingStore.Domain.Entities;
using ClothingStore.Infrastructure.Persistence;
using ClothingStore.Infrastructure.Security;
using ClothingStore.Infrastructure.Storage;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClothingStore.UnitTests;

[TestClass]
public sealed class PlatformPortalAndCloudPersistenceTests
{
    private sealed class InMemoryCloudStateBackend : ICloudStateBackend
    {
        public ConcurrentDictionary<string, string> Store { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string ProviderName => "InMemoryMock";

        public Task<IReadOnlyDictionary<string, string>> LoadAllSnapshotsAsync(CancellationToken ct = default)
        {
            IReadOnlyDictionary<string, string> copy = new Dictionary<string, string>(Store, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(copy);
        }

        public Task UpsertSnapshotAsync(string stateKey, string payload, CancellationToken ct = default)
        {
            Store[stateKey] = payload;
            return Task.CompletedTask;
        }

        public Task DeleteSnapshotAsync(string stateKey, CancellationToken ct = default)
        {
            Store.TryRemove(stateKey, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class MockSupabaseStorageHandler : HttpMessageHandler
    {
        public bool BucketExists { get; set; }
        public int BucketCreateCalls { get; private set; }
        public List<string> ObservedUrls { get; } = [];
        public Dictionary<string, string> Objects { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            ObservedUrls.Add($"{request.Method} {url}");

            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            Assert.AreEqual("srv-role-secret-123", request.Headers.Authorization?.Parameter);
            Assert.IsTrue(request.Headers.TryGetValues("apikey", out var apiKeys) && apiKeys.Contains("srv-role-secret-123"));

            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);

            if (request.Method == HttpMethod.Get && url.Contains("/storage/v1/bucket/youlink-state", StringComparison.OrdinalIgnoreCase))
            {
                return BucketExists
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"youlink-state\"}", Encoding.UTF8, "application/json") }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.Method == HttpMethod.Post && url.EndsWith("/storage/v1/bucket", StringComparison.OrdinalIgnoreCase))
            {
                BucketCreateCalls++;
                Assert.IsTrue(body.Contains("\"public\":false", StringComparison.OrdinalIgnoreCase),
                    "O bucket youlink-state precisa ser criado como privado (public: false).");
                BucketExists = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"name\":\"youlink-state\"}", Encoding.UTF8, "application/json")
                };
            }

            if (request.Method == HttpMethod.Post && url.Contains("/storage/v1/object/list/youlink-state", StringComparison.OrdinalIgnoreCase))
            {
                using var reqDoc = JsonDocument.Parse(body);
                var prefix = reqDoc.RootElement.TryGetProperty("prefix", out var p) ? (p.GetString() ?? string.Empty) : string.Empty;

                if (string.IsNullOrEmpty(prefix))
                {
                    var items = new List<object>();
                    if (Objects.ContainsKey("clothingstore_db.json"))
                        items.Add(new { name = "clothingstore_db.json", id = "obj-root-1" });
                    if (Objects.Keys.Any(k => k.StartsWith("tenants/", StringComparison.OrdinalIgnoreCase)))
                        items.Add(new { name = "tenants", id = (string?)null });

                    return JsonOk(items);
                }

                if (string.Equals(prefix, "tenants", StringComparison.OrdinalIgnoreCase))
                {
                    var tenantItems = Objects.Keys
                        .Where(k => k.StartsWith("tenants/", StringComparison.OrdinalIgnoreCase))
                        .Select(k => new { name = k["tenants/".Length..], id = $"obj-{k}" })
                        .ToList();
                    return JsonOk(tenantItems);
                }

                return JsonOk(Array.Empty<object>());
            }

            const string objectMarker = "/storage/v1/object/youlink-state/";
            var objIdx = url.IndexOf(objectMarker, StringComparison.OrdinalIgnoreCase);
            if (objIdx >= 0)
            {
                var key = Uri.UnescapeDataString(url[(objIdx + objectMarker.Length)..]);

                if (request.Method == HttpMethod.Post)
                {
                    Assert.IsTrue(request.Headers.TryGetValues("x-upsert", out var upsert) && upsert.Contains("true"));
                    Objects[key] = body;
                    return JsonOk(new { Key = $"youlink-state/{key}" });
                }

                if (request.Method == HttpMethod.Get)
                {
                    if (Objects.TryGetValue(key, out var content))
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                if (request.Method == HttpMethod.Delete)
                {
                    Objects.Remove(key);
                    return JsonOk(new { message = "deleted" });
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonOk(object payload) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
    }

    private sealed class MockOAuthAndPixHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("/oauth/token", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonOk(new
                {
                    access_token = "APP_USR-CLOUD-MIRROR-TOKEN-777",
                    refresh_token = "TG-CLOUD-MIRROR-REFRESH-777",
                    expires_in = 21600,
                    user_id = 123456L
                }));
            }

            if (url.Contains("/users/me", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonOk(new
                {
                    id = 123456L,
                    nickname = "YOULINK_MIRROR_TEST",
                    email = "owner@youlink.com.br"
                }));
            }

            if (url.Contains("/v1/payments", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonOk(new
                {
                    id = 99881122L,
                    status = "pending",
                    point_of_interaction = new
                    {
                        transaction_data = new
                        {
                            qr_code = "000201010212YOULINKPIXTEST",
                            qr_code_base64 = Convert.ToBase64String([1, 2, 3, 4]),
                            ticket_url = "https://mpago.la/test"
                        }
                    }
                }));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonOk(object payload) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
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

    private static int ReserveFreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static void CleanupDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
        }
    }

    [TestMethod]
    public void PostgresCloudStateBackend_NormalizePostgresConnectionString_ShouldSupportNeonAndLocalhost()
    {
        var neonUri = "postgresql://user%40neon:p%40ss%23word1@ep-cool-sky-123.us-east-2.aws.neon.tech/neondb?sslmode=require&channel_binding=require";
        var neonConn = PostgresCloudStateBackend.NormalizePostgresConnectionString(neonUri);

        Assert.IsTrue(neonConn.Contains("Host=ep-cool-sky-123.us-east-2.aws.neon.tech", StringComparison.OrdinalIgnoreCase), neonConn);
        Assert.IsTrue(neonConn.Contains("Database=neondb", StringComparison.OrdinalIgnoreCase), neonConn);
        Assert.IsTrue(neonConn.Contains("Username=user@neon", StringComparison.OrdinalIgnoreCase), neonConn);
        Assert.IsTrue(neonConn.Contains("Password=p@ss#word1", StringComparison.OrdinalIgnoreCase), neonConn);
        Assert.IsTrue(neonConn.Contains("SSL Mode=Require", StringComparison.OrdinalIgnoreCase), neonConn);

        var localUri = "postgres://postgres:secret@localhost:5433/youlink";
        var localConn = PostgresCloudStateBackend.NormalizePostgresConnectionString(localUri);
        Assert.IsTrue(localConn.Contains("Port=5433", StringComparison.OrdinalIgnoreCase), localConn);
        Assert.IsTrue(localConn.Contains("SSL Mode=Disable", StringComparison.OrdinalIgnoreCase), localConn);

        const string adoNet = "Host=127.0.0.1;Port=5432;Database=youlink;Username=postgres;Password=123";
        Assert.AreEqual(adoNet, PostgresCloudStateBackend.NormalizePostgresConnectionString(adoNet));
    }

    [TestMethod]
    public void CloudAppDataMirror_ShouldRejectPathTraversalAndUntrustedKeys()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ylk_traversal_{Guid.NewGuid():N}");
        using var mirror = new CloudAppDataMirror(root, new InMemoryCloudStateBackend());

        Assert.IsNull(mirror.TryNormalizeStateKey(Path.Combine(root, "..", "outside.json")));
        Assert.IsNull(mirror.TryNormalizeStateKey(Path.Combine(root, "email_outbox.txt")));
        Assert.IsNull(mirror.TryNormalizeStateKey(Path.Combine(root, "tenants", "..", "evil.json")));
        Assert.IsNull(mirror.TryNormalizeStateKey(Path.Combine(root, "tenants", "nested", "store.json")));
        Assert.IsNull(mirror.TryNormalizeStateKey(Path.Combine(root, "tenants", "bad name!.json")));

        Assert.AreEqual("clothingstore_db.json", mirror.TryNormalizeStateKey(Path.Combine(root, "clothingstore_db.json")));
        Assert.AreEqual("tenants/minha-loja.json", mirror.TryNormalizeStateKey(Path.Combine(root, "tenants", "Minha-Loja.json")));

        Assert.IsNull(mirror.ResolveSafeLocalPath("../evil.json"));
        Assert.IsNull(mirror.ResolveSafeLocalPath("tenants/../clothingstore_db.json"));
        Assert.IsNull(mirror.ResolveSafeLocalPath("tenants/sub/loja.json"));

        var validTenantPath = mirror.ResolveSafeLocalPath("tenants/minha-loja.json");
        Assert.IsNotNull(validTenantPath);
        Assert.IsTrue(validTenantPath!.EndsWith(Path.Combine("tenants", "minha-loja.json"), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task CloudAppDataMirror_FullRoundTrip_ShouldPersistAndRestoreRootTenantsAndOAuthAfterDiskWipe()
    {
        var previousEncKey = Environment.GetEnvironmentVariable("YOULINK_TOKEN_ENCRYPTION_KEY");
        var tempAppData = Path.Combine(Path.GetTempPath(), $"ylk_mirror_rt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempAppData);

        var cloudBackend = new InMemoryCloudStateBackend();
        CloudAppDataMirror? mirror = null;

        try
        {
            Environment.SetEnvironmentVariable("YOULINK_TOKEN_ENCRYPTION_KEY", null);

            mirror = new CloudAppDataMirror(tempAppData, cloudBackend);
            CloudAppDataMirrorRegistry.Register(tempAppData, mirror);

            var hasher = new Pbkdf2PasswordHasher();
            var dbPath = Path.Combine(tempAppData, "clothingstore_db.json");
            var repo = new ClothingStoreRepository(hasher, dbPath);

            // 1) Atualiza preços e configurações centrais (gera clothingstore_db.json + platform_owner_settings.json)
            var ownerSettings = await repo.GetStoreSettingsAsync();
            ownerSettings.StoreName = "YouLink Central Teste";
            ownerSettings.BasicPlanMonthlyPrice = 49.90m;
            ownerSettings.IntermediaryPlanMonthlyPrice = 89.90m;
            ownerSettings.PremiumPlanMonthlyPrice = 139.90m;
            ownerSettings.PicpayPixKey = "pix-central@youlink.com.br";
            await repo.UpdateStoreSettingsAsync(ownerSettings);

            // 2) Conecta OAuth da plataforma e cria pedido de plano
            var mpHandler = new MockOAuthAndPixHandler();
            var mpHttp = new HttpClient(mpHandler);
            var mpService = new MercadoPagoPixService(
                mpHttp,
                () => new MercadoPagoAppConfig("CLIENT-77", "SECRET-88", "WH-99"));
            var tokenStore = new PlatformMercadoPagoTokenStore(tempAppData);
            var storeManager = new MultiTenantStoreManager(repo, hasher, tempAppData);
            var storage = new SupabaseImageStorageService(new HttpClient(), new ConfigurationBuilder().Build(), Path.Combine(tempAppData, "wwwroot"));
            var gateway = new MultiTenantGatewayService(storeManager, mpService, mpHttp, hasher, storage, emailService: null, tokenStore);
            var subService = new PlatformSubscriptionService(repo, mpService, tokenStore);

            var oauthStart = gateway.StartPlatformOwnerMercadoPagoOAuth("https://youlink-loja.onrender.com/api/payments/mercadopago/oauth/callback");
            var oauthDone = await gateway.CompleteMercadoPagoOAuthCallbackAsync("CODE-123", oauthStart.StateToken, null, null);
            Assert.IsTrue(oauthDone.Success);

            var planOrder = await subService.CreatePlanOrderAsync(new CreatePlatformPlanOrderRequest(
                PlanTier: StoreSettings.PlanTierBasic,
                DurationMonths: 1,
                Amount: 49.90m,
                StoreName: "Loja Cliente Persistida",
                BuyerEmail: "lojista@persist.com",
                PaymentMethod: PlatformSubscriptionOrder.MethodMercadoPagoPix),
                publicBaseOrigin: "https://youlink-loja.onrender.com");

            // 3) Publica loja tenant + OAuth próprio da loja
            var publishRes = await gateway.PublishStoreAsync(new CloudCatalogSyncPayload
            {
                Settings = new UpdateStoreSettingsRequest
                {
                    StoreName = "Boutique Persistida",
                    StoreSlug = "boutique-persistida",
                    WhatsappNumber = "5511977776666",
                    StoreDescription = "Moda que sobrevive a deploys.",
                    IsSiteActive = true,
                    PlanTier = StoreSettings.PlanTierPremium,
                    DirectPixSalesEnabled = true,
                    PixGatewayMode = StoreSettings.PixGatewayDirect,
                    StorePixKey = "pix@boutiquepersistida.com.br",
                    OriginCep = "01310-100",
                    OriginCity = "São Paulo",
                    OriginState = "SP"
                },
                Categories =
                [
                    new SaveCategoryRequest { Name = "Casacos", DisplayOrder = 1, IsActive = true }
                ],
                CatalogSnapshot =
                [
                    new CloudProductSyncDto
                    {
                        SourceId = Guid.NewGuid(),
                        CategoryName = "Casacos",
                        CategorySlug = "casacos",
                        Name = "Jaqueta Jeans Premium",
                        Slug = "jaqueta-jeans-premium",
                        Description = "Lavagem escura reforçada.",
                        BasePrice = 229.90m,
                        IsVisibleOnSite = true,
                        Variants =
                        [
                            new CloudProductVariantSyncDto
                            {
                                Color = "Azul",
                                ColorHex = "#1E3A8A",
                                Size = "M",
                                Sku = "JAQ-M-01",
                                StockQuantity = 9,
                                LowStockThreshold = 2,
                                IsActive = true
                            }
                        ],
                        Images = []
                    }
                ]
            },
            requestedSlug: "boutique-persistida",
            syncKeyHeader: null,
            publishTokenHeader: null,
            hasValidSyncKey: false,
            authenticatedOwnerEmail: "dona@boutiquepersistida.com.br",
            publicServerOriginUrl: "https://youlink-loja.onrender.com");

            Assert.IsTrue(publishRes.Success, publishRes.Message);
            Assert.IsFalse(string.IsNullOrWhiteSpace(publishRes.PublishToken));

            await storeManager.SaveMercadoPagoTokensAsync(
                "boutique-persistida",
                "APP_USR-TENANT-SECRET-555",
                "TG-TENANT-REF-555",
                DateTimeOffset.UtcNow.AddHours(6),
                "555999",
                "BOUTIQUE_MP");

            await mirror.FlushAsync(TimeSpan.FromSeconds(5));

            Assert.IsTrue(cloudBackend.Store.ContainsKey("clothingstore_db.json"));
            Assert.IsTrue(cloudBackend.Store.ContainsKey("platform_owner_settings.json"));
            Assert.IsTrue(cloudBackend.Store.ContainsKey("platform_mp_credentials.json"));
            Assert.IsTrue(cloudBackend.Store.ContainsKey("tenants_index.json"));
            Assert.IsTrue(cloudBackend.Store.ContainsKey("tenants/boutique-persistida.json"));
            Assert.IsTrue(cloudBackend.Store.ContainsKey("keystore_platform.key"));
            Assert.IsTrue(cloudBackend.Store.ContainsKey("keystore_tenants.key"));

            // 4) Simula redeploy/reinício no Render Free Tier: APAGA TODO O DISCO LOCAL!
            CloudAppDataMirrorRegistry.Unregister(tempAppData);
            mirror.Dispose();
            mirror = null;
            Directory.Delete(tempAppData, recursive: true);
            Assert.IsFalse(Directory.Exists(tempAppData));

            // 5) Novo boot hidrata o disco vazio a partir da nuvem e recupera TUDO
            mirror = new CloudAppDataMirror(tempAppData, cloudBackend);
            CloudAppDataMirrorRegistry.Register(tempAppData, mirror);
            var restoredFiles = await mirror.HydrateToDiskAsync();
            Assert.IsTrue(restoredFiles >= 7, $"Esperava >= 7 arquivos restaurados, obteve {restoredFiles}.");

            var restoredRepo = new ClothingStoreRepository(hasher, dbPath);
            var restoredSettings = await restoredRepo.GetStoreSettingsAsync();
            Assert.AreEqual(49.90m, restoredSettings.BasicPlanMonthlyPrice);
            Assert.AreEqual(89.90m, restoredSettings.IntermediaryPlanMonthlyPrice);
            Assert.AreEqual("pix-central@youlink.com.br", restoredSettings.PicpayPixKey);

            var restoredOrders = await restoredRepo.GetPlatformSubscriptionOrdersAsync();
            Assert.IsTrue(restoredOrders.Any(o => o.OrderNumber == planOrder.OrderNumber));

            var restoredTokenStore = new PlatformMercadoPagoTokenStore(tempAppData);
            var restoredOwnerToken = await restoredTokenStore.GetValidAccessTokenAsync(mpService);
            Assert.AreEqual("APP_USR-CLOUD-MIRROR-TOKEN-777", restoredOwnerToken);

            var restoredManager = new MultiTenantStoreManager(restoredRepo, hasher, tempAppData);
            var tenantRepo = await restoredManager.ResolveReadRepositoryAsync("boutique-persistida");
            Assert.IsNotNull(tenantRepo);
            var products = await tenantRepo!.GetProductsAsync();
            Assert.AreEqual(1, products.Count);
            Assert.AreEqual("Jaqueta Jeans Premium", products[0].Name);

            var tenantCreds = await restoredManager.GetTenantCredentialsAsync("boutique-persistida");
            Assert.IsNotNull(tenantCreds);
            Assert.AreEqual("APP_USR-TENANT-SECRET-555", tenantCreds!.MercadoPagoAccessToken);

            var reauth = await restoredManager.AuthorizeAndResolvePublishRepositoryAsync(
                "boutique-persistida",
                syncKeyHeader: null,
                publishRes.PublishToken,
                hasValidSyncKey: false,
                authenticatedOwnerEmail: null);
            Assert.IsTrue(reauth.Authorized, reauth.ErrorMessage);

            // 6) Desconectar OAuth também remove snapshot na nuvem
            await restoredTokenStore.ClearOAuthTokensAsync();
            await mirror.FlushAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(cloudBackend.Store.ContainsKey("platform_mp_credentials.json"));
        }
        finally
        {
            CloudAppDataMirrorRegistry.Unregister(tempAppData);
            mirror?.Dispose();
            CleanupDir(tempAppData);
            Environment.SetEnvironmentVariable("YOULINK_TOKEN_ENCRYPTION_KEY", previousEncKey);
        }
    }

    [TestMethod]
    public async Task SupabaseBucketStateBackend_ShouldCreatePrivateBucketUploadListDownloadAndDelete()
    {
        var handler = new MockSupabaseStorageHandler();
        using var http = new HttpClient(handler);
        var backend = new SupabaseBucketStateBackend(
            "https://demo-proj.supabase.co/",
            "srv-role-secret-123",
            http);

        Assert.AreEqual("SupabaseStorage", backend.ProviderName);

        await backend.UpsertSnapshotAsync("clothingstore_db.json", "{\"store\":\"root\"}");
        await backend.UpsertSnapshotAsync("tenants/atelier-vip.json", "{\"store\":\"vip\"}");

        Assert.AreEqual(1, handler.BucketCreateCalls, "Deveria criar bucket privado apenas uma vez no primeiro uso.");
        Assert.AreEqual(2, handler.Objects.Count);

        var loaded = await backend.LoadAllSnapshotsAsync();
        Assert.AreEqual(2, loaded.Count);
        Assert.AreEqual("{\"store\":\"root\"}", loaded["clothingstore_db.json"]);
        Assert.AreEqual("{\"store\":\"vip\"}", loaded["tenants/atelier-vip.json"]);

        await backend.DeleteSnapshotAsync("tenants/atelier-vip.json");
        Assert.IsFalse(handler.Objects.ContainsKey("tenants/atelier-vip.json"));
    }

    [TestMethod]
    public async Task WebPortalLoopback_ShouldServeCentralPortalWithLiveDownloadsAndKeepTenantStorefrontIntact()
    {
        var root = ResolveRepoRoot();
        var webProjectDir = Path.Combine(root, "src", "ClothingStore.Web");
        var testConfig = new FileInfo(typeof(PlatformPortalAndCloudPersistenceTests).Assembly.Location).Directory?.Parent?.Name ?? "Release";

        var candidateDirs = new[]
        {
            Path.Combine(webProjectDir, "bin", testConfig, "net10.0"),
            Path.Combine(webProjectDir, "bin", "Release", "net10.0"),
            Path.Combine(webProjectDir, "bin", "Debug", "net10.0")
        };

        var webBinDir = candidateDirs.FirstOrDefault(d =>
            File.Exists(Path.Combine(d, "ClothingStore.Web.dll")) ||
            File.Exists(Path.Combine(d, "ClothingStore.Web.exe")));
        Assert.IsNotNull(webBinDir, "ClothingStore.Web compilada não foi encontrada para o teste loopback.");

        var webExe = Path.Combine(webBinDir!, "ClothingStore.Web.exe");
        var webDll = Path.Combine(webBinDir!, "ClothingStore.Web.dll");
        var useExe = OperatingSystem.IsWindows() && File.Exists(webExe);

        // 1) Stub local imitando GitHub Releases API sem acesso externo à internet
        var stubReleaseJson = JsonSerializer.Serialize(new
        {
            tag_name = "v9.9.9",
            name = "YouLink v9.9.9 Teste",
            html_url = "https://github.com/giovannipieronisleal-pixel/yourlink/releases/tag/v9.9.9",
            published_at = DateTimeOffset.UtcNow,
            assets = new object[]
            {
                new
                {
                    name = AppUpdateCatalog.WindowsSetupAssetName,
                    browser_download_url = "https://downloads.youlink.test/YouLink-Setup-x64.exe",
                    size = 24_000_000L
                },
                new
                {
                    name = AppUpdateCatalog.WindowsAssetName,
                    browser_download_url = "https://downloads.youlink.test/YouLink-Windows-x64.zip",
                    size = 20_000_000L
                },
                new
                {
                    name = AppUpdateCatalog.AndroidAssetName,
                    browser_download_url = "https://downloads.youlink.test/YouLink-Android.apk",
                    size = 18_000_000L
                }
            }
        });

        using var stubListener = new TcpListener(IPAddress.Loopback, 0);
        stubListener.Start();
        var stubPort = ((IPEndPoint)stubListener.LocalEndpoint).Port;
        using var stubCts = new CancellationTokenSource();
        var stubTask = Task.Run(async () =>
        {
            while (!stubCts.IsCancellationRequested)
            {
                try
                {
                    using var client = await stubListener.AcceptTcpClientAsync(stubCts.Token);
                    using var stream = client.GetStream();
                    var buf = new byte[2048];
                    _ = await stream.ReadAsync(buf, stubCts.Token);
                    var bodyBytes = Encoding.UTF8.GetBytes(stubReleaseJson);
                    var header = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, stubCts.Token);
                    await stream.WriteAsync(bodyBytes, stubCts.Token);
                }
                catch
                {
                    if (stubCts.IsCancellationRequested)
                        break;
                }
            }
        });

        // 2) Prepara App_Data temporário com preços da plataforma e loja publicada /loja/boutique-luz
        var tempDir = Path.Combine(Path.GetTempPath(), $"ylk_web_portal_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var hasher = new Pbkdf2PasswordHasher();
            var dbPath = Path.Combine(tempDir, "clothingstore_db.json");
            var repo = new ClothingStoreRepository(hasher, dbPath);
            var settings = await repo.GetStoreSettingsAsync();
            settings.BasicPlanMonthlyPrice = 49.90m;
            settings.IntermediaryPlanMonthlyPrice = 89.90m;
            settings.PremiumPlanMonthlyPrice = 139.90m;
            await repo.UpdateStoreSettingsAsync(settings);

            var manager = new MultiTenantStoreManager(repo, hasher, tempDir);
            var gateway = new MultiTenantGatewayService(
                manager,
                new MercadoPagoPixService(new HttpClient(), () => new MercadoPagoAppConfig(null, null, null)),
                new HttpClient(),
                hasher,
                new SupabaseImageStorageService(new HttpClient(), new ConfigurationBuilder().Build(), Path.Combine(tempDir, "wwwroot")));

            var seeded = await gateway.PublishStoreAsync(new CloudCatalogSyncPayload
            {
                Settings = new UpdateStoreSettingsRequest
                {
                    StoreName = "Boutique Luz Real",
                    StoreSlug = "boutique-luz",
                    WhatsappNumber = "5511999990000",
                    StoreDescription = "Roupas leves e exclusivas.",
                    IsSiteActive = true,
                    PlanTier = StoreSettings.PlanTierBasic
                },
                Categories =
                [
                    new SaveCategoryRequest { Name = "Vestidos", DisplayOrder = 1, IsActive = true }
                ],
                CatalogSnapshot =
                [
                    new CloudProductSyncDto
                    {
                        SourceId = Guid.NewGuid(),
                        CategoryName = "Vestidos",
                        CategorySlug = "vestidos",
                        Name = "Vestido Seda Azul",
                        Slug = "vestido-seda-azul",
                        Description = "Corte fluido.",
                        BasePrice = 189.90m,
                        IsVisibleOnSite = true,
                        Variants =
                        [
                            new CloudProductVariantSyncDto
                            {
                                Color = "Azul",
                                ColorHex = "#2563EB",
                                Size = "M",
                                Sku = "VSA-M",
                                StockQuantity = 6,
                                LowStockThreshold = 1,
                                IsActive = true
                            }
                        ],
                        Images = []
                    }
                ]
            },
            requestedSlug: "boutique-luz",
            syncKeyHeader: null,
            publishTokenHeader: null,
            hasValidSyncKey: false,
            authenticatedOwnerEmail: "luz@boutique.com",
            publicServerOriginUrl: "https://youlink-loja.onrender.com");
            Assert.IsTrue(seeded.Success, seeded.Message);

            var webPort = ReserveFreeLoopbackPort();
            var webUrl = $"http://127.0.0.1:{webPort}";

            var psi = new ProcessStartInfo
            {
                FileName = useExe ? webExe : "dotnet",
                Arguments = useExe ? string.Empty : $"\"{webDll}\"",
                WorkingDirectory = webBinDir!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.Environment["ASPNETCORE_URLS"] = webUrl;
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            psi.Environment["Database__FilePath"] = dbPath;
            psi.Environment["YOULINK_PORTAL_HOME"] = "true";
            psi.Environment["YOULINK_GITHUB_RELEASE_API_URL"] = $"http://127.0.0.1:{stubPort}/releases/latest";
            psi.Environment["YOULINK_SYNC_SECRET"] = "portal-loopback-sync-secret";
            psi.Environment["DOTNET_PRINT_TELEMETRY_MESSAGE"] = "false";

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Falha ao iniciar processo ClothingStore.Web.");

            try
            {
                using var http = new HttpClient { BaseAddress = new Uri(webUrl), Timeout = TimeSpan.FromSeconds(5) };

                HttpResponseMessage? healthRes = null;
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    if (process.HasExited)
                    {
                        var outText = await process.StandardOutput.ReadToEndAsync();
                        var errText = await process.StandardError.ReadToEndAsync();
                        throw new InvalidOperationException($"ClothingStore.Web encerrou ({process.ExitCode}). STDOUT: {outText}\nSTDERR: {errText}");
                    }

                    try
                    {
                        healthRes = await http.GetAsync("/health");
                        if (healthRes.IsSuccessStatusCode)
                            break;
                    }
                    catch
                    {
                    }
                    await Task.Delay(150);
                }

                Assert.IsNotNull(healthRes);
                Assert.AreEqual(HttpStatusCode.OK, healthRes!.StatusCode);
                var healthJson = await healthRes.Content.ReadAsStringAsync();
                Assert.IsTrue(healthJson.Contains("\"service\":\"ClothingStore.Web\"", StringComparison.Ordinal), healthJson);
                Assert.IsTrue(healthJson.Contains("\"persistence\":\"LocalDisk\"", StringComparison.Ordinal), healthJson);

                // Portal central em /
                var rootHtml = await http.GetStringAsync("/");
                Assert.IsTrue(rootHtml.Contains("v9.9.9", StringComparison.Ordinal), "Portal / deve exibir versão v9.9.9 do release.");
                Assert.IsTrue(rootHtml.Contains("YouLink-Setup-x64.exe", StringComparison.Ordinal));
                Assert.IsTrue(rootHtml.Contains("YouLink-Windows-x64.zip", StringComparison.Ordinal));
                Assert.IsTrue(rootHtml.Contains("YouLink-Android.apk", StringComparison.Ordinal));
                Assert.IsTrue(rootHtml.Contains("Como começar a usar o YouLink hoje", StringComparison.Ordinal));
                Assert.IsTrue(rootHtml.Contains("49,90", StringComparison.Ordinal), "Preços vivos dos planos devem aparecer no portal /.");
                Assert.IsTrue(rootHtml.Contains("89,90", StringComparison.Ordinal));
                Assert.IsTrue(rootHtml.Contains("139,90", StringComparison.Ordinal));
                Assert.IsFalse(rootHtml.Contains("AdminApp", StringComparison.OrdinalIgnoreCase),
                    "O portal público nunca deve expor links ou nomes do aplicativo privado Admin.");

                // Vitrine de loja em /loja/boutique-luz intacta
                var storeHtml = await http.GetStringAsync("/loja/boutique-luz");
                Assert.IsTrue(storeHtml.Contains("Boutique Luz Real", StringComparison.Ordinal),
                    "/loja/boutique-luz deve continuar exibindo a vitrine da loja publicada.");
                Assert.IsTrue(storeHtml.Contains("Vestido Seda Azul", StringComparison.Ordinal));
                Assert.IsFalse(storeHtml.Contains("Baixar YouLink Grátis", StringComparison.Ordinal),
                    "/loja/{slug} não deve renderizar o portal central de downloads.");
            }
            finally
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            }
        }
        finally
        {
            stubCts.Cancel();
            try { stubListener.Stop(); } catch { }
            try { await stubTask; } catch { }
            CleanupDir(tempDir);
        }
    }
}
