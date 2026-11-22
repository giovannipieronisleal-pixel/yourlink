namespace ClothingStore.Shared.DTOs;

public sealed record SendEmailOtpRequest(
    string Email,
    string? Purpose = "store_verification",
    string? ContextLabel = null);

public sealed record SendEmailOtpResponse(
    bool Sent,
    bool ProviderConfigured,
    string MaskedEmail,
    int ExpiresInSeconds,
    int ResendAfterSeconds,
    string Message);

public sealed record VerifyEmailOtpRequest(
    string Email,
    string Code,
    string? Purpose = "store_verification");

public sealed record VerifyEmailOtpResponse(
    bool Verified,
    string NormalizedEmail,
    string Message);

public sealed record EmailOtpStatusDto(
    bool IsConfigured,
    string Provider,
    string Message);
