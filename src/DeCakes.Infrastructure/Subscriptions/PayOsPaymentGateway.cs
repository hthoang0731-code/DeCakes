using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeCakes.Application.Subscriptions;
using Microsoft.Extensions.Configuration;

namespace DeCakes.Infrastructure.Subscriptions;

/// <summary>
/// Adapter payOS bằng HttpClient của BCL (không thêm SDK), theo tài liệu chính thức:
///   - Tạo link: POST https://api-merchant.payos.vn/v2/payment-requests, header x-client-id / x-api-key, body có
///     signature = HMAC-SHA256(checksum key, "amount=..&amp;cancelUrl=..&amp;description=..&amp;orderCode=..&amp;returnUrl=..") dạng hex.
///   - Webhook: chỉ ký đối tượng data; khóa sắp xếp theo alphabet, nối "key=value" bằng '&amp;', null thành chuỗi rỗng,
///     HMAC-SHA256 hex với checksum key (https://payos.vn/docs/tich-hop-webhook/kiem-tra-du-lieu-voi-signature/).
/// TẮT mặc định: chỉ bật khi Payments:PayOS:Enabled = true và đủ ClientId, ApiKey, ChecksumKey, ReturnUrl, CancelUrl (HTTPS).
/// Cấu hình được đọc khi dùng, không ở lúc khởi động. Không ghi khóa, chữ ký hay nội dung phản hồi vào exception/log.
/// Chưa kiểm chứng với tài khoản merchant payOS thật.
/// </summary>
public sealed class PayOsPaymentGateway : ISubscriptionPaymentGateway
{
    public const string ProviderName = "payos";
    private static readonly Uri Endpoint = new("https://api-merchant.payos.vn/v2/payment-requests");

    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private readonly IConfiguration _configuration;
    private readonly HttpClient _client;

    public PayOsPaymentGateway(IConfiguration configuration) : this(configuration, SharedClient) { }

    internal PayOsPaymentGateway(IConfiguration configuration, HttpClient client)
    {
        _configuration = configuration;
        _client = client;
    }

    public string Provider => ProviderName;

    public bool IsEnabled => TryLoadSettings(out _);

    public async Task<PaymentLink> CreatePaymentLinkAsync(PaymentLinkRequest request, CancellationToken cancellationToken)
    {
        if (!TryLoadSettings(out var settings))
        {
            throw new PaymentGatewayException("Cổng thanh toán chưa được cấu hình.");
        }

        var signed = string.Create(CultureInfo.InvariantCulture,
            $"amount={request.Amount}&cancelUrl={settings.CancelUrl}&description={request.Description}&orderCode={request.OrderCode}&returnUrl={settings.ReturnUrl}");
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new
            {
                orderCode = request.OrderCode,
                amount = request.Amount,
                description = request.Description,
                cancelUrl = settings.CancelUrl,
                returnUrl = settings.ReturnUrl,
                expiredAt = new DateTimeOffset(request.ExpiresAt, TimeSpan.Zero).ToUnixTimeSeconds(),
                signature = Sign(settings.ChecksumKey, signed),
            }),
        };
        message.Headers.Add("x-client-id", settings.ClientId);
        message.Headers.Add("x-api-key", settings.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new PaymentGatewayException("Không kết nối được cổng thanh toán.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new PaymentGatewayException($"Cổng thanh toán từ chối yêu cầu (HTTP {(int)response.StatusCode}).");
            }

            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var root = json.RootElement;
                var data = root.GetProperty("data");
                // Chỉ chấp nhận khi nhà cung cấp xác nhận đúng mã đơn và số tiền đã gửi.
                if (root.GetProperty("code").GetString() != "00"
                    || data.GetProperty("orderCode").GetInt64() != request.OrderCode
                    || data.GetProperty("amount").GetInt64() != request.Amount)
                {
                    throw new PaymentGatewayException("Cổng thanh toán trả kết quả không khớp yêu cầu.");
                }

                var checkoutUrl = data.GetProperty("checkoutUrl").GetString();
                var linkId = data.GetProperty("paymentLinkId").GetString();
                if (string.IsNullOrEmpty(linkId) || !Uri.TryCreate(checkoutUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
                {
                    throw new PaymentGatewayException("Cổng thanh toán trả link không hợp lệ.");
                }

                return new PaymentLink(linkId, checkoutUrl!);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new PaymentGatewayException("Cổng thanh toán trả dữ liệu không đọc được.", ex);
            }
        }
    }

    public WebhookVerification VerifyWebhook(string body)
    {
        if (!TryLoadSettings(out var settings))
        {
            return new WebhookVerification(WebhookVerificationStatus.NotConfigured);
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("signature", out var signatureElement) || signatureElement.ValueKind != JsonValueKind.String)
            {
                return new WebhookVerification(WebhookVerificationStatus.InvalidSignature);
            }

            var expected = Encoding.ASCII.GetBytes(Sign(settings.ChecksumKey, CanonicalData(data)));
            var actual = Encoding.ASCII.GetBytes(signatureElement.GetString()!.ToLowerInvariant());
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                return new WebhookVerification(WebhookVerificationStatus.InvalidSignature);
            }

            // Từ đây dữ liệu đã được nhà cung cấp ký.
            var paid = root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True
                       && String(root, "code") == "00" && String(data, "code") == "00";
            if (!data.TryGetProperty("orderCode", out var orderCode) || !orderCode.TryGetInt64(out var code)
                || !data.TryGetProperty("amount", out var amountElement) || !amountElement.TryGetDecimal(out var amount))
            {
                return new WebhookVerification(WebhookVerificationStatus.NotPaid);
            }

            var notice = new VerifiedPaymentNotice(
                ProviderName, code, amount, String(data, "currency"), String(data, "paymentLinkId"), String(data, "reference"));
            return new WebhookVerification(paid ? WebhookVerificationStatus.Paid : WebhookVerificationStatus.NotPaid, notice);
        }
        catch (JsonException)
        {
            return new WebhookVerification(WebhookVerificationStatus.InvalidSignature);
        }
    }

    /// <summary>Chuỗi ký của đối tượng data theo quy tắc payOS cho webhook link thanh toán.</summary>
    internal static string CanonicalData(JsonElement data) =>
        string.Join('&', data.EnumerateObject()
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => p.Name + "=" + CanonicalValue(p.Value)));

    internal static string Sign(string key, string payload) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(payload)));

    private static string CanonicalValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.String => value.GetString() is "null" or "undefined" ? "" : value.GetString()!,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        // Số giữ nguyên dạng văn bản nhận được; mảng/đối tượng dạng JSON (webhook link thanh toán hiện không có).
        _ => value.GetRawText(),
    };

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record Settings(string ClientId, string ApiKey, string ChecksumKey, string ReturnUrl, string CancelUrl);

    private bool TryLoadSettings(out Settings settings)
    {
        settings = null!;
        var section = _configuration.GetSection("Payments:PayOS");
        if (!bool.TryParse(section["Enabled"], out var enabled) || !enabled)
        {
            return false;
        }

        string?[] secrets = [section["ClientId"], section["ApiKey"], section["ChecksumKey"]];
        if (secrets.Any(IsPlaceholder) || !IsHttpsUrl(section["ReturnUrl"]) || !IsHttpsUrl(section["CancelUrl"]))
        {
            return false;
        }

        settings = new Settings(secrets[0]!.Trim(), secrets[1]!.Trim(), secrets[2]!.Trim(), section["ReturnUrl"]!.Trim(), section["CancelUrl"]!.Trim());
        return true;
    }

    private static bool IsHttpsUrl(string? value) =>
        !IsPlaceholder(value) && Uri.TryCreate(value!.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo);

    private static bool IsPlaceholder(string? value) => string.IsNullOrWhiteSpace(value) || value.TrimStart().StartsWith('<');
}
