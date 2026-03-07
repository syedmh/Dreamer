using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Fundraiser.Models;

namespace Fundraiser.Services;

public class DonationService
{
    private readonly string _csvFilePath;
    private readonly object _lock = new object();

    public DonationService()
    {
        var dataFolder = Path.Combine(Directory.GetCurrentDirectory(), "Data");
        Directory.CreateDirectory(dataFolder);
        _csvFilePath = Path.Combine(dataFolder, "donations.csv");
    }

    public void SaveDonation(decimal amount)
    {
        lock (_lock)
        {
            var donation = new Donation
            {
                Timestamp = DateTime.Now,
                Amount = amount
            };

            var fileExists = File.Exists(_csvFilePath);

            // Use UTF-8 with BOM for Excel compatibility
            using var stream = new FileStream(_csvFilePath, FileMode.Append, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(true));
            using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = !fileExists
            });

            if (!fileExists)
            {
                csv.WriteHeader<Donation>();
                csv.NextRecord();
            }

            csv.WriteRecord(donation);
            csv.NextRecord();
        }
    }

    public decimal GetTotalRaised()
    {
        lock (_lock)
        {
            if (!File.Exists(_csvFilePath))
                return 0;

            try
            {
                using var reader = new StreamReader(_csvFilePath, Encoding.UTF8);
                using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
                var donations = csv.GetRecords<Donation>().ToList();
                return donations.Sum(d => d.Amount);
            }
            catch
            {
                return 0;
            }
        }
    }

    public List<Donation> GetAllDonations()
    {
        lock (_lock)
        {
            if (!File.Exists(_csvFilePath))
                return new List<Donation>();

            try
            {
                using var reader = new StreamReader(_csvFilePath, Encoding.UTF8);
                using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
                return csv.GetRecords<Donation>().ToList();
            }
            catch
            {
                return new List<Donation>();
            }
        }
    }

    public string GetCsvFilePath()
    {
        return _csvFilePath;
    }
}
