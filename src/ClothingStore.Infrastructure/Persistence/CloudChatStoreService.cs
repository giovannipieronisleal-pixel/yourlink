using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClothingStore.Application.Interfaces;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;

namespace ClothingStore.Infrastructure.Persistence;

/// <summary>
/// Armazenamento central em nuvem para:
/// 1) Contas de compradores das vitrines (login obrigatório para conversar com o lojista);
/// 2) Chat Comprador ↔ Lojista por loja;
/// 3) Chat de Suporte Lojista ↔ Plataforma;
/// 4) Exclusão individual estilo WhatsApp ("Apagar para mim" — oculta somente para quem apagou).
/// Espelhado automaticamente via <see cref="CloudAppDataMirrorRegistry"/>.
/// </summary>
public sealed class CloudChatStoreService
{
    public const string StoreFileName = "cloud_chat_store.json";
    public const string ChannelStore = "store";
    public const string ChannelSupport = "support";
    public const int MaxMessageLength = 1500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly IPasswordHasher _passwordHasher;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTime _lastLoadedUtc = DateTime.MinValue;
    private CloudChatState _state = new();

    public CloudChatStoreService(string rootDirectory, IPasswordHasher passwordHasher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _filePath = Path.Combine(Path.GetFullPath(rootDirectory), StoreFileName);
    }

    public string StoreFilePath => _filePath;

    public static string BuildBuyerActorKey(Guid buyerId) => $"buyer:{buyerId:N}";
    public static string BuildMerchantStoreActorKey(string storeSlug) => $"merchant:{WhatsAppLinkBuilder.GenerateStoreSlug(storeSlug)}";
    public static string BuildMerchantSupportActorKey(string accountKey) => $"merchant_support:{accountKey.Trim().ToLowerInvariant()}";
    public const string PlatformOwnerActorKey = "platform_owner";

    // =========================================================================
    // 1. AUTENTICAÇÃO DE COMPRADORES DA VITRINE
    // =========================================================================

    public async Task<BuyerAuthResultDto> RegisterBuyerAsync(
        RegisterBuyerAccountRequest request,
        CancellationToken ct = default)
    {
        var fullName = (request?.FullName ?? string.Empty).Trim();
        var email = NormalizeEmail(request?.Email);
        var password = request?.Password ?? string.Empty;

        if (fullName.Length < 2)
            return new BuyerAuthResultDto(false, "Informe seu nome (mínimo 2 caracteres).");

        if (string.IsNullOrWhiteSpace(email))
            return new BuyerAuthResultDto(false, "Informe um e-mail válido.");

        if (password.Trim().Length < 4)
            return new BuyerAuthResultDto(false, "A senha deve ter pelo menos 4 caracteres.");

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();

            if (_state.Buyers.Any(b => string.Equals(b.Email, email, StringComparison.OrdinalIgnoreCase)))
            {
                return new BuyerAuthResultDto(false, "Já existe uma conta com este e-mail. Toque em 'Já tenho conta' para entrar.");
            }

            var rawToken = GenerateOpaqueToken();
            var expiresAt = DateTimeOffset.UtcNow.AddDays(30);
            var buyer = new BuyerEntry
            {
                Id = Guid.NewGuid(),
                FullName = fullName.Length > 80 ? fullName[..80] : fullName,
                Email = email,
                PasswordHash = _passwordHasher.HashPassword(password),
                CreatedAt = DateTimeOffset.UtcNow
            };
            buyer.Sessions.Add(new BuyerSessionEntry
            {
                TokenHash = HashTokenSha256(rawToken),
                ExpiresAt = expiresAt
            });

            _state.Buyers.Add(buyer);
            SaveToDiskUnsafe();

            return new BuyerAuthResultDto(
                Success: true,
                Message: "Conta criada com sucesso!",
                Session: new BuyerAuthSessionDto(
                    BuyerId: buyer.Id,
                    FullName: buyer.FullName,
                    MaskedEmail: MaskEmail(buyer.Email),
                    SessionToken: rawToken,
                    ExpiresAt: expiresAt));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<BuyerAuthResultDto> LoginBuyerAsync(
        BuyerLoginRequest request,
        CancellationToken ct = default)
    {
        var email = NormalizeEmail(request?.Email);
        var password = request?.Password ?? string.Empty;

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return new BuyerAuthResultDto(false, "Informe seu e-mail e sua senha.");

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();

            var buyer = _state.Buyers.FirstOrDefault(b => string.Equals(b.Email, email, StringComparison.OrdinalIgnoreCase));
            if (buyer is null || !_passwordHasher.VerifyPassword(password, buyer.PasswordHash))
            {
                return new BuyerAuthResultDto(false, "E-mail ou senha incorretos.");
            }

            var now = DateTimeOffset.UtcNow;
            buyer.Sessions.RemoveAll(s => s.ExpiresAt <= now);
            if (buyer.Sessions.Count >= 8)
            {
                buyer.Sessions = buyer.Sessions.OrderByDescending(s => s.ExpiresAt).Take(7).ToList();
            }

            var rawToken = GenerateOpaqueToken();
            var expiresAt = now.AddDays(30);
            buyer.Sessions.Add(new BuyerSessionEntry
            {
                TokenHash = HashTokenSha256(rawToken),
                ExpiresAt = expiresAt
            });

            SaveToDiskUnsafe();

            return new BuyerAuthResultDto(
                Success: true,
                Message: "Login realizado!",
                Session: new BuyerAuthSessionDto(
                    BuyerId: buyer.Id,
                    FullName: buyer.FullName,
                    MaskedEmail: MaskEmail(buyer.Email),
                    SessionToken: rawToken,
                    ExpiresAt: expiresAt));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<BuyerAuthSessionDto?> ValidateBuyerTokenAsync(
        string? rawToken,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            return null;

        var tokenHash = HashTokenSha256(rawToken.Trim());
        var now = DateTimeOffset.UtcNow;

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            foreach (var buyer in _state.Buyers)
            {
                var session = buyer.Sessions.FirstOrDefault(s =>
                    s.ExpiresAt > now &&
                    CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(s.TokenHash),
                        Encoding.ASCII.GetBytes(tokenHash)));

                if (session is not null)
                {
                    return new BuyerAuthSessionDto(
                        BuyerId: buyer.Id,
                        FullName: buyer.FullName,
                        MaskedEmail: MaskEmail(buyer.Email),
                        SessionToken: rawToken.Trim(),
                        ExpiresAt: session.ExpiresAt);
                }
            }

            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    // =========================================================================
    // 2. CHAT DA LOJA (COMPRADOR ↔ LOJISTA)
    // =========================================================================

    public async Task<ChatThreadDetailDto?> GetOrCreateBuyerStoreThreadAsync(
        Guid buyerId,
        string storeSlug,
        string? storeName,
        CancellationToken ct = default)
    {
        var normalizedSlug = WhatsAppLinkBuilder.GenerateStoreSlug(storeSlug);
        if (buyerId == Guid.Empty || string.IsNullOrWhiteSpace(normalizedSlug))
            return null;

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            var buyer = _state.Buyers.FirstOrDefault(b => b.Id == buyerId);
            if (buyer is null)
                return null;

            var thread = FindOrCreateStoreThreadUnsafe(buyer, normalizedSlug, storeName);
            if (thread.UnreadForCounterparty > 0)
            {
                thread.UnreadForCounterparty = 0;
                SaveToDiskUnsafe();
            }

            var actorKey = BuildBuyerActorKey(buyerId);
            return MapThreadDetail(thread, actorKey, mySenderRole: "buyer", unreadCount: 0);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ChatThreadDetailDto?> SendBuyerStoreMessageAsync(
        Guid buyerId,
        string storeSlug,
        string? storeName,
        string? text,
        string? contextBadge,
        CancellationToken ct = default)
    {
        var cleanText = SanitizeMessageText(text);
        var normalizedSlug = WhatsAppLinkBuilder.GenerateStoreSlug(storeSlug);
        if (buyerId == Guid.Empty || string.IsNullOrWhiteSpace(normalizedSlug) || string.IsNullOrWhiteSpace(cleanText))
            return null;

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            var buyer = _state.Buyers.FirstOrDefault(b => b.Id == buyerId);
            if (buyer is null)
                return null;

            var thread = FindOrCreateStoreThreadUnsafe(buyer, normalizedSlug, storeName);
            var now = DateTimeOffset.UtcNow;
            thread.Messages.Add(new ChatMessageEntry
            {
                Id = Guid.NewGuid(),
                SenderRole = "buyer",
                SenderDisplayName = buyer.FullName,
                Text = cleanText,
                ContextBadge = SanitizeContextBadge(contextBadge),
                SentAt = now
            });
            thread.UpdatedAt = now;
            thread.UnreadForMerchant++;
            thread.UnreadForCounterparty = 0;
            SaveToDiskUnsafe();

            var actorKey = BuildBuyerActorKey(buyerId);
            return MapThreadDetail(thread, actorKey, mySenderRole: "buyer", unreadCount: 0);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<ChatThreadSummaryDto>> GetMerchantStoreThreadsAsync(
        string storeSlug,
        CancellationToken ct = default)
    {
        var normalizedSlug = WhatsAppLinkBuilder.GenerateStoreSlug(storeSlug);
        if (string.IsNullOrWhiteSpace(normalizedSlug))
            return [];

        var actorKey = BuildMerchantStoreActorKey(normalizedSlug);

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            return _state.Threads
                .Where(t => string.Equals(t.ChannelKind, ChannelStore, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(t.StoreSlug, normalizedSlug, StringComparison.OrdinalIgnoreCase) &&
                            t.Messages.Any(m => !m.DeletedForActors.Contains(actorKey)))
                .OrderByDescending(t => t.UpdatedAt)
                .Select(t => MapThreadSummary(t, actorKey, t.UnreadForMerchant))
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ChatThreadDetailDto?> GetMerchantStoreThreadDetailAsync(
        string storeSlug,
        Guid threadId,
        CancellationToken ct = default)
    {
        var normalizedSlug = WhatsAppLinkBuilder.GenerateStoreSlug(storeSlug);
        if (string.IsNullOrWhiteSpace(normalizedSlug) || threadId == Guid.Empty)
            return null;

        var actorKey = BuildMerchantStoreActorKey(normalizedSlug);

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            var thread = _state.Threads.FirstOrDefault(t =>
                t.Id == threadId &&
                string.Equals(t.ChannelKind, ChannelStore, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.StoreSlug, normalizedSlug, StringComparison.OrdinalIgnoreCase));

            if (thread is null)
                return null;

            if (thread.UnreadForMerchant > 0)
            {
                thread.UnreadForMerchant = 0;
                SaveToDiskUnsafe();
            }

            return MapThreadDetail(thread, actorKey, mySenderRole: "merchant", unreadCount: 0);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ChatThreadDetailDto?> SendMerchantStoreReplyAsync(
        string storeSlug,
        string? storeName,
        Guid threadId,
        string? text,
        string? contextBadge,
        CancellationToken ct = default)
    {
        var cleanText = SanitizeMessageText(text);
        var normalizedSlug = WhatsAppLinkBuilder.GenerateStoreSlug(storeSlug);
        if (string.IsNullOrWhiteSpace(normalizedSlug) || threadId == Guid.Empty || string.IsNullOrWhiteSpace(cleanText))
            return null;

        var actorKey = BuildMerchantStoreActorKey(normalizedSlug);

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            var thread = _state.Threads.FirstOrDefault(t =>
                t.Id == threadId &&
                string.Equals(t.ChannelKind, ChannelStore, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.StoreSlug, normalizedSlug, StringComparison.OrdinalIgnoreCase));

            if (thread is null)
                return null;

            if (!string.IsNullOrWhiteSpace(storeName))
                thread.StoreName = storeName.Trim();

            var now = DateTimeOffset.UtcNow;
            thread.Messages.Add(new ChatMessageEntry
            {
                Id = Guid.NewGuid(),
                SenderRole = "merchant",
                SenderDisplayName = string.IsNullOrWhiteSpace(thread.StoreName) ? "Loja" : thread.StoreName,
                Text = cleanText,
                ContextBadge = SanitizeContextBadge(contextBadge),
                SentAt = now
            });
            thread.UpdatedAt = now;
            thread.UnreadForCounterparty++;
            thread.UnreadForMerchant = 0;
            SaveToDiskUnsafe();

            return MapThreadDetail(thread, actorKey, mySenderRole: "merchant", unreadCount: 0);
        }
        finally
        {
            _lock.Release();
        }
    }

    // =========================================================================
    // 3. CHAT DE SUPORTE (LOJISTA ↔ DONO DA PLATAFORMA)
    // =========================================================================

    public async Task<ChatThreadDetailDto> GetOrCreateMerchantSupportThreadAsync(
        string accountKey,
        string merchantHandle,
        string storeSlug,
        string storeName,
        CancellationToken ct = default)
    {
        var cleanKey = accountKey.Trim().ToLowerInvariant();
        var actorKey = BuildMerchantSupportActorKey(cleanKey);

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            var thread = FindOrCreateSupportThreadUnsafe(cleanKey, merchantHandle, storeSlug, storeName);
            if (thread.UnreadForMerchant > 0)
            {
                thread.UnreadForMerchant = 0;
                SaveToDiskUnsafe();
            }

            return MapThreadDetail(thread, actorKey, mySenderRole: "merchant", unreadCount: 0);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ChatThreadDetailDto?> SendMerchantSupportMessageAsync(
        string accountKey,
        string merchantHandle,
        string storeSlug,
        string storeName,
        string? text,
        string? contextBadge,
        CancellationToken ct = default)
    {
        var cleanText = SanitizeMessageText(text);
        if (string.IsNullOrWhiteSpace(accountKey) || string.IsNullOrWhiteSpace(cleanText))
            return null;

        var cleanKey = accountKey.Trim().ToLowerInvariant();
        var actorKey = BuildMerchantSupportActorKey(cleanKey);

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            var thread = FindOrCreateSupportThreadUnsafe(cleanKey, merchantHandle, storeSlug, storeName);
            var now = DateTimeOffset.UtcNow;
            var senderLabel = !string.IsNullOrWhiteSpace(storeName) ? storeName.Trim() : merchantHandle;

            thread.Messages.Add(new ChatMessageEntry
            {
                Id = Guid.NewGuid(),
                SenderRole = "merchant",
                SenderDisplayName = senderLabel,
                Text = cleanText,
                ContextBadge = SanitizeContextBadge(contextBadge),
                SentAt = now
            });
            thread.UpdatedAt = now;
            thread.UnreadForCounterparty++; // unread for platform owner
            thread.UnreadForMerchant = 0;
            SaveToDiskUnsafe();

            return MapThreadDetail(thread, actorKey, mySenderRole: "merchant", unreadCount: 0);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<ChatThreadSummaryDto>> GetPlatformSupportThreadsAsync(
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            return _state.Threads
                .Where(t => string.Equals(t.ChannelKind, ChannelSupport, StringComparison.OrdinalIgnoreCase) &&
                            t.Messages.Any(m => !m.DeletedForActors.Contains(PlatformOwnerActorKey)))
                .OrderByDescending(t => t.UpdatedAt)
                .Select(t => MapThreadSummary(t, PlatformOwnerActorKey, t.UnreadForCounterparty))
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ChatThreadDetailDto?> GetPlatformSupportThreadDetailAsync(
        Guid threadId,
        CancellationToken ct = default)
    {
        if (threadId == Guid.Empty)
            return null;

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            var thread = _state.Threads.FirstOrDefault(t =>
                t.Id == threadId &&
                string.Equals(t.ChannelKind, ChannelSupport, StringComparison.OrdinalIgnoreCase));

            if (thread is null)
                return null;

            if (thread.UnreadForCounterparty > 0)
            {
                thread.UnreadForCounterparty = 0;
                SaveToDiskUnsafe();
            }

            return MapThreadDetail(thread, PlatformOwnerActorKey, mySenderRole: "platform", unreadCount: 0);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ChatThreadDetailDto?> SendPlatformSupportReplyAsync(
        Guid threadId,
        string? text,
        string? senderLabel,
        CancellationToken ct = default)
    {
        var cleanText = SanitizeMessageText(text);
        if (threadId == Guid.Empty || string.IsNullOrWhiteSpace(cleanText))
            return null;

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            var thread = _state.Threads.FirstOrDefault(t =>
                t.Id == threadId &&
                string.Equals(t.ChannelKind, ChannelSupport, StringComparison.OrdinalIgnoreCase));

            if (thread is null)
                return null;

            var now = DateTimeOffset.UtcNow;
            thread.Messages.Add(new ChatMessageEntry
            {
                Id = Guid.NewGuid(),
                SenderRole = "platform",
                SenderDisplayName = string.IsNullOrWhiteSpace(senderLabel) ? "Suporte YouLink" : senderLabel.Trim(),
                Text = cleanText,
                SentAt = now
            });
            thread.UpdatedAt = now;
            thread.UnreadForMerchant++;
            thread.UnreadForCounterparty = 0;
            SaveToDiskUnsafe();

            return MapThreadDetail(thread, PlatformOwnerActorKey, mySenderRole: "platform", unreadCount: 0);
        }
        finally
        {
            _lock.Release();
        }
    }

    // =========================================================================
    // 4. APAGAR PARA MIM (EXCLUSÃO INDIVIDUAL ESTILO WHATSAPP)
    // =========================================================================

    public async Task<bool> DeleteMessageForActorAsync(
        Guid threadId,
        Guid messageId,
        string actorKey,
        CancellationToken ct = default)
    {
        if (threadId == Guid.Empty || messageId == Guid.Empty || string.IsNullOrWhiteSpace(actorKey))
            return false;

        var normalizedActor = actorKey.Trim().ToLowerInvariant();

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            var thread = _state.Threads.FirstOrDefault(t => t.Id == threadId);
            var msg = thread?.Messages.FirstOrDefault(m => m.Id == messageId);
            if (msg is null)
                return false;

            if (!msg.DeletedForActors.Contains(normalizedActor, StringComparer.OrdinalIgnoreCase))
            {
                msg.DeletedForActors.Add(normalizedActor);
                SaveToDiskUnsafe();
            }

            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    // =========================================================================
    // HELPERS PRIVADOS
    // =========================================================================

    private ChatThreadEntry FindOrCreateStoreThreadUnsafe(BuyerEntry buyer, string normalizedSlug, string? storeName)
    {
        var buyerKey = buyer.Id.ToString("N");
        var thread = _state.Threads.FirstOrDefault(t =>
            string.Equals(t.ChannelKind, ChannelStore, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.StoreSlug, normalizedSlug, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.ParticipantKey, buyerKey, StringComparison.OrdinalIgnoreCase));

        if (thread is null)
        {
            var code = "CLI-" + buyer.Id.ToString("N")[..6].ToUpperInvariant();
            thread = new ChatThreadEntry
            {
                Id = Guid.NewGuid(),
                ChannelKind = ChannelStore,
                StoreSlug = normalizedSlug,
                StoreName = string.IsNullOrWhiteSpace(storeName) ? normalizedSlug : storeName.Trim(),
                ParticipantKey = buyerKey,
                CounterpartyCode = code,
                CounterpartyName = buyer.FullName,
                CounterpartyHandle = MaskEmail(buyer.Email),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _state.Threads.Add(thread);
            SaveToDiskUnsafe();
        }
        else if (!string.IsNullOrWhiteSpace(storeName))
        {
            thread.StoreName = storeName.Trim();
        }

        return thread;
    }

    private ChatThreadEntry FindOrCreateSupportThreadUnsafe(
        string accountKey,
        string merchantHandle,
        string storeSlug,
        string storeName)
    {
        var normalizedSlug = string.IsNullOrWhiteSpace(storeSlug)
            ? "minha-loja"
            : WhatsAppLinkBuilder.GenerateStoreSlug(storeSlug);

        var thread = _state.Threads.FirstOrDefault(t =>
            string.Equals(t.ChannelKind, ChannelSupport, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.ParticipantKey, accountKey, StringComparison.OrdinalIgnoreCase));

        var hashHex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountKey)))[..6];
        var safeHandle = string.IsNullOrWhiteSpace(merchantHandle)
            ? "@lojista"
            : "@" + merchantHandle.Trim().TrimStart('@');

        if (thread is null)
        {
            thread = new ChatThreadEntry
            {
                Id = Guid.NewGuid(),
                ChannelKind = ChannelSupport,
                StoreSlug = normalizedSlug,
                StoreName = string.IsNullOrWhiteSpace(storeName) ? normalizedSlug : storeName.Trim(),
                ParticipantKey = accountKey,
                CounterpartyCode = $"YL-{hashHex}",
                CounterpartyName = string.IsNullOrWhiteSpace(storeName) ? safeHandle : storeName.Trim(),
                CounterpartyHandle = safeHandle,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _state.Threads.Add(thread);
            SaveToDiskUnsafe();
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(storeName))
            {
                thread.StoreName = storeName.Trim();
                thread.CounterpartyName = storeName.Trim();
            }
            if (!string.IsNullOrWhiteSpace(merchantHandle))
            {
                thread.CounterpartyHandle = safeHandle;
            }
            thread.StoreSlug = normalizedSlug;
        }

        return thread;
    }

    private static ChatThreadSummaryDto MapThreadSummary(ChatThreadEntry t, string viewerActorKey, int unreadCount)
    {
        var normalizedActor = viewerActorKey.Trim().ToLowerInvariant();
        var lastVisible = t.Messages
            .Where(m => !m.DeletedForActors.Contains(normalizedActor, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(m => m.SentAt)
            .FirstOrDefault();

        var preview = lastVisible?.Text;
        if (!string.IsNullOrWhiteSpace(preview) && preview.Length > 72)
            preview = preview[..72] + "…";

        return new ChatThreadSummaryDto(
            ThreadId: t.Id,
            ChannelKind: t.ChannelKind,
            StoreSlug: t.StoreSlug,
            StoreName: t.StoreName,
            CounterpartyCode: t.CounterpartyCode,
            CounterpartyName: t.CounterpartyName,
            CounterpartyHandle: t.CounterpartyHandle,
            LastMessagePreview: preview,
            UpdatedAt: lastVisible?.SentAt ?? t.UpdatedAt,
            UnreadCount: unreadCount);
    }

    private static ChatThreadDetailDto MapThreadDetail(
        ChatThreadEntry t,
        string viewerActorKey,
        string mySenderRole,
        int unreadCount)
    {
        var normalizedActor = viewerActorKey.Trim().ToLowerInvariant();
        var visibleMessages = t.Messages
            .Where(m => !m.DeletedForActors.Contains(normalizedActor, StringComparer.OrdinalIgnoreCase))
            .OrderBy(m => m.SentAt)
            .Select(m => new ChatMessageDto(
                Id: m.Id,
                ThreadId: t.Id,
                SenderRole: m.SenderRole,
                SenderDisplayName: m.SenderDisplayName,
                Text: m.Text,
                SentAt: m.SentAt,
                ContextBadge: m.ContextBadge,
                IsMine: string.Equals(m.SenderRole, mySenderRole, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return new ChatThreadDetailDto(
            Summary: MapThreadSummary(t, viewerActorKey, unreadCount),
            Messages: visibleMessages);
    }

    private static string SanitizeMessageText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var trimmed = raw.Trim();
        return trimmed.Length > MaxMessageLength ? trimmed[..MaxMessageLength] : trimmed;
    }

    private static string? SanitizeContextBadge(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();
        return trimmed.Length > 80 ? trimmed[..80] : trimmed;
    }

    private static string NormalizeEmail(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var trimmed = raw.Trim().ToLowerInvariant();
        var at = trimmed.IndexOf('@');
        if (at <= 0 || at != trimmed.LastIndexOf('@') || at >= trimmed.Length - 3)
            return string.Empty;

        var domain = trimmed[(at + 1)..];
        if (!domain.Contains('.', StringComparison.Ordinal) || domain.StartsWith('.') || domain.EndsWith('.'))
            return string.Empty;

        return trimmed.Any(char.IsWhiteSpace) ? string.Empty : trimmed;
    }

    private static string MaskEmail(string email)
    {
        var parts = email.Split('@');
        if (parts.Length != 2)
            return "***";

        var local = parts[0];
        var visible = local.Length <= 2 ? local[..1] : local[..2];
        var domParts = parts[1].Split('.');
        var domHost = domParts[0];
        var domVisible = domHost.Length <= 2 ? domHost[..1] : domHost[..2];
        var tld = domParts.Length > 1 ? "." + string.Join('.', domParts[1..]) : string.Empty;
        return $"{visible}***@{domVisible}***{tld}";
    }

    private static string GenerateOpaqueToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string HashTokenSha256(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    private void RefreshFromDiskIfModifiedUnsafe()
    {
        try
        {
            if (!File.Exists(_filePath))
                return;

            var lastWrite = File.GetLastWriteTimeUtc(_filePath);
            if (lastWrite <= _lastLoadedUtc)
                return;

            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
                return;

            var parsed = JsonSerializer.Deserialize<CloudChatState>(json, JsonOptions);
            if (parsed is not null)
            {
                parsed.Buyers ??= [];
                parsed.Threads ??= [];
                _state = parsed;
                _lastLoadedUtc = lastWrite;
            }
        }
        catch
        {
        }
    }

    private void SaveToDiskUnsafe()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_state, JsonOptions);
        var tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, json, Encoding.UTF8);
            File.Move(tempPath, _filePath, overwrite: true);
            CloudAppDataMirrorRegistry.TryNotifySaved(_filePath, json);
            _lastLoadedUtc = File.GetLastWriteTimeUtc(_filePath);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }
        }
    }

    private sealed class CloudChatState
    {
        public List<BuyerEntry> Buyers { get; set; } = [];
        public List<ChatThreadEntry> Threads { get; set; } = [];
    }

    private sealed class BuyerEntry
    {
        public Guid Id { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public List<BuyerSessionEntry> Sessions { get; set; } = [];
    }

    private sealed class BuyerSessionEntry
    {
        public string TokenHash { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; set; }
    }

    private sealed class ChatThreadEntry
    {
        public Guid Id { get; set; }
        public string ChannelKind { get; set; } = ChannelStore;
        public string StoreSlug { get; set; } = string.Empty;
        public string StoreName { get; set; } = string.Empty;
        public string ParticipantKey { get; set; } = string.Empty;
        public string CounterpartyCode { get; set; } = string.Empty;
        public string CounterpartyName { get; set; } = string.Empty;
        public string CounterpartyHandle { get; set; } = string.Empty;
        public int UnreadForMerchant { get; set; }
        public int UnreadForCounterparty { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public List<ChatMessageEntry> Messages { get; set; } = [];
    }

    private sealed class ChatMessageEntry
    {
        public Guid Id { get; set; }
        public string SenderRole { get; set; } = string.Empty;
        public string SenderDisplayName { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string? ContextBadge { get; set; }
        public DateTimeOffset SentAt { get; set; }
        public List<string> DeletedForActors { get; set; } = [];
    }
}
