using Brutal.ImGuiApi;
using Brutal.Numerics;
using CineCam.Features.Replay;
using CineCam.Features.Takes;
using KSA;

namespace CineCam.Features.Ui;

// The strip shown while a flight records. The transport of a replay is in the timeline dock (LiveTimeline).
internal static class ReplayOverlay
{
    private static bool _stripHidden;
    private static bool _wasRecording;
    private const ImGuiWindowFlags Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing;

    public static void Draw()
    {
        bool recording = ReplaySession.IsRecording;
        if (recording && !_wasRecording)
            _stripHidden = false;
        _wasRecording = recording;
        // A take that records hides the strip, because it would be in the video.
        if (recording && !CineCamWindow.IsOpen && !_stripHidden && TakeRunner.Current is not { IsRecording: true, IsPaused: false })
            DrawRecording();
    }

    #region Recording strip

    private static readonly string OpenLabel = UiText.MessageText("text.bb0d56a0e21f", "OPEN CINECAM");
    private static readonly string StopLabel = UiText.MessageText("text.3a93055fe99f", "STOP RECORDING");
    private static readonly string HideLabel = UiText.MessageText("text.0c944fca9b62", "HIDE");

    // The strip sits below the stock clock and sim speed panel.
    private static void DrawRecording()
    {
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        float scale = ImGuiHelper.InterfaceScale;
        string time = UiText.Format("", $"{ReplaySession.RecordingSeconds:F1} s");

        ImGui.SetNextWindowPos(new float2(viewport.WorkPos.X + viewport.WorkSize.X * 0.5f, viewport.WorkPos.Y + 76f * scale),
            ImGuiCond.None, new float2(0.5f, 0f));
        DockStyle.BeginPanel("##cinecam-recording-strip", Flags | ImGuiWindowFlags.AlwaysAutoResize);
        try
        {
            float gap = ImGui.GetStyle().ItemSpacing.X;
            float buttonHeight = ConsoleWidgets.ButtonHeight;
            float dot = DockStyle.FontSize(false) * 0.5f;
            float head = dot + gap + DockStyle.TextWidth(UiText.MessageText("text.ac0a7f145139", "RECORDING"), false) + gap + DockStyle.TextWidth(time, false);
            float buttons = ConsoleWidgets.ButtonWidth(UiText.Visible(OpenLabel).AsSpan()) + ConsoleWidgets.ButtonWidth(UiText.Visible(StopLabel).AsSpan())
                + ConsoleWidgets.ButtonWidth(UiText.Visible(HideLabel).AsSpan()) + gap * 2f;
            float pad = ConsoleStyle.ContentPaddingPx;
            bool oneRow = head + gap * 2f + buttons + pad * 2f <= viewport.WorkSize.X - 16f * scale;

            // The head is one item of the height of a button, so the texts and the buttons share a line.
            float2 at = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new float2(head, buttonHeight));
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            float mid = at.Y + buttonHeight * 0.5f;
            draw.AddCircleFilled(new float2(at.X + dot * 0.5f, mid), dot * 0.5f, DockStyle.Ink(ConsoleStyle.DangerU32));
            ConsoleStyle.PushLabelFont();
            try
            {
                float textY = mid - ImGui.GetFontSize() * 0.5f;
                float x = at.X + dot + gap;
                draw.AddText(new float2(x, textY), DockStyle.Ink(ConsoleStyle.DangerU32), UiText.Render(UiText.MessageText("text.ac0a7f145139", "RECORDING")));
                x += ImGui.CalcTextSize(UiText.Render(UiText.MessageText("text.ac0a7f145139", "RECORDING"))).X + gap;
                draw.AddText(new float2(x, textY), DockStyle.Ink(ConsoleStyle.TextPrimaryU32), UiText.Render(time));
            }
            finally { ConsoleStyle.PopFont(); }

            if (oneRow) ImGui.SameLine(0f, gap * 2f);
            if (DockStyle.Button(OpenLabel, tip: UiText.MessageText("text.9165d39c4808", "Opens the CineCam window. The recording goes on.")))
                CineCamWindow.Instance.Open();
            if (oneRow) ImGui.SameLine();
            if (DockStyle.Button(StopLabel, kind: DockStyle.Kind.Danger))
                ReplaySession.StopRecording();
            if (oneRow) ImGui.SameLine();
            if (DockStyle.Button(HideLabel, tip: UiText.MessageText("text.32d596aaab38", "Hides this strip. The recording goes on.")))
                _stripHidden = true;
        }
        finally { DockStyle.EndPanel(); }
    }

    #endregion
}
