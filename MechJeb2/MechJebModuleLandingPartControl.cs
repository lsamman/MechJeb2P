extern alias JetBrainsAnnotations;
using System;
using System.Collections.Generic;
using System.Reflection;
using JetBrainsAnnotations::JetBrains.Annotations;
using UnityEngine;

namespace MuMech
{
    /// <summary>What a landing phase does to a part option: leave it as the player set it, switch it on, or switch it off.</summary>
    public enum PartSwitch
    {
        Leave,
        On,
        Off
    }

    /// <summary>The phases of a landing that the part options follow. A step of the landing autopilot maps to one of these.</summary>
    public enum LandingPhase
    {
        None,

        /// <summary>From the deorbit burn through the coast and the atmospheric entry, until the braking burn.</summary>
        Entry,

        /// <summary>The braking burn and the standard final descent.</summary>
        Braking,

        /// <summary>The precision terminal descent (MechJeb2P).</summary>
        Terminal
    }

    /// <summary>
    /// The part options one landing phase sets when it starts. Persisted as a sub-node of the part control module, so every
    /// field is a plain value KSP's ConfigNode can write.
    /// </summary>
    public class LandingPhaseRules
    {
        [Persistent] public PartSwitch FinsPitch = PartSwitch.Leave;
        [Persistent] public PartSwitch FinsYaw = PartSwitch.Leave;
        [Persistent] public PartSwitch FinsRoll = PartSwitch.Leave;
        [Persistent] public PartSwitch FinsDeploy = PartSwitch.Leave;
        [Persistent] public PartSwitch GimbalPitch = PartSwitch.Leave;
        [Persistent] public PartSwitch GimbalYaw = PartSwitch.Leave;
        [Persistent] public PartSwitch GimbalRoll = PartSwitch.Leave;
        [Persistent] public PartSwitch GimbalLock = PartSwitch.Leave;
        [Persistent] public PartSwitch Rcs = PartSwitch.Leave;
        [Persistent] public PartSwitch Sas = PartSwitch.Leave;

        /// <summary>A custom action group (1-10) fired once when the phase starts; 0 fires none.</summary>
        [Persistent] public int ActionGroup;

        /// <summary>
        /// An engine mode to select when the phase starts: the engineID of a Starship Expansion Project engine cluster mode
        /// (OuterTwenty, MiddleEight, MiddleTwo, CenterThree...) or of a stock multi-mode engine. Empty leaves the engines.
        /// </summary>
        [Persistent] public string EngineMode = "";

        public bool Any =>
            FinsPitch != PartSwitch.Leave || FinsYaw != PartSwitch.Leave || FinsRoll != PartSwitch.Leave || FinsDeploy != PartSwitch.Leave ||
            GimbalPitch != PartSwitch.Leave || GimbalYaw != PartSwitch.Leave || GimbalRoll != PartSwitch.Leave || GimbalLock != PartSwitch.Leave ||
            Rcs != PartSwitch.Leave || Sas != PartSwitch.Leave || ActionGroup != 0 || !string.IsNullOrEmpty(EngineMode);

        public void Clear()
        {
            FinsPitch = FinsYaw = FinsRoll = FinsDeploy = PartSwitch.Leave;
            GimbalPitch = GimbalYaw = GimbalRoll = GimbalLock = PartSwitch.Leave;
            Rcs = Sas = PartSwitch.Leave;
            ActionGroup = 0;
            EngineMode = "";
        }

        public void CopyFrom(LandingPhaseRules o)
        {
            FinsPitch = o.FinsPitch;
            FinsYaw = o.FinsYaw;
            FinsRoll = o.FinsRoll;
            FinsDeploy = o.FinsDeploy;
            GimbalPitch = o.GimbalPitch;
            GimbalYaw = o.GimbalYaw;
            GimbalRoll = o.GimbalRoll;
            GimbalLock = o.GimbalLock;
            Rcs = o.Rcs;
            Sas = o.Sas;
            ActionGroup = o.ActionGroup;
            EngineMode = o.EngineMode;
        }
    }

    /// <summary>
    /// MechJeb2P: lets the landing autopilot change part options as the landing goes through its phases, the way a pilot
    /// would click them in the part action windows: the pitch/yaw/roll authority and deployment of control surfaces (grid
    /// fins, flaps), the gimbal axes and lock of the engines, RCS and SAS, a custom action group, and the engine mode of a
    /// multi-mode engine or a Starship Expansion Project engine cluster.
    ///
    /// The values the parts had before the landing are remembered and, when RestoreAfterLanding is set, put back when the
    /// landing ends (landed, caught or aborted).
    /// </summary>
    [UsedImplicitly]
    public class MechJebModuleLandingPartControl : ComputerModule
    {
        public MechJebModuleLandingPartControl(MechJebCore core) : base(core) { }

        private const int PASSES = (int)(Pass.LOCAL | Pass.TYPE | Pass.GLOBAL);

        [Persistent(pass = PASSES)] public bool PartControlEnabled = true;

        [Persistent(pass = PASSES)] public bool RestoreAfterLanding = true;

        [Persistent(pass = PASSES)] public LandingPhaseRules Entry = new LandingPhaseRules();
        [Persistent(pass = PASSES)] public LandingPhaseRules Braking = new LandingPhaseRules();
        [Persistent(pass = PASSES)] public LandingPhaseRules Terminal = new LandingPhaseRules();

        public LandingPhaseRules Rules(LandingPhase phase)
        {
            switch (phase)
            {
                case LandingPhase.Entry:    return Entry;
                case LandingPhase.Braking:  return Braking;
                case LandingPhase.Terminal: return Terminal;
                default:                    return null;
            }
        }

        public LandingPhase AppliedPhase { get; private set; } = LandingPhase.None;

        /// <summary>A short account of what the last phase changed, for the window.</summary>
        public string LastApplied { get; private set; } = "";

        // ---- snapshot of the options before the landing ----

        private class ControlSurfaceState
        {
            public bool IgnorePitch, IgnoreYaw, IgnoreRoll, Deploy;
        }

        private class GimbalState
        {
            public bool EnablePitch, EnableYaw, EnableRoll, Lock;
        }

        private readonly Dictionary<PartModule, ControlSurfaceState> _surfaceBefore = new Dictionary<PartModule, ControlSurfaceState>();
        private readonly Dictionary<ModuleGimbal, GimbalState> _gimbalBefore = new Dictionary<ModuleGimbal, GimbalState>();
        private bool? _rcsBefore, _sasBefore;
        private bool _snapshotTaken;

        /// <summary>Call when a landing begins: the options are remembered from here.</summary>
        public void BeginLanding()
        {
            ForgetSnapshot();
            AppliedPhase = LandingPhase.None;
            LastApplied = "";
        }

        /// <summary>Call when a landing ends: the remembered options go back on the parts if RestoreAfterLanding is set.</summary>
        public void EndLanding()
        {
            if (RestoreAfterLanding && _snapshotTaken)
            {
                try
                {
                    Restore();
                }
                catch (Exception e)
                {
                    Debug.LogError("[MechJeb2P] part control: restoring the part options threw: " + e);
                }
            }

            ForgetSnapshot();
            AppliedPhase = LandingPhase.None;
        }

        private void ForgetSnapshot()
        {
            _surfaceBefore.Clear();
            _gimbalBefore.Clear();
            _rcsBefore = _sasBefore = null;
            _snapshotTaken = false;
        }

        /// <summary>Applies the rules of a phase once, when the autopilot enters it.</summary>
        public void ApplyPhase(LandingPhase phase)
        {
            if (phase == AppliedPhase) return;
            AppliedPhase = phase;

            if (!PartControlEnabled || phase == LandingPhase.None) return;

            LandingPhaseRules rules = Rules(phase);
            if (rules == null || !rules.Any) return;

            try
            {
                Apply(rules);
            }
            catch (Exception e)
            {
                Debug.LogError("[MechJeb2P] part control: applying the " + phase + " options threw: " + e);
            }
        }

        private void Apply(LandingPhaseRules r)
        {
            int surfaces = 0, gimbals = 0, engines = 0;

            for (int i = 0; i < Vessel.parts.Count; i++)
            {
                Part p = Vessel.parts[i];
                for (int j = 0; j < p.Modules.Count; j++)
                {
                    PartModule m = p.Modules[j];
                    if (m == null) continue;

                    switch (m)
                    {
                        case ModuleControlSurface cs:
                            if (ApplySurface(cs, r)) surfaces++;
                            break;
                        case ModuleGimbal g:
                            if (ApplyGimbal(g, r)) gimbals++;
                            break;
                        case MultiModeEngine mme:
                            if (ApplyMultiMode(mme, r)) engines++;
                            break;
                        default:
                            // the control surfaces of other mods (Starship Expansion Project's flaps): the same field names as stock
                            if (m.GetType().Name.IndexOf("ControlSurface", StringComparison.Ordinal) >= 0 && ApplySurfaceByReflection(m, r))
                                surfaces++;
                            else if (m.GetType().Name == "ModuleSEPEngineSwitch" && ApplySepEngineSwitch(m, r))
                                engines++;
                            break;
                    }
                }
            }

            if (r.Rcs != PartSwitch.Leave)
            {
                _rcsBefore ??= Vessel.ActionGroups[KSPActionGroup.RCS];
                Vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, r.Rcs == PartSwitch.On);
                _snapshotTaken = true;
            }

            if (r.Sas != PartSwitch.Leave)
            {
                _sasBefore ??= Vessel.ActionGroups[KSPActionGroup.SAS];
                Vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, r.Sas == PartSwitch.On);
                _snapshotTaken = true;
            }

            if (r.ActionGroup >= 1 && r.ActionGroup <= 10)
                Vessel.ActionGroups.ToggleGroup(CustomGroup(r.ActionGroup));

            LastApplied = AppliedPhase + ": " + surfaces + " control surfaces, " + gimbals + " gimbals, " + engines + " engine modes" +
                          (r.ActionGroup > 0 ? ", action group " + r.ActionGroup : "");
            Debug.Log("[MechJeb2P] part control " + LastApplied);
        }

        public static KSPActionGroup CustomGroup(int n)
        {
            switch (n)
            {
                case 1:  return KSPActionGroup.Custom01;
                case 2:  return KSPActionGroup.Custom02;
                case 3:  return KSPActionGroup.Custom03;
                case 4:  return KSPActionGroup.Custom04;
                case 5:  return KSPActionGroup.Custom05;
                case 6:  return KSPActionGroup.Custom06;
                case 7:  return KSPActionGroup.Custom07;
                case 8:  return KSPActionGroup.Custom08;
                case 9:  return KSPActionGroup.Custom09;
                case 10: return KSPActionGroup.Custom10;
                default: return KSPActionGroup.None;
            }
        }

        private static bool Wants(PartSwitch s, bool current, out bool value)
        {
            value = current;
            if (s == PartSwitch.Leave) return false;
            value = s == PartSwitch.On;
            return value != current;
        }

        private bool ApplySurface(ModuleControlSurface cs, LandingPhaseRules r)
        {
            bool touched = false;

            if (r.FinsPitch != PartSwitch.Leave || r.FinsYaw != PartSwitch.Leave || r.FinsRoll != PartSwitch.Leave || r.FinsDeploy != PartSwitch.Leave)
            {
                if (!_surfaceBefore.ContainsKey(cs))
                    _surfaceBefore[cs] = new ControlSurfaceState { IgnorePitch = cs.ignorePitch, IgnoreYaw = cs.ignoreYaw, IgnoreRoll = cs.ignoreRoll, Deploy = cs.deploy };
                _snapshotTaken = true;
            }

            // "On" is authority on that axis, which KSP stores as ignore = false
            if (r.FinsPitch != PartSwitch.Leave) { cs.ignorePitch = r.FinsPitch == PartSwitch.Off; touched = true; }
            if (r.FinsYaw != PartSwitch.Leave) { cs.ignoreYaw = r.FinsYaw == PartSwitch.Off; touched = true; }
            if (r.FinsRoll != PartSwitch.Leave) { cs.ignoreRoll = r.FinsRoll == PartSwitch.Off; touched = true; }
            if (r.FinsDeploy != PartSwitch.Leave) { cs.deploy = r.FinsDeploy == PartSwitch.On; touched = true; }

            return touched;
        }

        private static readonly Dictionary<Type, FieldInfo[]> _surfaceFields = new Dictionary<Type, FieldInfo[]>();

        // ignorePitch, ignoreYaw, ignoreRoll, deploy: null when the type has no such bool field
        private static FieldInfo[] SurfaceFields(Type t)
        {
            if (_surfaceFields.TryGetValue(t, out FieldInfo[] fields)) return fields;
            string[] names = { "ignorePitch", "ignoreYaw", "ignoreRoll", "deploy" };
            fields = new FieldInfo[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                FieldInfo f = t.GetField(names[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                fields[i] = f != null && f.FieldType == typeof(bool) ? f : null;
            }

            _surfaceFields[t] = fields;
            return fields;
        }

        private bool ApplySurfaceByReflection(PartModule m, LandingPhaseRules r)
        {
            FieldInfo[] f = SurfaceFields(m.GetType());
            if (f[0] == null && f[1] == null && f[2] == null && f[3] == null) return false;

            bool touched = false;
            if (r.FinsPitch != PartSwitch.Leave || r.FinsYaw != PartSwitch.Leave || r.FinsRoll != PartSwitch.Leave || r.FinsDeploy != PartSwitch.Leave)
            {
                if (!_surfaceBefore.ContainsKey(m))
                    _surfaceBefore[m] = new ControlSurfaceState
                    {
                        IgnorePitch = f[0] != null && (bool)f[0].GetValue(m),
                        IgnoreYaw = f[1] != null && (bool)f[1].GetValue(m),
                        IgnoreRoll = f[2] != null && (bool)f[2].GetValue(m),
                        Deploy = f[3] != null && (bool)f[3].GetValue(m)
                    };
                _snapshotTaken = true;
            }

            if (r.FinsPitch != PartSwitch.Leave && f[0] != null) { f[0].SetValue(m, r.FinsPitch == PartSwitch.Off); touched = true; }
            if (r.FinsYaw != PartSwitch.Leave && f[1] != null) { f[1].SetValue(m, r.FinsYaw == PartSwitch.Off); touched = true; }
            if (r.FinsRoll != PartSwitch.Leave && f[2] != null) { f[2].SetValue(m, r.FinsRoll == PartSwitch.Off); touched = true; }
            if (r.FinsDeploy != PartSwitch.Leave && f[3] != null) { f[3].SetValue(m, r.FinsDeploy == PartSwitch.On); touched = true; }
            return touched;
        }

        private bool ApplyGimbal(ModuleGimbal g, LandingPhaseRules r)
        {
            if (r.GimbalPitch == PartSwitch.Leave && r.GimbalYaw == PartSwitch.Leave && r.GimbalRoll == PartSwitch.Leave && r.GimbalLock == PartSwitch.Leave)
                return false;

            if (!_gimbalBefore.ContainsKey(g))
                _gimbalBefore[g] = new GimbalState { EnablePitch = g.enablePitch, EnableYaw = g.enableYaw, EnableRoll = g.enableRoll, Lock = g.gimbalLock };
            _snapshotTaken = true;

            if (Wants(r.GimbalPitch, g.enablePitch, out bool v)) g.enablePitch = v;
            if (Wants(r.GimbalYaw, g.enableYaw, out v)) g.enableYaw = v;
            if (Wants(r.GimbalRoll, g.enableRoll, out v)) g.enableRoll = v;
            if (Wants(r.GimbalLock, g.gimbalLock, out v)) g.gimbalLock = v;
            return true;
        }

        private static bool ApplyMultiMode(MultiModeEngine mme, LandingPhaseRules r)
        {
            if (string.IsNullOrEmpty(r.EngineMode)) return false;

            bool primary = string.Equals(mme.primaryEngineID, r.EngineMode, StringComparison.OrdinalIgnoreCase);
            bool secondary = string.Equals(mme.secondaryEngineID, r.EngineMode, StringComparison.OrdinalIgnoreCase);
            if (!primary && !secondary) return false;

            if (mme.runningPrimary != primary)
                mme.SetPrimary(primary);
            return true;
        }

        private static readonly Dictionary<Type, (FieldInfo engines, MethodInfo setActive)> _sepSwitch =
            new Dictionary<Type, (FieldInfo, MethodInfo)>();

        // Starship Expansion Project: ModuleSEPEngineSwitch.engines (List<ModuleEngines>) and SetActiveEngine(int index, bool setup)
        private static bool ApplySepEngineSwitch(PartModule m, LandingPhaseRules r)
        {
            if (string.IsNullOrEmpty(r.EngineMode)) return false;

            Type t = m.GetType();
            if (!_sepSwitch.TryGetValue(t, out (FieldInfo engines, MethodInfo setActive) api))
            {
                api.engines = t.GetField("engines", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                api.setActive = t.GetMethod("SetActiveEngine", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _sepSwitch[t] = api;
            }

            if (api.engines == null || api.setActive == null) return false;

            if (!(api.engines.GetValue(m) is IList<ModuleEngines> engines)) return false;

            for (int i = 0; i < engines.Count; i++)
            {
                ModuleEngines e = engines[i];
                if (e == null || !string.Equals(e.engineID, r.EngineMode, StringComparison.OrdinalIgnoreCase)) continue;

                ParameterInfo[] ps = api.setActive.GetParameters();
                object[] args = ps.Length == 2 ? new object[] { i, false } : new object[] { i };
                api.setActive.Invoke(m, args);
                return true;
            }

            return false;
        }

        private void Restore()
        {
            foreach (KeyValuePair<PartModule, ControlSurfaceState> kv in _surfaceBefore)
            {
                PartModule m = kv.Key;
                if (m == null || m.part == null) continue;
                ControlSurfaceState s = kv.Value;

                if (m is ModuleControlSurface cs)
                {
                    cs.ignorePitch = s.IgnorePitch;
                    cs.ignoreYaw = s.IgnoreYaw;
                    cs.ignoreRoll = s.IgnoreRoll;
                    cs.deploy = s.Deploy;
                }
                else
                {
                    FieldInfo[] f = SurfaceFields(m.GetType());
                    f[0]?.SetValue(m, s.IgnorePitch);
                    f[1]?.SetValue(m, s.IgnoreYaw);
                    f[2]?.SetValue(m, s.IgnoreRoll);
                    f[3]?.SetValue(m, s.Deploy);
                }
            }

            foreach (KeyValuePair<ModuleGimbal, GimbalState> kv in _gimbalBefore)
            {
                ModuleGimbal g = kv.Key;
                if (g == null || g.part == null) continue;
                g.enablePitch = kv.Value.EnablePitch;
                g.enableYaw = kv.Value.EnableYaw;
                g.enableRoll = kv.Value.EnableRoll;
                g.gimbalLock = kv.Value.Lock;
            }

            if (_rcsBefore is bool rcs) Vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, rcs);
            if (_sasBefore is bool sas) Vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, sas);

            LastApplied = "restored";
        }

        // ---- presets ----

        public void ClearAll()
        {
            Entry.Clear();
            Braking.Clear();
            Terminal.Clear();
            Dirty = true;
        }

        /// <summary>
        /// A Starship Expansion Project booster or ship coming back to the tower: grid fins (or flaps) on every axis for the
        /// entry, the gimbals free for the braking burn with the fins still trimming pitch and yaw, and in the last metres
        /// the fins' roll off (at low speed they only stir the roll the gimbals and RCS are holding) with RCS on for the fine
        /// translation. The engine modes are left for the player: the cluster mode names differ between booster versions.
        /// </summary>
        public void SepCatchPreset()
        {
            Entry.Clear();
            Entry.FinsPitch = Entry.FinsYaw = Entry.FinsRoll = PartSwitch.On;
            Entry.GimbalLock = PartSwitch.Leave;

            Braking.Clear();
            Braking.FinsPitch = Braking.FinsYaw = PartSwitch.On;
            Braking.FinsRoll = PartSwitch.On;
            Braking.GimbalPitch = Braking.GimbalYaw = Braking.GimbalRoll = PartSwitch.On;
            Braking.GimbalLock = PartSwitch.Off;
            Braking.Rcs = PartSwitch.On;

            Terminal.Clear();
            Terminal.FinsPitch = Terminal.FinsYaw = PartSwitch.On;
            Terminal.FinsRoll = PartSwitch.Off;
            Terminal.GimbalPitch = Terminal.GimbalYaw = Terminal.GimbalRoll = PartSwitch.On;
            Terminal.GimbalLock = PartSwitch.Off;
            Terminal.Rcs = PartSwitch.On;
            Terminal.Sas = PartSwitch.Off;
            Dirty = true;
        }

        public override void OnDestroy() => ForgetSnapshot();
    }
}
