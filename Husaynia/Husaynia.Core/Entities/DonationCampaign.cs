namespace Husaynia.Core.Entities
{
    public class DonationCampaign
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public decimal GoalAmount { get; set; }
        public decimal CurrentAmount { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public bool IsActive { get; set; } = true;
        public string Description { get; set; } = string.Empty;

        // Navigation property
        public ICollection<Donation> Donations { get; set; } = new List<Donation>();
    }
}
