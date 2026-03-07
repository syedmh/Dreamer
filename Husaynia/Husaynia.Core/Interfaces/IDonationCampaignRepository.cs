using Husaynia.Core.Entities;

namespace Husaynia.Core.Interfaces
{
    public interface IDonationCampaignRepository
    {
        Task<DonationCampaign?> GetByIdAsync(int id);
        Task<DonationCampaign?> GetByNameAsync(string name);
        Task<List<DonationCampaign>> GetAllActiveAsync();
        Task<DonationCampaign> AddAsync(DonationCampaign campaign);
        Task UpdateAsync(DonationCampaign campaign);
        Task UpdateCampaignAmountAsync(int campaignId, decimal amount);
    }
}
