using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TcpRouteTray;

internal enum RuleSourceKind
{
    Local,
    Remote,
    Geosite,
    GeoIP
}

internal sealed class EditableTcpRouteConfig
{
    public string ProfileName { get; set; } = "default";
    public int ListenPort { get; set; } = 7070;
    public List<EditableUpstream> Upstreams { get; } = new() { EditableUpstream.Direct() };
    public List<EditableHostSource> Hosts { get; } = new();
    public string DnsToml { get; set; } = "";
}

internal sealed class EditableHostSource
{
    public RuleSourceKind Source { get; set; } = RuleSourceKind.Local;
    public string Path { get; set; } = "";
    public string UpdateInterval { get; set; } = "24h";
    public string Type { get; set; } = "base";
    public int Credit { get; set; }
}

internal sealed class EditableDomainRule
{
    public RuleSourceKind Source { get; set; } = RuleSourceKind.Local;
    public string Type { get; set; } = "base";
    public string Path { get; set; } = "";
    public string UpdateInterval { get; set; } = "24h";
    public bool UsesManagedGeoData => Source is RuleSourceKind.Geosite or RuleSourceKind.GeoIP;
    public string QuickPath
    {
        get => UsesManagedGeoData ? "" : Path;
        set { if (!UsesManagedGeoData) Path = value; }
    }
    public string QuickUpdateInterval
    {
        get => UsesManagedGeoData ? "" : UpdateInterval;
        set { if (!UsesManagedGeoData) UpdateInterval = value; }
    }

    public static EditableDomainRule Template(RuleSourceKind source) => new()
    {
        Source = source,
        Type = source switch { RuleSourceKind.Geosite => "gfw", RuleSourceKind.GeoIP => "cn,private", _ => "base" },
        Path = source switch { RuleSourceKind.Geosite => "geosite.dat", RuleSourceKind.GeoIP => "geoip.dat", _ => "" }
    };
}

internal sealed class EditableUpstream
{
    public string Name { get; set; } = "";
    public string Alias { get; set; } = "";
    public string ProxyUrl { get; set; } = "";
    public string DialProxyFor { get; set; } = "";
    public bool Remove { get; set; }
    public bool DnsResolve { get; set; }
    public int Credit { get; set; }
    public int Sleep { get; set; }
    public int CorrectDelay { get; set; }
    public List<EditableDomainRule> Whitelist { get; } = new();
    public List<EditableDomainRule> Blacklist { get; } = new();

    public bool IsDirect => ProxyUrl.StartsWith("direct://", StringComparison.OrdinalIgnoreCase);

    public static EditableUpstream Direct() => new()
    {
        Name = "direct", ProxyUrl = "direct://0.0.0.0:0000", DnsResolve = true
    };

    public static EditableUpstream ProxyTemplate(string scheme = "socks5") => new()
    {
        Name = scheme,
        ProxyUrl = scheme switch
        {
            "http" => "http://127.0.0.1:8080",
            "https" => "https://127.0.0.1:8080",
            "shadowsocks" => "shadowsocks://aes-256-gcm:password@127.0.0.1:8388",
            "vmess" => "vmess://00000000-0000-0000-0000-000000000000@127.0.0.1:443?net=ws&tls=tls",
            "vless" => "vless://00000000-0000-0000-0000-000000000000@127.0.0.1:443?encryption=none&security=tls",
            "trojan" => "trojan://password@127.0.0.1:443?security=tls",
            "wireguard" => "wireguard://privateKey@127.0.0.1:2408?publicKey=peerPublicKey&address=172.16.0.2%2F32",
            "hysteria2" => "hysteria2://password@127.0.0.1:443",
            _ => "socks5://127.0.0.1:1080"
        },
        DnsResolve = false,
        Sleep = 80
    };
}

internal static partial class EditableTcpRouteConfigCodec
{
    internal static readonly string[] CreatableProxySchemes =
    [
        "socks5", "http", "https", "shadowsocks", "vmess", "vless",
        "trojan", "wireguard", "hysteria2"
    ];

    internal static readonly string[] ProxySchemes =
    [
        "socks5", "http", "https", "shadowsocks", "vmess", "vless",
        "trojan", "wireguard", "hysteria2"
    ];

    private static readonly HashSet<string> MatchTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "base", "suffix", "pan", "regex"
    };

    private static readonly HashSet<string> SupportedSchemes = new(ProxySchemes, StringComparer.OrdinalIgnoreCase)
    {
        "direct", "ss", "hysteria", "hy2"
    };

    public static EditableTcpRouteConfig FromConfig(TcpRouteConfig? config) =>
        config is null ? new EditableTcpRouteConfig() : Parse(config.Name, config.Content);

    public static EditableTcpRouteConfig Parse(string profileName, string content)
    {
        var editable = new EditableTcpRouteConfig
        {
            ProfileName = profileName,
            ListenPort = TcpRouteConfigParser.GetListenAddress(content).Port
        };
        editable.Upstreams.Clear();
        editable.Upstreams.AddRange(ParseUpstreams(content));
        if (editable.Upstreams.Count == 0)
        {
            editable.Upstreams.Add(EditableUpstream.Direct());
        }
        editable.Hosts.AddRange(ParseHosts(content));
        editable.DnsToml = ExtractDnsToml(content);
        Validate(editable);
        return editable;
    }

    public static string Render(EditableTcpRouteConfig config)
    {
        Validate(config);
        var builder = new StringBuilder();
        builder.AppendLine($"addr=\"127.0.0.1:{config.ListenPort}\"");
        builder.AppendLine();
        builder.Append(config.DnsToml);

        foreach (var host in config.Hosts)
        {
            builder.AppendLine("[[Hosts]]");
            builder.AppendLine($"Path={TomlString(host.Path.Trim())}");
            builder.AppendLine($"Type={TomlString(host.Type.Trim().ToLowerInvariant())}");
            builder.AppendLine($"Credit={host.Credit}");
            if (host.Source == RuleSourceKind.Remote && !string.IsNullOrWhiteSpace(host.UpdateInterval))
            {
                builder.AppendLine($"UpdateInterval={TomlString(host.UpdateInterval.Trim())}");
            }
            builder.AppendLine();
        }

        foreach (var upstream in config.Upstreams)
        {
            builder.AppendLine("[[UpStreams]]");
            builder.AppendLine($"Name={TomlString(upstream.Name.Trim())}");
            if (!string.IsNullOrEmpty(upstream.Alias)) builder.AppendLine($"Alias={TomlString(upstream.Alias)}");
            builder.AppendLine($"ProxyUrl={TomlString(upstream.ProxyUrl.Trim())}");
            if (!string.IsNullOrEmpty(upstream.DialProxyFor)) builder.AppendLine($"DialProxyFor={TomlString(upstream.DialProxyFor)}");
            if (upstream.Remove) builder.AppendLine("Remove=true");
            builder.AppendLine($"DnsResolve={upstream.DnsResolve.ToString().ToLowerInvariant()}");
            builder.AppendLine($"Credit={upstream.Credit}");
            builder.AppendLine($"Sleep={upstream.Sleep}");
            builder.AppendLine($"CorrectDelay={upstream.CorrectDelay}");
            builder.AppendLine();
            foreach (var rule in upstream.Whitelist) AppendRule(builder, "Whitelist", rule);
            foreach (var rule in upstream.Blacklist) AppendRule(builder, "Blacklist", rule);
        }
        return builder.ToString();
    }

    public static string StripLegacyGeneratedComment(string content) =>
        LegacyGeneratedCommentRegex().Replace(content, string.Empty);

    public static string FileNameFor(string profileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(profileName.Trim().Select(character =>
            invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : character).ToArray()).Trim('.', '-', ' ');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            throw new InvalidOperationException("Use a profile name such as default or work-proxy.");
        }
        return $"{sanitized}.toml";
    }

    public static string? ProxyUrlError(string value)
    {
        var text = value.Trim();
        if (text.Length == 0 || text.Any(char.IsWhiteSpace)) return "Proxy URL is empty or contains whitespace.";
        var separator = text.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0) return "Proxy URL needs a scheme.";
        var scheme = text[..separator].ToLowerInvariant();
        if (!SupportedSchemes.Contains(scheme)) return $"Unsupported proxy type: {scheme}.";
        if (scheme == "direct") return text.Length > separator + 3 ? null : "Direct URL is incomplete.";

        var payload = text[(separator + 3)..].Split('#', 2)[0];
        if (scheme == "vmess" && !payload.Contains('@') && TryDecodeBase64(payload, out var decoded))
        {
            try
            {
                using var json = JsonDocument.Parse(decoded);
                var root = json.RootElement;
                if (root.TryGetProperty("add", out var address) && !string.IsNullOrWhiteSpace(address.GetString()) &&
                    root.TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString())) return null;
            }
            catch (JsonException) { return "VMess JSON payload is malformed."; }
        }
        if (new[] { "shadowsocks", "ss" }.Contains(scheme) && !payload.Contains('@'))
        {
            if (!TryDecodeBase64(payload, out var encoded)) return "Shadowsocks URL payload is incomplete.";
            var decodedValue = Encoding.UTF8.GetString(encoded);
            if (!decodedValue.Contains(':') || !decodedValue.Contains('@')) return "Shadowsocks URL payload is incomplete.";
            return null;
        }
        if (new[] { "shadowsocks", "ss" }.Contains(scheme) && payload.Contains('@') &&
            Uri.TryCreate(text, UriKind.Absolute, out var shadowsocks) && !shadowsocks.UserInfo.Contains(':') &&
            TryDecodeBase64(shadowsocks.UserInfo, out var encodedShadowsocks))
        {
            var decodedValue = Encoding.UTF8.GetString(encodedShadowsocks);
            // Shadowsocks 2022 encodes method and key material as method:key[:key].
            if (decodedValue.Contains(':')) return null;
            return "Shadowsocks URL payload is incomplete.";
        }
        if (new[] { "vless", "trojan", "wireguard", "hysteria2", "hysteria", "hy2" }.Contains(scheme) && !payload.Contains('@'))
        {
            if (!TryDecodeBase64(payload, out var encoded)) return "Encoded proxy URL payload is incomplete.";
            var decodedValue = Encoding.UTF8.GetString(encoded);
            if (!decodedValue.Contains(':') || !decodedValue.Contains('@')) return "Encoded proxy URL payload is incomplete.";
            return null;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return "Proxy URL needs a host.";
        if (uri.Port is < 1 or > 65535) return "Proxy URL needs a valid port.";
        if (new[] { "socks5", "http", "https" }.Contains(scheme) && uri.AbsolutePath != "/")
            return "This proxy type does not support a URL path.";
        var userInfo = uri.UserInfo;
        if (new[] { "shadowsocks", "ss" }.Contains(scheme) && (!userInfo.Contains(':') || userInfo.StartsWith(':') || userInfo.EndsWith(':')))
            return "Shadowsocks URL needs a method and password.";
        if (new[] { "vmess", "vless", "trojan", "wireguard" }.Contains(scheme) && string.IsNullOrWhiteSpace(userInfo))
            return "This proxy type needs credentials before the host.";
        if (scheme == "wireguard")
        {
            var query = uri.Query;
            if (!query.Contains("publicKey=", StringComparison.OrdinalIgnoreCase) ||
                !query.Contains("address=", StringComparison.OrdinalIgnoreCase))
                return "WireGuard URL needs publicKey and address parameters.";
        }
        return null;
    }

    public static void Validate(EditableTcpRouteConfig config)
    {
        _ = FileNameFor(config.ProfileName);
        if (config.ListenPort is < 1 or > 65535) throw new InvalidOperationException("Listen port must be between 1 and 65535.");
        if (config.Upstreams.Count == 0) throw new InvalidOperationException("Add at least one upstream.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var upstream in config.Upstreams)
        {
            var name = upstream.Name.Trim();
            if (name.Length == 0) throw new InvalidOperationException("Each upstream needs a name.");
            if (!names.Add(name)) throw new InvalidOperationException("Upstream names must be unique.");
            var error = ProxyUrlError(upstream.ProxyUrl);
            if (error is not null) throw new InvalidOperationException($"Upstream {name}: {error}");
            ValidateRules(upstream.Whitelist, $"{name} whitelist");
            ValidateRules(upstream.Blacklist, $"{name} blacklist");
        }
        for (var index = 0; index < config.Hosts.Count; index++)
        {
            var host = config.Hosts[index];
            if (string.IsNullOrWhiteSpace(host.Path)) throw new InvalidOperationException($"Host source {index + 1} needs a Path.");
            if (host.Source is RuleSourceKind.Geosite or RuleSourceKind.GeoIP) throw new InvalidOperationException("Host sources support only local or remote files.");
            if (!MatchTypes.Contains(host.Type)) throw new InvalidOperationException($"Host source {index + 1} must use base, suffix, pan, or regex.");
            if (host.Source == RuleSourceKind.Remote)
            {
                if (!IsRemoteUrl(host.Path)) throw new InvalidOperationException($"Host source {index + 1} needs an HTTP or HTTPS URL.");
                ValidateInterval(host.UpdateInterval, $"Host source {index + 1}");
            }
        }
    }

    private static void ValidateRules(IReadOnlyList<EditableDomainRule> rules, string label)
    {
        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            var item = $"{label} {index + 1}";
            if (string.IsNullOrWhiteSpace(rule.Path)) throw new InvalidOperationException($"{item} needs a Path.");
            if (rule.Source is RuleSourceKind.Geosite or RuleSourceKind.GeoIP)
            {
                var kind = rule.Source == RuleSourceKind.GeoIP ? "GeoIP" : "Geosite";
                var example = rule.Source == RuleSourceKind.GeoIP ? "cn,private" : "gfw,google,bing";
                if (NormalizeGeositeRules(rule.Type) is null) throw new InvalidOperationException($"{item} needs {kind} rules such as {example}.");
                if (IsRemoteUrl(rule.Path)) ValidateInterval(rule.UpdateInterval, item);
            }
            else
            {
                if (!MatchTypes.Contains(rule.Type)) throw new InvalidOperationException($"{item} must use base, suffix, pan, or regex.");
                if (rule.Source == RuleSourceKind.Remote)
                {
                    if (!IsRemoteUrl(rule.Path)) throw new InvalidOperationException($"{item} needs an HTTP or HTTPS URL.");
                    ValidateInterval(rule.UpdateInterval, item);
                }
            }
        }
    }

    private static void ValidateInterval(string value, string label)
    {
        var text = value.Trim();
        var matches = DurationTokenRegex().Matches(text);
        var tokens = matches.Cast<Match>().ToArray();
        if (text.Length == 0 || string.Concat(tokens.Select(match => match.Value)) != text ||
            tokens.Sum(DurationSeconds) < 60)
            throw new InvalidOperationException($"{label} has an invalid update interval (minimum 1m).");
    }

    private static double DurationSeconds(Match match)
    {
        if (!double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out var amount)) return 0;
        var scale = match.Groups[2].Value switch
        {
            "ns" => 1e-9, "us" or "µs" or "μs" => 1e-6, "ms" => 1e-3,
            "s" => 1, "m" => 60, "h" => 3600, _ => 0
        };
        return amount * scale;
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)(ns|us|µs|μs|ms|s|m|h)")]
    private static partial Regex DurationTokenRegex();

    [GeneratedRegex(@"(?m)^# TcpRoute config generated by TcpRoute (?:Android|client)\r?\n?")]
    private static partial Regex LegacyGeneratedCommentRegex();

    private static void AppendRule(StringBuilder builder, string list, EditableDomainRule rule)
    {
        builder.AppendLine($"[[UpStreams.{list}]]");
        if ((rule.Source == RuleSourceKind.Geosite && rule.Path.Trim() == "geosite.dat") ||
            (rule.Source == RuleSourceKind.GeoIP && rule.Path.Trim() == "geoip.dat"))
        {
            var kind = rule.Source == RuleSourceKind.GeoIP ? "geoip" : "geosite";
            builder.AppendLine("Type=" + TomlString(kind));
            builder.AppendLine("Path=" + TomlString(string.Join(',', NormalizeGeositeRules(rule.Type)!.Split(',').Select(name => kind + ":" + name))));
            builder.AppendLine();
            return;
        }
        if (rule.Source is RuleSourceKind.Geosite or RuleSourceKind.GeoIP)
        {
            var prefix = rule.Source == RuleSourceKind.GeoIP ? "geoip:" : "geosite:";
            builder.AppendLine("Type=" + TomlString(prefix + NormalizeGeositeRules(rule.Type)!));
            builder.AppendLine($"Path={TomlString(rule.Path.Trim())}");
            if (IsRemoteUrl(rule.Path) && !string.IsNullOrWhiteSpace(rule.UpdateInterval))
                builder.AppendLine($"UpdateInterval={TomlString(rule.UpdateInterval.Trim())}");
        }
        else
        {
            builder.AppendLine($"Path={TomlString(rule.Path.Trim())}");
            builder.AppendLine($"Type={TomlString(rule.Type.Trim().ToLowerInvariant())}");
            if (rule.Source == RuleSourceKind.Remote && !string.IsNullOrWhiteSpace(rule.UpdateInterval))
                builder.AppendLine($"UpdateInterval={TomlString(rule.UpdateInterval.Trim())}");
        }
        builder.AppendLine();
    }

    private static string ExtractDnsToml(string content)
    {
        var result = new StringBuilder();
        var inDns = false;
        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[')) inDns = trimmed.Split('#')[0].Trim() == "[[Dns]]";
            if (inDns) result.AppendLine(line);
        }
        return result.ToString();
    }

    private static IEnumerable<EditableUpstream> ParseUpstreams(string content)
    {
        var result = new List<EditableUpstream>();
        List<string>? upstreamLines = null;
        List<string>? ruleLines = null;
        string? ruleList = null;
        var whitelist = new List<EditableDomainRule>();
        var blacklist = new List<EditableDomainRule>();

        void FlushRule()
        {
            if (ruleLines is null || ruleList is null) return;
            (ruleList == "Whitelist" ? whitelist : blacklist).Add(ParseRule(ruleLines));
            ruleLines = null;
            ruleList = null;
        }
        void FlushUpstream()
        {
            FlushRule();
            if (upstreamLines is null) return;
            var values = ParseValues(upstreamLines);
            var upstream = new EditableUpstream
            {
                Name = Value(values, "Name", "proxy"),
                Alias = Value(values, "Alias", ""),
                ProxyUrl = Value(values, "ProxyUrl", "socks5://127.0.0.1:1080"),
                DialProxyFor = Value(values, "DialProxyFor", ""),
                Remove = bool.TryParse(Value(values, "Remove"), out var remove) && remove,
                DnsResolve = bool.TryParse(Value(values, "DnsResolve"), out var dns) && dns,
                Credit = IntValue(values, "Credit"), Sleep = IntValue(values, "Sleep"),
                CorrectDelay = IntValue(values, "CorrectDelay")
            };
            upstream.Whitelist.AddRange(whitelist);
            upstream.Blacklist.AddRange(blacklist);
            result.Add(upstream);
            upstreamLines = null;
            whitelist = new();
            blacklist = new();
        }

        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = StripLineComment(line).Trim();
            if (trimmed == "[[UpStreams]]") { FlushUpstream(); upstreamLines = new(); }
            else if (trimmed is "[[UpStreams.Whitelist]]" or "[[UpStreams.Blacklist]]")
            {
                FlushRule();
                if (upstreamLines is not null) { ruleList = trimmed.Contains("Whitelist") ? "Whitelist" : "Blacklist"; ruleLines = new(); }
            }
            else if (trimmed.StartsWith("[[") && trimmed.EndsWith("]]")) FlushUpstream();
            else if (ruleLines is not null) ruleLines.Add(line);
            else upstreamLines?.Add(line);
        }
        FlushUpstream();
        return result;
    }

    private static IEnumerable<EditableHostSource> ParseHosts(string content)
    {
        var result = new List<EditableHostSource>();
        List<string>? lines = null;
        void Flush()
        {
            if (lines is null) return;
            var values = ParseValues(lines);
            var path = Value(values, "Path");
            result.Add(new EditableHostSource
            {
                Source = IsRemoteUrl(path) ? RuleSourceKind.Remote : RuleSourceKind.Local,
                Path = path, UpdateInterval = Value(values, "UpdateInterval", "24h"),
                Type = MatchTypes.Contains(Value(values, "Type")) ? Value(values, "Type").ToLowerInvariant() : "base",
                Credit = IntValue(values, "Credit")
            });
            lines = null;
        }
        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed == "[[Hosts]]") { Flush(); lines = new(); }
            else if (trimmed.StartsWith("[[") && trimmed.EndsWith("]]")) Flush();
            else lines?.Add(line);
        }
        Flush();
        return result;
    }

    private static EditableDomainRule ParseRule(List<string> lines)
    {
        var values = ParseValues(lines);
        var rawPath = Value(values, "Path").Trim();
        var rawType = Value(values, "Type").Trim().ToLowerInvariant();
        var geoReference = IsGeoType(rawType) ? rawType : IsGeoType(rawPath) ? rawPath.ToLowerInvariant() : null;
        var isGeoIP = geoReference?.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase) == true;
        var path = geoReference is null ? rawPath : string.IsNullOrWhiteSpace(rawPath) || IsGeoType(rawPath) ? (isGeoIP ? "geoip.dat" : "geosite.dat") : rawPath;
        return new EditableDomainRule
        {
            Source = geoReference is not null ? (isGeoIP ? RuleSourceKind.GeoIP : RuleSourceKind.Geosite) : IsRemoteUrl(path) ? RuleSourceKind.Remote : RuleSourceKind.Local,
            Type = geoReference is not null ? NormalizeGeositeRules(geoReference)! : MatchTypes.Contains(rawType) ? rawType : "base",
            Path = path,
            UpdateInterval = Value(values, "UpdateInterval", "24h")
        };
    }

    private static Dictionary<string, string> ParseValues(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = StripLineComment(raw);
            var separator = line.IndexOf('=');
            if (separator >= 0) values[line[..separator].Trim()] = Unquote(line[(separator + 1)..]);
        }
        return values;
    }

    private static bool IsRemoteUrl(string value) => Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrWhiteSpace(uri.Host);

    private static bool IsGeositeType(string value) => value.Trim().StartsWith("geosite:", StringComparison.OrdinalIgnoreCase) &&
        NormalizeGeositeRules(value) is not null;

    private static bool IsGeoIPType(string value) => value.Trim().StartsWith("geoip:", StringComparison.OrdinalIgnoreCase) &&
        NormalizeGeositeRules(value) is not null;

    private static bool IsGeoType(string value) => IsGeositeType(value) || IsGeoIPType(value);

    private static string? NormalizeGeositeRules(string value)
    {
        var text = value.Trim();
        var raw = text.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase) ? text["geosite:".Length..]
            : text.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase) ? text["geoip:".Length..] : text;
        var rules = raw.Split(',').Select(item => item.Trim().ToLowerInvariant()).Select(item => item.StartsWith("geosite:") ? item["geosite:".Length..] : item.StartsWith("geoip:") ? item["geoip:".Length..] : item).ToArray();
        return rules.Length > 0 && rules.All(item => Regex.IsMatch(item, @"^[^\\/:,@\s]+(?:@[^\\/:,@\s]+)*$"))
            ? string.Join(',', rules) : null;
    }

    private static bool TryDecodeBase64(string value, out byte[] data)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        try { data = Convert.FromBase64String(normalized); return true; }
        catch (FormatException) { data = []; return false; }
    }

    private static string Value(Dictionary<string, string> values, string key, string fallback = "") =>
        values.TryGetValue(key, out var value) ? value : fallback;
    private static int IntValue(Dictionary<string, string> values, string key) => int.TryParse(Value(values, key), out var value) ? value : 0;
    private static string TomlString(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == trimmed[^1] && (trimmed[0] == '"' || trimmed[0] == '\'')
            ? trimmed[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\") : trimmed;
    }

    private static string StripLineComment(string line)
    {
        var single = false; var quote = false; var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '\\' && !escaped) { escaped = true; continue; }
            if (character == '"' && !single && !escaped) quote = !quote;
            else if (character == '\'' && !quote && !escaped) single = !single;
            else if (character == '#' && !single && !quote) return line[..index];
            escaped = false;
        }
        return line;
    }
}
