namespace ShareApp.Web.Services.Interfaces
{
    public interface IEmailService
    {
        Task SendTransactionCreatedEmailAsync(string recipientEmail, string recipientName, string itemTitle, string buyerName, decimal? amount);
        Task SendTransactionCompletedEmailAsync(string recipientEmail, string recipientName, string itemTitle, decimal? amount);
        Task SendNewMessageEmailAsync(string recipientEmail, string recipientName, string senderName, string messagePreview);
        Task SendReviewReceivedEmailAsync(string recipientEmail, string recipientName, int rating, string? comment, string reviewerName);
        Task SendPaymentConfirmationEmailAsync(string recipientEmail, string recipientName, string itemTitle, decimal amount, string transactionId);
        Task SendWelcomeEmailAsync(string recipientEmail, string recipientName);
    }
}
