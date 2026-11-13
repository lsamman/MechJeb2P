using System;
using KSP.Localization;
using MechJebLib.Control;
using UnityEngine;

namespace MuMech
{
    namespace Landing
    {
        /// <summary>
        /// MechJeb2P: the precision terminal descent. It takes over from the braking burn / final descent at TerminalHeight
        /// above the catch point and flies the vessel's lowest point onto that point: a closed-loop position and velocity
        /// controller in the horizontal plane (tilting the thrust within MaxTilt, helped by RCS translation), a descent speed
        /// profile in the vertical that slows to CaptureSpeed at the catch altitude, then a hover there (position hold) until
        /// the vessel is caught (KSP reports it landed, resting in the tower's arms) or the hover time runs out, after which
        /// it keeps sinking at CaptureSpeed. With HoldHeading the vessel's top is kept on CatchHeading so the catch pins face
        /// the arms.
        ///
        /// The throttle goes through a delta-sigma modulator: engines whose minimum thrust is above the hover thrust pulse
        /// instead of climbing away.
        /// </summary>
        public class PrecisionDescent : AutopilotStep
        {
            private const double HORIZONTAL_TIME_CONSTANT = 1.5; // s, horizontal velocity tracking
            private const double VERTICAL_TIME_CONSTANT = 0.7;   // s, vertical velocity tracking
            private const double POSITION_TIME_CONSTANT = 2.0;   // s, proportional band near the target
            private const double HOVER_BAND = 0.5;               // m, "at the catch altitude" within this
            private const double MAX_BRAKING_TILT_DEG = 30;      // tilt allowed to kill an incoming horizontal speed

            private readonly DeltaSigmaThrottleModulator _pwm = new DeltaSigmaThrottleModulator(0.02, 0.3);
            private double _hoverStartUT = -1;
            private bool _initialized;
            private Mode _mode = Mode.Approach;

            private enum Mode { Approach, Hover, Capture }

            public PrecisionDescent(MechJebCore core) : base(core) { }

            public double DistanceToCatch { get; private set; } = double.NaN;
            public double HeightAboveCatch { get; private set; } = double.NaN;

            public override AutopilotStep OnFixedUpdate()
            {
                // the last metres are flown at normal speed, with the physics on
                if (!MuUtils.PhysicsRunning() || TimeWarp.CurrentRateIndex > 0) Core.Warp.MinimumWarp(true);
                return this;
            }

            public override AutopilotStep Drive(FlightCtrlState s)
            {
                MechJebModuleLandingAutopilot landing = Core.Landing;

                // caught by the tower (KSP counts a vessel resting on another's colliders as landed) or touched down
                if (Vessel.LandedOrSplashed)
                {
                    Core.Thrust.ThrustOff();
                    landing.StopLanding();
                    return null;
                }

                if (!_initialized)
                {
                    _initialized = true;
                    Core.Thrust.Tmode = MechJebModuleThrustController.TMode.OFF;
                    Core.Thrust.TransKillH = false;
                    _pwm.Reset();
                }

                Vector3d up = VesselState.Up;
                Vector3d bottom = VesselState.CoM - landing.BottomOffset * up;
                Vector3d error = landing.CatchPointWorld() - bottom;
                Vector3d errorH = Vector3d.Exclude(up, error);
                double h = -Vector3d.Dot(error, up); // our lowest point's height above the catch point
                double dist = errorH.magnitude;
                DistanceToCatch = dist;
                HeightAboveCatch = h;

                Vector3d v = VesselState.SurfaceVelocity;
                Vector3d vH = Vector3d.Exclude(up, v);
                double vZ = Vector3d.Dot(v, up);
                double g = VesselState.LocalGravity;

                double aMax = VesselState.ThrustVectorMaxThrottle.magnitude / VesselState.Mass;
                if (VesselState.LimitedMaxThrustAcceleration > 0 && VesselState.LimitedMaxThrustAcceleration < aMax)
                    aMax = VesselState.LimitedMaxThrustAcceleration;
                double aMin = VesselState.ThrustVectorMinThrottle.magnitude / VesselState.Mass;

                // ---- horizontal: a velocity profile toward the catch point, proportional near it ----
                double tilt = UtilMath.Deg2Rad * Math.Max(1, landing.MaxTilt);
                double aHMax = Math.Tan(tilt) * g;
                double vMaxH = Math.Max(0.5, landing.MaxHorizontalSpeed);
                double vDesMag = Math.Min(vMaxH, Math.Min(Math.Sqrt(aHMax * dist), dist / POSITION_TIME_CONSTANT));
                Vector3d vHDes = dist > 1e-3 ? errorH * (vDesMag / dist) : Vector3d.zero;
                Vector3d aH = (vHDes - vH) / HORIZONTAL_TIME_CONSTANT;

                // an incoming horizontal speed above the manoeuvring speed is braked with more tilt
                double tiltLimit = vH.magnitude > vMaxH ? Math.Min(2 * tilt, UtilMath.Deg2Rad * MAX_BRAKING_TILT_DEG) : tilt;

                // ---- vertical: slow down to the capture speed at the catch altitude ----
                double aDec = Math.Max(0.5, 0.6 * (aMax - g));
                double vDown = Math.Min(Math.Max(1, landing.MaxDescentSpeed), Math.Sqrt(2 * aDec * Math.Max(h, 0)));

                // arrive at the catch altitude no sooner than over the catch point
                double tolerance = Math.Max(0.5, landing.HorizontalTolerance);
                if (dist > tolerance)
                {
                    double tH = dist / Math.Max(vDesMag, 0.5);
                    vDown = Math.Min(vDown, Math.Max(h, 0) / Math.Max(tH, 1));
                }

                double capture = Math.Max(0.1, landing.CaptureSpeed);
                vDown = Math.Max(vDown, capture);

                double vZDes;
                if (h > HOVER_BAND)
                {
                    _mode = Mode.Approach;
                    _hoverStartUT = -1;
                    vZDes = -vDown;
                }
                else
                {
                    if (_hoverStartUT < 0) _hoverStartUT = VesselState.Time;
                    double hovered = VesselState.Time - _hoverStartUT;
                    bool hover = landing.HoverAtCatch && (landing.HoverTimeLimit <= 0 || hovered < landing.HoverTimeLimit);
                    // off the point at the catch altitude: hold the altitude until centred, whatever the hover setting
                    if (dist > tolerance) hover = true;

                    if (hover)
                    {
                        _mode = Mode.Hover;
                        vZDes = MechJebLib.Utils.Statics.Clamp(-h / POSITION_TIME_CONSTANT, -capture, capture);
                    }
                    else
                    {
                        _mode = Mode.Capture;
                        vZDes = -capture;
                    }
                }

                double aZ = g + (vZDes - vZ) / VERTICAL_TIME_CONSTANT;

                // the horizontal acceleration comes from tilting the thrust: keep the tilt within its limit
                if (aZ > 0.1)
                {
                    double maxH = aZ * Math.Tan(tiltLimit);
                    if (aH.magnitude > maxH) aH *= maxH / aH.magnitude;
                }
                else
                {
                    aH = Vector3d.zero;
                }

                Vector3d aCmd = aZ * up + aH;
                double accel = aCmd.magnitude;
                Vector3d dir = aZ > 0.1 ? aCmd.normalized : up;

                // ---- attitude: thrust along dir, the vessel's top on the catch heading if asked ----
                if (landing.HoldHeading)
                {
                    double heading = UtilMath.Deg2Rad * landing.CatchHeading;
                    Vector3 fwd = dir;
                    Vector3 top = Math.Sin(heading) * VesselState.East + Math.Cos(heading) * VesselState.North;
                    Vector3.OrthoNormalize(ref fwd, ref top);
                    Core.Attitude.AttitudeTo(QuaternionD.LookRotation(fwd, top), AttitudeReference.INERTIAL_COT, landing);
                }
                else
                {
                    Core.Attitude.AttitudeTo(dir, AttitudeReference.INERTIAL_COT, landing);
                }

                // ---- throttle ----
                // thrust well off its direction would mostly push sideways: hold the vertical and wait for the turn
                double aimError = Core.Attitude.AttitudeAngleFromTarget();
                if (aimError > 25)
                    accel = Math.Max(aCmd.magnitude, g) / Math.Max(Math.Cos(UtilMath.Deg2Rad * Math.Min(aimError, 60)), 0.5);

                _pwm.MinOffTime = VesselState.DeltaT;
                float throttle = _pwm.ThrottleCommand(accel, aMin, aMax, VesselState.DeltaT);
                if (throttle > 0)
                    Core.Thrust.RequestActiveThrottle(throttle, false);
                else
                    Core.Thrust.RequestActiveThrottle(0, false);

                // ---- RCS: the fine horizontal translation the tilt is too coarse for ----
                if (landing.RCSAdjustment)
                {
                    Core.RCS.Enabled = true;
                    Core.RCS.SetWorldVelocityError(vHDes - vH);
                }

                string modeText;
                switch (_mode)
                {
                    case Mode.Hover:
                        modeText = Localizer.Format("#MechJeb_LG2P_StatusHover"); //holding at the catch altitude
                        break;
                    case Mode.Capture:
                        modeText = Localizer.Format("#MechJeb_LG2P_StatusCapture"); //sinking through the catch window
                        break;
                    default:
                        modeText = Localizer.Format("#MechJeb_LG2P_StatusApproach"); //approaching
                        break;
                }

                Status = Localizer.Format("#MechJeb_LG2P_Status", dist.ToString("F1"), h.ToString("F1"), modeText); //Precision descent: <<1>> m off, <<2>> m above the catch point, <<3>>

                return this;
            }
        }
    }
}
