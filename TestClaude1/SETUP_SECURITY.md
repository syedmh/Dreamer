# Security Setup Guide

This guide will help you configure the security features of the Weather Service API.

## 1. Configure OpenWeatherMap API Key

### Option A: Using User Secrets (Recommended for Development)

User Secrets keep your API keys out of source control.

```bash
# Initialize user secrets (if not already done)
dotnet user-secrets init

# Set your OpenWeatherMap API key
dotnet user-secrets set "OpenWeatherMap:ApiKey" "YOUR_OPENWEATHERMAP_API_KEY_HERE"

# Verify the secret was set
dotnet user-secrets list
```

### Option B: Using appsettings.Development.json (Local Development)

**Note:** This file is now in `.gitignore` and won't be committed to source control.

1. Create `appsettings.Development.json` in the project root:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "OpenWeatherMap": {
    "ApiKey": "YOUR_OPENWEATHERMAP_API_KEY_HERE"
  },
  "ApiKeyAuthentication": {
    "HeaderName": "X-API-Key",
    "ValidApiKeys": []
  }
}
```

### Option C: Using Environment Variables (Production)

For production deployment:

**Windows:**
```cmd
setx OpenWeatherMap__ApiKey "YOUR_OPENWEATHERMAP_API_KEY_HERE"
```

**Linux/macOS:**
```bash
export OpenWeatherMap__ApiKey="YOUR_OPENWEATHERMAP_API_KEY_HERE"
```

**Docker:**
```yaml
environment:
  - OpenWeatherMap__ApiKey=YOUR_OPENWEATHERMAP_API_KEY_HERE
```

---

## 2. Configure API Key Authentication

The API now requires consumers to provide an API key to access the weather endpoints.

### Development Mode (No Authentication)

By default, if no API keys are configured, authentication is **disabled** for easier development.

### Enable API Key Authentication

1. **Generate API Keys for your consumers:**

```bash
# Generate a secure random API key (PowerShell)
[System.Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))

# Or use an online generator: https://randomkeygen.com/
```

2. **Add API keys using User Secrets:**

```bash
dotnet user-secrets set "ApiKeyAuthentication:ValidApiKeys:0" "your-first-api-key-here"
dotnet user-secrets set "ApiKeyAuthentication:ValidApiKeys:1" "your-second-api-key-here"
```

3. **Or add to appsettings.Development.json:**

```json
{
  "ApiKeyAuthentication": {
    "HeaderName": "X-API-Key",
    "ValidApiKeys": [
      "abc123-secure-key-1",
      "xyz789-secure-key-2"
    ]
  }
}
```

### Using the API with Authentication

Once enabled, all API requests must include the `X-API-Key` header:

```bash
curl -H "X-API-Key: abc123-secure-key-1" https://localhost:5001/api/weather/London
```

```powershell
$headers = @{ "X-API-Key" = "abc123-secure-key-1" }
Invoke-RestMethod -Uri "https://localhost:5001/api/weather/London" -Headers $headers
```

---

## 3. Rate Limiting

Rate limiting is **automatically enabled** with the following defaults:

- **100 requests per minute** per IP address
- **429 status code** when limit exceeded
- **No request queuing**

### Customize Rate Limiting

Edit `Program.cs` to adjust limits:

```csharp
options.AddFixedWindowLimiter("fixed", limiterOptions =>
{
    limiterOptions.PermitLimit = 200; // Change to 200 requests
    limiterOptions.Window = TimeSpan.FromMinutes(5); // per 5 minutes
    limiterOptions.QueueLimit = 10; // Allow 10 queued requests
});
```

### Testing Rate Limits

```bash
# Send 101 requests quickly to trigger rate limiting
for i in {1..101}; do
  curl -H "X-API-Key: your-key" https://localhost:5001/api/weather/London
done
```

Expected response when limit exceeded:
```json
{
  "error": "Rate limit exceeded",
  "status": 429
}
```

---

## 4. Input Validation

Input validation is automatically applied to all endpoints:

### City Name Validation
- **Min Length:** 1 character
- **Max Length:** 100 characters
- **Required:** Yes
- **Trimmed:** Whitespace removed automatically

### Country Code Validation
- **Format:** 2 uppercase letters (ISO 3166)
- **Examples:** US, GB, FR, JP
- **Optional:** Yes

### Coordinates Validation
- **Latitude:** -90 to 90
- **Longitude:** -180 to 180
- **Required:** Yes

### Error Responses

Invalid city name:
```json
{
  "error": "City name cannot be empty or whitespace"
}
```

Invalid country code:
```json
{
  "errors": {
    "countryCode": ["Country code must be 2 uppercase letters (ISO 3166)"]
  }
}
```

Invalid coordinates:
```json
{
  "error": "Invalid latitude",
  "message": "Latitude must be between -90 and 90 degrees"
}
```

---

## 5. Security Headers

The following security headers are automatically added to all responses:

| Header | Value | Purpose |
|--------|-------|---------|
| X-Content-Type-Options | nosniff | Prevent MIME-sniffing |
| X-Frame-Options | DENY | Prevent clickjacking |
| X-XSS-Protection | 1; mode=block | Enable XSS protection |
| Referrer-Policy | strict-origin-when-cross-origin | Control referrer info |
| Strict-Transport-Security | max-age=31536000 | Enforce HTTPS (production only) |

---

## 6. Health Check Endpoint

A health check endpoint is available without authentication:

```bash
GET /health
```

Response:
```json
{
  "status": "Healthy",
  "timestamp": "2026-01-13T10:30:00Z"
}
```

---

## 7. Testing the Security Features

### Test 1: Missing API Key (if authentication enabled)
```bash
curl https://localhost:5001/api/weather/London
# Expected: 401 Unauthorized
```

### Test 2: Invalid API Key
```bash
curl -H "X-API-Key: invalid-key" https://localhost:5001/api/weather/London
# Expected: 401 Unauthorized
```

### Test 3: Valid API Key
```bash
curl -H "X-API-Key: abc123-secure-key-1" https://localhost:5001/api/weather/London
# Expected: 200 OK with weather data
```

### Test 4: Input Validation
```bash
curl -H "X-API-Key: your-key" https://localhost:5001/api/weather/coordinates?lat=1000&lon=0
# Expected: 400 Bad Request - Invalid latitude
```

### Test 5: Rate Limiting
```bash
# Run this script to exceed rate limit
for i in {1..101}; do
  curl -H "X-API-Key: your-key" https://localhost:5001/api/weather/London
done
# Expected: Last requests return 429 Too Many Requests
```

---

## 8. Production Deployment Checklist

Before deploying to production:

- [ ] Store API keys in secure key management (Azure Key Vault, AWS Secrets Manager)
- [ ] Enable API key authentication (add valid consumer keys)
- [ ] Configure appropriate rate limits for your use case
- [ ] Set up monitoring and alerting
- [ ] Review and adjust security headers
- [ ] Enable HTTPS (required for HSTS)
- [ ] Set up logging aggregation
- [ ] Configure CORS for allowed origins
- [ ] Review input validation rules
- [ ] Test all security features thoroughly

---

## 9. Security Best Practices

1. **Never commit API keys** to source control
2. **Rotate API keys regularly** (every 90 days)
3. **Use HTTPS** in production
4. **Monitor API usage** for suspicious activity
5. **Keep dependencies updated** regularly
6. **Enable authentication** before public deployment
7. **Implement logging** for security events
8. **Regular security audits**

---

## 10. Troubleshooting

### Authentication Not Working

1. Check if API keys are configured:
   ```bash
   dotnet user-secrets list
   ```

2. Verify header name matches configuration (default: `X-API-Key`)

3. Ensure no whitespace in API key values

### Rate Limiting Too Restrictive

Adjust limits in `Program.cs` based on your needs:
- Increase `PermitLimit` for more requests
- Increase `Window` timespan for longer periods
- Add `QueueLimit` to queue excess requests

### API Key Not Loading

Check configuration priority:
1. User Secrets (highest priority)
2. Environment Variables
3. appsettings.Development.json
4. appsettings.json (lowest priority)

---

## Need Help?

- Review the [SECURITY_AUDIT_REPORT.md](SECURITY_AUDIT_REPORT.md) for detailed security analysis
- Check [README.md](README.md) for general setup instructions
- See [API_DOCUMENTATION.md](API_DOCUMENTATION.md) for API usage examples

---

*Security is an ongoing process. Regular reviews and updates are recommended.*
