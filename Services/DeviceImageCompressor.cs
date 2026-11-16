using Microsoft.Maui.Graphics;
using Microsoft.Maui.Graphics.Platform;
using IGraphicsImage = Microsoft.Maui.Graphics.IImage;

namespace appmaui.blazor.Services;

/// <summary>
/// Redimensiona e comprime imagens selecionadas no dispositivo (Windows/Android) antes do envio,
/// reduzindo drasticamente o consumo de banda, memória e tamanho do banco JSON/nuvem sem custo algum.
/// </summary>
public static class DeviceImageCompressor
{
    public const int DefaultSkipThresholdBytes = 150 * 1024;

    public readonly record struct OptimizedImageResult(
        MemoryStream Stream,
        string FileName,
        string ContentType,
        long OriginalBytes,
        long FinalBytes) : IAsyncDisposable, IDisposable
    {
        public void Dispose() => Stream.Dispose();
        public ValueTask DisposeAsync() => Stream.DisposeAsync();
    }

    public static async Task<OptimizedImageResult> OptimizeForUploadAsync(
        Stream sourceStream,
        string? fileName,
        string? contentType,
        int maxDimension = 1200,
        bool preservePngFormat = false,
        float jpegQuality = 0.82f,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceStream);

        var safeFileName = string.IsNullOrWhiteSpace(fileName) ? "imagem.jpg" : Path.GetFileName(fileName.Trim());
        var resolvedMime = string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType.Trim();

        var rawMs = new MemoryStream();
        if (sourceStream.CanSeek)
            sourceStream.Position = 0;

        await sourceStream.CopyToAsync(rawMs, ct);
        rawMs.Position = 0;

        var originalBytes = rawMs.Length;
        var ext = Path.GetExtension(safeFileName).ToLowerInvariant();
        if (originalBytes <= DefaultSkipThresholdBytes ||
            ext == ".gif" ||
            string.Equals(resolvedMime, "image/gif", StringComparison.OrdinalIgnoreCase))
        {
            return new OptimizedImageResult(rawMs, safeFileName, resolvedMime, originalBytes, originalBytes);
        }

        try
        {
            IGraphicsImage? loaded = PlatformImage.FromStream(rawMs, ImageFormat.Jpeg);
            if (loaded is null)
            {
                rawMs.Position = 0;
                loaded = PlatformImage.FromStream(rawMs, ImageFormat.Png);
            }

            if (loaded is null)
            {
                rawMs.Position = 0;
                return new OptimizedImageResult(rawMs, safeFileName, resolvedMime, originalBytes, originalBytes);
            }

            IGraphicsImage working = loaded;
            var resized = false;
            if (maxDimension > 0 && (loaded.Width > maxDimension || loaded.Height > maxDimension))
            {
                working = loaded.Downsize(maxDimension, disposeOriginal: true);
                resized = true;
            }

            try
            {
                var usePng = preservePngFormat &&
                             (ext == ".png" || string.Equals(resolvedMime, "image/png", StringComparison.OrdinalIgnoreCase));
                var targetFormat = usePng ? ImageFormat.Png : ImageFormat.Jpeg;
                var targetExt = usePng ? ".png" : ".jpg";
                var targetMime = usePng ? "image/png" : "image/jpeg";

                var compressedMs = new MemoryStream();
                await working.SaveAsync(compressedMs, targetFormat, usePng ? 1.0f : jpegQuality);
                compressedMs.Position = 0;

                // Só adota a versão recomprimida se ficou menor (ou se houve redimensionamento expressivo mantendo tamanho razoável).
                if (compressedMs.Length > 0 && (compressedMs.Length < originalBytes || resized && compressedMs.Length <= originalBytes))
                {
                    await rawMs.DisposeAsync();
                    var outputName = Path.ChangeExtension(safeFileName, targetExt);
                    return new OptimizedImageResult(compressedMs, outputName, targetMime, originalBytes, compressedMs.Length);
                }

                await compressedMs.DisposeAsync();
            }
            finally
            {
                working.Dispose();
            }
        }
        catch
        {
            // Se o decoder nativo da plataforma não suportar algum subformato (ex.: WebP exótico), mantém os bytes originais.
        }

        rawMs.Position = 0;
        return new OptimizedImageResult(rawMs, safeFileName, resolvedMime, originalBytes, originalBytes);
    }
}
