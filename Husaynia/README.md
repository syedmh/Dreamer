# Husaynia Islamic Society Website

A comprehensive ASP.NET Core 8.0 MVC website for Husaynia Islamic Society of Seattle, featuring prayer times, event management, donation processing, media galleries, and more.

## 🌟 Features

### Core Functionality
- **Prayer Times**: Accurate prayer time calculations using custom astronomical algorithms (16° Fajr, 14° Isha)
- **Event Calendar**: Interactive FullCalendar.js integration with iCal export
- **Announcements**: News and updates with pagination and pinned posts
- **Media Galleries**: Photo and video management with ImageSharp thumbnails
- **Donations**: Stripe-powered donation system with campaign tracking
- **Search**: Full-text search across announcements and events
- **Admin Panel**: Complete administrative interface for content management

### Technical Features
- Clean Architecture (3-project solution)
- Entity Framework Core with SQL Server
- ASP.NET Core Identity for authentication
- Bootstrap 5 responsive design
- SEO optimized (sitemap.xml, robots.txt, OpenGraph tags)
- Response caching and memory caching
- Background services for automated tasks

## 🏗️ Architecture

```
Husaynia.sln
├── Husaynia.Web (ASP.NET Core MVC)
│   ├── Controllers/
│   ├── Views/
│   ├── Areas/Admin/
│   ├── Services/
│   └── wwwroot/
├── Husaynia.Core (Domain Entities & Interfaces)
│   ├── Entities/
│   ├── Enums/
│   └── Interfaces/
└── Husaynia.Infrastructure (Data Access & External Services)
    ├── Data/
    ├── Repositories/
    └── ExternalServices/
```

## 🚀 Quick Start

### Prerequisites
- .NET 8.0 SDK
- SQL Server or SQL Server Express
- Visual Studio 2022 or VS Code
- Stripe Account (for donations)

### Installation

1. **Clone the repository**
```bash
git clone https://github.com/your-org/husaynia-website.git
cd husaynia-website
```

2. **Update connection string**
Edit `Husaynia.Web/appsettings.json`:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=(localdb)\\mssqllocaldb;Database=HusayniaDb;Trusted_Connection=True;MultipleActiveResultSets=true"
  }
}
```

3. **Run database migrations**
```bash
cd Husaynia.Infrastructure
dotnet ef database update --startup-project ../Husaynia.Web
```

4. **Configure Stripe (optional for development)**
Add your Stripe test keys to User Secrets:
```bash
cd Husaynia.Web
dotnet user-secrets set "Stripe:SecretKey" "sk_test_your_key"
dotnet user-secrets set "Stripe:PublishableKey" "pk_test_your_key"
```

5. **Run the application**
```bash
cd Husaynia.Web
dotnet run
```

Navigate to `https://localhost:5001`

## 📦 NuGet Packages

### Core Dependencies
- Microsoft.EntityFrameworkCore.SqlServer (8.0.*)
- Microsoft.AspNetCore.Identity.EntityFrameworkCore (8.0.*)
- Stripe.net (44.*)
- SixLabors.ImageSharp (3.*)
- Ical.Net (5.*)

## 🗄️ Database Schema

### Main Tables
- **Announcements**: News and updates
- **Events**: Calendar events and programs
- **MediaItems**: Photos and videos
- **Donations**: Donation records
- **DonationCampaigns**: Fundraising campaigns
- **PrayerTimes**: Cached prayer time calculations
- **SiteConfiguration**: Dynamic site settings
- **AspNetUsers**: Identity user accounts

## 🎨 Features Walkthrough

### 1. Prayer Times System
- Automatic calculation for Snohomish, WA (47.9129°N, 122.0982°W)
- Custom angles: 16° for Fajr, 14° for Isha
- 30-day ahead caching
- Background service updates daily at midnight
- Monthly prayer schedule view

### 2. Event Management
- FullCalendar.js interactive calendar
- Multiple view modes (month, week, day, list)
- Category color coding
- iCal export (single event or full calendar)
- Google Calendar integration
- Social sharing (Facebook, Twitter, Email)

### 3. Donation System
- Stripe Checkout integration
- Campaign progress tracking ($2.9M / $6.9M goal)
- One-time and recurring donations
- Email receipts
- Webhook support for payment confirmation
- Anonymous donation option

### 4. Media Galleries
- Photo upload with automatic thumbnail generation
- Album organization
- PhotoSwipe lightbox integration
- YouTube/Vimeo video embeds
- Lazy loading for performance

### 5. Admin Panel
- Dashboard with statistics
- Announcements CRUD
- Events CRUD
- Media management
- Donation reports
- Role-based authorization

### 6. Search & SEO
- Cross-content search (announcements, events)
- Dynamic sitemap.xml generation
- OpenGraph tags for social sharing
- Twitter Card support
- robots.txt configuration
- Canonical URLs

## 🔐 Security

### Implemented Security Features
- ASP.NET Core Identity authentication
- Role-based authorization
- HSTS (HTTP Strict Transport Security)
- Content Security Policy headers
- Input validation and sanitization
- SQL injection prevention (EF Core)
- XSS protection
- CSRF protection (anti-forgery tokens)

### Security Best Practices
- Store secrets in User Secrets (development)
- Use Azure Key Vault (production)
- Enable HTTPS only
- Regular dependency updates
- Security headers configured

## 🧪 Testing

### Manual Testing Checklist
- [ ] Prayer times display correctly
- [ ] Events calendar interactive
- [ ] Donations process successfully (use Stripe test cards)
- [ ] Media upload and display
- [ ] Search returns relevant results
- [ ] Admin panel CRUD operations
- [ ] Mobile responsive on all pages
- [ ] Cross-browser compatibility

### Stripe Test Cards
- **Success**: 4242 4242 4242 4242
- **Decline**: 4000 0000 0000 0002
- **3D Secure**: 4000 0025 0000 3155

## 📱 Mobile Responsive
- Fully responsive design (Bootstrap 5)
- Tested on iOS Safari, Android Chrome
- Touch-friendly navigation
- Optimized for screens 320px - 1920px

## 🎯 Performance Optimizations
- Response caching
- Memory caching (prayer times, frequently accessed data)
- Database query optimization with EF Core
- ImageSharp for dynamic image processing
- Lazy loading for images
- CDN-ready static assets

## 📊 Admin Access

### Creating Admin User
After first deployment, create an admin user:

1. Register a user through the website
2. Update user role in database:
```sql
INSERT INTO AspNetRoles (Id, Name, NormalizedName)
VALUES (NEWID(), 'Admin', 'ADMIN');

INSERT INTO AspNetUserRoles (UserId, RoleId)
SELECT u.Id, r.Id
FROM AspNetUsers u, AspNetRoles r
WHERE u.Email = 'admin@husaynia.org' AND r.Name = 'Admin';
```

3. Access admin panel at `/Admin`

## 🌐 Deployment

See [DEPLOYMENT.md](DEPLOYMENT.md) for detailed deployment instructions including:
- IIS deployment
- Azure App Service deployment
- Docker deployment
- Production configuration
- Security hardening
- Monitoring setup

## 📝 Configuration

### Key Configuration Sections

#### appsettings.json
```json
{
  "PrayerTimeSettings": {
    "Latitude": 47.9129,
    "Longitude": -122.0982,
    "FajrAngle": 16.0,
    "IshaAngle": 14.0
  },
  "Stripe": {
    "PublishableKey": "pk_test_...",
    "SecretKey": "sk_test_..."
  }
}
```

## 🤝 Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/AmazingFeature`)
3. Commit your changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

## 📄 License

This project is proprietary software for Husaynia Islamic Society of Seattle.

## 📧 Contact

**Husaynia Islamic Society**
- Address: 15231 State St, Snohomish, WA 98296
- Phone: +1-425-312-3196
- Email: info@husaynia.org
- Website: https://www.husaynia.org

## 🙏 Acknowledgments

- Bootstrap Team for the excellent CSS framework
- FullCalendar.js for calendar functionality
- Stripe for payment processing
- SixLabors team for ImageSharp
- Microsoft for ASP.NET Core

---

**Built with ❤️ for the Husaynia Community**

**Co-Authored-By: Claude Sonnet 4.5 <noreply@anthropic.com>**
