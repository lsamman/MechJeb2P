extern alias JetBrainsAnnotations;
using System;
using System.Collections.Generic;
using JetBrainsAnnotations::JetBrains.Annotations;
using KSP.Localization;
using UnityEngine;
using static MechJebLib.Utils.Statics;

namespace MuMech
{
    /// <summary>
    /// MechJeb2P: lets a landing autopilot keep flying a vessel the player is not focused on, so the player can switch to the
    /// tower, work its arms and watch the catch.
    ///
    /// KSP feeds the fly-by-wire callbacks (MechJeb's steering and throttle) to every loaded vessel that is not packed, so
    /// MechJeb already flies an unfocused vessel: what stops it is KSP unloading a vessel farther than 2.25 km from the one in
    /// focus, or packing it on rails. While a landing with RemoteControl runs, this module widens the vessel's own load and
    /// pack ranges (KSP reads the ranges of the vessel it decides about), and the target vessel's, to RemoteRangeKm: the
    /// booster stays in physics from the tower and the tower loads from the booster, so the focus switch is a plain one with
    /// no scene change. The stock ranges are put back when the landing ends.
    ///
    /// It also drops a rails time warp the player starts from the other vessel while this one flies, since on rails the
    /// vessel would fall ballistically with its engines frozen; and it lists the vessels flying unfocused for the remote window.
    /// </summary>
    [UsedImplicitly]
    public class MechJebModuleRemoteControl : ComputerModule
    {
        public MechJebModuleRemoteControl(MechJebCore core) : base(core)
        {
            Priority = 90;
        }

        /// <summary>The cores whose landing autopilot flies with remote control on, for the remote window.</summary>
        public static readonly List<MechJebCore> Flying = new List<MechJebCore>();

        private readonly Dictionary<Vessel, VesselRanges> _stockRanges = new Dictionary<Vessel, VesselRanges>();
        private double _lastRangeKm = -1;
        private double _lastScan = double.NegativeInfinity;
        private float _lastWarpMessage = float.NegativeInfinity;
        private bool _wasFocused = true;

        /// <summary>Grounded vessels this close (m) to the target coordinates are kept loadable too: the tower, the pad.</summary>
        private const double GROUND_VESSEL_RADIUS = 20000;

        public bool Active { get; private set; }

        public override void OnStart(PartModule.StartState state) => Users.Add(this); // runs for the whole flight, like the target controller

        private bool Wanted => Core.Landing != null && Core.Landing.Enabled && Core.Landing.RemoteControl && HighLogic.LoadedSceneIsFlight;

        public override void OnFixedUpdate()
        {
            bool wanted = Wanted;

            if (wanted)
            {
                if (!Active)
                {
                    Active = true;
                    if (!Flying.Contains(Core)) Flying.Add(Core);
                    _wasFocused = Vessel.isActiveVessel;
                    Core.Landing.Log?.Event("remote control on: load range " + Math.Max(5, Core.Landing.RemoteRangeKm).ToString("F0") + " km");
                }

                bool focused = Vessel.isActiveVessel;
                if (focused != _wasFocused)
                {
                    _wasFocused = focused;
                    Core.Landing.Log?.Event(focused ? "remote: vessel focused again" : "remote: flying unfocused, player on " + (FlightGlobals.ActiveVessel?.vesselName ?? "?") +
                                                                                      (Vessel.packed ? " (PACKED: no physics!)" : ""));
                }

                KeepLoaded();
                GuardWarp();
            }
            else if (Active)
            {
                Release();
            }
        }

        private void KeepLoaded()
        {
            double rangeKm = Math.Max(5, Core.Landing.RemoteRangeKm);
            bool changed = Math.Abs(rangeKm - _lastRangeKm) > 1e-6;
            _lastRangeKm = rangeKm;

            Extend(Vessel, rangeKm, changed);

            Vessel target = Core.Target.Target?.GetVessel();
            if (target != null && target != Vessel && target.mainBody == Vessel.mainBody)
                Extend(target, rangeKm, changed);

            // the grounded vessels near the target coordinates (the tower when the target is a point on the pad, not the tower
            // itself): scanned once a second, so a tower loaded later is caught too
            if (changed || VesselState.Time - _lastScan > 1)
            {
                _lastScan = VesselState.Time;
                if (Core.Target.SurfaceTargetExists(Vessel.mainBody))
                {
                    Vector3d targetPos = Vessel.mainBody.GetWorldSurfacePosition(Core.Target.targetLatitude, Core.Target.targetLongitude, 0);
                    for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
                    {
                        Vessel v = FlightGlobals.Vessels[i];
                        if (v == null || v == Vessel || v == target || v.mainBody != Vessel.mainBody) continue;
                        if (v.situation != Vessel.Situations.LANDED && v.situation != Vessel.Situations.PRELAUNCH && v.situation != Vessel.Situations.SPLASHED) continue;
                        if (v.vesselType == VesselType.Debris || v.vesselType == VesselType.Flag || v.vesselType == VesselType.EVA) continue;
                        Vector3d pos = v.mainBody.GetWorldSurfacePosition(v.latitude, v.longitude, 0);
                        if (Vector3d.Distance(pos, targetPos) > GROUND_VESSEL_RADIUS) continue;
                        Extend(v, rangeKm, changed);
                    }
                }
            }
        }

        // KSP builds a vessel's ranges once from PhysicsGlobals when it is made: the object is replaced by one of ours and the stock
        // one kept to give back. A range the player edits mid-landing is applied to the same vessels.
        private void Extend(Vessel v, double rangeKm, bool changed)
        {
            if (v == null || v.vesselRanges == null) return;

            if (!_stockRanges.ContainsKey(v))
            {
                _stockRanges[v] = new VesselRanges(v.vesselRanges);
                changed = true;
            }
            else if (!changed && v.vesselRanges.flying.load < (float)(rangeKm * 1000) - 1)
            {
                // something (another mod) rewrote the ranges: ours go back on
                changed = true;
            }

            if (!changed) return;

            float load = (float)(rangeKm * 1000);
            float unload = load * 1.1f;
            var r = new VesselRanges(v.vesselRanges);

            // in the air: never on rails while it is loaded (pack beyond the unload range, unpack as soon as loaded)
            r.flying = new VesselRanges.Situation(load, unload, unload * 1.05f, load);
            r.subOrbital = new VesselRanges.Situation(load, unload, unload * 1.05f, load);
            r.escaping = new VesselRanges.Situation(load, unload, unload * 1.05f, load);
            r.orbit = new VesselRanges.Situation(load, unload, unload * 1.05f, load);

            // on the ground (the tower): loaded from far, off rails well before the booster arrives
            r.landed = new VesselRanges.Situation(load, unload, Math.Max(r.landed.pack, 3000f), Math.Max(r.landed.unpack, 2500f));
            r.splashed = new VesselRanges.Situation(load, unload, Math.Max(r.splashed.pack, 3000f), Math.Max(r.splashed.unpack, 2500f));
            r.prelaunch = new VesselRanges.Situation(load, unload, Math.Max(r.prelaunch.pack, 3000f), Math.Max(r.prelaunch.unpack, 2500f));

            v.vesselRanges = r;
        }

        private void GuardWarp()
        {
            if (!Core.Landing.RemoteBlockWarp || Vessel.isActiveVessel) return;
            if (Vessel.LandedOrSplashed) return;

            // a rails warp always (the vessel would fall ballistically with its engines frozen); any warp while the boostback
            // burns or the precision descent flies, where the control loops want real physics ticks
            AutopilotStep step = Core.Landing.CurrentStep;
            bool critical = step is Landing.PrecisionDescent || (step is Landing.Boostback bb && bb.Burning);
            bool rails = TimeWarp.WarpMode == TimeWarp.Modes.HIGH && TimeWarp.CurrentRateIndex > 0;
            bool physicsWarp = TimeWarp.WarpMode == TimeWarp.Modes.LOW && TimeWarp.CurrentRateIndex > 0;

            if (rails || (physicsWarp && critical))
            {
                Core.Landing.Log?.Event("remote: dropped a " + (rails ? "rails" : "physics") + " warp of x" + TimeWarp.CurrentRate.ToString("F0"));
                TimeWarp.SetRate(0, true);
                if (Time.realtimeSinceStartup - _lastWarpMessage > 5f)
                {
                    _lastWarpMessage = Time.realtimeSinceStartup;
                    ScreenMessages.PostScreenMessage(Localizer.Format("#MechJeb_LG2P_WarpBlocked", Vessel.vesselName), 4f, ScreenMessageStyle.UPPER_CENTER); //MechJeb2P: no time warp while <<1>> is flying its landing
                }
            }
        }

        private void Release()
        {
            Active = false;
            Flying.Remove(Core);
            _lastRangeKm = -1;
            _lastScan = double.NegativeInfinity;
            Core.Landing?.Log?.Event("remote control off: stock vessel ranges restored on " + _stockRanges.Count + " vessel(s)");

            foreach (KeyValuePair<Vessel, VesselRanges> kv in _stockRanges)
            {
                Vessel v = kv.Key;
                if (v != null && v.vesselRanges != null)
                    v.vesselRanges = kv.Value;
            }

            _stockRanges.Clear();
        }

        protected override void OnModuleDisabled()
        {
            if (Active) Release();
        }

        public override void OnDestroy()
        {
            // the scene is going: the vessel objects go with it, nothing to give back
            Flying.Remove(Core);
            _stockRanges.Clear();
            Active = false;
        }
    }

    /// <summary>
    /// The MechJeb2P remote window: while a MechJeb landing autopilot flies a vessel other than the one in focus, a small
    /// IMGUI window lists those vessels with their status, distance and height, and lets the player abort a landing or switch
    /// to the vessel. It is a scene object, not a MechJeb window, so it shows on a vessel with no MechJeb (the tower).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    [UsedImplicitly]
    public class MechJeb2PRemoteWindow : MonoBehaviour
    {
        private static Rect _windowPos = new Rect(80, 120, 340, 100);
        private const string LOCK_ID = "MechJeb2P_remote_noclick";
        private bool _locked;
        private readonly int _windowId = "MechJeb2PRemoteWindow".GetHashCode();

        private static bool AnyRemote()
        {
            for (int i = 0; i < MechJebModuleRemoteControl.Flying.Count; i++)
            {
                MechJebCore core = MechJebModuleRemoteControl.Flying[i];
                if (core != null && core.vessel != null && !core.vessel.isActiveVessel && core.Landing != null && core.Landing.Enabled)
                    return true;
            }

            return false;
        }

        private void OnGUI()
        {
            if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready || MapView.MapIsEnabled) { Unlock(); return; }
            if (!AnyRemote()) { Unlock(); return; }

            GUISkin previous = GUI.skin;
            if (GuiUtils.Skin != null) GUI.skin = GuiUtils.Skin;

            _windowPos = GUILayout.Window(_windowId, _windowPos, Window, Localizer.Format("#MechJeb_LG2P_RemoteTitle"), GUILayout.Width(340)); //MechJeb2P remote landings

            GUI.skin = previous;

            bool over = _windowPos.Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y));
            if (over && !_locked)
            {
                InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS & ~ControlTypes.KEYBOARDINPUT, LOCK_ID);
                _locked = true;
            }
            else if (!over && _locked)
            {
                Unlock();
            }
        }

        private void Unlock()
        {
            if (!_locked) return;
            InputLockManager.RemoveControlLock(LOCK_ID);
            _locked = false;
        }

        private void OnDestroy() => Unlock();

        private void Window(int id)
        {
            Vessel active = FlightGlobals.ActiveVessel;

            for (int i = 0; i < MechJebModuleRemoteControl.Flying.Count; i++)
            {
                MechJebCore core = MechJebModuleRemoteControl.Flying[i];
                if (core == null || core.vessel == null || core.vessel.isActiveVessel || core.Landing == null || !core.Landing.Enabled) continue;

                Vessel v = core.vessel;
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label("<b>" + v.vesselName + "</b>");
                GUILayout.Label(core.Landing.Status ?? "");

                double distance = active != null ? Vector3d.Distance(active.CoMD, v.CoMD) : 0;
                string line = Localizer.Format("#MechJeb_LG2P_RemoteLine", distance.ToSI() + "m", v.altitude.ToSI() + "m",
                    v.srfSpeed.ToString("F1")); //<<1>> away, <<2>> ASL, <<3>> m/s
                if (core.Landing.PrecisionActive)
                    line += "  " + Localizer.Format("#MechJeb_LG2P_RemoteCatch", core.Landing.HeightAboveCatch.ToString("F0")); //<<1>> m above catch
                GUILayout.Label(line);

                GUILayout.BeginHorizontal();
                if (GUILayout.Button(Localizer.Format("#MechJeb_LandingGuidance_button3"))) //Abort autoland
                    core.Landing.StopLanding();
                if (!v.packed && GUILayout.Button(Localizer.Format("#MechJeb_LG2P_RemoteSwitch"))) //Switch to
                    FlightGlobals.ForceSetActiveVessel(v);
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }

            GUI.DragWindow();
        }
    }
}
