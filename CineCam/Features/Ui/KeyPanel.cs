using Brutal.ImGuiApi;
using CineCam.Director;
using CineCam.Editing;
using CineCam.Features.Takes;

namespace CineCam.Features.Ui;

// In a pause a new shot goes into the file of the window only, because the timeline of the running
// take is fixed when the take starts. In a replay the replay plays it at once.
internal static class KeyPanel
{
    private const string KeyPopup = "##cinecam-add-key";
    private const string PresetPopup = "##cinecam-add-preset";

    private static readonly KeyKind[] Kinds = Enum.GetValues<KeyKind>();

    private static string _message = "";
    private static bool _messageIsError;

    public static void Draw(EditSession session)
    {
        string block = Block(session);
        bool free = block.Length == 0;
        if (Widgets.Button("ADD KEY HERE", free, free
                ? "Takes the place and the view of the free camera and adds a shot of a kind you pick, after the selected shot."
                : block))
            ImGui.OpenPopup(KeyPopup.AsSpan());
        Widgets.SameLine();
        if (Widgets.Button("ADD PRESET", free, free
                ? "Adds a shot from a template, sized for the vehicle, after the selected shot."
                : block))
            ImGui.OpenPopup(PresetPopup.AsSpan());
        Widgets.Reason(block);

        if (ImGui.BeginPopup(KeyPopup.AsSpan()))
        {
            try
            {
                Widgets.Label("Make a shot from the camera", Widgets.MutedColor);
                foreach (KeyKind kind in Kinds)
                {
                    if (ImGui.Selectable(ShotCapture.KeyLabel(kind).AsSpan()))
                        AddKey(session, kind);
                    Widgets.Tip(ShotCapture.KeyHelp(kind));
                }
            }
            finally
            {
                ImGui.EndPopup();
            }
        }

        if (ImGui.BeginPopup(PresetPopup.AsSpan()))
        {
            try
            {
                Widgets.Label("Add a shot from a template", Widgets.MutedColor);
                foreach (ShotPreset preset in ShotPresets.All)
                {
                    if (ImGui.Selectable(ShotPresets.Label(preset).AsSpan()))
                        AddPreset(session, preset);
                    Widgets.Tip(ShotPresets.Help(preset));
                }
            }
            finally
            {
                ImGui.EndPopup();
            }
        }

        if (_message.Length > 0)
            Widgets.Colored(_message, _messageIsError ? Widgets.ErrorColor : Widgets.MutedColor);
    }

    private static string Block(EditSession session) => KeyCapture.Block(session);

    private static void AddKey(EditSession session, KeyKind kind)
    {
        bool ok = KeyCapture.AddKey(session, kind, out string message);
        SetMessage(message, !ok);
    }

    private static void AddPreset(EditSession session, ShotPreset preset)
    {
        bool ok = KeyCapture.AddPreset(session, preset, out string message);
        SetMessage(message, !ok);
    }

    private static void SetMessage(string text, bool isError)
    {
        _message = text;
        _messageIsError = isError;
    }
}
