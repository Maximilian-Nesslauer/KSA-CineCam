namespace CineCam.Director;

/// <summary>The shot file that the mod writes when the player has none.</summary>
public static class DefaultShotFile
{
    public static ShotFileReadResult Load() => ShotFileJson.Parse(Json);

    // Lengths are in meters, angles in degrees. The vehicle frame has +X toward the nose and its
    // origin at the base of the vehicle.
    public const string Json = """
{
  "fps": 60,
  "events": {
    "stageSeparation": "sequence:2",
    "fairing": "sequence:3"
  },
  "takes": [
    {
      // A short launch of any vehicle from any place. Start the take on the ground before the
      // engines start. The first two shots repeat until the ignition.
      "name": "launch",
      "default": true,
      "preRoll": { "until": "ignition" },
      "camera": [
        { "type": "orbit", "target": "vehicle", "center": [15, 0, 0], "radius": 70, "height": 10, "startDeg": 0, "degPerSec": 6, "lookAt": [15, 0, 0], "duration": 10, "ease": "none", "fov": 45 },
        { "type": "chase", "target": "vehicle", "frame": "local", "offset": [3, -35, -30], "lookAt": [15, 0, 0], "aim": "vehicle", "fit": 0.7, "smoothing": 0.5, "duration": 8, "transition": { "type": "blend", "seconds": 2 } },
        { "type": "chase", "target": "vehicle", "frame": "local", "offset": [2, 30, -25], "lookAt": [15, 0, 0], "aim": "vehicle", "fit": 0.8, "smoothing": 0.3, "duration": 10, "at": { "event": "ignition" } },
        { "type": "chase", "target": "vehicle", "frame": "flight", "offset": [5, 15, -60], "lookAt": [10, 0, 0], "aim": "vehicle", "fit": 0.5, "smoothing": 1, "duration": 20, "transition": { "type": "blend", "seconds": 2 }, "at": { "event": "liftoff", "delay": 4 } },
        { "type": "chase", "target": "vehicle", "frame": "flight", "offset": [15, 25, -120], "lookAt": [10, 0, 0], "aim": "vehicle", "fit": 0.35, "smoothing": 1.5, "duration": 40, "transition": { "type": "blend", "seconds": 3 }, "at": { "event": "altitude:5000" } }
      ],
      "speed": [
        { "at": { "event": "ignition" }, "to": 0.25, "ramp": 0.5, "ease": "inOut" },
        { "at": { "event": "liftoff", "delay": 6 }, "to": 1, "ramp": 2, "ease": "inOut" },
        { "at": { "event": "altitude:15000" }, "to": 2, "ramp": 3, "ease": "inOut" }
      ]
    },
    {
      // A vehicle with a stage separation and a fairing. The events use the names of the file
      // above. Every shot follows the vehicle, so the take works from any pad.
      "name": "ascent",
      "camera": [
        { "type": "chase", "target": "vehicle", "frame": "local", "offset": [2, -40, -30], "lookAt": [15, 0, 0], "aim": "vehicle", "fit": 0.75, "smoothing": 0.5, "duration": 10 },
        { "type": "chase", "target": "vehicle", "frame": "flight", "offset": [5, 15, -60], "lookAt": [10, 0, 0], "aim": "vehicle", "fit": 0.5, "smoothing": 1, "duration": 20, "transition": { "type": "blend", "seconds": 1.5 }, "at": { "event": "liftoff", "delay": 3 } },
        { "type": "chase", "target": "vehicle", "frame": "flight", "offset": [20, 30, -100], "lookAt": [0, 0, 0], "aim": "vehicle", "fit": 0.35, "smoothing": 2, "duration": 20, "transition": { "type": "blend", "seconds": 3 }, "at": { "event": "altitude:12000" } },
        { "type": "chase", "target": "vehicle", "frame": "flight", "offset": [3, 12, -25], "lookAt": [0, 0, 0], "aim": "vehicle", "fit": 0.6, "smoothing": 0.6, "duration": 12, "transition": { "type": "blend", "seconds": 0.5 }, "at": { "event": "stageSeparation" } },
        { "type": "orbit", "target": "vehicle", "center": [10, 0, 0], "radius": 40, "height": 0, "startDeg": 0, "degPerSec": 8, "lookAt": [10, 0, 0], "duration": 25, "fov": 40, "transition": { "type": "blend", "seconds": 3 }, "at": { "event": "fairing" } }
      ],
      "speed": [
        { "at": { "event": "ignition" }, "to": 0.25, "ramp": 0.5, "ease": "inOut" },
        { "at": { "event": "liftoff", "delay": 8 }, "to": 1, "ramp": 2, "ease": "inOut" },
        { "at": { "event": "stageSeparation" }, "to": 0.25, "ramp": 1, "ease": "inOut" },
        { "at": { "event": "stageSeparation", "delay": 10 }, "to": 2, "ramp": 2, "ease": "inOut" }
      ]
    }
  ]
}
""";
}
