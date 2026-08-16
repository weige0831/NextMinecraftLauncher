using System.IO.Compression;
using System.Text;
using NML.Core.Download;

namespace NML.Core.Tests;

/// <summary>
/// Security regression tests: Zip Slip / path traversal protection in every archive-extraction
/// and remote-path-to-filesystem path (<see cref="ZipSafeExtractor"/>, <see cref="Downloader"/>).
/// Each case mirrors the concrete exploits from the security audit.
/// </summary>
public class ZipSlipSecurityTests
{
    private static string TempRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nml-sec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string MakeZip(params (string Name, string Content)[] entries)
    {
        string zipPath = Path.Combine(TempRoot(), "evil.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var e = zip.CreateEntry(name);
            using var w = new StreamWriter(e.Open());
            w.Write(content);
        }
        return zipPath;
    }

    [Theory]
    [InlineData("../evil.txt")]                       // classic parent escape
    [InlineData("..\\evil.txt")]                      // windows separators
    [InlineData("a/../../evil.txt")]                  // nested escape
    [InlineData("/etc/evil")]                         // rooted unix
    [InlineData("C:\\Windows\\evil.bat")]             // absolute windows
    [InlineData("\\\\server\\share\\evil")]           // UNC
    public void SafeDestination_Rejects_Traversal_And_Absolute(string entryName)
    {
        string root = TempRoot();
        try
        {
            var act = () => ZipSafeExtractor.SafeDestination(root, entryName);
            act.Should().Throw<IOException>($"'{entryName}' must not be resolvable under the root");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SafeDestination_Rejects_Empty_Name()
    {
        string root = TempRoot();
        try
        {
            var act = () => ZipSafeExtractor.SafeDestination(root, "  ");
            act.Should().Throw<IOException>();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SafeDestination_Accepts_Normal_Relative_Paths()
    {
        string root = TempRoot();
        try
        {
            string dest = ZipSafeExtractor.SafeDestination(root, "mods/sodium.jar");
            dest.Should().StartWith(Path.GetFullPath(root));
            // Deep nesting and case-difference still fine.
            ZipSafeExtractor.SafeDestination(root, "A/B/c.TXT").Should().Contain("A");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExtractEntry_Blocks_Startup_Folder_Write()
    {
        // The audit's exploit sketch: instance bundle with ..\..\Startup\x.bat.
        string root = TempRoot();
        string zipPath = MakeZip(("../../evil.bat", "payload"));
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var act = () => ZipSafeExtractor.ExtractEntry(zip.Entries[0], root);
            act.Should().Throw<IOException>();
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Should().NotContain(f => f.EndsWith("evil.bat"), "nothing may be written outside");
        }
        finally { Directory.Delete(root, recursive: true); Directory.Delete(Path.GetDirectoryName(zipPath)!, recursive: true); }
    }

    [Fact]
    public async Task Downloader_Rejects_Traversal_RelativePath_From_Remote()
    {
        // Modrinth/CurseForge filenames and .mrpack paths flow into DownloadAsync — a hostile
        // "../pwn.bat" must throw before any HTTP call.
        string root = TempRoot();
        try
        {
            var dl = new Downloader(new NeverFetch(), Microsoft.Extensions.Logging.Abstractions.NullLogger<Downloader>.Instance);
            var file = new NML.Core.Models.Downloadable { Url = "https://example.com/x", Sha1 = "", Size = 1 };
            var act = () => dl.DownloadAsync(file, "../pwn.bat", root);
            await act.Should().ThrowAsync<IOException>();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class NeverFetch : IHttpFetcher
    {
        public Task<byte[]> GetByteArrayAsync(string url, CancellationToken ct = default) =>
            throw new InvalidOperationException("Must not fetch when the path is rejected.");
        public Task<string> GetStringAsync(string url, CancellationToken ct = default) =>
            throw new InvalidOperationException("Must not fetch when the path is rejected.");
        public Task StreamToAsync(string url, Stream d, IProgress<long>? p = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("Must not fetch when the path is rejected.");
        public Task<RangeResponse?> TryRangeDownloadAsync(string url, long f, long? t, CancellationToken ct = default) =>
            throw new InvalidOperationException("Must not fetch when the path is rejected.");
    }

    [Fact]
    public void SafeRelativePath_Normalizes_Remote_Path()
    {
        string baseDir = TempRoot();
        try
        {
            string rel = ZipSafeExtractor.SafeRelativePath(baseDir, "mods\\sodium-0.5.jar");
            rel.Replace('\\', '/').Should().Be("mods/sodium-0.5.jar");
            var act = () => ZipSafeExtractor.SafeRelativePath(baseDir, "..\\..\\evil");
            act.Should().Throw<IOException>();
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void ExtractAll_Writes_Only_Inside_Root()
    {
        string root = TempRoot();
        string outside = Path.Combine(Path.GetDirectoryName(root)!, "outside-marker.txt");
        string zipPath = MakeZip(("ok.txt", "fine"),(("bad.txt"), "x"));
        try
        {
            // Manually craft a traversal entry via ZipArchive (MakeZip would resolve it).
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
                zip.CreateEntry("../bad.txt");
            using var read = ZipFile.OpenRead(zipPath);
            var act = () => ZipSafeExtractor.ExtractAll(read, root);
            act.Should().Throw<IOException>("the traversal entry must abort extraction");
            File.Exists(outside).Should().BeFalse();
        }
        finally
        {
            if (File.Exists(outside)) File.Delete(outside);
            Directory.Delete(root, recursive: true);
            Directory.Delete(Path.GetDirectoryName(zipPath)!, recursive: true);
        }
    }
}
