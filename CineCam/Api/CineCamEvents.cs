namespace CineCam.Api;

/// <summary>
/// The integration surface of CineCam for other mods. The events use only strings, numbers and booleans, so a
/// mod can subscribe through reflection without a reference to CineCam, see docs/integration.md. CineCam raises
/// every event on the main thread of the game, in the frame in which the change happens and before the game
/// draws its interface, so a handler can draw the change in the same frame. A handler that throws is logged once
/// and does not stop the other handlers or CineCam.
/// </summary>
/// <remarks>
/// The take modes are "live" (a take on the live flight), "dryRun" (a live take that changes neither the
/// flight, the sim speed nor the interface), "replay" (a recorded flight plays the take) and "render" (a
/// replay saves every picture to a file). A replay runs the take from the moment the play starts until the
/// play stops: a pause, the end, a seek, an edit of the take and the close each end that run, and the next play
/// starts a new one. A render is one run from its first to its last picture.
/// </remarks>
public static class CineCamEvents
{
    /// <summary>Rises when an event, a parameter or a meaning changes in a way that an older subscriber can notice.</summary>
    public const int ApiVersion = 1;

    #region Events

    /// <summary>A take started: the take name, the mode, and whether CineCam hides the game interface now.</summary>
    public static event Action<string, string, bool>? TakeStarted;

    /// <summary>A take ended: the take name, the mode and the reason in plain words.</summary>
    public static event Action<string, string, string>? TakeEnded;

    /// <summary>A shot started: its index in the take (from 0), its type ("orbit", "chase" and so on) and its name, empty when it has none.</summary>
    public static event Action<int, string, string>? ShotStarted;

    /// <summary>
    /// A flight event came: the canonical name as the shot file writes it ("liftoff", "sequence:3",
    /// "altitude:5000"), the take seconds and the game seconds of the event. In a replay the game seconds are
    /// those of the recorded flight.
    /// </summary>
    public static event Action<string, double, double>? FlightEvent;

    /// <summary>A cue of the take fired: its name as the take writes it, the take seconds and the game seconds.</summary>
    public static event Action<string, double, double>? Cue;

    /// <summary>CineCam hid the game interface (true) or showed it again (false), see <see cref="InterfaceHidden"/>.</summary>
    public static event Action<bool>? InterfaceChanged;

    /// <summary>A replay opened, with the id of its recording.</summary>
    public static event Action<string>? ReplayOpened;

    /// <summary>A replay closed, with the id of its recording.</summary>
    public static event Action<string>? ReplayClosed;

    /// <summary>A render started: the take name and the folder of the render.</summary>
    public static event Action<string, string>? RenderStarted;

    /// <summary>A render ended: the take name, the folder of the render and the reason, "done" when every picture was saved.</summary>
    public static event Action<string, string, string>? RenderEnded;

    #endregion

    #region State

    public static bool IsTakeRunning { get; private set; }

    /// <summary>Empty while no take runs.</summary>
    public static string TakeName { get; private set; } = "";

    /// <summary>"live", "dryRun", "replay" or "render", empty while no take runs.</summary>
    public static string TakeMode { get; private set; } = "";

    /// <summary>The take seconds of the running take, the time of the video. A pause for an edit stops it.</summary>
    public static double TakeSeconds { get; private set; }

    /// <summary>The index of the shot that shows, or -1.</summary>
    public static int ShotIndex { get; private set; } = -1;

    /// <summary>Empty while no shot shows.</summary>
    public static string ShotType { get; private set; } = "";

    /// <summary>Empty while no shot shows or the shot has no name.</summary>
    public static string ShotName { get; private set; } = "";

    /// <summary>
    /// True while CineCam keeps the game interface hidden: a take with the option to hide it, a render, or an
    /// open replay in which the player hid it with the window key.
    /// </summary>
    public static bool InterfaceHidden { get; private set; }

    public static bool IsReplayOpen { get; private set; }

    /// <summary>The id of the recording of the open replay, or empty.</summary>
    public static string ReplayName { get; private set; } = "";

    public static bool IsRendering { get; private set; }

    /// <summary>
    /// The names of the cues of the running take that fired, in the order in which they fired. A replay run
    /// that starts after a cue counts that cue as fired. A new array on each read.
    /// </summary>
    public static string[] PassedCues => [.. Passed];

    private static readonly List<string> Passed = new();

    #endregion

    #region Raise

    /// <summary>Gets the text of a handler error. The game side sets it, the tests leave it null.</summary>
    internal static Action<string>? ErrorLog { get; set; }

    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);

    internal static void RaiseTakeStarted(string take, string mode, bool interfaceHidden, double takeSeconds, IEnumerable<string> passedCues)
    {
        IsTakeRunning = true;
        TakeName = take;
        TakeMode = mode;
        TakeSeconds = takeSeconds;
        Passed.Clear();
        Passed.AddRange(passedCues);
        SetShot(-1, "", "");
        Invoke(TakeStarted, nameof(TakeStarted), h => h(take, mode, interfaceHidden));
    }

    internal static void RaiseTakeEnded(string reason)
    {
        if (!IsTakeRunning)
            return;
        string take = TakeName, mode = TakeMode;
        IsTakeRunning = false;
        TakeName = "";
        TakeMode = "";
        TakeSeconds = 0.0;
        Passed.Clear();
        SetShot(-1, "", "");
        Invoke(TakeEnded, nameof(TakeEnded), h => h(take, mode, reason));
    }

    internal static void SetTakeSeconds(double seconds)
    {
        if (IsTakeRunning)
            TakeSeconds = seconds;
    }

    /// <summary>Sets the shot that shows without an event, for a blend back or the shot that runs at the start of a run.</summary>
    internal static void SetShot(int index, string type, string name)
    {
        ShotIndex = index;
        ShotType = index >= 0 ? type : "";
        ShotName = index >= 0 ? name : "";
    }

    internal static void RaiseShotStarted(int index, string type, string name)
    {
        if (!IsTakeRunning)
            return;
        SetShot(index, type, name);
        Invoke(ShotStarted, nameof(ShotStarted), h => h(index, type, name));
    }

    internal static void RaiseFlightEvent(string name, double takeSeconds, double gameSeconds)
    {
        if (IsTakeRunning)
            Invoke(FlightEvent, nameof(FlightEvent), h => h(name, takeSeconds, gameSeconds));
    }

    internal static void RaiseCue(string name, double takeSeconds, double gameSeconds)
    {
        if (!IsTakeRunning)
            return;
        Passed.Add(name);
        Invoke(Cue, nameof(Cue), h => h(name, takeSeconds, gameSeconds));
    }

    /// <summary>Raises <see cref="InterfaceChanged"/> only when the value differs from the last one.</summary>
    internal static void SyncInterface(bool hidden)
    {
        if (hidden == InterfaceHidden)
            return;
        InterfaceHidden = hidden;
        Invoke(InterfaceChanged, nameof(InterfaceChanged), h => h(hidden));
    }

    internal static void RaiseReplayOpened(string recording)
    {
        IsReplayOpen = true;
        ReplayName = recording;
        Invoke(ReplayOpened, nameof(ReplayOpened), h => h(recording));
    }

    internal static void RaiseReplayClosed()
    {
        if (!IsReplayOpen)
            return;
        string recording = ReplayName;
        IsReplayOpen = false;
        ReplayName = "";
        Invoke(ReplayClosed, nameof(ReplayClosed), h => h(recording));
    }

    internal static void RaiseRenderStarted(string take, string folder)
    {
        IsRendering = true;
        Invoke(RenderStarted, nameof(RenderStarted), h => h(take, folder));
    }

    internal static void RaiseRenderEnded(string take, string folder, string reason)
    {
        if (!IsRendering)
            return;
        IsRendering = false;
        Invoke(RenderEnded, nameof(RenderEnded), h => h(take, folder, reason));
    }

    /// <summary>
    /// Clears the state and the handlers when the mod unloads. A subscriber of a later load subscribes again,
    /// because the handlers belong to this load of the assembly.
    /// </summary>
    internal static void Reset()
    {
        TakeStarted = null;
        TakeEnded = null;
        ShotStarted = null;
        FlightEvent = null;
        Cue = null;
        InterfaceChanged = null;
        ReplayOpened = null;
        ReplayClosed = null;
        RenderStarted = null;
        RenderEnded = null;
        IsTakeRunning = false;
        TakeName = "";
        TakeMode = "";
        TakeSeconds = 0.0;
        Passed.Clear();
        SetShot(-1, "", "");
        InterfaceHidden = false;
        IsReplayOpen = false;
        ReplayName = "";
        IsRendering = false;
        Reported.Clear();
    }

    // Each handler runs on its own, so a handler that throws or unsubscribes does not skip the others.
    private static void Invoke<T>(T? handlers, string name, Action<T> call) where T : Delegate
    {
        if (handlers == null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                call((T)handler);
            }
            catch (Exception ex)
            {
                string who = $"{handler.Method.DeclaringType?.FullName}.{handler.Method.Name}";
                if (Reported.Add(name + ":" + who))
                    ErrorLog?.Invoke($"[CineCam] A handler of CineCamEvents.{name} in {who} failed, and later errors of this handler are not logged: {ex}");
            }
        }
    }

    #endregion
}
