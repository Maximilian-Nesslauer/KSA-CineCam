# Development

This page tells how to build and test CineCam, the console commands, the local control, and the checks for a game update. Back to the [README](../README.md).

## Building and tests

Targets **.NET 10**. The project references the game DLLs with the path `..\..\..\refs\game\` from `CineCam\CineCam.csproj`, so the install folder of the game (or a link to it) must exist at that place, three folders above the project folder.

| Package | Source | Tested Version |
| --- | --- | --- |
| [StarMap.API](https://github.com/StarMapLoader/StarMap) | NuGet | 0.5.2 |
| [Lib.Harmony](https://www.nuget.org/packages/Lib.Harmony) | NuGet | 2.4.2 |

```
dotnet build CineCam.slnx -maxcpucount:4 -p:DeployToMods=false
dotnet run --project CineCam.Tests
```

`-p:DeployToMods=false` stops the build from copying the mod into the game folder. A build without it deploys the mod and fails while the game runs.

`CineCam.Tests` is a console program that compiles only the game-free code (`Director` and `Editing`) and never loads the game. It tests the shot file reader and writer, the timeline layout, the presets, the pose math, the event detector, the speed rules, the actions and the stage guard, the local horizon frame, the fired sequence detection, the offline frame bookkeeping and the picture collector. It exits with 0 when all tests pass. The code that touches the game (`Features` and `Patches`) is not covered by these tests. The Python tools have their own tests: `python -I scripts/test_events_to_markers.py` and `python -I scripts/test_cinecam.py`.

The mod has a self test that runs inside the game. It is off unless the environment variable `CINECAM_AUTOTEST` names a result folder. It plays a take of every shot type in real time and an offline take, checks the event log and the return of the camera, and writes `result.json` in the result folder.

## Console commands

Open the game console and run:

| Command | Action |
| --- | --- |
| `cinecam_start [take]` | Starts the named take, or the default take |
| `cinecam_stop` | Stops the running take |
| `cinecam_reload` | Checks the shot file |
| `cinecam_pause` | Pauses the running take for an edit, or resumes a paused one |
| `cinecam_mark` | Gives a mark in the running take or flight recording: the events `mark`, `mark:2` and so on |
| `cinecam_checkpoint [take]` | Saves the game as the checkpoint of the take |
| `cinecam_reset [take]` | Ends a running take, loads the checkpoint and makes the take ready |
| `cinecam_sun [elevation] [set]` | Moves the game time to the next time the sun at the launch site has this elevation in degrees. Add `set` for a sun that goes down |

## Local control from a terminal (developer mode)

Set `"enableLocalControl": true` in the instance's `CineCam\settings.json` and restart KSA. CineCam creates `CineCam\control` in that instance's game data folder. Run the included `scripts/cinecam.py` with Python 3.11 or later. Without options the CLI uses `CineCam\control` in the game's Documents folder. For another instance, set `CINECAM_CONTROL_DIR` to the control folder or give its path with `--control-dir`. When the folder has no running session, the CLI says which folder it checked. Preview frames go to `CineCam\previews\`; CineCam removes preview folders that are older than one hour when it takes the next preview.

The CLI prints JSON with `ok`, `code`, `message` and the current `state` when the game responds. The state has the take time and the recording time, the take that plays, the camera, and the events with their recording and take times (the first 1000 events and separations, with `eventCount` and `markerCount` for the whole number). Use `list` to find recording IDs. Times are take seconds, the time of the video. The commands below use example IDs.

```powershell
python cinecam.py list
python cinecam.py open "Flight 2026-11-10 17-00-00 abcd1234"
python cinecam.py seek 12.5
python cinecam.py play
python cinecam.py speed 0.5
python cinecam.py camera free
python cinecam.py follow 1
python cinecam.py fov 35
python cinecam.py add-key orbit
python cinecam.py preview
python cinecam.py render --from-here
python cinecam.py render-stop
python cinecam.py close
```

`add-key` takes `fixed`, `track`, `chase`, `orbit`, `part-mount` or `path-point` and adds the shot to the selected take of the editor at the replay time. The preview command pauses playback and returns the PNG path in `previewPath`. `render` starts a render of the take of the open replay (`--from-here` starts at the replay time), `render-stop` stops it after the current picture, and the field `render` of the state has its progress and, after the end, its result. Use `--help` for the remaining commands (`status`, `record`, `stop`, `retry-save`, `pause`, `interface`). Any program on this computer that can write to the control folder can send these commands, so leave it off unless you use it.

## Maintenance on each game update

The mod patches game methods with Harmony and reads private game members by reflection, so each game update can break it. `GameReflection` resolves the takes targets at start-up, and `ReplayPatches` applies the replay patches. A missing target turns off its feature and logs a warning or an error. On each game update, check every item below in the new decompiled source, then do one test in the game: a quick camera, a take with the default shot file, an offline take, a replay of a short recording, and a sun move. Change `TestedGameVersion` in `Mod.cs`.

### Harmony patch targets of the takes (`CineCamPatches.cs`)

`CineCamPatches.Apply` applies one group per feature. A group whose target is missing, or whose `Harmony.Patch` call throws, is off with one log line that names the features it turns off, and the other groups still go in. `Mod.Unload` removes all patches that went in. `CineCamPatches.FrameBlock` and `CineCamPatches.CameraBlock` give the reason when the frame group or the camera group is off. The take, dry run, shot preview, quick camera, checkpoint, reset, pause, reload and sun move requests (keys, window buttons and console commands) are refused with that reason, the window shows it on the disabled buttons, and the self test writes it into `result.json` and ends the game with exit code 1.

- `Program.OnFrame(double currentPlayerTime, double dtPlayer)`: a prefix and a postfix. The prefix parameter names must stay the same, because the offline capture changes both values by reference. The frame order must still be `PrepareFrame`, then `OnFrameViewports`, then the render and `PostRender`. The quick camera, the takes, the checkpoints and resets, the sun move, the self test and the replay preview pictures and render run in this prefix and postfix. Without it a replay still opens and plays, because it runs in the replay patches.
- `FixedController.OnFrame(IViewport, double)`: a prefix that sets the camera and skips the stock controller of the main viewport, for a take and for a quick camera. It must still run after the vehicles have their new state and before `Camera.OnFrame` builds the matrices. Without it no take, dry run, shot preview or quick camera starts, and a reset does not start its take. The take camera of a replay does not need it, because the replay camera follows nothing and `FixedController.OnFrame` moves the camera only while it follows something.
- `FixedController.OnSwitchOn(CameraMode)`: a prefix that skips the "Fixed camera" alert during a take. Without it, the alert stays.
- `Program.OnKey`: a postfix that reads the parameters `key` (`GlfwKey`) and `action` (`GlfwKeyAction`). The guard must match the one in the game: `Program.IsLoaded`, `ImGui.GetIO().WantCaptureKeyboard` and `Program.ConsoleWindow.IsOpen`.
- `Program.DrawFps()` (private, static): a postfix that draws the white sync frame. It must still run when `Program.DrawUI` is false.
- `ConsoleWindow.Draw(float2, float2, float, bool)`: a prefix that skips the console while a take hides the interface, or while a replay hides it.
- `UncompressedSave.Load()`: a prefix and a postfix. The prefix stores `UniverseData` and the postfix counts a load as finished when the reference changed, because a refused or failed load returns normally without a change. The patch parameters are `__instance` and `__state`.
- `ScreenshotCapture.OnPostRender()`: a prefix that finds out whether the frame has a picture.
- The render of a replay uses the same `Program.OnFrame` prefix and postfix as the offline capture, so `Program.OnFrame` must still call `PrepareFrame` (where the replay commands run) before `OnFrameViewports` (where the replay sets the camera). It writes its progress into `Program.GetWindow().Title` (`GlfwWindow.Title`) and gives the old title back at the end. At the end it removes the alerts that the game made during the render from the private list `TimedAlert.All` (without that list the alerts stay), and the key postfix closes `EscMenu.Open` when Escape stopped the render.
- `SequenceList.ActivateNextSequence(Vehicle)`: a prefix and a postfix that read `GetNextSequenceNumber()`, `ActiveSequence` and `Count` before and after. The postfix parameter must stay named `vehicle`.

### Harmony patch targets of the replay (`ReplayPatches.cs`)

A required target that is missing turns the whole replay off. An optional target logs a warning, and the replay then runs without it.

Required:

- `Program.RefreshVehiclesInFrame()`: a postfix that samples the flight for the recorder.
- `Program.OnFrameViewports(double)`: a prefix that advances the playback.
- `Vehicle.Split(Part.Connection, Part.Connection.IConnector, double, ref PoseChange, string)`: a postfix that records a split-off stage.
- `PartModelRenderer.UpdateRenderData(IViewport, int)`: a prefix that draws the recorded parts.
- `Vehicle.UpdateRenderData(IViewport, int)`: a prefix that skips the live vehicles in the replay.
- `Vehicle.UpdateExhaustLights(Camera, IViewport)` and `Vehicle.AddVolumetricExhaustInstances(Camera, VolumetricExhaustRenderer, double)`: prefixes that skip the live exhaust.
- `VolumetricExhaustRenderer.EndPlumeGrouping()`: a prefix that draws the recorded plumes.
- `Program.UpdateShaderData(double, IViewport)`: a transpiler that replaces the call of `Camera.GetPositionEgo(IPosition)` for the sun. The call must still exist.
- `VolumetricExhaustRenderer.UpdateFrameData()` and `VolumetricExhaustRenderer.EndPlumeGrouping()`: transpilers that replace the call of `Universe.GetElapsedSeconds()`. The call must still exist in both.
- `Universe.DeserializeSave(UniverseData)` and `Universe.LoadSystem(string)`: a prefix and a finalizer that close the replay when the world changes.
- `Universe.SetSimulationSpeed(double, bool)` and `Universe.SetSimulationSpeed(SimSpeed)`: prefixes that keep the sim stopped.
- `Universe.GetJobSimStep(double)`: a prefix that stops an automatic warp and keeps the speed at 0.
- `Vehicle.ProcessInput(InputAction, GlfwKeyAction, GlfwModifier)`: a prefix that skips the flight input in the replay.

Optional:

- `KittenEva.ProcessInput(InputAction, GlfwKeyAction, GlfwModifier)`: the same prefix for a kitten on EVA.
- `Universe.SeekNextVehicle(SeekDirection)` and `Universe.SeekNextCelestial(SeekDirection)`: prefixes, so the vessel and body keys do not change the controlled vehicle under the replay.
- `KittenEva.UpdateRenderData(IViewport, int)`, `Vehicle.UpdateParachuteRenderData(IViewport)` and `Vehicle.UpdateSeatedCrewRenderData(IViewport, int)`: prefixes that hide the live kittens, parachutes and seated crew.
- `VolumetricTrailRenderer.PrepareAndRenderShadowCascades`: a prefix that sets the `Celestial` parameter to null so no plume trail is drawn. The method is found by its parameter type names (`CommandBuffer`, `IViewport`, `Celestial`, `Int32`), so a change of that list turns the patch off.
- `ParticleSystem<ParticleUpdateData, ParticleRenderData>.WriteCommandsColorOpaque` and `WriteCommandsColorTranslucent`: prefixes that skip the particles. They are found by the parameter type names `CommandBuffer`, `IViewport` and `Int32`.
- `CloudLayerRenderData.UpdateCommonFrameData(int, int, double, double4x4)` and `CloudShadowVolume.GetPlanetToCloudRotation(CloudLayerRenderData)` (private, static): transpilers that replace the call of `Universe.GetElapsedSeconds()`, so the cloud layers and their shadows turn to the recorded moment. The turn must still be `AngularRotationSpeed` times the game time. Without the patches the replay shows the clouds and the cloud shadows of the moment at which it opened, and a cloud shadow can darken the vehicle and the ground where the live flight was in the sun.
- `GroundClutterRenderer.UpdateShadowCullingData(Camera, Celestial, int, int)`: a transpiler that gives the sun of the recorded moment to the shadow culling of the ground clutter, through the same replacement of `Camera.GetPositionEgo(IPosition)` as in `Program.UpdateShaderData`.
- `FixedAlert.Draw(float, double)`: a prefix that skips the alert "Simulation paused" while a replay is open. It finds that alert once in the private array `FixedAlert.All` by the private field `_displayName` equal to `LStrings.AlertSimulationPaused`. Without the patch, or when the lookup finds nothing, the alert shows over the replay view; a render hides the interface, so it is never in a picture.
- `VolumetricExhaustRenderer.EndPlumeGrouping()`: a postfix that lights the recorded plumes. It needs `VolumetricExhaustInstance.LastPlumeData`, which `EndPlumeGrouping` computes. The light copies `Vehicle.UpdateExhaustLight` (the template values `Absorption.Density`, `Absorption.ScatteringBrightness`, `Emission.Brightness`, `Emission.ColorGradient.WeightedAverageColor`, the `StartupTransient` curve `EmissiveSizeCurve`, and `Program.LightSystem.CreateLightInstance` with `Light.CreatePointLight`), so check that method on an update. Without the patch the recorded plumes give no light.

### Reflection keys (`Core/GameReflection.cs`)

- `Program.Screenshot(int, string)` (private, static), called with the scale 1 and the flag `ui`. The flag must still arm the swapchain grab, after the interface is drawn.
- `ScreenshotCapture._state` and `ScreenshotCapture._copyRecordedThisFrame`: the mod reads the state names `ArmSwapchainGrab` and `CaptureFrame`.
- `GameSave.UniverseData` (a field) and `Program.Instance.IsScreenshotCaptureActive`.
- The file name of the game picture: `ksa_<date>_<time>_<width>x<height>.png` in `exports\screenshots`. The collector looks for `ksa_*.png` there. The game writes the file on a worker thread, and the collector moves it away when no process holds it open any more.

### Game members the mod uses

- Camera: `Program.MainViewport` (`BaseCamera`, `FixedController`, `Mode`, `SetCameraMode` for the fixed, free, orbit, IVA and map modes), `Camera.PositionEcl`, `Camera.PositionCce`, `Camera.LocalRotation`, `Camera.LookAtRotation(double3, double3)`, `Camera.SetFieldOfView(float)`, `Camera.GetFieldOfView()` (radians), `Camera.GetForwardEcl()`, `Camera.GetUpEcl()`, `FixedController.CameraOffset`, `CameraRotation` and `DockingConnector`. `FixedController.OnFrame` must still keep the pose that the prefix wrote. `Camera.ClampCamera` lifts the camera to 0.5 m above the terrain, and `Camera.IsVisibleTo` hides the launch pad object below the pad plane.
- Free camera restore: `FlyController.OnSwitchOn` must still read the camera rotation (`CacheOffset`) and not move the camera.
- Sim speed: `Universe.GetSimulationSpeed()`, `Universe.SetSimulationSpeed(double, bool alert)`, `Universe.IsAutoWarpActive`, `Universe.GetAchivedSpeedFraction()`. `Program.UpdateMenuPause` must still set the speed to 0 for an open menu and give the earlier speed back.
- Time: `Universe.GetElapsedTime()`, `Universe.GetElapsedSeconds()`, `InputEvents.AutoWarpBuffer` with `InputEvents.AutoWarpData` (`StopWarp`, `WarpToTime`, `SimTimeMargin`, `IsUncapped`), `Universe.WorldSun`, `Universe.CurrentSystem.HomeBody`, `Orbit.GetStateVectorsAt`, `Celestial.GetCci2Cce()` and the `UniverseTime` overloads of `GetCce2Ccf`.
- Window widgets: the console kit `ConsoleStyle` and `ConsoleWidgets` (window frame, bodies, footer, buttons, rows, switches, combos, segmented control, region headers, input wells), `ImGui.InvisibleButton`, `IsItemHovered` with `ImGuiHoveredFlags.AllowWhenDisabled`, `BeginTooltip`, `BeginChild`, `DragFloat`, `InputText`, `Selectable` and `SetClipboardText` (the path of a render folder).
- Interface: `Program.DrawUI` (static property), `TimedAlert.ClearAll()`, `ImGuiHelper.GetMainOverlayDrawList()`, `ImGuiHelper.InterfaceScale`, `ImGuiWindow` (the base class of the window), `StarMapAfterGui` and `StarMapMod` of the StarMap API, `IModMenuService.AddModMenuItem`, `TerminalInterface.RegisterObject` and `UnregisterObject`, `TerminalAction`.
- Vehicle: `Program.ControlledVehicle`, `Program.Editor`, `Vehicle.IsDisposed`, `Vehicle.Body2Cce` (+X nose, +Y right, +Z down), `Vehicle.GetPositionEcl()`, `Vehicle.CenterOfMassAsmb`, `Vehicle.PosAsmbToBody`, `Vehicle.Situation` with `HasAnyContact()`, `Vehicle.GetBarometricAltitude()`, `Vehicle.GetSurfaceSpeed()`, `Vehicle.Parent` (a `Celestial`, its `GetPositionEcl()` is the body center of the local frame), `Vehicle.Id`. The pole of the local frame is `+Z` of the body-fixed frame, as in `LocationReference.GetAxesCcf`.
- Staging: `InputEvents.VehicleInputBuffer` with `InputEvents.VehicleInputData` (`Vehicle`, `Action`, `KeyAction`, `Modifiers`), `InputAction.CameraUp` with `GlfwKeyAction.Release` (in `Vehicle.ProcessInput` this calls `SequenceList.ActivateNextSequence`), `SequenceList.GetNextSequenceNumber()` and `SequenceList.ActiveSequence`. `SequenceList.RemoveSpentSequences` and `Remove` must still lower `ActiveSequence` when a fired sequence has no parts left, which is why the sequence patch exists. The next sequence must still be the first one that is not activated and has parts, and the staging key must still go through the input queue.
- Parts and events: `PartTree.Modules.Get<EngineController>()` with `EngineController.Cores`, `PartTree.RocketCores.GetStateByIdx` and `RocketCoreState.Throttle`, `PartTree.SequenceList.ActiveSequence`, `PartTree.Count`, `PartTree.Parts`, `PartTree.ComputeBoundingBoxAsmb()`, `Part.Id`, `Part.DisplayName`, `Part.PositionVehicleAsmb`.
- Separated vehicles and the sun: `Vehicle.Split` (creates the new `Vehicle` in the same `CelestialSystem` with the same `Parent`, called by `Decoupler.Decouple`), `CelestialSystem.All` with `LookupCollection.OfType<Vehicle>()`, `KittenEva`, `Universe.CurrentSystem`, `Universe.WorldSun` with `Astronomical.GetPositionEcl()`, `Celestial.MeanRadius`.
- Launch site and anchor: `Celestial.GetLocation(string)`, `Celestial.BodyTemplate.Locations`, `LandmarkReference` (`ForwardCcf`, `IsLaunchPad`, `GetAxesCcf`, `GetStaticObject().GroundOffset`), `Celestial.GetTerrainHeightFromDirCcf(double3, bool accurate)`, `Celestial.GetCcf2Cce()`, `Celestial.GetCce2Ccf()`, `Celestial.GetCci2Ccf()`, `Celestial.MeanRadius`, `Celestial.GetPositionEcl()`, `CelestialSystem.Get(string)`. The anchor math copies `LocationReference.OnDataLoad` (latitude and longitude to `ForwardCcf`) and `LocationReference.GetAxesCcf`, so check both on an update. The ground point under a vehicle with no Landmark uses `Vehicle.GetPositionCci()` and `Celestial.GetCci2Ccf()`.
- World events: `Vehicle.Parent` (`IParentBody.Id`, `IParentBody.GetAtmosphereRadius()`), `Vehicle.GetPositionCci()`, `Vehicle.GetVelocityCci()`, `Vehicle.Orbit.GetOrbitType()` (`Orbit.OrbitType.Elliptical`), `PartTree.DockingPorts.Modules` with `DockingPort.Docked`, `PartTree.Parts`. A docking (`DockingPort.Dock`, `Vehicle.MergeFrom`) and an undocking (`DockingPort.Undock`, `Vehicle.Split`) must still move the existing `Part` objects to the vehicle that gets the control, because the take finds that vehicle by a shared part.
- Save and load: `UncompressedSave.Make(string)` (replaces a save of the same name without a question), `GameSaves.LoadSaveGame(string)` (synchronous, and the sim speed is 1x after it), `GameSaves.SaveFolderPath`, `GameSaves.RefusedInEditor()`, `Program.IsLoaded`, `SaveName` rules (the checkpoint name follows them), `Universe.AutoWarpStop(bool)`, and the file `universe.xml` in the save folder.
- Paths and settings: `Constants.DocumentsFolderPath` (read at the time of use), `GameSettings.Current.Simulation.MinTargetFrameRate`, `VersionInfo.Current.VersionString`.

### Other things to check

- Whether the sim step per picture (`1 / fps`) is still below `1 / MinTargetFrameRate`, and whether `Universe` still limits the step by the vehicle solver time (`GetAchivedSpeedFraction`).
- That the swapchain grab still includes the interface of the frame, and that the file name of the picture still has a one-second resolution (the collector handles a repeated name).
- That `Universe.SetSimulationSpeed` still accepts a value of 0 to 1000 and that the speed lock above 30 (`Vehicle.ControlsLockout`) is unchanged.
- That a recording replays on the new build: the replay file keeps the game build, and it opens only on the same build.
- The replay world maps the recorded pose with the rule of the recorder: the position is the model origin in the body-fixed frame (`Vehicle.GetPositionEcl()` minus `Vehicle.CenterOfMassAsmb` turned by `Vehicle.Asmb2Cce`), and the rotation is `Vehicle.Asmb2Cce` followed by `Celestial.GetCce2Ccf()`. The game-free code turns a vector by a rotation as `q v q*`, which is what `double3.Transform(double3, doubleQuat)` of `Brutal.Numerics` does, and `doubleQuat.Concatenate(a, b)` must still be `b * a`. The vehicle frame needs the assembly frame to keep the axes of `Vehicle.Body2Cce`.
- The part state of the replay (`ReplayPartState.cs`) reads and writes the module states of `KeyframeAnimationModule` (`State.TimeCurrent`, `TimeGoal`, `DeriveDeploymentState`, `ApplyAnimationTransforms(float, doubleQuat?)`, `SolarTrackingExtension.CurrentAngle` and `Rotation`), `SolarTracker` (`SolarTrackerState.CurrentAngle`, `ExcludeSubPartIds`, the sub part rotation of `SolarTracker.UpdateModules`), `Gimbal` (`GimbalState.AngleY`, `AngleZ`, `Gimbal2Asmb`, `Gimbal.ComputeGimbal2Asmb`), `FxTemperature` (`EmissivityFraction`, `ThinFilmFraction`), `FxWetness` (`WetnessFraction`) and `PowerConsumer` with `LightSwitch` (`LightIsActive`, `PowerConsumerState.Active`), through `ModuleStateList.TryGetTypeList` and `GetModuleAndAllMutableStatesForInitializationByIdx`. The replay needs `PartTreeRenderData` to still read the gimbal and heat states in every frame, compare the wetness and light switch states in every frame, and rebuild its matrices when `Part.PositionParentAsmb` or `Asmb2ParentAsmb` changes. The channel order is the order of `PartTreeIterator` with the sub parts, so a change of the module list of a part makes the replay show that segment without its recorded state (with a warning in the log). Neighbor vehicles come from `CelestialSystem.All`, and a docking must still dispose the merged vehicle (`Vehicle.MergeFrom`).
- The recorder stores the vehicle state from `VehicleReader.Sample` and the center of mass `Vehicle.CenterOfMassAsmb` per segment. The replay finds parts by `Part.Template.Id`, `Part.Id` and `Part.DisplayName` with `Part.PositionVehicleAsmb` of the decoded part tree, and the camera of the replay writes `Camera.PositionEcl`, `Camera.LocalRotation` and then `FlyController.CacheOffset()`, which must still only store the rotation. The take camera of a replay uses the fixed mode with no followed object, so `FixedController.OnFrame` must still leave the camera alone without `Camera.Following` and `FixedController.OnKey` must still take no key. The recorder follows the controlled vehicle through `VehicleReader.TryFollow` as a live take does (`DockingPort.Undock`, `DockingPort.Dock`, `PartFailureEvent.Apply` must still give the control to the vehicle with the parts). An old saved view turns into a camera direction with `Camera.ForwardView`.
