namespace ClothingStore.Shared.DTOs;

public sealed record RegisterBuyerAccountRequest(
    string FullName,
    string Email,
    string Password);

public sealed record BuyerLoginRequest(
    string Email,
    string Password);

public sealed record BuyerAuthSessionDto(
    Guid BuyerId,
    string FullName,
    string MaskedEmail,
    string SessionToken,
    DateTimeOffset ExpiresAt);

public sealed record BuyerAuthResultDto(
    bool Success,
    string Message,
    BuyerAuthSessionDto? Session = null);

public sealed record ChatMessageDto(
    Guid Id,
    Guid ThreadId,
    string SenderRole,
    string SenderDisplayName,
    string Text,
    DateTimeOffset SentAt,
    string? ContextBadge = null,
    bool IsMine = false);

public sealed record ChatThreadSummaryDto(
    Guid ThreadId,
    string ChannelKind,
    string StoreSlug,
    string StoreName,
    string CounterpartyCode,
    string CounterpartyName,
    string CounterpartyHandle,
    string? LastMessagePreview,
    DateTimeOffset UpdatedAt,
    int UnreadCount);

public sealed record ChatThreadDetailDto(
    ChatThreadSummaryDto Summary,
    IReadOnlyList<ChatMessageDto> Messages);

public sealed record SendBuyerChatMessageRequest(
    string Text,
    string? StoreName = null,
    string? ContextBadge = null);

public sealed record MerchantChatAuthRequest(
    string? Email,
    string? Username,
    string? PasswordHash,
    string StoreSlug,
    string? StoreName = null,
    string? PublishToken = null);

public sealed record MerchantSendChatMessageRequest(
    MerchantChatAuthRequest Auth,
    string Text,
    string? ContextBadge = null);

public sealed record PlatformSupportSendMessageRequest(
    string Text,
    string? SenderLabel = null);
