using System.Text.Json;
using WeatherService.Models;

namespace WeatherService.Services
{
    /// <summary>
    /// Service for fetching weather data from OpenWeatherMap API
    /// </summary>
    public class OpenWeatherMapService : IWeatherService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly ILogger<OpenWeatherMapService> _logger;
        private const string BaseUrl = "https://api.openweathermap.org/data/2.5/weather";

        public OpenWeatherMapService(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<OpenWeatherMapService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;

            // Get API key from configuration
            _apiKey = configuration["OpenWeatherMap:ApiKey"]
                ?? throw new InvalidOperationException("OpenWeatherMap API key is not configured");
        }

        /// <summary>
        /// Gets current weather data for a specific city
        /// </summary>
        public async Task<WeatherData?> GetWeatherByCityAsync(string city, string? countryCode = null)
        {
            try
            {
                // Build query string with city and optional country code
                var query = string.IsNullOrEmpty(countryCode)
                    ? city
                    : $"{city},{countryCode}";

                var url = $"{BaseUrl}?q={Uri.EscapeDataString(query)}&appid={_apiKey}&units=metric";

                // Log without exposing the API key
                _logger.LogInformation("Fetching weather data for: {Query} from OpenWeatherMap", query);

                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Failed to fetch weather data. Status: {StatusCode}, Response: {ErrorContent}",
                        response.StatusCode, errorContent);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                var openWeatherData = JsonSerializer.Deserialize<OpenWeatherMapResponse>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (openWeatherData == null)
                {
                    _logger.LogWarning("Failed to deserialize weather data");
                    return null;
                }

                // Map OpenWeatherMap response to our WeatherData model
                return MapToWeatherData(openWeatherData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching weather data for city: {City}", city);
                return null;
            }
        }

        /// <summary>
        /// Gets current weather data by geographic coordinates
        /// </summary>
        public async Task<WeatherData?> GetWeatherByCoordinatesAsync(double latitude, double longitude)
        {
            try
            {
                var url = $"{BaseUrl}?lat={latitude}&lon={longitude}&appid={_apiKey}&units=metric";

                // Log without exposing the API key
                _logger.LogInformation("Fetching weather data for coordinates: {Lat}, {Lon} from OpenWeatherMap", latitude, longitude);

                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Failed to fetch weather data. Status: {StatusCode}, Response: {ErrorContent}",
                        response.StatusCode, errorContent);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                var openWeatherData = JsonSerializer.Deserialize<OpenWeatherMapResponse>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (openWeatherData == null)
                {
                    _logger.LogWarning("Failed to deserialize weather data");
                    return null;
                }

                return MapToWeatherData(openWeatherData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching weather data for coordinates: {Lat}, {Lon}", latitude, longitude);
                return null;
            }
        }

        /// <summary>
        /// Maps OpenWeatherMap API response to our WeatherData model
        /// </summary>
        private WeatherData MapToWeatherData(OpenWeatherMapResponse response)
        {
            return new WeatherData
            {
                City = response.Name ?? "Unknown",
                Country = response.Sys?.Country ?? "Unknown",
                Temperature = response.Main?.Temp ?? 0,
                TemperatureUnit = "Celsius",
                Description = response.Weather?.FirstOrDefault()?.Description ?? "Unknown",
                Humidity = response.Main?.Humidity ?? 0,
                WindSpeed = response.Wind?.Speed ?? 0,
                Timestamp = DateTime.UtcNow
            };
        }
    }

    #region OpenWeatherMap API Response Models

    /// <summary>
    /// Response model from OpenWeatherMap API
    /// </summary>
    public class OpenWeatherMapResponse
    {
        public string? Name { get; set; }
        public MainData? Main { get; set; }
        public List<WeatherInfo>? Weather { get; set; }
        public WindData? Wind { get; set; }
        public SysData? Sys { get; set; }
    }

    public class MainData
    {
        public double Temp { get; set; }
        public int Humidity { get; set; }
    }

    public class WeatherInfo
    {
        public string? Description { get; set; }
    }

    public class WindData
    {
        public double Speed { get; set; }
    }

    public class SysData
    {
        public string? Country { get; set; }
    }

    #endregion
}
