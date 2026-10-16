using System;
using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TcpRouteTray;

internal sealed record TcpRouteConfig(string Id, string Name, string Path, string Content)
{
    public TcpRouteRuntimeConfig ToRuntime(bool allowLan)
    {
        var listen = TcpRouteConfigParser.GetListenAddress(Content);
        var host = allowLan ? "0.0.0.0" : "127.0.0.1";
        var runtimeContent = TcpRouteConfigParser.ReplaceListenAddress(Content, host, listen.Port);
        return new TcpRouteRuntimeConfig(Name, runtimeContent, listen.Port);
    }
}

internal readonly record struct TcpRouteRuntimeConfig(string SourceName, string Content, int Port);

internal sealed record GeoDataSource(string Type, string Url, string ChecksumUrl, string UpdateInterval)
{
    public static TimeSpan ParseInterval(string value)
    {
        if (value is "0" or "off") return TimeSpan.Zero;
        var parts = Regex.Matches(value, @"(?<amount>[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?<unit>ns|us|\u00b5s|\u03bcs|ms|s|m|h)");
        var position = 0;
        decimal ticks = 0;
        foreach (Match part in parts)
        {
            if (part.Index != position || !decimal.TryParse(part.Groups["amount"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
                throw new InvalidDataException("GeoData UpdateInterval must be 0 or a duration such as 24h or 1h30m.");
            var multiplier = part.Groups["unit"].Value switch {
                "h" => TimeSpan.TicksPerHour, "m" => TimeSpan.TicksPerMinute, "s" => TimeSpan.TicksPerSecond,
                "ms" => TimeSpan.TicksPerMillisecond, "ns" => 0.01m, _ => 10m
            };
            if (amount > (long.MaxValue - ticks) / multiplier) throw new InvalidDataException("GeoData UpdateInterval is too large.");
            ticks += amount * multiplier;
            position += part.Length;
        }
        if (position != value.Length || ticks < TimeSpan.TicksPerMinute)
            throw new InvalidDataException("GeoData UpdateInterval must be 0 or at least 1m.");
        return TimeSpan.FromTicks((long)ticks);
    }
}

internal readonly record struct ListenAddress(string Host, int Port);

internal static partial class TcpRouteConfigParser
{
    private static readonly Regex AddrRegex = new(
        @"(?m)^(?<indent>\s*)addr\s*=\s*(?<quote>[""'])(?<value>[^""']+)\k<quote>",
        RegexOptions.Compiled);

    public static ListenAddress GetListenAddress(string content)
    {
        var match = AddrRegex.Match(StripLineComments(content));
        if (!match.Success)
        {
            throw new InvalidOperationException("TcpRoute2 config must contain an addr value.");
        }

        return ParseAddress(match.Groups["value"].Value);
    }

    public static IReadOnlyList<GeoDataSource> GetGeoDataSources(string content)
    {
        var sources = new List<GeoDataSource>();
        Dictionary<string, string>? values = null;
        void Finish()
        {
            if (values is null) return;
            var type = values.GetValueOrDefault("Type", "").ToLowerInvariant();
            if (type is not ("geosite" or "geoip")) throw new InvalidDataException($"GeoData Type must be geosite or geoip: {type}");
            var url = values.GetValueOrDefault("URL", "");
            if (url == "") url = $"https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/{type}.dat";
            var checksum = values.GetValueOrDefault("ChecksumURL", "");
            var interval = values.GetValueOrDefault("UpdateInterval", "0");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https")) throw new InvalidDataException($"GeoData {type} URL must be HTTP(S).");
            if (checksum != "" && (!Uri.TryCreate(checksum, UriKind.Absolute, out var checksumUri) || checksumUri.Scheme is not ("http" or "https"))) throw new InvalidDataException($"GeoData {type} ChecksumURL must be HTTP(S), or empty to disable verification.");
            _ = GeoDataSource.ParseInterval(interval);
            sources.Add(new GeoDataSource(type, url, checksum, interval));
        }
        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = StripLineComment(raw).Trim();
            if (line == "[[GeoData]]") { Finish(); values = new(StringComparer.OrdinalIgnoreCase); continue; }
            if (line.StartsWith('[')) { Finish(); values = null; continue; }
            if (values is null || !line.Contains('=')) continue;
            var parts = line.Split('=', 2); var key = parts[0].Trim(); var value = parts[1].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') values[key] = value[1..^1];
        }
        Finish();
        if (sources.GroupBy(source => source.Type, StringComparer.Ordinal).Any(group => group.Count() > 1)) throw new InvalidDataException("GeoData may define each type only once.");
        return sources;
    }

    public static string ReplaceListenAddress(string content, string host, int port)
    {
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = content.Replace("\r\n", "\n").Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var uncommentedLine = StripLineComment(lines[index]);
            var match = AddrRegex.Match(uncommentedLine);
            if (!match.Success)
            {
                continue;
            }

            lines[index] = $"{match.Groups["indent"].Value}addr=\"{host}:{port}\"";
            return string.Join(newline, lines);
        }

            throw new InvalidOperationException("TcpRoute2 config must contain an addr value.");
    }

    private static ListenAddress ParseAddress(string address)
    {
        if (address.StartsWith("[", StringComparison.Ordinal))
        {
            var end = address.IndexOf(']');
            if (end < 0 || end + 2 > address.Length || address[end + 1] != ':')
            {
                throw new InvalidOperationException($"Invalid TcpRoute2 listen address: {address}");
            }

            return new ListenAddress(address[1..end], ParsePort(address[(end + 2)..], address));
        }

        var colon = address.LastIndexOf(':');
        if (colon < 0)
        {
            return new ListenAddress("", ParsePort(address, address));
        }

        return new ListenAddress(address[..colon], ParsePort(address[(colon + 1)..], address));
    }

    private static int ParsePort(string rawPort, string originalAddress)
    {
        if (!int.TryParse(rawPort, out var port) || port < 1 || port > 65535)
        {
            throw new InvalidOperationException($"Invalid TcpRoute2 listen address: {originalAddress}");
        }

        return port;
    }

    private static string StripLineComments(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = StripLineComment(lines[i]);
        }

        return string.Join('\n', lines);
    }

    private static string StripLineComment(string line)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escaped = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '\\' && !escaped)
            {
                escaped = true;
                continue;
            }

            if (character == '"' && !inSingleQuote && !escaped)
            {
                inDoubleQuote = !inDoubleQuote;
            }
            else if (character == '\'' && !inDoubleQuote && !escaped)
            {
                inSingleQuote = !inSingleQuote;
            }
            else if (character == '#' && !inSingleQuote && !inDoubleQuote)
            {
                return line[..index];
            }

            escaped = false;
        }

        return line;
    }
}
