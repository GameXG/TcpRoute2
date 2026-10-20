using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace TcpRouteTray;

internal sealed class TcpRouteController : IDisposable
{
    private readonly string _settingsPath;
    private readonly string _pidPath;
    private readonly string _runtimeStatePath;
    private readonly string _sessionSettingsPath;
    private readonly object _logLock = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Queue<string> _recentLog = new();
    private bool _starting;

    private TraySettings _settings;
    private TrayRuntimeState _runtimeState;
    private Process? _process;
    private string? _runtimeConfigPath;
    private TcpRouteRuntimeConfig? _runningRuntime;
    private TcpRouteConfig? _activeConfig;
    private bool _preserveUserSettings;
    private readonly SubscriptionManager _subscriptions;
    private string? _subscriptionSelectionContent;
    private SubscriptionIdentity[] _subscriptionSelections = [];

    public TcpRouteController()
    {
        var temp = Path.GetTempPath();
        var portableRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));

        ConfigDirectory = portableRoot;
        RuntimeDirectory = Path.Combine(temp, WintunNetwork.AdapterName(portableRoot));
        _subscriptions = new SubscriptionManager(Path.Combine(RuntimeDirectory, "subscriptions"));
        LogDirectory = Path.Combine(portableRoot, "logs");
        GeoDataDirectory = portableRoot;
        _settingsPath = Path.Combine(portableRoot, "TcpRoute2Client.json");
        _pidPath = Path.Combine(RuntimeDirectory, "tcproute.pid");
        _runtimeStatePath = Path.Combine(RuntimeDirectory, "tray-runtime.json");
        _sessionSettingsPath = Path.Combine(RuntimeDirectory, "client-recovery.json");
        var legacySettingsPath = Path.Combine(portableRoot, "TcpRouteClient.json");
        _settings = TraySettings.Load(File.Exists(_settingsPath) ? _settingsPath : legacySettingsPath);
        if (File.Exists(_sessionSettingsPath)) _settings.CopyManagedStateFrom(TraySettings.Load(_sessionSettingsPath));
        _runtimeState = TrayRuntimeState.Load(_runtimeStatePath);
    }

    public string ConfigDirectory { get; }

    public string RuntimeDirectory { get; }

    public string LogDirectory { get; }

    public string GeoDataDirectory { get; }

    public string LogPath => Path.Combine(LogDirectory, "tcproute.log");

    public string LastMessage { get; private set; } = "Ready";

    public string? SelectedConfigId => _settings.SelectedConfigId;
    public string SettingsPath => _settingsPath;
    public bool AutoReloadClientSettings => _settings.AutoReloadClientSettings;
    public IReadOnlyList<SubscriptionSource> Subscriptions => _settings.Subscriptions;
    public SubscriptionCache SubscriptionCache(SubscriptionSource source) => _subscriptions.Load(source);
    public HashSet<string> SubscriptionSelectedNodes(string sourceId)
    {
        var config = GetSelectedConfig();
        if (_subscriptionSelectionContent != config.Content)
        {
            _subscriptionSelections = SubscriptionTomlEditor.EditAsync(ResolveExecutable(), config.Content, [], [], SubscriptionOwnership()).GetAwaiter().GetResult().Selections;
            _subscriptionSelectionContent = config.Content;
        }
        return _subscriptionSelections.Where(node => node.SourceId == sourceId).Select(node => node.NodeId).ToHashSet(StringComparer.Ordinal);
    }
    public bool SubscriptionsDue() => _settings.Subscriptions.Any(source => _subscriptions.IsDue(source));
    private SubscriptionOwnership[] SubscriptionOwnership() => SubscriptionTomlEditor.Ownership(_settings.Subscriptions, _subscriptions);

    public async Task RefreshDueSubscriptionsAsync()
    {
        foreach (var source in _settings.Subscriptions.Where(source => _subscriptions.IsDue(source)).ToArray())
        {
            try { await RefreshSubscriptionAsync(source.Id); }
            catch (Exception error) { RecordEvent(error.Message); }
        }
    }

    public async Task RefreshSubscriptionAsync(string id)
    {
        var source = _settings.Subscriptions.First(source => source.Id == id);
        var config = GetSelectedConfig();
        var selected = await SubscriptionTomlEditor.EditAsync(ResolveExecutable(), config.Content, [], [], SubscriptionOwnership());
        source.SelectedNodes = selected.Selections.Where(node => node.SourceId == id).Select(node => node.NodeId).ToArray();
        var previous = _subscriptions.Load(source);
        var owned = selected.Selections.Where(node => node.SourceId == id).ToArray();
        await _subscriptions.RefreshAsync(source, owned);
        SubscriptionTomlChange[] changes = [];
        var previousActive = _activeConfig;
        var wasRunning = IsRunning;
        var reloaded = false;
        try
        {
            changes = await SubscriptionTomlEditor.PrepareSyncAsync(ResolveExecutable(), source, config.Id, LoadConfigs(), _subscriptions.Load(source).Nodes, previous.Nodes, SubscriptionOwnership());
            SubscriptionTomlEditor.ApplyChanges(changes);
            if (changes.Any(change => change.Path == config.Path)) { await ReloadConfigurationAsync(false); reloaded = true; }
            var result = await SubscriptionTomlEditor.EditAsync(ResolveExecutable(), File.ReadAllText(config.Path), [], [], SubscriptionOwnership());
            _settings.Subscriptions.First(item => item.Id == id).SelectedNodes = result.Selections.Where(node => node.SourceId == id).Select(node => node.NodeId).ToArray();
            _subscriptionSelectionContent = result.Content;
            _subscriptionSelections = result.Selections;
            SaveSettings(userConfigurationChanged: true);
            RecordEvent($"Subscription {source.Id} synchronized: {_subscriptions.Load(source).Nodes.Count} nodes, {changes.Length} profiles updated");
        }
        catch
        {
            try { SubscriptionTomlEditor.RollbackChanges(changes); }
            finally { _subscriptions.Restore(source, previous); _subscriptionSelectionContent = null; }
            if (reloaded) {
                if (wasRunning && previousActive is not null) await StartAsync(previousActive);
                else { _activeConfig = previousActive; if (previousActive is not null) GeoDataManager.Configure(TcpRouteConfigParser.GetGeoDataSources(previousActive.Content)); }
            }
            throw;
        }
    }


    public async Task SelectSubscriptionNodeAsync(string sourceId, string nodeId, bool selected, bool reload = true)
    {
        var source = _settings.Subscriptions.First(source => source.Id == sourceId);
        var node = _subscriptions.Load(source).Nodes.FirstOrDefault(node => node.Id == nodeId);
        if (selected && (node is null || node.Removed)) throw new InvalidOperationException("This subscription node is unavailable; update the subscription or select another node.");
        _settings.ValidatePreferences();
        var config = GetSelectedConfig();
        var nodeName = node?.Name ?? _subscriptionSelections.FirstOrDefault(identity => identity.SourceId == sourceId && identity.NodeId == nodeId)?.NodeName ?? nodeId;
        object[] imports = selected ? [new { SourceId = sourceId, NodeId = nodeId, NodeName = node!.Name, Upstream = SubscriptionManager.GenerateUpstream(source, node!), source.LockedFields }] : [];
        SubscriptionIdentity[] removals = selected ? [] : [new(sourceId, nodeId, nodeName, Delete: node is { Removed: false })];
        var result = await ApplySubscriptionTomlAsync(config, imports, removals, reload);
        var currentSource = _settings.Subscriptions.First(item => item.Id == sourceId);
        currentSource.SelectedNodes = result.Selections.Where(identity => identity.SourceId == sourceId).Select(identity => identity.NodeId).ToArray();
        SubscriptionManager.NormalizeExclusions(currentSource, _subscriptions.Load(currentSource).Nodes, config.Id);
        var excluded = currentSource.ExcludedNodesByProfile.GetValueOrDefault(config.Id, []).ToHashSet(StringComparer.Ordinal);
        if (selected) { excluded.Remove(nodeId); excluded.Remove(nodeName); } else { excluded.Remove(nodeName); excluded.Add(nodeId); }
        currentSource.ExcludedNodesByProfile[config.Id] = excluded.Order(StringComparer.Ordinal).ToArray();
        SaveSettings(userConfigurationChanged: true);
    }

    private async Task<SubscriptionTomlResult> ApplySubscriptionTomlAsync(TcpRouteConfig config, object[] imports, SubscriptionIdentity[] removals, bool reload = true)
    {
        var result = await SubscriptionTomlEditor.EditAsync(ResolveExecutable(), config.Content, imports, removals, SubscriptionOwnership());
        if (result.Content != config.Content)
        {
            SubscriptionTomlEditor.WriteIfUnchanged(config.Path, config.Content, result.Content);
            try { if (reload) await ReloadConfigurationAsync(false); }
            catch
            {
                if (File.ReadAllText(config.Path) == result.Content) SubscriptionTomlEditor.WriteIfUnchanged(config.Path, result.Content, config.Content);
                throw;
            }
        }
        _subscriptionSelectionContent = result.Content;
        _subscriptionSelections = result.Selections;
        return result;
    }

    public bool AllowLan => !_settings.UseWintun && _settings.AllowLan;

    public bool SystemProxyEnabled => !_settings.UseWintun && _settings.SystemProxyEnabled;

    public bool AllowStun => _settings.AllowStun;

    public bool AllowQuic => _settings.AllowQuic;

    public bool EnableIpv6 => _settings.EnableIpv6;
    public bool UseWintun => _settings.UseWintun;
    public string OutboundInterfaceId => _settings.OutboundInterfaceId;
    public bool IsStarting => _starting;
    public bool RequiresNetworkRecovery => _settings.TunRecovery is not null && !RecordedCoreIsAlive();
    public bool HasTunRecoveryState => _settings.TunRecovery is not null;

    private bool RecordedCoreIsAlive()
    {
        if (_runtimeState.Pid is not { } pid) return false;
        try { using var process = Process.GetProcessById(pid); return !process.HasExited && ProcessMatchesRuntime(process, _runtimeState); }
        catch (ArgumentException) { return false; }
    }

    public string Appearance => _settings.Appearance;

    public string Language => _settings.Language;

    public int? RunningPort => IsRunning ? _runningRuntime?.Port ?? _runtimeState.Port : null;

    public int LocalProxyPort
    {
        get
        {
            if (RunningPort is { } port) return port;
            if (_activeConfig is not null)
            {
                try { return TcpRouteConfigParser.GetListenAddress(_activeConfig.Content).Port; }
                catch { }
            }
            return 7070;
        }
    }

    public bool IsRunning
    {
        get
        {
            if (_process is { HasExited: false })
            {
                return true;
            }

            if (_process is { HasExited: true })
            {
                if (_starting) return false;
                CleanupExitedProcess();
            }

            if (_runtimeState.Pid is null)
            {
                if (File.Exists(_pidPath))
                {
                    File.Delete(_pidPath);
                }
                return false;
            }

            try
            {
                using var process = Process.GetProcessById(_runtimeState.Pid.Value);
                if (!ProcessMatchesRuntime(process, _runtimeState))
                {
                    CleanupExitedProcess();
                    return false;
                }

                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                CleanupExitedProcess();
                return false;
            }
        }
    }

    public void Initialize()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
        GeoDataManager.EnsureInstalled(GeoDataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        EnsureDefaultConfig();
        if (!IsRunning) { RestoreTunNetwork(); RestoreManagedProxy(); }
        else if (_settings.TunRecovery is not null) NetworkChange.NetworkAddressChanged += PhysicalNetworkChanged;

        var configs = LoadConfigs();
        _activeConfig = configs.FirstOrDefault(config => config.Id == _settings.SelectedConfigId) ?? configs.FirstOrDefault();
        if (IsRunning && _runtimeState.RuntimeConfigPath is { } activePath && File.Exists(activePath))
        {
            var source = configs.FirstOrDefault(config => config.Id == _settings.SelectedConfigId) ?? configs.FirstOrDefault();
            if (source is not null) _activeConfig = source with { Content = File.ReadAllText(activePath) };
        }
        if (_settings.SelectedConfigId is null && configs.Count > 0)
        {
            SelectConfig(configs[0].Id);
        }
        if (!File.Exists(_settingsPath)) SaveSettings(userConfigurationChanged: true);
        if (_activeConfig is not null) GeoDataManager.Configure(TcpRouteConfigParser.GetGeoDataSources(_activeConfig.Content));
    }

    public GeoDataSnapshot GetGeoDataSnapshot() => GeoDataManager.Snapshot(
        GeoDataDirectory,
        GeoDataManager.ConfiguredInterval(),
        _settings.GeoDataLastSuccess,
        _settings.GeoDataLastError);

    public async Task<GeoDataSnapshot> UpdateGeoDataAsync(bool onlyIfDue = false)
    {
        var state = GetGeoDataSnapshot();
        if (onlyIfDue && !GeoDataManager.IsDue(state)) return state;
        try
        {
            await GeoDataManager.UpdateAsync(GeoDataDirectory, onlyIfDue: onlyIfDue);
            _settings.GeoDataLastSuccess = DateTimeOffset.UtcNow;
            _settings.GeoDataLastError = null;
            LastMessage = "Geo data updated";
        }
        catch (Exception error)
        {
            _settings.GeoDataLastError = error.Message;
            LastMessage = $"Geo data update failed: {error.Message}";
            throw;
        }
        finally
        {
            SaveSettings();
        }
        return GetGeoDataSnapshot();
    }

    public List<TcpRouteConfig> LoadConfigs()
    {
        Directory.CreateDirectory(ConfigDirectory);
        EnsureDefaultConfig();

        var configs = Directory
            .EnumerateFiles(ConfigDirectory, "*.toml")
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(path => new TcpRouteConfig(
                Id: Path.GetFileName(path),
                Name: Path.GetFileNameWithoutExtension(path),
                Path: path,
                Content: File.ReadAllText(path)))
            .ToList();

        return configs;
    }

    public EditableTcpRouteConfig LoadEditableConfig(string? configId = null)
    {
        var configs = LoadConfigs();
        var selected = configs.FirstOrDefault(config => config.Id == (configId ?? _settings.SelectedConfigId))
            ?? configs.FirstOrDefault();
        return EditableTcpRouteConfigCodec.FromConfig(selected);
    }

    public (string ProfileName, string Content) LoadRawEditableConfig(string? configId = null)
    {
        var configs = LoadConfigs();
        var selected = configs.FirstOrDefault(config => config.Id == (configId ?? _settings.SelectedConfigId))
            ?? configs.FirstOrDefault();
        if (selected is null)
        {
            var fallback = new EditableTcpRouteConfig();
            return (fallback.ProfileName, EditableTcpRouteConfigCodec.Render(fallback));
        }

        return (selected.Name, EditableTcpRouteConfigCodec.StripLegacyGeneratedComment(selected.Content));
    }

    public string SaveEditableConfig(EditableTcpRouteConfig config)
    {
        Directory.CreateDirectory(ConfigDirectory);
        var fileName = EditableTcpRouteConfigCodec.FileNameFor(config.ProfileName);
        var path = Path.Combine(ConfigDirectory, fileName);
        File.WriteAllText(path, EditableTcpRouteConfigCodec.Render(config), Encoding.UTF8);
        SelectConfig(fileName);
        LastMessage = $"Saved {fileName}";
        return fileName;
    }

    public string SaveRawEditableConfig(string profileName, string content)
    {
        Directory.CreateDirectory(ConfigDirectory);
        _ = TcpRouteConfigParser.GetListenAddress(content);
        var fileName = EditableTcpRouteConfigCodec.FileNameFor(profileName);
        var path = Path.Combine(ConfigDirectory, fileName);
        File.WriteAllText(path, content, Encoding.UTF8);
        SelectConfig(fileName);
        LastMessage = $"Saved {fileName}";
        return fileName;
    }

    public void SelectConfig(string configId)
    {
        _settings.SelectedConfigId = configId;
        SaveSettings(userConfigurationChanged: true);
        LastMessage = $"Selected {Path.GetFileNameWithoutExtension(configId)}";
    }

    public void SetAutoReloadClientSettings(bool enabled)
    {
        _settings.AutoReloadClientSettings = enabled;
        SaveSettings(userConfigurationChanged: true);
        LastMessage = enabled ? "Automatic client settings reload enabled" : "Automatic client settings reload disabled";
    }

    public void SetAllowLan(bool allowLan)
    {
        _settings.AllowLan = allowLan;
        SaveSettings(userConfigurationChanged: true);
        LastMessage = allowLan ? "LAN clients enabled" : "LAN clients disabled";
    }

    public void SetAllowStun(bool enabled)
    {
        if (IsRunning || _starting) throw new InvalidOperationException("Stop TcpRoute2 before changing STUN.");
        _settings.AllowStun = enabled;
        SaveSettings(userConfigurationChanged: true);
        LastMessage = "STUN setting saved; restart to apply";
    }

    public void SetAllowQuic(bool enabled)
    {
        if (IsRunning || _starting) throw new InvalidOperationException("Stop TcpRoute2 before changing QUIC.");
        _settings.AllowQuic = enabled;
        SaveSettings(userConfigurationChanged: true);
        LastMessage = "QUIC setting saved; restart to apply";
    }

    public void SetEnableIpv6(bool enabled)
    {
        if (IsRunning || _starting) throw new InvalidOperationException("Stop TcpRoute2 before changing IPv6.");
        _settings.EnableIpv6 = enabled;
        SaveSettings(userConfigurationChanged: true);
        LastMessage = "IPv6 setting saved; restart to apply";
    }

    public void SetAppearance(string appearance)
    {
        _settings.Appearance = appearance;
        SaveSettings(userConfigurationChanged: true);
    }

    public void SetLanguage(string language)
    {
        _settings.Language = language;
        SaveSettings(userConfigurationChanged: true);
    }

    public void SetSystemProxyEnabled(bool enabled)
    {
        if (UseWintun && enabled) throw new InvalidOperationException("System proxy is unavailable in Wintun mode.");
        _settings.SystemProxyEnabled = enabled;
        SaveSettings(userConfigurationChanged: true);

        if (enabled)
        {
            var port = _runningRuntime?.Port ?? _runtimeState.Port;
            if (port is not null)
            {
                ApplyManagedProxy(port.Value);
                LastMessage = "Windows SOCKS proxy enabled";
            }
            else
            {
                LastMessage = "Windows SOCKS proxy will be enabled on start";
            }
        }
        else
        {
            RestoreManagedProxy();
            LastMessage = "Windows SOCKS proxy disabled";
        }
    }

    public void SetExecutablePath(string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("The TcpRoute2 executable was not found.", executablePath);
        }

        _settings.ExecutablePath = executablePath;
        SaveSettings(userConfigurationChanged: true);
        LastMessage = "TcpRoute2 executable path saved";
    }

    public void SetUseWintun(bool enabled)
    {
        if (IsRunning || _starting) throw new InvalidOperationException("Stop TcpRoute2 before changing network mode.");
        _settings.UseWintun = enabled;
        SaveSettings(userConfigurationChanged: true);
    }

    public void SetOutboundInterface(string interfaceId)
    {
        if (IsRunning || _starting) throw new InvalidOperationException("Stop TcpRoute2 before changing the outbound interface.");
        _settings.OutboundInterfaceId = string.IsNullOrEmpty(interfaceId) ? "" : Guid.Parse(interfaceId).ToString("D");
        SaveSettings(userConfigurationChanged: true);
    }

    public static void RelaunchElevated(string argument)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Could not locate the client executable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add(argument);
        _ = Process.Start(start) ?? throw new InvalidOperationException("Administrator relaunch failed.");
    }

    public async Task StartAsync(TcpRouteConfig? savedConfig = null)
    {
        if (_starting) return;
        if (_settings.UseWintun && !WintunNetwork.IsAdministrator)
        {
            RelaunchElevated("--start-wintun");
            System.Windows.Application.Current?.Shutdown();
            return;
        }
        await _lifecycle.WaitAsync();
        _starting = true;
        try
        {
        _settings.ValidatePreferences();
        StopProcess(clearProxy: true);

        var config = savedConfig ?? GetSelectedConfig();
        if (savedConfig is null)
        {
            var protectedConfig = await SubscriptionTomlEditor.ProtectAsync(ResolveExecutable(), config, _settings.Subscriptions, _subscriptions);
            if (protectedConfig.Content != config.Content)
            {
                SubscriptionTomlEditor.WriteIfUnchanged(config.Path, config.Content, protectedConfig.Content);
                config = config with { Content = protectedConfig.Content };
            }
        }
        GeoDataManager.Configure(TcpRouteConfigParser.GetGeoDataSources(config.Content));
        var runtime = config.ToRuntime(!_settings.UseWintun && _settings.AllowLan);
        var executablePath = ResolveExecutable();
        var runtimePath = Path.Combine(RuntimeDirectory, "tcproute-runtime.toml");
        WintunNetworkState? network = null;
        if (_settings.UseWintun)
        {
            WintunDependencies.Validate(executablePath, RuntimeInformation.ProcessArchitecture);
            WintunDependencies.Validate(Path.Combine(Path.GetDirectoryName(executablePath)!, "wintun.dll"), RuntimeInformation.ProcessArchitecture);
            network = await WintunNetwork.CaptureAsync(ConfigDirectory, _settings.EnableIpv6, _settings.OutboundInterfaceId);
        }

        Directory.CreateDirectory(RuntimeDirectory);
        File.WriteAllText(runtimePath, runtime.Content, Encoding.UTF8);
        CloseLog();
        AppendLogLine($"--- TcpRoute2 started at {DateTimeOffset.Now} ---");

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = $"-config \"{runtimePath}\"",
            WorkingDirectory = Path.GetDirectoryName(executablePath),
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.Environment["xray.location.asset"] = GeoDataDirectory;
        startInfo.Environment["TCPROUTE_CONFIG_BASE"] = Path.GetDirectoryName(config.Path)!;
        startInfo.Environment["TCPROUTE_CLIENT_SESSION"] = "true";
        startInfo.Environment.Remove("TCPROUTE_TEMP_UPSTREAMS_FILE");
        startInfo.Environment.Remove("TCPROUTE_WINTUN_NAME");
        startInfo.Environment.Remove("TCPROUTE_OUTBOUND_INTERFACE");
        startInfo.Environment.Remove("TCPROUTE_BOOTSTRAP_DNS");
        startInfo.Environment.Remove("TCPROUTE_TUN_AUTO_BYPASS");
        startInfo.Environment.Remove("TCPROUTE_TUN_BYPASS_PROCESSES");
        startInfo.Environment.Remove("TCPROUTE_TUN_BYPASS_RULES");
        if (network is not null)
        {
            startInfo.Environment["TCPROUTE_WINTUN_NAME"] = network.Name;
            startInfo.Environment["TCPROUTE_OUTBOUND_INTERFACE"] = network.InterfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            startInfo.Environment["TCPROUTE_BOOTSTRAP_DNS"] = string.Join(',', network.DnsServers);
            startInfo.Environment["TCPROUTE_TUN_AUTO_BYPASS"] = _settings.TunAutoBypassLocalProxies.ToString();
            startInfo.Environment["TCPROUTE_TUN_BYPASS_PROCESSES"] = JsonSerializer.Serialize(_settings.TunBypassProcesses);
            startInfo.Environment["TCPROUTE_TUN_BYPASS_RULES"] = JsonSerializer.Serialize(_settings.TunBypassRules);
        }
        var networkPolicy = new NetworkFeaturePolicy(_settings.AllowStun, _settings.AllowQuic, _settings.EnableIpv6);
        foreach (var (key, value) in networkPolicy.EnvironmentVariables) startInfo.Environment[key] = value;
        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        var tunReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proxyReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? startupError = null;
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data == "TCPROUTE_WINTUN_READY") tunReady.TrySetResult();
            else if (args.Data == "TCPROUTE_PROXY_READY") proxyReady.TrySetResult();
            else AppendLogLine(args.Data);
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data)) startupError = args.Data.Trim();
            AppendLogLine(args.Data);
        };
        process.Exited += (_, _) =>
        {
            if (network is not null) tunReady.TrySetException(new InvalidOperationException("TcpRoute2 exited before Wintun was ready."));
            var detail = string.IsNullOrWhiteSpace(startupError) ? "no core error was reported" : startupError;
            proxyReady.TrySetException(new InvalidOperationException($"TcpRoute2 exited with code {process.ExitCode} before the local proxy was ready: {detail}"));
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_starting && ReferenceEquals(_process, process))
                {
                    var exitCode = process.ExitCode;
                    try { CleanupExitedProcess(); RestoreManagedProxy(); }
                    catch (Exception error) { LastMessage = "Network recovery required: " + error.Message; return; }
                    LastMessage = $"TcpRoute2 exited with code {exitCode}";
                }
            }));
        };

        process.Start();
        _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        File.WriteAllText(_pidPath, process.Id.ToString());

        _runtimeConfigPath = runtimePath;
        _runningRuntime = runtime;
        _runtimeState = new TrayRuntimeState
        {
            Pid = process.Id,
            ExecutablePath = executablePath,
            RuntimeConfigPath = runtimePath,
            Port = runtime.Port
        };
        _runtimeState.Save(_runtimeStatePath);
        await proxyReady.Task.WaitAsync(TimeSpan.FromSeconds(15));

        if (network is not null)
        {
            await tunReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await WaitForSocksAsync(process, runtime.Port);
            _settings.TunRecovery = network;
            SaveSettings();
            await WintunNetwork.ApplyAsync(network);
            if (process.HasExited || !WintunNetwork.PhysicalInterfaceIsAvailable(network))
                throw new InvalidOperationException("Core or physical interface changed during Wintun startup.");
            NetworkChange.NetworkAddressChanged += PhysicalNetworkChanged;
        }
        else
        {
            await WaitForSocksAsync(process, runtime.Port);
            if (_settings.SystemProxyEnabled) ApplyManagedProxy(runtime.Port);
        }

        LastMessage = network is null ? $"TcpRoute2 is running on SOCKS5 :{runtime.Port}" : $"Wintun is running on {network.Name}";
        _activeConfig = config;
        }
        catch
        {
            StopProcess(clearProxy: true);
            throw;
        }
        finally { _starting = false; _lifecycle.Release(); }
    }

    public Task RestartAsync()
    {
        return StartAsync();
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try {
        StopProcess(clearProxy: true);
        LastMessage = "TcpRoute2 stopped";
        } finally { _lifecycle.Release(); }
    }

    public async Task<long> MeasureSocksConnectAsync(CancellationToken cancellationToken = default)
    {
        var port = _runningRuntime?.Port ?? _runtimeState.Port;
        if (port is null || !IsRunning)
        {
            throw new InvalidOperationException("Start TcpRoute2 before speed test.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var token = timeout.Token;

        var watch = Stopwatch.StartNew();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port.Value, token);
        await using var stream = client.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, token);
        var authResponse = new byte[2];
        await stream.ReadExactlyAsync(authResponse, token);
        if (authResponse[0] != 0x05 || authResponse[1] != 0x00)
        {
            throw new InvalidOperationException("TcpRoute2 SOCKS5 auth handshake failed.");
        }

        var host = Encoding.ASCII.GetBytes("www.gstatic.com");
        var request = new byte[7 + host.Length];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        request[3] = 0x03;
        request[4] = (byte)host.Length;
        Buffer.BlockCopy(host, 0, request, 5, host.Length);
        request[^2] = (byte)(443 >> 8);
        request[^1] = (byte)(443 & 0xff);

        await stream.WriteAsync(request, token);
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        if (header[0] != 0x05 || header[1] != 0x00)
        {
            throw new InvalidOperationException($"TcpRoute2 SOCKS5 connect failed with code {header[1]}.");
        }

        await ConsumeSocksAddressAsync(stream, header[3], token);
        watch.Stop();

        LastMessage = $"Speed test: {watch.ElapsedMilliseconds} ms";
        return watch.ElapsedMilliseconds;
    }

    public void OpenConfigFolder()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Process.Start(new ProcessStartInfo(ConfigDirectory) { UseShellExecute = true });
    }

    public void OpenLogs()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "tcproute.log", Filter = "Log file|*.log|All files|*.*" };
        if (dialog.ShowDialog() == true) File.WriteAllLines(dialog.FileName, ReadRecentLogLines(1000), Encoding.UTF8);
    }

    public IReadOnlyList<string> ReadRecentLogLines(int maximumLines)
    {
        lock (_logLock) return _recentLog.TakeLast(Math.Max(0, maximumLines)).ToArray();
    }

    public void ClearRecentOutput() { lock (_logLock) _recentLog.Clear(); }

    public void RecordEvent(string message) { LastMessage = message; AppendLogLine(message); }

    public (TraySettings Settings, TcpRouteConfig Config, string Signature) ReadConfigurationSnapshot(bool reloadClientSettings = true)
    {
        var settings = TraySettings.ReadForReload(_settingsPath, _settings, reloadClientSettings);
        settings.ValidatePreferences();
        var id = settings.SelectedConfigId ?? _settings.SelectedConfigId ?? "config.toml";
        if (Path.GetFileName(id) != id || !id.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SelectedConfigId must be a TOML file name in the application directory.");
        var path = Path.Combine(ConfigDirectory, id);
        var config = new TcpRouteConfig(id, Path.GetFileNameWithoutExtension(id), path, File.ReadAllText(path));
        _ = TcpRouteConfigParser.GetGeoDataSources(config.Content);
        var signature = ConfigurationSignature(settings, config);
        return (settings, config, signature);
    }

    private static string ConfigurationSignature(TraySettings settings, TcpRouteConfig config) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(settings.UserConfigurationJson() + "\0" + config.Id + "\0" + config.Content)));

    public string AppliedConfigurationSignature() => _activeConfig is null ? "" : ConfigurationSignature(_settings, _activeConfig);

    public async Task ReloadConfigurationAsync(bool reloadClientSettings = true)
    {
        var next = ReadConfigurationSnapshot(reloadClientSettings);
        _ = TcpRouteConfigParser.GetListenAddress(next.Config.Content);
        var removedSources = _settings.Subscriptions.Select(source => source.Id).Except(next.Settings.Subscriptions.Select(source => source.Id), StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        SubscriptionTomlChange[] cleanup = removedSources.Count == 0 ? [] : await SubscriptionTomlEditor.PrepareSourceRemovalAsync(ResolveExecutable(), LoadConfigs(), removedSources, SubscriptionOwnership());
        var wasRunning = IsRunning;
        var previousSettings = _settings;
        var previousConfig = _activeConfig;
        var stopped = false;
        _preserveUserSettings = true;
        try
        {
            SubscriptionTomlEditor.ApplyChanges(cleanup);
            next = ReadConfigurationSnapshot(reloadClientSettings);
            if (wasRunning) { await StopAsync(); stopped = true; }
            next.Settings.CopyManagedStateFrom(previousSettings);
            _settings = next.Settings;
            if (wasRunning) await StartAsync(next.Config);
            else { _activeConfig = next.Config; GeoDataManager.Configure(TcpRouteConfigParser.GetGeoDataSources(next.Config.Content)); }
            foreach (var source in _settings.Subscriptions)
                if (previousSettings.Subscriptions.FirstOrDefault(old => old.Id == source.Id) is { } previous &&
                    (previous.ManagedPrefix != source.ManagedPrefix || previous.Url != source.Url))
                    _subscriptions.RememberSource(previous, source);
            RecordEvent("Configuration reloaded");
        }
        catch (Exception error)
        {
            Exception? cleanupError = null;
            try { SubscriptionTomlEditor.RollbackChanges(cleanup); } catch (Exception failure) { cleanupError = failure; }
            _settings = previousSettings;
            if (stopped && previousConfig is not null)
            {
                try { await StartAsync(previousConfig); }
                catch (Exception rollback) { throw new InvalidOperationException("Configuration reload and recovery failed: " + error.Message + "; " + rollback.Message, rollback); }
            }
            if (cleanupError is not null) throw new InvalidOperationException("Configuration reload failed; TOML rollback also failed: " + cleanupError.Message, error);
            throw new InvalidOperationException("Configuration reload failed; previous settings retained: " + error.Message, error);
        }
        finally { _preserveUserSettings = false; }
    }

    public void ClearWindowsProxy()
    {
        WindowsProxy.Clear();
        _settings.ProxyRecovery = null;
        _settings.ProxyOwnedPort = 0;
        _settings.SystemProxyEnabled = false;
        SaveSettings(userConfigurationChanged: true);
        LastMessage = "Windows proxy cleared";
    }

    public void Dispose()
    {
        StopProcess(clearProxy: true);
        CloseLog();
    }

    private TcpRouteConfig GetSelectedConfig()
    {
        var configs = LoadConfigs();
        var selected = _settings.SelectedConfigId is null ? configs.FirstOrDefault() : configs.FirstOrDefault(config => config.Id == _settings.SelectedConfigId);

        if (selected is null)
        {
            throw new InvalidOperationException("No TcpRoute2 configs found.");
        }

        _settings.SelectedConfigId = selected.Id;
        SaveSettings();
        return selected;
    }

    private string ResolveExecutable()
    {
        var configured = _settings.ExecutablePath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("TCPROUTE_EXE");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "amd64"
        };

        foreach (var candidate in ExecutableCandidates(arch))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Cannot find the TcpRoute2 executable. Put TcpRoute2.exe next to ProxyClient, set TCPROUTE_EXE, or locate it from the tray menu.");
    }

    private IEnumerable<string> ExecutableCandidates(string arch)
    {
        yield return Path.Combine(ConfigDirectory, "TcpRoute2.exe");
        yield return Path.Combine(ConfigDirectory, "TcpRoute.exe");

        yield return Path.Combine(AppContext.BaseDirectory, $"TcpRoute2-windows-{arch}.exe");
    }

    private static IEnumerable<string> EnumerateAncestors(string start)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(start));
        while (directory is not null)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }
    }

    private void StopProcess(bool clearProxy)
    {
        NetworkChange.NetworkAddressChanged -= PhysicalNetworkChanged;
        Process? process = null;

        if (_process is { HasExited: false })
        {
            process = _process;
        }
        else if (_runtimeState.Pid is { } pid)
        {
            try
            {
                process = Process.GetProcessById(pid);
                if (!ProcessMatchesRuntime(process, _runtimeState))
                {
                    process.Dispose();
                    process = null;
                }
            }
            catch (ArgumentException)
            {
                process = null;
            }
        }

        if (process is not null && !process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(5000)) throw new InvalidOperationException("Core shutdown timed out; recovery state was retained.");
        }

        CleanupExitedProcess();

        if (clearProxy) RestoreManagedProxy();
    }

    private void CleanupExitedProcess()
    {
        RestoreTunNetwork();
        RestoreManagedProxy();
        if (_process is not null)
        {
            _process.Dispose();
            _process = null;
        }

        if (File.Exists(_pidPath))
        {
            File.Delete(_pidPath);
        }

        var runtimeConfigPath = _runtimeConfigPath ?? _runtimeState.RuntimeConfigPath;
        if (runtimeConfigPath is not null && File.Exists(runtimeConfigPath))
        {
            File.Delete(runtimeConfigPath);
        }

        _runtimeConfigPath = null;
        _runningRuntime = null;
        _runtimeState = new TrayRuntimeState();
        if (File.Exists(_runtimeStatePath))
        {
            File.Delete(_runtimeStatePath);
        }
    }

    private void CloseLog()
    {
        lock (_logLock)
        {
            _recentLog.Clear();
        }
    }

    private void AppendLogLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        lock (_logLock)
        {
            _recentLog.Enqueue(line.Length > 2048 ? line[..2048] : line);
            while (_recentLog.Count > 1000) _recentLog.Dequeue();
        }
    }

    private void ApplyManagedProxy(int port)
    {
        if (_settings.ProxyRecovery is null) _settings.ProxyRecovery = WindowsProxy.Capture();
        _settings.ProxyOwnedPort = port;
        SaveSettings();
        WindowsProxy.SetSocks(port);
    }

    private void RestoreManagedProxy()
    {
        if (_settings.ProxyRecovery is not { } snapshot) return;
        var current = WindowsProxy.Capture();
        if (current.Enable == 1 && current.Server == WindowsProxy.ServerValue(_settings.ProxyOwnedPort))
            WindowsProxy.Restore(snapshot);
        _settings.ProxyRecovery = null;
        _settings.ProxyOwnedPort = 0;
        SaveSettings();
    }

    private void RestoreTunNetwork()
    {
        if (_settings.TunRecovery is not { } state) return;
        WintunNetwork.RestoreAsync(state).GetAwaiter().GetResult();
        _settings.TunRecovery = null;
        SaveSettings();
    }

    private void PhysicalNetworkChanged(object? sender, EventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_starting || _settings.TunRecovery is not { } state || WintunNetwork.PhysicalInterfaceIsAvailable(state)) return;
            try { await StopAsync(); LastMessage = "Physical interface changed; Wintun stopped. Restart to select a new interface."; }
            catch (Exception error) { LastMessage = "Network recovery required: " + error.Message; }
        }));
    }

    private static async Task WaitForSocksAsync(Process process, int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            if (process.HasExited) throw new InvalidOperationException("TcpRoute2 exited during startup.");
            try
            {
                using var socket = new TcpClient();
                await socket.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                await SocksListenerProbe.VerifyAsync(socket.GetStream(), timeout.Token);
                return;
            }
            catch (SocketException) { await Task.Delay(100, timeout.Token); }
        }
        throw new TimeoutException("TcpRoute2 SOCKS listener was not ready.");
    }

    private void SaveSettings(bool userConfigurationChanged = false)
    {
        var saveUser = !_preserveUserSettings && (userConfigurationChanged || !File.Exists(_settingsPath));
        _settings.Save(_sessionSettingsPath);
        if (saveUser) _settings.SaveUserConfiguration(_settingsPath);
    }

    private void EnsureDefaultConfig()
    {
        if (Directory.EnumerateFiles(ConfigDirectory, "*.toml").Any())
        {
            return;
        }

        File.WriteAllText(Path.Combine(ConfigDirectory, "config.toml"), """
addr="127.0.0.1:7070"

[[UpStreams]]
Name="direct"
ProxyUrl="direct://0.0.0.0:0000"
DnsResolve=true
Credit=0
Sleep=0
CorrectDelay=0
""", Encoding.UTF8);
    }

    private static async Task ConsumeSocksAddressAsync(Stream stream, byte addressType, CancellationToken cancellationToken)
    {
        var length = addressType switch
        {
            0x01 => 4,
            0x03 => await ReadDomainLengthAsync(stream, cancellationToken),
            0x04 => 16,
            _ => throw new InvalidOperationException("TcpRoute2 returned an invalid SOCKS5 address type.")
        };

        var buffer = new byte[length + 2];
        await stream.ReadExactlyAsync(buffer, cancellationToken);
    }

    private static async Task<int> ReadDomainLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        await stream.ReadExactlyAsync(buffer, cancellationToken);
        return buffer[0];
    }

    private static bool ProcessMatchesRuntime(Process process, TrayRuntimeState state)
    {
        if (string.IsNullOrWhiteSpace(state.ExecutablePath))
        {
            return false;
        }

        var expectedName = Path.GetFileNameWithoutExtension(state.ExecutablePath);
        if (!string.Equals(process.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var actualPath = process.MainModule?.FileName;
            return actualPath is null || string.Equals(actualPath, state.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }
}


internal sealed class TrayRuntimeState
{
    public int? Pid { get; set; }

    public string? ExecutablePath { get; set; }

    public string? RuntimeConfigPath { get; set; }

    public int? Port { get; set; }

    public static TrayRuntimeState Load(string path)
    {
        if (!File.Exists(path))
        {
            return new TrayRuntimeState();
        }

        try
        {
            return JsonSerializer.Deserialize<TrayRuntimeState>(File.ReadAllText(path)) ?? new TrayRuntimeState();
        }
        catch (JsonException)
        {
            return new TrayRuntimeState();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}
