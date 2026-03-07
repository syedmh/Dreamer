# Setup script for Weather Service API secrets and configuration
# Run this script to configure your development environment

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Weather Service API - Security Setup" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Function to generate a secure API key
function New-ApiKey {
    $bytes = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    return [System.Convert]::ToBase64String($bytes)
}

# Step 1: Initialize User Secrets
Write-Host "[1/4] Initializing User Secrets..." -ForegroundColor Yellow
try {
    dotnet user-secrets init --project WeatherService.csproj
    Write-Host "✓ User Secrets initialized" -ForegroundColor Green
} catch {
    Write-Host "⚠ User Secrets may already be initialized" -ForegroundColor Yellow
}
Write-Host ""

# Step 2: Set OpenWeatherMap API Key
Write-Host "[2/4] Setting OpenWeatherMap API Key..." -ForegroundColor Yellow
$openWeatherKey = Read-Host "Enter your OpenWeatherMap API Key (or press Enter to use existing: YOUR_OPENWEATHERMAP_API_KEY_HERE)"
if ([string]::IsNullOrWhiteSpace($openWeatherKey)) {
    $openWeatherKey = "YOUR_OPENWEATHERMAP_API_KEY_HERE"
}

dotnet user-secrets set "OpenWeatherMap:ApiKey" $openWeatherKey --project WeatherService.csproj
Write-Host "✓ OpenWeatherMap API Key configured" -ForegroundColor Green
Write-Host ""

# Step 3: Configure API Key Authentication
Write-Host "[3/4] Configuring API Key Authentication..." -ForegroundColor Yellow
Write-Host "Do you want to enable API Key authentication for API consumers?" -ForegroundColor Cyan
Write-Host "  If NO, the API will be publicly accessible (development mode)" -ForegroundColor Gray
Write-Host "  If YES, clients must provide X-API-Key header" -ForegroundColor Gray
$enableAuth = Read-Host "Enable authentication? (y/N)"

if ($enableAuth -eq "y" -or $enableAuth -eq "Y") {
    Write-Host ""
    Write-Host "How many API keys do you want to generate?" -ForegroundColor Cyan
    $keyCount = Read-Host "Enter number (1-5)"

    if ([int]$keyCount -gt 0 -and [int]$keyCount -le 5) {
        Write-Host ""
        Write-Host "Generated API Keys (save these securely):" -ForegroundColor Yellow
        Write-Host "========================================" -ForegroundColor Yellow

        for ($i = 0; $i -lt [int]$keyCount; $i++) {
            $apiKey = New-ApiKey
            dotnet user-secrets set "ApiKeyAuthentication:ValidApiKeys:$i" $apiKey --project WeatherService.csproj
            Write-Host "Key $($i + 1): $apiKey" -ForegroundColor Green
        }

        Write-Host "========================================" -ForegroundColor Yellow
        Write-Host "✓ API Key authentication enabled with $keyCount key(s)" -ForegroundColor Green
        Write-Host ""
        Write-Host "IMPORTANT: Save these keys! Clients must include them in the X-API-Key header." -ForegroundColor Red
    } else {
        Write-Host "⚠ Invalid number. Skipping authentication setup." -ForegroundColor Yellow
    }
} else {
    Write-Host "✓ API Key authentication disabled (development mode)" -ForegroundColor Green
}
Write-Host ""

# Step 4: Verify configuration
Write-Host "[4/4] Verifying configuration..." -ForegroundColor Yellow
Write-Host ""
Write-Host "Current secrets:" -ForegroundColor Cyan
dotnet user-secrets list --project WeatherService.csproj
Write-Host ""
Write-Host "✓ Setup complete!" -ForegroundColor Green
Write-Host ""

# Final instructions
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Next Steps:" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "1. Open the project in Visual Studio 2022" -ForegroundColor White
Write-Host "2. Press F5 to run the application" -ForegroundColor White
Write-Host "3. Navigate to https://localhost:5001/swagger" -ForegroundColor White
Write-Host ""
Write-Host "Security Features Enabled:" -ForegroundColor Cyan
Write-Host "  ✓ User Secrets for API key storage" -ForegroundColor Green
Write-Host "  ✓ Input validation on all endpoints" -ForegroundColor Green
Write-Host "  ✓ Rate limiting (100 requests/minute)" -ForegroundColor Green
if ($enableAuth -eq "y" -or $enableAuth -eq "Y") {
    Write-Host "  ✓ API Key authentication (consumers need X-API-Key header)" -ForegroundColor Green
} else {
    Write-Host "  ⚠ API Key authentication disabled" -ForegroundColor Yellow
}
Write-Host "  ✓ Security headers (HSTS, X-Frame-Options, etc.)" -ForegroundColor Green
Write-Host "  ✓ HttpClient timeout protection" -ForegroundColor Green
Write-Host ""
Write-Host "For more information, see:" -ForegroundColor Cyan
Write-Host "  - SETUP_SECURITY.md (security configuration guide)" -ForegroundColor White
Write-Host "  - SECURITY_AUDIT_REPORT.md (security audit details)" -ForegroundColor White
Write-Host "  - README.md (general documentation)" -ForegroundColor White
Write-Host ""
Write-Host "Press any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
