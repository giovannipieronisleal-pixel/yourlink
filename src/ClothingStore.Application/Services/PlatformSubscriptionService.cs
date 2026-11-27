using System.Security.Cryptography;
using System.Text;
using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Exceptions;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;

namespace ClothingStore.Application.Services;

/// <summary>
/// Orquestra os pedidos de assinatura/upgrade do aplicativo (Básico, Meia Versão e Premium):
/// suporta cobrança automática via Mercado Pago (Pix Automático, Checkout Pro / Cartão e Assinatura Recorrente)
/// além do modo fallback via chave Pix direta do dono com aprovação no Admin.
/// </summary>
public sealed class PlatformSubscriptionService
{
    private readonly IClothingStoreRepository _repository;
    private readonly IMercadoPagoPixService? _mercadoPago;
    private readonly IPlatformMercadoPagoTokenStore? _platformTokenStore;
    private readonly IMultiTenantStoreResolver? _storeResolver;

    public PlatformSubscriptionService(
        IClothingStoreRepository repository,
        IMercadoPagoPixService? mercadoPago = null,
        IPlatformMercadoPagoTokenStore? platformTokenStore = null,
        IMultiTenantStoreResolver? storeResolver = null)
    {
        _repository = repository;
        _mercadoPago = mercadoPago;
        _platformTokenStore = platformTokenStore;
        _storeResolver = storeResolver;
    }

    public Task<PlatformSubscriptionOrderDto> CreatePlanOrderAsync(
        CreatePlatformPlanOrderRequest request,
        CancellationToken ct = default) =>
        CreatePlanOrderAsync(request, publicBaseOrigin: null, ct);

    public async Task<PlatformSubscriptionOrderDto> CreatePlanOrderAsync(
        CreatePlatformPlanOrderRequest request,
        string? publicBaseOrigin,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = await _repository.GetStoreSettingsAsync(ct);
        var paymentMethod = PlatformSubscriptionOrder.NormalizePaymentMethod(request.PaymentMethod);
        var pixKey = (settings.PicpayPixKey ?? string.Empty).Trim();
        if (paymentMethod == PlatformSubscriptionOrder.MethodDirectPix && string.IsNullOrWhiteSpace(pixKey))
        {
            throw new DomainException("A chave Pix oficial de recebimento da plataforma ainda não foi configurada no Admin.");
        }

        var normalizedTier = StoreSettings.NormalizePlanTier(request.PlanTier);
        var durationMonths = request.DurationMonths switch
        {
            3 => 3,
            12 => 12,
            _ => 1
        };

        var expectedMonthlyBase = normalizedTier switch
        {
            StoreSettings.PlanTierPremium => settings.PremiumPlanMonthlyPrice,
            StoreSettings.PlanTierIntermediate => settings.IntermediaryPlanMonthlyPrice,
            _ => settings.BasicPlanMonthlyPrice
        };

        // Segurança crítica (Item 5): o valor do plano é calculado EXCLUSIVAMENTE no servidor.
        // Ignora completamente request.Amount enviado pelo cliente e só concede desconto Google
        // quando a conta/loja tiver verificação OAuth real gravada no servidor.
        string? verifiedServerGoogleTier = null;
        if (_storeResolver is not null && !string.IsNullOrWhiteSpace(request.StoreSlug))
        {
            var tenantRepo = await _storeResolver.ResolveReadRepositoryAsync(request.StoreSlug, ct);
            if (tenantRepo is not null && !ReferenceEquals(tenantRepo, _repository))
            {
                var tenantSettings = await tenantRepo.GetStoreSettingsAsync(ct);
                if (tenantSettings.GoogleAccountVerifiedAt.HasValue)
                    verifiedServerGoogleTier = tenantSettings.GoogleAccountTier;
            }
        }
        else if (settings.GoogleAccountVerifiedAt.HasValue)
        {
            verifiedServerGoogleTier = settings.GoogleAccountTier;
        }

        var serverBenefits = GoogleAccountTierVerifier.CalculatePlanBenefits(
            expectedMonthlyBase,
            false,
            verifiedServerGoogleTier,
            false);

        var effectiveAmount = Math.Round(serverBenefits.FinalMonthlyPrice * durationMonths, 2, MidpointRounding.AwayFromZero);

        if (effectiveAmount <= 0m)
            throw new DomainException("O valor da contratação do plano deve ser maior que zero.");

        var storeName = string.IsNullOrWhiteSpace(request.StoreName)
            ? settings.StoreName
            : request.StoreName.Trim();
        var storeSlug = string.IsNullOrWhiteSpace(request.StoreSlug)
            ? Category.GenerateSlug(storeName)
            : request.StoreSlug.Trim();
        var candidateCentral = !string.IsNullOrWhiteSpace(settings.CentralCatalogBaseUrl) &&
                               !StoreSettings.IsPlaceholderStoreUrl(settings.CentralCatalogBaseUrl)
            ? WhatsAppLinkBuilder.NormalizePublicServerUrl(settings.CentralCatalogBaseUrl)
            : null;
        var centralBase = !string.IsNullOrWhiteSpace(candidateCentral)
            ? candidateCentral
            : AppUpdateCatalog.DefaultCentralPlatformServerUrl;
        var safeOnlineUrl = !string.IsNullOrWhiteSpace(settings.OnlineBaseUrl) && !StoreSettings.IsPlaceholderStoreUrl(settings.OnlineBaseUrl)
            ? settings.OnlineBaseUrl
            : $"{centralBase.TrimEnd('/')}/loja/{storeSlug}";
        var desiredDomain = string.IsNullOrWhiteSpace(request.DesiredDomain)
            ? safeOnlineUrl
            : request.DesiredDomain.Trim();

        var orderNumber = $"YLK-{DateTimeOffset.UtcNow:yyyyMMdd}-{RandomNumberGenerator.GetInt32(1000, 9999)}";
        var brCode = paymentMethod == PlatformSubscriptionOrder.MethodDirectPix && PixPayloadBuilder.IsValidPixKey(pixKey)
            ? PixPayloadBuilder.BuildBrCode(
                pixKey,
                "YouLink",
                settings.OriginCity ?? "Brasil",
                effectiveAmount,
                orderNumber)
            : string.Empty;

        var order = PlatformSubscriptionOrder.CreatePending(
            orderNumber,
            normalizedTier,
            durationMonths,
            effectiveAmount,
            storeName,
            storeSlug,
            desiredDomain,
            request.UseOwnDedicatedServer,
            request.BuyerUserId,
            request.BuyerUsername,
            request.BuyerEmail,
            request.BuyerFullName,
            paymentMethod == PlatformSubscriptionOrder.MethodDirectPix ? pixKey : ResolveMethodDisplayLabel(paymentMethod),
            brCode,
            notes: null,
            paymentMethod: paymentMethod);

        var uiMessage = "Pedido registrado! Chave Pix liberada abaixo. Após enviar o Pix, sua versão paga será ativada automaticamente neste aparelho.";
        if (paymentMethod != PlatformSubscriptionOrder.MethodDirectPix)
        {
            uiMessage = await InitializeMercadoPagoOrderAsync(order, settings, publicBaseOrigin, ct);
        }

        await _repository.SavePlatformSubscriptionOrderAsync(order, ct);
        return MapToDto(order, uiMessage);
    }

    public async Task<PlatformPlanOrderStatusDto> GetPlanOrderStatusAsync(
        string orderNumber,
        string claimToken,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(orderNumber) || string.IsNullOrWhiteSpace(claimToken))
            throw new DomainException("Número do pedido e token de acompanhamento são obrigatórios.");

        var order = await _repository.GetPlatformSubscriptionOrderByNumberAsync(orderNumber, ct)
            ?? throw new DomainException($"Pedido '{orderNumber.Trim()}' não encontrado.");

        var expectedBytes = Encoding.UTF8.GetBytes(order.ClaimToken);
        var providedBytes = Encoding.UTF8.GetBytes(claimToken.Trim());
        if (expectedBytes.Length != providedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
        {
            throw new DomainException("Token de acompanhamento inválido para este pedido.");
        }

        if (string.Equals(order.Status, PlatformSubscriptionOrder.StatusPending, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(order.PaymentMethod, PlatformSubscriptionOrder.MethodDirectPix, StringComparison.OrdinalIgnoreCase))
        {
            order = await TryRefreshPendingMercadoPagoOrderAsync(order, ct);
        }

        var isApproved = string.Equals(order.Status, PlatformSubscriptionOrder.StatusPaid, StringComparison.OrdinalIgnoreCase);
        var message = order.Status switch
        {
            PlatformSubscriptionOrder.StatusPaid => $"Pagamento confirmado! Plano {order.PlanTier} liberado automaticamente.",
            PlatformSubscriptionOrder.StatusCancelled => "Este pedido foi cancelado pelo administrador.",
            _ => "Aguardando confirmação automática do Mercado Pago para liberar seu plano."
        };

        return new PlatformPlanOrderStatusDto(
            order.OrderNumber,
            order.Status,
            isApproved,
            order.PlanTier,
            order.DurationDays,
            order.AppliedPlanExpiresAt,
            order.IssuedGiftCardCode,
            order.ApprovedAt,
            message,
            order.DesiredDomain,
            order.PaymentMethod,
            order.MercadoPagoStatus);
    }

    public async Task<IReadOnlyList<PlatformSubscriptionOrderDto>> GetAllOrdersAsync(CancellationToken ct = default)
    {
        var orders = await _repository.GetPlatformSubscriptionOrdersAsync(ct);
        return orders.Select(o => MapToDto(o, ResolveDefaultMessage(o))).ToList();
    }

    public async Task<PlatformSubscriptionOrderDto> ApprovePlanOrderAsync(
        Guid orderId,
        string? adminName = null,
        Guid? adminEmployeeId = null,
        CancellationToken ct = default)
    {
        var order = await _repository.GetPlatformSubscriptionOrderByIdAsync(orderId, ct)
            ?? throw new DomainException("Pedido de plano não encontrado.");

        if (string.Equals(order.Status, PlatformSubscriptionOrder.StatusPaid, StringComparison.OrdinalIgnoreCase))
        {
            return MapToDto(order, "Pagamento já confirmado anteriormente.");
        }

        if (string.Equals(order.Status, PlatformSubscriptionOrder.StatusCancelled, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("Este pedido foi cancelado e não pode ser aprovado.");
        }

        await ApproveOrderInternalAsync(
            order,
            adminName,
            adminEmployeeId,
            DateTimeOffset.UtcNow,
            ct);

        return MapToDto(order, $"Pagamento confirmado! Plano {order.PlanTier} liberado automaticamente para '{order.StoreName}'.");
    }

    public sealed record WebhookLifecycleOutcome(
        bool Handled,
        bool OrderApproved,
        bool IsBillingGraceOrFailure,
        PlatformSubscriptionOrderDto? Order,
        string Message);

    public async Task<(bool Handled, bool OrderApproved, string Message)> HandleMercadoPagoWebhookAsync(
        string? topic,
        string? dataId,
        CancellationToken ct = default)
    {
        var detailed = await HandleMercadoPagoWebhookDetailedAsync(topic, dataId, ct);
        return (detailed.Handled, detailed.OrderApproved, detailed.Message);
    }

    public async Task<WebhookLifecycleOutcome> HandleMercadoPagoWebhookDetailedAsync(
        string? topic,
        string? dataId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dataId) || _mercadoPago is null || _platformTokenStore is null)
            return new(false, false, false, null, "Notificação ignorada.");

        var accessToken = await _platformTokenStore.GetValidAccessTokenAsync(_mercadoPago, ct);
        if (string.IsNullOrWhiteSpace(accessToken))
            return new(false, false, false, null, "Conta Mercado Pago da plataforma não configurada.");

        var normalizedTopic = (topic ?? string.Empty).Trim().ToLowerInvariant();
        var cleanId = dataId.Trim();

        if (normalizedTopic.Contains("subscription_authorized_payment", StringComparison.Ordinal) ||
            normalizedTopic.Contains("authorized_payments", StringComparison.Ordinal))
        {
            var authPay = await _mercadoPago.GetAuthorizedPaymentAsync(accessToken, cleanId, ct);
            if (authPay is null)
                return new(false, false, false, null, "Cobrança recorrente não encontrada no Mercado Pago.");

            var order = await FindOrderForWebhookAsync(authPay.ExternalReference, null, authPay.PreapprovalId, ct);
            if (order is null && !string.IsNullOrWhiteSpace(authPay.PreapprovalId))
            {
                var pre = await _mercadoPago.GetPreapprovalStatusAsync(accessToken, authPay.PreapprovalId, ct);
                order = await FindOrderForWebhookAsync(pre?.ExternalReference, null, authPay.PreapprovalId, ct);
            }

            if (order is null)
                return new(false, false, false, null, "Pedido de assinatura não localizado.");

            order.MercadoPagoStatus = authPay.Status;
            if (!authPay.IsApproved)
            {
                await _repository.SavePlatformSubscriptionOrderAsync(order, ct);
                var isRenewalFailure = string.Equals(order.Status, PlatformSubscriptionOrder.StatusPaid, StringComparison.OrdinalIgnoreCase) ||
                                       !string.IsNullOrWhiteSpace(order.MercadoPagoPreapprovalId);
                var failureMsg = $"Cobrança recorrente não aprovada ({authPay.Status ?? "recusada"}). Regularize o cartão/pagamento em até {StoreSettings.SubscriptionBillingGraceDays} dias para evitar desativação da conta e loja.";
                if (isRenewalFailure)
                {
                    var now = DateTimeOffset.UtcNow;
                    var failureAnchor = order.AppliedPlanExpiresAt.HasValue && order.AppliedPlanExpiresAt.Value < now
                        ? order.AppliedPlanExpiresAt.Value
                        : now;
                    await ApplySubscriptionGraceToStoreAsync(order, failureAnchor, failureMsg, ct);
                }

                return new(true, false, isRenewalFailure, MapToDto(order, failureMsg), $"Assinatura em status '{authPay.Status}'.");
            }

            if (string.Equals(order.Status, PlatformSubscriptionOrder.StatusPaid, StringComparison.OrdinalIgnoreCase))
            {
                // Renovação mensal automática: estende a validade a partir do vencimento atual e reativa conta/vitrine.
                var now = DateTimeOffset.UtcNow;
                var baseDate = order.AppliedPlanExpiresAt.HasValue && order.AppliedPlanExpiresAt.Value > now
                    ? order.AppliedPlanExpiresAt.Value
                    : now;
                var nextExpiry = baseDate.AddDays(Math.Max(1, order.DurationDays));
                order.AppliedPlanExpiresAt = nextExpiry;
                await _repository.SavePlatformSubscriptionOrderAsync(order, ct);
                await SyncTargetStorePlanSettingsAsync(order, nextExpiry, order.IssuedGiftCardCode, now, null, ct);
                var msg = $"Renovação automática da assinatura {order.OrderNumber} confirmada.";
                return new(true, true, false, MapToDto(order, msg), msg);
            }

            await ApproveOrderInternalAsync(order, "Mercado Pago Assinatura", null, DateTimeOffset.UtcNow, ct);
            var approvedMsg = $"Assinatura {order.OrderNumber} aprovada automaticamente.";
            return new(true, true, false, MapToDto(order, approvedMsg), approvedMsg);
        }

        if (normalizedTopic.Contains("subscription_preapproval", StringComparison.Ordinal) ||
            normalizedTopic == "preapproval")
        {
            var pre = await _mercadoPago.GetPreapprovalStatusAsync(accessToken, cleanId, ct);
            if (pre is null)
                return new(false, false, false, null, "Assinatura não encontrada no Mercado Pago.");

            var order = await FindOrderForWebhookAsync(pre.ExternalReference, null, pre.PreapprovalId, ct);
            if (order is null)
                return new(false, false, false, null, "Pedido de assinatura não localizado.");

            order.MercadoPagoPreapprovalId ??= pre.PreapprovalId;
            order.MercadoPagoStatus = pre.Status;

            if (!pre.IsAuthorized)
            {
                await _repository.SavePlatformSubscriptionOrderAsync(order, ct);
                var statusLower = (pre.Status ?? string.Empty).Trim().ToLowerInvariant();
                var pausedOrCancelledAfterPaid =
                    string.Equals(order.Status, PlatformSubscriptionOrder.StatusPaid, StringComparison.OrdinalIgnoreCase) &&
                    (statusLower is "paused" or "cancelled" or "canceled");
                var msg = pausedOrCancelledAfterPaid
                    ? $"Assinatura '{pre.Status}' no Mercado Pago. Tolerância de até {StoreSettings.SubscriptionBillingGraceDays} dias após o vencimento antes da desativação."
                    : $"Assinatura '{pre.Status}' registrada.";

                if (pausedOrCancelledAfterPaid)
                {
                    var now = DateTimeOffset.UtcNow;
                    var failureAnchor = order.AppliedPlanExpiresAt.HasValue && order.AppliedPlanExpiresAt.Value < now
                        ? order.AppliedPlanExpiresAt.Value
                        : now;
                    await ApplySubscriptionGraceToStoreAsync(order, failureAnchor, msg, ct);
                }

                return new(true, false, pausedOrCancelledAfterPaid, MapToDto(order, msg), msg);
            }

            var minMonthly = Math.Round(order.Amount / Math.Max(1, order.DurationMonths), 2, MidpointRounding.AwayFromZero);
            if (pre.TransactionAmount.HasValue && pre.TransactionAmount.Value + 0.01m < minMonthly)
            {
                await _repository.SavePlatformSubscriptionOrderAsync(order, ct);
                return new(false, false, false, MapToDto(order, "Valor divergente na assinatura Mercado Pago."), "Valor divergente na assinatura Mercado Pago.");
            }

            await ApproveOrderInternalAsync(order, "Mercado Pago Assinatura Automática", null, DateTimeOffset.UtcNow, ct);
            var subMsg = $"Assinatura {order.OrderNumber} ativada automaticamente.";
            return new(true, true, false, MapToDto(order, subMsg), subMsg);
        }

        if (normalizedTopic.Contains("payment", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(normalizedTopic))
        {
            var payment = await _mercadoPago.GetPaymentStatusAsync(accessToken, cleanId, ct);
            if (payment is null)
                return new(false, false, false, null, "Pagamento não encontrado no Mercado Pago.");

            var order = await FindOrderForWebhookAsync(payment.ExternalReference, payment.ProviderPaymentId, null, ct);
            if (order is null)
                return new(false, false, false, null, "Pedido de plano correspondente não encontrado.");

            order.MercadoPagoPaymentId ??= payment.ProviderPaymentId;
            order.MercadoPagoStatus = payment.Status;

            if (!payment.IsApproved || payment.TransactionAmount + 0.01m < order.Amount)
            {
                await _repository.SavePlatformSubscriptionOrderAsync(order, ct);
                return new(true, false, false, MapToDto(order, $"Pagamento '{payment.Status}' registrado."), $"Pagamento '{payment.Status}' registrado para {order.OrderNumber}.");
            }

            await ApproveOrderInternalAsync(order, "Mercado Pago Automático", null, payment.DateApproved ?? DateTimeOffset.UtcNow, ct);
            var okMsg = $"Pagamento {order.OrderNumber} aprovado automaticamente via Mercado Pago.";
            return new(true, true, false, MapToDto(order, okMsg), okMsg);
        }

        return new(false, false, false, null, "Tópico não tratado.");
    }

    private async Task<string> InitializeMercadoPagoOrderAsync(
        PlatformSubscriptionOrder order,
        StoreSettings settings,
        string? publicBaseOrigin,
        CancellationToken ct)
    {
        if (_mercadoPago is null || _platformTokenStore is null)
            throw new DomainException("Integração Mercado Pago de planos indisponível neste servidor.");

        var accessToken = await _platformTokenStore.GetValidAccessTokenAsync(_mercadoPago, ct);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new DomainException(
                "A conta Mercado Pago da plataforma ainda não está conectada no servidor central (configure MP_PLATFORM_ACCESS_TOKEN ou conecte pelo YouLink Admin).");
        }

        var baseOrigin = ResolveEffectivePublicOrigin(publicBaseOrigin, settings.CentralCatalogBaseUrl);
        var webhookUrl = $"{baseOrigin}/api/public/webhooks/mercadopago/platform";
        var returnBaseUrl = $"{baseOrigin}/planos/retorno";
        var planTitle = $"YouLink {GetFriendlyPlanName(order.PlanTier)} ({order.DurationMonths} mes(es)) - {order.StoreName}";

        switch (order.PaymentMethod)
        {
            case PlatformSubscriptionOrder.MethodMercadoPagoPix:
                {
                    var mpPix = await _mercadoPago.CreatePixPaymentAsync(
                        accessToken,
                        order.Amount,
                        planTitle,
                        order.OrderNumber,
                        order.BuyerFullName,
                        order.BuyerEmail,
                        webhookUrl,
                        $"plan-pix-{order.Id:N}",
                        ct);

                    if (mpPix.Success)
                    {
                        order.MercadoPagoPaymentId = mpPix.ProviderPaymentId;
                        order.PixBrCode = mpPix.PixQrCode;
                        order.MercadoPagoQrCodeBase64 = mpPix.PixQrCodeBase64;
                        order.MercadoPagoStatus = mpPix.Status;
                        return "Pix oficial do Mercado Pago gerado! Ao pagar, seu plano libera automaticamente em poucos segundos.";
                    }

                    throw new DomainException(MercadoPagoPixService.TranslateMercadoPagoError(
                        mpPix.ErrorMessage,
                        "Não foi possível gerar o Pix automático no Mercado Pago."));
                }

            case PlatformSubscriptionOrder.MethodMercadoPagoCheckoutPro:
                {
                    var pref = await _mercadoPago.CreateCheckoutPreferenceAsync(
                        accessToken,
                        order.Amount,
                        planTitle,
                        order.OrderNumber,
                        order.BuyerFullName,
                        order.BuyerEmail,
                        returnBaseUrl,
                        webhookUrl,
                        $"plan-pref-{order.Id:N}",
                        ct);

                    if (!pref.Success)
                        throw new DomainException(MercadoPagoPixService.TranslateMercadoPagoError(
                            pref.ErrorMessage,
                            "Não foi possível iniciar o Checkout Pro do Mercado Pago."));

                    order.MercadoPagoPreferenceId = pref.PreferenceId;
                    order.MercadoPagoCheckoutUrl = pref.InitPoint;
                    order.MercadoPagoStatus = "pending";
                    return "Checkout seguro do Mercado Pago pronto! Conclua com Cartão, Saldo Mercado Pago ou Pix na janela aberta.";
                }

            case PlatformSubscriptionOrder.MethodMercadoPagoSubscription:
                {
                    var payerEmail = (order.BuyerEmail ?? string.Empty).Trim();
                    if (!payerEmail.Contains('@', StringComparison.Ordinal) || !payerEmail.Contains('.', StringComparison.Ordinal))
                    {
                        throw new DomainException(
                            "Para assinatura recorrente no cartão pelo Mercado Pago, informe um e-mail válido no campo Usuário / Gmail de Acesso.");
                    }

                    var sub = await _mercadoPago.CreatePreapprovalSubscriptionAsync(
                        accessToken,
                        order.Amount,
                        order.DurationMonths,
                        planTitle,
                        order.OrderNumber,
                        payerEmail,
                        $"{returnBaseUrl}?orderNumber={Uri.EscapeDataString(order.OrderNumber)}",
                        webhookUrl,
                        ct);

                    if (!sub.Success)
                        throw new DomainException(MercadoPagoPixService.TranslateMercadoPagoError(
                            sub.ErrorMessage,
                            "Não foi possível criar a assinatura automática no Mercado Pago."));

                    order.MercadoPagoPreapprovalId = sub.SubscriptionId;
                    order.MercadoPagoCheckoutUrl = sub.InitPoint;
                    order.MercadoPagoStatus = sub.Status;
                    return "Assinatura automática criada! Conclua a autorização no cartão pelo Mercado Pago para liberar e renovar automaticamente.";
                }

            default:
                throw new DomainException("Forma de pagamento de plano inválida.");
        }
    }

    private async Task<PlatformSubscriptionOrder> TryRefreshPendingMercadoPagoOrderAsync(
        PlatformSubscriptionOrder order,
        CancellationToken ct)
    {
        if (_mercadoPago is null || _platformTokenStore is null)
            return order;

        try
        {
            var accessToken = await _platformTokenStore.GetValidAccessTokenAsync(_mercadoPago, ct);
            if (string.IsNullOrWhiteSpace(accessToken))
                return order;

            if (string.Equals(order.PaymentMethod, PlatformSubscriptionOrder.MethodMercadoPagoSubscription, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(order.MercadoPagoPreapprovalId))
            {
                var pre = await _mercadoPago.GetPreapprovalStatusAsync(accessToken, order.MercadoPagoPreapprovalId, ct);
                if (pre is not null)
                {
                    order.MercadoPagoStatus = pre.Status;
                    if (pre.IsAuthorized && (!pre.TransactionAmount.HasValue || pre.TransactionAmount.Value + 0.01m >= order.Amount))
                    {
                        await ApproveOrderInternalAsync(order, "Mercado Pago Assinatura Automática", null, DateTimeOffset.UtcNow, ct);
                        return order;
                    }

                    await _repository.SavePlatformSubscriptionOrderAsync(order, ct);
                }

                return order;
            }

            MercadoPagoPaymentLookupResult? lookup = null;
            if (!string.IsNullOrWhiteSpace(order.MercadoPagoPaymentId))
            {
                lookup = await _mercadoPago.GetPaymentStatusAsync(accessToken, order.MercadoPagoPaymentId, ct);
            }
            else if (string.Equals(order.PaymentMethod, PlatformSubscriptionOrder.MethodMercadoPagoCheckoutPro, StringComparison.OrdinalIgnoreCase) ||
                     !string.IsNullOrWhiteSpace(order.MercadoPagoPreferenceId))
            {
                lookup = await _mercadoPago.SearchLatestPaymentByExternalReferenceAsync(accessToken, order.OrderNumber, ct);
            }

            if (lookup is not null)
            {
                order.MercadoPagoPaymentId ??= lookup.ProviderPaymentId;
                order.MercadoPagoStatus = lookup.Status;
                if (lookup.IsApproved && lookup.TransactionAmount + 0.01m >= order.Amount)
                {
                    await ApproveOrderInternalAsync(order, "Mercado Pago Automático", null, lookup.DateApproved ?? DateTimeOffset.UtcNow, ct);
                    return order;
                }

                await _repository.SavePlatformSubscriptionOrderAsync(order, ct);
            }
        }
        catch
        {
        }

        return order;
    }

    private async Task<PlatformSubscriptionOrder?> FindOrderForWebhookAsync(
        string? externalReference,
        string? providerPaymentId,
        string? providerPreapprovalId,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(externalReference))
        {
            var byNumber = await _repository.GetPlatformSubscriptionOrderByNumberAsync(externalReference.Trim(), ct);
            if (byNumber is not null)
                return byNumber;
        }

        var all = await _repository.GetPlatformSubscriptionOrdersAsync(ct);
        if (!string.IsNullOrWhiteSpace(providerPaymentId))
        {
            var byPay = all.FirstOrDefault(o =>
                string.Equals(o.MercadoPagoPaymentId, providerPaymentId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byPay is not null)
                return byPay;
        }

        if (!string.IsNullOrWhiteSpace(providerPreapprovalId))
        {
            return all.FirstOrDefault(o =>
                string.Equals(o.MercadoPagoPreapprovalId, providerPreapprovalId.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private async Task ApproveOrderInternalAsync(
        PlatformSubscriptionOrder order,
        string? adminName,
        Guid? adminEmployeeId,
        DateTimeOffset? approvedAt,
        CancellationToken ct)
    {
        if (string.Equals(order.Status, PlatformSubscriptionOrder.StatusPaid, StringComparison.OrdinalIgnoreCase))
            return;

        var now = approvedAt ?? DateTimeOffset.UtcNow;
        var expiresAt = now.AddDays(Math.Max(1, order.DurationDays));

        var auditCode = PlanActivationCode.Create(
            order.PlanTier,
            order.DurationDays,
            customCode: null,
            targetStoreName: order.StoreName,
            notes: $"Compra automática aprovada — Pedido {order.OrderNumber} ({order.BuyerUsername})",
            createdByEmployeeId: adminEmployeeId,
            codeExpiresAt: null,
            isGiftedUnpaid: false,
            suggestedDomainName: order.DesiredDomain);

        auditCode.MarkRedeemed(order.StoreName, order.BuyerUserId ?? adminEmployeeId, expiresAt, now);
        await _repository.SavePlanActivationCodeAsync(auditCode, ct);

        order.Approve(auditCode.Code, expiresAt, adminName, now);
        await _repository.SavePlatformSubscriptionOrderAsync(order, ct);

        await SyncTargetStorePlanSettingsAsync(order, expiresAt, auditCode.Code, now, adminEmployeeId, ct);
    }

    private async Task<IClothingStoreRepository?> ResolveTargetStoreRepositoryForOrderAsync(
        PlatformSubscriptionOrder order,
        CancellationToken ct)
    {
        if (_storeResolver is not null && !string.IsNullOrWhiteSpace(order.StoreSlug))
        {
            var tenantRepo = await _storeResolver.ResolveReadRepositoryAsync(order.StoreSlug, ct);
            if (tenantRepo is not null)
                return tenantRepo;
        }

        var rootSettings = await _repository.GetStoreSettingsAsync(ct);
        if (string.IsNullOrWhiteSpace(order.StoreName) ||
            string.Equals(rootSettings.StoreName, order.StoreName, StringComparison.OrdinalIgnoreCase))
        {
            return _repository;
        }

        return null;
    }

    private async Task SyncTargetStorePlanSettingsAsync(
        PlatformSubscriptionOrder order,
        DateTimeOffset expiresAt,
        string? auditCode,
        DateTimeOffset now,
        Guid? adminEmployeeId,
        CancellationToken ct)
    {
        var targetRepo = await ResolveTargetStoreRepositoryForOrderAsync(order, ct);
        if (targetRepo is null)
            return;

        var settings = await targetRepo.GetStoreSettingsAsync(ct);
        settings.UpdatePlanAndDesign(order.PlanTier);
        settings.PlanActivatedAt ??= now;
        settings.PlanExpiresAt = expiresAt;
        settings.ActivePlanBillingKind = PlanActivationCode.BillingKindPaid;
        if (!string.IsNullOrWhiteSpace(auditCode))
            settings.ActiveGiftCardCode = auditCode;
        settings.IsRecurringSubscription = string.Equals(
            order.PaymentMethod,
            PlatformSubscriptionOrder.MethodMercadoPagoSubscription,
            StringComparison.OrdinalIgnoreCase);
        if (order.UseOwnDedicatedServer)
            settings.ServerHostingMode = "DedicatedOwnServer";

        if (!string.IsNullOrWhiteSpace(order.DesiredDomain) &&
            (order.DesiredDomain.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             order.DesiredDomain.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                settings.ConfigureDomainChoice(null, order.DesiredDomain, order.BuyerUserId ?? adminEmployeeId);
            }
            catch
            {
            }
        }

        settings.ClearBillingGraceAndActivate(adminEmployeeId, now);
        await targetRepo.UpdateStoreSettingsAsync(settings, ct);
    }

    private async Task ApplySubscriptionGraceToStoreAsync(
        PlatformSubscriptionOrder order,
        DateTimeOffset dueAt,
        string failureMessage,
        CancellationToken ct)
    {
        var targetRepo = await ResolveTargetStoreRepositoryForOrderAsync(order, ct);
        if (targetRepo is null)
            return;

        var settings = await targetRepo.GetStoreSettingsAsync(ct);
        settings.StartOrRefreshSubscriptionGrace(dueAt, failureMessage, DateTimeOffset.UtcNow);
        await targetRepo.UpdateStoreSettingsAsync(settings, ct);
    }

    private static string ResolveEffectivePublicOrigin(string? publicBaseOrigin, string? fallbackCatalogUrl)
    {
        var candidates = new[] { publicBaseOrigin, fallbackCatalogUrl, AppUpdateCatalog.DefaultCentralPlatformServerUrl };
        foreach (var raw in candidates)
        {
            var normalized = WhatsAppLinkBuilder.NormalizePublicServerUrl(raw);
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }

        return AppUpdateCatalog.DefaultCentralPlatformServerUrl;
    }

    private static string ResolveMethodDisplayLabel(string paymentMethod) => paymentMethod switch
    {
        PlatformSubscriptionOrder.MethodMercadoPagoPix => "Mercado Pago (Pix Automático)",
        PlatformSubscriptionOrder.MethodMercadoPagoCheckoutPro => "Mercado Pago (Cartão / Conta MP)",
        PlatformSubscriptionOrder.MethodMercadoPagoSubscription => "Mercado Pago (Assinatura no Cartão)",
        _ => string.Empty
    };

    private static string GetFriendlyPlanName(string tier) => tier switch
    {
        StoreSettings.PlanTierPremium => "Premium",
        StoreSettings.PlanTierIntermediate => "Intermediário (Meia Versão)",
        _ => "Básico"
    };

    public async Task<PlatformSubscriptionOrderDto> CancelPlanOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        var order = await _repository.GetPlatformSubscriptionOrderByIdAsync(orderId, ct)
            ?? throw new DomainException("Pedido de plano não encontrado.");

        order.Cancel();
        await _repository.SavePlatformSubscriptionOrderAsync(order, ct);

        return MapToDto(order, "Pedido cancelado.");
    }

    private static string ResolveDefaultMessage(PlatformSubscriptionOrder order) =>
        order.Status switch
        {
            PlatformSubscriptionOrder.StatusPaid => $"Pago — liberado em {order.ApprovedAt?.ToLocalTime():dd/MM HH:mm}.",
            PlatformSubscriptionOrder.StatusCancelled => "Pedido cancelado.",
            _ => string.Equals(order.PaymentMethod, PlatformSubscriptionOrder.MethodDirectPix, StringComparison.OrdinalIgnoreCase)
                ? "Aguardando pagamento."
                : "Aguardando pagamento no Mercado Pago."
        };

    private static PlatformSubscriptionOrderDto MapToDto(PlatformSubscriptionOrder order, string message) =>
        new(
            order.Id,
            order.OrderNumber,
            order.ClaimToken,
            order.PlanTier,
            order.DurationMonths,
            order.DurationDays,
            order.Amount,
            order.StoreName,
            order.StoreSlug,
            order.DesiredDomain,
            order.UseOwnDedicatedServer,
            order.BuyerUserId,
            order.BuyerUsername,
            order.BuyerEmail,
            order.BuyerFullName,
            order.PixKeyUsed,
            order.PixBrCode,
            order.Status,
            order.IssuedGiftCardCode,
            order.AppliedPlanExpiresAt,
            order.CreatedAt,
            order.ApprovedAt,
            order.ApprovedByAdminName,
            message,
            order.PaymentMethod,
            order.MercadoPagoCheckoutUrl,
            order.MercadoPagoQrCodeBase64,
            order.MercadoPagoPaymentId,
            order.MercadoPagoPreapprovalId);
}
