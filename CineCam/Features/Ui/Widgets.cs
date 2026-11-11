using Brutal.ImGuiApi;
using Brutal.Numerics;
using CineCam.Director;
using KSA;

namespace CineCam.Features.Ui;

// A key in a label after "##" keeps the ImGui id stable when the visible text changes. A helper
// returns true in the frame in which the player changed the value.
//
// ImGui.Text of Brutal calls TextUnformatted, but ImGui.TextWrapped, TextColored, TextDisabled and
// SetTooltip pass the text as a printf format, and so do the kit calls that end in TextColored
// (ConsoleWidgets.RegionHeader, ConsoleWidgets.Tooltip, ConsoleWidgets.Readout,
// ConsoleStyle.FooterStatus). Text for those goes through Escape.
internal static class Widgets
{
    public static readonly float4 ErrorColor = ConsoleStyle.Danger;
    public static readonly float4 WarnColor = ConsoleStyle.Pending;
    public static readonly float4 GoodColor = ConsoleStyle.Positive;
    public static readonly float4 MutedColor = ConsoleStyle.TextMuted;

    public enum Tone { Plain, Primary, Positive, Danger }

    // A text field shows the value of the document until the player starts to type in it.
    private static readonly Dictionary<string, ImInputString> Buffers = new();
    private static string? _activeTextKey;

    private static readonly Dictionary<string, bool> Folds = new();

    // A Tip right after a row uses the row hover, because the last ImGui item of a row is the spacer of ConsoleWidgets.EndRow.
    private static bool _afterRow;
    private static bool _lastRowHovered;

    // The full label of the current row when it had to be cut, for its tooltip.
    private static string? _rowLabelCut;

    private const float TipLines = 30f;

    public static void ClearBuffers()
    {
        Buffers.Clear();
        FitCache.Clear();
        _activeTextKey = null;
    }

    public static float Scale => ImGuiHelper.InterfaceScale;

    // Forgets a line or a row hover that the window left behind, so the docks that draw after it start clean.
    public static void EndWindow()
    {
        _sameLine = false;
        _afterRow = false;
        _lastRowHovered = false;
    }

    public static string Escape(string text) => text.Replace("%", "%%");

    private static string Visible(string label)
    {
        int cut = label.IndexOf("##", StringComparison.Ordinal);
        return cut < 0 ? label : label[..cut];
    }

    // The kit builds its ids in a buffer of 128 chars, so a long label gets a short id.
    private static string IdOf(string label) =>
        label.Length <= 96 ? label : label[..64] + "#" + label.GetHashCode().ToString("x8", System.Globalization.CultureInfo.InvariantCulture);

    #region Text

    public static void Colored(string text, float4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, in color);
        Wrapped(text);
        ImGui.PopStyleColor();
    }

    public static void Wrapped(string text)
    {
        _afterRow = false;
        if (_sameLine)
        {
            _sameLine = false;
            ImGui.SameLine();
        }

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Max(1f, ConsoleStyle.ContentAvailWidth()));
        ImGui.Text(text.AsSpan());
        ImGui.PopTextWrapPos();
    }

    public static void Muted(string text) => Colored(text, MutedColor);

    public static void MutedSteps(IReadOnlyList<string> steps)
    {
        _afterRow = false;
        _sameLine = false;
        float avail = Math.Max(1f, ConsoleStyle.ContentAvailWidth());
        float gap = ImGui.CalcTextSize("  ").X;
        float used = 0f;
        ImGui.PushStyleColor(ImGuiCol.Text, in MutedColor);
        for (int i = 0; i < steps.Count; i++)
        {
            float w = ImGui.CalcTextSize(steps[i]).X;
            if (i > 0 && used + gap + w <= avail)
            {
                ImGui.SameLine(0f, gap);
                used += gap + w;
            }
            else
                used = w;
            ImGui.Text(steps[i].AsSpan());
        }

        ImGui.PopStyleColor();
    }

    public static void Label(string text, float4 color)
    {
        _afterRow = false;
        Place(ImGui.CalcTextSize(text).X);
        ImGui.PushStyleColor(ImGuiCol.Text, in color);
        ImGui.Text(text.AsSpan());
        ImGui.PopStyleColor();
    }

    public static void Reason(string reason)
    {
        if (reason.Length > 0)
            Muted(reason);
    }

    public static void Section(string title)
    {
        _afterRow = false;
        _sameLine = false;
        ImGui.Dummy(new float2(0f, 6f * Scale));
        ConsoleWidgets.RegionHeader(Escape(title).AsSpan());
    }

    public static void Readout(string key, string value, float4? color = null)
    {
        _afterRow = false;
        _sameLine = false;
        float width = ConsoleStyle.ContentAvailWidth();
        float h = ConsoleStyle.FrameHeightPx;
        float2 at = ImGui.GetCursorScreenPos();
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        ConsoleStyle.PushValueFont();
        float y = at.Y + (h - ImGui.GetFontSize()) * 0.5f;
        string name = key.ToUpperInvariant();
        float nameW = ImGui.CalcTextSize(name).X;
        float room = Math.Max(0f, width - nameW - ConsoleWidgets.ReadoutColumnGapPx);
        string shown = Fit(value, room);
        float valueW = ImGui.CalcTextSize(shown).X;
        dl.AddText(new float2(at.X, y), ConsoleStyle.ApplyAlpha(ConsoleStyle.TextMutedU32), name);
        float4 ink = color ?? ConsoleStyle.TextPrimary;
        dl.AddText(new float2(at.X + width - valueW, y), ConsoleStyle.ApplyAlpha(ImGui.ColorConvertFloat4ToU32(in ink)), shown);
        ConsoleStyle.PopFont();
        ImGui.Dummy(new float2(width, h));
        _afterRow = true;
        _lastRowHovered = shown != value && Hovering(at, at + new float2(width, h));
        if (_lastRowHovered)
            ShowTip(value);
    }

    // Measures in the font that is pushed now.
    private static string Fit(string text, float room)
    {
        if (ImGui.CalcTextSize(text).X <= room)
            return text;
        const string Dots = "...";
        float dots = ImGui.CalcTextSize(Dots).X;
        int low = 0, high = text.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (ImGui.CalcTextSize(text.AsSpan(0, mid)).X + dots <= room)
                low = mid;
            else
                high = mid - 1;
        }

        return low == 0 ? "" : text[..low].TrimEnd() + Dots;
    }

    // FitWords measures in the font that is pushed now, and a first word that does not fit is cut in
    // the word. The cache key holds the full width because that tells the fonts apart.
    private static readonly Dictionary<(string Text, float Room, float Width), string> FitCache = new();

    public static string FitWords(string text, float room)
    {
        float width = ImGui.CalcTextSize(text).X;
        if (width <= room)
            return text;
        if (FitCache.TryGetValue((text, room, width), out string? cached))
            return cached;
        string shown = Editing.StatusText.FitWords(text, room, static t => ImGui.CalcTextSize(t).X);
        if (shown.Length == 0)
            shown = Fit(text, room);
        if (FitCache.Count >= 64)
            FitCache.Clear();
        FitCache[(text, room, width)] = shown;
        return shown;
    }

    public static bool Hovering(float2 min, float2 max) =>
        ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows) && ImGui.IsMouseHoveringRect(in min, in max);

    // A tooltip for an area that is no ImGui item, such as the footer.
    public static void TipOver(float2 min, float2 max, string text)
    {
        if (text.Length > 0 && Hovering(min, max))
            ShowTip(text);
    }

    public static void StatusLine(string text, float4 color, string? tip = null)
    {
        _afterRow = false;
        _sameLine = false;
        float width = Math.Max(1f, ConsoleStyle.ContentAvailWidth());
        float h = ConsoleStyle.FrameHeightPx;
        float2 at = ImGui.GetCursorScreenPos();
        ConsoleStyle.PushValueFont();
        string shown;
        try
        {
            shown = FitWords(text, width);
            ImGui.GetWindowDrawList().AddText(new float2(at.X, at.Y + (h - ImGui.GetFontSize()) * 0.5f),
                ConsoleStyle.ApplyAlpha(ImGui.ColorConvertFloat4ToU32(in color)), shown);
        }
        finally { ConsoleStyle.PopFont(); }

        ImGui.Dummy(new float2(width, h));
        string full = shown != text ? text : "";
        if (!string.IsNullOrEmpty(tip))
            full = full.Length > 0 ? full + "\n\n" + tip : tip;
        if (full.Length > 0 && Hovering(at, at + new float2(width, h)))
            ShowTip(full);
    }

    public static bool LinkLine(string id, string text, float4 color, string? tip = null)
    {
        _afterRow = false;
        _sameLine = false;
        ConsoleStyle.EnsureInitialized();
        float width = Math.Max(1f, ConsoleStyle.ContentAvailWidth());
        float h = ConsoleStyle.FrameHeightPx;
        float2 at = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(("##link-" + id).AsSpan(), new float2(width, h));
        bool hovered = ImGui.IsItemHovered();
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        if (hovered)
            dl.AddRectFilled(at, at + new float2(width, h), ConsoleStyle.ApplyAlpha(ConsoleStyle.RowHoverWashU32), ConsoleStyle.FrameRoundingPx);
        ConsoleStyle.PushValueFont();
        string shown;
        try
        {
            float inset = ConsoleWidgets.RowLabelInsetPx;
            shown = FitWords(text, Math.Max(0f, width - inset * 2f));
            dl.AddText(new float2(at.X + inset, at.Y + (h - ImGui.GetFontSize()) * 0.5f),
                ConsoleStyle.ApplyAlpha(ImGui.ColorConvertFloat4ToU32(in color)), shown);
        }
        finally { ConsoleStyle.PopFont(); }

        string full = shown != text ? text : "";
        if (!string.IsNullOrEmpty(tip))
            full = full.Length > 0 ? full + "\n\n" + tip : tip;
        if (hovered && full.Length > 0)
            ShowTip(full);
        return clicked;
    }

    #endregion

    #region Tooltips

    // AllowWhenDisabled is needed because the tooltip of a disabled control says why it is off.
    public static void Tip(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        bool hovered = _afterRow ? _lastRowHovered : ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        if (hovered)
            ShowTip(text);
    }

    // The look of ConsoleWidgets.Tooltip, but drawn with ImGui.Text so the text is not a format. The
    // alpha is full, because the tip of a disabled control must stay readable.
    private static void ShowTip(string text)
    {
        ConsoleStyle.EnsureInitialized();
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 1f);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, in ConsoleStyle.PanelRaised);
        ImGui.PushStyleColor(ImGuiCol.Border, in ConsoleStyle.Frame);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, ConsoleWidgets.TooltipRoundingPx);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, ConsoleStyle.WindowBorderThicknessPx);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, ConsoleStyle.ScaledWindowPaddingPx);
        if (ImGui.BeginTooltip())
        {
            ConsoleStyle.PushValueFont();
            ImGui.PushStyleColor(ImGuiCol.Text, in ConsoleStyle.TextLabel);
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * TipLines);
            ImGui.Text(text.AsSpan());
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
            ConsoleStyle.PopFont();
            ImGui.EndTooltip();
        }

        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(2);
    }

    #endregion

    #region Rows

    // A label that does not fit left of the control goes on its own line above the row. The room is
    // first estimated from the content width, so a label that the real row cannot hold is still cut,
    // with its full text in the tooltip.
    private static void BeginRow(string label, float controlWidth = 0f)
    {
        _afterRow = false;
        _sameLine = false;
        string text = Visible(label);
        float used = controlWidth > 0f ? Math.Min(controlWidth, ConsoleWidgets.RowControlWidth) : ConsoleWidgets.RowControlWidth;
        ConsoleStyle.PushLabelFont();
        bool above = text.Length > 0
            && ImGui.CalcTextSize(text).X > ConsoleStyle.ContentAvailWidth() - used - ConsoleWidgets.MinControlGapPx - ConsoleWidgets.RowLabelInsetPx * 2f;
        if (above)
        {
            ImGui.SetCursorScreenPos(ImGui.GetCursorScreenPos() + new float2(ConsoleWidgets.RowLabelInsetPx, 0f));
            ImGui.PushStyleColor(ImGuiCol.Text, in ConsoleStyle.TextLabel);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Max(1f, ConsoleStyle.ContentAvailWidth()));
            ImGui.Text(text.AsSpan());
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
            text = "";
        }

        ConsoleStyle.PopFont();
        float2 at = ImGui.GetCursorScreenPos();
        ConsoleWidgets.BeginRow(" ".AsSpan());
        float control = ImGui.GetCursorScreenPos().X;
        float left = at.X + ConsoleWidgets.RowLabelInsetPx;
        float room = control + ConsoleWidgets.RowControlWidth - used - ConsoleWidgets.MinControlGapPx - left;
        ConsoleStyle.PushLabelFont();
        string shown = Fit(text, room);
        float y = at.Y + (ConsoleStyle.FrameHeightPx - ImGui.GetFontSize()) * 0.5f;
        if (shown.Length > 0)
            ImGui.GetWindowDrawList().AddText(new float2(left, y), ConsoleStyle.ApplyAlpha(ConsoleStyle.TextLabelU32), shown);
        ConsoleStyle.PopFont();
        _rowLabelCut = shown != text ? text : null;
    }

    private static void EndRow(string? tip)
    {
        bool hovered = ConsoleWidgets.RowHovered;
        string text = _rowLabelCut ?? "";
        if (!string.IsNullOrEmpty(tip))
            text = text.Length > 0 ? text + "\n\n" + tip : tip;
        if (hovered && text.Length > 0)
            ShowTip(text);
        ConsoleWidgets.EndRow();
        _rowLabelCut = null;
        _afterRow = true;
        _lastRowHovered = hovered;
    }

    private static void RowRight(float width)
    {
        float2 control = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new float2(control.X + Math.Max(0f, ConsoleWidgets.RowControlWidth - width), control.Y));
    }

    // The kit well is drawn behind a stock ImGui field with a transparent frame, so the field keeps
    // its own speed, limits and number format.
    private static void PushWell(float width)
    {
        ConsoleWidgets.InputWell(ImGui.GetCursorScreenPos(), new float2(width, ConsoleStyle.FrameHeightPx));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, in ConsoleStyle.Transparent);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, in ConsoleStyle.Transparent);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, in ConsoleStyle.Transparent);
        ImGui.PushStyleColor(ImGuiCol.Border, in ConsoleStyle.Transparent);
        ImGui.SetNextItemWidth(width);
    }

    private static void PopWell() => ImGui.PopStyleColor(4);

    #endregion

    #region Buttons and switches

    private static bool _sameLine;

    // Unlike ImGui.SameLine, the next button, switch or label goes to the next line when it does not fit.
    public static void SameLine() => _sameLine = true;

    private static void Place(float width)
    {
        if (!_sameLine)
            return;
        _sameLine = false;
        ImGui.SameLine();
        if (ConsoleStyle.ContentAvailWidth() < width)
            ImGui.NewLine();
    }

    public static bool Button(string label, bool enabled = true, string? tip = null, Tone tone = Tone.Plain)
    {
        _afterRow = false;
        string shown = Visible(label);
        Place(ConsoleWidgets.ButtonWidth(shown.AsSpan()));
        ReadOnlySpan<char> id = IdOf(label).AsSpan();
        ImGui.BeginDisabled(!enabled);
        bool clicked = tone switch
        {
            Tone.Primary => ConsoleWidgets.PrimaryButton(shown.AsSpan(), id, default),
            Tone.Positive => ConsoleWidgets.PositiveButton(shown.AsSpan(), id, default),
            Tone.Danger => ConsoleWidgets.DangerButton(shown.AsSpan(), id, default),
            _ => ConsoleWidgets.Button(shown.AsSpan(), id, default),
        };
        ImGui.EndDisabled();
        Tip(tip);
        return clicked && enabled;
    }

    public static bool ButtonRow(string label, string verb, bool enabled = true, string? tip = null, Tone tone = Tone.Plain)
    {
        string verbText = Visible(verb);
        float w = ConsoleWidgets.ButtonWidth(verbText.AsSpan());
        BeginRow(label, w);
        RowRight(w);
        string id = verb.Contains("##", StringComparison.Ordinal) ? verb : verb + "##" + label;
        bool clicked = Button(id, enabled, null, tone);
        EndRow(tip);
        return clicked;
    }

    public static bool Check(string label, ref bool value, string? tip = null)
    {
        BeginRow(label, SwitchWidth(""));
        bool changed = ConsoleWidgets.Checkbox(IdOf(label).AsSpan(), ref value, pending: false);
        EndRow(tip);
        return changed;
    }

    // The track and knob sizes of the stock ConsoleWidgets.Checkbox, as fractions of the row height.
    private const float TrackW = 1.1538461f, TrackH = 0.61538464f, KnobW = 11f / 26f, KnobH = 0.3846154f, KnobInset = 1f / 13f;

    private static float SwitchWidth(string label)
    {
        float gap = ConsoleWidgets.CheckboxStateGapPx;
        ConsoleStyle.PushLabelFont();
        float labelW = label.Length > 0 ? ImGui.CalcTextSize(label).X + gap : 0f;
        ConsoleStyle.PopFont();
        ConsoleStyle.PushValueFont();
        float stateW = ImGui.CalcTextSize("OFF").X;
        ConsoleStyle.PopFont();
        return labelW + ConsoleStyle.FrameHeightPx * TrackW + gap + stateW;
    }

    // The look of ConsoleWidgets.Checkbox for a place next to buttons, because the stock control always moves to the right edge of a row.
    public static bool Switch(string label, ref bool on, string? tip = null)
    {
        _afterRow = false;
        string text = Visible(label);
        float h = ConsoleStyle.FrameHeightPx;
        float gap = ConsoleWidgets.CheckboxStateGapPx;
        float2 pos = ImGui.GetCursorScreenPos();
        ConsoleStyle.PushLabelFont();
        float labelW = text.Length > 0 ? ImGui.CalcTextSize(text).X + gap : 0f;
        ConsoleStyle.PopFont();
        ConsoleStyle.PushValueFont();
        float stateW = ImGui.CalcTextSize("OFF").X;
        ConsoleStyle.PopFont();
        var track = new float2(h * TrackW, h * TrackH);
        var size = new float2(labelW + track.X + gap + stateW, h);
        Place(size.X);
        pos = ImGui.GetCursorScreenPos();

        bool hit = ImGui.InvisibleButton(("##switch-" + IdOf(label)).AsSpan(), size);
        bool live = ImGui.GetStyle().Alpha >= 1f;
        bool hovered = ImGui.IsItemHovered() && live;
        if (hit)
            on = !on;

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        if (text.Length > 0)
        {
            ConsoleStyle.PushLabelFont();
            dl.AddText(pos + new float2(0f, (h - ImGui.GetFontSize()) * 0.5f), ConsoleStyle.ApplyAlpha(ConsoleStyle.TextLabelU32), text);
            ConsoleStyle.PopFont();
        }

        var tMin = new float2(pos.X + labelW, pos.Y + (h - track.Y) * 0.5f);
        float2 tMax = tMin + track;
        dl.AddRectFilled(tMin, tMax, ConsoleStyle.ApplyAlpha(ConsoleStyle.InsetU32), ConsoleStyle.FrameRoundingPx);
        uint border = !on ? (hovered ? ConsoleStyle.TextMutedU32 : ConsoleStyle.FrameU32) : (hovered ? ConsoleStyle.TextPrimaryU32 : ConsoleStyle.TextMutedU32);
        dl.AddRect(tMin, tMax, ConsoleStyle.ApplyAlpha(border), ConsoleStyle.FrameRoundingPx, ImDrawFlags.None, ConsoleStyle.WindowBorderThicknessPx);
        float inset = h * KnobInset;
        var knob = new float2(h * KnobW, h * KnobH);
        float2 kMin = tMin + new float2(on ? track.X - inset - knob.X : inset, inset);
        uint knobInk = on ? ConsoleStyle.TextPrimaryU32 : hovered ? ConsoleStyle.TextLabelU32 : ConsoleStyle.TextMutedU32;
        dl.AddRectFilled(kMin, kMin + knob, ConsoleStyle.ApplyAlpha(knobInk), ConsoleStyle.FrameRoundingPx * 0.5f);

        string state = on ? "ON" : "OFF";
        ConsoleStyle.PushValueFont();
        float w = ImGui.CalcTextSize(state).X;
        uint stateInk = !on ? (hovered ? ConsoleStyle.TextLabelU32 : ConsoleStyle.TextMutedU32) : (hovered ? ConsoleStyle.TextPrimaryU32 : ConsoleStyle.TextLabelU32);
        dl.AddText(new float2(tMax.X + gap + (stateW - w), pos.Y + (h - ImGui.GetFontSize()) * 0.5f), ConsoleStyle.ApplyAlpha(stateInk), state);
        ConsoleStyle.PopFont();

        Tip(tip);
        return hit;
    }

    #endregion

    #region Numbers

    // With a width the field stands inline with its label at the right, else it is a row. A negative
    // width leaves that much room at the right. The format is a printf format of ImGui.DragFloat.
    public static bool Num(string label, ref double value, float speed, double min, double max, string format = "%.2f",
        string? tip = null, float? width = null)
    {
        float f = (float)value;
        bool dragged;
        if (width is float inline)
        {
            _afterRow = false;
            float w = inline < 0f ? Math.Max(1f, ImGui.GetContentRegionAvail().X + inline) : inline;
            PushWell(w);
            dragged = ImGui.DragFloat(label.AsSpan(), ref f, speed, (float)min, (float)max, format.AsSpan(), ImGuiSliderFlags.AlwaysClamp);
            PopWell();
            Tip(tip);
        }
        else
        {
            BeginRow(label);
            PushWell(ConsoleWidgets.RowControlWidth);
            dragged = ImGui.DragFloat(("##" + IdOf(label)).AsSpan(), ref f, speed, (float)min, (float)max, format.AsSpan(), ImGuiSliderFlags.AlwaysClamp);
            PopWell();
            EndRow(tip);
        }

        if (!dragged)
            return false;
        value = f;
        return true;
    }

    // ConsoleWidgets.SliderFloat draws the value text as it is, so it is no printf format.
    public static bool Slider(string label, ref float value, float min, float max, string valueText, string? tip = null)
    {
        BeginRow(label);
        bool changed = ConsoleWidgets.SliderFloat(IdOf(label).AsSpan(), ref value, min, max, valueText.AsSpan(), pending: false);
        EndRow(tip);
        return changed;
    }

    public static bool Vec(string label, ref V3 value, float speed = 0.5f, string? tip = null)
    {
        var v = new float3((float)value.X, (float)value.Y, (float)value.Z);
        BeginRow(label);
        float full = ConsoleWidgets.RowControlWidth;
        float spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        float one = Math.Max(1f, MathF.Floor((full - spacing * 2f) / 3f));
        float2 at = ImGui.GetCursorScreenPos();
        for (int i = 0; i < 3; i++)
        {
            float x = at.X + (one + spacing) * i;
            float w = i < 2 ? one : Math.Max(1f, full - (one + spacing) * 2f);
            ConsoleWidgets.InputWell(new float2(x, at.Y), new float2(w, ConsoleStyle.FrameHeightPx));
        }

        ImGui.PushStyleColor(ImGuiCol.FrameBg, in ConsoleStyle.Transparent);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, in ConsoleStyle.Transparent);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, in ConsoleStyle.Transparent);
        ImGui.PushStyleColor(ImGuiCol.Border, in ConsoleStyle.Transparent);
        ImGui.SetNextItemWidth(full);
        bool dragged = ImGui.DragFloat3(("##" + IdOf(label)).AsSpan(), ref v, speed, 0f, 0f, "%.2f".AsSpan());
        ImGui.PopStyleColor(4);
        EndRow(tip);
        if (!dragged)
            return false;
        value = new V3(v.X, v.Y, v.Z);
        return true;
    }

    #endregion

    #region Choices

    // When it returns true the list is open and the caller must call EndComboRow. When it returns
    // false the row is already closed.
    public static bool BeginComboRow(string label, string preview, string? tip = null)
    {
        BeginRow(label);
        float room = ConsoleWidgets.RowControlWidth - ConsoleWidgets.ComboTextPaddingPx * 2f - ConsoleStyle.FrameHeightPx * 0.35f;
        ConsoleStyle.PushValueFont();
        string shown = Fit(preview, room);
        ConsoleStyle.PopFont();
        bool open = ConsoleWidgets.BeginComboControl(IdOf(label).AsSpan(), shown.AsSpan(), pending: false);
        _comboTip = shown != preview && !string.IsNullOrEmpty(tip) ? preview + "\n\n" + tip : shown != preview ? preview : tip;
        if (!open)
        {
            EndRow(_comboTip);
            return false;
        }

        _afterRow = false;
        return true;
    }

    private static string? _comboTip;

    public static void EndComboRow()
    {
        ConsoleWidgets.EndComboControl();
        EndRow(_comboTip);
    }

    public static bool Enum<T>(string label, ref T value, IReadOnlyList<T>? options = null, string? tip = null) where T : struct, Enum
    {
        bool changed = false;
        if (!BeginComboRow(label, value.ToString(), tip))
            return false;
        try
        {
            IEnumerable<T> items = options ?? System.Enum.GetValues<T>();
            foreach (T item in items)
            {
                ImGui.PushID(item.ToString().AsSpan());
                try
                {
                    if (ImGui.Selectable(item.ToString().AsSpan(), EqualityComparer<T>.Default.Equals(item, value)))
                    {
                        value = item;
                        changed = true;
                    }
                }
                finally { ImGui.PopID(); }
            }
        }
        finally
        {
            EndComboRow();
        }

        return changed;
    }

    public static bool Choice<T>(string label, ref T value, IReadOnlyList<T> options, Func<T, string> name,
        Func<T, string>? help = null, string? tip = null)
    {
        bool changed = false;
        if (!BeginComboRow(label, name(value), tip))
            return false;
        try
        {
            for (int i = 0; i < options.Count; i++)
            {
                T item = options[i];
                ImGui.PushID(i);
                try
                {
                    if (ImGui.Selectable(name(item).AsSpan(), EqualityComparer<T>.Default.Equals(item, value)))
                    {
                        value = item;
                        changed = true;
                    }

                    if (help != null)
                        Tip(help(item));
                }
                finally { ImGui.PopID(); }
            }
        }
        finally
        {
            EndComboRow();
        }

        return changed;
    }

    // Returns the clicked index, or -1.
    public static int Tabs(string id, IReadOnlyList<string> tabs, int active, IReadOnlyList<string?>? tips = null)
    {
        _afterRow = false;
        _sameLine = false;
        ConsoleStyle.EnsureInitialized();
        float2 origin = ImGui.GetCursorScreenPos();
        float avail = Math.Max(1f, ConsoleStyle.ContentAvailWidth());
        float h = ConsoleStyle.FrameHeightPx;
        float pad = ImGui.GetStyle().ItemSpacing.X;
        float line = Math.Max(1f, 2f * Scale);
        float right = origin.X + avail;
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        bool windowHovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows);
        int clicked = -1;
        float x = origin.X, y = origin.Y;
        ConsoleStyle.PushLabelFont();
        try
        {
            float fontSize = ImGui.GetFontSize();
            for (int i = 0; i < tabs.Count; i++)
            {
                string text = tabs[i].ToUpperInvariant();
                float w = ImGui.CalcTextSize(text).X + pad * 2f;
                if (i > 0 && x + w > right)
                {
                    dl.AddLine(new float2(origin.X, y + h), new float2(right, y + h), ConsoleStyle.ApplyAlpha(ConsoleStyle.HairlineU32), ConsoleStyle.WindowBorderThicknessPx);
                    x = origin.X;
                    y += h;
                }

                var pos = new float2(x, y);
                var size = new float2(w, h);
                ImGui.SetCursorScreenPos(pos);
                if (ImGui.InvisibleButton((id + "##tab" + i).AsSpan(), size))
                    clicked = i;
                bool hovered = windowHovered && ImGui.IsMouseHoveringRect(in pos, pos + size);
                bool isActive = i == active;
                if (isActive)
                {
                    dl.AddRectFilled(pos, pos + size, ConsoleStyle.ApplyAlpha(ConsoleStyle.PanelRaisedU32), ConsoleStyle.FrameRoundingPx, ImDrawFlags.RoundCornersTop);
                    dl.AddRectFilled(new float2(pos.X, pos.Y + h - line), pos + size, ConsoleStyle.ApplyAlpha(ConsoleStyle.ActiveToolU32));
                }
                else if (hovered)
                {
                    dl.AddRectFilled(pos, pos + size, ConsoleStyle.ApplyAlpha(ConsoleStyle.RowHoverWashU32), ConsoleStyle.FrameRoundingPx, ImDrawFlags.RoundCornersTop);
                }

                uint ink = isActive ? ConsoleStyle.TextPrimaryU32 : hovered ? ConsoleStyle.TextLabelU32 : ConsoleStyle.TextMutedU32;
                dl.AddText(pos + new float2(pad, (h - fontSize) * 0.5f), ConsoleStyle.ApplyAlpha(ink), text);
                if (hovered && tips != null && i < tips.Count && !string.IsNullOrEmpty(tips[i]))
                    ShowTip(tips[i]!);
                x += w;
            }
        }
        finally
        {
            ConsoleStyle.PopFont();
        }

        dl.AddLine(new float2(x, y + h), new float2(right, y + h), ConsoleStyle.ApplyAlpha(ConsoleStyle.HairlineU32), ConsoleStyle.WindowBorderThicknessPx);
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new float2(avail, y - origin.Y + h + line));
        return clicked;
    }

    // Returns the clicked index, or -1.
    public static int Segmented(string id, string[] segments, int active, IReadOnlyList<string?>? tips = null)
    {
        _afterRow = false;
        float2 at = ImGui.GetCursorScreenPos();
        float width = ConsoleStyle.ContentAvailWidth();
        float h = ConsoleStyle.FrameHeightPx;
        int clicked = ConsoleWidgets.Segmented(id.AsSpan(), segments.AsSpan(), active);
        if (tips != null && segments.Length > 0)
        {
            float one = width / segments.Length;
            for (int i = 0; i < segments.Length && i < tips.Count; i++)
            {
                var min = new float2(at.X + one * i, at.Y);
                if (!string.IsNullOrEmpty(tips[i]) && Hovering(min, min + new float2(one, h)))
                    ShowTip(tips[i]!);
            }
        }

        return clicked;
    }

    // Returns true while the fold is open.
    public static bool Fold(string title, string id, bool openByDefault = false)
    {
        _afterRow = false;
        _sameLine = false;
        ConsoleStyle.EnsureInitialized();
        bool open = Folds.TryGetValue(id, out bool stored) ? stored : openByDefault;
        ImGui.Dummy(new float2(0f, 6f * Scale));
        float2 at = ImGui.GetCursorScreenPos();
        float width = Math.Max(1f, ConsoleStyle.ContentAvailWidth());
        float h = ConsoleStyle.FrameHeightPx;
        if (ImGui.InvisibleButton(("##fold-" + id).AsSpan(), new float2(width, h)))
        {
            open = !open;
            Folds[id] = open;
        }

        bool hovered = ImGui.IsItemHovered();
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        if (hovered)
            dl.AddRectFilled(at, at + new float2(width, h), ConsoleStyle.ApplyAlpha(ConsoleStyle.RowHoverWashU32), ConsoleStyle.FrameRoundingPx);
        uint ink = ConsoleStyle.ApplyAlpha(hovered ? ConsoleStyle.TextPrimaryU32 : ConsoleStyle.TextMutedU32);
        float caret = h * 0.3f;
        var c = new float2(at.X + caret, at.Y + h * 0.5f);
        if (open)
            dl.AddTriangleFilled(c + new float2(-caret * 0.5f, -caret * 0.3f), c + new float2(caret * 0.5f, -caret * 0.3f), c + new float2(0f, caret * 0.4f), ink);
        else
            dl.AddTriangleFilled(c + new float2(-caret * 0.3f, -caret * 0.5f), c + new float2(caret * 0.4f, 0f), c + new float2(-caret * 0.3f, caret * 0.5f), ink);

        ConsoleStyle.PushLabelFont();
        string text = title.ToUpperInvariant();
        float textX = at.X + caret * 2f + ConsoleWidgets.RowLabelInsetPx;
        dl.AddText(new float2(textX, at.Y + (h - ImGui.GetFontSize()) * 0.5f), ink, text);
        float lineFrom = textX + ImGui.CalcTextSize(text).X + ConsoleWidgets.RegionHeaderRuleGapPx;
        ConsoleStyle.PopFont();
        if (at.X + width > lineFrom)
            dl.AddLine(new float2(lineFrom, at.Y + h * 0.5f), new float2(at.X + width, at.Y + h * 0.5f), ConsoleStyle.ApplyAlpha(ConsoleStyle.HairlineU32), ConsoleStyle.WindowBorderThicknessPx);
        return open;
    }

    #endregion

    #region Lists

    public static void ListHeader(IReadOnlyList<string> heads, IReadOnlyList<float> columns)
    {
        _afterRow = false;
        float2 at = ImGui.GetCursorScreenPos();
        float width = ConsoleStyle.ContentAvailWidth();
        ConsoleStyle.PushSignatureFont();
        float h = ImGui.GetFontSize();
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        for (int c = 0; c < heads.Count && c < columns.Count; c++)
            dl.AddText(new float2(at.X + columns[c], at.Y), ConsoleStyle.ApplyAlpha(ConsoleStyle.TextMutedU32), heads[c].ToUpperInvariant());
        ConsoleStyle.PopFont();
        ImGui.Dummy(new float2(width, h));
    }

    public static bool ListRow(string id, IReadOnlyList<string> cells, IReadOnlyList<float> columns, bool selected, bool active = false,
        int mutedColumn = -1)
    {
        _afterRow = false;
        _sameLine = false;
        ConsoleStyle.EnsureInitialized();
        float2 at = ImGui.GetCursorScreenPos();
        float width = Math.Max(1f, ConsoleStyle.ContentAvailWidth());
        float h = ConsoleStyle.FrameHeightPx;
        bool clicked = ImGui.InvisibleButton(("##list-" + id).AsSpan(), new float2(width, h));
        bool hovered = ImGui.IsItemHovered();
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        if (selected)
        {
            dl.AddRectFilled(at, at + new float2(width, h), ConsoleStyle.ApplyAlpha(ConsoleStyle.PanelRaisedU32), ConsoleStyle.FrameRoundingPx);
            dl.AddRectFilled(at, at + new float2(ConsoleStyle.ModifiedTickThicknessPx, h), ConsoleStyle.ApplyAlpha(ConsoleStyle.ActiveToolU32));
        }
        else if (hovered)
        {
            dl.AddRectFilled(at, at + new float2(width, h), ConsoleStyle.ApplyAlpha(ConsoleStyle.RowHoverWashU32), ConsoleStyle.FrameRoundingPx);
        }

        ConsoleStyle.PushValueFont();
        float y = at.Y + (h - ImGui.GetFontSize()) * 0.5f;
        for (int c = 0; c < cells.Count && c < columns.Count; c++)
        {
            float end = c + 1 < columns.Count ? columns[c + 1] - ConsoleWidgets.RowLabelInsetPx : width;
            string shown = Fit(cells[c], Math.Max(0f, end - columns[c]));
            uint ink = active ? ConsoleStyle.PositiveU32
                : c == mutedColumn ? ConsoleStyle.TextMutedU32
                : selected ? ConsoleStyle.TextPrimaryU32 : ConsoleStyle.TextLabelU32;
            dl.AddText(new float2(at.X + columns[c], y), ConsoleStyle.ApplyAlpha(ink), shown);
        }

        ConsoleStyle.PopFont();
        return clicked;
    }

    #endregion

    #region Event names

    // The stored text of a trigger stays canonical. These helpers only change what the player reads.
    public static string EventName(string name) => name.Length == 0 ? "(no event)" : EventDisplay.Name(name);

    public static string TriggerText(Trigger trigger, int index)
    {
        if (trigger.Kind != TriggerKind.Event)
            return Editing.ViewText.TriggerText(trigger, index);
        if (trigger.Delay == 0.0)
            return EventName(trigger.Event);
        return EventName(trigger.Event) + (trigger.Delay > 0.0 ? " +" : " -") + Editing.ViewText.Seconds(Math.Abs(trigger.Delay));
    }

    public static string DescribeTrigger(Trigger trigger)
    {
        if (trigger.Kind != TriggerKind.Event)
            return Editing.ShotEdit.Describe(trigger);
        string delay = trigger.Delay == 0.0 ? ""
            : (trigger.Delay > 0.0 ? " +" : " ") + trigger.Delay.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " s";
        return "on " + EventName(trigger.Event) + delay;
    }

    // Only a text that starts with an event name changes.
    public static string StartsText(string text, IReadOnlyDictionary<string, string> aliases)
    {
        int end = text.IndexOf(' ');
        string head = end < 0 ? text : text[..end];
        if (head.Length == 0 || !Director.EventName.TryResolve(head, aliases, out _, out _))
            return text;
        return EventDisplay.Name(head) + (end < 0 ? "" : text[end..]);
    }

    #endregion

    // The key is the ImGui id and the buffer key. It must differ between two fields that show at the
    // same time and between two objects that the same field shows one after the other.
    public static bool Text(string label, string key, string value, out string result, int capacity = 512, string? tip = null)
    {
        result = value;
        if (!Buffers.TryGetValue(key, out ImInputString? buffer))
        {
            int size = Math.Max(capacity, System.Text.Encoding.UTF8.GetByteCount(value) + 64);
            buffer = new ImInputString(size);
            Buffers[key] = buffer;
            buffer.Value16 = value;
        }
        else if (key != _activeTextKey && buffer.ToString() != value)
        {
            buffer.Value16 = value;
        }

        BeginRow(label);
        PushWell(ConsoleWidgets.RowControlWidth);
        bool changed = ImGui.InputText(("##" + IdOf(key)).AsSpan(), buffer);
        bool active = ImGui.IsItemActive();
        PopWell();
        EndRow(tip);
        if (active)
            _activeTextKey = key;
        else if (_activeTextKey == key)
            _activeTextKey = null;

        if (!changed)
            return false;
        result = buffer.ToString();
        return true;
    }

    public static float4 StateColor(Editing.RunState state) => state switch
    {
        Editing.RunState.Idle => MutedColor,
        Editing.RunState.Paused => WarnColor,
        _ => GoodColor,
    };
}
