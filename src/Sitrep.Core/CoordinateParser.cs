using System.Globalization;
using System.Text.RegularExpressions;

namespace Sitrep.Core;

public static partial class CoordinateParser
{
    // Keep numeric-like malformed bodies (xBAD1, x.77, x + 101.53), but not alphabetic words or lone labels.
    // Surrogates are not delimiters either; an adjacent supplementary Unicode letter is still part of a token.
    [GeneratedRegex(@"(?<![\p{L}\p{M}\p{N}\p{Pc}\p{Cf}\p{Cs}+\-\u2212])(?<axis>[XxYy])(?=\s*[0-9.,+\-\u2212]|\S*[0-9])\s*(?<body>[+-]?[0-9][0-9.,]*)?", RegexOptions.CultureInvariant)]
    private static partial Regex AxisTokenRegex();

    private static bool IsTokenContinuation(char value) =>
        char.IsLetterOrDigit(value) || char.IsSurrogate(value) || value is '+' or '-' or '\u2212'
        || char.GetUnicodeCategory(value) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.Format
            or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;

    public static bool TryParse(string? rawText, out MapCoordinate coordinate, out string rejectionReason)
    {
        coordinate = default;
        rejectionReason = string.Empty;
        if (string.IsNullOrWhiteSpace(rawText))
        {
            rejectionReason = "EMPTY";
            return false;
        }
        var matches = AxisTokenRegex().Matches(rawText);
        if (matches.Count == 0)
        {
            rejectionReason = "NO_COORDINATES";
            return false;
        }
        List<double> xs = new();
        List<double> ys = new();
        foreach (Match m in matches)
        {
            char axis = char.ToUpperInvariant(m.Groups["axis"].Value[0]);
            string body = m.Groups["body"].Value;
            int tokenEnd = m.Index + m.Length;
            if (tokenEnd < rawText.Length)
            {
                char next = rawText[tokenEnd];
                if (char.IsDigit(next) || next == '.' || next == ',')
                {
                    rejectionReason = axis == 'X' ? "BAD_X_PRECISION" : "BAD_Y_PRECISION";
                    return false;
                }
                if (IsTokenContinuation(next))
                {
                    rejectionReason = "PARTIAL_TOKEN";
                    return false;
                }
            }
            if (body.StartsWith('+') || body.StartsWith('-'))
            {
                rejectionReason = "UNSUPPORTED_SIGN";
                return false;
            }
            string normalized = body.Replace(',', '.');
            int dots = normalized.Count(c => c == '.');
            if (dots != 1)
            {
                rejectionReason = axis == 'X' ? "BAD_X_PRECISION" : "BAD_Y_PRECISION";
                return false;
            }
            var parts = normalized.Split('.');
            if (parts.Length != 2)
            {
                rejectionReason = axis == 'X' ? "BAD_X_PRECISION" : "BAD_Y_PRECISION";
                return false;
            }
            if (parts[0].Length is < 1 or > 3 || parts[1].Length != 2)
            {
                rejectionReason = axis == 'X' ? "BAD_X_PRECISION" : "BAD_Y_PRECISION";
                return false;
            }
            if (!parts[0].All(char.IsDigit) || !parts[1].All(char.IsDigit))
            {
                rejectionReason = "PARTIAL_TOKEN";
                return false;
            }
            if (!double.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value))
            {
                rejectionReason = "NON_FINITE";
                return false;
            }
            if (!double.IsFinite(value))
            {
                rejectionReason = "NON_FINITE";
                return false;
            }
            if (axis == 'X')
            {
                xs.Add(value);
            }
            else
            {
                ys.Add(value);
            }
        }
        if (xs.Count == 0 || ys.Count == 0)
        {
            rejectionReason = xs.Count == 0 ? "MISSING_X" : "MISSING_Y";
            return false;
        }
        if (xs.Count > 1 || ys.Count > 1)
        {
            if (xs.Distinct().Count() > 1 || ys.Distinct().Count() > 1)
            {
                rejectionReason = "CONFLICTING_PAIR";
            }
            else
            {
                rejectionReason = "DUPLICATE_AXIS";
            }
            return false;
        }
        coordinate = new MapCoordinate(xs[0], ys[0]);
        if (!coordinate.IsFinite)
        {
            rejectionReason = "NON_FINITE";
            return false;
        }
        return true;
    }
}
