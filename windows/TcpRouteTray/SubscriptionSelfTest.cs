using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TcpRouteTray;

internal static class SubscriptionSelfTest
{
    public static void Run()
    {
        const string urls = "socks5://127.0.0.1:1080#Entry\nhttps://user:secret@proxy.example:443#Exit";
        var plain = SubscriptionManager.Parse(urls);
        var encoded = SubscriptionManager.Parse(Convert.ToBase64String(Encoding.UTF8.GetBytes(urls)).TrimEnd('='));
        Expect(plain.Count == 2 && plain[0].Name == "Entry" && plain[1].ProxyUrl.Contains("user:secret"), "URI subscription lost connection information");
        Expect(plain.Select(node => node.Id).SequenceEqual(encoded.Select(node => node.Id)), "Base64 subscription changed node identities");
        var vmess = "{\"v\":\"2\",\"ps\":\"VMess node\",\"add\":\"old.test\",\"port\":\"443\",\"id\":\"11111111-1111-1111-1111-111111111111\",\"net\":\"tcp\",\"tls\":\"tls\"}";
        var vmessBefore = SubscriptionManager.Parse("vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(vmess))).Single();
        var vmessAfter = SubscriptionManager.Parse("vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(vmess.Replace("old.test", "new.test", StringComparison.Ordinal)))).Single();
        Expect(vmessBefore.Name == "VMess node" && vmessBefore.Id == vmessAfter.Id, "VMess ps labels did not keep same-name identity during address rotation");
        var localName = "sub-local-" + Guid.NewGuid().ToString("N") + ".txt";
        var localPath = Path.Combine(AppContext.BaseDirectory, localName);
        File.WriteAllText(localPath, "socks5://local.test:1080#Local");
        try
        {
            var localSource = new SubscriptionSource { Id = "local", Name = "local", Url = localName };
            var localManager = new SubscriptionManager(Path.Combine(Path.GetTempPath(), "TcpRoute2-LocalSubscription-" + Guid.NewGuid().ToString("N")));
            localManager.RefreshAsync(localSource).GetAwaiter().GetResult();
            Expect(localManager.Load(localSource).Nodes.Single().Name == "Local", "Local subscription file was not loaded");
        }
        finally { if (File.Exists(localPath)) File.Delete(localPath); }
        var clash = SubscriptionManager.Parse("proxies:\n  - name: Clash HTTP\n    type: http\n    server: clash.example\n    port: 8080\n  - name: Clash SS\n    type: ss\n    server: ss.example\n    port: 443\n    cipher: aes-128-gcm\n    password: secret\n");
        Expect(clash.Count == 2 && clash[0].ProxyUrl == "http://clash.example:8080" && clash[1].ProxyUrl.StartsWith("ss://aes-128-gcm:secret@ss.example:443"), "Clash YAML proxies were not extracted");
        var surge = SubscriptionManager.Parse("[General]\nloglevel = notify\n\n[Proxy]\nSurge HTTP = http, surge.example, 8080\nSurge SOCKS = socks5, socks.example, 1080, username, password\n");
        Expect(surge.Count == 2 && surge[0].ProxyUrl == "http://surge.example:8080" && surge[1].ProxyUrl == "socks5://username:password@socks.example:1080", $"Surge proxy entries were not extracted: {string.Join(";", surge.Select(node => node.ProxyUrl))}");
        var surgeShadowsocksBody = "[Proxy]\nSS = ss, ss.example, 65000, encrypt-method=2022-blake3-aes-256-gcm, password=secret, tfo=true, udp-relay=true\n";
        try
        {
            var surgeShadowsocks = SubscriptionManager.Parse(surgeShadowsocksBody).Single();
            Expect(surgeShadowsocks.ProxyUrl.StartsWith("ss://2022-blake3-aes-256-gcm:secret@ss.example:65000"), $"Surge Shadowsocks options changed: {surgeShadowsocks.ProxyUrl}");
        }
        catch (Exception error) { throw new Exception($"Surge Shadowsocks parsing failed: {error.Message}", error); }
        var encodedSs = "ss://MjAyMi1ibGFrZTMtYWVzLTI1Ni1nY206TkRZMlkyTTNNelZpTkdSbE16YzFNR05tWkdVMVl6azFZekU0WTJKbE9UTT06WVRNNFpHRmhPREF0TjJNNE5DMDBZVFdGbU5UWXRNelpoTjJVNFpHWT0@ss.example:65000";
        Expect(SubscriptionManager.Parse(encodedSs).Count == 1, "Encoded Shadowsocks subscription URL was rejected");
        var clashVless = SubscriptionManager.Parse("proxies:\n  - name: vless-reality\n    type: vless\n    server: vless.example\n    port: 443\n    uuid: 11111111-1111-1111-1111-111111111111\n    flow: xtls-rprx-vision\n    tls: true\n    servername: tesla.com\n    client-fingerprint: chrome\n    network: tcp\n    reality-opts:\n      public-key: public-key\n      short-id: short-id\n").Single();
        Expect(clashVless.ProxyUrl.Contains("security=reality") && clashVless.ProxyUrl.Contains("pbk=public-key") && clashVless.ProxyUrl.Contains("flow=xtls-rprx-vision"), "Clash VLESS Reality options were not converted");
        foreach (var body in new[] { "", "proxies:\n- name: unsupported", "http://host:8080/not-supported", "direct://0.0.0.0:0", "[{\"ProxyUrl\":\"socks5://host:1080\",\"unknown\":true}]", "socks5://a.test:1080#Same\nsocks5://b.test:1080#Same" })
            Reject(() => SubscriptionManager.Parse(body));

        var source = new SubscriptionSource {
            Id = "test", Url = "https://example.test/sub?token=private", SelectedNodes = ["entry"],
            Defaults = new() { Sleep = 80, Credit = -100, DnsResolve = false, CorrectDelay = 50, UpProxy = "front" },
            LockedFields = ["Sleep", "Credit", "UpProxy"],
            Overrides = new() { ["entry"] = new() { Sleep = 1, Credit = 500, CorrectDelay = 75, UpProxy = "wrong" } }
        };
        source.SelectedNodesAll = true;
        Expect(SubscriptionManager.PlanSync(source, "config.toml", [new SubscriptionNode { Id = "other", Name = "Other", ProxyUrl = "socks5://other.test:1080" }], []).Imports.Length == 0,
            "SelectedNodesAll overrode explicit SelectedNodes");
        source.SelectedNodesAll = false;
        var reverseSource = new SubscriptionSource { Id = "reverse", Defaults = new() { DialProxyFor = "target" }, LockedFields = ["DialProxyFor"] };
        var reverseNode = new SubscriptionNode { Id = "front", ProxyUrl = "socks5://127.0.0.1:1080", Options = new() { DialProxyFor = "other" } };
        using var reverseJson = JsonDocument.Parse(JsonSerializer.Serialize(SubscriptionManager.GenerateUpstream(reverseSource, reverseNode)));
        Expect(reverseJson.RootElement.GetProperty("DialProxyFor").GetString() == "other", "Node option did not override the default");
        Reject(() => new SubscriptionOptions { DialProxyFor = "target", UpProxy = "front" }.Validate());
        Reject(() => new SubscriptionOptions { DialProxyFor = "*" }.Validate());
        Reject(() => new SubscriptionOptions { DialProxyFor = "tw_0,*" }.Validate());
        new SubscriptionOptions { DialProxyFor = "tw_0*,hk_" }.Validate();
        var nodes = SubscriptionManager.Parse("[{\"Id\":\"entry\",\"Name\":\"Node\",\"ProxyUrl\":\"https://user:secret@proxy.example:443?credit=999&upProxy=remote#label\",\"Options\":{\"Sleep\":2,\"Credit\":800,\"DnsResolve\":true,\"CorrectDelay\":25}}]");
        source.Validate();
        using var generated = JsonDocument.Parse(JsonSerializer.Serialize(SubscriptionManager.GenerateUpstream(source, nodes[0])));
        var upstream = generated.RootElement;
        Expect(upstream.GetProperty("Sleep").GetInt32() == 1 && upstream.GetProperty("Credit").GetInt32() == 500, "Node overrides did not win over defaults");
        Expect(upstream.GetProperty("DnsResolve").GetBoolean() && upstream.GetProperty("CorrectDelay").GetInt32() == 75, "Unlocked node/local precedence failed");
        var url = upstream.GetProperty("ProxyUrl").GetString()!;
        Expect(url.Contains(":443?") && url.Contains("user:secret@") && url.Contains("upProxy=wrong") && !url.Contains("credit", StringComparison.OrdinalIgnoreCase) && !url.Contains("remote"), "URL options or overrides were not applied");
        source.LockedFields = ["Sleep"];
        source.Defaults.Sleep = null;
        source.Validate();
        var lockOnly = new SubscriptionSource { Id = "lock-only", Url = "https://example.test/sub", Defaults = new(), LockedFields = ["DnsResolve", "Credit", "Sleep", "CorrectDelay", "UpProxy", "DialProxyFor"] };
        lockOnly.Validate();
        lockOnly.LockedFields = ["Unknown"];
        Reject(lockOnly.Validate);
        lockOnly.LockedFields = ["DialProxyFor"];
        lockOnly.Defaults.DialProxyFor = "";
        Reject(lockOnly.Validate);
        source.Defaults.Sleep = 80;
        source.LockedFields = ["Sleep", "Credit", "UpProxy"];
        var originalJson = new TraySettings { Subscriptions = [source] }.UserConfigurationJson();
        var restored = JsonSerializer.Deserialize<TraySettings>(originalJson)!;
        restored.ValidatePreferences();
        Expect(restored.UserConfigurationJson() == originalJson, "Subscription preferences did not round-trip");
        TestReadableJson();
        TestExclusionMigration();
        TestRefreshAndCache(source, nodes[0]).GetAwaiter().GetResult();
        if (Environment.GetEnvironmentVariable("TCPROUTE_TEST_CORE") is { Length: > 0 } executable)
        {
            TestTomlBridge(executable).GetAwaiter().GetResult();
            TestSubscriptionLifecycle(executable).GetAwaiter().GetResult();
        }
    }

    private static void TestReadableJson()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TcpRoute2-ReadableJson-" + Guid.NewGuid().ToString("N"));
        var name = "\u8282\u70b9 \u00b7 \u6D4B\u8BD5";
        var excluded = name + " \"A\"\\B\nnext";
        var url = "https://proxy.example:443?type=tcp&security=tls&sni=example.com";
        var source = new SubscriptionSource {
            Id = "readable", Url = "https://example.test/sub?format=json&client=windows",
            ExcludedNodesByProfile = new() { ["config.toml"] = [excluded] }
        };
        try
        {
            var settings = new TraySettings { Subscriptions = [source] };
            var path = Path.Combine(directory, "TcpRoute2Client.json");
            settings.SaveUserConfiguration(path);
            var json = File.ReadAllText(path);
            Expect(json.Contains(name) && json.Contains(source.Url), "User configuration escaped readable names or URL separators");
            Expect(TraySettings.Load(path).Subscriptions.Single().ExcludedNodesByProfile["config.toml"].Single() == excluded,
                "Readable JSON did not preserve quotes, backslashes, or newlines");
            File.WriteAllText(path, JsonSerializer.Serialize(settings));
            var legacy = TraySettings.Load(path);
            legacy.SaveUserConfiguration(path);
            Expect(File.ReadAllText(path) == json, "Legacy escaped configuration did not save as readable JSON");

            var cacheDirectory = Path.Combine(directory, "subscriptions");
            var manager = new SubscriptionManager(cacheDirectory);
            manager.Restore(source, new SubscriptionCache { Nodes = [new SubscriptionNode { Id = "node", Name = name, ProxyUrl = url }] });
            var cachePath = Directory.GetFiles(cacheDirectory, "*.json").Single();
            var cacheJson = File.ReadAllText(cachePath);
            Expect(cacheJson.Contains(name) && cacheJson.Contains(url) && cacheJson.Contains('\n'), "Subscription cache is not readable indented JSON");
            var node = new SubscriptionManager(cacheDirectory).Load(source).Nodes.Single();
            Expect(node.Name == name && node.ProxyUrl == url, "Readable cache changed node names or proxy URLs");
            using var generated = JsonDocument.Parse(JsonSerializer.Serialize(SubscriptionManager.GenerateUpstream(source, node)));
            Expect(generated.RootElement.GetProperty("Name").GetString() == source.ManagedPrefix + node.Id && generated.RootElement.GetProperty("Alias").GetString() == name, "Stable name and readable alias were not separated");
            var renamed = new SubscriptionSource { Id = source.Id, Url = source.Url, Name = "renamed" };
            manager.RememberSource(source, renamed);
            var restarted = new SubscriptionManager(cacheDirectory);
            var owners = SubscriptionTomlEditor.Ownership([renamed], restarted);
            Expect(owners.Any(owner => owner.Prefix == source.ManagedPrefix) && owners.Any(owner => owner.Prefix == renamed.ManagedPrefix) && owners.All(owner => owner.Nodes[node.Id] == name),
                "Subscription rename lost its prior prefix or node identities after restart");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void TestExclusionMigration()
    {
        var source = new SubscriptionSource { Id = "migration", SelectedNodes = ["id-a"], ExcludedNodesByProfile = new() {
            ["config.toml"] = ["\u9999\u6E2F", "id-a"], ["other.toml"] = ["Beta", "0123456789abcdef01234567"]
        } };
        SubscriptionManager.NormalizeExclusions(source, [new() { Id = "id-a", Name = "\u9999\u6E2F" }, new() { Id = "id-b", Name = "Beta" }, new() { Id = "id-c", Name = "id-a" }]);
        Expect(source.ExcludedNodesByProfile["config.toml"].SequenceEqual(["id-a"]), "Legacy names were not migrated or an existing ID was remapped");
        Expect(source.ExcludedNodesByProfile["other.toml"].SequenceEqual(["0123456789abcdef01234567", "id-b"]), "Profile-specific exclusions changed during migration");
        Expect(source.SelectedNodes.SequenceEqual(["id-a"]), "Migration changed selected node IDs");
        source.ExcludedNodesByProfile = new() { ["config.toml"] = ["Alpha"], ["other.toml"] = ["Alpha"] };
        SubscriptionManager.NormalizeExclusions(source, [new() { Id = "first-id", Name = "Alpha" }], "config.toml");
        SubscriptionManager.NormalizeExclusions(source, [new() { Id = "second-id", Name = "Alpha" }], "other.toml");
        Expect(source.ExcludedNodesByProfile["config.toml"].Single() == "first-id" && source.ExcludedNodesByProfile["other.toml"].Single() == "second-id", "Name migration mixed identities between profiles");
    }

    private static async Task TestRefreshAndCache(SubscriptionSource source, SubscriptionNode original)
    {
        var directory = Path.Combine(Path.GetTempPath(), "TcpRoute2-Subscriptions-" + Guid.NewGuid().ToString("N"));
        var body = JsonSerializer.Serialize(new[] { original });
        var fail = false;
        string? userAgent = null;
        using var http = new HttpClient(new ResponseHandler(() => fail ? new(HttpStatusCode.ServiceUnavailable) : new(HttpStatusCode.OK) { Content = new StringContent(body) }, request => userAgent = request.Headers.UserAgent.ToString()));
        try
        {
            var manager = new SubscriptionManager(directory, http);
            await manager.RefreshAsync(source);
            Expect(userAgent == source.UserAgent, "Default subscription UA was not sent");
            source.UserAgent = "clash";
            await manager.RefreshAsync(source);
            Expect(userAgent == source.UserAgent, "Subscription-specific UA was not sent");
            source.UserAgent = "";
            await manager.RefreshAsync(source);
            Expect(userAgent == "", "Subscription UA leaked to a later request");
            var initial = GeneratedSelection(manager, source);
            Expect(initial.Contains("sub_test_entry"), "Selected node was not generated");
            var cache = manager.Load(source);
            var fresh = new SubscriptionManager(directory, http);
            Expect(GeneratedSelection(fresh, source) == initial, "Cache did not survive manager restart");
            fail = true;
            try { await manager.RefreshAsync(source); throw new Exception("Failed response accepted"); }
            catch (InvalidOperationException error) { Expect(!error.Message.Contains("private") && !error.Message.Contains("secret"), "Subscription error leaked credentials"); }
            Expect(GeneratedSelection(manager, source) == initial, "Download failure replaced cached nodes");
            fail = false; body = "not a subscription";
            try { await manager.RefreshAsync(source); throw new Exception("Invalid response accepted"); }
            catch (InvalidOperationException) { }
            Expect(GeneratedSelection(manager, source) == initial, "Parse failure replaced cached nodes");
            body = "[{\"Id\":\"changed\",\"Name\":\"Node\",\"ProxyUrl\":\"https://user:new-secret@new-proxy.example:443\"}]";
            await manager.RefreshAsync(source);
            Expect(manager.Load(source).Nodes.Single().Id == "entry" && manager.Load(source).Nodes.Single().ProxyUrl.Contains("new-secret"), "Same-name connection change lost stable identity");
            body = "[{\"Id\":\"new\",\"Name\":\"New\",\"ProxyUrl\":\"socks5://127.0.0.1:1081\"}]";
            await manager.RefreshAsync(source);
            Expect(manager.Load(source).Nodes.Count == 1 && manager.Load(source).Nodes[0].Id == "new", "Removed nodes remained in the current subscription");
            var plan = SubscriptionManager.PlanSync(source, "config.toml", manager.Load(source).Nodes, [new(source.Id, "entry", "Node"), new("other", "keep", "Other")]);
            Expect(plan.Imports.Length == 0 && plan.Removals.Length == 1 && plan.Removals[0].NodeId == "entry", "Refresh ignored the default no-auto-select policy");
            manager.Restore(source, cache);
            Expect(manager.Load(source).Nodes.Count == 1 && GeneratedSelection(manager, source) == initial, "Runtime rollback did not restore cached nodes");
            source.SelectedNodes = [];
            Expect(GeneratedSelection(manager, source) == "[]", "Deselected node remained in import candidates");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static string GeneratedSelection(SubscriptionManager manager, SubscriptionSource source) =>
        JsonSerializer.Serialize(manager.Load(source).Nodes.Where(node => source.SelectedNodes.Contains(node.Id, StringComparer.Ordinal)).Select(node => SubscriptionManager.GenerateUpstream(source, node)));

    private static async Task TestSubscriptionLifecycle(string executable)
    {
        var source = new SubscriptionSource { Id = "lifecycle", Name = "mysub", Url = "https://example.test/sub", SelectedNodesAll = true, LockedFields = ["DialProxyFor"], Defaults = new() { Sleep = 80, Credit = 0, DnsResolve = false, CorrectDelay = 0, DialProxyFor = "exit" } };
        var oldNodes = SubscriptionManager.Parse("[{\"Id\":\"old-a\",\"Name\":\"Alpha\",\"ProxyUrl\":\"http://alice:oldpass@old.test:1080?standardheader=false\"},{\"Id\":\"old-b\",\"Name\":\"Beta\",\"ProxyUrl\":\"socks5://beta.test:1081\"}]");
        var owners = new[] { new SubscriptionOwnership(source.Id, source.ManagedPrefix, new() { ["old-a"] = "Alpha", ["old-b"] = "Beta", ["gamma"] = "Gamma" }) };
        const string original = "# untouched manual config\naddr='127.0.0.1:7070'\n[[UpStreams]]\nName='manual'\nProxyUrl='direct://0.0.0.0:0'\n[[UpStreams]]\nName='exit'\nProxyUrl='https://exit.test:443'\n[[UpStreams]]\nName='otherexit'\nProxyUrl='https://other.test:443'\n";
        var firstPlan = SubscriptionManager.PlanSync(source, "config.toml", [oldNodes[0]], []);
        var first = await SubscriptionTomlEditor.EditAsync(executable, original, firstPlan.Imports, firstPlan.Removals, owners);
        var customized = first.Content.Replace("Sleep=80", "Sleep=17 # user", StringComparison.Ordinal)
            .Replace("Credit=0", "Credit=-100", StringComparison.Ordinal).Replace("CorrectDelay=0", "CorrectDelay=43", StringComparison.Ordinal)
            .Replace("DnsResolve=false", "DnsResolve=true", StringComparison.Ordinal).Replace("DialProxyFor=\"exit\"", "DialProxyFor=\"otherexit\"", StringComparison.Ordinal)
            .Replace("old.test:1080?standardheader=false", "custom.test:9000?standardheader=false&custom=yes", StringComparison.Ordinal)
            + "\n[[UpStreams.Whitelist]]\nPath='user-sites.txt'\nType='suffix'\n";
        var secondPlan = SubscriptionManager.PlanSync(source, "config.toml", oldNodes, first.Selections);
        var before = await SubscriptionTomlEditor.EditAsync(executable, customized, secondPlan.Imports, secondPlan.Removals, owners);
        source.Defaults.Sleep = 120; source.Defaults.Credit = 50; source.Defaults.CorrectDelay = 90;
        var incoming = SubscriptionManager.Parse("[{\"Id\":\"rotated-a\",\"Name\":\"Alpha\",\"ProxyUrl\":\"http://alice:newpass@new.test:2080?standardheader=true\"},{\"Id\":\"gamma\",\"Name\":\"Gamma\",\"ProxyUrl\":\"socks5://gamma.test:1082\"}]");
        SubscriptionManager.ReconcileNodeNames(incoming, oldNodes, before.Selections);
        Expect(incoming[0].Id == "old-a", "Same-name lifecycle match did not retain the upstream name");
        var plan = SubscriptionManager.PlanSync(source, "config.toml", incoming, before.Selections);
        Expect(plan.Imports.Length == 2 && plan.Removals.Single().NodeId == "old-b", "Lifecycle failed to add Gamma and remove Beta");
        var after = await SubscriptionTomlEditor.EditAsync(executable, before.Content, plan.Imports, plan.Removals, owners);
        var parsed = EditableTcpRouteConfigCodec.Parse("lifecycle", after.Content);
        var alpha = parsed.Upstreams.Single(node => node.Name == "sub_mysub_old-a");
        var gamma = parsed.Upstreams.Single(node => node.Name == "sub_mysub_gamma");
        Expect(alpha.ProxyUrl.Contains("newpass@new.test:2080") && !alpha.ProxyUrl.Contains("custom=yes") && alpha.ProxyUrl.Contains("standardheader=true"), "Subscription connection fields or unlocked URL options were not refreshed");
        Expect(alpha.Sleep == 120 && alpha.Credit == 50 && alpha.CorrectDelay == 90 && !alpha.DnsResolve && alpha.DialProxyFor == "otherexit", "Refresh did not replace unlocked fields while retaining the locked field");
        Expect(alpha.Whitelist.Single().Path == "user-sites.txt" && after.Content.Contains("Sleep=120 # user"), "Lifecycle lost user rules or comments");
        Expect(gamma.Sleep == 120 && gamma.Credit == 50 && parsed.Upstreams.Single(node => node.Name == "sub_mysub_old-b").Remove && after.Content.StartsWith(original, StringComparison.Ordinal), "New node defaults, stale removal, or manual configuration changed unexpectedly");
        var cacheless = SubscriptionManager.Parse("[{\"Id\":\"different-id\",\"Name\":\"Alpha\",\"ProxyUrl\":\"http://alice:again@new.test:2081\"}]");
        SubscriptionManager.ReconcileNodeNames(cacheless, [], after.Selections);
        Expect(cacheless.Single().Id == "old-a", "Ownership metadata did not preserve same-name identity without cache");
        source.ExcludedNodesByProfile["config.toml"] = ["Gamma"];
        var excluded = SubscriptionManager.PlanSync(source, "config.toml", incoming, after.Selections);
        Expect(excluded.Imports.Length == 1 && excluded.Removals.Single().NodeId == "gamma" && excluded.Removals.Single().Delete, "Known deselection must delete its TOML entry");
        Expect(SubscriptionManager.PlanSync(source, "another.toml", incoming, []).Imports.Length == 2, "Node exclusion leaked to another profile");
        var empty = SubscriptionManager.Parse("[]");
        var clear = SubscriptionManager.PlanSync(source, "config.toml", empty, after.Selections);
        var cleared = await SubscriptionTomlEditor.EditAsync(executable, after.Content, clear.Imports, clear.Removals, owners);
        Expect(cleared.Selections.Length == 0 && cleared.Content.StartsWith(original, StringComparison.Ordinal), "Empty subscription did not remove only its owned entries");
        await TestMultiProfileLifecycle(executable, source, original, after.Content);
    }

    private static async Task TestMultiProfileLifecycle(string executable, SubscriptionSource source, string original, string firstProfile)
    {
        var directory = Path.Combine(Path.GetTempPath(), "TcpRoute2-SubscriptionProfiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var other = new SubscriptionSource { Id = "other", Url = "https://example.test/other" };
            var beta = new SubscriptionNode { Id = "profile-b-alpha", Name = "Alpha", ProxyUrl = "http://alice:before@old.test:1080" };
            var unrelated = new SubscriptionNode { Id = "stay", Name = "Unrelated", ProxyUrl = "socks5://unrelated.test:1080" };
            object Import(SubscriptionSource owner, SubscriptionNode node) => new { SourceId = owner.Id, NodeId = node.Id, NodeName = node.Name, Upstream = SubscriptionManager.GenerateUpstream(owner, node), owner.LockedFields };
            var secondOwners = new[] { new SubscriptionOwnership(source.Id, source.ManagedPrefix, new() { [beta.Id] = beta.Name, ["gamma"] = "Gamma", ["delta"] = "Delta" }), new SubscriptionOwnership(other.Id, other.ManagedPrefix, new() { [unrelated.Id] = unrelated.Name }) };
            var second = await SubscriptionTomlEditor.EditAsync(executable, original, [Import(source, beta), Import(other, unrelated)], [], secondOwners);
            var secondProfile = second.Content.Replace("Sleep=120", "Sleep=29 # profile-specific", StringComparison.Ordinal);
            var profiles = new[] {
                new TcpRouteConfig("config.toml", "first", Path.Combine(directory, "config.toml"), firstProfile),
                new TcpRouteConfig("second.toml", "second", Path.Combine(directory, "second.toml"), secondProfile),
                new TcpRouteConfig("manual.toml", "manual", Path.Combine(directory, "manual.toml"), original)
            };
            foreach (var profile in profiles) File.WriteAllText(profile.Path, profile.Content);
            var latest = SubscriptionManager.Parse("[{\"Id\":\"a-new\",\"Name\":\"Alpha\",\"ProxyUrl\":\"http://alice:latest@latest.test:3080\"},{\"Id\":\"gamma\",\"Name\":\"Gamma\",\"ProxyUrl\":\"socks5://gamma.test:1082\"},{\"Id\":\"delta\",\"Name\":\"Delta\",\"ProxyUrl\":\"socks5://delta.test:1083\"}]");
            var legacyNode = new SubscriptionNode { Id = "legacy-alpha", Name = "Alpha", ProxyUrl = "http://alice:before@old.test:1080" };
            var legacy = await SubscriptionTomlEditor.EditAsync(executable, original, [new { SourceId = source.Id, NodeId = legacyNode.Id, Upstream = SubscriptionManager.GenerateUpstream(source, legacyNode), source.LockedFields }], []);
            var legacyProfile = new TcpRouteConfig("legacy.toml", "legacy", Path.Combine(directory, "legacy.toml"), legacy.Content.Replace("Sleep=120", "Sleep=31", StringComparison.Ordinal));
            var migrated = await SubscriptionTomlEditor.PrepareSyncAsync(executable, source, "legacy.toml", [legacyProfile], latest, [legacyNode]);
            Expect(migrated.Single().After.Contains("sub_mysub_legacy-alpha") && migrated.Single().After.Contains("Sleep=120"), "Legacy cached names did not preserve identity and update unlocked defaults");
            var changes = await SubscriptionTomlEditor.PrepareSyncAsync(executable, source, "config.toml", profiles, latest);
            Expect(changes.Length == 2, "Subscription update modified an unrelated profile or omitted an imported profile");
            var dependent = secondProfile + "\n[[UpStreams]]\nName='dependent'\nProxyUrl='http://127.0.0.1:8080?upProxy=sub_mysub_profile-b-alpha'\n";
            File.WriteAllText(profiles[1].Path, dependent);
            var withDependency = profiles.Select(profile => profile.Id == "second.toml" ? profile with { Content = dependent } : profile).ToArray();
            var inactive = await SubscriptionTomlEditor.PrepareSyncAsync(executable, source, "config.toml", withDependency, latest.Where(node => node.Name != "Alpha").ToList());
            Expect(inactive.Single(change => change.Path == profiles[1].Path).After.Contains("Remove=true"), "Cross-profile missing node did not retain a protected parent");
            Expect(File.ReadAllText(profiles[0].Path) == firstProfile && File.ReadAllText(profiles[1].Path) == dependent, "Preparing changes partially wrote TOML files");
            File.WriteAllText(profiles[1].Path, secondProfile);
            File.WriteAllText(profiles[1].Path, "# concurrent\n" + secondProfile);
            try { SubscriptionTomlEditor.ApplyChanges(changes); throw new Exception("Concurrent multi-profile change was overwritten"); }
            catch (IOException) { }
            Expect(File.ReadAllText(profiles[0].Path) == firstProfile && File.ReadAllText(profiles[1].Path).StartsWith("# concurrent"), "Batch write did not roll back without losing user edits");
            File.WriteAllText(profiles[1].Path, secondProfile);
            SubscriptionTomlEditor.ApplyChanges(changes);
            var first = EditableTcpRouteConfigCodec.Parse("first", File.ReadAllText(profiles[0].Path));
            var secondParsed = EditableTcpRouteConfigCodec.Parse("second", File.ReadAllText(profiles[1].Path));
            Expect(first.Upstreams.Single(node => node.Name == "sub_mysub_old-a").Sleep == 120 && !first.Upstreams.Any(node => node.Name == "sub_mysub_gamma"), "First profile did not update unlocked fields or remove its excluded node");
            var secondAlpha = secondParsed.Upstreams.Single(node => node.Name == "sub_mysub_profile-b-alpha");
            Expect(secondAlpha.Sleep == 120 && secondAlpha.ProxyUrl.Contains("latest@latest.test:3080") && secondParsed.Upstreams.Any(node => node.Name == "sub_mysub_gamma"), "Cross-profile sync lost identity or failed to update unlocked fields");
            var updatedProfiles = profiles.Select(profile => profile with { Content = File.ReadAllText(profile.Path) }).ToArray();
            var removal = await SubscriptionTomlEditor.PrepareSourceRemovalAsync(executable, updatedProfiles, new HashSet<string>([source.Id], StringComparer.Ordinal), SubscriptionTomlEditor.Ownership([source, other]));
            Expect(removal.Length == 2, "Deleting a source omitted one of its profiles");
            SubscriptionTomlEditor.ApplyChanges(removal);
            foreach (var profile in profiles)
            {
                var state = await SubscriptionTomlEditor.EditAsync(executable, File.ReadAllText(profile.Path), [], [], SubscriptionTomlEditor.Ownership([source, other]));
                Expect(state.Selections.All(node => node.SourceId != source.Id), "Deleted source left owned upstreams behind");
            }
            Expect(File.ReadAllText(profiles[1].Path).Contains("sub_other_stay") && File.ReadAllText(profiles[2].Path) == original, "Source removal deleted another source or unrelated configuration");
            SubscriptionTomlEditor.RollbackChanges(removal);
            Expect(File.ReadAllText(profiles[0].Path) == updatedProfiles[0].Content && File.ReadAllText(profiles[1].Path) == updatedProfiles[1].Content, "Source removal rollback did not restore exact TOML content");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task TestTomlBridge(string executable)
    {
        var source = new SubscriptionSource { Id = "feed", Name = "mysub", Url = "https://example.test/sub" };
        var node = new SubscriptionNode { Id = "entry", Name = "Example", ProxyUrl = "socks5://127.0.0.1:1080" };
        object Import() => new { SourceId = source.Id, NodeId = node.Id, NodeName = node.Name, Upstream = SubscriptionManager.GenerateUpstream(source, node), source.LockedFields };
        SubscriptionOwnership[] Owners() => new[] { "sub_mysub_", "sub_newsub_" }.Select(prefix => new SubscriptionOwnership(source.Id, prefix, new() { [node.Id] = node.Name })).ToArray();
        const string original = "# retain comments\r\naddr='127.0.0.1:7070'\r\n";
        var directory = Path.Combine(Path.GetTempPath(), "TcpRoute2-SubscriptionImport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.toml");
            File.WriteAllText(path, original);
            var result = await SubscriptionTomlEditor.EditAsync(executable, original, [Import()], []);
            Expect(result.Content.StartsWith(original, StringComparison.Ordinal) && result.Content.Contains("Name=\"sub_mysub_entry\""), "Real core did not append a named TOML upstream");
            SubscriptionTomlEditor.WriteIfUnchanged(path, original, result.Content);
            var customized = result.Content.Replace("Sleep=80", "Sleep=12 # user override", StringComparison.Ordinal);
            node.ProxyUrl = "socks5://127.0.0.1:1081";
            var refreshed = await SubscriptionTomlEditor.EditAsync(executable, customized, [Import()], [], Owners());
            Expect(refreshed.Content.Contains("Sleep=80 # user override") && refreshed.Content.Contains(":1081"), "Real refresh did not update unlocked fields");
            source.LockedFields = ["Sleep"];
            var locked = await SubscriptionTomlEditor.EditAsync(executable, customized, [Import()], [], Owners());
            Expect(locked.Content.Contains("Sleep=12 # user override"), "Real core incorrectly overwrote a locked existing value");
            var chain = locked.Content + "\n[[UpStreams]]\nName='chain'\nProxyUrl='http://127.0.0.1:8080?upProxy=sub_mysub_entry'\n";
            source.Name = "newsub";
            var renamed = await SubscriptionTomlEditor.EditAsync(executable, chain, [Import()], [], Owners());
            Expect(renamed.Content.Contains("Name=\"sub_newsub_entry\"") && renamed.Content.Contains("upProxy=sub_newsub_entry"), "Subscription rename did not repair the proxy chain");
            var inactiveChain = await SubscriptionTomlEditor.EditAsync(executable, renamed.Content, [], [new(source.Id, node.Id)], Owners());
            Expect(inactiveChain.Selections.Length == 0 && inactiveChain.Content.Contains("Remove=true") && inactiveChain.Content.Contains("upProxy=sub_newsub_entry"), "Referenced upstream or its chain was lost during deselection");
            var removed = await SubscriptionTomlEditor.EditAsync(executable, locked.Content, [], [new(source.Id, node.Id)], Owners());
            Expect(removed.Selections.Length == 0 && removed.Content.Contains("Remove=true") && removed.Content.Contains("[[UpStreams]]"), "TOML upstream was not soft-removed");
            var reselected = await SubscriptionTomlEditor.EditAsync(executable, removed.Content, [Import()], [], Owners());
            Expect(reselected.Selections.Length == 1 && reselected.Content.Contains("Remove=false"), "Reselection did not reactivate the upstream");
            source.Defaults.DialProxyFor = "target";
            var reverseBase = original + "[[UpStreams]]\nName='target'\nProxyUrl='https://127.0.0.1:8443'\n";
            var reverse = await SubscriptionTomlEditor.EditAsync(executable, reverseBase, [Import()], []);
            Expect(reverse.Content.Contains("DialProxyFor=\"target\""), "Real importer dropped DialProxyFor");
            source.Defaults.DialProxyFor = null;
            source.LockedFields = ["DialProxyFor"];
            source.Validate();
            var reverseRefresh = await SubscriptionTomlEditor.EditAsync(executable, reverse.Content, [Import()], [], Owners());
            Expect(reverseRefresh.Content.Contains("DialProxyFor=\"target\""), "Lock without a default did not preserve the existing DialProxyFor");
            var deselected = await SubscriptionTomlEditor.EditAsync(executable, reverse.Content, [], [new(source.Id, node.Id, Delete: true)], Owners());
            Expect(deselected.Selections.Length == 0 && !deselected.Content.Contains("DialProxyFor=") && deselected.Content.Contains("Name='target'"), "DialProxyFor prevented deletion or removed its target");
            File.WriteAllText(path, "# concurrent edit\n" + result.Content);
            try { SubscriptionTomlEditor.WriteIfUnchanged(path, result.Content, refreshed.Content); throw new Exception("Concurrent edit was overwritten"); }
            catch (IOException) { }
            Expect(File.ReadAllText(path).StartsWith("# concurrent edit"), "Concurrent TOML change was lost");
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class ResponseHandler(Func<HttpResponseMessage> response, Action<HttpRequestMessage>? inspect = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            inspect?.Invoke(request);
            return Task.FromResult(response());
        }
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or JsonException) { return; }
        throw new InvalidOperationException("Invalid subscription data accepted");
    }
    private static void Expect(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
