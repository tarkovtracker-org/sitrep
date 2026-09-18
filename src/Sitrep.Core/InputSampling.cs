namespace Sitrep.Core;

public readonly record struct InputSample(long Foreground, int X, int Y, bool HasCursor,
    bool Origin, bool Target, bool Clear, bool Middle, bool Control, bool Shift);

public enum InputAction { Origin, Target, Clear }

public readonly record struct InputGesture(InputAction Action, InputSample Sample, long Timestamp, long Epoch)
{
    public DateTimeOffset EventTime { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Single-lock caller-owned sampler/mailbox. No callbacks, UI work, or native input synthesis.</summary>
public sealed class InputSampling
{
    private const int Capacity = 64;
    private readonly Queue<InputGesture> _pending = new();
    private InputSample _previous;
    private bool _seeded;
    private bool _enabled;
    public long Epoch { get; private set; }
    public bool Invalidated { get; private set; }

    public void Reset(bool enabled, InputSample sample)
    {
        _enabled = enabled;
        _previous = sample;
        _seeded = true;
        Invalidate();
    }

    private void Invalidate()
    {
        Epoch++;
        _pending.Clear();
        Invalidated = true;
    }

    public void Sample(InputSample sample, long timestamp)
    {
        if (!_seeded || sample.Foreground != _previous.Foreground)
        {
            Reset(_enabled, sample);
            return;
        }
        bool middle = sample.Middle && !_previous.Middle;
        bool origin = (sample.Origin && !_previous.Origin) || (middle && sample.Control && !sample.Shift);
        bool target = (sample.Target && !_previous.Target) || (middle && sample.Shift && !sample.Control);
        bool clear = sample.Clear && !_previous.Clear;
        _previous = sample; // Freeze all edges/modifiers before any consumer can run synchronous work.
        if (!_enabled) { return; }
        if (clear)
        {
            // Clear is a barrier: never replay older captures after F9, including simultaneous aliases.
            _pending.Clear();
            Epoch++; // Already-running capture/OCR also observes the clear barrier before UI dispatch.
            _pending.Enqueue(new(InputAction.Clear, sample, timestamp, Epoch));
            return;
        }
        if (!origin && !target) { return; }
        if (_pending.Count >= Capacity)
        {
            Invalidate(); // Bound memory and fail closed rather than replay a stale backlog.
            return;
        }
        // Origin wins simultaneous origin/target aliases; one gesture per sample.
        _pending.Enqueue(new(origin ? InputAction.Origin : InputAction.Target, sample, timestamp, Epoch));
    }

    public bool TakeInvalidation()
    {
        bool invalidated = Invalidated;
        Invalidated = false;
        return invalidated;
    }

    public bool TryTake(out InputGesture gesture) => _pending.TryDequeue(out gesture);
}
