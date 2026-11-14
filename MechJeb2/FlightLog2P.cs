using System;
using System.Globalization;
using System.IO;
using System.Text;
using MuMech.Landing;
using UnityEngine;

namespace MuMech
{
    /// <summary>
    /// MechJeb2P flight log: one CSV file per landing under &lt;KSP&gt;/Logs/MechJeb2P/, with a telemetry row several times a
    /// second (every physics tick during the boostback burn and the precision descent) and an event row for each step change,
    /// part option change, focus change and the reason the landing ended. The settings in force are written as comment lines
    /// at the top. Events are mirrored to KSP.log with a [MechJeb2P] prefix.
    /// </summary>
    public class FlightLog2P
    {
        private readonly MechJebModuleLandingAutopilot _landing;
        private StreamWriter _w;
        private double _lastSample = double.NegativeInfinity;
        private double _lastFlush = double.NegativeInfinity;
        private double _startUT;
        private string _lastStep = "";
        private bool? _lastFocused;
        private int _lastParts = -1;
        private readonly StringBuilder _sb = new StringBuilder(1024);

        public FlightLog2P(MechJebModuleLandingAutopilot landing) => _landing = landing;

        public string Path { get; private set; } = "";
        public string FileName => string.IsNullOrEmpty(Path) ? "" : System.IO.Path.GetFileName(Path);
        public bool Open => _w != null;

        public static string Directory => System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "Logs", "MechJeb2P");

        private MechJebCore Core => _landing.Core;
        private VesselState VS => _landing.VesselState;
        private Vessel Vessel => _landing.Vessel;

        private static string MechJebCoreVersion()
        {
            try
            {
                var attributes = Attribute.GetCustomAttributes(typeof(MechJebCore).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                return attributes.Length > 0 ? ((System.Reflection.AssemblyInformationalVersionAttribute)attributes[0]).InformationalVersion : "?";
            }
            catch { return "?"; }
        }

        private static string F(double v, string fmt = "F2") => double.IsNaN(v) || double.IsInfinity(v) ? "" : v.ToString(fmt, CultureInfo.InvariantCulture);

        private static string Q(string s) => "\"" + (s ?? "").Replace("\"", "'").Replace("\n", " | ") + "\"";

        public void Begin(string how)
        {
            End("restarted");
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                string vesselName = Vessel != null ? Vessel.vesselName : "vessel";
                foreach (char c in System.IO.Path.GetInvalidFileNameChars()) vesselName = vesselName.Replace(c, '_');
                vesselName = vesselName.Replace(' ', '_');
                Path = System.IO.Path.Combine(Directory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_" + vesselName + ".csv");
                _w = new StreamWriter(Path, false, new UTF8Encoding(false));
                _startUT = VS.Time;
                _lastStep = "";
                _lastFocused = null;
                _lastParts = -1;

                WriteSettings(how);
                _w.WriteLine(Header());
                Event("log started: " + how);
                Debug.Log("[MechJeb2P] flight log: " + Path);
                ScreenMessages.PostScreenMessage("MechJeb2P: logging to Logs/MechJeb2P/" + FileName, 5f, ScreenMessageStyle.UPPER_LEFT);
            }
            catch (Exception e)
            {
                Debug.LogError("[MechJeb2P] flight log could not be opened: " + e.Message);
                _w = null;
                Path = "";
            }
        }

        private void WriteSettings(string how)
        {
            _w.WriteLine("# MechJeb2P flight log, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ", " + how);
            _w.WriteLine("# MechJeb " + MechJebCoreVersion() + ", KSP " + Versioning.version_major + "." + Versioning.version_minor + "." + Versioning.Revision);
            _w.WriteLine("# vessel: " + Vessel.vesselName + ", parts " + Vessel.parts.Count + ", mass " + F(VS.Mass, "F1") + " t, body " + _landing.MainBody.bodyName);
            _w.WriteLine("# target: lat " + F(Core.Target.targetLatitude, "F5") + " lon " + F(Core.Target.targetLongitude, "F5") +
                         " terrain " + F(_landing.MainBody.TerrainAltitude(Core.Target.targetLatitude, Core.Target.targetLongitude), "F1") + " m ASL" +
                         ", target object: " + (Core.Target.Target != null ? Core.Target.Target.GetName() + " (" + Core.Target.Target.GetType().Name + ")" : "none"));
            MechJebModuleLandingAutopilot l = _landing;
            _w.WriteLine("# landing: LandAtTarget " + l.LandAtTarget + ", PrecisionMode " + l.PrecisionMode + ", TouchdownSpeed " + F(l.TouchdownSpeed) +
                         ", DeployGears " + l.DeployGears + ", DeployChutes " + l.DeployChutes + ", RCSAdjustment " + l.RCSAdjustment +
                         ", Autowarp " + Core.Node.Autowarp);
            _w.WriteLine("# precision: CatchAltitudeASL " + F(l.CatchAltitudeASL) + ", TerminalHeight " + F(l.TerminalHeight) + ", MaxDescentSpeed " + F(l.MaxDescentSpeed) +
                         ", MaxHorizontalSpeed " + F(l.MaxHorizontalSpeed) + ", MaxTilt " + F(l.MaxTilt) + ", CaptureSpeed " + F(l.CaptureSpeed) +
                         ", HoverAtCatch " + l.HoverAtCatch + ", HoverTimeLimit " + F(l.HoverTimeLimit) + ", HorizontalTolerance " + F(l.HorizontalTolerance) +
                         ", HoldHeading " + l.HoldHeading + ", CatchHeading " + F(l.CatchHeading) + ", TerminalThrustFraction " + F(l.TerminalThrustFraction));
            _w.WriteLine("# entry: AeroSteering " + l.AeroSteering + ", MaxAoA " + F(l.MaxAoA) + ", BrakingMaxTilt " + F(l.BrakingMaxTilt) +
                         ", BoostbackFlipThrottle " + F(l.BoostbackFlipThrottle) + ", BoostbackMaxDrag " + F(l.BoostbackMaxDrag) + ", BoostbackDoneMiss " + F(l.BoostbackDoneMiss));
            _w.WriteLine("# remote: RemoteControl " + l.RemoteControl + ", RemoteRangeKm " + F(l.RemoteRangeKm) + ", RemoteBlockWarp " + l.RemoteBlockWarp);
            MechJebModuleLandingPartControl p = Core.LandingParts;
            if (p != null)
            {
                _w.WriteLine("# parts: enabled " + p.PartControlEnabled + ", restore " + p.RestoreAfterLanding);
                _w.WriteLine("#   entry: " + Describe(p.Entry));
                _w.WriteLine("#   braking: " + Describe(p.Braking));
                _w.WriteLine("#   terminal: " + Describe(p.Terminal));
            }

            _w.WriteLine("# engines: " + Engines());
            _w.WriteLine("# thrust: max " + F(VS.ThrustVectorMaxThrottle.magnitude, "F0") + " kN, min " + F(VS.ThrustVectorMinThrottle.magnitude, "F0") +
                         " kN, aMax " + F(VS.ThrustVectorMaxThrottle.magnitude / VS.Mass) + " m/s2, limited aMax " + F(VS.LimitedMaxThrustAcceleration) +
                         " m/s2, g " + F(VS.LocalGravity) + ", UseAtmosphereToBrake " + l.UseAtmosphereToBrake() + ", DecelerationEndAltitude " + F(l.DecelerationEndAltitude(), "F0"));
            _w.WriteLine("# columns: kind T=telemetry E=event; h_catch = lowest point above the catch altitude; err_n/err_e = catch point minus lowest point, north/east;" +
                         " v_z up positive; tilt_cmd/tilt_act = angle of commanded/actual thrust from vertical; aim_err = attitude error; pred_miss = predicted landing site to target");
        }

        private static string Describe(LandingPhaseRules r) =>
            "fins P/Y/R/deploy " + r.FinsPitch + "/" + r.FinsYaw + "/" + r.FinsRoll + "/" + r.FinsDeploy + ", gimbal P/Y/R/lock " + r.GimbalPitch + "/" + r.GimbalYaw + "/" +
            r.GimbalRoll + "/" + r.GimbalLock + ", RCS " + r.Rcs + ", SAS " + r.Sas + ", AG " + r.ActionGroup + ", engine mode '" + r.EngineMode + "'";

        private string Engines()
        {
            _sb.Length = 0;
            int on = 0, total = 0;
            for (int i = 0; i < Vessel.parts.Count; i++)
            {
                Part p = Vessel.parts[i];
                for (int j = 0; j < p.Modules.Count; j++)
                {
                    if (!(p.Modules[j] is ModuleEngines e)) continue;
                    total++;
                    bool running = e.EngineIgnited && e.isEnabled && !e.getFlameoutState;
                    if (running) on++;
                    if (_sb.Length < 600)
                        _sb.Append(e.engineID).Append(running ? "=on(" : "=off(").Append(F(e.maxThrust, "F0")).Append("kN) ");
                }
            }

            return on + "/" + total + " running: " + _sb;
        }

        private string EnginesShort()
        {
            _sb.Length = 0;
            for (int i = 0; i < Vessel.parts.Count; i++)
            {
                Part p = Vessel.parts[i];
                for (int j = 0; j < p.Modules.Count; j++)
                {
                    if (!(p.Modules[j] is ModuleEngines e)) continue;
                    if (e.EngineIgnited && e.isEnabled && !e.getFlameoutState)
                    {
                        if (_sb.Length > 0) _sb.Append('+');
                        _sb.Append(e.engineID);
                    }
                }
            }

            return _sb.ToString();
        }

        private static string Header() =>
            "ut,met,kind,step,mode,event,focused,loaded,packed,situation,parts,mass_t," +
            "alt_asl,alt_bottom,h_catch,dist_catch,err_n,err_e,v_z,v_h,v_h_n,v_h_e,speed_surf," +
            "a_max,a_min,a_cmd,a_z_cmd,a_h_cmd,v_z_des,v_h_des,tilt_cmd,tilt_act,aim_err,heading_top," +
            "throttle_cmd,throttle_act,thrust_accel_act,pitch_in,yaw_in,roll_in,drag_accel,g," +
            "pred_ready,pred_miss,course_dv,decel_end_alt,max_allowed_speed,rcs,sas,warp,dt,engines,status";

        /// <summary>A telemetry row, rate-limited unless the step asks for every tick.</summary>
        public void Sample(FlightCtrlState s)
        {
            if (_w == null) return;
            try
            {
                AutopilotStep step = _landing.CurrentStep;
                string stepName = step?.GetType().Name ?? "none";
                if (stepName != _lastStep)
                {
                    Event("step " + (_lastStep == "" ? "start" : _lastStep) + " -> " + stepName);
                    _lastStep = stepName;
                }

                bool focused = Vessel.isActiveVessel;
                if (_lastFocused != focused)
                {
                    if (_lastFocused != null) Event(focused ? "vessel focused" : "vessel unfocused (player on " + (FlightGlobals.ActiveVessel?.vesselName ?? "?") + ")");
                    _lastFocused = focused;
                }

                int parts = Vessel.parts.Count;
                if (_lastParts >= 0 && parts < _lastParts) Event("lost " + (_lastParts - parts) + " part(s)");
                _lastParts = parts;

                bool everyTick = step is PrecisionDescent || (step is Boostback bb && bb.Burning);
                double interval = everyTick ? 0 : 0.2;
                if (VS.Time - _lastSample < interval) return;
                _lastSample = VS.Time;

                WriteRow("T", "", s);

                if (VS.Time - _lastFlush > 2)
                {
                    _lastFlush = VS.Time;
                    _w.Flush();
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[MechJeb2P] flight log sample failed: " + e);
            }
        }

        public void Event(string text)
        {
            Debug.Log("[MechJeb2P] " + (Vessel != null ? Vessel.vesselName + ": " : "") + text);
            if (_w == null) return;
            try
            {
                WriteRow("E", text, Vessel?.ctrlState);
                _w.Flush();
            }
            catch (Exception e)
            {
                Debug.LogError("[MechJeb2P] flight log event failed: " + e);
            }
        }

        private void WriteRow(string kind, string ev, FlightCtrlState s)
        {
            MechJebModuleLandingAutopilot l = _landing;
            AutopilotStep step = l.CurrentStep;
            Vector3d up = VS.Up;
            Vector3d v = VS.SurfaceVelocity;
            Vector3d vH = Vector3d.Exclude(up, v);
            double vZ = Vector3d.Dot(v, up);

            double hCatch = double.NaN, dist = double.NaN, errN = double.NaN, errE = double.NaN;
            if (l.LandAtTarget)
            {
                Vector3d bottom = VS.CoM - l.BottomOffset * up;
                Vector3d err = l.CatchPointWorld() - bottom;
                hCatch = -Vector3d.Dot(err, up);
                Vector3d errH = Vector3d.Exclude(up, err);
                dist = errH.magnitude;
                errN = Vector3d.Dot(errH, VS.North);
                errE = Vector3d.Dot(errH, VS.East);
            }

            string mode = "";
            double aCmd = double.NaN, aZ = double.NaN, aH = double.NaN, vZDes = double.NaN, vHDes = double.NaN, tiltCmd = double.NaN, throttleCmd = double.NaN;
            double courseDv = double.NaN, predMiss = double.NaN;
            switch (step)
            {
                case PrecisionDescent pd:
                    mode = pd.ModeName;
                    aCmd = pd.AccelCommand;
                    aZ = pd.VerticalAccelCommand;
                    aH = pd.HorizontalAccelCommand;
                    vZDes = pd.DesiredVerticalSpeed;
                    vHDes = pd.DesiredHorizontalSpeed;
                    tiltCmd = pd.TiltCommandDeg;
                    throttleCmd = pd.ThrottleCommand;
                    break;
                case Boostback bb:
                    mode = bb.Burning ? "burn" : "flip";
                    courseDv = bb.Dv;
                    predMiss = bb.PredictedMiss;
                    throttleCmd = Core.Thrust.TargetThrottle;
                    break;
                default:
                    throttleCmd = Core.Thrust.TargetThrottle;
                    break;
            }

            bool predReady = l.PredictionReady;
            if (predReady && l.LandAtTarget && double.IsNaN(predMiss))
            {
                try { predMiss = Vector3d.Distance(Core.Target.GetPositionTargetPosition(), l.LandingSite); }
                catch { predMiss = double.NaN; }
            }

            double tiltAct = Vector3d.Angle(VS.ThrustForward.sqrMagnitude > 0 ? VS.ThrustForward : VS.Forward, up);
            double maxAllowed = double.NaN;
            if (l.DescentSpeedPolicy != null)
            {
                try { maxAllowed = l.DescentSpeedPolicy.MaxAllowedSpeed(VS.CoM - l.MainBody.position, v); }
                catch { maxAllowed = double.NaN; }
            }

            double thrustAct = VS.ThrustVectorLastFrame.magnitude / VS.Mass;
            double throttleAct = s != null ? s.mainThrottle : double.NaN;
            double aMax = VS.ThrustVectorMaxThrottle.magnitude / VS.Mass;
            double aMin = VS.ThrustVectorMinThrottle.magnitude / VS.Mass;
            double warp = TimeWarp.CurrentRate;

            _sb.Length = 0;
            _sb.Append(F(VS.Time, "F3")).Append(',').Append(F(VS.Time - _startUT, "F3")).Append(',').Append(kind).Append(',')
                .Append(step?.GetType().Name ?? "none").Append(',').Append(mode).Append(',').Append(Q(ev)).Append(',')
                .Append(Vessel.isActiveVessel ? 1 : 0).Append(',').Append(Vessel.loaded ? 1 : 0).Append(',').Append(Vessel.packed ? 1 : 0).Append(',')
                .Append(Vessel.situation).Append(',').Append(Vessel.parts.Count).Append(',').Append(F(VS.Mass, "F2")).Append(',')
                .Append(F(VS.AltitudeASL, "F1")).Append(',').Append(F(VS.AltitudeBottom, "F1")).Append(',').Append(F(hCatch)).Append(',').Append(F(dist)).Append(',')
                .Append(F(errN)).Append(',').Append(F(errE)).Append(',')
                .Append(F(vZ)).Append(',').Append(F(vH.magnitude)).Append(',').Append(F(Vector3d.Dot(vH, VS.North))).Append(',').Append(F(Vector3d.Dot(vH, VS.East))).Append(',')
                .Append(F(VS.SpeedSurface)).Append(',')
                .Append(F(aMax)).Append(',').Append(F(aMin)).Append(',').Append(F(aCmd)).Append(',').Append(F(aZ)).Append(',').Append(F(aH)).Append(',')
                .Append(F(vZDes)).Append(',').Append(F(vHDes)).Append(',').Append(F(tiltCmd, "F1")).Append(',').Append(F(tiltAct, "F1")).Append(',')
                .Append(F(Core.Attitude.AttitudeAngleFromTarget(), "F1")).Append(',').Append(F(l.CurrentTopHeading(), "F1")).Append(',')
                .Append(F(throttleCmd, "F3")).Append(',').Append(F(throttleAct, "F3")).Append(',').Append(F(thrustAct)).Append(',')
                .Append(s != null ? F(s.pitch, "F2") : "").Append(',').Append(s != null ? F(s.yaw, "F2") : "").Append(',').Append(s != null ? F(s.roll, "F2") : "").Append(',')
                .Append(F(VS.DragAcceleration, "F3")).Append(',').Append(F(VS.LocalGravity, "F3")).Append(',')
                .Append(predReady ? 1 : 0).Append(',').Append(F(predMiss, "F0")).Append(',').Append(F(courseDv, "F1")).Append(',')
                .Append(F(l.DecelerationEndAltitude(), "F0")).Append(',').Append(F(maxAllowed, "F1")).Append(',')
                .Append(Vessel.ActionGroups[KSPActionGroup.RCS] ? 1 : 0).Append(',').Append(Vessel.ActionGroups[KSPActionGroup.SAS] ? 1 : 0).Append(',')
                .Append(F(warp, "F0")).Append(',').Append(F(TimeWarp.fixedDeltaTime, "F3")).Append(',')
                .Append(EnginesShort()).Append(',').Append(Q(step?.Status ?? ""));
            _w.WriteLine(_sb.ToString());
        }

        public void End(string reason)
        {
            if (_w == null) return;
            try
            {
                Event("log ended: " + reason);
                _w.Flush();
                _w.Dispose();
                Debug.Log("[MechJeb2P] flight log closed: " + Path);
            }
            catch (Exception e)
            {
                Debug.LogError("[MechJeb2P] flight log close failed: " + e.Message);
            }

            _w = null;
        }
    }
}
