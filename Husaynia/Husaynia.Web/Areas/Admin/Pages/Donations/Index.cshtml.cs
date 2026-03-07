using Husaynia.Core.Entities;
using Husaynia.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Husaynia.Web.Areas.Admin.Pages.Donations
{
    [Authorize]
    public class DonationsIndexModel : PageModel
    {
        private readonly IDonationRepository _donationRepository;

        public DonationsIndexModel(IDonationRepository donationRepository)
        {
            _donationRepository = donationRepository;
        }

        public decimal TotalDonations { get; set; }
        public int DonationCount { get; set; }
        public decimal CampaignTotal { get; set; }
        public List<Donation> RecentDonations { get; set; } = new();

        public async Task OnGetAsync()
        {
            TotalDonations = await _donationRepository.GetTotalDonationsAsync();
            CampaignTotal = await _donationRepository.GetTotalForCampaignAsync("Build Husaynia");

            var allDonations = await _donationRepository.GetAllAsync();
            DonationCount = allDonations.Count;
            RecentDonations = allDonations.Take(20).ToList();
        }
    }
}
