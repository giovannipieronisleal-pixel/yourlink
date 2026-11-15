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

    [TestMethod]
    public void OptionalSizeAndColor_And_SingleStepLoginBinding_ShouldBeEnforced()
    {
        var root = ResolveRepoRoot();

        // 1. Domínio permite criar e editar variantes sem tamanho e sem cor (ex.: eletrônicos, cosméticos, acessórios)
        var product = new ClothingStore.Domain.Entities.Product(
            Guid.NewGuid(),
            "Mouse Gamer",
            "Mouse sem fio",
            149.90m,
            true);

        var defaultVariant = product.AddVariant(color: "", size: "");
        Assert.AreEqual("Padrão", defaultVariant.Color);
        Assert.AreEqual("ÚNICO", defaultVariant.Size);
        Assert.IsTrue(ClothingStore.Domain.Entities.ProductVariant.IsDefaultPlaceholderColor(defaultVariant.Color));
        Assert.IsTrue(ClothingStore.Domain.Entities.ProductVariant.IsDefaultPlaceholderSize(defaultVariant.Size));

        // 2. Mensagem do WhatsApp omite "na cor padrão e tamanho ÚNICO" quando o produto não usa cor/tamanho
        var msgNoSizeOrColor = WhatsAppLinkBuilder.BuildProductConsultationMessage("mause hazor", "Padrão", "ÚNICO");
        Assert.AreEqual("Olá! Tenho interesse no produto mause hazor. Gostaria de saber mais informações.", msgNoSizeOrColor);

        var msgColorOnly = WhatsAppLinkBuilder.BuildProductConsultationMessage("mause hazor", "Preto", "ÚNICO");
        Assert.AreEqual("Olá! Tenho interesse no produto mause hazor, na cor preto. Gostaria de saber mais informações.", msgColorOnly);

        var msgWithBoth = WhatsAppLinkBuilder.BuildProductConsultationMessage("Camiseta Básica", "Preta", "M");
        Assert.AreEqual("Olá! Tenho interesse no produto Camiseta Básica, na cor preta e tamanho M. Gostaria de saber mais informações.", msgWithBoth);

        // 3. Vitrine pública usa pt-BR para moeda e oculta seletores de tamanho/cor quando são apenas placeholders
        var productDetailRazor = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Web", "Components", "Pages", "ProductDetail.razor"));
        Assert.IsTrue(productDetailRazor.Contains("HasMeaningfulSizes", StringComparison.Ordinal));
        Assert.IsTrue(productDetailRazor.Contains("HasMeaningfulColors", StringComparison.Ordinal));
        Assert.IsTrue(productDetailRazor.Contains("CultureInfo(\"pt-BR\")", StringComparison.Ordinal));

        // 4. Login.razor usa @bind:event="oninput" em _login e _password e inicia na aba "login"
        var loginRazor = File.ReadAllText(Path.Combine(root, "Components", "Pages", "Login.razor"));
        Assert.IsTrue(loginRazor.Contains("private string _activeTab = \"login\";", StringComparison.Ordinal));
        Assert.IsTrue(loginRazor.Contains("@bind=\"_login\"", StringComparison.Ordinal));
        Assert.IsTrue(loginRazor.Contains("@bind=\"_password\"", StringComparison.Ordinal));
        Assert.IsTrue(loginRazor.Contains("@bind:event=\"oninput\"", StringComparison.Ordinal));

        // 5. Imagens por URL: desempacota Google Imagens / Google Drive / Dropbox, extrai og:image de páginas HTML e dispara auto-sync
        var googleImg = WhatsAppLinkBuilder.NormalizeExternalImageUrl(
            "https://www.google.com/imgres?imgurl=https%3A%2F%2Fimages.kabum.com.br%2Fprodutos%2Ffotos%2Fmause.jpg&imgrefurl=https%3A%2F%2Fkabum.com.br");
        Assert.AreEqual("https://images.kabum.com.br/produtos/fotos/mause.jpg", googleImg);

        var driveImg = WhatsAppLinkBuilder.NormalizeExternalImageUrl(
            "https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz/view?usp=sharing");
        Assert.AreEqual("https://drive.google.com/uc?export=view&id=1AbCdEfGhIjKlMnOpQrStUvWxYz", driveImg);

        var ogExtracted = WhatsAppLinkBuilder.ExtractOpenGraphImageUrl(
            "<html><head><meta property=\"og:image\" content=\"https://cdn.loja.com/fotos/mouse-hazor.webp\" /></head></html>");
        Assert.AreEqual("https://cdn.loja.com/fotos/mouse-hazor.webp", ogExtracted);
        var webAppCss = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Web", "wwwroot", "app.css"));
        Assert.IsTrue(webAppCss.Contains("object-fit: contain;", StringComparison.Ordinal));

        var mauiAppCss = File.ReadAllText(Path.Combine(root, "wwwroot", "app.css"));
        Assert.IsTrue(mauiAppCss.Contains("object-fit: contain;", StringComparison.Ordinal));

        var previewRazor = File.ReadAllText(Path.Combine(root, "Components", "Pages", "PublicCatalogPreview.razor"));
        Assert.IsTrue(previewRazor.Contains("object-fit: contain;", StringComparison.Ordinal));
    }
}
