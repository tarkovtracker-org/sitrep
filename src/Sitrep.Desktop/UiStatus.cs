using System.Windows;
using System.Windows.Media;
using Sitrep.Core;

namespace Sitrep.Desktop;

/// <summary>Visual severity bucket for a solution-state status, shared by the control window and the overlay.</summary>
public enum StatusLevel
{
    Neutral,
    Info,
    Working,
    Success,
    Warning,
    Error
}

public static class StatusLevelMapper
{
    public static StatusLevel ForStatus(string status)
    {
        if (status == DisplayStatuses.Ready)
        {
            return StatusLevel.Success;
        }
        if (status == DisplayStatuses.Reading)
        {
            return StatusLevel.Working;
        }
        if (status == DisplayStatuses.WindowLost || status == DisplayStatuses.Moved || status == DisplayStatuses.OutsideMap)
        {
            return StatusLevel.Warning;
        }
        if (status == DisplayStatuses.Disabled)
        {
            return StatusLevel.Neutral;
        }
        if (status == DisplayStatuses.OutOfRange
            || status == DisplayStatuses.TableUnavailable
            || status.StartsWith("TARGET OCR FAILED", StringComparison.Ordinal)
            || status.StartsWith("ORIGIN OCR FAILED", StringComparison.Ordinal))
        {
            return StatusLevel.Error;
        }
        return StatusLevel.Info;
    }

    public static string ResourceKeyFor(StatusLevel level) => level switch
    {
        StatusLevel.Success => "SuccessBrush",
        StatusLevel.Working => "WorkingBrush",
        StatusLevel.Warning => "WarningBrush",
        StatusLevel.Error => "ErrorBrush",
        StatusLevel.Neutral => "DisabledBrush",
        _ => "InfoBrush",
    };

    public static Brush BrushFor(StatusLevel level) =>
        Application.Current?.TryFindResource(ResourceKeyFor(level)) as Brush ?? Brushes.LightGray;
}
