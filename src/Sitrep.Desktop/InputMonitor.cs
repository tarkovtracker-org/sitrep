using System.Diagnostics;
using System.Windows.Threading;
using Sitrep.Core;

namespace Sitrep.Desktop;

/// <summary>Independent high-bit polling; only bounded, frozen gestures cross to the UI dispatcher.</summary>
public sealed class InputMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly InputSampling _sampling = new();
    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread _thread;
    private readonly DispatcherTimer _timer;
    private readonly Func<InputSample> _read;
    private bool _enabled;
    private bool _disposed;

    public event Action<InputGesture>? CapturePressed;
    public event Action<InputGesture>? ClearPressed;
    public event Action? Invalidated;
    public event Action? Poll;

    public InputMonitor() : this(ReadSample) { }

    internal InputMonitor(Func<InputSample> read)
    {
        _read = read;
        ResetEdges();
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
        _timer.Tick += Tick;
        _thread = new Thread(SampleLoop) { IsBackground = true, Name = "SITREP input sampling" };
        _thread.Start();
        _timer.Start();
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            _enabled = enabled;
            _sampling.Reset(enabled, _read());
        }
    }

    public void ResetEdges()
    {
        lock (_gate) { _sampling.Reset(_enabled, _read()); }
    }

    public bool IsCurrent(InputGesture gesture)
    {
        lock (_gate) { return !_disposed && _enabled && gesture.Epoch == _sampling.Epoch; }
    }

    private static bool IsDown(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static InputSample ReadSample()
    {
        var foreground = Win32.GetForegroundWindow();
        bool hasCursor = Win32.GetCursorPos(out var point);
        var sample = new InputSample(foreground.ToInt64(), point.X, point.Y, hasCursor,
            IsDown(Win32.VK_F8), IsDown(Win32.VK_F7), IsDown(Win32.VK_F9), IsDown(Win32.VK_MBUTTON),
            IsDown(Win32.VK_CONTROL), IsDown(Win32.VK_SHIFT));
        // Do not associate a mixed sample with a window that changed during the native reads.
        return Win32.GetForegroundWindow() == foreground ? sample : sample with { Foreground = 0, HasCursor = false };
    }

    private void SampleLoop()
    {
        while (!_stop.Wait(10))
        {
            lock (_gate) { _sampling.Sample(_read(), Stopwatch.GetTimestamp()); }
        }
    }

    private static void InvokeSafely(Action callback)
    {
        try { callback(); }
        catch (Exception ex) { Trace.TraceError("InputMonitor callback error: {0}", ex); }
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (_disposed) { return; }
        bool invalidated;
        lock (_gate) { invalidated = _sampling.TakeInvalidation(); }
        if (invalidated) { InvokeSafely(() => Invalidated?.Invoke()); }
        InvokeSafely(() => Poll?.Invoke());
        // Limit dispatcher work too; the sampler keeps observing even inside a slow capture callback.
        for (int i = 0; i < 64 && !_disposed; i++)
        {
            InputGesture gesture;
            bool hasGesture;
            lock (_gate)
            {
                invalidated = _sampling.TakeInvalidation();
                hasGesture = _sampling.TryTake(out gesture);
            }
            if (invalidated)
            {
                InvokeSafely(() => Invalidated?.Invoke());
                InvokeSafely(() => Poll?.Invoke());
            }
            if (!hasGesture) { break; }
            if (!IsCurrent(gesture)) { continue; }
            if (gesture.Action == InputAction.Clear) { InvokeSafely(() => ClearPressed?.Invoke(gesture)); }
            else { InvokeSafely(() => CapturePressed?.Invoke(gesture)); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
        }
        _timer.Stop();
        _timer.Tick -= Tick;
        _stop.Set();
        _thread.Join(); // Sampling never waits on the dispatcher.
        _stop.Dispose();
    }
}
