using System.Net;
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
public sealed class AdminClientVipVerificationTests
{
    private sealed class NoOpHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
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

    private static (
        string TempDir,
        ClothingStoreRepository DefaultRepo,
        MultiTenantStoreManager Resolver,
        MultiTenantGatewayService Gateway,
        CloudAccountRegistryService Registry,
        Pbkdf2PasswordHasher Hasher) CreateHost()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink_vip_verify_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var defaultDbPath = Path.Combine(tempDir, "db.json");
        var hasher = new Pbkdf2PasswordHasher();
        var defaultRepo = new ClothingStoreRepository(hasher, defaultDbPath);
        var resolver = new MultiTenantStoreManager(defaultRepo, hasher, tempDir);
        var http = new HttpClient(new NoOpHttpHandler());
        var mpService = new MercadoPagoPixService(http, () => new MercadoPagoAppConfig("id", "secret", "wh"));
        var storage = new SupabaseImageStorageService(
            http,
            new ConfigurationBuilder().Build(),
            Path.Combine(tempDir, "wwwroot"));
        var gateway = new MultiTenantGatewayService(resolver, mpService, http, hasher, storage);
        var registry = new CloudAccountRegistryService(tempDir, hasher);

        return (tempDir, defaultRepo, resolver, gateway, registry, hasher);
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

    private static CloudCatalogSyncPayload BuildTenantPayload(
        string storeName,
        string storeSlug,
        string planTier = StoreSettings.PlanTierBasic)
    {
        return new CloudCatalogSyncPayload
        {
            Settings = new UpdateStoreSettingsRequest
            {
                StoreName = storeName,
                StoreSlug = storeSlug,
                WhatsappNumber = "5541999991111",
                StoreDescription = $"Catálogo oficial {storeName}",
                IsSiteActive = true,
                AutoSyncOnlineOnSave = true,
                PlanTier = planTier,
                ServerHostingMode = "PlatformShared"
            }
        };
    }

    [TestMethod]
    public async Task ApplyOrCreatePlanEntitlement_PreProvisionedAdminVip_ShouldBePreservedWhenClientLaterRegisters_WhileBlockingSelfPromotion()
    {
        var (tempDir, _, _, _, registry, hasher) = CreateHost();
        try
        {
            // 1) Admin pre-grants VIP before user ever synced account from client
            var preGranted = await registry.ApplyOrCreatePlanEntitlementAsync(
                targetEmail: "flavinhaasilva15@gmail.com",
                targetUsername: "flavia",
                storeName: "Loja flavia",
                storeSlug: "loja-flavia",
                planTier: StoreSettings.PlanTierPremium,
                durationDays: null,
                extendExistingDuration: false,
                activateSite: true,
                ct: CancellationToken.None,
                isCourtesyVip: true);

            Assert.IsNotNull(preGranted);
            Assert.AreEqual(StoreSettings.PlanTierPremium, preGranted!.PlanTier);
            Assert.IsTrue(preGranted.IsCourtesyVip);

            // Unclaimed entitlement must NOT authenticate with any password
            var preVerify = await registry.VerifyLoginAsync(new LoginRequest("flavinhaasilva15@gmail.com", "Qualquer@123"));
            Assert.IsFalse(preVerify.Verified);

            // 2) Client registers and posts UpsertAccountAsync WITHOUT SyncKey and with PlanTier = Basico
            var clientHash = hasher.HashPassword("SenhaFlavia@123");
            var claim = await registry.UpsertAccountAsync(new CloudAccountCredentialSyncDto(
                FullName: "Flavia Silva",
                Username: "flavia",
                Email: "flavinhaasilva15@gmail.com",
                PasswordHash: clientHash,
                Phone: "41999991111",
                StoreName: "Loja flavia",
                StoreSlug: "loja-flavia",
                PlanTier: StoreSettings.PlanTierBasic));

            Assert.IsTrue(claim.Success);
            var claimedMeta = await registry.GetPlanStatusWithAccountAsync("flavinhaasilva15@gmail.com", "flavia", "loja-flavia", "Loja flavia");
            Assert.IsNotNull(claimedMeta);
            Assert.AreEqual(StoreSettings.PlanTierPremium, claimedMeta!.Value.Account.PlanTier);
            Assert.IsTrue(claimedMeta.Value.Account.IsCourtesyVip);

            // Client can now authenticate AND receives the preserved Premium VIP
            var postVerify = await registry.VerifyLoginAsync(new LoginRequest("flavia", "SenhaFlavia@123"));
            Assert.IsTrue(postVerify.Verified);
            Assert.AreEqual(StoreSettings.PlanTierPremium, postVerify.Account?.PlanTier);

            // 3) Unrelated account attempting self-promotion without SyncKey stays Basico
            var attackerUpsert = await registry.UpsertAccountAsync(new CloudAccountCredentialSyncDto(
                FullName: "Outro Usuario",
                Username: "outrouser",
                Email: "outro@gmail.com",
                PasswordHash: hasher.HashPassword("Outro@123"),
                StoreName: "Outra Loja",
                StoreSlug: "outra-loja",
                PlanTier: StoreSettings.PlanTierPremium),
                hasValidSyncKey: false);

            Assert.IsTrue(attackerUpsert.Success);
            var attackerStatus = await registry.GetPlanStatusAsync("outro@gmail.com", "outrouser", "outra-loja", "Outra Loja");
            Assert.IsNotNull(attackerStatus);
            Assert.AreEqual(StoreSettings.PlanTierBasic, attackerStatus!.PlanTier);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task ApplyRemoteVip_RevokeAndRegrant_ShouldReactivateBothCloudAndTenant_AndMatchCompactSlug()
    {
        var (tempDir, defaultRepo, _, gateway, registry, _) = CreateHost();
        try
        {
            var publish = await gateway.PublishStoreAsync(
                BuildTenantPayload("Loja flavia", "loja-flavia", StoreSettings.PlanTierBasic),
                requestedSlug: "loja-flavia",
                syncKeyHeader: "secret",
                publishTokenHeader: null,
                hasValidSyncKey: true,
                authenticatedOwnerEmail: "flavinhaasilva15@gmail.com",
                publicServerOriginUrl: "https://youlink-loja.onrender.com");
            Assert.IsTrue(publish.Success);

            // Grant Intermediario
            var granted1 = await CloudPlatformPlanSyncHelper.ApplyRemoteVipAsync(
                new RemoteApplyVipRequest(
                    PlanTier: StoreSettings.PlanTierIntermediate,
                    DurationDays: 30,
                    ExtendExistingDuration: false,
                    ActivateSite: true,
                    StoreName: "Loja flavia",
                    TargetUsername: "flavia",
                    TargetEmail: "flavinhaasilva15@gmail.com",
                    IsCourtesyVip: true),
                defaultRepo,
                gateway,
                registry,
                CancellationToken.None);
            Assert.IsNotNull(granted1);
            Assert.AreEqual(StoreSettings.PlanTierIntermediate, granted1!.PlanTier);

            // Revoke to Basico
            var revoked = await CloudPlatformPlanSyncHelper.ApplyRemoteVipAsync(
                new RemoteApplyVipRequest(
                    PlanTier: StoreSettings.PlanTierBasic,
                    DurationDays: null,
                    ExtendExistingDuration: false,
                    ActivateSite: true,
                    StoreName: "Loja flavia",
                    TargetUsername: "flavia",
                    TargetEmail: "flavinhaasilva15@gmail.com",
                    IsCourtesyVip: false),
                defaultRepo,
                gateway,
                registry,
                CancellationToken.None);
            Assert.IsNotNull(revoked);
            Assert.AreEqual(StoreSettings.PlanTierBasic, revoked!.PlanTier);

            // Re-grant Premium (must clear prior PlanRevokedAt so tenant & cloud agree on Premium)
            await Task.Delay(20);
            var regranted = await CloudPlatformPlanSyncHelper.ApplyRemoteVipAsync(
                new RemoteApplyVipRequest(
                    PlanTier: StoreSettings.PlanTierPremium,
                    DurationDays: null,
                    ExtendExistingDuration: false,
                    ActivateSite: true,
                    StoreName: "Loja flavia",
                    TargetUsername: "flavia",
                    TargetEmail: "flavinhaasilva15@gmail.com",
                    IsCourtesyVip: true),
                defaultRepo,
                gateway,
                registry,
                CancellationToken.None);
            Assert.IsNotNull(regranted);
            Assert.AreEqual(StoreSettings.PlanTierPremium, regranted!.PlanTier);
            Assert.IsNull(regranted.PlanExpiresAt);

            // Compact slug lookup ("lojaflavia" -> "loja-flavia") + check-in telemetry
            var resolved = await CloudPlatformPlanSyncHelper.ResolveCloudPlanStatusAsync(
                email: null,
                username: null,
                storeSlug: "lojaflavia",
                storeName: null,
                clientPlanTier: StoreSettings.PlanTierBasic,
                clientPlanExpiresAt: null,
                clientAppVersion: AppUpdateCatalog.CurrentAppVersion,
                defaultRepo,
                gateway,
                registry,
                CancellationToken.None);

            Assert.AreEqual(StoreSettings.PlanTierPremium, resolved.PlanTier);
            Assert.IsTrue(resolved.IsCourtesyVip);
            Assert.IsNull(resolved.PlanRevokedAt);

            var meta = await registry.GetPlanStatusWithAccountAsync("flavinhaasilva15@gmail.com", null, null, null);
            Assert.IsNotNull(meta);
            Assert.IsNotNull(meta!.Value.Account.LastClientCheckInAt);
            Assert.AreEqual(StoreSettings.PlanTierBasic, meta.Value.Account.LastReportedClientPlanTier);
            Assert.AreEqual(AppUpdateCatalog.CurrentAppVersion, meta.Value.Account.LastClientAppVersion);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task VerifyAndRepairClientVip_ShouldAutoRepairDivergence_ConfirmClientAck_AndProtectPaidSubscription()
    {
        var (tempDir, defaultRepo, _, gateway, registry, _) = CreateHost();
        try
        {
            await gateway.PublishStoreAsync(
                BuildTenantPayload("Loja flavia", "loja-flavia", StoreSettings.PlanTierBasic),
                requestedSlug: "loja-flavia",
                syncKeyHeader: "secret",
                publishTokenHeader: null,
                hasValidSyncKey: true,
                authenticatedOwnerEmail: "flavinhaasilva15@gmail.com",
                publicServerOriginUrl: "https://youlink-loja.onrender.com");

            // Admin expects Premium, but server & tenant still show Basico -> AutoRepaired
            var repairedResult = await CloudPlatformPlanSyncHelper.VerifyAndRepairClientVipAsync(
                new RemoteVerifyClientVipRequest(
                    TargetEmail: "flavinhaasilva15@gmail.com",
                    TargetUsername: "flavia",
                    StoreName: "Loja flavia",
                    StoreSlug: "loja-flavia",
                    ExpectedAdminPlanTier: StoreSettings.PlanTierPremium,
                    ExpectedAdminExpiresAt: null,
                    AutoRepairIfDivergent: true,
                    IsCourtesyVip: true),
                defaultRepo,
                gateway,
                registry,
                "https://youlink-loja.onrender.com",
                CancellationToken.None);

            Assert.IsTrue(repairedResult.AutoRepaired);
            Assert.AreEqual("AutoRepaired", repairedResult.StatusCode);
            Assert.IsTrue(repairedResult.HasActiveServerVip);
            Assert.AreEqual(StoreSettings.PlanTierPremium, repairedResult.ServerEffectivePlanTier);
            Assert.AreEqual(StoreSettings.PlanTierPremium, repairedResult.PublishedTenantPlanTier);

            // Client reports Basico on check-in -> ServerReadyAwaitingClient
            await registry.RecordClientCheckInAsync(
                "flavinhaasilva15@gmail.com",
                "flavia",
                "loja-flavia",
                "Loja flavia",
                StoreSettings.PlanTierBasic,
                null,
                "1.0.48");

            var awaitingResult = await CloudPlatformPlanSyncHelper.VerifyAndRepairClientVipAsync(
                new RemoteVerifyClientVipRequest(
                    TargetEmail: "flavinhaasilva15@gmail.com",
                    TargetUsername: "flavia",
                    StoreName: "Loja flavia",
                    StoreSlug: "loja-flavia",
                    ExpectedAdminPlanTier: StoreSettings.PlanTierPremium,
                    ExpectedAdminExpiresAt: null,
                    AutoRepairIfDivergent: true,
                    IsCourtesyVip: true),
                defaultRepo,
                gateway,
                registry,
                "https://youlink-loja.onrender.com",
                CancellationToken.None);

            Assert.IsFalse(awaitingResult.AutoRepaired);
            Assert.AreEqual("ServerReadyAwaitingClient", awaitingResult.StatusCode);
            Assert.AreEqual(StoreSettings.PlanTierBasic, awaitingResult.LastReportedClientPlanTier);

            // Client applies Premium locally and sends client-vip-ack -> VerifiedSynced
            await registry.RecordClientCheckInAsync(
                "flavinhaasilva15@gmail.com",
                "flavia",
                "loja-flavia",
                "Loja flavia",
                StoreSettings.PlanTierPremium,
                null,
                "1.0.48");

            var verifiedResult = await CloudPlatformPlanSyncHelper.VerifyAndRepairClientVipAsync(
                new RemoteVerifyClientVipRequest(
                    TargetEmail: "flavinhaasilva15@gmail.com",
                    TargetUsername: "flavia",
                    StoreName: "Loja flavia",
                    StoreSlug: "loja-flavia",
                    ExpectedAdminPlanTier: StoreSettings.PlanTierPremium,
                    ExpectedAdminExpiresAt: null,
                    AutoRepairIfDivergent: true,
                    IsCourtesyVip: true),
                defaultRepo,
                gateway,
                registry,
                "https://youlink-loja.onrender.com",
                CancellationToken.None);

            Assert.AreEqual("VerifiedSynced", verifiedResult.StatusCode);
            Assert.AreEqual(StoreSettings.PlanTierPremium, verifiedResult.LastReportedClientPlanTier);
            Assert.AreEqual("1.0.48", verifiedResult.LastClientAppVersion);

            // Paid Mercado Pago VIP on server must NOT be downgraded when Admin local says Basico
            await registry.ApplyOrCreatePlanEntitlementAsync(
                targetEmail: "pago@gmail.com",
                targetUsername: "pagouser",
                storeName: "Loja Paga",
                storeSlug: "loja-paga",
                planTier: StoreSettings.PlanTierPremium,
                durationDays: 30,
                extendExistingDuration: false,
                activateSite: true,
                ct: CancellationToken.None,
                isCourtesyVip: false,
                isRecurringSubscription: true);

            var paidCheck = await CloudPlatformPlanSyncHelper.VerifyAndRepairClientVipAsync(
                new RemoteVerifyClientVipRequest(
                    TargetEmail: "pago@gmail.com",
                    TargetUsername: "pagouser",
                    StoreName: "Loja Paga",
                    StoreSlug: "loja-paga",
                    ExpectedAdminPlanTier: StoreSettings.PlanTierBasic,
                    ExpectedAdminExpiresAt: null,
                    AutoRepairIfDivergent: true,
                    IsCourtesyVip: false),
                defaultRepo,
                gateway,
                registry,
                "https://youlink-loja.onrender.com",
                CancellationToken.None);

            Assert.IsFalse(paidCheck.AutoRepaired);
            Assert.IsTrue(paidCheck.HasActiveServerVip);
            Assert.AreEqual(StoreSettings.PlanTierPremium, paidCheck.ServerEffectivePlanTier);

            // Null storeName + null storeSlug must NOT throw NullReferenceException
            var nullStoreVip = await CloudPlatformPlanSyncHelper.ApplyRemoteVipAsync(
                new RemoteApplyVipRequest(
                    PlanTier: StoreSettings.PlanTierIntermediate,
                    DurationDays: 30,
                    ActivateSite: true,
                    StoreName: null,
                    TargetUsername: "sem.loja",
                    TargetEmail: null,
                    IsCourtesyVip: false),
                defaultRepo,
                gateway,
                registry,
                CancellationToken.None);
            Assert.IsNotNull(nullStoreVip);
            Assert.AreEqual(StoreSettings.PlanTierIntermediate, nullStoreVip!.PlanTier);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public void ClientAndAdminCodebase_ShouldEnforceVipVerificationAndResilientSync()
    {
        var root = ResolveRepoRoot();

        var mauiGateway = File.ReadAllText(Path.Combine(root, "Services", "MauiAdminGateway.cs"));
        Assert.IsTrue(mauiGateway.Contains("BuildCentralCloudCandidateUrls", StringComparison.Ordinal));
        Assert.IsTrue(mauiGateway.Contains("/api/cloud/client-vip-ack", StringComparison.Ordinal));
        Assert.IsTrue(mauiGateway.Contains("clientPlanTier=", StringComparison.Ordinal));
        Assert.IsTrue(mauiGateway.Contains("clientAppVersion=", StringComparison.Ordinal));
        Assert.IsFalse(mauiGateway.Contains("timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));", StringComparison.Ordinal));

        var mainLayout = File.ReadAllText(Path.Combine(root, "Components", "Layout", "MainLayout.razor"));
        Assert.IsTrue(mainLayout.Contains("EnsureCloudPlanBackgroundMonitor", StringComparison.Ordinal));

        var storeSettings = File.ReadAllText(Path.Combine(root, "Components", "Pages", "StoreSettingsPage.razor"));
        Assert.IsTrue(storeSettings.Contains("VerifyCloudVipNowAsync", StringComparison.Ordinal));
        Assert.IsTrue(storeSettings.Contains("PlanEntitlementActivated", StringComparison.Ordinal));

        var homePage = File.ReadAllText(Path.Combine(root, "Components", "Pages", "Home.razor"));
        Assert.IsTrue(homePage.Contains("PlanEntitlementActivated", StringComparison.Ordinal));

        var salesPage = File.ReadAllText(Path.Combine(root, "Components", "Pages", "SalesReportPage.razor"));
        Assert.IsTrue(salesPage.Contains("PlanEntitlementActivated", StringComparison.Ordinal));

        var plansPage = File.ReadAllText(Path.Combine(root, "Components", "Pages", "PlansPage.razor"));
        Assert.IsTrue(plansPage.Contains("PlanEntitlementActivated", StringComparison.Ordinal));

        var appXamlCs = File.ReadAllText(Path.Combine(root, "App.xaml.cs"));
        Assert.IsTrue(appXamlCs.Contains("RefreshPlanFromCentralCloudBestEffortAsync", StringComparison.Ordinal));

        var usersPagePath = Path.Combine(root, "src", "ClothingStore.AdminApp", "Components", "Pages", "UsersAndVipPage.razor");
        if (File.Exists(usersPagePath))
        {
            var usersPage = File.ReadAllText(usersPagePath);
            Assert.IsTrue(usersPage.Contains("VerifySingleClientVipAsync", StringComparison.Ordinal));
            Assert.IsTrue(usersPage.Contains("VerifyAllClientsVipAsync", StringComparison.Ordinal));
            Assert.IsTrue(usersPage.Contains("LastVipSyncWarning", StringComparison.Ordinal));
        }

        var webProgram = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Web", "Program.cs"));
        Assert.IsTrue(webProgram.Contains("/api/platform-sync/verify-client-vip", StringComparison.Ordinal));
        Assert.IsTrue(webProgram.Contains("/api/cloud/client-vip-ack", StringComparison.Ordinal));

        var apiProgram = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Api", "Program.cs"));
        Assert.IsTrue(apiProgram.Contains("/api/platform-sync/verify-client-vip", StringComparison.Ordinal));
        Assert.IsTrue(apiProgram.Contains("/api/cloud/client-vip-ack", StringComparison.Ordinal));
    }
}
