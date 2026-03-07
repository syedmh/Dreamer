using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using WeatherService.Models;
using WeatherService.Services;

namespace WeatherService.Controllers
{
    /// <summary>
    /// API Controller for managing weather data operations
    /// Uses live data from OpenWeatherMap API
    /// Requires API Key authentication via X-API-Key header
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class WeatherController : ControllerBase
    {
        private readonly IWeatherService _weatherService;
        private readonly ILogger<WeatherController> _logger;

        /// <summary>
        /// Constructor with dependency injection
        /// </summary>
        public WeatherController(IWeatherService weatherService, ILogger<WeatherController> logger)
        {
            _weatherService = weatherService;
            _logger = logger;
        }

        /// <summary>
        /// Gets current weather data for a specific city
        /// </summary>
        /// <param name="city">The name of the city (1-100 characters)</param>
        /// <param name="countryCode">Optional ISO 3166 country code (e.g., "US", "GB", "JP")</param>
        /// <returns>Current weather data for the specified city</returns>
        /// <response code="200">Returns the current weather data for the city</response>
        /// <response code="400">If input validation fails</response>
        /// <response code="404">If the city is not found or data cannot be retrieved</response>
        /// <example>GET /api/weather/London or /api/weather/London?countryCode=GB</example>
        [HttpGet("{city}")]
        public async Task<ActionResult<WeatherData>> GetWeatherByCity(
            [Required][StringLength(100, MinimumLength = 1, ErrorMessage = "City name must be between 1 and 100 characters")]
            string city,
            [RegularExpression(@"^[A-Z]{2}$", ErrorMessage = "Country code must be 2 uppercase letters (ISO 3166)")]
            [FromQuery] string? countryCode = null)
        {
            // Sanitize input - trim whitespace
            city = city.Trim();

            _logger.LogInformation("Received request for weather data: City={City}, CountryCode={CountryCode}",
                city, countryCode ?? "None");

            // Call the weather service to get live data
            var weather = await _weatherService.GetWeatherByCityAsync(city, countryCode);

            // Return 404 if city not found or service returned null
            if (weather == null)
            {
                _logger.LogWarning("Weather data not found for city: {City}", city);
                return NotFound(new
                {
                    error = "City not found",
                    message = $"Weather data for city '{city}' not found. Please check the city name and try again."
                });
            }

            return Ok(weather);
        }

        /// <summary>
        /// Gets current weather data by geographic coordinates
        /// </summary>
        /// <param name="lat">Latitude coordinate (-90 to 90)</param>
        /// <param name="lon">Longitude coordinate (-180 to 180)</param>
        /// <returns>Current weather data for the specified location</returns>
        /// <response code="200">Returns the current weather data for the location</response>
        /// <response code="400">If coordinate values are out of range</response>
        /// <response code="404">If weather data cannot be retrieved</response>
        /// <example>GET /api/weather/coordinates?lat=51.5074&amp;lon=-0.1278</example>
        [HttpGet("coordinates")]
        public async Task<ActionResult<WeatherData>> GetWeatherByCoordinates(
            [FromQuery][Range(-90, 90, ErrorMessage = "Latitude must be between -90 and 90")]
            double lat,
            [FromQuery][Range(-180, 180, ErrorMessage = "Longitude must be between -180 and 180")]
            double lon)
        {
            _logger.LogInformation("Received request for weather data: Latitude={Lat}, Longitude={Lon}", lat, lon);

            // Call the weather service to get live data by coordinates
            var weather = await _weatherService.GetWeatherByCoordinatesAsync(lat, lon);

            // Return 404 if data cannot be retrieved
            if (weather == null)
            {
                _logger.LogWarning("Weather data not found for coordinates: {Lat}, {Lon}", lat, lon);
                return NotFound(new
                {
                    error = "Location not found",
                    message = $"Weather data for coordinates ({lat}, {lon}) not found."
                });
            }

            return Ok(weather);
        }
    }
}
