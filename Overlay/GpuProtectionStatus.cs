namespace Overlay;

// Immutable snapshots are published across the capture and UI threads.
internal sealed record GpuProtectionStatus(bool Blocked, bool Active, string Summary, string Detail)
{
    public static readonly GpuProtectionStatus Off = new(false, false, "Off", "Normal GPU output; window capture settings still apply.");
    public static readonly GpuProtectionStatus Pending = new(false, false, "Checking", "No protected frame submitted yet.");
}
