using System.Data;
using DeCakes.Application.BakeryOnboarding;
using DeCakes.Application.DesignRequests;
using DeCakes.Application.Subscriptions;
using DeCakes.Domain.Subscriptions;
using DeCakes.Infrastructure.BakeryOnboarding;
using DeCakes.Infrastructure.DesignRequests;
using DeCakes.Infrastructure.Subscriptions;
using MySqlConnector;

namespace DeCakes.Infrastructure.Tests.MySql;

/// <summary>
/// Quyền gói (migration đề xuất V005) trên MySQL 8.4 kiểm thử: guard trong procedure dưới khóa, giao dịch phí sử dụng và
/// webhook đã xác minh. Thời điểm kỳ được đặt theo giờ của database vì procedure so với v_now của database.
/// Bị bỏ qua khi chưa cấu hình DECAKES_TEST_MYSQL_ADMIN. Không dùng Aiven.
/// </summary>
[Collection(MySqlCollection.Name)]
public class MySqlSubscriptionStoreTests(MySqlTestDatabase database)
{
    private static CancellationToken Ct => CancellationToken.None;

    private MySqlSubscriptionStore Store => new(database.Factory);

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

    private async Task<DateTime> DbNowAsync() => (DateTime)(await database.AdminScalarAsync("SELECT UTC_TIMESTAMP(3)"))!;

    private Task<long> PlanAsync(decimal? monthly = 199_000m, decimal? yearly = 1_990_000m, string status = "active") =>
        database.AdminExecuteAsync(
            "INSERT INTO subscription_plans (code, name, status, monthly_price, yearly_price) VALUES (@c, 'Gói', @s, @m, @y)",
            ("@c", "p-" + Guid.NewGuid().ToString("N")[..10]), ("@s", status), ("@m", monthly), ("@y", yearly));

    /// <summary>Tiệm active có owner, staff, gói đã chọn; kỳ sử dụng tùy chọn [start, end).</summary>
    private async Task<(long Owner, long Bakery, long Staff, long Plan)> BakeryAsync(
        DateTime? accessFrom = null, DateTime? accessTo = null, string profileStatus = "active", long? planId = null)
    {
        var (owner, bakery) = await new MySqlBakeryOnboardingStore(database.Factory).CreateOwnerAsync(
            new NewBakeryOwner(UniqueEmail("owner"), "H", "Chủ", null, "Tiệm gói", "tiem-goi"), Ct);
        var staff = await database.AdminExecuteAsync(
            "INSERT INTO users (email, full_name, system_role) VALUES (@e, 'Nhân viên', 'bakery')", ("@e", UniqueEmail("staff")));
        await database.AdminExecuteAsync("INSERT INTO bakery_members (bakery_id, user_id, member_role) VALUES (@b, @u, 'staff')", ("@b", bakery), ("@u", staff));
        await database.AdminExecuteAsync("UPDATE bakeries SET profile_status = @s WHERE id = @b", ("@s", profileStatus), ("@b", bakery));
        var plan = planId ?? await PlanAsync();
        await database.AdminExecuteAsync(
            "INSERT INTO bakery_subscriptions (bakery_id, plan_id, selected_by_user_id) VALUES (@b, @p, @u)", ("@b", bakery), ("@p", plan), ("@u", owner));
        if (accessFrom is not null)
        {
            await database.AdminExecuteAsync(
                "INSERT INTO bakery_subscription_periods (bakery_id, plan_id, period_kind, starts_at, ends_at) VALUES (@b, @p, 'trial', @s, @e)",
                ("@b", bakery), ("@p", plan), ("@s", accessFrom), ("@e", accessTo));
        }

        return (owner, bakery, staff, plan);
    }

    private Task<long> CustomerAsync() =>
        database.AdminExecuteAsync("INSERT INTO users (email, full_name, system_role) VALUES (@e, 'Khách', 'customer')", ("@e", UniqueEmail("c")));

    private Task<long> DraftRequestAsync(long customer) =>
        database.AdminExecuteAsync(
            """
            INSERT INTO design_requests (customer_id, title, idea_description, budget_max, pickup_at, status)
            VALUES (@c, 'Bánh', 'Ý tưởng', 500000, UTC_TIMESTAMP(3) + INTERVAL 10 DAY, 'draft')
            """,
            ("@c", customer));

    private Task<SendToBakeryOutcome> SendAsync(long request, long customer, long bakery) =>
        new MySqlDesignRequestStore(database.Factory).SendToBakeryAsync(request, customer, bakery, Ct);

    private Task ExpireAsync(long bakery) =>
        database.AdminExecuteAsync(
            "UPDATE bakery_subscription_periods SET ends_at = UTC_TIMESTAMP(3) - INTERVAL 1 SECOND, starts_at = LEAST(starts_at, UTC_TIMESTAMP(3) - INTERVAL 2 SECOND) WHERE bakery_id = @b",
            ("@b", bakery));

    /// <summary>Gọi procedure bằng tài khoản backend; trả mã SIGNAL (null nếu thành công) và giá trị OUT.</summary>
    private async Task<(string? Signal, long? Out)> ProcedureAsync(string name, string? outName, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await database.Factory.OpenConnectionAsync(Ct);
        await using var command = new MySqlCommand(name, connection) { CommandType = CommandType.StoredProcedure };
        foreach (var (parameter, value) in parameters) command.Parameters.AddWithValue(parameter, value ?? DBNull.Value);
        MySqlParameter? output = null;
        if (outName is not null)
        {
            output = new MySqlParameter(outName, MySqlDbType.Int64) { Direction = ParameterDirection.Output };
            command.Parameters.Add(output);
        }

        try
        {
            await command.ExecuteNonQueryAsync(Ct);
            return (null, output?.Value is long id ? id : null);
        }
        catch (MySqlException ex) when (ex.SqlState == "45000")
        {
            return (ex.Message, null);
        }
    }

    private Task<(string? Signal, long? Out)> CreateQuoteAsync(long request, long bakery, long actor) =>
        ProcedureAsync("sp_create_quote", "p_quote_id",
            ("p_request_id", request), ("p_bakery_id", bakery), ("p_actor_user_id", actor), ("p_price", 300_000m),
            ("p_size_description", "20 cm"), ("p_servings", null), ("p_flavor", "Vani"), ("p_decoration", "Hoa kem"),
            ("p_inscription", null), ("p_note", null),
            ("p_estimated_pickup_at", DateTime.UtcNow.AddDays(9)), ("p_valid_until", DateTime.UtcNow.AddDays(3)), ("p_design_asset_id", null));

    // ---------------- Guard trong procedure ----------------

    [MySqlFact]
    public async Task Active_bakery_without_any_access_period_cannot_receive_new_requests()
    {
        var (_, bakery, _, _) = await BakeryAsync();
        var customer = await CustomerAsync();
        var request = await DraftRequestAsync(customer);

        Assert.Equal(SendToBakeryOutcome.BakerySubscriptionInactive, await SendAsync(request, customer, bakery));
        Assert.Equal(0, Convert.ToInt64(await database.AdminScalarAsync("SELECT COUNT(*) FROM request_bakeries WHERE design_request_id = @r", ("@r", request))));
        Assert.Equal("draft", await database.AdminScalarAsync("SELECT status FROM design_requests WHERE id = @r", ("@r", request)));
    }

    [MySqlFact]
    public async Task Expiry_blocks_new_requests_quotes_revisions_and_orders_but_not_existing_order_handling()
    {
        var now = await DbNowAsync();
        var (owner, bakery, staff, _) = await BakeryAsync(now.AddDays(-1), now.AddDays(10));
        var customer = await CustomerAsync();
        var first = await DraftRequestAsync(customer);
        var second = await DraftRequestAsync(customer);
        var third = await DraftRequestAsync(customer);
        var unquoted = await DraftRequestAsync(customer);

        // Còn hạn: nhận yêu cầu, staff tạo báo giá, khách chọn tạo đơn.
        Assert.Equal(SendToBakeryOutcome.Sent, await SendAsync(first, customer, bakery));
        Assert.Equal(SendToBakeryOutcome.Sent, await SendAsync(second, customer, bakery));
        Assert.Equal(SendToBakeryOutcome.Sent, await SendAsync(unquoted, customer, bakery));
        var (signal, ordered) = await CreateQuoteAsync(first, bakery, staff);
        Assert.Null(signal);
        var (_, pendingOrder) = await ProcedureAsync("sp_accept_quote", "p_order_id", ("p_quote_id", ordered), ("p_customer_id", customer));
        Assert.NotNull(pendingOrder);
        var (_, beforeExpiry) = await CreateQuoteAsync(second, bakery, owner);
        Assert.NotNull(beforeExpiry);

        await ExpireAsync(bakery);

        Assert.Equal(SendToBakeryOutcome.BakerySubscriptionInactive, await SendAsync(third, customer, bakery));
        // Yêu cầu nhận trước khi hết hạn vẫn không được tạo báo giá mới.
        Assert.Equal("BAKERY_SUBSCRIPTION_INACTIVE", (await CreateQuoteAsync(unquoted, bakery, owner)).Signal);
        Assert.Equal("BAKERY_SUBSCRIPTION_INACTIVE", (await ProcedureAsync("sp_revise_quote", "p_new_quote_id",
            ("p_quote_id", beforeExpiry), ("p_actor_user_id", owner), ("p_price", 320_000m), ("p_size_description", "22 cm"),
            ("p_servings", null), ("p_flavor", "Vani"), ("p_decoration", "Hoa"), ("p_inscription", null), ("p_note", null),
            ("p_estimated_pickup_at", DateTime.UtcNow.AddDays(9)), ("p_valid_until", DateTime.UtcNow.AddDays(3)), ("p_design_asset_id", null))).Signal);
        // Báo giá tạo trước khi hết hạn cũng không được chọn để tạo đơn mới.
        Assert.Equal("BAKERY_SUBSCRIPTION_INACTIVE",
            (await ProcedureAsync("sp_accept_quote", "p_order_id", ("p_quote_id", beforeExpiry), ("p_customer_id", customer))).Signal);
        Assert.Equal("submitted", await database.AdminScalarAsync("SELECT status FROM quotes WHERE id = @q", ("@q", beforeExpiry)));

        // Đơn đã có vẫn xử lý được; rút báo giá vẫn được.
        Assert.Null((await ProcedureAsync("sp_bakery_decide_order", null,
            ("p_order_id", pendingOrder), ("p_actor_user_id", staff), ("p_decision", "confirm"), ("p_reason", null))).Signal);
        Assert.Equal("confirmed", await database.AdminScalarAsync("SELECT status FROM orders WHERE id = @o", ("@o", pendingOrder)));
        Assert.Null((await ProcedureAsync("sp_withdraw_quote", null, ("p_quote_id", beforeExpiry), ("p_actor_user_id", owner))).Signal);
        // Hết hạn không khóa user, không đổi hồ sơ.
        Assert.Equal("active", await database.AdminScalarAsync("SELECT profile_status FROM bakeries WHERE id = @b", ("@b", bakery)));
        Assert.Equal("active", await database.AdminScalarAsync("SELECT status FROM users WHERE id = @u", ("@u", owner)));
    }

    [MySqlFact]
    public async Task Access_ending_while_procedure_waits_for_lock_is_checked_after_the_wait()
    {
        var now = await DbNowAsync();
        // Kỳ còn 2 giây khi bắt đầu; khóa yêu cầu được giữ 3 giây nên lúc procedure lấy được khóa thì kỳ đã hết.
        var (_, bakery, _, _) = await BakeryAsync(now.AddDays(-1), now.AddSeconds(2));
        var customer = await CustomerAsync();
        var request = await DraftRequestAsync(customer);

        await using var holder = await database.AdminFactory.OpenConnectionAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await using (var lockRow = new MySqlCommand("SELECT id FROM design_requests WHERE id = @r FOR UPDATE", holder, transaction))
        {
            lockRow.Parameters.AddWithValue("@r", request);
            await lockRow.ExecuteScalarAsync(Ct);
        }

        var send = Task.Run(() => SendAsync(request, customer, bakery));
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.False(send.IsCompleted);
        await transaction.CommitAsync(Ct);

        Assert.Equal(SendToBakeryOutcome.BakerySubscriptionInactive, await send);
    }

    // ---------------- Checkout ----------------

    [MySqlFact]
    public async Task Checkout_snapshots_server_price_for_owner_of_active_bakery_only()
    {
        var now = await DbNowAsync();
        var (owner, bakery, staff, plan) = await BakeryAsync(now.AddDays(-1), now.AddDays(5));
        var payment = new NewSubscriptionPayment(owner, bakery, BillingCycles.Year, "payos", Random.Shared.NextInt64(1, 1L << 40), now);

        var created = await Store.CreatePaymentAsync(payment, Ct);

        Assert.Equal((CheckoutOutcome.Created, 1_990_000m, "VND"), (created.Status, created.Amount, created.Currency));
        Assert.Equal(CheckoutOutcome.OwnerRequired, (await Store.CreatePaymentAsync(payment with { OwnerUserId = staff, OrderCode = payment.OrderCode + 1 }, Ct)).Status);
        await Assert.ThrowsAsync<DuplicateOrderCodeException>(() => Store.CreatePaymentAsync(payment, Ct));

        // Sửa giá danh mục sau đó không đổi giao dịch đã chốt.
        await database.AdminExecuteAsync("UPDATE subscription_plans SET yearly_price = 2500000 WHERE id = @p", ("@p", plan));
        var (_, stored) = await Store.FindPaymentAsync(owner, bakery, created.PaymentId, Ct);
        Assert.Equal((1_990_000m, "created", BillingCycles.Year), (stored!.Amount, stored.Status, stored.BillingCycle));
        Assert.Equal(OwnerActionOutcome.OwnerRequired, (await Store.FindPaymentAsync(staff, bakery, created.PaymentId, Ct)).Outcome);
    }

    [MySqlFact]
    public async Task Checkout_is_refused_for_unpriced_unavailable_or_unselected_plans_and_unapproved_profiles()
    {
        var now = await DbNowAsync();
        NewSubscriptionPayment For(long owner, long bakery, string cycle = "month") =>
            new(owner, bakery, cycle, "payos", Random.Shared.NextInt64(1, 1L << 40), now);

        var unpriced = await BakeryAsync(planId: await PlanAsync(monthly: null, yearly: 1_000_000m));
        Assert.Equal(CheckoutOutcome.PlanNotPriced, (await Store.CreatePaymentAsync(For(unpriced.Owner, unpriced.Bakery), Ct)).Status);
        Assert.Equal(CheckoutOutcome.Created, (await Store.CreatePaymentAsync(For(unpriced.Owner, unpriced.Bakery, "year"), Ct)).Status);

        var retired = await BakeryAsync(planId: await PlanAsync(status: "retired"));
        Assert.Equal(CheckoutOutcome.PlanNotAvailable, (await Store.CreatePaymentAsync(For(retired.Owner, retired.Bakery), Ct)).Status);

        var noPlan = await BakeryAsync();
        await database.AdminExecuteAsync("DELETE FROM bakery_subscriptions WHERE bakery_id = @b", ("@b", noPlan.Bakery));
        Assert.Equal(CheckoutOutcome.PlanNotSelected, (await Store.CreatePaymentAsync(For(noPlan.Owner, noPlan.Bakery), Ct)).Status);

        foreach (var status in new[] { "draft", "pending_review", "suspended", "rejected" })
        {
            var bakery = await BakeryAsync(profileStatus: status);
            Assert.Equal(CheckoutOutcome.ProfileNotActive, (await Store.CreatePaymentAsync(For(bakery.Owner, bakery.Bakery), Ct)).Status);
        }

        // Chỉ chu kỳ có giá tạo được giao dịch; các trường hợp bị từ chối không để lại dòng nào.
        Assert.Equal(1, Convert.ToInt64(await database.AdminScalarAsync(
            "SELECT COUNT(*) FROM subscription_payments WHERE bakery_id = @b", ("@b", unpriced.Bakery))));
        Assert.Equal(0, Convert.ToInt64(await database.AdminScalarAsync(
            "SELECT COUNT(*) FROM subscription_payments WHERE bakery_id IN (@a, @b)", ("@a", retired.Bakery), ("@b", noPlan.Bakery))));
    }

    // ---------------- Webhook đã xác minh ----------------

    private async Task<(long PaymentId, long OrderCode, long Owner, long Bakery)> PendingPaymentAsync(
        DateTime? accessTo = null, string cycle = BillingCycles.Month, string profileStatus = "active")
    {
        var now = await DbNowAsync();
        var (owner, bakery, _, _) = await BakeryAsync(now.AddDays(-20), accessTo ?? now.AddDays(10));
        var orderCode = Random.Shared.NextInt64(1, 1L << 40);
        var created = await Store.CreatePaymentAsync(new NewSubscriptionPayment(owner, bakery, cycle, "payos", orderCode, now), Ct);
        await Store.MarkPaymentLinkAsync(created.PaymentId, "link-" + orderCode, "https://pay.example/" + orderCode, now, Ct);
        if (profileStatus != "active")
        {
            await database.AdminExecuteAsync("UPDATE bakeries SET profile_status = @s WHERE id = @b", ("@s", profileStatus), ("@b", bakery));
        }

        return (created.PaymentId, orderCode, owner, bakery);
    }

    private static VerifiedPaymentNotice Notice(long orderCode, decimal amount = 199_000m, string? currency = "VND", string? link = null) =>
        new("payos", orderCode, amount, currency, link ?? "link-" + orderCode, "FT123");

    private async Task<List<(string Kind, DateTime Start, DateTime End, long? Payment)>> PeriodsAsync(long bakery)
    {
        await using var connection = await database.AdminFactory.OpenConnectionAsync(Ct);
        await using var command = MySqlTestDatabase.Command(connection,
            "SELECT period_kind, starts_at, ends_at, payment_id FROM bakery_subscription_periods WHERE bakery_id = @b ORDER BY starts_at, id", ("@b", bakery));
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<(string, DateTime, DateTime, long?)>();
        while (await reader.ReadAsync(Ct)) rows.Add((reader.GetString(0), reader.GetDateTime(1), reader.GetDateTime(2), reader.IsDBNull(3) ? null : reader.GetInt64(3)));
        return rows;
    }

    [MySqlFact]
    public async Task Verified_payment_appends_one_calendar_period_after_remaining_trial()
    {
        var (paymentId, orderCode, owner, bakery) = await PendingPaymentAsync();
        var trialEnd = (await PeriodsAsync(bakery))[0].End;
        var paidAt = trialEnd.AddDays(-10);

        Assert.Equal(PaymentApplyOutcome.Activated, await Store.ApplyVerifiedPaymentAsync(Notice(orderCode), paidAt, Ct));

        var paid = (await PeriodsAsync(bakery))[1];
        Assert.Equal(("paid", trialEnd, trialEnd.AddMonths(1), (long?)paymentId), paid);
        var (_, payment) = await Store.FindPaymentAsync(owner, bakery, paymentId, Ct);
        Assert.Equal(("paid", (DateTime?)paidAt), (payment!.Status, payment.PaidAt));
        var status = await Store.FindStatusAsync(owner, bakery, Ct);
        Assert.Equal(["trial", "paid"], status!.Periods.Select(p => p.Kind));
    }

    [MySqlFact]
    public async Task Late_payment_after_expiry_starts_at_payment_time_and_does_not_reopen_suspended_profile()
    {
        var now = await DbNowAsync();
        var (paymentId, orderCode, _, bakery) = await PendingPaymentAsync(accessTo: now.AddDays(-2), cycle: BillingCycles.Year, profileStatus: "suspended");

        Assert.Equal(PaymentApplyOutcome.Activated, await Store.ApplyVerifiedPaymentAsync(Notice(orderCode, 1_990_000m), now, Ct));

        Assert.Equal(("paid", now, now.AddYears(1), (long?)paymentId), (await PeriodsAsync(bakery))[1]);
        Assert.Equal("suspended", await database.AdminScalarAsync("SELECT profile_status FROM bakeries WHERE id = @b", ("@b", bakery)));
    }

    [MySqlFact]
    public async Task Duplicate_and_concurrent_webhooks_extend_exactly_once()
    {
        var (_, orderCode, _, bakery) = await PendingPaymentAsync();
        var now = await DbNowAsync();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => Store.ApplyVerifiedPaymentAsync(Notice(orderCode), now, Ct))));
        var late = await Store.ApplyVerifiedPaymentAsync(Notice(orderCode), now.AddDays(40), Ct);

        Assert.Equal(1, outcomes.Count(o => o == PaymentApplyOutcome.Activated));
        Assert.Equal(9, outcomes.Count(o => o == PaymentApplyOutcome.AlreadyProcessed));
        Assert.Equal(PaymentApplyOutcome.AlreadyProcessed, late);
        Assert.Single(await PeriodsAsync(bakery), p => p.Kind == "paid");
    }

    [MySqlFact]
    public async Task Two_payments_of_one_bakery_processed_concurrently_never_overlap()
    {
        var (_, firstCode, owner, bakery) = await PendingPaymentAsync();
        var now = await DbNowAsync();
        var secondCode = firstCode + 7;
        var second = await Store.CreatePaymentAsync(new NewSubscriptionPayment(owner, bakery, BillingCycles.Month, "payos", secondCode, now), Ct);
        await Store.MarkPaymentLinkAsync(second.PaymentId, "link-" + secondCode, "https://pay.example/x", now, Ct);

        var results = await Task.WhenAll(
            Task.Run(() => Store.ApplyVerifiedPaymentAsync(Notice(firstCode), now, Ct)),
            Task.Run(() => Store.ApplyVerifiedPaymentAsync(Notice(secondCode), now, Ct)));

        Assert.All(results, r => Assert.Equal(PaymentApplyOutcome.Activated, r));
        var periods = await PeriodsAsync(bakery);
        Assert.Equal(3, periods.Count);
        Assert.Equal(periods[0].End, periods[1].Start);
        Assert.Equal(periods[1].End, periods[2].Start);
    }

    [MySqlFact]
    public async Task Mismatched_or_unknown_payments_never_grant_access()
    {
        foreach (var kind in new[] { "amount", "currency", "link" })
        {
            var (paymentId, orderCode, owner, bakery) = await PendingPaymentAsync();
            var now = await DbNowAsync();
            var wrong = kind switch
            {
                "amount" => Notice(orderCode, amount: 1_000m),
                "currency" => Notice(orderCode, currency: "USD"),
                _ => Notice(orderCode, link: "link-khac"),
            };

            Assert.Equal(PaymentApplyOutcome.Mismatch, await Store.ApplyVerifiedPaymentAsync(wrong, now, Ct));
            // Sau khi bị đánh dấu cần đối soát, thông báo đúng cũng không tự cấp quyền.
            Assert.Equal(PaymentApplyOutcome.AlreadyProcessed, await Store.ApplyVerifiedPaymentAsync(Notice(orderCode), now, Ct));
            Assert.DoesNotContain(await PeriodsAsync(bakery), p => p.Kind == "paid");
            Assert.Equal("mismatch", (await Store.FindPaymentAsync(owner, bakery, paymentId, Ct)).Payment!.Status);
        }

        Assert.Equal(PaymentApplyOutcome.UnknownPayment,
            await Store.ApplyVerifiedPaymentAsync(Notice(long.MaxValue - 3), await DbNowAsync(), Ct));
    }

    [MySqlFact]
    public async Task Status_is_visible_to_members_only()
    {
        var now = await DbNowAsync();
        var (owner, bakery, staff, _) = await BakeryAsync(now.AddDays(-1), now.AddDays(29));
        var other = await BakeryAsync();

        Assert.Single((await Store.FindStatusAsync(staff, bakery, Ct))!.Periods);
        Assert.NotNull(await Store.FindStatusAsync(owner, bakery, Ct));
        Assert.Null(await Store.FindStatusAsync(other.Owner, bakery, Ct));
    }

    [MySqlFact]
    public async Task Backend_account_cannot_rewrite_periods_or_delete_payments()
    {
        await using var connection = await database.Factory.OpenConnectionAsync(Ct);
        foreach (var sql in new[]
                 {
                     "UPDATE bakery_subscription_periods SET ends_at = ends_at WHERE id = -1",
                     "DELETE FROM subscription_payments WHERE id = -1",
                     "UPDATE subscription_plans SET monthly_price = 1 WHERE id = -1",
                 })
        {
            await using var command = new MySqlCommand(sql, connection);
            var error = await Assert.ThrowsAsync<MySqlException>(() => command.ExecuteNonQueryAsync(Ct));
            Assert.Contains(error.ErrorCode, new[] { MySqlErrorCode.TableAccessDenied, MySqlErrorCode.ColumnAccessDenied });
        }
    }
}
