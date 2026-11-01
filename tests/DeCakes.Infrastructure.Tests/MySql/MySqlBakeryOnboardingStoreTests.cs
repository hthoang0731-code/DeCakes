using DeCakes.Application.Accounts;
using DeCakes.Application.BakeryOnboarding;
using DeCakes.Domain.BakeryOnboarding;
using DeCakes.Domain.Subscriptions;
using DeCakes.Infrastructure.BakeryOnboarding;
using DeCakes.Infrastructure.BakeryWorkspace;
using MySqlConnector;

namespace DeCakes.Infrastructure.Tests.MySql;

/// <summary>
/// MySqlBakeryOnboardingStore và migration đề xuất V004 trên MySQL 8.4 kiểm thử (database tạm, tài khoản có quyền backend +
/// grants V003/V004). Bị bỏ qua khi chưa cấu hình DECAKES_TEST_MYSQL_ADMIN. Không dùng Aiven.
/// </summary>
[Collection(MySqlCollection.Name)]
public class MySqlBakeryOnboardingStoreTests(MySqlTestDatabase database)
{
    private static readonly DateTime Now = new(2026, 10, 22, 1, 0, 0, 500, DateTimeKind.Utc);
    private static CancellationToken Ct => CancellationToken.None;

    private MySqlBakeryOnboardingStore Store => new(database.Factory);

    private static string UniqueEmail(string prefix = "o") => $"{prefix}-{Guid.NewGuid():N}@example.com";

    private Task<long> UserAsync(string email, string role = "bakery", string status = "active") =>
        database.AdminExecuteAsync(
            "INSERT INTO users (email, password_hash, full_name, system_role, status) VALUES (@e, 'H', 'Người dùng', @r, @s)",
            ("@e", email), ("@r", role), ("@s", status));

    private Task<long> PlanAsync(string status = "active", decimal? monthly = null) =>
        database.AdminExecuteAsync(
            "INSERT INTO subscription_plans (code, name, status, monthly_price) VALUES (@c, 'Gói thử', @s, @m)",
            ("@c", "p-" + Guid.NewGuid().ToString("N")[..10]), ("@s", status), ("@m", monthly));

    private Task MemberAsync(long bakeryId, long userId, string role, string status = "active") =>
        database.AdminExecuteAsync(
            "INSERT INTO bakery_members (bakery_id, user_id, member_role, status) VALUES (@b, @u, @r, @s)",
            ("@b", bakeryId), ("@u", userId), ("@r", role), ("@s", status));

    /// <summary>Tiệm với owner (và staff) qua đăng ký thật của store.</summary>
    private async Task<(long OwnerId, long BakeryId, long StaffId)> BakeryAsync(string name = "Tiệm Bánh Đẹp")
    {
        var (ownerId, bakeryId) = await Store.CreateOwnerAsync(
            new NewBakeryOwner(UniqueEmail(), "HASH", "Chủ tiệm", null, name, BakeryProfileRules.SlugBase(name)), Ct);
        var staffId = await UserAsync(UniqueEmail("staff"));
        await MemberAsync(bakeryId, staffId, "staff");
        return (ownerId, bakeryId, staffId);
    }

    private async Task<long> SubmittedAsync(long ownerId, long bakeryId, long? planId = null)
    {
        Assert.Equal(OwnerActionOutcome.Succeeded, await Store.UpdateDraftProfileAsync(ownerId, bakeryId,
            new BakeryProfileValues("Tiệm Bánh Đẹp", null, "1 Lê Lợi", "Huế", "0901234567"), Now, Ct));
        planId ??= await PlanAsync();
        Assert.Equal(OwnerActionOutcome.Succeeded, await Store.SelectPlanAsync(ownerId, bakeryId, planId.Value, Now, Ct));
        Assert.Equal(SubmitProfileStatus.Submitted, (await Store.SubmitAsync(ownerId, bakeryId, Now, Ct)).Status);
        return planId.Value;
    }

    private async Task<object?> ScalarAsync(string sql, params (string, object?)[] parameters) =>
        await database.AdminScalarAsync(sql, parameters);

    private async Task<long> CountAsync(string sql, params (string, object?)[] parameters) =>
        Convert.ToInt64(await ScalarAsync(sql, parameters));

    // ---------------- Đăng ký chủ tiệm ----------------

    [MySqlFact]
    public async Task Owner_registration_creates_bakery_user_draft_profile_and_owner_membership_atomically()
    {
        var email = UniqueEmail();
        var (userId, bakeryId) = await Store.CreateOwnerAsync(
            new NewBakeryOwner(email, "HASH", "Chủ tiệm", "0901", "Tiệm Bánh Đẹp", "tiem-banh-dep"), Ct);

        Assert.Equal("bakery|active|0|", await ScalarAsync(
            "SELECT CONCAT(system_role, '|', status, '|', is_demo, '|', IFNULL(email_verified_at, '')) FROM users WHERE id = @id", ("@id", userId)));
        Assert.Equal("draft|0", await ScalarAsync("SELECT CONCAT(profile_status, '|', is_demo) FROM bakeries WHERE id = @id", ("@id", bakeryId)));
        Assert.Matches("^tiem-banh-dep-[0-9a-f]{6}$", (string)(await ScalarAsync("SELECT slug FROM bakeries WHERE id = @id", ("@id", bakeryId)))!);
        Assert.Equal("owner|active", await ScalarAsync(
            "SELECT CONCAT(member_role, '|', status) FROM bakery_members WHERE bakery_id = @b AND user_id = @u", ("@b", bakeryId), ("@u", userId)));
    }

    [MySqlFact]
    public async Task Owner_registration_with_existing_email_changes_nothing_and_leaves_no_orphans()
    {
        var email = UniqueEmail();
        var customerId = await UserAsync(email, role: "customer");
        var name = "Tiệm " + Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<DuplicateEmailException>(() => Store.CreateOwnerAsync(
            new NewBakeryOwner(email.ToUpperInvariant(), "HASH", "X", null, name, "x"), Ct));

        Assert.Equal("customer", await ScalarAsync("SELECT system_role FROM users WHERE id = @id", ("@id", customerId)));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM bakeries WHERE name = @n", ("@n", name)));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM bakery_members WHERE user_id = @u", ("@u", customerId)));
    }

    [MySqlFact]
    public async Task Concurrent_owner_registrations_with_one_email_create_exactly_one_account_bakery_and_owner()
    {
        var email = UniqueEmail();
        var name = "Đồng thời " + Guid.NewGuid().ToString("N");

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            try
            {
                await Store.CreateOwnerAsync(new NewBakeryOwner(email, "HASH", "X", null, name, "dong-thoi"), Ct);
                return true;
            }
            catch (DuplicateEmailException)
            {
                return false;
            }
        })));

        Assert.Equal(1, attempts.Count(ok => ok));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM bakeries WHERE name = @n", ("@n", name)));
        Assert.Equal(1, await CountAsync(
            "SELECT COUNT(*) FROM bakery_members bm JOIN bakeries b ON b.id = bm.bakery_id WHERE b.name = @n AND bm.member_role = 'owner'", ("@n", name)));
    }

    [MySqlFact]
    public async Task Same_bakery_name_gets_distinct_slugs()
    {
        var first = await BakeryAsync("Tiệm Trùng Tên");
        var second = await BakeryAsync("Tiệm Trùng Tên");

        Assert.NotEqual(
            await ScalarAsync("SELECT slug FROM bakeries WHERE id = @id", ("@id", first.BakeryId)),
            await ScalarAsync("SELECT slug FROM bakeries WHERE id = @id", ("@id", second.BakeryId)));
    }

    // ---------------- Hồ sơ và gửi duyệt ----------------

    [MySqlFact]
    public async Task Profile_is_visible_to_active_members_only_and_editable_by_owner_in_draft_only()
    {
        var (owner, bakery, staff) = await BakeryAsync();
        var (otherOwner, _, _) = await BakeryAsync();
        var values = new BakeryProfileValues("Tên mới", "Mô tả", "1 Lê Lợi", "Huế", "0901");

        Assert.Equal("owner", (await Store.FindProfileAsync(owner, bakery, Ct))!.MemberRole);
        Assert.Equal("staff", (await Store.FindProfileAsync(staff, bakery, Ct))!.MemberRole);
        Assert.Null(await Store.FindProfileAsync(otherOwner, bakery, Ct));

        Assert.Equal(OwnerActionOutcome.OwnerRequired, await Store.UpdateDraftProfileAsync(staff, bakery, values, Now, Ct));
        Assert.Equal(OwnerActionOutcome.BakeryNotFound, await Store.UpdateDraftProfileAsync(otherOwner, bakery, values, Now, Ct));
        Assert.Equal(OwnerActionOutcome.BakeryNotFound, await Store.UpdateDraftProfileAsync(owner, long.MaxValue, values, Now, Ct));
        Assert.Equal(OwnerActionOutcome.Succeeded, await Store.UpdateDraftProfileAsync(owner, bakery, values, Now, Ct));
        var profile = (await Store.FindProfileAsync(owner, bakery, Ct))!;
        Assert.Equal(("Tên mới", "Mô tả", "1 Lê Lợi", "Huế", "0901"), (profile.Name, profile.Description, profile.AddressLine, profile.City, profile.Phone));

        await database.AdminExecuteAsync("UPDATE bakery_members SET status = 'removed' WHERE bakery_id = @b AND user_id = @u", ("@b", bakery), ("@u", staff));
        Assert.Null(await Store.FindProfileAsync(staff, bakery, Ct));

        await SubmittedAsync(owner, bakery);
        Assert.Equal(OwnerActionOutcome.NotAllowedInStatus, await Store.UpdateDraftProfileAsync(owner, bakery, values, Now, Ct));
    }

    [MySqlFact]
    public async Task Submit_requires_owner_draft_complete_profile_and_open_plan_then_records_event()
    {
        var (owner, bakery, staff) = await BakeryAsync();

        var incomplete = await Store.SubmitAsync(owner, bakery, Now, Ct);
        Assert.Equal(SubmitProfileStatus.Incomplete, incomplete.Status);
        Assert.Equal(["addressLine", "city", "phone", "plan"], incomplete.Missing);

        await Store.UpdateDraftProfileAsync(owner, bakery, new BakeryProfileValues("Tiệm", null, "1 Lê Lợi", "Huế", "0901"), Now, Ct);
        var draftPlan = await PlanAsync(status: "draft");
        Assert.Equal(OwnerActionOutcome.PlanNotAvailable, await Store.SelectPlanAsync(owner, bakery, draftPlan, Now, Ct));
        Assert.Equal(["plan"], (await Store.SubmitAsync(owner, bakery, Now, Ct)).Missing);

        var plan = await PlanAsync();
        Assert.Equal(OwnerActionOutcome.OwnerRequired, await Store.SelectPlanAsync(staff, bakery, plan, Now, Ct));
        Assert.Equal(OwnerActionOutcome.Succeeded, await Store.SelectPlanAsync(owner, bakery, plan, Now, Ct));
        // Gói bị đóng sau khi chọn: không đủ điều kiện gửi.
        await database.AdminExecuteAsync("UPDATE subscription_plans SET status = 'retired' WHERE id = @p", ("@p", plan));
        Assert.Equal(["plan"], (await Store.SubmitAsync(owner, bakery, Now, Ct)).Missing);
        await database.AdminExecuteAsync("UPDATE subscription_plans SET status = 'active' WHERE id = @p", ("@p", plan));

        Assert.Equal(SubmitProfileStatus.OwnerRequired, (await Store.SubmitAsync(staff, bakery, Now, Ct)).Status);
        Assert.Equal(SubmitProfileStatus.Submitted, (await Store.SubmitAsync(owner, bakery, Now, Ct)).Status);
        Assert.Equal(SubmitProfileStatus.NotDraft, (await Store.SubmitAsync(owner, bakery, Now, Ct)).Status);

        Assert.Equal("pending_review", await ScalarAsync("SELECT profile_status FROM bakeries WHERE id = @b", ("@b", bakery)));
        var events = (await Store.ListReviewEventsAsync(bakery, Ct))!;
        var submitted = Assert.Single(events);
        Assert.Equal((BakeryReviewEvents.Submitted, owner, (string?)null, Now), (submitted.EventType, submitted.ActorUserId, submitted.Reason, submitted.CreatedAt));
        // Chưa có kỳ sử dụng trước khi duyệt.
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM bakery_subscription_periods WHERE bakery_id = @b", ("@b", bakery)));
    }

    // ---------------- Admin review và trial ----------------

    [MySqlFact]
    public async Task Approve_activates_and_starts_one_thirty_day_trial_with_selected_plan()
    {
        var admin = await UserAsync(UniqueEmail("admin"), role: "admin");
        var (owner, bakery, _) = await BakeryAsync();
        var plan = await SubmittedAsync(owner, bakery);
        var approvedAt = Now.AddHours(2);

        Assert.Equal(ReviewOutcome.Succeeded,
            await Store.ReviewAsync(admin, bakery, BakeryReviewEvents.Approved, null, approvedAt, SubscriptionRules.TrialLength, Ct));

        Assert.Equal("active", await ScalarAsync("SELECT profile_status FROM bakeries WHERE id = @b", ("@b", bakery)));
        Assert.Equal($"trial|{plan}|{approvedAt:yyyy-MM-dd HH:mm:ss.fff}|{approvedAt.AddDays(30):yyyy-MM-dd HH:mm:ss.fff}", await ScalarAsync(
            "SELECT CONCAT(period_kind, '|', plan_id, '|', starts_at, '|', ends_at) FROM bakery_subscription_periods WHERE bakery_id = @b", ("@b", bakery)));
        var decision = (await Store.ListReviewEventsAsync(bakery, Ct))![^1];
        Assert.Equal((BakeryReviewEvents.Approved, admin, approvedAt), (decision.EventType, decision.ActorUserId, decision.CreatedAt));
        Assert.Equal(BakeryReviewEvents.Approved, (await Store.FindProfileAsync(owner, bakery, Ct))!.LastDecision!.EventType);

        // Duyệt lặp (hồ sơ được đưa lại pending_review bằng tay) không cấp thêm trial, không dời mốc cũ.
        await database.AdminExecuteAsync("UPDATE bakeries SET profile_status = 'pending_review' WHERE id = @b", ("@b", bakery));
        Assert.Equal(ReviewOutcome.Succeeded,
            await Store.ReviewAsync(admin, bakery, BakeryReviewEvents.Approved, null, approvedAt.AddDays(10), SubscriptionRules.TrialLength, Ct));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM bakery_subscription_periods WHERE bakery_id = @b", ("@b", bakery)));
        Assert.Equal(approvedAt.AddDays(30), (DateTime)(await ScalarAsync("SELECT ends_at FROM bakery_subscription_periods WHERE bakery_id = @b", ("@b", bakery)))!);
    }

    [MySqlFact]
    public async Task Concurrent_reviews_have_exactly_one_transition_and_one_trial()
    {
        var admin = await UserAsync(UniqueEmail("admin"), role: "admin");
        var (owner, bakery, _) = await BakeryAsync();
        await SubmittedAsync(owner, bakery);
        var decisions = new[] { BakeryReviewEvents.Approved, BakeryReviewEvents.Rejected, BakeryReviewEvents.ChangesRequested };

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() =>
            Store.ReviewAsync(admin, bakery, decisions[i % 3], decisions[i % 3] == BakeryReviewEvents.Approved ? null : "Lý do",
                Now, SubscriptionRules.TrialLength, Ct))));

        Assert.Equal(1, outcomes.Count(o => o == ReviewOutcome.Succeeded));
        Assert.Equal(11, outcomes.Count(o => o == ReviewOutcome.NotPendingReview));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM bakery_review_events WHERE bakery_id = @b AND event_type <> 'submitted'", ("@b", bakery)));
        var status = (string)(await ScalarAsync("SELECT profile_status FROM bakeries WHERE id = @b", ("@b", bakery)))!;
        Assert.Equal(status == "active" ? 1 : 0, await CountAsync("SELECT COUNT(*) FROM bakery_subscription_periods WHERE bakery_id = @b", ("@b", bakery)));
    }

    [MySqlFact]
    public async Task Request_changes_returns_to_draft_with_reason_and_reject_is_final_without_touching_accounts()
    {
        var admin = await UserAsync(UniqueEmail("admin"), role: "admin");
        var (owner, bakery, staff) = await BakeryAsync();
        await SubmittedAsync(owner, bakery);

        Assert.Equal(ReviewOutcome.Succeeded, await Store.ReviewAsync(
            admin, bakery, BakeryReviewEvents.ChangesRequested, "Bổ sung địa chỉ", Now, SubscriptionRules.TrialLength, Ct));
        var profile = (await Store.FindProfileAsync(owner, bakery, Ct))!;
        Assert.Equal(("draft", BakeryReviewEvents.ChangesRequested, "Bổ sung địa chỉ"),
            (profile.ProfileStatus, profile.LastDecision!.EventType, profile.LastDecision.Reason));
        Assert.Equal(OwnerActionOutcome.Succeeded, await Store.UpdateDraftProfileAsync(owner, bakery,
            new BakeryProfileValues("Tiệm", null, "2 Lê Lợi", "Huế", "0901"), Now, Ct));
        Assert.Equal(SubmitProfileStatus.Submitted, (await Store.SubmitAsync(owner, bakery, Now, Ct)).Status);

        Assert.Equal(ReviewOutcome.Succeeded, await Store.ReviewAsync(
            admin, bakery, BakeryReviewEvents.Rejected, "Không đủ điều kiện", Now, SubscriptionRules.TrialLength, Ct));
        Assert.Equal("rejected", await ScalarAsync("SELECT profile_status FROM bakeries WHERE id = @b", ("@b", bakery)));
        Assert.Equal(SubmitProfileStatus.NotDraft, (await Store.SubmitAsync(owner, bakery, Now, Ct)).Status);
        Assert.Equal(OwnerActionOutcome.NotAllowedInStatus, await Store.UpdateDraftProfileAsync(owner, bakery,
            new BakeryProfileValues("Tiệm", null, "2 Lê Lợi", "Huế", "0901"), Now, Ct));
        Assert.Equal(OwnerActionOutcome.NotAllowedInStatus, await Store.SelectPlanAsync(owner, bakery, await PlanAsync(), Now, Ct));
        Assert.Equal(ReviewOutcome.NotPendingReview, await Store.ReviewAsync(
            admin, bakery, BakeryReviewEvents.Approved, null, Now, SubscriptionRules.TrialLength, Ct));
        // Không khóa tài khoản, không gỡ membership, không cấp trial.
        Assert.Equal(2, await CountAsync(
            "SELECT COUNT(*) FROM bakery_members bm JOIN users u ON u.id = bm.user_id WHERE bm.bakery_id = @b AND bm.status = 'active' AND u.status = 'active'",
            ("@b", bakery)));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM bakery_subscription_periods WHERE bakery_id = @b", ("@b", bakery)));
        Assert.NotNull(await Store.FindProfileAsync(staff, bakery, Ct));
        Assert.Equal(
            [BakeryReviewEvents.Submitted, BakeryReviewEvents.ChangesRequested, BakeryReviewEvents.Submitted, BakeryReviewEvents.Rejected],
            (await Store.ListReviewEventsAsync(bakery, Ct))!.Select(e => e.EventType));
    }

    [MySqlFact]
    public async Task Review_requires_pending_profile_and_selected_plan()
    {
        var admin = await UserAsync(UniqueEmail("admin"), role: "admin");
        var (_, bakery, _) = await BakeryAsync();

        Assert.Equal(ReviewOutcome.NotPendingReview, await Store.ReviewAsync(admin, bakery, BakeryReviewEvents.Approved, null, Now, SubscriptionRules.TrialLength, Ct));
        Assert.Equal(ReviewOutcome.BakeryNotFound, await Store.ReviewAsync(admin, long.MaxValue, BakeryReviewEvents.Approved, null, Now, SubscriptionRules.TrialLength, Ct));
        Assert.Null(await Store.ListReviewEventsAsync(long.MaxValue, Ct));

        await database.AdminExecuteAsync("UPDATE bakeries SET profile_status = 'pending_review' WHERE id = @b", ("@b", bakery));
        Assert.Equal(ReviewOutcome.PlanNotSelected, await Store.ReviewAsync(admin, bakery, BakeryReviewEvents.Approved, null, Now, SubscriptionRules.TrialLength, Ct));
        Assert.Equal("pending_review", await ScalarAsync("SELECT profile_status FROM bakeries WHERE id = @b", ("@b", bakery)));
    }

    [MySqlFact]
    public async Task Database_constraints_back_up_trial_uniqueness_reason_and_rejected_status()
    {
        var admin = await UserAsync(UniqueEmail("admin"), role: "admin");
        var (owner, bakery, _) = await BakeryAsync();
        await SubmittedAsync(owner, bakery);
        await Store.ReviewAsync(admin, bakery, BakeryReviewEvents.Approved, null, Now, SubscriptionRules.TrialLength, Ct);
        var plan = Convert.ToInt64(await ScalarAsync("SELECT plan_id FROM bakery_subscriptions WHERE bakery_id = @b", ("@b", bakery)));

        var duplicateTrial = await Assert.ThrowsAsync<MySqlException>(() => database.AdminExecuteAsync(
            "INSERT INTO bakery_subscription_periods (bakery_id, plan_id, period_kind, starts_at, ends_at) VALUES (@b, @p, 'trial', @s, @e)",
            ("@b", bakery), ("@p", plan), ("@s", Now.AddYears(1)), ("@e", Now.AddYears(2))));
        Assert.Equal(MySqlErrorCode.DuplicateKeyEntry, duplicateTrial.ErrorCode);

        var missingReason = await Assert.ThrowsAsync<MySqlException>(() => database.AdminExecuteAsync(
            "INSERT INTO bakery_review_events (bakery_id, event_type, actor_user_id, reason) VALUES (@b, 'rejected', @a, '  ')",
            ("@b", bakery), ("@a", admin)));
        Assert.Equal(3819, missingReason.Number); // ER_CHECK_CONSTRAINT_VIOLATED

        await database.AdminExecuteAsync("UPDATE bakeries SET profile_status = 'rejected' WHERE id = @b", ("@b", bakery));
        await Assert.ThrowsAsync<MySqlException>(() => database.AdminExecuteAsync(
            "UPDATE bakeries SET profile_status = 'expired' WHERE id = @b", ("@b", bakery)));
        Assert.Equal(1L, Convert.ToInt64(await ScalarAsync("SELECT COUNT(*) FROM schema_migrations WHERE version = 4")));
    }

    // ---------------- Gói đã chọn ----------------

    [MySqlFact]
    public async Task Plan_cannot_change_while_access_period_is_running_but_can_after_expiry()
    {
        var admin = await UserAsync(UniqueEmail("admin"), role: "admin");
        var (owner, bakery, _) = await BakeryAsync();
        var plan = await SubmittedAsync(owner, bakery);
        var other = await PlanAsync();
        // Trước duyệt: đổi tự do.
        Assert.Equal(OwnerActionOutcome.Succeeded, await Store.SelectPlanAsync(owner, bakery, other, Now, Ct));
        Assert.Equal(OwnerActionOutcome.Succeeded, await Store.SelectPlanAsync(owner, bakery, plan, Now, Ct));
        await Store.ReviewAsync(admin, bakery, BakeryReviewEvents.Approved, null, Now, SubscriptionRules.TrialLength, Ct);

        Assert.Equal(OwnerActionOutcome.Succeeded, await Store.SelectPlanAsync(owner, bakery, plan, Now.AddDays(1), Ct));
        Assert.Equal(OwnerActionOutcome.PlanChangeNotAllowed, await Store.SelectPlanAsync(owner, bakery, other, Now.AddDays(29), Ct));
        // Đúng mốc hết hạn: kỳ [starts_at, ends_at) đã kết thúc.
        Assert.Equal(OwnerActionOutcome.Succeeded, await Store.SelectPlanAsync(owner, bakery, other, Now + SubscriptionRules.TrialLength, Ct));
        Assert.Equal(other, (await Store.FindProfileAsync(owner, bakery, Ct))!.SelectedPlan!.PlanId);
    }

    // ---------------- Thành viên và lời mời ----------------

    [MySqlFact]
    public async Task Owner_removes_staff_which_immediately_loses_workspace_access_but_owner_cannot_be_removed()
    {
        var (owner, bakery, staff) = await BakeryAsync();
        var (otherOwner, otherBakery, _) = await BakeryAsync();
        var workspace = new MySqlBakeryWorkspaceStore(database.Factory);
        Assert.True(await workspace.IsActiveMemberAsync(staff, bakery, Ct));

        Assert.Equal(RemoveMemberOutcome.OwnerRequired, await Store.RemoveStaffAsync(staff, bakery, owner, Now, Ct));
        Assert.Equal(RemoveMemberOutcome.BakeryNotFound, await Store.RemoveStaffAsync(otherOwner, bakery, staff, Now, Ct));
        Assert.Equal(RemoveMemberOutcome.MemberNotFound, await Store.RemoveStaffAsync(owner, bakery, otherOwner, Now, Ct));
        Assert.Equal(RemoveMemberOutcome.CannotRemoveOwner, await Store.RemoveStaffAsync(owner, bakery, owner, Now, Ct));
        Assert.Equal(RemoveMemberOutcome.Removed, await Store.RemoveStaffAsync(owner, bakery, staff, Now, Ct));
        Assert.Equal(RemoveMemberOutcome.MemberNotFound, await Store.RemoveStaffAsync(owner, bakery, staff, Now, Ct));

        Assert.False(await workspace.IsActiveMemberAsync(staff, bakery, Ct));
        Assert.Equal("removed", await ScalarAsync("SELECT status FROM bakery_members WHERE bakery_id = @b AND user_id = @u", ("@b", bakery), ("@u", staff)));
        Assert.Equal("active", await ScalarAsync("SELECT status FROM users WHERE id = @u", ("@u", staff)));
        var (outcome, members) = await Store.ListMembersAsync(owner, bakery, Ct);
        Assert.Equal(OwnerActionOutcome.Succeeded, outcome);
        Assert.Equal([("owner", "active"), ("staff", "removed")], members.Select(m => (m.MemberRole, m.Status)));
        // Staff đã bị gỡ không còn là thành viên; owner tiệm khác không đọc được.
        Assert.Equal(OwnerActionOutcome.BakeryNotFound, (await Store.ListMembersAsync(staff, bakery, Ct)).Outcome);
        Assert.Equal(OwnerActionOutcome.BakeryNotFound, (await Store.ListMembersAsync(owner, otherBakery, Ct)).Outcome);
        Assert.Equal(OwnerActionOutcome.BakeryNotFound, (await Store.ListMembersAsync(otherOwner, bakery, Ct)).Outcome);
    }

    private async Task<(string Token, byte[] Hash, long Id)> InviteAsync(long owner, long bakery, string email, DateTime? at = null)
    {
        var (token, hash) = AccountTokens.Create();
        var created = at ?? Now;
        var result = await Store.CreateInvitationAsync(new NewBakeryInvitation(owner, bakery, email, hash, created, created.AddDays(7)), Ct);
        Assert.Equal(CreateInvitationStatus.Created, result.Status);
        return (token, hash, result.Invitation!.Id);
    }

    [MySqlFact]
    public async Task Invitations_are_owner_only_replace_previous_ones_and_reject_existing_members()
    {
        var (owner, bakery, staff) = await BakeryAsync("Tiệm Mời");
        var staffEmail = (string)(await ScalarAsync("SELECT email FROM users WHERE id = @u", ("@u", staff)))!;
        var email = UniqueEmail("moi");

        Assert.Equal(CreateInvitationStatus.OwnerRequired, (await Store.CreateInvitationAsync(
            new NewBakeryInvitation(staff, bakery, email, AccountTokens.Create().Hash, Now, Now.AddDays(7)), Ct)).Status);
        Assert.Equal(CreateInvitationStatus.AlreadyMember, (await Store.CreateInvitationAsync(
            new NewBakeryInvitation(owner, bakery, staffEmail.ToUpperInvariant(), AccountTokens.Create().Hash, Now, Now.AddDays(7)), Ct)).Status);

        var first = await InviteAsync(owner, bakery, email);
        var created = await Store.CreateInvitationAsync(
            new NewBakeryInvitation(owner, bakery, email, AccountTokens.Create().Hash, Now.AddMinutes(1), Now.AddDays(7)), Ct);
        Assert.Equal("Tiệm Mời", created.BakeryName);

        var (_, pending) = await Store.ListInvitationsAsync(owner, bakery, Now.AddMinutes(2), Ct);
        Assert.Equal([created.Invitation!.Id], pending.Select(i => i.Id));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM bakery_invitations WHERE id = @i AND revoked_at IS NOT NULL", ("@i", first.Id)));
        Assert.Empty((await Store.ListInvitationsAsync(owner, bakery, Now.AddDays(8), Ct)).Invitations);

        Assert.Equal(RevokeInvitationOutcome.OwnerRequired, await Store.RevokeInvitationAsync(staff, bakery, created.Invitation.Id, Now, Ct));
        Assert.Equal(RevokeInvitationOutcome.Revoked, await Store.RevokeInvitationAsync(owner, bakery, created.Invitation.Id, Now, Ct));
        Assert.Equal(RevokeInvitationOutcome.InvitationNotFound, await Store.RevokeInvitationAsync(owner, bakery, created.Invitation.Id, Now, Ct));
    }

    [MySqlFact]
    public async Task Existing_bakery_account_with_matching_email_accepts_once_and_gets_verified_staff_membership()
    {
        var (owner, bakery, _) = await BakeryAsync();
        var email = UniqueEmail("nv");
        var invitee = await UserAsync(email);
        var wrongEmail = await UserAsync(UniqueEmail("khac"));
        var invite = await InviteAsync(owner, bakery, email);

        Assert.Equal(AcceptInvitationStatus.InvalidToken, (await Store.AcceptInvitationAsync(wrongEmail, invite.Hash, Now, Ct)).Status);
        Assert.Equal(AcceptInvitationStatus.InvalidToken, (await Store.AcceptInvitationAsync(invitee, invite.Hash, Now.AddDays(7), Ct)).Status);

        var accepted = await Store.AcceptInvitationAsync(invitee, invite.Hash, Now, Ct);
        Assert.Equal((AcceptInvitationStatus.Accepted, bakery, invitee), (accepted.Status, accepted.BakeryId, accepted.UserId));
        Assert.Equal("staff|active", await ScalarAsync(
            "SELECT CONCAT(member_role, '|', status) FROM bakery_members WHERE bakery_id = @b AND user_id = @u", ("@b", bakery), ("@u", invitee)));
        Assert.NotNull(await ScalarAsync("SELECT email_verified_at FROM users WHERE id = @u", ("@u", invitee)));
        Assert.Equal(AcceptInvitationStatus.InvalidToken, (await Store.AcceptInvitationAsync(invitee, invite.Hash, Now, Ct)).Status);
    }

    [MySqlFact]
    public async Task Customer_or_admin_with_invited_email_is_not_upgraded_and_invitation_stays_open()
    {
        var (owner, bakery, _) = await BakeryAsync();
        foreach (var role in new[] { "customer", "admin" })
        {
            var email = UniqueEmail(role);
            var user = await UserAsync(email, role: role);
            var invite = await InviteAsync(owner, bakery, email);

            Assert.Equal(AcceptInvitationStatus.RoleNotAllowed, (await Store.AcceptInvitationAsync(user, invite.Hash, Now, Ct)).Status);
            Assert.Equal(role, await ScalarAsync("SELECT system_role FROM users WHERE id = @u", ("@u", user)));
            Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM bakery_members WHERE user_id = @u", ("@u", user)));
            Assert.Equal(AcceptInvitationStatus.AccountExists, (await Store.AcceptInvitationWithNewAccountAsync(
                invite.Hash, new NewInvitedUser("H", "X", null), Now, Ct)).Status);
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM bakery_invitations WHERE id = @i AND accepted_at IS NULL", ("@i", invite.Id)));
        }
    }

    [MySqlFact]
    public async Task Removed_staff_is_reactivated_by_a_new_invitation()
    {
        var (owner, bakery, staff) = await BakeryAsync();
        var email = (string)(await ScalarAsync("SELECT email FROM users WHERE id = @u", ("@u", staff)))!;
        await Store.RemoveStaffAsync(owner, bakery, staff, Now, Ct);
        var invite = await InviteAsync(owner, bakery, email);

        Assert.Equal(AcceptInvitationStatus.Accepted, (await Store.AcceptInvitationAsync(staff, invite.Hash, Now, Ct)).Status);
        Assert.Equal("staff|active", await ScalarAsync(
            "SELECT CONCAT(member_role, '|', status) FROM bakery_members WHERE bakery_id = @b AND user_id = @u", ("@b", bakery), ("@u", staff)));
    }

    [MySqlFact]
    public async Task New_account_acceptance_is_atomic_and_concurrent_uses_succeed_once()
    {
        var (owner, bakery, _) = await BakeryAsync();
        var email = UniqueEmail("moi");
        var invite = await InviteAsync(owner, bakery, email);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            Store.AcceptInvitationWithNewAccountAsync(invite.Hash, new NewInvitedUser($"HASH-{i}", "Nhân viên", null), Now, Ct))));

        var winner = Assert.Single(results, r => r.Status == AcceptInvitationStatus.Accepted);
        Assert.Equal(email, winner.Email);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM users WHERE email = @e", ("@e", email)));
        Assert.Equal("bakery|active|1", await ScalarAsync(
            "SELECT CONCAT(system_role, '|', status, '|', email_verified_at IS NOT NULL) FROM users WHERE id = @u", ("@u", winner.UserId)));
        Assert.Equal("staff", await ScalarAsync("SELECT member_role FROM bakery_members WHERE bakery_id = @b AND user_id = @u", ("@b", bakery), ("@u", winner.UserId)));
    }

    [MySqlFact]
    public async Task Backend_account_cannot_rewrite_history_or_plan_catalog()
    {
        await using var connection = await database.Factory.OpenConnectionAsync(Ct);
        foreach (var sql in new[]
                 {
                     "UPDATE bakery_review_events SET reason = 'x' WHERE id = -1",
                     "UPDATE bakery_subscription_periods SET ends_at = ends_at WHERE id = -1",
                     "DELETE FROM bakery_invitations WHERE id = -1",
                     "INSERT INTO subscription_plans (code, name) VALUES ('x', 'x')",
                 })
        {
            await using var command = new MySqlCommand(sql, connection);
            var error = await Assert.ThrowsAsync<MySqlException>(() => command.ExecuteNonQueryAsync(Ct));
            Assert.Contains(error.ErrorCode, new[] { MySqlErrorCode.TableAccessDenied, MySqlErrorCode.ColumnAccessDenied });
        }
    }
}
