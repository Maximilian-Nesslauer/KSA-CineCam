using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Brutal.FmodApi;
using Brutal.Logging;
using CineCam.Director.Sound;
using KSA;

namespace CineCam.Features.Sound;

/// <summary>
/// Records the game sound into a WAV file through a custom FMOD DSP at the head of the master channel
/// group of GameAudio.System, so the file holds the whole mix as the player hears it, after the volume
/// settings. The DSP passes the sound through unchanged and hands each block to a
/// <see cref="SoundCapture"/>. Only one recording runs at a time.
/// </summary>
internal static unsafe class GameSoundRecorder
{
    public const string FileName = "game-sound.wav";

    // FMOD_PLUGIN_SDK_VERSION of the FMOD 2 headers that match the DspDescription layout of Brutal.Fmod.
    private const uint PluginSdkVersion = 110;

    private static SoundCapture? _capture;
    private static Dsp _dsp;
    private static ChannelGroup _master;
    private static bool _attached;

    public static bool IsRecording => _attached;

    /// <summary>The take time of the first recorded sample, once the mixer delivered it.</summary>
    public static double? FirstSampleSeconds(double clockStartSeconds)
    {
        long first = Volatile.Read(ref _capture)?.FirstTimestamp ?? 0;
        return first == 0 ? null : first / (double)Stopwatch.Frequency - clockStartSeconds;
    }

    /// <summary>Starts a recording into the file. Returns why it did not start, or null.</summary>
    public static string? Start(string path, out int sampleRate)
    {
        sampleRate = 0;
        Stop();
        FmodSystem system = GameAudio.System;
        if (system.IsNull())
            return "the game sound system is not ready";
        Result result = system.TryGetSoftwareFormat(out sampleRate, out _, out _);
        if (result != Result.Ok)
            return $"the sound format could not be read ({result})";
        result = system.TryGetMasterChannelGroup(out ChannelGroup master);
        if (result != Result.Ok)
            return $"the master channel group could not be read ({result})";

        SoundCapture capture;
        try
        {
            capture = new SoundCapture(path, sampleRate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"the file could not be made: {ex.Message}";
        }

        DspDescription description = default;
        description.PluginSdkVersion = PluginSdkVersion;
        ReadOnlySpan<byte> name = "CineCam sound capture"u8;
        for (int i = 0; i < name.Length; i++)
            description.Name[i] = name[i];
        description.Version = 0x00010000;
        description.NumInputBuffers = 1;
        description.NumOutputBuffers = 1;
        // DspReadCallbackPtr takes only a delegate, so the pointer of the static callback goes into its one field.
        Unsafe.As<DspReadCallbackPtr, nint>(ref description.Read) =
            (nint)(delegate* unmanaged[Cdecl]<DspState*, float*, float*, uint, int, int*, Result>)&Read;

        result = system.TryCreateDsp(in description, out Dsp dsp);
        if (result != Result.Ok)
        {
            capture.Stop();
            TryDelete(path);
            return $"the sound capture could not be made ({result})";
        }

        Volatile.Write(ref _capture, capture);
        result = master.TryAddDsp((int)ChannelControlDspIndex.ChannelControlDspHead, dsp);
        if (result != Result.Ok)
        {
            Volatile.Write(ref _capture, null);
            dsp.TryRelease();
            capture.Stop();
            TryDelete(path);
            return $"the sound capture could not be added to the mix ({result})";
        }

        dsp.TrySetActive(true);
        _dsp = dsp;
        _master = master;
        _attached = true;
        return null;
    }

    public static void SetPaused(bool paused)
    {
        if (Volatile.Read(ref _capture) is { } capture)
            capture.Paused = paused;
    }

    /// <summary>Takes the DSP out of the mix, ends the writer thread and closes the file. Null when nothing recorded.</summary>
    public static SoundCaptureResult? Stop()
    {
        if (!_attached)
            return null;
        _attached = false;
        // ChannelControl.RemoveDSP changes the DSP network under the mixer lock of FMOD, so no read
        // of this DSP runs after it returns. The capture also refuses blocks once it stops.
        Result removed = _master.TryRemoveDsp(_dsp);
        Result released = removed == Result.Ok ? _dsp.TryRelease() : removed;
        if (removed != Result.Ok || released != Result.Ok)
            DefaultCategory.Log.Warning($"[CineCam] The sound capture could not be taken out of the mix cleanly (remove {removed}, release {released}).");
        SoundCapture? capture = Interlocked.Exchange(ref _capture, null);
        _dsp = default;
        _master = default;
        return capture?.Stop();
    }

    // FMOD calls this on its mixer thread for every block. It copies the input to the output and
    // gives the block to the capture, with no allocation and no lock. An exception here would end
    // the process, so nothing escapes.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static Result Read(DspState* state, float* inBuffer, float* outBuffer, uint length, int inChannels, int* outChannels)
    {
        try
        {
            int frames = (int)length;
            int outCount = *outChannels;
            if (outCount == inChannels)
            {
                long bytes = (long)frames * inChannels * sizeof(float);
                Buffer.MemoryCopy(inBuffer, outBuffer, bytes, bytes);
            }
            else
            {
                for (int i = 0; i < frames; i++)
                {
                    for (int c = 0; c < outCount; c++)
                        outBuffer[i * outCount + c] = c < inChannels ? inBuffer[i * inChannels + c] : 0f;
                }
            }

            Volatile.Read(ref _capture)?.Push(new ReadOnlySpan<float>(inBuffer, frames * inChannels), inChannels, frames,
                Stopwatch.GetTimestamp());
        }
        catch
        {
            // The mix goes on without this block in the file.
        }

        return Result.Ok;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
