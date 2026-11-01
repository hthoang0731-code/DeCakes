namespace DeCakes.Domain.BakeryWorkspace;

/// <summary>Giá trị hợp lệ của bakery_members.member_role (chk_bakery_members_role).</summary>
public static class BakeryMemberRoles
{
    public const string Owner = "owner";
    public const string Staff = "staff";
}

/// <summary>
/// Giá trị hợp lệ của bakery_members.status (chk_bakery_members_status). Chỉ thành viên active được đọc dữ liệu
/// của tiệm, giống sp_guard_active_bakery_member (user active, system_role 'bakery', thành viên active).
/// </summary>
public static class BakeryMemberStatuses
{
    public const string Active = "active";
    public const string Removed = "removed";
}
