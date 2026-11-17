using Brutal.Logging;
using Brutal.Numerics;
using CineCam.Core;
using CineCam.Director.Replay;
using Core;
using KSA;

namespace CineCam.Features.Replay;

/// <summary>
/// Plays the engine sounds of the burning recorded nozzles, the way Vehicle.UpdateAudio plays the
/// sounds of a live vehicle: GameAudio.PlaySound with the RocketSoundEvent of the nozzle, at the
/// recorded exhaust position, with the throttle, distance, pressure and inside-view parameters. It is
/// an IAudio in GameAudio.AllIAudio, so the game calls <see cref="UpdateAudio"/> once per frame after
/// the render and keeps its cluster sounds. The game holds the sim at speed 0 in a replay, which
/// gives every sound the pitch 0 in ChannelWrapper.HandleSimSpeedPitch, so a prefix there gives the
/// sounds of this replay the pitch of the replay rate (<see cref="Owns"/>, <see cref="Rate"/>).
/// </summary>
internal sealed class ReplaySound : IAudio, IDisposable
{
    private static readonly KeyHash ThrottleHash = KeyHash.Make("Throttle".AsSpan());
    private static readonly KeyHash DistanceHash = KeyHash.Make("Distance".AsSpan());
    private static readonly KeyHash PressureHash = KeyHash.Make("Pressure".AsSpan());
    private static readonly KeyHash IvaHash = KeyHash.Make("Iva".AsSpan());

    // Pa to atm, as in the SpatialAudio constructor from an Astronomical.
    private const double PascalToAtmosphere = 9.869232667160129E-06;

    private readonly ReplayScene _scene;
    private readonly Dictionary<(ReplaySegment Segment, int Nozzle), IChannel> _voices = new();
    private readonly List<IChannel> _ending = [];
    private readonly HashSet<ClusterChannelWrapper> _clusters = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ChannelWrapper> _leaves = new(ReferenceEqualityComparer.Instance);
    private readonly List<ReplayScene.EngineSource> _sources = [];
    private readonly HashSet<(ReplaySegment Segment, int Nozzle)> _heard = new();
    private readonly List<(ReplaySegment Segment, int Nozzle)> _silent = [];
    private bool _paused;
    private bool _failed;
    private bool _disposed;

    /// <summary>The sound of the open replay, read by the pitch prefix.</summary>
    public static ReplaySound? Current { get; private set; }

    /// <summary>True when the pitch prefix went in. Without it every replay sound has the pitch 0, so the replay plays none.</summary>
    public static bool PitchHook { get; set; }

    public ReplaySound(ReplayScene scene)
    {
        _scene = scene;
        GameAudio.Register(this);
        Current = this;
    }

    /// <summary>Recording seconds per real second in the last frame, which is the sim speed that the pitch follows.</summary>
    public double Rate { get; private set; }

    public ReplaySoundMode Mode { get; private set; } = ReplaySoundMode.Off;

    /// <summary>The engines that sound in the last frame.</summary>
    public int Voices => _voices.Count;

    public double AudibleDistance => ReplaySoundMath.AudibleDistance;

    public List<ClusterChannelWrapper> ClusterChannels { get; } = new(4);

    public bool Owns(ChannelWrapper channel) => _leaves.Contains(channel);

    public bool IsAudible() => true;

    public void OnIsAudible(bool isAudible)
    {
    }

    public ClusterChannelWrapper? GetPlayingClusterChannel(KeyHash hash)
    {
        foreach (ClusterChannelWrapper cluster in ClusterChannels)
        {
            if (!cluster.IsStopping && cluster.Sound.Hash == hash)
                return cluster;
        }

        return null;
    }

    public void StopAudio(bool immediate)
    {
        foreach (IChannel channel in _voices.Values)
            channel.Stop(immediate);
        _voices.Clear();
        foreach (IChannel channel in _ending)
            channel.Stop(immediate);
        if (immediate)
        {
            _ending.Clear();
            foreach (ClusterChannelWrapper cluster in _clusters)
                cluster.Stop(stopImmediate: true);
            _clusters.Clear();
        }

        _paused = false;
    }

    // GameAudio.UpdateAudio calls this inside its loop over GameAudio.AllIAudio, so it must not
    // deregister or throw. An error turns the replay sound off until the replay closes.
    public void UpdateAudio()
    {
        if (_disposed)
            return;
        try
        {
            Update();
        }
        catch (Exception ex)
        {
            _failed = true;
            LogHelper.WarnOnce("replay-sound", $"[CineCam] The replay plays no engine sound after an error: {ex.Message}");
            try
            {
                StopAudio(immediate: true);
            }
            catch (Exception)
            {
                // The channels end with the replay.
            }
        }
        finally
        {
            CollectLeaves();
        }
    }

    private void Update()
    {
        ReplayTakePlayer player = _scene.Player;
        double rate = player.IsPlaying ? player.Plan.RecordingRate(player.Time) * player.PreviewSpeed : 0.0;
        Mode = ReplaySoundMath.Mode(CineCamSettings.ReplaySound && PitchHook && !_failed, ReplaySession.IsRendering, player.IsPlaying, rate);
        Rate = Mode == ReplaySoundMode.Play ? rate : 0.0;
        if (Mode == ReplaySoundMode.Off)
        {
            if (_voices.Count > 0 || _ending.Count > 0 || _clusters.Count > 0)
                StopAudio(immediate: true);
            return;
        }

        bool pause = Mode == ReplaySoundMode.Paused;
        if (pause != _paused)
        {
            _paused = pause;
            foreach (IChannel channel in _voices.Values)
                Pause(channel, pause);
        }

        Camera camera = GameAudio.GetAudioCamera();
        double pressure = camera.ComputeAtmosphericPressure() * PascalToAtmosphere;
        _scene.CollectEngineSources(_sources);
        _heard.Clear();
        foreach (ReplayScene.EngineSource source in _sources)
        {
            double distance = source.PositionEgo.Length();
            if (!(distance <= AudibleDistance))
                continue;
            var key = (source.Segment, source.Nozzle);
            var spatial = new SpatialAudio(source.PositionEgo, double3.Zero, pressure);
            _voices.TryGetValue(key, out IChannel? channel);
            bool start = false;
            if (channel == null || !channel.IsPlaying())
            {
                if (pause)
                    continue;
                channel?.Stop(stopImmediate: true);
                GameAudio.PlaySound(source.Sound, spatial, out channel, this, 1f, startPaused: true);
                if (channel == null)
                {
                    _voices.Remove(key);
                    continue;
                }

                _voices[key] = channel;
                start = true;
            }

            _heard.Add(key);
            channel.SetSpatialAudio(spatial);
            channel.SetParameter(ThrottleHash, source.Throttle);
            channel.SetParameter(DistanceHash, (float)distance);
            channel.SetParameter(PressureHash, (float)pressure);
            channel.SetParameter(IvaHash, 0f);
            if (start)
                Pause(channel, false);
        }

        _silent.Clear();
        foreach (var key in _voices.Keys)
        {
            if (!_heard.Contains(key))
                _silent.Add(key);
        }

        foreach (var key in _silent)
        {
            IChannel channel = _voices[key];
            _voices.Remove(key);
            // A pause keeps a sound that is out of view, so it goes on where it was.
            if (pause)
            {
                channel.Stop(stopImmediate: true);
                continue;
            }

            channel.Stop();
            _ending.Add(channel);
        }

        _ending.RemoveAll(channel => !channel.IsPlaying());
    }

    private static void Pause(IChannel channel, bool paused)
    {
        channel.SetPaused(paused);
        if (channel.SubscribedClusterSound is { } cluster && cluster.IsPaused() != paused)
            cluster.SetPaused(paused);
    }

    // The leaves of every channel that this replay started, for the pitch prefix. A cluster sound
    // stays in the set until it ends, also after GameAudio.UpdateAudio took it out of ClusterChannels.
    private void CollectLeaves()
    {
        foreach (ClusterChannelWrapper cluster in ClusterChannels)
            _clusters.Add(cluster);
        _clusters.RemoveWhere(cluster => !cluster.IsPlaying());
        _leaves.Clear();
        foreach (IChannel channel in _voices.Values)
            AddLeaves(channel);
        foreach (IChannel channel in _ending)
            AddLeaves(channel);
        foreach (ClusterChannelWrapper cluster in _clusters)
            AddLeaves(cluster);
    }

    private void AddLeaves(IChannel? channel)
    {
        switch (channel)
        {
            case ChannelWrapper leaf:
                _leaves.Add(leaf);
                break;
            case CompoundChannelWrapper compound:
                AddLeaves(compound.ChannelStart);
                AddLeaves(compound.Channel);
                AddLeaves(compound.ChannelStop);
                break;
            case MultiChannelWrapper multi:
                foreach (IChannel inner in multi.Channels)
                    AddLeaves(inner);
                break;
        }
    }

    /// <summary>Stops every sound at once and takes the sound out of the game audio. Never call it from <see cref="UpdateAudio"/>.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (ReferenceEquals(Current, this))
            Current = null;
        try
        {
            StopAudio(immediate: true);
        }
        finally
        {
            _leaves.Clear();
            GameAudio.Deregister(this);
        }
    }
}
