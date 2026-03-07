# Weather Service API

A simple ASP.NET Core 8.0 Web API for retrieving **live weather data** from cities around the world using the OpenWeatherMap API.

## Quick Start Guide

### Prerequisites

- Visual Studio 2022
- .NET 8.0 SDK or later
- Windows, macOS, or Linux
- **OpenWeatherMap API Key** (free tier available)

### Getting Your OpenWeatherMap API Key

1. Visit [OpenWeatherMap](https://openweathermap.org/)
2. Click "Sign Up" and create a free account
3. After signing in, go to "API keys" in your account dashboard
4. Copy your API key (or generate a new one)
5. Open `appsettings.json` or `appsettings.Development.json` in the project
6. Replace `YOUR_API_KEY_HERE` with your actual API key:

```json
{
  "OpenWeatherMap": {
    "ApiKey": "your_actual_api_key_here"
  }
}
```

**Note:** The free tier allows 1,000 API calls per day, which is sufficient for development and testing.

### Running the Application

#### Option 1: Visual Studio 2022

1. Open `WeatherService.csproj` in Visual Studio 2022
2. Press `F5` to run in Debug mode or `Ctrl+F5` to run without debugging
3. The browser will automatically open to the Swagger UI at `https://localhost:5001/swagger`

#### Option 2: Command Line

```bash
# Navigate to the project directory
cd Q:\Source\TestClaude1

# Restore dependencies
dotnet restore

# Build the project
dotnet build

# Run the application
dotnet run
```

The API will be available at:
- HTTPS: `https://localhost:5001`
- HTTP: `http://localhost:5000`
- Swagger UI: `https://localhost:5001/swagger`

### Testing the API

#### Using Swagger UI

1. Navigate to `https://localhost:5001/swagger`
2. Expand any endpoint
3. Click "Try it out"
4. Enter parameters (if required)
5. Click "Execute"
6. View the response

#### Using cURL

```bash
# Get weather for a specific city
curl https://localhost:5001/api/weather/London

# Get weather for a city with country code
curl https://localhost:5001/api/weather/Paris?countryCode=FR

# Get weather by coordinates (London)
curl "https://localhost:5001/api/weather/coordinates?lat=51.5074&lon=-0.1278"
```

#### Using PowerShell

```powershell
# Get weather for a specific city
Invoke-RestMethod -Uri https://localhost:5001/api/weather/London -Method Get

# Get weather for a city with country code
Invoke-RestMethod -Uri "https://localhost:5001/api/weather/Paris?countryCode=FR" -Method Get

# Get weather by coordinates (Tokyo)
Invoke-RestMethod -Uri "https://localhost:5001/api/weather/coordinates?lat=35.6762&lon=139.6503" -Method Get
```

## Available Endpoints

See [API_DOCUMENTATION.md](API_DOCUMENTATION.md) for detailed endpoint documentation.

## Project Structure

```
WeatherService/
├── Controllers/
│   └── WeatherController.cs    # API endpoints for weather data
├── Models/
│   └── WeatherData.cs          # Weather data model
├── Services/
│   ├── IWeatherService.cs      # Weather service interface
│   └── OpenWeatherMapService.cs # OpenWeatherMap API integration
├── Properties/
│   └── launchSettings.json     # Launch configuration
├── Program.cs                   # Application entry point
├── WeatherService.csproj       # Project file
├── appsettings.json            # Application configuration
└── appsettings.Development.json # Development configuration
```

## Features

- **Live Weather Data**: Real-time weather information from OpenWeatherMap
- **City Search**: Get weather by city name with optional country code
- **Coordinate Search**: Get weather by latitude and longitude
- **RESTful API**: Clean, well-documented REST endpoints
- **Swagger UI**: Interactive API documentation
- **Dependency Injection**: Clean architecture with service layer
- **Logging**: Built-in logging for debugging and monitoring

## Configuration

### Changing the Port

Edit `Properties/launchSettings.json` to change the application URL:

```json
"applicationUrl": "https://localhost:YOUR_PORT;http://localhost:YOUR_PORT"
```

### API Key Security

**Important:** Never commit your API key to source control!

1. Add `appsettings.Development.json` to `.gitignore` (already included)
2. Use User Secrets for development:

```bash
dotnet user-secrets init
dotnet user-secrets set "OpenWeatherMap:ApiKey" "your_api_key_here"
```

3. For production, use environment variables or Azure Key Vault

## Next Steps

- **Caching**: Implement Redis or in-memory caching to reduce API calls
- **Rate Limiting**: Add rate limiting to protect against abuse
- **Authentication**: Add JWT authentication for secured endpoints
- **Database Integration**: Store historical weather data
- **Additional Providers**: Support multiple weather APIs (Weather.com, AccuWeather)
- **Forecasts**: Add 5-day weather forecast endpoints
- **Unit Tests**: Add xUnit or NUnit tests with mocked services
- **Docker**: Containerize the application

## Troubleshooting

### API Key Not Configured

If you see an error about missing API key:
1. Ensure you've added your API key to `appsettings.json` or `appsettings.Development.json`
2. Check that the key is valid and active in your OpenWeatherMap account
3. New API keys may take a few minutes to activate

### 401 Unauthorized from OpenWeatherMap

Your API key is invalid or not activated yet. Wait a few minutes or generate a new key.

### 404 City Not Found

- Check the city name spelling
- Try adding a country code: `/api/weather/Paris?countryCode=FR`
- Use coordinates instead: `/api/weather/coordinates?lat=48.8566&lon=2.3522`

### Port Already in Use

If you see an error about the port being in use, change the port in `launchSettings.json`.

### SSL Certificate Issues

If you encounter SSL certificate warnings, trust the development certificate:

```bash
dotnet dev-certs https --trust
```

### Build Errors

Ensure you have .NET 8.0 SDK installed:

```bash
dotnet --version
```

## Resources

- [ASP.NET Core Documentation](https://learn.microsoft.com/en-us/aspnet/core/)
- [OpenWeatherMap API Documentation](https://openweathermap.org/api)
- [Swagger/OpenAPI](https://swagger.io/docs/)
- [REST API Best Practices](https://learn.microsoft.com/en-us/azure/architecture/best-practices/api-design)

## License

This project is provided as-is for educational purposes.
