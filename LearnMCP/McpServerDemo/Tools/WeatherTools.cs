using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace McpServerDemo.Tools;

// ============================================================
// Tool 3: Weather — Tool returning structured data
// ============================================================
// This demonstrates returning richer data (as JSON).
// In a real app, you'd call a weather API here.

[McpServerToolType]
public static class WeatherTools
{
    [McpServerTool, Description("Get the current weather for a city (simulated data)")]
    public static string GetWeather(
        [Description("The name of the city")] string city)
    {
        // Simulated weather data — in a real server you'd call an external API
        var random = new Random(city.GetHashCode());
        var weather = new
        {
            City = city,
            Temperature = random.Next(-10, 40),
            Unit = "°C",
            Condition = new[] { "Sunny", "Cloudy", "Rainy", "Snowy", "Windy" }[random.Next(5)],
            Humidity = random.Next(20, 100),
            WindSpeed = random.Next(0, 50)
        };

        return JsonSerializer.Serialize(weather, new JsonSerializerOptions { WriteIndented = true });
    }
}
