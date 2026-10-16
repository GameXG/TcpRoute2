using System.IO;
using System.Globalization;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TcpRouteTray;

internal sealed class TraySettings
{
    internal static readonly JsonSerializerOptions FileJsonOptions = new() {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public string? SelectedConfigId { get; set; }

    public string? ExecutablePath { get; set; }

    public bool AllowLan { get; set; }

    public bool SystemProxyEnabled { get; set; }

    public bool AllowStun { get; set; }

    public bool AllowQuic { get; set; }

    public bool EnableIpv6 { get; set; }
    public bool UseWintun { get; set; }
    public bool AutoReloadClientSettings { get; set; }
    public string OutboundInterfaceId { get; set; } = "";
    public bool TunAutoBypassLocalProxies { get; set; } = true;
    public string[] TunBypassProcesses { get; set; } = Array.Empty<string>();
    public TunBypassRule[] TunBypassRules { get; set; } = Array.Empty<TunBypassRule>();
    public SubscriptionSource[] Subscriptions { get; set; } = [];
    public WintunNetworkState? TunRecovery { get; set; }
    public WindowsProxy.Snapshot? ProxyRecovery { get; set; }
    public int ProxyOwnedPort { get; set; }

    public string Appearance { get; set; } = "system";

    public string Language { get; set; } = "system";

    public DateTimeOffset? GeoDataLastSuccess { get; set; }

    public string? GeoDataLastError { get; set; }

    public void CopyManagedStateFrom(TraySettings source)
    {
        TunRecovery = source.TunRecovery;
        ProxyRecovery = source.ProxyRecovery;
        ProxyOwnedPort = source.ProxyOwnedPort;
        GeoDataLastSuccess = source.GeoDataLastSuccess;
        GeoDataLastError = source.GeoDataLastError;
    }

    public void ValidatePreferences()
    {
        if (Subscriptions is null || Subscriptions.Length > 16) throw new InvalidDataException("Subscriptions must be an array of at most 16 sources.");
        var subscriptionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in Subscriptions)
        {
            if (source is null) throw new InvalidDataException("A subscription must be an object.");
            source.Validate();
            if (!subscriptionIds.Add(source.Id)) throw new InvalidDataException("Duplicate subscription Id: " + source.Id);
        }
        if (TunBypassRules is null) throw new InvalidDataException("TunBypassRules must be an array.");
        for (var index = 0; index < TunBypassRules.Length; index++)
        {
            if (TunBypassRules[index] is not { } rule) throw new InvalidDataException($"TUN bypass rule {index + 1} must be an object.");
            rule.Validate(index + 1);
        }
        if (TunBypassProcesses is null || TunBypassProcesses.Any(name => string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.IndexOfAny(new[] { '*', '?', '\r', '\n' }) >= 0))
            throw new InvalidDataException("TunBypassProcesses must contain executable names or paths without wildcards.");
        if (!string.IsNullOrEmpty(OutboundInterfaceId) && !Guid.TryParse(OutboundInterfaceId, out _))
            throw new InvalidDataException("OutboundInterfaceId must be empty for Automatic, or an interface GUID.");
        if (!string.IsNullOrEmpty(OutboundInterfaceId)) OutboundInterfaceId = Guid.Parse(OutboundInterfaceId).ToString("D");
        if (Appearance is not ("system" or "light" or "dark") || Language is not ("system" or "en" or "zh-Hans" or "zh-Hant"))
            throw new InvalidDataException("Invalid client appearance or language.");
    }

    public string UserConfigurationJson() => JsonSerializer.Serialize(new {
        SelectedConfigId, ExecutablePath, AllowLan, SystemProxyEnabled, AllowStun, AllowQuic,
        EnableIpv6, UseWintun, AutoReloadClientSettings, OutboundInterfaceId, TunAutoBypassLocalProxies, TunBypassProcesses, TunBypassRules, Subscriptions, Appearance, Language
    }, FileJsonOptions);

    public void SaveUserConfiguration(string path) => SaveJson(path, UserConfigurationJson());

    internal static TraySettings ReadForReload(string path, TraySettings applied, bool reloadClientSettings)
    {
        if (!reloadClientSettings) return applied;
        if (!File.Exists(path)) throw new FileNotFoundException("Client configuration is missing; current settings retained.", path);
        return Load(path);
    }

    public static TraySettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new TraySettings();
        }

        try
        {
            return JsonSerializer.Deserialize<TraySettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("Client configuration must be a JSON object.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("TcpRoute2Client.json is invalid; the original file was retained.", error);
        }
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, FileJsonOptions);
        SaveJson(path, json);
    }

    private static void SaveJson(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try { File.WriteAllText(temporary, json); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class TunBypassRule
{
    public string Network { get; set; } = "any";
    public string[] Destinations { get; set; } = Array.Empty<string>();
    public string[] Ports { get; set; } = Array.Empty<string>();

    public void Validate(int index)
    {
        var error = $"Invalid TUN bypass rule {index}: ";
        if (Network is not ("" or "any" or "tcp" or "udp"))
            throw new InvalidDataException(error + "Network must be any, tcp, or udp.");
        if (Destinations is null || Ports is null || (Destinations.Length == 0 && Ports.Length == 0))
            throw new InvalidDataException(error + "At least one destination or port is required; lists must be arrays.");
        foreach (var value in Destinations)
        {
            if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Contains('%'))
                throw new InvalidDataException(error + "Destinations must be IP addresses or CIDRs without zone IDs.");
            var parts = value.Split('/');
            if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address) ||
                (!parts[0].Contains(':') && parts[0] != address.ToString()))
                throw new InvalidDataException(error + $"Invalid destination: {value}.");
            if (parts.Length == 2 && (address.IsIPv4MappedToIPv6 ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var bits) ||
                bits < 0 || bits > (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128)))
                throw new InvalidDataException(error + $"Invalid CIDR: {value}.");
        }
        foreach (var value in Ports)
        {
            var bounds = value?.Split('-') ?? Array.Empty<string>();
            if (bounds.Length is < 1 or > 2 || !ValidPort(bounds[0], out var first) ||
                !ValidPort(bounds[^1], out var last) || first > last)
                throw new InvalidDataException(error + "Ports must be 1-65535 or an ascending range such as 8000-9000.");
        }
    }

    private static bool ValidPort(string value, out int port) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
}
