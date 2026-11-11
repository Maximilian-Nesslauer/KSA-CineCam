using Brutal.Numerics;
using CineCam.Director;
using KSA;

namespace CineCam.Features.Takes;

// The take puts the camera into the fixed mode, so no other controller moves it, and writes the
// pose in a prefix of FixedController.OnFrame. That runs after the vehicles have their new state
// and before Camera.OnFrame builds the matrices, so the picture does not lag a fast vehicle.
internal sealed class CameraRig
{
    private readonly CameraMode _savedMode;
    private readonly double3 _savedOffset;
    private readonly double3 _savedRotation;
    private readonly Part.Connector? _savedConnector;
    private readonly double3 _savedPositionCce;
    private readonly doubleQuat _savedLocalRotation;
    private readonly float _savedFovDegrees;
    private readonly UpStabilizer _up = new();
    private V3 _lastForwardSite = V3.UnitX;

    public CameraRig()
    {
        IGameViewport viewport = Program.MainViewport;
        _savedMode = viewport.Mode;
        _savedOffset = viewport.FixedController.CameraOffset;
        _savedRotation = viewport.FixedController.CameraRotation;
        _savedConnector = viewport.FixedController.DockingConnector;
        _savedPositionCce = viewport.BaseCamera.PositionCce;
        _savedLocalRotation = viewport.BaseCamera.LocalRotation;
        _savedFovDegrees = (float)(viewport.BaseCamera.GetFieldOfView() * (180.0 / Math.PI));
    }

    public static Camera Camera => Program.MainViewport.BaseCamera;

    public void Enter()
    {
        FixedController fixedController = Program.MainViewport.FixedController;
        fixedController.DockingConnector = null;
        if (_savedMode != CameraMode.Fixed)
            Program.MainViewport.SetCameraMode(CameraMode.Fixed);
    }

    // The switch to the free mode can change the rotation, so the pose is written back after it.
    public void BeginFree()
    {
        IGameViewport viewport = Program.MainViewport;
        Camera camera = viewport.BaseCamera;
        double3 position = camera.PositionCce;
        doubleQuat rotation = camera.LocalRotation;
        viewport.SetCameraMode(CameraMode.Free);
        camera.PositionCce = position;
        camera.LocalRotation = rotation;
    }

    public void EndFree()
    {
        IGameViewport viewport = Program.MainViewport;
        viewport.FixedController.DockingConnector = null;
        if (viewport.Mode != CameraMode.Fixed)
            viewport.SetCameraMode(CameraMode.Fixed);
    }

    // Orbit, IVA and Map rebuild their pose when they switch on, but the free camera does not. Its
    // pose is written before the switch, because the free controller reads the rotation when it
    // switches on, and again after it. When the player left the fixed mode during the take, only
    // the field of view returns.
    public void Restore()
    {
        IGameViewport viewport = Program.MainViewport;
        FixedController fixedController = viewport.FixedController;
        fixedController.CameraOffset = _savedOffset;
        fixedController.CameraRotation = _savedRotation;
        fixedController.DockingConnector = _savedConnector;
        Camera camera = viewport.BaseCamera;
        if (viewport.Mode == CameraMode.Fixed && _savedMode != CameraMode.Fixed)
        {
            if (_savedMode == CameraMode.Free)
                WriteSavedPose(camera);
            viewport.SetCameraMode(_savedMode);
            if (_savedMode == CameraMode.Free)
                WriteSavedPose(camera);
        }

        camera.SetFieldOfView(_savedFovDegrees);
    }

    private void WriteSavedPose(Camera camera)
    {
        camera.PositionCce = _savedPositionCce;
        camera.LocalRotation = _savedLocalRotation;
    }

    // In the site frame.
    public static CameraPose CurrentPose(SiteFrame site)
    {
        Camera camera = Camera;
        V3 position = site.FromEcl(camera.PositionEcl);
        V3 forward = site.DirectionFromEcl(camera.GetForwardEcl());
        V3 up = site.DirectionFromEcl(camera.GetUpEcl());
        return new CameraPose(PoseFrame.Site, position, position + forward * 100.0, up,
            camera.GetFieldOfView() * (180.0 / Math.PI));
    }

    // The pose is in the site frame. The view direction is mapped as a direction, not as the
    // difference of two mapped positions, because those are about the size of a planet.
    public void Apply(SiteFrame site, CameraPose pose)
    {
        Camera camera = Camera;
        V3 forwardSite = pose.LookAt - pose.Position;
        if (forwardSite.Length < 1e-6)
            forwardSite = _lastForwardSite;
        forwardSite = forwardSite.NormalizedOr(_lastForwardSite);
        _lastForwardSite = forwardSite;

        V3 upSite = _up.Stabilize(forwardSite, pose.Up);
        double3 forward = site.DirectionToEcl(forwardSite).Normalized();
        double3 up = site.DirectionToEcl(upSite).Normalized();

        camera.PositionEcl = site.ToEcl(pose.Position);
        camera.LocalRotation = Camera.LookAtRotation(forward, up);

        float fov = (float)Math.Clamp(pose.Fov, 1.0, 150.0);
        if (Math.Abs(camera.GetFieldOfView() * (180.0 / Math.PI) - fov) > 1e-3)
            camera.SetFieldOfView(fov);
    }
}
