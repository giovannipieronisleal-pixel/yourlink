using System.Text;
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
public class LaunchReadinessTests
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

    [TestMethod]
    public void SupportWhatsapp_Sanitization_ShouldRejectPlaceholders_AndNormalizeDddNumbers()
    {
        Assert.IsNull(WhatsAppLinkBuilder.SanitizeSupportPhone(null));
        Assert.IsNull(WhatsAppLinkBuilder.SanitizeSupportPhone("   "));
        Assert.IsNull(WhatsAppLinkBuilder.SanitizeSupportPhone("5511900000000"));
        Assert.IsNull(WhatsAppLinkBuilder.SanitizeSupportPhone("11900000000"));
        Assert.IsNull(WhatsAppLinkBuilder.SanitizeSupportPhone("12345"));
        Assert.IsNull(WhatsAppLinkBuilder.BuildSupportWhatsappUrl("5511900000000"));

        Assert.AreEqual("5511987654321", WhatsAppLinkBuilder.SanitizeSupportPhone("(11) 98765-4321"));
        Assert.AreEqual("5521998877665", WhatsAppLinkBuilder.SanitizeSupportPhone("+55 (21) 99887-7665"));

        var link = WhatsAppLinkBuilder.BuildSupportWhatsappUrl("(11) 98765-4321", "Olá YouLink");
        Assert.IsNotNull(link);
        Assert.IsTrue(link!.StartsWith("https://wa.me/5511987654321?text=", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PlatformSupportWhatsapp_ShouldPersistAcrossDbRecreation_AndSupportExplicitClear()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink_launch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var dbPath = Path.Combine(tempDir, "clothingstore_db.json");
            var hasher = new Pbkdf2PasswordHasher();
            var storage = new SupabaseImageStorageService(new HttpClient(), new ConfigurationBuilder().Build(), Path.Combine(tempDir, "wwwroot"));

            var repo1 = new ClothingStoreRepository(hasher, dbPath);
            var settingsSvc1 = new EmployeeAndSettingsService(repo1, hasher, storage);

            var initial = await repo1.GetStoreSettingsAsync();
            Assert.IsNull(initial.OnlineBaseUrl, "Seed inicial não deve expor domínio fictício lojas.minhaloja.com.");
            Assert.IsNull(initial.PlatformSupportWhatsapp, "WhatsApp de suporte não deve nascer preenchido com número fictício.");

            var synced = await settingsSvc1.SyncOwnerPricingAsync(new UpdateOwnerPricingSyncRequest(
                PicpayPixKey: "recebimentos@youlink.app",
                BasicPlanMonthlyPrice: 49.90m,
                IntermediaryPlanMonthlyPrice: 89.90m,
                PremiumPlanMonthlyPrice: 139.90m,
                CentralCatalogServerUrl: "https://youlink-loja.onrender.com",
                SupportWhatsappNumber: "(11) 98765-4321"));
            Assert.AreEqual("5511987654321", synced.PlatformSupportWhatsapp);

            // Recria banco do zero mantendo companion platform_owner_settings.json
            File.Delete(dbPath);
            var repo2 = new ClothingStoreRepository(hasher, dbPath);
            var restored = await repo2.GetStoreSettingsAsync();
            Assert.AreEqual("5511987654321", restored.PlatformSupportWhatsapp);

            // Limpar suporte explicitamente apaga também no companion
            var settingsSvc2 = new EmployeeAndSettingsService(repo2, hasher, storage);
            var cleared = await settingsSvc2.SyncOwnerPricingAsync(new UpdateOwnerPricingSyncRequest(
                PicpayPixKey: "recebimentos@youlink.app",
                BasicPlanMonthlyPrice: 49.90m,
                IntermediaryPlanMonthlyPrice: 89.90m,
                PremiumPlanMonthlyPrice: 139.90m,
                CentralCatalogServerUrl: "https://youlink-loja.onrender.com",
                SupportWhatsappNumber: string.Empty));
            Assert.IsNull(cleared.PlatformSupportWhatsapp);

            File.Delete(dbPath);
            var repo3 = new ClothingStoreRepository(hasher, dbPath);
            var afterClear = await repo3.GetStoreSettingsAsync();
            Assert.IsNull(afterClear.PlatformSupportWhatsapp);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void LegalPages_InstallGuide_And_AppConsent_ShouldExistWithoutBannedCopyOrAdminLeaks()
    {
        var root = ResolveRepoRoot();

        var privacy = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Web", "Components", "Pages", "PrivacyPolicy.razor"), Encoding.UTF8);
        var terms = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Web", "Components", "Pages", "TermsOfUse.razor"), Encoding.UTF8);
        var installGuide = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Web", "Components", "Pages", "HowToInstall.razor"), Encoding.UTF8);
        var webLayout = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Web", "Components", "Layout", "MainLayout.razor"), Encoding.UTF8);
        var portalView = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Web", "Components", "Shared", "PlatformPortalView.razor"), Encoding.UTF8);
        var detailRazor = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Web", "Components", "Pages", "ProductDetail.razor"), Encoding.UTF8);
        var webProgram = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Web", "Program.cs"), Encoding.UTF8);

        var mauiLogin = File.ReadAllText(Path.Combine(root, "Components", "Pages", "Login.razor"), Encoding.UTF8);
        var mauiNav = File.ReadAllText(Path.Combine(root, "Components", "Layout", "NavMenu.razor"), Encoding.UTF8);
        var mauiPlans = File.ReadAllText(Path.Combine(root, "Components", "Pages", "PlansPage.razor"), Encoding.UTF8);

        // Rotas legais (portal e por loja)
        Assert.IsTrue(privacy.Contains("@page \"/privacidade\"", StringComparison.Ordinal));
        Assert.IsTrue(privacy.Contains("@page \"/loja/{StoreSlug}/privacidade\"", StringComparison.Ordinal));
        Assert.IsTrue(privacy.Contains("13.709/2018", StringComparison.Ordinal));

        Assert.IsTrue(terms.Contains("@page \"/termos\"", StringComparison.Ordinal));
        Assert.IsTrue(terms.Contains("@page \"/loja/{StoreSlug}/termos\"", StringComparison.Ordinal));

        // Guia visual de instalação Windows SmartScreen + Android APK
        Assert.IsTrue(installGuide.Contains("@page \"/como-instalar\"", StringComparison.Ordinal));
        Assert.IsTrue(installGuide.Contains("SmartScreen", StringComparison.Ordinal));
        Assert.IsTrue(installGuide.Contains("Executar assim mesmo", StringComparison.Ordinal));
        Assert.IsTrue(installGuide.Contains("Permitir desta fonte", StringComparison.Ordinal));

        // Links no Web Portal, Footer, Sitemap e Pix Checkout
        Assert.IsTrue(webLayout.Contains("/privacidade", StringComparison.Ordinal));
        Assert.IsTrue(webLayout.Contains("/termos", StringComparison.Ordinal));
        Assert.IsTrue(webLayout.Contains("/como-instalar", StringComparison.Ordinal));
        Assert.IsTrue(portalView.Contains("/como-instalar", StringComparison.Ordinal));
        Assert.IsTrue(detailRazor.Contains("Privacidade (LGPD)", StringComparison.Ordinal));
        Assert.IsTrue(webProgram.Contains("/como-instalar", StringComparison.Ordinal));
        Assert.IsTrue(webProgram.Contains("/privacidade", StringComparison.Ordinal));
        Assert.IsTrue(webProgram.Contains("/termos", StringComparison.Ordinal));

        // App MAUI: aceite LGPD no cadastro + Ajuda/Suporte no menu e em Planos
        Assert.IsTrue(mauiLogin.Contains("_acceptedLegalTerms", StringComparison.Ordinal));
        Assert.IsTrue(mauiLogin.Contains("Política de Privacidade (LGPD)", StringComparison.Ordinal));
        Assert.IsTrue(mauiLogin.Contains("Termos de Uso", StringComparison.Ordinal));
        Assert.IsTrue(mauiNav.Contains("Suporte", StringComparison.Ordinal));
        Assert.IsTrue(mauiNav.Contains("privacidade", StringComparison.Ordinal));
        Assert.IsTrue(mauiPlans.Contains("Privacidade (LGPD)", StringComparison.Ordinal));
        Assert.IsTrue(mauiPlans.Contains("SupportWhatsappUrl", StringComparison.Ordinal));

        // Segurança pública: nenhuma menção a AdminApp ou keystore comprometida no portal público
        Assert.IsFalse(privacy.Contains("AdminApp", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(terms.Contains("AdminApp", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(installGuide.Contains("AdminApp", StringComparison.OrdinalIgnoreCase));

        // Copy genérica para qualquer loja (zero palavras restritas de moda nas novas páginas)
        string[] bannedWords = ["peça", "roupa", "look", "tecido"];
        foreach (var pageText in new[] { privacy, terms, installGuide })
        {
            foreach (var word in bannedWords)
            {
                Assert.IsFalse(pageText.Contains(word, StringComparison.OrdinalIgnoreCase),
                    $"Nova página pública contém palavra restrita '{word}'.");
            }
        }
    }
}
