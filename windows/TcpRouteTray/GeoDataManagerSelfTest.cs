using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace TcpRouteTray;

internal static class GeoDataManagerSelfTest
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TcpRoute2-GeoDataSelfTest-{Guid.NewGuid():N}");
        try
        {
            GeoDataManager.EnsureInstalled(directory);
            var snapshot = GeoDataManager.Snapshot(directory, "24h", null, null);
            Expect(snapshot.Interval == "24h", "Geo data TOML interval was not retained");
            Expect(snapshot.GeositeBytes > 0 && snapshot.GeoipBytes > 0, "Bundled Geo data was not installed");
            Expect(File.ReadAllText(Path.Combine(directory, "geosite.dat.sha256")).Length == 64,
                "Geosite checksum marker is invalid");
            Expect(File.ReadAllText(Path.Combine(directory, "geoip.dat.sha256")).Length == 64,
                "GeoIP checksum marker is invalid");
            Expect(!GeoDataManager.IsDue(snapshot with { Interval = "off" }),
                "Disabled Geo data schedule was considered due");
            Expect(GeoDataManager.IsDue(snapshot with { LastSuccess = DateTimeOffset.UtcNow.AddDays(-2) }), "Positive TOML interval did not become due");
            CheckOptionalChecksumAndSchedules(directory).GetAwaiter().GetResult();
        }
        finally
        {
            GeoDataManager.Configure([]);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static async Task CheckOptionalChecksumAndSchedules(string directory)
    {
        var requests = new List<string>();
        var failChecksum = false;
        using var client = new HttpClient(new GeoResponseHandler(request => {
            var url = request.RequestUri!.AbsoluteUri;
            requests.Add(url);
            var site = url.Contains("geosite", StringComparison.Ordinal);
            var data = Encoding.UTF8.GetBytes(site ? "site-data" : "ip-data");
            var bytes = url.EndsWith(".sha256sum", StringComparison.Ordinal)
                ? Encoding.ASCII.GetBytes(failChecksum ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(data))) : data;
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
        }));
        GeoDataManager.Configure([
            new("geosite", "https://example.test/geosite.dat", "", "1h"),
            new("geoip", "https://example.test/geoip.dat", "", "0")
        ]);
        await GeoDataManager.UpdateAsync(directory, client: client);
        Expect(requests.Count == 2 && requests.All(url => !url.EndsWith(".sha256sum")), "Omitted checksum triggered a checksum download");
        Expect(GeoDataManager.ConfiguredInterval() == "1h", "Manual source disabled another source's schedule");
        requests.Clear();
        File.SetLastWriteTimeUtc(Path.Combine(directory, "geosite.dat"), DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(Path.Combine(directory, "geoip.dat"), DateTime.UtcNow.AddHours(-2));
        var originalIPTime = File.GetLastWriteTimeUtc(Path.Combine(directory, "geoip.dat"));
        await GeoDataManager.UpdateAsync(directory, onlyIfDue: true, client: client);
        Expect(requests.Count == 1 && requests[0].Contains("geosite"), "Automatic update downloaded a manual-only source");
        Expect(File.GetLastWriteTimeUtc(Path.Combine(directory, "geoip.dat")) == originalIPTime, "Automatic update replaced a manual-only file");
        var originalSite = File.ReadAllBytes(Path.Combine(directory, "geosite.dat"));
        GeoDataManager.Configure([
            new("geosite", "https://example.test/geosite.dat", "https://example.test/geosite.dat.sha256sum", "0"),
            new("geoip", "https://example.test/geoip.dat", "", "0")
        ]);
        failChecksum = true;
        try { await GeoDataManager.UpdateAsync(directory, client: client); throw new Exception("Checksum mismatch was accepted"); }
        catch (InvalidDataException) { }
        Expect(File.ReadAllBytes(Path.Combine(directory, "geosite.dat")).SequenceEqual(originalSite), "Failed checksum replaced installed data");
        GeoDataManager.Configure([]);
        Expect(GeoDataManager.ConfiguredInterval() == "0", "Missing GeoData configuration enabled automatic updates");
    }

    private sealed class GeoResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
