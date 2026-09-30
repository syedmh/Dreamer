using System.Text.Json.Serialization;

namespace Husaynia.BaselineCapture;

public sealed record CaptureOptions(
    Uri BaseUri,
    string OutputDirectory,
    bool NoSubmit,
    int DelayMilliseconds,
    int RequestTimeoutSeconds,
    int MaxAssetBytes,
    int MaxConcurrency,
    int MaxDurationMinutes);

public sealed record RedirectHop(string Url, int Status, string? Location);

public sealed record HttpRecord(
    string Url,
    string Path,
    int? Status,
    string? FinalUrl,
    IReadOnlyList<RedirectHop> Redirects,
    string? ContentType,
    long? ContentLength,
    string? Sha256,
    string? SavedAs,
    string? Error,
    IReadOnlyDictionary<string, string> Headers);

public sealed record MetadataRecord(
    string Url,
    string? Title,
    string? Description,
    string? Canonical,
    string? Robots,
    string? OpenGraphTitle,
    string? OpenGraphDescription,
    string? OpenGraphImage,
    string? TwitterCard,
    IReadOnlyList<string> JsonLdTypes,
    string? Language,
    string? Direction,
    string? H1,
    string ContentSha256);

public sealed record AssetRecord(
    string Key,
    string Url,
    string SourcePage,
    string Kind,
    string Relation,
    string Ownership,
    int? Status,
    string? ContentType,
    long? Bytes,
    string? Sha256,
    string? SavedAs,
    string? Error);

public sealed record NavigationItem(
    string NavigationKey,
    string SourcePage,
    string Label,
    string Destination,
    string? ParentLabel,
    string? ParentNavigationKey,
    IReadOnlyList<string> AncestorLabels,
    IReadOnlyList<string> AncestorNavigationKeys,
    int Depth,
    string Region,
    int Order,
    bool IsExternal,
    bool IsDestination,
    bool IsParentControl,
    bool IsActive,
    bool IsFocusable,
    bool IsMobileControl,
    bool VisibleOnDesktop,
    bool VisibleOnMobile,
    string StateEvidence);

public sealed record FormObservation(
    string SourcePage,
    string FormKey,
    string Method,
    string Action,
    IReadOnlyList<FormFieldObservation> Fields,
    bool SubmissionAttempted,
    string RiskClassification);

public sealed record FormFieldObservation(
    string? Name,
    string Type,
    string? Label,
    bool Required,
    string? Autocomplete);

public sealed record DynamicRegionObservation(
    string SourcePage,
    string RegionKey,
    string Provider,
    string Selector,
    string Normalization,
    string RiskClassification);

public sealed record ScreenshotRecord(
    string TemplateKey,
    string Url,
    string Viewport,
    int Width,
    int Height,
    string? SavedAs,
    string? Sha256,
    string Status,
    string? Error,
    bool Ready,
    int TextLength,
    bool HorizontalOverflow,
    int FormCount,
    long PngBytes,
    int PngWidth,
    int PngHeight,
    string TemplateEvidence,
    string QualityStatus,
    double ByteEntropy,
    bool GiantSvgDetected,
    bool LoadingOnlyState,
    IReadOnlyList<string> VisibleLandmarks,
    int ActualViewportWidth,
    int ActualViewportHeight,
    int ActualScreenWidth,
    int ActualScreenHeight,
    double ActualDevicePixelRatio,
    int DocumentScrollWidth,
    int DocumentScrollHeight,
    double BodyWidth,
    double BodyHeight,
    bool FontsReady,
    int IncompleteImageCount,
    int InFlightAllowedRequests,
    IReadOnlyList<string> OverflowSources,
    IReadOnlyList<ScreenshotReadinessSample> ReadinessSamples,
    bool LayoutInjectionUsed,
    string NetworkDecisionRef,
    string ProvenanceRef,
    IReadOnlyList<string> QualityReasonCodes,
    int AttemptCount,
    IReadOnlyList<string> AttemptReasonCodes,
    int? ControlledRunDonationFormCount);

public sealed record ScreenshotReadinessSample(
    DateTimeOffset CapturedAtUtc,
    int InnerWidth,
    int InnerHeight,
    double DevicePixelRatio,
    int DocumentScrollWidth,
    int DocumentScrollHeight,
    double BodyWidth,
    double BodyHeight,
    bool FontsReady,
    int IncompleteImageCount,
    int InFlightAllowedRequests);

public sealed record ScreenshotNetworkDecision(
    string CaptureKey,
    string RequestId,
    DateTimeOffset TimestampUtc,
    string EventType,
    string Method,
    string RedactedUrl,
    string ResourceType,
    bool IsNavigationRequest,
    string Decision,
    string ReasonCode,
    int? ResponseStatus,
    string? Failure,
    string PolicyVersion,
    int Attempt,
    bool IsMainFrame,
    string? PinnedAddress)
{
    public long? DnsEpoch { get; init; }

    public string? BrowserInstanceId { get; init; }

    public string? BrowserContextId { get; init; }
}

public sealed record DnsPinBindingEvidence(
    string Host,
    string HostClass,
    string SelectedAddress,
    IReadOnlyList<string> CompleteObservedPublicSet,
    string AnswerSetSha256,
    DateTimeOffset ObservedAtUtc,
    bool Rotated);

public sealed record DnsContextEpochEvidence(
    long Epoch,
    string CaptureKey,
    int Attempt,
    string BrowserInstanceId,
    string BrowserContextId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PreNavigationValidatedAtUtc,
    DateTimeOffset? PreScreenshotValidatedAtUtc,
    DateTimeOffset? PostScreenshotValidatedAtUtc,
    IReadOnlyList<DnsPinBindingEvidence> Bindings);

public sealed record BrowserLaunchEvidence(
    string BrowserInstanceId,
    DateTimeOffset LaunchedAtUtc,
    string ResolverMapSha256,
    string? PreviousBrowserInstanceId,
    string Reason);

public sealed record ScreenshotCaptureProvenance(
    string CaptureId,
    DateTimeOffset CapturedAtUtc,
    string OperatingSystem,
    string Architecture,
    string DotNetSdkVersion,
    string PlaywrightPackageVersion,
    string ChromiumVersion,
    string ChromiumExecutable,
    string BrowserInstallCommand,
    string BrowserCacheKey,
    string NetworkPolicyVersion,
    int NavigationAttempts,
    int NavigationAttemptTimeoutSeconds,
    int ReadinessTimeoutSeconds,
    int CaptureTimeoutSeconds,
    int InterCaptureDelayMilliseconds,
    bool ToolingOnlyDependency,
    string ChromiumRevision,
    string ExpectedExecutableSha256,
    string ActualExecutableSha256,
    bool ChromiumSandbox,
    IReadOnlyList<string> ChildEnvironmentKeys,
    string BrowserWorkingDirectory,
    bool ProcessElevated,
    string PrimaryOrigin,
    IReadOnlyDictionary<string, IReadOnlyList<string>> PinnedDnsAnswers,
    IReadOnlyList<string> ChromiumHostResolverRules,
    bool CookiesUsed,
    bool StorageStateUsed,
    bool PermissionsGranted,
    bool ProxyUsed,
    bool CredentialsUsed)
{
    public string Status { get; init; } = "complete";

    public string? FailureReason { get; init; }

    public bool BrowserExecutableOutsideWorkspace { get; init; }

    public bool BrowserWorkingDirectoryOutsideWorkspace { get; init; }

    public bool BrowserWorkingDirectoryEmpty { get; init; }

    public bool ChildEnvironmentSanitized { get; init; }

    public IReadOnlyList<DnsContextEpochEvidence> DnsContextEpochs { get; init; } = [];

    public IReadOnlyList<BrowserLaunchEvidence> BrowserLaunches { get; init; } = [];
}

public sealed record ReligiousContentRecord(
    string Route,
    string Title,
    string TemplateKey,
    string SourceHtml,
    string SourceChecksum,
    IReadOnlyList<ReligiousParagraph> Paragraphs,
    IReadOnlyList<string> AudioReferences,
    string FixtureChecksum);

public sealed record ReligiousParagraph(
    int Order,
    string Role,
    string Text,
    string Direction,
    string Language,
    string Checksum);

public sealed record RouteManifest(
    string SchemaVersion,
    string CaptureId,
    DateTimeOffset CapturedAtUtc,
    string SourceBaseUrl,
    IReadOnlyList<RouteEntry> Routes);

public sealed record RouteEntry(
    string RouteId,
    string LegacyPath,
    string CanonicalPath,
    int ExpectedStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RedirectTarget,
    string TemplateKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentKey,
    bool Indexable,
    bool Sitemap,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MetadataKey,
    IReadOnlyList<string> AssetKeys,
    IReadOnlyList<string> DynamicRegionKeys,
    IReadOnlyList<string> EvidenceRefs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentChecksum);

public sealed record ImportManifest(
    string SchemaVersion,
    string Source,
    string SourceVersion,
    DateTimeOffset CapturedAtUtc,
    string RightsProfile,
    string Mode,
    IReadOnlyList<ImportCandidate> Candidates);

public sealed record ImportCandidate(
    string SourceKey,
    string SourceVersion,
    string SourceChecksum,
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceUri,
    string TargetKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CanonicalPath,
    string PayloadRef,
    IReadOnlyList<string> MediaRefs,
    IReadOnlyList<string> DependencyKeys,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExpectedTargetChecksum,
    string Decision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ConflictReason);

public sealed record ResidualRisk(
    string RiskId,
    string Area,
    string Description,
    string Evidence,
    string RequiredFollowUp);

public sealed record CaptureSummary(
    string CaptureId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string SourceBaseUrl,
    bool NoSubmit,
    int SitemapCount,
    int RouteCount,
    int SuccessfulRouteCount,
    int RedirectCount,
    int MetadataCount,
    int AssetCount,
    int DownloadedAssetCount,
    int FormCount,
    int DynamicRegionCount,
    int ScreenshotCount,
    int SuccessfulScreenshotCount,
    int QualityPassScreenshotCount,
    int ResidualRiskCount,
    double DurationSeconds,
    int NetworkRequestCount,
    int CacheHitCount)
{
    public string Status { get; init; } = "complete";

    public string? FailureReason { get; init; }

    public string? FailureStage { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DiscoveredUrlCount { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ManifestPathCount { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? QueryEndpointExcludedByFrozenSchemaCount { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureAffectedUrl { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureDetailReason { get; init; }
}

internal sealed record ScreenshotNetworkPolicyEvidence(
    string PolicyVersion,
    IReadOnlyList<string> AllowedMethods,
    IReadOnlyList<string> AllowedOrigins,
    IReadOnlyList<string> AllowedStaticHosts,
    IReadOnlyList<string> AllowedStaticResourceTypes,
    IReadOnlyList<string> BlockedCapabilities,
    IReadOnlyDictionary<string, IReadOnlyList<string>> PinnedDnsAnswers,
    IReadOnlyList<string> ChromiumHostResolverRules,
    int NavigationAttempts,
    int NavigationAttemptTimeoutSeconds,
    int ReadinessTimeoutSeconds,
    int CaptureTimeoutSeconds,
    int InterCaptureDelayMilliseconds,
    string Enforcement)
{
    public string Status { get; init; } = "complete";

    public string? FailureReason { get; init; }
}
