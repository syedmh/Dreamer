using System.Reflection;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Application.Signups.Submit;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Signups;

namespace HusayniaTabruk.Application.Tests.Signups.Submit;

public sealed class SubmitSignupServiceTests
{
    [Fact]
    public async Task OversizedLabelIsRejectedBeforeAuthorityPersistenceOrIdempotency()
    {
        SubmitSignupService service = CreateValidationOnlyService();

        Result<SignupSummary> result = await service.ExecuteAsync(
            new SubmitSignupCommand(
                HelpNeedId.New(),
                SignupKind.Individual,
                new string('x', ApplicationLimits.MaximumSignupLabelUnicodeScalars + 1),
                [],
                0,
                IdempotencyKey.New()));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.PayloadTooLarge, result.Error.Code);
        Assert.Equal(ErrorType.PayloadTooLarge, result.Error.Type);
    }

    [Fact]
    public async Task RawOversizedPaddedLabelIsRejectedBeforeAuthorityPersistenceOrIdempotency()
    {
        SubmitSignupService service = CreateValidationOnlyService();
        string rawLabel =
            new string(' ', ApplicationLimits.MaximumSignupLabelUnicodeScalars + 1)
            + "Food";

        Result<SignupSummary> result = await service.ExecuteAsync(
            new SubmitSignupCommand(
                HelpNeedId.New(),
                SignupKind.Individual,
                rawLabel,
                [],
                0,
                IdempotencyKey.New()));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.PayloadTooLarge, result.Error.Code);
        Assert.Equal(ErrorType.PayloadTooLarge, result.Error.Type);
        Assert.DoesNotContain(rawLabel, result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateParticipantIdsAreRejectedBeforeDatabaseAccess()
    {
        SubmitSignupService service = CreateValidationOnlyService();
        MembershipId participantId = MembershipId.New();

        Result<SignupSummary> result = await service.ExecuteAsync(
            new SubmitSignupCommand(
                HelpNeedId.New(),
                SignupKind.Team,
                null,
                [participantId, participantId],
                0,
                IdempotencyKey.New()));

        Assert.True(result.IsFailure);
        Assert.Equal(SignupApplicationErrorCodes.InvalidSignupRequest, result.Error.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Theory]
    [InlineData("Fatima")]
    [InlineData("Fatima Ali")]
    [InlineData("fatima@example.test")]
    [InlineData("+1 (555) 010-0200")]
    [InlineData("WhatsApp 5550100200")]
    public async Task IdentifyingLabelIsRejectedBeforeAuthorityPersistenceOrIdempotency(
        string identifyingLabel)
    {
        SubmitSignupService service = CreateValidationOnlyService();

        Result<SignupSummary> result = await service.ExecuteAsync(
            new SubmitSignupCommand(
                HelpNeedId.New(),
                SignupKind.Team,
                identifyingLabel,
                [],
                1,
                IdempotencyKey.New()));

        Assert.True(result.IsFailure);
        Assert.Equal(
            SignupApplicationErrorCodes.InvalidSignupRequest,
            result.Error.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.DoesNotContain(
            identifyingLabel,
            result.Error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ControlledLabelPreservesNullOptionality()
    {
        SignupLabelValidationResult result = SignupLabelPolicy.Normalize(null);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData(" Food")]
    [InlineData("Food ")]
    [InlineData("  Food Preparation Group 2  ")]
    public void ControlledLabelRejectsEmptyWhitespaceOnlyAndPaddedValues(string label)
    {
        SignupLabelValidationResult result = SignupLabelPolicy.Normalize(label);

        Assert.False(result.IsSuccess);
        Assert.Equal(SignupLabelValidationError.UnsupportedContent, result.Error);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("food")]
    [InlineData("FOOD")]
    [InlineData("Food preparation")]
    [InlineData("Fоod")]
    [InlineData("Ｆｏｏｄ")]
    [InlineData("Food\u0301")]
    [InlineData("Food-Team")]
    [InlineData("Food.Team")]
    [InlineData("Food/Team")]
    [InlineData("Food  Team")]
    [InlineData("Food\tTeam")]
    [InlineData("Food\nTeam")]
    [InlineData("Food\u00A0Team")]
    [InlineData("Food\u200BTeam")]
    [InlineData("Food 0")]
    [InlineData("Food 01")]
    [InlineData("Food 1000")]
    [InlineData("Food 12 34")]
    [InlineData("Food +1")]
    [InlineData("Food １")]
    public void ControlledLabelRejectsCaseUnicodePunctuationWhitespaceAndNumericBypasses(
        string label)
    {
        SignupLabelValidationResult result = SignupLabelPolicy.Normalize(label);

        Assert.False(result.IsSuccess);
        Assert.Equal(SignupLabelValidationError.UnsupportedContent, result.Error);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("Team 1")]
    [InlineData("Team 999")]
    public void ControlledLabelAcceptsDocumentedNumericSuffixBounds(string label)
    {
        SignupLabelValidationResult result = SignupLabelPolicy.Normalize(label);

        Assert.True(result.IsSuccess);
        Assert.Equal(label, result.Value);
    }

    [Fact]
    public void SubmissionAndProjectionContractsContainNoFreeTextParticipantIdentity()
    {
        Assert.Equal(
            new[]
            {
                nameof(SubmitSignupCommand.HelpNeedId),
                nameof(SubmitSignupCommand.Kind),
                nameof(SubmitSignupCommand.Label),
                nameof(SubmitSignupCommand.MemberParticipantIds),
                nameof(SubmitSignupCommand.UnnamedParticipantCount),
                nameof(SubmitSignupCommand.IdempotencyKey),
            },
            typeof(SubmitSignupCommand).GetProperties()
                .Select(property => property.Name)
                .ToArray());
        Assert.DoesNotContain(
            typeof(SubmitSignupCommand).GetProperties(),
            property => property.Name.Contains(
                "ParticipantName",
                StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains(
                    "DisplayName",
                    StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(SignupSummary).GetProperties(),
            property => property.Name.Contains(
                    "Email",
                    StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains(
                    "Phone",
                    StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains(
                    "Login",
                    StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains(
                    "UserId",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static SubmitSignupService CreateValidationOnlyService() =>
        new(
            new InlineUnitOfWork(),
            ThrowingProxy.Create<ISignupRepository>(),
            ThrowingProxy.Create<IMembershipRepository>(),
            ThrowingProxy.Create<IIdempotencyStore>(),
            new CurrentActor(),
            new FixedClock());

    private sealed class InlineUnitOfWork : IUnitOfWork
    {
        public ValueTask<Result<T>> ExecuteAsync<T>(
            Func<CancellationToken, ValueTask<Result<T>>> operation,
            CancellationToken cancellationToken = default) =>
            operation(cancellationToken);
    }

    private sealed class CurrentActor : ICurrentActor
    {
        public UserId UserId { get; } = UserId.New();
        public MembershipId MembershipId { get; } = MembershipId.New();
        public OrganizationId OrganizationId { get; } = OrganizationId.New();
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } =
            new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
    }

    public class ThrowingProxy : DispatchProxy
    {
        public static T Create<T>()
            where T : class =>
            DispatchProxy.Create<T, ThrowingProxy>();

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args) =>
            throw new InvalidOperationException(
                $"Unexpected dependency call: {targetMethod?.Name}.");
    }
}
