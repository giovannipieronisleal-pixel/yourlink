using System.Globalization;
using System.Text;
using System.Text.Json;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Application.Services;

/// <summary>
/// Assistente IA de marketing e cadastro de produtos usando a API Gemini v1 Interactions
/// com o modelo atual <c>gemini-4-hydrogen</c> e fallback inteligente offline caso não haja GEMINI_API_KEY.
/// </summary>
public sealed class GeminiProductAssistantService
{
    public const string DefaultGeminiModel = "gemini-4-hydrogen";
    private const string InteractionsEndpoint = "https://generativelanguage.googleapis.com/v1/interactions";
    private const string ApiRevisionHeaderValue = "2026-05-20";
    private static readonly CultureInfo PtBr = new("pt-BR");

    private readonly HttpClient _httpClient;
    private readonly Func<string?> _apiKeyProvider;

    public GeminiProductAssistantService(
        HttpClient httpClient,
        Func<string?>? apiKeyProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKeyProvider = apiKeyProvider ?? (() =>
            Environment.GetEnvironmentVariable("GEMINI_API_KEY")?.Trim()
            ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY")?.Trim());
    }

    public bool HasServerApiKey => !string.IsNullOrWhiteSpace(_apiKeyProvider());

    public async Task<GeneratedProductCopyDto> GenerateProductCopyAsync(
        GenerateProductCopyRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var productName = (request.ProductName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(productName))
            throw new ArgumentException("Informe o nome do produto para gerar o conteúdo com IA.", nameof(request));

        var effectiveKey = !string.IsNullOrWhiteSpace(request.CustomApiKey)
            ? request.CustomApiKey.Trim()
            : _apiKeyProvider();

        if (!string.IsNullOrWhiteSpace(effectiveKey))
        {
            var live = await TryCallGeminiInteractionsAsync(request, productName, effectiveKey, ct);
            if (live is not null)
                return live;
        }

        return BuildOfflineFallbackCopy(request, productName);
    }

    private async Task<GeneratedProductCopyDto?> TryCallGeminiInteractionsAsync(
        GenerateProductCopyRequest request,
        string productName,
        string apiKey,
        CancellationToken ct)
    {
        try
        {
            var tone = NormalizeTone(request.Tone);
            var priceText = request.Price > 0m ? request.Price.ToString("C", PtBr) : "preço sob consulta";
            var variantsText = request.VariantHints is { Count: > 0 }
                ? string.Join(", ", request.VariantHints.Where(v => !string.IsNullOrWhiteSpace(v)).Take(8))
                : "opções variadas em estoque";

            var systemInstruction =
                "Você é um redator sênior de e-commerce brasileiro especialista em conversão pelo WhatsApp, Instagram e catálogo digital. " +
                "Responda EXCLUSIVAMENTE com um objeto JSON válido (sem markdown, sem comentários) contendo exatamente as chaves: " +
                "suggestedTitle (string), persuasiveDescription (string, 2 a 3 frases objetivas em português do Brasil), " +
                "whatsappPromoMessage (string curta pronta para WhatsApp com chamada para ação), " +
                "instagramCaption (string envolvente com 3 a 5 hashtags), " +
                "seoTags (array de 4 a 6 strings curtas sem #), " +
                "bannerHeadline (string curta de até 8 palavras).";

            var userInput =
                $"Crie um kit de vendas para o produto:\n" +
                $"- Nome: {productName}\n" +
                $"- Categoria: {(string.IsNullOrWhiteSpace(request.CategoryName) ? "Geral" : request.CategoryName.Trim())}\n" +
                $"- Loja: {(string.IsNullOrWhiteSpace(request.StoreName) ? "Nossa Loja" : request.StoreName.Trim())}\n" +
                $"- Preço: {priceText}\n" +
                $"- Variações: {variantsText}\n" +
                $"- Descrição atual: {(string.IsNullOrWhiteSpace(request.CurrentDescription) ? "(vazia)" : request.CurrentDescription.Trim())}\n" +
                $"- Tom desejado: {tone}";

            var payload = new
            {
                model = DefaultGeminiModel,
                system_instruction = systemInstruction,
                input = userInput,
                store = false,
                generation_config = new
                {
                    thinking_level = "low",
                    max_output_tokens = 32768
                }
            };

            using var httpReq = new HttpRequestMessage(HttpMethod.Post, InteractionsEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            httpReq.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
            httpReq.Headers.TryAddWithoutValidation("Api-Revision", ApiRevisionHeaderValue);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(25));

            using var response = await _httpClient.SendAsync(httpReq, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
                return null;

            var raw = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var text = ExtractTextFromInteractionJson(raw);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var cleanJson = StripCodeFences(text);
            using var parsed = JsonDocument.Parse(cleanJson);
            var root = parsed.RootElement;

            var suggestedTitle = ReadString(root, "suggestedTitle") ?? productName;
            var persuasiveDescription = ReadString(root, "persuasiveDescription");
            var whatsappPromo = ReadString(root, "whatsappPromoMessage");
            var instagramCaption = ReadString(root, "instagramCaption");
            var bannerHeadline = ReadString(root, "bannerHeadline");
            var tags = ReadStringArray(root, "seoTags");

            if (string.IsNullOrWhiteSpace(persuasiveDescription))
                return null;

            var fallback = BuildOfflineFallbackCopy(request, productName);
            return new GeneratedProductCopyDto(
                SuggestedTitle: suggestedTitle,
                PersuasiveDescription: persuasiveDescription,
                WhatsappPromoMessage: string.IsNullOrWhiteSpace(whatsappPromo) ? fallback.WhatsappPromoMessage : whatsappPromo,
                InstagramCaption: string.IsNullOrWhiteSpace(instagramCaption) ? fallback.InstagramCaption : instagramCaption,
                SeoTags: tags.Count > 0 ? tags : fallback.SeoTags,
                BannerHeadline: string.IsNullOrWhiteSpace(bannerHeadline) ? fallback.BannerHeadline : bannerHeadline,
                ProviderUsed: $"Google Gemini ({DefaultGeminiModel})",
                UsedLiveGemini: true);
        }
        catch
        {
            return null;
        }
    }

    public static string? ExtractTextFromInteractionJson(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("output_text", out var directText) &&
                directText.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(directText.GetString()))
            {
                return directText.GetString();
            }

            if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var step in steps.EnumerateArray())
                {
                    var isModelOutput = !step.TryGetProperty("type", out var stepType) ||
                        string.Equals(stepType.GetString(), "model_output", StringComparison.OrdinalIgnoreCase);
                    if (!isModelOutput)
                        continue;

                    if (step.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in content.EnumerateArray())
                        {
                            if (item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                            {
                                var val = t.GetString();
                                if (!string.IsNullOrWhiteSpace(val))
                                    parts.Add(val);
                            }
                        }
                    }
                }

                if (parts.Count > 0)
                    return string.Join("\n", parts);
            }

            if (root.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in outputs.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString()))
                        return t.GetString();
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static GeneratedProductCopyDto BuildOfflineFallbackCopy(GenerateProductCopyRequest request, string productName)
    {
        var category = string.IsNullOrWhiteSpace(request.CategoryName) ? "Destaque" : request.CategoryName.Trim();
        var store = string.IsNullOrWhiteSpace(request.StoreName) ? "nossa loja" : request.StoreName.Trim();
        var tone = NormalizeTone(request.Tone);
        var priceText = request.Price > 0m ? request.Price.ToString("C", PtBr) : "condição especial";
        var variantLine = request.VariantHints is { Count: > 0 }
            ? $" Disponível em: {string.Join(", ", request.VariantHints.Where(v => !string.IsNullOrWhiteSpace(v)).Take(5))}."
            : string.Empty;

        var (title, desc, headline) = tone switch
        {
            "Promocional" => (
                $"{productName} — Oferta Especial",
                $"Aproveite {productName} na categoria {category} por apenas {priceText} na {store}. Qualidade selecionada com envio rápido e atendimento direto pelo WhatsApp ou Pix.{variantLine}",
                $"Oferta Imperdível: {productName}"
            ),
            "Direto" => (
                productName,
                $"{productName} ({category}) disponível na {store} por {priceText}. Pronta entrega com estoque atualizado em tempo real e compra rápida via Pix ou WhatsApp.{variantLine}",
                $"{productName} — Pronta Entrega"
            ),
            _ => (
                $"{productName} | Coleção {category}",
                $"Descubra {productName}, peça selecionada da linha {category} da {store} por {priceText}. Acabamento refinado pensado para unir estilo, praticidade e confiança no seu pedido.{variantLine}",
                $"Curadoria {category}: {productName}"
            )
        };

        var wa = $"✨ *{title}* chegou na *{store}*!\n💰 Valor: *{priceText}*\n🛍️ Estoque disponível para pedido imediato pelo catálogo ou WhatsApp. Garanta já o seu!";
        var cleanCatTag = new string(category.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var cleanStoreTag = new string(store.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var ig = $"{desc}\n\n📲 Peça pelo link da bio ou chame no WhatsApp!\n#novidades #{(string.IsNullOrEmpty(cleanCatTag) ? "ofertas" : cleanCatTag)} #{(string.IsNullOrEmpty(cleanStoreTag) ? "lojaonline" : cleanStoreTag)} #compras #pix";

        var tags = new List<string> { productName, category, "compra segura", "pix rapido", "entrega agil" }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new GeneratedProductCopyDto(
            SuggestedTitle: title,
            PersuasiveDescription: desc,
            WhatsappPromoMessage: wa,
            InstagramCaption: ig,
            SeoTags: tags,
            BannerHeadline: headline,
            ProviderUsed: "Assistente Inteligente YouLink",
            UsedLiveGemini: false);
    }

    private static string NormalizeTone(string? tone)
    {
        if (string.Equals(tone, "Promocional", StringComparison.OrdinalIgnoreCase))
            return "Promocional";
        if (string.Equals(tone, "Direto", StringComparison.OrdinalIgnoreCase))
            return "Direto";
        return "Luxo";
    }

    private static string StripCodeFences(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
                trimmed = trimmed[(firstNewline + 1)..];
            if (trimmed.EndsWith("```", StringComparison.Ordinal))
                trimmed = trimmed[..^3];
        }
        return trimmed.Trim();
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()?.Trim()
            : null;

    private static List<string> ReadStringArray(JsonElement root, string name)
    {
        var list = new List<string>();
        if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in prop.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    list.Add(item.GetString()!.Trim().TrimStart('#'));
            }
        }
        return list;
    }
}
