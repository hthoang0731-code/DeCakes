using DeCakes.Application.BakeryOnboarding;
using DeCakes.Application.Subscriptions;
using DeCakes.Domain.BakeryOnboarding;
using DeCakes.Domain.BakeryWorkspace;
using DeCakes.Domain.Subscriptions;
using DeCakes.Infrastructure.Persistence;
using MySqlConnector;
using static DeCakes.Infrastructure.BakeryOnboarding.MySqlBakeryOnboardingStore;

namespace DeCakes.Infrastructure.Subscriptions;

/// <summary>
/// Giao dịch phí sử dụng và kỳ trả phí (migrations/V005__subscription_payments.sql). Transaction của ứng dụng, không gọi 6
/// procedure nghiệp vụ. Thứ tự khóa khi tạo giao dịch: bakeries rồi bakery_subscriptions; khi xử lý thanh toán:
/// subscription_payments rồi bakery_subscriptions (tuần tự hóa các thanh toán của cùng tiệm) rồi
/// bakery_subscription_periods. Webhook lặp bị chặn bởi trạng thái paid dưới khóa và UNIQUE(payment_id) của kỳ.
/// </summary>
public sealed class MySqlSubscriptionStore(IMySqlConnectionFactory connectionFactory) : ISubscriptionStore
{
    private const int FailureReasonMaxLength = 200;

    public async Task<SubscriptionStatusRecord?> FindStatusAsync(long userId, long bakeryId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        if (await MemberRoleAsync(connection, null, userId, bakeryId, cancellationToken) is null)
        {
            return null;
        }

        SelectedPlanDto? plan = null;
        decimal? monthly = null, yearly = null;
        await using (var command = Command(connection, null,
                         """
                         SELECT sp.id, sp.code, sp.name, sp.monthly_price, sp.yearly_price
                           FROM bakery_subscriptions bs JOIN subscription_plans sp ON sp.id = bs.plan_id
                          WHERE bs.bakery_id = @bakeryId
                         """,
                         ("@bakeryId", bakeryId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                plan = new SelectedPlanDto(reader.GetInt64("id"), reader.GetString("code"), reader.GetString("name"));
                monthly = reader.IsDBNull(reader.GetOrdinal("monthly_price")) ? null : reader.GetDecimal("monthly_price");
                yearly = reader.IsDBNull(reader.GetOrdinal("yearly_price")) ? null : reader.GetDecimal("yearly_price");
            }
        }

        var periods = new List<SubscriptionPeriodDto>();
        await using (var command = Command(connection, null,
                         "SELECT period_kind, starts_at, ends_at FROM bakery_subscription_periods WHERE bakery_id = @bakeryId ORDER BY starts_at, id",
                         ("@bakeryId", bakeryId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                periods.Add(new SubscriptionPeriodDto(reader.GetString("period_kind"), reader.GetDateTime("starts_at"), reader.GetDateTime("ends_at")));
            }
        }

        return new SubscriptionStatusRecord(bakeryId, plan, monthly, yearly, periods);
    }

    public async Task<CreatePaymentResult> CreatePaymentAsync(NewSubscriptionPayment payment, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var (status, role) = await LockBakeryForMemberAsync(connection, transaction, payment.OwnerUserId, payment.BakeryId, cancellationToken);
        if (status is null || role is null) return new CreatePaymentResult(CheckoutOutcome.BakeryNotFound);
        if (role != BakeryMemberRoles.Owner) return new CreatePaymentResult(CheckoutOutcome.OwnerRequired);
        if (status != BakeryProfileStatuses.Active) return new CreatePaymentResult(CheckoutOutcome.ProfileNotActive);

        long planId;
        decimal? price;
        string currency;
        await using (var plan = Command(connection, transaction,
                         """
                         SELECT sp.id, sp.status, sp.monthly_price, sp.yearly_price, sp.currency
                           FROM bakery_subscriptions bs JOIN subscription_plans sp ON sp.id = bs.plan_id
                          WHERE bs.bakery_id = @bakeryId
                            FOR SHARE
                         """,
                         ("@bakeryId", payment.BakeryId)))
        await using (var reader = await plan.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return new CreatePaymentResult(CheckoutOutcome.PlanNotSelected);
            if (reader.GetString("status") != "active") return new CreatePaymentResult(CheckoutOutcome.PlanNotAvailable);
            planId = reader.GetInt64("id");
            var column = payment.BillingCycle == BillingCycles.Year ? "yearly_price" : "monthly_price";
            price = reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetDecimal(column);
            currency = reader.GetString("currency");
        }

        // Chưa định giá: không mở checkout và không thay bằng giá 0.
        if (price is not > 0) return new CreatePaymentResult(CheckoutOutcome.PlanNotPriced);

        long paymentId;
        await using (var insert = Command(connection, transaction,
                         """
                         INSERT INTO subscription_payments
                             (bakery_id, plan_id, billing_cycle, amount, currency, provider, order_code, status,
                              created_by_user_id, created_at, updated_at)
                         VALUES (@bakeryId, @planId, @cycle, @amount, @currency, @provider, @orderCode, 'created',
                                 @userId, @now, @now)
                         """,
                         ("@bakeryId", payment.BakeryId), ("@planId", planId), ("@cycle", payment.BillingCycle),
                         ("@amount", price.Value), ("@currency", currency), ("@provider", payment.Provider),
                         ("@orderCode", payment.OrderCode), ("@userId", payment.OwnerUserId), ("@now", payment.Now)))
        {
            try
            {
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
            {
                throw new DuplicateOrderCodeException(ex);
            }

            paymentId = insert.LastInsertedId;
        }

        await transaction.CommitAsync(cancellationToken);
        return new CreatePaymentResult(CheckoutOutcome.Created, paymentId, price.Value, currency);
    }

    public async Task MarkPaymentLinkAsync(
        long paymentId, string providerPaymentId, string checkoutUrl, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        // Không ghi đè giao dịch đã được webhook xử lý (paid/mismatch) nếu webhook đến trước.
        await using var command = Command(connection, null,
            """
            UPDATE subscription_payments
               SET provider_payment_id = @providerId, checkout_url = @url,
                   status = IF(status = 'created', 'pending', status), updated_at = @now
             WHERE id = @id AND status IN ('created', 'pending', 'paid', 'mismatch') AND provider_payment_id IS NULL
            """,
            ("@providerId", providerPaymentId), ("@url", checkoutUrl), ("@now", now), ("@id", paymentId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkPaymentFailedAsync(long paymentId, string reason, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null,
            "UPDATE subscription_payments SET status = 'failed', failure_reason = @reason, updated_at = @now WHERE id = @id AND status = 'created'",
            ("@reason", reason.Length > FailureReasonMaxLength ? reason[..FailureReasonMaxLength] : reason), ("@now", now), ("@id", paymentId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<(OwnerActionOutcome Outcome, SubscriptionPaymentDto? Payment)> FindPaymentAsync(
        long ownerUserId, long bakeryId, long paymentId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var role = await MemberRoleAsync(connection, null, ownerUserId, bakeryId, cancellationToken);
        if (role is null) return (OwnerActionOutcome.BakeryNotFound, null);
        if (role != BakeryMemberRoles.Owner) return (OwnerActionOutcome.OwnerRequired, null);

        await using var command = Command(connection, null,
            """
            SELECT id, plan_id, billing_cycle, amount, currency, status, checkout_url, created_at, paid_at
              FROM subscription_payments WHERE id = @id AND bakery_id = @bakeryId
            """,
            ("@id", paymentId), ("@bakeryId", bakeryId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return (OwnerActionOutcome.Succeeded, null);
        return (OwnerActionOutcome.Succeeded, new SubscriptionPaymentDto(
            reader.GetInt64("id"), reader.GetInt64("plan_id"), reader.GetString("billing_cycle"), reader.GetDecimal("amount"),
            reader.GetString("currency"), reader.GetString("status"),
            reader.IsDBNull(reader.GetOrdinal("checkout_url")) ? null : reader.GetString("checkout_url"),
            reader.GetDateTime("created_at"),
            reader.IsDBNull(reader.GetOrdinal("paid_at")) ? null : reader.GetDateTime("paid_at")));
    }

    public async Task<PaymentApplyOutcome> ApplyVerifiedPaymentAsync(
        VerifiedPaymentNotice notice, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        long id, bakeryId, planId;
        string cycle, currency, status;
        decimal amount;
        string? providerPaymentId;
        await using (var command = Command(connection, transaction,
                         """
                         SELECT id, bakery_id, plan_id, billing_cycle, amount, currency, status, provider_payment_id
                           FROM subscription_payments WHERE provider = @provider AND order_code = @orderCode
                            FOR UPDATE
                         """,
                         ("@provider", notice.Provider), ("@orderCode", notice.OrderCode)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return PaymentApplyOutcome.UnknownPayment;
            id = reader.GetInt64("id");
            bakeryId = reader.GetInt64("bakery_id");
            planId = reader.GetInt64("plan_id");
            cycle = reader.GetString("billing_cycle");
            amount = reader.GetDecimal("amount");
            currency = reader.GetString("currency");
            status = reader.GetString("status");
            providerPaymentId = reader.IsDBNull(reader.GetOrdinal("provider_payment_id")) ? null : reader.GetString("provider_payment_id");
        }

        // Đã xử lý (lặp, đến trễ, đảo thứ tự): không gia hạn thêm, không đổi kết luận đối soát.
        if (status is "paid" or "mismatch") return PaymentApplyOutcome.AlreadyProcessed;

        var mismatch = notice.Amount != amount ? "amount"
            : notice.Currency is not null && !string.Equals(notice.Currency, currency, StringComparison.Ordinal) ? "currency"
            : providerPaymentId is not null && !string.Equals(notice.ProviderPaymentId, providerPaymentId, StringComparison.Ordinal) ? "payment-link"
            : null;
        if (mismatch is not null)
        {
            await using var flag = Command(connection, transaction,
                "UPDATE subscription_payments SET status = 'mismatch', failure_reason = @reason, provider_reference = @ref, updated_at = @now WHERE id = @id",
                ("@reason", "mismatch:" + mismatch), ("@ref", Truncate(notice.Reference)), ("@now", now), ("@id", id));
            await flag.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return PaymentApplyOutcome.Mismatch;
        }

        await using (var lockSubscription = Command(connection, transaction,
                         "SELECT bakery_id FROM bakery_subscriptions WHERE bakery_id = @bakeryId FOR UPDATE", ("@bakeryId", bakeryId)))
        {
            await lockSubscription.ExecuteScalarAsync(cancellationToken);
        }

        DateTime? currentEnd;
        await using (var end = Command(connection, transaction,
                         "SELECT MAX(ends_at) FROM bakery_subscription_periods WHERE bakery_id = @bakeryId FOR SHARE", ("@bakeryId", bakeryId)))
        {
            currentEnd = await end.ExecuteScalarAsync(cancellationToken) is DateTime value ? value : null;
        }

        var (startsAt, endsAt) = SubscriptionRules.PaidPeriod(now, currentEnd, cycle);
        await using (var period = Command(connection, transaction,
                         """
                         INSERT INTO bakery_subscription_periods (bakery_id, plan_id, period_kind, starts_at, ends_at, payment_id, created_at)
                         VALUES (@bakeryId, @planId, 'paid', @startsAt, @endsAt, @paymentId, @now)
                         """,
                         ("@bakeryId", bakeryId), ("@planId", planId), ("@startsAt", startsAt), ("@endsAt", endsAt),
                         ("@paymentId", id), ("@now", now)))
        {
            await period.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var paid = Command(connection, transaction,
                         """
                         UPDATE subscription_payments
                            SET status = 'paid', paid_at = @now, provider_reference = @ref,
                                provider_payment_id = COALESCE(provider_payment_id, @providerId), updated_at = @now
                          WHERE id = @id
                         """,
                         ("@now", now), ("@ref", Truncate(notice.Reference)), ("@providerId", Truncate(notice.ProviderPaymentId)), ("@id", id)))
        {
            await paid.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return PaymentApplyOutcome.Activated;
    }

    private static string? Truncate(string? value) => value is { Length: > 100 } ? value[..100] : value;
}
