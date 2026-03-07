# Husaynia Islamic Society Website - Deployment Guide

## Overview
This guide covers deploying the Husaynia website to production on Windows/Linux servers with IIS, Azure, or Docker.

## Prerequisites
- .NET 8.0 SDK and Runtime
- SQL Server or Azure SQL Database
- SSL Certificate for HTTPS
- Stripe Account (Production keys)
- Email service (SendGrid, SMTP, etc.)

## Pre-Deployment Checklist

### 1. Database Setup
```bash
# Update connection string in appsettings.Production.json
dotnet ef database update --project Husaynia.Infrastructure --startup-project Husaynia.Web
```

### 2. Environment Variables / User Secrets
Required secrets for production:
- `ConnectionStrings:DefaultConnection` - SQL Server connection string
- `Stripe:SecretKey` - Stripe production secret key
- `Stripe:PublishableKey` - Stripe production publishable key
- `Stripe:WebhookSecret` - Stripe webhook signing secret
- Email configuration (if implemented)

### 3. Configuration Files

#### appsettings.Production.json
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=your-server;Database=HusayniaDb;..."
  },
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "PrayerTimeSettings": {
    "Latitude": 47.9129,
    "Longitude": -122.0982,
    "TimeZone": "America/Los_Angeles",
    "FajrAngle": 16.0,
    "IshaAngle": 14.0
  },
  "SiteSettings": {
    "SiteName": "Husaynia Islamic Society of Seattle",
    "ContactEmail": "info@husaynia.org",
    "Address": "15231 State St, Snohomish, WA"
  },
  "Stripe": {
    "PublishableKey": "pk_live_...",
    "SecretKey": "sk_live_...",
    "WebhookSecret": "whsec_..."
  }
}
```

## Deployment Options

### Option 1: IIS Deployment (Windows Server)

1. **Publish the Application**
```bash
dotnet publish -c Release -o ./publish
```

2. **IIS Setup**
   - Install .NET 8.0 Hosting Bundle
   - Create new website in IIS
   - Point to published folder
   - Configure application pool (.NET CLR Version: No Managed Code)
   - Enable HTTPS with SSL certificate

3. **Web.config** (auto-generated, verify settings)
```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers>
        <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" />
      </handlers>
      <aspNetCore processPath="dotnet"
                  arguments=".\Husaynia.Web.dll"
                  stdoutLogEnabled="false"
                  stdoutLogFile=".\logs\stdout"
                  hostingModel="inprocess" />
    </system.webServer>
  </location>
</configuration>
```

### Option 2: Azure App Service

1. **Create Azure Resources**
   - App Service (B1 or higher)
   - Azure SQL Database
   - Application Insights (optional but recommended)

2. **Deploy via Visual Studio**
   - Right-click Husaynia.Web project
   - Select "Publish"
   - Choose Azure App Service
   - Configure and deploy

3. **Configure App Settings** in Azure Portal
   - Add all connection strings and secrets
   - Enable HTTPS Only
   - Configure custom domain and SSL

### Option 3: Docker Deployment

1. **Create Dockerfile** (in Husaynia.Web directory)
```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["Husaynia.Web/Husaynia.Web.csproj", "Husaynia.Web/"]
COPY ["Husaynia.Core/Husaynia.Core.csproj", "Husaynia.Core/"]
COPY ["Husaynia.Infrastructure/Husaynia.Infrastructure.csproj", "Husaynia.Infrastructure/"]
RUN dotnet restore "Husaynia.Web/Husaynia.Web.csproj"
COPY . .
WORKDIR "/src/Husaynia.Web"
RUN dotnet build "Husaynia.Web.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Husaynia.Web.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Husaynia.Web.dll"]
```

2. **Build and Run**
```bash
docker build -t husaynia-web .
docker run -d -p 8080:80 --name husaynia husaynia-web
```

## Post-Deployment Steps

### 1. Database Initialization
- Run migrations to create database schema
- Seed initial data if needed
- Create admin user account

### 2. Stripe Configuration
- Configure webhook endpoint: `https://yourdomain.com/Donations/Webhook`
- Enable webhook events: `checkout.session.completed`
- Update webhook secret in configuration

### 3. Create Admin User
```csharp
// Run this via a seeding script or manually through Identity
var user = new AppUser { UserName = "admin@husaynia.org", Email = "admin@husaynia.org" };
await userManager.CreateAsync(user, "SecurePassword123!");
await userManager.AddToRoleAsync(user, "Admin");
```

### 4. Test Critical Functionality
- [ ] Homepage loads correctly
- [ ] Prayer times display accurately
- [ ] Announcements and events visible
- [ ] Media galleries load
- [ ] Donation form works with Stripe test cards
- [ ] Search functionality works
- [ ] Admin panel accessible
- [ ] Sitemap.xml generates correctly
- [ ] HTTPS enforced
- [ ] Mobile responsiveness

### 5. Monitoring Setup
- Configure Application Insights or similar
- Set up error logging
- Enable health checks
- Monitor performance metrics

## Security Hardening

### 1. HTTPS/SSL
- Enforce HTTPS in production
- Configure HSTS headers (already in code)
- Obtain and install valid SSL certificate

### 2. Security Headers
Already implemented in code:
- HSTS
- Content Security Policy
- X-Content-Type-Options
- X-Frame-Options

### 3. Authentication
- Use strong passwords for admin accounts
- Enable two-factor authentication if available
- Regularly update ASP.NET Core Identity

### 4. API Keys
- Store all secrets in User Secrets (dev) or Azure Key Vault (prod)
- Never commit secrets to source control
- Rotate keys regularly

## Performance Optimization

### Already Implemented
- Response caching
- Memory caching for prayer times
- ImageSharp for optimized image serving
- Database query optimization with EF Core

### Additional Recommendations
- Enable CDN for static assets
- Configure Redis cache for distributed caching
- Enable database query performance insights
- Set up application performance monitoring

## Backup Strategy
- Database: Daily automated backups
- Uploaded media: Regular backups to Azure Blob Storage or S3
- Configuration: Version controlled in Git

## Testing Checklist

### Browser Compatibility
- [ ] Chrome (latest)
- [ ] Firefox (latest)
- [ ] Safari (latest)
- [ ] Edge (latest)

### Mobile Testing
- [ ] iOS Safari
- [ ] Android Chrome
- [ ] Responsive layouts (320px - 1920px)

### Functional Testing
- [ ] User registration/login
- [ ] Announcement CRUD
- [ ] Event CRUD
- [ ] Media upload
- [ ] Donation flow
- [ ] Search functionality
- [ ] Contact form submission
- [ ] Prayer times accuracy

### Payment Testing (Stripe Test Mode)
Test cards:
- Success: 4242 4242 4242 4242
- Declined: 4000 0000 0000 0002
- 3D Secure: 4000 0025 0000 3155

## Troubleshooting

### Common Issues

1. **Prayer times not showing**
   - Verify PrayerTimesCacheService is running
   - Check database connection
   - Verify latitude/longitude coordinates

2. **Stripe payments failing**
   - Confirm webhook secret is correct
   - Check Stripe dashboard for webhook delivery
   - Verify publishable/secret keys match environment

3. **Images not displaying**
   - Check file permissions in wwwroot/uploads
   - Verify ImageSharp middleware is configured
   - Check IIS static file handler

4. **Admin panel inaccessible**
   - Verify Identity tables created in database
   - Check user has Admin role
   - Confirm authentication cookies are set

## Support and Maintenance
- Monitor error logs daily
- Review Stripe dashboard for payment issues
- Update dependencies monthly
- Review security advisories

## Contact
For technical support:
- Email: tech@husaynia.org
- GitHub Issues: (repository URL)
