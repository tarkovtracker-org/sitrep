using Sitrep.Core;
using Sitrep.Desktop;

namespace Sitrep.Tests;

public sealed class DesktopHardeningTests
{
    [Fact]
    public void TrayFailureRetryRestartAndShutdownAreIdempotent()
    {
        int adds = 0, deletes = 0;
        bool succeeds = false;
        var tray = new TrayRegistration(() => { adds++; return succeeds; }, () => deletes++);
        Assert.False(tray.EnsureRegistered());
        Assert.False(tray.IsRegistered); // Minimize must remain on the taskbar.
        succeeds = true;
        Assert.True(tray.EnsureRegistered());
        Assert.True(tray.EnsureRegistered());
        Assert.Equal(2, adds);
        Assert.True(tray.ShellRestarted());
        Assert.Equal(3, adds);
        Assert.Equal(0, deletes);
        tray.Dispose();
        tray.Dispose();
        Assert.False(tray.ShellRestarted());
        Assert.False(tray.EnsureRegistered());
        Assert.Equal(3, adds);
        Assert.Equal(1, deletes);
    }

    [Fact]
    public void FailedShellRecoveryCannotAuthorizeHiding()
    {
        bool succeeds = true;
        using var tray = new TrayRegistration(() => succeeds, () => { });
        Assert.True(tray.EnsureRegistered());
        succeeds = false;
        Assert.False(tray.ShellRestarted());
        Assert.False(tray.IsRegistered);
    }

    private sealed class Identity(string name) : IProcessIdentity
    {
        public string Name => name;
        public bool IsAlive { get; set; } = true;
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }

    [Fact]
    public void PidReuseRequiresNewIdentityButLiveHitsNeverReopen()
    {
        int opens = 0;
        var old = new Identity("wardogs");
        var replacement = new Identity("other");
        using var cache = new ProcessIdentityCache(_ => ++opens == 1 ? old : replacement, () => 0);
        for (int i = 0; i < 1000; i++) { Assert.Equal("wardogs", cache.GetName(42)); }
        Assert.Equal(1, opens);
        old.IsAlive = false;
        Assert.Equal("other", cache.GetName(42));
        Assert.Equal(2, opens);
        Assert.Equal(1, old.Disposals);
        cache.Dispose();
        Assert.Equal(1, replacement.Disposals);
    }

    [Fact]
    public void DeniedLookupIsThrottledAndRecoversWithoutLeakingOldName()
    {
        long time = 0;
        int opens = 0;
        var old = new Identity("wardogs");
        using var cache = new ProcessIdentityCache(_ => ++opens == 1 ? old : null, () => time);
        Assert.Equal("wardogs", cache.GetName(1));
        Assert.Equal(string.Empty, cache.GetName(2));
        for (int i = 0; i < 100; i++) { Assert.Equal(string.Empty, cache.GetName(2)); }
        Assert.Equal(2, opens);
        Assert.Equal(1, old.Disposals);
        time = 1000;
        Assert.Equal(string.Empty, cache.GetName(2));
        Assert.Equal(3, opens);
        Assert.Equal(string.Empty, cache.GetName(0));
        Assert.Equal(3, opens);
    }

    [Fact]
    public void IdentityThatIsAlreadyDeadOnOpenIsDisposedAndThrottledLikeADeniedLookup()
    {
        // Exit between open and check, or a handle whose zero-time wait is denied, must not requery at input rate.
        long time = 0;
        int opens = 0;
        var dead = new Identity("wardogs") { IsAlive = false };
        var live = new Identity("wardogs");
        using var cache = new ProcessIdentityCache(_ => ++opens == 1 ? dead : live, () => time);
        Assert.Equal(string.Empty, cache.GetName(42));
        Assert.Equal(1, dead.Disposals);
        for (int i = 0; i < 100; i++) { Assert.Equal(string.Empty, cache.GetName(42)); }
        Assert.Equal(1, opens);
        time = 1000;
        Assert.Equal("wardogs", cache.GetName(42));
        Assert.Equal(2, opens);
        Assert.Equal(0, live.Disposals);
    }

    private static InputSample Idle => new(1, -400, 500, true, false, false, false, false, false, false);

    private static InputSampling Enabled()
    {
        var sampler = new InputSampling();
        sampler.Reset(true, Idle);
        sampler.TakeInvalidation();
        return sampler;
    }

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, true, null)]
    [InlineData(true, false, InputAction.Origin)]
    [InlineData(false, true, InputAction.Target)]
    public void ChordUsesModifiersAtMiddleDownNotAtDispatch(bool control, bool shift, InputAction? expected)
    {
        var sampler = Enabled();
        sampler.Sample(Idle with { Middle = true, Control = control, Shift = shift }, 10);
        sampler.Sample(Idle, 20); // Both buttons and modifiers released before UI work finishes.
        Assert.Equal(expected.HasValue, sampler.TryTake(out var gesture));
        if (expected.HasValue)
        {
            Assert.Equal(expected, gesture.Action);
            Assert.Equal(10, gesture.Timestamp);
            Assert.Equal(-400, gesture.Sample.X);
            Assert.Equal(control, gesture.Sample.Control);
        }
        Assert.False(sampler.TryTake(out _));
    }

    [Fact]
    public void ModifierChangesWhileMiddleHeldNeverCreateAnotherEdge()
    {
        var sampler = Enabled();
        sampler.Sample(Idle with { Middle = true }, 1);
        sampler.Sample(Idle with { Middle = true, Control = true }, 2);
        sampler.Sample(Idle with { Middle = true, Shift = true }, 3);
        Assert.False(sampler.TryTake(out _));
        sampler.Sample(Idle, 4);
        sampler.Sample(Idle with { Middle = true, Shift = true }, 5);
        Assert.True(sampler.TryTake(out var target));
        Assert.Equal(InputAction.Target, target.Action);
    }

    [Fact]
    public void AliasesDeduplicateAndClearIsABarrier()
    {
        var sampler = Enabled();
        sampler.Sample(Idle with { Origin = true, Middle = true, Control = true }, 1);
        Assert.True(sampler.TryTake(out var origin));
        Assert.Equal(InputAction.Origin, origin.Action);
        Assert.False(sampler.TryTake(out _));
        sampler.Sample(Idle, 2);
        sampler.Sample(Idle with { Target = true }, 3);
        long epoch = sampler.Epoch;
        sampler.Sample(Idle with { Clear = true, Origin = true }, 4);
        Assert.True(sampler.Epoch > epoch);
        Assert.True(sampler.TryTake(out var clear));
        Assert.Equal(InputAction.Clear, clear.Action);
        Assert.False(sampler.TryTake(out _));
    }

    [Fact]
    public void FocusRoundTripAndDisableInvalidateBacklogAndReseedHeldKeys()
    {
        var sampler = Enabled();
        sampler.Sample(Idle with { Origin = true }, 1);
        long epoch = sampler.Epoch;
        sampler.Sample(Idle with { Foreground = 2 }, 2);
        sampler.Sample(Idle with { Origin = true }, 3);
        Assert.True(sampler.Epoch > epoch);
        Assert.True(sampler.TakeInvalidation());
        Assert.False(sampler.TryTake(out _));
        sampler.Reset(false, Idle);
        sampler.Sample(Idle with { Target = true }, 4);
        sampler.Reset(true, Idle with { Target = true });
        sampler.Sample(Idle with { Target = true }, 5);
        Assert.False(sampler.TryTake(out _));
        sampler.Sample(Idle, 6);
        sampler.Sample(Idle with { Target = true }, 7);
        Assert.True(sampler.TryTake(out var target));
        Assert.Equal(InputAction.Target, target.Action);
    }

    [Fact]
    public void FrozenEventTimeSurvivesDelayedStateRequests()
    {
        var state = new AssistantState(null);
        var eventTime = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        var origin = state.BeginOrigin(1, 2, 3, 4, 5, 40, 40, eventTime);
        Assert.Equal(eventTime, origin.EventTime);
        state.Complete(new OcrCompletion(origin.Sequence, origin.Generation, origin.Role, origin.OriginRevision,
            true, new MapCoordinate(100, 100), "", ""));
        var (target, _) = state.BeginTarget(1, 2, 3, 4, 5, 40, 40, eventTime.AddSeconds(1));
        Assert.Equal(eventTime.AddSeconds(1), target!.EventTime);
    }

    [Fact]
    public void StalledConsumerHasBoundedBacklogAndOverflowFailsClosed()
    {
        var sampler = Enabled();
        long epoch = sampler.Epoch;
        for (int i = 0; i < 65; i++)
        {
            sampler.Sample(Idle with { Origin = true }, i * 2);
            sampler.Sample(Idle, i * 2 + 1);
        }
        Assert.True(sampler.Epoch > epoch);
        Assert.True(sampler.TakeInvalidation());
        Assert.False(sampler.TryTake(out _));
    }
}
