namespace CineCam.Director;

internal enum PictureOwner
{
    Still,
    Preview,
    OfflineTake,
    ReplayRender,
    SelfTest,
}

// The main thread owns the slot. KSA's PNG task can still own the source folder after the GPU grab
// ends or the operation stops, so it blocks the next request even after the client releases its slot.
internal sealed class PictureCaptureOwnership
{
    private Task? _writer;

    public PictureOwner? Owner { get; private set; }

    public bool IsWriting => _writer is { IsCompleted: false };

    public bool IsBusy(bool nativeActive) => Owner.HasValue || nativeActive || IsWriting;

    public bool Owns(PictureOwner owner) => Owner == owner;

    public bool TryAcquire(PictureOwner owner, bool nativeActive)
    {
        if (IsBusy(nativeActive))
            return false;
        Owner = owner;
        return true;
    }

    public bool CanRequest(PictureOwner owner, bool nativeActive) =>
        Owns(owner) && !nativeActive && !IsWriting;

    public void TrackWrite(Task writer) => _writer = writer;

    public void Release(PictureOwner owner)
    {
        if (Owns(owner))
            Owner = null;
    }
}
