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
                StoreSlug: "loja-real"),
                emailVerifiedByServer: true);
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

            // IMPORTANTE 8 + v1.0.81: SHA-256 ausente OU divergente DEVE abortar a instalação!
            var pkgFile = Path.Combine(tempDir, "YouLink-Setup-x64.exe");
            await File.WriteAllBytesAsync(pkgFile, Encoding.UTF8.GetBytes("MZ-instalador-oficial-integro-1234567890"));
            var realSha256 = AppUpdateCatalog.ComputeFileSha256(pkgFile);

            Assert.ThrowsException<InvalidOperationException>(() =>
                AppUpdateCatalog.VerifyDownloadedPackageSha256(pkgFile, null),
                "Se o SHA-256 não for encontrado (null), a instalação deve abortar.");
            Assert.ThrowsException<InvalidOperationException>(() =>
                AppUpdateCatalog.VerifyDownloadedPackageSha256(pkgFile, string.Empty),
                "Se o SHA-256 vier vazio, a instalação deve abortar.");

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
                          script.Contains("if (-not $expectedSha256", StringComparison.Ordinal) &&
                          script.Contains("Get-AuthenticodeSignature", StringComparison.Ordinal) &&
                          script.Contains(realSha256, StringComparison.Ordinal),
                "O script PowerShell do instalador deve exigir SHA-256 presente, conferir o hash e validar assinatura Authenticode.");
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

            // CRÍTICO A (HTTP): /api/cloud/accounts/register exige código OTP mesmo em cadastro comum!
            var regWithoutOtp = await http.PostAsJsonAsync("/api/cloud/accounts/register", new RegisterUserAccountRequest(
                "Usuário Sem OTP", "semotp@exemplo.com", "SenhaForte#123", Username: "semotp"));
            Assert.IsTrue(
                regWithoutOtp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest,
                $"CRÍTICO A: Cadastro comum sem confirmar código OTP no e-mail não pode criar conta direto (status={regWithoutOtp.StatusCode}).");

            // Cria 2 contas verificadas via SyncKey + OTP para testar Crítico B (merchant-chat) e VIP antes da autorização
            using (var syncVictim = new HttpRequestMessage(HttpMethod.Post, "/api/cloud/accounts/upsert"))
            {
                syncVictim.Headers.TryAddWithoutValidation("X-YouLink-Sync-Key", "pentest-sync-secret-999");
                syncVictim.Content = JsonContent.Create(new CloudAccountCredentialSyncDto(
                    "Vítima Loja", "vitimaloja", "vitima@loja.com", new Pbkdf2PasswordHasher().HashPassword("Vitima#12345"),
                    StoreName: "Loja Vítima", StoreSlug: "loja-vitima"));
                var r = await http.SendAsync(syncVictim);
                Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
            }

            // Vítima publica sua loja com o PublishToken inicial
            var victimPubRes = await http.PostAsJsonAsync("/api/cloud/publish-store?storeSlug=loja-vitima", new CloudCatalogSyncPayload
            {
                Settings = new UpdateStoreSettingsRequest
                {
                    StoreName = "Loja Vítima",
                    StoreSlug = "loja-vitima",
                    WhatsappNumber = "5511999999999",
                    StoreDescription = "Vitrine da Vítima",
                    IsSiteActive = true
                }
            });
            Assert.AreEqual(HttpStatusCode.OK, victimPubRes.StatusCode);
            var victimPubBody = await victimPubRes.Content.ReadFromJsonAsync<CloudStorePublishResponse>();
            Assert.IsNotNull(victimPubBody);
            Assert.IsFalse(string.IsNullOrWhiteSpace(victimPubBody!.PublishToken));

            // Comprador abre conversa na loja da vítima
            var buyerReg = await http.PostAsJsonAsync("/api/cloud/buyers/register", new RegisterBuyerAccountRequest(
                "Cliente Real", "cliente@comprador.com", "Cliente#12345"));
            var buyerAuth = await buyerReg.Content.ReadFromJsonAsync<BuyerAuthResultDto>();
            Assert.IsTrue(buyerAuth?.Success == true);

            using (var msgReq = new HttpRequestMessage(HttpMethod.Post, "/api/cloud/store-chat/loja-vitima/messages"))
            {
                msgReq.Headers.TryAddWithoutValidation("X-Buyer-Token", buyerAuth!.Session!.SessionToken);
                msgReq.Content = JsonContent.Create(new SendBuyerChatMessageRequest("Olá loja vítima!", "Loja Vítima", null));
                var mRes = await http.SendAsync(msgReq);
                Assert.AreEqual(HttpStatusCode.OK, mRes.StatusCode);
            }

            // Cria conta do invasor (com VIP Premium concedido na conta dele!) e faz login para obter CloudSessionToken válido
            using (var syncAttacker = new HttpRequestMessage(HttpMethod.Post, "/api/cloud/accounts/upsert"))
            {
                syncAttacker.Headers.TryAddWithoutValidation("X-YouLink-Sync-Key", "pentest-sync-secret-999");
                syncAttacker.Content = JsonContent.Create(new CloudAccountCredentialSyncDto(
                    "Invasor Logado", "invasor", "invasor@evil.com", new Pbkdf2PasswordHasher().HashPassword("Invasor#12345"),
                    StoreName: "Loja Invasor", StoreSlug: "loja-invasor", PlanTier: StoreSettings.PlanTierPremium));
                var r = await http.SendAsync(syncAttacker);
                Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
            }

            var attackerLoginRes = await http.PostAsJsonAsync("/api/cloud/accounts/verify-login", new LoginRequest("invasor@evil.com", "Invasor#12345"));
            var attackerLogin = await attackerLoginRes.Content.ReadFromJsonAsync<CloudVerifyLoginResponseDto>();
            Assert.IsTrue(attackerLogin?.Verified == true && !string.IsNullOrWhiteSpace(attackerLogin.CloudSessionToken));

            // CRÍTICO B (HTTP): Invasor logado tenta ler as conversas da "loja-vitima" em /api/cloud/merchant-chat/threads -> DEVE retornar 403!
            var chatStealAttempt = await http.PostAsJsonAsync("/api/cloud/merchant-chat/threads", new MerchantChatAuthRequest(
                Email: "invasor@evil.com",
                Username: "invasor",
                PasswordHash: null,
                PublishToken: null,
                StoreSlug: "loja-vitima",
                StoreName: "Loja Vítima",
                CloudSessionToken: attackerLogin!.CloudSessionToken));
            Assert.AreEqual(HttpStatusCode.Forbidden, chatStealAttempt.StatusCode,
                "CRÍTICO B: Conta logada que não é dona do StoreSlug nem tem o PublishToken da loja deve receber 403 no merchant-chat.");

            // Lojista logado com loja ainda não publicada ("minha-loja" ou slug inédito) recebe lista vazia 200 OK sem erro 403 falso
            var unpublishedOwnChat = await http.PostAsJsonAsync("/api/cloud/merchant-chat/threads", new MerchantChatAuthRequest(
                Email: "invasor@evil.com",
                Username: "invasor",
                PasswordHash: null,
                PublishToken: null,
                StoreSlug: "minha-loja",
                StoreName: "Minha Loja",
                CloudSessionToken: attackerLogin.CloudSessionToken));
            Assert.AreEqual(HttpStatusCode.OK, unpublishedOwnChat.StatusCode,
                "Loja padrão 'minha-loja' ou ainda não publicada deve retornar lista vazia 200 OK sem 403 falso.");

            // Dono legítimo com PublishToken da loja-vitima consegue ler normalmente (200 OK)
            var ownerChatOk = await http.PostAsJsonAsync("/api/cloud/merchant-chat/threads", new MerchantChatAuthRequest(
                Email: "vitima@loja.com",
                Username: "vitimaloja",
                PasswordHash: null,
                PublishToken: victimPubBody.PublishToken,
                StoreSlug: "loja-vitima",
                StoreName: "Loja Vítima"));
            Assert.AreEqual(HttpStatusCode.OK, ownerChatOk.StatusCode);

            // IMPORTANTE (VIP antes da autorização): Invasor com VIP tenta publicar contra "loja-vitima" sem o PublishToken dela;
            // deve falhar (409) E a "loja-vitima" deve continuar no plano Básico!
            using (var vipPolluteReq = new HttpRequestMessage(HttpMethod.Post, "/api/cloud/publish-store?storeSlug=loja-vitima"))
            {
                vipPolluteReq.Headers.TryAddWithoutValidation("X-YouLink-Cloud-Token", attackerLogin.CloudSessionToken);
                vipPolluteReq.Content = JsonContent.Create(new CloudCatalogSyncPayload
                {
                    Settings = new UpdateStoreSettingsRequest
                    {
                        StoreName = "Loja Vítima",
                        StoreSlug = "loja-vitima",
                        WhatsappNumber = "5511000000000",
                        StoreDescription = "Tentativa poluir VIP",
                        IsSiteActive = true
                    }
                });
                var polluteRes = await http.SendAsync(vipPolluteReq);
                Assert.AreEqual(HttpStatusCode.Conflict, polluteRes.StatusCode);
            }

            // IMPORTANTE (Baixar loja sem dono): "loja-vitima" foi publicada sem OwnerEmail verificado; invasor logado tenta pull-store -> Found = false!
            var pullOwnerlessRes = await http.PostAsJsonAsync("/api/cloud/pull-store", new CloudPullStoreRequest(
                Email: "invasor@evil.com",
                Username: "invasor",
                StoreSlug: "loja-vitima",
                CloudSessionToken: attackerLogin.CloudSessionToken));
            Assert.AreEqual(HttpStatusCode.OK, pullOwnerlessRes.StatusCode);
            var pullOwnerlessBody = await pullOwnerlessRes.Content.ReadFromJsonAsync<CloudPullStoreResponse>();
            Assert.IsFalse(pullOwnerlessBody!.Found,
                "IMPORTANTE: Conta logada não pode baixar loja sem dono (ou de outro dono) sem o PublishToken da loja.");
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task CriticalA_And_ChatAntiSpam_EmailSwapHijackBlocked_UnverifiedSessionNotTrusted_And_FloodRateLimited()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink_pentest_v81_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var hasher = new Pbkdf2PasswordHasher();
            var registry = new CloudAccountRegistryService(tempDir, hasher);

            // 1) Vítima cadastra conta com OTP confirmado (IsEmailVerified = true)
            var victimRes = await registry.UpsertAccountAsync(new CloudAccountCredentialSyncDto(
                FullName: "Dona Vítima",
                Username: "vitima",
                Email: "vitima@minhaloja.com",
                PasswordHash: hasher.HashPassword("VitimaForte#2026"),
                StoreName: "Minha Loja Oficial",
                StoreSlug: "minha-loja-oficial"),
                emailVerifiedByServer: true);
            Assert.IsTrue(victimRes.Success);

            // 2) Invasor cadastra sua própria conta com OTP confirmado
            var attackerHash = hasher.HashPassword("InvasorForte#2026");
            var attackerRes = await registry.UpsertAccountAsync(new CloudAccountCredentialSyncDto(
                FullName: "Invasor",
                Username: "invasor",
                Email: "invasor@atacante.com",
                PasswordHash: attackerHash,
                StoreName: "Loja Atacante",
                StoreSlug: "loja-atacante"),
                emailVerifiedByServer: true);
            Assert.IsTrue(attackerRes.Success);
            Assert.IsFalse(string.IsNullOrWhiteSpace(attackerRes.CloudSessionToken));

            // 3) CRÍTICO A: Invasor logado tenta trocar o próprio e-mail para "vitima@minhaloja.com" via PreviousEmail -> BLOQUEADO COM 409!
            var hijackExistingEmail = await registry.UpsertAccountAsync(new CloudAccountCredentialSyncDto(
                FullName: "Invasor",
                Username: "invasor",
                Email: "vitima@minhaloja.com",
                PreviousEmail: "invasor@atacante.com",
                PasswordHash: attackerHash,
                CloudSessionToken: attackerRes.CloudSessionToken));
            Assert.IsFalse(hijackExistingEmail.Success, "CRÍTICO A: Nunca permitir assumir e-mail já pertencente a outra conta.");
            Assert.AreEqual(409, hijackExistingEmail.StatusCode);

            // 4) CRÍTICO A: Invasor logado tenta trocar o próprio e-mail para outro endereço ("outroalvo@loja.com") SEM código OTP -> BLOQUEADO COM 403!
            var swapWithoutOtp = await registry.UpsertAccountAsync(new CloudAccountCredentialSyncDto(
                FullName: "Invasor",
                Username: "invasor",
                Email: "outroalvo@loja.com",
                PreviousEmail: "invasor@atacante.com",
                PasswordHash: attackerHash,
                CloudSessionToken: attackerRes.CloudSessionToken),
                hasValidSyncKey: false,
                emailVerifiedByServer: false);
            Assert.IsFalse(swapWithoutOtp.Success, "CRÍTICO A: Troca de e-mail sem código OTP confirmado no novo e-mail deve falhar com 403.");
            Assert.AreEqual(403, swapWithoutOtp.StatusCode);
            Assert.IsTrue(swapWithoutOtp.RequiresEmailVerification);

            // 5) CRÍTICO A: Conta sincronizada sem confirmação OTP (IsEmailVerified = false) NÃO é tratada como e-mail verificado na validação de sessão
            var unverifiedSync = await registry.UpsertAccountAsync(new CloudAccountCredentialSyncDto(
                FullName: "Importado Sem OTP",
                Username: "semotp",
                Email: "semotp@loja.com",
                PasswordHash: hasher.HashPassword("Importado#2026")),
                hasValidSyncKey: true,
                emailVerifiedByServer: false);
            Assert.IsTrue(unverifiedSync.Success);
            var sessionCheck = await registry.ValidateSessionTokenAsync(unverifiedSync.CloudSessionToken);
            Assert.IsTrue(sessionCheck.Verified);
            Assert.IsFalse(sessionCheck.IsEmailVerified, "Conta que não confirmou código OTP nunca deve ter IsEmailVerified=true.");

            // 6) Anti-spam e isolamento de exclusão no CloudChatStoreService
            var chatStore = new CloudChatStoreService(tempDir, hasher);
            var buyer = await chatStore.RegisterBuyerAsync(new RegisterBuyerAccountRequest("Spammer", "spammer@teste.com", "Spammer#2026"));
            Assert.IsTrue(buyer.Success);
            var buyerId = buyer.Session!.BuyerId;

            ChatThreadDetailDto? lastSent = null;
            for (var i = 1; i <= CloudChatStoreService.MaxMessagesPerMinutePerSender; i++)
            {
                lastSent = await chatStore.SendBuyerStoreMessageAsync(buyerId, "minha-loja-oficial", "Minha Loja Oficial", $"Mensagem {i}", null);
                Assert.IsNotNull(lastSent, $"Mensagem {i} dentro do limite deveria passar.");
            }

            // A 16ª mensagem seguida no mesmo minuto deve ser bloqueada pelo anti-flood!
            var blockedFlood = await chatStore.SendBuyerStoreMessageAsync(buyerId, "minha-loja-oficial", "Minha Loja Oficial", "SPAM #16", null);
            Assert.IsNull(blockedFlood, "IMPORTANTE (Spam no chat): O 16º envio no mesmo minuto deve ser bloqueado.");

            // Outra loja tentando apagar mensagem de thread que não é dela falha
            var firstMsgId = lastSent!.Messages[0].Id;
            var crossStoreDelete = await chatStore.DeleteMessageForActorAsync(
                lastSent.Summary.ThreadId,
                firstMsgId,
                CloudChatStoreService.BuildMerchantStoreActorKey("loja-atacante"));
            Assert.IsFalse(crossStoreDelete, "CRÍTICO B: Outra loja jamais pode ocultar mensagens em thread de terceiro.");
        }
        finally
        {
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
