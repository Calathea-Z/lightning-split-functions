using Functions.Services.Abstractions;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace Functions.Services;

public sealed class ImagePreprocessor(ILogger<ImagePreprocessor> log) : IImagePreprocessor
{
    const int MaxEdge = 2000;
    const int MaxPixels = 32_000_000; // ~32MP

    public async Task<Stream> PrepareAsync(Stream original, CancellationToken ct)
    {
        var pre = new MemoryStream();
        try
        {
            using var img = await Image.LoadAsync(original, ct);
            var pixels = (long)img.Width * img.Height;

            if (pixels > MaxPixels)
            {
                var scale = Math.Sqrt((double)MaxPixels / pixels);
                img.Mutate(x => x.Resize((int)(img.Width * scale), (int)(img.Height * scale)));
            }
            else
            {
                var scale = Math.Min(1.0, (double)MaxEdge / Math.Max(img.Width, img.Height));
                if (scale < 1.0) img.Mutate(x => x.Resize((int)(img.Width * scale), (int)(img.Height * scale)));
            }

            img.Mutate(x => x.Grayscale());
            await img.SaveAsync(pre, new PngEncoder(), ct);
            pre.Position = 0;
            return pre;
        }
        catch (UnknownImageFormatException uif)
        {
            log.LogWarning(uif, "Unknown image format, passing through original bytes");
            original.Position = 0;
            await original.CopyToAsync(pre, ct);
            pre.Position = 0;
            return pre;
        }
    }
}
