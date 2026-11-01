using System.Security.Cryptography;
using DeCakes.Application.Accounts;
using DeCakes.Application.BakeryOnboarding;
using DeCakes.Domain.BakeryOnboarding;
using DeCakes.Domain.BakeryWorkspace;
using DeCakes.Infrastructure.Persistence;
using MySqlConnector;

namespace DeCakes.Infrastructure.BakeryOnboarding;

/// <summary>
/// Onboarding tiệm (migrations/V004__bakery_onboarding.sql). Thao tác nhiều câu lệnh dùng transaction của ứng dụng (không
/// gọi 6 procedure nghiệp vụ). Quyết định luôn dựa trên locking read trong transaction. Thứ tự khóa: bakeries (FOR UPDATE)
/// rồi bakery_members/users; lời mời: bakery_invitations rồi users rồi bakery_members. Tư cách thành viên được kiểm tra
/// trong SQL như sp_guard_active_bakery_member (user active, role bakery, membership active).
/// </summary>
public sealed class MySqlBakeryOnboardingStore(IMySqlConnectionFactory connectionFactory) : IBakeryOnboardingStore
{
    private const int SlugAttempts = 5;

    // Vai trò của user trong tiệm nếu là thành viên active hợp lệ; @bakeryId, @userId luôn là tham số.
    private const string MemberRoleSql =
        """
        SELECT bm.member_role
          FROM bakery_members bm
          JOIN users u ON u.id = bm.user_id
         WHERE bm.bakery_id = @bakeryId AND bm.user_id = @userId AND bm.status = 'active'
           AND u.status = 'active' AND u.system_role = 'bakery'
        """;

    public async Task<(long UserId, long BakeryId)> CreateOwnerAsync(NewBakeryOwner owner, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        long userId;
        await using (var user = Command(connection, transaction,
                         """
                         INSERT INTO users (email, password_hash, full_name, phone, system_role, status, is_demo)
                         VALUES (@email, @hash, @fullName, @phone, 'bakery', 'active', 0)
                         """,
                         ("@email", owner.Email), ("@hash", owner.PasswordHash), ("@fullName", owner.FullName), ("@phone", owner.Phone)))
        {
            try
            {
                await user.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
            {
                // UNIQUE uq_users_email; không đổi role của tài khoản đang có.
                throw new DuplicateEmailException(ex);
            }

            userId = user.LastInsertedId;
        }

        var bakeryId = 0L;
        for (var attempt = 1; bakeryId == 0; attempt++)
        {
            var slug = $"{owner.SlugBase}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";
            await using var bakery = Command(connection, transaction,
                "INSERT INTO bakeries (name, slug, profile_status, is_demo) VALUES (@name, @slug, 'draft', 0)",
                ("@name", owner.BakeryName), ("@slug", slug));
            try
            {
                await bakery.ExecuteNonQueryAsync(cancellationToken);
                bakeryId = bakery.LastInsertedId;
            }
            catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry && attempt < SlugAttempts)
            {
                // Trùng uq_bakeries_slug: InnoDB chỉ hoàn tác câu lệnh này, transaction vẫn tiếp tục với slug khác.
            }
        }

        await using (var member = Command(connection, transaction,
                         "INSERT INTO bakery_members (bakery_id, user_id, member_role, status) VALUES (@bakeryId, @userId, 'owner', 'active')",
                         ("@bakeryId", bakeryId), ("@userId", userId)))
        {
            await member.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (userId, bakeryId);
    }

    public async Task<BakeryProfileRecord?> FindProfileAsync(long userId, long bakeryId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null,
            """
            SELECT b.id, b.name, b.slug, b.description, b.address_line, b.city, b.phone, b.profile_status, bm.member_role,
                   sp.id AS plan_id, sp.code AS plan_code, sp.name AS plan_name, sp.status AS plan_status,
                   e.event_type, e.reason, e.created_at AS event_at
              FROM bakeries b
              JOIN bakery_members bm ON bm.bakery_id = b.id AND bm.user_id = @userId AND bm.status = 'active'
              JOIN users u ON u.id = bm.user_id AND u.status = 'active' AND u.system_role = 'bakery'
              LEFT JOIN bakery_subscriptions bs ON bs.bakery_id = b.id
              LEFT JOIN subscription_plans sp ON sp.id = bs.plan_id
              LEFT JOIN bakery_review_events e ON e.id = (SELECT MAX(x.id) FROM bakery_review_events x
                                                          WHERE x.bakery_id = b.id AND x.event_type <> 'submitted')
             WHERE b.id = @bakeryId
            """,
            ("@userId", userId), ("@bakeryId", bakeryId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var plan = reader.IsDBNull(reader.GetOrdinal("plan_id"))
            ? null
            : new SelectedPlanDto(reader.GetInt64("plan_id"), reader.GetString("plan_code"), reader.GetString("plan_name"));
        var decision = reader.IsDBNull(reader.GetOrdinal("event_type"))
            ? null
            : new ReviewEventDto(reader.GetString("event_type"), NullableString(reader, "reason"), reader.GetDateTime("event_at"));
        return new BakeryProfileRecord(
            reader.GetInt64("id"), reader.GetString("name"), reader.GetString("slug"), NullableString(reader, "description"),
            NullableString(reader, "address_line"), NullableString(reader, "city"), NullableString(reader, "phone"),
            reader.GetString("profile_status"), reader.GetString("member_role"), plan,
            plan is not null && reader.GetString("plan_status") == "active", decision);
    }

    public async Task<OwnerActionOutcome> UpdateDraftProfileAsync(
        long userId, long bakeryId, BakeryProfileValues values, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var (status, role) = await LockBakeryForMemberAsync(connection, transaction, userId, bakeryId, cancellationToken);
        var denied = OwnerDenied(status, role);
        if (denied is not null) return denied.Value;
        if (status != BakeryProfileStatuses.Draft) return OwnerActionOutcome.NotAllowedInStatus;

        await using (var update = Command(connection, transaction,
                         """
                         UPDATE bakeries
                            SET name = @name, description = @description, address_line = @addressLine, city = @city,
                                phone = @phone, updated_at = @now
                          WHERE id = @bakeryId
                         """,
                         ("@name", values.Name), ("@description", values.Description), ("@addressLine", values.AddressLine),
                         ("@city", values.City), ("@phone", values.Phone), ("@now", now), ("@bakeryId", bakeryId)))
        {
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return OwnerActionOutcome.Succeeded;
    }

    public async Task<SubmitProfileResult> SubmitAsync(long userId, long bakeryId, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        string? name = null, address = null, city = null, phone = null, status = null;
        await using (var lockBakery = Command(connection, transaction,
                         "SELECT name, address_line, city, phone, profile_status FROM bakeries WHERE id = @bakeryId FOR UPDATE",
                         ("@bakeryId", bakeryId)))
        await using (var reader = await lockBakery.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                name = reader.GetString("name");
                address = NullableString(reader, "address_line");
                city = NullableString(reader, "city");
                phone = NullableString(reader, "phone");
                status = reader.GetString("profile_status");
            }
        }

        var role = status is null ? null : await MemberRoleAsync(connection, transaction, userId, bakeryId, cancellationToken);
        if (role is null) return new SubmitProfileResult(SubmitProfileStatus.BakeryNotFound, []);
        if (role != BakeryMemberRoles.Owner) return new SubmitProfileResult(SubmitProfileStatus.OwnerRequired, []);
        if (status != BakeryProfileStatuses.Draft) return new SubmitProfileResult(SubmitProfileStatus.NotDraft, []);

        bool planOpen;
        await using (var plan = Command(connection, transaction,
                         """
                         SELECT EXISTS (SELECT 1 FROM bakery_subscriptions bs JOIN subscription_plans sp ON sp.id = bs.plan_id
                                         WHERE bs.bakery_id = @bakeryId AND sp.status = 'active')
                         """,
                         ("@bakeryId", bakeryId)))
        {
            planOpen = Convert.ToInt64(await plan.ExecuteScalarAsync(cancellationToken)) == 1;
        }

        var missing = BakeryProfileRules.MissingForSubmit(name, address, city, phone, planOpen);
        if (missing.Count > 0) return new SubmitProfileResult(SubmitProfileStatus.Incomplete, missing);

        await SetProfileStatusAsync(connection, transaction, bakeryId, BakeryProfileStatuses.PendingReview, now, cancellationToken);
        await AddReviewEventAsync(connection, transaction, bakeryId, BakeryReviewEvents.Submitted, userId, null, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SubmitProfileResult(SubmitProfileStatus.Submitted, []);
    }

    public async Task<IReadOnlyList<ReviewEventDto>?> ListReviewEventsForMemberAsync(
        long userId, long bakeryId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        if (await MemberRoleAsync(connection, null, userId, bakeryId, cancellationToken) is null)
        {
            return null;
        }

        return (await ReadEventsAsync(connection, bakeryId, cancellationToken))
            .Select(e => new ReviewEventDto(e.EventType, e.Reason, e.CreatedAt)).ToList();
    }

    public async Task<IReadOnlyList<SubscriptionPlanDto>> ListActivePlansAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null,
            "SELECT id, code, name, monthly_price, yearly_price, currency FROM subscription_plans WHERE status = 'active' ORDER BY id");
        var plans = new List<SubscriptionPlanDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            plans.Add(new SubscriptionPlanDto(
                reader.GetInt64("id"), reader.GetString("code"), reader.GetString("name"),
                NullableDecimal(reader, "monthly_price"), NullableDecimal(reader, "yearly_price"), reader.GetString("currency")));
        }

        return plans;
    }

    public async Task<OwnerActionOutcome> SelectPlanAsync(
        long userId, long bakeryId, long planId, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var (status, role) = await LockBakeryForMemberAsync(connection, transaction, userId, bakeryId, cancellationToken);
        var denied = OwnerDenied(status, role);
        if (denied is not null) return denied.Value;
        if (status == BakeryProfileStatuses.Rejected) return OwnerActionOutcome.NotAllowedInStatus;

        await using (var plan = Command(connection, transaction,
                         "SELECT EXISTS (SELECT 1 FROM subscription_plans WHERE id = @planId AND status = 'active')",
                         ("@planId", planId)))
        {
            if (Convert.ToInt64(await plan.ExecuteScalarAsync(cancellationToken)) != 1) return OwnerActionOutcome.PlanNotAvailable;
        }

        long? currentPlan;
        await using (var current = Command(connection, transaction,
                         "SELECT plan_id FROM bakery_subscriptions WHERE bakery_id = @bakeryId FOR UPDATE", ("@bakeryId", bakeryId)))
        {
            currentPlan = await current.ExecuteScalarAsync(cancellationToken) is { } value and not DBNull ? Convert.ToInt64(value) : null;
        }

        if (currentPlan == planId) return OwnerActionOutcome.Succeeded;
        if (currentPlan is not null && await HasAccessAtAsync(connection, transaction, bakeryId, now, cancellationToken))
        {
            // Nâng/hạ gói khi còn kỳ sử dụng chưa được hỗ trợ.
            return OwnerActionOutcome.PlanChangeNotAllowed;
        }

        await using (var upsert = Command(connection, transaction,
                         """
                         INSERT INTO bakery_subscriptions (bakery_id, plan_id, selected_by_user_id, created_at, updated_at)
                         VALUES (@bakeryId, @planId, @userId, @now, @now) AS chosen
                         ON DUPLICATE KEY UPDATE plan_id = chosen.plan_id, selected_by_user_id = chosen.selected_by_user_id,
                                                 updated_at = chosen.updated_at
                         """,
                         ("@bakeryId", bakeryId), ("@planId", planId), ("@userId", userId), ("@now", now)))
        {
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return OwnerActionOutcome.Succeeded;
    }

    public async Task<ReviewOutcome> ReviewAsync(
        long adminUserId, long bakeryId, string eventType, string? reason, DateTime now, TimeSpan trialLength,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        string? status;
        await using (var lockBakery = Command(connection, transaction,
                         "SELECT profile_status FROM bakeries WHERE id = @bakeryId FOR UPDATE", ("@bakeryId", bakeryId)))
        {
            status = await lockBakery.ExecuteScalarAsync(cancellationToken) as string;
        }

        if (status is null) return ReviewOutcome.BakeryNotFound;
        // Chỉ hồ sơ đang chờ duyệt; lần xử lý thứ hai (kể cả đồng thời, sau khi chờ khóa) thấy trạng thái đã đổi.
        if (status != BakeryProfileStatuses.PendingReview) return ReviewOutcome.NotPendingReview;

        string newStatus;
        switch (eventType)
        {
            case BakeryReviewEvents.Approved:
                long? planId;
                await using (var plan = Command(connection, transaction,
                                 "SELECT plan_id FROM bakery_subscriptions WHERE bakery_id = @bakeryId FOR SHARE", ("@bakeryId", bakeryId)))
                {
                    planId = await plan.ExecuteScalarAsync(cancellationToken) is { } value and not DBNull ? Convert.ToInt64(value) : null;
                }

                if (planId is null) return ReviewOutcome.PlanNotSelected;

                // Trial một lần cho mỗi tiệm: duyệt lại không cấp thêm. UNIQUE uq_bakery_subscription_periods_one_trial chặn
                // cả trường hợp kiểm tra này bị vượt qua.
                bool hadTrial;
                await using (var trial = Command(connection, transaction,
                                 "SELECT EXISTS (SELECT 1 FROM bakery_subscription_periods WHERE bakery_id = @bakeryId AND period_kind = 'trial')",
                                 ("@bakeryId", bakeryId)))
                {
                    hadTrial = Convert.ToInt64(await trial.ExecuteScalarAsync(cancellationToken)) == 1;
                }

                if (!hadTrial)
                {
                    await using var insert = Command(connection, transaction,
                        """
                        INSERT INTO bakery_subscription_periods (bakery_id, plan_id, period_kind, starts_at, ends_at, created_at)
                        VALUES (@bakeryId, @planId, 'trial', @startsAt, @endsAt, @startsAt)
                        """,
                        ("@bakeryId", bakeryId), ("@planId", planId), ("@startsAt", now), ("@endsAt", now + trialLength));
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }

                newStatus = BakeryProfileStatuses.Active;
                break;
            case BakeryReviewEvents.ChangesRequested:
                newStatus = BakeryProfileStatuses.Draft;
                break;
            case BakeryReviewEvents.Rejected:
                newStatus = BakeryProfileStatuses.Rejected;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(eventType), eventType, "Quyết định review không hợp lệ.");
        }

        await SetProfileStatusAsync(connection, transaction, bakeryId, newStatus, now, cancellationToken);
        await AddReviewEventAsync(connection, transaction, bakeryId, eventType, adminUserId, reason, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ReviewOutcome.Succeeded;
    }

    public async Task<IReadOnlyList<AdminReviewEventDto>?> ListReviewEventsAsync(long bakeryId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using (var exists = Command(connection, null, "SELECT EXISTS (SELECT 1 FROM bakeries WHERE id = @bakeryId)", ("@bakeryId", bakeryId)))
        {
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken)) != 1) return null;
        }

        return await ReadEventsAsync(connection, bakeryId, cancellationToken);
    }

    public async Task<(OwnerActionOutcome Outcome, IReadOnlyList<BakeryMemberDto> Members)> ListMembersAsync(
        long ownerUserId, long bakeryId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var denied = OwnerDenied("", await MemberRoleAsync(connection, null, ownerUserId, bakeryId, cancellationToken));
        if (denied is not null) return (denied.Value, []);

        await using var command = Command(connection, null,
            """
            SELECT u.id, u.email, u.full_name, bm.member_role, bm.status
              FROM bakery_members bm JOIN users u ON u.id = bm.user_id
             WHERE bm.bakery_id = @bakeryId
             ORDER BY bm.member_role = 'owner' DESC, bm.status = 'active' DESC, u.id
            """,
            ("@bakeryId", bakeryId));
        var members = new List<BakeryMemberDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            members.Add(new BakeryMemberDto(
                reader.GetInt64("id"), reader.GetString("email"), reader.GetString("full_name"),
                reader.GetString("member_role"), reader.GetString("status")));
        }

        return (OwnerActionOutcome.Succeeded, members);
    }

    public async Task<RemoveMemberOutcome> RemoveStaffAsync(
        long ownerUserId, long bakeryId, long memberUserId, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var role = await MemberRoleAsync(connection, transaction, ownerUserId, bakeryId, cancellationToken, lockShared: true);
        if (role is null) return RemoveMemberOutcome.BakeryNotFound;
        if (role != BakeryMemberRoles.Owner) return RemoveMemberOutcome.OwnerRequired;

        string? targetRole = null, targetStatus = null;
        await using (var target = Command(connection, transaction,
                         "SELECT member_role, status FROM bakery_members WHERE bakery_id = @bakeryId AND user_id = @memberId FOR UPDATE",
                         ("@bakeryId", bakeryId), ("@memberId", memberUserId)))
        await using (var reader = await target.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                targetRole = reader.GetString("member_role");
                targetStatus = reader.GetString("status");
            }
        }

        if (targetStatus != BakeryMemberStatuses.Active) return RemoveMemberOutcome.MemberNotFound;
        // Không gỡ owner (chuyển owner chưa chốt); không xóa dòng để giữ liên kết báo giá/đơn.
        if (targetRole == BakeryMemberRoles.Owner) return RemoveMemberOutcome.CannotRemoveOwner;

        await using (var update = Command(connection, transaction,
                         "UPDATE bakery_members SET status = 'removed', updated_at = @now WHERE bakery_id = @bakeryId AND user_id = @memberId",
                         ("@now", now), ("@bakeryId", bakeryId), ("@memberId", memberUserId)))
        {
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return RemoveMemberOutcome.Removed;
    }

    public async Task<CreateInvitationResult> CreateInvitationAsync(NewBakeryInvitation invitation, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var (status, role) = await LockBakeryForMemberAsync(
            connection, transaction, invitation.OwnerUserId, invitation.BakeryId, cancellationToken);
        if (status is null || role is null) return new CreateInvitationResult(CreateInvitationStatus.BakeryNotFound, null, null);
        if (role != BakeryMemberRoles.Owner) return new CreateInvitationResult(CreateInvitationStatus.OwnerRequired, null, null);
        if (status == BakeryProfileStatuses.Rejected) return new CreateInvitationResult(CreateInvitationStatus.NotAllowedInStatus, null, null);

        await using (var member = Command(connection, transaction,
                         """
                         SELECT EXISTS (SELECT 1 FROM bakery_members bm JOIN users u ON u.id = bm.user_id
                                         WHERE bm.bakery_id = @bakeryId AND bm.status = 'active' AND u.email = @email)
                         """,
                         ("@bakeryId", invitation.BakeryId), ("@email", invitation.Email)))
        {
            if (Convert.ToInt64(await member.ExecuteScalarAsync(cancellationToken)) == 1)
            {
                return new CreateInvitationResult(CreateInvitationStatus.AlreadyMember, null, null);
            }
        }

        await using (var revoke = Command(connection, transaction,
                         """
                         UPDATE bakery_invitations SET revoked_at = @now
                          WHERE bakery_id = @bakeryId AND email = @email AND accepted_at IS NULL AND revoked_at IS NULL
                         """,
                         ("@now", invitation.CreatedAt), ("@bakeryId", invitation.BakeryId), ("@email", invitation.Email)))
        {
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }

        long id;
        await using (var insert = Command(connection, transaction,
                         """
                         INSERT INTO bakery_invitations (bakery_id, email, token_hash, invited_by_user_id, created_at, expires_at)
                         VALUES (@bakeryId, @email, @tokenHash, @ownerId, @createdAt, @expiresAt)
                         """,
                         ("@bakeryId", invitation.BakeryId), ("@email", invitation.Email), ("@tokenHash", invitation.TokenHash),
                         ("@ownerId", invitation.OwnerUserId), ("@createdAt", invitation.CreatedAt), ("@expiresAt", invitation.ExpiresAt)))
        {
            await insert.ExecuteNonQueryAsync(cancellationToken);
            id = insert.LastInsertedId;
        }

        string bakeryName;
        await using (var name = Command(connection, transaction, "SELECT name FROM bakeries WHERE id = @bakeryId", ("@bakeryId", invitation.BakeryId)))
        {
            bakeryName = (string)(await name.ExecuteScalarAsync(cancellationToken))!;
        }

        await transaction.CommitAsync(cancellationToken);
        return new CreateInvitationResult(
            CreateInvitationStatus.Created,
            new BakeryInvitationDto(id, invitation.Email, invitation.CreatedAt, invitation.ExpiresAt),
            bakeryName);
    }

    public async Task<(OwnerActionOutcome Outcome, IReadOnlyList<BakeryInvitationDto> Invitations)> ListInvitationsAsync(
        long ownerUserId, long bakeryId, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var denied = OwnerDenied("", await MemberRoleAsync(connection, null, ownerUserId, bakeryId, cancellationToken));
        if (denied is not null) return (denied.Value, []);

        await using var command = Command(connection, null,
            """
            SELECT id, email, created_at, expires_at FROM bakery_invitations
             WHERE bakery_id = @bakeryId AND accepted_at IS NULL AND revoked_at IS NULL AND expires_at > @now
             ORDER BY id
            """,
            ("@bakeryId", bakeryId), ("@now", now));
        var invitations = new List<BakeryInvitationDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            invitations.Add(new BakeryInvitationDto(
                reader.GetInt64("id"), reader.GetString("email"), reader.GetDateTime("created_at"), reader.GetDateTime("expires_at")));
        }

        return (OwnerActionOutcome.Succeeded, invitations);
    }

    public async Task<RevokeInvitationOutcome> RevokeInvitationAsync(
        long ownerUserId, long bakeryId, long invitationId, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var role = await MemberRoleAsync(connection, null, ownerUserId, bakeryId, cancellationToken);
        if (role is null) return RevokeInvitationOutcome.BakeryNotFound;
        if (role != BakeryMemberRoles.Owner) return RevokeInvitationOutcome.OwnerRequired;

        await using var command = Command(connection, null,
            """
            UPDATE bakery_invitations SET revoked_at = @now
             WHERE id = @id AND bakery_id = @bakeryId AND accepted_at IS NULL AND revoked_at IS NULL
            """,
            ("@now", now), ("@id", invitationId), ("@bakeryId", bakeryId));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1
            ? RevokeInvitationOutcome.Revoked
            : RevokeInvitationOutcome.InvitationNotFound;
    }

    public async Task<AcceptInvitationResult> AcceptInvitationAsync(
        long userId, byte[] tokenHash, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var invitation = await LockUsableInvitationAsync(connection, transaction, tokenHash, now, cancellationToken);
        if (invitation is null) return new AcceptInvitationResult(AcceptInvitationStatus.InvalidToken);

        string? email = null, status = null, role = null;
        await using (var user = Command(connection, transaction,
                         "SELECT email, status, system_role FROM users WHERE id = @userId FOR UPDATE", ("@userId", userId)))
        await using (var reader = await user.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                email = reader.GetString("email");
                status = reader.GetString("status");
                role = reader.GetString("system_role");
            }
        }

        // Lời mời gắn với email: user khác email không dùng được (không phân biệt với token sai).
        if (status != "active" || !string.Equals(email, invitation.Email, StringComparison.OrdinalIgnoreCase))
        {
            return new AcceptInvitationResult(AcceptInvitationStatus.InvalidToken);
        }

        // Một role/email: không nâng customer/admin thành bakery.
        if (role != "bakery") return new AcceptInvitationResult(AcceptInvitationStatus.RoleNotAllowed);

        string? memberStatus;
        await using (var member = Command(connection, transaction,
                         "SELECT status FROM bakery_members WHERE bakery_id = @bakeryId AND user_id = @userId FOR UPDATE",
                         ("@bakeryId", invitation.BakeryId), ("@userId", userId)))
        {
            memberStatus = await member.ExecuteScalarAsync(cancellationToken) as string;
        }

        var membershipSql = memberStatus switch
        {
            null => "INSERT INTO bakery_members (bakery_id, user_id, member_role, status, created_at, updated_at) VALUES (@bakeryId, @userId, 'staff', 'active', @now, @now)",
            BakeryMemberStatuses.Removed => "UPDATE bakery_members SET member_role = 'staff', status = 'active', updated_at = @now WHERE bakery_id = @bakeryId AND user_id = @userId",
            _ => null, // Đã là thành viên active: chỉ đánh dấu lời mời đã nhận.
        };
        if (membershipSql is not null)
        {
            await using var membership = Command(connection, transaction, membershipSql,
                ("@bakeryId", invitation.BakeryId), ("@userId", userId), ("@now", now));
            await membership.ExecuteNonQueryAsync(cancellationToken);
        }

        await MarkAcceptedAsync(connection, transaction, invitation.Id, userId, now, cancellationToken);
        await using (var verify = Command(connection, transaction,
                         "UPDATE users SET email_verified_at = COALESCE(email_verified_at, @now) WHERE id = @userId",
                         ("@now", now), ("@userId", userId)))
        {
            await verify.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new AcceptInvitationResult(AcceptInvitationStatus.Accepted, invitation.BakeryId, userId, email);
    }

    public async Task<AcceptInvitationResult> AcceptInvitationWithNewAccountAsync(
        byte[] tokenHash, NewInvitedUser user, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var invitation = await LockUsableInvitationAsync(connection, transaction, tokenHash, now, cancellationToken);
        if (invitation is null) return new AcceptInvitationResult(AcceptInvitationStatus.InvalidToken);

        long userId;
        await using (var insert = Command(connection, transaction,
                         """
                         INSERT INTO users (email, password_hash, email_verified_at, full_name, phone, system_role, status, is_demo)
                         VALUES (@email, @hash, @now, @fullName, @phone, 'bakery', 'active', 0)
                         """,
                         ("@email", invitation.Email), ("@hash", user.PasswordHash), ("@now", now),
                         ("@fullName", user.FullName), ("@phone", user.Phone)))
        {
            try
            {
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
            {
                return new AcceptInvitationResult(AcceptInvitationStatus.AccountExists);
            }

            userId = insert.LastInsertedId;
        }

        await using (var member = Command(connection, transaction,
                         "INSERT INTO bakery_members (bakery_id, user_id, member_role, status, created_at, updated_at) VALUES (@bakeryId, @userId, 'staff', 'active', @now, @now)",
                         ("@bakeryId", invitation.BakeryId), ("@userId", userId), ("@now", now)))
        {
            await member.ExecuteNonQueryAsync(cancellationToken);
        }

        await MarkAcceptedAsync(connection, transaction, invitation.Id, userId, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AcceptInvitationResult(AcceptInvitationStatus.Accepted, invitation.BakeryId, userId, invitation.Email);
    }

    /// <summary>Có kỳ sử dụng (trial hoặc trả phí) chứa thời điểm <paramref name="at"/>.</summary>
    internal static async Task<bool> HasAccessAtAsync(
        MySqlConnection connection, MySqlTransaction? transaction, long bakeryId, DateTime at, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            """
            SELECT EXISTS (SELECT 1 FROM bakery_subscription_periods
                            WHERE bakery_id = @bakeryId AND starts_at <= @at AND ends_at > @at)
            """,
            ("@bakeryId", bakeryId), ("@at", at));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private sealed record LockedInvitation(long Id, long BakeryId, string Email);

    private static async Task<LockedInvitation?> LockUsableInvitationAsync(
        MySqlConnection connection, MySqlTransaction transaction, byte[] tokenHash, DateTime now, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            """
            SELECT id, bakery_id, email, expires_at, accepted_at, revoked_at
              FROM bakery_invitations WHERE token_hash = @tokenHash FOR UPDATE
            """,
            ("@tokenHash", tokenHash));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var usable = reader.IsDBNull(reader.GetOrdinal("accepted_at"))
                     && reader.IsDBNull(reader.GetOrdinal("revoked_at"))
                     && reader.GetDateTime("expires_at") > now;
        return usable ? new LockedInvitation(reader.GetInt64("id"), reader.GetInt64("bakery_id"), reader.GetString("email")) : null;
    }

    private static async Task MarkAcceptedAsync(
        MySqlConnection connection, MySqlTransaction transaction, long invitationId, long userId, DateTime now,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            """
            UPDATE bakery_invitations SET accepted_at = @now, accepted_user_id = @userId
             WHERE id = @id AND accepted_at IS NULL AND revoked_at IS NULL
            """,
            ("@now", now), ("@userId", userId), ("@id", invitationId));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Lời mời không còn ở trạng thái dùng được.");
        }
    }

    /// <summary>Khóa dòng tiệm (FOR UPDATE) rồi đọc vai trò thành viên; (null, null) nếu tiệm không tồn tại.</summary>
    internal static async Task<(string? Status, string? Role)> LockBakeryForMemberAsync(
        MySqlConnection connection, MySqlTransaction transaction, long userId, long bakeryId, CancellationToken cancellationToken)
    {
        string? status;
        await using (var command = Command(connection, transaction,
                         "SELECT profile_status FROM bakeries WHERE id = @bakeryId FOR UPDATE", ("@bakeryId", bakeryId)))
        {
            status = await command.ExecuteScalarAsync(cancellationToken) as string;
        }

        return status is null
            ? (null, null)
            : (status, await MemberRoleAsync(connection, transaction, userId, bakeryId, cancellationToken, lockShared: true));
    }

    internal static async Task<string?> MemberRoleAsync(
        MySqlConnection connection, MySqlTransaction? transaction, long userId, long bakeryId, CancellationToken cancellationToken,
        bool lockShared = false)
    {
        await using var command = Command(connection, transaction,
            lockShared ? MemberRoleSql + " FOR SHARE" : MemberRoleSql, ("@bakeryId", bakeryId), ("@userId", userId));
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    /// <summary>Null khi được phép thao tác như owner.</summary>
    private static OwnerActionOutcome? OwnerDenied(string? status, string? role) =>
        status is null || role is null ? OwnerActionOutcome.BakeryNotFound
        : role != BakeryMemberRoles.Owner ? OwnerActionOutcome.OwnerRequired
        : null;

    private static async Task SetProfileStatusAsync(
        MySqlConnection connection, MySqlTransaction transaction, long bakeryId, string status, DateTime now,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            "UPDATE bakeries SET profile_status = @status, updated_at = @now WHERE id = @bakeryId",
            ("@status", status), ("@now", now), ("@bakeryId", bakeryId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AddReviewEventAsync(
        MySqlConnection connection, MySqlTransaction transaction, long bakeryId, string eventType, long actorUserId, string? reason,
        DateTime now, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            """
            INSERT INTO bakery_review_events (bakery_id, event_type, actor_user_id, reason, created_at)
            VALUES (@bakeryId, @eventType, @actorId, @reason, @now)
            """,
            ("@bakeryId", bakeryId), ("@eventType", eventType), ("@actorId", actorUserId), ("@reason", reason), ("@now", now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<AdminReviewEventDto>> ReadEventsAsync(
        MySqlConnection connection, long bakeryId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, null,
            "SELECT id, event_type, reason, actor_user_id, created_at FROM bakery_review_events WHERE bakery_id = @bakeryId ORDER BY id",
            ("@bakeryId", bakeryId));
        var events = new List<AdminReviewEventDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new AdminReviewEventDto(
                reader.GetInt64("id"), reader.GetString("event_type"), NullableString(reader, "reason"),
                reader.GetInt64("actor_user_id"), reader.GetDateTime("created_at")));
        }

        return events;
    }

    /// <summary>Lệnh có tham số; giá trị null thành DBNull. Kiểu tham số suy ra từ giá trị .NET (long, string, DateTime, byte[]).</summary>
    internal static MySqlCommand Command(
        MySqlConnection connection, MySqlTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = new MySqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static string? NullableString(MySqlDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetString(column);

    private static decimal? NullableDecimal(MySqlDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetDecimal(column);
}
