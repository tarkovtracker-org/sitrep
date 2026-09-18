using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Sitrep.Core;

namespace Sitrep.Desktop;

/// <summary>
/// Click-through gameplay HUD. Locked (default) it is non-activating and passes all input to the game;
/// unlocked it can be dragged, and a double-click locks it again and persists the new position.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly AppConfig _config;

    public bool IsInteractive { get; private set; }

    /// <summary>Raised after a lock or unlock, including the double-click lock from the overlay itself.</summary>
    public event Action? InteractiveChanged;

    public OverlayWindow(AppConfig config)
    {
        _config = config;
        InitializeComponent();
        ApplyPosition(config);
    }

    public void ApplyPosition(AppConfig config)
    {
        // Null means "never positioned". Any finite value is honoured, including negative coordinates
        // from monitors left of / above the primary.
        if (config.OverlayLeft.HasValue && config.OverlayTop.HasValue)
        {
            Left = config.OverlayLeft.Value;
            Top = config.OverlayTop.Value;
        }
        else
        {
            Left = Math.Max(0, SystemParameters.PrimaryScreenWidth - Width - 24);
            Top = 64;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Win32.MakeClickThrough(new WindowInteropHelper(this).Handle);
    }

    /// <summary>Unlocks for dragging; the overlay stays visible even when the game is not in the foreground.</summary>
    public void Unlock()
    {
        if (IsInteractive)
        {
            return;
        }
        IsInteractive = true;
        // The overlay may never have been shown (no game in the foreground yet). Create the HWND first so
        // the style change has a valid handle; this also runs OnSourceInitialized, which applies the
        // click-through baseline that RemoveClickThrough then relaxes.
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        Win32.RemoveClickThrough(helper.Handle);
        IsHitTestVisible = true;
        DragBanner.Visibility = Visibility.Visible;
        Cursor = Cursors.SizeAll;
        if (!IsVisible)
        {
            Show();
        }
        InteractiveChanged?.Invoke();
    }

    /// <summary>
    /// Restores click-through and records the current position in the shared config (in memory only;
    /// the control window owns persistence and saves on <see cref="InteractiveChanged"/>).
    /// </summary>
    public void Lock()
    {
        if (!IsInteractive)
        {
            return;
        }
        IsInteractive = false;
        Win32.MakeClickThrough(new WindowInteropHelper(this).Handle);
        IsHitTestVisible = false;
        DragBanner.Visibility = Visibility.Collapsed;
        Cursor = Cursors.Arrow;
        _config.OverlayLeft = Left;
        _config.OverlayTop = Top;
        InteractiveChanged?.Invoke();
    }

    /// <summary>Returns to the default top-right position (config updated in memory).</summary>
    public void ResetPosition()
    {
        _config.OverlayLeft = null;
        _config.OverlayTop = null;
        ApplyPosition(_config);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractive && e.ClickCount == 1 && e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractive && e.ChangedButton == MouseButton.Left)
        {
            Lock();
        }
    }

    public void ShowSolution(AssistantState s, StatusLevel level)
    {
        StatusText.Text = s.Status;
        AccentBar.Background = StatusLevelMapper.BrushFor(level);
        ProfileText.Text = s.Table is not null ? "L81 · uncorrected table" : "L81 · TABLE UNVERIFIED";

        ElevationValue.Text = s.ElevationMil.HasValue ? $"{s.ElevationMil.Value:0.0}" : "—";
        ElevationValue.Foreground = level == StatusLevel.Success ? StatusLevelMapper.BrushFor(level) : System.Windows.Media.Brushes.White;
        RangeValue.Text = s.RangeMeters.HasValue ? $"{s.RangeMeters.Value:0.0} m" : "—";
        BearingValue.Text = s.BearingDegrees.HasValue ? GeoMath.FormatBearing(s.BearingDegrees.Value) : "—";
        OriginText.Text = "ORG  " + FormatCoordinate(s.ConfirmedOrigin);
        TargetText.Text = "TGT  " + FormatCoordinate(s.ActiveTarget);

        if (!IsVisible)
        {
            Show();
        }
    }

    private static string FormatCoordinate(MapCoordinate? c) =>
        c.HasValue ? $"x{c.Value.X:0.00} y{c.Value.Y:0.00}" : "—";
}
