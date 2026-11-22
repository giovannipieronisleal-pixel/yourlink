using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClothingStore.Application.Services;
using ClothingStore.Shared.DTOs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClothingStore.UnitTests;

[TestClass]
public class EmailOtpTests
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public string ResponseJson { get; set; } = "{\"id\":\"msg_test\"}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(ResponseJson, Encoding.UTF8, "application/json")
            };
        }
    }

    [TestMethod]
    public void NormalizeAndMaskEmail_ShouldValidateAndProtectAddresses()
    {
        Assert.AreEqual("lojista@exemplo.com", EmailOtpService.NormalizeEmail("  Lojista@Exemplo.COM  "));
        Assert.AreEqual("lo***@exemplo.com", EmailOtpService.MaskEmail("lojista@exemplo.com"));
        Assert.AreEqual(string.Empty, EmailOtpService.NormalizeEmail("invalido"));
        Assert.AreEqual(string.Empty, EmailOtpService.NormalizeEmail("a@b"));
        Assert.AreEqual(string.Empty, EmailOtpService.NormalizeEmail("com espaco@exemplo.com"));
    }

    [TestMethod]
    public async Task SendAndVerifyOtp_ViaResend_ShouldEnforceCooldownAndSingleUse()
    {
        var handler = new StubHttpMessageHandler();
        var now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

        var service = new EmailOtpService(
            new HttpClient(handler),
            configProvider: () => new EmailOtpConfig(
                ResendApiKey: "re_synthetic_key",
                BrevoApiKey: null,
                FromEmail: "noreply@exemplo.com",
                FromName: "YouLink",
                SmtpHost: null,
                SmtpPort: 587,
                SmtpUsername: null,
                SmtpPassword: null),
            clock: () => now);

        Assert.IsTrue(service.IsConfigured);
        Assert.AreEqual("Resend", service.GetPublicStatus().Provider);

        var sendResp = await service.SendOtpAsync(new SendEmailOtpRequest("lojista@exemplo.com", "store_verification", "Loja Modelo"));
        Assert.IsTrue(sendResp.Sent);
        Assert.IsTrue(sendResp.ProviderConfigured);
        Assert.AreEqual("lo***@exemplo.com", sendResp.MaskedEmail);
        Assert.IsNotNull(handler.LastRequest);
        Assert.AreEqual("https://api.resend.com/emails", handler.LastRequest!.RequestUri!.ToString());
        Assert.AreEqual("Bearer", handler.LastRequest.Headers.Authorization?.Scheme);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var subject = doc.RootElement.GetProperty("subject").GetString()!;
        var match = Regex.Match(subject, @"\b(\d{6})\b");
        Assert.IsTrue(match.Success, "O assunto do e-mail deve conter o código de 6 dígitos.");
        var code = match.Groups[1].Value;

        // Cooldown impede reenvio imediato
        var resendResp = await service.SendOtpAsync(new SendEmailOtpRequest("lojista@exemplo.com", "store_verification"));
        Assert.IsFalse(resendResp.Sent);
        Assert.IsTrue(resendResp.ResendAfterSeconds > 0);

        // Código errado falha
        var wrong = service.VerifyOtp(new VerifyEmailOtpRequest("lojista@exemplo.com", "000000", "store_verification"));
        Assert.IsFalse(wrong.Verified);

        // Código correto confirma uma vez
        var ok = service.VerifyOtp(new VerifyEmailOtpRequest("lojista@exemplo.com", code, "store_verification"));
        Assert.IsTrue(ok.Verified);

        // Uso único: tentar de novo falha
        var replay = service.VerifyOtp(new VerifyEmailOtpRequest("lojista@exemplo.com", code, "store_verification"));
        Assert.IsFalse(replay.Verified);
    }

    [TestMethod]
    public async Task SendOtp_ViaBrevo_AndWhenUnconfigured_ShouldWorkSafely()
    {
        var handler = new StubHttpMessageHandler();
        var brevoService = new EmailOtpService(
            new HttpClient(handler),
            configProvider: () => new EmailOtpConfig(
                ResendApiKey: null,
                BrevoApiKey: "xkeysib-synthetic",
                FromEmail: "noreply@exemplo.com",
                FromName: "YouLink",
                SmtpHost: null,
                SmtpPort: 587,
                SmtpUsername: null,
                SmtpPassword: null));

        Assert.AreEqual("Brevo", brevoService.GetPublicStatus().Provider);
        var sent = await brevoService.SendOtpAsync(new SendEmailOtpRequest("cliente@exemplo.com"));
        Assert.IsTrue(sent.Sent);
        Assert.AreEqual("https://api.brevo.com/v3/smtp/email", handler.LastRequest!.RequestUri!.ToString());

        var unconfiguredHandler = new StubHttpMessageHandler();
        var unconfigured = new EmailOtpService(
            new HttpClient(unconfiguredHandler),
            configProvider: () => new EmailOtpConfig(null, null, null, "YouLink", null, 587, null, null));

        Assert.IsFalse(unconfigured.IsConfigured);
        var failed = await unconfigured.SendOtpAsync(new SendEmailOtpRequest("cliente@exemplo.com"));
        Assert.IsFalse(failed.Sent);
        Assert.IsFalse(failed.ProviderConfigured);
        Assert.IsNull(unconfiguredHandler.LastRequest);
    }
}
