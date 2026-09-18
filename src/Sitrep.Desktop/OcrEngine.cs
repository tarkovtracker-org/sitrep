using System.Drawing;
using ImageFormat = System.Drawing.Imaging.ImageFormat;
using System.IO;
using Tesseract;

namespace Sitrep.Desktop;

public sealed record OcrOutput(string RawText, float Confidence, bool EngineUsed);

public sealed class OcrEngine : IDisposable
{
    private TesseractEngine? _engine;
    private bool _disposed;

    public string TessDataPath { get; }
    public string InitError { get; private set; } = string.Empty;
    public bool IsReady => _engine is not null;

    public OcrEngine(string tessDataPath)
    {
        TessDataPath = tessDataPath;
    }

    public bool TryInit(out string error)
    {
        error = string.Empty;
        try
        {
            if (!File.Exists(Path.Combine(TessDataPath, "eng.traineddata")))
            {
                error = $"Missing eng.traineddata in {TessDataPath}. Run scripts/setup-model.ps1.";
                InitError = error;
                return false;
            }
            _engine = new TesseractEngine(TessDataPath, "eng", EngineMode.LstmOnly);
            // Preserve signs and surrounding letters for strict token validation. A numeric whitelist
            // erases this rejection evidence (e.g. x-101.53 becomes the plausible positive x101.53).
            return true;
        }
        catch (Exception ex)
        {
            error = $"OCR init failed: {ex.GetType().Name}: {ex.Message}. Ensure VC++ x64 runtime is installed.";
            InitError = error;
            return false;
        }
    }

    public IReadOnlyList<OcrOutput> Recognize(Bitmap image)
    {
        if (_engine is null)
        {
            return [new OcrOutput(string.Empty, 0, false)];
        }
        var outputs = new List<OcrOutput>();
        foreach (bool threshold in new[] { false, true })
        {
            using var pre = ScreenCapture.PreprocessForOcr(image, threshold);
            using var ms = new MemoryStream();
            pre.Save(ms, ImageFormat.Bmp);
            using var pix = Pix.LoadFromMemory(ms.ToArray());
            using var page = _engine.Process(pix, PageSegMode.SparseText);
            outputs.Add(new OcrOutput(page.GetText() ?? string.Empty, page.GetMeanConfidence(), true));
        }
        return outputs;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _engine?.Dispose();
        _engine = null;
    }
}
