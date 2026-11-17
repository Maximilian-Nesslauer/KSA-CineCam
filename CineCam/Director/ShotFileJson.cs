using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CineCam.Director;

/// <summary>
/// Reads and writes shot files. The reader accepts comments and trailing commas and keeps the
/// fields it does not know. The writer uses a fixed field order and indents every object, but
/// writes a vector or a trigger on one line. Comments of the source text are not kept.
/// </summary>
public static class ShotFileJson
{
    #region Reading

    public static ShotFileReadResult Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            string where = ex.LineNumber.HasValue ? UiText.Format("", $" (line {ex.LineNumber + 1})") : "";
            throw new ShotFileException("", UiText.Format("text.bc0dfd14411f", $"the text is not valid JSON{where}: {ex.Message}"));
        }

        using (document)
            return new Reader().ReadFile(document.RootElement);
    }

    public static ShotFileReadResult Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (IOException ex)
        {
            throw new ShotFileException("", UiText.Format("text.5db8f241679b", $"cannot read \"{path}\": {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ShotFileException("", UiText.Format("text.5db8f241679b", $"cannot read \"{path}\": {ex.Message}"));
        }

        return Parse(text);
    }

    private sealed class Reader
    {
        private readonly List<string> _warnings = new();
        private ShotFile _file = new();

        public ShotFileReadResult ReadFile(JsonElement root)
        {
            var obj = new Obj(root, "");
            var file = new ShotFile();
            _file = file;

            double? version = obj.OptNum("formatVersion", 1.0, 1e6);
            if (version.HasValue && version.Value != Math.Floor(version.Value))
                throw new ShotFileException("formatVersion", UiText.MessageText("text.5015ab8d9d3b", "expected a whole number from 1"));
            file.FormatVersion = version.HasValue ? (int)version.Value : 1;
            if (file.FormatVersion > ShotFile.CurrentFormatVersion)
                _warnings.Add(UiText.Format("text.b115f6453b21", $"formatVersion: the file has format version {file.FormatVersion}, and this version of CineCam knows version {ShotFile.CurrentFormatVersion}. Fields that it does not know are kept, but they have no effect"));

            file.Site = obj.OptString("site");
            file.Fps = obj.OptNum("fps", 1.0, 1000.0) ?? 60.0;
            file.HideUi = obj.OptBool("hideUi") ?? false;
            file.SyncFlash = obj.OptBool("syncFlash") ?? false;
            if (obj.TryGet("capture", out JsonElement capture))
                file.Capture = ReadCapture(new Obj(capture, "capture"));

            if (obj.TryGet("keepOut", out JsonElement keepOut))
                file.KeepOut.AddRange(ReadKeepOut(keepOut, "keepOut"));

            if (obj.TryGet("events", out JsonElement events))
            {
                if (events.ValueKind != JsonValueKind.Object)
                    throw new ShotFileException("events", UiText.MessageText("text.c58d3e6ba72d", "expected an object of alias names"));
                foreach (JsonProperty property in events.EnumerateObject())
                {
                    string path = "events." + property.Name;
                    if (property.Value.ValueKind != JsonValueKind.String)
                        throw new ShotFileException(path, UiText.MessageText("text.815dc5785b7b", "expected an event name as text, for example \"sequence:3\""));
                    string target = property.Value.GetString()!;
                    if (!EventName.TryParse(target, out EventName parsed, out string error))
                        throw new ShotFileException(path, error);
                    // An alias may keep the name of an event that came after the file was written, so the file means
                    // what it meant before.
                    if (EventName.TryParse(property.Name, out EventName builtIn, out _) && !builtIn.AliasMayShadow)
                        throw new ShotFileException(path, UiText.MessageText("text.9114074e92d0", "an alias must not use the name of a built-in event"));
                    file.Events[property.Name] = parsed.Canonical;
                }
            }

            bool hasList = obj.Has("takes");
            bool hasTop = obj.Has("camera") || obj.Has("speed");
            if (hasList && hasTop)
                throw new ShotFileException("takes", UiText.MessageText("text.c04053af3ce4", "a file has either \"takes\" or \"camera\" and \"speed\" at the top level, not both"));

            if (hasList)
            {
                file.UsesTakeList = true;
                obj.TryGet("takes", out JsonElement list);
                if (list.ValueKind != JsonValueKind.Array)
                    throw new ShotFileException("takes", UiText.MessageText("text.e85e8d866929", "expected a list of takes"));
                int index = 0;
                foreach (JsonElement item in list.EnumerateArray())
                {
                    file.Takes.Add(ReadTake(new Obj(item, UiText.Format("", $"takes[{index}]")), requireName: true));
                    index++;
                }

                if (file.Takes.Count == 0)
                    throw new ShotFileException("takes", UiText.MessageText("text.877b140a6831", "the list has no take"));

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Take take in file.Takes)
                {
                    if (!names.Add(take.Name))
                        throw new ShotFileException("takes", UiText.Format("text.39626608dd89", $"the take name \"{take.Name}\" is used twice"));
                }

                if (file.Takes.Count(t => t.IsDefault) > 1)
                    throw new ShotFileException("takes", UiText.MessageText("text.b3e3149d1d86", "more than one take is marked \"default\""));
                if (!file.Takes.Any(t => t.IsDefault))
                    file.Takes[0].IsDefault = true;
            }
            else
            {
                Take take = ReadTake(obj, requireName: false);
                take.Name = "default";
                take.IsDefault = true;
                file.Takes.Add(take);
            }

            string? hideAt = obj.OptString("hideUiAt");
            if (hideAt != null)
            {
                CheckHideUiAt(hideAt, file.Events, "hideUiAt");
                file.HideUiAt = hideAt;
            }

            obj.CollectExtras(file.Extras);
            return new ShotFileReadResult(file, _warnings);
        }

        private static void CheckHideUiAt(string value, IReadOnlyDictionary<string, string> aliases, string path)
        {
            if (ShotFile.IsHideUiAtStart(value))
                return;
            if (!EventName.TryResolve(value, aliases, out _, out string error))
                throw new ShotFileException(path, UiText.Concat(error, UiText.MessageText("text.740edfff9cff", ", or \"start\"")));
        }

        private static List<KeepOutBox> ReadKeepOut(JsonElement list, string path)
        {
            if (list.ValueKind != JsonValueKind.Array)
                throw new ShotFileException(path, UiText.MessageText("text.0aed8e0c3a41", "expected a list of boxes, each with \"min\" and \"max\""));
            var boxes = new List<KeepOutBox>();
            int index = 0;
            foreach (JsonElement item in list.EnumerateArray())
            {
                var obj = new Obj(item, UiText.Format("", $"{path}[{index}]"));
                var box = new KeepOutBox
                {
                    Min = obj.ReqVec("min"),
                    Max = obj.ReqVec("max"),
                    Name = obj.OptString("name") ?? "",
                };
                obj.RejectUnknown();
                if (box.Min.X > box.Max.X || box.Min.Y > box.Max.Y || box.Min.Z > box.Max.Z)
                    throw new ShotFileException(obj.Child("min"), UiText.MessageText("text.b66e766b5c2c", "the minimum corner must not be above the maximum corner on any axis"));
                boxes.Add(box);
                index++;
            }

            return boxes;
        }

        private static WorldAnchor ReadAnchor(Obj obj)
        {
            string body = obj.OptString("body")
                ?? throw new ShotFileException(obj.Child("body"), UiText.MessageText("text.e6eda35df951", "an anchor needs the id of its body, for example \"Earth\""));
            if (body.Trim().Length == 0)
                throw new ShotFileException(obj.Child("body"), UiText.MessageText("text.ff70860ab45c", "the body id is empty"));
            var anchor = new WorldAnchor
            {
                Body = body.Trim(),
                Latitude = obj.ReqNum("latitude", -90.0, 90.0),
                Longitude = obj.ReqNum("longitude", -180.0, 180.0),
                Altitude = obj.OptNum("altitude", -1e5, 1e9) ?? 0.0,
            };
            obj.CollectExtras(anchor.Extras);
            return anchor;
        }

        private static CaptureOptions ReadCapture(Obj obj)
        {
            var capture = new CaptureOptions
            {
                Mode = obj.OptEnum<CaptureMode>("mode") ?? CaptureMode.Realtime,
                Fps = obj.OptNum("fps", 1.0, 1000.0),
                Folder = obj.OptString("folder"),
            };
            if (capture.Folder != null && capture.Folder.Trim().Length == 0)
                capture.Folder = null;
            double? scale = obj.OptNum("scale", 1.0, 8.0);
            if (scale.HasValue)
            {
                if (scale.Value != Math.Floor(scale.Value))
                    throw new ShotFileException(obj.Child("scale"), UiText.MessageText("text.d2c0b3317412", "expected a whole number from 1 to 8"));
                capture.Scale = (int)scale.Value;
            }

            capture.Grab = obj.OptEnum<CaptureGrab>("grab") ?? CaptureGrab.Swapchain;
            obj.CollectExtras(capture.Extras);
            return capture;
        }

        private static TakeSound ReadSound(Obj obj)
        {
            var sound = new TakeSound
            {
                Music = obj.OptNum("music", 0.0, 1.0),
                Effects = obj.OptNum("effects", 0.0, 1.0),
            };
            obj.CollectExtras(sound.Extras);
            return sound;
        }

        private static TakePicture ReadPicture(Obj obj)
        {
            var picture = new TakePicture
            {
                Exposure = obj.OptNum("exposure", TakePicture.MinExposure, TakePicture.MaxExposure),
                CloudAmbientLight = obj.OptBool("cloudAmbientLight"),
                CloudShadows = obj.OptBool("cloudShadows"),
                Reflections = obj.OptBool("reflections"),
            };
            obj.CollectExtras(picture.Extras);
            return picture;
        }

        private Take ReadTake(Obj obj, bool requireName)
        {
            var take = new Take();
            if (requireName)
            {
                take.Name = obj.OptString("name") ?? throw new ShotFileException(obj.Child("name"), UiText.MessageText("text.3f4c0ad44e13", "a take needs a name"));
                if (take.Name.Trim().Length == 0)
                    throw new ShotFileException(obj.Child("name"), UiText.MessageText("text.755fbc3895c3", "the take name is empty"));
                take.IsDefault = obj.OptBool("default") ?? false;
                take.Site = obj.OptString("site");
                string? hideAt = obj.OptString("hideUiAt");
                if (hideAt != null)
                {
                    CheckHideUiAt(hideAt, _file.Events, obj.Child("hideUiAt"));
                    take.HideUiAt = hideAt;
                }

                if (obj.TryGet("keepOut", out JsonElement keepOut))
                    take.KeepOut = ReadKeepOut(keepOut, obj.Child("keepOut"));
            }

            if (obj.TryGet("anchor", out JsonElement anchor))
                take.Anchor = ReadAnchor(new Obj(anchor, obj.Child("anchor")));

            string? checkpoint = obj.OptString("checkpoint");
            if (!string.IsNullOrEmpty(checkpoint))
            {
                if (!CheckpointName.IsValid(checkpoint))
                    throw new ShotFileException(obj.Child("checkpoint"), CheckpointName.RuleText);
                take.Checkpoint = checkpoint;
            }

            take.AutoStart = obj.OptBool("autoStart") ?? false;
            take.AutoStartDelay = obj.OptNum("autoStartDelay", 0.0, 600.0) ?? Take.DefaultAutoStartDelay;
            if (obj.TryGet("sound", out JsonElement sound))
                take.Sound = ReadSound(new Obj(sound, obj.Child("sound")));
            if (obj.TryGet("picture", out JsonElement picture))
                take.Picture = ReadPicture(new Obj(picture, obj.Child("picture")));
            take.ExactApsides = obj.OptBool("exactApsides") ?? false;
            take.PauseAt = obj.OptNum("pauseAt", 0.0, Take.MaxPauseAt, minExclusive: true);

            if (!obj.TryGet("camera", out JsonElement camera) || camera.ValueKind != JsonValueKind.Array)
                throw new ShotFileException(obj.Child("camera"), UiText.MessageText("text.a2d91b91d49e", "expected a list of shots"));
            int index = 0;
            foreach (JsonElement item in camera.EnumerateArray())
            {
                take.Shots.Add(ReadShot(new Obj(item, UiText.Format("", $"{obj.Child("camera")}[{index}]"))));
                index++;
            }

            if (take.Shots.Count == 0)
                throw new ShotFileException(obj.Child("camera"), UiText.MessageText("text.24c062f52c3c", "the camera track has no shot"));

            if (obj.TryGet("preRoll", out JsonElement preRoll))
                take.PreRoll = ReadPreRoll(new Obj(preRoll, obj.Child("preRoll")), _file.Events);

            if (obj.TryGet("speed", out JsonElement speed))
            {
                if (speed.ValueKind != JsonValueKind.Array)
                    throw new ShotFileException(obj.Child("speed"), UiText.MessageText("text.223f06a2658c", "expected a list of speed keys"));
                index = 0;
                foreach (JsonElement item in speed.EnumerateArray())
                {
                    take.Speed.Add(ReadSpeedKey(new Obj(item, UiText.Format("", $"{obj.Child("speed")}[{index}]"))));
                    index++;
                }
            }

            if (obj.TryGet("actions", out JsonElement actions))
            {
                if (actions.ValueKind != JsonValueKind.Array)
                    throw new ShotFileException(obj.Child("actions"), UiText.MessageText("text.7cb70da0c34b", "expected a list of actions"));
                index = 0;
                foreach (JsonElement item in actions.EnumerateArray())
                {
                    take.Actions.Add(ReadAction(new Obj(item, UiText.Format("", $"{obj.Child("actions")}[{index}]"))));
                    index++;
                }
            }

            if (requireName)
                obj.CollectExtras(take.Extras);
            return take;
        }

        private Shot ReadShot(Obj obj)
        {
            var shot = new Shot();
            string typeText = obj.OptString("type")
                ?? throw new ShotFileException(obj.Child("type"), UiText.MessageText("text.d223685a5355", "a shot needs a type (orbit, crane, tripod, track, chase, part or path)"));
            shot.Type = Obj.ParseEnum<ShotType>(obj.Child("type"), typeText);
            shot.Target = Shot.DefaultTarget(shot.Type);
            if (obj.TryGet("target", out JsonElement target))
            {
                string targetText = Obj.AsString(obj.Child("target"), target);
                if (string.Equals(targetText, "separated", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Shot.SupportsAim(shot.Type))
                        throw new ShotFileException(obj.Child("target"), UiText.MessageText("text.fd91d86e4f2c", "only a chase, part, path or track shot can follow a separated vehicle"));
                    shot.Subject = ShotSubject.Separated;
                    shot.Target = ShotTarget.Vehicle;
                }
                else
                {
                    shot.Target = Obj.ParseEnum<ShotTarget>(obj.Child("target"), targetText);
                }
            }

            shot.Name = ReadShotName(obj);
            shot.Duration = obj.ReqNum("duration", 0.0, 3600.0, minExclusive: true);
            shot.Ease = obj.OptEnum<Ease>("ease") ?? (shot.Type == ShotType.Path ? Ease.InOut : Ease.None);
            shot.Fov = obj.OptNum("fov", 1.0, 150.0) ?? 40.0;
            shot.Roll = obj.OptNum("roll", -180.0, 180.0) ?? 0.0;
            shot.Up = obj.OptEnum<ShotUp>("up");
            shot.AvoidSun = ReadAvoidSun(obj);
            shot.Transition = ReadTransition(obj);
            shot.At = ReadTrigger(obj, "at");

            switch (shot.Type)
            {
                case ShotType.Orbit:
                    shot.Center = obj.OptVec("center") ?? V3.Zero;
                    shot.Radius = obj.ReqNum("radius", 0.0, 1e7, minExclusive: true);
                    shot.Height = obj.OptNum("height", -1e7, 1e7) ?? 0.0;
                    shot.StartDeg = obj.OptNum("startDeg", -1e5, 1e5) ?? 0.0;
                    shot.DegPerSec = obj.OptNum("degPerSec", -1e5, 1e5) ?? 0.0;
                    shot.LookAt = obj.OptVec("lookAt") ?? V3.Zero;
                    break;
                case ShotType.Crane:
                    shot.From = obj.OptNum("from", -1e7, 1e7) ?? 0.0;
                    shot.To = obj.ReqNum("to", -1e7, 1e7);
                    shot.Radius = obj.ReqNum("radius", 0.0, 1e7);
                    shot.Azimuth = obj.OptNum("azimuth", -1e5, 1e5) ?? 0.0;
                    break;
                case ShotType.Tripod:
                    shot.Position = obj.ReqVec("position");
                    shot.LookAt = obj.ReqVec("lookAt");
                    break;
                case ShotType.Track:
                    shot.Position = obj.ReqVec("position");
                    shot.KeepSize = obj.OptBool("keepSize") ?? false;
                    shot.Fit = ReadFit(obj);
                    break;
                case ShotType.Chase:
                    shot.Frame = obj.OptEnum<ShotFrame>("frame") ?? ShotFrame.Vehicle;
                    shot.Offset = obj.ReqVec("offset");
                    shot.LookAt = obj.OptVec("lookAt") ?? V3.Zero;
                    shot.Smoothing = obj.OptNum("smoothing", 0.0, 60.0) ?? 0.5;
                    shot.Fit = ReadFit(obj);
                    break;
                case ShotType.Part:
                    shot.Frame = obj.OptEnum<ShotFrame>("frame") ?? ShotFrame.Vehicle;
                    shot.Part = obj.OptString("part") ?? "";
                    shot.Offset = obj.ReqVec("offset");
                    shot.LookAt = obj.OptVec("lookAt") ?? V3.Zero;
                    shot.Smoothing = obj.OptNum("smoothing", 0.0, 60.0) ?? 0.5;
                    shot.Fit = ReadFit(obj);
                    break;
                case ShotType.Path:
                    shot.Frame = obj.OptEnum<ShotFrame>("frame") ?? ShotFrame.Vehicle;
                    ReadPoints(obj, shot);
                    shot.Fit = ReadFit(obj);
                    break;
            }

            if (Shot.SupportsAim(shot.Type))
                ReadAim(obj, shot);
            if (PoseMath.SupportsFit(shot.Type))
                ReadFitBy(obj, shot);

            obj.CollectExtras(shot.Extras);
            return shot;
        }

        private void ReadAim(Obj obj, Shot shot)
        {
            shot.Aim = obj.OptEnum<ShotAim>("aim") ?? ShotAim.Look;
            V3? offset = obj.OptVec("aimOffset");
            if (offset.HasValue)
            {
                shot.AimOffset = offset.Value;
                if (shot.Aim != ShotAim.Vehicle)
                    _warnings.Add(UiText.Format("text.5e55f9bd850e", $"{obj.Child("aimOffset")}: the aim offset has no effect unless \"aim\" is \"vehicle\""));
            }

            SeparatedPick? pick = obj.OptEnum<SeparatedPick>("pick");
            if (pick.HasValue)
            {
                shot.Pick = pick.Value;
                if (shot.Subject != ShotSubject.Separated)
                    _warnings.Add(UiText.Format("text.79e5284b0030", $"{obj.Child("pick")}: the pick has no effect unless \"target\" is \"separated\""));
            }
        }

        private void ReadFitBy(Obj obj, Shot shot)
        {
            FitMeasure? by = obj.OptEnum<FitMeasure>("fitBy");
            if (!by.HasValue)
                return;
            shot.FitBy = by.Value;
            if (shot.Fit == null)
                _warnings.Add(UiText.Format("text.fd1b375ab3f7", $"{obj.Child("fitBy")}: the fit measure has no effect unless \"fit\" is on"));
        }

        // A number is the smallest angle between the view and the sun in degrees. "off" and false turn it off.
        private static double? ReadAvoidSun(Obj obj)
        {
            if (!obj.TryGet("avoidSun", out JsonElement value))
                return null;
            string path = obj.Child("avoidSun");
            if (value.ValueKind == JsonValueKind.False
                || (value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), "off", StringComparison.OrdinalIgnoreCase)))
                return null;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double angle))
                throw new ShotFileException(path, UiText.MessageText("text.bc6b8082dab8", "expected the smallest angle to the sun in degrees (for example 30), or \"off\""));
            if (double.IsNaN(angle) || angle <= 0.0 || angle > 90.0)
                throw new ShotFileException(path, UiText.Format("text.cf7c6098dc06", $"the value {angle:G} is out of range (must be greater than 0 and at most 90)"));
            return angle;
        }

        private static PreRoll ReadPreRoll(Obj obj, IReadOnlyDictionary<string, string> aliases)
        {
            string until = obj.OptString("until")
                ?? throw new ShotFileException(obj.Child("until"), UiText.MessageText("text.6e8374a71c84", "a pre-roll needs the event that ends it, for example \"liftoff\""));
            if (!EventName.TryResolve(until, aliases, out _, out string error))
                throw new ShotFileException(obj.Child("until"), error);
            var preRoll = new PreRoll { Until = until.Trim(), Loop = obj.OptBool("loop") ?? true };
            obj.RejectUnknown();
            return preRoll;
        }

        // A number is the share of the picture height. "off" and false turn the fit off.
        private static double? ReadFit(Obj obj)
        {
            if (!obj.TryGet("fit", out JsonElement value))
                return null;
            string path = obj.Child("fit");
            if (value.ValueKind == JsonValueKind.False
                || (value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), "off", StringComparison.OrdinalIgnoreCase)))
                return null;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double fit))
                throw new ShotFileException(path, UiText.MessageText("text.66107494d2da", "expected a number from 0.05 to 1.5 (the share of the picture height that the vehicle fills), or \"off\""));
            if (double.IsNaN(fit) || fit < 0.05 || fit > 1.5)
                throw new ShotFileException(path, UiText.Format("text.fb9f84a52944", $"the value {fit:G} is out of range (must be at least 0.05 and at most 1.5)"));
            return fit;
        }

        private static void ReadPoints(Obj owner, Shot shot)
        {
            string listPath = owner.Child("points");
            if (!owner.TryGet("points", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                throw new ShotFileException(listPath, UiText.MessageText("text.3b27f49fac61", "expected a list of path points"));
            int index = 0;
            foreach (JsonElement item in list.EnumerateArray())
            {
                var obj = new Obj(item, UiText.Format("", $"{listPath}[{index}]"));
                var point = new PathPoint
                {
                    T = obj.OptNum("t", 0.0, 1.0) ?? 0.0,
                    Position = obj.ReqVec("position"),
                    LookAt = obj.ReqVec("lookAt"),
                    Fov = obj.OptNum("fov", 1.0, 150.0),
                    Roll = obj.OptNum("roll", -180.0, 180.0),
                };
                obj.RejectUnknown();
                shot.Points.Add(point);
                index++;
            }

            if (shot.Points.Count == 0)
                throw new ShotFileException(listPath, UiText.MessageText("text.894a01808ddf", "a path needs at least one point"));
            string? problem = PathMath.CheckTimes(shot.Points);
            if (problem != null)
                throw new ShotFileException(listPath, problem);
        }

        private SpeedKey ReadSpeedKey(Obj obj)
        {
            var key = new SpeedKey
            {
                At = ReadTrigger(obj, "at"),
                To = obj.ReqNum("to", 0.0, 1000.0),
                Ramp = obj.OptNum("ramp", 0.0, 3600.0) ?? 0.0,
                Ease = obj.OptEnum<Ease>("ease") ?? Ease.InOut,
            };
            obj.CollectExtras(key.Extras);
            return key;
        }

        private TakeAction ReadAction(Obj obj)
        {
            string kindText = obj.OptString("action")
                ?? throw new ShotFileException(obj.Child("action"), UiText.MessageText("text.799ad5992e7f", "an action needs a kind (stage or cue)"));
            var action = new TakeAction { Kind = Obj.ParseEnum<ActionKind>(obj.Child("action"), kindText) };
            if (!obj.Has("at"))
                throw new ShotFileException(obj.Child("at"), UiText.MessageText("text.6dccc2cd9dbb", "an action needs a trigger: a take time or an event"));
            action.At = ReadTrigger(obj, "at");

            // A stage action leaves "name" to the unknown fields, which are kept, so an older file reads as before.
            if (action.Kind == ActionKind.Cue)
                action.Name = ReadCueName(obj);

            double? sequence = obj.OptNum("sequence", 1.0, 1e6);
            if (sequence.HasValue)
            {
                if (action.Kind != ActionKind.Stage)
                    throw new ShotFileException(obj.Child("sequence"), UiText.MessageText("text.a01a32dba458", "only a stage action has a sequence number"));
                if (sequence.Value != Math.Floor(sequence.Value))
                    throw new ShotFileException(obj.Child("sequence"), UiText.MessageText("text.5015ab8d9d3b", "expected a whole number from 1"));
                action.Sequence = (int)sequence.Value;
            }

            obj.CollectExtras(action.Extras);
            return action;
        }

        private static string ReadCueName(Obj obj)
        {
            string name = obj.OptString("name")
                ?? throw new ShotFileException(obj.Child("name"), UiText.Concat(UiText.MessageText("text.c67be58ca8c2", "a cue needs a name, "), TakeAction.NameRule));
            if (!TakeAction.IsValidName(name))
                throw new ShotFileException(obj.Child("name"), UiText.Concat("expected ", TakeAction.NameRule));
            return name;
        }

        // An older file can have a free label in the "name" of a shot, because the field had no meaning before.
        // A label that is not a valid shot name stays in the unknown fields, so the file reads and writes as before.
        private string ReadShotName(Obj obj)
        {
            if (!obj.Peek("name", out JsonElement value))
                return "";
            if (value.ValueKind == JsonValueKind.String && TakeAction.IsValidName(value.GetString()))
                return obj.OptString("name")!;
            _warnings.Add(UiText.Format("text.56b105fb81f1", $"{obj.Child("name")}: this is not used as the shot name, because a shot name has {TakeAction.NameRule}. The field is kept as it is"));
            return "";
        }

        private Transition ReadTransition(Obj obj)
        {
            if (!obj.TryGet("transition", out JsonElement value))
                return new Transition();
            string path = obj.Child("transition");
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    string text = value.GetString()!;
                    if (string.Equals(text, "cut", StringComparison.OrdinalIgnoreCase))
                        return new Transition();
                    if (string.Equals(text, "blend", StringComparison.OrdinalIgnoreCase))
                        return new Transition { Kind = TransitionKind.Blend, Seconds = Transition.DefaultSeconds };
                    throw new ShotFileException(path, UiText.Format("text.0eb406eac9a4", $"unknown transition \"{text}\" (cut or blend)"));
                case JsonValueKind.Number:
                    return BlendOf(path, value.GetDouble());
                case JsonValueKind.Object:
                    var inner = new Obj(value, path);
                    string kind = inner.OptString("type") ?? "cut";
                    var kindValue = Obj.ParseEnum<TransitionKind>(inner.Child("type"), kind);
                    if (kindValue == TransitionKind.Cut)
                        return new Transition();
                    return BlendOf(inner.Child("seconds"), inner.OptNum("seconds", 0.0, 3600.0, minExclusive: true) ?? Transition.DefaultSeconds);
                default:
                    throw new ShotFileException(path, UiText.MessageText("text.b53051e5d62b", "expected \"cut\", a number of blend seconds, or an object with \"type\" and \"seconds\""));
            }
        }

        private static Transition BlendOf(string path, double seconds)
        {
            if (!(seconds > 0.0) || seconds > 3600.0)
                throw new ShotFileException(path, UiText.MessageText("text.e9f8c01f92c3", "the blend length must be greater than 0 and at most 3600 seconds"));
            return new Transition { Kind = TransitionKind.Blend, Seconds = seconds };
        }

        private Trigger ReadTrigger(Obj owner, string name)
        {
            if (!owner.TryGet(name, out JsonElement value))
                return Trigger.After();
            string path = owner.Child(name);
            if (value.ValueKind == JsonValueKind.Number)
                return TimeTrigger(path, value.GetDouble());
            if (value.ValueKind != JsonValueKind.Object)
                throw new ShotFileException(path, UiText.MessageText("text.01f3d07f9b77", "expected a take time in seconds, or an object with \"event\" or \"time\""));

            var obj = new Obj(value, path);
            double? time = obj.OptNum("time", 0.0, 1e7);
            string? eventName = obj.OptString("event");
            if (time.HasValue && eventName != null)
                throw new ShotFileException(path, UiText.MessageText("text.05652cac4d16", "use either \"time\" or \"event\", not both"));
            if (time.HasValue)
                return Finish(obj, Trigger.AtTime(time.Value));
            if (eventName == null)
                throw new ShotFileException(path, UiText.MessageText("text.41579babfdf0", "expected \"event\" or \"time\""));

            if (!EventName.TryResolve(eventName, _file.Events, out EventName resolved, out string error))
                throw new ShotFileException(obj.Child("event"), error);
            double delay = obj.OptNum("delay", -3600.0, 3600.0) ?? 0.0;
            if (delay < 0.0 && !resolved.IsPredictable)
            {
                _warnings.Add(UiText.Format("text.d290a4d019c3", $"{obj.Child("delay")}: a delay below 0 works only for altitude, speed, ground, slow, q and maxq events, so {delay.ToString("0.###", CultureInfo.InvariantCulture)} was set to 0"));
                delay = 0.0;
            }

            return Finish(obj, Trigger.OnEvent(eventName.Trim(), delay));

            static Trigger Finish(Obj o, Trigger trigger)
            {
                o.RejectUnknown();
                return trigger;
            }
        }

        private static Trigger TimeTrigger(string path, double time)
        {
            if (time < 0.0 || double.IsNaN(time) || double.IsInfinity(time))
                throw new ShotFileException(path, UiText.MessageText("text.394fb3560bd5", "a take time is 0 or more"));
            return Trigger.AtTime(time);
        }
    }

    // Wraps one JSON object and tracks which fields were read, so the rest are kept as extras.
    private sealed class Obj
    {
        private readonly JsonElement _element;
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);

        public string Path { get; }

        public Obj(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new ShotFileException(path, UiText.MessageText("text.63aa3b57aef1", "expected an object"));
            _element = element;
            Path = path;
        }

        public string Child(string name) => Path.Length == 0 ? name : UiText.Concat(UiText.Concat(Path, "."), name);

        public bool Has(string name) => _element.TryGetProperty(name, out JsonElement v) && v.ValueKind != JsonValueKind.Null;

        /// <summary>Gets a field without marking it as read, so it stays an extra unless a later call reads it.</summary>
        public bool Peek(string name, out JsonElement value) =>
            _element.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;

        public bool TryGet(string name, out JsonElement value)
        {
            if (_element.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null)
            {
                _used.Add(name);
                return true;
            }

            value = default;
            return false;
        }

        public void CollectExtras(Dictionary<string, JsonElement> into)
        {
            foreach (JsonProperty property in _element.EnumerateObject())
            {
                if (!_used.Contains(property.Name))
                    into[property.Name] = property.Value.Clone();
            }
        }

        public void RejectUnknown()
        {
            foreach (JsonProperty property in _element.EnumerateObject())
            {
                if (!_used.Contains(property.Name))
                    throw new ShotFileException(Child(property.Name), UiText.MessageText("text.b118d94a36a2", "unknown field"));
            }
        }

        public static string AsString(string path, JsonElement value) =>
            value.ValueKind == JsonValueKind.String
                ? value.GetString()!
                : throw new ShotFileException(path, UiText.MessageText("text.3b187d73ec74", "expected text"));

        public string? OptString(string name) =>
            TryGet(name, out JsonElement v) ? AsString(Child(name), v) : null;

        public bool? OptBool(string name)
        {
            if (!TryGet(name, out JsonElement v))
                return null;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new ShotFileException(Child(name), UiText.MessageText("text.9e23e45c5cb3", "expected true or false")),
            };
        }

        public double? OptNum(string name, double min, double max, bool minExclusive = false)
        {
            if (!TryGet(name, out JsonElement v))
                return null;
            string path = Child(name);
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double number))
                throw new ShotFileException(path, UiText.MessageText("text.326baa00937a", "expected a number"));
            bool tooLow = minExclusive ? number <= min : number < min;
            if (double.IsNaN(number) || double.IsInfinity(number) || tooLow || number > max)
            {
                string low = minExclusive ? UiText.Format("text.da018a40d08c", $"greater than {min:G}") : UiText.Format("text.40af0ae95060", $"at least {min:G}");
                throw new ShotFileException(path, UiText.Format("text.2f363e19f1fb", $"the value {number:G} is out of range (must be {low} and at most {max:G})"));
            }

            return number;
        }

        public double ReqNum(string name, double min, double max, bool minExclusive = false) =>
            OptNum(name, min, max, minExclusive) ?? throw new ShotFileException(Child(name), UiText.MessageText("text.7d4dd646b1f8", "this field is required"));

        public V3? OptVec(string name)
        {
            if (!TryGet(name, out JsonElement v))
                return null;
            string path = Child(name);
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() != 3)
                throw new ShotFileException(path, UiText.MessageText("text.b51a96b57b7c", "expected a vector of three numbers, for example [0, 1, 2]"));
            var parts = new double[3];
            int i = 0;
            foreach (JsonElement item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double d)
                    || double.IsNaN(d) || double.IsInfinity(d) || Math.Abs(d) > 1e9)
                    throw new ShotFileException(UiText.Format("", $"{path}[{i}]"), UiText.MessageText("text.102dd982b5d8", "expected a number between -1e9 and 1e9"));
                parts[i++] = d;
            }

            return new V3(parts[0], parts[1], parts[2]);
        }

        public V3 ReqVec(string name) =>
            OptVec(name) ?? throw new ShotFileException(Child(name), UiText.MessageText("text.7d4dd646b1f8", "this field is required"));

        public T? OptEnum<T>(string name) where T : struct, Enum =>
            TryGet(name, out JsonElement v) ? ParseEnum<T>(Child(name), AsString(Child(name), v)) : null;

        public static T ParseEnum<T>(string path, string text) where T : struct, Enum
        {
            foreach (string candidate in Enum.GetNames<T>())
            {
                if (string.Equals(candidate, text, StringComparison.OrdinalIgnoreCase))
                    return Enum.Parse<T>(candidate);
            }

            string known = UiText.Join(", ", Enum.GetNames<T>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]));
            throw new ShotFileException(path, UiText.Format("text.a6f41c991c6e", $"unknown value \"{text}\" (known: {known})"));
        }
    }

    #endregion

    #region Writing

    public static string Write(ShotFile file)
    {
        var json = new JsonOut();
        json.BeginObject();
        json.Key("formatVersion").Num(Math.Max(file.FormatVersion, ShotFile.CurrentFormatVersion));
        if (file.Site != null)
            json.Key("site").Str(file.Site);
        json.Key("fps").Num(file.Fps);
        if (file.HideUi)
            json.Key("hideUi").Bool(true);
        if (file.HideUiAt != null)
            json.Key("hideUiAt").Str(file.HideUiAt);
        if (file.SyncFlash)
            json.Key("syncFlash").Bool(true);
        if (!file.Capture.IsDefault)
        {
            CaptureOptions capture = file.Capture;
            json.Key("capture").BeginObject();
            json.Key("mode").Str(Lower(capture.Mode.ToString()));
            if (capture.Fps.HasValue)
                json.Key("fps").Num(capture.Fps.Value);
            if (capture.Folder != null)
                json.Key("folder").Str(capture.Folder);
            if (capture.Scale != 1)
                json.Key("scale").Num(capture.Scale);
            if (capture.Grab != CaptureGrab.Swapchain)
                json.Key("grab").Str(Lower(capture.Grab.ToString()));
            WriteExtras(json, capture.Extras);
            json.EndObject();
        }

        if (file.KeepOut.Count > 0)
            WriteKeepOut(json, file.KeepOut);

        if (file.Events.Count > 0)
        {
            json.Key("events").BeginObject();
            foreach (KeyValuePair<string, string> pair in file.Events)
                json.Key(pair.Key).Str(pair.Value);
            json.EndObject();
        }

        if (file.UsesTakeList || file.Takes.Count != 1)
        {
            json.Key("takes").BeginArray();
            foreach (Take take in file.Takes)
            {
                json.Item().BeginObject();
                json.Key("name").Str(take.Name);
                if (take.IsDefault)
                    json.Key("default").Bool(true);
                if (take.Site != null)
                    json.Key("site").Str(take.Site);
                WriteAnchor(json, take);
                if (take.HideUiAt != null)
                    json.Key("hideUiAt").Str(take.HideUiAt);
                if (take.KeepOut != null)
                    WriteKeepOut(json, take.KeepOut);
                WriteCheckpoint(json, take);
                WriteTrack(json, take);
                WriteExtras(json, take.Extras);
                json.EndObject();
            }

            json.EndArray();
        }
        else
        {
            WriteAnchor(json, file.Takes[0]);
            WriteCheckpoint(json, file.Takes[0]);
            WriteTrack(json, file.Takes[0]);
        }

        WriteExtras(json, file.Extras);
        json.EndObject();
        return UiText.Concat(json.ToString(), "\n");
    }

    public static void Save(ShotFile file, string path) =>
        File.WriteAllText(path, Write(file), new UTF8Encoding(false));

    private static void WriteKeepOut(JsonOut json, List<KeepOutBox> boxes)
    {
        json.Key("keepOut").BeginArray();
        foreach (KeepOutBox box in boxes)
        {
            string name = box.Name.Length > 0 ? UiText.Format("", $", \"name\": {JsonOut.Quote(box.Name)}") : "";
            json.Item().Raw(UiText.Format("", $"{{ \"min\": {VecText(box.Min)}, \"max\": {VecText(box.Max)}{name} }}"));
        }

        json.EndArray();
    }

    private static void WriteAnchor(JsonOut json, Take take)
    {
        if (take.Anchor is not { } anchor)
            return;
        json.Key("anchor").BeginObject();
        json.Key("body").Str(anchor.Body);
        json.Key("latitude").Num(anchor.Latitude);
        json.Key("longitude").Num(anchor.Longitude);
        json.Key("altitude").Num(anchor.Altitude);
        WriteExtras(json, anchor.Extras);
        json.EndObject();
    }

    private static void WriteCheckpoint(JsonOut json, Take take)
    {
        if (take.Checkpoint != null)
            json.Key("checkpoint").Str(take.Checkpoint);
        if (take.AutoStart)
            json.Key("autoStart").Bool(true);
        if (take.AutoStartDelay != Take.DefaultAutoStartDelay)
            json.Key("autoStartDelay").Num(take.AutoStartDelay);
        WriteLook(json, take);
    }

    private static void WriteLook(JsonOut json, Take take)
    {
        if (take.Sound is { IsEmpty: false } sound)
        {
            json.Key("sound").BeginObject();
            if (sound.Music.HasValue)
                json.Key("music").Num(sound.Music.Value);
            if (sound.Effects.HasValue)
                json.Key("effects").Num(sound.Effects.Value);
            WriteExtras(json, sound.Extras);
            json.EndObject();
        }

        if (take.Picture is { IsEmpty: false } picture)
        {
            json.Key("picture").BeginObject();
            if (picture.Exposure.HasValue)
                json.Key("exposure").Num(picture.Exposure.Value);
            if (picture.CloudAmbientLight.HasValue)
                json.Key("cloudAmbientLight").Bool(picture.CloudAmbientLight.Value);
            if (picture.CloudShadows.HasValue)
                json.Key("cloudShadows").Bool(picture.CloudShadows.Value);
            if (picture.Reflections.HasValue)
                json.Key("reflections").Bool(picture.Reflections.Value);
            WriteExtras(json, picture.Extras);
            json.EndObject();
        }

        if (take.ExactApsides)
            json.Key("exactApsides").Bool(true);
        if (take.PauseAt is double pauseAt)
            json.Key("pauseAt").Num(pauseAt);
    }

    private static void WriteTrack(JsonOut json, Take take)
    {
        if (take.PreRoll is { } preRoll)
        {
            string loop = preRoll.Loop ? "" : ", \"loop\": false";
            json.Key("preRoll").Raw(UiText.Format("", $"{{ \"until\": {JsonOut.Quote(preRoll.Until)}{loop} }}"));
        }

        json.Key("camera").BeginArray();
        foreach (Shot shot in take.Shots)
        {
            json.Item().BeginObject();
            WriteShot(json, shot);
            json.EndObject();
        }

        json.EndArray();

        if (take.Speed.Count > 0)
        {
            json.Key("speed").BeginArray();
            foreach (SpeedKey key in take.Speed)
            {
                json.Item().BeginObject();
                WriteTrigger(json, key.At);
                json.Key("to").Num(key.To);
                json.Key("ramp").Num(key.Ramp);
                json.Key("ease").Str(EaseText(key.Ease));
                WriteExtras(json, key.Extras);
                json.EndObject();
            }

            json.EndArray();
        }

        if (take.Actions.Count > 0)
        {
            json.Key("actions").BeginArray();
            foreach (TakeAction action in take.Actions)
            {
                json.Item().BeginObject();
                WriteTrigger(json, action.At);
                json.Key("action").Str(Lower(action.Kind.ToString()));
                if (action.Kind == ActionKind.Cue)
                    json.Key("name").Str(action.Name);
                if (action.Sequence.HasValue)
                    json.Key("sequence").Num(action.Sequence.Value);
                WriteExtras(json, action.Extras, action.Kind == ActionKind.Cue ? "name" : null);
                json.EndObject();
            }

            json.EndArray();
        }
    }

    private static void WriteShot(JsonOut json, Shot shot)
    {
        json.Key("type").Str(Lower(shot.Type.ToString()));
        if (shot.Name.Length > 0)
            json.Key("name").Str(shot.Name);
        json.Key("target").Str(shot.Subject == ShotSubject.Separated && Shot.SupportsAim(shot.Type) ? "separated" : Lower(shot.Target.ToString()));
        switch (shot.Type)
        {
            case ShotType.Orbit:
                json.Key("center").Vec(shot.Center);
                json.Key("radius").Num(shot.Radius);
                json.Key("height").Num(shot.Height);
                json.Key("startDeg").Num(shot.StartDeg);
                json.Key("degPerSec").Num(shot.DegPerSec);
                json.Key("lookAt").Vec(shot.LookAt);
                break;
            case ShotType.Crane:
                json.Key("from").Num(shot.From);
                json.Key("to").Num(shot.To);
                json.Key("radius").Num(shot.Radius);
                json.Key("azimuth").Num(shot.Azimuth);
                break;
            case ShotType.Tripod:
                json.Key("position").Vec(shot.Position);
                json.Key("lookAt").Vec(shot.LookAt);
                break;
            case ShotType.Track:
                json.Key("position").Vec(shot.Position);
                if (shot.KeepSize)
                    json.Key("keepSize").Bool(true);
                WriteFit(json, shot);
                break;
            case ShotType.Chase:
                if (shot.Frame != ShotFrame.Vehicle)
                    json.Key("frame").Str(Lower(shot.Frame.ToString()));
                json.Key("offset").Vec(shot.Offset);
                json.Key("lookAt").Vec(shot.LookAt);
                json.Key("smoothing").Num(shot.Smoothing);
                WriteFit(json, shot);
                break;
            case ShotType.Part:
                if (shot.Frame != ShotFrame.Vehicle)
                    json.Key("frame").Str(Lower(shot.Frame.ToString()));
                if (shot.Part.Length > 0)
                    json.Key("part").Str(shot.Part);
                json.Key("offset").Vec(shot.Offset);
                json.Key("lookAt").Vec(shot.LookAt);
                json.Key("smoothing").Num(shot.Smoothing);
                WriteFit(json, shot);
                break;
            case ShotType.Path:
                if (shot.UsesHorizonFrame)
                    json.Key("frame").Str(Lower(shot.Frame.ToString()));
                json.Key("points").BeginArray();
                foreach (PathPoint point in shot.Points)
                {
                    string fov = point.Fov.HasValue ? UiText.Format("", $", \"fov\": {Number(point.Fov.Value)}") : "";
                    string roll = point.Roll.HasValue ? UiText.Format("", $", \"roll\": {Number(point.Roll.Value)}") : "";
                    json.Item().Raw(UiText.Format("", $"{{ \"t\": {Number(point.T)}, \"position\": {VecText(point.Position)}, \"lookAt\": {VecText(point.LookAt)}{fov}{roll} }}"));
                }

                json.EndArray();
                WriteFit(json, shot);
                break;
        }

        json.Key("duration").Num(shot.Duration);
        json.Key("ease").Str(EaseText(shot.Ease));
        json.Key("fov").Num(shot.Fov);
        if (shot.Roll != 0.0)
            json.Key("roll").Num(shot.Roll);
        if (shot.Up is { } up)
            json.Key("up").Str(Lower(up.ToString()));
        if (shot.AvoidSun.HasValue)
            json.Key("avoidSun").Num(shot.AvoidSun.Value);
        if (Shot.SupportsAim(shot.Type))
        {
            if (shot.Aim == ShotAim.Vehicle)
            {
                json.Key("aim").Str("vehicle");
                if (shot.AimOffset != V3.Zero)
                    json.Key("aimOffset").Vec(shot.AimOffset);
            }

            if (shot.Subject == ShotSubject.Separated && shot.Pick != SeparatedPick.Newest)
                json.Key("pick").Str(Lower(shot.Pick.ToString()));
        }

        if (shot.Transition.Kind == TransitionKind.Blend)
            json.Key("transition").Raw(UiText.Format("", $"{{ \"type\": \"blend\", \"seconds\": {Number(shot.Transition.Seconds)} }}"));
        WriteTrigger(json, shot.At);
        WriteExtras(json, shot.Extras, shot.Name.Length > 0 ? "name" : null);
    }

    private static void WriteFit(JsonOut json, Shot shot)
    {
        if (shot.Fit.HasValue)
            json.Key("fit").Num(shot.Fit.Value);
        if (shot.Fit.HasValue && shot.FitBy != FitMeasure.Size)
            json.Key("fitBy").Str(Lower(shot.FitBy.ToString()));
    }

    private static void WriteTrigger(JsonOut json, Trigger trigger)
    {
        switch (trigger.Kind)
        {
            case TriggerKind.Time:
                json.Key("at").Num(trigger.Time);
                break;
            case TriggerKind.Event:
                string delay = trigger.Delay != 0.0 ? UiText.Format("", $", \"delay\": {Number(trigger.Delay)}") : "";
                json.Key("at").Raw(UiText.Format("", $"{{ \"event\": {JsonOut.Quote(trigger.Event)}{delay} }}"));
                break;
        }
    }

    // An extra with the key of a field that the object already wrote is left out, because a JSON reader takes the
    // last of two equal keys and would read the old value.
    private static void WriteExtras(JsonOut json, Dictionary<string, JsonElement> extras, string? written = null)
    {
        foreach (KeyValuePair<string, JsonElement> pair in extras)
        {
            if (pair.Key != written)
                json.Key(pair.Key).Raw(pair.Value.GetRawText());
        }
    }

    private static string VecText(V3 v) => UiText.Format("", $"[{Number(v.X)}, {Number(v.Y)}, {Number(v.Z)}]");

    private static string Lower(string text) => UiText.Concat(char.ToLowerInvariant(text[0]), text[1..]);

    private static string EaseText(Ease ease) => Lower(ease.ToString());

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private sealed class JsonOut
    {
        private readonly StringBuilder _text = new();
        private readonly Stack<bool> _first = new();

        public override string ToString() => _text.ToString();

        public static string Quote(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c > 0x7E)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }

            return sb.Append('"').ToString();
        }

        public JsonOut BeginObject() => Begin('{');

        public JsonOut BeginArray() => Begin('[');

        public JsonOut EndObject() => End('}');

        public JsonOut EndArray() => End(']');

        public JsonOut Key(string name)
        {
            NextSlot();
            _text.Append(Quote(name)).Append(": ");
            return this;
        }

        public JsonOut Item()
        {
            NextSlot();
            return this;
        }

        public JsonOut Str(string value) => Raw(Quote(value));

        public JsonOut Num(double value) => Raw(Number(value));

        public JsonOut Bool(bool value) => Raw(value ? "true" : "false");

        public JsonOut Vec(V3 value) => Raw(UiText.Format("", $"[{Number(value.X)}, {Number(value.Y)}, {Number(value.Z)}]"));

        public JsonOut Raw(string value)
        {
            _text.Append(value);
            return this;
        }

        private JsonOut Begin(char open)
        {
            _text.Append(open);
            _first.Push(true);
            return this;
        }

        private JsonOut End(char close)
        {
            bool wasEmpty = _first.Pop();
            if (!wasEmpty)
            {
                _text.Append('\n');
                _text.Append(' ', _first.Count * 2);
            }

            _text.Append(close);
            return this;
        }

        private void NextSlot()
        {
            bool first = _first.Pop();
            if (!first)
                _text.Append(',');
            _first.Push(false);
            _text.Append('\n');
            _text.Append(' ', _first.Count * 2);
        }
    }

    #endregion
}
