using Microsoft.Extensions.Logging;
using NML.Core.Models;

namespace NML.Core.Download;

/// <summary>
/// Downloads files described by Mojang's <see cref="Downloadable"/> shape into a target
/// directory, verifying SHA-1 on completion. Skips files that are already present and
/// correct (idempotent re-runs). Supports cancellation and per-file progress.
/// </summary>
public sealed class Downloader
{
    private readonly IHttpFetcher _http;
    private readonly ILogger<Downloader> _logger;

    public Downloader(IHttpFetcher http, ILogger<Downloader> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Download <paramref name="file"/> to <c><paramref name="targetDir"/>/<paramref name="relativePath"/></c>.
    /// Creates parent directories. Verifies SHA-1 (skips if already correct).
    /// </summary>
    public async Task DownloadAsync(
        Downloadable file,
        string relativePath,
        string targetDir,
        DownloadCancel? cancel = null,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        // Path-injection guard: relativePath comes from remote manifests (Modrinth/CurseForge
        // filenames, .mrpack paths) — reject rooted names and traversal out of targetDir.
        string fullPath = ZipSafeExtractor.SafeDestination(targetDir, relativePath);
        string fullDir = Path.GetDirectoryName(fullPath) ?? targetDir;
        Directory.CreateDirectory(fullDir);

        // Idempotency: skip if the existing file already matches the expected SHA-1 and size.
        if (File.Exists(fullPath) && file.Size > 0)
        {
            var fi = new FileInfo(fullPath);
            if (fi.Length == file.Size &&
                await Sha1.FileMatchesAsync(fullPath, file.Sha1, ct))
            {
                progress?.Report(file.Size);
                _logger.LogDebug("Skipped (already valid): {Path}", relativePath);
                return;
            }
        }

        _logger.LogInformation("Downloading {Url} → {Path}", file.Url, relativePath);

        // Stream into a .part file so a partial download never masquerades as complete.
        string partPath = fullPath + ".part";
        await using (var fs = new FileStream(
                         partPath, FileMode.Create, FileAccess.Write, FileShare.None,
                         bufferSize: 81920, useAsync: true))
        {
            await _http.StreamToAsync(file.Url, fs, progress, ct);
        }

        // Verify integrity before promoting to the final path.
        if (!string.IsNullOrEmpty(file.Sha1) && !await Sha1.FileMatchesAsync(partPath, file.Sha1, ct))
        {
            File.Delete(partPath);
            throw new InvalidDataException(
                $"SHA-1 mismatch for {relativePath} (expected {file.Sha1}).");
        }

        // Atomic-ish promotion: delete old, rename .part.
        if (File.Exists(fullPath)) File.Delete(fullPath);
        File.Move(partPath, fullPath);
    }

    /// <summary>
    /// Download a batch of files concurrently (bounded), reporting aggregate progress.
    /// </summary>
    public async Task DownloadBatchAsync(
        IReadOnlyList<(Downloadable File, string RelativePath)> files,
        string targetDir,
        int maxConcurrency = 8,
        DownloadCancel? cancel = null,
        ProgressReporter? progress = null,
        CancellationToken ct = default)
    {
        using var semaphore = new SemaphoreSlim(Math.Max(1, maxConcurrency));
        long bytesDone = 0;
        int filesDone = 0;
        long totalBytes = files.Sum(f => f.File.Size);
        int totalFiles = files.Count;

        var tasks = files.Select(async item =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                long localBytes = 0;
                string currentItem = item.RelativePath;
                await DownloadAsync(
                    item.File, item.RelativePath, targetDir,
                    cancel,
                    new Progress<long>(b =>
                    {
                        Interlocked.Add(ref bytesDone, b - localBytes);
                        localBytes = b;
                        ReportProgress(currentItem);
                    }),
                    ct);

                Interlocked.Increment(ref filesDone);
                ReportProgress(currentItem);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);
        return;

        void ReportProgress(string currentItem)
        {
            if (progress is null) return;
            var p = new DownloadProgress(
                Interlocked.Read(ref bytesDone), totalBytes,
                Volatile.Read(ref filesDone), totalFiles);
            progress(in p, currentItem);
        }
    }
}
