using NML.Core.Skins;

namespace NML.Core.Tests;

/// <summary>
/// Validates the skin-URL builder. These URLs are the contract the UI binds to (Image source),
/// so getting them exactly right matters — a wrong URL renders nothing.
/// </summary>
public class SkinServiceTests
{
    private const string OnlineUuid = "853c80ef-3c37-49fd-aa49-938b674adae6"; // jeb_
    private const string OnlineNoDash = "853c80ef3c3749fdaa49938b674adae6";
    // A real offline (v3 MD5) UUID: version nibble at index 12 must be '3'.
    // Constructed so chars 0-11 are arbitrary and char 12 is '3'.
    private const string OfflineUuid = "660a28a1bc3d349fdaa49938b674adae6";

    [Fact]
    public void Avatar_url_has_correct_shape()
    {
        var svc = new SkinService();
        svc.AvatarUrl(OnlineUuid, 64)
            .Should().Be("https://crafatar.com/avatars/853c80ef3c3749fdaa49938b674adae6?size=64&overlay");
    }

    [Fact]
    public void Avatar_url_accepts_no_dash_uuid()
    {
        var svc = new SkinService();
        svc.AvatarUrl(OnlineNoDash).Should().Contain(OnlineNoDash);
    }

    [Fact]
    public void Head_render_url_uses_renders_head()
    {
        var svc = new SkinService();
        svc.HeadRenderUrl(OnlineUuid, scale: 8)
            .Should().Be("https://crafatar.com/renders/head/853c80ef3c3749fdaa49938b674adae6?scale=8&overlay");
    }

    [Fact]
    public void Body_render_url_uses_renders_body()
    {
        var svc = new SkinService();
        svc.BodyRenderUrl(OnlineUuid).Should().StartWith("https://crafatar.com/renders/body/");
    }

    [Fact]
    public void Skin_texture_url_uses_skins_endpoint()
    {
        var svc = new SkinService();
        svc.SkinTextureUrl(OnlineUuid).Should().Be("https://crafatar.com/skins/853c80ef3c3749fdaa49938b674adae6");
    }

    [Theory]
    [InlineData(8)]
    [InlineData(64)]
    [InlineData(512)]
    public void Size_is_clamped_to_valid_range(int size)
    {
        var svc = new SkinService();
        string url = svc.AvatarUrl(OnlineUuid, size);
        url.Should().Contain($"size={size}");
    }

    [Fact]
    public void Size_above_max_is_clamped_to_512()
    {
        new SkinService().AvatarUrl(OnlineUuid, 9999).Should().Contain("size=512");
    }

    [Fact]
    public void Normalize_strips_dashes_and_lowercases()
    {
        SkinService.Normalize("AA-BB-CC").Should().Be("aabbcc");
    }

    [Fact]
    public void Offline_uuid_detected_by_version_nibble()
    {
        // v3 (MD5/offline) → version nibble is '3' → offline.
        SkinService.IsLikelyOfflineUuid(OfflineUuid).Should().BeTrue();
        SkinService.IsLikelyOfflineUuid("660a28a1-bc3d-349f-aa49-938b674adae6").Should().BeTrue();
    }

    [Fact]
    public void Online_uuid_not_flagged_offline()
    {
        SkinService.IsLikelyOfflineUuid(OnlineUuid).Should().BeFalse();
    }

    [Fact]
    public void Empty_uuid_throws()
    {
        Action act = () => new SkinService().AvatarUrl("");
        act.Should().Throw<ArgumentException>();
    }

    // ===== DownloadSkinPngAsync: cache + idempotency (previously untested) =====

    /// <summary>A fake IHttpFetcher that returns canned PNG bytes for any URL.</summary>
    private sealed class CannedPngFetcher : NML.Core.Download.IHttpFetcher
    {
        public Task<byte[]> GetByteArrayAsync(string url, CancellationToken ct = default) =>
            Task.FromResult(PngBytes);
        public Task<string> GetStringAsync(string url, CancellationToken ct = default) =>
            Task.FromResult("{}");
        public Task StreamToAsync(string url, Stream destination, IProgress<long>? bytesReceived = null, CancellationToken ct = default)
        {
            byte[] b = PngBytes;
            destination.Write(b, 0, b.Length);
            bytesReceived?.Report(b.Length);
            return Task.CompletedTask;
        }
        public Task<NML.Core.Download.RangeResponse?> TryRangeDownloadAsync(string url, long from, long? to, CancellationToken ct = default) =>
            Task.FromResult<NML.Core.Download.RangeResponse?>(null);

        // 1×1 red PNG (valid 8-byte signature + minimal IHDR/IDAT/IEND).
        public static readonly byte[] PngBytes = Convert.FromHexString(
            "89504E470D0A1A0A0000000D49484452000000010000000108020000009077" +
            "53DE0000000C4944415408D76360A049181310002C7801E9A5000000004945" +
            "4E44AE426082");
    }

    [Fact]
    public async Task DownloadSkinPng_Writes_Png_To_Cache_Dir()
    {
        string cacheDir = Path.Combine(Path.GetTempPath(), "nml-skin-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(cacheDir);
        try
        {
            var svc = new SkinService(new CannedPngFetcher(), cacheDir);
            string path = await svc.DownloadSkinPngAsync(OnlineUuid);

            path.Should().NotBeNullOrEmpty("a successful download returns the cached path");
            File.Exists(path).Should().BeTrue("the PNG must be written to disk");
            // The file must be a valid PNG (signature bytes).
            byte[] written = await File.ReadAllBytesAsync(path);
            written[0].Should().Be(0x89);
            written[1].Should().Be(0x50); // 'P'
            // The cache file is named after the normalized UUID (no dashes).
            Path.GetFileName(path).Should().Be(OnlineNoDash + ".png");
        }
        finally { try { Directory.Delete(cacheDir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task DownloadSkinPng_Is_Idempotent_Second_Call_Does_Not_Throw()
    {
        // A repeated download for the same UUID must not re-download or error; it returns the cached path.
        string cacheDir = Path.Combine(Path.GetTempPath(), "nml-skin-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(cacheDir);
        try
        {
            var svc = new SkinService(new CannedPngFetcher(), cacheDir);
            string first = await svc.DownloadSkinPngAsync(OnlineUuid);
            string second = await svc.DownloadSkinPngAsync(OnlineUuid);

            second.Should().Be(first, "the idempotent second call returns the same cached path");
            File.Exists(second).Should().BeTrue();
        }
        finally { try { Directory.Delete(cacheDir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task DownloadSkinPng_Returns_Empty_On_Network_Failure()
    {
        // A fetcher that throws should degrade gracefully to "" (the launch flow tolerates a missing skin).
        string cacheDir = Path.Combine(Path.GetTempPath(), "nml-skin-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(cacheDir);
        try
        {
            var svc = new SkinService(new ThrowingFetcher(), cacheDir);
            string path = await svc.DownloadSkinPngAsync(OnlineUuid);
            path.Should().BeEmpty("a download failure must return empty, not throw");
        }
        finally { try { Directory.Delete(cacheDir, recursive: true); } catch { } }
    }

    private sealed class ThrowingFetcher : NML.Core.Download.IHttpFetcher
    {
        public Task<byte[]> GetByteArrayAsync(string url, CancellationToken ct = default) =>
            throw new HttpRequestException("network down");
        public Task<string> GetStringAsync(string url, CancellationToken ct = default) =>
            throw new HttpRequestException("network down");
        public Task StreamToAsync(string url, Stream destination, IProgress<long>? bytesReceived = null, CancellationToken ct = default) =>
            throw new HttpRequestException("network down");
        public Task<NML.Core.Download.RangeResponse?> TryRangeDownloadAsync(string url, long from, long? to, CancellationToken ct = default) =>
            throw new HttpRequestException("network down");
    }

    // ===== Provider fallback (crafatar.com was observed down with Cloudflare 521) =====

    /// <summary>Fails the first N URLs, serves PNG bytes afterwards — simulates a mirror outage.</summary>
    private sealed class FlakyFetcher : NML.Core.Download.IHttpFetcher
    {
        private int _failCount;
        public List<string> RequestedUrls { get; } = new();
        public FlakyFetcher(int failCount) => _failCount = failCount;

        public Task<byte[]> GetByteArrayAsync(string url, CancellationToken ct = default)
        {
            RequestedUrls.Add(url);
            if (_failCount-- > 0) throw new HttpRequestException("provider down");
            return Task.FromResult(Convert.FromHexString("89504E470D0A1A0A"));
        }
        public Task<string> GetStringAsync(string url, CancellationToken ct = default) => Task.FromResult("{}");
        public Task StreamToAsync(string url, Stream d, IProgress<long>? p = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<NML.Core.Download.RangeResponse?> TryRangeDownloadAsync(string url, long f, long? t, CancellationToken ct = default) =>
            Task.FromResult<NML.Core.Download.RangeResponse?>(null);
    }

    [Fact]
    public async Task DownloadSkinPng_Falls_Back_To_Next_Mirror_When_Primary_Down()
    {
        // crafatar.com down → the download must proceed via mc-heads.net (mirror #2).
        string cacheDir = Path.Combine(Path.GetTempPath(), "nml-skin-fb-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(cacheDir);
        try
        {
            var fetcher = new FlakyFetcher(failCount: 1); // first URL (crafatar) fails
            var svc = new SkinService(fetcher, cacheDir);
            string path = await svc.DownloadSkinPngAsync(OnlineUuid);

            path.Should().NotBeNullOrEmpty("the mirror should have served the skin");
            fetcher.RequestedUrls.Should().HaveCount(2);
            fetcher.RequestedUrls[0].Should().StartWith("https://crafatar.com/skins/");
            fetcher.RequestedUrls[1].Should().StartWith("https://minotar.net/skin/",
                "the fallback must use the correct per-provider path shape");
            File.Exists(path).Should().BeTrue();
        }
        finally { try { Directory.Delete(cacheDir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task DownloadSkinPng_Returns_Empty_When_All_Mirrors_Down()
    {
        string cacheDir = Path.Combine(Path.GetTempPath(), "nml-skin-all-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(cacheDir);
        try
        {
            var svc = new SkinService(new ThrowingFetcher(), cacheDir);
            string path = await svc.DownloadSkinPngAsync(OnlineUuid);
            path.Should().BeEmpty("total outage degrades gracefully");
        }
        finally { try { Directory.Delete(cacheDir, recursive: true); } catch { } }
    }

    [Fact]
    public void SkinTextureUrls_Lists_All_Providers_In_Order()
    {
        var svc = new SkinService();
        var urls = svc.SkinTextureUrls(OnlineUuid);
        urls.Should().HaveCount(SkinService.ProviderTemplates.Count);
        urls[0].Should().StartWith("https://crafatar.com/skins/");
        urls[1].Should().StartWith("https://minotar.net/skin/", "minotar verified up; sits before mc-heads (403)");
        urls[2].Should().StartWith("https://mc-heads.net/skin/");
    }
}
