namespace Sitrep.Core;

public readonly record struct CaptureRegion(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public static class RoiBuilder
{
    public const int DefaultWidth = 360;
    public const int DefaultHeight = 200;
    public const int DefaultOffsetX = -40;
    public const int DefaultOffsetBottom = 24;
    public const int DefaultOffsetTop = 176;

    public static CaptureRegion BuildCursorRelative(int cursorX, int cursorY, int width = DefaultWidth, int height = DefaultHeight, int offsetX = DefaultOffsetX, int offsetTop = DefaultOffsetTop)
    {
        int x = cursorX + offsetX;
        int y = cursorY - offsetTop;
        return new CaptureRegion(x, y, width, height);
    }

    public static CaptureRegion? Intersect(CaptureRegion roi, CaptureRegion bounds)
    {
        int x1 = Math.Max(roi.X, bounds.X);
        int y1 = Math.Max(roi.Y, bounds.Y);
        int x2 = Math.Min(roi.X + roi.Width, bounds.X + bounds.Width);
        int y2 = Math.Min(roi.Y + roi.Height, bounds.Y + bounds.Height);
        if (x2 <= x1 || y2 <= y1)
        {
            return null;
        }
        return new CaptureRegion(x1, y1, x2 - x1, y2 - y1);
    }

    public static bool IsSafeCapture(CaptureRegion roi, CaptureRegion? bounds, IReadOnlyList<CaptureRegion> exclude) =>
        !roi.IsEmpty && bounds.HasValue && Intersect(roi, bounds.Value) == roi
        && !exclude.Any(ex => Overlaps(roi, ex));

    public static bool Overlaps(CaptureRegion a, CaptureRegion b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    /// <summary>Map square height as a fraction of the game client height (880 px on a 2560x1440 reference capture).</summary>
    public const double MapHeightFraction = 0.611;

    /// <summary>Centered square map area of the game client, in client-relative pixels.</summary>
    public static CaptureRegion GetCenteredMapRegion(int clientWidth, int clientHeight)
    {
        if (clientWidth <= 0 || clientHeight <= 0)
        {
            return new CaptureRegion(0, 0, 0, 0);
        }
        int size = (int)Math.Round(clientHeight * MapHeightFraction);
        return new CaptureRegion((clientWidth - size) / 2, (clientHeight - size) / 2, size, size);
    }

    public static bool IsPointInsideMap(int clientX, int clientY, int clientWidth, int clientHeight)
    {
        var map = GetCenteredMapRegion(clientWidth, clientHeight);
        return !map.IsEmpty && clientX >= map.X && clientX < map.X + map.Width && clientY >= map.Y && clientY < map.Y + map.Height;
    }
}
