using System.Text.Json;
using ClothingStore.Application.Services;
using ClothingStore.Infrastructure.Persistence;
using ClothingStore.Shared.DTOs;
using Microsoft.JSInterop;

namespace ClothingStore.Web.Services;

/// <summary>
/// Gerencia a sessão autenticada do comprador na vitrine web e o estado da gaveta de Chat Interno com a Loja.
/// Exige login (e verificação de e-mail no cadastro) do comprador para conversar com o lojista e suporta "Apagar para mim".
/// </summary>
public sealed class StoreBuyerChatStateService
{
    private const string StorageKey = "youlink_buyer_session_v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CloudChatStoreService _chatStore;
    private readonly EmailOtpService _emailOtp;
    private bool _loadedFromStorage;

    public event Action? OnChanged;

    public bool IsDrawerOpen { get; private set; }
    public BuyerAuthSessionDto? CurrentBuyer { get; private set; }
    public string? PendingContextBadge { get; private set; }
    public string? PendingDraftText { get; private set; }

    public bool IsAuthenticated => CurrentBuyer is not null && CurrentBuyer.ExpiresAt > DateTimeOffset.UtcNow;

    public StoreBuyerChatStateService(CloudChatStoreService chatStore, EmailOtpService emailOtp)
    {
        _chatStore = chatStore;
        _emailOtp = emailOtp;
    }

    public void OpenChat(string? contextBadge = null, string? draftText = null)
    {
        if (!string.IsNullOrWhiteSpace(contextBadge))
            PendingContextBadge = contextBadge.Trim();
        if (!string.IsNullOrWhiteSpace(draftText))
            PendingDraftText = draftText.Trim();

        IsDrawerOpen = true;
        OnChanged?.Invoke();
    }

    public void CloseChat()
    {
        if (!IsDrawerOpen)
            return;

        IsDrawerOpen = false;
        OnChanged?.Invoke();
    }

    public void ClearPendingDraft()
    {
        PendingContextBadge = null;
        PendingDraftText = null;
    }

    public async Task EnsureSessionLoadedAsync(IJSRuntime js)
    {
        if (_loadedFromStorage)
            return;

        _loadedFromStorage = true;
        try
        {
            var rawJson = await js.InvokeAsync<string>("youlinkStore.getCart", StorageKey);
            if (!string.IsNullOrWhiteSpace(rawJson))
            {
                var saved = JsonSerializer.Deserialize<BuyerAuthSessionDto>(rawJson, JsonOptions);
                if (saved is not null && !string.IsNullOrWhiteSpace(saved.SessionToken))
                {
                    var validated = await _chatStore.ValidateBuyerTokenAsync(saved.SessionToken);
                    if (validated is not null)
                    {
                        CurrentBuyer = validated;
                        OnChanged?.Invoke();
                        return;
                    }
                }
            }
        }
        catch
        {
        }
    }

    public async Task<SendEmailOtpResponse> SendRegisterOtpAsync(BuyerRegisterSendOtpRequest request)
    {
        var check = await _chatStore.ValidateNewBuyerRegistrationAsync(request?.FullName, request?.Email, request?.Password);
        if (!check.Valid)
        {
            return new SendEmailOtpResponse(
                Sent: false,
                ProviderConfigured: _emailOtp.IsConfigured,
                MaskedEmail: string.Empty,
                ExpiresInSeconds: 0,
                ResendAfterSeconds: 0,
                Message: check.ErrorMessage ?? "Verifique os dados informados.");
        }

        return await _emailOtp.SendOtpAsync(new SendEmailOtpRequest(
            Email: check.NormalizedEmail,
            Purpose: "buyer_register",
            ContextLabel: "confirmação de nova conta na vitrine YouLink"));
    }

    public async Task<BuyerAuthResultDto> RegisterAsync(IJSRuntime js, RegisterBuyerAccountRequest request)
    {
        var preCheck = await _chatStore.ValidateNewBuyerRegistrationAsync(request?.FullName, request?.Email, request?.Password);
        if (!preCheck.Valid)
            return new BuyerAuthResultDto(false, preCheck.ErrorMessage ?? "Verifique os dados informados.");

        if (string.IsNullOrWhiteSpace(request?.EmailOtpCode))
        {
            var sent = await SendRegisterOtpAsync(new BuyerRegisterSendOtpRequest(
                request?.FullName ?? string.Empty,
                request?.Email ?? string.Empty,
                request?.Password ?? string.Empty));

            return new BuyerAuthResultDto(
                Success: false,
                Message: sent.Sent
                    ? $"Enviamos um código de 6 dígitos para {sent.MaskedEmail}. Digite o código abaixo para ativar sua conta."
                    : sent.Message,
                RequiresEmailOtp: true);
        }

        var otpVerify = _emailOtp.VerifyOtp(new VerifyEmailOtpRequest(preCheck.NormalizedEmail, request.EmailOtpCode.Trim(), "buyer_register"));
        if (!otpVerify.Verified)
        {
            return new BuyerAuthResultDto(false, otpVerify.Message, RequiresEmailOtp: true);
        }

        var result = await _chatStore.RegisterBuyerAsync(request);
        if (result.Success && result.Session is not null)
        {
            CurrentBuyer = result.Session;
            await PersistSessionAsync(js, result.Session);
            OnChanged?.Invoke();
        }
        return result;
    }

    public async Task<BuyerAuthResultDto> LoginAsync(IJSRuntime js, BuyerLoginRequest request)
    {
        var result = await _chatStore.LoginBuyerAsync(request);
        if (result.Success && result.Session is not null)
        {
            CurrentBuyer = result.Session;
            await PersistSessionAsync(js, result.Session);
            OnChanged?.Invoke();
        }
        return result;
    }

    public async Task LogoutAsync(IJSRuntime js)
    {
        CurrentBuyer = null;
        try
        {
            await js.InvokeVoidAsync("youlinkStore.saveCart", StorageKey, string.Empty);
        }
        catch
        {
        }
        OnChanged?.Invoke();
    }

    public Task<ChatThreadDetailDto?> LoadThreadAsync(string storeSlug, string? storeName)
    {
        if (!IsAuthenticated || CurrentBuyer is null)
            return Task.FromResult<ChatThreadDetailDto?>(null);

        return _chatStore.GetOrCreateBuyerStoreThreadAsync(CurrentBuyer.BuyerId, storeSlug, storeName);
    }

    public Task<ChatThreadDetailDto?> SendMessageAsync(string storeSlug, string? storeName, string text, string? contextBadge)
    {
        if (!IsAuthenticated || CurrentBuyer is null)
            return Task.FromResult<ChatThreadDetailDto?>(null);

        return _chatStore.SendBuyerStoreMessageAsync(CurrentBuyer.BuyerId, storeSlug, storeName, text, contextBadge);
    }

    public Task<bool> DeleteMessageForMeAsync(Guid threadId, Guid messageId)
    {
        if (!IsAuthenticated || CurrentBuyer is null)
            return Task.FromResult(false);

        return _chatStore.DeleteMessageForActorAsync(
            threadId,
            messageId,
            CloudChatStoreService.BuildBuyerActorKey(CurrentBuyer.BuyerId));
    }

    private static async Task PersistSessionAsync(IJSRuntime js, BuyerAuthSessionDto session)
    {
        try
        {
            var json = JsonSerializer.Serialize(session, JsonOptions);
            await js.InvokeVoidAsync("youlinkStore.saveCart", StorageKey, json);
        }
        catch
        {
        }
    }
}
