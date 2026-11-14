using System.Text.Json;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClothingStore.UnitTests;

[TestClass]
public class GeneralCatalogCopyTests
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
    public void PublicCatalogAndAppPages_ShouldUseGeneralProductCopy_InsteadOfClothingOnlyCopy()
    {
        var root = ResolveRepoRoot();
        string[] targetFiles =
        [
            "Components/Layout/NavMenu.razor",
            "Components/Pages/Home.razor",
            "Components/Pages/Login.razor",
            "Components/Pages/OrdersPage.razor",
            "Components/Pages/CloudPublishWizard.razor",
            "Components/Pages/Products.razor",
            "Components/Pages/ProductEditor.razor",
            "Components/Pages/StockControl.razor",
            "Components/Pages/SalesReportPage.razor",
            "Components/Pages/PlansPage.razor",
            "Components/Pages/DesignStudioPanel.razor",
            "Components/Pages/StoreSettingsPage.razor",
            "Components/Pages/PublicCatalogPreview.razor",
            "Services/LocalWebServerLauncher.cs",
            "src/ClothingStore.Web/Components/Layout/MainLayout.razor",
            "src/ClothingStore.Web/Components/Pages/Home.razor",
            "src/ClothingStore.Web/Components/Pages/Catalog.razor",
            "src/ClothingStore.Web/Components/Pages/ProductDetail.razor",
            "src/ClothingStore.Web/Components/Shared/ProductCard.razor",
            "src/ClothingStore.Web/Components/Shared/PlatformPortalView.razor",
            "src/ClothingStore.Shared/Clients/CatalogApiClient.cs",
            "src/ClothingStore.Shared/Helpers/PwaManifestBuilder.cs",
            "src/ClothingStore.Shared/Helpers/GoogleCloudDomainChecker.cs"
        ];

        string[] bannedClothingCopy =
        [
            "Início &amp; Roupas",
            "Ver Roupas",
            "Ver Todas as Roupas",
            "Todas as Roupas",
            "Todas as roupas",
            "Roupas Disponíveis",
            "Roupas Mais Vendidas",
            "Categoria de Roupas",
            "Minha Loja de Roupas",
            "Moda selecionada",
            "Escolha a sua roupa",
            "Peças para Você Escolher",
            "Pedir esta Peça",
            "peça encontrada",
            "peça(s)",
            "Últimas peças",
            "👗",
            "Ver Look",
            "Nova Coleção Disponível",
            "Composição do tecido",
            "Aura Vestuário",
            "Catálogo online de roupas",
            "minha-boutique"
        ];

        foreach (var relativePath in targetFiles)
        {
            var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(fullPath), $"Arquivo esperado não encontrado: {relativePath}");
            var content = File.ReadAllText(fullPath);

            foreach (var banned in bannedClothingCopy)
            {
                Assert.IsFalse(
                    content.Contains(banned, StringComparison.OrdinalIgnoreCase),
                    $"Texto restrito a roupas encontrado em '{relativePath}': '{banned}'.");
            }
        }

        var previewRazor = File.ReadAllText(Path.Combine(root, "Components", "Pages", "PublicCatalogPreview.razor"));
        Assert.IsTrue(previewRazor.Contains("Início &amp; Produtos", StringComparison.Ordinal));
        Assert.IsTrue(previewRazor.Contains("Produtos Disponíveis", StringComparison.Ordinal));

        var webMainLayout = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Web", "Components", "Layout", "MainLayout.razor"));
        Assert.IsTrue(webMainLayout.Contains("Ver Produtos", StringComparison.Ordinal));

        var appNav = File.ReadAllText(Path.Combine(root, "Components", "Layout", "NavMenu.razor"));
        Assert.IsTrue(appNav.Contains("🛍️", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SharedHelpersAndPresets_ShouldDefaultToGeneralStoreCopy()
    {
        var manifestJson = PwaManifestBuilder.BuildJson(null);
        using var doc = JsonDocument.Parse(manifestJson);
        var description = doc.RootElement.GetProperty("description").GetString() ?? string.Empty;
        Assert.IsTrue(description.Contains("produtos", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(description.Contains("roupas", StringComparison.OrdinalIgnoreCase));

        foreach (var preset in StoreDesignPresets.All)
        {
            Assert.IsFalse(preset.Name.Contains("Vogue", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(preset.Name.Contains("Boutique", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(preset.Name.Contains("Sartorial", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(preset.Tagline.Contains("Alfaiataria", StringComparison.OrdinalIgnoreCase));
        }

        using var http = new HttpClient();
        var taken = await GoogleCloudDomainChecker.EvaluateDomainAsync(
            "https://lojas.minhaloja.com",
            http,
            isTakenLocally: d => Task.FromResult(d == "lojas.minhaloja.com"));
        Assert.IsTrue(taken.Suggestions.Contains("lojas.minhalojastore.com"));

        var now = DateTimeOffset.UtcNow;
        var summary = new DashboardSummaryDto(
            1, 1, 5, 0, 0, [],
            [
                new StockMovementDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", "SKU-X", "Preto", "Padrão", Guid.NewGuid(), "vendedor", "OUT", 1, 6, 5, "Venda balcão — Cliente", now)
            ]);
        var feed = DashboardRecentSalesHelper.BuildCompactSalesFeed(summary, null, 3);
        Assert.AreEqual(1, feed.Count);
        Assert.AreEqual("1x Produto", feed[0].WhatSold);
    }
}
