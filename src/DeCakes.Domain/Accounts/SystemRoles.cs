namespace DeCakes.Domain.Accounts;

/// <summary>Giá trị hợp lệ của users.system_role (chk_users_system_role).</summary>
public static class SystemRoles
{
    public const string Customer = "customer";
    public const string Bakery = "bakery";
    public const string Admin = "admin";

    public static bool IsKnown(string? role) => role is Customer or Bakery or Admin;
}
