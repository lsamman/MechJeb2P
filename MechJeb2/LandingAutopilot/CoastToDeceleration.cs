using System;
using System.Linq;
using KSP.Localization;
using UnityEngine;

namespace MuMech
{
    namespace Landing
    {
        public class CoastToDeceleration : AutopilotStep
        {
            public CoastToDeceleration(MechJebCore core) : base(core)
            {
            }

            public override AutopilotStep Drive(FlightCtrlState s)
            {
                if (!Core.Landing.PredictionReady)
                    return this;

                Vector3d deltaV = Core.Landing.ComputeCourseCorrection(true);

                // it can stop the landing
                if (!Core.Landing.Enabled) return null;

                if (!Core.Landing.RCSAdjustment) return this;

                if (deltaV.magnitude > 3)
                    Core.RCS.Enabled = true;
                else if (deltaV.magnitude < 0.01)
                    Core.RCS.Enabled = false;

                if (Core.RCS.Enabled)
                    Core.RCS.SetWorldVelocityError(deltaV);

                return this;
            }

            private bool _warpReady;

            // the angle of attack flown now, for the status
            private double _aoaDeg;

            /// <summary>
            /// MechJeb2P aero steering. Flying engines-first through the atmosphere, the body makes lift toward the side its
            /// windward end (the engines) is turned to. The course correction says which way the landing site must move: the
            /// engines go that way, so the nose (the control forward, held retrograde by the standard step) goes the other
            /// way, by an angle of attack that grows with the correction and is capped at MaxAoA. The predictions do not see
            /// this lift, so the loop is a proportional one on the predicted miss: as the miss closes, the angle goes to zero.
            /// </summary>
            private Vector3d AeroSteeringDirection()
            {
                Vector3d retrograde = -VesselState.SurfaceVelocity.normalized;
                Vector3d correction = Core.Landing.ComputeCourseCorrection(true);
                Vector3d side = Vector3d.Exclude(VesselState.SurfaceVelocity, correction);

                const double FULL_DEFLECTION_DV = 20; // m/s of correction for the full angle of attack
                double maxAoA = UtilMath.Deg2Rad * MechJebLib.Utils.Statics.Clamp(Core.Landing.MaxAoA, 0, 30);
                double aoa = maxAoA * Math.Min(1, side.magnitude / FULL_DEFLECTION_DV);
                _aoaDeg = UtilMath.Rad2Deg * aoa;

                if (side.sqrMagnitude < 1e-6 || aoa < 1e-4) return retrograde;

                return (Math.Cos(aoa) * retrograde - Math.Sin(aoa) * side.normalized).normalized;
            }

            public override AutopilotStep OnFixedUpdate()
            {
                Core.Thrust.ThrustOff();

                // If the atmospheric drag is has started to act on the vessel then we are in a position to start considering when to deploy the parachutes.
                if (Core.Landing.DeployChutes)
                {
                    if (Core.Landing.ParachutesDeployable())
                    {
                        Core.Landing.ControlParachutes();
                    }
                }

                double maxAllowedSpeed = Core.Landing.MaxAllowedSpeed();
                if (VesselState.SpeedSurface > 0.9 * maxAllowedSpeed)
                {
                    Core.Warp.MinimumWarp();
                    if (Core.Landing.RCSAdjustment)
                        Core.RCS.Enabled = false;
                    return new DecelerationBurn(Core);
                }

                Status = Localizer.Format("#MechJeb_LandingGuidance_Status1"); //"Coasting toward deceleration burn"

                if (Core.Landing.LandAtTarget)
                {
                    double currentError = Vector3d.Distance(Core.Target.GetPositionTargetPosition(), Core.Landing.LandingSite);
                    if (currentError > 1000)
                    {
                        if (!VesselState.ParachuteDeployed && VesselState.LimitedMaxThrustAcceleration > 0 &&
                            VesselState.DragAcceleration <=
                            0.1) // However if there is already a parachute deployed or drag is high, then do not bother trying to correct the course as we will not have any attitude control anyway.
                        {
                            Core.Warp.MinimumWarp();
                            if (Core.Landing.RCSAdjustment)
                                Core.RCS.Enabled = false;
                            return Core.Landing.CourseCorrectionStep();
                        }
                    }
                    else
                    {
                        Vector3d deltaV = Core.Landing.ComputeCourseCorrection(true);
                        Status += "\n" + Localizer.Format("#MechJeb_LandingGuidance_Status2",
                            deltaV.magnitude.ToString("F3")); //"Course correction DV: " +  + " m/s"
                        if (Core.Landing.AeroSteering && VesselState.DragAcceleration >= 0.01)
                            Status += "\n" + Localizer.Format("#MechJeb_LG2P_StatusAero", _aoaDeg.ToString("F1")); //Aero steering: <<1>>° angle of attack
                    }
                }

                // If we're already low, skip directly to the Deceleration burn
                if (VesselState.AltitudeASL < Core.Landing.DecelerationEndAltitude() + 5)
                {
                    Core.Warp.MinimumWarp();
                    if (Core.Landing.RCSAdjustment)
                        Core.RCS.Enabled = false;
                    return new DecelerationBurn(Core);
                }

                if (Core.Attitude.AttitudeAngleFromTarget() < 1) { _warpReady = true; } // less warp start warp stop jumping

                if (Core.Attitude.AttitudeAngleFromTarget() > 5) { _warpReady = false; } // hopefully

                if (Core.Landing.PredictionReady)
                {
                    if (VesselState.DragAcceleration < 0.01)
                    {
                        double decelerationStartTime = Core.Landing.Prediction.Trajectory.Any()
                            ? Core.Landing.Prediction.Trajectory.First().UT
                            : VesselState.Time;
                        Vector3d decelerationStartAttitude = -Orbit.WorldOrbitalVelocityAtUT(decelerationStartTime);
                        decelerationStartAttitude += MainBody.getRFrmVel(Orbit.WorldPositionAtUT(decelerationStartTime));
                        decelerationStartAttitude = decelerationStartAttitude.normalized;
                        Core.Attitude.AttitudeTo(decelerationStartAttitude, AttitudeReference.INERTIAL, Core.Landing);
                    }
                    else if (Core.Landing.AeroSteering && Core.Landing.LandAtTarget && VesselState.SpeedSurface > 50)
                    {
                        Core.Attitude.AttitudeTo(AeroSteeringDirection(), AttitudeReference.INERTIAL, Core.Landing);
                    }
                    else
                    {
                        Core.Attitude.AttitudeTo(Vector3.back, AttitudeReference.SURFACE_VELOCITY, Core.Landing);
                    }
                }

                //Warp at a rate no higher than the rate that would have us impacting the ground 10 seconds from now:
                if (_warpReady && Core.Node.Autowarp && !Core.Landing.NearJettison)
                {
                    // Make sure if we're hovering that we don't go straight into too fast of a warp
                    // (g * 5 is average velocity falling for 10 seconds from a hover)
                    double velocityGuess = Math.Max(Math.Abs(VesselState.SpeedVertical), VesselState.LocalGravity * 5);
                    Core.Warp.WarpRegularAtRate((float)(VesselState.AltitudeASL / (10 * velocityGuess)));
                }
                else
                {
                    Core.Warp.MinimumWarp();
                }

                return this;
            }
        }
    }
}
