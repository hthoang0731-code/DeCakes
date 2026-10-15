using System.Buffers.Binary;
using System.Security.Cryptography;
using DeCakes.Application.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace DeCakes.Infrastructure.Accounts;

/// <summary>Bọc <see cref="PasswordHasher{TUser}"/> của Microsoft (mặc định V3: PBKDF2-HMAC-SHA512).</summary>
public sealed class AspNetPasswordHashService : IPasswordHashService
{
    private const int V2Length = 1 + 16 + 32;
    private const int V3HeaderLength = 13;
    private const int MinSaltLength = 16;
    private const int MinSubkeyLength = 16;

    private readonly PasswordHasher<PasswordOwner> _hasher;
    private readonly Lazy<string> _dummyHash;
    private long _dummyVerificationCount;

    public AspNetPasswordHashService() : this(Options.Create(new PasswordHasherOptions())) { }

    public AspNetPasswordHashService(IOptions<PasswordHasherOptions> options)
    {
        _hasher = new PasswordHasher<PasswordOwner>(options);
        // Hash giả cùng tham số với hash thật, để nhánh "không có user/hash" tốn thời gian tương đương.
        _dummyHash = new Lazy<string>(
            () => _hasher.HashPassword(PasswordOwner.Instance, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Số lần đã xác minh trên hash giả. Chỉ để kiểm thử hành vi, không dùng cho nghiệp vụ.</summary>
    internal long DummyVerificationCount => Interlocked.Read(ref _dummyVerificationCount);

    public string Hash(string password) => _hasher.HashPassword(PasswordOwner.Instance, password);

    public PasswordCheck Verify(string? hashedPassword, string password)
    {
        // Hash NULL, không phải base64, hoặc sai cấu trúc V2/V3: PasswordHasher sẽ từ chối trước bước PBKDF2,
        // nên xác minh trên hash giả để giữ chi phí tương đương rồi trả Failed.
        if (hashedPassword is null || !IsWellFormed(hashedPassword))
        {
            VerifyDummy(password);
            return PasswordCheck.Failed;
        }

        // Hash đúng cấu trúc: dùng nguyên xác minh chuẩn của Microsoft.
        return _hasher.VerifyHashedPassword(PasswordOwner.Instance, hashedPassword, password) switch
        {
            PasswordVerificationResult.Success => PasswordCheck.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessRehashNeeded,
            _ => PasswordCheck.Failed,
        };
    }

    /// <summary>
    /// Kiểm tra cấu trúc theo định dạng của PasswordHasher (không xác minh mật khẩu):
    /// V2 = 0x00 | salt 16 | subkey 32; V3 = 0x01 | prf | iter | saltLen | salt | subkey,
    /// với prf trong 0..2, iter ≥ 1, salt ≥ 16 byte và subkey ≥ 16 byte.
    /// </summary>
    internal static bool IsWellFormed(string hashedPassword)
    {
        var buffer = new byte[(hashedPassword.Length * 3 / 4) + 3];
        if (!Convert.TryFromBase64String(hashedPassword, buffer, out var length) || length == 0)
        {
            return false;
        }

        ReadOnlySpan<byte> bytes = buffer.AsSpan(0, length);
        return bytes[0] switch
        {
            0x00 => length == V2Length,
            0x01 => IsWellFormedV3(bytes),
            _ => false,
        };
    }

    private static bool IsWellFormedV3(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < V3HeaderLength)
        {
            return false;
        }

        var prf = BinaryPrimitives.ReadUInt32BigEndian(bytes[1..5]);
        var iterations = BinaryPrimitives.ReadUInt32BigEndian(bytes[5..9]);
        var saltLength = BinaryPrimitives.ReadUInt32BigEndian(bytes[9..13]);
        return prf <= 2
            && iterations is >= 1 and <= int.MaxValue
            && saltLength >= MinSaltLength
            && saltLength <= (uint)(bytes.Length - V3HeaderLength - MinSubkeyLength);
    }

    private void VerifyDummy(string password)
    {
        Interlocked.Increment(ref _dummyVerificationCount);
        _hasher.VerifyHashedPassword(PasswordOwner.Instance, _dummyHash.Value, password);
    }

    // PasswordHasher<TUser> không dùng tới đối tượng user; lớp này chỉ để thỏa kiểu generic.
    private sealed class PasswordOwner
    {
        public static readonly PasswordOwner Instance = new();
    }
}
