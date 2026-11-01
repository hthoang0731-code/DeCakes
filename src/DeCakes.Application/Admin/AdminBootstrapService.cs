using System.Net.Mail;
using System.Text;
using DeCakes.Application.Accounts;

namespace DeCakes.Application.Admin;

// Deliberately not registered with the HTTP application. Used only by the operator console.
public interface IAdminProvisioningStore
{
    Task<long> CreateAdminAsync(string email, string fullName, string passwordHash, CancellationToken cancellationToken);
}

public enum AdminBootstrapOutcome { Created, InvalidInput, EmailTaken }

public sealed class AdminBootstrapService(IAdminProvisioningStore store, IPasswordHashService hasher)
{
    public async Task<AdminBootstrapOutcome> CreateAsync(
        string? email, string? fullName, string? password, CancellationToken cancellationToken)
    {
        var normalized = AccountService.NormalizeEmail(email);
        var name = fullName?.Trim().Normalize(NormalizationForm.FormC);
        if (normalized is null || normalized.Length > AccountService.EmailMaxLength
            || normalized.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || !MailAddress.TryCreate(normalized, out var address) || address.Address != normalized || address.DisplayName.Length > 0
            || string.IsNullOrEmpty(name) || name.Length > AccountService.FullNameMaxLength || name.Any(char.IsControl)
            || password is null || password.Length < AccountService.PasswordMinLength || password.Length > AccountService.PasswordMaxLength)
            return AdminBootstrapOutcome.InvalidInput;

        try
        {
            await store.CreateAdminAsync(normalized, name, hasher.Hash(password), cancellationToken);
            return AdminBootstrapOutcome.Created;
        }
        catch (DuplicateEmailException)
        {
            return AdminBootstrapOutcome.EmailTaken;
        }
    }
}
