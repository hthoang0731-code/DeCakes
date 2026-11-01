using DeCakes.Application.BakeryWorkspace;
using DeCakes.Application.DesignRequests;
using DeCakes.Domain.BakeryWorkspace;
using DeCakes.Domain.DesignRequests;
using DeCakes.Infrastructure.BakeryWorkspace;
using DeCakes.Infrastructure.DesignRequests;

namespace DeCakes.Infrastructure.Tests.MySql;

/// <summary>
/// MySqlBakeryWorkspaceStore (inbox của tiệm) trên MySQL 8.4 kiểm thử. Yêu cầu được tạo và gửi bằng store của khách,
/// tức là qua sp_send_request_to_bakery nguồn với tài khoản có quyền như decakes_app. Users, tiệm và thành viên được
/// chuẩn bị bằng tài khoản quản trị của database tạm. Bị bỏ qua khi chưa cấu hình DECAKES_TEST_MYSQL_ADMIN. Không dùng Aiven.
/// </summary>
[Collection(MySqlCollection.Name)]
public class MySqlBakeryWorkspaceStoreTests(MySqlTestDatabase database)
{
    private static CancellationToken Ct => CancellationToken.None;

    private MySqlBakeryWorkspaceStore Store => new(database.Factory);

    private MySqlDesignRequestStore Requests => new(database.Factory);

    // ---------- Chuẩn bị dữ liệu ----------

    private Task<long> UserAsync(string role = "bakery", string status = "active") =>
        database.AdminExecuteAsync(
            "INSERT INTO users (email, full_name, phone, system_role, status) VALUES (@e, 'Người thử', '0900000000', @r, @s)",
            ("@e", $"u-{Guid.NewGuid():N}@example.com"), ("@r", role), ("@s", status));

    // Tiệm có kỳ sử dụng còn hạn (điều kiện nhận yêu cầu mới từ migrations/V005).
    private async Task<long> BakeryAsync(string name = "Tiệm thử", string profileStatus = "active")
    {
        var id = await database.AdminExecuteAsync(
            "INSERT INTO bakeries (name, slug, profile_status) VALUES (@n, @s, @p)",
            ("@n", name), ("@s", $"tiem-{Guid.NewGuid():N}"), ("@p", profileStatus));
        await database.GrantAccessAsync(id);
        return id;
    }

    private Task MemberAsync(long bakeryId, long userId, string role = BakeryMemberRoles.Staff, string status = BakeryMemberStatuses.Active) =>
        database.AdminExecuteAsync(
            "INSERT INTO bakery_members (bakery_id, user_id, member_role, status) VALUES (@b, @u, @r, @s)",
            ("@b", bakeryId), ("@u", userId), ("@r", role), ("@s", status));

    private static DesignRequestValues Values(string title)
    {
        var t = DateTime.UtcNow.AddDays(5);
        var pickup = new DateTime(t.Ticks - (t.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
        return new DesignRequestValues(title, "Bánh hai tầng, tông hồng.", 500_000m, 1_200_000.50m, "20 cm", 12, "Vani", "Mừng sinh nhật", pickup, null);
    }

    /// <summary>Tạo draft rồi gửi đến từng tiệm bằng procedure nguồn.</summary>
    private async Task<long> SentRequestAsync(long customer, string title, params long[] bakeries)
    {
        var id = await Requests.CreateDraftAsync(customer, Values(title), Ct);
        foreach (var bakery in bakeries)
        {
            Assert.Equal(SendToBakeryOutcome.Sent, await Requests.SendToBakeryAsync(id, customer, bakery, Ct));
        }

        return id;
    }

    private async Task<PagedResult<BakeryInboxItemDto>> InboxAsync(
        long userId, long bakeryId, string? status = null, string? requestStatus = null, int page = 1, int pageSize = 100) =>
        (await Store.ListInboxAsync(userId, bakeryId, status, requestStatus, page, pageSize, Ct))!;

    /// <summary>Toàn bộ cột của các dòng request_bakeries của tiệm và yêu cầu tương ứng, theo thứ tự id.</summary>
    private async Task<List<string>> SnapshotAsync(long bakeryId)
    {
        await using var connection = await database.AdminFactory.OpenConnectionAsync(Ct);
        await using var command = MySqlTestDatabase.Command(
            connection,
            """
            SELECT CONCAT_WS('|', rb.id, rb.design_request_id, rb.status, IFNULL(rb.decline_reason, '-'),
                   DATE_FORMAT(rb.sent_at, '%Y-%m-%d %H:%i:%s.%f'), IFNULL(DATE_FORMAT(rb.responded_at, '%Y-%m-%d %H:%i:%s.%f'), '-'),
                   DATE_FORMAT(rb.updated_at, '%Y-%m-%d %H:%i:%s.%f'), dr.status, dr.title,
                   DATE_FORMAT(dr.updated_at, '%Y-%m-%d %H:%i:%s.%f'))
              FROM request_bakeries rb JOIN design_requests dr ON dr.id = rb.design_request_id
             WHERE rb.bakery_id = @b
             ORDER BY rb.id
            """,
            ("@b", bakeryId));
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) rows.Add(reader.GetString(0));
        return rows;
    }

    // ---------- Tiệm của tôi ----------

    [MySqlFact]
    public async Task Member_bakeries_lists_only_active_memberships_of_active_bakery_users()
    {
        var user = await UserAsync();
        var alpha = await BakeryAsync("A tiệm active");
        var beta = await BakeryAsync("B tiệm suspended", "suspended");
        var gamma = await BakeryAsync("C tiệm đã nghỉ");
        await MemberAsync(alpha, user, BakeryMemberRoles.Owner);
        await MemberAsync(beta, user, BakeryMemberRoles.Staff);
        await MemberAsync(gamma, user, BakeryMemberRoles.Staff, BakeryMemberStatuses.Removed);

        var list = await Store.ListMemberBakeriesAsync(user, Ct);

        Assert.Equal([alpha, beta], list.Select(b => b.BakeryId));
        Assert.Equal(new WorkspaceBakeryDto(alpha, "A tiệm active", "active", BakeryMemberRoles.Owner), list[0]);
        Assert.Equal(new WorkspaceBakeryDto(beta, "B tiệm suspended", "suspended", BakeryMemberRoles.Staff), list[1]);

        // Giống sp_guard_active_bakery_member: user disabled hoặc không có system_role 'bakery' không có tiệm nào.
        var disabled = await UserAsync(status: "disabled");
        var customerWithRow = await UserAsync(role: "customer");
        await MemberAsync(alpha, disabled);
        await MemberAsync(alpha, customerWithRow);
        Assert.Empty(await Store.ListMemberBakeriesAsync(disabled, Ct));
        Assert.Empty(await Store.ListMemberBakeriesAsync(customerWithRow, Ct));
        Assert.Empty(await Store.ListMemberBakeriesAsync(long.MaxValue, Ct));
    }

    // ---------- Inbox: toàn luồng và cách ly ----------

    [MySqlFact]
    public async Task Inbox_reads_requests_sent_by_procedure_and_isolates_bakeries()
    {
        var customer = await UserAsync("customer");
        var bakeryA = await BakeryAsync();
        var bakeryB = await BakeryAsync();
        var ownerA = await UserAsync();
        var staffA = await UserAsync();
        var ownerB = await UserAsync();
        await MemberAsync(bakeryA, ownerA, BakeryMemberRoles.Owner);
        await MemberAsync(bakeryA, staffA);
        await MemberAsync(bakeryB, ownerB, BakeryMemberRoles.Owner);
        var both = await SentRequestAsync(customer, "Gửi cả hai", bakeryA, bakeryB);
        var onlyB = await SentRequestAsync(customer, "Chỉ B", bakeryB);
        var draft = await Requests.CreateDraftAsync(customer, Values("Chưa gửi"), Ct);

        foreach (var member in new[] { ownerA, staffA })
        {
            var inbox = await InboxAsync(member, bakeryA);
            Assert.Equal(1, inbox.TotalCount);
            var item = Assert.Single(inbox.Items);
            Assert.Equal(both, item.RequestId);
            Assert.Equal("Gửi cả hai", item.Title);
            Assert.Equal(DesignRequestStatuses.Open, item.RequestStatus);
            Assert.Equal(RequestBakeryStatuses.Pending, item.Status);
            Assert.Equal(500_000m, item.BudgetMin);
            Assert.Equal(1_200_000.50m, item.BudgetMax);
            Assert.Equal("VND", item.Currency);
            Assert.Equal(DateTimeKind.Utc, item.SentAt.Kind);
            Assert.Equal(DateTimeKind.Utc, item.PickupAt.Kind);

            Assert.NotNull(await Store.FindInboxRequestAsync(member, bakeryA, both, Ct));
            Assert.Null(await Store.FindInboxRequestAsync(member, bakeryA, onlyB, Ct));   // gửi tiệm khác
            Assert.Null(await Store.FindInboxRequestAsync(member, bakeryA, draft, Ct));   // chưa gửi
            Assert.Null(await Store.FindInboxRequestAsync(member, bakeryA, long.MaxValue, Ct));
            Assert.True(await Store.IsActiveMemberAsync(member, bakeryA, Ct));
        }

        Assert.Equal([onlyB, both], (await InboxAsync(ownerB, bakeryB)).Items.Select(i => i.RequestId));

        // bakeryId trên route không phải bằng chứng có quyền: thành viên A đọc B bị chặn ngay trong truy vấn.
        Assert.Null(await Store.ListInboxAsync(ownerA, bakeryB, null, null, 1, 20, Ct));
        Assert.Null(await Store.FindInboxRequestAsync(ownerA, bakeryB, onlyB, Ct));
        Assert.Null(await Store.FindInboxRequestAsync(ownerA, bakeryB, both, Ct));
        Assert.False(await Store.IsActiveMemberAsync(ownerA, bakeryB, Ct));
        Assert.Null(await Store.ListInboxAsync(ownerA, long.MaxValue, null, null, 1, 20, Ct));
        // Khách gửi yêu cầu không phải thành viên tiệm.
        Assert.Null(await Store.FindInboxRequestAsync(customer, bakeryA, both, Ct));
    }

    [MySqlFact]
    public async Task Removed_disabled_or_non_bakery_role_member_reads_nothing()
    {
        var customer = await UserAsync("customer");
        var bakery = await BakeryAsync();
        var removed = await UserAsync();
        var disabled = await UserAsync(status: "disabled");
        var wrongRole = await UserAsync(role: "customer");
        await MemberAsync(bakery, removed, BakeryMemberRoles.Owner, BakeryMemberStatuses.Removed);
        await MemberAsync(bakery, disabled, BakeryMemberRoles.Owner);
        await MemberAsync(bakery, wrongRole);
        var id = await SentRequestAsync(customer, "Yêu cầu", bakery);

        foreach (var user in new[] { removed, disabled, wrongRole })
        {
            Assert.False(await Store.IsActiveMemberAsync(user, bakery, Ct));
            Assert.Null(await Store.ListInboxAsync(user, bakery, null, null, 1, 20, Ct));
            Assert.Null(await Store.FindInboxRequestAsync(user, bakery, id, Ct));
        }

        // Bị removed sau khi đã đọc được: lần đọc kế tiếp bị chặn.
        var staff = await UserAsync();
        await MemberAsync(bakery, staff);
        Assert.NotNull(await Store.FindInboxRequestAsync(staff, bakery, id, Ct));
        await database.AdminExecuteAsync(
            "UPDATE bakery_members SET status = 'removed' WHERE bakery_id = @b AND user_id = @u", ("@b", bakery), ("@u", staff));
        Assert.Null(await Store.FindInboxRequestAsync(staff, bakery, id, Ct));
        Assert.Null(await Store.ListInboxAsync(staff, bakery, null, null, 1, 20, Ct));
    }

    [MySqlFact]
    public async Task Member_of_non_active_bakery_still_reads_history()
    {
        var customer = await UserAsync("customer");
        var bakery = await BakeryAsync();
        var owner = await UserAsync();
        await MemberAsync(bakery, owner, BakeryMemberRoles.Owner);
        var id = await SentRequestAsync(customer, "Gửi khi còn active", bakery);

        foreach (var profile in new[] { "suspended", "pending_review", "draft" })
        {
            await database.AdminExecuteAsync("UPDATE bakeries SET profile_status = @p WHERE id = @b", ("@p", profile), ("@b", bakery));
            Assert.Equal([id], (await InboxAsync(owner, bakery)).Items.Select(i => i.RequestId));
            Assert.NotNull(await Store.FindInboxRequestAsync(owner, bakery, id, Ct));
            Assert.Equal(profile, Assert.Single(await Store.ListMemberBakeriesAsync(owner, Ct)).ProfileStatus);
        }
    }

    // ---------- Thứ tự, phân trang, bộ lọc ----------

    [MySqlFact]
    public async Task Inbox_orders_by_sent_at_desc_then_row_id_desc_and_pages_stably_on_ties()
    {
        var customer = await UserAsync("customer");
        var bakery = await BakeryAsync();
        var member = await UserAsync();
        await MemberAsync(bakery, member);
        var newest = await SentRequestAsync(customer, "Gửi đầu tiên, sent_at muộn nhất", bakery);
        var tie1 = await SentRequestAsync(customer, "Trùng 1", bakery);
        var tie2 = await SentRequestAsync(customer, "Trùng 2", bakery);
        var tie3 = await SentRequestAsync(customer, "Trùng 3", bakery);
        var oldest = await SentRequestAsync(customer, "Gửi cuối, sent_at sớm nhất", bakery);

        // Đặt sent_at để có ba dòng trùng nhau; id dòng gửi tăng theo thứ tự gửi ở trên.
        await database.AdminExecuteAsync(
            """
            UPDATE request_bakeries
               SET sent_at = CASE design_request_id
                               WHEN @newest THEN '2030-01-01 10:00:00.000'
                               WHEN @oldest THEN '2030-01-01 08:00:00.000'
                               ELSE '2030-01-01 09:00:00.000' END
             WHERE bakery_id = @b
            """,
            ("@newest", newest), ("@oldest", oldest), ("@b", bakery));

        long[] expected = [newest, tie3, tie2, tie1, oldest];
        var all = await InboxAsync(member, bakery);
        Assert.Equal(expected, all.Items.Select(i => i.RequestId));
        Assert.Equal(5, all.TotalCount);
        Assert.Equal(new DateTime(2030, 1, 1, 9, 0, 0, DateTimeKind.Utc), all.Items[1].SentAt);

        for (var size = 1; size <= 3; size++)
        {
            var paged = new List<long>();
            for (var page = 1; page <= 5; page++)
            {
                var result = await InboxAsync(member, bakery, page: page, pageSize: size);
                Assert.Equal(5, result.TotalCount);
                Assert.Equal(page, result.Page);
                Assert.Equal(size, result.PageSize);
                paged.AddRange(result.Items.Select(i => i.RequestId));
            }

            Assert.Equal(expected, paged);
        }

        Assert.Empty((await InboxAsync(member, bakery, page: 6, pageSize: 1)).Items);
    }

    [MySqlFact]
    public async Task Inbox_filters_send_status_and_request_status_separately()
    {
        var customer = await UserAsync("customer");
        var bakery = await BakeryAsync();
        var member = await UserAsync();
        await MemberAsync(bakery, member);
        var pending = await SentRequestAsync(customer, "Chờ", bakery);
        var viewed = await SentRequestAsync(customer, "Đã xem", bakery);
        var declined = await SentRequestAsync(customer, "Từ chối và đã hủy", bakery);
        await database.AdminExecuteAsync(
            "UPDATE request_bakeries SET status = 'viewed' WHERE bakery_id = @b AND design_request_id = @r", ("@b", bakery), ("@r", viewed));
        await database.AdminExecuteAsync(
            """
            UPDATE request_bakeries SET status = 'declined', decline_reason = 'Hết lịch', responded_at = UTC_TIMESTAMP(3)
             WHERE bakery_id = @b AND design_request_id = @r
            """,
            ("@b", bakery), ("@r", declined));
        await database.AdminExecuteAsync("UPDATE design_requests SET status = 'cancelled' WHERE id = @r", ("@r", declined));

        Assert.Equal([pending], (await InboxAsync(member, bakery, status: "pending")).Items.Select(i => i.RequestId));
        Assert.Equal([viewed], (await InboxAsync(member, bakery, status: "viewed")).Items.Select(i => i.RequestId));
        var open = await InboxAsync(member, bakery, requestStatus: "open");
        Assert.Equal([viewed, pending], open.Items.Select(i => i.RequestId));
        Assert.Equal(2, open.TotalCount);
        Assert.Equal([declined], (await InboxAsync(member, bakery, requestStatus: "cancelled")).Items.Select(i => i.RequestId));
        Assert.Empty((await InboxAsync(member, bakery, status: "declined", requestStatus: "open")).Items);
        var both = await InboxAsync(member, bakery, status: "declined", requestStatus: "cancelled");
        Assert.Equal(1, both.TotalCount);
        Assert.Equal(DesignRequestStatuses.Cancelled, Assert.Single(both.Items).RequestStatus);
        Assert.Equal(RequestBakeryStatuses.Declined, both.Items[0].Status);
        Assert.Equal(0, (await InboxAsync(member, bakery, status: "quoted")).TotalCount);

        var detail = (await Store.FindInboxRequestAsync(member, bakery, declined, Ct))!;
        Assert.Equal("Hết lịch", detail.DeclineReason);
        Assert.NotNull(detail.RespondedAt);
        Assert.Equal(DateTimeKind.Utc, detail.RespondedAt.Value.Kind);
    }

    // ---------- Chi tiết và chỉ đọc ----------

    [MySqlFact]
    public async Task Detail_maps_request_content_and_send_row()
    {
        var customer = await UserAsync("customer");
        var bakery = await BakeryAsync();
        var member = await UserAsync();
        await MemberAsync(bakery, member);
        var cake = await database.AdminExecuteAsync(
            "INSERT INTO cakes (bakery_id, name, visibility) VALUES (@b, 'Mẫu', 'published')", ("@b", bakery));
        var values = Values("Tối giản") with { BudgetMin = null, SizeDescription = null, Servings = 65_535, Flavor = null, InspiredByCakeId = cake };
        var id = await Requests.CreateDraftAsync(customer, values, Ct);
        Assert.Equal(SendToBakeryOutcome.Sent, await Requests.SendToBakeryAsync(id, customer, bakery, Ct));
        var sent = Assert.Single((await Requests.ListBakeriesAsync(id, customer, Ct))!);

        var detail = (await Store.FindInboxRequestAsync(member, bakery, id, Ct))!;

        Assert.Equal(new BakeryInboxRequestDto(
            id, DesignRequestStatuses.Open, "Tối giản", values.IdeaDescription, null, values.BudgetMax, "VND", null, 65_535, null,
            values.InscriptionText, values.PickupAtUtc, cake, RequestBakeryStatuses.Pending, null, sent.SentAt, null), detail);
    }

    [MySqlFact]
    public async Task Reads_do_not_change_status_or_times()
    {
        var customer = await UserAsync("customer");
        var bakery = await BakeryAsync();
        var member = await UserAsync();
        await MemberAsync(bakery, member);
        var first = await SentRequestAsync(customer, "Một", bakery);
        await SentRequestAsync(customer, "Hai", bakery);
        var before = await SnapshotAsync(bakery);
        var membershipBefore = await database.AdminScalarAsync(
            "SELECT CONCAT_WS('|', status, member_role, updated_at) FROM bakery_members WHERE bakery_id = @b AND user_id = @u",
            ("@b", bakery), ("@u", member));

        await Task.Delay(TimeSpan.FromMilliseconds(20), Ct); // nếu có ghi UTC_TIMESTAMP(3) thì giá trị sẽ khác
        for (var i = 0; i < 3; i++)
        {
            await Store.ListMemberBakeriesAsync(member, Ct);
            await Store.ListInboxAsync(member, bakery, null, null, 1, 20, Ct);
            await Store.ListInboxAsync(member, bakery, "pending", "open", 1, 1, Ct);
            await Store.FindInboxRequestAsync(member, bakery, first, Ct);
            await Store.IsActiveMemberAsync(member, bakery, Ct);
        }

        Assert.Equal(2, before.Count);
        Assert.Equal(before, await SnapshotAsync(bakery));
        Assert.Equal(membershipBefore, await database.AdminScalarAsync(
            "SELECT CONCAT_WS('|', status, member_role, updated_at) FROM bakery_members WHERE bakery_id = @b AND user_id = @u",
            ("@b", bakery), ("@u", member)));
        Assert.All((await InboxAsync(member, bakery)).Items, i => Assert.Equal(RequestBakeryStatuses.Pending, i.Status));
    }
}
