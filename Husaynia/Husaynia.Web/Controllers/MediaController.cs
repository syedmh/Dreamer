using Husaynia.Core.Entities;
using Husaynia.Core.Enums;
using Husaynia.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace Husaynia.Web.Controllers
{
    public class MediaController : Controller
    {
        private readonly IMediaRepository _mediaRepository;
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<MediaController> _logger;

        public MediaController(
            IMediaRepository mediaRepository,
            IWebHostEnvironment environment,
            ILogger<MediaController> logger)
        {
            _mediaRepository = mediaRepository;
            _environment = environment;
            _logger = logger;
        }

        public async Task<IActionResult> Photos(string? album = null)
        {
            List<MediaItem> photos;

            if (!string.IsNullOrEmpty(album))
            {
                photos = await _mediaRepository.GetByAlbumAsync(album);
                ViewBag.CurrentAlbum = album;
            }
            else
            {
                photos = await _mediaRepository.GetPhotoGalleryAsync(1, 50);
            }

            var albums = await _mediaRepository.GetAlbumsAsync();
            ViewBag.Albums = albums;

            return View(photos);
        }

        public async Task<IActionResult> Videos()
        {
            var videos = await _mediaRepository.GetVideoGalleryAsync(1, 50);
            return View(videos);
        }

        public async Task<IActionResult> Album(string name)
        {
            var photos = await _mediaRepository.GetByAlbumAsync(name);
            ViewBag.AlbumName = name;

            return View(photos);
        }

        [HttpPost]
        public async Task<IActionResult> Upload(IFormFile file, string title, string? description, string? album)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest("No file uploaded");
            }

            try
            {
                // Create uploads directory if it doesn't exist
                var uploadsPath = Path.Combine(_environment.WebRootPath, "uploads", "photos");
                var thumbnailsPath = Path.Combine(_environment.WebRootPath, "uploads", "thumbnails");

                Directory.CreateDirectory(uploadsPath);
                Directory.CreateDirectory(thumbnailsPath);

                // Generate unique filename
                var fileExtension = Path.GetExtension(file.FileName);
                var fileName = $"{Guid.NewGuid()}{fileExtension}";
                var filePath = Path.Combine(uploadsPath, fileName);
                var thumbnailPath = Path.Combine(thumbnailsPath, fileName);

                // Save original image
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                // Generate thumbnail
                using (var image = await Image.LoadAsync(file.OpenReadStream()))
                {
                    image.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Size = new Size(300, 300),
                        Mode = ResizeMode.Crop
                    }));

                    await image.SaveAsync(thumbnailPath);
                }

                // Save to database
                var mediaItem = new MediaItem
                {
                    Title = title ?? file.FileName,
                    Description = description ?? string.Empty,
                    Type = MediaType.Photo,
                    Url = $"/uploads/photos/{fileName}",
                    ThumbnailUrl = $"/uploads/thumbnails/{fileName}",
                    Album = album,
                    UploadDate = DateTime.UtcNow
                };

                await _mediaRepository.AddAsync(mediaItem);

                return RedirectToAction(nameof(Photos));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading photo");
                return StatusCode(500, "Error uploading photo");
            }
        }

        [HttpPost]
        public async Task<IActionResult> AddVideo(string title, string videoUrl, string? description, string? album)
        {
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(videoUrl))
            {
                return BadRequest("Title and video URL are required");
            }

            try
            {
                // Extract video ID from YouTube or Vimeo URL
                var embedUrl = ConvertToEmbedUrl(videoUrl);
                var thumbnailUrl = GetVideoThumbnail(videoUrl);

                var mediaItem = new MediaItem
                {
                    Title = title,
                    Description = description ?? string.Empty,
                    Type = MediaType.Video,
                    Url = embedUrl,
                    ThumbnailUrl = thumbnailUrl,
                    Album = album,
                    UploadDate = DateTime.UtcNow
                };

                await _mediaRepository.AddAsync(mediaItem);

                return RedirectToAction(nameof(Videos));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding video");
                return StatusCode(500, "Error adding video");
            }
        }

        private string ConvertToEmbedUrl(string videoUrl)
        {
            // YouTube
            if (videoUrl.Contains("youtube.com") || videoUrl.Contains("youtu.be"))
            {
                var videoId = ExtractYouTubeVideoId(videoUrl);
                return $"https://www.youtube.com/embed/{videoId}";
            }

            // Vimeo
            if (videoUrl.Contains("vimeo.com"))
            {
                var videoId = ExtractVimeoVideoId(videoUrl);
                return $"https://player.vimeo.com/video/{videoId}";
            }

            return videoUrl;
        }

        private string ExtractYouTubeVideoId(string url)
        {
            // Handle youtu.be format
            if (url.Contains("youtu.be/"))
            {
                return url.Split("youtu.be/")[1].Split('?')[0];
            }

            // Handle youtube.com format
            var uri = new Uri(url);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            return query["v"] ?? string.Empty;
        }

        private string ExtractVimeoVideoId(string url)
        {
            var parts = url.Split('/');
            return parts[^1].Split('?')[0];
        }

        private string? GetVideoThumbnail(string videoUrl)
        {
            // YouTube thumbnail
            if (videoUrl.Contains("youtube.com") || videoUrl.Contains("youtu.be"))
            {
                var videoId = ExtractYouTubeVideoId(videoUrl);
                return $"https://img.youtube.com/vi/{videoId}/maxresdefault.jpg";
            }

            // Vimeo thumbnails require API call, so we'll skip for now
            return null;
        }
    }
}
