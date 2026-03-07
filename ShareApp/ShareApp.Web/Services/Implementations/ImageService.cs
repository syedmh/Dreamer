using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using ShareApp.Web.Services.Interfaces;

namespace ShareApp.Web.Services.Implementations;

public class ImageService : IImageService
{
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly long _maxFileSizeBytes;
    private readonly string[] _allowedExtensions;

    public ImageService(IWebHostEnvironment environment, IConfiguration configuration)
    {
        _environment = environment;
        _configuration = configuration;

        var maxSizeMB = configuration.GetValue<int>("ApplicationSettings:MaxImageSizeMB", 5);
        _maxFileSizeBytes = maxSizeMB * 1024 * 1024;

        _allowedExtensions = configuration.GetSection("ApplicationSettings:AllowedImageTypes")
            .Get<string[]>() ?? new[] { ".jpg", ".jpeg", ".png", ".webp" };
    }

    public bool ValidateImage(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return false;

        if (file.Length > _maxFileSizeBytes)
            return false;

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!_allowedExtensions.Contains(extension))
            return false;

        return true;
    }

    public async Task<string> SaveImageAsync(IFormFile file, string folder)
    {
        if (!ValidateImage(file))
            throw new InvalidOperationException("Invalid image file");

        var uploadsFolder = Path.Combine(_environment.WebRootPath, "uploads", folder);
        Directory.CreateDirectory(uploadsFolder);

        var uniqueFileName = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
        var filePath = Path.Combine(uploadsFolder, uniqueFileName);

        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        // Optionally resize/optimize the image
        await ResizeAndOptimizeAsync(filePath, 1200, 1200);

        return $"/uploads/{folder}/{uniqueFileName}";
    }

    public async Task<bool> DeleteImageAsync(string imageUrl)
    {
        if (string.IsNullOrEmpty(imageUrl))
            return false;

        try
        {
            var filePath = Path.GetFullPath(Path.Combine(_environment.WebRootPath, imageUrl.TrimStart('/')));
            if (!filePath.StartsWith(Path.GetFullPath(_environment.WebRootPath), StringComparison.OrdinalIgnoreCase))
                return false;

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                return await Task.FromResult(true);
            }
        }
        catch (Exception ex)
        {
            _ = ex; // Logged by caller
        }

        return false;
    }

    public async Task<string> ResizeAndOptimizeAsync(string imagePath, int maxWidth, int maxHeight)
    {
        try
        {
            using var image = await Image.LoadAsync(imagePath);

            // Calculate new dimensions while maintaining aspect ratio
            var ratioX = (double)maxWidth / image.Width;
            var ratioY = (double)maxHeight / image.Height;
            var ratio = Math.Min(ratioX, ratioY);

            if (ratio < 1.0)
            {
                var newWidth = (int)(image.Width * ratio);
                var newHeight = (int)(image.Height * ratio);

                image.Mutate(x => x.Resize(newWidth, newHeight));
            }

            // Save with compression
            await image.SaveAsync(imagePath, new JpegEncoder { Quality = 85 });

            return imagePath;
        }
        catch
        {
            // If optimization fails, return original path
            return imagePath;
        }
    }
}
