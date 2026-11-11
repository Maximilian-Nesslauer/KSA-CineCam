using Brutal.Numerics;
using CineCam.Core;
using KSA;

namespace CineCam.Features.Replay;

internal sealed class ReplayLiveState : IDisposable
{
    private static IGameViewport Viewport => Program.MainViewport;
    private static Camera Camera => Viewport.BaseCamera;
    private readonly CameraMode _mode = Viewport.Mode;
    private readonly IFollowable? _target = Camera.Following;
    private readonly bool _tidalLock = Camera.TidalLocking;
    private readonly double3 _position = Camera.PositionCce;
    private readonly doubleQuat _rotation = Camera.LocalRotation;
    private readonly float _fov = (float)(Camera.GetFieldOfView() * (180.0 / Math.PI));
    private readonly bool _ui = Program.DrawUI;
    private readonly bool _control = Program.IsControlledVehicleActive;
    private readonly Vehicle? _controlled = Program.ControlledVehicle;
    private readonly double _speed = Universe.GetSimulationSpeed();
    private bool _held;

    public void Enter()
    {
        _held = true;
        Program.ControlledVehicle?.ClearHeldPlayerInput();
        Universe.AutoWarpStop(resetSimulationSpeed: false);
        Universe.SetSimulationSpeed(0, alert: false);
        Camera.Unfollow(changeControl: false);
        Hold(false);
    }

    // The take camera uses the fixed mode, because FixedController.OnFrame moves the camera only
    // while it follows something and FixedController.OnKey takes no key, so the written pose stays. FlyController.OnFrame would add the player input to it. The pose is saved across the
    // switch, because FlyController.OnSwitchOn can change the rotation.
    public void Hold(bool takeCamera)
    {
        CameraMode mode = takeCamera ? CameraMode.Fixed : CameraMode.Free;
        if (takeCamera && Camera.Following != null)
            Camera.Unfollow(changeControl: false);
        if (Viewport.Mode != mode)
        {
            double3 position = Camera.PositionCce;
            doubleQuat rotation = Camera.LocalRotation;
            Viewport.SetCameraMode(mode);
            Camera.PositionCce = position;
            Camera.LocalRotation = rotation;
            if (mode == CameraMode.Free)
                Viewport.FlyController.CacheOffset();
        }

        Program.IsControlledVehicleActive = false;
    }

    public void Dispose()
    {
        if (!_held)
            return;
        _held = false;
        Program.DrawUI = _ui;
        RestoreSpeed();
        try
        {
            // StarMap can unload after KSA has disposed the viewport registry.
            if (ViewportRegistry.GameViews.IsEmpty)
                return;
            if (_target != null && _target is not Vehicle { IsDisposed: true })
                Camera.SetFollow(_target, _tidalLock, changeControl: false, alert: false);
            else
                Camera.Unfollow(changeControl: false);
            Camera.PositionCce = _position;
            Camera.LocalRotation = _rotation;
            Viewport.SetCameraMode(_mode);
            Camera.PositionCce = _position;
            Camera.LocalRotation = _rotation;
            Camera.SetFieldOfView(_fov);
            if (_mode == CameraMode.Free)
                Viewport.FlyController.CacheOffset();
        }
        finally
        {
            // A replay that closes because the vehicle editor opened leaves the control to the editor.
            if (Program.Editor == null && !Program.EditorFlag)
                RestoreControl();
        }
    }

    private void RestoreControl()
    {
        // The same setter that Camera.SetFollow uses when it changes the control.
        if (_controlled is { IsDisposed: false } && !ReferenceEquals(Program.ControlledVehicle, _controlled))
            Program.ControlledVehicle = _controlled;
        Program.IsControlledVehicleActive = _control;
    }

    // A game menu that opened during the replay saved speed 0 in Program.UpdateMenuPause. The live
    // speed replaces that saved value, so the flight goes on at its speed when the menu closes.
    private void RestoreSpeed()
    {
        if (MenuPause.Active && MenuPause.Hand(_speed))
            return;
        Universe.SetSimulationSpeed(_speed, alert: false);
    }
}
