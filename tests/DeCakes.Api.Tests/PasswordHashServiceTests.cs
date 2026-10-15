using DeCakes.Application.Accounts;
using DeCakes.Infrastructure.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace DeCakes.Api.Tests;

/// <summary>
/// Adapter PasswordHasher thật của Infrastructure (không cần database). Nhánh hash giả được chứng minh bằng bộ đếm
/// nội bộ, không đo thời gian.
/// </summary>
public class PasswordHashServiceTests
{
    private const string Password = "mat-khau-du-dai-1";
    private readonly AspNetPasswordHashService _hasher = new();

    [Fact]
    public void Hash_then_verify_succeeds_and_hash_does_not_contain_password()
    {
        var hash = _hasher.Hash(Password);

        Assert.DoesNotContain(Password, hash);
        Assert.True(hash.Length <= 255, "Hash phải vừa cột password_hash VARCHAR(255).");
        Assert.Equal(PasswordCheck.Success, _hasher.Verify(hash, Password));
        Assert.Equal(0, _hasher.DummyVerificationCount);
    }

    [Fact]
    public void Wrong_or_untrimmed_password_fails_through_microsoft_verification_not_dummy()
    {
        var hash = _hasher.Hash(Password);

        Assert.Equal(PasswordCheck.Failed, _hasher.Verify(hash, Password + "x"));
        Assert.Equal(PasswordCheck.Failed, _hasher.Verify(hash, " " + Password));
        Assert.Equal(0, _hasher.DummyVerificationCount);
    }

    public static TheoryData<string?> MalformedHashes()
    {
        // V3 hợp lệ để lấy phần đầu làm cơ sở cho các biến thể hỏng.
        var valid = Convert.FromBase64String(new AspNetPasswordHashService().Hash(Password));
        string Mutate(Action<byte[]> change)
        {
            var copy = (byte[])valid.Clone();
            change(copy);
            return Convert.ToBase64String(copy);
        }

        return new()
        {
            null,
            "",
            "%%%khong-phai-base64%%%",
            "AAAA",                                                    // base64 hợp lệ, marker 0x00 nhưng sai độ dài V2
            Convert.ToBase64String([0x02, 1, 2, 3]),                    // marker không xác định
            Convert.ToBase64String(valid.AsSpan(0, 12).ToArray()),      // V3 thiếu header
            Mutate(b => b[4] = 9),                                      // prf ngoài 0..2
            Mutate(b => { b[5] = 0; b[6] = 0; b[7] = 0; b[8] = 0; }),   // iteration = 0
            Mutate(b => { b[9] = 0; b[10] = 0; b[11] = 0; b[12] = 8; }),// salt < 16 byte
            Convert.ToBase64String(valid.AsSpan(0, valid.Length - 20).ToArray()), // subkey < 16 byte
        };
    }

    [Theory]
    [MemberData(nameof(MalformedHashes))]
    public void Missing_or_malformed_hash_fails_after_dummy_verification(string? hash)
    {
        Assert.Equal(PasswordCheck.Failed, _hasher.Verify(hash, Password));
        Assert.Equal(1, _hasher.DummyVerificationCount);
    }

    [Fact]
    public void Hash_with_lower_iteration_count_needs_rehash()
    {
        var weak = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions { IterationCount = 1_000 }))
            .HashPassword(new object(), Password);

        Assert.Equal(PasswordCheck.SuccessRehashNeeded, _hasher.Verify(weak, Password));
        Assert.Equal(0, _hasher.DummyVerificationCount);
    }

    [Fact]
    public void Legacy_v2_hash_is_well_formed_and_needs_rehash()
    {
        var v2 = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2,
        })).HashPassword(new object(), Password);

        Assert.Equal(PasswordCheck.SuccessRehashNeeded, _hasher.Verify(v2, Password));
        Assert.Equal(PasswordCheck.Failed, _hasher.Verify(v2, Password + "x"));
        Assert.Equal(0, _hasher.DummyVerificationCount);
    }

    [Fact]
    public void Every_hash_produced_by_microsoft_passes_the_structure_check()
    {
        for (var i = 0; i < 20; i++)
        {
            Assert.True(AspNetPasswordHashService.IsWellFormed(_hasher.Hash(Password + i)));
        }
    }
}
