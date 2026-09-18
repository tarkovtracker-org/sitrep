using Sitrep.Core;

namespace Sitrep.Tests;

public sealed class ClearInputPolicyTests
{
    [Theory]
    [InlineData(1, 1, 1, true, 2, true, true)] // Attached, still allowed game.
    [InlineData(2, 2, 1, false, 2, true, true)] // Exact control HWND, not any owned window.
    [InlineData(3, 3, 1, false, 2, true, false)] // Overlay / unrelated window.
    [InlineData(3, 2, 1, false, 2, true, false)] // Unrelated sample, now control.
    [InlineData(3, 1, 1, true, 2, true, false)] // Unrelated sample, now game.
    [InlineData(1, 2, 1, true, 2, true, false)] // Focus changed before next sampler poll.
    [InlineData(1, 1, 1, false, 2, true, false)] // Attached but no longer allowed.
    [InlineData(4, 4, 1, true, 2, true, false)] // Another matching game cannot inherit attachment.
    [InlineData(1, 1, 1, true, 2, false, false)] // Sampled focus round-trip / disable.
    [InlineData(2, 2, 1, false, 2, false, false)]
    [InlineData(0, 0, 0, true, 0, true, false)]
    public void AuthorizesOnlyFreshSampledGameOrExactControl(long sampled, long current, long attached,
        bool allowed, long control, bool fresh, bool expected)
    {
        var sample = new InputSample(sampled, 10, 20, true, false, false, true, false, false, false);
        var gesture = new InputGesture(InputAction.Clear, sample, 42, 7);
        Assert.Equal(expected, ClearInputPolicy.IsAllowed(gesture, fresh, current, attached, allowed, control));
        Assert.False(ClearInputPolicy.IsAllowed(gesture with { Action = InputAction.Origin }, fresh,
            current, attached, allowed, control));
    }

    [Fact]
    public void FocusRoundTripDropsQueuedClearAndCannotClearLaterOrigin()
    {
        var idle = new InputSample(1, 0, 0, true, false, false, false, false, false, false);
        var sampling = new InputSampling();
        sampling.Reset(true, idle);
        sampling.Sample(idle with { Clear = true }, 1);
        Assert.True(sampling.TryTake(out var stale));
        sampling.Sample(idle, 2);
        sampling.Sample(idle with { Clear = true }, 3); // A second clear is still queued.
        sampling.Sample(idle with { Foreground = 2 }, 4);
        sampling.Sample(idle, 5);
        Assert.False(sampling.TryTake(out _));
        Assert.NotEqual(stale.Epoch, sampling.Epoch);
        var state = new AssistantState(null);
        var origin = state.BeginOrigin(1, 0, 0, 0, 0, 40, 40);
        Assert.True(state.Complete(new OcrCompletion(origin.Sequence, origin.Generation, origin.Role,
            origin.OriginRevision, true, new MapCoordinate(100, 100), "", "")));
        if (ClearInputPolicy.IsAllowed(stale, stale.Epoch == sampling.Epoch, 1, 1, true, 2)) { state.Clear(); }
        Assert.Equal(new MapCoordinate(100, 100), state.ConfirmedOrigin);
    }

    [Fact]
    public void AuthorizedClearDiscardsPendingCompletion()
    {
        var state = new AssistantState(null);
        var pending = state.BeginOrigin(1, 0, 0, 0, 0, 40, 40);
        var sample = new InputSample(1, 0, 0, true, false, false, true, false, false, false);
        Assert.True(ClearInputPolicy.IsAllowed(new InputGesture(InputAction.Clear, sample, 1, 1), true, 1, 1, true, 2));
        state.Clear();
        Assert.False(state.Complete(new OcrCompletion(pending.Sequence, pending.Generation, pending.Role,
            pending.OriginRevision, true, new MapCoordinate(100, 100), "", "")));
        Assert.Null(state.ConfirmedOrigin);
        Assert.Null(state.Pending);
    }
}
