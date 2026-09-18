using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Interop;
using Sitrep.Core;

namespace Sitrep.Desktop;

internal static class IntegrationChecks
{
    public static async Task<int> RunAsync()
    {
        try
        {
            CheckInvalidStartupConfig();
            CheckOcrTokenEvidence();
            CheckPreprocessing();
            CheckWindowBounds();
            CheckUnlockBeforeFirstShow();
            CheckNegativeOverlayPositionRoundTrip();
            CheckDebugRetention();
            CheckProcessIdentityAndMatching();
            await CheckInputDuringBlockedDispatcherAsync();
            await CheckClearDispatchAsync();
            CheckDelayedInputGuard();
            await CheckSnapshotQueueAsync();
            await CheckRetryMovementAsync();
            CheckMapGuard();
            await CheckCompletionGuardAsync(closed: false);
            await CheckCompletionGuardAsync(closed: true);
            await CheckCompletionGuardAsync(closed: false, sampledFocusRoundTrip: true);
            Console.WriteLine("INTEGRATION OK: preprocessing parity/ownership, native overlay bounds/styles/lock round-trip, unlock before first show, negative overlay position round-trip, concurrent paired debug retention/failure recovery, native process identity/matching, blocked-dispatcher input chords/disposal, delayed input/focus epoch guards, snapshot queue, OCR failure, map guard, completion focus/closure guards.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"INTEGRATION FAIL: {ex}");
            return 1;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private static void RequireEqual<T>(T expected, T actual, string message)
    {
        Require(EqualityComparer<T>.Default.Equals(expected, actual), message);
    }

    // Poll live state on the dispatcher; worker callbacks may change it between awaits.
    private static async Task UntilCompletedAsync(AssistantState state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (state.Pending is not null)
        {
            if (DateTime.UtcNow > deadline) { throw new TimeoutException("Integration condition did not complete."); }
            await Task.Delay(10);
        }
    }

    private static Bitmap Frame(byte marker)
    {
        var bitmap = new Bitmap(40, 40, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(marker, 100, 100));
        return bitmap;
    }

    private static void CheckInvalidStartupConfig()
    {
        string path = Path.Combine(Path.GetTempPath(), "Sitrep-config-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            foreach (string contents in new[] { "null", "{", "{\"RoiWidth\":0}" })
            {
                File.WriteAllText(path, contents);
                var errors = new List<string>();
                var config = App.LoadInteractiveConfig(errors.Add, path);
                Require(config is null && errors.Count == 1 && errors[0].Contains(path, StringComparison.Ordinal),
                    "Invalid startup config must produce one actionable error and no usable config.");
                Require(File.ReadAllText(path) == contents, "Startup overwrote invalid config.");
            }
        }
        finally { File.Delete(path); }
    }

    private static void CheckOcrTokenEvidence()
    {
        // Exercise the actual engine and both recipes: text-only parser tests cannot detect OCR filtering
        // that erases a sign/letter and turns an invalid label into a plausible coordinate.
        using var ocr = new OcrEngine(Path.Combine(AppContext.BaseDirectory, "tessdata"));
        Require(ocr.TryInit(out string error), $"OCR evidence regression could not initialize: {error}");
        foreach (string text in new[] { "x-101.53 y107.77", "x+101.53 y107.77", "ax101.53 y107.77", "x101.53z y107.77", "x101.53 y107.77" })
        {
            using var image = new Bitmap(600, 140);
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.Clear(Color.White);
                using var font = new Font("Consolas", 36, GraphicsUnit.Pixel);
                graphics.DrawString(text, font, Brushes.Black, 10, 40);
            }
            var result = RecognitionPipeline.Recognize(image, ocr);
            bool valid = text == "x101.53 y107.77";
            Require(result.Success == valid && (!valid || result.Coordinate == new MapCoordinate(101.53, 107.77)),
                $"OCR token evidence changed acceptance for '{text}': success={result.Success}, raw='{result.RawText}', rejection={result.RejectionReason}");
            Require(result.RejectionReason != "OCR_UNAVAILABLE", "OCR evidence regression did not exercise recognition.");
        }
        Console.WriteLine("Live OCR signed/letter-glued negatives and positive control checked through both recipes.");
    }

    private static void CheckPreprocessing()
    {
        using var source = Frame(180);
        source.SetPixel(10, 10, Color.FromArgb(255, 20, 140, 220));
        foreach (bool threshold in new[] { false, true })
        {
            using var actual = ScreenCapture.PreprocessForOcr(source, threshold);
            using var scaled = new Bitmap(source.Width * 2, source.Height * 2, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(source, 0, 0, scaled.Width, scaled.Height);
            }
            using var padded = new Bitmap(scaled.Width + 20, scaled.Height + 20, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(padded))
            {
                g.Clear(Color.Black);
                g.DrawImageUnscaled(scaled, 10, 10);
            }
            CheckPreprocessedPixels(actual, padded, threshold);
            Require(source.Width == 40, "Preprocessing disposed its borrowed input.");
        }
    }

    private static void CheckPreprocessedPixels(Bitmap actual, Bitmap padded, bool threshold)
    {
        for (int y = 0; y < actual.Height; y++)
        {
            for (int x = 0; x < actual.Width; x++)
            {
                Color before = padded.GetPixel(x, y);
                int gray = (299 * before.R + 587 * before.G + 114 * before.B) / 1000;
                int binary = gray > 140 ? 255 : 0;
                int expected = threshold ? binary : gray;
                Color after = actual.GetPixel(x, y);
                Require(after.R == expected && after.G == expected && after.B == expected && after.A == before.A,
                    "LockBits preprocessing changed pixels.");
            }
        }
    }

    private static void CheckWindowBounds()
    {
        // The overlay never touches disk; lock/unlock only updates the in-memory config it was given.
        var overlay = new OverlayWindow(new AppConfig());
        try
        {
            overlay.ShowSolution(new AssistantState(null), StatusLevel.Neutral);
            overlay.UpdateLayout();
            var hwnd = new WindowInteropHelper(overlay).Handle;
            var region = Win32.GetScreenRegion(hwnd);
            var topLeft = overlay.PointToScreen(new System.Windows.Point(0, 0));
            var bottomRight = overlay.PointToScreen(new System.Windows.Point(overlay.ActualWidth, overlay.ActualHeight));
            Require(Math.Abs(region.X - topLeft.X) <= 1 && Math.Abs(region.Y - topLeft.Y) <= 1
                && Math.Abs(region.Width - (bottomRight.X - topLeft.X)) <= 1
                && Math.Abs(region.Height - (bottomRight.Y - topLeft.Y)) <= 1,
                "Native exclusion bounds do not match WPF physical pixels.");
            long mask = Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED | Win32.WS_EX_NOACTIVATE;
            Require((Win32.GetExtendedStyle(hwnd) & mask) == mask, "Missing click-through/non-activation styles.");
            Require(ScreenCapture.CaptureRegion(region, region, MainWindow.GetVisibleExclusions()) is null, "Own-window overlap accepted.");

            overlay.Unlock();
            long unlocked = Win32.GetExtendedStyle(hwnd);
            Require(overlay.IsInteractive && overlay.IsHitTestVisible && (unlocked & Win32.WS_EX_TRANSPARENT) == 0
                && (unlocked & (Win32.WS_EX_LAYERED | Win32.WS_EX_NOACTIVATE)) == (Win32.WS_EX_LAYERED | Win32.WS_EX_NOACTIVATE),
                "Unlocked overlay must be hit-testable but still layered and non-activating.");
            overlay.Lock();
            Require(!overlay.IsInteractive && !overlay.IsHitTestVisible && (Win32.GetExtendedStyle(hwnd) & mask) == mask,
                "Locked overlay did not restore click-through styles.");
            Console.WriteLine($"Overlay bounds/styles and lock round-trip checked at DPI {System.Windows.Media.VisualTreeHelper.GetDpi(overlay).PixelsPerInchX}.");
        }
        finally { overlay.Close(); }
    }

    // Regression: "Unlock overlay" is often the first click at startup, before the game (and thus the overlay)
    // has ever been shown. Toggling WS_EX styles on a window without an HWND used to throw Win32Exception 1400.
    private static void CheckUnlockBeforeFirstShow()
    {
        var overlay = new OverlayWindow(new AppConfig());
        try
        {
            Require(new WindowInteropHelper(overlay).Handle == IntPtr.Zero && !overlay.IsVisible,
                "Precondition: overlay must start without an HWND and hidden.");
            overlay.Unlock();
            var hwnd = new WindowInteropHelper(overlay).Handle;
            Require(hwnd != IntPtr.Zero, "Unlock on a never-shown overlay must create its HWND.");
            long unlocked = Win32.GetExtendedStyle(hwnd);
            Require(overlay.IsInteractive && overlay.IsHitTestVisible && overlay.IsVisible
                && (unlocked & Win32.WS_EX_TRANSPARENT) == 0
                && (unlocked & (Win32.WS_EX_LAYERED | Win32.WS_EX_NOACTIVATE)) == (Win32.WS_EX_LAYERED | Win32.WS_EX_NOACTIVATE),
                "Unlock on a never-shown overlay must show it hit-testable, layered and non-activating.");
            overlay.Lock();
            long mask = Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED | Win32.WS_EX_NOACTIVATE;
            Require(!overlay.IsInteractive && (Win32.GetExtendedStyle(hwnd) & mask) == mask,
                "Lock after a first-show unlock did not restore click-through styles.");
        }
        finally { overlay.Close(); }
    }

    // Regression: monitors left of / above the primary yield negative WPF coordinates. The former -1 sentinel
    // plus ">= 0" checks silently dropped them on restore; null now means "unset" and any finite value is kept.
    private static void CheckNegativeOverlayPositionRoundTrip()
    {
        const double left = -1920, top = -50;
        var config = new AppConfig { OverlayLeft = left, OverlayTop = top };
        var overlay = new OverlayWindow(config);
        OverlayWindow? restored = null;
        try
        {
            Require(overlay.Left == left && overlay.Top == top, "Negative saved overlay position was not applied.");

            // Lock() records whatever position the window actually has; it must keep the sign and the value.
            overlay.Unlock();
            overlay.Lock();
            Require(config.OverlayLeft.HasValue && config.OverlayTop.HasValue
                && config.OverlayLeft.Value == overlay.Left && config.OverlayTop.Value == overlay.Top
                && config.OverlayLeft.Value < 0 && config.OverlayTop.Value < 0,
                $"Lock did not record the negative position (config {config.OverlayLeft},{config.OverlayTop}; window {overlay.Left},{overlay.Top}).");

            // Persisted config must survive JSON and validation, then restore the same position in a fresh window.
            string path = Path.Combine(Path.GetTempPath(), "Sitrep-overlay-" + Guid.NewGuid().ToString("N") + ".json");
            AppConfig reloaded;
            try
            {
                config.Save(path);
                reloaded = AppConfig.Load(path);
            }
            finally { File.Delete(path); }
            Require(reloaded.OverlayLeft == config.OverlayLeft && reloaded.OverlayTop == config.OverlayTop,
                "Negative overlay position did not survive config save/load.");
            // Configs written by earlier builds carry the old -1/-1 "unset" sentinel; only that exact pair migrates to null.
            try
            {
                File.WriteAllText(path, "{\"OverlayLeft\":-1,\"OverlayTop\":-1}");
                var legacy = AppConfig.Load(path);
                Require(legacy.OverlayLeft is null && legacy.OverlayTop is null, "Legacy -1/-1 overlay sentinel must load as unset.");
                File.WriteAllText(path, "{\"OverlayLeft\":-1,\"OverlayTop\":-2}");
                var custom = AppConfig.Load(path);
                Require(custom.OverlayLeft == -1 && custom.OverlayTop == -2, "Non-sentinel negative position must not be migrated.");
            }
            finally { File.Delete(path); }
            restored = new OverlayWindow(reloaded);
            Require(restored.Left == overlay.Left && restored.Top == overlay.Top,
                $"Restored overlay position {restored.Left},{restored.Top} differs from saved {overlay.Left},{overlay.Top}.");

            // Reset must return to "unset" (null), not to a sentinel, and the default position must be used.
            restored.ResetPosition();
            Require(reloaded.OverlayLeft is null && reloaded.OverlayTop is null && restored.Left >= 0 && restored.Top == 64,
                "ResetPosition must clear the saved position and return to the default placement.");
            Require(new AppConfig().OverlayLeft is null && new AppConfig().OverlayTop is null,
                "Fresh config must have no saved overlay position.");
        }
        finally
        {
            overlay.Close();
            restored?.Close();
        }
    }

    private static void CheckDebugRetention()
    {
        string dir = Path.Combine(Path.GetTempPath(), "Sitrep-integration-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "records.log"), "legacy log");
            using var frame = Frame(100);
            for (int i = 0; i < 60; i++)
            {
                var request = new CaptureRequest(i, 0, CaptureRole.Target, 0, 1, DateTimeOffset.UtcNow, 10, 20, 30, 40, 40, 40);
                DebugCaptureStore.Save(request, frame, new RecognitionResult(false, default, "noise", 0, "NO_COORDINATES"), dir);
            }
            Require(Directory.GetFiles(dir).Length == 100, "Debug retention must keep 50 complete PNG/JSON pairs.");
            Require(!File.Exists(Path.Combine(dir, "records.log")), "Legacy unbounded log survived.");
            File.WriteAllText(Path.Combine(dir, "orphan.png"), "interrupted image");
            File.WriteAllText(Path.Combine(dir, "orphan-record.json"), "interrupted metadata");
            File.WriteAllText(Path.Combine(dir, "interrupted.png.tmp"), "partial image");
            Parallel.For(0, 80, i =>
            {
                using var concurrentFrame = Frame((byte)i);
                var request = new CaptureRequest(7, 0, CaptureRole.Target, 0, 1, DateTimeOffset.UtcNow, 10, 20, 30, 40, 40, 40);
                DebugCaptureStore.Save(request, concurrentFrame, new RecognitionResult(false, default, i.ToString(System.Globalization.CultureInfo.InvariantCulture), 0, "NO_COORDINATES"), dir);
            });
            var images = Directory.GetFiles(dir, "*.png");
            Require(images.Length == 50 && Directory.GetFiles(dir).Length == 100, "Concurrent retention left missing pairs, temps or orphans.");
            foreach (string image in images)
            {
                using var metadata = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(image, ".json")));
                int sequence = metadata.RootElement.GetProperty("request").GetProperty("Sequence").GetInt32();
                using var bitmap = new Bitmap(image);
                int marker = int.Parse(metadata.RootElement.GetProperty("result").GetProperty("RawText").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture);
                Require(sequence == 7 && bitmap.GetPixel(0, 0).R == marker, "Concurrent same-request save collided or mismatched image and metadata.");
            }
            var failed = new CaptureRequest(999, 0, CaptureRole.Origin, 0, 1, DateTimeOffset.UtcNow, 0, 0, 0, 0, 40, 40);
            DebugCaptureStore.SaveRecord(failed, stream =>
            {
                stream.WriteByte(1);
                throw new IOException("Injected partial encoder failure");
            }, new RecognitionResult(false, default, "", 0, "NO_COORDINATES"), dir);
            Require(Directory.GetFiles(dir).Length == 98 && !Directory.GetFiles(dir).Any(p => p.Contains("_999_", StringComparison.Ordinal)),
                "Failed write left a partial image, metadata, or temporary file.");
            DebugCaptureStore.Save(failed, frame, new RecognitionResult(false, default, "", 0, "NO_COORDINATES"), dir);
            Require(Directory.GetFiles(dir).Length == 100, "Store did not recover after failed write.");
        }
        finally { if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); } }
    }

    private static void CheckProcessIdentityAndMatching()
    {
        var window = new OverlayWindow(new AppConfig());
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            for (int i = 0; i < 100; i++)
            {
                RequireEqual(process.ProcessName, Win32.GetProcessName(hwnd), "Retained native process identity mismatch.");
            }
            RequireEqual(string.Empty, Win32.GetProcessName(IntPtr.Zero), "Zero HWND must not inherit the cached name.");
            var config = new AppConfig { GameProcessName = " WARDOGS.exe " };
            using var service = new AssistantService(new AssistantState(null), config,
                _ => new RecognitionResult(false, default, "", 0, "NO_COORDINATES"), () => hwnd, _ => true);
            Require(service.MatchesGameProcess("wardogs-Win64-Shipping") && service.MatchesGameProcess("prefixWARDOGSsuffix"),
                "Case-insensitive basename substring semantics changed.");
            Require(!service.MatchesGameProcess("other") && !service.MatchesGameProcess(""), "Unrelated process matched.");
            config.GameProcessName = " ";
            Require(!service.MatchesGameProcess("wardogs"), "Empty configured process must never match.");
            Require(!service.IsForegroundAllowed(hwnd), "Desktop test/title matching must not allow SITREP's own window.");
        }
        finally { window.Close(); Win32.ClearProcessNameCache(); }
    }

    private sealed record SampleBox(InputSample Value);

    private static async Task CheckInputDuringBlockedDispatcherAsync()
    {
        var idle = new InputSample(1, -400, 500, true, false, false, false, false, false, false);
        var current = new SampleBox(idle);
        using var sampled = new ManualResetEventSlim();
        using var input = new InputMonitor(() =>
        {
            var value = Volatile.Read(ref current).Value;
            sampled.Set();
            return value;
        });
        input.SetEnabled(true);
        var received = new List<InputGesture>();
        Exception? callbackFailure = null;
        void SampleWhileBlocked(InputSample value, InputGesture gesture)
        {
            Volatile.Write(ref current, new SampleBox(value));
            sampled.Reset();
            Require(sampled.Wait(TimeSpan.FromSeconds(5)), "Input sampler stopped with UI blocked.");
            Require(input.IsCurrent(gesture), "Unexpected input epoch change."); // Acquires sampler lock after read.
        }
        input.CapturePressed += gesture =>
        {
            try
            {
                received.Add(gesture);
                if (received.Count != 1) { return; }
                // This handler deliberately blocks the dispatcher, just like synchronous screen capture.
                SampleWhileBlocked(idle, gesture);
                SampleWhileBlocked(idle with { Middle = true, Shift = true }, gesture);
                SampleWhileBlocked(idle, gesture); // Release modifier before the queued callback can run.
            }
            catch (Exception ex) { callbackFailure = ex; }
        };
        Volatile.Write(ref current, new SampleBox(idle with { Middle = true, Control = true }));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (received.Count < 2 && callbackFailure is null && DateTime.UtcNow < deadline) { await Task.Delay(10); }
        if (callbackFailure is not null) { throw callbackFailure; }
        Require(received.Count == 2 && received[0].Action == InputAction.Origin && received[1].Action == InputAction.Target
            && received[1].Sample.Shift && received[1].Sample.X == -400,
            "Blocked dispatcher lost or reinterpreted a sampled Ctrl/Shift+MMB chord.");
        input.Dispose();
        await Task.Delay(30);
        Require(received.Count == 2, "Input callback survived disposal.");
    }

    private static async Task CheckClearDispatchAsync()
    {
        // The real sampling thread and WPF timer dispatch run here; only native input reads are substituted.
        // No SendInput, key messages, hooks, game window, or user configuration is involved.
        foreach (var (sampledForeground, dispatchForeground, expected) in new[]
        {
            (1L, 1L, true), (2L, 2L, true), (3L, 3L, false), (3L, 2L, false), (3L, 1L, false), (1L, 2L, false),
        })
        {
            var idle = new InputSample(sampledForeground, -400, 500, true, false, false, false, false, false, false);
            var current = new SampleBox(idle);
            SampleBox? observed = null;
            using var input = new InputMonitor(() =>
            {
                var value = Volatile.Read(ref current);
                Volatile.Write(ref observed, value);
                return value.Value;
            });
            input.SetEnabled(true);
            void SampleWhileBlocked(InputSample value)
            {
                var next = new SampleBox(value);
                Volatile.Write(ref current, next);
                Require(SpinWait.SpinUntil(() => ReferenceEquals(Volatile.Read(ref observed), next), TimeSpan.FromSeconds(5)),
                    "Clear sample did not reach the independent input thread.");
                _ = input.IsCurrent(default); // Acquire sampler lock after the read, ensuring enqueue/reset finished.
            }
            var received = new List<InputGesture>();
            input.ClearPressed += received.Add;
            var pressed = idle with { Clear = true, Control = true };
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            DateTimeOffset eventStart = DateTimeOffset.UtcNow;
            SampleWhileBlocked(pressed);
            SampleWhileBlocked(idle with { X = 200 }); // Release F9 and move before the UI can dispatch.
            long end = System.Diagnostics.Stopwatch.GetTimestamp();
            DateTimeOffset eventEnd = DateTimeOffset.UtcNow;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (received.Count == 0 && DateTime.UtcNow < deadline) { await Task.Delay(10); }
            Require(received.Count == 1, "Frozen clear was not dispatched exactly once.");
            var clear = received[0];
            Require(clear.Action == InputAction.Clear && clear.Sample == pressed && input.IsCurrent(clear)
                && clear.Timestamp >= start && clear.Timestamp <= end && clear.EventTime >= eventStart && clear.EventTime <= eventEnd,
                "Clear dispatch lost its frozen gesture (foreground, modifiers, cursor, timestamp or epoch).");
            Require(ClearInputPolicy.IsAllowed(clear, input.IsCurrent(clear), dispatchForeground, 1, true, 2) == expected,
                "Dispatched clear was authorized using a later foreground or a non-control window.");

            // Retain conservative invalidation: a queued clear must not survive even an away/back focus round-trip.
            SampleWhileBlocked(pressed);
            SampleWhileBlocked(idle with { Foreground = 9 });
            SampleWhileBlocked(idle);
            var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            input.Poll += () => polled.TrySetResult();
            await polled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(received.Count == 1 && !input.IsCurrent(clear), "A stale clear survived a sampled focus round-trip.");
            SampleWhileBlocked(pressed);
            input.Dispose();
            await Task.Delay(30);
            Require(received.Count == 1, "Clear callback survived disposal.");
        }
        Console.WriteLine("Frozen F9 dispatch/policy, sampled focus-reset rejection and disposal checked on the WPF dispatcher.");
    }

    private static void CheckDelayedInputGuard()
    {
        var state = new AssistantState(null);
        bool current = true;
        int captures = 0;
        using var service = new AssistantService(state, new AppConfig(), _ => throw new InvalidOperationException("No OCR expected"),
            () => new IntPtr(1), _ => true, _ => true, () => (400, 500));
        service.Request(CaptureRole.Origin, new IntPtr(1), 400, 500, _ => { captures++; return Frame(1); }, () => current, anchorValid: false);
        Require(captures == 0 && state.Status == DisplayStatuses.Moved && state.ConfirmedOrigin is null,
            "A delayed/moved gesture must fail closed before capture.");
        service.Request(CaptureRole.Origin, new IntPtr(1), 400, 500, _ =>
        {
            current = false; // Focus left and returned while CopyFromScreen blocked the UI.
            return Frame(1);
        }, () => current);
        Require(state.Pending is null && state.ConfirmedOrigin is null && state.Status == DisplayStatuses.WindowLost,
            "Sampled focus round-trip during capture was not invalidated.");
    }

    private static async Task CheckSnapshotQueueAsync()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new AssistantState(null, true);
        using var service = new AssistantService(state, new AppConfig(), image =>
        {
            byte marker = image.GetPixel(0, 0).R;
            if (marker == 1)
            {
                started.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException(); }
            }
            if (marker == 4) { throw new InvalidOperationException("synthetic native OCR failure"); }
            return new RecognitionResult(true, new MapCoordinate(marker, 100), "fixture", 1, "");
        }, () => new IntPtr(1), _ => true, _ => true, () => (400, 500));
        var captures = new List<CaptureRequest>();
        var markers = new Dictionary<long, byte>();
        Bitmap Capture(CaptureRequest req)
        {
            Require(req.CursorX == 400 && req.CursorY == 500 && req.RoiX == 360 && req.RoiY == 324
                && req.RoiWidth == 360 && req.RoiHeight == 200, "Request lost its event anchor/ROI.");
            captures.Add(req);
            if (!markers.TryGetValue(req.Sequence, out byte marker))
            {
                marker = (byte)(markers.Count + 1);
                markers.Add(req.Sequence, marker);
            }
            return Frame(marker);
        }
        try
        {
            service.Request(CaptureRole.Origin, new IntPtr(1), 400, 500, Capture);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var origin = state.Pending;
            service.Request(CaptureRole.Target, new IntPtr(1), 400, 500, Capture);
            RequireEqual(2, captures.Count, "Target without usable origin took another snapshot.");
            Require(ReferenceEquals(state.Pending, origin), "Target superseded pending origin.");
            service.Request(CaptureRole.Origin, new IntPtr(1), 400, 500, Capture);
            service.Request(CaptureRole.Origin, new IntPtr(1), 400, 500, Capture);
            RequireEqual(4, captures.Count, "Snapshots waited behind busy OCR instead of capturing on trigger.");
            release.Set();
            await UntilCompletedAsync(state);
            Require(state.ConfirmedOrigin == new MapCoordinate(3, 100), "Newest snapshot did not win.");
            service.Request(CaptureRole.Target, new IntPtr(1), 400, 500, Capture);
            await UntilCompletedAsync(state);
            Require(state.Status.StartsWith("TARGET OCR FAILED: OCR_ERROR", StringComparison.Ordinal)
                && state.ConfirmedOrigin.HasValue && state.ElevationMil is null, "OCR exception left stale/pending state.");
            service.Request(CaptureRole.Origin, new IntPtr(1), 400, 500, _ => throw new InvalidOperationException());
            Require(state.Pending is null && state.ConfirmedOrigin is null, "Capture exception left usable origin.");
        }
        finally { release.Set(); }
    }

    private static async Task CheckRetryMovementAsync()
    {
        var state = new AssistantState(null, true);
        int recognitions = 0;
        using var service = new AssistantService(state, new AppConfig(), _ =>
        {
            Interlocked.Increment(ref recognitions);
            return new RecognitionResult(true, new MapCoordinate(100, 100), "fixture", 1, "");
        }, () => new IntPtr(1), _ => true, _ => true, () => (401, 500));
        int captures = 0;
        service.Request(CaptureRole.Origin, new IntPtr(1), 400, 500, _ => { captures++; return Frame(1); });
        await UntilCompletedAsync(state);
        Require(captures == 1 && recognitions == 0 && state.Status == DisplayStatuses.Moved
            && state.ConfirmedOrigin is null, "Retry failed to reject a moved anchor before capture/OCR.");
    }

    private static void CheckMapGuard()
    {
        // 2560x1440 client at (0,0): map square is (840,280)-(1719,1159). A gutter click must fail before capture/OCR.
        var state = new AssistantState(null, true);
        int captures = 0, recognitions = 0;
        using var service = new AssistantService(state, new AppConfig(), _ => { recognitions++; return new RecognitionResult(true, new MapCoordinate(100, 100), "fixture", 1, ""); },
            () => new IntPtr(1), _ => true, _ => true, () => (100, 700), _ => new CaptureRegion(0, 0, 2560, 1440));
        service.Request(CaptureRole.Origin, new IntPtr(1), 100, 700, _ => { captures++; return Frame(1); });
        Require(captures == 0 && recognitions == 0 && state.Pending is null && state.Status == DisplayStatuses.OutsideMap,
            "Click outside the map square must be rejected without capture or OCR.");

        // Desktop test mode targets arbitrary windows, so the same click must proceed to capture.
        var testState = new AssistantState(null, true);
        using var testService = new AssistantService(testState, new AppConfig { DesktopTestMode = true }, _ => new RecognitionResult(true, new MapCoordinate(100, 100), "fixture", 1, ""),
            () => new IntPtr(1), _ => true, _ => true, () => (100, 700), _ => new CaptureRegion(0, 0, 2560, 1440));
        int testCaptures = 0;
        testService.Request(CaptureRole.Origin, new IntPtr(1), 100, 700, _ => { testCaptures++; return Frame(1); });
        Require(testCaptures == 1 && testState.Status == DisplayStatuses.Reading, "Desktop test mode must bypass the map guard.");
        testService.DiscardPending();
    }

    private static async Task CheckCompletionGuardAsync(bool closed, bool sampledFocusRoundTrip = false)
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new AssistantState(null, true);
        IntPtr foreground = new(1);
        bool exists = true;
        bool inputCurrent = true;
        using var service = new AssistantService(state, new AppConfig(), _ =>
        {
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException(); }
            return new RecognitionResult(true, new MapCoordinate(100, 100), "fixture", 1, "");
        }, () => foreground, _ => exists, _ => true, () => (400, 500));
        try
        {
            service.Request(CaptureRole.Origin, new IntPtr(1), 400, 500, _ => Frame(1), () => inputCurrent);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (sampledFocusRoundTrip) { inputCurrent = false; }
            else if (closed) { exists = false; }
            else { foreground = new IntPtr(2); }
            release.Set();
            await UntilCompletedAsync(state);
            Require(state.ConfirmedOrigin is null && state.ElevationMil is null, "Late completion bypassed window identity guard.");
            Require(state.Status == (closed ? DisplayStatuses.SetMortar : DisplayStatuses.WindowLost), "Wrong foreground failure status.");
        }
        finally { release.Set(); }
    }
}
