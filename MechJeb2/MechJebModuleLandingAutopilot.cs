extern alias JetBrainsAnnotations;
using System;
using KSP.Localization;
using MechJebLib.Primitives;
using ModuleWheels;
using MuMech.Landing;
using UnityEngine;

namespace MuMech
{
    // Landing AP TODO / BUG list
    // - Add a parameter to define how steep the descent will be ( fraction of Orbit ? )
    // - Fix the auto wrap stop start dance
    // - Replace the openGL code with a LineRenderer
    // -
    public class MechJebModuleLandingAutopilot : AutopilotModule
    {
        private bool _deployedGears;
        public bool LandAtTarget;

        [Persistent(pass = (int)(Pass.LOCAL | Pass.TYPE | Pass.GLOBAL))]
        public readonly EditableDouble TouchdownSpeed = 0.5;

        [Persistent(pass = (int)(Pass.LOCAL | Pass.TYPE | Pass.GLOBAL))]
        public bool DeployGears = true;

        [Persistent(pass = (int)(Pass.LOCAL | Pass.TYPE | Pass.GLOBAL))]
        public readonly EditableInt LimitGearsStage = 0;

        [Persistent(pass = (int)(Pass.LOCAL | Pass.TYPE | Pass.GLOBAL))]
        public bool DeployChutes = true;

        [Persistent(pass = (int)(Pass.LOCAL | Pass.TYPE | Pass.GLOBAL))]
        public readonly EditableInt LimitChutesStage = 0;

        [Persistent(pass = (int)(Pass.LOCAL | Pass.TYPE | Pass.GLOBAL))]
        public bool RCSAdjustment = true;

        // Drop the current stage just above the atmosphere, and aim for what will land, not what is flying now. For a capsule and its service stage.
        [Persistent(pass = (int)(Pass.LOCAL | Pass.TYPE | Pass.GLOBAL))]
        public bool JettisonBeforeEntry = false;

        private bool _stageToDrop;

        // ---- MechJeb2P: precision terminal guidance, tower catch, aero steering and remote flight ----

        private const int PASSES_2P = (int)(Pass.LOCAL | Pass.TYPE | Pass.GLOBAL);

        /// <summary>The precision terminal descent replaces the standard final descent when landing at a target.</summary>
        [Persistent(pass = PASSES_2P)] public bool PrecisionMode;

        /// <summary>Altitude above sea level (m) of the vessel's lowest point at the catch; the ground at the target when landing.</summary>
        [Persistent(pass = PASSES_2P)] public readonly EditableDouble CatchAltitudeASL = 0;

        /// <summary>Height above the catch point (m) at which the precision descent takes over.</summary>
        [Persistent(pass = PASSES_2P)] public readonly EditableDouble TerminalHeight = 800;

        [Persistent(pass = PASSES_2P)] public readonly EditableDouble MaxDescentSpeed = 40;

        [Persistent(pass = PASSES_2P)] public readonly EditableDouble MaxHorizontalSpeed = 15;

        /// <summary>Thrust tilt (°) the precision descent uses to translate.</summary>
        [Persistent(pass = PASSES_2P)] public readonly EditableDouble MaxTilt = 12;

        /// <summary>Descent speed (m/s) through the catch window, and the touchdown speed of a precision landing.</summary>
        [Persistent(pass = PASSES_2P)] public readonly EditableDouble CaptureSpeed = 0.5;

        /// <summary>Hold the catch altitude until caught, up to HoverTimeLimit seconds (0: no limit); off sinks straight through.</summary>
        [Persistent(pass = PASSES_2P)] public bool HoverAtCatch = true;

        [Persistent(pass = PASSES_2P)] public readonly EditableDouble HoverTimeLimit = 20;

        /// <summary>Horizontal miss (m) within which the catch counts as centred.</summary>
        [Persistent(pass = PASSES_2P)] public readonly EditableDouble HorizontalTolerance = 1.5;

        /// <summary>Keep the vessel's top (navball up) on CatchHeading during the terminal descent, so the catch pins face the arms.</summary>
        [Persistent(pass = PASSES_2P)] public bool HoldHeading;

        [Persistent(pass = PASSES_2P)] public readonly EditableDouble CatchHeading = 0;

        /// <summary>Steer the entry with body lift: an angle of attack off retrograde toward the target, up to MaxAoA.</summary>
        [Persistent(pass = PASSES_2P)] public bool AeroSteering;

        [Persistent(pass = PASSES_2P)] public readonly EditableDouble MaxAoA = 5;

        /// <summary>Thrust tilt (°) the braking burn may use to steer the landing site (MechJeb's own limit is 5.7°).</summary>
        [Persistent(pass = PASSES_2P)] public readonly EditableDouble BrakingMaxTilt = 5.7;

        /// <summary>Keep flying when the player switches to another vessel: the vessel and its target stay loaded within RemoteRangeKm.</summary>
        [Persistent(pass = PASSES_2P)] public bool RemoteControl = true;

        [Persistent(pass = PASSES_2P)] public readonly EditableDouble RemoteRangeKm = 150;

        /// <summary>Drop a rails time warp the player starts while this vessel flies unfocused: on rails it would fall ballistically.</summary>
        [Persistent(pass = PASSES_2P)] public bool RemoteBlockWarp = true;

        /// <summary>Write a CSV flight log of each landing under Logs/MechJeb2P in the KSP folder.</summary>
        [Persistent(pass = PASSES_2P)] public bool FlightLog = true;

        /// <summary>Throttle (fraction) held while the boostback turns the vessel around, so the gimbals have torque to turn with.</summary>
        [Persistent(pass = PASSES_2P)] public readonly EditableDouble BoostbackFlipThrottle = 0.05;

        /// <summary>Drag acceleration (m/s²) above which the boostback stands down and the entry is flown as it is.</summary>
        [Persistent(pass = PASSES_2P)] public readonly EditableDouble BoostbackMaxDrag = 1.0;

        /// <summary>Predicted miss (m) within which the boostback is done and the coast begins.</summary>
        [Persistent(pass = PASSES_2P)] public readonly EditableDouble BoostbackDoneMiss = 100;

        /// <summary>
        /// Thrust left after the terminal phase's engine mode switch, as a fraction of the thrust before it (3 of 13 engines:
        /// 0.23). The switch waits until the braking still to do fits that thrust. 1 = no reduction.
        /// </summary>
        [Persistent(pass = PASSES_2P)] public readonly EditableDouble TerminalThrustFraction = 1;

        public FlightLog2P Log { get; private set; }
        private string _stopReason;

        public bool PrecisionActive => PrecisionMode && LandAtTarget;

        /// <summary>Distance from the centre of mass down to the vessel's lowest point, along the local vertical.</summary>
        public double BottomOffset => Math.Max(0, VesselState.AltitudeTrue - VesselState.AltitudeBottom);

        /// <summary>Altitude above sea level of the vessel's lowest point.</summary>
        public double BottomAltitudeASL => VesselState.AltitudeASL - BottomOffset;

        public double HeightAboveCatch => BottomAltitudeASL - CatchAltitudeASL;

        /// <summary>The catch point in world coordinates: the target coordinates at the catch altitude.</summary>
        public Vector3d CatchPointWorld() => MainBody.GetWorldSurfacePosition(Core.Target.targetLatitude, Core.Target.targetLongitude, CatchAltitudeASL);

        /// <summary>Height of the catch point above the terrain at the target.</summary>
        public double CatchHeightAboveTerrain() =>
            Core.Target.targetBody == null ? 0 : CatchAltitudeASL - Core.Target.targetBody.TerrainAltitude(Core.Target.targetLatitude, Core.Target.targetLongitude);

        /// <summary>Sets the catch altitude from where the vessel's lowest point is now: sitting in the arms on the pad, or hovering at the catch height.</summary>
        public void SetCatchAltitudeFromVessel()
        {
            CatchAltitudeASL.Val = Math.Round(BottomAltitudeASL, 1);
            Dirty = true;
        }

        /// <summary>Sets the catch altitude to the terrain at the target: a precision landing on the ground.</summary>
        public void SetCatchAltitudeToTerrain()
        {
            if (Core.Target.targetBody == null) return;
            CatchAltitudeASL.Val = Math.Round(Core.Target.targetBody.TerrainAltitude(Core.Target.targetLatitude, Core.Target.targetLongitude), 1);
            Dirty = true;
        }

        /// <summary>Compass heading (°) the vessel's top (navball up) points at now.</summary>
        public double CurrentTopHeading()
        {
            Vector3d top = -Vessel.GetTransform().forward;
            double heading = UtilMath.Rad2Deg * Math.Atan2(Vector3d.Dot(top, VesselState.East), Vector3d.Dot(top, VesselState.North));
            return MechJebLib.Utils.Statics.Clamp360(heading);
        }

        public void SetHeadingFromVessel()
        {
            CatchHeading.Val = Math.Round(CurrentTopHeading(), 1);
            Dirty = true;
        }

        /// <summary>Sets the target coordinates and the catch altitude from where the vessel stands now: on the pad, in the arms.</summary>
        public void SetCatchPointFromVessel()
        {
            Core.Target.SetPositionTarget(MainBody, VesselState.Latitude, VesselState.Longitude);
            SetCatchAltitudeFromVessel();
            SetHeadingFromVessel();
        }

        /// <summary>The settings a Starship Expansion Project booster or ship catch wants.</summary>
        public void SepCatchPreset()
        {
            PrecisionMode = true;
            HoverAtCatch = true;
            HoldHeading = true;
            AeroSteering = true;
            RemoteControl = true;
            TerminalHeight.Val = 800;
            MaxDescentSpeed.Val = 60;
            MaxHorizontalSpeed.Val = 15;
            MaxTilt.Val = 12;
            CaptureSpeed.Val = 0.5;
            HoverTimeLimit.Val = 20;
            HorizontalTolerance.Val = 1.5;
            MaxAoA.Val = 5;
            BrakingMaxTilt.Val = 15;
            BoostbackFlipThrottle.Val = 0.05;
            BoostbackMaxDrag.Val = 1.0;
            BoostbackDoneMiss.Val = 100;
            TerminalThrustFraction.Val = 0.25; // the preset's braking mode is 13 engines, its terminal mode 3
            RemoteRangeKm.Val = 150;
            FlightLog = true;
            DeployGears = false;
            Core.LandingParts?.SepCatchPreset();
            Dirty = true;
        }

        public static LandingPhase PhaseOf(AutopilotStep step)
        {
            switch (step)
            {
                case null:                 return LandingPhase.None;
                case Boostback _:          return LandingPhase.Entry;
                case PrecisionDescent _:   return LandingPhase.Terminal;
                case DecelerationBurn _:
                case KillHorizontalVelocity _:
                case FinalDescent _:       return LandingPhase.Braking;
                default:                   return LandingPhase.Entry;
            }
        }

        public LandingPhase CurrentPhase => PhaseOf(CurrentStep);

        // The part options follow the phase. The engine modes are the exception: the braking phase's mode goes on when the coast
        // to the braking burn begins, because the burn's timing is computed from the engines that are running during the coast
        // (switching at the burn's start to fewer engines than it was planned with would leave it unable to stop); and the
        // terminal phase's mode goes on only once the precision descent finds the braking still to do fits the thrust that
        // remains after the switch (TerminalThrustFraction).
        private void UpdatePartControlPhase()
        {
            if (Core.LandingParts == null || !Enabled) return;
            LandingPhase phase = CurrentPhase;
            Core.LandingParts.ApplyPhase(phase);

            if (phase == LandingPhase.Entry && !(CurrentStep is CoastToDeceleration))
                Core.LandingParts.ApplyEngineMode(LandingPhase.Entry);
            else if (phase == LandingPhase.Braking || CurrentStep is CoastToDeceleration)
                Core.LandingParts.ApplyEngineMode(LandingPhase.Braking);
            else if (phase == LandingPhase.Terminal && CurrentStep is PrecisionDescent pd && pd.TerminalEnginesReady)
                Core.LandingParts.ApplyEngineMode(LandingPhase.Terminal);
        }

        /// <summary>The step that steers the landing site after the deorbit: the boostback in precision mode, MechJeb's course correction otherwise.</summary>
        public AutopilotStep CourseCorrectionStep() => PrecisionActive ? (AutopilotStep)new Boostback(Core) : new CourseCorrection(Core);

        // The precision descent takes over from the braking steps once below TerminalHeight and the braking still to do fits
        // its descent profile (the braking burn, a near-suicide burn with a big cluster, may still be fast at that height), or
        // whatever the state once below a quarter of that height, where the standard steps would be flying to the ground.
        private void ConsiderPrecisionTakeover()
        {
            if (!PrecisionActive || CurrentStep == null || CurrentStep is PrecisionDescent) return;
            if (!(CurrentStep is DecelerationBurn || CurrentStep is KillHorizontalVelocity || CurrentStep is FinalDescent || CurrentStep is CoastToDeceleration)) return;

            double terminal = Math.Max(50, TerminalHeight);
            double h = HeightAboveCatch;
            if (h >= terminal) return;

            double g = VesselState.LocalGravity;
            double aMax = VesselState.ThrustVectorMaxThrottle.magnitude / VesselState.Mass;
            if (VesselState.LimitedMaxThrustAcceleration > 0 && VesselState.LimitedMaxThrustAcceleration < aMax) aMax = VesselState.LimitedMaxThrustAcceleration;
            double vZ = VesselState.SpeedVertical;
            double aReq = g + (vZ < 0 ? vZ * vZ / (2 * Math.Max(h, 1)) : 0); // to stop at the catch altitude from here
            bool fits = aReq < 0.6 * aMax && VesselState.SpeedSurfaceHorizontal < 60;

            if (fits || h < 0.25 * terminal)
            {
                Log?.Event("precision descent takes over from " + CurrentStep.GetType().Name + ": h " + h.ToString("F0") + " m, vZ " + vZ.ToString("F1") + " m/s, vH " +
                           VesselState.SpeedSurfaceHorizontal.ToString("F1") + " m/s, needs " + aReq.ToString("F1") + " of " + aMax.ToString("F1") + " m/s2" + (fits ? "" : " (forced: low)"));
                Core.Thrust.Tmode = MechJebModuleThrustController.TMode.OFF;
                Core.Thrust.TransKillH = false;
                SetStep(new PrecisionDescent(Core));
            }
        }

        // the jettison has to happen at normal speed
        public bool NearJettison => _stageToDrop && VesselState.AltitudeASL < MainBody.RealMaxAtmosphereAltitude() + 30000;

        // This is used to adjust the height at which the parachutes semi deploy as a means of
        // targeting the landing in an atmosphere where it is not possible to control atitude
        // to perform course correction burns.
        private ParachutePlan _parachutePlan;

        //Landing prediction data:
        private MechJebModuleLandingPredictions _predictor;
        public ReentrySimulation.Result Prediction => _predictor.Result;

        private ReentrySimulation.Result _errorPrediction => _predictor.GetErrorResult();

        public bool PredictionReady //We shouldn't do any autopilot stuff until this is true
        {
            get
            {
                // Check that there is a prediction and that it is a landing prediction.
                if (Prediction == null)
                {
                    return false;
                }

                return Prediction.Outcome == ReentrySimulation.Outcome.LANDED;
            }
        }

        // We shouldn't try to use an ErrorPrediction until this is true.
        private bool _errorPredictionReady => _errorPrediction is { Outcome: ReentrySimulation.Outcome.LANDED };

        private double _landingAltitudeTime = -1;
        private double _landingAltitudeCached;

        // read about ten times per tick, and each read queries the terrain
        private double _landingAltitude
        {
            get
            {
                if (_landingAltitudeTime != Time.fixedTime)
                {
                    _landingAltitudeCached = ComputeLandingAltitude;
                    _landingAltitudeTime = Time.fixedTime;
                }

                return _landingAltitudeCached;
            }
        }

        private double ComputeLandingAltitude // The altitude above sea level of the terrain at the landing site
        {
            get
            {
                if (!PredictionReady) return 0;

                // Although we know the landingASL as it is in the prediction, we suspect that
                // it might sometimes be incorrect. So to check we will calculate it again here,
                // and if the two differ log an error. It seems that this terrain ASL calls when
                // made from the simulatiuon thread are regularly incorrect, but are OK when made
                // from this thread. At the time of writting (KSP0.23) there seem to be several
                // other things going wrong with he terrain system, such as visual glitches as
                // we as the occasional exceptions being thrown when calls to the CelestialBody
                // object are made. I suspect a bug or some sort - for now this hack improves
                // the landing results.
                {
                    double checkASL = Prediction.Body.TerrainAltitude(Prediction.EndPosition.Latitude, Prediction.EndPosition.Longitude);
                    // ReSharper disable once CompareOfFloatsByEqualityOperator
                    // ReSharper disable once RedundantCheckBeforeAssignment
                    if (checkASL != Prediction.EndASL)
                    {
                        // I know that this check is not required as we might as well always make
                        // the asignment. However this allows for some debug monitoring of how often this is occuring.
                        Prediction.EndASL = checkASL;
                    }
                }

                return Prediction.EndASL;
            }
        }

        public Vector3d LandingSite =>
            MainBody.GetWorldSurfacePosition(Prediction.EndPosition.Latitude,
                Prediction.EndPosition.Longitude, _landingAltitude) - MainBody.position; // The current position of the landing site

        private Vector3d _rotatedLandingSite => Prediction.WorldEndPosition(); // The position where the landing site will be when we land at it

        public IDescentSpeedPolicy DescentSpeedPolicy;
        private double _vesselAverageDrag;

        public MechJebModuleLandingAutopilot(MechJebCore core)
            : base(core)
        {
            Log = new FlightLog2P(this);
        }

        public override void OnStart(PartModule.StartState state) => _predictor = Core.GetComputerModule<MechJebModuleLandingPredictions>();

        //public interface:
        public void LandAtPositionTarget(object controller)
        {
            // every step lands at the target coordinates on this body
            if (!Core.Target.SurfaceTargetExists(MainBody))
            {
                ScreenMessages.PostScreenMessage(Localizer.Format("#MechJeb_LandingGuidance_noSurfaceTarget", MainBody.displayName.LocalizeRemoveGender()), 5,
                    ScreenMessageStyle.UPPER_CENTER); //Land at target: the target is not a point on <<1>>'s surface.
                return;
            }

            LandAtTarget = true;
            Users.Add(controller);

            _predictor.Users.Add(this);
            Vessel.RemoveAllManeuverNodes(); // For the benefit of the landing predictions module

            _deployedGears = false;
            _stageToDrop = JettisonBeforeEntry && MainBody.atmosphere && NextStageDropsParts();

            // Create a new parachute plan
            _parachutePlan = new ParachutePlan(this);
            _parachutePlan.StartPlanning();

            if (Orbit.PeA < 0)
                SetStep(CourseCorrectionStep());
            else if (UseLowDeorbitStrategy())
                SetStep(new PlaneChange(Core));
            else
                SetStep(new DeorbitBurn(Core));

            _stopReason = null;
            if (FlightLog) Log.Begin("land at target" + (PrecisionMode ? " (precision mode)" : ""));
        }

        public void LandUntargeted(object controller)
        {
            LandAtTarget = false;
            Users.Add(controller);

            // the predicted landing site gives the ground altitude the chutes are armed above
            _predictor.Users.Add(this);

            _deployedGears = false;
            _stageToDrop = JettisonBeforeEntry && MainBody.atmosphere && NextStageDropsParts();

            // Create a new parachute plan
            _parachutePlan = new ParachutePlan(this);
            _parachutePlan.StartPlanning();

            SetStep(new UntargetedDeorbit(Core));

            _stopReason = null;
            if (FlightLog) Log.Begin("land somewhere");
        }

        public void StopLanding(string reason = "stopped by the player or another module")
        {
            if (Enabled && _stopReason == null)
            {
                _stopReason = reason;
                Log?.Event("landing stopped: " + reason);
            }

            Users.Clear();
            Core.Thrust.ThrustOff();
            Core.Thrust.Users.Remove(this);
            if (Core.Landing.RCSAdjustment)
                Core.RCS.Enabled = false;
            SetStep(null);
        }

        protected override void StopAfterStepFailures() => StopLanding("the step kept throwing exceptions (see KSP.log)");

        public override void Drive(FlightCtrlState s)
        {
            if (!Active)
                return;

            DescentSpeedPolicy = PickDescentSpeedPolicy();

            _predictor.dropStage = _stageToDrop ? Vessel.currentStage - 1 : -1;
            _predictor.descentSpeedPolicy = DescentSpeedPolicy; // immutable, so the simulation can share it
            _predictor.decelEndAltitudeASL = DecelerationEndAltitude();
            _predictor.parachuteSemiDeployMultiplier = _parachutePlan.Multiplier;
            _predictor.maxOrbits = 0.5;
            _predictor.noSkipToFreefall = false;
            _predictor.deployChutes = DeployChutes;
            _predictor.limitChutesStage = LimitChutesStage;

            // Consider lowering the langing gear
            {
                double minalt = Math.Min(VesselState.AltitudeBottom, Math.Min(VesselState.AltitudeASL, VesselState.AltitudeTrue));
                if (DeployGears && !_deployedGears && minalt < 1000)
                    DeployLandingGears();
            }

            ConsiderPrecisionTakeover();

            base.Drive(s);

            UpdatePartControlPhase();
            Log?.Sample(s);
        }

        public override void OnFixedUpdate()
        {
            _vesselAverageDrag = VesselAverageDrag();
            base.OnFixedUpdate();
            UpdatePartControlPhase();
            DeployParachutes();
            JettisonIfDue();
        }

        protected override void OnModuleEnabled()
        {
            Core.Attitude.Users.Add(this);
            Core.Thrust.Users.Add(this);
            Core.LandingParts?.BeginLanding();
        }

        protected override void OnModuleDisabled()
        {
            Log?.End(_stopReason ?? "landing module disabled");
            _stopReason = null;
            Core.LandingParts?.EndLanding();
            Core.Attitude.AttitudeDeactivate();
            _predictor.Users.Remove(this);
            _predictor.descentSpeedPolicy = null;
            _predictor.maxOrbits = MechJebModuleLandingPredictions.MAX_ORBITS_FREE;
            _predictor.noSkipToFreefall = true;
            Core.Thrust.ThrustOff();
            Core.Thrust.Users.Remove(this);
            if (Core.Landing.RCSAdjustment)
                Core.RCS.Enabled = false;
            SetStep(null);
        }

        // Estimate the delta-V of the correction burn that would be required to put us on
        // course for the target
        public Vector3d ComputeCourseCorrection(bool allowPrograde)
        {
            // actualLandingPosition is the predicted actual landing position
            Vector3d actualLandingPosition = _rotatedLandingSite - MainBody.position;

            // orbitLandingPosition is the point where our current orbit intersects the planet
            double endRadius = MainBody.Radius + DecelerationEndAltitude() - 100;

            // Seems we are already landed ? (an ellipse that never rises above the end radius; ApR is negative on a hyperbola, which
            // always rises again)
            if ((Orbit.eccentricity < 1 && endRadius > Orbit.ApR) || Vessel.LandedOrSplashed)
            {
                StopLanding(Vessel.LandedOrSplashed ? "landed (KSP reports the vessel landed or splashed)" : "the orbit never rises above the braking end radius: seems already landed");
                return Vector3d.zero;
            }

            Vector3d orbitLandingPosition = Orbit.WorldBCIPositionAtUT(
                Orbit.PeR < endRadius ? Orbit.NextTimeOfRadius(VesselState.Time, endRadius) : Orbit.NextPeriapsisTime(VesselState.Time)
            );

            // convertOrbitToActual is a rotation that rotates orbitLandingPosition on actualLandingPosition
            var convertOrbitToActual = Quaternion.FromToRotation(orbitLandingPosition, actualLandingPosition);

            // Consider the effect small changes in the velocity in each of these three directions
            Vector3d[] perturbationDirections = { VesselState.SurfaceVelocity.normalized, VesselState.RadialPlusSurface, VesselState.NormalPlusSurface };

            // Compute the effect burns in these directions would
            // have on the landing position, where we approximate the landing position as the place
            // the perturbed orbit would intersect the planet.
            var deltas = new Vector3d[3];
            for (int i = 0; i < 3; i++)
            {
                //warning: hard experience shows that setting this too low leads to bewildering bugs due to finite precision of Orbit functions
                const double PERTURBATION_DELTA_V = 1;

                Orbit perturbedOrbit =
                    Orbit.PerturbedOrbit(VesselState.Time, PERTURBATION_DELTA_V * perturbationDirections[i]); //compute the perturbed orbit

                double perturbedLandingTime = perturbedOrbit.PeR < endRadius
                    ? perturbedOrbit.NextTimeOfRadius(VesselState.Time, endRadius)
                    : perturbedOrbit.NextPeriapsisTime(VesselState.Time);

                Vector3d perturbedLandingPosition = perturbedOrbit.WorldBCIPositionAtUT(perturbedLandingTime); //find where it hits the planet

                //find the difference between that and the original orbit's intersection point
                Vector3d landingDelta = perturbedLandingPosition - orbitLandingPosition;

                //rotate that difference vector so that we can now think of it as starting at the actual landing position
                landingDelta = convertOrbitToActual * landingDelta;

                //project the difference vector onto the plane tangent to the actual landing position
                landingDelta = Vector3d.Exclude(actualLandingPosition, landingDelta);

                //normalize by the delta-V considered, so that deltas now has units of meters per (meter/second) [i.e., seconds]
                deltas[i] = landingDelta / PERTURBATION_DELTA_V;
            }

            // Now deltas stores the predicted offsets in landing position produced by each of the three perturbations.
            // We now figure out the offset we actually want

            // First we compute the target landing position. We have to convert the latitude and longitude of the target
            // into a position. We can't just get the current position of those coordinates, because the planet will
            // rotate during the descent, so we have to account for that.
            Vector3d desiredLandingPosition =
                MainBody.GetWorldSurfacePosition(Core.Target.targetLatitude, Core.Target.targetLongitude, 0) - MainBody.position;
            float bodyRotationAngleDuringDescent = (float)(360 * (Prediction.EndUT - VesselState.Time) / MainBody.rotationPeriod);
            var bodyRotationDuringFall = Quaternion.AngleAxis(bodyRotationAngleDuringDescent, MainBody.angularVelocity.normalized);
            desiredLandingPosition = bodyRotationDuringFall * desiredLandingPosition;

            Vector3d desiredDelta = desiredLandingPosition - actualLandingPosition;
            desiredDelta = Vector3d.Exclude(actualLandingPosition, desiredDelta);

            // Now desiredDelta gives the desired change in our actual landing position (projected onto a plane
            // tangent to the actual landing position).

            Vector3d downrangeDirection;
            Vector3d downrangeDelta;
            if (allowPrograde)
            {
                // Construct the linear combination of the prograde and radial+ perturbations
                // that produces the largest effect on the landing position. The Math.Sign is to
                // detect and handle the case where radial+ burns actually bring the landing sign closer
                // (e.g. when we are traveling close to straight up)
                downrangeDirection = (deltas[0].magnitude * perturbationDirections[0]
                    + Math.Sign(Vector3d.Dot(deltas[0], deltas[1])) * deltas[1].magnitude * perturbationDirections[1]).normalized;

                downrangeDelta = Vector3d.Dot(downrangeDirection, perturbationDirections[0]) * deltas[0]
                    + Vector3d.Dot(downrangeDirection, perturbationDirections[1]) * deltas[1];
            }
            else
            {
                // If we aren't allowed to burn prograde, downrange component of the landing
                // position has to be controlled by radial+/- burns:
                downrangeDirection = perturbationDirections[1];
                downrangeDelta = deltas[1];
            }

            // Now solve a 2x2 system of linear equations to determine the linear combination
            // of perturbationDirection01 and normal+ that will give the desired offset in the
            // predicted landing position.
            var a = new M2(
                downrangeDelta.sqrMagnitude, Vector3d.Dot(downrangeDelta, deltas[2]),
                Vector3d.Dot(downrangeDelta, deltas[2]), deltas[2].sqrMagnitude
            );

            var b = new V2(Vector3d.Dot(desiredDelta, downrangeDelta), Vector3d.Dot(desiredDelta, deltas[2]));

            V2 coeffs = a.inverse * b;

            Vector3d courseCorrection = coeffs.x * downrangeDirection + coeffs.y * perturbationDirections[2];

            return courseCorrection;
        }

        public void ControlParachutes()
        {
            // Firstly - do we have a parachute plan? If not then we had better get one quick!
            _parachutePlan ??= new ParachutePlan(this);

            // Are there any deployable parachute? If not then there is no point in us being here. Let's switch to cruising to the deceleration burn instead.
            if (!ParachutesDeployable())
            {
                _predictor.runErrorSimulations = false;
                _parachutePlan.ClearData();
                return;
            }

            _predictor.runErrorSimulations = true;

            // Is there an error prediction available? If so add that into the mix
            if (_errorPredictionReady && !double.IsNaN(_errorPrediction.ParachuteMultiplier))
            {
                _parachutePlan.AddResult(_errorPrediction);
            }

            // Has the Landing prediction been updated? If so then we can use the result to refine our parachute plan.
            if (PredictionReady && !double.IsNaN(Prediction.ParachuteMultiplier))
            {
                _parachutePlan.AddResult(Prediction);
            }
        }

        // The next stage could be anything: firing one that separates nothing could open the parachutes above the atmosphere.
        private bool NextStageDropsParts()
        {
            int stage = Vessel.currentStage - 1;
            bool drops = false, stays = false;
            foreach (Part p in Vessel.parts)
            {
                if (p.IsDecoupledInStage(stage)) drops = true;
                else if (p.isControlSource != Vessel.ControlLevel.NONE) stays = true;
            }

            return drops && stays;
        }

        private void JettisonIfDue()
        {
            if (!_stageToDrop || Orbit.PeA > 0 || VesselState.AltitudeASL > MainBody.RealMaxAtmosphereAltitude() + 4000)
                return;

            // still burning towards the target, or not at normal speed yet
            if (Core.Thrust.TargetThrottle > 0 || !MuUtils.PhysicsRunning())
                return;

            _stageToDrop = false;
            _predictor.dropStage = -1;
            KSP.UI.Screens.StageManager.ActivateNextStage();
        }

        private void DeployParachutes()
        {
            if (!(MainBody.atmosphere && DeployChutes)) return;

            for (int i = 0; i < VesselState.Parachutes.Count; i++)
            {
                ModuleParachute p = VesselState.Parachutes[i];
                // what is the ASL at which we should deploy this parachute? It is the actual deployment height above the surface + the ASL of the predicted landing point.
                double landingSiteASL = _landingAltitude;
                double parachuteDeployAboveGroundAtLandingSite = p.deployAltitude * _parachutePlan.Multiplier;

                double aslDeployAltitude = parachuteDeployAboveGroundAtLandingSite + landingSiteASL;

                if (p.part.inverseStage >= LimitChutesStage && p.deploymentState == ModuleParachute.deploymentStates.STOWED &&
                    aslDeployAltitude > VesselState.AltitudeASL && p.deploymentSafeState == ModuleParachute.deploymentSafeStates.SAFE)
                {
                    p.Deploy();
                    //Debug.Log("Deploying parachute " + p.name + " at " + ASLDeployAltitude + ". (" + LandingSiteASL + " + " + ParachuteDeployAboveGroundAtLandingSite +")");
                }
            }
        }

        // This methods works out if there are any parachutes that are capable of being deployed
        public bool ParachutesDeployable()
        {
            if (!MainBody.atmosphere) return false;
            if (!DeployChutes) return false;

            for (int i = 0; i < VesselState.Parachutes.Count; i++)
            {
                ModuleParachute p = VesselState.Parachutes[i];
                if (Math.Max(p.part.inverseStage, 0) >= LimitChutesStage && p.deploymentState == ModuleParachute.deploymentStates.STOWED)
                {
                    return true;
                }
            }

            return false;
        }

        // This methods works out if there are any parachutes that have already been deployed (or semi deployed)
        private bool ParachutesDeployed() => VesselState.ParachuteDeployed;

        private void DeployLandingGears()
        {
            for (int i = 0; i < Vessel.parts.Count; i++)
            {
                Part p = Vessel.parts[i];
                if (p.HasModule<ModuleWheelDeployment>())
                {
                    // p.inverseStage is -1 for some configuration ?!?
                    if (Math.Max(p.inverseStage, 0) >= LimitGearsStage)
                    {
                        foreach (ModuleWheelDeployment wd in p.FindModulesImplementing<ModuleWheelDeployment>())
                        {
                            if (wd.fsm.CurrentState == wd.st_retracted || wd.fsm.CurrentState == wd.st_retracting)
                                wd.EventToggle();
                        }
                    }
                }
            }

            _deployedGears = true;
        }

        private IDescentSpeedPolicy PickDescentSpeedPolicy()
        {
            if (UseAtmosphereToBrake())
            {
                return new PoweredCoastDescentSpeedPolicy(MainBody.Radius + DecelerationEndAltitude(), MainBody.GeeASL * 9.81,
                    VesselState.LimitedMaxThrustAcceleration);
            }

            return new SafeDescentSpeedPolicy(MainBody.Radius + DecelerationEndAltitude(), MainBody.GeeASL * 9.81, VesselState.LimitedMaxThrustAcceleration);
        }

        private const double MIN_ATMOSPHERE_DECELERATION_END = 50;

        public double DecelerationEndAltitude()
        {
            // MechJeb2P: a catch point above the ground (a tower) moves the end of the braking burn up with it, so the precision
            // descent gets its terminal height
            double catchFloor = PrecisionActive ? CatchAltitudeASL + 0.5 * Math.Max(50, TerminalHeight) : double.NegativeInfinity;

            //if the atmosphere is thin, the deceleration burn should end
            //500 meters above the landing site to allow for a controlled final descent
            //MechJebCore.print("DecelerationEndAltitude Vacum " + (200 + LandingAltitude).ToString("F2"));
            if (!UseAtmosphereToBrake()) return Math.Max(200 + _landingAltitude, catchFloor);

            // if the atmosphere is thick, deceleration (meaning freefall through the atmosphere)
            // should end a safe height above the landing site in order to allow braking from terminal velocity
            // FIXME: Drag Length is quite large now without parachutes, check this better
            double landingSiteDragLength = MainBody.DragLength(_landingAltitude, _vesselAverageDrag + ParachuteAddedDragArea(), VesselState.Mass);

            //MechJebCore.print("DecelerationEndAltitude Atmo " + (2 * landingSiteDragLength + LandingAltitude).ToString("F2"));
            // under open chutes the drag length is a few metres: keep a margin for terrain above the prediction and for the engines' spool-up
            return Math.Max(Math.Max(1.1 * landingSiteDragLength, MIN_ATMOSPHERE_DECELERATION_END) + _landingAltitude, catchFloor);
        }

        //On planets with thick enough atmospheres, we shouldn't do a deceleration burn. Rather,
        //we should let the atmosphere decelerate us and only burn during the final descent to
        //ensure a safe touchdown speed. How do we tell if the atmosphere is thick enough? We check
        //to see if there is an altitude within the atmosphere for which the characteristic distance
        //over which drag slows the ship is smaller than the altitude above the terrain. If so, we can
        //expect to get slowed to near terminal velocity before impacting the ground.
        public bool UseAtmosphereToBrake()
        {
            double landingSiteDragLength = MainBody.DragLength(_landingAltitude, _vesselAverageDrag + ParachuteAddedDragArea(), VesselState.Mass);

            //if (mainBody.RealMaxAtmosphereAltitude() > 0 && (ParachutesDeployable() || ParachutesDeployed()))
            return MainBody.RealMaxAtmosphereAltitude() > 0 &&
                landingSiteDragLength < 0.7 * MainBody.RealMaxAtmosphereAltitude(); // the ratio is totally arbitrary until I get something better
        }

        // Get an average drag for the whole vessel. Far from precise but fast.
        private double VesselAverageDrag()
        {
            float dragCoef = 0;
            for (int i = 0; i < Vessel.parts.Count; i++)
            {
                Part p = Vessel.parts[i];
                if (p.DragCubes.None || p.ShieldedFromAirstream)
                {
                    continue;
                }
                //part.DragCubes.SetDrag( -part.partTransform.InverseTransformDirection( vessel.ReferenceTransform.up ) , 1 );

                float partAreaDrag = 0;
                for (int f = 0; f < 6; f++)
                {
                    partAreaDrag += p.DragCubes.WeightedDrag[f] *
                        p.DragCubes.AreaOccluded[f]; // * PhysicsGlobals.DragCurveValue(0.5, machNumber) but I ll assume it is 1 for now
                }

                dragCoef += partAreaDrag / 6;
            }

            return dragCoef * PhysicsGlobals.DragCubeMultiplier;
        }

        // The drag area the chutes add once fully open, averaged over the six faces as VesselAverageDrag averages a part: the DEPLOYED
        // cube's area times its drag coefficient, less what the chute counts for there now. This is not the exact number, but it's good
        // enough for our use
        private double ParachuteAddedDragArea()
        {
            if (!MainBody.atmosphere || !DeployChutes) return 0;

            double addedDragArea = 0;
            for (int i = 0; i < VesselState.Parachutes.Count; i++)
            {
                ModuleParachute p = VesselState.Parachutes[i];
                if (p.part.inverseStage < LimitChutesStage || p.deploymentState == ModuleParachute.deploymentStates.CUT) continue;

                DragCubeList cubes = p.part.DragCubes;

                float deployedArea = 0;
                for (int c = 0; c < cubes.Cubes.Count; c++)
                {
                    DragCube dragCube = cubes.Cubes[c];
                    if (dragCube.Name != "DEPLOYED")
                        continue;

                    for (int f = 0; f < 6; f++)
                        deployedArea += dragCube.Area[f] * dragCube.Drag[f];
                }

                float currentArea = 0;
                if (!cubes.None && !p.part.ShieldedFromAirstream)
                {
                    for (int f = 0; f < 6; f++)
                        currentArea += cubes.WeightedDrag[f] * cubes.AreaOccluded[f];
                }

                addedDragArea += Math.Max(0, deployedArea - currentArea) / 6;
            }

            return addedDragArea * PhysicsGlobals.DragCubeMultiplier;
        }

        private bool UseLowDeorbitStrategy()
        {
            if (MainBody.atmosphere) return false;

            double periapsisSpeed = Orbit.WorldOrbitalVelocityAtUT(Orbit.NextPeriapsisTime(VesselState.Time)).magnitude;
            double stoppingDistance = Math.Pow(periapsisSpeed, 2) / (2 * VesselState.LimitedMaxThrustAcceleration);

            return Orbit.PeA < 2 * stoppingDistance + MainBody.Radius / 4;
        }

        public double MaxAllowedSpeed() => DescentSpeedPolicy.MaxAllowedSpeed(VesselState.CoM - MainBody.position, VesselState.SurfaceVelocity);

        public double MaxAllowedSpeedAfterDt(double dt) =>
            DescentSpeedPolicy.MaxAllowedSpeed(VesselState.CoM + VesselState.OrbitalVelocity * dt - MainBody.position,
                VesselState.SurfaceVelocity + dt * VesselState.GravityForce);

        [ValueInfoItem("#MechJeb_ParachuteControlInfo", InfoItem.Category.Misc, showInEditor = false)] //ParachuteControlInfo
        public string ParachuteControlInfo()
        {
            if (!ParachutesDeployable()) return "N/A";

            string retVal = Localizer.Format("#MechJeb_ChuteMultiplier", _parachutePlan.Multiplier.ToString("F7")); //"'Chute Multiplier: " +
            retVal += Localizer.Format("#MechJeb_MultiplierQuality",
                _parachutePlan.MultiplierQuality.ToString("F1")); //"\nMultiplier Quality: " +  + "%"
            retVal += Localizer.Format("#MechJeb_Usingpredictions", _parachutePlan.MultiplierDataAmount); //"\nUsing " +  + " predictions"

            return retVal;
        }

    }

    //A descent speed policy that gives the max safe speed if our entire velocity were straight down
    internal class SafeDescentSpeedPolicy : IDescentSpeedPolicy
    {
        private readonly double _terrainRadius;
        private readonly double _g;
        private readonly double _thrust;

        public SafeDescentSpeedPolicy(double terrainRadius, double g, double thrust)
        {
            _terrainRadius = terrainRadius;
            _g = g;
            _thrust = thrust;
        }

        public double MaxAllowedSpeed(Vector3d pos, Vector3d vel)
        {
            double altitude = pos.magnitude - _terrainRadius;
            return 0.9 * Math.Sqrt(2 * (_thrust - _g) * altitude);
        }
    }

    internal class PoweredCoastDescentSpeedPolicy : IDescentSpeedPolicy
    {
        private readonly float _terrainRadius;
        private readonly float _g;
        private readonly float _thrust;

        public PoweredCoastDescentSpeedPolicy(double terrainRadius, double g, double thrust)
        {
            _terrainRadius = (float)terrainRadius;
            _g = (float)g;
            _thrust = (float)thrust;
        }

        public double MaxAllowedSpeed(Vector3d pos, Vector3d vel)
        {
            if (_terrainRadius < pos.magnitude)
                return double.MaxValue;

            double vSpeed = Vector3d.Dot(vel, pos.normalized);
            double toF = (vSpeed + Math.Sqrt(vSpeed * vSpeed + 2 * _g * (pos.magnitude - _terrainRadius))) / _g;

            //MechJebCore.print("ToF = " + ToF.ToString("F2"));
            return 0.8 * (_thrust - _g) * toF;
        }
    }

    internal class GravityTurnDescentSpeedPolicy : IDescentSpeedPolicy
    {
        private readonly double _terrainRadius;
        private readonly double _g;
        private readonly double _thrust;

        public GravityTurnDescentSpeedPolicy(double terrainRadius, double g, double thrust)
        {
            _terrainRadius = terrainRadius;
            _g = g;
            _thrust = thrust;
        }

        public double MaxAllowedSpeed(Vector3d pos, Vector3d vel)
        {
            //do a binary search for the max speed that avoids death
            double maxFallDistance = pos.magnitude - _terrainRadius;

            double lowerBound = 0;
            double upperBound = 1.1 * vel.magnitude;

            while (upperBound - lowerBound > 0.1)
            {
                double test = (upperBound + lowerBound) / 2;
                if (GravityTurnFallDistance(pos, test * vel.normalized) < maxFallDistance) lowerBound = test;
                else upperBound = test;
            }

            return 0.95 * ((upperBound + lowerBound) / 2);
        }

        private double GravityTurnFallDistance(Vector3d x, Vector3d v)
        {
            double startRadius = x.magnitude;

            const int STEPS = 10;
            for (int i = 0; i < STEPS; i++)
            {
                Vector3d gVec = -_g * x.normalized;
                Vector3d thrustVec = -_thrust * v.normalized;
                double dt = 1.0 / (STEPS - i) * (v.magnitude / _thrust);
                Vector3d newV = v + dt * (thrustVec + gVec);
                x += dt * (v + newV) / 2;
                v = newV;
            }

            double endRadius = x.magnitude;

            endRadius -= v.sqrMagnitude / (2 * (_thrust - _g));

            return startRadius - endRadius;
        }
    }

    // Class to hold all the information about the planned deployment of parachutes.
    internal class ParachutePlan
    {
        // We use a linear regression to calculate the best place to deploy based on previous predictions
        private LinearRegression _regression;

        //  store the last result so that we can check if any new result is actually a new one, or the same one again.
        private ReentrySimulation.Result _lastResult;

        //  store the last error result so that we can check if any new error result is actually a new one, or the same one again.
        private ReentrySimulation.Result _lastErrorResult;

        private readonly CelestialBody _body;
        private readonly MechJebModuleLandingAutopilot _autoPilot;
        private bool _parachutePresent;
        private double _maxSemiDeployHeight;
        private double _minSemiDeployHeight;
        private double _maxMultiplier;

        // This is the correlation coefficient of the dataset, and is used to tell if the data set is providing helpful information or not.
        // It is exposed outside the class as a "quality percentage" where -1 -> 100% and 0 or more -> 0%
        private double _correlation;

        private const int DATA_SET_SIZE = 40;

        // kPa in one atm, the conversion ModuleParachute uses
        private const double KPA_PER_ATM = 101.325;

        public double Multiplier { get; private set; }

        public int MultiplierDataAmount => _regression.DataSetSize;

        public double MultiplierQuality
        {
            get
            {
                double corr = _correlation;
                if (double.IsNaN(corr)) return 0;
                return Math.Max(0, corr * -100);
            }
        }

        // Incorporates a new simulation result into the simulation data set and calculate a new semi deployment multiplier.
        // If the data set has a poor correlation, then it might just leave the mutiplier.
        // If the correlation becomes positive then it will clear the dataset and start again.
        public void AddResult(ReentrySimulation.Result newResult)
        {
            // if this result is the same as the old result, then it is not new!
            if (newResult.MultiplierHasError)
            {
                if (_lastErrorResult != null)
                {
                    if (newResult.ID == _lastErrorResult.ID) { return; }
                }

                _lastErrorResult = newResult;
            }
            else
            {
                if (_lastResult != null)
                {
                    if (newResult.ID == _lastResult.ID) { return; }
                }

                _lastResult = newResult;
            }

            // What was the overshoot for this new result?
            double overshoot = newResult.GetOvershoot(_autoPilot.Core.Target.targetLatitude, _autoPilot.Core.Target.targetLongitude);

            //Debug.Log("overshoot: " + overshoot.ToString("F2") + " multiplier: " + newResult.parachuteMultiplier.ToString("F4") + " hasError:" + newResult.multiplierHasError);

            // Add the new result to the linear regression
            _regression.Add(overshoot, newResult.ParachuteMultiplier);

            // What is the correlation coefficent of the data. If it is weak a correlation then we will dismiss the dataset and use it to change the current multiplier
            _correlation = _regression.CorrelationCoefficient;
            if (_correlation > -0.2) // TODO this is the best value to test for non-correlation?
            {
                // If the correlation is less that 0 then we will give up controlling the parachutes and throw away the dataset. Also check that we have got several bits of data, just in case we get two datapoints that are badly correlated.
                if (_correlation > 0 && _regression.DataSetSize > 5)
                {
                    ClearData();
                    // Debug.Log("Giving up control of the parachutes as the data does not correlate: " + correlation);
                }
                // Debug.Log("Ignoring the simulation dataset because the correlation is not significant enough: " + correlation);
            }
            else
            {
                // How much data is there? If just one datapoint then we need to slightly vary the multiplier to avoid doing exactly the same multiplier again and getting a divide by zero!. If there is just two then we will not update the multiplier as we can't conclude much from two points of data!
                int dataSetSize = _regression.DataSetSize;
                switch (dataSetSize)
                {
                    case 1:
                        Multiplier *= 0.99999;
                        break;
                    case 2:
                        // Doing nothing
                        break;
                    default:
                        // Use the linear regression to give us a new prediction for when to open the parachutes
                        try
                        {
                            Multiplier = _regression.YIntercept;
                        }
                        catch (Exception)
                        {
                            // If there is not enough data then we expect an exception. However we need to vary the multiplier everso slightly so that we get different data in order to start generating data. This should never happen as we have already checked the size of the dataset.
                            Multiplier *= 0.99999;
                        }

                        break;
                }

                // Impose sensible limits on the multiplier
                if (Multiplier < 1 || double.IsNaN(Multiplier)) { Multiplier = 1; }

                if (Multiplier > _maxMultiplier) { Multiplier = _maxMultiplier; }
            }
        }

        public ParachutePlan(MechJebModuleLandingAutopilot autopliot)
        {
            // Create the linear regression for storing previous prediction results
            _regression = new LinearRegression(DATA_SET_SIZE); // Store the previous however many predictions

            // Take a reference to the landing autopilot module that we are working for.
            _autoPilot = autopliot;

            // Take a note of which body this parachute plan is for. If we go to a different body, we will need a new plan!
            _body = autopliot.Vessel.orbit.referenceBody;
        }

        // Throw away any old data, and create a new empty dataset
        public void ClearData() =>
            // Create the linear regression for storing previous prediction results
            _regression = new LinearRegression(DATA_SET_SIZE); // Stored the previous 20 predictions

        public void StartPlanning()
        {
            // what is the highest point at which we could semi deploy? - look at all the parachutes in the craft, and consider the lowest semi deployment pressure.
            float minSemiDeployPressure = 0;
            float maxFullDeployHeight = 0;
            _parachutePresent = false; // First assume that there are no parachutes.

            // TODO should we check if each of these parachutes is withing the staging limit?
            for (int i = 0; i < _autoPilot.VesselState.Parachutes.Count; i++)
            {
                ModuleParachute p = _autoPilot.VesselState.Parachutes[i];
                if (p.minAirPressureToOpen > minSemiDeployPressure)
                    // Although this is called "minSemiDeployPressure" we want to find the largest value for each of our parachutes. This can be used to calculate the corresponding height, and hence a height at which we can be guarenteed that all our parachutes will deploy if asked to.
                {
                    minSemiDeployPressure = p.minAirPressureToOpen;
                }

                if (p.deployAltitude > maxFullDeployHeight)
                {
                    maxFullDeployHeight = p.deployAltitude;
                }

                _parachutePresent = true;
            }

            // If parachutes are present on the craft then work out the max / min semideployment heights and the starting value.
            if (_parachutePresent)
            {
                // TODO is there benefit in running an initial simulation to calculate the height at which the ratio between vertical and horizontal velocity would be the best for being able to deply the chutes to control the landing site?

                // At what ASL height does the reference body have this pressure? minAirPressureToOpen is in atm, as ModuleParachute compares it.
                _maxSemiDeployHeight = _body.AltitudeForPressure(minSemiDeployPressure * KPA_PER_ATM);

                // We have to have semi deployed by the time we fully deploy.
                _minSemiDeployHeight = maxFullDeployHeight;

                // A thin atmosphere reaches the opening pressure below the deploy altitude, where the chutes open on altitude anyway:
                // the multiplier stays at least 1, as AddResult keeps it.
                _maxMultiplier = Math.Max(1, _maxSemiDeployHeight / _minSemiDeployHeight);

                // Set the inital multiplier to be the mid point.
                Multiplier = Math.Max(1, _maxMultiplier / 2);
            }
        }
    }

    // A class to hold a set of x,y data and perform linear regression analysis on it
    internal class LinearRegression
    {
        private readonly double[] _x;
        private readonly double[] _y;
        private readonly int _maxDataPoints;
        private int _currentDataPoint;
        private double _sumX;
        private double _sumY;
        private double _sumXx;
        private double _sumXY;
        private double _sumYy;

        public LinearRegression(int maxDataPoints)
        {
            _maxDataPoints = maxDataPoints;
            DataSetSize = 0;
            _currentDataPoint = -1;

            _x = new double[maxDataPoints];
            _y = new double[maxDataPoints];
        }

        // Add a new data point. It might be that this will replace an old data point, or it might be that this is the first or second datapoints
        public void Add(double x, double y)
        {
            _currentDataPoint++;
            DataSetSize++;

            // wrap back to the beginning if we have got the end of the array
            if (_currentDataPoint >= _maxDataPoints)
            {
                _currentDataPoint = 0;
            }

            // If we have maxed out the number of data points then we need to remove the old values from the running totals
            if (DataSetSize > _maxDataPoints)
            {
                DataSetSize = _maxDataPoints;
            }

            _x[_currentDataPoint] = x;
            _y[_currentDataPoint] = y;

            // Calculate the new totals
            _sumX = 0;
            _sumY = 0;
            _sumXx = 0;
            _sumXY = 0;
            _sumYy = 0;

            for (int i = 0; i < DataSetSize; i++)
            {
                double thisx = _x[i];
                double thisy = _y[i];

                _sumX += thisx;
                _sumXx += thisx * thisx;
                _sumY += thisy;
                _sumYy += thisy * thisy;
                _sumXY += thisx * thisy;
            }
        }

        private double _slope
        {
            get
            {
                // Require at least half the datapoints in the dataset
                if (DataSetSize < 2)
                {
                    throw new Exception("Not enough data to calculate trend line");
                }

                double result = (_sumXY - _sumX * _sumY / DataSetSize) / (_sumXx - _sumX * _sumX / DataSetSize);

                return result;
            }
        }

        public double YIntercept
        {
            get
            {
                double result = _sumY / DataSetSize - _slope * (_sumX / DataSetSize);

                return result;
            }
        }

        public int DataSetSize { get; private set; }

        // Calculation the Pearson product-moment correlation coefficient
        public double CorrelationCoefficient
        {
            get
            {
                double stdX = Math.Sqrt(_sumXx / DataSetSize - _sumX * _sumX / DataSetSize / DataSetSize);
                double stdY = Math.Sqrt(_sumYy / DataSetSize - _sumY * _sumY / DataSetSize / DataSetSize);
                double covariance = _sumXY / DataSetSize - _sumX * _sumY / DataSetSize / DataSetSize;

                return covariance / stdX / stdY;
            }
        }
    }
}
