# Shot file reference

This page is the reference of the shot file format, the take folder and the event log. Back to the [README](../README.md).

The window writes this file. You never have to open it, and everything below can be set in the window. A hand-written file is useful for sharing a take or for a large edit.

The shot file is `Documents\My Games\Kitten Space Agency\CineCam\shots.json`. The mod writes an example when the file is missing. Set the environment variable `CINECAM_SHOTS` to use another file. The file is JSON with comments and trailing commas. A save keeps a `.bak` copy of the old file and refuses a file with errors. The reader names the bad field, for example `takes[0].camera[2].duration`. A field that this version does not know is kept when the file is saved. Comments are not kept.

## Frames and units

All positions are in meters and all angles in degrees. The frames of the window have these names in the file:

- The **site frame** (the World point frame) has its origin at a point on the ground (a Landmark, the anchor of the take, or the ground under the vehicle at the take start), +X up, +Y east and +Z north. The frame is fixed to the body and turns with it.
- The **vehicle frame** has its origin at the base of the vehicle, with +X toward the nose, +Y to the right and +Z down.
- The **local frame** (chase, part and vehicle-target path shots with `"frame": "local"`, shown as "horizon level") has its origin at the vehicle, or at the part for a part shot. +X points away from the center of the body, +Y east and +Z north, the same axes as the site frame, but the frame moves with the vehicle, so +X stays "up" over the whole flight. North comes from the rotation axis of the body, east is north cross up.
- The **flight frame** (`"frame": "flight"`) is the local frame turned about +X, so that +Z points along the horizontal direction of flight, +X is still away from the center of the body, and +Y is Z cross X, which makes the frame right handed. The direction of flight is the velocity over the surface (the inertial velocity minus the rotation of the body, as `Vehicle.GetSurfaceVelocityCci` gives it) without its part along up. Below 2 m/s of horizontal speed the direction holds where it was (north, when the vehicle never had a direction, for example on the pad), so a stop, a vertical ascent or a fall straight down keeps the frame steady. Above 2 m/s it turns toward the direction of the velocity, slowly near 2 m/s and at up to 180 degrees per second above 6 m/s, so a reverse turns the frame around in about one second and a velocity that crosses any direction gives no jump. In normal flight the velocity turns slower than that, and the direction is the direction of the velocity. The velocity is smoothed over about one second, so the frame does not jitter. A replay measures the velocity at the center of mass, as the game does for a live take, and after a seek it starts from the direction that the play holds there.

A shot with `"target": "site"` uses the site frame, and a shot with `"target": "vehicle"` uses the vehicle frame and moves with the vehicle. The default target is `site` for orbit, tripod and path, and `vehicle` for the others. The azimuth of an orbit or a crane is 0 at +Z (north) and 90 at +Y (east).

## Top level fields and takes

Top level fields:

| Field | Meaning |
| --- | --- |
| `formatVersion` | The version of the file format. The mod writes `2`. A file without it is version 1 and is read as before. A file with a higher version is read with a warning, and the fields that this version does not know are kept |
| `site` | Landmark id of the launch site. A take can set its own `site` |
| `fps` | Frames per second of the video. Default 60 |
| `hideUi` | Hides the game interface during a take. Default `false` |
| `hideUiAt` | An event (or alias) at which `hideUi` hides the interface, for example `"ignition"`. `"start"` or no value hides it at the take start. A take can set its own `hideUiAt` |
| `syncFlash` | Draws one white frame at the take start, as a mark for the cut. Default `false` |
| `capture` | The capture options, see below |
| `events` | Names for events, for example `"fairing": "sequence:3"` |
| `keepOut` | Boxes the camera must stay out of, see the keep-out boxes. A take can set its own `keepOut`, and then the take wins |
| `takes` | The list of takes. A file with one take can keep `camera` and `speed` at the top level |

A take has `name`, `default` (the take that starts without a name), `site`, `anchor`, `checkpoint`, `autoStart`, `autoStartDelay`, `preRoll`, `camera` (a list of shots, at least one), `speed` (a list of speed keys) and `actions` (a list of actions).

`anchor` is a point on a body that is the origin of the site frame when no Landmark is named: `{ "body": "Earth", "latitude": 28.6, "longitude": -80.6, "altitude": 0 }`. Latitude and longitude are in degrees (north and east are positive, longitude 0 is the 0 meridian of the body), and `altitude` is in meters above the terrain (default 0). When a take names a `site` too, the Landmark wins and the anchor has no effect.

## Shots

Fields of every shot:

| Field | Meaning |
| --- | --- |
| `type` | `orbit`, `crane`, `tripod`, `track`, `chase`, `part` or `path`. The window calls a tripod "Fixed" and a part shot "Part mount" |
| `duration` | Length in seconds, greater than 0 |
| `target` | `site` or `vehicle`. A chase, part, path or track shot can also use `separated`, see below |
| `fov` | Vertical field of view, 1 to 150. Default 40 |
| `roll` | Turn of the camera about its view direction in degrees, -180 to 180. 0 (default) keeps the horizon level, a positive value turns the picture counterclockwise |
| `up` | Where the camera up comes from. `vehicle` keeps the up of the shot frame (for a vehicle frame the nose). `horizon` uses the direction away from the center of the body at the camera, so the horizon is level and the Earth is at the bottom. The default is `horizon` for `"frame": "local"` and `vehicle` for everything else. When the camera looks straight along the up direction, the roll stays continuous |
| `ease` | `none` (default; `inOut` for a path), `in`, `out` or `inOut` |
| `transition` | `"cut"` (default), `"blend"` (3 seconds), a number of blend seconds, or `{ "type": "blend", "seconds": 1.5 }` |
| `at` | When the shot starts, see below. The default is after the shot before |
| `avoidSun` | The smallest angle in degrees between the view direction and the direction to the sun, greater than 0 and at most 90, or `"off"` (the default). A value from 25 to 35 keeps the sun out of the picture and still lets it light the vehicle |

Fields of one shot type:

| Type | Fields |
| --- | --- |
| `orbit` | `center` (default `[0,0,0]`), `radius`, `height`, `startDeg`, `degPerSec`, `lookAt`. The camera circles the +X axis through `center` |
| `crane` | `from`, `to` (heights along +X), `radius`, `azimuth`. The camera rises and looks at the axis |
| `tripod` | `position`, `lookAt`. A fixed camera |
| `track` | `position`, `keepSize`, `fit`. A fixed camera in the site frame that looks at the middle of the vehicle. With `keepSize` the field of view follows the distance, so the vehicle keeps its size on the screen |
| `chase` | `frame`, `offset`, `lookAt`, `smoothing`, `fit`. Fixed to the vehicle. `frame` is `vehicle` (default), `local` or `flight`. `smoothing` is the turn smoothing in seconds (0 to 60, default 0.5). It smooths the turn of the vehicle frame, so a local shot ignores it |
| `part` | `part`, `frame`, `offset`, `lookAt`, `smoothing`, `fit`. Like chase, with the origin at a part. `part` is the id or the display name of a part. An empty or unknown part uses the base of the vehicle |
| `path` | `points`, `fit`, and `frame` for a vehicle-target path. A camera move through keyframes |

A chase, part, path or track shot can also have `aim`, `aimOffset` and `pick`.

A path shot has a list of `points`. Each point is `{ "t": 0.5, "position": [x, y, z], "lookAt": [x, y, z], "fov": 30, "roll": 5 }`. `t` is the share of the path from 0 to 1 at which the camera is at `position` and looks at `lookAt`. The times must rise from point to point, and the first one need not be 0. `fov` and `roll` are optional: a point without `fov` takes the `fov` of the shot, a point without `roll` has 0, and the `roll` of the shot is added to the roll of the path. The points are in the frame that `target` and `frame` name. The position and the look point run through the points on a smooth cubic curve (a centripetal Catmull-Rom spline, so the curve has no loops and no cusps), and the speed has no jump at a point. The field of view and the roll follow a monotone curve. The `ease` of the shot shapes the time over the path.

```json
{ "type": "path", "target": "site", "duration": 12, "fov": 35,
  "points": [
    { "t": 0, "position": [3, -80, -60], "lookAt": [15, 0, 0] },
    { "t": 0.6, "position": [6, -40, -25], "lookAt": [10, 0, 0] },
    { "t": 1, "position": [22, -12, -8], "lookAt": [25, 0, 0], "fov": 22 }
  ] }
```

In a blend, the weight follows a smootherstep curve, so the camera starts and stops gently. The point that the camera looks at is mixed with the same weight, so a subject that both shots look at stays in the picture. When the two shots are tied to different frames (for example a site tripod and a vehicle chase), the camera position follows a curve that swings around the subject instead of the straight line. A blend between two shots of the same frame stays a straight line. A cut is not changed.

`fit` is a number, the share of the picture height that the whole vehicle should fill, for example `0.7`, or `"off"` (the default). The field of view is set in every frame from the distance to the middle of the vehicle and from the size of the vehicle, so the vehicle keeps its share while it flies away. The field of view stays between 5 and 90 degrees. `fit` replaces the `fov` of the shot and of its path points, and `keepSize`. A part shot frames the whole vehicle, not the part.

```json
{ "type": "chase", "frame": "flight", "offset": [15, 8, 120], "lookAt": [10, 0, 0], "fit": 0.7, "duration": 12, "transition": 2 }
```

`"aim": "vehicle"` (the default is `"look"`) makes the camera look at the center of the bounding box of the vehicle in every frame, and `lookAt` or the look points of a path are not used. The mod measures the extent again when the part count changes, so the aim follows the vehicle after a stage separation. `aimOffset` is an optional `[x, y, z]` that moves the look point away from the center, in the frame of the shot.

`"avoidSun": 30` keeps the angle between the view direction and the direction to the sun at 30 degrees or more. When the shot looks closer to the sun, the camera moves on a circle around its look point until the angle is reached. The look point and the distance to it stay. The move eases over about one second. The check is skipped while the body hides the sun from the camera. A preview in the window does not use `avoidSun`.

`"target": "separated"` makes the shot follow the vehicle that most recently split off from the controlled vehicle, with `fit`, `aim` and the offsets applied to that vehicle. A vehicle counts when it was not in the star system at the take start, orbits the same body as the controlled vehicle, and was within 200 m of it when it first showed up. A decoupler makes such a vehicle, and so does an undocking. `"pick": "newest"` (the default) takes the one that appeared last, and `"pick": "nearest"` the one that is nearest to the controlled vehicle now. A vehicle that was destroyed is dropped. While no vehicle has separated, the shot follows the controlled vehicle. In the window this is the **Follows** and **Pick** fields.

```json
{ "type": "chase", "target": "separated", "pick": "newest", "frame": "local", "offset": [0, 30, -20], "aim": "vehicle", "fit": 0.5, "avoidSun": 30, "duration": 8, "transition": 1 }
```

## Pre-roll and triggers

`"preRoll": { "until": "liftoff", "loop": true }` is a field of a take. The shots before the first shot that starts on that event are the pre-roll block. They repeat as a loop, each shot after the one before with its own `transition`, until the event comes. Then the shot that is active is left through the transition of the waiting shot. The first shot may start at a time. The other shots of the block must follow the one before. Without `preRoll`, or with `"loop": false`, the shots play once, and a shot that waits holds the last pose of the shot before. In the window this is the switch **Repeat the opening shots until an event** above the shot list, with the field **Until the event** and the switch **Loop the opening shots** (off plays them once and holds the last pose).

A trigger (`at` of a shot, a speed key or an action) is a take time in seconds (`"at": 12.5`), an object with a time (`{ "time": 12.5 }`), or an event with a delay (`{ "event": "liftoff", "delay": 3 }`). A negative delay works only for altitude and speed events. For other events it is set to 0 with a warning. A blend needs seconds above 0, and it lasts 3 seconds when the file gives no length.

## Speed keys

A speed key:

| Field | Meaning |
| --- | --- |
| `at` | When the key starts |
| `to` | The sim speed to reach, 0 to 1000 |
| `ramp` | Seconds to reach it from the speed at the start of the key. Default 0 |
| `ease` | Default `inOut` |

The speed track starts to write the speed when its first key starts. Before that the take leaves the sim speed alone, except that a take started while the sim is stopped (speed 0) sets it to 1x. Speeds above 30 lock the manual throttle in the game. The take does not write the speed while an automatic warp runs or a menu pauses the game, and it gives the earlier speed back at the end.

## Actions

Actions. The only action now is `stage`.

| Field | Meaning |
| --- | --- |
| `at` | When the action fires. It is required. A negative delay works for altitude and speed events only |
| `action` | `stage` |
| `sequence` | Optional. The number of the sequence that must be the next one when the action fires |

```json
"actions": [
  { "at": { "event": "altitude:90000" }, "action": "stage", "sequence": 3 }
]
```

A `stage` action sends the staging key of the game for the controlled vehicle. The key fires the next sequence that was not fired yet, in order, and it cannot pick a number. With `sequence` set, the action fires only when that sequence is the next one. If an earlier sequence has not fired yet, the action waits up to 10 seconds of take time for it and then is skipped. If a later sequence is next, or none is left, the action is skipped at once. The log says why. An action that waits holds back the actions after it.

- The key goes through the input queue of the vehicle (`InputEvents.VehicleInputBuffer` with `InputAction.CameraUp` on key release), the same path as a key press, so another mod that patches `SequenceList.ActivateNextSequence` handles it as staging by the player.
- The action checks `SequenceList.GetNextSequenceNumber()` first. It sends at most one key until the active sequence has changed, and a key that does not take effect for 2 seconds counts as lost.
- Each action fires once per run. A dry run never fires it, and a preview has no actions.
- An action that was not reached, or not fired, when the take ended gets a `warning` row in the event log. Make the last shot long enough to reach it.

A sequence whose parts all leave the vehicle when it fires, for example a fairing, is removed by the game inside the same call. The mod therefore watches every call of `SequenceList.ActivateNextSequence` and reports the sequence that was next before the call. The watch only reads.

## Capture options

The capture options:

| Field | Meaning |
| --- | --- |
| `mode` | `realtime` (default) or `offline` |
| `fps` | Pictures per second of the offline mode. Default is the `fps` of the file |
| `folder` | Output folder of the offline mode. A relative path is relative to the folder of the take |
| `scale` | Only 1 works. The pictures have the size of the game window |

## Keep-out boxes

Keep-out boxes. `keepOut` is a list of boxes in the site frame, for a pad, a trench or a tower. The window has no editor for them.

```json
"keepOut": [
  { "min": [-12, -40, -40], "max": [0, 40, 40], "name": "deck" },
  { "min": [0, -6, -6], "max": [90, 6, 6], "name": "tower" }
]
```

`min` is the corner with the lowest up, east and north values, `max` the opposite corner, and `name` is optional. Whenever the final camera position of a take is inside a box, the camera moves to the nearest face of the box that is not the bottom face, plus a margin of 1 m, and the point that it looks at stays. A preview, a paused take and the free camera do not use the boxes. The boxes act for a camera within 5 km of the site origin. A take can set its own `keepOut`, which replaces the list of the file for that take, and an empty list turns the boxes off for the take.

## Events

Events, as the file names them:

| Event | When it happens |
| --- | --- |
| `ignition` | A core of an engine of the vehicle has a throttle above 0. Thrusters do not count. This is the first ignition (`ignition:1` is the same event) |
| `ignition:N` | The Nth ignition of the take. A new ignition is the engines coming on after all were off, or an engine that was not running before and that lights at least 5 seconds after the last ignition while others still burn. Engines of one stage that light a moment apart count as one ignition |
| `liftoff` | The vehicle has no ground contact any more |
| `burnout`, `burnout:N` | All engines turn off after they ran for at least one second. The Nth time |
| `sequence:N` | Sequence N of the vehicle fired: its number became the active one, or a call of the staging method fired it and the game removed it at once because its parts left the vehicle (each number that was skipped is reported too) |
| `altitude:M` | The altitude above the mean radius of the body reaches M meters. The delay may be negative |
| `speed:V` | The speed over the surface reaches V meters per second. The delay may be negative |
| `landed`, `landed:N` | The vehicle has ground contact again after the lift-off, after at least one second without it (so a bounce does not count) |
| `soi`, `soi:N` | The vehicle changed the body whose sphere of influence it is in. The log names both bodies |
| `apoapsis`, `apoapsis:N` | The vehicle passed the highest point of its orbit: the speed away from the body center changed from outward to inward. Only in free flight: no engine runs, the orbit is closed, and the vehicle is above the atmosphere. On a body without an atmosphere the periapsis must also be above the highest terrain. So the coast after an ascent has its apoapsis, while a burn and the top of a hop on an airless body give none |
| `periapsis`, `periapsis:N` | The vehicle passed the lowest point of its orbit, with the same rules as the apoapsis |
| `docked`, `docked:N` | A docking port of the vehicle was connected. When the game makes a new vehicle at the docking and gives it the control, the take follows it |
| `undocked`, `undocked:N` | A docking port of the vehicle lost its connection, not within one second of a fired sequence. The take follows the vehicle that the game gives the control to |
| `mark`, `mark:N` | The player gave a mark with the mark key or `cinecam_mark` during the take, or during the flight recording that a replay plays |

Each event is reported once per take. A take that starts in flight sees the past events at the start. An event name is not case sensitive.

## Take folder and event log

Each take run writes a folder `Documents\My Games\Kitten Space Agency\CineCam\takes\<date>-<time>-<take name>\`. It holds a copy of the shot file and the log `events.csv` with the time of every event, shot, speed key and action.

`scripts/events-to-markers.py` turns the event log into markers for a video editor, so the cuts can follow the flight. It needs Python 3 and nothing else.

```
python -I scripts/events-to-markers.py events events.csv --fps 60 --flash-seconds 2.5
```

It writes `markers.edl` and `markers.csv`. The CSV has the columns `timecode`, `frame`, `videoSeconds`, `kind`, `name`, `detail` and `color`. In DaVinci Resolve, use **Timeline > Import > Timeline Markers from EDL** and choose the file. The import has not been run in Resolve yet, so check the first import against the CSV.

| Option | Meaning |
| --- | --- |
| `--fps` | Frame rate of the timeline. Default 60. |
| `--start-tc` | Start timecode of the timeline. Default `01:00:00:00`. |
| `--flash-seconds`, `--flash-frames` | Where the white frame is in the video. Default 0. The video clip must start at the start of the timeline. |
| `--kinds` | The kinds of rows to include, comma separated, or `all`. Default `event,shot,speed,action`. |
| `--no-pause-shift` | Do not add the length of earlier pauses to the later rows. |
| `-o` | Output path without extension. |

Without a white frame row in the log, the take start is the zero. A take clock stops during a pause for an edit while the screen recording goes on, so the tool adds the length of earlier pauses to the later rows.

The columns of `events.csv`:

| Column | Meaning |
| --- | --- |
| `realSeconds` | Take time. In an offline capture it is the picture number over the picture rate |
| `gameSeconds` | Game time since the start of the universe |
| `missionSeconds` | Seconds since the ignition, or 0 before it |
| `simSpeed` | The sim speed |
| `frame` | The picture number of an offline capture, empty in real time |
| `kind` | `take`, `event`, `shot`, `speed`, `action`, `pause`, `marker`, `info` or `warning` |
| `name` | The event, shot or key |
| `detail` | More text |

## Example

A complete example:

```json
{
  "fps": 60,
  "hideUi": true,
  "events": { "stageSeparation": "sequence:2" },
  "capture": { "mode": "offline", "fps": 30 },
  "takes": [
    {
      "name": "launch",
      "default": true,
      "camera": [
        { "type": "orbit", "target": "site", "radius": 120, "height": 25, "degPerSec": 6, "lookAt": [30, 0, 0], "duration": 12, "fov": 40 },
        { "type": "tripod", "target": "site", "position": [3, -60, -45], "lookAt": [15, 0, 0], "duration": 8, "fov": 30, "at": { "event": "ignition" } },
        { "type": "chase", "target": "vehicle", "offset": [-25, 8, -12], "lookAt": [10, 0, 0], "smoothing": 0.8, "duration": 20, "fov": 45, "transition": { "type": "blend", "seconds": 1.5 }, "at": { "event": "liftoff", "delay": 4 } },
        { "type": "chase", "target": "vehicle", "offset": [-40, 15, -20], "lookAt": [10, 0, 0], "duration": 40, "fov": 40, "transition": 2.5, "at": { "event": "altitude:5000", "delay": -2 } }
      ],
      "speed": [
        { "at": { "event": "ignition" }, "to": 0.25, "ramp": 0.5 },
        { "at": { "event": "liftoff", "delay": 6 }, "to": 1, "ramp": 2 },
        { "at": { "event": "stageSeparation" }, "to": 0.25, "ramp": 1 }
      ]
    }
  ]
}
```
