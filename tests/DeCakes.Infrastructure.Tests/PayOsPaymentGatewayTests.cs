using System.Net;
using System.Text.Json;
using DeCakes.Application.Subscriptions;
using DeCakes.Infrastructure.Subscriptions;
using Microsoft.Extensions.Configuration;

namespace DeCakes.Infrastructure.Tests;

/// <summary>
/// Adapter payOS với HttpMessageHandler giả, không gọi mạng. Chữ ký kỳ vọng được tính độc lập bằng openssl
/// (`printf '%s' '&lt;chuỗi&gt;' | openssl dgst -sha256 -hmac 'checksum-key-test-only'`) từ quy tắc trong tài liệu payOS,
/// không bằng mã của adapter. Không chứng minh tài khoản merchant payOS thật hoạt động.
/// </summary>
public class PayOsPaymentGatewayTests
{
    private const string ChecksumKey = "checksum-key-test-only";

    // amount=199000&cancelUrl=https://app.example/cancel&description=DC1234567&orderCode=91234567&returnUrl=https://app.example/return
    private const string ExpectedRequestSignature = "b89411a600912e76c0565e69d83f7ff5bb25359d9779665409d21537dc9b0f58";

    // data sắp xếp theo khóa; null và "" đều thành rỗng; số giữ nguyên.
    private const string ExpectedWebhookSignature = "ea483277e6b4a7fb72f3ed31b5202e29c3407277e5eca51cc8453cc26901dd61";

    private static IConfiguration Config(bool enabled = true, string? clientId = "client-test", string? returnUrl = "https://app.example/return") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payments:PayOS:Enabled"] = enabled ? "true" : "false",
            ["Payments:PayOS:ClientId"] = clientId,
            ["Payments:PayOS:ApiKey"] = "api-key-test-only",
            ["Payments:PayOS:ChecksumKey"] = ChecksumKey,
            ["Payments:PayOS:ReturnUrl"] = returnUrl,
            ["Payments:PayOS:CancelUrl"] = "https://app.example/cancel",
        }).Build();

    private static string WebhookBody(string signature = ExpectedWebhookSignature, long amount = 199000, bool success = true, string code = "00") => $$"""
        {
          "code": "{{code}}",
          "desc": "success",
          "success": {{(success ? "true" : "false")}},
          "data": {
            "orderCode": 91234567,
            "amount": {{amount}},
            "description": "DC1234567",
            "accountNumber": "12345678",
            "reference": "TF230204212323",
            "transactionDateTime": "2023-02-04 18:25:00",
            "currency": "VND",
            "paymentLinkId": "124c33293c43417ab7879e14c8d9eb18",
            "code": "00",
            "desc": "success",
            "counterAccountBankId": null,
            "counterAccountBankName": "",
            "counterAccountName": null,
            "counterAccountNumber": "",
            "virtualAccountName": "",
            "virtualAccountNumber": null
          },
          "signature": "{{signature}}"
        }
        """;

    [Fact]
    public void Disabled_or_incomplete_configuration_is_not_enabled()
    {
        Assert.True(new PayOsPaymentGateway(Config()).IsEnabled);
        Assert.False(new PayOsPaymentGateway(new ConfigurationBuilder().Build()).IsEnabled);
        Assert.False(new PayOsPaymentGateway(Config(enabled: false)).IsEnabled);
        Assert.False(new PayOsPaymentGateway(Config(clientId: "<đặt bằng user-secrets>")).IsEnabled);
        Assert.False(new PayOsPaymentGateway(Config(returnUrl: "http://app.example/return")).IsEnabled);
    }

    [Fact]
    public async Task Create_link_sends_documented_signature_and_credentials_headers()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """
            {"code":"00","desc":"success","data":{"orderCode":91234567,"amount":199000,"paymentLinkId":"plink-1","checkoutUrl":"https://pay.payos.vn/web/plink-1","status":"PENDING"},"signature":"x"}
            """);
        var gateway = new PayOsPaymentGateway(Config(), new HttpClient(handler));

        var link = await gateway.CreatePaymentLinkAsync(
            new PaymentLinkRequest(91234567, 199000, "DC1234567", new DateTime(2026, 10, 23, 2, 0, 0, DateTimeKind.Utc)), CancellationToken.None);

        Assert.Equal(new PaymentLink("plink-1", "https://pay.payos.vn/web/plink-1"), link);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api-merchant.payos.vn/v2/payment-requests", request.Uri);
        Assert.Equal(("client-test", "api-key-test-only"), (request.ClientId, request.ApiKey));
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(ExpectedRequestSignature, body.RootElement.GetProperty("signature").GetString());
        Assert.Equal(1792720800, body.RootElement.GetProperty("expiredAt").GetInt64());
        Assert.Equal(199000, body.RootElement.GetProperty("amount").GetInt64());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"code":"20","desc":"error","data":null}""")]
    [InlineData(HttpStatusCode.OK, """{"code":"00","data":{"orderCode":91234567,"amount":1,"paymentLinkId":"p","checkoutUrl":"https://pay.example/p"}}""")]
    [InlineData(HttpStatusCode.OK, """{"code":"00","data":{"orderCode":91234567,"amount":199000,"paymentLinkId":"p","checkoutUrl":"http://pay.example/p"}}""")]
    [InlineData(HttpStatusCode.OK, "khong-phai-json")]
    [InlineData(HttpStatusCode.Unauthorized, """{"code":"401","desc":"secret-detail api-key-test-only"}""")]
    public async Task Provider_errors_or_mismatched_responses_raise_gateway_exception_without_secrets(HttpStatusCode status, string body)
    {
        var gateway = new PayOsPaymentGateway(Config(), new HttpClient(new CapturingHandler(status, body)));

        var error = await Assert.ThrowsAsync<PaymentGatewayException>(() => gateway.CreatePaymentLinkAsync(
            new PaymentLinkRequest(91234567, 199000, "DC1234567", DateTime.UtcNow.AddHours(1)), CancellationToken.None));

        Assert.DoesNotContain("api-key-test-only", error.Message);
        Assert.DoesNotContain(ChecksumKey, error.Message);
        Assert.DoesNotContain("secret-detail", error.Message);
    }

    [Fact]
    public async Task Disabled_gateway_never_calls_the_network()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{}");
        var gateway = new PayOsPaymentGateway(Config(enabled: false), new HttpClient(handler));

        await Assert.ThrowsAsync<PaymentGatewayException>(() => gateway.CreatePaymentLinkAsync(
            new PaymentLinkRequest(1, 1, "DC1", DateTime.UtcNow), CancellationToken.None));
        Assert.Equal(WebhookVerificationStatus.NotConfigured, gateway.VerifyWebhook(WebhookBody()).Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Webhook_with_documented_signature_is_verified_as_paid()
    {
        var gateway = new PayOsPaymentGateway(Config());

        var result = gateway.VerifyWebhook(WebhookBody());
        var upper = gateway.VerifyWebhook(WebhookBody(ExpectedWebhookSignature.ToUpperInvariant()));

        Assert.Equal(WebhookVerificationStatus.Paid, result.Status);
        Assert.Equal(new VerifiedPaymentNotice("payos", 91234567, 199000m, "VND", "124c33293c43417ab7879e14c8d9eb18", "TF230204212323"), result.Notice);
        Assert.Equal(WebhookVerificationStatus.Paid, upper.Status);
    }

    [Fact]
    public void Tampered_or_unsigned_webhooks_are_rejected()
    {
        var gateway = new PayOsPaymentGateway(Config());

        // Đổi số tiền mà giữ chữ ký cũ.
        Assert.Equal(WebhookVerificationStatus.InvalidSignature, gateway.VerifyWebhook(WebhookBody(amount: 1)).Status);
        Assert.Equal(WebhookVerificationStatus.InvalidSignature, gateway.VerifyWebhook(WebhookBody(signature: new string('0', 64))).Status);
        Assert.Equal(WebhookVerificationStatus.InvalidSignature, gateway.VerifyWebhook(WebhookBody(signature: "")).Status);
        Assert.Equal(WebhookVerificationStatus.InvalidSignature, gateway.VerifyWebhook("{\"data\":{}}").Status);
        Assert.Equal(WebhookVerificationStatus.InvalidSignature, gateway.VerifyWebhook("khong-phai-json").Status);
        Assert.Equal(WebhookVerificationStatus.InvalidSignature, gateway.VerifyWebhook("[]").Status);
    }

    [Fact]
    public void Signed_but_unsuccessful_notification_does_not_count_as_paid()
    {
        var gateway = new PayOsPaymentGateway(Config());

        // Phần top-level (code/success) không nằm trong chữ ký, nhưng vẫn phải báo thành công mới coi là đã trả.
        Assert.Equal(WebhookVerificationStatus.NotPaid, gateway.VerifyWebhook(WebhookBody(success: false)).Status);
        Assert.Equal(WebhookVerificationStatus.NotPaid, gateway.VerifyWebhook(WebhookBody(code: "01")).Status);
    }

    private sealed class CapturingHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public List<(string Uri, string? ClientId, string? ApiKey, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(),
                request.Headers.TryGetValues("x-client-id", out var id) ? id.Single() : null,
                request.Headers.TryGetValues("x-api-key", out var key) ? key.Single() : null,
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
        }
    }
}
