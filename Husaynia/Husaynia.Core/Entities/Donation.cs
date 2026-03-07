using Husaynia.Core.Enums;

namespace Husaynia.Core.Entities
{
    public class Donation
    {
        public int Id { get; set; }
        public decimal Amount { get; set; }
        public string DonorName { get; set; } = string.Empty;
        public string DonorEmail { get; set; } = string.Empty;
        public bool IsAnonymous { get; set; }
        public DateTime DonationDate { get; set; } = DateTime.UtcNow;
        public string PaymentMethod { get; set; } = string.Empty;
        public string? TransactionId { get; set; }
        public DonationStatus Status { get; set; }
        public string Campaign { get; set; } = "Build Husaynia";

        // Foreign key
        public int? DonationCampaignId { get; set; }
        public DonationCampaign? DonationCampaign { get; set; }
    }
}
