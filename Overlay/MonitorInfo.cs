using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Overlay;

internal sealed record MonitorInfo(string Name, string DeviceName, Rectangle Bounds, bool IsPrimary)
{
    public static IEnumerable<MonitorInfo> Enumerate()
    {
        return Screen.AllScreens.Select((screen, index) =>
        {
            var label = $"Display {index + 1}";
            if (screen.Primary)
            {
                label += " [Primary]";
            }

            return new MonitorInfo(
                $"{label} {screen.Bounds.Width}x{screen.Bounds.Height}",
                screen.DeviceName,
                screen.Bounds,
                screen.Primary);
        });
    }
}
