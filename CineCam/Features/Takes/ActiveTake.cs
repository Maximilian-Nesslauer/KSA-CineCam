using CineCam.Director;
using KSA;

namespace CineCam.Features.Takes;

internal sealed class ActiveTake
{
    public ActiveTake(Take take, ShotFile file, TakeDirector director, VehicleReader vehicle, SiteFrame site,
        CameraRig camera, EventLogCsv log, string folder, long startFrame)
    {
        Take = take;
        File = file;
        Director = director;
        Vehicle = vehicle;
        Site = site;
        Camera = camera;
        Log = log;
        Folder = folder;
        StartFrame = startFrame;
        HoldPose = CameraRig.CurrentPose(site);
        Clock = new TakeClock(TakeRunner.ClockSeconds);
    }

    public Take Take { get; }

    public ShotFile File { get; }

    public TakeDirector Director { get; }

    public VehicleReader Vehicle { get; }

    public SiteFrame Site { get; }

    public CameraRig Camera { get; }

    public EventLogCsv Log { get; }

    public string Folder { get; }

    public long StartFrame { get; }

    /// <summary>The real-time clock of the take. It stops while the take is paused for an edit.</summary>
    public TakeClock Clock { get; }

    /// <summary>Set for a take that saves one picture per frame, where the take time follows the picture count.</summary>
    public OfflineCapture? Offline { get; init; }

    /// <summary>The take time in seconds, the real time or the picture count over the picture rate in an offline capture.</summary>
    public double Seconds => Offline?.TakeSeconds ?? Clock.Seconds(TakeRunner.ClockSeconds);

    /// <summary>The camera pose at the take start. It holds until the first shot starts.</summary>
    public CameraPose HoldPose { get; }

    public long LastTickFrame { get; set; }

    public bool HidUi { get; set; }

    /// <summary>The canonical event that hides the interface, or null when the take hid it at its start or keeps it.</summary>
    public string? HideUiEvent { get; set; }

    public bool SavedDrawUi { get; set; }

    public bool FlashPending { get; set; }

    public double? LastWrittenSpeed { get; set; }

    /// <summary>The sim speed before the take wrote its first value, given back at the end.</summary>
    public double? SpeedBefore { get; set; }

    public bool ReportedAutoWarp { get; set; }

    /// <summary>The game time at which the ignition was seen, the zero of the mission time column.</summary>
    public double? IgnitionGameSeconds { get; set; }

    public bool ReportedSpeedOverride { get; set; }

    /// <summary>A test run, where the camera follows the shots but the sim speed is not touched and nothing is written to disk.</summary>
    public bool DryRun { get; init; }

    /// <summary>Set when the run plays one shot for the editor, so the shot list and the speed track do not run.</summary>
    public PreviewState? Preview { get; init; }

    public DirectorFrame? LastFrame { get; set; }

    /// <summary>True while the take waits for an edit, with the sim stopped and the camera free.</summary>
    public bool IsPaused => Clock.IsPaused;

    /// <summary>The sim speed that the pause gives back.</summary>
    public double PausedSpeed { get; set; } = 1.0;

    /// <summary>Indices of actions whose trigger was reached and that have not fired yet.</summary>
    public List<int> PendingActions { get; } = new();

    public StageGuard StageGuard { get; } = new();

    /// <summary>Exists only when a shot follows a vehicle that separates during the take.</summary>
    public SeparationTracker? Separation { get; set; }

    public bool HideUiDeferred { get; set; }

    /// <summary>True for a real take that records, during which the window stays hidden.</summary>
    public bool IsRecording => !DryRun && Preview == null;
}
