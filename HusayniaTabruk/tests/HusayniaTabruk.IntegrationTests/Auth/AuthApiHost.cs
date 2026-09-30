using System.Collections.Concurrent;
using HusayniaTabruk.Api;
using HusayniaTabruk.Application.Abstractions.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HusayniaTabruk.IntegrationTests.Auth;

internal sealed class AuthApiHost : IAsyncDisposable
{
    private const string SigningKey = "integration-test-signing-key-0123456789abcdef";
    private readonly WebApplication application;

    private AuthApiHost(
        WebApplication application,
        HttpClient client,
        TestLogCollector logs)
    {
        this.application = application;
        Client = client;
        Logs = logs;
    }

    public HttpClient Client { get; }
    public TestLogCollector Logs { get; }
    public IServiceProvider Services => application.Services;

    public static async Task<AuthApiHost> StartAsync(
        string connectionString,
        IClock clock,
        Action<RouteGroupBuilder>? configureApi = null,
        IEnumerable<KeyValuePair<string, string?>>? configurationOverrides = null,
        Action<IServiceCollection>? configureServices = null)
    {
        TestLogCollector logs = new();
        WebApplication application = Program.BuildApplication(
            new WebApplicationOptions
            {
                EnvironmentName = "Testing",
            },
            builder =>
            {
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.AddProvider(logs);
                List<KeyValuePair<string, string?>> settings =
                [
                    new KeyValuePair<string, string?>("ConnectionStrings:Tabruk", connectionString),
                    new KeyValuePair<string, string?>("TabrukAuth:SigningKey", SigningKey),
                ];
                if (configurationOverrides is not null)
                {
                    settings.AddRange(configurationOverrides);
                }

                builder.Configuration.AddInMemoryCollection(settings);
                builder.Services.AddSingleton(clock);
                builder.Services.AddSingleton<IClock>(clock);
                configureServices?.Invoke(builder.Services);
            },
            configureApi);

        await application.StartAsync();
        IServer server = application.Services.GetRequiredService<IServer>();
        IServerAddressesFeature addresses =
            server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("The integration server did not expose an address.");

        HttpClient client = new()
        {
            BaseAddress = new Uri(Assert.Single(addresses.Addresses), UriKind.Absolute),
        };

        return new AuthApiHost(application, client, logs);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await application.StopAsync();
        await application.DisposeAsync();
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
    private readonly ConcurrentQueue<TestLogRecord> records = new();

    public IReadOnlyList<TestLogRecord> Records => records.ToArray();

    public ILogger CreateLogger(string categoryName) => new TestLogger(categoryName, records);

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

internal sealed class MutableClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = utcNow;

    public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);

    public void Set(DateTimeOffset value) => UtcNow = value;
}
