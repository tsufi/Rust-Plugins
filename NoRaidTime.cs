using Oxide.Core;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("NoRaidTime", "Tsufi", "2.4.0")]
    [Description("Blocks raiding during configured hours with logging, UI toggle, TC grace, decay exceptions, and admin override.")]

    public class NoRaidTime : RustPlugin
    {
        private TimeSpan noRaidStart;
        private TimeSpan noRaidEnd;
        private int timeZoneOffset;
        private int tcGraceMinutes;
        private int raidCooldownMinutes;
        private int decayBypassHours;

        private bool overrideActive = false;
        private bool overrideBlock = false;

        private Dictionary<ulong, DateTime> tcPlacedTimes = new();
        private Dictionary<BuildingPrivlidge, DateTime> lastRaidDamage = new();
        private HashSet<ulong> uiHidden = new(); // UI toggle tracking

        private const string PanelName = "RaidBlockUI";

        void OnServerInitialized()
        {
            LoadConfigValues();
            permission.RegisterPermission("noraidtime.admin", this);
            timer.Every(30f, UpdateAllUI);
            timer.Every(60f, CheckForConfigReload);
            timer.Every(3600f, AnnounceRemainingBlockTime);
        }

        void OnPlayerInit(BasePlayer player) => timer.Once(5f, () => UpdateUI(player));

        void OnEntityBuilt(Planner planner, GameObject go)
        {
            var entity = go.ToBaseEntity();
            if (entity is BuildingPrivlidge)
                tcPlacedTimes[entity.net.ID.Value] = DateTime.UtcNow;
        }

        object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info?.InitiatorPlayer == null) return null;
            if (!IsStructure(entity)) return null;

            var player = info.InitiatorPlayer;
            var priv = entity.GetBuildingPrivilege();

            if (priv != null)
            {
                if (priv.IsAuthed(player))
                    lastRaidDamage[priv] = DateTime.UtcNow;

                bool authed = priv.IsAuthed(player);
                bool decayFree = IsBuildingDecaying(priv);
                bool grace = IsInTCGracePeriod(priv);
                bool raidCooldown = IsInRaidCooldown(priv);
                bool raidBlocked = IsRaidBlocked(priv);

                string entityName = entity.ShortPrefabName;
                string log = $"Player '{player.displayName}' tried to raid '{entityName}' | TC: Found | Authed: {authed} | Grace: {grace} | Cooldown: {raidCooldown} | DecayFree: {decayFree} | Blocked: {raidBlocked}";
                LogToFile(log);

                if (authed || grace || !raidBlocked)
                    return null;

                if (!(decayFree || raidCooldown))
                {
                    info.damageTypes.ScaleAll(0f);
                    info.HitMaterial = 0;
                    SendReply(player, "<color=orange>Raiding is currently blocked! You don’t have building privilege here.</color>");
                    return true;
                }
            }

            return null;
        }

        [ChatCommand("raidstatus")]
        void RaidStatusCommand(BasePlayer player, string command, string[] args)
        {
            if (args.Length > 0 && args[0].ToLower() == "ui")
            {
                if (uiHidden.Contains(player.userID))
                {
                    uiHidden.Remove(player.userID);
                    SendReply(player, "<color=green>Raid status HUD enabled.</color>");
                    UpdateUI(player);
                }
                else
                {
                    uiHidden.Add(player.userID);
                    CuiHelper.DestroyUi(player, PanelName);
                    SendReply(player, "<color=yellow>Raid status HUD disabled.</color>");
                }
                return;
            }

            bool blocked = IsRaidBlocked();
            string status = blocked ? "<color=red>Raiding is currently BLOCKED.</color>" : "<color=green>Raiding is currently ALLOWED.</color>";
            string time = "<color=yellow>Block Time: " + noRaidStart.ToString(@"hh\:mm") + " - " + noRaidEnd.ToString(@"hh\:mm") + "</color>";
            string overrideNote = overrideActive ? $"<color=orange>Admin override: {(overrideBlock ? "Blocked" : "Allowed")}</color>" : "";
            string remaining = GetTimeUntilNextChange(blocked);
            SendReply(player, $"{status}\n{overrideNote}\n{time}\n{remaining}\n /raidstatus ui Hides UI");
        }

        [ChatCommand("raidblock")]
        void RaidBlockCommand(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, "noraidtime.admin"))
            {
                SendReply(player, "<color=red>You don’t have permission.</color>");
                return;
            }

            if (args.Length == 0)
            {
                bool blocked = IsRaidBlocked();
                string status = blocked ? "<color=red>BLOCKED</color>" : "<color=green>ALLOWED</color>";
                string time = "<color=yellow>Block Time: " + noRaidStart.ToString(@"hh\:mm") + " - " + noRaidEnd.ToString(@"hh\:mm") + "</color>";
                string overrideNote = overrideActive ? $"<color=orange>Admin override: {(overrideBlock ? "Blocked" : "Allowed")}</color>" : "<color=orange>No override active.</color>";
                string remaining = GetTimeUntilNextChange(blocked);
                SendReply(player, $"Status: {status}\n{overrideNote}\n{time}\n{remaining}");
                return;
            }

            switch (args[0].ToLower())
            {
                case "on":
                    overrideActive = true;
                    overrideBlock = true;
                    SendReply(player, "<color=orange>Raid block forced ON by admin override.</color>");
                    break;
                case "off":
                    overrideActive = true;
                    overrideBlock = false;
                    SendReply(player, "<color=orange>Raid block forced OFF by admin override.</color>");
                    break;
                case "reset":
                    overrideActive = false;
                    overrideBlock = false;
                    SendReply(player, "<color=orange>Raid block override reset. Time-based blocking is now active.</color>");
                    break;
                default:
                    SendReply(player, "Usage: /raidblock <on|off|reset>");
                    break;
            }

            UpdateAllUI();
        }

        private void UpdateUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, PanelName);

            if (uiHidden.Contains(player.userID))
                return;

            bool blocked = IsRaidBlocked();
            string text = blocked ? "⛔ Raid Block Active" : "✅ Raiding Allowed";
            string color = blocked ? "0.85 0.3 0.3 0.8" : "0.3 0.85 0.3 0.8";

            var elements = new CuiElementContainer();
            elements.Add(new CuiPanel
            {
                Image = { Color = color },
                RectTransform = { AnchorMin = "0.01 0.95", AnchorMax = "0.25 0.99" },
                CursorEnabled = false
            }, "Hud", PanelName);

            elements.Add(new CuiLabel
            {
                Text = { Text = text, FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, PanelName);

            CuiHelper.AddUi(player, elements);
        }

        private void UpdateAllUI()
        {
            foreach (var player in BasePlayer.activePlayerList)
                UpdateUI(player);
        }

        private void LogToFile(string message)
        {
            LogToFile("NoRaidTime", $"[{DateTime.Now:HH:mm:ss}] {message}", this);
        }

        private bool IsRaidBlocked(BuildingPrivlidge tc = null)
        {
            if (overrideActive) return overrideBlock;

            var now = DateTime.UtcNow.AddHours(timeZoneOffset).TimeOfDay;
            if (noRaidStart < noRaidEnd)
                return now >= noRaidStart && now < noRaidEnd;
            else
                return now >= noRaidStart || now < noRaidEnd;
        }

        private bool IsInTCGracePeriod(BuildingPrivlidge tc)
        {
            if (tcPlacedTimes.TryGetValue(tc.net.ID.Value, out var placedAt))
                return (DateTime.UtcNow - placedAt).TotalMinutes < tcGraceMinutes;
            return false;
        }

        private bool IsInRaidCooldown(BuildingPrivlidge tc)
        {
            if (lastRaidDamage.TryGetValue(tc, out var lastHit))
                return (DateTime.UtcNow - lastHit).TotalMinutes < raidCooldownMinutes;
            return false;
        }

        private bool IsBuildingDecaying(BuildingPrivlidge priv)
        {
            var decay = priv.GetComponent<DecayEntity>();
            if (decay != null)
            {
                if (decay.lastDecayTick <= 0) return false;
                if ((Time.realtimeSinceStartup - decay.lastDecayTick) > decayBypassHours * 3600f)
                    return true;
            }
            return false;
        }

        private bool IsStructure(BaseEntity entity)
        {
            if (entity is BuildingBlock) return true;

            string[] keywords = {
                "wall", "foundation", "floor", "door", "window",
                "roof", "ladder", "gate", "barricade", "turret"
            };

            foreach (var keyword in keywords)
                if (entity.ShortPrefabName.ToLower().Contains(keyword))
                    return true;

            return false;
        }

        private string GetTimeUntilNextChange(bool currentlyBlocked)
        {
            var now = DateTime.UtcNow.AddHours(timeZoneOffset).TimeOfDay;
            TimeSpan until;

            if (noRaidStart < noRaidEnd)
                until = currentlyBlocked ? noRaidEnd - now : noRaidStart - now;
            else
            {
                if (currentlyBlocked && now < noRaidEnd)
                    until = noRaidEnd - now;
                else if (currentlyBlocked)
                    until = TimeSpan.FromDays(1) - now + noRaidEnd;
                else if (now < noRaidStart)
                    until = noRaidStart - now;
                else
                    until = TimeSpan.FromDays(1) - now + noRaidStart;
            }

            if (until.TotalMinutes < 0) until = TimeSpan.FromMinutes(0);
            return $"<color=yellow>Next change in {until.Hours}h {until.Minutes}m</color>";
        }

        private void AnnounceRemainingBlockTime()
        {
            if (!IsRaidBlocked()) return;

            var now = DateTime.UtcNow.AddHours(timeZoneOffset).TimeOfDay;
            TimeSpan untilEnd;

            if (noRaidStart < noRaidEnd)
                untilEnd = noRaidEnd - now;
            else if (now < noRaidEnd)
                untilEnd = noRaidEnd - now;
            else
                untilEnd = TimeSpan.FromDays(1) - now + noRaidEnd;

            int hours = Math.Max(1, (int)Math.Ceiling(untilEnd.TotalHours));
            Server.Broadcast($"<color=orange>Raid blocking is active. Raiding allowed again in {hours} hour{(hours == 1 ? "" : "s")}.</color>");
        }

        protected override void LoadDefaultConfig()
        {
            Config["NoRaidStart"] = "22:00";
            Config["NoRaidEnd"] = "08:00";
            Config["TimeZoneOffsetHours"] = 0;
            Config["TCGraceMinutes"] = 30;
            Config["RaidCooldownMinutes"] = 15;
            Config["DecayBypassHours"] = 6;
            SaveConfig();
        }

        private void CheckForConfigReload()
        {
            string start = Config["NoRaidStart"].ToString();
            string end = Config["NoRaidEnd"].ToString();
            int offset = Convert.ToInt32(Config["TimeZoneOffsetHours"]);

            if (start != noRaidStart.ToString() || end != noRaidEnd.ToString() || offset != timeZoneOffset)
            {
                bool preserveOverride = overrideActive;
                bool preserveOverrideBlock = overrideBlock;

                LoadConfigValues();

                overrideActive = preserveOverride;
                overrideBlock = preserveOverrideBlock;
            
            }
        }

        private void LoadConfigValues()
        {
            noRaidStart = TimeSpan.Parse(Config["NoRaidStart"].ToString());
            noRaidEnd = TimeSpan.Parse(Config["NoRaidEnd"].ToString());
            timeZoneOffset = Convert.ToInt32(Config["TimeZoneOffsetHours"]);
            tcGraceMinutes = Convert.ToInt32(Config["TCGraceMinutes"]);
            raidCooldownMinutes = Convert.ToInt32(Config["RaidCooldownMinutes"]);
            decayBypassHours = Convert.ToInt32(Config["DecayBypassHours"]);
        }
        public string GetRaidStatus()
{
    bool blocked = IsRaidBlocked();
    return blocked ? "⛔ Raiding is currently BLOCKED." : "✅ Raiding is currently ALLOWED.";
}
    }
    
}

