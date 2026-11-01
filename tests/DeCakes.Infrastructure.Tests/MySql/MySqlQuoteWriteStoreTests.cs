using System.Data;
using DeCakes.Application.DesignRequests;
using DeCakes.Application.Quotes;
using DeCakes.Infrastructure.DesignRequests;
using DeCakes.Infrastructure.Quotes;
using MySqlConnector;

namespace DeCakes.Infrastructure.Tests.MySql;

/// <summary>Ghi báo giá qua procedure nguồn bằng đúng quyền backend, trên MySQL 8.4 loopback disposable.</summary>
[Collection(MySqlCollection.Name)]
public class MySqlQuoteWriteStoreTests(MySqlTestDatabase database)
{
    private static CancellationToken Ct => CancellationToken.None;
    private MySqlQuoteStore Store => new(database.Factory);

    // Nội dung bất biến của một phiên bản: mọi cột trừ status, withdrawn_at, updated_at.
    private const string ContentSql =
        """
        SELECT CONCAT_WS('|', id, design_request_id, bakery_id, revision_no, price, currency, IFNULL(size_description, '-'),
               IFNULL(servings, '-'), flavor, decoration_description, IFNULL(inscription_text, '-'), IFNULL(note, '-'),
               estimated_pickup_at, valid_until, IFNULL(confirmed_design_asset_id, '-'), created_by_user_id, created_at)
          FROM quotes WHERE id = @q
        """;

    private static DateTime Ms(DateTime value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    private static QuoteValues Values(
        decimal price = 680_000.50m, DateTime? validUntil = null, string? note = null, long? asset = null) =>
        new(price, "20 cm", 10, "Vani", "Hoa 🌷", null, note,
            Ms(DateTime.UtcNow.AddDays(10)), validUntil ?? Ms(DateTime.UtcNow.AddDays(2)), asset);

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

    private async Task<long> SavedAsync(Task<QuoteWriteResult> write)
    {
        var result = await write;
        Assert.Equal(QuoteWriteOutcome.Saved, result.Outcome);
        return result.QuoteId!.Value;
    }

    private async Task<long> CountAsync(long request, long bakery) => Convert.ToInt64(await database.AdminScalarAsync(
        "SELECT COUNT(*) FROM quotes WHERE design_request_id=@r AND bakery_id=@b", ("@r", request), ("@b", bakery)));

    private async Task<string?> RequestBakeryStatusAsync(long request, long bakery) => (string?)await database.AdminScalarAsync(
        "SELECT status FROM request_bakeries WHERE design_request_id=@r AND bakery_id=@b", ("@r", request), ("@b", bakery));

    private async Task<(string Status, string Content)> SnapshotAsync(long quote) => (
        (string)(await database.AdminScalarAsync("SELECT status FROM quotes WHERE id=@q", ("@q", quote)))!,
        (string)(await database.AdminScalarAsync(ContentSql, ("@q", quote)))!);

    [MySqlFact]
    public async Task Create_returns_the_out_id_and_stores_exact_values_above_budget_with_another_pickup_time()
    {
        var customer = await UserAsync();
        var owner = await UserAsync("bakery");
        var bakery = await BakeryAsync(owner);
        var request = await RequestAsync(customer, bakery);
        var pickup = Ms(DateTime.UtcNow.AddDays(12));
        var values = new QuoteValues(9_999_999_999.99m, null, 65_535, string.Concat(Enumerable.Repeat("🎂", 200)),
            new string('ệ', 21_845), "Chúc mừng 🎉", "Ghi chú\r\n\tdòng 2", pickup, pickup, null);

        var id = await SavedAsync(Store.CreateAsync(owner, bakery, request, values, Ct));

        Assert.Equal(id, Convert.ToInt64(await database.AdminScalarAsync("SELECT MAX(id) FROM quotes WHERE design_request_id=@r", ("@r", request))));
        Assert.Equal(owner, Convert.ToInt64(await database.AdminScalarAsync("SELECT created_by_user_id FROM quotes WHERE id=@q", ("@q", id))));
        Assert.Equal("quoted", await RequestBakeryStatusAsync(request, bakery));
        var quote = (await Store.FindForBakeryAsync(owner, bakery, request, id, Ct)).Quote!;
        Assert.Equal((1L, "submitted", "VND"), (quote.RevisionNo, quote.Status, quote.Currency));
        Assert.Equal(values, new QuoteValues(quote.Price, quote.SizeDescription, quote.Servings, quote.Flavor, quote.DecorationDescription,
            quote.InscriptionText, quote.Note, quote.EstimatedPickupAt, quote.ValidUntil, quote.DesignAssetId));
        Assert.Equal(DateTimeKind.Utc, quote.EstimatedPickupAt.Kind);
        // Nội dung yêu cầu không bị ghi đè bởi giá/giờ của tiệm.
        Assert.Equal(1_000_000m, Convert.ToDecimal(await database.AdminScalarAsync("SELECT budget_max FROM design_requests WHERE id=@r", ("@r", request))));
    }

    [MySqlFact]
    public async Task Route_scope_is_checked_before_call_even_for_a_member_of_both_bakeries()
    {
        var customer = await UserAsync();
        var ownerA = await UserAsync("bakery");
        var ownerB = await UserAsync("bakery");
        var bakeryA = await BakeryAsync(ownerA);
        var bakeryB = await BakeryAsync(ownerB);
        await MemberAsync(bakeryB, ownerA);
        var request = await RequestAsync(customer, bakeryA, bakeryB);
        var onlyB = await RequestAsync(customer, bakeryB);
        var quoteB = await SavedAsync(Store.CreateAsync(ownerB, bakeryB, request, Values(), Ct));
        var before = await SnapshotAsync(quoteB);

        // Procedure suy ra tiệm từ báo giá và ownerA cũng là thành viên B: chỉ chốt route mới chặn được.
        Assert.Equal(QuoteWriteOutcome.QuoteNotFound, (await Store.ReviseAsync(ownerA, bakeryA, request, quoteB, Values(), Ct)).Outcome);
        Assert.Equal(QuoteWriteOutcome.QuoteNotFound, (await Store.WithdrawAsync(ownerA, bakeryA, request, quoteB, Ct)).Outcome);
        Assert.Equal(QuoteWriteOutcome.QuoteNotFound, (await Store.WithdrawAsync(ownerA, bakeryB, onlyB, quoteB, Ct)).Outcome);
        Assert.Equal(QuoteWriteOutcome.RequestNotFound, (await Store.CreateAsync(ownerA, bakeryA, onlyB, Values(), Ct)).Outcome);
        Assert.Equal(QuoteWriteOutcome.BakeryNotFound, (await Store.CreateAsync(ownerB, bakeryA, request, Values(), Ct)).Outcome);
        Assert.Equal(before, await SnapshotAsync(quoteB));
        Assert.Equal(1, await CountAsync(request, bakeryB));
        Assert.Equal(0, await CountAsync(request, bakeryA));
        Assert.Equal(0, await CountAsync(onlyB, bakeryA));

        // Đúng route tiệm B thì thành viên staff của B được rút.
        Assert.Equal(quoteB, await SavedAsync(Store.WithdrawAsync(ownerA, bakeryB, request, quoteB, Ct)));
    }

    [MySqlFact]
    public async Task Active_staff_can_write_and_lost_membership_or_role_is_rejected()
    {
        var customer = await UserAsync();
        var owner = await UserAsync("bakery");
        var staff = await UserAsync("bakery");
        var bakery = await BakeryAsync(owner);
        await MemberAsync(bakery, staff);
        var request = await RequestAsync(customer, bakery);
        var quote = await SavedAsync(Store.CreateAsync(staff, bakery, request, Values(), Ct));
        var revised = await SavedAsync(Store.ReviseAsync(owner, bakery, request, quote, Values(700_000m), Ct));

        await database.AdminExecuteAsync("UPDATE bakery_members SET status='removed' WHERE bakery_id=@b AND user_id=@u", ("@b", bakery), ("@u", staff));
        Assert.Equal(QuoteWriteOutcome.BakeryNotFound, (await Store.WithdrawAsync(staff, bakery, request, revised, Ct)).Outcome);
        await database.AdminExecuteAsync("UPDATE users SET status='disabled' WHERE id=@u", ("@u", owner));
        Assert.Equal(QuoteWriteOutcome.BakeryNotFound, (await Store.ReviseAsync(owner, bakery, request, revised, Values(), Ct)).Outcome);
        await database.AdminExecuteAsync("UPDATE users SET status='active', system_role='customer' WHERE id=@u", ("@u", owner));
        Assert.Equal(QuoteWriteOutcome.BakeryNotFound, (await Store.WithdrawAsync(owner, bakery, request, revised, Ct)).Outcome);
        Assert.Equal("submitted", (await SnapshotAsync(revised)).Status);
        Assert.Equal(2, await CountAsync(request, bakery));
    }

    [MySqlFact]
    public async Task Revise_adds_a_version_keeps_history_immutable_and_works_on_expired_quotes()
    {
        var customer = await UserAsync();
        var owner = await UserAsync("bakery");
        var bakery = await BakeryAsync(owner);
        var request = await RequestAsync(customer, bakery);
        var first = await SavedAsync(Store.CreateAsync(owner, bakery, request, Values(), Ct));
        await database.AdminExecuteAsync(
            "UPDATE quotes SET created_at=UTC_TIMESTAMP(3)-INTERVAL 3 DAY, valid_until=UTC_TIMESTAMP(3)-INTERVAL 1 DAY WHERE id=@q", ("@q", first));
        var before = await SnapshotAsync(first);

        var second = await SavedAsync(Store.ReviseAsync(owner, bakery, request, first, Values(1_500_000m, note: "Bản sửa"), Ct));

        Assert.NotEqual(first, second);
        Assert.Equal(("superseded", before.Content), await SnapshotAsync(first));
        var current = (await Store.FindForBakeryAsync(owner, bakery, request, second, Ct)).Quote!;
        Assert.Equal((2L, "submitted", 1_500_000m, "Bản sửa"), (current.RevisionNo, current.Status, current.Price, current.Note));
        Assert.Equal(QuoteWriteOutcome.QuoteNotCurrent, (await Store.ReviseAsync(owner, bakery, request, first, Values(), Ct)).Outcome);
        Assert.Equal(QuoteWriteOutcome.QuoteAlreadyExists, (await Store.CreateAsync(owner, bakery, request, Values(), Ct)).Outcome);
        Assert.Equal(2, await CountAsync(request, bakery));
        Assert.Equal("quoted", await RequestBakeryStatusAsync(request, bakery));
    }

    [MySqlFact]
    public async Task Withdraw_keeps_request_bakery_quoted_is_not_repeatable_and_allows_a_new_quote()
    {
        var customer = await UserAsync();
        var owner = await UserAsync("bakery");
        var bakery = await BakeryAsync(owner);
        var request = await RequestAsync(customer, bakery);
        var quote = await SavedAsync(Store.CreateAsync(owner, bakery, request, Values(), Ct));
        var before = await SnapshotAsync(quote);

        Assert.Equal(quote, await SavedAsync(Store.WithdrawAsync(owner, bakery, request, quote, Ct)));

        Assert.Equal(("withdrawn", before.Content), await SnapshotAsync(quote));
        Assert.NotNull((await Store.FindForBakeryAsync(owner, bakery, request, quote, Ct)).Quote!.WithdrawnAt);
        Assert.Equal("quoted", await RequestBakeryStatusAsync(request, bakery));
        Assert.Equal(QuoteWriteOutcome.QuoteNotCurrent, (await Store.WithdrawAsync(owner, bakery, request, quote, Ct)).Outcome);
        Assert.Equal(QuoteWriteOutcome.QuoteNotCurrent, (await Store.ReviseAsync(owner, bakery, request, quote, Values(), Ct)).Outcome);
        var again = await SavedAsync(Store.CreateAsync(owner, bakery, request, Values(), Ct));
        Assert.Equal(2L, (await Store.FindForBakeryAsync(owner, bakery, request, again, Ct)).Quote!.RevisionNo);
    }

    [MySqlFact]
    public async Task Expired_subscription_blocks_create_and_revise_but_not_withdraw()
    {
        var customer = await UserAsync();
        var owner = await UserAsync("bakery");
        var bakery = await BakeryAsync(owner);
        var request = await RequestAsync(customer, bakery);
        var other = await RequestAsync(customer, bakery);
        var quote = await SavedAsync(Store.CreateAsync(owner, bakery, request, Values(), Ct));
        await database.AdminExecuteAsync(
            "UPDATE bakery_subscription_periods SET starts_at=UTC_TIMESTAMP(3)-INTERVAL 2 DAY, ends_at=UTC_TIMESTAMP(3)-INTERVAL 1 DAY WHERE bakery_id=@b", ("@b", bakery));

        Assert.Equal(QuoteWriteOutcome.BakerySubscriptionInactive, (await Store.CreateAsync(owner, bakery, other, Values(), Ct)).Outcome);
        Assert.Equal(QuoteWriteOutcome.BakerySubscriptionInactive, (await Store.ReviseAsync(owner, bakery, request, quote, Values(), Ct)).Outcome);
        Assert.Equal("submitted", (await SnapshotAsync(quote)).Status);
        Assert.Equal(0, await CountAsync(other, bakery));

        await database.AdminExecuteAsync("UPDATE bakeries SET profile_status='suspended' WHERE id=@b", ("@b", bakery));
        Assert.Equal(quote, await SavedAsync(Store.WithdrawAsync(owner, bakery, request, quote, Ct)));
        Assert.Equal(1, await CountAsync(request, bakery));
    }

    [MySqlFact]
    public async Task Request_bakery_and_acceptance_states_map_to_allowlisted_outcomes()
    {
        var customer = await UserAsync();
        var owner = await UserAsync("bakery");
        var bakery = await BakeryAsync(owner);

        var declined = await RequestAsync(customer, bakery);
        await database.AdminExecuteAsync(
            "UPDATE request_bakeries SET status='declined', responded_at=UTC_TIMESTAMP(3) WHERE design_request_id=@r", ("@r", declined));
        Assert.Equal(QuoteWriteOutcome.RequestDeclined, (await Store.CreateAsync(owner, bakery, declined, Values(), Ct)).Outcome);

        var cancelled = await RequestAsync(customer, bakery);
        await database.AdminExecuteAsync("UPDATE design_requests SET status='cancelled' WHERE id=@r", ("@r", cancelled));
        Assert.Equal(QuoteWriteOutcome.RequestNotOpen, (await Store.CreateAsync(owner, bakery, cancelled, Values(), Ct)).Outcome);

        var accepted = await RequestAsync(customer, bakery);
        var quote = await SavedAsync(Store.CreateAsync(owner, bakery, accepted, Values(), Ct));
        await using (var connection = await database.Factory.OpenConnectionAsync(Ct))
        await using (var accept = new MySqlCommand("sp_accept_quote", connection) { CommandType = CommandType.StoredProcedure })
        {
            accept.Parameters.Add("p_quote_id", MySqlDbType.Int64).Value = quote;
            accept.Parameters.Add("p_customer_id", MySqlDbType.Int64).Value = customer;
            accept.Parameters.Add("p_order_id", MySqlDbType.Int64).Direction = ParameterDirection.Output;
            await accept.ExecuteNonQueryAsync(Ct);
        }

        Assert.Equal(QuoteWriteOutcome.QuoteAlreadyAccepted, (await Store.ReviseAsync(owner, bakery, accepted, quote, Values(), Ct)).Outcome);
        Assert.Equal(QuoteWriteOutcome.QuoteAlreadyAccepted, (await Store.WithdrawAsync(owner, bakery, accepted, quote, Ct)).Outcome);

        var suspended = await RequestAsync(customer, bakery);
        await database.AdminExecuteAsync("UPDATE bakeries SET profile_status='suspended' WHERE id=@b", ("@b", bakery));
        Assert.Equal(QuoteWriteOutcome.BakeryNotActive, (await Store.CreateAsync(owner, bakery, suspended, Values(), Ct)).Outcome);
        Assert.Equal(0, await CountAsync(suspended, bakery));
    }

    [MySqlFact]
    public async Task Design_asset_must_belong_to_the_request_and_procedure_time_checks_are_mapped()
    {
        var customer = await UserAsync();
        var owner = await UserAsync("bakery");
        var bakery = await BakeryAsync(owner);
        var request = await RequestAsync(customer, bakery);
        var other = await RequestAsync(customer, bakery);
        const string asset = "INSERT INTO media_assets(asset_kind,storage_url,mime_type,uploaded_by_user_id,design_request_id) VALUES ('customer_reference','https://example.com/a.png','image/png',@u,@r)";
        var foreign = await database.AdminExecuteAsync(asset, ("@u", customer), ("@r", other));
        var own = await database.AdminExecuteAsync(asset, ("@u", customer), ("@r", request));

        Assert.Equal(QuoteWriteOutcome.DesignAssetInvalid, (await Store.CreateAsync(owner, bakery, request, Values(asset: foreign), Ct)).Outcome);
        Assert.Equal(QuoteWriteOutcome.DesignAssetInvalid, (await Store.CreateAsync(owner, bakery, request, Values(asset: long.MaxValue), Ct)).Outcome);
        // Bỏ qua validator để chạm guard của procedure: hạn đã qua theo giờ database, giá không dương.
        Assert.Equal(QuoteWriteOutcome.InvalidTimes, (await Store.CreateAsync(owner, bakery, request, Values(validUntil: Ms(DateTime.UtcNow.AddMinutes(-1))), Ct)).Outcome);
        Assert.Equal(QuoteWriteOutcome.InvalidPrice, (await Store.CreateAsync(owner, bakery, request, Values(price: -1m), Ct)).Outcome);
        Assert.Equal(0, await CountAsync(request, bakery));

        var quote = await SavedAsync(Store.CreateAsync(owner, bakery, request, Values(asset: own), Ct));
        Assert.Equal(own, (await Store.FindForBakeryAsync(owner, bakery, request, quote, Ct)).Quote!.DesignAssetId);
    }

    [MySqlFact]
    public async Task Errors_outside_the_allowlist_propagate_and_write_nothing()
    {
        var customer = await UserAsync();
        var owner = await UserAsync("bakery");
        var bakery = await BakeryAsync(owner);
        var request = await RequestAsync(customer, bakery);

        // TEXT vượt 65 535 byte (validator đã chặn ở Application): lỗi dữ liệu của MySQL, không phải SIGNAL 45000.
        var error = await Assert.ThrowsAsync<MySqlException>(() => Store.CreateAsync(owner, bakery, request, Values(note: new string('a', 65_536)), Ct));
        Assert.NotEqual(QuoteProcedureErrors.SqlState, error.SqlState);
        Assert.Equal(0, await CountAsync(request, bakery));
        Assert.Equal("pending", await RequestBakeryStatusAsync(request, bakery));
    }

    [MySqlFact]
    public async Task Concurrent_creates_produce_one_current_version()
    {
        var customer = await UserAsync();
        var owner = await UserAsync("bakery");
        var staff = await UserAsync("bakery");
        var bakery = await BakeryAsync(owner);
        await MemberAsync(bakery, staff);
        var request = await RequestAsync(customer, bakery);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(() => Store.CreateAsync(i % 2 == 0 ? owner : staff, bakery, request, Values(), Ct))));

        Assert.Single(results, r => r.Outcome == QuoteWriteOutcome.Saved);
        Assert.All(results.Where(r => r.Outcome != QuoteWriteOutcome.Saved), r => Assert.Equal(QuoteWriteOutcome.QuoteAlreadyExists, r.Outcome));
        Assert.Equal(1, await CountAsync(request, bakery));
    }

    [MySqlFact]
    public async Task Concurrent_revisions_of_one_version_produce_exactly_one_new_version()
    {
        var customer = await UserAsync();
        var owner = await UserAsync("bakery");
        var bakery = await BakeryAsync(owner);
        var request = await RequestAsync(customer, bakery);
        var first = await SavedAsync(Store.CreateAsync(owner, bakery, request, Values(), Ct));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(() => Store.ReviseAsync(owner, bakery, request, first, Values(700_000m + i), Ct))));

        var winner = Assert.Single(results, r => r.Outcome == QuoteWriteOutcome.Saved);
        Assert.All(results.Where(r => r.Outcome != QuoteWriteOutcome.Saved), r => Assert.Equal(QuoteWriteOutcome.QuoteNotCurrent, r.Outcome));
        Assert.Equal(2, await CountAsync(request, bakery));
        Assert.Equal("superseded", (await SnapshotAsync(first)).Status);
        Assert.Equal(winner.QuoteId, Convert.ToInt64(await database.AdminScalarAsync(
            "SELECT id FROM quotes WHERE design_request_id=@r AND bakery_id=@b AND status='submitted'", ("@r", request), ("@b", bakery))));
    }
}
