using ClothingStore.Application.Interfaces;
using ClothingStore.Application.Services;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Enums;
using ClothingStore.Domain.Exceptions;
using ClothingStore.Infrastructure.Persistence;
using ClothingStore.Infrastructure.Security;
using ClothingStore.Infrastructure.Storage;
using ClothingStore.Shared.DTOs;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClothingStore.UnitTests;

[TestClass]
public class MultiAccountStoreIsolationTests
{
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
        SwitchableClothingStoreRepository Repo,
        AuthService Auth,
        CatalogService Catalog,
        EmployeeAndSettingsService Settings,
        ProductManagementService Products,
        StockManagementService Stock,
        Pbkdf2PasswordHasher Hasher,
        JwtTokenService Jwt,
        string TempDir,
        string DefaultDbPath) CreateStack()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink_multi_store_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var defaultDbPath = Path.Combine(tempDir, "clothingstore_db.json");

        var hasher = new Pbkdf2PasswordHasher();
        var repo = new SwitchableClothingStoreRepository(hasher, defaultDbPath, enablePlatformOwnerCompanion: false);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"] = "unit-test-secret-key-with-at-least-32-characters-12345"
            })
            .Build();
        var jwt = new JwtTokenService(config);
        var storage = new SupabaseImageStorageService(new HttpClient(), config, Path.Combine(tempDir, "wwwroot"));

        return (
            repo,
            new AuthService(repo, hasher, jwt),
            new CatalogService(repo),
            new EmployeeAndSettingsService(repo, hasher, storage),
            new ProductManagementService(repo, storage),
            new StockManagementService(repo),
            hasher,
            jwt,
            tempDir,
            defaultDbPath);
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
    public async Task RegisterSecondAccount_GetsIsolatedStoreAsAdmin_WithEmptyCatalogZeroMovementsAndCleanPlan()
    {
        var (repo, auth, catalog, settings, products, stock, _, _, tempDir, _) = CreateStack();
        try
        {
            // Conta 1 cria e personaliza a Loja A
            var h1 = await repo.ProvisionIsolatedWorkspaceForNewAccountAsync("giovanni@exemplo.com", "giovanni");
            Assert.IsFalse(h1.CreatedNewFile);
            Assert.AreEqual(SwitchableClothingStoreRepository.DefaultWorkspaceId, h1.WorkspaceId);

            var loginA = await auth.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                "Giovanni",
                "giovanni@exemplo.com",
                "123456",
                Username: "giovanni"));

            Assert.IsTrue(loginA.Employee.IsAdmin);
            Assert.AreEqual(RoleNames.Admin, loginA.Employee.RoleName);

            await settings.UpdateStoreSettingsAsync(
                loginA.Employee.EmployeeId,
                new UpdateStoreSettingsRequest
                {
                    StoreName = "Loja de Roupas da Flavia",
                    WhatsappNumber = "5511999990001",
                    StoreDescription = "Catálogo da loja A",
                    OwnServerSyncSecret = "secret-loja-a"
                });

            var storeEntityA = await repo.GetStoreSettingsAsync();
            storeEntityA.UpdatePlanAndDesign("Premium", employeeId: loginA.Employee.EmployeeId);
            await repo.UpdateStoreSettingsAsync(storeEntityA);

            var prodA = await products.CreateProductAsync(
                new SaveProductRequest
                {
                    CategoryName = "Camisas",
                    Name = "Camisa Tricot Modal Nóbrega",
                    BasePrice = 149.90m,
                    InitialColor = "Branco Puro",
                    InitialSize = "M",
                    InitialStockQuantity = 7
                },
                loginA.Employee.EmployeeId);

            await stock.RegisterMovementAsync(
                loginA.Employee.EmployeeId,
                new CreateStockMovementRequest
                {
                    ProductVariantId = prodA.Variants[0].Id,
                    MovementType = "OUT",
                    Quantity = 2,
                    Reason = "Retirada na loja A"
                });

            Assert.AreEqual(1, (await products.GetAdminProductsAsync()).Count);
            Assert.AreEqual(2, (await stock.GetMovementsAsync()).Count);
            var keyA = repo.ResolveScopedPreferenceKey("youlink_sync_url");

            // Conta 2 cria conta com outro e-mail — DEVE abrir uma loja própria limpa como Admin (nunca Equipe!)
            var h2 = await repo.ProvisionIsolatedWorkspaceForNewAccountAsync("maria@novaloja.com", "maria.boutique");
            Assert.IsTrue(h2.CreatedNewFile);
            Assert.IsTrue(h2.WorkspaceId.StartsWith("ws_", StringComparison.Ordinal));
            Assert.AreEqual(SwitchableClothingStoreRepository.DefaultWorkspaceId, h2.PreviousWorkspaceId);

            var loginB = await auth.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                "Maria Silva",
                "maria@novaloja.com",
                "654321",
                Username: "maria.boutique"));

            Assert.IsTrue(loginB.Employee.IsAdmin, "Novo usuário deve ser Admin/dono da própria loja.");
            Assert.AreEqual(RoleNames.Admin, loginB.Employee.RoleName);
            Assert.IsTrue(loginB.Employee.CanManageProducts);
            Assert.IsTrue(loginB.Employee.CanManageStock);

            var publicB = await catalog.GetPublicStoreSettingsAsync();
            var adminB = await settings.GetAdminStoreSettingsAsync();
            Assert.AreEqual("Minha Loja", publicB.StoreName);
            Assert.AreEqual("Basico", publicB.PlanTier);
            Assert.IsFalse(adminB.HasOwnServerSyncSecret);
            Assert.AreEqual(0, (await products.GetAdminProductsAsync()).Count);
            Assert.AreEqual(0, (await products.GetAllCategoriesAsync()).Count);
            Assert.AreEqual(0, (await stock.GetMovementsAsync()).Count);
            Assert.AreEqual(1, (await settings.GetEmployeesAsync()).Count);

            var keyB = repo.ResolveScopedPreferenceKey("youlink_sync_url");
            Assert.IsFalse(string.Equals(keyA, keyB, StringComparison.Ordinal));
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task SwitchWorkspace_BetweenAccountsAndAcrossRestart_PreservesIsolatedStores()
    {
        var (repo, auth, catalog, settings, products, stock, hasher, _, tempDir, defaultDbPath) = CreateStack();
        try
        {
            var h1 = await repo.ProvisionIsolatedWorkspaceForNewAccountAsync("dono1@loja.com", "dono1");
            var login1 = await auth.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                "Dono Um", "dono1@loja.com", "1234", Username: "dono1"));
            await settings.UpdateStoreSettingsAsync(login1.Employee.EmployeeId, new UpdateStoreSettingsRequest
            {
                StoreName = "Atelier Verano",
                WhatsappNumber = "5511911111111",
                StoreDescription = "Catálogo Atelier Verano"
            });
            await products.CreateProductAsync(new SaveProductRequest
            {
                CategoryName = "Blazers",
                Name = "Blazer Paris",
                BasePrice = 289.90m,
                InitialColor = "Off-White",
                InitialSize = "M",
                InitialStockQuantity = 5
            }, login1.Employee.EmployeeId);

            var h2 = await repo.ProvisionIsolatedWorkspaceForNewAccountAsync("dono2@loja.com", "dono2");
            var login2 = await auth.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                "Dono Dois", "dono2@loja.com", "5678", Username: "dono2"));
            await settings.UpdateStoreSettingsAsync(login2.Employee.EmployeeId, new UpdateStoreSettingsRequest
            {
                StoreName = "Boutique Horizonte",
                WhatsappNumber = "5521922222222",
                StoreDescription = "Catálogo Boutique Horizonte"
            });
            await products.CreateProductAsync(new SaveProductRequest
            {
                CategoryName = "Vestidos",
                Name = "Vestido Midi Seda",
                BasePrice = 349.90m,
                InitialColor = "Esmeralda",
                InitialSize = "P",
                InitialStockQuantity = 3
            }, login2.Employee.EmployeeId);

            var changedEvents = new List<string>();
            repo.WorkspaceChanged += id => changedEvents.Add(id);

            // Alterna para Conta 1 via username
            var switched1 = await repo.SwitchToWorkspaceForAccountAsync("dono1");
            Assert.AreEqual(h1.WorkspaceId, switched1);
            Assert.AreEqual("Atelier Verano", (await catalog.GetPublicStoreSettingsAsync()).StoreName);
            Assert.AreEqual("Blazer Paris", (await products.GetAdminProductsAsync()).Single().Name);

            // Alterna para Conta 2 via e-mail
            var switched2 = await repo.SwitchToWorkspaceForAccountAsync("DONO2@LOJA.COM");
            Assert.AreEqual(h2.WorkspaceId, switched2);
            Assert.AreEqual("Boutique Horizonte", (await catalog.GetPublicStoreSettingsAsync()).StoreName);
            Assert.AreEqual("Vestido Midi Seda", (await products.GetAdminProductsAsync()).Single().Name);
            Assert.AreEqual(2, changedEvents.Count);

            // Simula reinicialização do app com nova instância apontando para o mesmo diretório
            var repoAfterRestart = new SwitchableClothingStoreRepository(
                hasher,
                defaultDbPath,
                enablePlatformOwnerCompanion: false);
            var catalogAfterRestart = new CatalogService(repoAfterRestart);

            var restored = await repoAfterRestart.SwitchToWorkspaceByIdAsync(
                h2.WorkspaceId,
                login2.Employee.EmployeeId,
                "dono2@loja.com");
            Assert.AreEqual(h2.WorkspaceId, restored);
            Assert.AreEqual("Boutique Horizonte", (await catalogAfterRestart.GetPublicStoreSettingsAsync()).StoreName);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task ProvisionWorkspace_RejectsDuplicateAcrossWorkspaces_AndRollbackRemovesOrphanDb()
    {
        var (repo, auth, _, _, products, _, _, _, tempDir, _) = CreateStack();
        try
        {
            await repo.ProvisionIsolatedWorkspaceForNewAccountAsync("first@store.com", "firstuser");
            var first = await auth.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                "First User", "first@store.com", "1234", Username: "firstuser"));
            await products.CreateProductAsync(new SaveProductRequest
            {
                CategoryName = "Tops",
                Name = "Top Cropped",
                BasePrice = 79.90m,
                InitialColor = "Preto",
                InitialSize = "P",
                InitialStockQuantity = 4
            }, first.Employee.EmployeeId);

            // E-mail ou username já existente deve falhar sem criar novo arquivo nem trocar workspace
            await Assert.ThrowsExceptionAsync<DomainException>(async () =>
                await repo.ProvisionIsolatedWorkspaceForNewAccountAsync("FIRST@store.com", "anotheruser"));
            Assert.AreEqual(SwitchableClothingStoreRepository.DefaultWorkspaceId, repo.ActiveWorkspaceId);

            await Assert.ThrowsExceptionAsync<DomainException>(async () =>
                await repo.ProvisionIsolatedWorkspaceForNewAccountAsync("second@store.com", "FirstUser"));
            Assert.AreEqual(SwitchableClothingStoreRepository.DefaultWorkspaceId, repo.ActiveWorkspaceId);

            // Provisiona segundo workspace e testa Rollback
            var handle = await repo.ProvisionIsolatedWorkspaceForNewAccountAsync("pending@store.com", "pendinguser");
            var createdDbPath = repo.ActiveStorageFilePath;
            Assert.IsTrue(File.Exists(createdDbPath));

            await repo.RollbackProvisionedWorkspaceAsync(handle, "pending@store.com", "pendinguser");

            Assert.AreEqual(SwitchableClothingStoreRepository.DefaultWorkspaceId, repo.ActiveWorkspaceId);
            Assert.IsFalse(File.Exists(createdDbPath), "Rollback deve remover o arquivo de workspace órfão.");
            Assert.IsNull(await repo.SwitchToWorkspaceForAccountAsync("pending@store.com"));
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public void MauiUiAndGatewayWiring_IsolateOnboardingAndScopePreferencesPerWorkspace()
    {
        var root = ResolveRepoRoot();

        var loginRazor = File.ReadAllText(Path.Combine(root, "Components", "Pages", "Login.razor"));
        Assert.IsTrue(loginRazor.Contains("@inject StoreBrandStateService BrandState", StringComparison.Ordinal));
        Assert.IsFalse(loginRazor.Contains("_initStoreName = store.StoreName;", StringComparison.Ordinal),
            "Login.razor não pode pré-preencher o nome da loja nova com o da conta anterior.");
        Assert.IsFalse(loginRazor.Contains("_initStoreWhatsapp = store.WhatsappNumber;", StringComparison.Ordinal),
            "Login.razor não pode pré-preencher o WhatsApp da loja nova com o da conta anterior.");
        Assert.IsTrue(loginRazor.Contains("await BrandState.ReloadAsync();", StringComparison.Ordinal));

        var mainLayout = File.ReadAllText(Path.Combine(root, "Components", "Layout", "MainLayout.razor"));
        Assert.IsTrue(mainLayout.Contains("await RefreshStoreInfoAsync();", StringComparison.Ordinal),
            "MainLayout deve atualizar _storeName/_storeSlug e BrandState ao trocar de conta.");

        var gateway = File.ReadAllText(Path.Combine(root, "Services", "MauiAdminGateway.cs"));
        Assert.IsTrue(gateway.Contains("ProvisionIsolatedWorkspaceForNewAccountAsync", StringComparison.Ordinal));
        Assert.IsTrue(gateway.Contains("SwitchWorkspaceForIdentifierAsync", StringComparison.Ordinal));
        Assert.IsTrue(gateway.Contains("ResolveScopedPreferenceKey", StringComparison.Ordinal));
        Assert.IsTrue(gateway.Contains("IsIsolatedSecondaryWorkspace", StringComparison.Ordinal));

        var cloudSync = File.ReadAllText(Path.Combine(root, "Services", "GoogleCloudSyncService.cs"));
        Assert.IsTrue(cloudSync.Contains("WorkspaceChanged", StringComparison.Ordinal));
        Assert.IsTrue(cloudSync.Contains("ResolveCentralUrlKey", StringComparison.Ordinal));
        Assert.IsTrue(cloudSync.Contains("ResolvePublishTokenKey", StringComparison.Ordinal));

        var mauiProgram = File.ReadAllText(Path.Combine(root, "MauiProgram.cs"));
        Assert.IsTrue(mauiProgram.Contains("SwitchableClothingStoreRepository", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SingleOwnerUiCleanup_ShouldRemoveTeamAndDevClutterAcrossPages()
    {
        var root = ResolveRepoRoot();

        Assert.IsFalse(File.Exists(Path.Combine(root, "Components", "Pages", "Employees.razor")),
            "Página /funcionarios deve ser removida em aplicativo com 1 dono por loja.");

        var navMenu = File.ReadAllText(Path.Combine(root, "Components", "Layout", "NavMenu.razor"));
        Assert.IsFalse(navMenu.Contains("/funcionarios", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(navMenu.Contains("\"Equipe\"", StringComparison.Ordinal));

        var mainLayout = File.ReadAllText(Path.Combine(root, "Components", "Layout", "MainLayout.razor"));
        Assert.IsFalse(mainLayout.Contains("\"Equipe\"", StringComparison.Ordinal));

        var profileModal = File.ReadAllText(Path.Combine(root, "Components", "Pages", "UserProfileModal.razor"));
        Assert.IsFalse(profileModal.Contains("\"Equipe\"", StringComparison.Ordinal));
        Assert.IsFalse(profileModal.Contains("Cargo / Nível de Acesso", StringComparison.Ordinal));

        var stockControl = File.ReadAllText(Path.Combine(root, "Components", "Pages", "StockControl.razor"));
        Assert.IsFalse(stockControl.Contains("Funcionário Responsável", StringComparison.Ordinal));
        Assert.IsTrue(stockControl.Contains("<th>Responsável</th>", StringComparison.Ordinal));
        Assert.IsFalse(stockControl.Contains("2. Variante (Cor / Tamanho)", StringComparison.Ordinal));
        Assert.IsFalse(stockControl.Contains("Selecione uma variante ao lado para retirar ou ajustar.", StringComparison.Ordinal));
        Assert.IsTrue(stockControl.Contains("stk-live-balance", StringComparison.Ordinal));
        Assert.IsTrue(stockControl.Contains("stk-form-grid", StringComparison.Ordinal));
        Assert.IsTrue(stockControl.Contains("FormatVariantDisplay", StringComparison.Ordinal));

        var portal = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Web", "Components", "Shared", "PlatformPortalView.razor"));
        Assert.IsFalse(portal.Contains("controle de equipe", StringComparison.OrdinalIgnoreCase));

        var storeSettings = File.ReadAllText(Path.Combine(root, "Components", "Pages", "StoreSettingsPage.razor"));
        Assert.IsFalse(storeSettings.Contains("Para Devs", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(storeSettings.Contains("oauthplayground", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(storeSettings.Contains("Segue o Android", StringComparison.Ordinal));

        var loginRazor = File.ReadAllText(Path.Combine(root, "Components", "Pages", "Login.razor"));
        Assert.IsFalse(loginRazor.Contains("_setupSyncNotice", StringComparison.Ordinal));
        Assert.IsFalse(loginRazor.Contains("Abrir Catálogo Público (/loja/", StringComparison.Ordinal));

        var plansPage = File.ReadAllText(Path.Combine(root, "Components", "Pages", "PlansPage.razor"));
        Assert.IsFalse(plansPage.Contains("Verificar Conta Google Pro (-R$ 5/mês)", StringComparison.Ordinal));

        var planCheckout = File.ReadAllText(Path.Combine(root, "Components", "Pages", "PlanCheckoutPage.razor"));
        Assert.IsFalse(planCheckout.Contains("Pix Direto (Aprovação Admin)", StringComparison.Ordinal));
        Assert.IsFalse(planCheckout.Contains("Pagar com Pix Direto (Aprovação Admin)", StringComparison.Ordinal));
        Assert.IsFalse(planCheckout.Contains("Confirmar Compra e Liberar Chave Pix", StringComparison.Ordinal));

        var notFound = File.ReadAllText(Path.Combine(root, "Components", "Pages", "NotFound.razor"));
        Assert.IsTrue(notFound.Contains("Página não encontrada", StringComparison.Ordinal));

        var mauiGateway = File.ReadAllText(Path.Combine(root, "Services", "MauiAdminGateway.cs"));
        Assert.IsTrue(mauiGateway.Contains("TryRedeemGiftCardViaClientAsync", StringComparison.Ordinal));
        Assert.IsTrue(mauiGateway.Contains("IsGiftCardNotFoundError", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GiftCard_CreatedInDefaultAdminWorkspace_CanBeRedeemedInIsolatedSecondaryStoreWorkspace()
    {
        var (repo, auth, _, settings, _, _, _, _, tempDir, _) = CreateStack();
        try
        {
            await repo.ProvisionIsolatedWorkspaceForNewAccountAsync("admin1@gmail.com", "admin1");
            var login1 = await auth.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                "Admin Um",
                "admin1@gmail.com",
                "SenhaForte@123",
                Username: "admin1"));
            await settings.UpdateStoreSettingsAsync(login1.Employee.EmployeeId, new UpdateStoreSettingsRequest
            {
                StoreName = "Loja Principal",
                WhatsappNumber = "5511999990001",
                StoreDescription = "Catálogo Loja Principal"
            });

            // Admin gera o Gift Card no workspace padrão (clothingstore_db.json), exatamente como o YouLink Admin faz
            var giftCard = PlanActivationCode.Create(
                targetPlanTier: "Premium",
                durationDays: 7,
                customCode: "LUXO-PREM-7D-MAXVFZH6",
                notes: "Gift Card de 7 dias gerado no YouLink Admin");
            await repo.SavePlanActivationCodeAsync(giftCard);

            // Segunda conta cria e entra em seu workspace isolado ("Minha Loja" / "gio")
            await repo.ProvisionIsolatedWorkspaceForNewAccountAsync("giovannipreal@gmail.com", "gio");
            var secondLogin = await auth.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                "gio",
                "giovannipreal@gmail.com",
                "SenhaForte@456",
                Username: "gio"));

            Assert.IsFalse(repo.IsDefaultWorkspace);
            var settingsBefore = await settings.GetAdminStoreSettingsAsync();
            Assert.AreEqual("Minha Loja", settingsBefore.StoreName);
            Assert.AreEqual("Basico", settingsBefore.PlanTier);

            // Resgata o Gift Card estando no workspace isolado da segunda loja
            var redeemResponse = await settings.RedeemActivationCodeAsync(
                new RedeemActivationCodeRequest { Code = "LUXO-PREM-7D-MAXVFZH6" },
                secondLogin.Employee.EmployeeId);

            Assert.AreEqual("LUXO-PREM-7D-MAXVFZH6", redeemResponse.Code);
            Assert.AreEqual("Premium", redeemResponse.ActivatedPlanTier);
            Assert.AreEqual(7, redeemResponse.DurationDays);
            Assert.AreEqual("Premium", redeemResponse.UpdatedSettings.PlanTier);
            Assert.AreEqual("LUXO-PREM-7D-MAXVFZH6", redeemResponse.UpdatedSettings.ActiveGiftCardCode);

            // Verifica que o Gift Card ficou marcado como resgatado também no banco padrão (visível ao YouLink Admin)
            await repo.SwitchToWorkspaceForAccountAsync("admin1");
            Assert.IsTrue(repo.IsDefaultWorkspace);
            var primarySettings = await settings.GetAdminStoreSettingsAsync();
            Assert.AreEqual("Loja Principal", primarySettings.StoreName);
            Assert.IsFalse(string.Equals("LUXO-PREM-7D-MAXVFZH6", primarySettings.ActiveGiftCardCode, StringComparison.OrdinalIgnoreCase));

            var codeInPrimaryDb = await repo.GetPlanActivationCodeByCodeAsync("LUXO-PREM-7D-MAXVFZH6");
            Assert.IsNotNull(codeInPrimaryDb);
            Assert.IsTrue(codeInPrimaryDb!.IsRedeemed);
            Assert.AreEqual("Minha Loja", codeInPrimaryDb.RedeemedByStoreName);

            // Impede segundo resgate do mesmo Gift Card em qualquer loja
            var duplicateErrorThrown = false;
            try
            {
                await settings.RedeemActivationCodeAsync(
                    new RedeemActivationCodeRequest { Code = "LUXO-PREM-7D-MAXVFZH6" });
            }
            catch (DomainException)
            {
                duplicateErrorThrown = true;
            }

            Assert.IsTrue(duplicateErrorThrown, "O mesmo Gift Card não pode ser resgatado uma segunda vez.");
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task CentralPublishUrl_NeverUsesPlaceholderLojasMinhaloja_AndFallsBackToOfficialRenderServer()
    {
        Assert.IsTrue(StoreSettings.IsPlaceholderStoreUrl("https://lojas.minhaloja.com"));
        Assert.IsTrue(StoreSettings.IsPlaceholderStoreUrl("https://lojas.loja-de-eletronicos-do-gio.com"));
        Assert.IsFalse(StoreSettings.IsPlaceholderStoreUrl("https://youlink-loja.onrender.com"));

        var (repo, auth, _, settings, _, _, _, _, tempDir, defaultDbPath) = CreateStack();
        try
        {
            await repo.ProvisionIsolatedWorkspaceForNewAccountAsync("admin1@gmail.com", "admin1");
            var login1 = await auth.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                "Admin Um",
                "admin1@gmail.com",
                "SenhaForte@123",
                Username: "admin1"));
            await settings.UpdateStoreSettingsAsync(login1.Employee.EmployeeId, new UpdateStoreSettingsRequest
            {
                StoreName = "Loja Principal",
                WhatsappNumber = "5511999990001",
                StoreDescription = "Catálogo Loja Principal",
                CentralCatalogBaseUrl = "https://youlink-loja.onrender.com"
            });

            var primaryEntity = await repo.GetStoreSettingsAsync();
            PlatformOwnerSettingsStore.SaveFromStoreSettings(defaultDbPath, primaryEntity);

            await repo.ProvisionIsolatedWorkspaceForNewAccountAsync("giovannipreal@gmail.com", "gio");
            await auth.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                "gio",
                "giovannipreal@gmail.com",
                "SenhaForte@456",
                Username: "gio"));

            var storeEntity = await repo.GetStoreSettingsAsync();
            storeEntity.ConfigureCentralPublishing("https://lojas.minhaloja.com");
            Assert.IsNull(storeEntity.CentralCatalogBaseUrl, "Placeholder lojas.minhaloja.com jamais deve ser salvo como CentralCatalogBaseUrl.");

            // Mesmo quando carregado do companion platform_owner_settings.json do diretório pai (App_Data), a loja secundária recebe o servidor central oficial
            var applied = PlatformOwnerSettingsStore.ApplyToStoreSettingsIfMissingOrDefault(repo.ActiveStorageFilePath, storeEntity);
            Assert.IsTrue(applied);
            Assert.AreEqual("https://youlink-loja.onrender.com", storeEntity.CentralCatalogBaseUrl);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task LoginNormalization_UsernameWithAtPrefix_AndProfileUpdate_KeepWorkspaceIndexSynced_AndSucceedOnSingleLookup()
    {
        var (repo, auth, _, _, _, _, hasher, _, tempDir, defaultDbPath) = CreateStack();
        try
        {
            await repo.ProvisionIsolatedWorkspaceForNewAccountAsync("flavia.loja@gmail.com", "flavia.boutique");
            var reg = await auth.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                "Flavia Store",
                "Flavia.Loja@Gmail.Com",
                "SenhaForte@2026",
                Username: "flavia.boutique"));

            var switchedByAtUsername = await repo.SwitchToWorkspaceForAccountAsync(" @FLAVIA.BOUTIQUE ");
            Assert.IsNotNull(switchedByAtUsername);

            var foundByAtUsername = await repo.GetUserByEmailAsync(" @flavia.boutique ");
            Assert.IsNotNull(foundByAtUsername);
            Assert.AreEqual(reg.Employee.UserId, foundByAtUsername!.Id);

            var challenge = await auth.LoginWithChallengeAsync(new LoginRequest(" @Flavia.Boutique ", "SenhaForte@2026"));
            Assert.IsFalse(challenge.RequiresTwoFactor);
            Assert.IsNotNull(challenge.AuthResult);

            // Atualiza e-mail e usuário — SwitchableClothingStoreRepository deve reindexar automaticamente account_workspaces.json
            await auth.UpdateUserProfileAsync(reg.Employee.UserId, new UpdateUserProfileRequest(
                "Flavia Vip",
                "flavia.novo@gmail.com",
                "11999998888",
                "flavia.vip"));

            var repoAfterReload = new SwitchableClothingStoreRepository(hasher, defaultDbPath, enablePlatformOwnerCompanion: false);
            var switchedAfterUpdate = await repoAfterReload.SwitchToWorkspaceForAccountAsync("@flavia.vip");
            Assert.AreEqual(switchedByAtUsername, switchedAfterUpdate);

            var switchedByNewEmail = await repoAfterReload.SwitchToWorkspaceForAccountAsync("flavia.novo@gmail.com");
            Assert.AreEqual(switchedByAtUsername, switchedByNewEmail);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task CloudAccountRegistryService_UpsertVerifyLoginAndMirrorPersistence_AllowsCrossDeviceHydrationWithoutLeakingHashes()
    {
        var (repo, auth, catalog, _, _, _, hasher, _, tempDir, _) = CreateStack();
        try
        {
            var registryDir = Path.Combine(tempDir, "cloud_server_root");
            Directory.CreateDirectory(registryDir);
            var registry = new CloudAccountRegistryService(registryDir, hasher);

            var originalHash = hasher.HashPassword("sfgild1505s");
            var upsertOk = await registry.UpsertAccountAsync(new CloudAccountCredentialSyncDto(
                FullName: "Flavia Leal",
                Username: "flavia",
                Email: "giovannilealpr@gmail.com",
                PasswordHash: originalHash,
                Phone: "41999990000",
                StoreName: "Atelier Flavia",
                StoreSlug: "atelier-flavia"));
            Assert.IsTrue(upsertOk.Success);
            Assert.AreEqual(200, upsertOk.StatusCode);

            // Senha incorreta NÃO vaza hash nem dados da conta
            var wrongPwd = await registry.VerifyLoginAsync(new LoginRequest("giovannilealpr@gmail.com", "errada"));
            Assert.IsFalse(wrongPwd.Verified);
            Assert.IsTrue(wrongPwd.AccountExists);
            Assert.IsNull(wrongPwd.Account);

            // Outro cliente tentando sobrescrever o mesmo e-mail sem o hash anterior é bloqueado com 401
            var rogueOverwrite = await registry.UpsertAccountAsync(new CloudAccountCredentialSyncDto(
                FullName: "Invasor",
                Username: "flavia",
                Email: "giovannilealpr@gmail.com",
                PasswordHash: hasher.HashPassword("outraSenha")));
            Assert.IsFalse(rogueOverwrite.Success);
            Assert.AreEqual(401, rogueOverwrite.StatusCode);

            // Login válido em dispositivo novo / pós-reinstalação Android verifica e importa para workspace isolado
            var verified = await registry.VerifyLoginAsync(new LoginRequest(" @flavia ", "sfgild1505s"));
            Assert.IsTrue(verified.Verified);
            Assert.IsNotNull(verified.Account);

            var hydrated = await repo.UpsertCloudHydratedAccountAsync(verified.Account!);
            Assert.IsTrue(hydrated);
            Assert.AreEqual("Atelier Flavia", (await catalog.GetPublicStoreSettingsAsync()).StoreName);

            var loginAfterHydration = await auth.LoginWithChallengeAsync(new LoginRequest("giovannilealpr@gmail.com", "sfgild1505s"));
            Assert.IsFalse(loginAfterHydration.RequiresTwoFactor);
            Assert.IsNotNull(loginAfterHydration.AuthResult);
            Assert.IsTrue(loginAfterHydration.AuthResult!.Employee.IsAdmin);

            // Troca de senha em outro aparelho atualiza registro com PreviousPasswordHash e reidrata no aparelho local
            var updatedHash = hasher.HashPassword("NovaSenha@99");
            var updatePwd = await registry.UpsertAccountAsync(new CloudAccountCredentialSyncDto(
                FullName: "Flavia Leal",
                Username: "flavia",
                Email: "giovannilealpr@gmail.com",
                PasswordHash: updatedHash,
                PreviousPasswordHash: originalHash));
            Assert.IsTrue(updatePwd.Success);

            var verifiedNew = await registry.VerifyLoginAsync(new LoginRequest("giovannilealpr@gmail.com", "NovaSenha@99"));
            Assert.IsTrue(verifiedNew.Verified);
            Assert.IsTrue(await repo.UpsertCloudHydratedAccountAsync(verifiedNew.Account!));
            var rehydratedLogin = await auth.LoginWithChallengeAsync(new LoginRequest("giovannilealpr@gmail.com", "NovaSenha@99"));
            Assert.IsNotNull(rehydratedLogin.AuthResult);

            // Garante que CloudAppDataMirror espelha e restaura cloud_accounts_registry.json após wipe em disco
            var mirrorBackend = new InMemoryCloudStateBackend();
            using (var mirror = new CloudAppDataMirror(registryDir, mirrorBackend))
            {
                var jsonPayload = await File.ReadAllTextAsync(registry.RegistryFilePath);
                mirror.EnqueueUpsert(registry.RegistryFilePath, jsonPayload);
                await mirror.FlushAsync(TimeSpan.FromSeconds(3));
            }
            Assert.IsTrue(mirrorBackend.Snapshots.ContainsKey(CloudAccountRegistryService.RegistryFileName));

            File.Delete(registry.RegistryFilePath);
            using (var restoreMirror = new CloudAppDataMirror(registryDir, mirrorBackend))
            {
                await restoreMirror.HydrateToDiskAsync();
            }
            Assert.IsTrue(File.Exists(registry.RegistryFilePath));
            var restoredRegistry = new CloudAccountRegistryService(registryDir, hasher);
            Assert.IsTrue((await restoredRegistry.VerifyLoginAsync(new LoginRequest("giovannilealpr@gmail.com", "NovaSenha@99"))).Verified);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task PurgeAllUserAccountsAcrossAllWorkspaces_RemovesAllPreSalesUsersAndProducts_WhilePreservingRoles()
    {
        var (repo, auth, _, settings, products, _, hasher, _, tempDir, defaultDbPath) = CreateStack();
        try
        {
            var h1 = await repo.ProvisionIsolatedWorkspaceForNewAccountAsync("teste1@gmail.com", "teste1");
            var l1 = await auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("Teste Um", "teste1@gmail.com", "1234", Username: "teste1"));
            await settings.UpdateStoreSettingsAsync(l1.Employee.EmployeeId, new UpdateStoreSettingsRequest
            {
                StoreName = "Loja Teste 1",
                WhatsappNumber = "11999990001",
                StoreDescription = "Teste"
            });
            await products.CreateProductAsync(new SaveProductRequest
            {
                CategoryName = "Geral",
                Name = "Produto Teste",
                BasePrice = 50m,
                InitialStockQuantity = 3
            }, l1.Employee.EmployeeId);

            await repo.ProvisionIsolatedWorkspaceForNewAccountAsync("teste2@gmail.com", "teste2");
            await auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("Teste Dois", "teste2@gmail.com", "5678", Username: "teste2"));

            var purgedCount = await repo.PurgeAllUserAccountsAcrossAllWorkspacesAsync(alsoClearCatalog: true);
            Assert.IsTrue(purgedCount >= 2);
            Assert.IsFalse(await repo.HasAnyIndexedAccountAsync());
            Assert.IsFalse(await repo.AccountExistsInAnyWorkspaceAsync("teste1@gmail.com"));
            Assert.IsFalse(await repo.AccountExistsInAnyWorkspaceAsync("teste2"));
            Assert.AreEqual(SwitchableClothingStoreRepository.DefaultWorkspaceId, repo.ActiveWorkspaceId);

            var cleanRepo = new SwitchableClothingStoreRepository(hasher, defaultDbPath, enablePlatformOwnerCompanion: false);
            var cleanAuth = new AuthService(cleanRepo, hasher, new JwtTokenService(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:SecretKey"] = "unit-test-secret-key-with-at-least-32-characters-12345" })
                .Build()));

            var status = await cleanAuth.GetSetupStatusAsync();
            Assert.IsTrue(status.RequiresInitialSetup);
            Assert.IsTrue((await cleanRepo.GetRolesAsync()).Count > 0, "Perfis de acesso (Roles) devem ser preservados após limpeza.");
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public void LoginFixes_CloudEndpoints_And_SalesLaunchReset_ShouldBeProperlyWired()
    {
        var root = ResolveRepoRoot();

        var mainLayout = File.ReadAllText(Path.Combine(root, "Components", "Layout", "MainLayout.razor"));
        var authChangedIdx = mainLayout.IndexOf("private async void OnAuthStateChanged()", StringComparison.Ordinal);
        var stateChangedIdx = mainLayout.IndexOf("await InvokeAsync(StateHasChanged);", authChangedIdx, StringComparison.Ordinal);
        var refreshStoreIdx = mainLayout.IndexOf("await RefreshStoreInfoAsync();", authChangedIdx, StringComparison.Ordinal);
        Assert.IsTrue(authChangedIdx > 0 && stateChangedIdx > authChangedIdx && refreshStoreIdx > stateChangedIdx,
            "OnAuthStateChanged deve chamar InvokeAsync(StateHasChanged) antes de RefreshStoreInfoAsync para transição em 1 clique.");

        var gateway = File.ReadAllText(Path.Combine(root, "Services", "MauiAdminGateway.cs"));
        Assert.IsTrue(gateway.Contains("TryVerifyAndImportAccountFromCentralCloudAsync", StringComparison.Ordinal));
        Assert.IsTrue(gateway.Contains("SyncAccountByUserIdToCloudAsync", StringComparison.Ordinal));
        Assert.IsTrue(gateway.Contains("ExecuteOneTimeSalesLaunchResetAsync", StringComparison.Ordinal));
        Assert.IsTrue(gateway.Contains("EnsurePreSalesV1040CleanReset", StringComparison.Ordinal));

        var mauiProgram = File.ReadAllText(Path.Combine(root, "MauiProgram.cs"));
        Assert.IsTrue(mauiProgram.Contains("EnsurePreSalesV1040CleanReset()", StringComparison.Ordinal));

        var webProgram = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Web", "Program.cs"));
        Assert.IsTrue(webProgram.Contains("/api/cloud/accounts/upsert", StringComparison.Ordinal));
        Assert.IsTrue(webProgram.Contains("/api/cloud/accounts/verify-login", StringComparison.Ordinal));
        Assert.IsTrue(webProgram.Contains("/api/cloud/accounts/purge-all", StringComparison.Ordinal));

        var updateService = File.ReadAllText(Path.Combine(root, "Services", "AppUpdateService.cs"));
        var updateModal = File.ReadAllText(Path.Combine(root, "Components", "Shared", "AppUpdateModal.razor"));
        Assert.IsTrue(updateService.Contains("ApplyCloudHotUpdateAsync", StringComparison.Ordinal));
        Assert.IsTrue(updateService.Contains("ReplaceConflictingAndroidPackageAsync", StringComparison.Ordinal));
        Assert.IsTrue(updateModal.Contains("Atualizar pela Nuvem", StringComparison.Ordinal));
        Assert.IsTrue(updateModal.Contains("Substituir Pacote Antigo", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CloudAccountRegistryService_ApplyVipPlan_SyncsAcrossLoginPlanStatusAndTenantPublish_WithoutRootFallback()
    {
        var (repo, _, _, settings, _, _, hasher, _, tempDir, _) = CreateStack();
        try
        {
            var registryDir = Path.Combine(tempDir, "cloud_vip_sync_root");
            Directory.CreateDirectory(registryDir);
            var registry = new CloudAccountRegistryService(registryDir, hasher);

            var passwordHash = hasher.HashPassword("SenhaForte@123");
            var upsert = await registry.UpsertAccountAsync(new CloudAccountCredentialSyncDto(
                FullName: "Flavia",
                Username: "flavia",
                Email: "flavinhaasilva15@gmail.com",
                PasswordHash: passwordHash,
                Phone: "41999991111",
                StoreName: "Loja flavia",
                StoreSlug: "loja-flavia",
                PlanTier: StoreSettings.PlanTierPremium));
            Assert.IsTrue(upsert.Success);

            // Cliente sem SyncKey NÃO pode auto-promover para Premium durante upsert
            var initialStatus = await registry.GetPlanStatusAsync("flavinhaasilva15@gmail.com", "flavia", "loja-flavia", "Loja flavia");
            Assert.IsNotNull(initialStatus);
            Assert.AreEqual(StoreSettings.PlanTierBasic, initialStatus!.PlanTier);

            // Admin aplica VIP Premium Vitalício pelo servidor central
            var applied = await registry.ApplyPlanAsync(
                targetEmail: "flavinhaasilva15@gmail.com",
                targetUsername: "flavia",
                storeName: "Loja flavia",
                planTier: StoreSettings.PlanTierPremium,
                durationDays: null,
                extendExistingDuration: true,
                activateSite: true);
            Assert.IsNotNull(applied);
            Assert.AreEqual(StoreSettings.PlanTierPremium, applied!.PlanTier);
            Assert.IsNull(applied.PlanExpiresAt);

            var syncedStatus = await registry.GetPlanStatusAsync("flavinhaasilva15@gmail.com", "flavia", "loja-flavia", "Loja flavia");
            Assert.IsNotNull(syncedStatus);
            Assert.AreEqual(StoreSettings.PlanTierPremium, syncedStatus!.PlanTier);
            Assert.IsNull(syncedStatus.PlanExpiresAt);

            // Login em outro aparelho traz o plano Premium automaticamente na hidratação
            var verified = await registry.VerifyLoginAsync(new LoginRequest("flavia", "SenhaForte@123"));
            Assert.IsTrue(verified.Verified);
            Assert.AreEqual(StoreSettings.PlanTierPremium, verified.Account?.PlanTier);

            Assert.IsTrue(await repo.UpsertCloudHydratedAccountAsync(verified.Account!));
            Assert.AreEqual(StoreSettings.PlanTierPremium, (await repo.GetStoreSettingsAsync()).GetEffectivePlanTier());

            // Workspace local já existente em Básico é atualizado idempotentemente por ApplyCloudPlanSyncAsync
            await repo.ProvisionIsolatedWorkspaceForNewAccountAsync("outraloja@gmail.com", "outraloja");
            var (updated, updatedSettings) = await settings.ApplyCloudPlanSyncAsync(syncedStatus);
            Assert.IsTrue(updated);
            Assert.AreEqual(StoreSettings.PlanTierPremium, updatedSettings.PlanTier);
            var (updatedAgain, _) = await settings.ApplyCloudPlanSyncAsync(syncedStatus);
            Assert.IsFalse(updatedAgain);

            // CloudSyncStateService preserva VIP ativo do servidor mesmo se payload antigo ainda mandar Básico
            var syncService = new CloudSyncStateService(repo, settings);
            var syncResult = await syncService.ApplySyncPayloadAsync(new CloudCatalogSyncPayload
            {
                Settings = new UpdateStoreSettingsRequest
                {
                    StoreName = "Loja flavia",
                    StoreSlug = "loja-flavia",
                    WhatsappNumber = "41999991111",
                    StoreDescription = "Catalogo da Flavia",
                    PlanTier = StoreSettings.PlanTierBasic,
                    IsSiteActive = true
                }
            });
            Assert.AreEqual(StoreSettings.PlanTierPremium, syncResult.AppliedSettings.PlanTier);

            var root = ResolveRepoRoot();
            var webProgram = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Web", "Program.cs"));
            Assert.IsTrue(webProgram.Contains("/api/cloud/plan-status", StringComparison.Ordinal));
            Assert.IsTrue(webProgram.Contains("ApplyGrantedCloudAccountVipToPublishedTenantAsync", StringComparison.Ordinal));
            Assert.IsTrue(webProgram.Contains("hasExplicitNonRootTarget", StringComparison.Ordinal));

            var adminGatewayPath = Path.Combine(root, "src", "ClothingStore.AdminApp", "Services", "PlatformAdminGateway.cs");
            if (File.Exists(adminGatewayPath))
            {
                var adminGateway = File.ReadAllText(adminGatewayPath);
                Assert.IsTrue(adminGateway.Contains("keepLocalVip", StringComparison.Ordinal));
                Assert.IsTrue(adminGateway.Contains("SyncMissingLocalVipsToCloudBestEffortAsync", StringComparison.Ordinal));
            }
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task CourtesyVipExclusion_And_Popups_And_SyncLabel_ShouldWorkAccurately()
    {
        var (repo, _, _, settingsService, _, _, hasher, _, tempDir, _) = CreateStack();
        try
        {
            var registryDir = Path.Combine(tempDir, "cloud_courtesy_revoke_root");
            Directory.CreateDirectory(registryDir);
            var registry = new CloudAccountRegistryService(registryDir, hasher);

            var passwordHash = hasher.HashPassword("SenhaForte@123");
            await registry.UpsertAccountAsync(new CloudAccountCredentialSyncDto(
                FullName: "Loja Cortesia",
                Username: "cortesia1",
                Email: "cortesia1@gmail.com",
                PasswordHash: passwordHash,
                Phone: "41999990000",
                StoreName: "Loja Cortesia",
                StoreSlug: "loja-cortesia"));

            // 1) Admin concede VIP Cortesia -> PlanStatus deve refletir IsCourtesyVip = true
            var granted = await registry.ApplyPlanAsync(
                targetEmail: "cortesia1@gmail.com",
                targetUsername: "cortesia1",
                storeName: "Loja Cortesia",
                planTier: StoreSettings.PlanTierPremium,
                durationDays: null,
                extendExistingDuration: false,
                activateSite: true,
                isCourtesyVip: true);
            Assert.IsNotNull(granted);
            Assert.IsTrue(granted!.IsCourtesyVip);

            var courtesyStatus = await registry.GetPlanStatusAsync("cortesia1@gmail.com", "cortesia1", "loja-cortesia", "Loja Cortesia");
            Assert.IsNotNull(courtesyStatus);
            Assert.IsTrue(courtesyStatus!.IsCourtesyVip);

            var (updatedVip, vipSettings) = await settingsService.ApplyCloudPlanSyncAsync(courtesyStatus);
            Assert.IsTrue(updatedVip);
            Assert.AreEqual(StoreSettings.PlanTierPremium, vipSettings.PlanTier);
            Assert.AreEqual(PlanActivationCode.BillingKindGiftedUnpaid, vipSettings.ActivePlanBillingKind);

            // 2) Admin exclui VIP Cortesia -> PlanRevokedAt é gravado e ApplyCloudPlanSyncAsync reverte cliente para Básico
            var revoked = await registry.ApplyPlanAsync(
                targetEmail: "cortesia1@gmail.com",
                targetUsername: "cortesia1",
                storeName: "Loja Cortesia",
                planTier: StoreSettings.PlanTierBasic,
                durationDays: null,
                extendExistingDuration: false,
                activateSite: true,
                isCourtesyVip: false);
            Assert.IsNotNull(revoked);
            Assert.AreEqual(StoreSettings.PlanTierBasic, revoked!.PlanTier);
            Assert.IsNotNull(revoked.PlanRevokedAt);

            var revokedStatus = await registry.GetPlanStatusAsync("cortesia1@gmail.com", "cortesia1", "loja-cortesia", "Loja Cortesia");
            Assert.IsNotNull(revokedStatus);
            Assert.IsNotNull(revokedStatus!.PlanRevokedAt);

            var (updatedAfterRevoke, basicSettings) = await settingsService.ApplyCloudPlanSyncAsync(revokedStatus);
            Assert.IsTrue(updatedAfterRevoke);
            Assert.AreEqual(StoreSettings.PlanTierBasic, basicSettings.PlanTier);

            // 2b) VIP Vitalício (sem vencimento) é sempre tratado como Cortesia mesmo se vindo de registro legado sem flag explícita
            var legacyLifetime = await registry.ApplyPlanAsync(
                targetEmail: "cortesia1@gmail.com",
                targetUsername: "cortesia1",
                storeName: "Loja Cortesia",
                planTier: StoreSettings.PlanTierPremium,
                durationDays: null,
                extendExistingDuration: false,
                activateSite: true,
                isCourtesyVip: false);
            Assert.IsNotNull(legacyLifetime);
            Assert.IsTrue(legacyLifetime!.IsCourtesyVip, "Todo VIP Vitalício sem assinatura recorrente deve ser identificado como Cortesia.");

            // 3) VIP Comprado em falha recorrente entra em carência de 5 dias e desativa após expirar
            await registry.ApplyPlanAsync(
                targetEmail: "cortesia1@gmail.com",
                targetUsername: "cortesia1",
                storeName: "Loja Cortesia",
                planTier: StoreSettings.PlanTierPremium,
                durationDays: 30,
                extendExistingDuration: false,
                activateSite: true,
                isCourtesyVip: false);
            var dueAt = DateTimeOffset.UtcNow;
            var graceStatus = await registry.MarkRecurringPaymentFailureOrGraceAsync(
                targetEmail: "cortesia1@gmail.com",
                targetUsername: "cortesia1",
                storeName: "Loja Cortesia",
                dueAt: dueAt,
                failureReason: "Cartão sem saldo. Carência de até 5 dias.");
            Assert.IsNotNull(graceStatus);
            Assert.IsTrue(graceStatus!.IsRecurringSubscription);
            Assert.IsFalse(graceStatus.IsBillingSuspended);
            Assert.IsTrue(graceStatus.IsSiteActive);

            // Após 5 dias vencidos: marca automaticamente como suspensa / desativada
            var suspendedStatus = await registry.MarkRecurringPaymentFailureOrGraceAsync(
                targetEmail: "cortesia1@gmail.com",
                targetUsername: "cortesia1",
                storeName: "Loja Cortesia",
                dueAt: dueAt.AddDays(-6),
                failureReason: "Carência de 5 dias encerrada sem pagamento.");
            Assert.IsNotNull(suspendedStatus);
            Assert.IsTrue(suspendedStatus!.IsBillingSuspended);
            Assert.IsFalse(suspendedStatus.IsSiteActive);

            // 4) Validação dos Popups e Excluir VIP somente para Cortesia
            var root = ResolveRepoRoot();
            var userMainLayout = File.ReadAllText(Path.Combine(root, "Components", "Layout", "MainLayout.razor"));
            var userPopup = File.ReadAllText(Path.Combine(root, "Components", "Shared", "PlanStatusPopupModal.razor"));
            Assert.IsTrue(userMainLayout.Contains("PlanStatusPopupModal", StringComparison.Ordinal));
            Assert.IsFalse(userMainLayout.Contains("_autoActivatedPlanBanner", StringComparison.Ordinal),
                "Avisos de VIP/pagamento devem aparecer em popup centralizado e não em faixa no topo.");
            Assert.IsTrue(userPopup.Contains("BillingSuspended", StringComparison.Ordinal));
            Assert.IsTrue(userPopup.Contains("PurchasedVip", StringComparison.Ordinal));

            var usersPagePath = Path.Combine(root, "src", "ClothingStore.AdminApp", "Components", "Pages", "UsersAndVipPage.razor");
            if (File.Exists(usersPagePath))
            {
                var usersPage = File.ReadAllText(usersPagePath);
                Assert.IsTrue(usersPage.Contains("u.HasActiveVip && u.IsCourtesyVip", StringComparison.Ordinal),
                    "Botão Excluir VIP deve aparecer apenas para VIP Cortesia.");
                Assert.IsTrue(usersPage.Contains("Excluir VIP", StringComparison.Ordinal));
                Assert.IsTrue(usersPage.Contains("VIP Comprado (Mercado Pago)", StringComparison.Ordinal));
            }

            var adminGatewayPath = Path.Combine(root, "src", "ClothingStore.AdminApp", "Services", "PlatformAdminGateway.cs");
            if (File.Exists(adminGatewayPath))
            {
                var adminGateway = File.ReadAllText(adminGatewayPath);
                Assert.IsTrue(adminGateway.Contains("Portal Central YouLink", StringComparison.Ordinal),
                    "Status conectado não deve exibir nome genérico 'Minha Loja' para o servidor central.");
                Assert.IsTrue(adminGateway.Contains("ExcludeCourtesyVipAsync", StringComparison.Ordinal));
            }
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    private sealed class InMemoryCloudStateBackend : ICloudStateBackend
    {
        public Dictionary<string, string> Snapshots { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string ProviderName => "InMemoryTest";

        public Task<IReadOnlyDictionary<string, string>> LoadAllSnapshotsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(Snapshots, StringComparer.OrdinalIgnoreCase));

        public Task UpsertSnapshotAsync(string stateKey, string payload, CancellationToken ct = default)
        {
            Snapshots[stateKey] = payload;
            return Task.CompletedTask;
        }

        public Task DeleteSnapshotAsync(string stateKey, CancellationToken ct = default)
        {
            Snapshots.Remove(stateKey);
            return Task.CompletedTask;
        }
    }
}
