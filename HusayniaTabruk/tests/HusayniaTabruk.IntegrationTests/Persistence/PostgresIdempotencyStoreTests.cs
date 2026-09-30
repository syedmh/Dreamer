using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Infrastructure.Identity.Entities;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using HusayniaTabruk.Infrastructure.Persistence.Repositories;

namespace HusayniaTabruk.IntegrationTests.Persistence;

[Trait("Category", "Persistence")]
public sealed class PostgresIdempotencyStoreTests : PostgresPersistenceTest
{
    [RequiresPostgresFact]
    public async Task ProcessingReceiptCompletesAndReadsBackItsResultReference()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        IdempotencyCreateRequest createRequest = CreateRequest(seed);
        IdempotencyRequest transitionRequest = new(
            createRequest.OrganizationId,
            createRequest.MembershipId,
            createRequest.Key,
            createRequest.Operation,
            createRequest.RequestFingerprint);
        await using TabrukDbContext context = database.CreateContext();
        PostgresIdempotencyStore store = new(context);

        IdempotencyCreateResult created = await store.TryCreateProcessingAsync(createRequest);
        IdempotencyTransitionResult completed = await store.TryCompleteAsync(
            transitionRequest,
            "signup-123");
        IdempotencyReceipt? readBack = await store.FindAsync(
            seed.OrganizationId,
            seed.MemberMembershipId,
            createRequest.Key);

        Assert.Equal(IdempotencyCreateOutcome.Created, created.Outcome);
        Assert.Equal(IdempotencyStatus.Processing, created.Receipt.Status);
        Assert.Equal(IdempotencyTransitionOutcome.Completed, completed.Outcome);
        Assert.NotNull(completed.Receipt);
        Assert.Equal(IdempotencyStatus.Completed, completed.Receipt!.Status);
        Assert.Equal("signup-123", completed.Receipt.ResultReference);
        Assert.Equal(completed.Receipt, readBack);
    }

    [RequiresPostgresFact]
    public async Task CreateRejectsRequestMismatchAndExpiryWithoutReplacingTheStoredReceipt()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        IdempotencyCreateRequest original = CreateRequest(seed);
        await using TabrukDbContext context = database.CreateContext();
        PostgresIdempotencyStore store = new(context);

        IdempotencyCreateResult created = await store.TryCreateProcessingAsync(original);
        IdempotencyCreateResult mismatch = await store.TryCreateProcessingAsync(
            CreateRequest(
                seed,
                key: original.Key,
                fingerprint: Fingerprint('b'),
                createdAt: original.CreatedAt.AddMinutes(1),
                expiresAt: original.ExpiresAt.AddMinutes(1)));
        IdempotencyCreateResult expired = await store.TryCreateProcessingAsync(
            CreateRequest(
                seed,
                key: original.Key,
                fingerprint: original.RequestFingerprint,
                createdAt: original.ExpiresAt,
                expiresAt: original.ExpiresAt.AddMinutes(10)));
        IdempotencyReceipt? stored = await store.FindAsync(
            seed.OrganizationId,
            seed.MemberMembershipId,
            original.Key);

        Assert.Equal(IdempotencyCreateOutcome.Created, created.Outcome);
        Assert.Equal(IdempotencyCreateOutcome.RequestMismatch, mismatch.Outcome);
        Assert.Equal(IdempotencyCreateOutcome.Expired, expired.Outcome);
        Assert.Equal(created.Receipt, mismatch.Receipt);
        Assert.Equal(created.Receipt, expired.Receipt);
        Assert.Equal(created.Receipt, stored);
    }

    [RequiresPostgresFact]
    public async Task ConcurrentTerminalCompareAndSetProducesOneWinnerAndOneExpectedStatusMismatch()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        IdempotencyCreateRequest createRequest = CreateRequest(seed);
        await using (TabrukDbContext createContext = database.CreateContext())
        {
            IdempotencyCreateResult created =
                await new PostgresIdempotencyStore(createContext).TryCreateProcessingAsync(createRequest);
            Assert.Equal(IdempotencyCreateOutcome.Created, created.Outcome);
        }

        IdempotencyRequest transitionRequest = new(
            createRequest.OrganizationId,
            createRequest.MembershipId,
            createRequest.Key,
            createRequest.Operation,
            createRequest.RequestFingerprint);
        await using TabrukDbContext completeContext = database.CreateContext();
        await using TabrukDbContext failContext = database.CreateContext();
        PostgresIdempotencyStore completeStore = new(completeContext);
        PostgresIdempotencyStore failStore = new(failContext);

        Task<IdempotencyTransitionResult> completeTask = Task.Run(
            async () => await completeStore.TryCompleteAsync(transitionRequest, "signup-123"));
        Task<IdempotencyTransitionResult> failTask = Task.Run(
            async () => await failStore.TryFailAsync(transitionRequest));
        IdempotencyTransitionResult[] results = await Task.WhenAll(completeTask, failTask);

        IdempotencyTransitionResult winner = Assert.Single(
            results,
            result => result.Outcome is IdempotencyTransitionOutcome.Completed
                or IdempotencyTransitionOutcome.Failed);
        IdempotencyTransitionResult loser = Assert.Single(
            results,
            result => result.Outcome == IdempotencyTransitionOutcome.ExpectedStatusMismatch);

        Assert.NotNull(winner.Receipt);
        Assert.Equal(winner.Receipt, loser.Receipt);
        Assert.NotEqual(IdempotencyStatus.Processing, winner.Receipt!.Status);
    }

    [RequiresPostgresFact]
    public async Task SameKeyInAnotherOrganizationReturnsOnlyThatOrganizationsReceipt()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        OrganizationId otherOrganizationId = OrganizationId.New();
        MembershipId otherMembershipId = MembershipId.New();
        Guid otherUserId = Guid.CreateVersion7();
        await using (TabrukDbContext setup = database.CreateContext())
        {
            setup.Users.Add(
                new TabrukIdentityUser
                {
                    Id = otherUserId,
                    UserName = "other@example.test",
                    NormalizedUserName = "OTHER@EXAMPLE.TEST",
                    Email = "other@example.test",
                    NormalizedEmail = "OTHER@EXAMPLE.TEST",
                    SecurityStamp = Guid.NewGuid().ToString("N"),
                    ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                });
            setup.Organizations.Add(
                new OrganizationEntity
                {
                    Id = otherOrganizationId.Value,
                    Name = "Other test organization",
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
                    DisplayName = "Other member",
                    Status = (short)MembershipStatus.Active,
                    EligibleAsNamedParticipant = true,
                });
            await setup.SaveChangesAsync();
        }

        IdempotencyKey key = IdempotencyKey.New();
        IdempotencyCreateRequest firstRequest = CreateRequest(seed, key: key);
        IdempotencyCreateRequest otherRequest = new(
            otherOrganizationId,
            otherMembershipId,
            key,
            "signup.submit",
            Fingerprint('a'),
            seed.Now,
            seed.Now.AddMinutes(10));
        await using TabrukDbContext firstContext = database.CreateContext();
        await using TabrukDbContext otherContext = database.CreateContext();
        PostgresIdempotencyStore firstStore = new(firstContext);
        PostgresIdempotencyStore otherStore = new(otherContext);

        IdempotencyCreateResult first = await firstStore.TryCreateProcessingAsync(firstRequest);
        IdempotencyCreateResult other = await otherStore.TryCreateProcessingAsync(otherRequest);
        IdempotencyReceipt? firstReceipt = await firstStore.FindAsync(
            seed.OrganizationId,
            seed.MemberMembershipId,
            key);
        IdempotencyReceipt? otherReceipt = await otherStore.FindAsync(
            otherOrganizationId,
            otherMembershipId,
            key);

        Assert.Equal(IdempotencyCreateOutcome.Created, first.Outcome);
        Assert.Equal(IdempotencyCreateOutcome.Created, other.Outcome);
        Assert.Equal(seed.OrganizationId, firstReceipt!.OrganizationId);
        Assert.Equal(otherOrganizationId, otherReceipt!.OrganizationId);
        Assert.NotEqual(firstReceipt, otherReceipt);
    }

    private static IdempotencyCreateRequest CreateRequest(
        PersistenceSeed seed,
        IdempotencyKey? key = null,
        RequestFingerprint? fingerprint = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? expiresAt = null)
    {
        DateTimeOffset actualCreatedAt =
            createdAt ?? new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

        return new IdempotencyCreateRequest(
            seed.OrganizationId,
            seed.MemberMembershipId,
            key ?? IdempotencyKey.New(),
            "signup.submit",
            fingerprint ?? Fingerprint('a'),
            actualCreatedAt,
            expiresAt ?? actualCreatedAt.AddMinutes(10));
    }

    private static RequestFingerprint Fingerprint(char character) =>
        RequestFingerprint.FromSha256(new string(character, 64));
}
