using System.Drawing;
using System.Windows.Threading;
using Sitrep.Core;

namespace Sitrep.Desktop;

public sealed class AssistantService : IDisposable
{
    private sealed record Work(CaptureRequest Request, Bitmap Image, Bitmap RetryImage, bool Debug) : IDisposable
    {
        public void Dispose()
        {
            Image.Dispose();
            RetryImage.Dispose();
        }
    }

    private sealed record Retry(CaptureRequest Request, Bitmap Image, Func<CaptureRequest, Bitmap?> Capture, bool Debug);
    private readonly DispatcherTimer _retryTimer;
    private readonly Func<(int X, int Y)?> _getCursor;
    private Retry? _retry;
    private long _snapshotTime;
    private Func<bool>? _inputStillCurrent;

    private readonly AssistantState _state;
    private readonly Func<Bitmap, RecognitionResult> _recognize;
    private readonly Func<IntPtr> _getForeground;
    private readonly Func<IntPtr, bool> _windowExists;
    private readonly Func<IntPtr, bool>? _allowedForTest;
    private readonly Func<IntPtr, CaptureRegion?> _clientBounds;
    private readonly AppConfig _config;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly LatestCaptureWorker<Work> _worker;
    private bool _disposed;

    public event Action? Changed;

    public AssistantService(AssistantState state, OcrEngine ocr, AppConfig config)
        : this(state, config, image => RecognitionPipeline.Recognize(image, ocr), Win32.GetForegroundWindow, Win32.IsWindow,
            clientBounds: Win32.GetClientRegion)
    {
    }

    // One fakeable boundary for deterministic packaged orchestration tests; production uses Win32 + live OCR above.
    internal AssistantService(AssistantState state, AppConfig config, Func<Bitmap, RecognitionResult> recognize,
        Func<IntPtr> foreground, Func<IntPtr, bool> windowExists, Func<IntPtr, bool>? allowedForTest = null,
        Func<(int X, int Y)?>? cursor = null, Func<IntPtr, CaptureRegion?>? clientBounds = null)
    {
        _state = state;
        _recognize = recognize;
        _getForeground = foreground;
        _windowExists = windowExists;
        _allowedForTest = allowedForTest;
        _clientBounds = clientBounds ?? (_ => null);
        _config = config;
        _getCursor = cursor ?? (() => Win32.GetCursorPos(out var point) ? (point.X, point.Y) : null);
        _retryTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
        _retryTimer.Tick += CaptureRetry;
        _worker = new LatestCaptureWorker<Work>(Process, (work, ex) =>
            Publish(work.Request, new RecognitionResult(false, default, string.Empty, 0, $"OCR_ERROR: {ex.GetType().Name}")));
    }

    public AssistantState State => _state;

    public bool IsForegroundAllowed(IntPtr hwnd)
    {
        if (_allowedForTest is not null)
        {
            return _allowedForTest(hwnd);
        }
        if (hwnd == IntPtr.Zero || !_windowExists(hwnd) || Win32.IsOwnWindow(hwnd))
        {
            return false;
        }
        if (_config.DesktopTestMode)
        {
            return true;
        }
        return MatchesGameProcess(Win32.GetProcessName(hwnd))
            || (!string.IsNullOrWhiteSpace(_config.ForegroundTitleContains)
                && Win32.GetWindowTitle(hwnd).Contains(_config.ForegroundTitleContains, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Case-insensitive substring match of the executable name against the configured game process, so
    /// launcher-style names such as <c>WARDOGS-Win64-Shipping</c> still match <c>wardogs</c>.
    /// </summary>
    public bool MatchesGameProcess(string processName)
    {
        string wanted = System.IO.Path.GetFileNameWithoutExtension(_config.GameProcessName.Trim());
        return wanted.Length > 0 && !string.IsNullOrWhiteSpace(processName)
            && processName.Contains(wanted, StringComparison.OrdinalIgnoreCase);
    }

    // Clicks outside the centered map square cannot carry a coordinate label; reject before capture and OCR.
    // Desktop test mode targets arbitrary windows, and unknown client bounds fail open: OCR remains the real gate.
    private bool IsOutsideMap(IntPtr hwnd, int cursorX, int cursorY) =>
        !_config.DesktopTestMode
        && _clientBounds(hwnd) is { } client
        && !RoiBuilder.IsPointInsideMap(cursorX - client.X, cursorY - client.Y, client.Width, client.Height);

    public void Request(CaptureRole role, IntPtr hwnd, int cursorX, int cursorY, Func<CaptureRequest, Bitmap?> capture,
        Func<bool>? inputStillCurrent = null, bool anchorValid = true, long? inputTimestamp = null, DateTimeOffset? eventTime = null)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || !_state.LiveEnabled || !IsForegroundAllowed(hwnd) || _getForeground() != hwnd
            || inputStillCurrent?.Invoke() == false)
        {
            return;
        }
        var roi = ScreenCapture.BuildRoi(cursorX, cursorY, _config);
        var req = role == CaptureRole.Origin
            ? _state.BeginOrigin(hwnd.ToInt64(), cursorX, cursorY, roi.X, roi.Y, roi.Width, roi.Height, eventTime)
            : _state.BeginTarget(hwnd.ToInt64(), cursorX, cursorY, roi.X, roi.Y, roi.Width, roi.Height, eventTime).Request;
        if (req is null)
        {
            Changed?.Invoke();
            return;
        }
        DiscardPending();
        _inputStillCurrent = inputStillCurrent;
        if (!anchorValid)
        {
            CompleteFailure(req, DisplayStatuses.Moved);
            Changed?.Invoke();
            return;
        }
        if (IsOutsideMap(hwnd, cursorX, cursorY))
        {
            CompleteFailure(req, DisplayStatuses.OutsideMap);
            Changed?.Invoke();
            return;
        }
        // Snapshot on the input dispatcher, before any wait for recognition. The request owns the anchor/ROI.
        Bitmap? image = null;
        string? captureError = null;
        try
        {
            image = capture(req);
        }
        catch (Exception ex)
        {
            captureError = $"CAPTURE_FAILED: {ex.GetType().Name}";
        }
        if (!CheckWindowAndForeground(hwnd))
        {
            image?.Dispose();
        }
        else if (image is null)
        {
            CompleteFailure(req, captureError ?? "CAPTURE_FAILED");
        }
        else
        {
            _snapshotTime = inputTimestamp ?? System.Diagnostics.Stopwatch.GetTimestamp();
            _retry = new Retry(req, image, capture, _config.DebugMode);
            _retryTimer.Start();
        }
        Changed?.Invoke();
    }

    public void DiscardPending()
    {
        _dispatcher.VerifyAccess();
        _retryTimer.Stop();
        _retry?.Image.Dispose();
        _retry = null;
        _worker.DiscardPending();
    }

    private bool CheckWindowAndForeground(IntPtr hwnd)
    {
        if (!_windowExists(hwnd))
        {
            _state.OnGameWindowClosed();
            return false;
        }
        if (_getForeground() != hwnd || !IsForegroundAllowed(hwnd) || _inputStillCurrent?.Invoke() == false)
        {
            _state.OnForegroundLost();
            return false;
        }
        return true;
    }

    private void CaptureRetry(object? sender, EventArgs e)
    {
        _retryTimer.Stop();
        var retry = _retry;
        _retry = null;
        if (retry is null) { return; }
        var req = retry.Request;
        Bitmap? second = null;
        bool transferred = false;
        try
        {
            if (_disposed || _state.Pending?.Sequence != req.Sequence) { return; }
            var hwnd = new IntPtr(req.ForegroundHwnd);
            if (!CheckWindowAndForeground(hwnd)) { return; }
            if (_getCursor() != (req.CursorX, req.CursorY)
                || System.Diagnostics.Stopwatch.GetElapsedTime(_snapshotTime) > TimeSpan.FromMilliseconds(500))
            {
                CompleteFailure(req, DisplayStatuses.Moved);
                return;
            }
            string? captureError = null;
            try
            {
                second = retry.Capture(req);
            }
            catch (Exception ex)
            {
                captureError = $"CAPTURE_FAILED: {ex.GetType().Name}";
            }
            if (!CheckWindowAndForeground(hwnd)) { return; }
            if (second is null)
            {
                CompleteFailure(req, captureError ?? "CAPTURE_FAILED");
                return;
            }
            if (_getCursor() != (req.CursorX, req.CursorY))
            {
                CompleteFailure(req, DisplayStatuses.Moved);
                return;
            }
            _worker.Enqueue(new Work(req, retry.Image, second, retry.Debug));
            transferred = true;
        }
        catch (Exception ex)
        {
            CompleteFailure(req, $"CAPTURE_FAILED: {ex.GetType().Name}");
        }
        finally
        {
            if (!transferred)
            {
                retry.Image.Dispose();
                second?.Dispose();
            }
            Changed?.Invoke();
        }
    }

    private void CompleteFailure(CaptureRequest req, string reason) =>
        _state.Complete(new OcrCompletion(req.Sequence, req.Generation, req.Role, req.OriginRevision,
            false, default, string.Empty, reason));

    private void Process(Work work)
    {
        var result = RecognitionResult.Reconcile(_recognize(work.Image), _recognize(work.RetryImage));
        if (work.Debug)
        {
            DebugCaptureStore.Save(work.Request, work.Image, result);
            DebugCaptureStore.Save(work.Request, work.RetryImage, result);
        }
        Publish(work.Request, result);
    }

    private void Publish(CaptureRequest req, RecognitionResult result)
    {
        // Never synchronously wait for the UI: shutdown drains the worker on that thread.
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _state.Pending?.Sequence != req.Sequence)
            {
                return;
            }
            var hwnd = new IntPtr(req.ForegroundHwnd);
            if (!_windowExists(hwnd))
            {
                _state.OnGameWindowClosed();
            }
            else if (_getForeground() != hwnd || !IsForegroundAllowed(hwnd) || _inputStillCurrent?.Invoke() == false)
            {
                _state.OnForegroundLost();
            }
            else
            {
                _state.Complete(new OcrCompletion(req.Sequence, req.Generation, req.Role, req.OriginRevision,
                    result.Success, result.Coordinate, result.RawText, result.RejectionReason));
            }
            Changed?.Invoke();
        });
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _state.SetLiveEnabled(false);
        DiscardPending();
        _retryTimer.Tick -= CaptureRetry;
        _worker.Dispose();
    }
}
