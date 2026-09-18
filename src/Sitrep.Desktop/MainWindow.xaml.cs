using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Sitrep.Core;

namespace Sitrep.Desktop;

/// <summary>Single control window: status, hotkey guide, overlay lock, auto-saved settings, tray minimize.</summary>
public partial class MainWindow : Window
{
    private static readonly TimeSpan GameScanInterval = TimeSpan.FromSeconds(2);

    private readonly AssistantService _service;
    private readonly InputMonitor _input;
    private readonly AppConfig _config;
    private readonly OverlayWindow _overlay;
    private readonly string _startupError;
    private readonly ForegroundSession _foreground;
    private readonly DispatcherTimer _gameScan;
    private readonly DispatcherTimer _savedFlash;

    private Win32.NOTIFYICONDATA _trayData;
    private System.Drawing.Icon? _trayIcon;
    private TrayRegistration? _tray;
    private HwndSource? _source;
    private uint _taskbarCreated;
    private ContextMenu? _trayMenu;
    private bool _closing;
    private IntPtr _lastForeground;
    private string _lastForeignProcess = string.Empty;
    private bool _gameRunning;
    private bool _loadingSettings;

    public MainWindow(AssistantService service, InputMonitor input, AppConfig config, OverlayWindow overlay, string startupError)
    {
        _service = service;
        _input = input;
        _config = config;
        _overlay = overlay;
        _startupError = startupError;
        _foreground = new ForegroundSession(service.State);

        InitializeComponent();
        LoadSettings();

        _service.Changed += OnServiceChanged;
        _input.CapturePressed += GuardedCapture;
        _input.Invalidated += OnInputInvalidated;
        _input.ClearPressed += OnClear;
        _input.Poll += ObserveForeground;
        _overlay.InteractiveChanged += OnOverlayInteractiveChanged;

        _gameScan = new DispatcherTimer(DispatcherPriority.Background) { Interval = GameScanInterval };
        _gameScan.Tick += (_, _) => ScanForGame();
        _gameScan.Start();
        _savedFlash = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _savedFlash.Tick += (_, _) => { SavedText.Visibility = Visibility.Hidden; _savedFlash.Stop(); };

        ScanForGame();
        Refresh();
    }

    // ---- lifecycle / tray -------------------------------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
        InitTray(hwnd);
    }

    private void InitTray(IntPtr hwnd)
    {
        _trayIcon = LoadTrayIcon();
        _trayData = new Win32.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP,
            uCallbackMessage = (uint)Win32.WM_TRAYICON,
            hIcon = _trayIcon?.Handle ?? IntPtr.Zero,
            szTip = "SITREP — WARDOGS mortar companion",
        };
        _tray = new TrayRegistration(
            () => !_closing && _taskbarCreated != 0 && _trayData.hIcon != IntPtr.Zero
                && Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref _trayData),
            () => Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref _trayData));
        _tray.EnsureRegistered();
    }

    // The icon object must outlive the tray entry: Shell_NotifyIcon keeps only the raw handle.
    private System.Drawing.Icon? LoadTrayIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (info is null)
            {
                return null;
            }
            using var stream = info.Stream;
            int size = (int)Math.Round(16 * VisualTreeHelper.GetDpi(this).DpiScaleX);
            return new System.Drawing.Icon(stream, size, size);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_closing) { return IntPtr.Zero; }
        if (_taskbarCreated != 0 && (uint)msg == _taskbarCreated)
        {
            if (_trayMenu is not null) { _trayMenu.IsOpen = false; }
            if (_tray?.ShellRestarted() != true && !IsVisible)
            {
                // Explorer recovery can fail. Retain the minimized state and restore taskbar access.
                bool activate = ShowActivated;
                ShowActivated = false;
                try { Show(); }
                finally { ShowActivated = activate; }
            }
            return IntPtr.Zero;
        }
        if (msg != Win32.WM_TRAYICON || _tray?.IsRegistered != true || wParam.ToInt64() != _trayData.uID)
        {
            return IntPtr.Zero;
        }
        switch ((int)(lParam.ToInt64() & 0xFFFF))
        {
            case Win32.WM_LBUTTONUP:
            case Win32.WM_LBUTTONDBLCLK:
                RestoreFromTray();
                handled = true;
                break;
            case Win32.WM_RBUTTONUP:
                ShowTrayMenu(hwnd);
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private void RestoreFromTray()
    {
        if (_closing) { return; }
        if (_trayMenu is not null) { _trayMenu.IsOpen = false; }
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ShowTrayMenu(IntPtr hwnd)
    {
        if (_closing) { return; }
        if (_trayMenu is not null) { _trayMenu.IsOpen = false; }
        var menu = new ContextMenu();
        _trayMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(_trayMenu, menu)) { _trayMenu = null; } };
        var open = new MenuItem { Header = "Open SITREP" };
        open.Click += (_, _) => RestoreFromTray();
        var lockItem = new MenuItem { Header = _overlay.IsInteractive ? "Lock overlay" : "Unlock overlay" };
        lockItem.Click += (_, _) => ToggleOverlayLock();
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => { if (!_closing) { Close(); } };
        menu.Items.Add(open);
        menu.Items.Add(lockItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        // A menu owned by a hidden window only dismisses on outside clicks once our thread is foreground.
        Win32.SetForegroundWindow(hwnd);
        menu.IsOpen = true;
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (!_closing && WindowState == WindowState.Minimized && _tray?.EnsureRegistered() == true)
        {
            Hide();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        _gameScan.Stop();
        _savedFlash.Stop();
        _input.CapturePressed -= GuardedCapture;
        _input.ClearPressed -= OnClear;
        _input.Invalidated -= OnInputInvalidated;
        _input.Poll -= ObserveForeground;
        _service.Changed -= OnServiceChanged;
        _overlay.InteractiveChanged -= OnOverlayInteractiveChanged;
        if (_trayMenu is not null) { _trayMenu.IsOpen = false; }
        _source?.RemoveHook(WndProc);
        _tray?.Dispose();
        _trayIcon?.Dispose();
        Win32.ClearProcessNameCache();
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    // ---- capture orchestration ---------------------------------------------------------------------

    private void OnServiceChanged() => Dispatcher.BeginInvoke(Refresh);

    private System.Drawing.Bitmap? CaptureFor(CaptureRequest req)
    {
        var hwnd = new IntPtr(req.ForegroundHwnd);
        if (Win32.GetForegroundWindow() != hwnd || !_service.IsForegroundAllowed(hwnd))
        {
            return null;
        }
        var roi = new CaptureRegion(req.RoiX, req.RoiY, req.RoiWidth, req.RoiHeight);
        var bounds = Win32.GetCaptureBounds(hwnd, req.CursorX, req.CursorY);
        return ScreenCapture.CaptureRegion(roi, bounds, GetVisibleExclusions());
    }

    internal static CaptureRegion[] GetVisibleExclusions() =>
        Application.Current?.Windows.Cast<Window>()
            .Where(w => w.IsVisible)
            .Select(w => Win32.GetScreenRegion(new WindowInteropHelper(w).Handle))
            .ToArray() ?? [];

    private void GuardedCapture(Core.InputGesture gesture)
    {
        if (_closing || !_input.IsCurrent(gesture)) { return; }
        ObserveForeground();
        var sample = gesture.Sample;
        var hwnd = new IntPtr(sample.Foreground);
        if (!_foreground.IsForeground || Win32.GetForegroundWindow() != hwnd) { return; }
        var role = gesture.Action == InputAction.Origin ? CaptureRole.Origin : CaptureRole.Target;
        bool anchorValid = sample.HasCursor && Win32.GetCursorPos(out var point)
            && point.X == sample.X && point.Y == sample.Y
            && System.Diagnostics.Stopwatch.GetElapsedTime(gesture.Timestamp) <= TimeSpan.FromMilliseconds(500);
        _service.Request(role, hwnd, sample.X, sample.Y, CaptureFor,
            () => _input.IsCurrent(gesture), anchorValid, gesture.Timestamp, gesture.EventTime);
    }

    private void OnInputInvalidated()
    {
        if (_service.State.Pending is not null || _service.State.ConfirmedOrigin.HasValue)
        {
            _service.State.OnForegroundLost();
        }
        _service.DiscardPending();
        Refresh();
    }

    private void ObserveForeground()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd != _lastForeground)
        {
            _lastForeground = hwnd;
            if (hwnd != IntPtr.Zero && !Win32.IsOwnWindow(hwnd))
            {
                _lastForeignProcess = Win32.GetProcessName(hwnd);
            }
        }
        if (_foreground.Observe(hwnd.ToInt64(), _service.IsForegroundAllowed(hwnd), h => Win32.IsWindow(new IntPtr(h))))
        {
            _service.DiscardPending();
            Refresh();
        }
    }

    private void ScanForGame()
    {
        if (_closing) { return; }
        bool running = _service.MatchesGameProcess(Win32.GetProcessName(Win32.GetForegroundWindow()));
        if (!running && !_config.DesktopTestMode && !string.IsNullOrWhiteSpace(_config.GameProcessName))
        {
            // Status-only enumeration at 2 s, never on the input sampling thread. Dispose even exited processes.
            foreach (var process in System.Diagnostics.Process.GetProcesses())
            {
                using (process)
                {
                    if (running) { continue; }
                    try { running = _service.MatchesGameProcess(process.ProcessName); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // A process may exit or deny metadata access during enumeration.
                    }
                }
            }
        }
        if (running != _gameRunning)
        {
            _gameRunning = running;
            Refresh();
        }
    }

    private void OnClear(Core.InputGesture gesture)
    {
        if (_closing || !_input.IsCurrent(gesture)) { return; }
        ObserveForeground();
        // Never retarget a sampled F9 to a later foreground, nor authorize an owned overlay as the control window.
        // Focus resets still discard queued clears; recheck the epoch after foreground observation as well.
        if (!ClearInputPolicy.IsAllowed(gesture, _input.IsCurrent(gesture), Win32.GetForegroundWindow().ToInt64(),
            _foreground.AttachedWindow, _foreground.IsForeground, new WindowInteropHelper(this).Handle.ToInt64())) { return; }
        _service.State.Clear();
        _service.DiscardPending();
        Refresh();
    }

    // ---- overlay ------------------------------------------------------------------------------------

    private void ToggleOverlayLock()
    {
        if (_closing) { return; }
        if (_overlay.IsInteractive)
        {
            _overlay.Lock();
        }
        else
        {
            _overlay.Unlock();
        }
    }

    private void OnOverlayInteractiveChanged()
    {
        OverlayLockButton.Content = _overlay.IsInteractive ? "Lock overlay" : "Unlock overlay";
        if (!_overlay.IsInteractive)
        {
            PersistConfig();
        }
        UpdateOverlayPosText();
        Refresh();
    }

    private void OverlayLockButton_Click(object sender, RoutedEventArgs e) => ToggleOverlayLock();

    private void ResetOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        _overlay.ResetPosition();
        PersistConfig();
        UpdateOverlayPosText();
    }

    private void UpdateOverlayPosText() =>
        OverlayPosText.Text = _config.OverlayLeft.HasValue && _config.OverlayTop.HasValue
            ? $"Custom ({_config.OverlayLeft.Value:0}, {_config.OverlayTop.Value:0})"
            : "Default (top-right)";

    // ---- settings (auto-saved) ----------------------------------------------------------------------

    private void LoadSettings()
    {
        _loadingSettings = true;
        GameProcessBox.Text = _config.GameProcessName;
        DesktopTestBox.IsChecked = _config.DesktopTestMode;
        AlwaysOnTopBox.IsChecked = _config.AlwaysOnTop;
        DebugModeBox.IsChecked = _config.DebugMode;
        Topmost = _config.AlwaysOnTop;
        GameProcessBox.IsEnabled = !_config.DesktopTestMode;
        UpdateOverlayPosText();
        _loadingSettings = false;
    }

    private void Setting_Changed(object sender, RoutedEventArgs e) => ApplySettings();

    private void GameProcessBox_LostFocus(object sender, RoutedEventArgs e) => ApplySettings();

    private void GameProcessBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplySettings();
            e.Handled = true;
        }
    }

    private void ApplySettings()
    {
        if (_loadingSettings)
        {
            return;
        }
        string process = GameProcessBox.Text.Trim();
        bool testMode = DesktopTestBox.IsChecked == true;
        bool onTop = AlwaysOnTopBox.IsChecked == true;
        bool debug = DebugModeBox.IsChecked == true;
        bool targetingChanged = process != _config.GameProcessName || testMode != _config.DesktopTestMode;
        if (!targetingChanged && onTop == _config.AlwaysOnTop && debug == _config.DebugMode)
        {
            return;
        }

        _config.GameProcessName = process;
        _config.DesktopTestMode = testMode;
        _config.AlwaysOnTop = onTop;
        _config.DebugMode = debug;
        Topmost = onTop;
        GameProcessBox.IsEnabled = !testMode;

        if (targetingChanged)
        {
            // A different game identity invalidates the attached window and any confirmed origin.
            _service.DiscardPending();
            _foreground.Reset();
            _input.ResetEdges();
            ObserveForeground();
            ScanForGame();
        }
        PersistConfig();
        Refresh();
    }

    private void PersistConfig()
    {
        try
        {
            _config.Save();
            ShowSettingsError(null);
            SavedText.Visibility = Visibility.Visible;
            _savedFlash.Stop();
            _savedFlash.Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowSettingsError($"Could not save {AppConfig.ConfigPath}: {ex.Message}");
        }
    }

    private void ShowSettingsError(string? message)
    {
        SettingsErrorText.Text = message ?? string.Empty;
        SettingsErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenCapturesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DebugCaptureStore.DefaultDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{DebugCaptureStore.DefaultDirectory}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowSettingsError($"Could not open {DebugCaptureStore.DefaultDirectory}: {ex.Message}");
        }
    }

    // ---- presentation -------------------------------------------------------------------------------

    private static Brush ThemeBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.LightGray;

    private void Refresh()
    {
        if (_closing) { return; }
        var s = _service.State;
        StatusLevel level = StatusLevelMapper.ForStatus(s.Status);
        bool inGame = _foreground.IsForeground;

        // Header pill: where the game is relative to us.
        (string pill, StatusLevel pillLevel) = _config.DesktopTestMode ? ("TEST MODE", StatusLevel.Warning)
            : inGame ? ("IN GAME", StatusLevel.Success)
            : _gameRunning ? ("GAME RUNNING", StatusLevel.Info)
            : ("GAME NOT FOUND", StatusLevel.Neutral);
        GamePillText.Text = pill;
        GamePillText.Foreground = ThemeBrush(pillLevel == StatusLevel.Neutral ? "TextDimBrush" : "TextBrush");
        GameDot.Fill = StatusLevelMapper.BrushFor(pillLevel);
        GamePill.BorderBrush = StatusLevelMapper.BrushFor(pillLevel);

        bool hasStartupError = !string.IsNullOrWhiteSpace(_startupError);
        StartupBanner.Visibility = Visibility.Visible;
        StartupBannerText.Text = hasStartupError ? _startupError
            : "Guarded capture is active. Publisher permission is unresolved: obtain approval before in-game use.";

        bool tableOk = s.Table is not null;
        ProfileText.Text = tableOk ? "L81 · table-backed" : "L81 · TABLE UNVERIFIED";
        ProfileText.Foreground = tableOk ? ThemeBrush("TextDimBrush") : StatusLevelMapper.BrushFor(StatusLevel.Warning);

        StatusText.Text = s.Status;
        StatusDot.Fill = StatusLevelMapper.BrushFor(level);
        SolutionText.Text = s.RangeMeters.HasValue && s.BearingDegrees.HasValue
            ? (s.ElevationMil.HasValue ? $"{s.ElevationMil.Value:0.0} MIL  ·  " : string.Empty)
              + $"{s.RangeMeters.Value:0.0} m  ·  {GeoMath.FormatBearing(s.BearingDegrees.Value)}"
            : s.ConfirmedOrigin.HasValue ? $"origin x{s.ConfirmedOrigin.Value.X:0.00} y{s.ConfirmedOrigin.Value.Y:0.00}" : "—";
        SolutionText.Foreground = level == StatusLevel.Success ? StatusLevelMapper.BrushFor(level) : ThemeBrush("TextDimBrush");

        ForegroundHintText.Text = !_config.DesktopTestMode && !_gameRunning && _lastForeignProcess.Length > 0
            ? $"Matched against the foreground window's process. Last seen app: {_lastForeignProcess}"
            : "Matched against the foreground window's process, e.g. wardogs.";

        if (!inGame && !_overlay.IsInteractive)
        {
            _overlay.Hide();
            return;
        }
        _overlay.ShowSolution(s, level);
    }
}
