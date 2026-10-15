using System.Security.Cryptography;
using DeCakes.Application.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace DeCakes.Infrastructure.Accounts;

/// <summary>Bọc <see cref="PasswordHasher{TUser}"/> của Microsoft (mặc định V3: PBKDF2-HMAC-SHA512).</summary>
public sealed class AspNetPasswordHashService : IPasswordHashService
{
    private readonly PasswordHasher<PasswordOwner> _hasher;
    private readonly Lazy<string> _dummyHash;

    public AspNetPasswordHashService() : this(Options.Create(new PasswordHasherOptions())) { }

    public AspNetPasswordHashService(IOptions<PasswordHasherOptions> options)
    {
        _hasher = new PasswordHasher<PasswordOwner>(options);
        // Hash giả cùng tham số với hash thật, để nhánh "không có user/hash" tốn thời gian tương đương.
        _dummyHash = new Lazy<string>(
            () => _hasher.HashPassword(PasswordOwner.Instance, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Hash(string password) => _hasher.HashPassword(PasswordOwner.Instance, password);

    public PasswordCheck Verify(string? hashedPassword, string password)
    {
        if (hashedPassword is null)
        {
            VerifyDummy(password);
            return PasswordCheck.Failed;
        }

        PasswordVerificationResult result;
        try
        {
            result = _hasher.VerifyHashedPassword(PasswordOwner.Instance, hashedPassword, password);
        }
        catch (FormatException)
        {
            // Hash hỏng (không phải base64).
            VerifyDummy(password);
            return PasswordCheck.Failed;
        }

        return result switch
        {
            PasswordVerificationResult.Success => PasswordCheck.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessRehashNeeded,
            _ => PasswordCheck.Failed,
        };
    }

    private void VerifyDummy(string password) =>
        _hasher.VerifyHashedPassword(PasswordOwner.Instance, _dummyHash.Value, password);

    // PasswordHasher<TUser> không dùng tới đối tượng user; lớp này chỉ để thỏa kiểu generic.
    private sealed class PasswordOwner
    {
        public static readonly PasswordOwner Instance = new();
    }
}
