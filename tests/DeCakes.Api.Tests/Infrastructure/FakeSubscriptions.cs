using DeCakes.Application.BakeryOnboarding;
using DeCakes.Application.Subscriptions;

namespace DeCakes.Api.Tests.Infrastructure;

/// <summary>Store gói giả cho kiểm thử HTTP; quy tắc dữ liệu được kiểm thử trên MySQL (MySqlSubscriptionStoreTests).</summary>
public sealed class ScriptedSubscriptionStore : ISubscriptionStore
{
    private readonly object _gate = new();

    public List<(string Method, long UserId, long BakeryId)> Calls { get; } = [];

    public SubscriptionStatusRecord? Status { get; set; }

    public CheckoutOutcome CreateOutcome { get; set; } = CheckoutOutcome.Created;

    public List<VerifiedPaymentNotice> Applied { get; } = [];

    public PaymentApplyOutcome ApplyOutcome { get; set; } = PaymentApplyOutcome.Activated;

    public string PaymentStatus { get; private set; } = "created";

    public string? FailureReason { get; private set; }

    public int Count(string method)
    {
        lock (_gate) return Calls.Count(c => c.Method == method);
    }

    private void Record(string method, long userId, long bakeryId)
    {
        lock (_gate) Calls.Add((method, userId, bakeryId));
    }

    public Task<SubscriptionStatusRecord?> FindStatusAsync(long userId, long bakeryId, CancellationToken cancellationToken)
    {
        Record(nameof(FindStatusAsync), userId, bakeryId);
        return Task.FromResult(Status);
    }

    public Task<CreatePaymentResult> CreatePaymentAsync(NewSubscriptionPayment payment, CancellationToken cancellationToken)
    {
        Record(nameof(CreatePaymentAsync), payment.OwnerUserId, payment.BakeryId);
        return Task.FromResult(CreateOutcome == CheckoutOutcome.Created
            ? new CreatePaymentResult(CheckoutOutcome.Created, 31, 199_000m, "VND")
            : new CreatePaymentResult(CreateOutcome));
    }

    public Task MarkPaymentLinkAsync(long paymentId, string providerPaymentId, string checkoutUrl, DateTime now, CancellationToken cancellationToken)
    {
        Record(nameof(MarkPaymentLinkAsync), 0, 0);
        PaymentStatus = "pending";
        return Task.CompletedTask;
    }

    public Task MarkPaymentFailedAsync(long paymentId, string reason, DateTime now, CancellationToken cancellationToken)
    {
        Record(nameof(MarkPaymentFailedAsync), 0, 0);
        (PaymentStatus, FailureReason) = ("failed", reason);
        return Task.CompletedTask;
    }

    public Task<(OwnerActionOutcome Outcome, SubscriptionPaymentDto? Payment)> FindPaymentAsync(
        long ownerUserId, long bakeryId, long paymentId, CancellationToken cancellationToken)
    {
        Record(nameof(FindPaymentAsync), ownerUserId, bakeryId);
        return Task.FromResult<(OwnerActionOutcome, SubscriptionPaymentDto?)>((OwnerActionOutcome.Succeeded,
            new SubscriptionPaymentDto(paymentId, 1, "month", 199_000m, "VND", PaymentStatus,
                PaymentStatus == "pending" ? "https://pay.example/31" : null, DateTime.UtcNow, null)));
    }

    public Task<PaymentApplyOutcome> ApplyVerifiedPaymentAsync(VerifiedPaymentNotice notice, DateTime now, CancellationToken cancellationToken)
    {
        lock (_gate) Applied.Add(notice);
        return Task.FromResult(ApplyOutcome);
    }
}

/// <summary>
/// Cổng thanh toán giả CHỈ dùng trong kiểm thử. Body "paid:&lt;orderCode&gt;:&lt;amount&gt;" coi như có chữ ký hợp lệ,
/// "unpaid:&lt;orderCode&gt;" hợp lệ nhưng không thành công, còn lại là chữ ký sai.
/// </summary>
public sealed class FakePaymentGateway : ISubscriptionPaymentGateway
{
    public string Provider => "payos";

    public bool IsEnabled { get; set; } = true;

    public bool FailLink { get; set; }

    public List<PaymentLinkRequest> LinkRequests { get; } = [];

    public Task<PaymentLink> CreatePaymentLinkAsync(PaymentLinkRequest request, CancellationToken cancellationToken)
    {
        LinkRequests.Add(request);
        if (FailLink) throw new PaymentGatewayException("Cổng thanh toán từ chối yêu cầu (HTTP 500).");
        return Task.FromResult(new PaymentLink("plink-" + request.OrderCode, "https://pay.example/" + request.OrderCode));
    }

    public WebhookVerification VerifyWebhook(string body)
    {
        if (!IsEnabled) return new WebhookVerification(WebhookVerificationStatus.NotConfigured);
        var parts = body.Split(':');
        return parts switch
        {
            ["paid", var code, var amount] => new WebhookVerification(WebhookVerificationStatus.Paid,
                new VerifiedPaymentNotice("payos", long.Parse(code), decimal.Parse(amount), "VND", "plink-" + code, "FT1")),
            ["unpaid", var code] => new WebhookVerification(WebhookVerificationStatus.NotPaid,
                new VerifiedPaymentNotice("payos", long.Parse(code), 0, "VND", null, null)),
            _ => new WebhookVerification(WebhookVerificationStatus.InvalidSignature),
        };
    }
}
