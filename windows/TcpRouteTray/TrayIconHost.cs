using System.Diagnostics;
using System.Drawing;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace TcpRouteTray;

internal sealed class TrayIconHost : IDisposable
{
    private readonly TcpRouteController _controller;
    private readonly LogWindow _window;
    private readonly System.Windows.Application _application;
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _trayIcon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly ConfigurationChangeTracker _changes;
    private bool _busy;
    private bool _geoBusy;
    private bool _subscriptionSelectionDirty;
    private bool _menuOpen;
    private DateTime _nextGeoAttempt;
    private DateTime _nextSubscriptionAttempt;
    private readonly ConfigurationChangeTracker _readErrors = new("");

    public TrayIconHost(TcpRouteController controller, LogWindow window, System.Windows.Application application)
    {
        _controller = controller; _window = window; _application = application;
        _changes = new ConfigurationChangeTracker(controller.AppliedConfigurationSignature());
        _menu.Opening += (_, _) => { RebuildMenu(); _menuOpen = true; };
        _menu.Closed += (_, _) => {
            _menuOpen = false;
            // Let the clicked command start before applying deferred node changes.
            _application.Dispatcher.BeginInvoke(new Action(async () => {
                if (_subscriptionSelectionDirty && !_busy && !_menuOpen)
                    await RunAsync(() => Task.CompletedTask, rebuild: false);
            }));
        };
        _trayIcon = LoadIcon();
        _icon = new Forms.NotifyIcon { ContextMenuStrip = _menu, Icon = _trayIcon, Text = "TcpRoute2", Visible = true };
        _icon.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) _window.ShowOutput(); };
        _timer.Tick += async (_, _) => await PollConfigurationAsync();
        _timer.Start(); RebuildMenu();
    }

    public Task StartAsync() => RunAsync(() => _controller.StartAsync());

    private void RebuildMenu()
    {
        _menu.Items.Clear();
        var running = _controller.IsRunning;
        var mode = _controller.UseWintun ? "TUN / VPN (Wintun)" : T("Proxy", "代理", "代理");
        _icon.Text = "TcpRoute2 - " + (running ? mode : T("Stopped", "已停止", "已停止"));
        _menu.Items.Add(new Forms.ToolStripMenuItem(running ? $"{mode} · 127.0.0.1:{_controller.RunningPort}" : T("Stopped", "已停止", "已停止")) { Enabled = false });
        _menu.Items.Add(new Forms.ToolStripMenuItem(_controller.LastMessage) { Enabled = false });
        var copyProxy = Submenu(T("Copy proxy", "复制代理", "複製代理"));
        foreach (var format in new[] {
            (Id: "url", Label: T("Proxy URL", "代理地址", "代理位址")),
            (Id: "cmd", Label: T("CMD environment variables", "CMD 环境变量", "CMD 環境變數")),
            (Id: "ps", Label: T("PowerShell environment variables", "PowerShell 环境变量", "PowerShell 環境變數"))
        })
            Add(copyProxy.DropDownItems, format.Label, () => {
                var port = _controller.LocalProxyPort;
                var endpoint = $"http://127.0.0.1:{port}";
                var text = format.Id == "url" ? endpoint : string.Join("\r\n", new[] {
                    (Name: "HTTP_PROXY", Value: endpoint),
                    (Name: "HTTPS_PROXY", Value: endpoint),
                    (Name: "ALL_PROXY", Value: $"socks5h://127.0.0.1:{port}")
                }.Select(variable => format.Id == "cmd"
                    ? $"set \"{variable.Name}={variable.Value}\""
                    : $"$env:{variable.Name} = '{variable.Value}'"));
                Forms.Clipboard.SetText(text);
                _controller.RecordEvent(T($"Copied {format.Label}", $"已复制{format.Label}", $"已複製{format.Label}"));
                return Task.CompletedTask;
            });
        Add(_menu.Items, T("Activity log", "活动日志", "活動日誌"), () => { _window.ShowOutput(); return Task.CompletedTask; });
        _menu.Items.Add(new Forms.ToolStripSeparator());
        Add(_menu.Items, running ? T("Stop", "停止", "停止") : T("Start", "启动", "啟動"), async () => {
            if (_controller.IsRunning) await _controller.StopAsync(); else await _controller.StartAsync();
        });
        Add(_menu.Items, T("Restart", "重新启动", "重新啟動"), () => _controller.RestartAsync(), running);
        Add(_menu.Items, T("Connectivity check", "连通性检查", "連通性檢查"), async () => {
            var elapsed = await _controller.MeasureSocksConnectAsync(); _controller.RecordEvent($"SOCKS5 connect: {elapsed} ms");
        }, running);
        var network = Submenu(T("Network mode", "网络模式", "網路模式"));
        Choice(network, T("Proxy", "代理", "代理"), !_controller.UseWintun, () => _controller.SetUseWintun(false), !running);
        Choice(network, "TUN / VPN (Wintun)", _controller.UseWintun, () => _controller.SetUseWintun(true), !running);
        var interfaces = Submenu(T("Outbound interface", "出口网络接口", "出口網路介面"));
        Choice(interfaces, T("Automatic", "自动", "自動"), _controller.OutboundInterfaceId == "", () => _controller.SetOutboundInterface(""), !running);
        foreach (var adapter in WintunNetwork.AvailableInterfaces())
            Choice(interfaces, adapter.Name, adapter.Id == _controller.OutboundInterfaceId, () => _controller.SetOutboundInterface(adapter.Id), !running).ToolTipText = adapter.Id;
        if (_controller.OutboundInterfaceId != "" && !interfaces.DropDownItems.OfType<Forms.ToolStripMenuItem>().Any(item => item.Checked))
            interfaces.DropDownItems.Add(new Forms.ToolStripMenuItem(T("Selected interface unavailable", "已选接口不可用", "已選介面不可用")) { Enabled = false });

        var preferences = Submenu(T("Runtime settings", "运行设置", "執行設定"));
        Choice(preferences, T("System proxy", "系统代理", "系統代理"), _controller.SystemProxyEnabled, () => _controller.SetSystemProxyEnabled(!_controller.SystemProxyEnabled), !_controller.UseWintun);
        Choice(preferences, T("Allow LAN", "允许 LAN", "允許 LAN"), _controller.AllowLan, () => _controller.SetAllowLan(!_controller.AllowLan), !running && !_controller.UseWintun);
        Choice(preferences, "STUN", _controller.AllowStun, () => _controller.SetAllowStun(!_controller.AllowStun), !running && !_controller.IsStarting);
        Choice(preferences, "QUIC", _controller.AllowQuic, () => _controller.SetAllowQuic(!_controller.AllowQuic), !running && !_controller.IsStarting);
        Choice(preferences, "IPv6", _controller.EnableIpv6, () => _controller.SetEnableIpv6(!_controller.EnableIpv6), !running && !_controller.IsStarting);
        Choice(preferences, T("Automatic client settings reload", "自动重载客户端设置", "自動重新載入客戶端設定"), _controller.AutoReloadClientSettings, () => _controller.SetAutoReloadClientSettings(!_controller.AutoReloadClientSettings), true);
        var language = new Forms.ToolStripMenuItem(T("Language", "语言", "語言"));
        foreach (var option in new[] { (Id: "system", Label: T("System", "跟随系统", "跟隨系統")), (Id: "en", Label: "English"), (Id: "zh-Hans", Label: "简体中文"), (Id: "zh-Hant", Label: "繁體中文") })
            Choice(language, option.Label, _controller.Language == option.Id, () => _controller.SetLanguage(option.Id), true);
        preferences.DropDownItems.Add(language);
        var appearance = new Forms.ToolStripMenuItem(T("Appearance", "主题", "主題"));
        foreach (var option in new[] { (Id: "system", Label: T("System", "跟随系统", "跟隨系統")), (Id: "light", Label: T("Light", "浅色", "淺色")), (Id: "dark", Label: T("Dark", "深色", "深色")) })
            Choice(appearance, option.Label, _controller.Appearance == option.Id, () => _controller.SetAppearance(option.Id), true);
        preferences.DropDownItems.Add(appearance);
        Add(preferences.DropDownItems, T("Edit client configuration...", "编辑客户端配置…", "編輯客戶端配置…"), () => EditFile(_controller.SettingsPath));

        var configurations = Submenu(T("Configuration", "配置", "配置"));
        foreach (var config in _controller.LoadConfigs())
        {
            var profile = new Forms.ToolStripMenuItem(config.Name) { Checked = config.Id == _controller.SelectedConfigId };
            Add(profile.DropDownItems, T("Use this configuration", "使用此配置", "使用此配置"), async () => {
                _controller.SelectConfig(config.Id); await _controller.ReloadConfigurationAsync();
            });
            Add(profile.DropDownItems, T("Edit file...", "编辑文件…", "編輯檔案…"), () => EditFile(config.Path));
            configurations.DropDownItems.Add(profile);
        }
        Add(configurations.DropDownItems, T("Open configuration folder", "打开配置目录", "開啟配置目錄"), () => { _controller.OpenConfigFolder(); return Task.CompletedTask; });
        Add(configurations.DropDownItems, T("Reload now", "立即重载", "立即重新載入"), () => _controller.ReloadConfigurationAsync());
        var subscriptions = Submenu(T("Subscriptions", "订阅", "訂閱"));
        Add(subscriptions.DropDownItems, T("Edit subscriptions...", "编辑订阅…", "編輯訂閱…"), () => EditFile(_controller.SettingsPath));
        foreach (var source in _controller.Subscriptions)
        {
            var menu = new Forms.ToolStripMenuItem((string.IsNullOrEmpty(source.Name) ? source.Id : source.Name).Replace("&", "&&")) { DropDown = new SubscriptionNodeMenu() };
            subscriptions.DropDownItems.Add(menu);
            var exitHint = T("Press Esc or click outside to exit editing", "按 Esc 或点击菜单外部退出编辑", "按 Esc 或點擊選單外部退出編輯");
            menu.DropDownItems.Add(new Forms.ToolStripMenuItem(exitHint) { Enabled = false });
            menu.DropDownItems.Add(new Forms.ToolStripSeparator());
            Add(menu.DropDownItems, T("Update now", "立即更新", "立即更新"), () => _controller.RefreshSubscriptionAsync(source.Id));
            try
            {
                var cache = _controller.SubscriptionCache(source);
                var selectedNodes = _controller.SubscriptionSelectedNodes(source.Id);
                var status = new Forms.ToolStripMenuItem { Enabled = false };
                void UpdateStatus() => status.Text = $"{selectedNodes.Count} / {cache.Nodes.Count} · {cache.UpdatedAt.ToLocalTime():g}";
                UpdateStatus();
                menu.DropDownItems.Add(status);
                void AddNode(Forms.ToolStripItemCollection items, string nodeId, string label, bool available)
                {
                    Forms.ToolStripMenuItem? item = null;
                    item = Add(items, label.Replace("&", "&&"), async () => {
                        var selected = !item!.Checked;
                        await _controller.SelectSubscriptionNodeAsync(source.Id, nodeId, selected, reload: false);
                        item.Checked = selected;
                        item.Enabled = available || selected;
                        if (selected) selectedNodes.Add(nodeId); else selectedNodes.Remove(nodeId);
                        UpdateStatus();
                        _subscriptionSelectionDirty = true;
                    }, available || selectedNodes.Contains(nodeId), rebuild: false, keepOpen: true);
                    item.Checked = selectedNodes.Contains(nodeId);
                    item.ToolTipText = source.ManagedPrefix + nodeId;
                }
                foreach (var page in cache.Nodes.Chunk(50).Select((nodes, index) => (nodes, index)))
                {
                    var items = menu.DropDownItems;
                    if (cache.Nodes.Count > 50)
                    {
                        var group = new Forms.ToolStripMenuItem($"{page.index * 50 + 1}-{page.index * 50 + page.nodes.Length}") { DropDown = new SubscriptionNodeMenu() };
                        group.DropDownItems.Add(new Forms.ToolStripMenuItem(exitHint) { Enabled = false });
                        group.DropDownItems.Add(new Forms.ToolStripSeparator());
                        items.Add(group); items = group.DropDownItems;
                    }
                    foreach (var node in page.nodes)
                    {
                        var label = (node.Removed ? T("[Removed] ", "[已移除] ", "[已移除] ") : "") + node.Name;
                        AddNode(items, node.Id, label, !node.Removed);
                    }
                }
                foreach (var missingId in selectedNodes.Except(cache.Nodes.Select(node => node.Id), StringComparer.Ordinal))
                {
                    AddNode(menu.DropDownItems, missingId, T("[Not in cache] ", "[缓存缺失] ", "[快取缺失] ") + missingId, false);
                }
            }
            catch { menu.DropDownItems.Add(new Forms.ToolStripMenuItem(T("Cache unavailable; update required", "缓存不可用，请更新", "快取不可用，請更新")) { Enabled = false }); }
        }
        var geo = Submenu(T("Geo data", "Geo 数据", "Geo 資料"));
        var snapshot = _controller.GetGeoDataSnapshot();
        geo.DropDownItems.Add(new Forms.ToolStripMenuItem($"geosite: {snapshot.GeositeBytes:N0} B / geoip: {snapshot.GeoipBytes:N0} B") { Enabled = false });
        geo.DropDownItems.Add(new Forms.ToolStripMenuItem($"{snapshot.Interval} / {snapshot.LastSuccess?.ToLocalTime().ToString("g") ?? "-"}") { Enabled = false });
        Add(geo.DropDownItems, T("Update now", "立即更新", "立即更新"), async () => { await _controller.UpdateGeoDataAsync(); });
        Add(_menu.Items, T("Export activity log...", "导出活动日志…", "匯出活動日誌…"), () => { _controller.OpenLogs(); return Task.CompletedTask; });
        _menu.Items.Add(new Forms.ToolStripSeparator());
        Add(_menu.Items, T("Quit", "退出", "退出"), () => { _window.AllowClose(); _window.Close(); _application.Shutdown(); return Task.CompletedTask; });
    }

    private Forms.ToolStripMenuItem Submenu(string label)
    {
        var item = new Forms.ToolStripMenuItem(label); _menu.Items.Add(item); return item;
    }

    private Forms.ToolStripMenuItem Choice(Forms.ToolStripMenuItem parent, string label, bool selected, Action action, bool enabled)
    {
        var item = Add(parent.DropDownItems, label, () => { action(); return Task.CompletedTask; }, enabled);
        item.Checked = selected; return item;
    }

    private Forms.ToolStripMenuItem Add(Forms.ToolStripItemCollection items, string label, Func<Task> action, bool enabled = true, bool rebuild = true, bool keepOpen = false)
    {
        var item = keepOpen ? new SubscriptionNodeMenuItem(label) : new Forms.ToolStripMenuItem(label);
        item.Enabled = enabled && !_busy && !_controller.IsStarting;
        item.Click += async (_, _) => await RunAsync(action, rebuild); items.Add(item); return item;
    }

    private static Task EditFile(string path)
    {
        var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = true }; start.ArgumentList.Add(path);
        _ = Process.Start(start) ?? throw new InvalidOperationException("Could not open the configuration file.");
        return Task.CompletedTask;
    }

    private string ReadSignature()
    {
        try { return _controller.ReadConfigurationSnapshot(_controller.AutoReloadClientSettings).Signature; }
        catch (Exception error) { _controller.RecordEvent(error.Message); return ""; }
    }

    private async Task RunAsync(Func<Task> action, bool rebuild = true)
    {
        if (_busy) return;
        _busy = true;
        var success = false;
        try {
            await action();
            if (_subscriptionSelectionDirty && !_menuOpen)
            {
                _subscriptionSelectionDirty = false;
                if (_controller.AutoReloadClientSettings) await _controller.ReloadConfigurationAsync();
            }
            success = true;
        }
        catch (Exception error) { ReportError(error); }
        finally { _busy = false; _changes.Accept(success ? _controller.AppliedConfigurationSignature() : ReadSignature()); if (rebuild) RebuildMenu(); }
    }

    private async Task PollConfigurationAsync()
    {
        if (_busy || _controller.IsStarting || _menuOpen) return;
        try
        {
            var next = _controller.ReadConfigurationSnapshot(_controller.AutoReloadClientSettings); _readErrors.Accept("");
            if (_changes.Observe(next.Signature)) await RunAsync(() => _controller.ReloadConfigurationAsync(_controller.AutoReloadClientSettings));
        }
        catch (Exception error)
        {
            if (_readErrors.Observe(error.Message)) ReportError(error);
        }
        if (DateTime.UtcNow >= _nextSubscriptionAttempt)
        {
            _nextSubscriptionAttempt = DateTime.UtcNow.AddMinutes(1);
            try { if (_controller.SubscriptionsDue()) await RunAsync(() => _controller.RefreshDueSubscriptionsAsync()); }
            catch (Exception error) { ReportError(error); }
        }
        if (!_geoBusy && DateTime.UtcNow >= _nextGeoAttempt && GeoDataManager.IsDue(_controller.GetGeoDataSnapshot()))
        {
            _geoBusy = true; _nextGeoAttempt = DateTime.UtcNow.AddMinutes(5);
            try { await _controller.UpdateGeoDataAsync(onlyIfDue: true); }
            catch (Exception error) { ReportError(error); }
            finally { _geoBusy = false; }
        }
    }

    private void ReportError(Exception error)
    {
        _controller.RecordEvent(error.Message); _icon.BalloonTipTitle = "TcpRoute2"; _icon.BalloonTipText = error.Message;
        _icon.BalloonTipIcon = Forms.ToolTipIcon.Error; _icon.ShowBalloonTip(4000);
    }

    private string T(string english, string simplified, string traditional) => WindowsLocalization.T(_controller.Language, english, simplified, traditional);

    private static Icon LoadIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/TcpRoute2.ico"));
        return resource is null ? (Icon)SystemIcons.Application.Clone() : new Icon(resource.Stream);
    }

    public void Dispose()
    {
        _timer.Stop(); _window.AllowClose(); _window.Close();
        _icon.Visible = false; _icon.Dispose(); _trayIcon.Dispose(); _menu.Dispose();
    }
}
