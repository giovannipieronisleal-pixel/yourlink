using ClothingStore.Infrastructure.Persistence;
using ClothingStore.Infrastructure.Security;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClothingStore.UnitTests;

[TestClass]
public sealed class InAppChatAndBuyerAuthTests
{
    [TestMethod]
    public void FeatureFlags_SalesEnabled_And_PhoneDisabled()
    {
        Assert.IsTrue(CatalogFeatureFlags.SalesAndOrdersEnabled,
            "As compras via Pix e Mercado Pago devem estar habilitadas.");
        Assert.IsFalse(CatalogFeatureFlags.PhoneFeaturesEnabled,
            "Todas as funções de telefone e WhatsApp devem permanecer desativadas.");
    }

    [TestMethod]
    public async Task BuyerAuth_Register_Login_DuplicateCheck_And_TokenValidation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "youlink_chat_auth_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new CloudChatStoreService(tempDir, new Pbkdf2PasswordHasher());

            var reg = await store.RegisterBuyerAsync(new RegisterBuyerAccountRequest(
                FullName: "Marina Costa",
                Email: "Marina.Costa@Teste.com",
                Password: "senha123"));

            Assert.IsTrue(reg.Success);
            Assert.IsNotNull(reg.Session);
            Assert.AreEqual("Marina Costa", reg.Session!.FullName);
            Assert.IsFalse(reg.Session.MaskedEmail.Contains("Marina.Costa", StringComparison.OrdinalIgnoreCase),
                "O e-mail retornado na sessão deve estar mascarado.");

            var dup = await store.RegisterBuyerAsync(new RegisterBuyerAccountRequest(
                FullName: "Outra Marina",
                Email: "marina.costa@teste.com",
                Password: "outra123"));
            Assert.IsFalse(dup.Success, "Não deve permitir duas contas de comprador com o mesmo e-mail.");

            var badLogin = await store.LoginBuyerAsync(new BuyerLoginRequest("marina.costa@teste.com", "errada"));
            Assert.IsFalse(badLogin.Success);

            var okLogin = await store.LoginBuyerAsync(new BuyerLoginRequest("marina.costa@teste.com", "senha123"));
            Assert.IsTrue(okLogin.Success);
            Assert.IsNotNull(okLogin.Session);

            var validated = await store.ValidateBuyerTokenAsync(okLogin.Session!.SessionToken);
            Assert.IsNotNull(validated);
            Assert.AreEqual(reg.Session.BuyerId, validated!.BuyerId);

            var invalidToken = await store.ValidateBuyerTokenAsync("token-invalido-123");
            Assert.IsNull(invalidToken);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task StoreChat_BuyerAndMerchant_Exchange_And_DeleteForMe_Isolation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "youlink_store_chat_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new CloudChatStoreService(tempDir, new Pbkdf2PasswordHasher());
            var buyerReg = await store.RegisterBuyerAsync(new RegisterBuyerAccountRequest(
                "LucasComprador",
                "lucas@cliente.com",
                "123456"));
            var buyerId = buyerReg.Session!.BuyerId;

            var afterBuyerMsg = await store.SendBuyerStoreMessageAsync(
                buyerId: buyerId,
                storeSlug: "loja-estilo",
                storeName: "Loja Estilo",
                text: "Olá! Paguei o pedido via Pix, tamanho M.",
                contextBadge: "Pedido #ABC123");

            Assert.IsNotNull(afterBuyerMsg);
            Assert.AreEqual(1, afterBuyerMsg!.Messages.Count);
            Assert.IsTrue(afterBuyerMsg.Messages[0].IsMine);
            Assert.AreEqual("Pedido #ABC123", afterBuyerMsg.Messages[0].ContextBadge);

            var merchantThreads = await store.GetMerchantStoreThreadsAsync("loja-estilo");
            Assert.AreEqual(1, merchantThreads.Count);
            Assert.AreEqual(1, merchantThreads[0].UnreadCount);

            var threadId = merchantThreads[0].ThreadId;
            var merchantView = await store.GetMerchantStoreThreadDetailAsync("loja-estilo", threadId);
            Assert.IsNotNull(merchantView);
            Assert.AreEqual(1, merchantView!.Messages.Count);
            Assert.IsFalse(merchantView.Messages[0].IsMine);

            var afterReply = await store.SendMerchantStoreReplyAsync(
                storeSlug: "loja-estilo",
                storeName: "Loja Estilo",
                threadId: threadId,
                text: "Pagamento confirmado! Vamos separar sua peça hoje.",
                contextBadge: null);

            Assert.IsNotNull(afterReply);
            Assert.AreEqual(2, afterReply!.Messages.Count);

            var firstMsgId = afterReply.Messages[0].Id;
            var secondMsgId = afterReply.Messages[1].Id;

            // Comprador apaga a 1ª mensagem SOMENTE para ele ("Apagar para mim")
            var deletedForBuyer = await store.DeleteMessageForActorAsync(
                threadId,
                firstMsgId,
                CloudChatStoreService.BuildBuyerActorKey(buyerId));
            Assert.IsTrue(deletedForBuyer);

            var buyerViewAfterDelete = await store.GetOrCreateBuyerStoreThreadAsync(buyerId, "loja-estilo", "Loja Estilo");
            Assert.IsNotNull(buyerViewAfterDelete);
            Assert.AreEqual(1, buyerViewAfterDelete!.Messages.Count,
                "Para o comprador que apagou, apenas 1 mensagem deve continuar visível.");
            Assert.AreEqual(secondMsgId, buyerViewAfterDelete.Messages[0].Id);

            var merchantViewUntouched = await store.GetMerchantStoreThreadDetailAsync("loja-estilo", threadId);
            Assert.IsNotNull(merchantViewUntouched);
            Assert.AreEqual(2, merchantViewUntouched!.Messages.Count,
                "Para o lojista, as 2 mensagens devem continuar intactas (Apagar para mim isolado).");

            // Lojista apaga a 2ª mensagem SOMENTE para ele
            var deletedForMerchant = await store.DeleteMessageForActorAsync(
                threadId,
                secondMsgId,
                CloudChatStoreService.BuildMerchantStoreActorKey("loja-estilo"));
            Assert.IsTrue(deletedForMerchant);

            var merchantViewAfterOwnDelete = await store.GetMerchantStoreThreadDetailAsync("loja-estilo", threadId);
            Assert.AreEqual(1, merchantViewAfterOwnDelete!.Messages.Count);
            Assert.AreEqual(firstMsgId, merchantViewAfterOwnDelete.Messages[0].Id);

            var buyerViewStillHasSecond = await store.GetOrCreateBuyerStoreThreadAsync(buyerId, "loja-estilo", "Loja Estilo");
            Assert.AreEqual(1, buyerViewStillHasSecond!.Messages.Count);
            Assert.AreEqual(secondMsgId, buyerViewStillHasSecond.Messages[0].Id);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task SupportChat_MerchantAndPlatform_Exchange_And_DeleteForMe_Isolation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "youlink_support_chat_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new CloudChatStoreService(tempDir, new Pbkdf2PasswordHasher());

            var sent = await store.SendMerchantSupportMessageAsync(
                accountKey: "lojista@teste.com",
                merchantHandle: "@minhaloja",
                storeSlug: "minha-loja",
                storeName: "Minha Loja VIP",
                text: "Preciso de ajuda para configurar o Mercado Pago.",
                contextBadge: null);

            Assert.IsNotNull(sent);
            var threadId = sent!.Summary.ThreadId;

            var supportList = await store.GetPlatformSupportThreadsAsync();
            Assert.AreEqual(1, supportList.Count);
            Assert.AreEqual(1, supportList[0].UnreadCount);

            var afterOwnerReply = await store.SendPlatformSupportReplyAsync(
                threadId: threadId,
                text: "Olá! Basta colar o Access Token na aba Pagamentos.",
                senderLabel: "Suporte YouLink");

            Assert.IsNotNull(afterOwnerReply);
            Assert.AreEqual(2, afterOwnerReply!.Messages.Count);

            var msgToHideForOwner = afterOwnerReply.Messages[0].Id;
            var deleted = await store.DeleteMessageForActorAsync(
                threadId,
                msgToHideForOwner,
                CloudChatStoreService.PlatformOwnerActorKey);
            Assert.IsTrue(deleted);

            var ownerDetail = await store.GetPlatformSupportThreadDetailAsync(threadId);
            Assert.AreEqual(1, ownerDetail!.Messages.Count, "A mensagem apagada para o suporte não deve aparecer para o suporte.");

            var merchantDetail = await store.GetOrCreateMerchantSupportThreadAsync(
                "lojista@teste.com",
                "@minhaloja",
                "minha-loja",
                "Minha Loja VIP");
            Assert.AreEqual(2, merchantDetail.Messages.Count, "O lojista continua vendo as 2 mensagens normalmente.");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
