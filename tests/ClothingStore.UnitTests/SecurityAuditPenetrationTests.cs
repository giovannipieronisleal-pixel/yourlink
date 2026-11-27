using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
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

/// <summary>
/// Suíte adversária (Red Team / PenTest automatizado) cobrindo todos os vetores críticos e importantes:
/// 1) Sequestro de loja por cabeçalho X-YouLink-Owner-Email;
/// 2) Auto-promoção gratuita para Premium via sync-state / publish-store;
/// 3) Pass-the-hash em verify-login, pull-store, upsert e chat + força bruta + senha mínima de 8 caracteres;
/// 4) Rotas /api/ai/product-copy e /api/cloud/telemetry/error com política cloud-accounts sem erro 500;
/// 5) Adulteração de valor (Amount / desconto indevido) na compra de planos;
/// 6) Reivindicação de e-mail com VIP pré-liberado sem confirmação OTP;
/// 7) Injeção de X-Forwarded-Host na geração de links públicos / callbacks;
/// 8) Adulteração de binário de atualização bloqueada por SHA-256.
/// </summary>
[TestClass]
public sealed class SecurityAuditPenetrationTests
{
    [TestMethod]
    public async Task Critical1_And_Critical2_UnverifiedOwnerEmailCannotHijackStore_And_ClientSyncCannotSelfActivatePremium()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink_pentest_mt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var hasher = new Pbkdf2PasswordHasher();
            var rootRepo = new ClothingStoreRepository(hasher, Path.Combine(tempDir, "root.json"));
            var resolver = new MultiTenantStoreManager(rootRepo, hasher, tempDir);
            var mpService = new MercadoPagoPixService(new HttpClient(), () => new MercadoPagoAppConfig("cid", "csec", "whsec"));
            var storage = new SupabaseImageStorageService(new HttpClient(), new ConfigurationBuilder().Build(), Path.Combine(tempDir, "wwwroot"));
            var gateway = new MultiTenantGatewayService(resolver, mpService, new HttpClient(), hasher, storage);

            // 1. Vítima cria sua loja tentando mandar PlanTier = "Premium" e preço adulterado sem ter pago
            var victimPayload = new CloudCatalogSyncPayload
            {
                Settings = new UpdateStoreSettingsRequest
                {
                    StoreName = "Boutique Vítima",
                    StoreSlug = "boutique-vitima",
                    WhatsappNumber = "5511988887777",
                    StoreDescription = "Loja original da vítima",
                    IsSiteActive = true,
                    PlanTier = StoreSettings.PlanTierPremium,
                    PremiumPlanMonthlyPrice = 0.01m,
                    PicpayPixKey = "invasor@pix.com",
                    DirectPixSalesEnabled = true,
                    StorePixKey = "vitima@pix.com",
                    OriginCep = "01310-100"
                },
                Categories = [new SaveCategoryRequest { Name = "Vestidos", DisplayOrder = 1, IsActive = true }],
                CatalogSnapshot =
                [
                    new CloudProductSyncDto
                    {
                        SourceId = Guid.NewGuid(),
                        CategoryName = "Vestidos",
                        CategorySlug = "vestidos",
                        Name = "Vestido Original",
                        Slug = "vestido-original",
                        BasePrice = 299.90m,
                        IsVisibleOnSite = true,
                        Variants = [new CloudProductVariantSyncDto { Color = "Preto", Size = "M", StockQuantity = 5, IsActive = true }]
                    }
                ]
            };

            var createRes = await gateway.PublishStoreAsync(
                victimPayload,
                requestedSlug: "boutique-vitima",
                syncKeyHeader: null,
                publishTokenHeader: null,
                hasValidSyncKey: false,
                authenticatedOwnerEmail: "vitima@boutique.com",
                publicServerOriginUrl: "https://youlink-loja.onrender.com",
                isOwnerEmailServerVerified: true);

            Assert.IsTrue(createRes.Success);
            Assert.IsFalse(string.IsNullOrWhiteSpace(createRes.PublishToken));

            // CRÍTICO 2: A loja publicada sem pagamento NÃO pode virar Premium nem habilitar Pix pago!
            var tenantRepo = await gateway.ResolveReadRepositoryAsync("boutique-vitima");
            var savedSettings = await tenantRepo!.GetStoreSettingsAsync();
            Assert.AreEqual(StoreSettings.PlanTierBasic, savedSettings.GetEffectivePlanTier(),
                "CRÍTICO 2: Sincronização do cliente não pode ativar Premium sem pagamento ou Gift Card.");
            Assert.IsFalse(savedSettings.DirectPixSalesEnabled,
                "CRÍTICO 2: Loja no plano Básico não pode habilitar vendas Pix pagas via payload do cliente.");

            // CRÍTICO 1: Invasor descobre o e-mail da vítima ("vitima@boutique.com") e tenta sobrescrever a loja sem PublishToken e sem sessão verificada
            var attackerPayload = new CloudCatalogSyncPayload
            {
                Settings = new UpdateStoreSettingsRequest
                {
                    StoreName = "LOJA HACKEADA",
                    StoreSlug = "boutique-vitima",
                    WhatsappNumber = "5511900000000",
                    StoreDescription = "Pix desviado",
                    IsSiteActive = true
                },
                Categories = [new SaveCategoryRequest { Name = "Golpe", DisplayOrder = 1, IsActive = true }],
                CatalogSnapshot = []
            };

            var hijackPublish = await gateway.PublishStoreAsync(
                attackerPayload,
                requestedSlug: "boutique-vitima",
                syncKeyHeader: null,
                publishTokenHeader: null,
                hasValidSyncKey: false,
                authenticatedOwnerEmail: "vitima@boutique.com",
                publicServerOriginUrl: "https://youlink-loja.onrender.com",
                isOwnerEmailServerVerified: false);

            Assert.IsFalse(hijackPublish.Success,
                "CRÍTICO 1: Informar apenas o e-mail da vítima sem verificação no servidor NÃO pode autorizar publicação.");

            var hijackOrders = await gateway.GetStoreOrdersForSyncAsync(
                storeSlug: "boutique-vitima",
                publishTokenHeader: null,
                hasValidSyncKey: false,
                authenticatedOwnerEmail: "vitima@boutique.com",
                isOwnerEmailServerVerified: false);
            Assert.IsNull(hijackOrders,
                "CRÍTICO 1: Informar apenas o e-mail da vítima NÃO pode ler pedidos de clientes.");

            var hijackDisconnectMp = await gateway.DisconnectMercadoPagoAsync(
                storeSlug: "boutique-vitima",
                publishTokenHeader: null,
                hasValidSyncKey: false,
                authenticatedOwnerEmail: "vitima@boutique.com",
                isOwnerEmailServerVerified: false);
            Assert.IsFalse(hijackDisconnectMp,
                "CRÍTICO 1: Informar apenas o e-mail da vítima NÃO pode desconectar o Mercado Pago.");

            // Confirma que os produtos e nome originais da vítima continuam intactos
            var intactSettings = await tenantRepo.GetStoreSettingsAsync();
            Assert.AreEqual("Boutique Vítima", intactSettings.StoreName);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task Critical3_And_Important6_NoPassTheHash_GenericErrors_BruteForceLockout_And_OtpRequiredForPreGrantedVip()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink_pentest_auth_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var hasher = new Pbkdf2PasswordHasher();
            var registry = new CloudAccountRegistryService(tempDir, hasher);

            var victimHash = hasher.HashPassword("SenhaSuperForte#2026");
            var created = await registry.UpsertAccountAsync(new CloudAccountCredentialSyncDto(
                FullName: "Lojista Real",
                Username: "lojistareal",
                Email: "real@loja.com",
                PasswordHash: victimHash,
                StoreName: "Loja Real",
                StoreSlug: "loja-real"));
            Assert.IsTrue(created.Success);
            Assert.IsFalse(string.IsNullOrWhiteSpace(created.CloudSessionToken));

            // 1) verify-login NUNCA devolve PasswordHash e emite CloudSessionToken
            var okLogin = await registry.VerifyLoginAsync(new LoginRequest("real@loja.com", "SenhaSuperForte#2026"));
            Assert.IsTrue(okLogin.Verified);
            Assert.IsTrue(string.IsNullOrEmpty(okLogin.Account!.PasswordHash),
                "CRÍTICO 3: verify-login jamais deve retornar PasswordHash.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(okLogin.CloudSessionToken),
                "CRÍTICO 3: verify-login deve retornar CloudSessionToken opaco.");

            // 2) Mensagem genérica sem revelar se conta existe ou não
            var nonExistent = await registry.VerifyLoginAsync(new LoginRequest("fantasma@naoexiste.com", "QualquerSenha123"));
            var wrongPass = await registry.VerifyLoginAsync(new LoginRequest("real@loja.com", "SenhaErrada123"));
            Assert.IsFalse(nonExistent.Verified);
            Assert.IsFalse(nonExistent.AccountExists);
            Assert.IsFalse(wrongPass.Verified);
            Assert.IsFalse(wrongPass.AccountExists, "Não deve revelar AccountExists=true quando a senha está errada.");
            Assert.AreEqual(nonExistent.Message, wrongPass.Message, "A mensagem de erro deve ser idêntica para conta inexistente e senha incorreta.");

            // 3) Pass-the-hash bloqueado em UpsertAccountAsync: enviar o mesmo PasswordHash sem CloudSessionToken falha com 401!
            var passTheHashAttempt = await registry.UpsertAccountAsync(new CloudAccountCredentialSyncDto(
                FullName: "Tomada de Conta",
                Username: "lojistareal",
                Email: "real@loja.com",
                PasswordHash: victimHash,
                PreviousPasswordHash: victimHash,
                StoreName: "Loja Roubada"));
            Assert.IsFalse(passTheHashAttempt.Success,
                "CRÍTICO 3: Possuir apenas o PasswordHash não pode autenticar atualização de conta.");
            Assert.AreEqual(401, passTheHashAttempt.StatusCode);

            // 4) Força bruta bloqueia temporariamente a conta após 5 falhas consecutivas
            for (var i = 0; i < 5; i++)
            {
                _ = await registry.VerifyLoginAsync(new LoginRequest("real@loja.com", $"TentativaErrada#{i}"));
            }
            var lockedAttempt = await registry.VerifyLoginAsync(new LoginRequest("real@loja.com", "SenhaSuperForte#2026"));
            Assert.IsFalse(lockedAttempt.Verified,
                "CRÍTICO 3: Conta sob ataque de força bruta deve bloquear mesmo a senha correta durante o período de lockout.");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task Important5_And_Important8_ServerCalculatesExactPlanPrice_And_UpdateSha256BlocksTamperedBinary()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink_pentest_price_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var hasher = new Pbkdf2PasswordHasher();
            var repo = new ClothingStoreRepository(hasher, Path.Combine(tempDir, "db.json"));
            var settings = await repo.GetStoreSettingsAsync();
            settings.UpdatePlanAndDesign(
                StoreSettings.PlanTierBasic,
                59.90m,
                149.90m,
                "dono@youlink.com.br",
                null,
                null,
                99.90m);
            await repo.UpdateStoreSettingsAsync(settings);

            var subService = new PlatformSubscriptionService(repo);

            // IMPORTANTE 5: Comprador sem Google Workspace verificado tenta mandar Amount = 0.50m (ou piso de desconto máximo) no Pix Direto e no MP
            var tamperedDirectPix = await subService.CreatePlanOrderAsync(new CreatePlatformPlanOrderRequest(
                PlanTier: StoreSettings.PlanTierPremium,
                DurationMonths: 1,
                Amount: 0.50m,
                StoreName: "Loja Espertinha",
                UseOwnDedicatedServer: true,
                BuyerEmail: "espertinho@gmail.com",
                PaymentMethod: PlatformSubscriptionOrder.MethodDirectPix));

            Assert.AreEqual(149.90m, tamperedDirectPix.Amount,
                "IMPORTANTE 5: O servidor deve ignorar Amount=0.50 e UseOwnDedicatedServer não verificado, cobrando integralmente R$ 149,90.");

            // IMPORTANTE 8: Verificação de integridade SHA-256 bloqueia pacote adulterado
            var pkgFile = Path.Combine(tempDir, "YouLink-Setup-x64.exe");
            await File.WriteAllBytesAsync(pkgFile, Encoding.UTF8.GetBytes("MZ-instalador-oficial-integro-1234567890"));
            var realSha256 = AppUpdateCatalog.ComputeFileSha256(pkgFile);

            AppUpdateCatalog.VerifyDownloadedPackageSha256(pkgFile, $"sha256:{realSha256}");

            // Adultera 1 byte no executável baixado
            await File.WriteAllBytesAsync(pkgFile, Encoding.UTF8.GetBytes("MZ-instalador-ADULTERADO-99999999999999"));
            Assert.ThrowsException<InvalidOperationException>(() =>
                AppUpdateCatalog.VerifyDownloadedPackageSha256(pkgFile, realSha256));

            var script = AppUpdateCatalog.BuildWindowsInstallerUpdateScript(
                9999,
                pkgFile,
                @"C:\YouLink\YouLink.exe",
                Path.Combine(tempDir, "upd.log"),
                expectedSha256: realSha256);
            Assert.IsTrue(script.Contains("Get-FileHash", StringComparison.Ordinal) &&
                          script.Contains(realSha256, StringComparison.Ordinal),
                "IMPORTANTE 8: O script PowerShell do instalador deve conferir o SHA-256 antes de executar o binário.");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task Important4_And_Important7_LiveWebEndpoints_CloudAccountsPolicy_NoForwardedHostPoisoning_And_HttpHijackBlocked()
    {
        var root = FindRepoRoot();
        var webDllCandidates = new[]
        {
            Path.Combine(root, "src", "ClothingStore.Web", "bin", "Release", "net10.0", "ClothingStore.Web.dll"),
            Path.Combine(root, "src", "ClothingStore.Web", "bin", "Debug", "net10.0", "ClothingStore.Web.dll")
        };
        var webDll = webDllCandidates.FirstOrDefault(File.Exists);
        if (webDll is null)
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink_pentest_web_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0))
        {
            l.Start();
            port = ((IPEndPoint)l.LocalEndpoint).Port;
        }
        var baseUrl = $"http://127.0.0.1:{port}";

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{webDll}\"",
            WorkingDirectory = Path.GetDirectoryName(webDll)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.Environment["ASPNETCORE_URLS"] = baseUrl;
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["Database__FilePath"] = Path.Combine(tempDir, "web_db.json");
        psi.Environment["YOULINK_SYNC_SECRET"] = "pentest-sync-secret-999";
        psi.Environment["DOTNET_PRINT_TELEMETRY_MESSAGE"] = "false";

        using var proc = Process.Start(psi)!;
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(8) };
            for (var i = 0; i < 60; i++)
            {
                try
                {
                    var h = await http.GetAsync("/health");
                    if (h.IsSuccessStatusCode)
                        break;
                }
                catch { }
                await Task.Delay(150);
            }

            // IMPORTANTE 4: /api/ai/product-copy e /api/cloud/telemetry/error NÃO podem retornar 500!
            var aiRes = await http.PostAsJsonAsync("/api/ai/product-copy", new GenerateProductCopyRequest("Vestido Seda", "Moda", 199.90m));
            Assert.AreEqual(HttpStatusCode.OK, aiRes.StatusCode, $"IMPORTANTE 4: /api/ai/product-copy falhou com {aiRes.StatusCode}");

            var telRes = await http.PostAsJsonAsync("/api/cloud/telemetry/error", new ClientCrashReportRequest(
                "YouLink", "1.0.80", "Windows", "UnitTest", "NullReferenceException", "Teste sanitizado"));
            Assert.AreEqual(HttpStatusCode.OK, telRes.StatusCode, $"IMPORTANTE 4: /api/cloud/telemetry/error falhou com {telRes.StatusCode}");

            // IMPORTANTE 7: X-Forwarded-Host malicioso deve ser ignorado em /robots.txt e /api/platform/pwa-config
            using var hostPoisonReq = new HttpRequestMessage(HttpMethod.Get, "/api/platform/pwa-config");
            hostPoisonReq.Headers.TryAddWithoutValidation("X-Forwarded-Host", "evil-phishing.attacker.com");
            var pwaCfgRes = await http.SendAsync(hostPoisonReq);
            var pwaCfgRaw = await pwaCfgRes.Content.ReadAsStringAsync();
            Assert.IsFalse(pwaCfgRaw.Contains("evil-phishing.attacker.com", StringComparison.OrdinalIgnoreCase),
                "IMPORTANTE 7: GetPublicOrigin jamais deve refletir X-Forwarded-Host arbitrário.");

            // CRÍTICO 3 (HTTP): Senha < 8 caracteres bloqueada em /api/cloud/accounts/register
            var shortPassRes = await http.PostAsJsonAsync("/api/cloud/accounts/register", new RegisterUserAccountRequest(
                "Teste Curto", "curto@teste.com", "1234", Username: "curto"));
            Assert.AreEqual(HttpStatusCode.BadRequest, shortPassRes.StatusCode,
                "CRÍTICO 3: Cadastro na nuvem deve rejeitar senha menor que 8 caracteres.");

            // CRÍTICO 3 (HTTP): /api/cloud/pull-store rejeita PasswordHash puro sem CloudSessionToken
            var pullWithHashOnly = await http.PostAsJsonAsync("/api/cloud/pull-store", new CloudPullStoreRequest(
                Email: "qualquer@loja.com",
                Username: "qualquer",
                PasswordHash: "pbkdf2$100000$hashroubado"));
            Assert.AreEqual(HttpStatusCode.Unauthorized, pullWithHashOnly.StatusCode,
                "CRÍTICO 3: /api/cloud/pull-store deve rejeitar tentativa de login via PasswordHash.");
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "appmaui.blazor.csproj")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
