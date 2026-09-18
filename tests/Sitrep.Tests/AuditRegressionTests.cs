using Sitrep.Core;
using Sitrep.Desktop;

namespace Sitrep.Tests;

public sealed class AuditRegressionTests
{
    [Theory]
    [InlineData(359.96, "0.0°")]
    [InlineData(359.94, "359.9°")]
    [InlineData(-0.04, "0.0°")]
    [InlineData(360, "0.0°")]
    [InlineData(3.193449, "3.2°")]
    public void BearingDisplayNormalizesAfterRounding(double value, string expected) =>
        Assert.Equal(expected, GeoMath.FormatBearing(value));

    [Theory]
    [InlineData("x101.53 y107.77", "x101.54 y107.77", false)]
    [InlineData("x101.53 y107.77", "x101.53 y107.77", true)]
    [InlineData("x101.53", "y107.77", false)]
    [InlineData("noise.", "x101.53 y107.77", true)]
    [InlineData("x101.53 x101.54 y107.77", "x101.53 y107.77", false)]
    [InlineData("x101.53 y107.77", "x101.53 x101.54 y107.77", false)]
    public void RecipesMustNotDisagreeOrCombineAxes(string a, string b, bool expected)
    {
        Assert.Equal(expected, RecognitionConsensus.TryAccept([a, b], out var coordinate, out var reason));
        if (expected)
        {
            Assert.Equal(new MapCoordinate(101.53, 107.77), coordinate);
        }
        else if (a.Contains("x101.54", StringComparison.Ordinal) || b.Contains("x101.54", StringComparison.Ordinal))
        {
            Assert.Equal("CONFLICTING_RECIPES", reason);
        }
    }

    [Theory]
    [InlineData(true, 100, "", true)]
    [InlineData(true, 101, "", false)]
    [InlineData(false, 0, "NO_COORDINATES", true)]
    [InlineData(false, 0, "CONFLICTING_RECIPES", false)]
    public void RetryCanRecoverMissingLabelsButNeverOverrideConflictingCoordinates(bool firstSuccess, double x, string reason, bool expected)
    {
        var first = new RecognitionResult(firstSuccess, new MapCoordinate(x, 100), "first", 1, reason);
        var second = new RecognitionResult(true, new MapCoordinate(100, 100), "second", 1, "");
        var result = RecognitionResult.Reconcile(first, second);
        Assert.Equal(expected, result.Success);
        if (expected) { Assert.Equal(new MapCoordinate(100, 100), result.Coordinate); }
        else { Assert.Equal("CONFLICTING_SNAPSHOTS_OR_RECIPES", result.RejectionReason); }
    }

    [Fact]
    public void CaptureRejectsClippingAndEveryOwnWindowAtNegativeScreenOrigins()
    {
        var bounds = new CaptureRegion(-1920, -200, 1920, 1080);
        var roi = new CaptureRegion(-1800, 0, 360, 200);
        Assert.True(RoiBuilder.IsSafeCapture(roi, bounds, []));
        Assert.False(RoiBuilder.IsSafeCapture(roi, null, []));
        Assert.False(RoiBuilder.IsSafeCapture(roi with { X = -1950 }, bounds, []));
        foreach (var window in new[] { roi, roi with { X = -1600 }, roi with { Y = 190 } })
        {
            Assert.False(RoiBuilder.IsSafeCapture(roi, bounds, [window]));
        }
    }

    [Fact]
    public void HeldKeysAndDisabledCaptureDoNotCreatePhantomEdges()
    {
        // Reseeding with a key already held must not fire; only a release followed by a fresh press is an edge,
        // and nothing is queued while disabled. (InputSampling replaced the former per-key InputEdges helper.)
        var idle = new InputSample(1, 0, 0, true, false, false, false, false, false, false);
        var sampler = new InputSampling();
        sampler.Reset(true, idle with { Origin = true });
        sampler.Sample(idle with { Origin = true }, 1);
        Assert.False(sampler.TryTake(out _));
        sampler.Sample(idle, 2);
        Assert.False(sampler.TryTake(out _));
        sampler.Sample(idle with { Origin = true }, 3);
        Assert.True(sampler.TryTake(out var pressed));
        Assert.Equal(InputAction.Origin, pressed.Action);
        sampler.Sample(idle with { Origin = true }, 4);
        Assert.False(sampler.TryTake(out _));
        sampler.Reset(false, idle);
        sampler.Sample(idle with { Target = true }, 5);
        Assert.False(sampler.TryTake(out _));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"RoiWidth\":0}")]
    [InlineData("{\"RoiHeight\":2001}")]
    [InlineData("{\"RoiOffsetX\":-2001}")]
    [InlineData("{\"ForegroundTitleContains\":null}")]
    [InlineData("{\"TessDataDir\":null}")]
    public void InvalidConfigIsReportedAndNeverOverwritten(string contents)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, contents);
            var error = Assert.Throws<InvalidDataException>(() => AppConfig.Load(path));
            Assert.Contains(path, error.Message);
            Assert.Contains("Repair the file or rename it", error.Message);
            Assert.Equal(contents, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingConfigDefaultsAndValidConfigRoundTrips()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            Assert.Equal(360, AppConfig.Load(path).RoiWidth);
            Assert.False(File.Exists(path));
            var config = new AppConfig { RoiWidth = 400, ForegroundTitleContains = "local fixture" };
            config.Save(path);
            Assert.Equal(400, AppConfig.Load(path).RoiWidth);
            Assert.Equal("local fixture", AppConfig.Load(path).ForegroundTitleContains);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("{\"weaponId\":\"L81\",\"minRangeMeters\":132.000000001,\"maxRangeMeters\":684,\"samples\":[[132,850],[684,150]]}")]
    [InlineData("{\"weaponId\":\"L81\",\"minRangeMeters\":132,\"maxRangeMeters\":683.999999999,\"samples\":[[132,850],[684,150]]}")]
    [InlineData("{\"weaponId\":\"other\",\"minRangeMeters\":132,\"maxRangeMeters\":684,\"samples\":[[132,850],[684,150]]}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"weaponId\":42}")]
    [InlineData("{\"weaponId\":\"L81\",\"minRangeMeters\":\"132\"}")]
    [InlineData("{\"weaponId\":\"L81\",\"minRangeMeters\":132,\"maxRangeMeters\":684,\"samples\":[1]}")]
    [InlineData("{\"weaponId\":\"L81\",\"minRangeMeters\":132,\"maxRangeMeters\":684,\"samples\":[[]]}")]
    [InlineData("{\"weaponId\":\"L81\",\"minRangeMeters\":132,\"maxRangeMeters\":684,\"samples\":[[132,850,0]]}")]
    [InlineData("{\"weaponId\":\"L81\",\"minRangeMeters\":132,\"maxRangeMeters\":684,\"samples\":[[132,\"850\"]]}")]
    public void WrongJsonTypesFailClosed(string contents)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, contents);
            var (table, error) = FiringTable.LoadApollyonL81(path);
            Assert.Null(table);
            Assert.Equal("CORRUPT_DATA", error);
        }
        finally { File.Delete(path); }
    }
}
