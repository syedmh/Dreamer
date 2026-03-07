namespace ShareApp.Web.Services.Interfaces;

public interface IImageService
{
    Task<string> SaveImageAsync(IFormFile file, string folder);
    Task<bool> DeleteImageAsync(string imageUrl);
    Task<string> ResizeAndOptimizeAsync(string imagePath, int maxWidth, int maxHeight);
    bool ValidateImage(IFormFile file);
}
