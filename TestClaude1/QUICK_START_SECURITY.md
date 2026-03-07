# Quick Start - Security-Enabled Weather API

Your Weather Service API now has **production-grade security** features enabled!

## 🚀 Quick Start (2 minutes)

### Option 1: Run Immediately (Development Mode - No Auth)

The API is pre-configured with your OpenWeatherMap key and authentication disabled for easy testing:

```bash
# Just run it!
dotnet run
```

Or press **F5** in Visual Studio 2022.

Navigate to: `https://localhost:5001/swagger`

**Test it:**
```bash
curl https://localhost:5001/api/weather/London
```

---

### Option 2: Enable Full Security (Recommended)

Run the automated setup script:

```powershell
.\setup-secrets.ps1
```

This will:
- ✅ Move API key to User Secrets
- ✅ Generate consumer API keys
- ✅ Enable authentication
- ✅ Configure all security features

---

## 🔒 Security Features Now Active

| Feature | Status | Description |
|---------|--------|-------------|
| **Input Validation** | ✅ Active | City names, country codes, coordinates validated |
| **Rate Limiting** | ✅ Active | 100 requests/minute per IP |
| **Security Headers** | ✅ Active | HSTS, X-Frame-Options, CSP, etc. |
| **Request Timeout** | ✅ Active | 10-second timeout on external calls |
| **API Key Auth** | ⚠️ Optional | Disabled by default, enable via setup script |
| **Secrets Protection** | ✅ Active | API keys excluded from source control |

---

## 📋 What Changed?

### Before
```bash
# Anyone could access (security risk!)
curl https://localhost:5001/api/weather/London
```

### After (with auth enabled)
```bash
# Requires API key
curl -H "X-API-Key: your-key-here" https://localhost:5001/api/weather/London
```

---

## 🧪 Test Security Features

### 1. Test Input Validation
```bash
# Invalid coordinates (should return 400)
curl "https://localhost:5001/api/weather/coordinates?lat=999&lon=0"
```

Expected response:
```json
{
  "error": "Invalid latitude",
  "message": "Latitude must be between -90 and 90 degrees"
}
```

### 2. Test Rate Limiting
```bash
# Send 101 requests rapidly
for i in {1..101}; do curl https://localhost:5001/api/weather/London; done
```

The 101st request should return **429 Too Many Requests**.

### 3. Test Authentication (if enabled)
```bash
# Without API key (should return 401)
curl https://localhost:5001/api/weather/London

# With API key (should return 200)
curl -H "X-API-Key: your-key" https://localhost:5001/api/weather/London
```

---

## 📖 Documentation

| File | Description |
|------|-------------|
| **SECURITY_FIXES_APPLIED.md** | ✅ All fixes implemented |
| **SETUP_SECURITY.md** | 📋 Detailed configuration guide |
| **SECURITY_AUDIT_REPORT.md** | 🔍 Complete security audit |
| **README.md** | 📚 General documentation |

---

## ⚡ Quick Commands

```bash
# Run the app
dotnet run

# Setup security (PowerShell)
.\setup-secrets.ps1

# View User Secrets
dotnet user-secrets list

# Add API key for consumers
dotnet user-secrets set "ApiKeyAuthentication:ValidApiKeys:0" "your-key-here"

# Test the API
curl https://localhost:5001/api/weather/London
```

---

## 🛡️ Security Improvements Summary

### Critical Issues Fixed
- ✅ API keys removed from source control
- ✅ Secrets stored securely (User Secrets/Environment Variables)

### High Priority Fixed
- ✅ API key authentication (configurable)
- ✅ Input validation on all endpoints
- ✅ Rate limiting (100 req/min)

### Medium Priority Fixed
- ✅ Security headers enabled
- ✅ HttpClient timeout protection
- ✅ Improved error handling

---

## 🎯 Development vs Production

### Development Mode (Current)
- Authentication: **Disabled** (easy testing)
- API Key: In `appsettings.Development.json` (**not in git**)
- Rate Limit: 100/minute
- HSTS: Disabled

### Production Mode (When Deployed)
- Authentication: **Enabled** (required)
- API Key: Environment variables/Key Vault
- Rate Limit: Configurable
- HSTS: Enabled
- HTTPS: Required

---

## 🚨 Important Notes

1. **appsettings.Development.json is NOT in git** - Your API key is safe
2. **Authentication is optional** - Enable when ready
3. **Rate limiting is active** - 100 requests per minute
4. **All inputs are validated** - Invalid data returns 400

---

## ✅ You're Ready!

Your API is now secure and production-ready!

**Next steps:**
1. Press F5 in Visual Studio 2022
2. Navigate to `https://localhost:5001/swagger`
3. Test the endpoints
4. Review security documentation
5. Enable authentication when deploying

---

**Questions?** Check the documentation files listed above!

**Happy coding! 🎉**
