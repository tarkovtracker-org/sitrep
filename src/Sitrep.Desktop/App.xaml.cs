using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows;
using Sitrep.Core;

namespace Sitrep.Desktop;

public partial class App : Application
{
    private AssistantService? _service;
    private InputMonitor? _input;
    private OcrEngine? _ocr;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string[] args = e.Args;
        if (args.Contains("--integration-test"))
        {
            Shutdown(await IntegrationChecks.RunAsync());
            return;
        }
        if (args.Contains("--self-test"))
        {
            int code = RunSelfTest();
            Shutdown(code);
            return;
        }
        int diagIdx = Array.IndexOf(args, "--diagnose-image");
        if (diagIdx >= 0)
        {
            string? path = diagIdx + 1 < args.Length ? args[diagIdx + 1] : null;
            string? cropArg = GetArg(args, "--crop");
            string? report = GetArg(args, "--report");
            int code = RunDiagnose(path, cropArg, report);
            Shutdown(code);
            return;
        }
        RunInteractive();
    }

    private static string? GetArg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string BaseDir => AppContext.BaseDirectory;

    private static FiringTable? LoadTable(out string error)
    {
        error = string.Empty;
        string p = Path.Combine(BaseDir, "data", "l81-apollyon.json");
        if (!File.Exists(p))
        {
            error = $"Missing data file {p}.";
            return null;
        }
        var (t, err) = FiringTable.LoadApollyonL81(p);
        if (t is null)
        {
            error = $"Invalid data: {err}.";
            return null;
        }
        return t;
    }

    private static OcrEngine CreateOcr(AppConfig cfg, out string error)
    {
        error = string.Empty;
        string dir = Path.IsPathRooted(cfg.TessDataDir) ? cfg.TessDataDir : Path.Combine(BaseDir, cfg.TessDataDir);
        var ocr = new OcrEngine(dir);
        ocr.TryInit(out error);
        return ocr;
    }

    internal static AppConfig? LoadInteractiveConfig(Action<string> reportError, string? path = null)
    {
        try
        {
            return AppConfig.Load(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            reportError(ex.Message);
            return null;
        }
    }

    private void RunInteractive()
    {
        var config = LoadInteractiveConfig(message =>
            MessageBox.Show(message, "SITREP startup error", MessageBoxButton.OK, MessageBoxImage.Error));
        if (config is null)
        {
            Shutdown(1);
            return;
        }
        FiringTable? table = LoadTable(out string tableError);
        _ocr = CreateOcr(config, out string ocrError);
        string startupError = string.Join(" ", new[] { tableError, ocrError }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (!string.IsNullOrWhiteSpace(startupError))
        {
            _ocr.Dispose();
            MessageBox.Show(startupError, "SITREP startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        // Always observing input; explicit gestures still require process/title/test-mode foreground guards.
        var state = new AssistantState(table, liveEnabled: true);
        _service = new AssistantService(state, _ocr, config);
        _input = new InputMonitor();
        _input.SetEnabled(true);
        var overlay = new OverlayWindow(config);
        var main = new MainWindow(_service, _input, config, overlay, startupError);
        MainWindow = main;
        main.Show();
        Exit += (_, __) =>
        {
            _input.Dispose();
            _service.Dispose();
            _ocr.Dispose();
        };
    }

    private static int RunSelfTest()
    {
        try
        {
            if (LoadTable(out string tableError) is null)
            {
                Console.WriteLine($"SELF-TEST FAIL: {tableError}");
                return 1;
            }
            using var ocr = new OcrEngine(Path.Combine(BaseDir, "tessdata"));
            if (!ocr.TryInit(out string err))
            {
                Console.WriteLine($"SELF-TEST FAIL (dependency smoke): {err}");
                return 2;
            }
            using var bmp = new Bitmap(500, 140);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                using var font = new Font("Consolas", 36, GraphicsUnit.Pixel);
                g.DrawString("x101.53 y107.77", font, Brushes.Black, 10, 40);
            }
            var result = RecognitionPipeline.Recognize(bmp, ocr);
            Console.WriteLine($"SELF-TEST (dependency smoke): engine=ok raw='{result.RawText.Trim()}' parsed={result.Success}");
            return result.Success && result.Coordinate == new MapCoordinate(101.53, 107.77) ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SELF-TEST ERROR: {ex.Message}");
            return 1;
        }
    }

    private static int RunDiagnose(string? imagePath, string? cropArg, string? reportPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                Console.WriteLine("DIAGNOSE FAIL: missing image file.");
                return 1;
            }
            using var ocr = new OcrEngine(Path.Combine(BaseDir, "tessdata"));
            if (!ocr.TryInit(out string err))
            {
                Console.WriteLine($"DIAGNOSE FAIL: {err}");
                return 2;
            }
            Core.CaptureRegion? crop = null;
            if (!string.IsNullOrWhiteSpace(cropArg))
            {
                var parts = cropArg.Split(',').Select(int.Parse).ToArray();
                if (parts.Length != 4)
                {
                    throw new ArgumentException("--crop requires x,y,width,height.");
                }
                crop = new Core.CaptureRegion(parts[0], parts[1], parts[2], parts[3]);
            }
            var result = RecognitionPipeline.RecognizeImageFile(imagePath, ocr, crop);
            var report = new
            {
                image = imagePath,
                success = result.Success,
                x = result.Success ? result.Coordinate.X : (double?)null,
                y = result.Success ? result.Coordinate.Y : (double?)null,
                rawText = result.RawText,
                confidence = result.Confidence,
                rejection = result.RejectionReason,
            };
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
            if (!string.IsNullOrWhiteSpace(reportPath))
            {
                File.WriteAllText(reportPath, json);
            }
            return result.Success ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DIAGNOSE ERROR: {ex.Message}");
            return 1;
        }
    }
}
