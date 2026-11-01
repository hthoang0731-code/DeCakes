namespace DeCakes.Application.DesignRequests;

/// <summary>Truy cập design_requests và request_bakeries. Triển khai ở Infrastructure.</summary>
public interface IDesignRequestStore
{
    /// <summary>Tạo yêu cầu draft, currency VND, is_demo = 0 cho khách.</summary>
    Task<long> CreateDraftAsync(long customerId, DesignRequestValues values, CancellationToken cancellationToken);

    /// <summary>Chỉ trả về yêu cầu thuộc khách; không có hoặc của khách khác đều là null.</summary>
    Task<DesignRequestDto?> FindForCustomerAsync(long requestId, long customerId, CancellationToken cancellationToken);

    /// <summary>Sắp xếp created_at DESC, id DESC.</summary>
    Task<PagedResult<DesignRequestSummaryDto>> ListForCustomerAsync(
        long customerId, string? status, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Sửa nội dung nguyên tử: chỉ ghi khi yêu cầu thuộc khách và còn draft trong cùng một câu lệnh.</summary>
    Task<UpdateDraftOutcome> UpdateDraftAsync(
        long requestId, long customerId, DesignRequestValues values, CancellationToken cancellationToken);

    /// <summary>Mẫu bánh tồn tại, published và thuộc tiệm active.</summary>
    Task<bool> IsCakeAvailableForInspirationAsync(long cakeId, CancellationToken cancellationToken);

    /// <summary>
    /// Gửi yêu cầu đến một tiệm qua sp_send_request_to_bakery (procedure tự quản lý transaction).
    /// Trả về <see cref="SendToBakeryOutcome.Sent"/> hoặc mã lỗi đã nhận diện; lỗi khác được ném ra.
    /// </summary>
    Task<SendToBakeryOutcome> SendToBakeryAsync(long requestId, long customerId, long bakeryId, CancellationToken cancellationToken);

    /// <summary>Các tiệm đã nhận yêu cầu; null nếu yêu cầu không tồn tại hoặc không thuộc khách.</summary>
    Task<IReadOnlyList<RequestBakeryDto>?> ListBakeriesAsync(long requestId, long customerId, CancellationToken cancellationToken);
}

public enum UpdateDraftOutcome
{
    Updated,
    NotFound,
    NotDraft,
}

/// <summary>Kết quả gửi tiệm, theo mã SIGNAL của sp_send_request_to_bakery và guard (allowlist).</summary>
public enum SendToBakeryOutcome
{
    Sent,
    InvalidArgument,     // INVALID_ARGUMENT
    RequestNotFound,     // REQUEST_NOT_FOUND, NOT_REQUEST_OWNER
    BakeryNotFound,      // BAKERY_NOT_FOUND
    AlreadySent,         // REQUEST_ALREADY_SENT
    RequestNotOpen,      // REQUEST_NOT_OPEN
    PickupInPast,        // REQUEST_PICKUP_IN_PAST
    BakeryNotActive,     // BAKERY_NOT_ACTIVE
    AccountNotAllowed,   // USER_NOT_FOUND, USER_NOT_ACTIVE, USER_NOT_CUSTOMER
    BakerySubscriptionInactive, // BAKERY_SUBSCRIPTION_INACTIVE (migrations/V005)
}
