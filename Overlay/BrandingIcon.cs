using System.Drawing;

namespace Overlay;

internal static class BrandingIcon
{
    public static Icon? TryLoad(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        using var source = new Icon(path);
        return (Icon)source.Clone();
    }
}
