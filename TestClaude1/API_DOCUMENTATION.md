# Weather Service API Documentation

Complete API reference for the Weather Service REST API. This API provides **live weather data** from OpenWeatherMap.

## Base URL

```
https://localhost:5001/api/weather
```

## Authentication

Currently, this API does not require authentication. However, you must configure an OpenWeatherMap API key in the application settings.

## Response Format

All responses are returned in JSON format.

### Success Response

```json
{
  "city": "London",
  "country": "GB",
  "temperature": 15.3,
  "temperatureUnit": "Celsius",
  "description": "overcast clouds",
  "humidity": 82,
  "windSpeed": 4.12,
  "timestamp": "2026-01-08T12:00:00Z"
}
```

### Error Response

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.4",
  "title": "Not Found",
  "status": 404,
  "detail": "Weather data for city 'InvalidCity' not found. Please check the city name and try again."
}
```

## Endpoints

### 1. Get Weather by City

Retrieves current weather data for a specific city.

**Endpoint:** `GET /api/weather/{city}`

**Path Parameters:**
- `city` (string, required): The name of the city

**Query Parameters:**
- `countryCode` (string, optional): ISO 3166 country code (e.g., "US", "GB", "FR", "JP")

**Response Codes:**
- `200 OK`: Weather data found
- `404 Not Found`: City not found or data unavailable

**Example Requests:**

```bash
# Without country code
GET https://localhost:5001/api/weather/London

# With country code for disambiguation
GET https://localhost:5001/api/weather/Paris?countryCode=FR

# Using cURL
curl https://localhost:5001/api/weather/Tokyo

# Using cURL with country code
curl "https://localhost:5001/api/weather/Springfield?countryCode=US"
```

**Example Response (Success):**

```json
{
  "city": "London",
  "country": "GB",
  "temperature": 15.3,
  "temperatureUnit": "Celsius",
  "description": "overcast clouds",
  "humidity": 82,
  "windSpeed": 4.12,
  "timestamp": "2026-01-08T12:00:00.123Z"
}
```

**Example Response (Not Found):**

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.4",
  "title": "Not Found",
  "status": 404,
  "detail": "Weather data for city 'Atlantis' not found. Please check the city name and try again."
}
```

---

### 2. Get Weather by Coordinates

Retrieves current weather data for a specific geographic location.

**Endpoint:** `GET /api/weather/coordinates`

**Query Parameters:**
- `lat` (double, required): Latitude coordinate (-90 to 90)
- `lon` (double, required): Longitude coordinate (-180 to 180)

**Response Codes:**
- `200 OK`: Weather data found
- `404 Not Found`: Weather data unavailable

**Example Requests:**

```bash
# London coordinates
GET https://localhost:5001/api/weather/coordinates?lat=51.5074&lon=-0.1278

# Tokyo coordinates
GET https://localhost:5001/api/weather/coordinates?lat=35.6762&lon=139.6503

# Using cURL
curl "https://localhost:5001/api/weather/coordinates?lat=40.7128&lon=-74.0060"
```

**Example Response (Success):**

```json
{
  "city": "London",
  "country": "GB",
  "temperature": 15.3,
  "temperatureUnit": "Celsius",
  "description": "overcast clouds",
  "humidity": 82,
  "windSpeed": 4.12,
  "timestamp": "2026-01-08T12:00:00.123Z"
}
```

---

## Data Model

### WeatherData

| Field | Type | Description |
|-------|------|-------------|
| `city` | string | Name of the city |
| `country` | string | ISO 3166 country code (e.g., "US", "GB") |
| `temperature` | double | Temperature in Celsius |
| `temperatureUnit` | string | Unit of temperature (always "Celsius") |
| `description` | string | Weather description from OpenWeatherMap |
| `humidity` | integer | Humidity percentage (0-100) |
| `windSpeed` | double | Wind speed in meters per second |
| `timestamp` | datetime | When the data was retrieved (ISO 8601 format) |

## HTTP Status Codes

| Status Code | Description |
|------------|-------------|
| `200 OK` | Request successful |
| `404 Not Found` | City/location not found or data unavailable |
| `500 Internal Server Error` | Server error occurred |

## Rate Limiting

This API relies on OpenWeatherMap's free tier which allows:
- **1,000 API calls per day**
- **60 calls per minute**

Consider implementing caching to reduce API calls.

## Common City Names

Some example cities you can query:

| City | Country | Country Code |
|------|---------|--------------|
| New York | USA | US |
| London | United Kingdom | GB |
| Tokyo | Japan | JP |
| Paris | France | FR |
| Sydney | Australia | AU |
| Berlin | Germany | DE |
| Mumbai | India | IN |
| Toronto | Canada | CA |
| Dubai | UAE | AE |
| Singapore | Singapore | SG |

## Examples with Different Tools

### cURL

```bash
# Get weather by city
curl -X GET "https://localhost:5001/api/weather/London" -H "accept: application/json"

# Get weather by city with country code
curl -X GET "https://localhost:5001/api/weather/Paris?countryCode=FR" -H "accept: application/json"

# Get weather by coordinates
curl -X GET "https://localhost:5001/api/weather/coordinates?lat=35.6762&lon=139.6503" -H "accept: application/json"
```

### PowerShell

```powershell
# Get weather by city
Invoke-RestMethod -Uri "https://localhost:5001/api/weather/London" -Method Get

# Get weather by city with country code
Invoke-RestMethod -Uri "https://localhost:5001/api/weather/Paris?countryCode=FR" -Method Get

# Get weather by coordinates
Invoke-RestMethod -Uri "https://localhost:5001/api/weather/coordinates?lat=35.6762&lon=139.6503" -Method Get
```

### JavaScript (Fetch API)

```javascript
// Get weather by city
fetch('https://localhost:5001/api/weather/London')
  .then(response => response.json())
  .then(data => console.log(data))
  .catch(error => console.error('Error:', error));

// Get weather by city with country code
fetch('https://localhost:5001/api/weather/Paris?countryCode=FR')
  .then(response => response.json())
  .then(data => console.log(data))
  .catch(error => console.error('Error:', error));

// Get weather by coordinates
fetch('https://localhost:5001/api/weather/coordinates?lat=35.6762&lon=139.6503')
  .then(response => response.json())
  .then(data => console.log(data))
  .catch(error => console.error('Error:', error));
```

### C# (HttpClient)

```csharp
using System.Net.Http;
using System.Net.Http.Json;
using WeatherService.Models;

var client = new HttpClient();
client.BaseAddress = new Uri("https://localhost:5001");

// Get weather by city
var londonWeather = await client.GetFromJsonAsync<WeatherData>("/api/weather/London");
Console.WriteLine($"{londonWeather.City}: {londonWeather.Temperature}°C");

// Get weather by city with country code
var parisWeather = await client.GetFromJsonAsync<WeatherData>(
    "/api/weather/Paris?countryCode=FR");
Console.WriteLine($"{parisWeather.City}: {parisWeather.Temperature}°C");

// Get weather by coordinates
var weatherByCoords = await client.GetFromJsonAsync<WeatherData>(
    "/api/weather/coordinates?lat=35.6762&lon=139.6503");
Console.WriteLine($"{weatherByCoords.City}: {weatherByCoords.Temperature}°C");
```

### Python (requests)

```python
import requests

base_url = "https://localhost:5001/api/weather"

# Get weather by city
response = requests.get(f"{base_url}/London")
data = response.json()
print(f"{data['city']}: {data['temperature']}°C")

# Get weather by city with country code
response = requests.get(f"{base_url}/Paris", params={"countryCode": "FR"})
data = response.json()
print(f"{data['city']}: {data['temperature']}°C")

# Get weather by coordinates
response = requests.get(f"{base_url}/coordinates",
                       params={"lat": 35.6762, "lon": 139.6503})
data = response.json()
print(f"{data['city']}: {data['temperature']}°C")
```

## Error Handling Best Practices

1. **Always check HTTP status codes** before processing the response
2. **Handle 404 errors gracefully** - city might not be found
3. **Implement retry logic** for transient failures
4. **Add timeouts** to prevent hanging requests
5. **Cache responses** to reduce API calls

## Tips for Best Results

- **Use country codes** when querying common city names (e.g., "Paris, FR" vs "Paris, TX")
- **Cache weather data** for at least 10 minutes (weather doesn't change that frequently)
- **Use coordinates** for precise locations or when city names are ambiguous
- **Handle errors gracefully** - display user-friendly messages
- **Monitor API usage** to stay within free tier limits

## Future Enhancements

- 5-day weather forecast endpoint
- Historical weather data
- Weather alerts and warnings
- Multiple cities in a single request
- Weather data caching
- Support for additional weather providers

## Support

For issues or questions:
- Check the [README.md](README.md) for setup instructions
- Review [OpenWeatherMap API documentation](https://openweathermap.org/api)
- Ensure your API key is properly configured

## API Version

Current Version: 1.0
OpenWeatherMap API Version: 2.5
Last Updated: 2026-01-08
