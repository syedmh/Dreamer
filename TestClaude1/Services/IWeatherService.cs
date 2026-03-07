using WeatherService.Models;

namespace WeatherService.Services
{
    /// <summary>
    /// Interface for weather service operations
    /// </summary>
    public interface IWeatherService
    {
        /// <summary>
        /// Gets current weather data for a specific city
        /// </summary>
        /// <param name="city">The name of the city</param>
        /// <param name="countryCode">Optional ISO 3166 country code (e.g., "US", "GB")</param>
        /// <returns>Weather data for the specified city</returns>
        Task<WeatherData?> GetWeatherByCityAsync(string city, string? countryCode = null);

        /// <summary>
        /// Gets current weather data by geographic coordinates
        /// </summary>
        /// <param name="latitude">Latitude coordinate</param>
        /// <param name="longitude">Longitude coordinate</param>
        /// <returns>Weather data for the specified location</returns>
        Task<WeatherData?> GetWeatherByCoordinatesAsync(double latitude, double longitude);
    }
}
