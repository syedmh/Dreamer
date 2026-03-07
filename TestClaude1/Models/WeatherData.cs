namespace WeatherService.Models
{
    /// <summary>
    /// Represents weather data for a specific city
    /// </summary>
    public class WeatherData
    {
        /// <summary>
        /// Gets or sets the city name
        /// </summary>
        public string City { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the country name
        /// </summary>
        public string Country { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the temperature value
        /// </summary>
        public double Temperature { get; set; }

        /// <summary>
        /// Gets or sets the temperature unit (default: Celsius)
        /// </summary>
        public string TemperatureUnit { get; set; } = "Celsius";

        /// <summary>
        /// Gets or sets the weather description (e.g., Sunny, Rainy, Cloudy)
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the humidity percentage (0-100)
        /// </summary>
        public int Humidity { get; set; }

        /// <summary>
        /// Gets or sets the wind speed in km/h
        /// </summary>
        public double WindSpeed { get; set; }

        /// <summary>
        /// Gets or sets the timestamp when the weather data was recorded
        /// </summary>
        public DateTime Timestamp { get; set; }
    }
}
