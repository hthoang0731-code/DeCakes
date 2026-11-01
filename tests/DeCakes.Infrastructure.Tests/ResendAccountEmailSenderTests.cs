using System.Net;
using System.Text.Json;
using DeCakes.Application.Accounts;
using DeCakes.Infrastructure.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DeCakes.Infrastructure.Tests;

/// <summary>
/// Hình dạng request gửi tới Resend và xử lý cấu hình/lỗi, bằng HttpMessageHandler giả. Không gọi mạng; không chứng minh
/// tài khoản/domain Resend thật hoạt động.
/// </summary>
public class ResendAccountEmailSenderTests
{
    private const string Token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string ApiKey = "re_test_khong_phai_khoa_that";
    private const string Recipient = "khach@example.com";
    private const string EmailId = "49a3999c-0ce1-4ea6-ab68-afcd6dc2e794";
    private static readonly DateTime ExpiresAt = new(2026, 10, 23, 1, 0, 0, DateTimeKind.Utc);

    private readonly CapturingLogger _logger = new();

    private static IConfiguration Config(string? baseUrl = "https://app.example/", string? from = "DeCakes <no-reply@mail.example>", string? apiKey = ApiKey) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:PublicBaseUrl"] = baseUrl,
            ["Email:From"] = from,
            ["Email:Resend:ApiKey"] = apiKey,
        }).Build();

    private ResendAccountEmailSender Sender(IConfiguration configuration, HttpMessageHandler handler) =>
        new(configuration, new HttpClient(handler), _logger);

    [Fact]
    public async Task Sends_verification_email_with_bearer_key_user_agent_and_fragment_link()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var sender = Sender(Config(), handler);

        await sender.SendEmailVerificationAsync(Recipient, "<b>Lan</b>", Token, ExpiresAt, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.resend.com/emails", request.Uri);
        Assert.Equal($"Bearer {ApiKey}", request.Authorization);
        // Resend trả 403 cho request không có User-Agent.
        Assert.False(string.IsNullOrWhiteSpace(request.UserAgent));
        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal("DeCakes <no-reply@mail.example>", root.GetProperty("from").GetString());
        Assert.Equal([Recipient], root.GetProperty("to").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains($"https://app.example/verify-email#token={Token}", root.GetProperty("text").GetString());
        var html = root.GetProperty("html").GetString()!;
        Assert.Contains($"https://app.example/verify-email#token={Token}", html);
        Assert.Contains("&lt;b&gt;Lan&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>Lan</b>", html);
    }

    [Fact]
    public async Task Reset_link_uses_reset_page_and_base_path()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var sender = Sender(Config(baseUrl: "https://example.vn/app"), handler);

        await sender.SendPasswordResetAsync("a@example.com", "A", Token, ExpiresAt, CancellationToken.None);

        Assert.Contains($"https://example.vn/app/reset-password#token={Token}", Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task Accepted_email_logs_resend_id_without_recipient_token_or_key()
    {
        var sender = Sender(Config(), new CapturingHandler(HttpStatusCode.OK, responseBody: $"{{\"id\":\"{EmailId}\"}}"));

        await sender.SendPasswordResetAsync(Recipient, "A", Token, ExpiresAt, CancellationToken.None);

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(EmailId, entry.Message);
        Assert.Contains("password-reset", entry.Message);
        AssertNoSecrets(entry.Message);
    }

    public static TheoryData<string?, string?, string?, string> MissingConfigurations() => new()
    {
        { null, "a@mail.example", ApiKey, "App:PublicBaseUrl" },
        { "http://app.example", "a@mail.example", ApiKey, "App:PublicBaseUrl" },
        { "https://app.example?x=1", "a@mail.example", ApiKey, "App:PublicBaseUrl" },
        { "https://user:pass@app.example", "a@mail.example", ApiKey, "App:PublicBaseUrl" },
        { "<origin HTTPS của frontend>", "a@mail.example", ApiKey, "App:PublicBaseUrl" },
        { "https://app.example", null, ApiKey, "Email:From" },
        { "https://app.example", "a@mail.example", "<đặt bằng user-secrets>", "Email:Resend:ApiKey" },
        { "https://app.example", "a@mail.example", "", "Email:Resend:ApiKey" },
    };

    [Theory]
    [MemberData(nameof(MissingConfigurations))]
    public async Task Missing_or_unsafe_configuration_fails_without_network_call(string? baseUrl, string? from, string? apiKey, string missingKey)
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var sender = Sender(Config(baseUrl, from, apiKey), handler);

        await Assert.ThrowsAsync<EmailDeliveryException>(() =>
            sender.SendPasswordResetAsync("a@example.com", "A", Token, ExpiresAt, CancellationToken.None));
        Assert.Empty(handler.Requests);

        // Log nêu tên khóa cần sửa, không nêu giá trị đã cấu hình.
        var entry = Assert.Single(_logger.Entries);
        Assert.Contains(missingKey, entry.Message);
        Assert.DoesNotContain(ApiKey, entry.Message);
        Assert.DoesNotContain("a@mail.example", entry.Message);
    }

    [Fact]
    public async Task Provider_error_is_reported_without_token_key_or_response_message()
    {
        var handler = new CapturingHandler(
            HttpStatusCode.Forbidden,
            responseBody: $"{{\"statusCode\":403,\"name\":\"validation_error\",\"message\":\"You can only send testing emails to your own email address ({Recipient}). secret-detail\"}}");
        var sender = Sender(Config(), handler);

        var error = await Assert.ThrowsAsync<EmailDeliveryException>(() =>
            sender.SendEmailVerificationAsync(Recipient, "A", Token, ExpiresAt, CancellationToken.None));

        Assert.Contains("403", error.Message);
        Assert.DoesNotContain(Token, error.ToString());
        Assert.DoesNotContain(ApiKey, error.ToString());
        Assert.DoesNotContain("secret-detail", error.ToString());

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("403", entry.Message);
        Assert.Contains("validation_error", entry.Message);
        Assert.DoesNotContain("secret-detail", entry.Message);
        AssertNoSecrets(entry.Message);
    }

    [Theory]
    [InlineData("không phải JSON")]
    [InlineData("{\"name\":\"bad name\\r\\nforged log line\"}")]
    [InlineData("{\"name\":123}")]
    public async Task Unexpected_error_body_is_logged_as_unknown(string responseBody)
    {
        var sender = Sender(Config(), new CapturingHandler((HttpStatusCode)422, responseBody));

        await Assert.ThrowsAsync<EmailDeliveryException>(() =>
            sender.SendEmailVerificationAsync(Recipient, "A", Token, ExpiresAt, CancellationToken.None));

        var entry = Assert.Single(_logger.Entries);
        Assert.Contains("unknown", entry.Message);
        Assert.DoesNotContain("forged", entry.Message);
    }

    [Fact]
    public async Task Network_failure_becomes_delivery_exception()
    {
        var sender = Sender(Config(), new CapturingHandler(throwOnSend: true));

        await Assert.ThrowsAsync<EmailDeliveryException>(() =>
            sender.SendEmailVerificationAsync(Recipient, "A", Token, ExpiresAt, CancellationToken.None));
        AssertNoSecrets(Assert.Single(_logger.Entries).Message);
    }

    private static void AssertNoSecrets(string message)
    {
        Assert.DoesNotContain(Token, message);
        Assert.DoesNotContain(ApiKey, message);
        Assert.DoesNotContain(Recipient, message);
        Assert.DoesNotContain("#token=", message);
    }

    private sealed class CapturingHandler(
        HttpStatusCode status = HttpStatusCode.OK, string responseBody = "{\"id\":\"x\"}", bool throwOnSend = false) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Uri, string? Authorization, string? UserAgent, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (throwOnSend) throw new HttpRequestException("không kết nối được");
            Requests.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
                request.Headers.UserAgent.ToString(), await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
        }
    }

    private sealed class CapturingLogger : ILogger<ResendAccountEmailSender>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
