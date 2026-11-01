using DeCakes.Application.Accounts;

namespace DeCakes.Api.Tests.Infrastructure;

/// <summary>Ghi lại email lẽ ra được gửi (kể cả token) để kiểm thử lấy link; không gọi mạng.</summary>
public sealed class RecordingEmailSender : IAccountEmailSender
{
    private readonly object _gate = new();
    private readonly List<SentEmail> _sent = [];
    private readonly List<SentEmail> _attempts = [];

    public sealed record SentEmail(string Kind, string To, string Token, DateTime ExpiresAt);

    /// <summary>Mô phỏng dịch vụ email lỗi hoặc chưa cấu hình.</summary>
    public bool Fail { get; set; }

    public IReadOnlyList<SentEmail> Sent
    {
        get { lock (_gate) return _sent.ToList(); }
    }

    /// <summary>Mọi lần gọi gửi, kể cả lần thất bại.</summary>
    public IReadOnlyList<SentEmail> Attempts
    {
        get { lock (_gate) return _attempts.ToList(); }
    }

    public SentEmail Last(string kind)
    {
        lock (_gate) return _sent.Last(e => e.Kind == kind);
    }

    public Task SendEmailVerificationAsync(string toEmail, string fullName, string token, DateTime expiresAt, CancellationToken cancellationToken) =>
        Record("verify", toEmail, token, expiresAt);

    public Task SendPasswordResetAsync(string toEmail, string fullName, string token, DateTime expiresAt, CancellationToken cancellationToken) =>
        Record("reset", toEmail, token, expiresAt);

    public Task SendStaffInvitationAsync(string toEmail, string bakeryName, string token, DateTime expiresAt, CancellationToken cancellationToken) =>
        Record("invite", toEmail, token, expiresAt);

    private Task Record(string kind, string to, string token, DateTime expiresAt)
    {
        var email = new SentEmail(kind, to, token, expiresAt);
        lock (_gate)
        {
            _attempts.Add(email);
            if (Fail) throw new EmailDeliveryException("Chưa cấu hình gửi email.");
            _sent.Add(email);
        }

        return Task.CompletedTask;
    }
}
