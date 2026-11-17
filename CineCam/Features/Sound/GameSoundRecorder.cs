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

    private static readonly SoundWriterSlot Writer = new();
    private static Dsp _dsp;
    private static ChannelGroup _master;
    private static bool _attached;

    public static bool IsRecording => _attached;

    public static bool IsFinalizing => Writer.IsFinalizing;

    /// <summary>The take time of the first recorded sample, once the mixer delivered it.</summary>
    public static double? FirstSampleSeconds(double clockStartSeconds)
    {
        long first = Writer.Active?.FirstTimestamp ?? 0;
        return first == 0 ? null : first / (double)Stopwatch.Frequency - clockStartSeconds;
    }

    /// <summary>Starts a recording into the file. Returns why it did not start, or null.</summary>
    public static string? Start(string path, out int sampleRate)
    {
        sampleRate = 0;
        Stop();
        if (Writer.IsFinalizing)
            return UiText.MessageText("capture.pending_sound", "The previous sound recording is still being saved. Try again in a moment.");
        FmodSystem system = GameAudio.System;
        if (system.IsNull())
            return UiText.MessageText("text.dfd1722783d3", "the game sound system is not ready");
        Result result = system.TryGetSoftwareFormat(out sampleRate, out _, out _);
        if (result != Result.Ok)
            return UiText.Format("text.aecfd2e2a832", $"the sound format could not be read ({result})");
        result = system.TryGetMasterChannelGroup(out ChannelGroup master);
        if (result != Result.Ok)
            return UiText.Format("text.aa9f41ae0d64", $"the master channel group could not be read ({result})");

        SoundCapture capture;
        try
        {
            capture = new SoundCapture(path, sampleRate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return UiText.Format("text.166ff01d50e2", $"the file could not be made: {ex.Message}");
        }

        Writer.Attach(capture);
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
            StopFailedStart(path);
            return UiText.Format("text.90ecd68036f5", $"the sound capture could not be made ({result})");
        }

        result = master.TryAddDsp((int)ChannelControlDspIndex.ChannelControlDspHead, dsp);
        if (result != Result.Ok)
        {
            dsp.TryRelease();
            StopFailedStart(path);
            return UiText.Format("text.8a6a586ee171", $"the sound capture could not be added to the mix ({result})");
        }

        dsp.TrySetActive(true);
        _dsp = dsp;
        _master = master;
        _attached = true;
        return null;
    }

    /// <summary>Takes the DSP out of the mix and asks the writer to finish the file. Null when nothing recorded.</summary>
    public static SoundCaptureResult? Stop()
    {
        if (!_attached)
            return Writer.Stop();
        _attached = false;
        SoundCaptureResult? result;
        try
        {
            // ChannelControl.RemoveDSP changes the DSP network under the mixer lock of FMOD, so no
            // read of this DSP runs after a successful removal.
            Result removed = _master.TryRemoveDsp(_dsp);
            Result released = removed == Result.Ok ? _dsp.TryRelease() : removed;
            if (removed != Result.Ok || released != Result.Ok)
                DefaultCategory.Log.Warning($"[CineCam] The sound capture could not be taken out of the mix cleanly (remove {removed}, release {released}).");
        }
        finally
        {
            _dsp = default;
            _master = default;
            result = Writer.Stop();
        }

        return result;
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

            Writer.Active?.Push(new ReadOnlySpan<float>(inBuffer, frames * inChannels), inChannels, frames,
                Stopwatch.GetTimestamp());
        }
        catch
        {
            // The mix goes on without this block in the file.
        }

        return Result.Ok;
    }

    private static void StopFailedStart(string path)
    {
        Writer.Stop();
        if (Writer.IsFinalizing)
            return;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
