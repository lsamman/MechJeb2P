using System;
using KSP.Localization;
using MechJebLib.Control;
using UnityEngine;

namespace MuMech
{
    namespace Landing
    {
        /// <summary>
        /// MechJeb2P: the precision terminal descent. It takes over from the braking burn / final descent once the vessel is
        /// below TerminalHeight above the catch point and the braking still to do fits the engines, and flies the vessel's
        /// lowest point onto that point: a closed-loop position and velocity controller in the horizontal plane (tilting the
        /// thrust, up to MaxTilt to manoeuvre and more to brake an incoming speed, helped by RCS translation), a descent speed
        /// profile in the vertical that slows to CaptureSpeed at the catch altitude, then a hover there (position hold) until
        /// the hover time runs out, after which it sinks at CaptureSpeed until something holds the vessel up.
        ///
        /// KSP only calls a vessel "landed" on terrain, so a vessel resting in a tower's arms is still "flying" to it: the catch
        /// is detected here, as the vessel no longer sinking while the controller asks it to. The thrust then goes off, and
        /// if the vessel starts falling again it is relit and the sink resumes.
        ///
        /// With HoldHeading the vessel's top is kept on CatchHeading so the catch pins face the arms. The throttle goes through
        /// a delta-sigma modulator for engines whose minimum thrust is above the hover thrust.
        /// </summary>
        public class PrecisionDescent : AutopilotStep
        {
            private const double HORIZONTAL_TIME_CONSTANT = 2.5; // s, horizontal velocity tracking (a tall vessel tilts slowly)
            private const double VERTICAL_TIME_CONSTANT = 0.8;   // s, vertical velocity tracking
            private const double POSITION_TIME_CONSTANT = 3.0;   // s, proportional band near the target
            private const double HOVER_BAND = 0.5;               // m, "at the catch altitude" within this
            private const double MAX_BRAKING_TILT_DEG = 35;      // tilt allowed to kill an incoming horizontal speed
            private const double PROFILE_MARGIN = 0.6;           // fraction of the spare thrust the descent profile plans with
            private const double RESTING_TIME = 1.5;             // s not sinking while asked to, before the catch is called
            private const double CAUGHT_CONFIRM_TIME = 2.0;      // s resting with the thrust off before the landing ends
            private const double FALLING_SPEED = 1.0;            // m/s down with the thrust off: not held after all, relight

            private readonly DeltaSigmaThrottleModulator _pwm = new DeltaSigmaThrottleModulator(0.02, 0.3);
            private double _hoverStartUT = -1;
            private double _restingSince = -1;
            private double _caughtSince = -1;
            private int _relights;
            private bool _initialized;
            private bool _terminalEnginesReady;
            private Mode _mode = Mode.Approach;

            private enum Mode { Approach, Hover, Capture, Caught }

            public PrecisionDescent(MechJebCore core) : base(core) { }

            // ---- for the window, the remote window and the flight log ----
            public double DistanceToCatch { get; private set; } = double.NaN;
            public double HeightAboveCatch { get; private set; } = double.NaN;
            public string ModeName => _mode.ToString();
            public double AccelCommand { get; private set; } = double.NaN;
            public double VerticalAccelCommand { get; private set; } = double.NaN;
            public double HorizontalAccelCommand { get; private set; } = double.NaN;
            public double DesiredVerticalSpeed { get; private set; } = double.NaN;
            public double DesiredHorizontalSpeed { get; private set; } = double.NaN;
            public double TiltCommandDeg { get; private set; } = double.NaN;
            public double ThrottleCommand { get; private set; } = double.NaN;

            /// <summary>
            /// Whether the braking still to do fits the thrust left after the terminal engine switch (TerminalThrustFraction of
            /// the present thrust), so the switch can be made. Once true it stays true.
            /// </summary>
            public bool TerminalEnginesReady => _terminalEnginesReady;

            public override AutopilotStep OnFixedUpdate()
            {
                // the last metres are flown at normal speed, with the physics on
                if (!MuUtils.PhysicsRunning() || TimeWarp.CurrentRateIndex > 0) Core.Warp.MinimumWarp(true);
                return this;
            }

            public override AutopilotStep Drive(FlightCtrlState s)
            {
                MechJebModuleLandingAutopilot landing = Core.Landing;

                // touched down on the ground (KSP only counts terrain contact)
                if (Vessel.LandedOrSplashed)
                {
                    Core.Thrust.ThrustOff();
                    landing.StopLanding("landed (KSP reports the vessel landed or splashed)");
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

                // ---- caught: resting in the arms with the thrust off ----
                if (_mode == Mode.Caught)
                {
                    Core.Thrust.RequestActiveThrottle(0, false);
                    HoldAttitude(up, landing);
                    ThrottleCommand = 0;
                    Status = Localizer.Format("#MechJeb_LG2P_StatusCaught", (VesselState.Time - _caughtSince).ToString("F0")); //Caught: resting with the engines off (<<1>> s)

                    if (vZ < -FALLING_SPEED)
                    {
                        // not held after all: relight and sink on
                        _relights++;
                        landing.Log?.Event("not held: falling at " + (-vZ).ToString("F2") + " m/s with the thrust off, relighting (" + _relights + ")");
                        _mode = Mode.Capture;
                        _restingSince = -1;
                        _caughtSince = -1;
                        _pwm.Reset();
                    }
                    else if (VesselState.Time - _caughtSince > CAUGHT_CONFIRM_TIME)
                    {
                        landing.StopLanding("caught: resting " + CAUGHT_CONFIRM_TIME.ToString("F0") + " s with the engines off, " + dist.ToString("F2") + " m off the catch point, " +
                                            h.ToString("F2") + " m from the catch altitude");
                        return null;
                    }

                    return this;
                }

                // ---- horizontal: a velocity profile toward the catch point, proportional near it ----
                double tilt = UtilMath.Deg2Rad * Math.Max(1, landing.MaxTilt);
                double aHMax = Math.Tan(tilt) * g;
                double vMaxH = Math.Max(0.5, landing.MaxHorizontalSpeed);
                double vDesMag = Math.Min(vMaxH, Math.Min(Math.Sqrt(aHMax * dist), dist / POSITION_TIME_CONSTANT));
                Vector3d vHDes = dist > 1e-3 ? errorH * (vDesMag / dist) : Vector3d.zero;
                Vector3d aH = (vHDes - vH) / HORIZONTAL_TIME_CONSTANT;
                DesiredHorizontalSpeed = vDesMag;

                // an incoming horizontal speed above the manoeuvring speed is braked with more tilt, the more the faster
                double tiltLimit = tilt;
                if (vH.magnitude > vMaxH)
                {
                    double brakingTilt = UtilMath.Deg2Rad * MAX_BRAKING_TILT_DEG;
                    double f = MechJebLib.Utils.Statics.Clamp((vH.magnitude - vMaxH) / (2 * vMaxH), 0, 1);
                    tiltLimit = tilt + f * (brakingTilt - tilt);
                }

                // ---- vertical: slow down to the capture speed at the catch altitude ----
                double aDec = Math.Max(0.5, PROFILE_MARGIN * (aMax - g));
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
                if (h > HOVER_BAND && _mode != Mode.Capture)
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
                    if (dist > tolerance && h > -2) hover = true;
                    // once sinking through the window, keep sinking: the window is behind us
                    if (_mode == Mode.Capture && dist <= tolerance) hover = false;

                    if (hover)
                    {
                        if (_mode != Mode.Hover) landing.Log?.Event("hover at the catch altitude: " + dist.ToString("F2") + " m off, " + h.ToString("F2") + " m above");
                        _mode = Mode.Hover;
                        vZDes = MechJebLib.Utils.Statics.Clamp(-h / POSITION_TIME_CONSTANT, -capture, capture);
                    }
                    else
                    {
                        if (_mode != Mode.Capture) landing.Log?.Event("capture: sinking at " + capture.ToString("F2") + " m/s, " + dist.ToString("F2") + " m off, " + h.ToString("F2") + " m above");
                        _mode = Mode.Capture;
                        vZDes = -capture;
                    }
                }

                DesiredVerticalSpeed = vZDes;
                double aZ = g + (vZDes - vZ) / VERTICAL_TIME_CONSTANT;

                // ---- the catch: asked to sink, and not sinking ----
                bool resting = _mode == Mode.Capture && aZ < g - 0.3 && Math.Abs(vZ) < 0.15 && h < 5;
                if (resting)
                {
                    if (_restingSince < 0) _restingSince = VesselState.Time;
                    if (VesselState.Time - _restingSince > RESTING_TIME)
                    {
                        landing.Log?.Event("caught: not sinking for " + RESTING_TIME.ToString("F1") + " s while asked to (" + dist.ToString("F2") + " m off, " + h.ToString("F2") +
                                           " m from the catch altitude), thrust off");
                        _mode = Mode.Caught;
                        _caughtSince = VesselState.Time;
                        Core.Thrust.RequestActiveThrottle(0, false);
                        HoldAttitude(up, landing);
                        return this;
                    }
                }
                else
                {
                    _restingSince = -1;
                }

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

                VerticalAccelCommand = aZ;
                HorizontalAccelCommand = aH.magnitude;
                TiltCommandDeg = Vector3d.Angle(dir, up);

                // ---- the terminal engine switch: once the braking left fits the thrust that will remain ----
                if (!_terminalEnginesReady)
                {
                    double fraction = MechJebLib.Utils.Statics.Clamp(landing.TerminalThrustFraction, 0.05, 1);
                    double aReq = g + (vZ < 0 ? vZ * vZ / (2 * Math.Max(h, 1)) : 0); // to stop at the catch altitude from here
                    if (fraction > 0.999 || h < 30 || (aReq < 0.65 * fraction * aMax && vH.magnitude < 2 * vMaxH))
                    {
                        _terminalEnginesReady = true;
                        landing.Log?.Event("terminal engines ready: need " + aReq.ToString("F1") + " m/s2 of " + (fraction * aMax).ToString("F1") + " after the switch (h " +
                                           h.ToString("F0") + " m, vZ " + vZ.ToString("F1") + " m/s)");
                    }
                }

                // ---- attitude: thrust along dir, the vessel's top on the catch heading if asked ----
                AimThrust(dir, landing);

                // ---- throttle ----
                // thrust well off its direction would mostly push sideways: hold the vertical and wait for the turn
                double aimError = Core.Attitude.AttitudeAngleFromTarget();
                if (aimError > 25)
                    accel = Math.Max(aCmd.magnitude, g) / Math.Max(Math.Cos(UtilMath.Deg2Rad * Math.Min(aimError, 60)), 0.5);
                AccelCommand = accel;

                _pwm.MinOffTime = VesselState.DeltaT;
                float throttle = _pwm.ThrottleCommand(accel, aMin, aMax, VesselState.DeltaT);
                ThrottleCommand = throttle;
                Core.Thrust.RequestActiveThrottle(throttle > 0 ? throttle : 0, false);

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

            private void AimThrust(Vector3d dir, MechJebModuleLandingAutopilot landing)
            {
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
            }

            private void HoldAttitude(Vector3d up, MechJebModuleLandingAutopilot landing) => AimThrust(up, landing);
        }
    }
}
