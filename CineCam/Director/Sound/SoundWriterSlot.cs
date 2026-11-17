namespace CineCam.Director.Sound;

// The mixer reads only the active capture. The main thread also holds a stopped capture until its
// writer closes the file, so it can check for pending output without joining the thread.
internal sealed class SoundWriterSlot
{
    private SoundCapture? _active;
    private SoundCapture? _finalizing;

    public SoundCapture? Active => Volatile.Read(ref _active);

    public bool IsFinalizing
    {
        get
        {
            if (_finalizing?.IsWriting == true)
                return true;
            _finalizing = null;
            return false;
        }
    }

    public void Attach(SoundCapture capture)
    {
        if (Active != null || IsFinalizing)
            throw new InvalidOperationException("A sound capture still owns the writer slot.");
        Volatile.Write(ref _active, capture);
    }

    public SoundCaptureResult? Stop(int timeoutMilliseconds = 5000)
    {
        SoundCapture? capture = Interlocked.Exchange(ref _active, null);
        if (capture == null)
            return null;
        _finalizing = capture;
        SoundCaptureResult result = capture.Stop(timeoutMilliseconds);
        if (!capture.IsWriting)
            _finalizing = null;
        return result;
    }
}
