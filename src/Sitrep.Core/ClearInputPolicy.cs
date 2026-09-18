namespace Sitrep.Core;

/// <summary>Authorize F9 from its frozen sample, never from a later focus change into SITREP.</summary>
public static class ClearInputPolicy
{
    public static bool IsAllowed(InputGesture gesture, bool isCurrent, long foreground,
        long attachedWindow, bool attachedAllowed, long controlWindow) =>
        gesture.Action == InputAction.Clear && isCurrent && gesture.Sample.Foreground != 0
        && gesture.Sample.Foreground == foreground
        && (gesture.Sample.Foreground == controlWindow
            || (gesture.Sample.Foreground == attachedWindow && attachedAllowed));
}
