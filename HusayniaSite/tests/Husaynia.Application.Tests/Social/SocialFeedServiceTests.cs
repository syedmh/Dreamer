using Husaynia.Application.Contracts;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Application.Social;
using Husaynia.Domain.Social;

namespace Husaynia.Application.Tests.Social;

public sealed class SocialFeedServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HealthyRefreshNormalizesMediaOrdersDeduplicatesAndBoundsItems()
    {
        var source = new StubSource(Success(
            Item("duplicate", "older", Now.AddHours(-3)),
            Item("newest", "newest", Now.AddHours(-1), Video()),
            Item("duplicate", "newer", Now.AddHours(-2))));
        var store = new MemoryStore();
        var metrics = new RecordingMetrics();
        var service = CreateRefresh(
            source,
            store,
            maximumItems: 2,
            metrics: metrics);

        var receipt = await service.RefreshAsync("facebook", CancellationToken.None);

        Assert.Equal(SocialRefreshOutcome.RefreshedWithRejectedItems, receipt.Outcome);
        Assert.Equal(["newest", "duplicate"], store.Active!.Items.Select(item => item.ExternalId));
        var media = store.Active.Items[0];
        Assert.Equal(SocialMediaType.Video, media.MediaType);
        Assert.Equal(640, media.Width);
        Assert.Equal(30, media.DurationSeconds);
        Assert.Equal(1, source.Calls);
        AssertDependencyMetric(metrics, "success_with_rejections");
    }

    [Fact]
    public async Task NonEmptyPayloadWithNoValidItemsIsMalformedAndPreservesLastKnownGood()
    {
        var existing = Stored(Item("saved", "Saved", Now.AddHours(-1)));
        var store = new MemoryStore { Active = existing };
        var metrics = new RecordingMetrics();
        var source = new StubSource(Success(new SocialProviderItem(
            "invalid",
            " ",
            new Uri("https://facebook.example/posts/invalid"),
            null,
            Now.AddHours(-1))));

        var receipt = await CreateRefresh(source, store, metrics: metrics)
            .RefreshAsync("facebook", CancellationToken.None);

        Assert.Equal(SocialRefreshOutcome.Malformed, receipt.Outcome);
        Assert.Equal(existing.Version, store.Active!.Version);
        Assert.Equal("saved", Assert.Single(store.Active.Items).ExternalId);
        Assert.Equal(SocialRefreshError.Malformed, store.LastFailure);
        AssertDependencyMetric(metrics, "malformed");
    }

    [Fact]
    public async Task TrueEmptyPayloadPublishesEmptySnapshot()
    {
        var store = new MemoryStore();

        var receipt = await CreateRefresh(new StubSource(Success()), store)
            .RefreshAsync("facebook", CancellationToken.None);

        Assert.Equal(SocialRefreshOutcome.Empty, receipt.Outcome);
        Assert.Empty(store.Active!.Items);
    }

    [Fact]
    public async Task HistoricalProviderFetchUsesLocalExpiryAndSchedulesFutureSuccessor()
    {
        var providerFetchedAtUtc = Now.AddHours(-12);
        var store = new MemoryStore();
        var refresh = CreateRefresh(
            new StubSource(SuccessAt(
                providerFetchedAtUtc,
                Item("historical", "Historical", Now.AddHours(-1)))),
            store);

        var receipt = await refresh.RefreshAsync(
            "facebook",
            CancellationToken.None);
        var jobs = new RecordingEnqueueJobStore();
        var coordinator = new SocialRefreshJobCoordinator(
            ["facebook"],
            store,
            jobs,
            new FixedCorrelationContext("social-historical-refresh"),
            new FixedTimeProvider(Now));
        await coordinator.EnsureScheduledAsync(
            "facebook",
            CancellationToken.None);

        Assert.Equal(SocialRefreshOutcome.Refreshed, receipt.Outcome);
        Assert.Equal(providerFetchedAtUtc, store.Active!.FetchedAtUtc);
        Assert.Equal(Now, store.Active.LastSuccessAtUtc);
        Assert.Equal(Now.AddHours(6), store.Active.ExpiresAtUtc);
        var successor = Assert.Single(jobs.Enqueues);
        Assert.Equal(store.Active.ExpiresAtUtc, successor.NotBeforeUtc);
        Assert.True(successor.NotBeforeUtc > Now);
    }

    [Fact]
    public async Task PartialPayloadRejectsSignedMediaAndPersistsValidSubset()
    {
        var source = new StubSource(Success(
            Item("valid", "Valid", Now.AddHours(-1), Image()),
            Item(
                "signed",
                "Signed",
                Now.AddHours(-2),
                Image(new Uri("https://media.example/image.jpg?X-Amz-Signature=secret")))));
        var store = new MemoryStore();

        var receipt = await CreateRefresh(source, store)
            .RefreshAsync("facebook", CancellationToken.None);

        Assert.Equal(SocialRefreshOutcome.RefreshedWithRejectedItems, receipt.Outcome);
        Assert.Equal("valid", Assert.Single(store.Active!.Items).ExternalId);
        Assert.Equal(1, receipt.RejectedItems);
    }

    [Fact]
    public async Task RateLimitUsesProviderRetryAfterAndApplicationDoesNotRetry()
    {
        var retryAfter = Now.AddMinutes(7);
        var source = new StubSource(Result.Fail<SocialProviderFeed, SocialSourceError>(
            new SocialSourceError(SocialSourceErrorKind.RateLimited, retryAfter)));
        var store = new MemoryStore();
        var metrics = new RecordingMetrics();
        var service = CreateRefresh(source, store, metrics: metrics);

        var receipt = await service.RefreshAsync("facebook", CancellationToken.None);
        var suppressed = await service.RefreshAsync("facebook", CancellationToken.None);

        Assert.Equal(SocialRefreshOutcome.RateLimited, receipt.Outcome);
        Assert.Equal(SocialRefreshOutcome.RateLimited, suppressed.Outcome);
        Assert.Equal(retryAfter, store.RetryAfterUtc);
        Assert.Equal(1, source.Calls);
        Assert.Equal(2, metrics.Dependencies.Count);
        Assert.All(metrics.Dependencies, record =>
            Assert.Equal("rate_limited", record.Outcome));
    }

    [Fact]
    public async Task FarFutureRateLimitIsCappedToBoundedRecoveryHorizon()
    {
        var source = new StubSource(
            Result.Fail<SocialProviderFeed, SocialSourceError>(
                new SocialSourceError(
                    SocialSourceErrorKind.RateLimited,
                    new DateTimeOffset(
                        2099,
                        1,
                        1,
                        0,
                        0,
                        0,
                        TimeSpan.Zero))));
        var store = new MemoryStore();
        var service = CreateRefresh(source, store);

        var receipt = await service.RefreshAsync("facebook", CancellationToken.None);
        var suppressed = await service.RefreshAsync("facebook", CancellationToken.None);

        Assert.Equal(SocialRefreshOutcome.RateLimited, receipt.Outcome);
        Assert.Equal(SocialRefreshOutcome.RateLimited, suppressed.Outcome);
        Assert.Equal(Now.AddHours(24), store.RetryAfterUtc);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task LegacyFarFutureRateLimitDoesNotSuppressPastBoundedHorizon()
    {
        var store = new MemoryStore();
        await store.RecordFailureAsync(
            "facebook",
            SocialRefreshError.RateLimited,
            Now.AddDays(-2),
            new DateTimeOffset(
                2099,
                1,
                1,
                0,
                0,
                0,
                TimeSpan.Zero),
            CancellationToken.None);
        var source = new StubSource(Success(
            Item("recovered", "Recovered", Now.AddMinutes(-1))));

        var receipt = await CreateRefresh(source, store)
            .RefreshAsync("facebook", CancellationToken.None);

        Assert.Equal(SocialRefreshOutcome.Refreshed, receipt.Outcome);
        Assert.Equal(1, source.Calls);
        Assert.Null(store.RetryAfterUtc);
        Assert.Equal("recovered", Assert.Single(store.Active!.Items).ExternalId);
    }

    [Fact]
    public async Task OverallTimeoutRecordsFailureWithoutASecondRetryLayer()
    {
        var source = new StubSource(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        var store = new MemoryStore();
        var metrics = new RecordingMetrics();

        var receipt = await CreateRefresh(
            source,
            store,
            timeout: TimeSpan.FromMilliseconds(20),
            metrics: metrics)
            .RefreshAsync("facebook", CancellationToken.None);

        Assert.Equal(SocialRefreshOutcome.TimedOut, receipt.Outcome);
        Assert.Equal(1, source.Calls);
        Assert.Equal(SocialRefreshError.Timeout, store.LastFailure);
        AssertDependencyMetric(metrics, "timeout");
    }

    [Fact]
    public async Task NonterminalFailureRetainsPastRecoveryGenerationAnchor()
    {
        var recoveryAnchor = Now.AddMinutes(-5);
        var store = new MemoryStore();
        await store.RecordFailureAsync(
            "facebook",
            SocialRefreshError.RateLimited,
            Now.AddMinutes(-20),
            recoveryAnchor,
            CancellationToken.None);
        var source = new StubSource(
            Result.Fail<SocialProviderFeed, SocialSourceError>(
                new SocialSourceError(SocialSourceErrorKind.Unavailable)));

        var receipt = await CreateRefresh(source, store)
            .RefreshAsync("facebook", CancellationToken.None);

        Assert.Equal(SocialRefreshOutcome.Unavailable, receipt.Outcome);
        Assert.Equal(recoveryAnchor, store.RetryAfterUtc);
        Assert.Equal(Now, store.LastAttemptAtUtc);
        Assert.Equal(1, source.Calls);
    }

    [Theory]
    [InlineData(SocialSourceErrorKind.Timeout, SocialRefreshOutcome.TimedOut)]
    [InlineData(SocialSourceErrorKind.Unavailable, SocialRefreshOutcome.Unavailable)]
    [InlineData(SocialSourceErrorKind.Malformed, SocialRefreshOutcome.Malformed)]
    public async Task FinalAttemptFailurePersistsReplacementRecoveryAtomically(
        SocialSourceErrorKind errorKind,
        SocialRefreshOutcome expectedOutcome)
    {
        var store = new MemoryStore();
        var source = new StubSource(
            Result.Fail<SocialProviderFeed, SocialSourceError>(
                new SocialSourceError(errorKind)));

        var receipt = await CreateRefresh(source, store)
            .RefreshAsync(
                "facebook",
                CancellationToken.None,
                TimeSpan.FromMinutes(15));

        Assert.Equal(expectedOutcome, receipt.Outcome);
        Assert.NotEqual(SocialRefreshError.None, store.LastFailure);
        Assert.Equal(Now, store.LastAttemptAtUtc);
        Assert.Equal(Now.AddMinutes(15), store.RetryAfterUtc);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task UnavailableDependencyRecordsBoundedNonSensitiveMetric()
    {
        var metrics = new RecordingMetrics();
        var source = new StubSource(
            Result.Fail<SocialProviderFeed, SocialSourceError>(
                new SocialSourceError(SocialSourceErrorKind.Unavailable)));

        var receipt = await CreateRefresh(
                source,
                new MemoryStore(),
                metrics: metrics)
            .RefreshAsync("facebook", CancellationToken.None);

        Assert.Equal(SocialRefreshOutcome.Unavailable, receipt.Outcome);
        AssertDependencyMetric(metrics, "unavailable");
        var record = Assert.Single(metrics.Dependencies);
        Assert.DoesNotContain("token", record.Dependency, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("diagnostic", record.Outcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PaginationCursorIsDeterministicBoundedAndVersionSpecific()
    {
        var store = new MemoryStore
        {
            Active = Stored(
                Item("five", "5", Now.AddMinutes(-5)),
                Item("four", "4", Now.AddMinutes(-4)),
                Item("three", "3", Now.AddMinutes(-3)),
                Item("two", "2", Now.AddMinutes(-2)),
                Item("one", "1", Now.AddMinutes(-1))),
        };
        var reader = Reader(store);

        var first = await reader.ReadPageAsync(
            "facebook",
            false,
            new SocialFeedPageRequest(2),
            CancellationToken.None);
        var repeated = await reader.ReadPageAsync(
            "facebook",
            false,
            new SocialFeedPageRequest(2),
            CancellationToken.None);
        var second = await reader.ReadPageAsync(
            "facebook",
            false,
            new SocialFeedPageRequest(2, first.NextCursor),
            CancellationToken.None);

        Assert.Equal(first.NextCursor, repeated.NextCursor);
        Assert.Equal(["five", "four"], first.Items.Select(item => item.ExternalId));
        Assert.Equal(["three", "two"], second.Items.Select(item => item.ExternalId));
        Assert.True(second.HasMore);
        Assert.Equal(5, second.TotalItems);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            reader.ReadPageAsync(
                "facebook",
                false,
                new SocialFeedPageRequest(21),
                CancellationToken.None));
        store.Active = store.Active with { Version = 2 };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            reader.ReadPageAsync(
                "facebook",
                false,
                new SocialFeedPageRequest(2, first.NextCursor),
                CancellationToken.None));
    }

    [Fact]
    public async Task MediaDescriptorsDefaultDenyAndExposePlaybackOrLightboxAfterConsent()
    {
        var store = new MemoryStore
        {
            Active = Stored(
                Item("image", "Image", Now.AddMinutes(-1), Image()),
                Item("video", "Video", Now.AddMinutes(-2), Video())),
        };
        var reader = Reader(store);

        var denied = await reader.ReadAsync("facebook", false, CancellationToken.None);
        var allowed = await reader.ReadAsync("facebook", true, CancellationToken.None);

        Assert.All(denied.Items, item =>
        {
            Assert.True(item.Media!.RequiresConsent);
            Assert.False(item.Media.MayLoad);
            Assert.Null(item.Media.LoadUri);
            Assert.Null(item.Media.ThumbnailUri);
        });
        Assert.Equal(SocialMediaInteraction.Lightbox, allowed.Items[0].Media!.Interaction);
        Assert.Equal(SocialMediaInteraction.Playback, allowed.Items[1].Media!.Interaction);
        Assert.All(allowed.Items, item => Assert.True(item.Media!.MayLoad));
    }

    [Fact]
    public async Task ReaderExposesStaleEmptyLoadingAndUpstreamErrorWithoutDiagnostics()
    {
        var metrics = new RecordingMetrics();
        var store = new MemoryStore
        {
            Active = Stored(Item("one", "Update", Now.AddHours(-2))) with
            {
                ExpiresAtUtc = Now.AddMinutes(-5),
                LastSuccessAtUtc = Now.AddMinutes(-20),
                LastFailureAtUtc = Now.AddMinutes(-2),
            },
        };
        var reader = Reader(store, metrics);

        var stale = await reader.ReadAsync("facebook", false, CancellationToken.None);
        store.Active = Stored();
        var empty = await reader.ReadAsync("facebook", false, CancellationToken.None);
        store.Active = null;
        var loading = await reader.ReadAsync("facebook", false, CancellationToken.None);
        await store.RecordFailureAsync(
            "facebook",
            SocialRefreshError.Unavailable,
            Now,
            null,
            CancellationToken.None);
        var upstreamError = await reader.ReadAsync(
            "facebook",
            false,
            CancellationToken.None);

        Assert.Equal(SocialFeedAvailability.Stale, stale.Availability);
        Assert.DoesNotContain("token", stale.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SocialFeedAvailability.Empty, empty.Availability);
        Assert.Equal(SocialFeedAvailability.Loading, loading.Availability);
        Assert.Equal(SocialFeedAvailability.UpstreamError, upstreamError.Availability);
        Assert.All(
            new[]
            {
                stale.Availability,
                empty.Availability,
                loading.Availability,
                upstreamError.Availability,
            },
            availability =>
                Assert.NotEqual(SocialFeedAvailability.Unavailable, availability));
        Assert.Contains(
            metrics.Staleness,
            record => record.DataSet == "social.facebook.snapshot");
    }

    [Fact]
    public async Task StaleEmptySnapshotIsAnExplicitUpstreamError()
    {
        var store = new MemoryStore
        {
            Active = Stored() with
            {
                ExpiresAtUtc = Now.AddMinutes(-1),
                LastFailureAtUtc = Now,
            },
        };

        var view = await Reader(store)
            .ReadAsync("facebook", false, CancellationToken.None);

        Assert.Equal(SocialFeedAvailability.UpstreamError, view.Availability);
        Assert.Empty(view.Items);
    }

    [Theory]
    [InlineData("https://media.example/file.jpg?size=large", "https://media.example/file.jpg")]
    [InlineData("https://facebook.example/post?view=full", "https://facebook.example/post")]
    public void HarmlessQueryParametersAreStrippedBeforePersistence(
        string input,
        string expected)
    {
        var normalized = SocialFeedValidation.NormalizePersistablePublicUri(new Uri(input));

        Assert.Equal(expected, normalized.AbsoluteUri.TrimEnd('/'));
    }

    [Theory]
    [InlineData("https://media.example/file.jpg#fragment")]
    [InlineData("https://media.example/file.jpg?sig=abc")]
    [InlineData("https://media.example/file.jpg?signature=abc")]
    [InlineData("https://media.example/file.jpg?X-Amz-Credential=abc")]
    [InlineData("https://media.example/file.jpg?expires=123")]
    [InlineData("https://user:pass@media.example/file.jpg")]
    [InlineData("https://127.0.0.1/file.jpg")]
    public void UnsafePersistedUrlsAreRejected(string input) =>
        Assert.Throws<ArgumentException>(() =>
            SocialFeedValidation.NormalizePersistablePublicUri(new Uri(input)));

    [Fact]
    public void MediaDimensionsAndDurationAreBounded()
    {
        var invalid = new (SocialMediaType Type, int Width, int Height, double? Duration)[]
        {
            (SocialMediaType.Image, 0, 600, null),
            (SocialMediaType.Image, 800, 20_000, null),
            (SocialMediaType.Image, 800, 600, 1d),
            (SocialMediaType.Video, 640, 360, 0d),
            (SocialMediaType.Video, 640, 360, 90_000d),
        };

        Assert.All(invalid, value => Assert.ThrowsAny<ArgumentException>(() =>
            SocialMediaValidation.ValidateMetadata(
                value.Type,
                new Uri("https://media.example/file"),
                null,
                value.Width,
                value.Height,
                value.Duration)));
    }

    [Fact]
    public void DiagnosticSanitizerNeverReturnsProviderInput()
    {
        var code = SocialDiagnosticSanitizer.SafeCode(
            new InvalidOperationException("token=abc user@example.com"));

        Assert.Equal("unexpected_provider_error", code);
    }

    private static SocialFeedRefreshService CreateRefresh(
        ISocialFeedSource source,
        MemoryStore store,
        int maximumItems = 20,
        TimeSpan? timeout = null,
        IOperationsMetrics? metrics = null) =>
        new(
            source,
            store,
            Policy(),
            new FixedTimeProvider(Now),
            new SocialRefreshOptions
            {
                MaximumItems = maximumItems,
                ProviderTimeout = timeout ?? TimeSpan.FromSeconds(1),
            },
            metrics);

    private static SocialFeedReader Reader(
        MemoryStore store,
        IOperationsMetrics? metrics = null) =>
        new(store, Policy(), new FixedTimeProvider(Now), metrics);

    private static void AssertDependencyMetric(
        RecordingMetrics metrics,
        string expectedOutcome)
    {
        var record = Assert.Single(metrics.Dependencies);
        Assert.Equal("social.facebook", record.Dependency);
        Assert.Equal(expectedOutcome, record.Outcome);
        Assert.True(record.DurationMilliseconds >= 0);
    }

    private static SocialLinkPolicy Policy() =>
        new(
            new Dictionary<string, IReadOnlySet<string>>
            {
                ["facebook"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "facebook.example",
                },
            },
            new Dictionary<string, IReadOnlySet<string>>
            {
                ["facebook"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "media.example",
                },
            });

    private static Result<SocialProviderFeed, SocialSourceError> Success(
        params SocialProviderItem[] items) =>
        SuccessAt(Now, items);

    private static Result<SocialProviderFeed, SocialSourceError> SuccessAt(
        DateTimeOffset fetchedAtUtc,
        params SocialProviderItem[] items) =>
        Result.Succeed<SocialProviderFeed, SocialSourceError>(
            new SocialProviderFeed("facebook", items, fetchedAtUtc));

    private static SocialProviderItem Item(
        string id,
        string text,
        DateTimeOffset publishedAtUtc,
        SocialProviderMedia? media = null) =>
        new(
            id,
            text,
            new Uri($"https://facebook.example/posts/{id}?view=full"),
            media,
            publishedAtUtc);

    private static SocialProviderMedia Image(Uri? url = null) =>
        new(
            SocialMediaType.Image,
            url ?? new Uri("https://media.example/image.jpg?size=large"),
            new Uri("https://media.example/thumb.jpg"),
            800,
            600,
            null,
            "Image alt",
            "Image caption");

    private static SocialProviderMedia Video() =>
        new(
            SocialMediaType.Video,
            new Uri("https://media.example/video.mp4"),
            new Uri("https://media.example/poster.jpg"),
            640,
            360,
            30,
            "Video alt",
            "Video caption");

    private static StoredSocialFeed Stored(params SocialProviderItem[] items) =>
        new(
            "facebook",
            1,
            Now,
            Now.AddHours(1),
            Now,
            null,
            items.Select(item => new StoredSocialFeedItem(
                item.ExternalId,
                item.Text,
                item.SourceLink is null
                    ? null
                    : SocialFeedValidation.NormalizePersistablePublicUri(item.SourceLink),
                item.Media?.Type ?? SocialMediaType.None,
                item.Media is null
                    ? null
                    : SocialFeedValidation.NormalizePersistablePublicUri(item.Media.Url),
                item.Media?.ThumbnailUrl is null
                    ? null
                    : SocialFeedValidation.NormalizePersistablePublicUri(item.Media.ThumbnailUrl),
                item.Media?.Width,
                item.Media?.Height,
                item.Media?.DurationSeconds,
                item.Media?.AltText,
                item.Media?.Caption,
                item.PublishedAtUtc)).ToArray());

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class StubSource : ISocialFeedSource
    {
        private readonly Func<SocialFeedRequest, CancellationToken, Task<Result<SocialProviderFeed, SocialSourceError>>> fetch;

        internal StubSource(Result<SocialProviderFeed, SocialSourceError> result)
            : this((_, _) => Task.FromResult(result))
        {
        }

        internal StubSource(
            Func<SocialFeedRequest, CancellationToken, Task<Result<SocialProviderFeed, SocialSourceError>>> fetch) =>
            this.fetch = fetch;

        internal int Calls { get; private set; }

        public Task<Result<SocialProviderFeed, SocialSourceError>> FetchAsync(
            SocialFeedRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return fetch(request, cancellationToken);
        }
    }

    private sealed class MemoryStore : ISocialSnapshotStore
    {
        internal StoredSocialFeed? Active { get; set; }
        internal SocialRefreshError LastFailure { get; private set; }
        internal DateTimeOffset? LastAttemptAtUtc { get; private set; }
        internal DateTimeOffset? RetryAfterUtc { get; private set; }

        public Task<StoredSocialFeed?> ReadAsync(
            string provider,
            CancellationToken cancellationToken) =>
            Task.FromResult(Active);

        public Task<SocialRefreshState?> ReadRefreshStateAsync(
            string provider,
            CancellationToken cancellationToken) =>
            Task.FromResult<SocialRefreshState?>(
                LastFailure == SocialRefreshError.None
                    ? null
                    : new SocialRefreshState(
                        LastAttemptAtUtc,
                        Active?.LastSuccessAtUtc,
                        Active?.LastFailureAtUtc,
                        LastFailure,
                        RetryAfterUtc,
                        1));

        public Task<SocialRefreshSchedulingState> ReadSchedulingStateAsync(
            string provider,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SocialRefreshSchedulingState(
                Active?.Version,
                Active?.ExpiresAtUtc,
                LastFailure == SocialRefreshError.None
                    ? null
                    : new SocialRefreshState(
                        LastAttemptAtUtc,
                        Active?.LastSuccessAtUtc,
                        Active?.LastFailureAtUtc,
                        LastFailure,
                        RetryAfterUtc,
                        1)));

        public Task<long> ReplaceAsync(
            NormalizedSocialFeed feed,
            int retainedVersions,
            CancellationToken cancellationToken)
        {
            var version = (Active?.Version ?? 0) + 1;
            Active = new StoredSocialFeed(
                feed.Provider,
                version,
                feed.FetchedAtUtc,
                feed.ExpiresAtUtc,
                feed.RefreshedAtUtc,
                null,
                feed.Items);
            LastFailure = SocialRefreshError.None;
            LastAttemptAtUtc = feed.RefreshedAtUtc;
            RetryAfterUtc = null;
            return Task.FromResult(version);
        }

        public Task RecordFailureAsync(
            string provider,
            SocialRefreshError failure,
            DateTimeOffset occurredAtUtc,
            DateTimeOffset? retryAfterUtc,
            CancellationToken cancellationToken)
        {
            LastFailure = failure;
            LastAttemptAtUtc = occurredAtUtc;
            RetryAfterUtc = retryAfterUtc ?? RetryAfterUtc;
            if (Active is not null)
            {
                Active = Active with { LastFailureAtUtc = occurredAtUtc };
            }

            return Task.CompletedTask;
        }

        public Task DeferUntilAsync(
            string provider,
            SocialRefreshError failure,
            DateTimeOffset occurredAtUtc,
            DateTimeOffset retryAfterUtc,
            CancellationToken cancellationToken)
        {
            RetryAfterUtc = retryAfterUtc;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedCorrelationContext(string correlationId)
        : ICorrelationContext
    {
        public CorrelationSnapshot Current =>
            new(correlationId, correlationId, null);

        public IDisposable Begin(
            string? suppliedCorrelationId = null,
            string operationName = "operation") =>
            new Scope();

        private sealed class Scope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class RecordingEnqueueJobStore : IDurableJobStore
    {
        internal List<JobEnqueueRequest> Enqueues { get; } = [];

        public Task<Result<Guid, JobStoreError>> RegisterDefinitionAsync(
            JobDefinitionRegistration registration,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<JobEnqueueReceipt, JobStoreError>> EnqueueAsync(
            JobEnqueueRequest request,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            Enqueues.Add(request);
            return Task.FromResult(
                Result.Succeed<JobEnqueueReceipt, JobStoreError>(
                    new JobEnqueueReceipt(Guid.NewGuid(), false)));
        }

        public Task<Result<JobAcquireResult, JobStoreError>> TryAcquireNextAsync(
            WorkerIdentity worker,
            TimeSpan leaseDuration,
            DateTimeOffset now,
            CancellationToken cancellationToken,
            string? definitionKey = null) =>
            throw new NotSupportedException();

        public Task<Result<bool, JobStoreError>> RenewAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            TimeSpan leaseDuration,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<bool, JobStoreError>> CompleteAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<bool, JobStoreError>> FailAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            string errorCode,
            DateTimeOffset? nextRunAtUtc,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<bool, JobStoreError>> ReleaseForRecoveryAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            DateTimeOffset nextRunAtUtc,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<bool, JobStoreError>> RequestCancellationAsync(
            Guid jobInstanceId,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<bool, JobStoreError>> RequeueDeadLetterAsync(
            Guid jobInstanceId,
            string actor,
            string reason,
            string correlationId,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<JobStateLookup, JobStoreError>> GetStateAsync(
            Guid jobInstanceId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingMetrics : IOperationsMetrics
    {
        internal List<DependencyRecord> Dependencies { get; } = [];

        internal List<StalenessRecord> Staleness { get; } = [];

        public void RecordDependency(
            string dependency,
            string outcome,
            double durationMilliseconds = 0) =>
            Dependencies.Add(new DependencyRecord(
                dependency,
                outcome,
                durationMilliseconds));

        public void RecordJob(string definition, string outcome, int attempt)
        {
        }

        public void RecordStaleness(string dataSet, TimeSpan age) =>
            Staleness.Add(new StalenessRecord(dataSet, age));
    }

    private sealed record DependencyRecord(
        string Dependency,
        string Outcome,
        double DurationMilliseconds);

    private sealed record StalenessRecord(string DataSet, TimeSpan Age);
}
