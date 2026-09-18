using Sitrep.Core;

namespace Sitrep.Tests;

public sealed class StateTests
{
    private static FiringTable SmallTable()
    {
        var (t, e) = FiringTable.TryCreate("L81", 100, 600,
            [new FiringSample(100, 800), new FiringSample(300, 600), new FiringSample(600, 300)]);
        Assert.Null(e);
        return t!;
    }

    private static OcrCompletion Ok(CaptureRequest req, MapCoordinate c) =>
        new(req.Sequence, req.Generation, req.Role, req.OriginRevision, true, c, $"x{c.X:0.00} y{c.Y:0.00}", string.Empty);

    private static OcrCompletion Fail(CaptureRequest req, string reason) =>
        new(req.Sequence, req.Generation, req.Role, req.OriginRevision, false, default, "garbage", reason);

    [Fact]
    public void LiveIsEnabledByDefaultSoNoManualActivationIsNeeded()
    {
        var s = new AssistantState(SmallTable());
        Assert.True(s.LiveEnabled);
        Assert.Equal(DisplayStatuses.SetMortar, s.Status);
        Assert.NotNull(s.BeginOrigin(1, 0, 0, 0, 0, 10, 10));
    }

    [Fact]
    public void OutsideMapOriginClearsUsableOriginWithSpecificStatus()
    {
        var s = new AssistantState(SmallTable());
        s.Complete(Ok(s.BeginOrigin(1, 0, 0, 0, 0, 10, 10), new MapCoordinate(100, 100)));
        var retry = s.BeginOrigin(1, 0, 0, 0, 0, 10, 10);
        Assert.True(s.Complete(Fail(retry, DisplayStatuses.OutsideMap)));
        Assert.Equal(DisplayStatuses.OutsideMap, s.Status);
        Assert.Null(s.ConfirmedOrigin);
        Assert.Null(s.Pending);
    }

    [Fact]
    public void OutsideMapTargetKeepsOriginAndDropsSolution()
    {
        var s = new AssistantState(SmallTable());
        s.Complete(Ok(s.BeginOrigin(1, 0, 0, 0, 0, 10, 10), new MapCoordinate(100, 100)));
        s.Complete(Ok(s.BeginTarget(1, 0, 0, 0, 0, 10, 10).Request!, new MapCoordinate(101, 102)));
        Assert.Equal(DisplayStatuses.Ready, s.Status);
        var (bad, _) = s.BeginTarget(1, 0, 0, 0, 0, 10, 10);
        Assert.True(s.Complete(Fail(bad!, DisplayStatuses.OutsideMap)));
        Assert.Equal(DisplayStatuses.OutsideMap, s.Status);
        Assert.NotNull(s.ConfirmedOrigin);
        Assert.Null(s.ActiveTarget);
        Assert.Null(s.ElevationMil);
    }

    [Fact]
    public void TargetWithoutOriginDoesNotEnqueue()
    {
        var s = new AssistantState(SmallTable());
        var (req, status) = s.BeginTarget(1, 0, 0, 0, 0, 10, 10);
        Assert.Null(req);
        Assert.Null(s.Pending);
        Assert.Equal(DisplayStatuses.SetMortar, status);
    }

    [Fact]
    public void OriginSuccessThenTargetSuccessProducesSolution()
    {
        var s = new AssistantState(SmallTable());
        var oReq = s.BeginOrigin(1, 0, 0, 0, 0, 10, 10);
        Assert.Equal(DisplayStatuses.Reading, s.Status);
        Assert.True(s.Complete(Ok(oReq, new MapCoordinate(100, 100))));
        Assert.Equal(DisplayStatuses.OriginSet, s.Status);
        var (tReq, _) = s.BeginTarget(1, 0, 0, 0, 0, 10, 10);
        Assert.NotNull(tReq);
        Assert.Equal(DisplayStatuses.Reading, s.Status);
        Assert.True(s.Complete(Ok(tReq!, new MapCoordinate(101, 102))));
        Assert.Equal(DisplayStatuses.Ready, s.Status);
        Assert.NotNull(s.RangeMeters);
        Assert.NotNull(s.BearingDegrees);
        Assert.NotNull(s.ElevationMil);
    }

    [Fact]
    public void OldCompletionCannotCommitAfterNewTarget()
    {
        var s = new AssistantState(SmallTable());
        var oReq = s.BeginOrigin(1, 0, 0, 0, 0, 10, 10);
        s.Complete(Ok(oReq, new MapCoordinate(100, 100)));
        var (first, _) = s.BeginTarget(1, 0, 0, 0, 0, 10, 10);
        var (second, _) = s.BeginTarget(1, 0, 0, 0, 0, 10, 10);
        Assert.False(s.Complete(Ok(first!, new MapCoordinate(101, 101))));
        Assert.True(s.Complete(Ok(second!, new MapCoordinate(102, 103))));
        Assert.Equal(102.0, s.ActiveTarget!.Value.X, 9);
    }

    [Fact]
    public void OriginChangeInvalidatesPendingTarget()
    {
        var s = new AssistantState(SmallTable());
        var oReq = s.BeginOrigin(1, 0, 0, 0, 0, 10, 10);
        s.Complete(Ok(oReq, new MapCoordinate(100, 100)));
        var (tReq, _) = s.BeginTarget(1, 0, 0, 0, 0, 10, 10);
        s.BeginOrigin(1, 0, 0, 0, 0, 10, 10);
        Assert.False(s.Complete(Ok(tReq!, new MapCoordinate(101, 101))));
        Assert.Equal(DisplayStatuses.Reading, s.Status);
    }

    [Fact]
    public void TargetDuringOriginReplacementCannotUseOldOriginOrSupersedeReplacement()
    {
        var s = new AssistantState(SmallTable());
        var first = s.BeginOrigin(1, 0, 0, 0, 0, 10, 10);
        Assert.True(s.Complete(Ok(first, new MapCoordinate(100, 100))));
        var replacement = s.BeginOrigin(1, 50, 60, 10, 20, 30, 40);
        Assert.Null(s.ConfirmedOrigin);
        var (target, status) = s.BeginTarget(1, 0, 0, 0, 0, 10, 10);
        Assert.Null(target);
        Assert.Equal(DisplayStatuses.SetMortar, status);
        Assert.Same(replacement, s.Pending);
        Assert.Equal(DisplayStatuses.Reading, s.Status);
        Assert.True(s.Complete(Ok(replacement, new MapCoordinate(200, 200))));
        Assert.Equal(new MapCoordinate(200, 200), s.ConfirmedOrigin);
        Assert.Null(s.ElevationMil);
    }

    [Theory]
    [InlineData(CaptureRole.Origin)]
    [InlineData(CaptureRole.Target)]
    public void WindowClosureDiscardsCompletionAndOrigin(CaptureRole role)
    {
        var s = new AssistantState(SmallTable());
        var req = s.BeginOrigin(1, 0, 0, 0, 0, 10, 10);
        if (role == CaptureRole.Target)
        {
            s.Complete(Ok(req, new MapCoordinate(100, 100)));
            req = s.BeginTarget(1, 0, 0, 0, 0, 10, 10).Request!;
        }
        s.OnGameWindowClosed();
        Assert.False(s.Complete(Ok(req, new MapCoordinate(101, 102))));
        Assert.Null(s.ConfirmedOrigin);
        Assert.Null(s.Pending);
        Assert.Null(s.ElevationMil);
    }

    [Fact]
    public void OriginFailureRequiresFreshF8()
    {
        var s = new AssistantState(SmallTable());
        var o1 = s.BeginOrigin(1, 0, 0, 0, 0, 10, 10);
        s.Complete(Ok(o1, new MapCoordinate(100, 100)));
        var o2 = s.BeginOrigin(1, 0, 0, 0, 0, 10, 10);
        s.Complete(Fail(o2, "NO_COORDINATES"));
        Assert.Null(s.ConfirmedOrigin);
        var (tReq, status) = s.BeginTarget(1, 0, 0, 0, 0, 10, 10);
        Assert.Null(tReq);
        Assert.Equal(DisplayStatuses.SetMortar, status);
    }

    [Fact]
    public void TargetFailurePreservesOrigin()
    {
        var s = new AssistantState(SmallTable());
        var oReq = s.BeginOrigin(1, 0, 0, 0, 0, 10, 10);
        s.Complete(Ok(oReq, new MapCoordinate(100, 100)));
        var (tReq, _) = s.BeginTarget(1, 0, 0, 0, 0, 10, 10);
        s.Complete(Fail(tReq!, "NO_COORDINATES"));
        Assert.NotNull(s.ConfirmedOrigin);
        Assert.Null(s.ActiveTarget);
        Assert.StartsWith("TARGET OCR FAILED", s.Status);
    }

    [Theory]
    [InlineData(CaptureRole.Origin)]
    [InlineData(CaptureRole.Target)]
    public void MovementDisplaysSpecificStatusWithoutActionableSolution(CaptureRole role)
    {
        var s = new AssistantState(SmallTable());
        var origin = s.BeginOrigin(1, 0, 0, 0, 0, 10, 10);
        s.Complete(Ok(origin, new MapCoordinate(100, 100)));
        var request = role == CaptureRole.Origin
            ? s.BeginOrigin(1, 0, 0, 0, 0, 10, 10)
            : s.BeginTarget(1, 0, 0, 0, 0, 10, 10).Request!;
        Assert.True(s.Complete(Fail(request, DisplayStatuses.Moved)));
        Assert.Equal(DisplayStatuses.Moved, s.Status);
        Assert.Equal(role == CaptureRole.Target, s.ConfirmedOrigin.HasValue);
        Assert.Null(s.Pending);
        Assert.Null(s.ElevationMil);
    }

    [Fact]
    public void ClearDiscardsPending()
    {
        var s = new AssistantState(SmallTable());
        var oReq = s.BeginOrigin(1, 0, 0, 0, 0, 10, 10);
        s.Clear();
        Assert.False(s.Complete(Ok(oReq, new MapCoordinate(100, 100))));
        Assert.Null(s.ConfirmedOrigin);
        Assert.Equal(DisplayStatuses.SetMortar, s.Status);
    }

    [Fact]
    public void DisableDiscardsPending()
    {
        var s = new AssistantState(SmallTable(), liveEnabled: true);
        var oReq = s.BeginOrigin(1, 0, 0, 0, 0, 10, 10);
        s.SetLiveEnabled(false);
        Assert.False(s.Complete(Ok(oReq, new MapCoordinate(100, 100))));
        Assert.Equal(DisplayStatuses.Disabled, s.Status);
    }

    [Fact]
    public void ForegroundLostKeepsOriginButDropsTargetWork()
    {
        var s = new AssistantState(SmallTable());
        var oReq = s.BeginOrigin(1, 0, 0, 0, 0, 10, 10);
        s.Complete(Ok(oReq, new MapCoordinate(100, 100)));
        var (tReq, _) = s.BeginTarget(1, 0, 0, 0, 0, 10, 10);
        s.OnForegroundLost();
        Assert.NotNull(s.ConfirmedOrigin);
        Assert.False(s.Complete(Ok(tReq!, new MapCoordinate(101, 101))));
        Assert.Equal(DisplayStatuses.WindowLost, s.Status);
    }

    [Fact]
    public void OutOfRangeShowsRangeBearingWithoutElevation()
    {
        var s = new AssistantState(SmallTable());
        var oReq = s.BeginOrigin(1, 0, 0, 0, 0, 10, 10);
        s.Complete(Ok(oReq, new MapCoordinate(100, 100)));
        var (tReq, _) = s.BeginTarget(1, 0, 0, 0, 0, 10, 10);
        s.Complete(Ok(tReq!, new MapCoordinate(200, 200)));
        Assert.Equal(DisplayStatuses.OutOfRange, s.Status);
        Assert.NotNull(s.RangeMeters);
        Assert.NotNull(s.BearingDegrees);
        Assert.Null(s.ElevationMil);
    }

    [Fact]
    public void MissingTableBlocksElevation()
    {
        var s = new AssistantState(null);
        var oReq = s.BeginOrigin(1, 0, 0, 0, 0, 10, 10);
        s.Complete(Ok(oReq, new MapCoordinate(100, 100)));
        var (tReq, _) = s.BeginTarget(1, 0, 0, 0, 0, 10, 10);
        s.Complete(Ok(tReq!, new MapCoordinate(101, 101)));
        Assert.Equal(DisplayStatuses.TableUnavailable, s.Status);
        Assert.Null(s.ElevationMil);
    }
}
