using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using Sitrep.Core;

namespace Sitrep.Desktop;

internal static class DebugCaptureStore
{
    internal static string DefaultDirectory => Path.Combine(AppConfig.ConfigDir, "captures");

    internal static void Save(CaptureRequest request, Bitmap image, RecognitionResult result, string? directory = null)
    {
        SaveRecord(request, stream => image.Save(stream, ImageFormat.Png), result, directory ?? DefaultDirectory);
    }

    // Stream boundary also lets packaged tests simulate a partially failed encoder without corrupting a bitmap.
    internal static void SaveRecord(CaptureRequest request, Action<Stream> writeImage, RecognitionResult result, string directory)
    {
        try
        {
            string dir = Path.GetFullPath(directory);
            // The named mutex also serializes separate SITREP instances writing to this local directory.
            string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(dir).ToUpperInvariant())));
            using var mutex = new Mutex(false, @"Local\Sitrep-captures-" + key);
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { return; } // Diagnostics must not indefinitely block OCR/shutdown.
            try { WritePair(dir, request, writeImage, result); }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or System.Runtime.InteropServices.ExternalException)
        {
            // Diagnostics are best effort. A later successful save repairs remnants of interrupted/denied I/O.
            System.Diagnostics.Trace.WriteLine($"Debug capture not saved: {ex.Message}");
        }
    }

    private static void WritePair(string dir, CaptureRequest request, Action<Stream> writeImage, RecognitionResult result)
    {
        Directory.CreateDirectory(dir);
        File.Delete(Path.Combine(dir, "records.log"));
        Prune(dir, 49); // Reserve a whole record before writing: never exceed 50 completed pairs.
        string name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}_{request.Role}_{request.Sequence}_{Guid.NewGuid():N}";
        string image = Path.Combine(dir, name + ".png");
        string metadata = Path.Combine(dir, name + ".json");
        bool committed = false;
        try
        {
            string json = JsonSerializer.Serialize(new
            {
                request,
                result,
                profile = "L81 Apollyon, uncorrected table",
                completedAt = DateTimeOffset.UtcNow,
            });
            using (var stream = new FileStream(image + ".tmp", FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                writeImage(stream);
            }
            File.WriteAllText(metadata + ".tmp", json);
            File.Move(image + ".tmp", image);
            File.Move(metadata + ".tmp", metadata);
            committed = true;
        }
        finally
        {
            // Attempt every cleanup even if one path is locked. No normal failure leaves half a record.
            DeleteBestEffort(image + ".tmp");
            DeleteBestEffort(metadata + ".tmp");
            if (!committed)
            {
                DeleteBestEffort(image);
                DeleteBestEffort(metadata);
            }
        }
    }

    private static void DeleteBestEffort(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"Debug cleanup deferred: {ex.Message}");
        }
    }

    private static void Prune(string dir, int keep)
    {
        foreach (string temp in Directory.EnumerateFiles(dir, "*.tmp")) { File.Delete(temp); }
        var records = new DirectoryInfo(dir).GetFiles()
            .Where(f => f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || f.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => Path.GetFileNameWithoutExtension(f.Name), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Min(f => f.CreationTimeUtc)).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        var pairs = new List<FileInfo[]>();
        foreach (var record in records)
        {
            var files = record.ToArray();
            if (files.Length == 2) { pairs.Add(files); }
            else { foreach (var orphan in files) { orphan.Delete(); } }
        }
        foreach (var pair in pairs.Take(Math.Max(0, pairs.Count - keep)))
        {
            foreach (var file in pair) { file.Delete(); }
        }
    }
}
