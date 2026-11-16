using System.Text;
using System.Text.Json;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClothingStore.UnitTests;

[TestClass]
public class FreeImprovementsTests
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
    public void QrCodeGenerator_ShouldProduceStandardFinderPatterns_AndScaleForLongUrls()
    {
        var shortUrl = "https://youlink-loja.onrender.com/loja/minha-loja";
        var matrix = QrCodeGenerator.EncodeMatrix(shortUrl);
        var n = matrix.GetLength(0);

        Assert.AreEqual(n, matrix.GetLength(1));
        Assert.IsTrue(n >= 21 && (n - 17) % 4 == 0, $"Dimensão QR inválida: {n}");

        // Verifica os 3 padrões localizadores 7x7 nos cantos esperados pela norma ISO 18004.
        static void AssertFinderAt(bool[,] m, int top, int left)
        {
            for (var r = 0; r < 7; r++)
            {
                for (var c = 0; c < 7; c++)
                {
                    var expectedDark = r == 0 || r == 6 || c == 0 || c == 6 || (r is >= 2 and <= 4 && c is >= 2 and <= 4);
                    Assert.AreEqual(expectedDark, m[top + r, left + c], $"Módulo finder divergente em ({top + r},{left + c}).");
                }
            }
        }

        AssertFinderAt(matrix, 0, 0);
        AssertFinderAt(matrix, 0, n - 7);
        AssertFinderAt(matrix, n - 7, 0);
        Assert.IsTrue(matrix[n - 8, 8], "Dark module obrigatório ausente em (4V+9, 8).");

        // URL longa deve escalar automaticamente para versão maior sem lançar exceção.
        var longUrl = "https://youlink-loja.onrender.com/loja/minha-super-loja-de-exemplo-online/produto/produto-exclusivo-com-nome-extenso-edicao-limitada-2026-colecao";
        var longMatrix = QrCodeGenerator.EncodeMatrix(longUrl);
        Assert.IsTrue(longMatrix.GetLength(0) > n);

        var dataUriA = WhatsAppLinkBuilder.GenerateQrCodeSvgDataUri(shortUrl);
        var dataUriB = WhatsAppLinkBuilder.GenerateQrCodeSvgDataUri(longUrl);
        Assert.IsTrue(dataUriA.StartsWith("data:image/svg+xml;base64,", StringComparison.Ordinal));
        Assert.IsFalse(string.Equals(dataUriA, dataUriB, StringComparison.Ordinal));

        var svgXml = Encoding.UTF8.GetString(Convert.FromBase64String(dataUriA["data:image/svg+xml;base64,".Length..]));
        Assert.IsTrue(svgXml.Contains("shape-rendering='crispEdges'", StringComparison.Ordinal));
        Assert.IsTrue(svgXml.Contains("<path d='M", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PwaManifestBuilder_ShouldScopeToStoreSlug_WhenStoreSlugProvided()
    {
        var settings = new PublicStoreSettingsDto(
            StoreName: "Casa Aurora Store",
            StoreSlug: "casa-aurora",
            WhatsappNumber: "5511999990000",
            StoreDescription: "Produtos selecionados para o dia a dia.",
            LogoPublicUrl: null,
            UpdatedAt: DateTimeOffset.UtcNow);

        var scopedJson = PwaManifestBuilder.BuildJson(settings, "Casa Aurora");
        using var doc = JsonDocument.Parse(scopedJson);
        var root = doc.RootElement;

        Assert.AreEqual("/loja/casa-aurora", root.GetProperty("id").GetString());
        Assert.AreEqual("/loja/casa-aurora", root.GetProperty("start_url").GetString());
        Assert.AreEqual("/loja/casa-aurora", root.GetProperty("scope").GetString());
    }

    [TestMethod]
    public void WhatsAppLinkBuilder_BuildCartOrder_ShouldFormatMultiItemMessageAndLink()
    {
        var items = new WhatsAppLinkBuilder.CartWhatsAppItem[]
        {
            new("Luminária Mesa LED", "Preto", "Bivolt", 2, 129.90m),
            new("Caneca Térmica", null, "500ml", 1, 79.50m)
        };

        var msg = WhatsAppLinkBuilder.BuildCartOrderMessage("Casa Tech", items, "Entregar após 14h");
        Assert.IsTrue(msg.Contains("Casa Tech", StringComparison.Ordinal));
        Assert.IsTrue(msg.Contains("2x Luminária Mesa LED (Preto / Bivolt)", StringComparison.Ordinal));
        Assert.IsTrue(msg.Contains("1x Caneca Térmica (500ml)", StringComparison.Ordinal));
        Assert.IsTrue(msg.Contains("Entregar após 14h", StringComparison.Ordinal));
        Assert.IsTrue(msg.Contains("R$ 339,30", StringComparison.Ordinal));

        var link = WhatsAppLinkBuilder.BuildCartOrderLink("55 (11) 98888-7777", "Casa Tech", items, "Entregar após 14h");
        Assert.IsTrue(link.StartsWith("https://wa.me/5511988887777?text=", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StoreDesignCssHelper_ShouldAllowAllPaletteFonts_AndExposeDefaultPortalFonts()
    {
        Assert.IsTrue(StoreDesignCssHelper.DefaultPortalGoogleFontsUrl.Contains("Playfair+Display", StringComparison.Ordinal));
        Assert.IsTrue(StoreDesignCssHelper.DefaultPortalGoogleFontsUrl.Contains("Plus+Jakarta+Sans", StringComparison.Ordinal));

        var editorial = StoreDesignPresets.ResolveByPresetKey("editorial-vogue");
        var fontsUrl = StoreDesignCssHelper.BuildGoogleFontsUrl(editorial);
        Assert.IsTrue(fontsUrl.Contains("Bodoni+Moda", StringComparison.Ordinal));
        Assert.IsTrue(fontsUrl.Contains("Inter", StringComparison.Ordinal));
        Assert.IsFalse(fontsUrl.Contains("Work+Sans", StringComparison.Ordinal));

        var custom = new StoreDesignDto
        {
            DisplayFont = "Space Grotesk",
            BodyFont = "Work Sans"
        };
        var customFontsUrl = StoreDesignCssHelper.BuildGoogleFontsUrl(custom);
        Assert.IsTrue(customFontsUrl.Contains("Space+Grotesk", StringComparison.Ordinal));
        Assert.IsTrue(customFontsUrl.Contains("Work+Sans", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WebAndApp_ShouldWireCompression_Seo_CopyPix_Cart_ShareQr_And_DeviceCompression()
    {
        var root = ResolveRepoRoot();

        var webProgram = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Web", "Program.cs"));
        Assert.IsTrue(webProgram.Contains("AddResponseCompression", StringComparison.Ordinal));
        Assert.IsTrue(webProgram.Contains("AddRequestDecompression", StringComparison.Ordinal));
        Assert.IsTrue(webProgram.Contains("UseRequestDecompression", StringComparison.Ordinal));
        Assert.IsTrue(webProgram.Contains("UseResponseCompression", StringComparison.Ordinal));
        Assert.IsTrue(webProgram.Contains("MapMethods(\"/health\", new[] { \"GET\", \"HEAD\" }", StringComparison.Ordinal));
        Assert.IsTrue(webProgram.Contains("\"/robots.txt\"", StringComparison.Ordinal));
        Assert.IsTrue(webProgram.Contains("\"/sitemap.xml\"", StringComparison.Ordinal));
        Assert.IsTrue(webProgram.Contains("\"/loja/{storeSlug}/media/product/{productId:guid}\"", StringComparison.Ordinal));
        Assert.IsTrue(webProgram.Contains("PwaManifestBuilder.BuildJson(settings, storeSlug)", StringComparison.Ordinal));

        var apiProgram = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Api", "Program.cs"));
        Assert.IsTrue(apiProgram.Contains("AddResponseCompression", StringComparison.Ordinal));
        Assert.IsTrue(apiProgram.Contains("AddRequestDecompression", StringComparison.Ordinal));
        Assert.IsTrue(apiProgram.Contains("MapMethods(\"/health\", new[] { \"GET\", \"HEAD\" }", StringComparison.Ordinal));

        var appRazor = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Web", "Components", "App.razor"));
        Assert.IsTrue(appRazor.Contains("store-utils.js", StringComparison.Ordinal));
        Assert.IsTrue(appRazor.Contains("BuildGoogleFontsUrl", StringComparison.Ordinal));
        Assert.IsFalse(appRazor.Contains("family=Bodoni+Moda:wght@500;600;700&family=Cormorant+Garamond", StringComparison.Ordinal));

        var detailRazor = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Web", "Components", "Pages", "ProductDetail.razor"));
        Assert.IsTrue(detailRazor.Contains("og:title", StringComparison.Ordinal));
        Assert.IsTrue(detailRazor.Contains("og:image", StringComparison.Ordinal));
        Assert.IsTrue(detailRazor.Contains("application/ld+json", StringComparison.Ordinal));
        Assert.IsTrue(detailRazor.Contains("Copiar Código Pix", StringComparison.Ordinal));
        Assert.IsTrue(detailRazor.Contains("Adicionar à Sacola", StringComparison.Ordinal));
        Assert.IsTrue(detailRazor.Contains("Compartilhar Produto", StringComparison.Ordinal));
        Assert.IsTrue(detailRazor.Contains("QR Code do Produto", StringComparison.Ordinal));

        var syncCs = File.ReadAllText(Path.Combine(root, "Services", "GoogleCloudSyncService.cs"));
        Assert.IsTrue(syncCs.Contains("GZipStream", StringComparison.Ordinal));
        Assert.IsTrue(syncCs.Contains("_autoSyncDebounceCts", StringComparison.Ordinal));

        Assert.IsTrue(File.Exists(Path.Combine(root, "Services", "DeviceImageCompressor.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "src", "ClothingStore.Web", "wwwroot", "store-utils.js")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "src", "ClothingStore.Web", "Services", "StoreCartService.cs")));
    }
}
