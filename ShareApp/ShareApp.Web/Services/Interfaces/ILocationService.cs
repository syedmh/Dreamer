using ShareApp.Web.Entities;

namespace ShareApp.Web.Services.Interfaces;

public interface ILocationService
{
    Task<Location?> GetLocationByIdAsync(int id);
    Task<IEnumerable<Location>> GetUserLocationsAsync(string userId);
    Task<int> CreateLocationAsync(Location location);
    Task<bool> UpdateLocationAsync(Location location);
    Task<bool> DeleteLocationAsync(int id, string userId);
    Task<(decimal Latitude, decimal Longitude)?> GeocodeAddressAsync(string address);
    double CalculateDistance(decimal lat1, decimal lon1, decimal lat2, decimal lon2);
    Task<bool> SetDefaultLocationAsync(int locationId, string userId);
}
