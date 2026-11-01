using DeCakes.Application.Accounts;
using DeCakes.Domain.Accounts;

namespace DeCakes.Application.BakeryOnboarding;

/// <summary>Chủ tiệm tự đăng ký, hoàn thiện hồ sơ, chọn gói và gửi duyệt.</summary>
public interface IBakeryOnboardingService
{
    Task<RegisterBakeryResult> RegisterAsync(RegisterBakeryRequest request, CancellationToken cancellationToken);

    /// <summary>Null nếu tiệm không tồn tại hoặc user không phải thành viên active.</summary>
    Task<BakeryProfileDto?> GetProfileAsync(long userId, long bakeryId, CancellationToken cancellationToken);

    Task<OwnerActionResult> UpdateProfileAsync(long userId, long bakeryId, BakeryProfileInput input, CancellationToken cancellationToken);

    Task<SubmitProfileResult> SubmitAsync(long userId, long bakeryId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReviewEventDto>?> ListReviewEventsAsync(long userId, long bakeryId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SubscriptionPlanDto>> ListPlansAsync(CancellationToken cancellationToken);

    Task<OwnerActionResult> SelectPlanAsync(long userId, long bakeryId, SelectPlanRequest request, CancellationToken cancellationToken);
}

public abstract record RegisterBakeryResult
{
    public sealed record Created(RegisteredBakeryDto Registered) : RegisterBakeryResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : RegisterBakeryResult;

    public sealed record EmailTaken : RegisterBakeryResult;
}

/// <summary><c>Errors</c> khác null: dữ liệu không hợp lệ, chưa chạm store.</summary>
public sealed record OwnerActionResult(OwnerActionOutcome Outcome, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public static OwnerActionResult Invalid(IReadOnlyDictionary<string, string[]> errors) => new(OwnerActionOutcome.Succeeded, errors);
}

public sealed class BakeryOnboardingService(
    IBakeryOnboardingStore store,
    IPasswordHashService passwordHasher,
    TimeProvider time) : IBakeryOnboardingService
{
    public async Task<RegisterBakeryResult> RegisterAsync(RegisterBakeryRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var account = AccountService.NormalizeNewAccount(
            new RegisterRequest(request.Email, request.Password, request.FullName, request.Phone), errors);
        var profile = BakeryProfileRules.Normalize(new BakeryProfileInput(request.BakeryName, null, null, null, null), errors);
        if (errors.Remove("name", out var nameErrors))
        {
            errors["bakeryName"] = nameErrors;
        }

        if (account is null || profile is null || errors.Count > 0)
        {
            return new RegisterBakeryResult.ValidationFailed(errors);
        }

        try
        {
            // Role, trạng thái, is_demo, trạng thái hồ sơ và member_role do server cố định trong store.
            var (userId, bakeryId) = await store.CreateOwnerAsync(
                new NewBakeryOwner(
                    account.Email, passwordHasher.Hash(account.Password), account.FullName, account.Phone,
                    profile.Name, BakeryProfileRules.SlugBase(profile.Name)),
                cancellationToken);
            return new RegisterBakeryResult.Created(new RegisteredBakeryDto(
                new UserDto(userId, account.Email, account.FullName, account.Phone, SystemRoles.Bakery, EmailVerified: false), bakeryId));
        }
        catch (DuplicateEmailException)
        {
            return new RegisterBakeryResult.EmailTaken();
        }
    }

    public async Task<BakeryProfileDto?> GetProfileAsync(long userId, long bakeryId, CancellationToken cancellationToken)
    {
        var record = await store.FindProfileAsync(userId, bakeryId, cancellationToken);
        return record is null
            ? null
            : new BakeryProfileDto(
                record.BakeryId, record.Name, record.Slug, record.Description, record.AddressLine, record.City, record.Phone,
                record.ProfileStatus, record.MemberRole, record.SelectedPlan, record.LastDecision,
                BakeryProfileRules.MissingForSubmit(record.Name, record.AddressLine, record.City, record.Phone, record.SelectedPlanOpen));
    }

    public async Task<OwnerActionResult> UpdateProfileAsync(
        long userId, long bakeryId, BakeryProfileInput input, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var values = BakeryProfileRules.Normalize(input, errors);
        if (values is null)
        {
            return OwnerActionResult.Invalid(errors);
        }

        return new OwnerActionResult(await store.UpdateDraftProfileAsync(userId, bakeryId, values, Now(), cancellationToken));
    }

    public Task<SubmitProfileResult> SubmitAsync(long userId, long bakeryId, CancellationToken cancellationToken) =>
        store.SubmitAsync(userId, bakeryId, Now(), cancellationToken);

    public Task<IReadOnlyList<ReviewEventDto>?> ListReviewEventsAsync(long userId, long bakeryId, CancellationToken cancellationToken) =>
        store.ListReviewEventsForMemberAsync(userId, bakeryId, cancellationToken);

    public Task<IReadOnlyList<SubscriptionPlanDto>> ListPlansAsync(CancellationToken cancellationToken) =>
        store.ListActivePlansAsync(cancellationToken);

    public async Task<OwnerActionResult> SelectPlanAsync(
        long userId, long bakeryId, SelectPlanRequest request, CancellationToken cancellationToken)
    {
        if (request.PlanId is not > 0)
        {
            return OwnerActionResult.Invalid(new Dictionary<string, string[]> { ["planId"] = ["planId là số nguyên dương."] });
        }

        return new OwnerActionResult(await store.SelectPlanAsync(userId, bakeryId, request.PlanId.Value, Now(), cancellationToken));
    }

    private DateTime Now() => time.GetUtcNow().UtcDateTime;
}
