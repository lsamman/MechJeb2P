using System;
using KSP.Localization;
using UnityEngine;

namespace MuMech
{
    namespace Landing
    {
        /// <summary>
        /// MechJeb2P: the boostback. Replaces the standard course correction when the precision mode is on. A booster coming
        /// off its ship has to turn around and burn back several hundred m/s; the standard step waits for the turn with the
        /// engines off (a vessel with no RCS then has only its reaction wheels to turn with), only burns once aligned within
        /// 2°, and gives up as soon as drag appears. This step turns under a small throttle so the gimbals have torque, opens
        /// the throttle as the aim closes, keeps going until the predicted landing site is within BoostbackDoneMiss of the
        /// target (or the correction has shrunk to nothing), and only stands down when the air is thick enough that turning
        /// is no longer wise (BoostbackMaxDrag).
        /// </summary>
        public class Boostback : AutopilotStep
        {
            private const double BURN_ALIGN_DEG = 20;   // the throttle opens inside this aim error
            private const double LOSE_ALIGN_DEG = 45;   // and shuts back to the flip throttle outside this
            private const double TIME_CONSTANT = 3.0;   // s, throttle-down at the end of the burn
            private const double MAX_BURN_ACCEL = 60;   // m/s², so a big cluster's burn lasts long enough for the prediction to follow
            private const double NO_THRUST_PATIENCE = 3; // s without thrust (engines igniting after a mode switch) before standing down

            private double _noThrustSince = -1;

            public Boostback(MechJebCore core) : base(core) { }

            public bool Burning { get; private set; }
            public double Dv { get; private set; } = double.NaN;
            public double PredictedMiss { get; private set; } = double.NaN;

            public override AutopilotStep OnFixedUpdate()
            {
                // the whole step is at normal speed: the turn and the burn are flown in physics
                if (!MuUtils.PhysicsRunning()) Core.Warp.MinimumWarp();
                return this;
            }

            public override AutopilotStep Drive(FlightCtrlState s)
            {
                MechJebModuleLandingAutopilot landing = Core.Landing;

                // already low: straight to the braking burn
                if (VesselState.AltitudeASL < landing.DecelerationEndAltitude() + 5)
                {
                    Core.Thrust.ThrustOff();
                    return new DecelerationBurn(Core);
                }

                // too deep in the air to turn and burn: let the atmosphere and the fins do what they can
                if (VesselState.DragAcceleration > Math.Max(0.05, landing.BoostbackMaxDrag))
                {
                    landing.Log?.Event("boostback stood down: drag " + VesselState.DragAcceleration.ToString("F2") + " m/s2 above the limit, predicted miss " + PredictedMiss.ToString("F0") + " m");
                    Core.Thrust.ThrustOff();
                    if (landing.RCSAdjustment) Core.RCS.Enabled = true;
                    return new CoastToDeceleration(Core);
                }

                if (VesselState.LimitedMaxThrustAcceleration <= 0)
                {
                    if (_noThrustSince < 0) _noThrustSince = VesselState.Time;
                    if (VesselState.Time - _noThrustSince > NO_THRUST_PATIENCE)
                    {
                        landing.Log?.Event("boostback stood down: no thrust available for " + NO_THRUST_PATIENCE.ToString("F0") + " s (no engine running?)");
                        Core.Thrust.ThrustOff();
                        return new CoastToDeceleration(Core);
                    }

                    Status = Localizer.Format("#MechJeb_LG2P_StatusBoostbackNoThrust"); //Boostback: no engine running
                    Core.Thrust.RequestActiveThrottle(0, false);
                    return this;
                }

                _noThrustSince = -1;

                float flipThrottle = (float)MechJebLib.Utils.Statics.Clamp(landing.BoostbackFlipThrottle, 0, 0.5);

                if (!landing.PredictionReady)
                {
                    // no landing prediction yet: start turning toward the horizontal retrograde, which is where a boostback
                    // points give or take the target's offset, under the flip throttle
                    Vector3d hv = Vector3d.Exclude(VesselState.Up, VesselState.SurfaceVelocity);
                    Vector3d dir = hv.magnitude > 1 ? -hv.normalized : VesselState.Up;
                    Core.Attitude.AttitudeTo(dir, AttitudeReference.INERTIAL_COT, landing);
                    Core.Thrust.RequestActiveThrottle(flipThrottle, false);
                    Burning = false;
                    Status = Localizer.Format("#MechJeb_LG2P_StatusBoostbackWait"); //Boostback: turning, waiting for the landing prediction
                    return this;
                }

                PredictedMiss = Vector3d.Distance(Core.Target.GetPositionTargetPosition(), landing.LandingSite);
                Vector3d deltaV = landing.ComputeCourseCorrection(true);

                // it can stop the landing
                if (!landing.Enabled) return null;

                Dv = deltaV.magnitude;

                if (PredictedMiss < Math.Max(10, landing.BoostbackDoneMiss) || Dv < 0.5)
                {
                    landing.Log?.Event("boostback done: predicted miss " + PredictedMiss.ToString("F0") + " m, remaining correction " + Dv.ToString("F1") + " m/s");
                    Core.Thrust.ThrustOff();
                    if (landing.RCSAdjustment) Core.RCS.Enabled = true;
                    return new CoastToDeceleration(Core);
                }

                Vector3d burnDir = deltaV.normalized;
                Core.Attitude.AttitudeTo(burnDir, AttitudeReference.INERTIAL_COT, landing);

                double aim = Vector3d.Angle(VesselState.ThrustForward.sqrMagnitude > 0 ? VesselState.ThrustForward : VesselState.Forward, burnDir);

                if (!Burning && aim < BURN_ALIGN_DEG)
                {
                    Burning = true;
                    landing.Log?.Event("boostback burn: aim " + aim.ToString("F1") + "°, " + Dv.ToString("F0") + " m/s to go, predicted miss " + PredictedMiss.ToString("F0") + " m");
                }
                else if (Burning && aim > LOSE_ALIGN_DEG)
                {
                    Burning = false;
                    landing.Log?.Event("boostback burn paused: aim " + aim.ToString("F1") + "°");
                }

                if (Burning)
                {
                    // the acceleration a burn of Dv wants: tapering over the last seconds, capped so the whole burn lasts long
                    // enough for the landing prediction (a few updates a second, lagging the burn) to be followed, and fading
                    // in over the last degrees of the turn so the thrust goes where it's wanted
                    double aMax = VesselState.MaxThrustAccelerationAlong(burnDir);
                    double aWanted = Math.Min(Dv / TIME_CONSTANT, MAX_BURN_ACCEL);
                    float wanted = aMax > 0 ? (float)MechJebLib.Utils.Statics.Clamp(aWanted / aMax, 0, 1) : 1f;
                    float fade = (float)MechJebLib.Utils.Statics.Clamp((LOSE_ALIGN_DEG - aim) / (LOSE_ALIGN_DEG - BURN_ALIGN_DEG), 0, 1);
                    Core.Thrust.RequestActiveThrottle(Math.Max(flipThrottle, wanted * fade), false);
                    Status = Localizer.Format("#MechJeb_LG2P_StatusBoostback", Dv.ToString("F0"), PredictedMiss.ToString("F0")); //Boostback burn: <<1>> m/s to go, predicted miss <<2>> m
                }
                else
                {
                    Core.Thrust.RequestActiveThrottle(flipThrottle, false);
                    Status = Localizer.Format("#MechJeb_LG2P_StatusBoostbackFlip", aim.ToString("F0"), Dv.ToString("F0")); //Boostback: turning (<<1>>° to go), then <<2>> m/s
                }

                return this;
            }
        }
    }
}
