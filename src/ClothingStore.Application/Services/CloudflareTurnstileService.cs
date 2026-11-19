using System.Text.Json;
using System.Text.Json.Serialization;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Application.Services;

/// <summary>
/// Verificação server-side canônica do Cloudflare Turnstile (/turnstile/v0/siteverify).
/// Quando as variáveis CLOUDFLARE_TURNSTILE_SITE_KEY e CLOUDFLARE_TURNSTILE_SECRET_KEY estão configuradas,
/// exige token válido, fail-closed em erro de rede, validação de action e hostname permitido.
/// </summary>
public sealed class CloudflareTurnstileService
{
    private const string SiteVerifyEndpoint = "https://challenges.cloudflare.com/turnstile/v0/siteverify";
    private const int MaxTokenLength = 2048;

    private readonly HttpClient _httpClient;
    private readonly Func<(string? SiteKey, string? SecretKey, IReadOnlySet<string> AllowedHostnames)> _configProvider;

    public CloudflareTurnstileService(
        HttpClient httpClient,
        Func<(string? SiteKey, string? SecretKey, IReadOnlySet<string> AllowedHostnames)>? configProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _configProvider = configProvider ?? ReadFromEnvironment;
    }

    public bool IsConfigured
    {
        get
        {
            var cfg = _configProvider();
            return !string.IsNullOrWhiteSpace(cfg.SiteKey) && !string.IsNullOrWhiteSpace(cfg.SecretKey);
        }
    }

    public PublicSecurityConfigDto GetPublicConfig()
    {
        var cfg = _configProvider();
        var enabled = !string.IsNullOrWhiteSpace(cfg.SiteKey) && !string.IsNullOrWhiteSpace(cfg.SecretKey);
        return new PublicSecurityConfigDto(enabled, enabled ? cfg.SiteKey!.Trim() : null);
    }

    public async Task<(bool IsValid, string? ErrorMessage)> VerifyTokenAsync(
        string? token,
        string expectedAction,
        string? remoteIp = null,
        CancellationToken ct = default)
    {
        var cfg = _configProvider();
        if (string.IsNullOrWhiteSpace(cfg.SiteKey) || string.IsNullOrWhiteSpace(cfg.SecretKey))
            return (true, null);

        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
            return (false, "Complete a verificação de segurança anti-bot (Cloudflare Turnstile) antes de continuar.");

        if (cfg.AllowedHostnames.Count == 0)
            return (false, "Verificação anti-bot indisponível: domínios autorizados não configurados no servidor.");

        try
        {
            var formFields = new List<KeyValuePair<string, string>>(3)
            {
                new("secret", cfg.SecretKey.Trim()),
                new("response", token.Trim())
            };
            if (!string.IsNullOrWhiteSpace(remoteIp))
                formFields.Add(new("remoteip", remoteIp.Trim()));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            using var content = new FormUrlEncodedContent(formFields);
            using var response = await _httpClient.PostAsync(SiteVerifyEndpoint, content, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
                return (false, "Não foi possível validar a verificação anti-bot agora. Tente novamente.");

            var raw = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var parsed = JsonSerializer.Deserialize<TurnstileSiteVerifyResponse>(
                raw,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            if (parsed is null || !parsed.Success)
                return (false, "Verificação anti-bot inválida ou expirada. Atualize o desafio e tente novamente.");

            if (!string.IsNullOrWhiteSpace(expectedAction) &&
                !string.IsNullOrWhiteSpace(parsed.Action) &&
                !string.Equals(parsed.Action.Trim(), expectedAction.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Verificação anti-bot emitida para outra ação. Recarregue e tente novamente.");
            }

            if (string.IsNullOrWhiteSpace(parsed.Hostname) ||
                !cfg.AllowedHostnames.Contains(parsed.Hostname.Trim()))
            {
                return (false, "Domínio da verificação anti-bot não autorizado.");
            }

            return (true, null);
        }
        catch
        {
            return (false, "Falha de conexão ao validar verificação anti-bot. Tente novamente.");
        }
    }

    public static (string? SiteKey, string? SecretKey, IReadOnlySet<string> AllowedHostnames) ReadFromEnvironment()
    {
        var siteKey = Environment.GetEnvironmentVariable("CLOUDFLARE_TURNSTILE_SITE_KEY")?.Trim();
        var secretKey = Environment.GetEnvironmentVariable("CLOUDFLARE_TURNSTILE_SECRET_KEY")?.Trim();
        var rawHosts = Environment.GetEnvironmentVariable("CLOUDFLARE_TURNSTILE_HOSTNAMES");
        var hostSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(rawHosts))
        {
            foreach (var h in rawHosts.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                hostSet.Add(h);
        }
        else if (!string.IsNullOrWhiteSpace(siteKey) && !string.IsNullOrWhiteSpace(secretKey))
        {
            hostSet.Add("youlink-loja.onrender.com");
            hostSet.Add("youlink-edge-accelerator.youlink-edge.workers.dev");
        }

        return (siteKey, secretKey, hostSet);
    }

    private sealed class TurnstileSiteVerifyResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("hostname")]
        public string? Hostname { get; set; }

        [JsonPropertyName("action")]
        public string? Action { get; set; }

        [JsonPropertyName("error-codes")]
        public List<string>? ErrorCodes { get; set; }
    }
}
