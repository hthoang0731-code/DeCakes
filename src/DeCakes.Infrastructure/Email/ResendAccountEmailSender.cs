using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DeCakes.Application.Accounts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DeCakes.Infrastructure.Email;

/// <summary>
/// Gửi email tài khoản qua Resend (POST https://api.resend.com/emails, Bearer API key; theo tài liệu
/// https://resend.com/docs/api-reference/emails/send-email). Dùng HttpClient của BCL, không thêm package.
/// Resend từ chối (403) request không có header User-Agent (https://resend.com/docs/api-reference/introduction),
/// HttpClient không tự gửi header này nên phải đặt rõ.
/// Cấu hình được kiểm tra ở lần gửi, không ở lúc khởi động, để ứng dụng và health chạy được khi chưa có dịch vụ email:
///   App:PublicBaseUrl   origin HTTPS của frontend, nơi có trang /verify-email và /reset-password
///   Email:From          địa chỉ gửi trên domain đã xác minh với Resend
///   Email:Resend:ApiKey bí mật; đặt bằng user-secrets/biến môi trường, không ghi vào appsettings
/// Thiếu hoặc sai một giá trị: ném <see cref="EmailDeliveryException"/>, không gọi mạng. Token chỉ nằm trong phần
/// fragment (#token=...) của link nên không đi vào log máy chủ hay header Referer; không ghi token vào log/exception.
/// Log chỉ gồm loại email, id email Resend trả về, mã HTTP, tên lỗi Resend và tên khóa cấu hình còn thiếu; không có
/// địa chỉ email, token, link, khóa API hay nội dung phản hồi.
/// </summary>
public sealed class ResendAccountEmailSender : IAccountEmailSender
{
    private static readonly Uri Endpoint = new("https://api.resend.com/emails");
    private static readonly ProductInfoHeaderValue UserAgent = new("DeCakes", "1.0");

    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private readonly IConfiguration _configuration;
    private readonly HttpClient _client;
    private readonly ILogger<ResendAccountEmailSender> _logger;

    public ResendAccountEmailSender(IConfiguration configuration, ILogger<ResendAccountEmailSender> logger)
        : this(configuration, SharedClient, logger) { }

    internal ResendAccountEmailSender(IConfiguration configuration, HttpClient client, ILogger<ResendAccountEmailSender> logger)
    {
        _configuration = configuration;
        _client = client;
        _logger = logger;
    }

    public Task SendEmailVerificationAsync(
        string toEmail, string fullName, string token, DateTime expiresAt, CancellationToken cancellationToken)
    {
        const string kind = "email-verification";
        var settings = LoadSettings(kind);
        var link = settings.BaseUrl + "/verify-email#token=" + token;
        return SendAsync(
            settings, kind, toEmail, "Xác minh email tài khoản DeCakes",
            $"Chào {fullName},\n\nMở liên kết sau để xác minh email tài khoản DeCakes:\n{link}\n\n"
            + $"Liên kết dùng một lần, hết hạn lúc {expiresAt:yyyy-MM-dd HH:mm} UTC. Nếu bạn không đăng ký, hãy bỏ qua email này.",
            $"<p>Chào {WebUtility.HtmlEncode(fullName)},</p>"
            + $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">Xác minh email tài khoản DeCakes</a></p>"
            + $"<p>Liên kết dùng một lần, hết hạn lúc {expiresAt:yyyy-MM-dd HH:mm} UTC. Nếu bạn không đăng ký, hãy bỏ qua email này.</p>",
            cancellationToken);
    }

    public Task SendPasswordResetAsync(
        string toEmail, string fullName, string token, DateTime expiresAt, CancellationToken cancellationToken)
    {
        const string kind = "password-reset";
        var settings = LoadSettings(kind);
        var link = settings.BaseUrl + "/reset-password#token=" + token;
        return SendAsync(
            settings, kind, toEmail, "Đặt lại mật khẩu DeCakes",
            $"Chào {fullName},\n\nMở liên kết sau để đặt mật khẩu mới:\n{link}\n\n"
            + $"Liên kết dùng một lần, hết hạn lúc {expiresAt:yyyy-MM-dd HH:mm} UTC. Nếu bạn không yêu cầu, hãy bỏ qua email này; mật khẩu hiện tại không đổi.",
            $"<p>Chào {WebUtility.HtmlEncode(fullName)},</p>"
            + $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">Đặt mật khẩu mới</a></p>"
            + $"<p>Liên kết dùng một lần, hết hạn lúc {expiresAt:yyyy-MM-dd HH:mm} UTC. Nếu bạn không yêu cầu, hãy bỏ qua email này; mật khẩu hiện tại không đổi.</p>",
            cancellationToken);
    }

    public Task SendStaffInvitationAsync(
        string toEmail, string bakeryName, string token, DateTime expiresAt, CancellationToken cancellationToken)
    {
        const string kind = "staff-invitation";
        var settings = LoadSettings(kind);
        var link = settings.BaseUrl + "/accept-invitation#token=" + token;
        return SendAsync(
            settings, kind, toEmail, "Lời mời tham gia tiệm trên DeCakes",
            $"Bạn được mời làm nhân viên của tiệm \"{bakeryName}\" trên DeCakes.\n\nMở liên kết sau để nhận lời mời:\n{link}\n\n"
            + $"Bạn tự đặt mật khẩu cho tài khoản mới hoặc đăng nhập tài khoản tiệm có đúng email này. Liên kết dùng một lần, hết hạn lúc {expiresAt:yyyy-MM-dd HH:mm} UTC.",
            $"<p>Bạn được mời làm nhân viên của tiệm <strong>{WebUtility.HtmlEncode(bakeryName)}</strong> trên DeCakes.</p>"
            + $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">Nhận lời mời</a></p>"
            + $"<p>Bạn tự đặt mật khẩu cho tài khoản mới hoặc đăng nhập tài khoản tiệm có đúng email này. Liên kết dùng một lần, hết hạn lúc {expiresAt:yyyy-MM-dd HH:mm} UTC.</p>",
            cancellationToken);
    }

    private async Task SendAsync(
        Settings settings, string kind, string toEmail, string subject, string text, string html, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new { from = settings.From, to = new[] { toEmail }, subject, text, html }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        request.Headers.UserAgent.Add(UserAgent);

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning("Không kết nối được Resend khi gửi email {EmailKind} ({ErrorType}).", kind, ex.GetType().Name);
            throw new EmailDeliveryException("Không kết nối được dịch vụ email.", ex);
        }

        using (response)
        {
            // Chỉ đọc trường id (thành công) hoặc name (lỗi); message của Resend có thể chứa địa chỉ email nên bị bỏ.
            var field = await ReadSafeFieldAsync(response, response.IsSuccessStatusCode ? "id" : "name", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Resend từ chối email {EmailKind}: HTTP {StatusCode}, lỗi {ResendError}.", kind, (int)response.StatusCode, field);
                // Không đưa nội dung phản hồi vào thông điệp để tránh lộ dữ liệu gửi đi.
                throw new EmailDeliveryException($"Dịch vụ email từ chối yêu cầu (HTTP {(int)response.StatusCode}).");
            }

            // Resend đã nhận yêu cầu; chưa có nghĩa email đã tới hộp thư. Tra id trong dashboard Resend để xem trạng thái.
            _logger.LogInformation("Resend đã nhận email {EmailKind}, id {ResendEmailId}.", kind, field);
        }
    }

    /// <summary>Giá trị chuỗi ngắn chỉ gồm chữ, số, '-', '_' (id UUID hoặc tên lỗi); khác đi trả "unknown".</summary>
    private static async Task<string> ReadSafeFieldAsync(HttpResponseMessage response, string name, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 and <= 64 } text
                && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return text;
            }
        }
        catch (JsonException)
        {
        }

        return "unknown";
    }

    private sealed record Settings(string BaseUrl, string From, string ApiKey);

    private Settings LoadSettings(string kind)
    {
        var baseUrl = _configuration["App:PublicBaseUrl"];
        var from = _configuration["Email:From"];
        var apiKey = _configuration["Email:Resend:ApiKey"];
        var missing = new List<string>();
        if (!TryNormalizeBaseUrl(baseUrl, out var normalized)) missing.Add("App:PublicBaseUrl");
        if (IsPlaceholder(from)) missing.Add("Email:From");
        if (IsPlaceholder(apiKey)) missing.Add("Email:Resend:ApiKey");
        if (missing.Count > 0)
        {
            // Chỉ ghi tên khóa, không ghi giá trị.
            _logger.LogWarning("Chưa gửi email {EmailKind}: thiếu hoặc sai cấu hình {MissingKeys}.", kind, string.Join(", ", missing));
            throw new EmailDeliveryException("Chưa cấu hình gửi email (App:PublicBaseUrl, Email:From, Email:Resend:ApiKey).");
        }

        return new Settings(normalized, from!.Trim(), apiKey!.Trim());
    }

    /// <summary>Chỉ nhận URL tuyệt đối HTTPS, không có user-info, query hay fragment.</summary>
    internal static bool TryNormalizeBaseUrl(string? value, out string normalized)
    {
        normalized = "";
        if (IsPlaceholder(value)
            || !Uri.TryCreate(value!.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    private static bool IsPlaceholder(string? value) => string.IsNullOrWhiteSpace(value) || value.TrimStart().StartsWith('<');
}
