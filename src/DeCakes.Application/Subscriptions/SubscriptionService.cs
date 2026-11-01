using System.Security.Cryptography;
using DeCakes.Application.BakeryOnboarding;
using DeCakes.Domain.Subscriptions;

namespace DeCakes.Application.Subscriptions;

/// <summary>Quyền gói của tiệm, checkout gia hạn và xử lý kết quả thanh toán đã xác minh.</summary>
public interface ISubscriptionService
{
    /// <summary>Null khi tiệm không tồn tại hoặc user không phải thành viên active.</summary>
    Task<SubscriptionStatusDto?> GetStatusAsync(long userId, long bakeryId, CancellationToken cancellationToken);

    Task<CheckoutResult> CheckoutAsync(long ownerUserId, long bakeryId, CheckoutRequest request, CancellationToken cancellationToken);

    Task<(OwnerActionOutcome Outcome, SubscriptionPaymentDto? Payment)> GetPaymentAsync(
        long ownerUserId, long bakeryId, long paymentId, CancellationToken cancellationToken);

    Task<WebhookResult> HandleWebhookAsync(string body, CancellationToken cancellationToken);
}

/// <summary><c>Errors</c> khác null: dữ liệu không hợp lệ, chưa chạm store/cổng thanh toán.</summary>
public sealed record CheckoutResult(
    CheckoutOutcome Outcome, SubscriptionPaymentDto? Payment = null, IReadOnlyDictionary<string, string[]>? Errors = null);

public sealed record WebhookResult(WebhookVerificationStatus Verification, PaymentApplyOutcome? Applied = null, long? OrderCode = null);

public sealed class SubscriptionService(ISubscriptionStore store, ISubscriptionPaymentGateway gateway, TimeProvider time) : ISubscriptionService
{
    public static readonly TimeSpan PaymentLinkLifetime = TimeSpan.FromMinutes(60);
    private const int OrderCodeAttempts = 3;

    // payOS orderCode là số nguyên dương; giữ trong 48 bit để an toàn với JSON/JavaScript.
    private const long MaxOrderCode = (1L << 48) - 1;

    public async Task<SubscriptionStatusDto?> GetStatusAsync(long userId, long bakeryId, CancellationToken cancellationToken)
    {
        var record = await store.FindStatusAsync(userId, bakeryId, cancellationToken);
        if (record is null)
        {
            return null;
        }

        var now = Now();
        var active = record.Periods.Any(p => p.StartsAt <= now && now < p.EndsAt);
        var lastEnd = record.Periods.Count == 0 ? (DateTime?)null : record.Periods.Max(p => p.EndsAt);
        return new SubscriptionStatusDto(
            record.BakeryId, record.Plan, record.MonthlyPrice, record.YearlyPrice, active,
            active ? lastEnd : null, record.Periods);
    }

    public async Task<CheckoutResult> CheckoutAsync(
        long ownerUserId, long bakeryId, CheckoutRequest request, CancellationToken cancellationToken)
    {
        var cycle = request.BillingCycle?.Trim();
        if (!BillingCycles.IsKnown(cycle))
        {
            return new CheckoutResult(CheckoutOutcome.Created,
                Errors: new Dictionary<string, string[]> { ["billingCycle"] = ["billingCycle phải là month hoặc year."] });
        }

        // Chưa bật cổng/chưa có merchant: không tạo giao dịch, không gọi mạng, không cấp kỳ.
        if (!gateway.IsEnabled)
        {
            return new CheckoutResult(CheckoutOutcome.PaymentNotConfigured);
        }

        var now = Now();
        CreatePaymentResult created = null!;
        long orderCode = 0;
        for (var attempt = 1; attempt <= OrderCodeAttempts; attempt++)
        {
            orderCode = NewOrderCode();
            try
            {
                created = await store.CreatePaymentAsync(
                    new NewSubscriptionPayment(ownerUserId, bakeryId, cycle!, gateway.Provider, orderCode, now), cancellationToken);
                break;
            }
            catch (DuplicateOrderCodeException) when (attempt < OrderCodeAttempts)
            {
            }
        }

        if (created.Status != CheckoutOutcome.Created)
        {
            return new CheckoutResult(created.Status);
        }

        PaymentLink link;
        try
        {
            link = await gateway.CreatePaymentLinkAsync(
                new PaymentLinkRequest(orderCode, decimal.ToInt64(created.Amount), $"DC{orderCode % 10_000_000}", now + PaymentLinkLifetime),
                cancellationToken);
        }
        catch (PaymentGatewayException ex)
        {
            await store.MarkPaymentFailedAsync(created.PaymentId, ex.Message, Now(), CancellationToken.None);
            return new CheckoutResult(CheckoutOutcome.ProviderError);
        }

        await store.MarkPaymentLinkAsync(created.PaymentId, link.ProviderPaymentId, link.CheckoutUrl, Now(), cancellationToken);
        var (_, payment) = await store.FindPaymentAsync(ownerUserId, bakeryId, created.PaymentId, cancellationToken);
        return new CheckoutResult(CheckoutOutcome.Created, payment);
    }

    public Task<(OwnerActionOutcome Outcome, SubscriptionPaymentDto? Payment)> GetPaymentAsync(
        long ownerUserId, long bakeryId, long paymentId, CancellationToken cancellationToken) =>
        store.FindPaymentAsync(ownerUserId, bakeryId, paymentId, cancellationToken);

    public async Task<WebhookResult> HandleWebhookAsync(string body, CancellationToken cancellationToken)
    {
        var verification = gateway.VerifyWebhook(body);
        if (verification.Status != WebhookVerificationStatus.Paid)
        {
            return new WebhookResult(verification.Status, OrderCode: verification.Notice?.OrderCode);
        }

        var applied = await store.ApplyVerifiedPaymentAsync(verification.Notice!, Now(), cancellationToken);
        return new WebhookResult(WebhookVerificationStatus.Paid, applied, verification.Notice!.OrderCode);
    }

    private static long NewOrderCode()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return (long)(BitConverter.ToUInt64(bytes) % (ulong)MaxOrderCode) + 1;
    }

    private DateTime Now() => time.GetUtcNow().UtcDateTime;
}
