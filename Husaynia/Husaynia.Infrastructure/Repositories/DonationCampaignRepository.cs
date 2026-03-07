using Husaynia.Core.Entities;
using Husaynia.Core.Interfaces;
using Husaynia.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Husaynia.Infrastructure.Repositories
{
    public class DonationCampaignRepository : IDonationCampaignRepository
    {
        private readonly ApplicationDbContext _context;

        public DonationCampaignRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<DonationCampaign?> GetByIdAsync(int id)
        {
            return await _context.DonationCampaigns
                .Include(c => c.Donations)
                .FirstOrDefaultAsync(c => c.Id == id);
        }

        public async Task<DonationCampaign?> GetByNameAsync(string name)
        {
            return await _context.DonationCampaigns
                .Include(c => c.Donations)
                .FirstOrDefaultAsync(c => c.Name == name);
        }

        public async Task<List<DonationCampaign>> GetAllActiveAsync()
        {
            return await _context.DonationCampaigns
                .Where(c => c.IsActive)
                .OrderByDescending(c => c.StartDate)
                .ToListAsync();
        }

        public async Task<DonationCampaign> AddAsync(DonationCampaign campaign)
        {
            _context.DonationCampaigns.Add(campaign);
            await _context.SaveChangesAsync();
            return campaign;
        }

        public async Task UpdateAsync(DonationCampaign campaign)
        {
            _context.Entry(campaign).State = EntityState.Modified;
            await _context.SaveChangesAsync();
        }

        public async Task UpdateCampaignAmountAsync(int campaignId, decimal amount)
        {
            var campaign = await _context.DonationCampaigns.FindAsync(campaignId);
            if (campaign != null)
            {
                campaign.CurrentAmount += amount;
                await _context.SaveChangesAsync();
            }
        }
    }
}
