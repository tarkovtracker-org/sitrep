namespace Sitrep.Core;

public enum CaptureRole
{
    Origin,
    Target
}

public sealed record CaptureRequest(
    long Sequence,
    int Generation,
    CaptureRole Role,
    int OriginRevision,
    long ForegroundHwnd,
    DateTimeOffset EventTime,
    int CursorX,
    int CursorY,
    int RoiX,
    int RoiY,
    int RoiWidth,
    int RoiHeight);

public sealed record OcrCompletion(
    long Sequence,
    int Generation,
    CaptureRole Role,
    int OriginRevision,
    bool Success,
    MapCoordinate Coordinate,
    string RawText,
    string RejectionReason);

public static class DisplayStatuses
{
    public const string SetMortar = "SET MORTAR (CTRL+MMB)";
    public const string Reading = "READING";
    public const string OriginSet = "ORIGIN SET—AWAITING TARGET";
    public const string Ready = "READY";
    public const string Disabled = "DISABLED";
    public const string WindowLost = "WINDOW LOST—RETARGET";
    public const string Moved = "MOVED—TRY AGAIN";
    public const string TableUnavailable = "TABLE UNVERIFIED/UNAVAILABLE";
    public const string OutOfRange = "OUT OF RANGE";
    public const string OutsideMap = "OUTSIDE MAP AREA";

    public static string TargetFailed(string reason) =>
        string.IsNullOrWhiteSpace(reason) ? "TARGET OCR FAILED" : $"TARGET OCR FAILED: {reason}";

    public static string OriginFailed(string reason) =>
        string.IsNullOrWhiteSpace(reason) ? "ORIGIN OCR FAILED—RETRY CTRL+MMB" : $"ORIGIN OCR FAILED: {reason}—RETRY CTRL+MMB";
}

public sealed class AssistantState
{
    private readonly object _gate = new();
    private long _nextSequence = 1;
    private int _generation;
    private int _originRevision;

    public bool LiveEnabled { get; private set; }
    public MapCoordinate? ConfirmedOrigin { get; private set; }
    public MapCoordinate? ActiveTarget { get; private set; }
    public CaptureRequest? Pending { get; private set; }
    public double? RangeMeters { get; private set; }
    public double? BearingDegrees { get; private set; }
    public double? ElevationMil { get; private set; }
    public string Status { get; private set; } = DisplayStatuses.SetMortar;
    public FiringTable? Table { get; private set; }

    public int Generation => _generation;
    public int OriginRevision => _originRevision;

    public AssistantState(FiringTable? table, bool liveEnabled = true)
    {
        Table = table;
        LiveEnabled = liveEnabled;
    }

    public void SetTable(FiringTable? table)
    {
        lock (_gate)
        {
            Table = table;
        }
    }

    public void SetLiveEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (LiveEnabled == enabled)
            {
                return;
            }
            LiveEnabled = enabled;
            _generation++;
            Pending = null;
            ActiveTarget = null;
            RangeMeters = null;
            BearingDegrees = null;
            ElevationMil = null;
            Status = enabled
                ? (ConfirmedOrigin.HasValue ? DisplayStatuses.OriginSet : DisplayStatuses.SetMortar)
                : DisplayStatuses.Disabled;
        }
    }

    public CaptureRequest BeginOrigin(long hwnd, int cursorX, int cursorY, int roiX, int roiY, int roiW, int roiH, DateTimeOffset? eventTime = null)
    {
        lock (_gate)
        {
            _originRevision++;
            _generation++;
            ConfirmedOrigin = null;
            ActiveTarget = null;
            RangeMeters = null;
            BearingDegrees = null;
            ElevationMil = null;
            Status = DisplayStatuses.Reading;
            var req = new CaptureRequest(_nextSequence++, _generation, CaptureRole.Origin, _originRevision, hwnd, eventTime ?? DateTimeOffset.UtcNow, cursorX, cursorY, roiX, roiY, roiW, roiH);
            Pending = req;
            return req;
        }
    }

    public (CaptureRequest? Request, string Status) BeginTarget(long hwnd, int cursorX, int cursorY, int roiX, int roiY, int roiW, int roiH, DateTimeOffset? eventTime = null)
    {
        lock (_gate)
        {
            if (!ConfirmedOrigin.HasValue)
            {
                return (null, DisplayStatuses.SetMortar);
            }
            _generation++;
            ActiveTarget = null;
            RangeMeters = null;
            BearingDegrees = null;
            ElevationMil = null;
            Status = DisplayStatuses.Reading;
            var req = new CaptureRequest(_nextSequence++, _generation, CaptureRole.Target, _originRevision, hwnd, eventTime ?? DateTimeOffset.UtcNow, cursorX, cursorY, roiX, roiY, roiW, roiH);
            Pending = req;
            return (req, Status);
        }
    }

    public bool Complete(OcrCompletion completion)
    {
        lock (_gate)
        {
            if (Pending is null)
            {
                return false;
            }
            if (completion.Generation != Pending.Generation || completion.Sequence != Pending.Sequence || completion.Role != Pending.Role)
            {
                return false;
            }
            if (completion.Role == CaptureRole.Origin && completion.OriginRevision != Pending.OriginRevision)
            {
                return false;
            }
            if (completion.Role == CaptureRole.Target)
            {
                if (!ConfirmedOrigin.HasValue || completion.OriginRevision != _originRevision)
                {
                    return false;
                }
            }
            Pending = null;
            if (completion.Role == CaptureRole.Origin)
            {
                if (!completion.Success)
                {
                    ConfirmedOrigin = null;
                    ActiveTarget = null;
                    RangeMeters = null;
                    BearingDegrees = null;
                    ElevationMil = null;
                    Status = FailureStatus(completion.RejectionReason, DisplayStatuses.OriginFailed);
                    return true;
                }
                ConfirmedOrigin = completion.Coordinate;
                ActiveTarget = null;
                RangeMeters = null;
                BearingDegrees = null;
                ElevationMil = null;
                Status = DisplayStatuses.OriginSet;
                return true;
            }
            else
            {
                if (!completion.Success)
                {
                    ActiveTarget = null;
                    RangeMeters = null;
                    BearingDegrees = null;
                    ElevationMil = null;
                    Status = FailureStatus(completion.RejectionReason, DisplayStatuses.TargetFailed);
                    return true;
                }
                if (!ConfirmedOrigin.HasValue)
                {
                    Status = DisplayStatuses.SetMortar;
                    return true;
                }
                if (!GeoMath.TryCompute(ConfirmedOrigin.Value, completion.Coordinate, out double range, out double bearing, out _))
                {
                    ActiveTarget = null;
                    RangeMeters = null;
                    BearingDegrees = null;
                    ElevationMil = null;
                    Status = DisplayStatuses.TargetFailed("BAD_GEOMETRY");
                    return true;
                }
                ActiveTarget = completion.Coordinate;
                RangeMeters = range;
                BearingDegrees = bearing;
                if (Table is null)
                {
                    ElevationMil = null;
                    Status = DisplayStatuses.TableUnavailable;
                    return true;
                }
                if (!Table.IsInWeaponLimits(range))
                {
                    ElevationMil = null;
                    Status = DisplayStatuses.OutOfRange;
                    return true;
                }
                if (!Table.TryInterpolate(range, out double mil, out string tableReason))
                {
                    ElevationMil = null;
                    Status = tableReason == "OUT_OF_RANGE" ? DisplayStatuses.OutOfRange : DisplayStatuses.TableUnavailable;
                    return true;
                }
                ElevationMil = mil;
                Status = DisplayStatuses.Ready;
                return true;
            }
        }
    }

    // MOVED and OUTSIDE MAP AREA are pre-OCR rejections with their own display text; everything else is an OCR failure.
    private static string FailureStatus(string reason, Func<string, string> ocrFailed) =>
        reason is DisplayStatuses.Moved or DisplayStatuses.OutsideMap ? reason : ocrFailed(reason);

    public void Clear()
    {
        lock (_gate)
        {
            _generation++;
            _originRevision++;
            Pending = null;
            ConfirmedOrigin = null;
            ActiveTarget = null;
            RangeMeters = null;
            BearingDegrees = null;
            ElevationMil = null;
            Status = DisplayStatuses.SetMortar;
        }
    }

    public void OnForegroundLost()
    {
        lock (_gate)
        {
            _generation++;
            Pending = null;
            ActiveTarget = null;
            RangeMeters = null;
            BearingDegrees = null;
            ElevationMil = null;
            Status = DisplayStatuses.WindowLost;
        }
    }

    public void OnGameWindowClosed()
    {
        lock (_gate)
        {
            _generation++;
            _originRevision++;
            Pending = null;
            ConfirmedOrigin = null;
            ActiveTarget = null;
            RangeMeters = null;
            BearingDegrees = null;
            ElevationMil = null;
            Status = DisplayStatuses.SetMortar;
        }
    }
}
