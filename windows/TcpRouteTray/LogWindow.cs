using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace TcpRouteTray;

internal sealed class LogWindow : Window
{
    private readonly TcpRouteController _controller;
    private readonly TextBox _output = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        FontFamily = new FontFamily("Consolas"), FontSize = 13, BorderThickness = new Thickness(0), Padding = new Thickness(12)
    };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Button _export = new() { Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 5, 12, 5) };
    private readonly Button _clear = new() { Padding = new Thickness(12, 5, 12, 5) };
    private bool _allowClose;

    public LogWindow(TcpRouteController controller)
    {
        _controller = controller;
        Width = 900; Height = 540; MinWidth = 480; MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/TcpRoute2.ico"));
        var layout = new DockPanel();
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        _clear.Click += (_, _) => ClearOutput();
        _export.Click += (_, _) => _controller.OpenLogs();
        toolbar.Children.Add(_clear); toolbar.Children.Add(_export);
        DockPanel.SetDock(toolbar, Dock.Top); layout.Children.Add(toolbar); layout.Children.Add(_output);
        Content = layout;
        _timer.Tick += (_, _) => { if (IsVisible) Refresh(); };
        _timer.Start();
        Closing += OnClosing;
        Closed += (_, _) => _timer.Stop();
        SourceInitialized += (_, _) => Refresh();
    }

    public void ShowOutput()
    {
        Refresh(); Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void AllowClose() => _allowClose = true;

    private void ClearOutput()
    {
        _controller.ClearRecentOutput();
        _output.Select(0, 0);
        _output.Clear();
        _output.ScrollToHome();
    }

    private void Refresh()
    {
        Title = WindowsLocalization.T(_controller.Language, "TcpRoute2 Activity", "TcpRoute2 活动日志", "TcpRoute2 活動日誌");
        _clear.Content = WindowsLocalization.T(_controller.Language, "Clear", "清空", "清空");
        _export.Content = WindowsLocalization.T(_controller.Language, "Export...", "导出…", "匯出…");
        var dark = WindowsTheme.IsDark(_controller.Appearance);
        Background = _output.Background = dark ? new SolidColorBrush(Color.FromRgb(24, 25, 28)) : Brushes.White;
        Foreground = _output.Foreground = dark ? Brushes.WhiteSmoke : Brushes.Black;
        var text = string.Join(Environment.NewLine, _controller.ReadRecentLogLines(1000));
        if (_output.Text == text || _output.SelectionLength > 0) return;
        var offset = _output.VerticalOffset;
        var atEnd = offset + _output.ViewportHeight >= _output.ExtentHeight - 2;
        _output.Text = text;
        if (atEnd) _output.ScrollToEnd(); else _output.ScrollToVerticalOffset(offset);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true; Hide();
    }
}
