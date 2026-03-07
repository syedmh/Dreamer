using Microsoft.EntityFrameworkCore;
using ShareApp.Web.Data;
using ShareApp.Web.Entities;
using ShareApp.Web.Services.Interfaces;

namespace ShareApp.Web.Services.Implementations;

public class LocationService : ILocationService
{
    private readonly ApplicationDbContext _context;
    private readonly IConfiguration _configuration;

    public LocationService(ApplicationDbContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
    }

    public async Task<Location?> GetLocationByIdAsync(int id)
    {
        return await _context.Locations
            .Include(l => l.User)
            .FirstOrDefaultAsync(l => l.Id == id);
    }

    public async Task<IEnumerable<Location>> GetUserLocationsAsync(string userId)
    {
        return await _context.Locations
            .Where(l => l.UserId == userId)
            .OrderByDescending(l => l.IsDefault)
            .ThenByDescending(l => l.CreatedAt)
            .ToListAsync();
    }

    public async Task<int> CreateLocationAsync(Location location)
    {
        // If this is set as default, unset other defaults
        if (location.IsDefault)
        {
            var existingDefaults = await _context.Locations
                .Where(l => l.UserId == location.UserId && l.IsDefault)
                .ToListAsync();

            foreach (var existing in existingDefaults)
            {
                existing.IsDefault = false;
            }
        }

        _context.Locations.Add(location);
        await _context.SaveChangesAsync();
        return location.Id;
    }

    public async Task<bool> UpdateLocationAsync(Location location)
    {
        var existing = await _context.Locations.FindAsync(location.Id);
        if (existing == null || existing.UserId != location.UserId)
            return false;

        // If setting as default, unset other defaults
        if (location.IsDefault && !existing.IsDefault)
        {
            var otherDefaults = await _context.Locations
                .Where(l => l.UserId == location.UserId && l.IsDefault && l.Id != location.Id)
                .ToListAsync();

            foreach (var other in otherDefaults)
            {
                other.IsDefault = false;
            }
        }

        existing.AddressLine1 = location.AddressLine1;
        existing.AddressLine2 = location.AddressLine2;
        existing.City = location.City;
        existing.State = location.State;
        existing.ZipCode = location.ZipCode;
        existing.Country = location.Country;
        existing.Latitude = location.Latitude;
        existing.Longitude = location.Longitude;
        existing.IsDefault = location.IsDefault;

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteLocationAsync(int id, string userId)
    {
        var location = await _context.Locations.FindAsync(id);
        if (location == null || location.UserId != userId)
            return false;

        // Check if any items are using this location
        var itemsUsingLocation = await _context.Items.AnyAsync(i => i.LocationId == id);
        if (itemsUsingLocation)
            return false; // Don't delete if items are using it

        _context.Locations.Remove(location);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<(decimal Latitude, decimal Longitude)?> GeocodeAddressAsync(string address)
    {
        // TODO: Implement Google Geocoding API integration
        // For now, return null - will implement when Google Maps API key is configured
        await Task.CompletedTask;
        return null;
    }

    public double CalculateDistance(decimal lat1, decimal lon1, decimal lat2, decimal lon2)
    {
        // Haversine formula for calculating distance between two points on Earth
        const double earthRadiusMiles = 3959;

        var dLat = DegreesToRadians((double)(lat2 - lat1));
        var dLon = DegreesToRadians((double)(lon2 - lon1));

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(DegreesToRadians((double)lat1)) * Math.Cos(DegreesToRadians((double)lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        var distance = earthRadiusMiles * c;

        return distance;
    }

    public async Task<bool> SetDefaultLocationAsync(int locationId, string userId)
    {
        var location = await _context.Locations.FindAsync(locationId);
        if (location == null || location.UserId != userId)
            return false;

        // Unset all other defaults
        var otherDefaults = await _context.Locations
            .Where(l => l.UserId == userId && l.IsDefault)
            .ToListAsync();

        foreach (var other in otherDefaults)
        {
            other.IsDefault = false;
        }

        location.IsDefault = true;
        await _context.SaveChangesAsync();
        return true;
    }

    private static double DegreesToRadians(double degrees)
    {
        return degrees * Math.PI / 180.0;
    }
}
