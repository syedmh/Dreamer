using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using HusayniaTabruk.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HusayniaTabruk.Api.ContractTests.Conventions;

internal sealed class ContractApiHost : IAsyncDisposable
{
    public const string MembershipHeaderName = "X-Test-Membership-Id";
    public const string OrganizationHeaderName = "X-Test-Organization-Id";

    private readonly WebApplication _application;

    private ContractApiHost(
        WebApplication application,
        HttpClient client,
        TestLogCollector logs)
    {
        _application = application;
        Client = client;
        Logs = logs;
    }

    public HttpClient Client { get; }

    public TestLogCollector Logs { get; }

    public IReadOnlyList<Endpoint> Endpoints =>
        ((IEndpointRouteBuilder)_application).DataSources
            .SelectMany(source => source.Endpoints)
            .ToArray();

    public static async Task<ContractApiHost> StartAsync(
        bool authenticated = true,
        string environmentName = "Testing",
        Action<RouteGroupBuilder>? configureApi = null)
    {
        TestLogCollector logs = new();
        WebApplication application = Program.BuildApplication(
            new WebApplicationOptions
            {
                EnvironmentName = environmentName,
            },
            builder =>
            {
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.AddProvider(logs);

                if (authenticated)
                {
                    builder.Services
                        .AddAuthentication()
                        .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                            TestAuthenticationHandler.SchemeName,
                            _ => { });
                    builder.Services.PostConfigure<AuthenticationOptions>(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
                    });
                }
            },
            configureApi);

        await application.StartAsync();
        IServer server = application.Services.GetRequiredService<IServer>();
        IServerAddressesFeature addresses =
            server.Features.Get<IServerAddressesFeature>() ??
            throw new InvalidOperationException("The test server did not expose an address.");
        string address = Assert.Single(addresses.Addresses);
        HttpClient client = new()
        {
            BaseAddress = new Uri(address, UriKind.Absolute),
        };

        return new ContractApiHost(application, client, logs);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _application.StopAsync();
        await _application.DisposeAsync();
    }
}

internal sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ContractTest";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string membershipId = Request.Headers[ContractApiHost.MembershipHeaderName].FirstOrDefault() ??
            "membership-default";
        string organizationId = Request.Headers[ContractApiHost.OrganizationHeaderName].FirstOrDefault() ??
            "organization-default";
        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, membershipId),
            new("membership_id", membershipId),
            new("organization_id", organizationId),
        ];
        ClaimsPrincipal principal = new(new ClaimsIdentity(claims, SchemeName));
        AuthenticationTicket ticket = new(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

internal sealed record TestLogRecord(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    Exception? Exception);

internal sealed class TestLogCollector : ILoggerProvider
{
    private readonly ConcurrentQueue<TestLogRecord> _records = new();

    public IReadOnlyList<TestLogRecord> Records => _records.ToArray();

    public ILogger CreateLogger(string categoryName) => new TestLogger(categoryName, _records);

    public void Dispose()
    {
    }

    private sealed class TestLogger(
        string category,
        ConcurrentQueue<TestLogRecord> records) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            records.Enqueue(new TestLogRecord(
                category,
                logLevel,
                eventId,
                formatter(state, exception),
                exception));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

internal sealed class UnknownLengthStreamingContent(byte[] content) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        stream.WriteAsync(content, 0, content.Length);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
