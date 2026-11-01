using System.Globalization;
using System.Security.Claims;
using DeCakes.Api.Authentication;
using DeCakes.Application.BakeryOnboarding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DeCakes.Api.Controllers;

/// <summary>
/// Hồ sơ, gói đã chọn, thành viên và lời mời của một tiệm, theo góc nhìn thành viên. Quyền theo tiệm (thành viên active,
/// owner cho thao tác quản lý) được kiểm tra trong store ở từng thao tác; bakeryId trên route chỉ là tham số tra cứu.
/// Tiệm không tồn tại và không phải thành viên dùng chung 404; staff làm thao tác của owner nhận 403 OWNER_REQUIRED.
/// </summary>
[ApiController]
[Route("api/bakery-workspace/bakeries/{bakeryId:long:min(1)}")]
[Authorize(Policy = AuthPolicies.Bakery)]
public class BakeryOnboardingController(IBakeryOnboardingService onboarding, IBakeryStaffService staff) : ControllerBase
{
    private long UserId => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);

    [HttpGet("profile")]
    [ProducesResponseType<BakeryProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProfile(long bakeryId, CancellationToken cancellationToken) =>
        await onboarding.GetProfileAsync(UserId, bakeryId, cancellationToken) is { } profile ? Ok(profile) : BakeryNotFound();

    /// <summary>Owner sửa hồ sơ khi còn draft (kể cả sau yêu cầu sửa). Lưu được khi chưa đủ thông tin gửi duyệt.</summary>
    [HttpPut("profile")]
    [ProducesResponseType<BakeryProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateProfile(long bakeryId, BakeryProfileInput input, CancellationToken cancellationToken)
    {
        var result = await onboarding.UpdateProfileAsync(UserId, bakeryId, input, cancellationToken);
        if (result.Errors is not null) return Validation(result.Errors);
        return result.Outcome == OwnerActionOutcome.Succeeded
            ? Ok(await onboarding.GetProfileAsync(UserId, bakeryId, cancellationToken))
            : OwnerOutcome(result.Outcome);
    }

    /// <summary>Owner đã xác minh email gửi hồ sơ draft đủ thông tin và đã chọn gói → pending_review.</summary>
    [HttpPost("profile/submit")]
    [RequireVerifiedEmail]
    [ProducesResponseType<BakeryProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Submit(long bakeryId, CancellationToken cancellationToken)
    {
        var result = await onboarding.SubmitAsync(UserId, bakeryId, cancellationToken);
        switch (result.Status)
        {
            case SubmitProfileStatus.Submitted:
                return Ok(await onboarding.GetProfileAsync(UserId, bakeryId, cancellationToken));
            case SubmitProfileStatus.BakeryNotFound:
                return BakeryNotFound();
            case SubmitProfileStatus.OwnerRequired:
                return OwnerRequired();
            case SubmitProfileStatus.NotDraft:
                return Coded(StatusCodes.Status409Conflict, "PROFILE_NOT_DRAFT", "Chỉ gửi duyệt được hồ sơ đang ở trạng thái draft.");
            case SubmitProfileStatus.Incomplete:
                var problem = Coded(StatusCodes.Status409Conflict, "PROFILE_INCOMPLETE", "Hồ sơ chưa đủ thông tin để gửi duyệt.");
                ((ProblemDetails)problem.Value!).Extensions["missing"] = result.Missing;
                return problem;
            default:
                throw new InvalidOperationException($"Kết quả không mong đợi: {result.Status}");
        }
    }

    [HttpGet("reviews")]
    [ProducesResponseType<IReadOnlyList<ReviewEventDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reviews(long bakeryId, CancellationToken cancellationToken) =>
        await onboarding.ListReviewEventsAsync(UserId, bakeryId, cancellationToken) is { } events ? Ok(events) : BakeryNotFound();

    /// <summary>Owner chọn gói. Không đổi được khi tiệm đang có kỳ sử dụng còn hạn hoặc hồ sơ bị từ chối.</summary>
    [HttpPut("subscription/plan")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SelectPlan(long bakeryId, SelectPlanRequest request, CancellationToken cancellationToken)
    {
        var result = await onboarding.SelectPlanAsync(UserId, bakeryId, request, cancellationToken);
        if (result.Errors is not null) return Validation(result.Errors);
        return result.Outcome == OwnerActionOutcome.Succeeded ? NoContent() : OwnerOutcome(result.Outcome);
    }

    [HttpGet("members")]
    [ProducesResponseType<IReadOnlyList<BakeryMemberDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Members(long bakeryId, CancellationToken cancellationToken)
    {
        var (outcome, members) = await staff.ListMembersAsync(UserId, bakeryId, cancellationToken);
        return outcome == OwnerActionOutcome.Succeeded ? Ok(members) : OwnerOutcome(outcome);
    }

    /// <summary>Owner gỡ staff (membership → removed, giữ lịch sử). Không gỡ được owner.</summary>
    [HttpDelete("members/{memberUserId:long:min(1)}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveMember(long bakeryId, long memberUserId, CancellationToken cancellationToken) =>
        await staff.RemoveStaffAsync(UserId, bakeryId, memberUserId, cancellationToken) switch
        {
            RemoveMemberOutcome.Removed => NoContent(),
            RemoveMemberOutcome.BakeryNotFound => BakeryNotFound(),
            RemoveMemberOutcome.OwnerRequired => OwnerRequired(),
            RemoveMemberOutcome.MemberNotFound => Coded(StatusCodes.Status404NotFound, "MEMBER_NOT_FOUND", "Không tìm thấy thành viên đang hoạt động."),
            RemoveMemberOutcome.CannotRemoveOwner => Coded(StatusCodes.Status409Conflict, "CANNOT_REMOVE_OWNER", "Không thể gỡ chủ tiệm."),
            var other => throw new InvalidOperationException($"Kết quả không mong đợi: {other}"),
        };

    [HttpGet("invitations")]
    [ProducesResponseType<IReadOnlyList<BakeryInvitationDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Invitations(long bakeryId, CancellationToken cancellationToken)
    {
        var (outcome, invitations) = await staff.ListInvitationsAsync(UserId, bakeryId, cancellationToken);
        return outcome == OwnerActionOutcome.Succeeded ? Ok(invitations) : OwnerOutcome(outcome);
    }

    /// <summary>
    /// Owner đã xác minh email mời staff theo email; lời mời cũ đang mở cùng email bị thu hồi. 201 kèm
    /// <c>emailDelivered</c>: false nghĩa là lời mời đã tạo nhưng email chưa gửi được.
    /// </summary>
    [HttpPost("invitations")]
    [RequireVerifiedEmail]
    [EnableRateLimiting(AuthDefaults.AuthRateLimitPolicy)]
    [ProducesResponseType<InvitationCreatedResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Invite(long bakeryId, InviteStaffRequest request, CancellationToken cancellationToken)
    {
        var result = await staff.InviteAsync(UserId, bakeryId, request, cancellationToken);
        if (result.Errors is not null) return Validation(result.Errors);
        return result.Status switch
        {
            CreateInvitationStatus.Created => StatusCode(StatusCodes.Status201Created,
                new InvitationCreatedResponse(result.Invitation!, result.EmailDelivered)),
            CreateInvitationStatus.BakeryNotFound => BakeryNotFound(),
            CreateInvitationStatus.OwnerRequired => OwnerRequired(),
            CreateInvitationStatus.AlreadyMember => Coded(StatusCodes.Status409Conflict, "ALREADY_MEMBER", "Email này đã là thành viên của tiệm."),
            CreateInvitationStatus.NotAllowedInStatus => StatusNotAllowed(),
            var other => throw new InvalidOperationException($"Kết quả không mong đợi: {other}"),
        };
    }

    [HttpDelete("invitations/{invitationId:long:min(1)}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RevokeInvitation(long bakeryId, long invitationId, CancellationToken cancellationToken) =>
        await staff.RevokeInvitationAsync(UserId, bakeryId, invitationId, cancellationToken) switch
        {
            RevokeInvitationOutcome.Revoked => NoContent(),
            RevokeInvitationOutcome.BakeryNotFound => BakeryNotFound(),
            RevokeInvitationOutcome.OwnerRequired => OwnerRequired(),
            RevokeInvitationOutcome.InvitationNotFound => Coded(StatusCodes.Status404NotFound, "INVITATION_NOT_FOUND", "Không tìm thấy lời mời đang mở."),
            var other => throw new InvalidOperationException($"Kết quả không mong đợi: {other}"),
        };

    private IActionResult OwnerOutcome(OwnerActionOutcome outcome) => outcome switch
    {
        OwnerActionOutcome.BakeryNotFound => BakeryNotFound(),
        OwnerActionOutcome.OwnerRequired => OwnerRequired(),
        OwnerActionOutcome.NotAllowedInStatus => StatusNotAllowed(),
        OwnerActionOutcome.PlanNotAvailable => Coded(StatusCodes.Status404NotFound, "PLAN_NOT_FOUND", "Gói không tồn tại hoặc không mở đăng ký."),
        OwnerActionOutcome.PlanChangeNotAllowed => Coded(StatusCodes.Status409Conflict, "PLAN_CHANGE_NOT_ALLOWED",
            "Chưa hỗ trợ đổi gói khi tiệm còn thời hạn sử dụng."),
        _ => throw new InvalidOperationException($"Kết quả không mong đợi: {outcome}"),
    };

    private ObjectResult BakeryNotFound() => Coded(StatusCodes.Status404NotFound, "BAKERY_NOT_FOUND", "Không tìm thấy tiệm bánh.");

    private ObjectResult OwnerRequired() => Coded(StatusCodes.Status403Forbidden, "OWNER_REQUIRED", "Chỉ chủ tiệm được thực hiện thao tác này.");

    private ObjectResult StatusNotAllowed() =>
        Coded(StatusCodes.Status409Conflict, "PROFILE_STATUS_NOT_ALLOWED", "Trạng thái hồ sơ hiện tại không cho phép thao tác này.");

    private IActionResult Validation(IReadOnlyDictionary<string, string[]> errors) =>
        ValidationProblem(new ValidationProblemDetails(errors.ToDictionary(e => e.Key, e => e.Value)));

    private ObjectResult Coded(int status, string code, string title)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode: status, title: title);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }
}

public sealed record InvitationCreatedResponse(BakeryInvitationDto Invitation, bool EmailDelivered);
