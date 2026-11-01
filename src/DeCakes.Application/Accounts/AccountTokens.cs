using System.Buffers.Text;
using System.Security.Cryptography;

namespace DeCakes.Application.Accounts;

/// <summary>
/// Token tài khoản: 32 byte từ CSPRNG, gửi cho người dùng dạng base64url (43 ký tự). Kho chỉ lưu SHA-256 của 32 byte
/// đó, nên đọc được database cũng không dựng lại được link. Token không bao giờ được ghi log.
/// </summary>
public static class AccountTokens
{
    public const int TokenBytes = 32;
    public const int TokenLength = 43;

    public static (string Token, byte[] Hash) Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        return (Base64Url.EncodeToString(bytes), SHA256.HashData(bytes));
    }

    /// <summary>Chỉ nhận đúng định dạng do <see cref="Create"/> sinh ra; mọi giá trị khác trả false, không ném lỗi.</summary>
    public static bool TryHash(string? token, out byte[] hash)
    {
        hash = [];
        // Base64Url.TryDecodeFromChars chỉ trả false khi thiếu chỗ; ký tự sai sẽ ném FormatException, nên kiểm tra trước.
        if (token is null || token.Length != TokenLength || !token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[TokenBytes + 2];
        if (!Base64Url.TryDecodeFromChars(token, bytes, out var written) || written != TokenBytes)
        {
            return false;
        }

        hash = SHA256.HashData(bytes[..TokenBytes]);
        return true;
    }
}
