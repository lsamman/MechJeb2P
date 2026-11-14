using System;
using System.Collections.Generic;
using KodeKit;
using KodeUI;
using KSP.Localization;
using MechJebLibBindings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static MuMech.MechJebModuleThrustController;

namespace MuMech.UI
{
    /// <summary>
    /// MechJebModuleLandingGuidance ("Landing Guidance") in KodeKit, row for row with its WindowGUI, including the
    /// landing prediction block of DrawGUITogglePredictions (LandingPredictionsBlock, shared with the custom windows).
    /// </summary>
    [KodeViewFor(typeof(MechJebModuleLandingGuidance))]
    public class LandingGuidanceView : KodeView
    {
        readonly MechJebModuleLandingGuidance guidance;

        public LandingGuidanceView(MechJebModuleLandingGuidance guidance) : base(guidance) => this.guidance = guidance;

        protected override float MinWidth => 200;

        MechJebCore Core => guidance.Core;
        CelestialBody Body => Core.vessel != null ? Core.vessel.mainBody : null;
        bool LandedOrSplashed => Core.vessel != null && Core.vessel.LandedOrSplashed;
        // a point on this body to land at: a vessel in flight or a target on another body has none
        bool HasPositionTarget => Core.Target != null && Core.Target.SurfaceTargetExists(Body);
        // names of the landing sites of the current body, cached until the body or the list changes, and where they are in
        // the list: the KSC sites move once the game knows its buildings, so the position is read when a site is set
        string[] siteNames = new string[0];
        CelestialBody siteBody;
        int siteCount = -1;
        readonly List<int> siteIndexes = new List<int>();

        string[] SiteNames()
        {
            CelestialBody body = Body;
            List<MechJebModuleLandingGuidance.LandingSite> all = MechJebModuleLandingGuidance.LandingSites;
            int count = all?.Count ?? 0;
            if (body == siteBody && count == siteCount) return siteNames;

            siteBody = body;
            siteCount = count;
            siteIndexes.Clear();
            for (int i = 0; i < count; i++)
                if (all[i].Body == body)
                    siteIndexes.Add(i);
            siteNames = new string[siteIndexes.Count];
            for (int i = 0; i < siteIndexes.Count; i++) siteNames[i] = all[siteIndexes[i]].Name;
            return siteNames;
        }

        // biome of the target, only looked up when the coordinates change
        double biomeLat = double.NaN, biomeLon = double.NaN;
        CelestialBody biomeBody;
        string biome = "";

        string TargetBiome()
        {
            CelestialBody body = Core.Target.targetBody;
            double lat = Core.Target.targetLatitude, lon = Core.Target.targetLongitude;
            if (lat != biomeLat || lon != biomeLon || body != biomeBody)
            {
                biomeLat = lat;
                biomeLon = lon;
                biomeBody = body;
                biome = body != null ? body.GetExperimentBiomeSafe(lat, lon) ?? "" : "";
            }

            return biome;
        }

        protected override void BuildContents(KodeWindow w)
        {
            if (KodeWindow.ModernLayout)
            {
                BuildModern(w);
                return;
            }

            UIObject r = w.Root;

            // ---- target ----
            Layout target = KodeWindow.Panel(r);
            w.Visible(target.gameObject, () => HasPositionTarget);
            KodeWindow.Label(target, "#MechJeb_LandingGuidance_label1"); //Target coordinates:
            AngleRow(w, target, () => Core.Target.targetLatitude, EditableAngle.Direction.NS, Sprites.Direction.Up, 10, Sprites.Direction.Down, -10, true); // ▲ ▼ as sprites: KSP's fonts lack them
            AngleRow(w, target, () => Core.Target.targetLongitude, EditableAngle.Direction.EW, Sprites.Direction.Left, -10, Sprites.Direction.Right, 10, false);

            TargetReadouts(w, target);
            TargetButtons(w, r, false);

            // ---- landing sites ----
            SiteRow(w, r);

            // ---- predictions ----
            new LandingPredictionsBlock(w, r, guidance);

            // ---- autopilot ----
            Layout autopilot = KodeWindow.Panel(r);
            w.Visible(autopilot.gameObject, () => Core.Landing != null);
            Autopilot(w, autopilot);
        }

        // the terrain height runs the body's PQS: asked again only when the target or the body changes
        CelestialBody aslBody;
        double aslLat = double.NaN, aslLon = double.NaN, asl;

        double TargetAsl()
        {
            CelestialBody body = Body;
            double lat = Core.Target.targetLatitude, lon = Core.Target.targetLongitude;
            if (body == aslBody && lat.Equals(aslLat) && lon.Equals(aslLon)) return asl;
            aslBody = body;
            aslLat = lat;
            aslLon = lon;
            asl = body.TerrainAltitude(lat, lon);
            return asl;
        }

        /// <summary>EditableAngle.DrawEditGUI (d ° m ' s " N/S) followed by the 10 m step buttons, on one row.</summary>
        void AngleRow(KodeWindow w, UIObject parent, Func<EditableAngle> angle, EditableAngle.Direction direction, Sprites.Direction first, double firstDelta, Sprites.Direction second, double secondDelta,
            bool latitude)
        {
            Layout row = KodeWindow.Row(parent);
            OpUi.AngleRow(w, row, angle, direction, false);
            KodeWindow.IconButton(row, Sprites.Arrow(first), () => Move(latitude, firstDelta));
            KodeWindow.Label(row, "10m");
            KodeWindow.IconButton(row, Sprites.Arrow(second), () => Move(latitude, secondDelta));
        }

        void Move(bool latitude, double meters)
        {
            double alt = TargetAsl();
            if (latitude) guidance.MoveByMeter(ref Core.Target.targetLatitude, meters, alt, false);
            else guidance.MoveByMeter(ref Core.Target.targetLongitude, meters, alt, true);
        }

        // ---- the autopilot block ----
        void Autopilot(KodeWindow w, UIObject p)
        {
            KodeWindow.Label(p, "#MechJeb_LandingGuidance_label2"); //Autopilot:

            LandButtons(w, p, false);

            Layout touchdown = KodeWindow.Row(p);
            KodeWindow.Label(touchdown, "#MechJeb_LandingGuidance_label3"); //Touchdown speed:
            EditUi.Field(w, touchdown, () => Core.Landing?.TouchdownSpeed, 35);
            KodeWindow.Label(touchdown, "m/s");

            NodeUi.AutowarpToggle(w, p, Core, "#MechJeb_LandingGuidance_checkbox1"); //Auto-warp

            w.Toggle(p, "#MechJeb_LandingGuidance_checkbox2", () => Core.Landing != null && Core.Landing.DeployGears, v => //Deploy Landing Gear
            {
                if (Core.Landing != null) Core.Landing.DeployGears = v;
            });
            StageRow(w, p, "#MechJeb_LandingGuidance_label4", () => Core.Landing?.LimitGearsStage); //Stage Limit:
            w.Toggle(p, "#MechJeb_LandingGuidance_checkbox3", () => Core.Landing != null && Core.Landing.DeployChutes, v => //Deploy Parachutes
            {
                if (Core.Landing != null) Core.Landing.DeployChutes = v;
            });
            StageRow(w, p, "#MechJeb_LandingGuidance_label5", () => Core.Landing?.LimitChutesStage); //Stage Limit:
            w.Toggle(p, "#MechJeb_LandingGuidance_jettison", () => Core.Landing != null && Core.Landing.JettisonBeforeEntry, v => //Jettison stage before entry
            {
                if (Core.Landing != null) Core.Landing.JettisonBeforeEntry = v;
            });
            w.Toggle(p, "#MechJeb_LandingGuidance_checkbox4", () => Core.Landing != null && Core.Landing.RCSAdjustment, v => //Use RCS for small adjustment
            {
                if (Core.Landing != null) Core.Landing.RCSAdjustment = v;
            });

            LowerThrottleLimit(w, p);

            Precision(w, p);
            PartControl(w, p);

            // while landing
            Layout running = KodeWindow.Panel(p);
            w.Visible(running.gameObject, () => Core.Landing != null && Core.Landing.Enabled);
            RunningLabels(w, running);
        }

        // ---- MechJeb2P: precision catch ----

        MechJebModuleLandingAutopilot Landing => Core.Landing;

        void Precision(KodeWindow w, UIObject p)
        {
            w.Section(p, "#MechJeb_LG2P_Section", false, s => //Precision catch (2P)
            {
                Toggle precision = w.Toggle(s, "#MechJeb_LG2P_PrecisionMode", () => Landing != null && Landing.PrecisionMode, v => //Precision terminal guidance
                {
                    if (Landing != null) { Landing.PrecisionMode = v; Landing.Dirty = true; }
                });
                Tooltips.Add(KodeWindow.RowOf(precision), "#MechJeb_LG2P_PrecisionMode_tooltip");

                // catch point
                Layout alt = EditUi.Row(w, s, "#MechJeb_LG2P_CatchAltitude", () => Landing?.CatchAltitudeASL, "m", 70); //Catch altitude (bottom, ASL):
                Tooltips.Add(alt.gameObject, "#MechJeb_LG2P_CatchAltitude_tooltip");
                Layout altButtons = KodeWindow.Row(s);
                KodeWindow.Button(altButtons, "#MechJeb_LG2P_FromVessel", () => Landing?.SetCatchAltitudeFromVessel(), true); //From vessel
                KodeWindow.Button(altButtons, "#MechJeb_LG2P_Terrain", () => Landing?.SetCatchAltitudeToTerrain(), true); //Terrain
                UIButton here = KodeWindow.Button(s, "#MechJeb_LG2P_CatchPointHere", () => Landing?.SetCatchPointFromVessel(), true); //Catch point = here
                Tooltips.Add(here.gameObject, "#MechJeb_LG2P_CatchPointHere_tooltip");
                GuidanceUi.Enable(w, here, () => Landing != null && !Landing.Enabled && LandedOrSplashed);
                string above = Localizer.Format("#MechJeb_LG2P_AboveTerrain", "{0}"); //<<1>> m above the terrain at the target
                w.Wrap(w.BoundLabel(s, Memo.Of(() => Landing != null && HasPositionTarget ? Math.Round(Landing.CatchHeightAboveTerrain()) : double.NaN,
                    h => double.IsNaN(h) ? "" : string.Format(above, h.ToString("F0"))), true));

                // the descent
                Layout take = EditUi.Row(w, s, "#MechJeb_LG2P_TerminalHeight", () => Landing?.TerminalHeight, "m", 60); //Take over at:
                Tooltips.Add(take.gameObject, "#MechJeb_LG2P_TerminalHeight_tooltip");
                EditUi.Row(w, s, "#MechJeb_LG2P_MaxDescentSpeed", () => Landing?.MaxDescentSpeed, "m/s", 60); //Max descent speed:
                EditUi.Row(w, s, "#MechJeb_LG2P_MaxHorizontalSpeed", () => Landing?.MaxHorizontalSpeed, "m/s", 60); //Max horizontal speed:
                Layout tilt = EditUi.Row(w, s, "#MechJeb_LG2P_MaxTilt", () => Landing?.MaxTilt, "°", 60); //Max tilt:
                Tooltips.Add(tilt.gameObject, "#MechJeb_LG2P_MaxTilt_tooltip");
                Layout capture = EditUi.Row(w, s, "#MechJeb_LG2P_CaptureSpeed", () => Landing?.CaptureSpeed, "m/s", 60); //Capture speed:
                Tooltips.Add(capture.gameObject, "#MechJeb_LG2P_CaptureSpeed_tooltip");
                EditUi.Row(w, s, "#MechJeb_LG2P_Tolerance", () => Landing?.HorizontalTolerance, "m", 60); //Centred within:
                Layout fraction = EditUi.Row(w, s, "#MechJeb_LG2P_TerminalThrustFraction", () => Landing?.TerminalThrustFraction, "", 60); //Thrust left after the terminal engine switch:
                Tooltips.Add(fraction.gameObject, "#MechJeb_LG2P_TerminalThrustFraction_tooltip");
                Toggle hover = w.Toggle(s, "#MechJeb_LG2P_HoverAtCatch", () => Landing != null && Landing.HoverAtCatch, v => //Hover at the catch altitude
                {
                    if (Landing != null) { Landing.HoverAtCatch = v; Landing.Dirty = true; }
                });
                Tooltips.Add(KodeWindow.RowOf(hover), "#MechJeb_LG2P_HoverAtCatch_tooltip");
                EditUi.Row(w, s, "#MechJeb_LG2P_HoverTimeLimit", () => Landing?.HoverTimeLimit, "s", 60); //Hover time limit:

                // heading
                Toggle heading = w.Toggle(s, "#MechJeb_LG2P_HoldHeading", () => Landing != null && Landing.HoldHeading, v => //Hold heading
                {
                    if (Landing != null) { Landing.HoldHeading = v; Landing.Dirty = true; }
                });
                Tooltips.Add(KodeWindow.RowOf(heading), "#MechJeb_LG2P_HoldHeading_tooltip");
                Layout headingRow = KodeWindow.Row(s);
                EditUi.Field(w, headingRow, () => Landing?.CatchHeading, 60);
                KodeWindow.Label(headingRow, "°");
                KodeWindow.Button(headingRow, "#MechJeb_LG2P_UseCurrent", () => Landing?.SetHeadingFromVessel()); //Use current

                // entry and braking
                Toggle aero = w.Toggle(s, "#MechJeb_LG2P_AeroSteering", () => Landing != null && Landing.AeroSteering, v => //Aero steering during entry
                {
                    if (Landing != null) { Landing.AeroSteering = v; Landing.Dirty = true; }
                });
                Tooltips.Add(KodeWindow.RowOf(aero), "#MechJeb_LG2P_AeroSteering_tooltip");
                EditUi.Row(w, s, "#MechJeb_LG2P_MaxAoA", () => Landing?.MaxAoA, "°", 60); //Max angle of attack:
                Layout braking = EditUi.Row(w, s, "#MechJeb_LG2P_BrakingTilt", () => Landing?.BrakingMaxTilt, "°", 60); //Braking burn max tilt:
                Tooltips.Add(braking.gameObject, "#MechJeb_LG2P_BrakingTilt_tooltip");

                // boostback
                Layout flip = EditUi.Row(w, s, "#MechJeb_LG2P_FlipThrottle", () => Landing?.BoostbackFlipThrottle, "", 60); //Boostback flip throttle (0-0.5):
                Tooltips.Add(flip.gameObject, "#MechJeb_LG2P_FlipThrottle_tooltip");
                Layout drag = EditUi.Row(w, s, "#MechJeb_LG2P_BoostbackMaxDrag", () => Landing?.BoostbackMaxDrag, "m/s²", 60); //Boostback gives up above drag:
                Tooltips.Add(drag.gameObject, "#MechJeb_LG2P_BoostbackMaxDrag_tooltip");
                EditUi.Row(w, s, "#MechJeb_LG2P_BoostbackDoneMiss", () => Landing?.BoostbackDoneMiss, "m", 60); //Boostback done within:

                // remote flight
                Toggle remote = w.Toggle(s, "#MechJeb_LG2P_Remote", () => Landing != null && Landing.RemoteControl, v => //Keep flying when not focused
                {
                    if (Landing != null) { Landing.RemoteControl = v; Landing.Dirty = true; }
                });
                Tooltips.Add(KodeWindow.RowOf(remote), "#MechJeb_LG2P_Remote_tooltip");
                EditUi.Row(w, s, "#MechJeb_LG2P_RemoteRange", () => Landing?.RemoteRangeKm, "km", 60); //Load range:
                Toggle warp = w.Toggle(s, "#MechJeb_LG2P_RemoteBlockWarp", () => Landing != null && Landing.RemoteBlockWarp, v => //Block time warp while flying unfocused
                {
                    if (Landing != null) { Landing.RemoteBlockWarp = v; Landing.Dirty = true; }
                });
                Tooltips.Add(KodeWindow.RowOf(warp), "#MechJeb_LG2P_RemoteBlockWarp_tooltip");

                // flight log
                Toggle log = w.Toggle(s, "#MechJeb_LG2P_FlightLog", () => Landing != null && Landing.FlightLog, v => //Write a flight log (Logs/MechJeb2P)
                {
                    if (Landing != null) { Landing.FlightLog = v; Landing.Dirty = true; }
                });
                Tooltips.Add(KodeWindow.RowOf(log), "#MechJeb_LG2P_FlightLog_tooltip");
                string logFile = Localizer.Format("#MechJeb_LG2P_LogFile") + " "; //Log file:
                UIText logLabel = w.BoundLabel(s, Memo.Of(() => Landing?.Log?.FileName ?? "", f => logFile + f), true);
                w.Wrap(logLabel);
                w.Visible(logLabel, () => Landing?.Log != null && Landing.Log.Open);

                // presets
                Layout presets = KodeWindow.Row(s);
                UIButton sep = KodeWindow.Button(presets, "#MechJeb_LG2P_SepPreset", () => Landing?.SepCatchPreset(), true); //SEP catch preset
                Tooltips.Add(sep.gameObject, "#MechJeb_LG2P_SepPreset_tooltip");
                KodeWindow.Button(presets, "#MechJeb_LG2P_Defaults", () => //Stock behaviour
                {
                    if (Landing == null) return;
                    Landing.PrecisionMode = false;
                    Landing.AeroSteering = false;
                    Landing.HoldHeading = false;
                    Landing.BrakingMaxTilt.Val = 5.7;
                    Landing.Dirty = true;
                    Core.LandingParts?.ClearAll();
                }, true);
            }, "LG2P.Precision");
        }

        // ---- MechJeb2P: part options by phase ----

        static readonly string[] _switchNames = { "#MechJeb_LG2P_Leave", "#MechJeb_LG2P_On", "#MechJeb_LG2P_Off" }; //Leave On Off

        void PartControl(KodeWindow w, UIObject p)
        {
            w.Section(p, "#MechJeb_LG2P_PartsSection", false, s => //Part control (2P)
            {
                MechJebModuleLandingPartControl parts = Core.LandingParts;
                if (parts == null) return;

                Toggle enabled = w.Toggle(s, "#MechJeb_LG2P_PartsEnabled", () => parts.PartControlEnabled, v => //Set part options by landing phase
                {
                    parts.PartControlEnabled = v;
                    parts.Dirty = true;
                });
                Tooltips.Add(KodeWindow.RowOf(enabled), "#MechJeb_LG2P_PartsEnabled_tooltip");
                w.Toggle(s, "#MechJeb_LG2P_PartsRestore", () => parts.RestoreAfterLanding, v => //Restore the options after the landing
                {
                    parts.RestoreAfterLanding = v;
                    parts.Dirty = true;
                });

                Phase(w, s, "#MechJeb_LG2P_PhaseEntry", parts.Entry, parts, "LG2P.Entry"); //Entry (deorbit, coast, atmosphere)
                Phase(w, s, "#MechJeb_LG2P_PhaseBraking", parts.Braking, parts, "LG2P.Braking"); //Braking burn
                Phase(w, s, "#MechJeb_LG2P_PhaseTerminal", parts.Terminal, parts, "LG2P.Terminal"); //Terminal descent

                string last = Localizer.Format("#MechJeb_LG2P_LastApplied") + " "; //Last applied:
                UIText applied = w.BoundLabel(s, Memo.Of(() => parts.LastApplied ?? "", a => last + a), true);
                w.Wrap(applied);
                w.Visible(applied, () => !string.IsNullOrEmpty(parts.LastApplied));
            }, "LG2P.Parts");
        }

        void Phase(KodeWindow w, UIObject p, string title, LandingPhaseRules r, MechJebModuleLandingPartControl parts, string key)
        {
            w.Section(p, title, false, s =>
            {
                Switch(w, s, "#MechJeb_LG2P_FinsPitch", () => r.FinsPitch, v => r.FinsPitch = v, parts);
                Switch(w, s, "#MechJeb_LG2P_FinsYaw", () => r.FinsYaw, v => r.FinsYaw = v, parts);
                Switch(w, s, "#MechJeb_LG2P_FinsRoll", () => r.FinsRoll, v => r.FinsRoll = v, parts);
                Switch(w, s, "#MechJeb_LG2P_FinsDeploy", () => r.FinsDeploy, v => r.FinsDeploy = v, parts);
                Switch(w, s, "#MechJeb_LG2P_GimbalPitch", () => r.GimbalPitch, v => r.GimbalPitch = v, parts);
                Switch(w, s, "#MechJeb_LG2P_GimbalYaw", () => r.GimbalYaw, v => r.GimbalYaw = v, parts);
                Switch(w, s, "#MechJeb_LG2P_GimbalRoll", () => r.GimbalRoll, v => r.GimbalRoll = v, parts);
                Switch(w, s, "#MechJeb_LG2P_GimbalLock", () => r.GimbalLock, v => r.GimbalLock = v, parts);
                Switch(w, s, "#MechJeb_LG2P_Rcs", () => r.Rcs, v => r.Rcs = v, parts);
                Switch(w, s, "#MechJeb_LG2P_Sas", () => r.Sas, v => r.Sas = v, parts);

                Layout ag = KodeWindow.Row(s);
                KodeWindow.Label(ag, "#MechJeb_LG2P_ActionGroup"); //Fire action group (0 = none):
                EditFields.Field(w, ag, () => r.ActionGroup.ToString(), t =>
                {
                    if (int.TryParse(t, out int n)) { r.ActionGroup = Mathf.Clamp(n, 0, 10); parts.Dirty = true; }
                }, 40, () => true);

                Layout mode = KodeWindow.Row(s);
                KodeWindow.Label(mode, "#MechJeb_LG2P_EngineMode"); //Engine mode:
                EditFields.Field(w, mode, () => r.EngineMode ?? "", t =>
                {
                    r.EngineMode = t.Trim();
                    parts.Dirty = true;
                }, 110, () => true);
                Tooltips.Add(mode.gameObject, "#MechJeb_LG2P_EngineMode_tooltip");
            }, key);
        }

        void Switch(KodeWindow w, UIObject p, string label, Func<PartSwitch> get, Action<PartSwitch> set, MechJebModuleLandingPartControl parts)
        {
            Layout row = KodeWindow.Row(p);
            KodeWindow.Label(row, label, true);
            w.Segmented(row, _switchNames, () => (int)get(), i =>
            {
                set((PartSwitch)i);
                parts.Dirty = true;
            });
        }

        // ---- the parts both layouts share ----

        /// <summary>ASL and biome of the target, filled every frame without allocating; returns both labels.</summary>
        UIText[] TargetReadouts(KodeWindow w, UIObject target)
        {
            Layout asl = KodeWindow.Row(target);
            return new[]
            {
                GuidanceUi.Live(w, asl, "ASL: -1.234 km", true, ct => ct.Append("ASL: ").AppendSI(TargetAsl()).Append('m'), () => HasPositionTarget),
                GuidanceUi.Live(w, asl, "Shores of the Northern Sea", true, ct => ct.Append(TargetBiome()), () => HasPositionTarget)
            };
        }

        void TargetButtons(KodeWindow w, UIObject r, bool flexible)
        {
            UIButton enter = KodeWindow.Button(r, "#MechJeb_LandingGuidance_button1", () => //Enter target coordinates
                Core.Target.SetPositionTarget(Body, Core.Target.targetLatitude, Core.Target.targetLongitude), flexible);
            w.Visible(enter.gameObject, () => !HasPositionTarget);
            KodeWindow.Button(r, "#MechJeb_LandingGuidance_button2", () => Core.Target.PickPositionTargetOnMap(), flexible); //Pick target on map
            // Clear target, except while the landing autopilot flies to it
            UIButton clear = KodeWindow.Button(r, "#MechJeb_LandingGuidance_buttonClear", () => Core.Target.Unset(), flexible); //Clear target
            w.Visible(clear.gameObject, () => HasPositionTarget);
            GuidanceUi.Enable(w, clear, () => Core.Landing == null || !Core.Landing.Enabled);
        }

        /// <summary>The landing sites list and its "Set" button (shown when there are sites).</summary>
        void SiteRow(KodeWindow w, UIObject r)
        {
            Layout siteRow = KodeWindow.Row(r);
            new ComboBox(w, siteRow, () => guidance._landingSiteIdx, i => guidance._landingSiteIdx = i, SiteNames);
            KodeWindow.Button(siteRow, "Set", () =>
            {
                SiteNames();
                if (siteIndexes.Count == 0) return;
                MechJebModuleLandingGuidance.UseRealKscSites();
                MechJebModuleLandingGuidance.LandingSite site =
                    MechJebModuleLandingGuidance.LandingSites[siteIndexes[Mathf.Clamp(guidance._landingSiteIdx, 0, siteIndexes.Count - 1)]];
                Core.Target.SetPositionTarget(Body, site.Latitude, site.Longitude);
            });
            w.Visible(siteRow.gameObject, () => SiteNames().Length > 0);
        }

        /// <summary>Abort while landing; Land at target / Land somewhere otherwise.</summary>
        void LandButtons(KodeWindow w, UIObject p, bool flexible)
        {
            UIButton abort = KodeWindow.Button(p, "#MechJeb_LandingGuidance_button3", () => Core.Landing.StopLanding(), ButtonRole.Danger, flexible); //Abort autoland
            w.Visible(abort.gameObject, () => Core.Landing != null && Core.Landing.Enabled);
            Layout land = KodeWindow.Row(p);
            w.Visible(land.gameObject, () => Core.Landing != null && !Core.Landing.Enabled);
            UIButton atTarget = KodeWindow.Button(land, "#MechJeb_LandingGuidance_button4", () => Core.Landing.LandAtPositionTarget(guidance),
                ButtonRole.Primary, true); //Land at target
            GuidanceUi.Enable(w, atTarget, () => HasPositionTarget && !LandedOrSplashed);
            UIButton somewhere = KodeWindow.Button(land, "#MechJeb_LandingGuidance_button5", () => Core.Landing.LandUntargeted(guidance), true); //Land somewhere
            GuidanceUi.Enable(w, somewhere, () => !LandedOrSplashed);
        }

        /// <summary>Status, step and descent mode while landing; each text is made when what it shows changes.</summary>
        void RunningLabels(KodeWindow w, UIObject running)
        {
            string status = Localizer.Format("#MechJeb_LandingGuidance_label6");
            string step = Localizer.Format("#MechJeb_LandingGuidance_label7");
            string mode = Localizer.Format("#MechJeb_LandingGuidance_label8");
            w.Wrap(w.BoundLabel(running, Memo.Of(() => Core.Landing?.Status ?? "", s => status + s), true)); //Status:
            w.BoundLabel(running, Memo.Of(() => Core.Landing?.CurrentStep?.GetType(), t => step + (t != null ? t.Name : "N/A")), true); //Step:
            // UseAtmosphereToBrake walks every chute's drag cubes: only while landing, when this panel shows
            w.BoundLabel(running, Memo.Of(() => Core.Landing != null && Core.Landing.Enabled
                    ? (Core.Landing.DescentSpeedPolicy?.GetType(), Core.Landing.UseAtmosphereToBrake())
                    : (null, false),
                k => mode + (k.Item1 != null ? k.Item1.Name : "N/A") + " (" + k.Item2 + ")"), true); //Mode
        }

        static void StageRow(KodeWindow w, UIObject parent, string key, Func<EditableInt> ed) => EditUi.Row(w, parent, key, ed, null, 35);

        // ---- modern layout: D-M-S fields with the hemisphere as a choice, one nudge pad, the options in a property grid ----

        void BuildModern(KodeWindow w)
        {
            UIObject r = w.Root;

            Layout target = KodeWindow.Panel(r);
            w.Visible(target.gameObject, () => HasPositionTarget);
            KodeWindow.Header(target, "#MechJeb_LandingGuidance_label1"); //Target coordinates:
            Layout coords = KodeWindow.Row(target);
            Layout fields = KodeWindow.Panel(coords);
            ModernAngle(w, fields, "Lat", () => Core.Target.targetLatitude, EditableAngle.Direction.NS);
            ModernAngle(w, fields, "Lon", () => Core.Target.targetLongitude, EditableAngle.Direction.EW);

            // the four 10 m nudges as one pad, the step in its centre
            KodeWindow.CompassGrid pad = KodeWindow.Compass(coords, 20);
            pad.Grid.FlexibleLayout(0f, 0f); // 0, not -1: the grid itself asks for flexible width
            KodeWindow.IconButton(pad[Dir.N], Sprites.Arrow(Sprites.Direction.Up), () => Move(true, 10));
            KodeWindow.IconButton(pad[Dir.W], Sprites.Arrow(Sprites.Direction.Left), () => Move(false, -10));
            UIText step = KodeWindow.Label(pad[Dir.C], "10m");
            step.tmpText.alignment = TextAlignmentOptions.Center;
            KodeWindow.IconButton(pad[Dir.E], Sprites.Arrow(Sprites.Direction.Right), () => Move(false, 10));
            KodeWindow.IconButton(pad[Dir.S], Sprites.Arrow(Sprites.Direction.Down), () => Move(true, -10));

            foreach (UIText t in TargetReadouts(w, target)) KodeWindow.ToneText(t, KodeWindow.Tone.Dim);
            TargetButtons(w, r, true);
            SiteRow(w, r);

            new LandingPredictionsBlock(w, r, guidance);

            Layout autopilot = KodeWindow.Panel(r);
            w.Visible(autopilot.gameObject, () => Core.Landing != null);
            ModernAutopilot(w, autopilot);
        }

        /// <summary>Lat [d] ° [m] ' [s] " [N|S]: the hemisphere is a two-way choice, not a button that flips.</summary>
        static void ModernAngle(KodeWindow w, UIObject parent, string label, Func<EditableAngle> angle, EditableAngle.Direction direction)
        {
            Layout row = KodeWindow.Row(parent);
            KodeWindow.Label(row, label);
            OpUi.AngleRow(w, row, angle, direction, false);
        }

        void ModernAutopilot(KodeWindow w, UIObject p)
        {
            KodeWindow.Header(p, "#MechJeb_LandingGuidance_label2"); //Autopilot:

            LandButtons(w, p, true);

            KodeWindow.PropertyGrid g = w.Properties(p);
            EditUi.Grid(g, "#MechJeb_LandingGuidance_label3", () => Core.Landing?.TouchdownSpeed, "m/s", 50); //Touchdown speed:
            g.Switch("#MechJeb_LandingGuidance_checkbox1", () => NodeUi.Autowarp(Core), v => NodeUi.Autowarp(Core, v)); //Auto-warp
            g.Switch("#MechJeb_LandingGuidance_checkbox2", () => Core.Landing != null && Core.Landing.DeployGears, v => //Deploy Landing Gear
            {
                if (Core.Landing != null) Core.Landing.DeployGears = v;
            });
            Layout gears = ModernStage(g, "#MechJeb_LandingGuidance_label4", () => Core.Landing?.LimitGearsStage); //Stage Limit:
            g.Dim(gears.gameObject, null, () => Core.Landing == null || !Core.Landing.DeployGears);
            g.Switch("#MechJeb_LandingGuidance_checkbox3", () => Core.Landing != null && Core.Landing.DeployChutes, v => //Deploy Parachutes
            {
                if (Core.Landing != null) Core.Landing.DeployChutes = v;
            });
            Layout chutes = ModernStage(g, "#MechJeb_LandingGuidance_label5", () => Core.Landing?.LimitChutesStage); //Stage Limit:
            g.Dim(chutes.gameObject, null, () => Core.Landing == null || !Core.Landing.DeployChutes);
            g.Switch("#MechJeb_LandingGuidance_jettison", () => Core.Landing != null && Core.Landing.JettisonBeforeEntry, v => //Jettison stage before entry
            {
                if (Core.Landing != null) Core.Landing.JettisonBeforeEntry = v;
            });
            g.Switch("#MechJeb_LandingGuidance_checkbox4", () => Core.Landing != null && Core.Landing.RCSAdjustment, v => //Use RCS for small adjustment
            {
                if (Core.Landing != null) Core.Landing.RCSAdjustment = v;
            });
            MechJebModuleThrustController t = Core.Thrust;
            ModernRows.MinThrottle(g, t, 50); //"Keep limited throttle over"

            Precision(w, p);
            PartControl(w, p);

            // while landing: the autopilot's state in a status bar
            Layout running = w.StatusBar(p, () => KodeWindow.Tone.Normal);
            w.Visible(running.gameObject, () => Core.Landing != null && Core.Landing.Enabled);
            RunningLabels(w, running);
        }

        static Layout ModernStage(KodeWindow.PropertyGrid g, string key, Func<EditableInt> ed) => EditUi.Grid(g, key, ed, null, 50);

        // MechJebModuleThrustController.LimiterMinThrottleInfoItem: [x] "Keep limited throttle over" [min] %
        void LowerThrottleLimit(KodeWindow w, UIObject parent)
        {
            GeneralItemBuilders.Build(w, parent, "General:ThrustController.LimiterMinThrottleInfoItem", Core.Thrust);
        }
    }
}
