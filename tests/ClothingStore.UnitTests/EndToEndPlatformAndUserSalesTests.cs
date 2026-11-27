using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClothingStore.Application.Interfaces;
using ClothingStore.Application.Services;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Enums;
using ClothingStore.Domain.Exceptions;
using ClothingStore.Infrastructure.Persistence;
using ClothingStore.Infrastructure.Security;
using ClothingStore.Infrastructure.Storage;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClothingStore.UnitTests;

[TestClass]
public sealed class EndToEndPlatformAndUserSalesTests
{
    private sealed class PlatformAndTenantMpHandler : HttpMessageHandler
    {
        public string AccessTokenToIssue { get; set; } = "APP_USR-SALES-E2E-TOKEN";
        public string RefreshTokenToIssue { get; set; } = "TG-SALES-E2E-REFRESH";

        // Platform payment simulation
        public string NextPlatformPaymentId { get; set; } = "77001122";
        public string PlatformPaymentStatus { get; set; } = "approved";
        public decimal PlatformPaymentAmount { get; set; } = 99.90m;
        public string? LastPlatformExternalReference { get; set; }

        public bool IncludeSearchResult { get; set; }
        public string SearchPaymentStatus { get; set; } = "approved";
        public decimal SearchPaymentAmount { get; set; } = 149.90m;
        public string? LastPreferenceExternalReference { get; set; }

        public string PreapprovalStatus { get; set; } = "authorized";
        public decimal PreapprovalAmount { get; set; } = 59.90m;
        public string AuthorizedPaymentStatus { get; set; } = "approved";
        public string? LastPreapprovalExternalReference { get; set; }

        // Tenant Pix simulation
        public string NextTenantPaymentId { get; set; } = "9911223344";
        public string TenantPaymentStatus { get; set; } = "pending";
        public decimal TenantPaymentAmount { get; set; } = 199.90m;
        public string? LastTenantExternalReference { get; set; }

        public int PlatformCreatePixCalls { get; private set; }
        public int PlatformCreatePreferenceCalls { get; private set; }
        public int PlatformCreatePreapprovalCalls { get; private set; }
        public int TenantCreatePixCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (request.Method == HttpMethod.Post && url.Contains("/oauth/token", StringComparison.OrdinalIgnoreCase))
            {
                return JsonOk(new
                {
                    access_token = AccessTokenToIssue,
                    refresh_token = RefreshTokenToIssue,
                    expires_in = 21600,
                    user_id = 880011L
                });
            }

            if (request.Method == HttpMethod.Get && url.Contains("/users/me", StringComparison.OrdinalIgnoreCase))
            {
                return JsonOk(new
                {
                    id = 880011L,
                    nickname = "YOULINK_VENDAS_E2E",
                    email = "vendas@youlink.com.br"
                });
            }

            if (request.Method == HttpMethod.Post &&
                url.Contains("/v1/payments", StringComparison.OrdinalIgnoreCase) &&
                !url.Contains("/search", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var extRef = doc.RootElement.TryGetProperty("external_reference", out var extEl)
                    ? extEl.GetString() ?? string.Empty
                    : string.Empty;
                var notifUrl = doc.RootElement.TryGetProperty("notification_url", out var notifEl)
                    ? notifEl.GetString() ?? string.Empty
                    : string.Empty;
                var isPlatformCall =
                    string.Equals(request.Headers.Authorization?.Parameter, "APP_USR-PLATFORM-OWNER-LIVE", StringComparison.Ordinal) ||
                    notifUrl.Contains("/webhooks/mercadopago/platform", StringComparison.OrdinalIgnoreCase);

                if (isPlatformCall)
                {
                    PlatformCreatePixCalls++;
                    LastPlatformExternalReference = extRef;
                    return JsonOk(new
                    {
                        id = long.Parse(NextPlatformPaymentId),
                        status = "pending",
                        external_reference = LastPlatformExternalReference,
                        transaction_amount = PlatformPaymentAmount,
                        point_of_interaction = new
                        {
                            transaction_data = new
                            {
                                qr_code = $"00020101021226770014BR.GOV.BCB.PIX0136MP-PLAN-{NextPlatformPaymentId}6304ABCD",
                                qr_code_base64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
                            }
                        }
                    });
                }

                TenantCreatePixCalls++;
                LastTenantExternalReference = extRef;
                return JsonOk(new
                {
                    id = long.Parse(NextTenantPaymentId),
                    status = "pending",
                    external_reference = LastTenantExternalReference,
                    point_of_interaction = new
                    {
                        transaction_data = new
                        {
                            qr_code = $"00020101021226770014BR.GOV.BCB.PIX0136MP-TENANT-{NextTenantPaymentId}6304EF01",
                            qr_code_base64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
                        }
                    }
                });
            }

            if (request.Method == HttpMethod.Post && url.Contains("/checkout/preferences", StringComparison.OrdinalIgnoreCase))
            {
                PlatformCreatePreferenceCalls++;
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                LastPreferenceExternalReference = doc.RootElement.TryGetProperty("external_reference", out var ext)
                    ? ext.GetString()
                    : null;
                return JsonOk(new
                {
                    id = "PREF-E2E-7001",
                    init_point = "https://www.mercadopago.com.br/checkout/v1/redirect?pref_id=PREF-E2E-7001",
                    sandbox_init_point = "https://sandbox.mercadopago.com.br/checkout/v1/redirect?pref_id=PREF-E2E-7001"
                });
            }

            if (request.Method == HttpMethod.Post && url.Contains("/preapproval", StringComparison.OrdinalIgnoreCase))
            {
                PlatformCreatePreapprovalCalls++;
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                LastPreapprovalExternalReference = doc.RootElement.TryGetProperty("external_reference", out var ext)
                    ? ext.GetString()
                    : null;
                return JsonOk(new
                {
                    id = "PREAPP-E2E-5001",
                    status = "pending",
                    init_point = "https://www.mercadopago.com.br/subscriptions/checkout?preapproval_id=PREAPP-E2E-5001"
                });
            }

            if (request.Method == HttpMethod.Get && url.Contains("/v1/payments/search", StringComparison.OrdinalIgnoreCase))
            {
                if (!IncludeSearchResult)
                    return JsonOk(new { results = Array.Empty<object>() });

                return JsonOk(new
                {
                    results = new[]
                    {
                        new
                        {
                            id = 77009988L,
                            status = SearchPaymentStatus,
                            external_reference = LastPreferenceExternalReference,
                            transaction_amount = SearchPaymentAmount,
                            date_approved = DateTimeOffset.UtcNow
                        }
                    }
                });
            }

            if (request.Method == HttpMethod.Get && url.Contains($"/v1/payments/{NextPlatformPaymentId}", StringComparison.OrdinalIgnoreCase))
            {
                return JsonOk(new
                {
                    id = long.Parse(NextPlatformPaymentId),
                    status = PlatformPaymentStatus,
                    external_reference = LastPlatformExternalReference,
                    transaction_amount = PlatformPaymentAmount,
                    date_approved = PlatformPaymentStatus == "approved" ? DateTimeOffset.UtcNow : (DateTimeOffset?)null
                });
            }

            if (request.Method == HttpMethod.Get && url.Contains($"/v1/payments/{NextTenantPaymentId}", StringComparison.OrdinalIgnoreCase))
            {
                return JsonOk(new
                {
                    id = long.Parse(NextTenantPaymentId),
                    status = TenantPaymentStatus,
                    external_reference = LastTenantExternalReference,
                    transaction_amount = TenantPaymentAmount,
                    date_approved = TenantPaymentStatus == "approved" ? DateTimeOffset.UtcNow : (DateTimeOffset?)null
                });
            }

            if (request.Method == HttpMethod.Get && url.Contains("/preapproval/", StringComparison.OrdinalIgnoreCase))
            {
                return JsonOk(new
                {
                    id = "PREAPP-E2E-5001",
                    status = PreapprovalStatus,
                    external_reference = LastPreapprovalExternalReference,
                    payer_email = "cliente.assinatura@youlink.com.br",
                    auto_recurring = new
                    {
                        transaction_amount = PreapprovalAmount,
                        currency_id = "BRL",
                        frequency = 1,
                        frequency_type = "months"
                    }
                });
            }

            if (request.Method == HttpMethod.Get && url.Contains("/authorized_payments/", StringComparison.OrdinalIgnoreCase))
            {
                return JsonOk(new
                {
                    id = 66112233L,
                    preapproval_id = "PREAPP-E2E-5001",
                    external_reference = LastPreapprovalExternalReference,
                    status = AuthorizedPaymentStatus,
                    transaction_amount = PreapprovalAmount,
                    payment = new
                    {
                        id = 66112299L,
                        status = AuthorizedPaymentStatus
                    }
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"Unmocked URL: {request.Method} {url}")
            };
        }

        private static HttpResponseMessage JsonOk(object obj) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json")
            };
    }

    private static void CleanupDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
        }
    }

    private static string BuildMpWebhookSignature(string secret, string requestId, string dataId, long ts)
    {
        var manifest = $"id:{dataId};request-id:{requestId};ts:{ts};";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(manifest));
        return $"ts={ts},v1={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static int ReserveFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [TestMethod]
    public async Task MinhasVendas_DonoPlataforma_PixQr_CheckoutProCartao_AssinaturaECancelamento()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink_owner_sales_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var hasher = new Pbkdf2PasswordHasher();
            var rootRepo = new ClothingStoreRepository(hasher, Path.Combine(tempDir, "owner_db.json"), enablePlatformOwnerCompanion: false);
            var settings = await rootRepo.GetStoreSettingsAsync();
            settings.BasicPlanMonthlyPrice = 59.90m;
            settings.IntermediaryPlanMonthlyPrice = 99.90m;
            settings.PremiumPlanMonthlyPrice = 149.90m;
            await rootRepo.UpdateStoreSettingsAsync(settings);

            var handler = new PlatformAndTenantMpHandler();
            var http = new HttpClient(handler);
            var mpService = new MercadoPagoPixService(
                http,
                () => new MercadoPagoAppConfig("CID-E2E", "CSEC-E2E", "WHSEC-E2E"));
            var tokenStore = new PlatformMercadoPagoTokenStore(tempDir, () => "APP_USR-PLATFORM-OWNER-LIVE");
            var resolver = new MultiTenantStoreManager(rootRepo, hasher, tempDir);
            var subService = new PlatformSubscriptionService(rootRepo, mpService, tokenStore, resolver);

            // 1) Venda #1 do Dono: Plano Intermediário via Mercado Pago Pix QR Code
            handler.PlatformPaymentAmount = 99.90m;
            handler.PlatformPaymentStatus = "pending";
            var pixOrder = await subService.CreatePlanOrderAsync(new CreatePlatformPlanOrderRequest(
                PlanTier: StoreSettings.PlanTierIntermediate,
                DurationMonths: 1,
                Amount: 99.90m,
                StoreName: "Loja Cliente Um",
                BuyerEmail: "cliente1@loja.com",
                PaymentMethod: PlatformSubscriptionOrder.MethodMercadoPagoPix),
                publicBaseOrigin: "https://youlink-loja.onrender.com");

            Assert.IsFalse(string.IsNullOrWhiteSpace(pixOrder.PixBrCode), "Venda Pix do dono precisa gerar código QR Pix.");
            Assert.AreEqual(PlatformSubscriptionOrder.StatusPending, pixOrder.Status);

            // Proteção antifraude: webhook com valor adulterado (R$ 1,00 em vez de R$ 99,90) NÃO aprova!
            handler.PlatformPaymentStatus = "approved";
            handler.PlatformPaymentAmount = 1.00m;
            var tampered = await subService.HandleMercadoPagoWebhookDetailedAsync("payment", handler.NextPlatformPaymentId);
            Assert.IsFalse(tampered.OrderApproved, "Pagamento com valor menor não deve ser aprovado.");

            // Webhook legítimo no valor cheio aprova automaticamente
            handler.PlatformPaymentAmount = 99.90m;
            var approvedPix = await subService.HandleMercadoPagoWebhookDetailedAsync("payment", handler.NextPlatformPaymentId);
            Assert.IsTrue(approvedPix.Handled);
            Assert.IsTrue(approvedPix.OrderApproved);

            var pixStatus = await subService.GetPlanOrderStatusAsync(pixOrder.OrderNumber, pixOrder.ClaimToken);
            Assert.IsTrue(pixStatus.IsApproved);
            Assert.IsFalse(string.IsNullOrWhiteSpace(pixStatus.IssuedGiftCardCode));

            // 2) Venda #2 do Dono: Plano Premium via Checkout Pro (Cartão / Saldo MP) + auto-aprovação por polling
            var cardOrder = await subService.CreatePlanOrderAsync(new CreatePlatformPlanOrderRequest(
                PlanTier: StoreSettings.PlanTierPremium,
                DurationMonths: 1,
                Amount: 149.90m,
                StoreName: "Loja Cliente Dois",
                BuyerEmail: "cliente2@loja.com",
                PaymentMethod: PlatformSubscriptionOrder.MethodMercadoPagoCheckoutPro),
                publicBaseOrigin: "https://youlink-loja.onrender.com");

            Assert.IsFalse(string.IsNullOrWhiteSpace(cardOrder.MercadoPagoCheckoutUrl));
            handler.IncludeSearchResult = true;
            handler.SearchPaymentAmount = 149.90m;
            handler.SearchPaymentStatus = "approved";

            var cardStatus = await subService.GetPlanOrderStatusAsync(cardOrder.OrderNumber, cardOrder.ClaimToken);
            Assert.IsTrue(cardStatus.IsApproved, "Venda Checkout Pro deve aprovar automaticamente quando pagamento aparecer na busca.");

            // 3) Venda #3 do Dono: Assinatura Recorrente no Cartão -> mês 1 aprovado + renovação mês 2
            var subOrder = await subService.CreatePlanOrderAsync(new CreatePlatformPlanOrderRequest(
                PlanTier: StoreSettings.PlanTierBasic,
                DurationMonths: 1,
                Amount: 59.90m,
                StoreName: "Loja Assinante",
                BuyerEmail: "cliente.assinatura@youlink.com.br",
                PaymentMethod: PlatformSubscriptionOrder.MethodMercadoPagoSubscription),
                publicBaseOrigin: "https://youlink-loja.onrender.com");

            handler.PreapprovalStatus = "authorized";
            handler.PreapprovalAmount = 59.90m;
            var subApproved = await subService.HandleMercadoPagoWebhookDetailedAsync("subscription_preapproval", "PREAPP-E2E-5001");
            Assert.IsTrue(subApproved.OrderApproved);
            var firstExpiry = subApproved.Order!.AppliedPlanExpiresAt!.Value;

            handler.AuthorizedPaymentStatus = "approved";
            var subRenewed = await subService.HandleMercadoPagoWebhookDetailedAsync("subscription_authorized_payment", "66112233");
            Assert.IsTrue(subRenewed.OrderApproved);
            Assert.IsTrue(subRenewed.Order!.AppliedPlanExpiresAt!.Value > firstExpiry, "Renovação recorrente deve estender a validade.");

            // 4) Pedido cancelado do dono não entra nas vendas aprovadas
            var cancelledOrder = await subService.CreatePlanOrderAsync(new CreatePlatformPlanOrderRequest(
                PlanTier: StoreSettings.PlanTierPremium,
                DurationMonths: 1,
                Amount: 149.90m,
                StoreName: "Loja Desistente",
                BuyerEmail: "desistente@loja.com",
                PaymentMethod: PlatformSubscriptionOrder.MethodMercadoPagoCheckoutPro),
                publicBaseOrigin: "https://youlink-loja.onrender.com");
            var cancelled = await subService.CancelPlanOrderAsync(cancelledOrder.Id);
            Assert.AreEqual(PlatformSubscriptionOrder.StatusCancelled, cancelled.Status);

            // 5) Resumo final das vendas do dono + Gift Card Cortesia gratuito
            var allPlatformOrders = await subService.GetAllOrdersAsync();
            var paidPlatformOrders = allPlatformOrders
                .Where(o => string.Equals(o.Status, PlatformSubscriptionOrder.StatusPaid, StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.AreEqual(3, paidPlatformOrders.Count);
            Assert.AreEqual(309.70m, paidPlatformOrders.Sum(o => o.Amount)); // 99.90 + 149.90 + 59.90

            var courtesyCard = PlanActivationCode.Create(
                StoreSettings.PlanTierPremium,
                30,
                customCode: "CORTESIA-TESTE-01",
                isGiftedUnpaid: true);
            await rootRepo.SavePlanActivationCodeAsync(courtesyCard);
            var config = new ConfigurationBuilder().Build();
            var storage = new SupabaseImageStorageService(new HttpClient(), config, Path.Combine(tempDir, "www"));
            var settingsSvc = new EmployeeAndSettingsService(rootRepo, hasher, storage);
            var redeemed = await settingsSvc.RedeemActivationCodeAsync(new RedeemActivationCodeRequest { Code = "CORTESIA-TESTE-01" });
            Assert.AreEqual(PlanActivationCode.BillingKindGiftedUnpaid, redeemed.UpdatedSettings.ActivePlanBillingKind);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task VendasDosUsuarios_PixDireto_MercadoPago_ManualCaixa_EstornoEIsolamentoMultiTenant()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink_user_sales_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var hasher = new Pbkdf2PasswordHasher();
            var defaultRepo = new ClothingStoreRepository(hasher, Path.Combine(tempDir, "root_db.json"), enablePlatformOwnerCompanion: false);
            var resolver = new MultiTenantStoreManager(defaultRepo, hasher, tempDir);
            var handler = new PlatformAndTenantMpHandler();
            var mpHttp = new HttpClient(handler);
            var webhookSecret = "TENANT-WH-SECRET-123";
            var mpService = new MercadoPagoPixService(
                mpHttp,
                () => new MercadoPagoAppConfig("CID-MT", "CSEC-MT", webhookSecret));
            var storage = new SupabaseImageStorageService(new HttpClient(), new ConfigurationBuilder().Build(), Path.Combine(tempDir, "www"));
            var gateway = new MultiTenantGatewayService(resolver, mpService, mpHttp, hasher, storage);

            // 1) Usuário Básico NÃO consegue vender diretamente nem registrar venda manual
            var basicRepo = new ClothingStoreRepository(hasher, Path.Combine(tempDir, "basic_store.json"), enablePlatformOwnerCompanion: false);
            var basicSettings = await basicRepo.GetStoreSettingsAsync();
            basicSettings.PlanTier = StoreSettings.PlanTierBasic;
            await basicRepo.UpdateStoreSettingsAsync(basicSettings);
            var basicOrderSvc = new OrderAndShippingService(basicRepo);
            await Assert.ThrowsExceptionAsync<DomainException>(async () =>
                await basicOrderSvc.RegisterManualSaleAsync(
                    Guid.NewGuid(),
                    new RegisterManualSaleRequest(Guid.NewGuid(), Guid.NewGuid(), 1, "Cliente Básico")));

            // 2) Usuário A (Intermediário + Pix Direto): publica loja, recebe pedido na vitrine, confirma, envia e estorna 2º pedido
            var prodAId = Guid.NewGuid();
            var publishA = await gateway.PublishStoreAsync(new CloudCatalogSyncPayload
            {
                Settings = new UpdateStoreSettingsRequest
                {
                    StoreName = "Loja Usuária Ana",
                    StoreSlug = "loja-ana",
                    WhatsappNumber = "5511999991111",
                    StoreDescription = "Catálogo Ana",
                    IsSiteActive = true,
                    PlanTier = StoreSettings.PlanTierIntermediate,
                    DirectPixSalesEnabled = true,
                    PixGatewayMode = StoreSettings.PixGatewayDirect,
                    StorePixKey = "ana@pix.com.br",
                    StorePixReceiverName = "Ana Silva",
                    OriginCep = "01310-100",
                    OriginCity = "São Paulo",
                    OriginState = "SP",
                    ServerHostingMode = "PlatformShared"
                },
                Categories = [new SaveCategoryRequest { Name = "Calçados", DisplayOrder = 1, IsActive = true }],
                CatalogSnapshot =
                [
                    new CloudProductSyncDto
                    {
                        SourceId = prodAId,
                        CategoryName = "Calçados",
                        CategorySlug = "calcados",
                        Name = "Tênis Urbano Ana",
                        Slug = "tenis-urbano-ana",
                        Description = "Conforto diário",
                        BasePrice = 150.00m,
                        IsVisibleOnSite = true,
                        Variants =
                        [
                            new CloudProductVariantSyncDto
                            {
                                Color = "Branco",
                                ColorHex = "#FFFFFF",
                                Size = "38",
                                Sku = "ANA-TNS-38",
                                StockQuantity = 5,
                                LowStockThreshold = 1,
                                IsActive = true
                            }
                        ],
                        Images = []
                    }
                ]
            }, "loja-ana", null, null, false, "ana@loja.com", "https://youlink-loja.onrender.com");
            Assert.IsTrue(publishA.Success, publishA.Message);

            var tenantRepoA = (await gateway.ResolveReadRepositoryAsync("loja-ana"))!;
            var settingsAna = await tenantRepoA.GetStoreSettingsAsync();
            settingsAna.UpdatePlanAndDesign(StoreSettings.PlanTierIntermediate);
            settingsAna.DirectPixSalesEnabled = true;
            await tenantRepoA.UpdateStoreSettingsAsync(settingsAna);
            var catServiceA = gateway.CreateCatalogService(tenantRepoA);
            var catalogA = await catServiceA.GetCatalogProductsAsync(new CatalogQueryFilter());
            var detailA = (await catServiceA.GetPublicProductDetailAsync(catalogA[0].Slug))!;
            var variantA = detailA.Variants[0];
            var orderSvcA = gateway.CreateOrderService(tenantRepoA);

            var quoteA = await orderSvcA.QuoteShippingForBuyerAsync(new ShippingQuoteRequest { DestinationCep = "01310-200", WeightGrams = 600 });
            Assert.IsTrue(quoteA.DirectPixSalesReady);
            Assert.IsTrue(quoteA.Eligibility.IsAllowed);

            var orderA1 = await gateway.PlacePixOrderForStoreAsync("loja-ana", new CreatePixOrderRequest
            {
                ProductId = detailA.Id,
                ProductVariantId = variantA.Id,
                Quantity = 2,
                CustomerName = "Compradora Lucia",
                CustomerWhatsapp = "11988880001",
                Cep = "01310-200",
                Street = "Av Paulista",
                Number = "500",
                Neighborhood = "Bela Vista",
                City = "São Paulo",
                State = "SP"
            }, "https://youlink-loja.onrender.com");

            Assert.AreEqual(300.00m, orderA1.ItemsTotal);
            Assert.IsTrue(orderA1.PixBrCode.StartsWith("000201", StringComparison.Ordinal));
            Assert.IsFalse(orderA1.StockDeducted, "Pix Direto só baixa estoque quando vendedor confirmar recebimento.");

            var sellerAId = Guid.NewGuid();
            var confirmedA1 = await orderSvcA.ConfirmPixPaymentAsync(orderA1.Id, sellerAId);
            Assert.IsTrue(confirmedA1.StockDeducted);
            Assert.AreEqual(StoreOrder.StatusPaidPreparing, confirmedA1.Status);

            var shippedA1 = await orderSvcA.UpdateOrderFulfillmentAsync(orderA1.Id, new UpdateOrderFulfillmentRequest
            {
                FulfillmentMethod = "Correios PAC",
                AgreedShippingCost = 22.00m,
                TrackingCode = "BR123456789SP",
                MarkAsShipped = true,
                MarkAsDelivered = true
            });
            Assert.AreEqual(StoreOrder.StatusDelivered, shippedA1.Status);

            // Pedido #2 da Ana é pago e depois CANCELADO -> estoque precisa voltar de 3 para 2 e depois 3!
            var orderA2 = await gateway.PlacePixOrderForStoreAsync("loja-ana", new CreatePixOrderRequest
            {
                ProductId = detailA.Id,
                ProductVariantId = variantA.Id,
                Quantity = 1,
                CustomerName = "Comprador Pedro",
                CustomerWhatsapp = "11988880002",
                Cep = "01310-200",
                Street = "Rua Augusta",
                Number = "100",
                Neighborhood = "Consolação",
                City = "São Paulo",
                State = "SP"
            }, "https://youlink-loja.onrender.com");
            await orderSvcA.ConfirmPixPaymentAsync(orderA2.Id, sellerAId);
            var afterPaid2 = await tenantRepoA.GetProductBySlugOrIdAsync(detailA.Id.ToString());
            Assert.AreEqual(2, afterPaid2!.Variants[0].StockQuantity); // 5 - 2 - 1 = 2

            var cancelledA2 = await orderSvcA.CancelOrderAsync(orderA2.Id, sellerAId, "Cliente desistiu");
            Assert.IsFalse(cancelledA2.StockDeducted);
            Assert.AreEqual(StoreOrder.StatusCancelled, cancelledA2.Status);
            var afterCancel2 = await tenantRepoA.GetProductBySlugOrIdAsync(detailA.Id.ToString());
            Assert.AreEqual(3, afterCancel2!.Variants[0].StockQuantity, "Cancelamento deve estornar exatamente a quantidade vendida ao estoque.");

            var reportA = await orderSvcA.GetSalesReportAsync("all");
            Assert.IsTrue(reportA.IsUnlocked);
            Assert.AreEqual(1, reportA.ConfirmedOrdersCount);
            Assert.AreEqual(1, reportA.CancelledOrdersCount);
            Assert.AreEqual(22.00m, shippedA1.AgreedShippingCost);
            Assert.AreEqual(300.00m, reportA.TotalConfirmedRevenue); // 2x 150.00 no Pix Direto (pedido cancelado estornado)

            // 3) Usuário B (Premium + Mercado Pago OAuth + Vendas Manuais no Caixa + Bloqueio de Oversell)
            var prodBId = Guid.NewGuid();
            var publishB = await gateway.PublishStoreAsync(new CloudCatalogSyncPayload
            {
                Settings = new UpdateStoreSettingsRequest
                {
                    StoreName = "Boutique Usuário Bruno",
                    StoreSlug = "loja-bruno",
                    WhatsappNumber = "5521999992222",
                    StoreDescription = "Boutique Bruno",
                    IsSiteActive = true,
                    PlanTier = StoreSettings.PlanTierPremium,
                    DirectPixSalesEnabled = false,
                    PixGatewayMode = StoreSettings.PixGatewayMercadoPago,
                    OriginCep = "20040-020",
                    OriginCity = "Rio de Janeiro",
                    OriginState = "RJ",
                    ServerHostingMode = "PlatformShared"
                },
                Categories = [new SaveCategoryRequest { Name = "Bolsas", DisplayOrder = 1, IsActive = true }],
                CatalogSnapshot =
                [
                    new CloudProductSyncDto
                    {
                        SourceId = prodBId,
                        CategoryName = "Bolsas",
                        CategorySlug = "bolsas",
                        Name = "Bolsa Couro Bruno",
                        Slug = "bolsa-couro-bruno",
                        Description = "Couro legítimo",
                        BasePrice = 200.00m,
                        IsVisibleOnSite = true,
                        Variants =
                        [
                            new CloudProductVariantSyncDto
                            {
                                Color = "Caramelo",
                                ColorHex = "#92400E",
                                Size = "U",
                                Sku = "BRU-BLS-U",
                                StockQuantity = 6,
                                LowStockThreshold = 2,
                                IsActive = true
                            }
                        ],
                        Images = []
                    }
                ]
            }, "loja-bruno", null, null, false, "bruno@loja.com", "https://youlink-loja.onrender.com");
            Assert.IsTrue(publishB.Success, publishB.Message);

            var preBrunoRepo = (await gateway.ResolveReadRepositoryAsync("loja-bruno"))!;
            var preBrunoSettings = await preBrunoRepo.GetStoreSettingsAsync();
            preBrunoSettings.UpdatePlanAndDesign(StoreSettings.PlanTierPremium);
            await preBrunoRepo.UpdateStoreSettingsAsync(preBrunoSettings);

            var oauthStart = await gateway.StartMercadoPagoOAuthAsync(
                "loja-bruno",
                null,
                publishB.PublishToken,
                false,
                "bruno@loja.com",
                "https://youlink-loja.onrender.com/api/payments/mercadopago/oauth/callback");
            var callbackRes = await gateway.CompleteMercadoPagoOAuthCallbackAsync("AUTH-CODE-BRUNO", oauthStart.Response.StateToken, null, null);
            Assert.IsTrue(callbackRes.Success, callbackRes.Message);

            var tenantRepoB = (await gateway.ResolveReadRepositoryAsync("loja-bruno"))!;
            var catServiceB = gateway.CreateCatalogService(tenantRepoB);
            var catalogB = await catServiceB.GetCatalogProductsAsync(new CatalogQueryFilter());
            var detailB = (await catServiceB.GetPublicProductDetailAsync(catalogB[0].Slug))!;
            var variantB = detailB.Variants[0];

            handler.TenantPaymentAmount = 400.00m;
            var orderB1 = await gateway.PlacePixOrderForStoreAsync("loja-bruno", new CreatePixOrderRequest
            {
                ProductId = detailB.Id,
                ProductVariantId = variantB.Id,
                Quantity = 2,
                CustomerName = "Cliente Site Bruno",
                CustomerWhatsapp = "21977770001",
                Cep = "20040-007",
                Street = "Av Rio Branco",
                Number = "120",
                Neighborhood = "Centro",
                City = "Rio de Janeiro",
                State = "RJ"
            }, "https://youlink-loja.onrender.com");

            Assert.AreEqual(StoreOrder.PaymentProviderMercadoPago, orderB1.PaymentProvider);
            Assert.IsTrue(orderB1.PixBrCode.Contains("MP-TENANT-", StringComparison.Ordinal));

            // Webhook do Mercado Pago confirma venda #1 de Bruno; segundo webhook igual é idempotente
            handler.TenantPaymentStatus = "approved";
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var sig = BuildMpWebhookSignature(webhookSecret, "req-bruno-1", handler.NextTenantPaymentId, ts);
            var webhookBody = JsonSerializer.Serialize(new { type = "payment", data = new { id = handler.NextTenantPaymentId } });
            var wh1 = await gateway.ProcessMercadoPagoWebhookAsync("loja-bruno", handler.NextTenantPaymentId, "payment", sig, "req-bruno-1", webhookBody);
            Assert.IsTrue(wh1.SignatureValid);
            Assert.IsTrue(wh1.OrderConfirmed);

            var whDup = await gateway.ProcessMercadoPagoWebhookAsync("loja-bruno", handler.NextTenantPaymentId, "payment", sig, "req-bruno-1", webhookBody);
            Assert.IsTrue(whDup.SignatureValid);
            var prodAfterDupWebhook = await tenantRepoB.GetProductBySlugOrIdAsync(detailB.Id.ToString());
            Assert.AreEqual(4, prodAfterDupWebhook!.Variants[0].StockQuantity, "Webhook repetido não pode baixar estoque novamente.");

            var prodBAfterMp = await tenantRepoB.GetProductBySlugOrIdAsync(detailB.Id.ToString());
            Assert.AreEqual(4, prodBAfterMp!.Variants[0].StockQuantity); // 6 - 2 = 4

            // Venda manual no caixa / WhatsApp do Usuário B (3 unidades * R$ 200 + R$ 15 frete = R$ 615)
            var orderSvcB = gateway.CreateOrderService(tenantRepoB);
            var manualB = await orderSvcB.RegisterManualSaleAsync(
                Guid.NewGuid(),
                new RegisterManualSaleRequest(
                    detailB.Id,
                    variantB.Id,
                    3,
                    "Cliente Balcão RJ",
                    "21966660000",
                    StoreOrder.PaymentMethodCardOrLink,
                    AgreedShippingCost: 15.00m,
                    SellerNotes: "Passado na maquininha"));
            Assert.AreEqual(615.00m, manualB.TotalPixAmount);

            // Estoque restante = 1; tentar vender 2 unidades no caixa bloqueia overselling!
            await Assert.ThrowsExceptionAsync<DomainException>(async () =>
                await orderSvcB.RegisterManualSaleAsync(
                    Guid.NewGuid(),
                    new RegisterManualSaleRequest(detailB.Id, variantB.Id, 2, "Cliente Oversell")));

            var reportB = await orderSvcB.GetSalesReportAsync("all");
            Assert.AreEqual(2, reportB.ConfirmedOrdersCount);
            Assert.AreEqual(5, reportB.TotalUnitsSold); // 2 MP + 3 manual
            Assert.AreEqual(1015.00m, reportB.TotalConfirmedRevenue); // 400.00 + 615.00

            // Isolamento estrito entre Usuário A e Usuário B
            var ordersA = await orderSvcA.GetOrdersAsync();
            var ordersB = await orderSvcB.GetOrdersAsync();
            Assert.IsFalse(ordersA.Any(o => o.CustomerName.Contains("Bruno", StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(ordersB.Any(o => o.CustomerName.Contains("Lucia", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task Vendas_BloqueioPorSuspensaoApos5Dias_LiberacaoNaCarencia_ELoopbackHttpRealWeb()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink_web_sales_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var hasher = new Pbkdf2PasswordHasher();
            var repo = new ClothingStoreRepository(hasher, Path.Combine(tempDir, "local_grace.json"), enablePlatformOwnerCompanion: false);
            var jwtCfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:SecretKey"] = "TestSecretKeyForUnitTesting_1234567890_ABCDEF!" })
                .Build();
            var auth = new AuthService(repo, hasher, new JwtTokenService(jwtCfg));
            var createdSeller = await auth.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                "Lojista Cartão",
                "lojista.cartao@teste.com",
                "Senha@123",
                Username: "lojista.cartao"));
            var empId = createdSeller.Employee.EmployeeId;
            await repo.AddProductAsync(CreateSeededProduct(empId, "Jaqueta Sarja", "JKT-01", 10, 180.00m, out var seededVar));

            var orderSvc = new OrderAndShippingService(repo);
            var settings = await repo.GetStoreSettingsAsync();
            settings.UpdatePlanAndDesign(StoreSettings.PlanTierPremium);
            settings.PlanActivatedAt = DateTimeOffset.UtcNow.AddDays(-30);
            settings.PlanExpiresAt = DateTimeOffset.UtcNow.AddDays(-1);
            settings.DirectPixSalesEnabled = true;
            settings.PixGatewayMode = StoreSettings.PixGatewayDirect;
            settings.StorePixKey = "lojista@pix.com.br";
            settings.StorePixReceiverName = "Lojista Teste";
            settings.OriginCep = "01310-100";
            settings.OriginCity = "São Paulo";
            settings.OriginState = "SP";
            settings.ActivateSite();

            // 1) Entra na janela de carência de até 5 dias (cartão falhou ontem -> faltam ~4 dias) -> vendas CONTINUAM funcionando!
            settings.StartOrRefreshSubscriptionGrace(DateTimeOffset.UtcNow.AddDays(-1), "Cartão de débito sem saldo.");
            await repo.UpdateStoreSettingsAsync(settings);
            Assert.IsTrue(settings.IsInSubscriptionGrace());
            Assert.IsFalse(settings.IsBillingSuspended);

            var product = (await repo.GetProductsAsync())[0];
            var duringGraceSale = await orderSvc.RegisterManualSaleAsync(
                empId,
                new RegisterManualSaleRequest(product.Id, seededVar.Id, 1, "Venda Durante Carência"));
            Assert.AreEqual(180.00m, duringGraceSale.TotalPixAmount);

            // 2) Encerra prazo de 5 dias sem pagamento -> desativa automaticamente e BLOQUEIA novas vendas na vitrine E no caixa!
            var pastFiveDays = await repo.GetStoreSettingsAsync();
            pastFiveDays.BillingGraceEndsAt = DateTimeOffset.UtcNow.AddHours(-2);
            await repo.UpdateStoreSettingsAsync(pastFiveDays);

            await Assert.ThrowsExceptionAsync<DomainException>(async () =>
                await orderSvc.PlacePixOrderAsync(new CreatePixOrderRequest
                {
                    ProductId = product.Id,
                    ProductVariantId = seededVar.Id,
                    Quantity = 1,
                    CustomerName = "Cliente Loja Pausada",
                    CustomerWhatsapp = "11999990000",
                    Cep = "01310-200",
                    Street = "Av Paulista",
                    Number = "10",
                    Neighborhood = "Bela Vista",
                    City = "São Paulo",
                    State = "SP"
                }));

            await Assert.ThrowsExceptionAsync<DomainException>(async () =>
                await orderSvc.RegisterManualSaleAsync(
                    empId,
                    new RegisterManualSaleRequest(product.Id, seededVar.Id, 1, "Venda Loja Pausada")));

            // 3) Regulariza pagamento -> reativa loja e libera vendas novamente!
            var restored = await repo.GetStoreSettingsAsync();
            restored.PlanExpiresAt = DateTimeOffset.UtcNow.AddDays(30);
            restored.ClearBillingGraceAndActivate(empId);
            await repo.UpdateStoreSettingsAsync(restored);

            var recoveredSale = await orderSvc.PlacePixOrderAsync(new CreatePixOrderRequest
            {
                ProductId = product.Id,
                ProductVariantId = seededVar.Id,
                Quantity = 1,
                CustomerName = "Cliente Pós-Regularização",
                CustomerWhatsapp = "11999990009",
                Cep = "01310-200",
                Street = "Av Paulista",
                Number = "20",
                Neighborhood = "Bela Vista",
                City = "São Paulo",
                State = "SP"
            });
            Assert.IsTrue(recoveredSale.TotalPixAmount > 0m);

            // 4) Teste HTTP Real em Loopback com ClothingStore.Web compilada!
            await VerifyRealWebLoopbackSalesEndpointsAsync(tempDir, hasher);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    private static Product CreateSeededProduct(
        Guid employeeId,
        string name,
        string sku,
        int stock,
        decimal price,
        out ProductVariant variant)
    {
        var prod = new Product(Guid.NewGuid(), name, "Produto teste", price, true);
        variant = prod.AddVariant("Preto", "M", "#111111", sku, 2);
        variant.ApplyStockMovement(employeeId, StockMovementType.In, stock, "Carga inicial");
        return prod;
    }

    private static async Task VerifyRealWebLoopbackSalesEndpointsAsync(string tempDir, Pbkdf2PasswordHasher hasher)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var webProjectDir = Path.Combine(root, "src", "ClothingStore.Web");
        var testConfig = new FileInfo(typeof(EndToEndPlatformAndUserSalesTests).Assembly.Location).Directory?.Parent?.Name ?? "Release";

        var candidateDirs = new[]
        {
            Path.Combine(webProjectDir, "bin", testConfig, "net10.0"),
            Path.Combine(webProjectDir, "bin", "Release", "net10.0"),
            Path.Combine(webProjectDir, "bin", "Debug", "net10.0")
        };

        var webBinDir = candidateDirs.FirstOrDefault(d =>
            File.Exists(Path.Combine(d, "ClothingStore.Web.dll")) ||
            File.Exists(Path.Combine(d, "ClothingStore.Web.exe")));
        Assert.IsNotNull(webBinDir, "ClothingStore.Web compilada não encontrada para teste de venda via HTTP loopback.");

        var webExe = Path.Combine(webBinDir!, "ClothingStore.Web.exe");
        var webDll = Path.Combine(webBinDir!, "ClothingStore.Web.dll");
        var useExe = OperatingSystem.IsWindows() && File.Exists(webExe);

        var webDataDir = Path.Combine(tempDir, "web_http_sales");
        Directory.CreateDirectory(webDataDir);
        var dbPath = Path.Combine(webDataDir, "clothingstore_db.json");

        var rootWebRepo = new ClothingStoreRepository(hasher, dbPath);
        var rootSettings = await rootWebRepo.GetStoreSettingsAsync();
        rootSettings.BasicPlanMonthlyPrice = 49.90m;
        rootSettings.IntermediaryPlanMonthlyPrice = 89.90m;
        rootSettings.PremiumPlanMonthlyPrice = 129.90m;
        await rootWebRepo.UpdateStoreSettingsAsync(rootSettings);

        var port = ReserveFreeLoopbackPort();
        var baseUrl = $"http://127.0.0.1:{port}";

        var psi = new ProcessStartInfo
        {
            FileName = useExe ? webExe : "dotnet",
            Arguments = useExe ? string.Empty : $"\"{webDll}\"",
            WorkingDirectory = webBinDir!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.Environment["ASPNETCORE_URLS"] = baseUrl;
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["Database__FilePath"] = dbPath;
        psi.Environment["YOULINK_PORTAL_HOME"] = "true";
        psi.Environment["YOULINK_SYNC_SECRET"] = "sales-loopback-secret";
        psi.Environment["DOTNET_PRINT_TELEMETRY_MESSAGE"] = "false";

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Falha ao subir ClothingStore.Web para teste HTTP de vendas.");

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(5) };

            HttpResponseMessage? health = null;
            for (var i = 0; i < 60; i++)
            {
                if (process.HasExited)
                {
                    var stdout = await process.StandardOutput.ReadToEndAsync();
                    var stderr = await process.StandardError.ReadToEndAsync();
                    throw new InvalidOperationException($"ClothingStore.Web encerrou ({process.ExitCode}). {stdout}\n{stderr}");
                }

                try
                {
                    health = await http.GetAsync("/health");
                    if (health.IsSuccessStatusCode)
                        break;
                }
                catch
                {
                }
                await Task.Delay(150);
            }

            Assert.IsNotNull(health);
            Assert.AreEqual(HttpStatusCode.OK, health!.StatusCode);

            // A) Publica loja ativa via HTTP POST /api/cloud/publish-store
            var activeReq = new HttpRequestMessage(HttpMethod.Post, "/api/cloud/publish-store?storeSlug=loja-venda-viva")
            {
                Content = JsonContent.Create(new CloudCatalogSyncPayload
                {
                    Settings = new UpdateStoreSettingsRequest
                    {
                        StoreName = "Loja Venda Viva HTTP",
                        StoreSlug = "loja-venda-viva",
                        WhatsappNumber = "5511999993333",
                        StoreDescription = "Vitrine HTTP ao vivo",
                        IsSiteActive = true,
                        PlanTier = StoreSettings.PlanTierIntermediate,
                        DirectPixSalesEnabled = true,
                        PixGatewayMode = StoreSettings.PixGatewayDirect,
                        StorePixKey = "vendaviva@pix.com.br",
                        StorePixReceiverName = "Loja Venda Viva",
                        OriginCep = "01310-100",
                        OriginCity = "São Paulo",
                        OriginState = "SP",
                        ServerHostingMode = "PlatformShared"
                    },
                    Categories = [new SaveCategoryRequest { Name = "Moda", DisplayOrder = 1, IsActive = true }],
                    CatalogSnapshot =
                    [
                        new CloudProductSyncDto
                        {
                            SourceId = Guid.NewGuid(),
                            CategoryName = "Moda",
                            CategorySlug = "moda",
                            Name = "Blazer Executivo HTTP",
                            Slug = "blazer-executivo-http",
                            Description = "Corte impecável",
                            BasePrice = 249.90m,
                            IsVisibleOnSite = true,
                            Variants =
                            [
                                new CloudProductVariantSyncDto
                                {
                                    Color = "Azul",
                                    ColorHex = "#1E3A8A",
                                    Size = "G",
                                    Sku = "BLZ-HTTP-G",
                                    StockQuantity = 8,
                                    LowStockThreshold = 2,
                                    IsActive = true
                                }
                            ],
                            Images = []
                        }
                    ]
                })
            };
            activeReq.Headers.Add("X-YouLink-Owner-Email", "viva@youlink.com.br");
            var pubActiveRes = await http.SendAsync(activeReq);
            Assert.AreEqual(HttpStatusCode.OK, pubActiveRes.StatusCode, $"Falha ao publicar loja ativa via HTTP: {pubActiveRes.StatusCode}");
            var pubActiveDto = await pubActiveRes.Content.ReadFromJsonAsync<CloudStorePublishResponse>();
            Assert.IsNotNull(pubActiveDto);
            Assert.IsTrue(pubActiveDto!.Success);
            Assert.IsFalse(string.IsNullOrWhiteSpace(pubActiveDto.PublishToken), "Publicação de usuário deve emitir PublishToken exclusivo.");

            // Libera o plano Intermediário no servidor com chave administrativa e sincroniza vendas Pix da loja
            using (var grantVipReq = new HttpRequestMessage(HttpMethod.Post, "/api/platform-sync/apply-vip")
            {
                Content = JsonContent.Create(new RemoteApplyVipRequest(
                    PlanTier: StoreSettings.PlanTierIntermediate,
                    DurationDays: 30,
                    ActivateSite: true,
                    StoreName: "Loja Venda Viva HTTP",
                    TargetEmail: "viva@youlink.com.br"))
            })
            {
                grantVipReq.Headers.Add("X-YouLink-Sync-Key", "sales-loopback-secret");
                var grantRes = await http.SendAsync(grantVipReq);
                Assert.AreEqual(HttpStatusCode.OK, grantRes.StatusCode);
            }

            using (var enablePixReq = new HttpRequestMessage(HttpMethod.Post, "/api/cloud/publish-store?storeSlug=loja-venda-viva")
            {
                Content = JsonContent.Create(new CloudCatalogSyncPayload
                {
                    Settings = new UpdateStoreSettingsRequest
                    {
                        StoreName = "Loja Venda Viva HTTP",
                        StoreSlug = "loja-venda-viva",
                        WhatsappNumber = "5511999993333",
                        StoreDescription = "Vitrine HTTP ao vivo",
                        IsSiteActive = true,
                        DirectPixSalesEnabled = true,
                        PixGatewayMode = StoreSettings.PixGatewayDirect,
                        StorePixKey = "vendaviva@pix.com.br",
                        StorePixReceiverName = "Loja Venda Viva",
                        OriginCep = "01310-100",
                        OriginCity = "São Paulo",
                        OriginState = "SP",
                        ServerHostingMode = "PlatformShared"
                    },
                    Categories = [new SaveCategoryRequest { Name = "Moda", DisplayOrder = 1, IsActive = true }],
                    CatalogSnapshot =
                    [
                        new CloudProductSyncDto
                        {
                            SourceId = Guid.NewGuid(),
                            CategoryName = "Moda",
                            CategorySlug = "moda",
                            Name = "Blazer Executivo HTTP",
                            Slug = "blazer-executivo-http",
                            Description = "Corte impecável",
                            BasePrice = 249.90m,
                            IsVisibleOnSite = true,
                            Variants =
                            [
                                new CloudProductVariantSyncDto
                                {
                                    Color = "Azul",
                                    ColorHex = "#1E3A8A",
                                    Size = "G",
                                    Sku = "BLZ-HTTP-G",
                                    StockQuantity = 8,
                                    LowStockThreshold = 2,
                                    IsActive = true
                                }
                            ],
                            Images = []
                        }
                    ]
                })
            })
            {
                enablePixReq.Headers.Add("X-YouLink-Publish-Token", pubActiveDto.PublishToken);
                var enablePixRes = await http.SendAsync(enablePixReq);
                Assert.AreEqual(HttpStatusCode.OK, enablePixRes.StatusCode);
            }

            // Busca produto publicado para pegar ProductId e VariantId reais da loja
            var pubProd = await http.GetFromJsonAsync<ProductDetailDto>("/api/public/stores/loja-venda-viva/products/blazer-executivo-http");
            Assert.IsNotNull(pubProd);
            Assert.AreEqual(1, pubProd!.Variants.Count);
            var pubVar = pubProd.Variants[0];

            // B) Cotação real de frete via HTTP
            var quoteRes = await http.PostAsJsonAsync("/api/public/stores/loja-venda-viva/shipping-quote", new ShippingQuoteRequest
            {
                DestinationCep = "01310-200",
                WeightGrams = 500
            });
            Assert.AreEqual(HttpStatusCode.OK, quoteRes.StatusCode);
            var quoteDto = await quoteRes.Content.ReadFromJsonAsync<ShippingQuoteResponse>();
            Assert.IsNotNull(quoteDto);
            Assert.IsTrue(quoteDto!.DirectPixSalesReady);
            Assert.IsTrue(quoteDto.Eligibility.IsAllowed);

            // C) Compra pública real via HTTP POST /api/public/stores/loja-venda-viva/orders/pix
            var placeOrderRes = await http.PostAsJsonAsync("/api/public/stores/loja-venda-viva/orders/pix", new CreatePixOrderRequest
            {
                ProductId = pubProd.Id,
                ProductVariantId = pubVar.Id,
                Quantity = 1,
                CustomerName = "Comprador HTTP Real",
                CustomerWhatsapp = "11988887766",
                Cep = "01310-200",
                Street = "Av Paulista",
                Number = "1234",
                Neighborhood = "Bela Vista",
                City = "São Paulo",
                State = "SP"
            });
            Assert.AreEqual(HttpStatusCode.Created, placeOrderRes.StatusCode);
            var placedOrder = await placeOrderRes.Content.ReadFromJsonAsync<StoreOrderDto>();
            Assert.IsNotNull(placedOrder);
            Assert.AreEqual(249.90m, placedOrder!.ItemsTotal);
            Assert.IsFalse(string.IsNullOrWhiteSpace(placedOrder.PublicStatusToken));

            // D) Consulta status público do pedido via HTTP GET
            var statusRes = await http.GetAsync(
                $"/api/public/stores/loja-venda-viva/orders/{placedOrder.Id}/status?token={Uri.EscapeDataString(placedOrder.PublicStatusToken!)}");
            Assert.AreEqual(HttpStatusCode.OK, statusRes.StatusCode);
            var publicStatus = await statusRes.Content.ReadFromJsonAsync<PublicOrderStatusDto>();
            Assert.IsNotNull(publicStatus);
            Assert.AreEqual(placedOrder.OrderNumber, publicStatus!.OrderNumber);

            // E) App da vendedora puxa vendas realizadas via HTTP GET /api/cloud/orders
            var syncOrdersReq = new HttpRequestMessage(HttpMethod.Get, "/api/cloud/orders?storeSlug=loja-venda-viva");
            syncOrdersReq.Headers.Add("X-YouLink-Publish-Token", pubActiveDto.PublishToken);
            var syncOrdersRes = await http.SendAsync(syncOrdersReq);
            Assert.AreEqual(HttpStatusCode.OK, syncOrdersRes.StatusCode);
            var syncOrdersDto = await syncOrdersRes.Content.ReadFromJsonAsync<CloudSyncOrdersResponse>();
            Assert.IsNotNull(syncOrdersDto);
            Assert.AreEqual(1, syncOrdersDto!.Orders.Count);
            Assert.AreEqual(placedOrder.OrderNumber, syncOrdersDto.Orders[0].OrderNumber);

            // F) Publica loja ativa primeiro para capturar ID, depois PAUSA (IsSiteActive = false) -> compra HTTP é bloqueada com BadRequest!
            var pausedSourceId = Guid.NewGuid();
            CloudCatalogSyncPayload BuildPausedPayload(bool siteActive) => new()
            {
                Settings = new UpdateStoreSettingsRequest
                {
                    StoreName = "Loja Pausada HTTP",
                    StoreSlug = "loja-pausada-http",
                    WhatsappNumber = "5511999994444",
                    StoreDescription = "Loja pausada",
                    IsSiteActive = siteActive,
                    PlanTier = StoreSettings.PlanTierIntermediate,
                    DirectPixSalesEnabled = true,
                    PixGatewayMode = StoreSettings.PixGatewayDirect,
                    StorePixKey = "pausada@pix.com.br",
                    StorePixReceiverName = "Loja Pausada",
                    OriginCep = "01310-100",
                    OriginCity = "São Paulo",
                    OriginState = "SP",
                    ServerHostingMode = "PlatformShared"
                },
                Categories = [new SaveCategoryRequest { Name = "Moda", DisplayOrder = 1, IsActive = true }],
                CatalogSnapshot =
                [
                    new CloudProductSyncDto
                    {
                        SourceId = pausedSourceId,
                        CategoryName = "Moda",
                        CategorySlug = "moda",
                        Name = "Blusa Pausada",
                        Slug = "blusa-pausada",
                        Description = "Indisponível",
                        BasePrice = 99.90m,
                        IsVisibleOnSite = true,
                        Variants =
                        [
                            new CloudProductVariantSyncDto
                            {
                                Color = "Preto",
                                ColorHex = "#000000",
                                Size = "M",
                                Sku = "PAUSA-M",
                                StockQuantity = 3,
                                LowStockThreshold = 1,
                                IsActive = true
                            }
                        ],
                        Images = []
                    }
                ]
            };

            var pubPausedWarm = new HttpRequestMessage(HttpMethod.Post, "/api/cloud/publish-store?storeSlug=loja-pausada-http") { Content = JsonContent.Create(BuildPausedPayload(true)) };
            pubPausedWarm.Headers.Add("X-YouLink-Owner-Email", "pausada@youlink.com.br");
            var warmRes = await http.SendAsync(pubPausedWarm);
            Assert.AreEqual(HttpStatusCode.OK, warmRes.StatusCode, $"Falha ao preparar loja-pausada-http: {warmRes.StatusCode}");
            var warmDto = await warmRes.Content.ReadFromJsonAsync<CloudStorePublishResponse>();

            using (var grantPausedVip = new HttpRequestMessage(HttpMethod.Post, "/api/platform-sync/apply-vip")
            {
                Content = JsonContent.Create(new RemoteApplyVipRequest(
                    PlanTier: StoreSettings.PlanTierIntermediate,
                    DurationDays: 30,
                    ActivateSite: true,
                    StoreName: "Loja Pausada HTTP",
                    TargetEmail: "pausada@youlink.com.br"))
            })
            {
                grantPausedVip.Headers.Add("X-YouLink-Sync-Key", "sales-loopback-secret");
                var gvRes = await http.SendAsync(grantPausedVip);
                Assert.AreEqual(HttpStatusCode.OK, gvRes.StatusCode);
            }
            var pausedProd = await http.GetFromJsonAsync<ProductDetailDto>("/api/public/stores/loja-pausada-http/products/blusa-pausada");
            Assert.IsNotNull(pausedProd);

            var pausedReq = new HttpRequestMessage(HttpMethod.Post, "/api/cloud/publish-store?storeSlug=loja-pausada-http") { Content = JsonContent.Create(BuildPausedPayload(false)) };
            pausedReq.Headers.Add("X-YouLink-Publish-Token", warmDto!.PublishToken);
            pausedReq.Headers.Add("X-YouLink-Owner-Email", "pausada@youlink.com.br");
            var pubPausedRes = await http.SendAsync(pausedReq);
            Assert.AreEqual(HttpStatusCode.OK, pubPausedRes.StatusCode, $"Falha ao pausar loja-pausada-http: {pubPausedRes.StatusCode}");

            var buyPausedRes = await http.PostAsJsonAsync("/api/public/stores/loja-pausada-http/orders/pix", new CreatePixOrderRequest
            {
                ProductId = pausedProd!.Id,
                ProductVariantId = pausedProd.Variants[0].Id,
                Quantity = 1,
                CustomerName = "Tentativa Loja Pausada",
                CustomerWhatsapp = "11977776666",
                Cep = "01310-200",
                Street = "Rua Teste",
                Number = "1",
                Neighborhood = "Centro",
                City = "São Paulo",
                State = "SP"
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, buyPausedRes.StatusCode,
                "Loja desativada/pausada precisa bloquear novas compras na vitrine HTTP.");
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }
    }
}
