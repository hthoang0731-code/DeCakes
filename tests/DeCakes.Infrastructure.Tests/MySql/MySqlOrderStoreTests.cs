using DeCakes.Application.DesignRequests;
using DeCakes.Application.Orders;
using DeCakes.Application.Quotes;
using DeCakes.Infrastructure.DesignRequests;
using DeCakes.Infrastructure.Orders;
using DeCakes.Infrastructure.Quotes;
using MySqlConnector;

namespace DeCakes.Infrastructure.Tests.MySql;

/// <summary>
/// Chọn báo giá, đọc đơn và quyết định đơn qua procedure V006 bằng đúng quyền backend, trên MySQL 8.4 loopback disposable.
/// Các ca chờ khóa giữ khóa bằng kết nối quản trị kiểm thử, chỉ trong database tạm.
/// </summary>
[Collection(MySqlCollection.Name)]
public class MySqlOrderStoreTests(MySqlTestDatabase database)
{
    private static CancellationToken Ct => CancellationToken.None;
    private MySqlOrderStore Store => new(database.Factory);
    private MySqlQuoteStore Quotes => new(database.Factory);

    // Toàn bộ dòng đơn (gồm agreed_design_url và người quyết định) để chứng minh không bị đổi.
    private const string OrderRowSql =
        """
        SELECT CONCAT_WS('|', id, quote_id, design_request_id, customer_id, bakery_id, agreed_quote_revision_no, agreed_price, currency,
               agreed_pickup_at, IFNULL(agreed_size_description, '-'), IFNULL(agreed_servings, '-'), agreed_flavor,
               agreed_decoration_description, IFNULL(agreed_inscription_text, '-'), IFNULL(agreed_note, '-'),
               IFNULL(agreed_design_asset_id, '-'), IFNULL(agreed_design_asset_kind, '-'), IFNULL(agreed_design_version_no, '-'),
               IFNULL(agreed_design_url, '-'), status, IFNULL(bakery_decided_at, '-'), IFNULL(bakery_decided_by_user_id, '-'),
               IFNULL(rejection_reason, '-'), IFNULL(picked_up_at, '-'), IFNULL(cancelled_at, '-'), created_at, updated_at)
          FROM orders WHERE id = @o
        """;

    private static DateTime Ms(DateTime value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    private static QuoteValues Values(decimal price = 1_500_000.50m, long? asset = null) =>
        new(price, "22 cm", 14, "Socola 🍫", "Hoa kem\nMàu hồng", "Chúc mừng", "Ghi chú của tiệm",
            Ms(DateTime.UtcNow.AddDays(12)), Ms(DateTime.UtcNow.AddDays(3)), asset);

    private Task<long> UserAsync(string role = "customer") => database.AdminExecuteAsync(
        "INSERT INTO users(email,full_name,system_role,status) VALUES (@email,'Người dùng kiểm thử',@role,'active')",
        ("@email", Guid.NewGuid().ToString("N") + "@example.com"), ("@role", role));

    private async Task<long> BakeryAsync(long owner)
    {
        var bakery = await database.SeedBakeryAsync("Tiệm " + Guid.NewGuid().ToString("N"));
        await MemberAsync(bakery, owner, "owner");
        await database.GrantAccessAsync(bakery);
        return bakery;
    }

    private Task<long> MemberAsync(long bakery, long user, string role = "staff") => database.AdminExecuteAsync(
        "INSERT INTO bakery_members(bakery_id,user_id,member_role) VALUES (@b,@u,@r)", ("@b", bakery), ("@u", user), ("@r", role));

    // Ngân sách tối đa 1 000 000, giờ nhận mong muốn sau 7 ngày.
    private async Task<long> RequestAsync(long customer, params long[] bakeries)
    {
        var store = new MySqlDesignRequestStore(database.Factory);
        var request = await store.CreateDraftAsync(customer, new DesignRequestValues("Bánh", "Hoa trang trí", null,
            1_000_000m, "20 cm", 10, "Vani", null, DateTime.UtcNow.AddDays(7), null), Ct);
        foreach (var bakery in bakeries)
            Assert.Equal(SendToBakeryOutcome.Sent, await store.SendToBakeryAsync(request, customer, bakery, Ct));
        return request;
    }

    private async Task<long> QuoteAsync(long actor, long bakery, long request, QuoteValues? values = null)
    {
        var result = await Quotes.CreateAsync(actor, bakery, request, values ?? Values(), Ct);
        Assert.Equal(QuoteWriteOutcome.Saved, result.Outcome);
        return result.QuoteId!.Value;
    }

    private async Task<long> AcceptedAsync(long customer, long request, long quote)
    {
        var result = await Store.AcceptAsync(customer, request, quote, Ct);
        Assert.Equal(OrderWriteOutcome.Saved, result.Outcome);
        return result.OrderId!.Value;
    }

    // Đơn chờ xác nhận của một khách mới với một tiệm mới; trả về đủ định danh để kiểm thử quyết định.
    private async Task<(long Customer, long Owner, long Bakery, long Request, long Quote, long Order)> PendingOrderAsync()
    {
        var customer = await UserAsync();
        var owner = await UserAsync("bakery");
        var bakery = await BakeryAsync(owner);
        var request = await RequestAsync(customer, bakery);
        var quote = await QuoteAsync(owner, bakery, request);
        return (customer, owner, bakery, request, quote, await AcceptedAsync(customer, request, quote));
    }

    private async Task<string> OrderRowAsync(long order) => (string)(await database.AdminScalarAsync(OrderRowSql, ("@o", order)))!;

    private async Task<string?> StatusAsync(string table, long id) =>
        (string?)await database.AdminScalarAsync($"SELECT status FROM {table} WHERE id = @id", ("@id", id));

    private async Task<long> OrderCountAsync(long request) => Convert.ToInt64(await database.AdminScalarAsync(
        "SELECT COUNT(*) FROM orders WHERE design_request_id = @r", ("@r", request)));

    private async Task<DateTime> DbNowAsync() => (DateTime)(await database.AdminScalarAsync("SELECT UTC_TIMESTAMP(3)"))!;

    /// <summary>
    /// Giữ khóa bằng một transaction quản trị, chạy thao tác trên connection khác, đợi tới khi thao tác thật sự chờ khóa trong
    /// database kiểm thử (trước <paramref name="deadline"/>), giữ khóa tới khi mốc thời gian đã qua rồi mới COMMIT.
    /// </summary>
    private async Task<OrderWriteResult> WhileLockedAsync(
        string lockSql, (string Name, object? Value) parameter, DateTime deadline, Func<Task<OrderWriteResult>> action)
    {
        await using var holder = await database.AdminFactory.OpenConnectionAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await using (var hold = MySqlTestDatabase.Command(holder, lockSql, parameter))
        {
            hold.Transaction = transaction;
            await hold.ExecuteNonQueryAsync(Ct);
        }

        var pending = Task.Run(action);
        var waiting = false;
        for (var attempt = 0; attempt < 100 && !waiting && !pending.IsCompleted; attempt++)
        {
            waiting = Convert.ToInt64(await database.AdminScalarAsync(
                """
                SELECT COUNT(*) FROM information_schema.innodb_trx t
                  JOIN performance_schema.threads th ON th.PROCESSLIST_ID = t.trx_mysql_thread_id
                 WHERE t.trx_state = 'LOCK WAIT' AND th.PROCESSLIST_DB = @db
                """, ("@db", database.DatabaseName))) > 0;
            if (!waiting) await Task.Delay(20, Ct);
        }

        Assert.True(waiting, "Thao tác không chờ khóa như kịch bản.");
        Assert.True(await DbNowAsync() < deadline, "Mốc thời gian đã qua trước khi thao tác chờ khóa.");
        while (await DbNowAsync() <= deadline.AddMilliseconds(200)) await Task.Delay(100, Ct);
        await transaction.CommitAsync(Ct);
        return await pending;
    }

    [MySqlFact]
    public async Task Accept_returns_the_out_id_and_snapshots_the_quote_exactly_and_permanently()
    {
        var customer = await UserAsync();
        var owner = await UserAsync("bakery");
        var other = await UserAsync("bakery");
        var bakery = await BakeryAsync(owner);
        var competitor = await BakeryAsync(other);
        var request = await RequestAsync(customer, bakery, competitor);
        var asset = await database.AdminExecuteAsync(
            """
            INSERT INTO media_assets(asset_kind,storage_url,mime_type,uploaded_by_user_id,owner_bakery_id,design_request_id,version_no)
            VALUES ('design_proposal','https://example.com/thiet-ke-v3.png','image/png',@u,@b,@r,3)
            """, ("@u", owner), ("@b", bakery), ("@r", request));
        // Giá vượt ngân sách 1 000 000 và giờ nhận khác yêu cầu: đơn chốt đúng phiên bản báo giá.
        var first = await QuoteAsync(owner, bakery, request, Values(900_000m));
        var values = Values(9_999_999_999.99m, asset);
        var quote = (await Quotes.ReviseAsync(owner, bakery, request, first, values, Ct)).QuoteId!.Value;
        var losing = await QuoteAsync(other, competitor, request);

        var result = await Store.AcceptAsync(customer, request, quote, Ct);

        Assert.Equal(OrderWriteOutcome.Saved, result.Outcome);
        Assert.Equal(result.OrderId, Convert.ToInt64(await database.AdminScalarAsync("SELECT id FROM orders WHERE design_request_id=@r", ("@r", request))));
        var order = (await Store.FindForCustomerAsync(customer, result.OrderId!.Value, Ct)).Order!;
        var stored = (await Quotes.FindForCustomerAsync(customer, request, quote, Ct)).Quote!;
        Assert.Equal((request, quote, bakery, "pending_confirmation", 2L, "VND"),
            (order.RequestId, order.QuoteId, order.BakeryId, order.Status, order.AgreedQuoteRevisionNo, order.Currency));
        Assert.Equal((stored.Price, stored.EstimatedPickupAt, stored.SizeDescription, stored.Servings, stored.Flavor, stored.DecorationDescription,
                stored.InscriptionText, stored.Note),
            (order.AgreedPrice, order.AgreedPickupAt, order.AgreedSizeDescription, order.AgreedServings, order.AgreedFlavor,
                order.AgreedDecorationDescription, order.AgreedInscriptionText, order.AgreedNote));
        Assert.Equal((9_999_999_999.99m, values.EstimatedPickupAtUtc), (order.AgreedPrice, order.AgreedPickupAt));
        Assert.Equal((asset, "design_proposal", 3L), (order.AgreedDesignAssetId!.Value, order.AgreedDesignAssetKind, order.AgreedDesignVersionNo!.Value));
        Assert.Equal("https://example.com/thiet-ke-v3.png", await database.AdminScalarAsync("SELECT agreed_design_url FROM orders WHERE id=@o", ("@o", order.Id)));
        Assert.Null(order.BakeryDecidedAt);
        Assert.Null(order.RejectionReason);
        Assert.Equal(DateTimeKind.Utc, order.CreatedAt.Kind);
        Assert.Equal(("accepted", "not_selected", "superseded", "ordered"),
            (await StatusAsync("quotes", quote), await StatusAsync("quotes", losing), await StatusAsync("quotes", first), await StatusAsync("design_requests", request)));

        // Nguồn bị sửa trực tiếp sau khi chốt: đơn không đổi.
        var before = await OrderRowAsync(order.Id);
        await database.AdminExecuteAsync("UPDATE quotes SET price=1, flavor='Đổi', note=NULL WHERE id=@q", ("@q", quote));
        await database.AdminExecuteAsync("UPDATE media_assets SET storage_url='https://example.com/khac.png', version_no=9 WHERE id=@a", ("@a", asset));
        await database.AdminExecuteAsync("UPDATE design_requests SET budget_max=5, flavor='Khác' WHERE id=@r", ("@r", request));
        Assert.Equal(before, await OrderRowAsync(order.Id));
        Assert.Equal(order, (await Store.FindForCustomerAsync(customer, order.Id, Ct)).Order);
        Assert.Equal(order, (await Store.FindForBakeryAsync(owner, bakery, order.Id, Ct)).Order);
    }

    [MySqlFact]
    public async Task Route_scope_and_ownership_are_checked_before_call_and_hide_other_customers_orders()
    {
        var (customer, owner, bakery, request, quote, order) = await PendingOrderAsync();
        var stranger = await UserAsync();
        var strangerRequest = await RequestAsync(stranger, bakery);
        var strangerQuote = await QuoteAsync(owner, bakery, strangerRequest);
        var mine = await RequestAsync(customer, bakery);

        Assert.Equal(OrderWriteOutcome.RequestNotFound, (await Store.AcceptAsync(stranger, request, quote, Ct)).Outcome);
        // Báo giá của yêu cầu khác qua route yêu cầu của mình: chặn trước CALL.
        Assert.Equal(OrderWriteOutcome.QuoteNotFound, (await Store.AcceptAsync(customer, mine, strangerQuote, Ct)).Outcome);
        Assert.Equal(OrderWriteOutcome.QuoteNotFound, (await Store.AcceptAsync(customer, mine, long.MaxValue, Ct)).Outcome);
        Assert.Equal("submitted", await StatusAsync("quotes", strangerQuote));
        Assert.Equal(0, await OrderCountAsync(strangerRequest));

        Assert.Equal(OrderReadOutcome.OrderNotFound, (await Store.FindForCustomerAsync(stranger, order, Ct)).Outcome);
        Assert.Equal(OrderReadOutcome.RequestNotFound, (await Store.FindForCustomerRequestAsync(stranger, request, Ct)).Outcome);
        Assert.Equal(OrderReadOutcome.OrderNotFound, (await Store.FindForCustomerRequestAsync(customer, mine, Ct)).Outcome);
        Assert.Equal(order, (await Store.FindForCustomerRequestAsync(customer, request, Ct)).Order!.Id);
        Assert.Empty((await Store.ListForCustomerAsync(stranger, null, 1, 20, Ct)).Page!.Items);

        await database.AdminExecuteAsync("UPDATE users SET status='disabled' WHERE id=@u", ("@u", customer));
        Assert.Equal(OrderReadOutcome.OrderNotFound, (await Store.FindForCustomerAsync(customer, order, Ct)).Outcome);
        Assert.Equal(OrderReadOutcome.RequestNotFound, (await Store.FindForCustomerRequestAsync(customer, request, Ct)).Outcome);
        Assert.Empty((await Store.ListForCustomerAsync(customer, null, 1, 20, Ct)).Page!.Items);
    }

    [MySqlFact]
    public async Task Concurrent_accepts_of_two_quotes_of_one_request_create_exactly_one_order()
    {
        var customer = await UserAsync();
        var ownerA = await UserAsync("bakery");
        var ownerB = await UserAsync("bakery");
        var bakeryA = await BakeryAsync(ownerA);
        var bakeryB = await BakeryAsync(ownerB);
        var request = await RequestAsync(customer, bakeryA, bakeryB);
        var quotes = new[] { await QuoteAsync(ownerA, bakeryA, request), await QuoteAsync(ownerB, bakeryB, request) };

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(() => Store.AcceptAsync(customer, request, quotes[i % 2], Ct))));

        var winner = Assert.Single(results, r => r.Outcome == OrderWriteOutcome.Saved);
        Assert.All(results.Where(r => r.Outcome != OrderWriteOutcome.Saved), r => Assert.Equal(OrderWriteOutcome.RequestNotOpen, r.Outcome));
        Assert.Equal(1, await OrderCountAsync(request));
        var accepted = (await Store.FindForCustomerAsync(customer, winner.OrderId!.Value, Ct)).Order!.QuoteId;
        Assert.Equal(("accepted", "not_selected"), (await StatusAsync("quotes", accepted), await StatusAsync("quotes", quotes.Single(q => q != accepted))));
        // Chọn lại chính báo giá đã accepted cũng là xung đột, không tạo đơn thứ hai.
        Assert.Equal(OrderWriteOutcome.RequestNotOpen, (await Store.AcceptAsync(customer, request, accepted, Ct)).Outcome);
    }

    [MySqlFact]
    public async Task Superseded_withdrawn_and_expired_quotes_or_legacy_quotes_with_past_pickup_are_rejected()
    {
        var customer = await UserAsync();
        var owner = await UserAsync("bakery");
        var bakery = await BakeryAsync(owner);

        var revisedRequest = await RequestAsync(customer, bakery);
        var old = await QuoteAsync(owner, bakery, revisedRequest);
        Assert.Equal(QuoteWriteOutcome.Saved, (await Quotes.ReviseAsync(owner, bakery, revisedRequest, old, Values(), Ct)).Outcome);
        Assert.Equal(OrderWriteOutcome.QuoteNotAvailable, (await Store.AcceptAsync(customer, revisedRequest, old, Ct)).Outcome);

        var withdrawnRequest = await RequestAsync(customer, bakery);
        var withdrawn = await QuoteAsync(owner, bakery, withdrawnRequest);
        Assert.Equal(QuoteWriteOutcome.Saved, (await Quotes.WithdrawAsync(owner, bakery, withdrawnRequest, withdrawn, Ct)).Outcome);
        Assert.Equal(OrderWriteOutcome.QuoteNotAvailable, (await Store.AcceptAsync(customer, withdrawnRequest, withdrawn, Ct)).Outcome);

        var expiredRequest = await RequestAsync(customer, bakery);
        var expired = await QuoteAsync(owner, bakery, expiredRequest);
        await database.AdminExecuteAsync(
            "UPDATE quotes SET created_at=UTC_TIMESTAMP(3)-INTERVAL 3 DAY, valid_until=UTC_TIMESTAMP(3)-INTERVAL 1 SECOND WHERE id=@q", ("@q", expired));
        Assert.Equal(OrderWriteOutcome.QuoteExpired, (await Store.AcceptAsync(customer, expiredRequest, expired, Ct)).Outcome);

        // Báo giá cũ (trước quy tắc validUntil <= estimatedPickupAt): chưa hết hạn nhưng giờ nhận đã qua.
        var legacyRequest = await RequestAsync(customer, bakery);
        var legacy = await QuoteAsync(owner, bakery, legacyRequest);
        await database.AdminExecuteAsync(
            """
            UPDATE quotes SET created_at=UTC_TIMESTAMP(3)-INTERVAL 3 DAY, estimated_pickup_at=UTC_TIMESTAMP(3)-INTERVAL 1 HOUR,
                   valid_until=UTC_TIMESTAMP(3)+INTERVAL 1 DAY WHERE id=@q
            """, ("@q", legacy));
        Assert.Equal(OrderWriteOutcome.QuotePickupPassed, (await Store.AcceptAsync(customer, legacyRequest, legacy, Ct)).Outcome);

        foreach (var (request, quote) in new[] { (revisedRequest, old), (withdrawnRequest, withdrawn), (expiredRequest, expired), (legacyRequest, legacy) })
        {
            Assert.Equal(0, await OrderCountAsync(request));
            Assert.Equal("open", await StatusAsync("design_requests", request));
        }

        Assert.Equal(("submitted", "submitted"), (await StatusAsync("quotes", expired), await StatusAsync("quotes", legacy)));
    }

    [MySqlFact]
    public async Task Expiry_while_waiting_for_the_request_lock_is_checked_after_the_wait()
    {
        var customer = await UserAsync();
        var owner = await UserAsync("bakery");
        var bakery = await BakeryAsync(owner);
        var request = await RequestAsync(customer, bakery);
        var quote = await QuoteAsync(owner, bakery, request);
        var deadline = (await DbNowAsync()).AddMilliseconds(1500);
        await database.AdminExecuteAsync("UPDATE quotes SET valid_until=@d WHERE id=@q", ("@d", deadline), ("@q", quote));

        var result = await WhileLockedAsync("SELECT id FROM design_requests WHERE id=@r FOR UPDATE", ("@r", request), deadline,
            () => Store.AcceptAsync(customer, request, quote, Ct));

        Assert.Equal(OrderWriteOutcome.QuoteExpired, result.Outcome);
        Assert.Equal(0, await OrderCountAsync(request));
    }

    [MySqlFact]
    public async Task Pickup_passing_while_the_entitlement_guard_waits_for_its_lock_is_checked_after_the_wait()
    {
        var customer = await UserAsync();
        var owner = await UserAsync("bakery");
        var bakery = await BakeryAsync(owner);
        var request = await RequestAsync(customer, bakery);
        var quote = await QuoteAsync(owner, bakery, request);
        var deadline = (await DbNowAsync()).AddMilliseconds(1500);
        await database.AdminExecuteAsync(
            "UPDATE quotes SET estimated_pickup_at=@d, valid_until=UTC_TIMESTAMP(3)+INTERVAL 1 DAY WHERE id=@q", ("@d", deadline), ("@q", quote));

        // V005 lấy giờ trước guard gói nên sẽ tạo đơn với giờ nhận đã qua; V006 lấy lại giờ sau guard.
        var result = await WhileLockedAsync("SELECT id FROM bakery_subscription_periods WHERE bakery_id=@b FOR UPDATE", ("@b", bakery), deadline,
            () => Store.AcceptAsync(customer, request, quote, Ct));

        Assert.Equal(OrderWriteOutcome.QuotePickupPassed, result.Outcome);
        Assert.Equal(0, await OrderCountAsync(request));
        Assert.Equal(("submitted", "open"), (await StatusAsync("quotes", quote), await StatusAsync("design_requests", request)));
    }

    [MySqlFact]
    public async Task Confirm_after_pickup_is_blocked_while_reject_requires_a_reason_and_cancels_the_request_for_good()
    {
        var (customer, owner, bakery, request, quote, order) = await PendingOrderAsync();
        await database.AdminExecuteAsync("UPDATE orders SET agreed_pickup_at=UTC_TIMESTAMP(3)-INTERVAL 1 SECOND WHERE id=@o", ("@o", order));
        var before = await OrderRowAsync(order);

        Assert.Equal(OrderWriteOutcome.OrderPickupPassed, (await Store.DecideAsync(owner, bakery, order, "confirm", null, Ct)).Outcome);
        // Bỏ qua service để chạm kiểm tra của procedure: NULL và chuỗi chỉ có khoảng trắng.
        Assert.Equal(OrderWriteOutcome.RejectionReasonRequired, (await Store.DecideAsync(owner, bakery, order, "reject", null, Ct)).Outcome);
        Assert.Equal(OrderWriteOutcome.RejectionReasonRequired, (await Store.DecideAsync(owner, bakery, order, "reject", "   ", Ct)).Outcome);
        Assert.Equal(before, await OrderRowAsync(order));

        var result = await Store.DecideAsync(owner, bakery, order, "reject", "Hết nguyên liệu 🍓", Ct);

        Assert.Equal(new OrderWriteResult(OrderWriteOutcome.Saved, order), result);
        var rejected = (await Store.FindForBakeryAsync(owner, bakery, order, Ct)).Order!;
        Assert.Equal(("rejected_by_bakery", "Hết nguyên liệu 🍓"), (rejected.Status, rejected.RejectionReason));
        Assert.NotNull(rejected.BakeryDecidedAt);
        Assert.Equal(owner, Convert.ToInt64(await database.AdminScalarAsync("SELECT bakery_decided_by_user_id FROM orders WHERE id=@o", ("@o", order))));
        Assert.Equal(("cancelled", "accepted"), (await StatusAsync("design_requests", request), await StatusAsync("quotes", quote)));
        var decided = await OrderRowAsync(order);

        Assert.Equal(OrderWriteOutcome.OrderNotPending, (await Store.DecideAsync(owner, bakery, order, "reject", "Lần hai", Ct)).Outcome);
        Assert.Equal(OrderWriteOutcome.OrderNotPending, (await Store.DecideAsync(owner, bakery, order, "confirm", null, Ct)).Outcome);
        Assert.Equal(decided, await OrderRowAsync(order));
        // Không mở lại yêu cầu, không tạo được đơn thứ hai.
        Assert.Equal(OrderWriteOutcome.RequestNotOpen, (await Store.AcceptAsync(customer, request, quote, Ct)).Outcome);
        Assert.Equal(1, await OrderCountAsync(request));
        Assert.Equal(rejected, (await Store.FindForCustomerRequestAsync(customer, request, Ct)).Order);
    }

    [MySqlFact]
    public async Task Pickup_passing_while_confirm_waits_for_the_request_lock_is_checked_after_the_wait()
    {
        var (_, owner, bakery, request, _, order) = await PendingOrderAsync();
        var deadline = (await DbNowAsync()).AddMilliseconds(1500);
        await database.AdminExecuteAsync("UPDATE orders SET agreed_pickup_at=@d WHERE id=@o", ("@d", deadline), ("@o", order));

        var result = await WhileLockedAsync("SELECT id FROM design_requests WHERE id=@r FOR UPDATE", ("@r", request), deadline,
            () => Store.DecideAsync(owner, bakery, order, "confirm", null, Ct));

        Assert.Equal(OrderWriteOutcome.OrderPickupPassed, result.Outcome);
        Assert.Equal("pending_confirmation", await StatusAsync("orders", order));
        Assert.Equal(new OrderWriteResult(OrderWriteOutcome.Saved, order),
            await Store.DecideAsync(owner, bakery, order, "reject", "Đã quá giờ nhận", Ct));
    }

    [MySqlFact]
    public async Task Concurrent_decisions_allow_exactly_one_transition()
    {
        var (_, owner, bakery, request, _, order) = await PendingOrderAsync();
        var staff = await UserAsync("bakery");
        await MemberAsync(bakery, staff);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => i % 2 == 0
            ? Store.DecideAsync(owner, bakery, order, "confirm", null, Ct)
            : Store.DecideAsync(staff, bakery, order, "reject", "Hết hàng", Ct))));

        Assert.Single(results, r => r.Outcome == OrderWriteOutcome.Saved);
        Assert.All(results.Where(r => r.Outcome != OrderWriteOutcome.Saved), r => Assert.Equal(OrderWriteOutcome.OrderNotPending, r.Outcome));
        var status = await StatusAsync("orders", order);
        Assert.Contains(status, new[] { "confirmed", "rejected_by_bakery" });
        Assert.Equal(status == "confirmed" ? "ordered" : "cancelled", await StatusAsync("design_requests", request));
    }

    [MySqlFact]
    public async Task Owner_and_staff_decide_only_through_their_own_bakery_route()
    {
        var customer = await UserAsync();
        var ownerA = await UserAsync("bakery");
        var ownerB = await UserAsync("bakery");
        var bakeryA = await BakeryAsync(ownerA);
        var bakeryB = await BakeryAsync(ownerB);
        await MemberAsync(bakeryB, ownerA);
        var request = await RequestAsync(customer, bakeryB);
        var order = await AcceptedAsync(customer, request, await QuoteAsync(ownerB, bakeryB, request));
        var before = await OrderRowAsync(order);

        // ownerA là staff của B: procedure suy ra tiệm từ đơn nên chỉ chốt route mới chặn được route A.
        Assert.Equal(OrderWriteOutcome.OrderNotFound, (await Store.DecideAsync(ownerA, bakeryA, order, "confirm", null, Ct)).Outcome);
        Assert.Equal(OrderReadOutcome.OrderNotFound, (await Store.FindForBakeryAsync(ownerA, bakeryA, order, Ct)).Outcome);
        Assert.Empty((await Store.ListForBakeryAsync(ownerA, bakeryA, null, 1, 20, Ct)).Page!.Items);
        Assert.Equal(OrderWriteOutcome.BakeryNotFound, (await Store.DecideAsync(ownerB, bakeryA, order, "confirm", null, Ct)).Outcome);
        Assert.Equal(before, await OrderRowAsync(order));

        var outsider = await UserAsync("bakery");
        await MemberAsync(bakeryB, outsider);
        await database.AdminExecuteAsync("UPDATE bakery_members SET status='removed' WHERE bakery_id=@b AND user_id=@u", ("@b", bakeryB), ("@u", outsider));
        Assert.Equal(OrderWriteOutcome.BakeryNotFound, (await Store.DecideAsync(outsider, bakeryB, order, "confirm", null, Ct)).Outcome);
        Assert.Equal(OrderReadOutcome.BakeryNotFound, (await Store.FindForBakeryAsync(outsider, bakeryB, order, Ct)).Outcome);
        await database.AdminExecuteAsync("UPDATE users SET status='disabled' WHERE id=@u", ("@u", ownerB));
        Assert.Equal(OrderWriteOutcome.BakeryNotFound, (await Store.DecideAsync(ownerB, bakeryB, order, "confirm", null, Ct)).Outcome);
        Assert.Equal(OrderReadOutcome.BakeryNotFound, (await Store.ListForBakeryAsync(ownerB, bakeryB, null, 1, 20, Ct)).Outcome);
        Assert.Equal(before, await OrderRowAsync(order));

        // Staff active của đúng tiệm có cùng quyền với owner.
        Assert.Equal(OrderWriteOutcome.Saved, (await Store.DecideAsync(ownerA, bakeryB, order, "confirm", null, Ct)).Outcome);
        var confirmed = (await Store.FindForBakeryAsync(ownerA, bakeryB, order, Ct)).Order!;
        Assert.Equal("confirmed", confirmed.Status);
        Assert.Null(confirmed.RejectionReason);
        Assert.Equal(ownerA, Convert.ToInt64(await database.AdminScalarAsync("SELECT bakery_decided_by_user_id FROM orders WHERE id=@o", ("@o", order))));
        Assert.Equal("ordered", await StatusAsync("design_requests", request));
    }

    [MySqlFact]
    public async Task Subscription_and_profile_rules_block_new_orders_but_keep_existing_ones_manageable_and_readable()
    {
        var (customer, owner, bakery, _, _, expiredOrder) = await PendingOrderAsync();
        var newRequest = await RequestAsync(customer, bakery);
        var newQuote = await QuoteAsync(owner, bakery, newRequest);
        await database.AdminExecuteAsync(
            "UPDATE bakery_subscription_periods SET starts_at=UTC_TIMESTAMP(3)-INTERVAL 2 DAY, ends_at=UTC_TIMESTAMP(3)-INTERVAL 1 DAY WHERE bakery_id=@b", ("@b", bakery));

        Assert.Equal(OrderWriteOutcome.BakerySubscriptionInactive, (await Store.AcceptAsync(customer, newRequest, newQuote, Ct)).Outcome);
        Assert.Equal(0, await OrderCountAsync(newRequest));
        Assert.Equal(OrderWriteOutcome.Saved, (await Store.DecideAsync(owner, bakery, expiredOrder, "confirm", null, Ct)).Outcome);
        // Hết hạn gói vẫn đọc được lịch sử đơn.
        Assert.Equal(expiredOrder, Assert.Single((await Store.ListForBakeryAsync(owner, bakery, null, 1, 20, Ct)).Page!.Items).Id);
        Assert.Equal("confirmed", (await Store.FindForCustomerAsync(customer, expiredOrder, Ct)).Order!.Status);

        var (suspendedCustomer, suspendedOwner, suspended, _, _, first) = await PendingOrderAsync();
        var second = await PendingOrderInAsync(suspendedCustomer, suspendedOwner, suspended);
        var third = await RequestAsync(suspendedCustomer, suspended);
        var thirdQuote = await QuoteAsync(suspendedOwner, suspended, third);
        await database.AdminExecuteAsync("UPDATE bakeries SET profile_status='suspended' WHERE id=@b", ("@b", suspended));

        Assert.Equal(OrderWriteOutcome.BakeryNotActive, (await Store.AcceptAsync(suspendedCustomer, third, thirdQuote, Ct)).Outcome);
        Assert.Equal(OrderWriteOutcome.BakeryNotActive, (await Store.DecideAsync(suspendedOwner, suspended, first, "confirm", null, Ct)).Outcome);
        Assert.Equal(OrderWriteOutcome.Saved, (await Store.DecideAsync(suspendedOwner, suspended, second, "reject", "Tiệm tạm ngừng", Ct)).Outcome);
        Assert.Equal("pending_confirmation", (await Store.FindForBakeryAsync(suspendedOwner, suspended, first, Ct)).Order!.Status);
        Assert.Equal(2, (await Store.ListForCustomerAsync(suspendedCustomer, null, 1, 20, Ct)).Page!.TotalCount);
    }

    private async Task<long> PendingOrderInAsync(long customer, long owner, long bakery)
    {
        var request = await RequestAsync(customer, bakery);
        return await AcceptedAsync(customer, request, await QuoteAsync(owner, bakery, request));
    }

    [MySqlFact]
    public async Task Writes_are_blocked_before_call_until_migration_v006_is_recorded_while_reads_keep_working()
    {
        var (customer, owner, bakery, _, _, order) = await PendingOrderAsync();
        var request = await RequestAsync(customer, bakery);
        var quote = await QuoteAsync(owner, bakery, request);
        var description = (string)(await database.AdminScalarAsync("SELECT description FROM schema_migrations WHERE version = 6"))!;
        var before = await OrderRowAsync(order);
        await database.AdminExecuteAsync("DELETE FROM schema_migrations WHERE version = 6");
        try
        {
            // Báo giá và đơn đều hợp lệ: nếu procedure được gọi thì đã tạo đơn/đổi trạng thái.
            Assert.Equal(OrderWriteOutcome.WorkflowNotReady, (await Store.AcceptAsync(customer, request, quote, Ct)).Outcome);
            Assert.Equal(OrderWriteOutcome.WorkflowNotReady, (await Store.DecideAsync(owner, bakery, order, "confirm", null, Ct)).Outcome);
            Assert.Equal(0, await OrderCountAsync(request));
            Assert.Equal(("submitted", "open"), (await StatusAsync("quotes", quote), await StatusAsync("design_requests", request)));
            Assert.Equal(before, await OrderRowAsync(order));
            Assert.Equal(order, (await Store.FindForCustomerAsync(customer, order, Ct)).Order!.Id);
            Assert.Equal(order, (await Store.FindForBakeryAsync(owner, bakery, order, Ct)).Order!.Id);
        }
        finally
        {
            await database.AdminExecuteAsync("INSERT INTO schema_migrations (version, description) VALUES (6, @d)", ("@d", description));
        }

        Assert.Equal(OrderWriteOutcome.Saved, (await Store.AcceptAsync(customer, request, quote, Ct)).Outcome);
    }

    [MySqlFact]
    public async Task Errors_outside_the_allowlist_propagate_and_write_nothing()
    {
        var (_, owner, bakery, request, _, order) = await PendingOrderAsync();
        var before = await OrderRowAsync(order);

        // Lý do vượt VARCHAR(500) (service đã chặn ở Application): lỗi dữ liệu của MySQL, không phải SIGNAL 45000.
        var error = await Assert.ThrowsAsync<MySqlException>(() => Store.DecideAsync(owner, bakery, order, "reject", new string('a', 501), Ct));

        Assert.NotEqual(OrderProcedureErrors.SqlState, error.SqlState);
        Assert.Equal(before, await OrderRowAsync(order));
        Assert.Equal("ordered", await StatusAsync("design_requests", request));
    }

    [MySqlFact]
    public async Task Lists_filter_page_and_order_by_newest_and_reads_never_write()
    {
        var customer = await UserAsync();
        var owner = await UserAsync("bakery");
        var bakery = await BakeryAsync(owner);
        var orders = new List<long>();
        for (var i = 0; i < 3; i++) orders.Add(await PendingOrderInAsync(customer, owner, bakery));
        // Hai đơn cùng created_at: id lớn hơn đứng trước.
        await database.AdminExecuteAsync("UPDATE orders SET created_at=UTC_TIMESTAMP(3)-INTERVAL 1 HOUR WHERE id IN (@a, @b)", ("@a", orders[0]), ("@b", orders[1]));
        Assert.Equal(OrderWriteOutcome.Saved, (await Store.DecideAsync(owner, bakery, orders[2], "confirm", null, Ct)).Outcome);
        var rows = new List<string>();
        foreach (var id in orders) rows.Add(await OrderRowAsync(id));

        var all = (await Store.ListForCustomerAsync(customer, null, 1, 20, Ct)).Page!;
        Assert.Equal([orders[2], orders[1], orders[0]], all.Items.Select(o => o.Id));
        Assert.Equal(3, all.TotalCount);
        var second = (await Store.ListForBakeryAsync(owner, bakery, null, 2, 1, Ct)).Page!;
        Assert.Equal((orders[1], 3L), (Assert.Single(second.Items).Id, second.TotalCount));
        var pending = (await Store.ListForBakeryAsync(owner, bakery, "pending_confirmation", 1, 20, Ct)).Page!;
        Assert.Equal([orders[1], orders[0]], pending.Items.Select(o => o.Id));
        Assert.Equal(orders[2], Assert.Single((await Store.ListForCustomerAsync(customer, "confirmed", 1, 20, Ct)).Page!.Items).Id);
        Assert.Empty((await Store.ListForCustomerAsync(customer, "picked_up", 1, 20, Ct)).Page!.Items);
        Assert.Empty((await Store.ListForBakeryAsync(owner, bakery, null, int.MaxValue, 100, Ct)).Page!.Items);

        var after = new List<string>();
        foreach (var id in orders) after.Add(await OrderRowAsync(id));
        Assert.Equal(rows, after);
    }
}
