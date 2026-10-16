namespace TcpRouteTray;

internal static class ConfigCodecSelfTest
{
    public static void Run()
    {
        const string dnsConfig = "addr=\"127.0.0.1:7070\"\n[[Dns]] # primary\nType=\"doh\"\nURL=\"https://dns.example/dns-query\"\nPriority=10\nTimeout=\"3s\"\n[[Dns]]\nType=\"system\"\nPriority=20\n";
        var dnsDraft = EditableTcpRouteConfigCodec.Parse("dns", dnsConfig);
        var dnsRendered = EditableTcpRouteConfigCodec.Render(dnsDraft);
        Expect(dnsRendered.Contains(dnsDraft.DnsToml, StringComparison.Ordinal) &&
            dnsDraft.DnsToml.Contains("Priority=20", StringComparison.Ordinal), "Quick save dropped DNS configuration");
        var defaults = new NetworkFeaturePolicy();
        var dialDraft = EditableTcpRouteConfigCodec.Parse("dial", "addr=\"127.0.0.1:7070\"\n[[UpStreams]]\nName=\"front\"\nProxyUrl=\"socks5://127.0.0.1:1080\"\nDialProxyFor=\"target\"\n[[UpStreams]]\nName=\"target\"\nProxyUrl=\"https://127.0.0.1:8443\"\n");
        Expect(dialDraft.Upstreams.Single(node => node.Name == "front").DialProxyFor == "target" && EditableTcpRouteConfigCodec.Render(dialDraft).Contains("DialProxyFor=\"target\""), "Windows codec dropped DialProxyFor");
        dialDraft.Upstreams.Single(node => node.Name == "front").Alias = "\u9999\u6E2F \u00B7 01";
        var aliasRoundTrip = EditableTcpRouteConfigCodec.Parse("alias", EditableTcpRouteConfigCodec.Render(dialDraft));
        Expect(aliasRoundTrip.Upstreams.Single(node => node.Name == "front").Alias == "\u9999\u6E2F \u00B7 01", "Windows codec dropped a readable alias");
        dialDraft.Upstreams.Single(node => node.Name == "front").DialProxyFor = "tw_0,hk_*";
        var selectorsRoundTrip = EditableTcpRouteConfigCodec.Parse("selectors", EditableTcpRouteConfigCodec.Render(dialDraft));
        Expect(selectorsRoundTrip.Upstreams.Single(node => node.Name == "front").DialProxyFor == "tw_0,hk_*", "Windows codec changed multi-target prefix selectors");
        Expect(!defaults.AllowStun && !defaults.AllowQuic && !defaults.EnableIpv6,
            "Network features must default off");
        var enabledEnvironment = new NetworkFeaturePolicy(true, true, true).EnvironmentVariables;
        Expect(enabledEnvironment.Values.All(value => value == bool.TrueString),
            "Enabled network features were not mapped to the core environment");
        var geoSources = TcpRouteConfigParser.GetGeoDataSources("[[GeoData]]\nType=\"geosite\"\nURL=\"https://example.test/geosite.dat\"\nUpdateInterval=\"0\"\n");
        Expect(geoSources.Count == 1 && geoSources[0].ChecksumUrl == "" && geoSources[0].UpdateInterval == "0", "GeoData TOML did not preserve omitted-checksum settings");
        var geoFollowedByRule = TcpRouteConfigParser.GetGeoDataSources("[[GeoData]]\nType=\"geoip\"\nUpdateInterval=\"24h\"\n[[UpStreams.Whitelist]]\nType=\"suffix\"\nPath=\"local.txt\"\n");
        Expect(geoFollowedByRule.Count == 1 && geoFollowedByRule[0].Type == "geoip" && geoFollowedByRule[0].Url.EndsWith("geoip.dat") && geoFollowedByRule[0].ChecksumUrl == "", "GeoData parser crossed TOML table boundaries or invented a checksum URL");
        Expect(GeoDataSource.ParseInterval("24h") == TimeSpan.FromDays(1) && GeoDataSource.ParseInterval("1h30m") == TimeSpan.FromMinutes(90), "TOML Geo data duration was not applied");
        var proxy = EditableUpstream.ProxyTemplate("socks5");
        proxy.Whitelist.Add(EditableDomainRule.Template(RuleSourceKind.Local));
        proxy.Whitelist[0].Path = "rules/local.txt";
        proxy.Whitelist.Add(new EditableDomainRule
        {
            Source = RuleSourceKind.Remote,
            Type = "regex",
            Path = "https://example.com/rules.txt",
            UpdateInterval = "6h"
        });
        proxy.Whitelist.Add(EditableDomainRule.Template(RuleSourceKind.Geosite));
        proxy.Whitelist.Add(new EditableDomainRule
        {
            Source = RuleSourceKind.Geosite,
            Type = "cn",
            Path = "https://example.com/geosite.dat",
            UpdateInterval = "12h"
        });
        proxy.Whitelist.Add(EditableDomainRule.Template(RuleSourceKind.GeoIP));
        var source = new EditableTcpRouteConfig { ProfileName = "self-test", ListenPort = 7071 };
        source.Upstreams[0].Whitelist.Add(new EditableDomainRule { Type = "suffix", Path = "rules/direct.txt" });
        source.Upstreams.Add(proxy);
        source.Hosts.Add(new EditableHostSource { Path = "hosts/local.txt" });
        source.Hosts.Add(new EditableHostSource
        {
            Source = RuleSourceKind.Remote,
            Path = "https://example.com/hosts.txt",
            UpdateInterval = "2h45m",
            Type = "suffix",
            Credit = -100
        });

        proxy.Whitelist[2].Type = "gfw,google,bing";
        var rendered = EditableTcpRouteConfigCodec.Render(source);
        Expect(rendered.StartsWith("addr=\"", StringComparison.Ordinal),
            "Generated config contains an unnecessary client comment");
        Expect(EditableTcpRouteConfigCodec.StripLegacyGeneratedComment(
                "# TcpRoute config generated by TcpRoute client\n" + rendered) == rendered,
            "Legacy generated comment should be hidden");
        Expect(rendered.Contains("Type=\"geosite\"\nPath=\"geosite:gfw,geosite:google,geosite:bing\"", StringComparison.Ordinal),
            "Geosite rules did not restore the config prefix");
        Expect(rendered.Contains("Type=\"geoip\"\nPath=\"geoip:cn,geoip:private\"", StringComparison.Ordinal),
            "GeoIP rules did not restore the config prefix");
        var parsed = EditableTcpRouteConfigCodec.Parse("self-test", rendered);
        Expect(parsed.Hosts.Count == 2, "Host sources did not round-trip");
        Expect(parsed.Hosts[1].Type == "suffix" && parsed.Hosts[1].Credit == -100, "Host Type and Credit did not round-trip");
        Expect(parsed.Upstreams[0].Whitelist.Count == 1, "Direct routing rules did not round-trip");
        Expect(parsed.Upstreams[1].Whitelist.Count == 5, "Routing sources did not round-trip");
        Expect(parsed.Upstreams[1].Whitelist[3].Source == RuleSourceKind.Geosite, "Remote Geosite Path changed its source type");
        Expect(parsed.Upstreams[1].Whitelist[2].Type == "gfw,google,bing", "Geosite rules were not compacted for the UI model");
        Expect(parsed.Upstreams[1].Whitelist[4].Source == RuleSourceKind.GeoIP && parsed.Upstreams[1].Whitelist[4].Type == "cn,private",
            "GeoIP rules did not round-trip");
        Expect(parsed.Upstreams[1].Whitelist[4].QuickPath == "", "GeoIP Path is visible in Quick UI");
        parsed.Upstreams[1].Whitelist[4].QuickPath = "changed.dat";
        Expect(parsed.Upstreams[1].Whitelist[4].Path == "geoip.dat", "Hidden GeoIP Path was modified through Quick UI");

        var oldTypeOnly = """
addr="127.0.0.1:7070"
[[UpStreams]]
Name="direct"
ProxyUrl="direct://0.0.0.0:0000"
[[UpStreams.Whitelist]]
Type="geosite:gfw"
""";
        var oldPathOnly = oldTypeOnly.Replace("Type=\"geosite:gfw\"", "Path=\"geosite:gfw\"");
        foreach (var content in new[] { oldTypeOnly, oldPathOnly })
        {
            var migrated = EditableTcpRouteConfigCodec.Parse("migration", content).Upstreams[0].Whitelist[0];
            Expect(migrated.Type == "gfw" && migrated.Path == "geosite.dat", "Old Geosite preview shape was not migrated");
        }

        Expect(EditableTcpRouteConfigCodec.ProxyUrlError("socks5://missing-port") is not null, "Invalid proxy URL was accepted");
        Expect(EditableTcpRouteConfigCodec.ProxyUrlError("socks5://127.0.0.1:1080") is null, "Valid SOCKS5 URL was rejected");
        Expect(!EditableTcpRouteConfigCodec.ProxySchemes.Contains("socks4") &&
               !EditableTcpRouteConfigCodec.ProxySchemes.Contains("socks4a"),
            "Removed SOCKS4 types are still offered");
        Expect(EditableTcpRouteConfigCodec.ProxyUrlError("socks4a://127.0.0.1:1080") is not null,
            "Removed SOCKS4a URL was accepted");
        Expect(EditableTcpRouteConfigCodec.ProxyUrlError("ss://YWVzLTI1Ni1nY206cGFzc3dvcmRAMTI3LjAuMC4xOjgzODg=") is null,
            "Encoded Shadowsocks URL was rejected");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
