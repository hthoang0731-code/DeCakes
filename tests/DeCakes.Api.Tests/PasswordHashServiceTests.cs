using DeCakes.Application.Accounts;
using DeCakes.Infrastructure.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace DeCakes.Api.Tests;

/// <summary>Kiểm tra adapter PasswordHasher thật của Infrastructure (không cần database).</summary>
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
    }

    [Fact]
    public void Wrong_or_untrimmed_password_fails()
    {
        var hash = _hasher.Hash(Password);

        Assert.Equal(PasswordCheck.Failed, _hasher.Verify(hash, Password + "x"));
        Assert.Equal(PasswordCheck.Failed, _hasher.Verify(hash, " " + Password));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("%%%khong-phai-base64%%%")]
    [InlineData("AAAA")]
    public void Missing_or_corrupt_hash_fails_without_throwing(string? hash)
    {
        Assert.Equal(PasswordCheck.Failed, _hasher.Verify(hash, Password));
    }

    [Fact]
    public void Hash_with_lower_iteration_count_needs_rehash()
    {
        var weak = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions { IterationCount = 1_000 }))
            .HashPassword(new object(), Password);

        Assert.Equal(PasswordCheck.SuccessRehashNeeded, _hasher.Verify(weak, Password));
    }
}
