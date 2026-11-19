namespace ClothingStore.Shared.DTOs;

/// <summary>
/// Configuração pública de segurança anti-bot (Cloudflare Turnstile).
/// Expõe somente a SiteKey pública — nunca o segredo do servidor.
/// </summary>
public sealed record PublicSecurityConfigDto(
    bool TurnstileEnabled,
    string? TurnstileSiteKey = null);

/// <summary>
/// Requisição para o Estúdio IA Gemini gerar título, descrição, WhatsApp, Instagram e tags de um produto.
/// </summary>
public sealed record GenerateProductCopyRequest(
    string ProductName,
    string? CategoryName = null,
    decimal Price = 0m,
    string? CurrentDescription = null,
    string? StoreName = null,
    string Tone = "Luxo",
    IReadOnlyList<string>? VariantHints = null,
    string? CustomApiKey = null);

/// <summary>
/// Kit de marketing gerado pelo Estúdio IA Gemini (ou fallback assistivo offline).
/// </summary>
public sealed record GeneratedProductCopyDto(
    string SuggestedTitle,
    string PersuasiveDescription,
    string WhatsappPromoMessage,
    string InstagramCaption,
    IReadOnlyList<string> SeoTags,
    string BannerHeadline,
    string ProviderUsed,
    bool UsedLiveGemini);

/// <summary>
/// Configuração remota dinâmica (compatível com Firebase Remote Config e servidor central YouLink),
/// permitindo ativar avisos globais e feature flags sem recompilar os aplicativos.
/// </summary>
public sealed record RemoteFeatureConfigDto(
    bool AiStudioEnabled = true,
    bool TurnstileCheckoutEnabled = true,
    bool CrashTelemetryEnabled = true,
    string? GlobalAnnouncementBanner = null,
    string GlobalAnnouncementKind = "info",
    string? VipPromoMessage = null,
    string? MinRecommendedAppVersion = null,
    string? EdgeProxyBaseUrl = null,
    DateTimeOffset? UpdatedAt = null);

/// <summary>
/// Relatório sanitizado de falha / exceção não tratada enviado pelo aplicativo (estilo Crashlytics).
/// </summary>
public sealed record ClientCrashReportRequest(
    string AppName,
    string AppVersion,
    string Platform,
    string Context,
    string ErrorType,
    string ErrorMessage,
    string? StackTrace = null,
    string? AccountHint = null);

public sealed record ClientCrashReportEntryDto(
    Guid Id,
    DateTimeOffset ReportedAt,
    string AppName,
    string AppVersion,
    string Platform,
    string Context,
    string ErrorType,
    string ErrorMessage,
    string? StackTrace,
    string? AccountHint);

/// <summary>
/// Requisição administrativa (YouLink Admin -> Servidor Central) para configurar ou remover
/// a chave central do Google Gemini usada automaticamente por todas as lojas.
/// </summary>
public sealed record SaveServerAiKeyRequest(string? GeminiApiKey);

/// <summary>
/// Status sanitizado da chave central de IA no servidor (nunca retorna a chave em texto puro).
/// </summary>
public sealed record ServerAiKeyStatusDto(
    bool Configured,
    string Source,
    DateTimeOffset? UpdatedAt = null);
