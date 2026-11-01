using System.Net;
using System.Text.Json;
using DeCakes.Api.Tests.Infrastructure;
using DeCakes.Domain.Accounts;
using DeCakes.Domain.BakeryWorkspace;
using DeCakes.Domain.DesignRequests;

namespace DeCakes.Api.Tests;

/// <summary>
/// Inbox của tiệm với store trong bộ nhớ (không database). Khách tạo và gửi yêu cầu qua API DesignRequests,
/// thành viên tiệm đọc qua /api/bakery-workspace. Đồng hồ: FakeTimeProvider, 2026-10-15T08:00Z.
/// </summary>
public class BakeryWorkspaceApiTests
{
    private const string Password = "mat-khau-du-dai-1";
    private const string Base = "/api/bakery-workspace/bakeries";
    private const long BakeryA = 9;
    private const long BakeryB = 10;

    private static async Task<(ApiClient Client, long UserId)> LoginAsync(TestApp app, string email, string role = SystemRoles.Bakery)
    {
        var id = app.AddUser(email, Password, role);
        var client = app.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(email, Password)).StatusCode);
        return (client, id);
    }

    private static TestApp NewApp()
    {
        var app = new TestApp();
        app.DesignRequests.AddBakery(BakeryA, "Tiệm A");
        app.DesignRequests.AddBakery(BakeryB, "Tiệm B");
        return app;
    }

    /// <summary>Khách tạo một yêu cầu và gửi đến các tiệm qua API của khách.</summary>
    private static async Task<long> SendRequestAsync(ApiClient customer, string title, params long[] bakeries)
    {
        var created = await customer.PostAsync("/api/design-requests", new
        {
            title,
            ideaDescription = "Bánh hai tầng, tông hồng.",
            budgetMin = 500_000m,
            budgetMax = 1_200_000.50m,
            sizeDescription = "20 cm",
            servings = 12,
            flavor = "Vani",
            inscriptionText = "Mừng sinh nhật",
            pickupAt = "2026-10-20T09:30:00+07:00",
        }, await customer.GetCsrfTokenAsync());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonAsync(created)).GetProperty("id").GetInt64();

        foreach (var bakery in bakeries)
        {
            var sent = await customer.PostAsync($"/api/design-requests/{id}/bakeries/{bakery}", null, await customer.GetCsrfTokenAsync());
            Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        }

        return id;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<string> TextAsync(ApiClient client, string url) =>
        await (await client.GetAsync(url)).Content.ReadAsStringAsync();

    private static async Task AssertProblemCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    private static long[] RequestIds(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("requestId").GetInt64()).ToArray();

    // ---------- Xác thực và policy ----------

    [Fact]
    public async Task Anonymous_gets_401_customer_and_admin_get_403()
    {
        using var app = NewApp();
        app.BakeryWorkspace.AddMember(BakeryA, 1);
        string[] urls = [Base, $"{Base}/{BakeryA}/requests", $"{Base}/{BakeryA}/requests/1"];

        using var anonymous = app.CreateApiClient();
        var (customer, _) = await LoginAsync(app, "khach@example.com", SystemRoles.Customer);
        var (admin, _) = await LoginAsync(app, "admin@example.com", SystemRoles.Admin);
        using (customer)
        using (admin)
        {
            foreach (var url in urls)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync(url)).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(url)).StatusCode);
            }
        }
    }

    // ---------- Tiệm của tôi ----------

    [Fact]
    public async Task Bakeries_lists_only_active_memberships_with_role_and_profile_status()
    {
        using var app = NewApp();
        app.DesignRequests.AddBakery(11, "Tiệm C", "suspended");
        app.DesignRequests.AddBakery(12, "Tiệm D", "pending_review");
        var (user, userId) = await LoginAsync(app, "tiem@example.com");
        var (other, otherId) = await LoginAsync(app, "khac@example.com");
        var (noBakery, _) = await LoginAsync(app, "chua-co-tiem@example.com");
        using (user)
        using (other)
        using (noBakery)
        {
            app.BakeryWorkspace.AddMember(BakeryA, userId, BakeryMemberRoles.Owner);
            app.BakeryWorkspace.AddMember(11, userId, BakeryMemberRoles.Staff);
            app.BakeryWorkspace.AddMember(12, userId, BakeryMemberRoles.Staff, BakeryMemberStatuses.Removed);
            app.BakeryWorkspace.AddMember(BakeryB, otherId, BakeryMemberRoles.Owner);

            var response = await user.GetAsync(Base);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var list = (await JsonAsync(response)).EnumerateArray().ToList();
            Assert.Equal([BakeryA, 11L], list.Select(b => b.GetProperty("bakeryId").GetInt64()));
            Assert.Equal(["Tiệm A", "Tiệm C"], list.Select(b => b.GetProperty("name").GetString()));
            Assert.Equal(["active", "suspended"], list.Select(b => b.GetProperty("profileStatus").GetString()));
            Assert.Equal([BakeryMemberRoles.Owner, BakeryMemberRoles.Staff], list.Select(b => b.GetProperty("memberRole").GetString()));
            Assert.All(list, b => Assert.Equal(
                ["bakeryId", "memberRole", "name", "profileStatus"], b.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));

            Assert.Equal("[]", await TextAsync(noBakery, Base));
            Assert.Equal([BakeryB], (await JsonAsync(await other.GetAsync(Base))).EnumerateArray().Select(b => b.GetProperty("bakeryId").GetInt64()));
        }
    }

    // ---------- Inbox ----------

    [Fact]
    public async Task Owner_and_staff_read_requests_sent_to_their_bakery_only()
    {
        using var app = NewApp();
        var (customer, _) = await LoginAsync(app, "khach@example.com", SystemRoles.Customer);
        var (owner, ownerId) = await LoginAsync(app, "chu@example.com");
        var (staff, staffId) = await LoginAsync(app, "nhanvien@example.com");
        var (otherBakery, otherId) = await LoginAsync(app, "tiem-b@example.com");
        using (customer)
        using (owner)
        using (staff)
        using (otherBakery)
        {
            app.BakeryWorkspace.AddMember(BakeryA, ownerId, BakeryMemberRoles.Owner);
            app.BakeryWorkspace.AddMember(BakeryA, staffId, BakeryMemberRoles.Staff);
            app.BakeryWorkspace.AddMember(BakeryB, otherId, BakeryMemberRoles.Owner);
            var both = await SendRequestAsync(customer, "Gửi cả hai tiệm", BakeryA, BakeryB);
            var onlyB = await SendRequestAsync(customer, "Chỉ tiệm B", BakeryB);

            foreach (var member in new[] { owner, staff })
            {
                var page = await JsonAsync(await member.GetAsync($"{Base}/{BakeryA}/requests"));
                Assert.Equal([both], RequestIds(page));
                Assert.Equal(1, page.GetProperty("totalCount").GetInt64());

                var detail = await member.GetAsync($"{Base}/{BakeryA}/requests/{both}");
                Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
                Assert.Equal("Gửi cả hai tiệm", (await JsonAsync(detail)).GetProperty("title").GetString());
            }

            Assert.Equal([onlyB, both], RequestIds(await JsonAsync(await otherBakery.GetAsync($"{Base}/{BakeryB}/requests"))));

            // Thành viên tiệm A đổi bakeryId trên route sang tiệm B: không có quyền, dù yêu cầu có gửi tới B.
            await AssertProblemCodeAsync(await owner.GetAsync($"{Base}/{BakeryB}/requests"), HttpStatusCode.NotFound, "BAKERY_NOT_FOUND");
            await AssertProblemCodeAsync(await owner.GetAsync($"{Base}/{BakeryB}/requests/{onlyB}"), HttpStatusCode.NotFound, "BAKERY_NOT_FOUND");
            await AssertProblemCodeAsync(await owner.GetAsync($"{Base}/{BakeryB}/requests/{both}"), HttpStatusCode.NotFound, "BAKERY_NOT_FOUND");
        }
    }

    [Fact]
    public async Task Removed_member_unknown_bakery_and_non_member_share_one_404()
    {
        using var app = NewApp();
        var (customer, _) = await LoginAsync(app, "khach@example.com", SystemRoles.Customer);
        var (removed, removedId) = await LoginAsync(app, "da-nghi@example.com");
        using (customer)
        using (removed)
        {
            app.BakeryWorkspace.AddMember(BakeryA, removedId, BakeryMemberRoles.Owner, BakeryMemberStatuses.Removed);
            var id = await SendRequestAsync(customer, "Yêu cầu", BakeryA);

            foreach (var bakery in new[] { BakeryA, BakeryB, 999L })
            {
                await AssertProblemCodeAsync(await removed.GetAsync($"{Base}/{bakery}/requests"), HttpStatusCode.NotFound, "BAKERY_NOT_FOUND");
                await AssertProblemCodeAsync(await removed.GetAsync($"{Base}/{bakery}/requests/{id}"), HttpStatusCode.NotFound, "BAKERY_NOT_FOUND");
            }

            Assert.Equal(0, (await JsonAsync(await removed.GetAsync(Base))).GetArrayLength());
            Assert.Equal(HttpStatusCode.NotFound, (await removed.GetAsync($"{Base}/0/requests")).StatusCode); // ràng buộc route
        }
    }

    [Fact]
    public async Task Request_not_sent_to_bakery_draft_or_unknown_is_request_not_found()
    {
        using var app = NewApp();
        var (customer, _) = await LoginAsync(app, "khach@example.com", SystemRoles.Customer);
        var (member, memberId) = await LoginAsync(app, "tiem@example.com");
        using (customer)
        using (member)
        {
            app.BakeryWorkspace.AddMember(BakeryA, memberId);
            var sentToB = await SendRequestAsync(customer, "Chỉ tiệm B", BakeryB);
            var draft = await SendRequestAsync(customer, "Còn draft");

            foreach (var id in new[] { sentToB, draft, long.MaxValue })
            {
                await AssertProblemCodeAsync(await member.GetAsync($"{Base}/{BakeryA}/requests/{id}"), HttpStatusCode.NotFound, "REQUEST_NOT_FOUND");
            }

            Assert.Empty(RequestIds(await JsonAsync(await member.GetAsync($"{Base}/{BakeryA}/requests"))));
        }
    }

    [Fact]
    public async Task Detail_and_list_expose_request_content_but_no_customer_account_data()
    {
        using var app = NewApp();
        var (customer, _) = await LoginAsync(app, "khach-bi-mat@example.com", SystemRoles.Customer);
        var (member, memberId) = await LoginAsync(app, "tiem@example.com");
        using (customer)
        using (member)
        {
            app.BakeryWorkspace.AddMember(BakeryA, memberId);
            var id = await SendRequestAsync(customer, "Bánh sinh nhật", BakeryA);

            var detailResponse = await member.GetAsync($"{Base}/{BakeryA}/requests/{id}");
            var listResponse = await member.GetAsync($"{Base}/{BakeryA}/requests");
            var detailText = await detailResponse.Content.ReadAsStringAsync();
            var listText = await listResponse.Content.ReadAsStringAsync();
            var detail = await JsonAsync(detailResponse);
            var item = (await JsonAsync(listResponse)).GetProperty("items")[0];

            Assert.Equal(
                ["budgetMax", "budgetMin", "currency", "declineReason", "flavor", "ideaDescription", "inscriptionText", "inspiredByCakeId",
                 "pickupAt", "requestId", "requestStatus", "respondedAt", "sentAt", "servings", "sizeDescription", "status", "title"],
                detail.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Equal(
                ["budgetMax", "budgetMin", "currency", "pickupAt", "requestId", "requestStatus", "sentAt", "status", "title"],
                item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            foreach (var text in new[] { detailText, listText })
            {
                Assert.DoesNotContain("khach-bi-mat", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("email", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("phone", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("customer", text, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Equal(id, detail.GetProperty("requestId").GetInt64());
            Assert.Equal(DesignRequestStatuses.Open, detail.GetProperty("requestStatus").GetString());
            Assert.Equal(RequestBakeryStatuses.Pending, detail.GetProperty("status").GetString());
            Assert.Equal("Bánh hai tầng, tông hồng.", detail.GetProperty("ideaDescription").GetString());
            Assert.Equal(1_200_000.50m, detail.GetProperty("budgetMax").GetDecimal());
            Assert.Equal("2026-10-20T02:30:00Z", detail.GetProperty("pickupAt").GetString());
            Assert.Equal("2026-10-15T08:00:00Z", detail.GetProperty("sentAt").GetString());
            Assert.Equal(JsonValueKind.Null, detail.GetProperty("respondedAt").ValueKind);
        }
    }

    [Fact]
    public async Task Inbox_orders_by_sent_at_then_id_descending_and_pages()
    {
        using var app = NewApp();
        var (customer, _) = await LoginAsync(app, "khach@example.com", SystemRoles.Customer);
        var (member, memberId) = await LoginAsync(app, "tiem@example.com");
        using (customer)
        using (member)
        {
            app.BakeryWorkspace.AddMember(BakeryA, memberId);
            // Tạo trước tất cả nhưng gửi muộn nhất: thứ tự theo thời điểm gửi, không theo thời điểm tạo.
            var createdEarly = await SendRequestAsync(customer, "Tạo sớm");
            // Ba yêu cầu gửi cùng thời điểm (sent_at trùng): thứ tự theo id dòng gửi giảm dần.
            var tie1 = await SendRequestAsync(customer, "Trùng 1", BakeryA);
            var tie2 = await SendRequestAsync(customer, "Trùng 2", BakeryA);
            var tie3 = await SendRequestAsync(customer, "Trùng 3", BakeryA);
            app.Time.Advance(TimeSpan.FromMinutes(1));
            var later = await SendRequestAsync(customer, "Sau", BakeryA);
            app.Time.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(HttpStatusCode.Created,
                (await customer.PostAsync($"/api/design-requests/{createdEarly}/bakeries/{BakeryA}", null, await customer.GetCsrfTokenAsync())).StatusCode);

            var all = await JsonAsync(await member.GetAsync($"{Base}/{BakeryA}/requests"));
            Assert.Equal([createdEarly, later, tie3, tie2, tie1], RequestIds(all));
            Assert.Equal(20, all.GetProperty("pageSize").GetInt32());
            Assert.Equal(1, all.GetProperty("page").GetInt32());

            var page1 = await JsonAsync(await member.GetAsync($"{Base}/{BakeryA}/requests?page=1&pageSize=2"));
            var page2 = await JsonAsync(await member.GetAsync($"{Base}/{BakeryA}/requests?page=2&pageSize=2"));
            var page3 = await JsonAsync(await member.GetAsync($"{Base}/{BakeryA}/requests?page=3&pageSize=2"));
            var beyond = await JsonAsync(await member.GetAsync($"{Base}/{BakeryA}/requests?page=4&pageSize=2"));
            Assert.Equal([createdEarly, later], RequestIds(page1));
            Assert.Equal([tie3, tie2], RequestIds(page2));
            Assert.Equal([tie1], RequestIds(page3));
            Assert.Empty(RequestIds(beyond));
            Assert.Equal(5, beyond.GetProperty("totalCount").GetInt64());
            Assert.Equal(5, (await JsonAsync(await member.GetAsync($"{Base}/{BakeryA}/requests?pageSize=100"))).GetProperty("items").GetArrayLength());
        }
    }

    [Fact]
    public async Task Inbox_filters_by_send_status_and_request_status_separately()
    {
        using var app = NewApp();
        var (customer, _) = await LoginAsync(app, "khach@example.com", SystemRoles.Customer);
        var (member, memberId) = await LoginAsync(app, "tiem@example.com");
        using (customer)
        using (member)
        {
            app.BakeryWorkspace.AddMember(BakeryA, memberId);
            var pending = await SendRequestAsync(customer, "Chờ", BakeryA);
            var viewed = await SendRequestAsync(customer, "Đã xem", BakeryA);
            var declinedCancelled = await SendRequestAsync(customer, "Từ chối", BakeryA);
            app.DesignRequests.SetSendStatus(viewed, BakeryA, RequestBakeryStatuses.Viewed);
            app.DesignRequests.SetSendStatus(declinedCancelled, BakeryA, RequestBakeryStatuses.Declined, "Hết lịch");
            app.DesignRequests.SetRequestStatus(declinedCancelled, DesignRequestStatuses.Cancelled);

            Assert.Equal([pending], RequestIds(await JsonAsync(await member.GetAsync($"{Base}/{BakeryA}/requests?status=pending"))));
            Assert.Equal([viewed], RequestIds(await JsonAsync(await member.GetAsync($"{Base}/{BakeryA}/requests?status=%20viewed%20"))));
            Assert.Equal([viewed, pending], RequestIds(await JsonAsync(await member.GetAsync($"{Base}/{BakeryA}/requests?requestStatus=open"))));
            Assert.Equal([declinedCancelled], RequestIds(await JsonAsync(await member.GetAsync($"{Base}/{BakeryA}/requests?requestStatus=cancelled"))));
            Assert.Empty(RequestIds(await JsonAsync(await member.GetAsync($"{Base}/{BakeryA}/requests?status=declined&requestStatus=open"))));
            var both = await JsonAsync(await member.GetAsync($"{Base}/{BakeryA}/requests?status=declined&requestStatus=cancelled"));
            Assert.Equal([declinedCancelled], RequestIds(both));
            Assert.Equal(1, both.GetProperty("totalCount").GetInt64());
            Assert.Equal(3, (await JsonAsync(await member.GetAsync($"{Base}/{BakeryA}/requests?status=&requestStatus=%20"))).GetProperty("totalCount").GetInt64());

            var declined = await JsonAsync(await member.GetAsync($"{Base}/{BakeryA}/requests/{declinedCancelled}"));
            Assert.Equal("Hết lịch", declined.GetProperty("declineReason").GetString());
            Assert.Equal(JsonValueKind.String, declined.GetProperty("respondedAt").ValueKind);
        }
    }

    [Theory]
    [InlineData("status=open", "status")]                  // giá trị của design_requests, không phải request_bakeries
    [InlineData("status=PENDING", "status")]
    [InlineData("requestStatus=pending", "requestStatus")] // giá trị của request_bakeries
    [InlineData("page=0", "page")]
    [InlineData("page=-1", "page")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("pageSize=101", "pageSize")]
    [InlineData("page=abc", "page")]
    public async Task Invalid_query_parameters_return_400(string query, string field)
    {
        using var app = NewApp();
        var (member, memberId) = await LoginAsync(app, "tiem@example.com");
        using (member)
        {
            app.BakeryWorkspace.AddMember(BakeryA, memberId);

            var response = await member.GetAsync($"{Base}/{BakeryA}/requests?{query}");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True((await JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _));
        }
    }

    [Fact]
    public async Task Get_does_not_mark_viewed_or_change_times()
    {
        using var app = NewApp();
        var (customer, _) = await LoginAsync(app, "khach@example.com", SystemRoles.Customer);
        var (member, memberId) = await LoginAsync(app, "tiem@example.com");
        using (customer)
        using (member)
        {
            app.BakeryWorkspace.AddMember(BakeryA, memberId);
            var id = await SendRequestAsync(customer, "Yêu cầu", BakeryA);
            var before = await TextAsync(customer, $"/api/design-requests/{id}/bakeries");
            var requestBefore = await TextAsync(customer, $"/api/design-requests/{id}");
            var firstDetail = await TextAsync(member, $"{Base}/{BakeryA}/requests/{id}");

            app.Time.Advance(TimeSpan.FromMinutes(5));
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"{Base}/{BakeryA}/requests")).StatusCode);
                Assert.Equal(firstDetail, await TextAsync(member, $"{Base}/{BakeryA}/requests/{id}"));
            }

            Assert.Equal(before, await TextAsync(customer, $"/api/design-requests/{id}/bakeries"));
            Assert.Equal(requestBefore, await TextAsync(customer, $"/api/design-requests/{id}"));
            Assert.Contains("\"status\":\"pending\"", before);
        }
    }

    [Fact]
    public async Task Member_of_non_active_bakery_still_reads_history()
    {
        using var app = NewApp();
        var (customer, _) = await LoginAsync(app, "khach@example.com", SystemRoles.Customer);
        var (member, memberId) = await LoginAsync(app, "tiem@example.com");
        using (customer)
        using (member)
        {
            app.BakeryWorkspace.AddMember(BakeryA, memberId);
            var id = await SendRequestAsync(customer, "Gửi khi còn active", BakeryA);

            foreach (var profile in new[] { "suspended", "pending_review", "draft" })
            {
                app.DesignRequests.AddBakery(BakeryA, "Tiệm A", profile);
                Assert.Equal([id], RequestIds(await JsonAsync(await member.GetAsync($"{Base}/{BakeryA}/requests"))));
                Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"{Base}/{BakeryA}/requests/{id}")).StatusCode);
                Assert.Equal(profile, (await JsonAsync(await member.GetAsync(Base)))[0].GetProperty("profileStatus").GetString());
            }
        }
    }

    [Fact]
    public async Task Database_failure_is_masked_500()
    {
        using var app = NewApp();
        var (member, memberId) = await LoginAsync(app, "tiem@example.com");
        using (member)
        {
            app.BakeryWorkspace.AddMember(BakeryA, memberId);
            app.BakeryWorkspace.Fail = true;

            foreach (var url in new[] { Base, $"{Base}/{BakeryA}/requests", $"{Base}/{BakeryA}/requests/1" })
            {
                var response = await member.GetAsync(url);
                Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
                Assert.DoesNotContain("SIMULATED", await response.Content.ReadAsStringAsync());
            }
        }
    }
}
