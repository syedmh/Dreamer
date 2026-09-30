using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Admin.Members;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Notifications;
using HusayniaTabruk.Domain.Signups;
using HusayniaTabruk.Infrastructure.Identity.Entities;
using HusayniaTabruk.Infrastructure.Identity.Services;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using HusayniaTabruk.Infrastructure.Persistence.Repositories;
using HusayniaTabruk.IntegrationTests.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace HusayniaTabruk.IntegrationTests.Persistence;

[Trait("Category", "Persistence")]
public sealed class PostgresGovernanceAndSignupConcurrencyTests : PostgresPersistenceTest
{
    [RequiresPostgresFact]
    public async Task GovernanceCompareAndSwapInsideUnitOfWorkRejectsStaleWriterAndRollsBackEffects()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await using TabrukDbContext firstContext = database.CreateContext();
        await using TabrukDbContext secondContext = database.CreateContext();
        PostgresGovernanceRepository firstRepository = new(firstContext);
        PostgresGovernanceRepository secondRepository = new(secondContext);

        Result<OrganizationAccountGovernance> firstLoaded =
            await firstRepository.GetAsync(seed.OrganizationId);
        Result<OrganizationAccountGovernance> secondLoaded =
            await secondRepository.GetAsync(seed.OrganizationId);
        Assert.True(firstLoaded.IsSuccess, firstLoaded.IsFailure ? firstLoaded.Error.Message : null);
        Assert.True(secondLoaded.IsSuccess, secondLoaded.IsFailure ? secondLoaded.Error.Message : null);

        Result<FoodInchargeAssigned> firstAssignment =
            firstLoaded.Value.AssignFoodIncharge(
                firstLoaded.Value.Memberships.Single(
                    membership => membership.Id == seed.SecondAdministratorMembershipId),
                firstLoaded.Value.Memberships.Single(
                    membership => membership.Id == seed.MemberMembershipId),
                seed.Now.AddMinutes(1));
        Assert.True(firstAssignment.IsSuccess, firstAssignment.IsFailure ? firstAssignment.Error.Message : null);
        Result firstSaved = await firstRepository.SaveAsync(
            firstLoaded.Value,
            seed.SecondAdministratorMembershipId,
            seed.Now.AddMinutes(1),
            PersistenceEffects.Empty);
        Assert.True(firstSaved.IsSuccess, firstSaved.IsFailure ? firstSaved.Error.Message : null);

        Result<FoodInchargeAssigned> secondAssignment =
            secondLoaded.Value.AssignFoodIncharge(
                secondLoaded.Value.Memberships.Single(
                    membership => membership.Id == seed.SecondAdministratorMembershipId),
                secondLoaded.Value.Memberships.Single(
                    membership => membership.Id == seed.MemberMembershipId),
                seed.Now.AddMinutes(1));
        Assert.True(secondAssignment.IsSuccess, secondAssignment.IsFailure ? secondAssignment.Error.Message : null);
        Result secondSaved = await ExecuteWithinUnitOfWorkAsync(
            new PostgresUnitOfWork(secondContext),
            token => secondRepository.SaveAsync(
                secondLoaded.Value,
                seed.SecondAdministratorMembershipId,
                seed.Now.AddMinutes(1),
                Effects(seed, "governance"),
                token));

        Assert.True(secondSaved.IsFailure);
        Assert.Equal(ErrorCodes.StaleVersion, secondSaved.Error.Code);

        await using TabrukDbContext verification = database.CreateContext();
        Assert.Equal(1, await verification.RoleAssignments.CountAsync(
            assignment => assignment.MembershipId == seed.MemberMembershipId.Value
                && assignment.Role == (short)OrganizationRole.FoodIncharge
                && assignment.RevokedAt == null));
        Assert.Empty(await verification.AuditEvents.ToListAsync());
        Assert.Empty(await verification.Notifications.ToListAsync());
        Assert.Empty(await verification.OutboxMessages.ToListAsync());
    }

    [RequiresPostgresFact]
    public async Task GovernanceSaveAfterInvitationIssueReturnsStaleVersionAndRollsBackEffects()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        DateTimeOffset invitationIssuedAt = seed.Now.AddMinutes(1);
        await using TabrukDbContext governanceContext = database.CreateContext();
        await using TabrukDbContext invitationContext = database.CreateContext();
        PostgresGovernanceRepository governanceRepository = new(governanceContext);
        PostgresAuthenticationMembershipRepository invitationRepository = new(
            invitationContext,
            new MutableClock(invitationIssuedAt));

        Result<OrganizationAccountGovernance> loadedGovernance =
            await governanceRepository.GetAsync(seed.OrganizationId);
        Assert.True(
            loadedGovernance.IsSuccess,
            loadedGovernance.IsFailure ? loadedGovernance.Error.Message : null);

        Result invitationIssued = await invitationRepository.IssueInvitationAsync(
            new IssueMembershipInvitationPersistenceRequest(
                UserId.New(),
                MembershipId.New(),
                seed.OrganizationId,
                seed.ManagerMembershipId,
                "concurrent-invite@example.test",
                CreateInvitationToken(),
                "Pending invite",
                eligibleAsNamedParticipant: true,
                invitationIssuedAt,
                invitationIssuedAt.AddHours(24),
                MembershipAdministrationPersistenceEffects.Empty));
        Assert.True(invitationIssued.IsSuccess, invitationIssued.IsFailure ? invitationIssued.Error.Message : null);

        Result<FoodInchargeAssigned> assignment = loadedGovernance.Value.AssignFoodIncharge(
            loadedGovernance.Value.Memberships.Single(
                membership => membership.Id == seed.SecondAdministratorMembershipId),
            loadedGovernance.Value.Memberships.Single(
                membership => membership.Id == seed.MemberMembershipId),
            invitationIssuedAt.AddTicks(1));
        Assert.True(assignment.IsSuccess, assignment.IsFailure ? assignment.Error.Message : null);

        Result saved = await ExecuteWithinUnitOfWorkAsync(
            new PostgresUnitOfWork(governanceContext),
            token => governanceRepository.SaveAsync(
                loadedGovernance.Value,
                seed.SecondAdministratorMembershipId,
                invitationIssuedAt.AddTicks(1),
                Effects(seed, "governance-after-invite"),
                token));

        Assert.True(saved.IsFailure);
        Assert.Equal(ErrorCodes.StaleVersion, saved.Error.Code);

        await using TabrukDbContext verification = database.CreateContext();
        Assert.Equal(
            0,
            await verification.RoleAssignments.CountAsync(
                roleAssignment => roleAssignment.MembershipId == seed.MemberMembershipId.Value
                    && roleAssignment.Role == (short)OrganizationRole.FoodIncharge
                    && roleAssignment.RevokedAt == null));
        Assert.Empty(await verification.AuditEvents.ToListAsync());
        Assert.Empty(await verification.Notifications.ToListAsync());
        Assert.Empty(await verification.OutboxMessages.ToListAsync());
        Assert.Equal(4, await verification.Memberships.CountAsync());
        Assert.Equal(1, await verification.Invitations.CountAsync());
    }

    [RequiresPostgresFact]
    public async Task SignupWaitlistCompareAndSwapInsideUnitOfWorkRejectsStaleWriterAndRollsBackItsChildWrite()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await using TabrukDbContext firstContext = database.CreateContext();
        await using TabrukDbContext secondContext = database.CreateContext();
        PostgresSignupRepository firstRepository = new(firstContext);
        PostgresSignupRepository secondRepository = new(secondContext);

        Result<HelpNeedSignups> firstLoaded = await firstRepository.GetAsync(
            seed.OrganizationId,
            seed.HelpNeedId);
        Result<HelpNeedSignups> secondLoaded = await secondRepository.GetAsync(
            seed.OrganizationId,
            seed.HelpNeedId);
        Assert.True(firstLoaded.IsSuccess, firstLoaded.IsFailure ? firstLoaded.Error.Message : null);
        Assert.True(secondLoaded.IsSuccess, secondLoaded.IsFailure ? secondLoaded.Error.Message : null);

        Result<SignupSubmitted> firstSubmitted = firstLoaded.Value.Submit(
            SignupId.New(),
            PersistenceSeed.ActiveMembership(seed.MemberMembershipId, seed.OrganizationId, "Member"),
            SignupKind.Individual,
            [],
            unnamedParticipantCount: 0,
            seed.Now);
        Assert.True(firstSubmitted.IsSuccess, firstSubmitted.IsFailure ? firstSubmitted.Error.Message : null);
        Result<SignupTransitioned> firstWaitlisted = firstLoaded.Value.Waitlist(
            firstSubmitted.Value.SignupId,
            seed.Now.AddTicks(1));
        Assert.True(
            firstWaitlisted.IsSuccess,
            firstWaitlisted.IsFailure ? firstWaitlisted.Error.Message : null);
        Result firstSaved = await firstRepository.SaveAsync(firstLoaded.Value);
        Assert.True(firstSaved.IsSuccess, firstSaved.IsFailure ? firstSaved.Error.Message : null);

        Result<SignupSubmitted> secondSubmitted = secondLoaded.Value.Submit(
            SignupId.New(),
            PersistenceSeed.ActiveMembership(seed.MemberMembershipId, seed.OrganizationId, "Member"),
            SignupKind.Individual,
            [],
            unnamedParticipantCount: 0,
            seed.Now);
        Assert.True(secondSubmitted.IsSuccess, secondSubmitted.IsFailure ? secondSubmitted.Error.Message : null);
        Result<SignupTransitioned> secondWaitlisted = secondLoaded.Value.Waitlist(
            secondSubmitted.Value.SignupId,
            seed.Now.AddTicks(1));
        Assert.True(
            secondWaitlisted.IsSuccess,
            secondWaitlisted.IsFailure ? secondWaitlisted.Error.Message : null);
        Result secondSaved = await ExecuteWithinUnitOfWorkAsync(
            new PostgresUnitOfWork(secondContext),
            token => secondRepository.SaveAsync(secondLoaded.Value, token));

        Assert.True(secondSaved.IsFailure);
        Assert.Equal(ErrorCodes.StaleVersion, secondSaved.Error.Code);

        await using TabrukDbContext verification = database.CreateContext();
        Assert.Equal(
            1,
            await verification.Signups.CountAsync(signup => signup.HelpNeedId == seed.HelpNeedId.Value));
        HelpNeedEntity helpNeed = await verification.HelpNeeds.SingleAsync(
            need => need.Id == seed.HelpNeedId.Value);
        Assert.Equal(2, helpNeed.SignupVersion);
        Assert.Equal(1, helpNeed.WaitlistOrderHighWater);
        SignupEntity signup = await verification.Signups.SingleAsync(
            candidate => candidate.HelpNeedId == seed.HelpNeedId.Value);
        Assert.Equal((short)SignupStatus.Waitlisted, signup.Status);
        Assert.Equal(1, signup.WaitlistOrder);
    }

    [RequiresPostgresFact]
    public async Task SignupSaveRejectsAnOpenAggregateAfterItsCanonicalDateAndNeedClose()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await using TabrukDbContext writerContext = database.CreateContext();
        PostgresSignupRepository repository = new(writerContext);
        Result<HelpNeedSignups> loaded = await repository.GetAsync(
            seed.OrganizationId,
            seed.HelpNeedId);
        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : null);

        Result<SignupSubmitted> submitted = loaded.Value.Submit(
            SignupId.New(),
            PersistenceSeed.ActiveMembership(seed.MemberMembershipId, seed.OrganizationId, "Member"),
            SignupKind.Individual,
            [],
            unnamedParticipantCount: 0,
            seed.Now);
        Assert.True(submitted.IsSuccess, submitted.IsFailure ? submitted.Error.Message : null);

        await CloseCanonicalDateAndNeedAsync(database, seed);

        Result saved = await repository.SaveAsync(loaded.Value);

        Assert.True(saved.IsFailure);
        Assert.Equal(ErrorCodes.CategoryClosed, saved.Error.Code);
        await using TabrukDbContext verification = database.CreateContext();
        HelpNeedEntity helpNeed = await verification.HelpNeeds.SingleAsync(
            need => need.Id == seed.HelpNeedId.Value);
        Assert.Equal((short)HelpNeedStatus.Closed, helpNeed.Status);
        Assert.Equal(0, helpNeed.SignupVersion);
        Assert.Equal(0, helpNeed.WaitlistOrderHighWater);
        Assert.Empty(
            await verification.Signups.Where(signup => signup.HelpNeedId == seed.HelpNeedId.Value).ToListAsync());
    }

    [RequiresPostgresFact]
    public async Task SignupSavePersistsNewValidatedLabelAndPreservesStoredLabel()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        SignupId existingSignupId = SignupId.New();
        SignupId newSignupId = SignupId.New();
        const string existingLabel = "Household Group";
        string newLabel = string.Join(
            ' ',
            Enumerable.Repeat("Food", 15).Append("Group"));
        await using (TabrukDbContext setup = database.CreateContext())
        {
            setup.Signups.Add(
                new SignupEntity
                {
                    Id = existingSignupId.Value,
                    OrganizationId = seed.OrganizationId.Value,
                    ServiceDateId = seed.ServiceDateId.Value,
                    HelpNeedId = seed.HelpNeedId.Value,
                    PrimaryMembershipId = seed.MemberMembershipId.Value,
                    Kind = (short)SignupKind.Household,
                    Label = existingLabel,
                    UnnamedParticipantCount = 1,
                    Status = (short)SignupStatus.Pending,
                    SubmittedAt = seed.Now,
                    Version = 0,
                });
            await setup.SaveChangesAsync();
        }

        await using TabrukDbContext context = database.CreateContext();
        PostgresSignupRepository repository = new(context);
        Result<HelpNeedSignups> loaded = await repository.GetAsync(
            seed.OrganizationId,
            seed.HelpNeedId);
        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : null);

        Result<SignupTransitioned> approved = loaded.Value.Approve(
            existingSignupId,
            seed.Now.AddMinutes(1));
        Assert.True(approved.IsSuccess, approved.IsFailure ? approved.Error.Message : null);
        Result<SignupSubmitted> submitted = loaded.Value.Submit(
            newSignupId,
            PersistenceSeed.ActiveMembership(seed.ManagerMembershipId, seed.OrganizationId, "Manager"),
            SignupKind.Household,
            [],
            unnamedParticipantCount: 1,
            seed.Now.AddMinutes(2));
        Assert.True(submitted.IsSuccess, submitted.IsFailure ? submitted.Error.Message : null);

        Result saved = await repository.SaveAsync(
            loaded.Value,
            [new NewSignupLabel(newSignupId, newLabel)]);

        Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error.Message : null);

        await using TabrukDbContext verification = database.CreateContext();
        SignupEntity updated = await verification.Signups.SingleAsync(
            candidate => candidate.Id == existingSignupId.Value);
        SignupEntity inserted = await verification.Signups.SingleAsync(
            candidate => candidate.Id == newSignupId.Value);
        HelpNeedEntity helpNeed = await verification.HelpNeeds.SingleAsync(
            candidate => candidate.Id == seed.HelpNeedId.Value);

        Assert.Equal(existingLabel, updated.Label);
        Assert.Equal((short)SignupStatus.Approved, updated.Status);
        Assert.Equal(ApplicationLimits.MaximumSignupLabelUnicodeScalars, newLabel.EnumerateRunes().Count());
        Assert.True(Encoding.UTF8.GetByteCount(newLabel) <= ApplicationLimits.MaximumSignupLabelUtf8Bytes);
        Assert.Equal(newLabel, inserted.Label);
        Assert.Equal((short)SignupStatus.Pending, inserted.Status);
        Assert.Equal(2, helpNeed.SignupVersion);
    }

    [RequiresPostgresFact]
    public async Task SignupSaveRejectsOversizedNewLabelBeforeIssuingSql()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await using (TabrukDbContext loadingContext = database.CreateContext())
        {
            PostgresSignupRepository loadingRepository = new(loadingContext);
            Result<HelpNeedSignups> loaded = await loadingRepository.GetAsync(
                seed.OrganizationId,
                seed.HelpNeedId);
            Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : null);

            Result<SignupSubmitted> submitted = loaded.Value.Submit(
                SignupId.New(),
                PersistenceSeed.ActiveMembership(seed.MemberMembershipId, seed.OrganizationId, "Member"),
                SignupKind.Individual,
                [],
                unnamedParticipantCount: 0,
                seed.Now);
            Assert.True(submitted.IsSuccess, submitted.IsFailure ? submitted.Error.Message : null);

            SqlCommandCountingInterceptor interceptor = new();
            DbContextOptions<TabrukDbContext> options = new DbContextOptionsBuilder<TabrukDbContext>()
                .UseNpgsql(database.ConnectionString)
                .AddInterceptors(interceptor)
                .Options;
            await using TabrukDbContext savingContext = new(options);
            Result saved = await new PostgresSignupRepository(savingContext).SaveAsync(
                loaded.Value,
                [
                    new NewSignupLabel(
                        submitted.Value.SignupId,
                        new string('x', ApplicationLimits.MaximumSignupLabelUnicodeScalars + 1)),
                ]);

            Assert.True(saved.IsFailure);
            Assert.Equal(ErrorCodes.PayloadTooLarge, saved.Error.Code);
            Assert.Equal(0, interceptor.CommandsExecuted);
        }

        await using TabrukDbContext verification = database.CreateContext();
        HelpNeedEntity helpNeed = await verification.HelpNeeds.SingleAsync(
            candidate => candidate.Id == seed.HelpNeedId.Value);
        Assert.Equal(0, helpNeed.SignupVersion);
        Assert.Empty(
            await verification.Signups.Where(
                candidate => candidate.HelpNeedId == seed.HelpNeedId.Value).ToListAsync());
    }

    [RequiresPostgresFact]
    public async Task SignupSaveRejectsNonCanonicalOrIdentifyingLabelBeforeIssuingSql()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await using TabrukDbContext loadingContext = database.CreateContext();
        PostgresSignupRepository loadingRepository = new(loadingContext);
        Result<HelpNeedSignups> loaded = await loadingRepository.GetAsync(
            seed.OrganizationId,
            seed.HelpNeedId);
        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : null);

        Result<SignupSubmitted> submitted = loaded.Value.Submit(
            SignupId.New(),
            PersistenceSeed.ActiveMembership(seed.MemberMembershipId, seed.OrganizationId, "Member"),
            SignupKind.Team,
            [],
            unnamedParticipantCount: 1,
            seed.Now);
        Assert.True(submitted.IsSuccess, submitted.IsFailure ? submitted.Error.Message : null);

        string[] invalidLabels =
        [
            "Fatima Ali",
            "fatima@example.test",
            "food",
            "Fоod",
            "Food-Team",
            "Food  Team",
            "Food 010",
            " Food ",
        ];
        foreach (string invalidLabel in invalidLabels)
        {
            SqlCommandCountingInterceptor interceptor = new();
            DbContextOptions<TabrukDbContext> options =
                new DbContextOptionsBuilder<TabrukDbContext>()
                    .UseNpgsql(database.ConnectionString)
                    .AddInterceptors(interceptor)
                    .Options;
            await using TabrukDbContext savingContext = new(options);

            Result saved = await new PostgresSignupRepository(savingContext).SaveAsync(
                loaded.Value,
                [new NewSignupLabel(submitted.Value.SignupId, invalidLabel)]);

            Assert.True(saved.IsFailure);
            Assert.Equal(SignupErrorCodes.InvalidSignupInput, saved.Error.Code);
            Assert.Equal(0, interceptor.CommandsExecuted);
        }

        await using TabrukDbContext verification = database.CreateContext();
        HelpNeedEntity helpNeed = await verification.HelpNeeds.SingleAsync(
            candidate => candidate.Id == seed.HelpNeedId.Value);
        Assert.Equal(0, helpNeed.SignupVersion);
        Assert.Empty(
            await verification.Signups.Where(
                candidate => candidate.HelpNeedId == seed.HelpNeedId.Value).ToListAsync());
    }

    [RequiresPostgresFact]
    public async Task SignupGetAsyncUsesOneSnapshotWhenConcurrentSignupCommitsAfterRootRead()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        ConcurrentSignupCommitAfterHelpNeedRootReadInterceptor interceptor = new(
            cancellationToken => CommitConcurrentSignupAsync(database, seed, cancellationToken));
        DbContextOptions<TabrukDbContext> options = new DbContextOptionsBuilder<TabrukDbContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using TabrukDbContext readerContext = new(options);
        Result<HelpNeedSignups> loaded = await new PostgresSignupRepository(readerContext).GetAsync(
            seed.OrganizationId,
            seed.HelpNeedId);

        Assert.True(interceptor.ConcurrentSignupCommitted);
        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : null);
        Assert.Empty(loaded.Value.Signups);
        Assert.Equal(0, loaded.Value.OriginalVersion);
        Assert.Equal(0, loaded.Value.Version);

        await using TabrukDbContext verificationContext = database.CreateContext();
        HelpNeedEntity committedNeed = await verificationContext.HelpNeeds.SingleAsync(
            candidate => candidate.Id == seed.HelpNeedId.Value);
        Assert.Equal(1, committedNeed.SignupVersion);
        Assert.Equal(
            1,
            await verificationContext.Signups.CountAsync(
                candidate => candidate.HelpNeedId == seed.HelpNeedId.Value));
    }

    [RequiresPostgresFact]
    public async Task SignupGetAsyncRetriesToCoherentVersionWhenAmbientUnitOfWorkIsReadCommitted()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        ConcurrentSignupCommitAfterHelpNeedRootReadInterceptor interceptor = new(
            cancellationToken => CommitConcurrentSignupAsync(database, seed, cancellationToken));
        DbContextOptions<TabrukDbContext> options = new DbContextOptionsBuilder<TabrukDbContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using TabrukDbContext readerContext = new(options);
        Result<HelpNeedSignups> loaded = await new PostgresUnitOfWork(readerContext).ExecuteAsync(
            cancellationToken => new PostgresSignupRepository(readerContext).GetAsync(
                seed.OrganizationId,
                seed.HelpNeedId,
                cancellationToken));

        Assert.True(interceptor.ConcurrentSignupCommitted);
        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : null);
        Assert.Single(loaded.Value.Signups);
        Assert.Equal(1, loaded.Value.OriginalVersion);
        Assert.Equal(1, loaded.Value.Version);
    }

    [RequiresPostgresFact]
    public async Task GovernanceGetAsyncUsesOneSnapshotWhenConcurrentAuthorityChangeCommitsAfterRootRead()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        ConcurrentGovernanceCommitAfterOrganizationRootReadInterceptor interceptor = new(
            cancellationToken => CommitConcurrentGovernanceAsync(database, seed, cancellationToken));
        DbContextOptions<TabrukDbContext> options = new DbContextOptionsBuilder<TabrukDbContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using TabrukDbContext readerContext = new(options);
        Result<OrganizationAccountGovernance> loaded =
            await new PostgresGovernanceRepository(readerContext).GetAsync(seed.OrganizationId);

        Assert.True(interceptor.ConcurrentGovernanceCommitted);
        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : null);
        Assert.Equal(0, loaded.Value.Version);
        Assert.False(
            loaded.Value.Memberships
                .Single(membership => membership.Id == seed.MemberMembershipId)
                .HasRole(OrganizationRole.FoodIncharge));

        await using TabrukDbContext verificationContext = database.CreateContext();
        OrganizationEntity committedOrganization = await verificationContext.Organizations.SingleAsync(
            candidate => candidate.Id == seed.OrganizationId.Value);
        Assert.Equal(1, committedOrganization.Version);
        Assert.Equal(
            1,
            await verificationContext.RoleAssignments.CountAsync(
                assignment => assignment.OrganizationId == seed.OrganizationId.Value
                    && assignment.MembershipId == seed.MemberMembershipId.Value
                    && assignment.Role == (short)OrganizationRole.FoodIncharge
                    && assignment.RevokedAt == null));
    }

    [RequiresPostgresFact]
    public async Task GovernanceGetAsyncRetriesToCoherentVersionWhenAmbientUnitOfWorkIsReadCommitted()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        ConcurrentGovernanceCommitAfterOrganizationRootReadInterceptor interceptor = new(
            cancellationToken => CommitConcurrentGovernanceAsync(database, seed, cancellationToken));
        DbContextOptions<TabrukDbContext> options = new DbContextOptionsBuilder<TabrukDbContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using TabrukDbContext readerContext = new(options);
        Result<OrganizationAccountGovernance> loaded = await new PostgresUnitOfWork(readerContext).ExecuteAsync(
            cancellationToken => new PostgresGovernanceRepository(readerContext).GetAsync(
                seed.OrganizationId,
                cancellationToken));

        Assert.True(interceptor.ConcurrentGovernanceCommitted);
        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : null);
        Assert.Equal(1, loaded.Value.Version);
        Assert.True(
            loaded.Value.Memberships
                .Single(membership => membership.Id == seed.MemberMembershipId)
                .HasRole(OrganizationRole.FoodIncharge));
    }

    [RequiresPostgresFact]
    public async Task GovernanceAmbientReadStopsAfterThreeVersionMismatches()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        GovernanceChurnAfterEachInitialRootReadInterceptor interceptor = new(
            cancellationToken => IncrementGovernanceVersionAsync(database, seed, cancellationToken));
        DbContextOptions<TabrukDbContext> options = new DbContextOptionsBuilder<TabrukDbContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using TabrukDbContext readerContext = new(options);
        Result<OrganizationAccountGovernance> loaded = await new PostgresUnitOfWork(readerContext).ExecuteAsync(
            cancellationToken => new PostgresGovernanceRepository(readerContext).GetAsync(
                seed.OrganizationId,
                cancellationToken));

        Assert.True(loaded.IsFailure);
        Assert.Equal(ErrorType.Conflict, loaded.Error.Type);
        Assert.Equal("governance_read_conflict", loaded.Error.Code);
        Assert.Equal(
            "The organization governance changed while it was being read. Please retry.",
            loaded.Error.Message);
        Assert.Equal(3, interceptor.InitialRootReadsChanged);
        Assert.Equal(6, interceptor.OrganizationRootReads);
    }

    [RequiresPostgresFact]
    public async Task GovernanceOwnedReadCancellationIsNotRetriedOrTranslated()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        using CancellationTokenSource cancellation = new();
        CancelAfterOrganizationRootReadInterceptor interceptor = new(cancellation);
        DbContextOptions<TabrukDbContext> options = new DbContextOptionsBuilder<TabrukDbContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using TabrukDbContext readerContext = new(options);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new PostgresGovernanceRepository(readerContext)
                .GetAsync(seed.OrganizationId, cancellation.Token)
                .AsTask());

        Assert.Equal(1, interceptor.OrganizationRootReads);
    }

    [RequiresPostgresFact]
    public async Task SignupHydrationUsesCanonicalContextAndRetainsHighWaterAboveCurrentMaximum()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        SignupId pendingSignupId = SignupId.New();
        SignupId waitlistedSignupId = SignupId.New();
        await using (TabrukDbContext setup = database.CreateContext())
        {
            HelpNeedEntity helpNeed = await setup.HelpNeeds.SingleAsync(
                need => need.Id == seed.HelpNeedId.Value);
            helpNeed.WaitlistOrderHighWater = 5;
            setup.Signups.AddRange(
                new SignupEntity
                {
                    Id = pendingSignupId.Value,
                    OrganizationId = seed.OrganizationId.Value,
                    ServiceDateId = seed.ServiceDateId.Value,
                    HelpNeedId = seed.HelpNeedId.Value,
                    PrimaryMembershipId = seed.MemberMembershipId.Value,
                    Kind = (short)SignupKind.Individual,
                    UnnamedParticipantCount = 0,
                    Status = (short)SignupStatus.Pending,
                    SubmittedAt = seed.Now,
                    Version = 0,
                },
                new SignupEntity
                {
                    Id = waitlistedSignupId.Value,
                    OrganizationId = seed.OrganizationId.Value,
                    ServiceDateId = seed.ServiceDateId.Value,
                    HelpNeedId = seed.HelpNeedId.Value,
                    PrimaryMembershipId = seed.ManagerMembershipId.Value,
                    Kind = (short)SignupKind.Individual,
                    UnnamedParticipantCount = 0,
                    Status = (short)SignupStatus.Waitlisted,
                    SubmittedAt = seed.Now,
                    LastTransitionAt = seed.Now,
                    WaitlistOrder = 2,
                    Version = 1,
                });
            await setup.SaveChangesAsync();
        }

        await using TabrukDbContext context = database.CreateContext();
        Result<HelpNeedSignups> hydrated =
            await new PostgresSignupRepository(context).GetAsync(seed.OrganizationId, seed.HelpNeedId);

        Assert.True(hydrated.IsSuccess, hydrated.IsFailure ? hydrated.Error.Message : null);
        Assert.Equal(seed.OrganizationId, hydrated.Value.OrganizationId);
        Assert.Equal(seed.ServiceDateId, hydrated.Value.ServiceDateId);
        Assert.Equal(seed.HelpNeedId, hydrated.Value.HelpNeedId);
        Assert.Equal(5, hydrated.Value.WaitlistOrderHighWater);
        Assert.Equal(
            new[] { pendingSignupId, waitlistedSignupId }
                .OrderBy(id => id.Value)
                .ToArray(),
            hydrated.Value.Signups.Select(signup => signup.Id).OrderBy(id => id.Value).ToArray());
        Signup waitlisted = Assert.Single(hydrated.Value.OrderedWaitlist());
        Assert.Equal(waitlistedSignupId, waitlisted.Id);
        Assert.Equal(2, waitlisted.WaitlistOrder);
    }

    [RequiresPostgresFact]
    public async Task SignupHydrationRejectsSchemaValidButDomainInvalidHelpNeedWithoutThrowing()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await using (TabrukDbContext corruptingContext = database.CreateContext())
        {
            HelpNeedEntity helpNeed = await corruptingContext.HelpNeeds.SingleAsync(
                need => need.Id == seed.HelpNeedId.Value);
            helpNeed.Instructions = string.Empty;
            await corruptingContext.SaveChangesAsync();
        }

        await using TabrukDbContext context = database.CreateContext();
        Result<HelpNeedSignups> hydrated =
            await new PostgresSignupRepository(context).GetAsync(seed.OrganizationId, seed.HelpNeedId);

        Assert.True(hydrated.IsFailure);
    }

    [RequiresPostgresFact]
    public async Task SignupHydrationReturnsControlledFailureForRawSqlGuidEmptyCanonicalHelpNeed()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await using (NpgsqlConnection connection = new(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand command =
                new(
                    """
                    INSERT INTO help_needs
                        (id, organization_id, service_date_id, category, instructions, capacity, status, version,
                         signup_version, waitlist_order_high_water)
                    VALUES
                        (@id, @organizationId, @serviceDateId, @category, @instructions, @capacity, @status,
                         @version, @signupVersion, @waitlistOrderHighWater)
                    """,
                    connection);
            command.Parameters.AddWithValue("id", Guid.Empty);
            command.Parameters.AddWithValue("organizationId", seed.OrganizationId.Value);
            command.Parameters.AddWithValue("serviceDateId", seed.ServiceDateId.Value);
            command.Parameters.AddWithValue("category", (short)HelpCategory.Cleanup);
            command.Parameters.AddWithValue("instructions", "Schema-valid corrupted canonical help need.");
            command.Parameters.AddWithValue("capacity", 1);
            command.Parameters.AddWithValue("status", (short)HelpNeedStatus.Open);
            command.Parameters.AddWithValue("version", 0L);
            command.Parameters.AddWithValue("signupVersion", 0L);
            command.Parameters.AddWithValue("waitlistOrderHighWater", 0L);
            await command.ExecuteNonQueryAsync();
        }

        await using TabrukDbContext context = database.CreateContext();
        Result<HelpNeedSignups> hydrated =
            await new PostgresSignupRepository(context).GetAsync(seed.OrganizationId, seed.HelpNeedId);

        Assert.True(hydrated.IsFailure);
        Assert.Equal(SignupErrorCodes.InvalidSignupAggregateState, hydrated.Error.Code);
    }

    [RequiresPostgresFact]
    public async Task SignupHydrationReturnsControlledFailureForRawSqlGuidEmptyCanonicalServiceDate()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await using (NpgsqlConnection connection = new(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand createDate =
                new(
                    """
                    INSERT INTO service_dates
                        (id, organization_id, title, instructions, starts_at, ends_at, cancellation_deadline_at,
                         manager_membership_id, status, version)
                    VALUES
                        (@id, @organizationId, @title, @instructions, @startsAt, @endsAt,
                         @cancellationDeadlineAt, @managerMembershipId, @status, @version)
                    """,
                    connection);
            createDate.Parameters.AddWithValue("id", Guid.Empty);
            createDate.Parameters.AddWithValue("organizationId", seed.OrganizationId.Value);
            createDate.Parameters.AddWithValue("title", "Schema-valid corrupted canonical date");
            createDate.Parameters.AddWithValue("instructions", "This row exists only for hydration regression coverage.");
            createDate.Parameters.AddWithValue("startsAt", seed.Now.AddDays(2));
            createDate.Parameters.AddWithValue("endsAt", seed.Now.AddDays(2).AddHours(4));
            createDate.Parameters.AddWithValue("cancellationDeadlineAt", seed.Now.AddDays(1));
            createDate.Parameters.AddWithValue("managerMembershipId", seed.ManagerMembershipId.Value);
            createDate.Parameters.AddWithValue("status", (short)ServiceDateStatus.Open);
            createDate.Parameters.AddWithValue("version", 0L);
            await createDate.ExecuteNonQueryAsync();

            await using NpgsqlCommand corruptNeed =
                new(
                    "UPDATE help_needs SET service_date_id = @serviceDateId WHERE id = @helpNeedId",
                    connection);
            corruptNeed.Parameters.AddWithValue("serviceDateId", Guid.Empty);
            corruptNeed.Parameters.AddWithValue("helpNeedId", seed.HelpNeedId.Value);
            await corruptNeed.ExecuteNonQueryAsync();
        }

        await using TabrukDbContext context = database.CreateContext();
        Result<HelpNeedSignups> hydrated =
            await new PostgresSignupRepository(context).GetAsync(seed.OrganizationId, seed.HelpNeedId);

        Assert.True(hydrated.IsFailure);
        Assert.Equal(SignupErrorCodes.InvalidSignupAggregateState, hydrated.Error.Code);
    }

    [RequiresPostgresFact]
    public async Task TenantScopedRepositoriesConcealOtherOrganizationRoots()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        OrganizationId otherOrganizationId = OrganizationId.New();
        NotificationId notificationId = NotificationId.New();
        await using (TabrukDbContext setup = database.CreateContext())
        {
            setup.Organizations.Add(
                new OrganizationEntity
                {
                    Id = otherOrganizationId.Value,
                    Name = "Other organization",
                    TimeZone = "America/Los_Angeles",
                    DefaultCancellationLeadMinutes = 60,
                    Status = 0,
                    BootstrapStatus = (short)AdministratorBootstrapStatus.Sealed,
                    BootstrapSealedAt = seed.Now,
                    Version = 0,
                });
            setup.Notifications.Add(
                new NotificationEntity
                {
                    Id = notificationId.Value,
                    OrganizationId = seed.OrganizationId.Value,
                    RecipientMembershipId = seed.MemberMembershipId.Value,
                    Type = 1,
                    ResourceType = 1,
                    ResourceId = seed.ThreadId.Value,
                    Title = "Scoped notification",
                    Body = "This notification belongs only to the seeded organization.",
                    CreatedAt = seed.Now,
                });
            await setup.SaveChangesAsync();
        }

        await using TabrukDbContext context = database.CreateContext();
        Result<HelpNeedSignups> signup = await new PostgresSignupRepository(context).GetAsync(
            otherOrganizationId,
            seed.HelpNeedId);
        Result<LoadedDateThread> thread = await new PostgresThreadRepository(context).GetAsync(
            otherOrganizationId,
            seed.ThreadId);
        Result<Notification> notification = await new PostgresNotificationRepository(context).GetAsync(
            otherOrganizationId,
            seed.MemberMembershipId,
            notificationId);

        Assert.True(signup.IsFailure);
        Assert.True(thread.IsFailure);
        Assert.True(notification.IsFailure);
        Assert.Equal("help_need_not_found", signup.Error.Code);
        Assert.Equal("thread_not_found", thread.Error.Code);
        Assert.Equal("notification_not_found", notification.Error.Code);
    }

    [RequiresPostgresFact]
    public async Task NotificationReadRequiresTheExactRecipientWithinTheOrganization()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        NotificationId notificationId = NotificationId.New();
        await using (TabrukDbContext setup = database.CreateContext())
        {
            setup.Notifications.Add(
                new NotificationEntity
                {
                    Id = notificationId.Value,
                    OrganizationId = seed.OrganizationId.Value,
                    RecipientMembershipId = seed.MemberMembershipId.Value,
                    Type = 1,
                    ResourceType = 1,
                    ResourceId = seed.ThreadId.Value,
                    Title = "Recipient-scoped notification",
                    Body = "Only the intended recipient may load this row.",
                    CreatedAt = seed.Now,
                });
            await setup.SaveChangesAsync();
        }

        await using TabrukDbContext context = database.CreateContext();
        PostgresNotificationRepository repository = new(context);
        Result<Notification> permitted = await repository.GetAsync(
            seed.OrganizationId,
            seed.MemberMembershipId,
            notificationId);
        Result<Notification> concealed = await repository.GetAsync(
            seed.OrganizationId,
            seed.ManagerMembershipId,
            notificationId);

        Assert.True(permitted.IsSuccess, permitted.IsFailure ? permitted.Error.Message : null);
        Assert.Equal(seed.MemberMembershipId, permitted.Value.RecipientMembershipId);
        Assert.True(concealed.IsFailure);
        Assert.Equal(ErrorType.NotFound, concealed.Error.Type);
        Assert.Equal("notification_not_found", concealed.Error.Code);
        Assert.Equal("The notification was not found.", concealed.Error.Message);
    }

    [RequiresPostgresFact]
    public async Task GovernanceHydrationIncludesEverySameOrganizationMembershipAndRoleRequest()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        RoleChangeRequestId requestId = RoleChangeRequestId.New();
        OrganizationId otherOrganizationId = OrganizationId.New();
        MembershipId otherMembershipId = MembershipId.New();
        await using (TabrukDbContext setup = database.CreateContext())
        {
            Guid otherUserId = Guid.CreateVersion7();
            setup.Users.Add(
                new TabrukIdentityUser
                {
                    Id = otherUserId,
                    UserName = "other-governance@example.test",
                    NormalizedUserName = "OTHER-GOVERNANCE@EXAMPLE.TEST",
                    Email = "other-governance@example.test",
                    NormalizedEmail = "OTHER-GOVERNANCE@EXAMPLE.TEST",
                    SecurityStamp = Guid.NewGuid().ToString("N"),
                    ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                });
            setup.Organizations.Add(
                new OrganizationEntity
                {
                    Id = otherOrganizationId.Value,
                    Name = "Other governance organization",
                    TimeZone = "America/Los_Angeles",
                    DefaultCancellationLeadMinutes = 60,
                    Status = 0,
                    BootstrapStatus = (short)AdministratorBootstrapStatus.Sealed,
                    BootstrapSealedAt = seed.Now,
                    Version = 0,
                });
            setup.Memberships.Add(
                new MembershipEntity
                {
                    Id = otherMembershipId.Value,
                    OrganizationId = otherOrganizationId.Value,
                    UserId = otherUserId,
                    DisplayName = "Other governance member",
                    Status = (short)MembershipStatus.Active,
                    EligibleAsNamedParticipant = true,
                });
            setup.RoleChangeRequests.Add(
                new RoleChangeRequestEntity
                {
                    Id = requestId.Value,
                    OrganizationId = seed.OrganizationId.Value,
                    TargetMembershipId = seed.MemberMembershipId.Value,
                    Action = (short)AdministratorRoleChangeAction.Grant,
                    ProposerMembershipId = seed.ManagerMembershipId.Value,
                    Reason = "Integration hydration coverage.",
                    ProposedAt = seed.Now,
                    ExpiresAt = seed.Now.AddHours(24),
                    Status = (short)RoleChangeRequestStatus.Pending,
                });
            await setup.SaveChangesAsync();
        }

        await using TabrukDbContext context = database.CreateContext();
        Result<OrganizationAccountGovernance> hydrated =
            await new PostgresGovernanceRepository(context).GetAsync(seed.OrganizationId);

        Assert.True(hydrated.IsSuccess, hydrated.IsFailure ? hydrated.Error.Message : null);
        Assert.Equal(
            new[] { seed.ManagerMembershipId, seed.SecondAdministratorMembershipId, seed.MemberMembershipId }
                .OrderBy(id => id.Value)
                .ToArray(),
            hydrated.Value.Memberships.Select(membership => membership.Id).OrderBy(id => id.Value).ToArray());
        RoleChangeRequest request = Assert.Single(hydrated.Value.RoleChangeRequests);
        Assert.Equal(requestId, request.Id);
        Assert.Equal(seed.OrganizationId, request.OrganizationId);
        Assert.Equal(seed.MemberMembershipId, request.TargetMembershipId);
        Assert.Equal(seed.ManagerMembershipId, request.ProposerMembershipId);
        Assert.DoesNotContain(
            hydrated.Value.Memberships,
            membership => membership.Id == otherMembershipId);
    }

    private static async ValueTask<Result> ExecuteWithinUnitOfWorkAsync(
        PostgresUnitOfWork unitOfWork,
        Func<CancellationToken, ValueTask<Result>> operation)
    {
        Result<bool> result = await unitOfWork.ExecuteAsync(
            async cancellationToken =>
            {
                Result operationResult = await operation(cancellationToken);
                return operationResult.IsSuccess
                    ? Result.Success(true)
                    : Result.Failure<bool>(operationResult.Error);
            });
        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }

    private static async Task CloseCanonicalDateAndNeedAsync(
        PostgresTestDatabase database,
        PersistenceSeed seed)
    {
        await using TabrukDbContext context = database.CreateContext();
        ServiceDateEntity date = await context.ServiceDates.SingleAsync(
            candidate => candidate.Id == seed.ServiceDateId.Value);
        HelpNeedEntity need = await context.HelpNeeds.SingleAsync(
            candidate => candidate.Id == seed.HelpNeedId.Value);
        date.Status = (short)ServiceDateStatus.Closed;
        date.Version++;
        need.Status = (short)HelpNeedStatus.Closed;
        need.Version++;
        await context.SaveChangesAsync();
    }

    private static PersistenceEffects Effects(PersistenceSeed seed, string operation) =>
        new(
            [
                new NotificationEntity
                {
                    Id = NotificationId.New().Value,
                    OrganizationId = seed.OrganizationId.Value,
                    RecipientMembershipId = seed.MemberMembershipId.Value,
                    Type = 3,
                    ResourceType = 3,
                    ResourceId = seed.MemberMembershipId.Value,
                    Title = "Governance update",
                    Body = "An account role changed.",
                    CreatedAt = seed.Now,
                },
            ],
            [
                new AuditEntry(
                    AuditEventId.New(),
                    seed.OrganizationId,
                    seed.SecondAdministratorMembershipId,
                    operation,
                    "membership",
                    seed.MemberMembershipId.ToString(),
                    "integration test",
                    "governance",
                    Guid.NewGuid().ToString("N"),
                    beforeState: null,
                    afterState: null,
                    seed.Now),
            ],
            [],
            [
                new OutboxMessage(
                    OutboxMessageId.New(),
                    seed.OrganizationId,
                    "governance.updated",
                    "{}",
                    seed.Now),
            ]);

    private static async Task CommitConcurrentSignupAsync(
        PostgresTestDatabase database,
        PersistenceSeed seed,
        CancellationToken cancellationToken)
    {
        await using TabrukDbContext writerContext = database.CreateContext();
        PostgresSignupRepository writer = new(writerContext);
        Result<HelpNeedSignups> writerLoaded = await writer.GetAsync(
            seed.OrganizationId,
            seed.HelpNeedId,
            cancellationToken);
        Assert.True(writerLoaded.IsSuccess, writerLoaded.IsFailure ? writerLoaded.Error.Message : null);

        Result<SignupSubmitted> submitted = writerLoaded.Value.Submit(
            SignupId.New(),
            PersistenceSeed.ActiveMembership(seed.MemberMembershipId, seed.OrganizationId, "Member"),
            SignupKind.Individual,
            [],
            unnamedParticipantCount: 0,
            seed.Now);
        Assert.True(submitted.IsSuccess, submitted.IsFailure ? submitted.Error.Message : null);
        Result saved = await writer.SaveAsync(writerLoaded.Value, cancellationToken);
        Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error.Message : null);
    }

    private static async Task CommitConcurrentGovernanceAsync(
        PostgresTestDatabase database,
        PersistenceSeed seed,
        CancellationToken cancellationToken)
    {
        await using TabrukDbContext writerContext = database.CreateContext();
        PostgresGovernanceRepository writer = new(writerContext);
        Result<OrganizationAccountGovernance> writerLoaded = await writer.GetAsync(
            seed.OrganizationId,
            cancellationToken);
        Assert.True(writerLoaded.IsSuccess, writerLoaded.IsFailure ? writerLoaded.Error.Message : null);

        Result<FoodInchargeAssigned> assigned = writerLoaded.Value.AssignFoodIncharge(
            writerLoaded.Value.Memberships.Single(
                membership => membership.Id == seed.SecondAdministratorMembershipId),
            writerLoaded.Value.Memberships.Single(
                membership => membership.Id == seed.MemberMembershipId),
            seed.Now.AddMinutes(1));
        Assert.True(assigned.IsSuccess, assigned.IsFailure ? assigned.Error.Message : null);
        Result saved = await writer.SaveAsync(
            writerLoaded.Value,
            seed.SecondAdministratorMembershipId,
            seed.Now.AddMinutes(1),
            PersistenceEffects.Empty,
            cancellationToken);
        Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error.Message : null);
    }

    private static async Task IncrementGovernanceVersionAsync(
        PostgresTestDatabase database,
        PersistenceSeed seed,
        CancellationToken cancellationToken)
    {
        await using TabrukDbContext writerContext = database.CreateContext();
        await writerContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE organizations
            SET version = version + 1
            WHERE id = {seed.OrganizationId.Value}
            """,
            cancellationToken);
    }

    private sealed class ConcurrentSignupCommitAfterHelpNeedRootReadInterceptor(
        Func<CancellationToken, Task> commitConcurrentSignup) : DbCommandInterceptor
    {
        private int rootReadIntercepted;
        private int concurrentSignupCommitted;

        public bool ConcurrentSignupCommitted => Volatile.Read(ref concurrentSignupCommitted) == 1;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("FROM help_needs", StringComparison.Ordinal)
                || Interlocked.Exchange(ref rootReadIntercepted, 1) != 0)
            {
                return result;
            }

            await commitConcurrentSignup(cancellationToken);
            Volatile.Write(ref concurrentSignupCommitted, 1);
            return result;
        }
    }

    private sealed class ConcurrentGovernanceCommitAfterOrganizationRootReadInterceptor(
        Func<CancellationToken, Task> commitConcurrentGovernance) : DbCommandInterceptor
    {
        private int rootReadIntercepted;
        private int concurrentGovernanceCommitted;

        public bool ConcurrentGovernanceCommitted =>
            Volatile.Read(ref concurrentGovernanceCommitted) == 1;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("FROM organizations", StringComparison.Ordinal)
                || Interlocked.Exchange(ref rootReadIntercepted, 1) != 0)
            {
                return result;
            }

            await commitConcurrentGovernance(cancellationToken);
            Volatile.Write(ref concurrentGovernanceCommitted, 1);
            return result;
        }
    }

    private sealed class GovernanceChurnAfterEachInitialRootReadInterceptor(
        Func<CancellationToken, Task> incrementGovernanceVersion) : DbCommandInterceptor
    {
        private int organizationRootReads;
        private int initialRootReadsChanged;

        public int OrganizationRootReads => Volatile.Read(ref organizationRootReads);
        public int InitialRootReadsChanged => Volatile.Read(ref initialRootReadsChanged);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("FROM organizations", StringComparison.Ordinal))
            {
                return result;
            }

            int readNumber = Interlocked.Increment(ref organizationRootReads);
            if ((readNumber & 1) == 1 && Volatile.Read(ref initialRootReadsChanged) < 3)
            {
                await incrementGovernanceVersion(cancellationToken);
                Interlocked.Increment(ref initialRootReadsChanged);
            }

            return result;
        }
    }

    private sealed class CancelAfterOrganizationRootReadInterceptor(
        CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        private int organizationRootReads;

        public int OrganizationRootReads => Volatile.Read(ref organizationRootReads);

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM organizations", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref organizationRootReads);
                cancellation.Cancel();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class SqlCommandCountingInterceptor : DbCommandInterceptor
    {
        private int commandsExecuted;

        public int CommandsExecuted => Volatile.Read(ref commandsExecuted);

        public override DbDataReader ReaderExecuted(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result)
        {
            Interlocked.Increment(ref commandsExecuted);
            return result;
        }

        public override int NonQueryExecuted(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result)
        {
            Interlocked.Increment(ref commandsExecuted);
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref commandsExecuted);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref commandsExecuted);
            return ValueTask.FromResult(result);
        }
    }

    private static string CreateInvitationToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(48);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
