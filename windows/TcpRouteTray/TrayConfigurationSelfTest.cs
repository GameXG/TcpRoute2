using System.IO;
using System.Text.Json;

namespace TcpRouteTray;

internal static class TrayConfigurationSelfTest
{
    public static void Run()
    {
        CheckTunBypassRuleValidation();
        var legacyGeo = JsonSerializer.Deserialize<TraySettings>("{\"GeoDataInterval\":\"daily\"}")!;
        legacyGeo.ValidatePreferences();
        Expect(!legacyGeo.UserConfigurationJson().Contains("GeoDataInterval", StringComparison.Ordinal), "Removed Geo data preference was retained");
        var changes = new ConfigurationChangeTracker("original");
        Expect(!changes.Observe("partial"), "A partial save reloaded immediately");
        Expect(!changes.Observe("complete"), "An unstable save was accepted");
        Expect(changes.Observe("complete"), "A stable save did not trigger reload");
        Expect(!changes.Observe("complete"), "An unchanged file reloaded repeatedly");
        changes.Accept("menu update");
        Expect(!changes.Observe("menu update"), "A client write triggered a reload loop");

        var directory = Path.Combine(Path.GetTempPath(), "TcpRoute2-TrayConfiguration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "TcpRoute2Client.json");
            var applied = new TraySettings { UseWintun = true };
            Expect(!applied.AutoReloadClientSettings, "Client JSON auto-reload must default off");
            File.WriteAllText(path, "{invalid");
            Expect(ReferenceEquals(TraySettings.ReadForReload(path, applied, false), applied), "Disabled JSON reload read an incomplete external edit");
            File.WriteAllText(path, "{\"UseWintun\":false,\"AutoReloadClientSettings\":true}");
            Expect(TraySettings.ReadForReload(path, applied, false).UseWintun, "JSON edit bypassed the disabled reload setting");
            var reloaded = TraySettings.ReadForReload(path, applied, true);
            Expect(!reloaded.UseWintun && reloaded.AutoReloadClientSettings, "Explicit JSON reload did not apply saved settings");
            var settings = new TraySettings {
                SelectedConfigId = "config.toml", UseWintun = true, EnableIpv6 = true,
                OutboundInterfaceId = Guid.NewGuid().ToString("D"), GeoDataLastError = "managed state",
                TunAutoBypassLocalProxies = false, TunBypassProcesses = new[] { @"C:\Proxy\engine.exe", "other.exe" },
                TunBypassRules = new[] { new TunBypassRule { Network = "udp", Destinations = new[] { "192.0.2.0/24", "2001:db8::1" }, Ports = new[] { "443", "8000-9000" } } },
                TunRecovery = new WintunNetworkState { Name = "TcpRoute-1234ABCD", InterfaceIndex = 7 }
            };
            settings.SaveUserConfiguration(path);
            var userJson = File.ReadAllText(path);
            using var json = JsonDocument.Parse(userJson);
            Expect(!json.RootElement.TryGetProperty("TunRecovery", out _), "Network recovery leaked into user configuration");
            var loaded = TraySettings.Load(path);
            loaded.ValidatePreferences();
            Expect(loaded.UseWintun && loaded.EnableIpv6 && loaded.OutboundInterfaceId == settings.OutboundInterfaceId, "Runtime preferences did not round-trip");
            Expect(!loaded.TunAutoBypassLocalProxies && loaded.TunBypassProcesses.SequenceEqual(settings.TunBypassProcesses), "TUN bypass preferences did not round-trip");
            Expect(loaded.TunBypassRules.Length == 1 && loaded.TunBypassRules[0].Network == "udp" &&
                loaded.TunBypassRules[0].Destinations.SequenceEqual(settings.TunBypassRules[0].Destinations) &&
                loaded.TunBypassRules[0].Ports.SequenceEqual(settings.TunBypassRules[0].Ports), "Destination bypass rules did not round-trip");
            Expect(new TraySettings().TunAutoBypassLocalProxies, "Local proxy bypass must default on");
            loaded.CopyManagedStateFrom(settings);
            Expect(loaded.TunRecovery?.InterfaceIndex == 7, "Reload discarded network recovery");
            Expect(loaded.UserConfigurationJson() == userJson, "Managed-state changes altered the configuration signature");
            loaded.TunBypassRules[0].Ports = new[] { "444" };
            Expect(loaded.UserConfigurationJson() != userJson, "Bypass rule changes did not affect reload signature");
            loaded.TunBypassProcesses = new[] { "*.exe" };
            try { loaded.ValidatePreferences(); throw new Exception("Wildcard process bypass was accepted"); }
            catch (InvalidDataException) { }
            loaded.TunBypassProcesses = Array.Empty<string>();
            loaded.OutboundInterfaceId = "invalid";
            try { loaded.ValidatePreferences(); throw new Exception("Invalid interface ID was accepted"); }
            catch (InvalidDataException) { }
            File.WriteAllText(path, "{invalid");
            try { _ = TraySettings.Load(path); throw new Exception("Invalid JSON was accepted"); }
            catch (InvalidDataException) { }
            Expect(File.ReadAllText(path) == "{invalid", "Invalid user configuration was overwritten");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void CheckTunBypassRuleValidation()
    {
        foreach (var rules in new[] {
            "[]", "[{\"Destinations\":[\"192.0.2.99/24\",\"2001:db8::/32\",\"::ffff:192.0.2.1\"]}]",
            "[{\"Network\":\"tcp\",\"Ports\":[\"1\",\"65535\",\"8000-9000\"]}]",
            "[{\"Network\":\"udp\",\"Destinations\":[\"192.0.2.1\"],\"Ports\":[\"443\"]}]"
        })
        {
            var settings = JsonSerializer.Deserialize<TraySettings>("{\"TunBypassRules\":" + rules + "}")!;
            settings.ValidatePreferences();
        }
        foreach (var rules in new[] {
            "null", "[null]", "[{}]", "[{\"Network\":\"udp\"}]",
            "[{\"Ports\":[\"0\"]}]", "[{\"Ports\":[\"65536\"]}]", "[{\"Ports\":[\"9000-8000\"]}]",
            "[{\"Ports\":[\"80-90-100\"]}]", "[{\"Ports\":[\" 80\"]}]", "[{\"Ports\":[\"+80\"]}]",
            "[{\"Network\":\"TCP\",\"Ports\":[\"80\"]}]", "[{\"Network\":\"icmp\",\"Ports\":[\"80\"]}]",
            "[{\"Destinations\":[\"example.com\"]}]", "[{\"Destinations\":[\"127.1\"]}]",
            "[{\"Destinations\":[\"192.0.2.1/33\"]}]", "[{\"Destinations\":[\"2001:db8::/129\"]}]",
            "[{\"Destinations\":[\"fe80::1%7\"]}]", "[{\"Destinations\":[\"::ffff:192.0.2.0/120\"]}]",
            "[{\"Destinations\":[\"192.0.2.1\"],\"Port\":[\"443\"]}]"
        })
        {
            try
            {
                var settings = JsonSerializer.Deserialize<TraySettings>("{\"TunBypassRules\":" + rules + "}")!;
                settings.ValidatePreferences();
            }
            catch (Exception error) when (error is InvalidDataException or JsonException) { continue; }
            throw new InvalidOperationException("Invalid TUN bypass rules were accepted: " + rules);
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
