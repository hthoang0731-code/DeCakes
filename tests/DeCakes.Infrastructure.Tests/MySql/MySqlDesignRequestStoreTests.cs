using DeCakes.Application.DesignRequests;
using DeCakes.Domain.DesignRequests;
using DeCakes.Infrastructure.DesignRequests;
using MySqlConnector;

namespace DeCakes.Infrastructure.Tests.MySql;

/// <summary>
/// MySqlDesignRequestStore và sp_send_request_to_bakery thật trên MySQL 8.4 kiểm thử (database decakes_test_* tạm,
/// tài khoản có quyền như decakes_app). Dữ liệu chuẩn bị bằng tài khoản quản trị của database tạm.
/// Bị bỏ qua khi chưa cấu hình DECAKES_TEST_MYSQL_ADMIN. Không dùng Aiven.
/// </summary>
[Collection(MySqlCollection.Name)]
public class MySqlDesignRequestStoreTests(MySqlTestDatabase database)
{
    private static CancellationToken Ct => CancellationToken.None;

    private MySqlDesignRequestStore Store => new(database.Factory);

    // ---------- Chuẩn bị dữ liệu ----------

    private Task<long> CustomerAsync(string status = "active") =>
        database.AdminExecuteAsync(
            "INSERT INTO users (email, full_name, system_role, status) VALUES (@e, 'Khách thử', 'customer', @s)",
            ("@e", $"c-{Guid.NewGuid():N}@example.com"), ("@s", status));

    // Tiệm có kỳ sử dụng còn hạn (điều kiện nhận yêu cầu mới từ migrations/V005).
    private async Task<long> BakeryAsync(string profileStatus = "active")
    {
        var id = await database.AdminExecuteAsync(
            "INSERT INTO bakeries (name, slug, profile_status) VALUES (@n, @s, @p)",
            ("@n", "Tiệm thử"), ("@s", $"tiem-{Guid.NewGuid():N}"), ("@p", profileStatus));
        await database.GrantAccessAsync(id);
        return id;
    }

    private Task<long> CakeAsync(long bakeryId, string visibility) =>
        database.AdminExecuteAsync(
            "INSERT INTO cakes (bakery_id, name, visibility) VALUES (@b, 'Bánh mẫu', @v)", ("@b", bakeryId), ("@v", visibility));

    private static DateTime FutureUtc(int days = 5)
    {
        var t = DateTime.UtcNow.AddDays(days);
        return new DateTime(t.Ticks - (t.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
    }

    private static DesignRequestValues Values(string title = "Bánh sinh nhật", DateTime? pickup = null, long? cakeId = null) =>
        new(title, "Bánh hai tầng, tông hồng.", 500_000m, 1_200_000.50m, "20 cm", 12, "Vani", "Mừng sinh nhật",
            pickup ?? FutureUtc(), cakeId);

    private async Task<string?> StatusOfAsync(long requestId) =>
        (string?)await database.AdminScalarAsync("SELECT status FROM design_requests WHERE id = @id", ("@id", requestId));

    private async Task<long> RequestBakeryRowsAsync(long requestId, long? bakeryId = null) =>
        Convert.ToInt64(await database.AdminScalarAsync(
            "SELECT COUNT(*) FROM request_bakeries WHERE design_request_id = @r AND (@b IS NULL OR bakery_id = @b)",
            ("@r", requestId), ("@b", bakeryId)));

    // ---------- CRUD draft ----------

    [MySqlFact]
    public async Task Create_draft_stores_server_defaults_and_exact_values()
    {
        var customer = await CustomerAsync();
        var future = FutureUtc();
        var pickup = future.AddMilliseconds(123 - future.Millisecond); // phần mili giây khác 0 phải được giữ nguyên
        var idea = new string('ữ', 21_845); // 65 535 byte UTF-8: đúng giới hạn TEXT
        var values = new DesignRequestValues("Tiêu đề", idea, null, 9_999_999_999.99m, null, 65_535, null, null, pickup, null);

        var id = await Store.CreateDraftAsync(customer, values, Ct);
        var dto = await Store.FindForCustomerAsync(id, customer, Ct);

        Assert.NotNull(dto);
        Assert.Equal(DesignRequestStatuses.Draft, dto.Status);
        Assert.Equal("VND", dto.Currency);
        Assert.Equal(9_999_999_999.99m, dto.BudgetMax);
        Assert.Null(dto.BudgetMin);
        Assert.Equal(65_535, dto.Servings);
        Assert.Equal(idea, dto.IdeaDescription);
        Assert.Equal(pickup, dto.PickupAt);
        Assert.Equal(DateTimeKind.Utc, dto.PickupAt.Kind);
        Assert.Equal(DateTimeKind.Utc, dto.CreatedAt.Kind);
        Assert.InRange(dto.CreatedAt, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        Assert.False(Convert.ToBoolean(await database.AdminScalarAsync("SELECT is_demo FROM design_requests WHERE id = @id", ("@id", id))));
    }

    [MySqlFact]
    public async Task Find_and_update_are_scoped_to_owner()
    {
        var owner = await CustomerAsync();
        var other = await CustomerAsync();
        var id = await Store.CreateDraftAsync(owner, Values(), Ct);

        Assert.Null(await Store.FindForCustomerAsync(id, other, Ct));
        Assert.Equal(UpdateDraftOutcome.NotFound, await Store.UpdateDraftAsync(id, other, Values("Không được"), Ct));
        Assert.Equal(UpdateDraftOutcome.NotFound, await Store.UpdateDraftAsync(long.MaxValue, owner, Values(), Ct));
        Assert.Null(await Store.ListBakeriesAsync(id, other, Ct));
        Assert.Equal("Bánh sinh nhật", (await Store.FindForCustomerAsync(id, owner, Ct))!.Title);
    }

    [MySqlFact]
    public async Task Update_draft_succeeds_including_identical_data()
    {
        var customer = await CustomerAsync();
        var id = await Store.CreateDraftAsync(customer, Values(), Ct);
        var changed = Values("Đổi tiêu đề") with { BudgetMin = null, Servings = null, Flavor = null };

        Assert.Equal(UpdateDraftOutcome.Updated, await Store.UpdateDraftAsync(id, customer, changed, Ct));
        Assert.Equal(UpdateDraftOutcome.Updated, await Store.UpdateDraftAsync(id, customer, changed, Ct));

        var dto = (await Store.FindForCustomerAsync(id, customer, Ct))!;
        Assert.Equal("Đổi tiêu đề", dto.Title);
        Assert.Null(dto.BudgetMin);
        Assert.Null(dto.Servings);
        Assert.Null(dto.Flavor);
        Assert.Equal(DesignRequestStatuses.Draft, dto.Status);
    }

    [MySqlFact]
    public async Task List_is_scoped_ordered_paged_and_filtered()
    {
        var customer = await CustomerAsync();
        var other = await CustomerAsync();
        await Store.CreateDraftAsync(other, Values("Của người khác"), Ct);
        var ids = new List<long>();
        for (var i = 0; i < 5; i++) ids.Add(await Store.CreateDraftAsync(customer, Values($"Yêu cầu {i}"), Ct));
        await database.AdminExecuteAsync("UPDATE design_requests SET status = 'cancelled' WHERE id = @id", ("@id", ids[1]));

        var page1 = await Store.ListForCustomerAsync(customer, null, 1, 2, Ct);
        var page3 = await Store.ListForCustomerAsync(customer, null, 3, 2, Ct);
        var drafts = await Store.ListForCustomerAsync(customer, DesignRequestStatuses.Draft, 1, 100, Ct);

        Assert.Equal(5, page1.TotalCount);
        Assert.Equal([ids[4], ids[3]], page1.Items.Select(x => x.Id));
        Assert.Equal([ids[0]], page3.Items.Select(x => x.Id));
        Assert.Equal(4, drafts.TotalCount);
        Assert.DoesNotContain(ids[1], drafts.Items.Select(x => x.Id));
        Assert.Empty((await Store.ListForCustomerAsync(customer, null, 4, 2, Ct)).Items);
    }

    [MySqlFact]
    public async Task Inspiration_cake_must_be_published_and_bakery_active()
    {
        var active = await BakeryAsync();
        var suspended = await BakeryAsync("suspended");
        var published = await CakeAsync(active, "published");
        var hidden = await CakeAsync(active, "hidden");
        var publishedButSuspended = await CakeAsync(suspended, "published");

        Assert.True(await Store.IsCakeAvailableForInspirationAsync(published, Ct));
        Assert.False(await Store.IsCakeAvailableForInspirationAsync(hidden, Ct));
        Assert.False(await Store.IsCakeAvailableForInspirationAsync(publishedButSuspended, Ct));
        Assert.False(await Store.IsCakeAvailableForInspirationAsync(long.MaxValue, Ct));

        var customer = await CustomerAsync();
        var id = await Store.CreateDraftAsync(customer, Values(cakeId: published), Ct);
        Assert.Equal(published, (await Store.FindForCustomerAsync(id, customer, Ct))!.InspiredByCakeId);
    }

    // ---------- Gửi tiệm qua procedure ----------

    [MySqlFact]
    public async Task Send_moves_draft_to_open_and_lists_bakeries()
    {
        var customer = await CustomerAsync();
        var first = await BakeryAsync();
        var second = await BakeryAsync();
        var id = await Store.CreateDraftAsync(customer, Values(), Ct);

        Assert.Equal(SendToBakeryOutcome.Sent, await Store.SendToBakeryAsync(id, customer, first, Ct));
        Assert.Equal(DesignRequestStatuses.Open, await StatusOfAsync(id));
        Assert.Equal(SendToBakeryOutcome.Sent, await Store.SendToBakeryAsync(id, customer, second, Ct));
        Assert.Equal(DesignRequestStatuses.Open, await StatusOfAsync(id));

        var bakeries = (await Store.ListBakeriesAsync(id, customer, Ct))!;
        Assert.Equal([first, second], bakeries.Select(b => b.BakeryId));
        Assert.All(bakeries, b =>
        {
            Assert.Equal(RequestBakeryStatuses.Pending, b.Status);
            Assert.Equal(DateTimeKind.Utc, b.SentAt.Kind);
            Assert.Null(b.RespondedAt);
        });
    }

    [MySqlFact]
    public async Task Update_after_open_is_rejected_and_content_unchanged()
    {
        var customer = await CustomerAsync();
        var bakery = await BakeryAsync();
        var id = await Store.CreateDraftAsync(customer, Values("Gốc"), Ct);
        await Store.SendToBakeryAsync(id, customer, bakery, Ct);

        Assert.Equal(UpdateDraftOutcome.NotDraft, await Store.UpdateDraftAsync(id, customer, Values("Sửa sau khi gửi"), Ct));
        Assert.Equal("Gốc", (await Store.FindForCustomerAsync(id, customer, Ct))!.Title);
    }

    [MySqlFact]
    public async Task Send_failures_map_procedure_codes()
    {
        var customer = await CustomerAsync();
        var other = await CustomerAsync();
        var active = await BakeryAsync();
        var draftBakery = await BakeryAsync("draft");
        var id = await Store.CreateDraftAsync(customer, Values(), Ct);

        Assert.Equal(SendToBakeryOutcome.RequestNotFound, await Store.SendToBakeryAsync(long.MaxValue, customer, active, Ct));
        Assert.Equal(SendToBakeryOutcome.RequestNotFound, await Store.SendToBakeryAsync(id, other, active, Ct));   // NOT_REQUEST_OWNER
        Assert.Equal(SendToBakeryOutcome.BakeryNotFound, await Store.SendToBakeryAsync(id, customer, long.MaxValue, Ct));
        Assert.Equal(SendToBakeryOutcome.BakeryNotActive, await Store.SendToBakeryAsync(id, customer, draftBakery, Ct));
        Assert.Equal(DesignRequestStatuses.Draft, await StatusOfAsync(id));
        Assert.Equal(0, await RequestBakeryRowsAsync(id));

        Assert.Equal(SendToBakeryOutcome.Sent, await Store.SendToBakeryAsync(id, customer, active, Ct));
        Assert.Equal(SendToBakeryOutcome.AlreadySent, await Store.SendToBakeryAsync(id, customer, active, Ct));
        Assert.Equal(1, await RequestBakeryRowsAsync(id));
    }

    [MySqlFact]
    public async Task Send_rejects_past_pickup_closed_request_and_disabled_customer()
    {
        var customer = await CustomerAsync();
        var bakery = await BakeryAsync();

        var past = await Store.CreateDraftAsync(customer, Values(), Ct);
        await database.AdminExecuteAsync(
            "UPDATE design_requests SET created_at = UTC_TIMESTAMP(3) - INTERVAL 3 DAY, pickup_at = UTC_TIMESTAMP(3) - INTERVAL 1 DAY WHERE id = @id",
            ("@id", past));
        Assert.Equal(SendToBakeryOutcome.PickupInPast, await Store.SendToBakeryAsync(past, customer, bakery, Ct));

        var cancelled = await Store.CreateDraftAsync(customer, Values(), Ct);
        await database.AdminExecuteAsync("UPDATE design_requests SET status = 'cancelled' WHERE id = @id", ("@id", cancelled));
        Assert.Equal(SendToBakeryOutcome.RequestNotOpen, await Store.SendToBakeryAsync(cancelled, customer, bakery, Ct));

        var disabledOwner = await CustomerAsync();
        var request = await Store.CreateDraftAsync(disabledOwner, Values(), Ct);
        await database.AdminExecuteAsync("UPDATE users SET status = 'disabled' WHERE id = @id", ("@id", disabledOwner));
        Assert.Equal(SendToBakeryOutcome.AccountNotAllowed, await Store.SendToBakeryAsync(request, disabledOwner, bakery, Ct));

        Assert.Equal(0, await RequestBakeryRowsAsync(past) + await RequestBakeryRowsAsync(cancelled) + await RequestBakeryRowsAsync(request));
    }

    [MySqlFact]
    public async Task Signal_from_procedure_has_sqlstate_45000_number_1644_and_exact_code()
    {
        await using var connection = await database.Factory.OpenConnectionAsync(Ct);
        await using var command = MySqlTestDatabase.Command(
            connection, "CALL sp_send_request_to_bakery(@r, @c, @b)", ("@r", long.MaxValue), ("@c", 1L), ("@b", 1L));

        var error = await Assert.ThrowsAsync<MySqlException>(() => command.ExecuteNonQueryAsync(Ct));

        Assert.Equal("45000", error.SqlState);
        Assert.Equal(1644, error.Number);
        Assert.Equal("REQUEST_NOT_FOUND", error.Message);
    }

    [MySqlFact]
    public async Task Backend_account_cannot_insert_request_bakeries_directly()
    {
        var customer = await CustomerAsync();
        var bakery = await BakeryAsync();
        var id = await Store.CreateDraftAsync(customer, Values(), Ct);
        await using var connection = await database.Factory.OpenConnectionAsync(Ct);
        await using var command = MySqlTestDatabase.Command(
            connection, "INSERT INTO request_bakeries (design_request_id, bakery_id) VALUES (@r, @b)", ("@r", id), ("@b", bakery));

        var error = await Assert.ThrowsAsync<MySqlException>(() => command.ExecuteNonQueryAsync(Ct));

        Assert.Equal(MySqlErrorCode.TableAccessDenied, error.ErrorCode);
    }

    // ---------- Đồng thời ----------

    [MySqlFact]
    public async Task Concurrent_sends_to_same_bakery_create_one_row()
    {
        var customer = await CustomerAsync();
        var bakery = await BakeryAsync();
        var id = await Store.CreateDraftAsync(customer, Values(), Ct);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Store.SendToBakeryAsync(id, customer, bakery, Ct))));

        Assert.Equal(1, outcomes.Count(o => o == SendToBakeryOutcome.Sent));
        Assert.All(outcomes.Where(o => o != SendToBakeryOutcome.Sent), o => Assert.Equal(SendToBakeryOutcome.AlreadySent, o));
        Assert.Equal(1, await RequestBakeryRowsAsync(id, bakery));
        Assert.Equal(DesignRequestStatuses.Open, await StatusOfAsync(id));
    }

    [MySqlFact]
    public async Task Update_waiting_on_row_lock_rechecks_draft_after_status_change()
    {
        var customer = await CustomerAsync();
        var id = await Store.CreateDraftAsync(customer, Values("Gốc"), Ct);

        // Một giao dịch khác đổi trạng thái và giữ khóa dòng; câu UPDATE của store phải chờ rồi đánh giá lại điều kiện draft.
        await using var connection = await database.AdminFactory.OpenConnectionAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var change = new MySqlCommand("UPDATE design_requests SET status = 'open' WHERE id = @id", connection, transaction))
        {
            change.Parameters.AddWithValue("@id", id);
            await change.ExecuteNonQueryAsync(Ct);
        }

        var update = Store.UpdateDraftAsync(id, customer, Values("Sửa trong lúc chờ"), Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(800), Ct);
        Assert.False(update.IsCompleted, "UPDATE phải đang chờ khóa dòng.");

        await transaction.CommitAsync(Ct);

        Assert.Equal(UpdateDraftOutcome.NotDraft, await update);
        Assert.Equal("Gốc", (await Store.FindForCustomerAsync(id, customer, Ct))!.Title);
    }

    [MySqlFact]
    public async Task Concurrent_update_and_send_never_edit_after_open()
    {
        var customer = await CustomerAsync();
        var bakery = await BakeryAsync();
        var outcomes = new List<UpdateDraftOutcome>();

        for (var i = 0; i < 15; i++)
        {
            var id = await Store.CreateDraftAsync(customer, Values("Gốc"), Ct);
            var update = Task.Run(() => Store.UpdateDraftAsync(id, customer, Values("Đã sửa"), Ct));
            var send = Task.Run(() => Store.SendToBakeryAsync(id, customer, bakery, Ct));
            await Task.WhenAll(update, send);
            outcomes.Add(update.Result);

            Assert.Equal(SendToBakeryOutcome.Sent, send.Result);
            var dto = (await Store.FindForCustomerAsync(id, customer, Ct))!;
            Assert.Equal(DesignRequestStatuses.Open, dto.Status);
            Assert.Equal(update.Result == UpdateDraftOutcome.Updated ? "Đã sửa" : "Gốc", dto.Title);
            Assert.Contains(update.Result, new[] { UpdateDraftOutcome.Updated, UpdateDraftOutcome.NotDraft });

            // Procedure đặt updated_at = sent_at khi chuyển open. Nếu có lần sửa sau khi open, updated_at sẽ lớn hơn.
            var sentAt = (await Store.ListBakeriesAsync(id, customer, Ct))!.Single().SentAt;
            Assert.Equal(sentAt, dto.UpdatedAt);
        }
    }
}
