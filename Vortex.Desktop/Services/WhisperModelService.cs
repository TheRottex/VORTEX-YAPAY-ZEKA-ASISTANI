using System.Net.Http;

namespace Vortex.Desktop.Services;

public sealed record WhisperModelInfo(string Id, string DisplayName, long SizeBytes, string FileName, Uri DownloadUri)
{
    public string SizeLabel => SizeBytes >= 1024L * 1024L * 1024L
        ? $"{SizeBytes / 1024d / 1024d / 1024d:F1} GiB"
        : $"{SizeBytes / 1024d / 1024d:F0} MiB";

    public override string ToString() => $"{DisplayName} ({SizeLabel})";
}

public sealed class WhisperModelService
{
    private const string RepositoryBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";
    private readonly HttpClient _httpClient;
    private readonly string _modelDirectory;

    public static IReadOnlyList<WhisperModelInfo> AvailableModels { get; } =
    [
        new("tiny", "Çok Hızlı · Tiny", 75L * 1024 * 1024, "ggml-tiny.bin", new Uri(RepositoryBaseUrl + "ggml-tiny.bin")),
        new("base", "Düşük Donanım · Base", 142L * 1024 * 1024, "ggml-base.bin", new Uri(RepositoryBaseUrl + "ggml-base.bin")),
        new("small", "Dengeli · Small (Önerilen)", 466L * 1024 * 1024, "ggml-small.bin", new Uri(RepositoryBaseUrl + "ggml-small.bin")),
        new("medium", "Yüksek Doğruluk · Medium", 1500L * 1024 * 1024, "ggml-medium.bin", new Uri(RepositoryBaseUrl + "ggml-medium.bin")),
        new("large-v3-turbo", "Maksimum Performans · Large V3 Turbo", 1600L * 1024 * 1024, "ggml-large-v3-turbo.bin", new Uri(RepositoryBaseUrl + "ggml-large-v3-turbo.bin"))
    ];

    public WhisperModelService(HttpClient? httpClient = null, string? modelDirectory = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        _modelDirectory = modelDirectory ?? GetDefaultModelDirectory();
    }

    public static string GetDefaultModelDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        return Path.Combine(root, "VortexAI", "whisper", "models");
    }

    public string GetModelPath(WhisperModelInfo model) => Path.Combine(_modelDirectory, model.FileName);

    public bool IsDownloaded(WhisperModelInfo model) => File.Exists(GetModelPath(model));

    public async Task<string> DownloadAsync(WhisperModelInfo model, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_modelDirectory);
        var destination = GetModelPath(model);
        var temporary = destination + ".download";

        try
        {
            using var response = await _httpClient.GetAsync(model.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > 0 && contentLength > model.SizeBytes * 2)
            {
                throw new InvalidDataException("Whisper model download is unexpectedly large.");
            }

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long totalRead = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    totalRead += read;
                    if (contentLength is > 0) progress?.Report(Math.Min(1d, (double)totalRead / contentLength.Value));
                }

                if (totalRead == 0) throw new InvalidDataException("Whisper model download is empty.");
            }
            progress?.Report(1d);
            File.Move(temporary, destination, true);
            return destination;
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
    }
}
