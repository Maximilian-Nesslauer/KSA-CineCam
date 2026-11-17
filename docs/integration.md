# Integration for other mods

This page is for authors of other mods. Back to the [README](../README.md).

CineCam tells other mods what it does: a take starts and ends, a shot starts, a flight event comes, a cue of the take fires, the game interface is hidden or shown, a replay opens and closes, and a render starts and ends. A mod can use this, for example, to show its own overlay at the lift-off of a take while CineCam hides the game interface.

Your mod does not need a reference to `CineCam.dll`. A reference would fail to load when CineCam is not installed. Subscribe through reflection instead, as in the example below. All events and properties use only `string`, `int`, `double` and `bool`, so the delegate types are plain `System.Action` types.

## The class `CineCam.Api.CineCamEvents`

`CineCamEvents.ApiVersion` (a public constant `int`) is 1. It goes up when an event, a parameter or a meaning changes in a way that an older subscriber can notice. New events do not change it.

### Events

| Event | Type | Parameters |
| --- | --- | --- |
| `TakeStarted` | `Action<string, string, bool>` | take name, mode, the interface is hidden now |
| `TakeEnded` | `Action<string, string, string>` | take name, mode, reason in plain words |
| `ShotStarted` | `Action<int, string, string>` | shot index from 0, shot type (`orbit`, `crane`, `tripod`, `track`, `chase`, `part`, `path`), shot name (empty when the shot has none) |
| `FlightEvent` | `Action<string, double, double>` | event name, take seconds, game seconds |
| `Cue` | `Action<string, double, double>` | cue name, take seconds, game seconds |
| `InterfaceChanged` | `Action<bool>` | true when CineCam hid the game interface, false when it shows again |
| `ReplayOpened` | `Action<string>` | id of the recording |
| `ReplayClosed` | `Action<string>` | id of the recording |
| `RenderStarted` | `Action<string, string>` | take name, folder of the render |
| `RenderEnded` | `Action<string, string, string>` | take name, folder of the render, reason (`done` when every picture was saved) |

The mode is `live` (a take on the live flight), `dryRun` (a live take that does not stage, does not set the sim speed and does not hide the interface), `replay` (a recorded flight plays the take) or `render` (a replay saves each picture to a file). A shot preview in the editor gives no events.

The event name is the canonical name of the [shot file](shot-file.md#events), for example `ignition`, `liftoff`, `sequence:3`, `altitude:5000` or `mark:2`. An alias of the shot file is not used. The take seconds are the time of the video: a pause for an edit stops them, and in a render they are the time of the picture. In a replay and a render, the game seconds are those of the recorded flight.

### Properties

| Property | Type | Value |
| --- | --- | --- |
| `IsTakeRunning` | `bool` | a take runs |
| `TakeName`, `TakeMode` | `string` | of the running take, empty otherwise |
| `TakeSeconds` | `double` | take seconds of the running take |
| `ShotIndex` | `int` | the shot that shows, or -1 |
| `ShotType`, `ShotName` | `string` | of the shot that shows, empty otherwise |
| `InterfaceHidden` | `bool` | CineCam keeps the game interface hidden: a take with `hideUi`, a render, or a replay in which the player hid the interface with the window key |
| `IsReplayOpen`, `ReplayName` | `bool`, `string` | the open replay and the id of its recording |
| `IsRendering` | `bool` | a render runs |
| `PassedCues` | `string[]` | the cues of the running take that fired, in order. A new array on each read |

### When the events come

- CineCam raises every event on the main thread of the game, in the frame of the change, before the game draws its interface. A handler can therefore change what it draws in the same frame.
- A handler that throws is logged once and does not stop the other handlers. Keep the handlers short, because they run inside the frame.
- In one frame the order is: flight events, then shots, then cues. `InterfaceChanged` comes before `TakeStarted` when a take hides the interface at its start, and before `TakeEnded` at the end. `RenderStarted` comes before the `TakeStarted` of the render, and `RenderEnded` after its `TakeEnded`.
- When an explosion destroys the live vehicle, its final `FlightEvent` and cues due at that time come before `TakeEnded`. A cue with a later delay does not fire after the take ends, and no stage action runs on the destroyed vehicle.
- A live take hides the interface at its start or at the event of `hideUiAt`. A pause for an edit shows the interface, so `InterfaceChanged(false)` and later `InterfaceChanged(true)` come.
- A replay runs the take while it plays. A pause, the end of the replay, a seek, an edit of the take and the close each end that run with `TakeEnded`, and the next play starts a new run with `TakeStarted`. A run that starts after a cue counts that cue in `PassedCues`, so read `PassedCues` in your `TakeStarted` handler to set your state. When a run starts in the middle of a shot, `ShotStarted` comes for that shot.
- While `IsReplayOpen` is true, a `TakeEnded` does not mean that the take is over: the replay is only paused or moved. Keep your take state until the next `TakeStarted` or until `ReplayClosed`, and do not treat a paused replay as "no take", or an overlay goes in and out at each pause.
- A render is one run from its first to its last picture. Its frames step by the picture time, so an animation that counts the frame time (the `dtPlayer` of the game) runs at the speed of the video.
- When CineCam unloads, it ends a running take (with its events) and then removes all handlers.

## Cues

A cue is an action of a take that gives a name to other mods at a take time or at an event. It changes nothing in the flight. In the shot file:

```json
"actions": [
  { "at": { "event": "liftoff" }, "action": "cue", "name": "overlay-in" },
  { "at": { "event": "liftoff", "delay": 40 }, "action": "cue", "name": "overlay-out" }
]
```

In the window, add an action on the **Actions** tab and set **Action** to **Cue**. The name has 1 to 64 letters, digits and the signs `-`, `_`, `.` and `:`. CineCam does not change it, so compare it as you like; ordinal and case-insensitive is a good choice. A cue fires in a live take, a dry run, a replay and a render, once per run, and it has a row in `events.csv` (kind `cue`). See [the actions](shot-file.md#actions).

## Example: subscribe without a reference

The class below finds CineCam at run time and subscribes four handlers. Call `CineCamLink.TryAttach()` in your `[StarMapAllModsLoaded]` method and `CineCamLink.Detach()` in your `[StarMapUnload]` method. When CineCam is not installed, `TryAttach` returns false and nothing else happens.

```csharp
using System.Reflection;

internal static class CineCamLink
{
    private static readonly List<(EventInfo Event, Delegate Handler)> Attached = new();
    private static Type? _api;

    public static bool TryAttach()
    {
        Type? api = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("CineCam.Api.CineCamEvents", throwOnError: false))
            .FirstOrDefault(type => type != null);
        if (api == null || api.GetField("ApiVersion")?.GetValue(null) is not int version || version != 1)
            return false;
        _api = api;
        return Attach(api, "Cue", nameof(OnCue)) & Attach(api, "TakeStarted", nameof(OnTakeStarted))
            & Attach(api, "TakeEnded", nameof(OnTakeEnded)) & Attach(api, "ReplayClosed", nameof(OnReplayClosed));
    }

    public static void Detach()
    {
        foreach ((EventInfo info, Delegate handler) in Attached)
            info.RemoveEventHandler(null, handler);
        Attached.Clear();
        _api = null;
    }

    private static bool Attach(Type api, string eventName, string handlerName)
    {
        EventInfo? info = api.GetEvent(eventName, BindingFlags.Public | BindingFlags.Static);
        MethodInfo? method = typeof(CineCamLink).GetMethod(handlerName, BindingFlags.NonPublic | BindingFlags.Static);
        if (info?.EventHandlerType == null || method == null)
            return false;
        Delegate? handler = Delegate.CreateDelegate(info.EventHandlerType, method, throwOnBindFailure: false);
        if (handler == null)
            return false;
        info.AddEventHandler(null, handler);
        Attached.Add((info, handler));
        return true;
    }

    private static void OnCue(string name, double takeSeconds, double gameSeconds)
    {
        if (string.Equals(name, "overlay-in", StringComparison.OrdinalIgnoreCase))
            MyOverlay.PlayIntro();
        else if (string.Equals(name, "overlay-out", StringComparison.OrdinalIgnoreCase))
            MyOverlay.PlayOutro();
    }

    private static void OnTakeStarted(string take, string mode, bool interfaceHidden)
    {
        // A replay run can start after a cue, so the state of the overlay comes from the cues that fired.
        string[] passed = _api?.GetProperty("PassedCues")?.GetValue(null) as string[] ?? [];
        int lastIn = Array.FindLastIndex(passed, cue => cue.Equals("overlay-in", StringComparison.OrdinalIgnoreCase));
        int lastOut = Array.FindLastIndex(passed, cue => cue.Equals("overlay-out", StringComparison.OrdinalIgnoreCase));
        MyOverlay.SetVisible(lastIn > lastOut);
    }

    // A replay ends its run at each pause and seek, so the overlay keeps its state until the replay closes.
    private static void OnTakeEnded(string take, string mode, string reason)
    {
        if (_api?.GetProperty("IsReplayOpen")?.GetValue(null) is not true)
            MyOverlay.PlayOutro();
    }

    private static void OnReplayClosed(string recording) => MyOverlay.PlayOutro();
}
```

`MyOverlay` stands for your own code. A handler whose parameters do not match the event gives no delegate (`throwOnBindFailure: false`), so the check of `TryAttach` also finds an API change. To draw while CineCam hides the game interface, draw from a hook that runs when `Program.DrawUI` is false, as CineCam does for its sync flash with a postfix of `Program.DrawFps`; the `StarMapAfterGui` hook runs only while the interface shows.

## Limits

- In a replay and a render the game still has the live vehicle, which the replay holds still and hides. Values that a mod reads from the game (`Program.ControlledVehicle`, its speed and altitude) are those of the live flight, not of the recording. The events and their game seconds come from the recording.
- The events tell what CineCam does. They give no control over CineCam.
