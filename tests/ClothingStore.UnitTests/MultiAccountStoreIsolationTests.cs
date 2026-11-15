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
    }
}
