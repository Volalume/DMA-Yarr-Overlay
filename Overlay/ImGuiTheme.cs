using System.Numerics;
using Hexa.NET.ImGui;

namespace Overlay;

internal static class ImGuiTheme
{
    public static readonly Vector4 Accent = Rgba(255, 194, 82);
    public static readonly Vector4 AccentBright = Rgba(255, 215, 118);
    public static readonly Vector4 Good = Rgba(94, 226, 142);
    public static readonly Vector4 Warning = Rgba(255, 126, 116);

    public static void Apply(float dpiScale)
    {
        ImGui.StyleColorsDark();
        var style = ImGui.GetStyle();
        style.WindowPadding = new Vector2(18, 16);
        style.FramePadding = new Vector2(11, 7);
        style.CellPadding = new Vector2(10, 7);
        style.ItemSpacing = new Vector2(10, 9);
        style.ItemInnerSpacing = new Vector2(8, 6);
        style.ScrollbarSize = 13;
        style.GrabMinSize = 11;
        style.WindowBorderSize = 1;
        style.ChildBorderSize = 1;
        style.PopupBorderSize = 1;
        style.FrameBorderSize = 1;
        style.TabBorderSize = 0;
        style.WindowRounding = 10;
        style.ChildRounding = 9;
        style.FrameRounding = 6;
        style.PopupRounding = 8;
        style.ScrollbarRounding = 8;
        style.GrabRounding = 6;
        style.TabRounding = 7;

        var colors = style.Colors;
        colors[(int)ImGuiCol.Text] = Rgba(231, 235, 241);
        colors[(int)ImGuiCol.TextDisabled] = Rgba(111, 120, 137);
        colors[(int)ImGuiCol.WindowBg] = Rgba(10, 12, 17);
        colors[(int)ImGuiCol.ChildBg] = Rgba(20, 24, 32);
        colors[(int)ImGuiCol.PopupBg] = Rgba(18, 22, 29, .98f);
        colors[(int)ImGuiCol.Border] = Rgba(52, 60, 74);
        colors[(int)ImGuiCol.BorderShadow] = Vector4.Zero;
        colors[(int)ImGuiCol.FrameBg] = Rgba(27, 32, 42);
        colors[(int)ImGuiCol.FrameBgHovered] = Rgba(39, 46, 59);
        colors[(int)ImGuiCol.FrameBgActive] = Rgba(49, 56, 70);
        colors[(int)ImGuiCol.TitleBg] = Rgba(13, 16, 22);
        colors[(int)ImGuiCol.TitleBgActive] = Rgba(16, 20, 27);
        colors[(int)ImGuiCol.ScrollbarBg] = Rgba(11, 14, 19);
        colors[(int)ImGuiCol.ScrollbarGrab] = Rgba(48, 56, 69);
        colors[(int)ImGuiCol.ScrollbarGrabHovered] = Rgba(63, 72, 87);
        colors[(int)ImGuiCol.ScrollbarGrabActive] = Rgba(80, 89, 104);
        colors[(int)ImGuiCol.CheckMark] = AccentBright;
        colors[(int)ImGuiCol.SliderGrab] = Accent;
        colors[(int)ImGuiCol.SliderGrabActive] = AccentBright;
        colors[(int)ImGuiCol.Button] = Rgba(41, 47, 59);
        colors[(int)ImGuiCol.ButtonHovered] = Rgba(58, 65, 78);
        colors[(int)ImGuiCol.ButtonActive] = Rgba(68, 74, 87);
        colors[(int)ImGuiCol.Header] = Rgba(42, 48, 60);
        colors[(int)ImGuiCol.HeaderHovered] = Rgba(60, 67, 80);
        colors[(int)ImGuiCol.HeaderActive] = Rgba(71, 78, 91);
        colors[(int)ImGuiCol.Separator] = Rgba(49, 57, 70);
        colors[(int)ImGuiCol.SeparatorHovered] = Accent;
        colors[(int)ImGuiCol.SeparatorActive] = AccentBright;
        colors[(int)ImGuiCol.Tab] = Rgba(22, 26, 34);
        colors[(int)ImGuiCol.TabHovered] = Rgba(58, 52, 39);
        colors[(int)ImGuiCol.TabSelected] = Rgba(43, 39, 31);
        colors[(int)ImGuiCol.TabSelectedOverline] = Accent;
        colors[(int)ImGuiCol.TabDimmed] = Rgba(17, 20, 27);
        colors[(int)ImGuiCol.TabDimmedSelected] = Rgba(30, 31, 31);
        colors[(int)ImGuiCol.PlotLines] = Accent;
        colors[(int)ImGuiCol.PlotHistogram] = Accent;
        colors[(int)ImGuiCol.TextSelectedBg] = Rgba(255, 194, 82, .28f);
        colors[(int)ImGuiCol.NavCursor] = AccentBright;
        style.ScaleAllSizes(dpiScale);
    }

    private static Vector4 Rgba(byte r, byte g, byte b, float alpha = 1) =>
        new(r / 255f, g / 255f, b / 255f, alpha);
}
