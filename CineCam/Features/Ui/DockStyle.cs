using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace CineCam.Features.Ui;

// The stock console look for the timeline dock and the recording strip. A caller ends every
// BeginPanel with EndPanel in a finally block.
internal static class DockStyle
{
    public enum Kind { Plain, Primary, Positive, Danger }

    private const string Signature = "CINECAM";

    public static uint Ink(uint color) => ConsoleStyle.ApplyAlpha(color);

    public static uint Ink(uint color, float alpha) => ConsoleStyle.ApplyAlpha(ConsoleStyle.ApplyAlpha(color, alpha));

    #region Panel

    // The console widget style stays pushed until EndPanel, so the popups and tooltips of the panel get the console colors too.
    public static void BeginPanel(string id, ImGuiWindowFlags flags)
    {
        ConsoleStyle.PushWidgetStyle();
        try
        {
            float pad = ConsoleStyle.ContentPaddingPx;
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(pad, pad));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, float2.Zero);
            try { ImGui.Begin(id.AsSpan(), flags | ImGuiWindowFlags.NoBackground); }
            finally { ImGui.PopStyleVar(3); }
        }
        catch
        {
            ConsoleStyle.PopWidgetStyle();
            throw;
        }

        try
        {
            ConsoleStyle.DrawPanelBezel(ImGui.GetWindowPos(), ImGui.GetWindowSize(), ImDrawFlags.RoundCornersAll, raised: false);
        }
        catch
        {
            EndPanel();
            throw;
        }
    }

    public static void EndPanel()
    {
        try { ImGui.End(); }
        finally { ConsoleStyle.PopWidgetStyle(); }
    }

    // Returns the right end of the title.
    public static float Titlebar(string title, out float height)
    {
        float2 pos = ImGui.GetWindowPos();
        height = ConsoleStyle.TitlebarHeight;
        var max = new float2(pos.X + ImGui.GetWindowSize().X, pos.Y + height);
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, max, Ink(ConsoleStyle.PanelRaisedU32), ConsoleStyle.WindowRoundingPx, ImDrawFlags.RoundCornersTop);
        draw.AddLine(new float2(pos.X, max.Y), max, Ink(ConsoleStyle.HairlineU32), ConsoleStyle.WindowBorderThicknessPx);
        float x = BarText(Signature, pos.X + ConsoleStyle.TitlebarHorizontalPaddingPx, max.X, ConsoleStyle.TextMutedU32, true);
        return BarText(title, x + ConsoleStyle.TitlebarSignatureGapPx, max.X, ConsoleStyle.TextPrimaryU32, false);
    }

    // Returns the right end of the clipped text. The small signature font sits on the base line of the label font.
    public static float BarText(string text, float x, float limit, uint color, bool small)
    {
        if (limit - x < 1f)
            return x;
        float2 pos = ImGui.GetWindowPos();
        float height = ConsoleStyle.TitlebarHeight;
        float labelSize = FontSize(false);
        float top = pos.Y + (height - labelSize) * 0.5f;
        PushFont(small);
        try
        {
            float size = ImGui.GetFontSize();
            float y = small ? top + (labelSize - size) * 0.8f : top;
            float width = ImGui.CalcTextSize(text).X;
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            draw.PushClipRect(new float2(x, pos.Y), new float2(limit, pos.Y + height), true);
            draw.AddText(new float2(x, y), Ink(color), text);
            draw.PopClipRect();
            return Math.Min(x + width, limit);
        }
        finally { ConsoleStyle.PopFont(); }
    }

    #endregion

    #region Text

    public static void PushFont(bool small)
    {
        if (small)
            ConsoleStyle.PushSignatureFont();
        else
            ConsoleStyle.PushLabelFont();
    }

    public static float FontSize(bool small)
    {
        PushFont(small);
        try { return ImGui.GetFontSize(); }
        finally { ConsoleStyle.PopFont(); }
    }

    public static float TextWidth(string text, bool small)
    {
        PushFont(small);
        try { return ImGui.CalcTextSize(text).X; }
        finally { ConsoleStyle.PopFont(); }
    }

    // ImGui.Text of Brutal draws the text unformatted, so a percent sign in a name shows as it is.
    public static void Text(string text, in float4 ink)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, in ink);
        try { ImGui.Text(text.AsSpan()); }
        finally { ImGui.PopStyleColor(); }
    }

    public static void Wrapped(string text, in float4 ink)
    {
        ImGui.PushTextWrapPos(0f);
        try { Text(text, in ink); }
        finally { ImGui.PopTextWrapPos(); }
    }

    #endregion

    #region Controls

    // BeginDisabled blocks the click and lowers the style alpha, which the kit applies to its ink.
    public static bool Button(string label, bool enabled = true, string? tip = null, Kind kind = Kind.Plain, string? id = null)
    {
        ReadOnlySpan<char> text = label.AsSpan();
        ReadOnlySpan<char> key = (id ?? label).AsSpan();
        bool clicked;
        ImGui.BeginDisabled(!enabled);
        try
        {
            clicked = kind switch
            {
                Kind.Primary => ConsoleWidgets.PrimaryButton(text, key, float2.Zero),
                Kind.Positive => ConsoleWidgets.PositiveButton(text, key, float2.Zero),
                Kind.Danger => ConsoleWidgets.DangerButton(text, key, float2.Zero),
                _ => ConsoleWidgets.Button(text, key, float2.Zero),
            };
        }
        finally { ImGui.EndDisabled(); }
        Tip(tip);
        return clicked && enabled;
    }

    public static void Flow(float width)
    {
        float right = ImGui.GetWindowPos().X + ImGui.GetWindowSize().X - ConsoleStyle.ContentPaddingPx;
        if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + width <= right)
            ImGui.SameLine();
    }

    // When it returns true, the caller ends the popup with ImGui.EndPopup.
    public static bool BeginMenu(string id)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, ConsoleWidgets.TooltipRoundingPx);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, ConsoleStyle.WindowBorderThicknessPx);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, ConsoleStyle.ScaledWindowPaddingPx);
        try { return ImGui.BeginPopup(id); }
        finally { ImGui.PopStyleVar(3); }
    }

    // ConsoleWidgets.Tooltip draws with ImGui.TextColored, which Brutal reads as a printf format, so
    // the card is built here with ImGui.Text. AllowWhenDisabled is needed because the tooltip of a
    // disabled button says why it is off.
    public static void Tip(string? text)
    {
        if (string.IsNullOrEmpty(text) || !ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            return;
        ConsoleStyle.EnsureInitialized();
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 1f);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, in ConsoleStyle.PanelRaised);
        ImGui.PushStyleColor(ImGuiCol.Border, in ConsoleStyle.Frame);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, ConsoleWidgets.TooltipRoundingPx);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, ConsoleStyle.WindowBorderThicknessPx);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, ConsoleStyle.ScaledWindowPaddingPx);
        try
        {
            if (!ImGui.BeginTooltip())
                return;
            ConsoleStyle.PushValueFont();
            ImGui.PushStyleColor(ImGuiCol.Text, in ConsoleStyle.TextLabel);
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35.0f);
            try { ImGui.Text(text.AsSpan()); }
            finally
            {
                ImGui.PopTextWrapPos();
                ImGui.PopStyleColor();
                ConsoleStyle.PopFont();
                ImGui.EndTooltip();
            }
        }
        finally
        {
            ImGui.PopStyleVar(4);
            ImGui.PopStyleColor(2);
        }
    }

    #endregion
}
