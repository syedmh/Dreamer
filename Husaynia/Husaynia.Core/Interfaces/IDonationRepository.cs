using Husaynia.Core.Entities;
using Husaynia.Core.Enums;

namespace Husaynia.Core.Interfaces
{
    public interface IDonationRepository
    {
        Task<Donation?> GetByIdAsync(int id);
        Task<Donation?> GetByTransactionIdAsync(string transactionId);
        Task<List<Donation>> GetAllAsync();
        Task<List<Donation>> GetByCampaignAsync(string campaign);
        Task<List<Donation>> GetRecentDonationsAsync(int count = 10);
        Task<decimal> GetTotalDonationsAsync();
        Task<decimal> GetTotalForCampaignAsync(string campaign);
        Task<Donation> AddAsync(Donation donation);
        Task UpdateAsync(Donation donation);
        Task<List<Donation>> GetDonationsByStatusAsync(DonationStatus status);
    }
}
