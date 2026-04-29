using Microsoft.Extensions.Logging;
using WillVault.Application.Interfaces.Services;

namespace WillVault.Infrastructure.Notifications;

/// <summary>
/// Placeholder email notification service. Logs to console instead of sending real emails.
/// Replace with an SMTP or SendGrid implementation in production.
/// </summary>
public class EmailNotificationService : INotificationService
{
    private readonly ILogger<EmailNotificationService> _logger;

    public EmailNotificationService(ILogger<EmailNotificationService> logger)
    {
        _logger = logger;
    }

    public Task SendEmailAsync(string to, string subject, string body,
        IReadOnlyDictionary<string, byte[]>? attachments = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[EMAIL] To: {To} | Subject: {Subject} | Attachments: {Count}",
            to, subject, attachments?.Count ?? 0);
        return Task.CompletedTask;
    }

    public Task SendSmsAsync(string to, string message, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[SMS] To: {PhoneNumber} | Message: {Message}", to, message);
        return Task.CompletedTask;
    }

    public Task SendPushAsync(string deviceToken, string title, string body, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[PUSH] To: {DeviceToken} | Title: {Title} | Message: {Message}", deviceToken, title, body);
        return Task.CompletedTask;
    }
}
