using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Application.Services;

public sealed record EmailOtpConfig(
    string? ResendApiKey,
    string? BrevoApiKey,
    string? FromEmail,
    string? FromName,
    string? SmtpHost,
    int SmtpPort,
    string? SmtpUsername,
    string? SmtpPassword);

/// <summary>
/// Serviço oficial de envio e validação de códigos OTP de 6 dígitos por E-mail.
/// Suporta Resend (HTTPS), Brevo (HTTPS) e SMTP TLS (ex.: Gmail Senha de App),
/// lendo todas as credenciais exclusivamente de variáveis de ambiente (nunca hardcoded).
/// </summary>
public sealed class EmailOtpService
{
    public const int OtpExpirySeconds = 600;       // 10 minutos
    public const int ResendCooldownSeconds = 45;   // 45 segundos entre envios
    public const int MaxVerifyAttempts = 5;

    private readonly HttpClient _httpClient;
    private readonly Func<EmailOtpConfig> _configProvider;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, OtpChallengeEntry> _challenges = new(StringComparer.Ordinal);

    private sealed record OtpChallengeEntry(
        byte[] Salt,
        byte[] CodeHash,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        int FailedAttempts);

    public EmailOtpService(
        HttpClient httpClient,
        Func<EmailOtpConfig>? configProvider = null,
        Func<DateTimeOffset>? clock = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _configProvider = configProvider ?? ReadFromEnvironment;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public bool IsConfigured => !string.Equals(ResolveActiveProvider(_configProvider()), "None", StringComparison.Ordinal);

    public EmailOtpStatusDto GetPublicStatus()
    {
        var provider = ResolveActiveProvider(_configProvider());
        var configured = !string.Equals(provider, "None", StringComparison.Ordinal);
        return new EmailOtpStatusDto(
            IsConfigured: configured,
            Provider: provider,
            Message: configured
                ? $"Envio automático de códigos por e-mail ativo ({provider})."
                : "Envio automático de e-mail aguardando configuração de variáveis de ambiente no servidor.");
    }

    public static string NormalizeEmail(string? rawEmail)
    {
        if (string.IsNullOrWhiteSpace(rawEmail))
            return string.Empty;

        var trimmed = rawEmail.Trim().ToLowerInvariant();
        var atIndex = trimmed.IndexOf('@');
        if (atIndex <= 0 || atIndex != trimmed.LastIndexOf('@') || atIndex >= trimmed.Length - 3)
            return string.Empty;

        var domain = trimmed[(atIndex + 1)..];
        if (!domain.Contains('.', StringComparison.Ordinal) || domain.StartsWith('.') || domain.EndsWith('.'))
            return string.Empty;

        if (trimmed.Any(char.IsWhiteSpace))
            return string.Empty;

        return trimmed;
    }

    public static string MaskEmail(string normalizedEmail)
    {
        if (string.IsNullOrWhiteSpace(normalizedEmail))
            return string.Empty;

        var parts = normalizedEmail.Split('@');
        if (parts.Length != 2)
            return normalizedEmail;

        var local = parts[0];
        var visible = local.Length <= 2 ? local[..1] : local[..2];
        return $"{visible}***@{parts[1]}";
    }

    public async Task<SendEmailOtpResponse> SendOtpAsync(
        SendEmailOtpRequest request,
        CancellationToken ct = default)
    {
        var normalizedEmail = NormalizeEmail(request?.Email);
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return new SendEmailOtpResponse(
                Sent: false,
                ProviderConfigured: IsConfigured,
                MaskedEmail: string.Empty,
                ExpiresInSeconds: 0,
                ResendAfterSeconds: 0,
                Message: "Informe um endereço de e-mail válido para receber o código.");
        }

        var masked = MaskEmail(normalizedEmail);
        var cfg = _configProvider();
        var provider = ResolveActiveProvider(cfg);
        if (string.Equals(provider, "None", StringComparison.Ordinal))
        {
            return new SendEmailOtpResponse(
                Sent: false,
                ProviderConfigured: false,
                MaskedEmail: masked,
                ExpiresInSeconds: 0,
                ResendAfterSeconds: 0,
                Message: "O envio automático por e-mail ainda não foi configurado nas variáveis do servidor.");
        }

        var now = _clock();
        PruneExpiredChallenges(now);

        var key = BuildChallengeKey(normalizedEmail, request?.Purpose);
        if (_challenges.TryGetValue(key, out var existing))
        {
            var elapsed = (now - existing.CreatedAt).TotalSeconds;
            if (elapsed < ResendCooldownSeconds)
            {
                var remaining = Math.Max(1, (int)Math.Ceiling(ResendCooldownSeconds - elapsed));
                return new SendEmailOtpResponse(
                    Sent: false,
                    ProviderConfigured: true,
                    MaskedEmail: masked,
                    ExpiresInSeconds: Math.Max(0, (int)(existing.ExpiresAt - now).TotalSeconds),
                    ResendAfterSeconds: remaining,
                    Message: $"Aguarde {remaining}s antes de solicitar um novo código.");
            }
        }

        var code = RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString();
        var contextLabel = string.IsNullOrWhiteSpace(request?.ContextLabel)
            ? "verificação da sua conta"
            : request!.ContextLabel!.Trim();

        var (ok, errorMessage) = provider switch
        {
            "Resend" => await SendViaResendAsync(cfg, normalizedEmail, code, contextLabel, ct),
            "Brevo" => await SendViaBrevoAsync(cfg, normalizedEmail, code, contextLabel, ct),
            "Smtp" => await SendViaSmtpAsync(cfg, normalizedEmail, code, contextLabel, ct),
            _ => (false, "Provedor de e-mail não configurado.")
        };

        if (!ok)
        {
            return new SendEmailOtpResponse(
                Sent: false,
                ProviderConfigured: true,
                MaskedEmail: masked,
                ExpiresInSeconds: 0,
                ResendAfterSeconds: 0,
                Message: errorMessage ?? "Não foi possível enviar o e-mail de verificação agora.");
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = ComputeCodeHash(salt, code);
        _challenges[key] = new OtpChallengeEntry(
            Salt: salt,
            CodeHash: hash,
            CreatedAt: now,
            ExpiresAt: now.AddSeconds(OtpExpirySeconds),
            FailedAttempts: 0);

        return new SendEmailOtpResponse(
            Sent: true,
            ProviderConfigured: true,
            MaskedEmail: masked,
            ExpiresInSeconds: OtpExpirySeconds,
            ResendAfterSeconds: ResendCooldownSeconds,
            Message: $"Código de 6 dígitos enviado para {masked}! Verifique a Caixa de Entrada (ou abas Atualizações/Promoções/Spam — válido por 10 min).");
    }

    public VerifyEmailOtpResponse VerifyOtp(VerifyEmailOtpRequest request)
    {
        var normalizedEmail = NormalizeEmail(request?.Email);
        var cleanCode = new string((request?.Code ?? string.Empty).Where(char.IsDigit).ToArray());

        if (string.IsNullOrWhiteSpace(normalizedEmail) || cleanCode.Length != 6)
        {
            return new VerifyEmailOtpResponse(
                Verified: false,
                NormalizedEmail: normalizedEmail,
                Message: "Informe o e-mail e o código de 6 dígitos enviado.");
        }

        var now = _clock();
        PruneExpiredChallenges(now);

        var key = BuildChallengeKey(normalizedEmail, request?.Purpose);
        if (!_challenges.TryGetValue(key, out var entry))
        {
            return new VerifyEmailOtpResponse(
                Verified: false,
                NormalizedEmail: normalizedEmail,
                Message: "Nenhum código ativo encontrado para este e-mail. Solicite um novo código.");
        }

        if (now > entry.ExpiresAt)
        {
            _challenges.TryRemove(key, out _);
            return new VerifyEmailOtpResponse(
                Verified: false,
                NormalizedEmail: normalizedEmail,
                Message: "Este código expirou. Solicite um novo código por e-mail.");
        }

        var candidateHash = ComputeCodeHash(entry.Salt, cleanCode);
        if (CryptographicOperations.FixedTimeEquals(candidateHash, entry.CodeHash))
        {
            _challenges.TryRemove(key, out _);
            return new VerifyEmailOtpResponse(
                Verified: true,
                NormalizedEmail: normalizedEmail,
                Message: "Código confirmado com sucesso!");
        }

        var nextAttempts = entry.FailedAttempts + 1;
        if (nextAttempts >= MaxVerifyAttempts)
        {
            _challenges.TryRemove(key, out _);
            return new VerifyEmailOtpResponse(
                Verified: false,
                NormalizedEmail: normalizedEmail,
                Message: "Muitas tentativas incorretas. Solicite um novo código por e-mail.");
        }

        _challenges[key] = entry with { FailedAttempts = nextAttempts };
        return new VerifyEmailOtpResponse(
            Verified: false,
            NormalizedEmail: normalizedEmail,
            Message: $"Código incorreto. Restam {MaxVerifyAttempts - nextAttempts} tentativa(s).");
    }

    private static string ResolveActiveProvider(EmailOtpConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.ResendApiKey))
            return "Resend";
        if (!string.IsNullOrWhiteSpace(cfg.BrevoApiKey) && !string.IsNullOrWhiteSpace(cfg.FromEmail))
            return "Brevo";
        if (!string.IsNullOrWhiteSpace(cfg.SmtpHost) &&
            !string.IsNullOrWhiteSpace(cfg.SmtpUsername) &&
            !string.IsNullOrWhiteSpace(cfg.SmtpPassword) &&
            cfg.SmtpPort > 0)
            return "Smtp";

        return "None";
    }

    private async Task<(bool Ok, string? ErrorMessage)> SendViaResendAsync(
        EmailOtpConfig cfg,
        string recipientEmail,
        string code,
        string contextLabel,
        CancellationToken ct)
    {
        var fromEmail = string.IsNullOrWhiteSpace(cfg.FromEmail) ? "onboarding@resend.dev" : cfg.FromEmail.Trim();
        var fromName = string.IsNullOrWhiteSpace(cfg.FromName) ? "YouLink" : cfg.FromName.Trim();
        var payload = new
        {
            from = $"{fromName} <{fromEmail}>",
            to = new[] { recipientEmail },
            subject = BuildEmailSubject(code),
            html = BuildEmailHtml(code, contextLabel),
            text = BuildEmailPlainText(code, contextLabel),
            headers = new Dictionary<string, string>
            {
                ["X-Entity-Ref-ID"] = Guid.NewGuid().ToString("N")
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ResendApiKey!.Trim());
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        return await ExecuteProviderRequestAsync(req, "Resend", ct);
    }

    private async Task<(bool Ok, string? ErrorMessage)> SendViaBrevoAsync(
        EmailOtpConfig cfg,
        string recipientEmail,
        string code,
        string contextLabel,
        CancellationToken ct)
    {
        var fromName = string.IsNullOrWhiteSpace(cfg.FromName) ? "YouLink" : cfg.FromName.Trim();
        var payload = new
        {
            sender = new { name = fromName, email = cfg.FromEmail!.Trim() },
            to = new[] { new { email = recipientEmail } },
            subject = BuildEmailSubject(code),
            htmlContent = BuildEmailHtml(code, contextLabel),
            textContent = BuildEmailPlainText(code, contextLabel),
            headers = new Dictionary<string, string>
            {
                ["X-Entity-Ref-ID"] = Guid.NewGuid().ToString("N")
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email");
        req.Headers.Add("api-key", cfg.BrevoApiKey!.Trim());
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        return await ExecuteProviderRequestAsync(req, "Brevo", ct);
    }

    private static async Task<(bool Ok, string? ErrorMessage)> SendViaSmtpAsync(
        EmailOtpConfig cfg,
        string recipientEmail,
        string code,
        string contextLabel,
        CancellationToken ct)
    {
        try
        {
            var fromEmail = string.IsNullOrWhiteSpace(cfg.FromEmail) ? cfg.SmtpUsername!.Trim() : cfg.FromEmail.Trim();
            var fromName = string.IsNullOrWhiteSpace(cfg.FromName) ? "YouLink" : cfg.FromName.Trim();

            using var message = new MailMessage
            {
                From = new MailAddress(fromEmail, fromName),
                Subject = BuildEmailSubject(code),
                Body = BuildEmailHtml(code, contextLabel),
                IsBodyHtml = true
            };
            message.To.Add(recipientEmail);
            message.Headers.Add("X-Entity-Ref-ID", Guid.NewGuid().ToString("N"));

            using var client = new SmtpClient(cfg.SmtpHost!.Trim(), cfg.SmtpPort)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(cfg.SmtpUsername!.Trim(), cfg.SmtpPassword!.Trim())
            };

            await client.SendMailAsync(message, ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Erro SMTP ao enviar e-mail: {ex.Message}");
        }
    }

    private async Task<(bool Ok, string? ErrorMessage)> ExecuteProviderRequestAsync(
        HttpRequestMessage req,
        string providerName,
        CancellationToken ct)
    {
        try
        {
            using var resp = await _httpClient.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
                return (true, null);

            var body = await resp.Content.ReadAsStringAsync(ct);
            var msg = ExtractErrorMessage(body);
            return (false, msg ?? $"{providerName} retornou erro HTTP {(int)resp.StatusCode}.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "Tempo esgotado ao conectar com o serviço de e-mail. Tente novamente.");
        }
        catch
        {
            return (false, "Falha de rede ao enviar o e-mail de verificação.");
        }
    }

    private static string BuildEmailSubject(string code) =>
        $"{code} é o seu código de segurança YouLink";

    private static string BuildEmailPlainText(string code, string contextLabel) =>
        $"Olá,\n\nSeu código de segurança YouLink ({contextLabel}) é: {code}\n\nEste código é de uso único e expira em 10 minutos.\nSe você não solicitou este código, basta ignorar esta mensagem.";

    private static string BuildEmailHtml(string code, string contextLabel)
    {
        var safeContext = WebUtility.HtmlEncode(contextLabel);
        return $"""
            <div style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Arial, sans-serif; color: #111827; font-size: 15px; line-height: 1.6; max-width: 520px;">
              <p style="margin: 0 0 12px;">Olá,</p>
              <p style="margin: 0 0 16px;">Use o código de segurança abaixo para concluir a <strong>{safeContext}</strong> no YouLink:</p>
              <p style="margin: 0 0 16px; font-size: 28px; font-weight: 700; letter-spacing: 0.2em; font-family: monospace; color: #111827;">{code}</p>
              <p style="margin: 0; font-size: 13px; color: #4b5563;">Este código é de uso único e expira em 10 minutos. Se você não solicitou este código, ignore este e-mail.</p>
            </div>
            """;
    }

    private static string? ExtractErrorMessage(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.TryGetProperty("message", out var m) && !string.IsNullOrWhiteSpace(m.GetString()))
                return m.GetString();
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                return e.GetString();
        }
        catch
        {
        }

        return null;
    }

    private static string BuildChallengeKey(string normalizedEmail, string? purpose)
    {
        var cleanPurpose = string.IsNullOrWhiteSpace(purpose)
            ? "store_verification"
            : purpose.Trim().ToLowerInvariant();
        return $"{cleanPurpose}:{normalizedEmail}";
    }

    private static byte[] ComputeCodeHash(byte[] salt, string code)
    {
        var codeBytes = Encoding.UTF8.GetBytes(code);
        var combined = new byte[salt.Length + codeBytes.Length];
        Buffer.BlockCopy(salt, 0, combined, 0, salt.Length);
        Buffer.BlockCopy(codeBytes, 0, combined, salt.Length, codeBytes.Length);
        return SHA256.HashData(combined);
    }

    private void PruneExpiredChallenges(DateTimeOffset now)
    {
        foreach (var pair in _challenges)
        {
            if (now > pair.Value.ExpiresAt)
                _challenges.TryRemove(pair.Key, out _);
        }
    }

    private static EmailOtpConfig ReadFromEnvironment()
    {
        var portStr = Environment.GetEnvironmentVariable("SMTP_PORT");
        var port = int.TryParse(portStr, out var parsed) && parsed > 0 ? parsed : 587;

        return new EmailOtpConfig(
            ResendApiKey: Environment.GetEnvironmentVariable("RESEND_API_KEY"),
            BrevoApiKey: Environment.GetEnvironmentVariable("BREVO_API_KEY"),
            FromEmail: Environment.GetEnvironmentVariable("EMAIL_FROM") ?? Environment.GetEnvironmentVariable("SMTP_FROM_EMAIL"),
            FromName: Environment.GetEnvironmentVariable("EMAIL_FROM_NAME") ?? "YouLink",
            SmtpHost: Environment.GetEnvironmentVariable("SMTP_HOST"),
            SmtpPort: port,
            SmtpUsername: Environment.GetEnvironmentVariable("SMTP_USERNAME"),
            SmtpPassword: Environment.GetEnvironmentVariable("SMTP_PASSWORD"));
    }
}
