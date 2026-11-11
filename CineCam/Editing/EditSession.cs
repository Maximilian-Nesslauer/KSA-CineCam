using System.Text;
using CineCam.Director;

namespace CineCam.Editing;

/// <summary>The window changes the document and calls <see cref="Changed"/>. Nothing here touches the game.</summary>
public sealed class EditSession
{
    private string _backedUpPath = "";

    public ShotFile? Document { get; private set; }

    public string Path { get; set; } = "";

    public bool Dirty { get; private set; }

    /// <summary>Counts every change of the document and of the selected take, so views can refresh their caches.</summary>
    public int Version { get; private set; }

    public string Message { get; private set; } = "";

    public bool MessageIsError { get; private set; }

    public ValidationResult Validation { get; private set; } = ValidationResult.Ok;

    public int TakeIndex { get; private set; }

    public int ShotIndex { get; set; } = -1;

    public int KeyIndex { get; set; } = -1;

    public int ActionIndex { get; set; } = -1;

    public Take? Take =>
        Document != null && Document.Takes.Count > 0 ? Document.Takes[Math.Clamp(TakeIndex, 0, Document.Takes.Count - 1)] : null;

    public Shot? SelectedShot =>
        Take is { } take && ShotIndex >= 0 && ShotIndex < take.Shots.Count ? take.Shots[ShotIndex] : null;

    public TakeAction? SelectedAction =>
        Take is { } take && ActionIndex >= 0 && ActionIndex < take.Actions.Count ? take.Actions[ActionIndex] : null;

    public SpeedKey? SelectedKey =>
        Take is { } take && KeyIndex >= 0 && KeyIndex < take.Speed.Count ? take.Speed[KeyIndex] : null;

    #region File

    public bool Load(string path)
    {
        try
        {
            ShotFileReadResult result = ShotFileJson.Load(path);
            Document = result.File;
            Path = path;
            Dirty = false;
            TakeIndex = Math.Max(0, Document.Takes.IndexOf(Document.DefaultTake));
            ShotIndex = Document.Takes[TakeIndex].Shots.Count > 0 ? 0 : -1;
            KeyIndex = -1;
            ActionIndex = -1;
            Revalidate();
            SetMessage(result.Warnings.Count == 0
                ? $"Loaded {Document.Takes.Count} take(s)."
                : $"Loaded {Document.Takes.Count} take(s). Warning: {result.Warnings[0]}", false);
            Version++;
            return true;
        }
        catch (ShotFileException ex)
        {
            SetMessage($"The file is not valid: {ex.Message}", true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetMessage($"The file could not be read: {ex.Message}", true);
        }

        return false;
    }

    /// <summary>The document counts as changed until it is saved.</summary>
    public void NewFromDefault(string path)
    {
        Document = DefaultShotFile.Load().File;
        Path = path;
        TakeIndex = Math.Max(0, Document.Takes.IndexOf(Document.DefaultTake));
        ShotIndex = Document.Takes[TakeIndex].Shots.Count > 0 ? 0 : -1;
        KeyIndex = -1;
        ActionIndex = -1;
        Dirty = true;
        Revalidate();
        SetMessage("A new file from the default shots. Save it to keep it.", false);
        Version++;
    }

    /// <summary>A revert or a load clears the dirty flag too, so a caller that waits for a save reads this count.</summary>
    public int SaveCount { get; private set; }

    /// <summary>
    /// A document with errors is not written, because a take could not read the file. The previous file is
    /// kept once per session as a .bak file, because the writer does not keep the comments of a file.
    /// </summary>
    public bool Save()
    {
        if (Document == null)
        {
            SetMessage("There is nothing to save.", true);
            return false;
        }

        if (string.IsNullOrWhiteSpace(Path))
        {
            SetMessage("The file path is empty.", true);
            return false;
        }

        Revalidate();
        if (!Validation.IsValid)
        {
            SetMessage($"Not saved. Fix {Validation.Errors.Count} problem(s) first.", true);
            return false;
        }

        try
        {
            string? folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            if (File.Exists(Path) && !string.Equals(_backedUpPath, Path, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(Path, Path + ".bak", overwrite: true);
                _backedUpPath = Path;
            }

            string temp = Path + ".tmp";
            File.WriteAllText(temp, ShotFileJson.Write(Document), new UTF8Encoding(false));
            File.Move(temp, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetMessage($"The file could not be written: {ex.Message}", true);
            return false;
        }

        Dirty = false;
        Version++;
        SaveCount++;
        SetMessage("Saved.", false);
        return true;
    }

    /// <summary>Throws <see cref="ShotFileException"/> when the document is not valid.</summary>
    public ShotFile CopyForRun() =>
        Document == null ? throw new ShotFileException("", "no file is loaded") : ShotFileValidator.Copy(Document);

    public void SetMessage(string text, bool isError)
    {
        Message = text;
        MessageIsError = isError;
    }

    #endregion

    #region Changes

    public void Changed()
    {
        Dirty = true;
        Version++;
        if (_validationHeld)
            _validationPending = true;
        else
            Revalidate();
    }

    private bool _validationHeld;
    private bool _validationPending;

    /// <summary>
    /// For a drag that changes the document in many small steps. <see cref="Validation"/> keeps the result
    /// from before the drag until <see cref="ReleaseValidation"/>.
    /// </summary>
    public void HoldValidation() => _validationHeld = true;

    public void ReleaseValidation()
    {
        _validationHeld = false;
        if (!_validationPending)
            return;
        _validationPending = false;
        Revalidate();
    }

    private void Revalidate() =>
        Validation = Document == null ? ValidationResult.Ok : ShotFileValidator.Validate(Document);

    public void SelectTake(int index)
    {
        if (Document == null || Document.Takes.Count == 0)
            return;
        TakeIndex = Math.Clamp(index, 0, Document.Takes.Count - 1);
        ShotIndex = Document.Takes[TakeIndex].Shots.Count > 0 ? 0 : -1;
        KeyIndex = -1;
        ActionIndex = -1;
        Version++;
    }

    public void AddTake()
    {
        Document ??= new ShotFile();
        var take = new Take { Name = ShotEdit.UniqueTakeName(Document.Takes, "take"), IsDefault = Document.Takes.Count == 0 };
        take.Shots.Add(ShotPresets.Generic());
        Document.Takes.Add(take);
        Document.UsesTakeList = true;
        TakeIndex = Document.Takes.Count - 1;
        ShotIndex = 0;
        KeyIndex = -1;
        ActionIndex = -1;
        Changed();
    }

    /// <summary>Leaves the selection as it is, and returns false when a take of that name exists, so an import does not add it twice.</summary>
    public bool ImportTake(Take take)
    {
        Document ??= new ShotFile();
        if (Document.FindTake(take.Name) != null)
            return false;
        take.IsDefault = Document.Takes.Count == 0;
        Document.Takes.Add(take);
        Document.UsesTakeList = true;
        Changed();
        return true;
    }

    public bool SelectTakeByName(string name)
    {
        if (Document?.FindTake(name) is not { } take)
            return false;
        SelectTake(Document.Takes.IndexOf(take));
        return true;
    }

    public void DuplicateTake()
    {
        if (Document == null || Take is not { } source)
            return;
        Take copy = ShotEdit.Clone(source, ShotEdit.UniqueTakeName(Document.Takes, source.Name + "-copy"));
        Document.Takes.Insert(TakeIndex + 1, copy);
        Document.UsesTakeList = true;
        TakeIndex++;
        Changed();
    }

    /// <summary>The last take cannot be removed.</summary>
    public void RemoveTake()
    {
        if (Document == null || Document.Takes.Count <= 1 || Take is not { } take)
            return;
        Document.Takes.Remove(take);
        if (take.IsDefault)
            Document.Takes[0].IsDefault = true;
        TakeIndex = Math.Clamp(TakeIndex, 0, Document.Takes.Count - 1);
        ShotIndex = Document.Takes[TakeIndex].Shots.Count > 0 ? 0 : -1;
        KeyIndex = -1;
        ActionIndex = -1;
        Changed();
    }

    public void MakeDefault()
    {
        if (Document == null || Take is not { } take)
            return;
        foreach (Take other in Document.Takes)
            other.IsDefault = ReferenceEquals(other, take);
        Changed();
    }

    public void RenameTake(string name)
    {
        if (Document == null || Take is not { } take || take.Name == name)
            return;
        take.Name = name;
        Document.UsesTakeList = true;
        Changed();
    }

    public void SetTakeSite(string site)
    {
        if (Document == null || Take is not { } take)
            return;
        take.Site = string.IsNullOrWhiteSpace(site) ? null : site.Trim();
        Document.UsesTakeList = true;
        Changed();
    }

    // A take without a Landmark and without an anchor keeps the world point of its first key, so the next
    // runs use the same origin even when the vehicle has moved.
    public void AdoptAnchor(WorldAnchor? anchor)
    {
        if (anchor == null || Take is not { } take || take.Anchor != null || take.Site != null || Document?.Site != null)
            return;
        take.Anchor = anchor;
        Changed();
    }

    public void SetAutoStart(bool autoStart)
    {
        if (Take is not { } take || take.AutoStart == autoStart)
            return;
        take.AutoStart = autoStart;
        Changed();
    }

    public void SetAutoStartDelay(double seconds)
    {
        if (Take is not { } take)
            return;
        double clamped = Math.Clamp(seconds, 0.0, 600.0);
        if (take.AutoStartDelay == clamped)
            return;
        take.AutoStartDelay = clamped;
        Changed();
    }

    /// <summary>
    /// A document without other changes is written at once, because the save on disk and the name in the
    /// file belong together. A document with unsaved changes keeps the name as one more unsaved change.
    /// </summary>
    public CheckpointRecord RecordCheckpoint(string takeName, string saveName)
    {
        if (Document?.FindTake(takeName) is not { } take)
            return CheckpointRecord.NoTake;
        bool wasDirty = Dirty;
        take.Checkpoint = saveName;
        Changed();
        return wasDirty || Save() ? CheckpointRecord.Stored : CheckpointRecord.NotSaved;
    }

    public void AddShot(ShotType type)
    {
        if (Take is not { } take)
            return;
        int at = ShotIndex >= 0 ? ShotIndex + 1 : take.Shots.Count;
        take.Shots.Insert(at, ShotEdit.NewShot(type));
        ShotIndex = at;
        Changed();
    }

    public void InsertShot(Shot shot) =>
        InsertShot(shot, Take is { } take && ShotIndex >= 0 && ShotIndex < take.Shots.Count ? ShotIndex + 1 : int.MaxValue);

    public void InsertShot(Shot shot, int index)
    {
        if (Take is not { } take)
            return;
        int at = Math.Clamp(index, 0, take.Shots.Count);
        take.Shots.Insert(at, shot);
        ShotIndex = at;
        Changed();
    }

    public void DuplicateShot()
    {
        if (Take is not { } take || SelectedShot is not { } shot)
            return;
        take.Shots.Insert(ShotIndex + 1, ShotEdit.Clone(shot));
        ShotIndex++;
        Changed();
    }

    public void RemoveShot()
    {
        if (Take is not { } take || SelectedShot == null)
            return;
        take.Shots.RemoveAt(ShotIndex);
        ShotIndex = Math.Min(ShotIndex, take.Shots.Count - 1);
        Changed();
    }

    public void MoveShot(int delta)
    {
        if (Take is not { } take)
            return;
        int moved = ShotEdit.Move(take.Shots, ShotIndex, delta);
        if (moved == ShotIndex)
            return;
        ShotIndex = moved;
        Changed();
    }

    public void AddKey()
    {
        if (Take is not { } take)
            return;
        int at = KeyIndex >= 0 ? KeyIndex + 1 : take.Speed.Count;
        take.Speed.Insert(at, ShotEdit.NewSpeedKey());
        KeyIndex = at;
        Changed();
    }

    public void DuplicateKey()
    {
        if (Take is not { } take || SelectedKey is not { } key)
            return;
        take.Speed.Insert(KeyIndex + 1, ShotEdit.Clone(key));
        KeyIndex++;
        Changed();
    }

    public void RemoveKey()
    {
        if (Take is not { } take || SelectedKey == null)
            return;
        take.Speed.RemoveAt(KeyIndex);
        KeyIndex = Math.Min(KeyIndex, take.Speed.Count - 1);
        Changed();
    }

    public void MoveKey(int delta)
    {
        if (Take is not { } take)
            return;
        int moved = ShotEdit.Move(take.Speed, KeyIndex, delta);
        if (moved == KeyIndex)
            return;
        KeyIndex = moved;
        Changed();
    }

    public void AddAction()
    {
        if (Take is not { } take)
            return;
        int at = ActionIndex >= 0 ? ActionIndex + 1 : take.Actions.Count;
        take.Actions.Insert(at, ShotEdit.NewAction());
        ActionIndex = at;
        Changed();
    }

    public void DuplicateAction()
    {
        if (Take is not { } take || SelectedAction is not { } action)
            return;
        take.Actions.Insert(ActionIndex + 1, ShotEdit.Clone(action));
        ActionIndex++;
        Changed();
    }

    public void RemoveAction()
    {
        if (Take is not { } take || SelectedAction == null)
            return;
        take.Actions.RemoveAt(ActionIndex);
        ActionIndex = Math.Min(ActionIndex, take.Actions.Count - 1);
        Changed();
    }

    public void MoveAction(int delta)
    {
        if (Take is not { } take)
            return;
        int moved = ShotEdit.Move(take.Actions, ActionIndex, delta);
        if (moved == ActionIndex)
            return;
        ActionIndex = moved;
        Changed();
    }

    /// <summary>Snaps to a tenth of a second and returns true when the value changed.</summary>
    public bool SetShotDuration(int index, double seconds)
    {
        if (Take is not { } take || index < 0 || index >= take.Shots.Count)
            return false;
        double snapped = Math.Min(TimelineLayout.SnapDuration(seconds), 3600.0);
        if (take.Shots[index].Duration == snapped)
            return false;
        take.Shots[index].Duration = snapped;
        Changed();
        return true;
    }

    /// <summary>A new blend gets its earlier length, else the default length, as the transition field of the shot list does.</summary>
    public bool SetShotTransition(int index, TransitionKind kind)
    {
        if (Take is not { } take || index < 0 || index >= take.Shots.Count)
            return false;
        Transition transition = take.Shots[index].Transition;
        if (transition.Kind == kind)
            return false;
        transition.Kind = kind;
        transition.Seconds = kind == TransitionKind.Blend
            ? (transition.Seconds > 0.0 ? transition.Seconds : Transition.DefaultSeconds)
            : 0.0;
        Changed();
        return true;
    }

    /// <summary>Changes only a time trigger, snaps to a tenth of a second and returns true when the value changed.</summary>
    public bool SetKeyTime(int index, double seconds) =>
        Take is { } take && index >= 0 && index < take.Speed.Count && SetTime(take.Speed[index].At, seconds);

    /// <summary>Changes only a time trigger, snaps to a tenth of a second and returns true when the value changed.</summary>
    public bool SetActionTime(int index, double seconds) =>
        Take is { } take && index >= 0 && index < take.Actions.Count && SetTime(take.Actions[index].At, seconds);

    private bool SetTime(Trigger trigger, double seconds)
    {
        if (trigger.Kind != TriggerKind.Time)
            return false;
        double snapped = TimelineLayout.SnapTime(seconds);
        if (trigger.Time == snapped)
            return false;
        trigger.Time = snapped;
        Changed();
        return true;
    }

    #endregion
}

/// <summary>
/// NotSaved means that the take has the name as an unsaved change, because the document has problems
/// that stop the save.
/// </summary>
public enum CheckpointRecord
{
    NoTake,
    Stored,
    NotSaved,
}
