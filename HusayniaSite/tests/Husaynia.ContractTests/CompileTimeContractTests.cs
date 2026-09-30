using Husaynia.Application.Contracts;

namespace Husaynia.ContractTests;

public sealed class CompileTimeContractTests
{
    [Fact]
    public void ContentAndUseCasePortsExposeFrozenMethods()
    {
        AssertMethods<IHusayniaModule>("AddServices");
        AssertMethods<IHusayniaConfigurationValidator>("Validate");
        AssertMethods<IRouteManifestValidator>("Validate");
        AssertMethods<IContentReader>("GetByPathAsync", "GetPreviewAsync");
        AssertMethods<IContentEditor>("SaveDraftAsync");
        AssertMethods<IPublisher>("PublishAsync", "RollbackAsync", "UnpublishAsync");
        AssertMethods<IEventReader>("GetBySlugAsync");
        AssertMethods<ICalendarExporter>("ExportAsync");
        AssertMethods<IPrayerScheduleService>("GetMonthAsync");
        AssertMethods<IPublicSearch>("SearchAsync");
        AssertMethods<IFormSubmissionService>("SubmitAsync");
        AssertMethods<IDonationService>("CreateCheckoutAsync", "HandleWebhookAsync");
        AssertMethods<IImportPlanner>("PlanAsync");
        AssertMethods<IImportExecutor>("ApplyAsync");
    }

    [Fact]
    public void ProviderPortsExposeFrozenMethods()
    {
        AssertMethods<IPrayerSource>("GetAsync");
        AssertMethods<ISocialFeedProvider>("FetchAsync");
        AssertMethods<IMediaStore>("OpenReadAsync", "PutAsync");
        AssertMethods<IUploadSafetyValidator>("ValidateAsync");
        AssertMethods<IOutboundMessageSender>("SendAsync");
        AssertMethods<IPaymentGateway>("CreateCheckoutAsync", "VerifyWebhookAsync");
        AssertMethods<IJobLeaseStore>("TryAcquireAsync");
    }

    [Fact]
    public void EveryFrozenPortMethodIsAsyncAndCancellationAware()
    {
        var interfaces = new[]
        {
            typeof(IContentReader),
            typeof(IContentEditor),
            typeof(IPublisher),
            typeof(IEventReader),
            typeof(ICalendarExporter),
            typeof(IPrayerScheduleService),
            typeof(IPublicSearch),
            typeof(IFormSubmissionService),
            typeof(IDonationService),
            typeof(IImportPlanner),
            typeof(IImportExecutor),
            typeof(IPrayerSource),
            typeof(ISocialFeedProvider),
            typeof(IMediaStore),
            typeof(IUploadSafetyValidator),
            typeof(IOutboundMessageSender),
            typeof(IPaymentGateway),
            typeof(IJobLeaseStore),
        };

        foreach (var method in interfaces.SelectMany(contract => contract.GetMethods()))
        {
            Assert.True(method.ReturnType.IsGenericType);
            Assert.Equal(typeof(Task<>), method.ReturnType.GetGenericTypeDefinition());
            Assert.Equal(typeof(CancellationToken), method.GetParameters()[^1].ParameterType);
        }
    }

    [Fact]
    public void ProviderPortSignaturesMatchFrozenC4()
    {
        AssertSignature<IPrayerSource>(
            "GetAsync",
            typeof(Task<Result<PrayerSourceSnapshot, IntegrationError>>),
            typeof(PrayerSourceRequest),
            typeof(CancellationToken));
        AssertSignature<ISocialFeedProvider>(
            "FetchAsync",
            typeof(Task<Result<SocialFeedSnapshot, IntegrationError>>),
            typeof(SocialFeedRequest),
            typeof(CancellationToken));
        AssertSignature<IMediaStore>(
            "PutAsync",
            typeof(Task<Result<MediaWriteReceipt, IntegrationError>>),
            typeof(MediaWriteRequest),
            typeof(CancellationToken));
        AssertSignature<IMediaStore>(
            "OpenReadAsync",
            typeof(Task<Result<MediaReadResult, IntegrationError>>),
            typeof(MediaReadRequest),
            typeof(CancellationToken));
        AssertSignature<IUploadSafetyValidator>(
            "ValidateAsync",
            typeof(Task<Result<UploadValidation, IntegrationError>>),
            typeof(UploadCandidate),
            typeof(CancellationToken));
        AssertSignature<IOutboundMessageSender>(
            "SendAsync",
            typeof(Task<Result<OutboundMessageReceipt, IntegrationError>>),
            typeof(OutboundMessage),
            typeof(CancellationToken));
        AssertSignature<IPaymentGateway>(
            "CreateCheckoutAsync",
            typeof(Task<Result<PaymentCheckout, IntegrationError>>),
            typeof(PaymentCheckoutRequest),
            typeof(string),
            typeof(CancellationToken));
        AssertSignature<IPaymentGateway>(
            "VerifyWebhookAsync",
            typeof(Task<Result<VerifiedWebhook, IntegrationError>>),
            typeof(ReadOnlyMemory<byte>),
            typeof(string),
            typeof(CancellationToken));
        AssertSignature<IJobLeaseStore>(
            "TryAcquireAsync",
            typeof(Task<Result<JobLease, IntegrationError>>),
            typeof(JobKey),
            typeof(WorkerIdentity),
            typeof(TimeSpan),
            typeof(CancellationToken));
    }

    private static void AssertMethods<TContract>(params string[] expectedNames)
    {
        var actualNames = typeof(TContract)
            .GetMethods()
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedNames.Order(StringComparer.Ordinal), actualNames);
    }

    private static void AssertSignature<TContract>(
        string methodName,
        Type expectedReturnType,
        params Type[] expectedParameterTypes)
    {
        var method = typeof(TContract).GetMethod(methodName) ??
            throw new InvalidOperationException($"{typeof(TContract).Name}.{methodName} was not found.");

        Assert.Equal(expectedReturnType, method.ReturnType);
        Assert.Equal(expectedParameterTypes, method.GetParameters().Select(parameter => parameter.ParameterType));
    }
}
