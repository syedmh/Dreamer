using System.Globalization;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Infrastructure.Identity.Entities;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HusayniaTabruk.Infrastructure.Identity.Services;

public sealed class AspNetIdentityService(
    TabrukDbContext context,
    IPasswordHasher<TabrukIdentityUser> passwordHasher)
    : IIdentityService
{
    private const int MinimumPasswordLength = 12;
    private const string DummyPasswordHash =
        "AQAAAAIAAYagAAAAEP6GSot41gfivBowbSWE7WhknwpGQrZu9USv15rBG/cuDsxU9+Ay/samD1I3QJvk3w==";

    public ValueTask<Result<UserId>> VerifyCredentialsAsync(
        string login,
        string password,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => VerifyCredentialsCoreAsync(login, password, cancellationToken));

    public ValueTask<Result> SetPasswordAsync(
        UserId userId,
        string password,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => SetPasswordCoreAsync(userId, password, cancellationToken));

    public ValueTask<Result> VerifyPasswordAsync(
        UserId userId,
        string password,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => VerifyPasswordCoreAsync(userId, password, cancellationToken));

    private async ValueTask<Result<UserId>> VerifyCredentialsCoreAsync(
        string login,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
        {
            return Result.Failure<UserId>(AuthenticationFailed());
        }

        string normalizedLogin = Normalize(login);
        TabrukIdentityUser? user = await context.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.NormalizedEmail == normalizedLogin
                    || candidate.NormalizedUserName == normalizedLogin,
                cancellationToken);

        if (!VerifyPasswordHash(user, password))
        {
            return Result.Failure<UserId>(AuthenticationFailed());
        }

        return Result.Success(UserId.From(user!.Id));
    }

    private ValueTask<Result> SetPasswordCoreAsync(
        UserId userId,
        string password,
        CancellationToken cancellationToken)
    {
        Result validation = ValidatePassword(password);
        if (validation.IsFailure)
        {
            return ValueTask.FromResult(validation);
        }

        return PersistenceWriteSupport.ExecuteWriteAsync(
            context,
            async token =>
            {
                TabrukIdentityUser? user = await context.Users
                    .SingleOrDefaultAsync(candidate => candidate.Id == userId.Value, token);
                if (user is null || !string.IsNullOrWhiteSpace(user.PasswordHash))
                {
                    return Result.Failure(AuthenticationErrorCodes.Unauthorized(
                        AuthenticationErrorCodes.InvitationInvalid,
                        "The invitation is invalid or has expired."));
                }

                user.PasswordHash = passwordHasher.HashPassword(user, password);
                user.EmailConfirmed = true;
                user.SecurityStamp = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                user.ConcurrencyStamp = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                return Result.Success();
            },
            cancellationToken);
    }

    private async ValueTask<Result> VerifyPasswordCoreAsync(
        UserId userId,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return Result.Failure(AuthenticationFailed());
        }

        TabrukIdentityUser? user = await context.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == userId.Value, cancellationToken);
        if (!VerifyPasswordHash(user, password))
        {
            return Result.Failure(AuthenticationFailed());
        }

        return Result.Success();
    }

    private bool VerifyPasswordHash(TabrukIdentityUser? user, string password)
    {
        TabrukIdentityUser verificationUser = user ?? new TabrukIdentityUser();
        string verificationHash = string.IsNullOrWhiteSpace(user?.PasswordHash)
            ? DummyPasswordHash
            : user.PasswordHash;
        PasswordVerificationResult verification =
            passwordHasher.VerifyHashedPassword(verificationUser, verificationHash, password);
        return user is not null
            && !string.IsNullOrWhiteSpace(user.PasswordHash)
            && verification != PasswordVerificationResult.Failed;
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();

    private static Result ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password)
            || password.Length < MinimumPasswordLength
            || !password.Any(char.IsUpper)
            || !password.Any(char.IsLower)
            || !password.Any(char.IsDigit)
            || !password.Any(character => !char.IsLetterOrDigit(character)))
        {
            return Result.Failure(
                AuthenticationErrorCodes.Validation(
                    AuthenticationErrorCodes.InvalidAuthInput,
                    "The password does not meet the security requirements."));
        }

        return Result.Success();
    }

    private static HusayniaTabruk.Domain.Common.Errors.DomainError AuthenticationFailed() =>
        AuthenticationErrorCodes.Unauthorized(
            AuthenticationErrorCodes.AuthenticationFailed,
            "Authentication failed.");
}
