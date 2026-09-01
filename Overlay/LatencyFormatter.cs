using System;
using System.Globalization;

namespace Overlay;

internal static class LatencyFormatter
{
    public static string Milliseconds(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return "N/A";

        var roundedHundredths = Math.Round(value.Value, 2, MidpointRounding.AwayFromZero);
        return Math.Abs(roundedHundredths) < 1
            ? roundedHundredths.ToString("0.00", CultureInfo.InvariantCulture)
            : value.Value.ToString("0", CultureInfo.InvariantCulture);
    }
}
