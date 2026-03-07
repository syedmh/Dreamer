using Husaynia.Core.Entities;
using Husaynia.Core.Enums;
using Husaynia.Core.Interfaces;
using Husaynia.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Husaynia.Infrastructure.Repositories
{
    public class DonationRepository : IDonationRepository
    {
        private readonly ApplicationDbContext _context;

        public DonationRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<Donation?> GetByIdAsync(int id)
        {
            return await _context.Donations
                .Include(d => d.DonationCampaign)
                .FirstOrDefaultAsync(d => d.Id == id);
        }

        public async Task<Donation?> GetByTransactionIdAsync(string transactionId)
        {
            return await _context.Donations
                .Include(d => d.DonationCampaign)
                .FirstOrDefaultAsync(d => d.TransactionId == transactionId);
        }

        public async Task<List<Donation>> GetAllAsync()
        {
            return await _context.Donations
                .Include(d => d.DonationCampaign)
                .OrderByDescending(d => d.DonationDate)
                .ToListAsync();
        }

        public async Task<List<Donation>> GetByCampaignAsync(string campaign)
        {
            return await _context.Donations
                .Where(d => d.Campaign == campaign && d.Status == DonationStatus.Completed)
                .OrderByDescending(d => d.DonationDate)
                .ToListAsync();
        }

        public async Task<List<Donation>> GetRecentDonationsAsync(int count = 10)
        {
            return await _context.Donations
                .Where(d => d.Status == DonationStatus.Completed && !d.IsAnonymous)
                .OrderByDescending(d => d.DonationDate)
                .Take(count)
                .ToListAsync();
        }

        public async Task<decimal> GetTotalDonationsAsync()
        {
            return await _context.Donations
                .Where(d => d.Status == DonationStatus.Completed)
                .SumAsync(d => d.Amount);
        }

        public async Task<decimal> GetTotalForCampaignAsync(string campaign)
        {
            return await _context.Donations
                .Where(d => d.Campaign == campaign && d.Status == DonationStatus.Completed)
                .SumAsync(d => d.Amount);
        }

        public async Task<Donation> AddAsync(Donation donation)
        {
            _context.Donations.Add(donation);
            await _context.SaveChangesAsync();
            return donation;
        }

        public async Task UpdateAsync(Donation donation)
        {
            _context.Entry(donation).State = EntityState.Modified;
            await _context.SaveChangesAsync();
        }

        public async Task<List<Donation>> GetDonationsByStatusAsync(DonationStatus status)
        {
            return await _context.Donations
                .Where(d => d.Status == status)
                .OrderByDescending(d => d.DonationDate)
                .ToListAsync();
        }
    }
}
