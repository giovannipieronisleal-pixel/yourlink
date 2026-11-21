using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ClothingStore.Shared.Helpers;

/// <summary>
/// Utilitário compartilhado entre o site Blazor e o aplicativo para construção do link oficial do WhatsApp
/// (https://wa.me/NUMERO?text=MENSAGEM), link personalizado da loja com o nome da loja (/loja/{slug}) e QR Code fixo.
/// </summary>
public sealed record WhatsAppPhoneValidationResult(
    bool IsValid,
    string NormalizedNumber,
    string FormattedDisplay,
    string Message);

public static class WhatsAppLinkBuilder
{
    private static readonly HashSet<int> ValidBrazilianDdds =
    [
        11, 12, 13, 14, 15, 16, 17, 18, 19,
        21, 22, 24, 27, 28,
        31, 32, 33, 34, 35, 37, 38,
        41, 42, 43, 44, 45, 46, 47, 48, 49,
        51, 53, 54, 55,
        61, 62, 63, 64, 65, 66, 67, 68, 69,
        71, 73, 74, 75, 77, 79,
        81, 82, 83, 84, 85, 86, 87, 88, 89,
        91, 92, 93, 94, 95, 96, 97, 98, 99
    ];

    public static string SanitizePhoneNumber(string? rawPhone)
    {
        if (string.IsNullOrWhiteSpace(rawPhone))
            return string.Empty;

        return Regex.Replace(rawPhone, @"\D", "");
    }

    public static WhatsAppPhoneValidationResult ValidateBrazilianWhatsAppNumber(string? rawPhone)
    {
        var digits = SanitizePhoneNumber(rawPhone);
        if (string.IsNullOrWhiteSpace(digits))
        {
            return new WhatsAppPhoneValidationResult(
                false,
                string.Empty,
                string.Empty,
                "Digite o DDD + número de celular do WhatsApp (ex.: 11999990000).");
        }

        if (digits.StartsWith('0') && digits.Length is 11 or 12)
        {
            digits = digits[1..];
        }

        if (digits.StartsWith("55", StringComparison.Ordinal) && digits.Length is 12 or 13)
        {
            digits = digits[2..];
        }

        if (digits.Length is not (10 or 11))
        {
            return new WhatsAppPhoneValidationResult(
                false,
                string.Empty,
                string.Empty,
                "Número incompleto ou inválido. Digite o DDD (2 dígitos) + celular com 9 dígitos (ex.: 11988887777).");
        }

        if (!int.TryParse(digits[..2], out var ddd) || !ValidBrazilianDdds.Contains(ddd))
        {
            return new WhatsAppPhoneValidationResult(
                false,
                string.Empty,
                string.Empty,
                $"O DDD '{digits[..2]}' não é um DDD brasileiro válido. Confira os 2 primeiros dígitos.");
        }

        var subscriber = digits[2..];
        if (subscriber.Length == 9 && subscriber[0] != '9')
        {
            return new WhatsAppPhoneValidationResult(
                false,
                string.Empty,
                string.Empty,
                "Celulares brasileiros de 9 dígitos devem começar com o dígito 9 após o DDD.");
        }

        if (subscriber is "900000000" or "00000000" or "999999999" ||
            subscriber.Distinct().Count() == 1 ||
            (subscriber.Length == 9 && subscriber[1..].Distinct().Count() == 1 && subscriber[1] is '0' or '9'))
        {
            return new WhatsAppPhoneValidationResult(
                false,
                string.Empty,
                string.Empty,
                "Informe um número de WhatsApp real (números de exemplo ou repetidos não são aceitos).");
        }

        var normalized = $"55{digits}";
        var formatted = subscriber.Length == 9
            ? $"({ddd:00}) {subscriber[..5]}-{subscriber[5..]}"
            : $"({ddd:00}) {subscriber[..4]}-{subscriber[4..]}";

        return new WhatsAppPhoneValidationResult(
            true,
            normalized,
            formatted,
            $"Número válido: {formatted}");
    }

    public static string GenerateWhatsAppVerificationCode() =>
        RandomNumberGenerator.GetInt32(1000, 10000).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Gera um código determinístico de 4 dígitos (1000–9999) vinculado ao número de WhatsApp informado.
    /// O cliente solicita o código no WhatsApp oficial de Suporte e o administrador gera/envia o mesmo código no YouLink Admin.
    /// </summary>
    public static string GenerateSupportWhatsAppVerificationCode(string? rawPhone)
    {
        var validation = ValidateBrazilianWhatsAppNumber(rawPhone);
        var normalized = validation.IsValid ? validation.NormalizedNumber : SanitizePhoneNumber(rawPhone);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var keyBytes = Encoding.UTF8.GetBytes("YouLink.SupportWhatsAppVerify.Key.v1");
        var dataBytes = Encoding.UTF8.GetBytes($"wa-verify:{normalized}");
        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(dataBytes);
        var value = BitConverter.ToUInt32(hash, 0) % 9000u + 1000u;
        return value.ToString("0000", CultureInfo.InvariantCulture);
    }

    public static string? BuildSupportVerificationRequestLink(
        string? supportPhone,
        string? merchantPhone,
        string? storeName = null)
    {
        var cleanSupport = SanitizeSupportPhone(supportPhone);
        if (string.IsNullOrWhiteSpace(cleanSupport))
            return null;

        var validation = ValidateBrazilianWhatsAppNumber(merchantPhone);
        var displayPhone = validation.IsValid ? validation.FormattedDisplay : (merchantPhone ?? string.Empty).Trim();
        var storeLabel = string.IsNullOrWhiteSpace(storeName) ? "minha loja" : $"a loja *{storeName.Trim()}*";
        var message = $"Olá Suporte YouLink! Quero confirmar o WhatsApp d{storeLabel}: *{displayPhone}*.\nPode me enviar meu código de verificação de 4 dígitos?";
        return $"https://wa.me/{cleanSupport}?text={Uri.EscapeDataString(message)}";
    }

    public static string? BuildSupportVerificationReplyLink(
        string? merchantPhone,
        string verificationCode,
        string? storeName = null)
    {
        var validation = ValidateBrazilianWhatsAppNumber(merchantPhone);
        if (!validation.IsValid)
            return null;

        var cleanCode = (verificationCode ?? string.Empty).Trim();
        var storeLabel = string.IsNullOrWhiteSpace(storeName) ? "YouLink" : $"YouLink ({storeName.Trim()})";
        var message = $"✅ Seu código de verificação do WhatsApp no {storeLabel} é: *{cleanCode}*\n\nDigite esses 4 números no aplicativo para concluir a autenticação.";
        return $"https://wa.me/{validation.NormalizedNumber}?text={Uri.EscapeDataString(message)}";
    }

    public static string BuildWhatsAppVerificationLink(string? rawPhone, string verificationCode, string? storeName = null)
    {
        var validation = ValidateBrazilianWhatsAppNumber(rawPhone);
        var phone = validation.IsValid ? validation.NormalizedNumber : SanitizePhoneNumber(rawPhone);
        var cleanCode = (verificationCode ?? string.Empty).Trim();
        var storeLabel = string.IsNullOrWhiteSpace(storeName) ? "YouLink" : $"YouLink ({storeName.Trim()})";
        var message = $"🔐 Código de autenticação do seu WhatsApp no {storeLabel}: *{cleanCode}*\n\nVolte ao aplicativo e digite os 4 números acima para confirmar seu WhatsApp.";
        return $"https://wa.me/{phone}?text={Uri.EscapeDataString(message)}";
    }

    public static string? SanitizeSupportPhone(string? rawPhone)
    {
        var digits = SanitizePhoneNumber(rawPhone);
        if (digits.Length < 10)
            return null;

        if (digits is "5511900000000" or "11900000000" or "00000000000")
            return null;

        if ((digits.Length == 10 || digits.Length == 11) && !digits.StartsWith("55", StringComparison.Ordinal))
            digits = $"55{digits}";

        if (digits.Length > 15 || digits == "5511900000000")
            return null;

        return digits;
    }

    public static string? BuildSupportWhatsappUrl(string? supportPhone, string? message = null)
    {
        var digits = SanitizeSupportPhone(supportPhone);
        if (string.IsNullOrWhiteSpace(digits))
            return null;

        var defaultMessage = string.IsNullOrWhiteSpace(message)
            ? "Olá! Preciso de ajuda com o YouLink."
            : message.Trim();
        return $"https://wa.me/{digits}?text={Uri.EscapeDataString(defaultMessage)}";
    }

    public static string GenerateStoreSlug(string? storeName)
    {
        if (string.IsNullOrWhiteSpace(storeName))
            return "minha-loja";

        var normalized = storeName.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        var clean = sb.ToString().Normalize(NormalizationForm.FormC);
        clean = Regex.Replace(clean, @"[^a-z0-9\s-]", "");
        clean = Regex.Replace(clean, @"\s+", "-");
        clean = Regex.Replace(clean, @"-+", "-").Trim('-');
        if (clean.Length > 80)
        {
            clean = clean[..80].TrimEnd('-');
        }
        return string.IsNullOrWhiteSpace(clean) ? "minha-loja" : clean;
    }

    public static string NormalizePublicServerUrl(string? rawUrl)
    {
        var candidate = (rawUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            return string.Empty;

        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = $"https://{candidate}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            IsCloudMetadataHost(uri.Host))
        {
            return string.Empty;
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    public static bool IsCloudMetadataHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var normalized = host.Trim().Trim('[', ']').ToLowerInvariant();
        return normalized.StartsWith("169.254.", StringComparison.Ordinal) ||
               normalized.StartsWith("::ffff:169.254.", StringComparison.Ordinal) ||
               normalized == "metadata.google.internal" ||
               normalized == "metadata.goog" ||
               normalized == "metadata";
    }

    private static readonly Regex SafeDataImageRegex = new(
        @"^data:image/(png|jpeg|jpg|webp|gif|avif|bmp);base64,[A-Za-z0-9+/=\s]+$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GoogleDriveFileIdRegex = new(
        @"drive\.google\.com/(?:file/d/|open\?id=)([A-Za-z0-9_-]{10,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MetaOgImageRegex = new(
        @"<meta\s+[^>]*?(?:property|name)\s*=\s*[""'](?:og:image|twitter:image)[""'][^>]*?content\s*=\s*[""']([^""']+)[""']|<meta\s+[^>]*?content\s*=\s*[""']([^""']+)[""'][^>]*?(?:property|name)\s*=\s*[""'](?:og:image|twitter:image)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string NormalizeExternalImageUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
            return string.Empty;

        var candidate = rawUrl.Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(candidate))
            return string.Empty;

        if (candidate.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
        {
            return candidate;
        }

        if (candidate.StartsWith("//", StringComparison.Ordinal))
        {
            candidate = "https:" + candidate;
        }
        else if (!candidate.Contains("://", StringComparison.Ordinal) &&
                 !candidate.Contains(':', StringComparison.Ordinal) &&
                 candidate.Contains('.', StringComparison.Ordinal) &&
                 !candidate.StartsWith('/'))
        {
            candidate = "https://" + candidate;
        }

        // Desempacota links copiados do Google Imagens (ex.: https://www.google.com/imgres?imgurl=https%3A%2F%2F...)
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var parsedUri))
        {
            var query = parsedUri.Query;
            if (!string.IsNullOrWhiteSpace(query))
            {
                foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split('=', 2);
                    if (kv.Length == 2 &&
                        (string.Equals(kv[0], "imgurl", StringComparison.OrdinalIgnoreCase) ||
                         (parsedUri.Host.Contains("google.", StringComparison.OrdinalIgnoreCase) &&
                          string.Equals(kv[0], "url", StringComparison.OrdinalIgnoreCase))))
                    {
                        var decoded = Uri.UnescapeDataString(kv[1]).Trim();
                        if (Uri.TryCreate(decoded, UriKind.Absolute, out var innerUri) &&
                            (string.Equals(innerUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(innerUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
                        {
                            candidate = decoded;
                            parsedUri = innerUri;
                            break;
                        }
                    }
                }
            }

            // Converte links de compartilhamento do Google Drive em link direto de imagem
            var driveMatch = GoogleDriveFileIdRegex.Match(candidate);
            if (driveMatch.Success)
            {
                candidate = $"https://drive.google.com/uc?export=view&id={driveMatch.Groups[1].Value}";
            }
            else if (parsedUri.Host.Contains("dropbox.com", StringComparison.OrdinalIgnoreCase) &&
                     candidate.EndsWith("?dl=0", StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate[..^5] + "?raw=1";
            }
        }

        return candidate;
    }

    public static string? ExtractOpenGraphImageUrl(string? html, Uri? pageUri = null)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var match = MetaOgImageRegex.Match(html);
        if (!match.Success)
            return null;

        var raw = !string.IsNullOrWhiteSpace(match.Groups[1].Value)
            ? match.Groups[1].Value
            : match.Groups[2].Value;

        raw = System.Net.WebUtility.HtmlDecode(raw).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (raw.StartsWith("//", StringComparison.Ordinal))
            return "https:" + raw;

        if (Uri.TryCreate(raw, UriKind.Absolute, out var absUri))
            return absUri.ToString();

        if (pageUri is not null && Uri.TryCreate(pageUri, raw, out var resolvedUri))
            return resolvedUri.ToString();

        return null;
    }

    public static bool IsSafeMediaUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
            return false;

        var candidate = NormalizeExternalImageUrl(rawUrl);
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 8_500_000 || candidate.Any(c => char.IsControl(c)))
            return false;

        if (candidate.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
            return !candidate.Contains("..", StringComparison.Ordinal);

        if (candidate.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return SafeDataImageRegex.IsMatch(candidate);

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            return false;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return false;

        return !string.IsNullOrWhiteSpace(uri.Host) && !IsCloudMetadataHost(uri.Host);
    }

    public static string SanitizeMediaUrl(string? rawUrl, string fallback = "")
    {
        var normalized = NormalizeExternalImageUrl(rawUrl);
        if (!IsSafeMediaUrl(normalized))
            return fallback;

        return normalized;
    }

    public static string NormalizeSyncSecretInput(string? rawSecret)
    {
        var candidate = (rawSecret ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            return string.Empty;

        const string envPrefix = "YOULINK_SYNC_SECRET=";
        if (candidate.StartsWith(envPrefix, StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[envPrefix.Length..].Trim();
        }

        if (candidate.Length >= 2 &&
            ((candidate.StartsWith('"') && candidate.EndsWith('"')) ||
             (candidate.StartsWith('\'') && candidate.EndsWith('\''))))
        {
            candidate = candidate[1..^1].Trim();
        }

        return candidate;
    }

    /// <summary>
    /// Gera o domínio padrão do catálogo no formato lojas.nomedaloja.com (ex.: lojas.minhaloja.com).
    /// </summary>
    public static string GenerateDefaultStoreDomain(string? storeName)
    {
        var clean = GenerateStoreSlug(storeName).Replace("-", "");
        if (string.IsNullOrWhiteSpace(clean))
            clean = "minhaloja";
        return $"lojas.{clean}.com";
    }

    /// <summary>
    /// Gera a URL pública padrão do catálogo no formato https://lojas.nomedaloja.com
    /// </summary>
    public static string GenerateDefaultStoreUrl(string? storeName)
    {
        return $"https://{GenerateDefaultStoreDomain(storeName)}";
    }

    private static readonly string[] SharedCloudHostSuffixes =
    [
        ".run.app",
        ".onrender.com",
        ".koyeb.app",
        ".fly.dev",
        ".up.railway.app",
        ".azurewebsites.net",
        ".pages.dev",
        ".workers.dev",
        ".github.io"
    ];

    private static bool IsSharedCloudHost(string host) =>
        host is "localhost" or "127.0.0.1" ||
        SharedCloudHostSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    public static string BuildCentralTenantCatalogUrl(string? centralServerBaseUrl, string? storeSlugOrName)
    {
        if (string.IsNullOrWhiteSpace(centralServerBaseUrl))
        {
            return GenerateDefaultStoreUrl(storeSlugOrName);
        }

        var cleanBase = centralServerBaseUrl.Trim().TrimEnd('/');
        var slug = GenerateStoreSlug(storeSlugOrName);

        var uriText = cleanBase.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                      cleanBase.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? cleanBase
            : $"https://{cleanBase}";

        if (Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.TrimEnd('/');
            if (path.EndsWith($"/loja/{slug}", StringComparison.OrdinalIgnoreCase))
            {
                return uriText;
            }
        }

        return $"{uriText.TrimEnd('/')}/loja/{slug}";
    }

    public static string BuildStoreCatalogUrl(string? baseSiteUrl, string? storeSlugOrName)
    {
        if (string.IsNullOrWhiteSpace(baseSiteUrl))
        {
            return GenerateDefaultStoreUrl(storeSlugOrName);
        }

        var cleanBase = baseSiteUrl.Trim().TrimEnd('/');

        var slug = GenerateStoreSlug(storeSlugOrName);

        var uriText = cleanBase.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                      cleanBase.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? cleanBase
            : $"https://{cleanBase}";

        if (Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.ToLowerInvariant();
            // Para servidor local ou hospedagem compartilhada gratuita/nuvem (Cloud Run, Render, Koyeb, Fly.io, etc.),
            // roteia diretamente para /loja/{slug} sem exigir compra de domínio separado por cliente.
            if (IsSharedCloudHost(host))
            {
                var path = uri.AbsolutePath.TrimEnd('/');
                if (path.EndsWith($"/loja/{slug}", StringComparison.OrdinalIgnoreCase))
                {
                    return cleanBase;
                }

                return $"{cleanBase}/loja/{slug}";
            }

            // Para domínio próprio oficial do lojista (ex.: https://minhalojadigital.com ou minhaloja.com.br):
            // O catálogo oficial é o próprio domínio direto da loja, limpo e profissional!
            return cleanBase;
        }

        return $"{cleanBase}/loja/{slug}";
    }

    public static bool IsPlaceholderSize(string? size)
    {
        if (string.IsNullOrWhiteSpace(size))
            return true;

        var normalized = size.Trim().ToUpperInvariant();
        return normalized is "ÚNICO" or "UNICO" or "ÚNICA" or "UNICA" or "PADRÃO" or "PADRAO" or "-" or "—" or "N/A";
    }

    public static bool IsPlaceholderColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
            return true;

        var normalized = color.Trim().ToUpperInvariant();
        return normalized is "PADRÃO" or "PADRAO" or "ÚNICO" or "UNICO" or "-" or "—" or "N/A";
    }

    public static string BuildProductConsultationMessage(string productName, string? color = null, string? size = null)
    {
        var cleanName = string.IsNullOrWhiteSpace(productName) ? "Produto" : productName.Trim();
        var hasColor = !IsPlaceholderColor(color);
        var hasSize = !IsPlaceholderSize(size);

        if (hasColor && hasSize)
        {
            return $"Olá! Tenho interesse no produto {cleanName}, na cor {color!.Trim().ToLowerInvariant()} e tamanho {size!.Trim().ToUpperInvariant()}. Gostaria de saber mais informações.";
        }

        if (hasColor)
        {
            return $"Olá! Tenho interesse no produto {cleanName}, na cor {color!.Trim().ToLowerInvariant()}. Gostaria de saber mais informações.";
        }

        if (hasSize)
        {
            return $"Olá! Tenho interesse no produto {cleanName}, no tamanho {size!.Trim().ToUpperInvariant()}. Gostaria de saber mais informações.";
        }

        return $"Olá! Tenho interesse no produto {cleanName}. Gostaria de saber mais informações.";
    }

    public static string BuildProductConsultationLink(string whatsappNumber, string productName, string? color = null, string? size = null)
    {
        var phone = SanitizePhoneNumber(whatsappNumber);
        var message = BuildProductConsultationMessage(productName, color, size);
        var encoded = Uri.EscapeDataString(message);
        return $"https://wa.me/{phone}?text={encoded}";
    }

    public static string BuildGeneralStoreLink(string whatsappNumber, string storeName)
    {
        var phone = SanitizePhoneNumber(whatsappNumber);
        var message = $"Olá! Vim pelo catálogo online da {storeName.Trim()} e gostaria de atendimento.";
        return $"https://wa.me/{phone}?text={Uri.EscapeDataString(message)}";
    }

    public sealed record CartWhatsAppItem(
        string ProductName,
        string? Color,
        string? Size,
        int Quantity,
        decimal UnitPrice);

    public static string BuildCartOrderMessage(
        string storeName,
        IReadOnlyList<CartWhatsAppItem> items,
        string? customerNotes = null)
    {
        var ptBr = CultureInfo.GetCultureInfo("pt-BR");
        var cleanStore = string.IsNullOrWhiteSpace(storeName) ? "Loja" : storeName.Trim();
        if (items is null || items.Count == 0)
            return $"Olá! Vim pelo catálogo online da {cleanStore} e gostaria de fazer um pedido.";

        var sb = new StringBuilder();
        sb.AppendLine($"Olá, *{cleanStore}*! Gostaria de pedir os seguintes itens da sacola:");
        sb.AppendLine();

        decimal total = 0m;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var qty = Math.Max(1, item.Quantity);
            var lineTotal = item.UnitPrice * qty;
            total += lineTotal;

            var variantParts = new List<string>(2);
            if (!IsPlaceholderColor(item.Color))
                variantParts.Add(item.Color!.Trim());
            if (!IsPlaceholderSize(item.Size))
                variantParts.Add(item.Size!.Trim());

            var variantSuffix = variantParts.Count > 0 ? $" ({string.Join(" / ", variantParts)})" : string.Empty;
            sb.AppendLine($"{i + 1}. {qty}x {item.ProductName.Trim()}{variantSuffix} — {lineTotal.ToString("C", ptBr)}");
        }

        sb.AppendLine();
        sb.Append($"*Total estimado:* {total.ToString("C", ptBr)}");
        if (!string.IsNullOrWhiteSpace(customerNotes))
        {
            sb.AppendLine();
            sb.Append($"Observação: {customerNotes.Trim()}");
        }

        return sb.ToString();
    }

    public static string BuildCartOrderLink(
        string whatsappNumber,
        string storeName,
        IReadOnlyList<CartWhatsAppItem> items,
        string? customerNotes = null)
    {
        var phone = SanitizePhoneNumber(whatsappNumber);
        var message = BuildCartOrderMessage(storeName, items, customerNotes);
        return $"https://wa.me/{phone}?text={Uri.EscapeDataString(message)}";
    }

    /// <summary>
    /// Gera um SVG real (ISO/IEC 18004) representando o QR Code do link da loja/produto para leitura por câmera.
    /// </summary>
    public static string GenerateQrCodeSvgDataUri(string storeUrl) =>
        QrCodeGenerator.GenerateSvgDataUri(storeUrl);
}
