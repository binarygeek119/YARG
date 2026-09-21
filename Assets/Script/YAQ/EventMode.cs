using System;
using YARG.Gameplay.HUD;
using YARG.Settings;

namespace YARG
{
    /// <summary>
    /// Curated Event Mode flags controlled by YAQ via <c>settings.update</c>.
    /// </summary>
    [Serializable]
    public class EventFlags
    {
        public bool hotMic = true;
        public bool showUpNextHud = true;
        public bool skipMainMenu = true;
        public bool openDifficultySelect = true;
        public bool addTestBots = false;
        public bool noFail = true;

        public static EventFlags Defaults => new();

        public void CopyFrom(EventFlags other)
        {
            if (other == null) return;
            hotMic = other.hotMic;
            showUpNextHud = other.showUpNextHud;
            skipMainMenu = other.skipMainMenu;
            openDifficultySelect = other.openDifficultySelect;
            addTestBots = other.addTestBots;
            noFail = other.noFail;
        }
    }

    /// <summary>
    /// YAQ Event Mode — strips normal menus and drives song selection from the YAQ LAN queue.
    /// Enable via Settings → Experimental → YAQ stream, or launch with
    /// <c>-event-mode</c> / <c>-yaq-event</c> (optional <c>-yaq-url …</c>).
    /// YAQ can suspend/resume with <c>eventmode.exit</c> / <c>eventmode.enter</c> while the bridge stays connected.
    /// </summary>
    public static class EventMode
    {
        public static bool Enabled { get; set; }

        /// <summary>
        /// When true, Event Mode behaviors are paused by YAQ but the WebSocket stays up.
        /// </summary>
        public static bool Suspended { get; set; }

        public static string YaqWebSocketUrl { get; set; } =
            "ws://127.0.0.1:3000/ws?role=yarg";

        public static EventFlags Flags { get; } = EventFlags.Defaults;

        public static bool StreamConnected =>
            Enabled ||
            CommandLineArgs.YaqEvent ||
            (SettingsManager.Settings?.YaqStreamEnabled.Value ?? false);

        public static bool IsActive => StreamConnected && !Suspended;

        /// <summary>
        /// Remember the player's No Fail setting so Event Mode can restore it on exit.
        /// </summary>
        private static NoFailMode? _noFailRestore;

        /// <summary>
        /// Put gameplay in No Fail while Event Mode is active and the flag is on.
        /// Restores the previous setting when Event Mode ends or the flag is cleared.
        /// </summary>
        public static void SyncNoFailSetting()
        {
            var setting = SettingsManager.Settings?.NoFail;
            if (setting == null) return;

            if (IsActive && Flags.noFail)
            {
                _noFailRestore ??= setting.Value;
                if (setting.Value == NoFailMode.Off)
                {
                    setting.Value = NoFailMode.On;
                }
                return;
            }

            RestoreNoFailSetting();
        }

        public static void RestoreNoFailSetting()
        {
            var setting = SettingsManager.Settings?.NoFail;
            if (setting == null) return;
            if (_noFailRestore is not { } previous) return;
            if (setting.Value != previous)
            {
                setting.Value = previous;
            }
            _noFailRestore = null;
        }

        /// <summary>
        /// Idle destination while Event Mode is active. Gameplay, score, and
        /// difficulty select still use their own scenes.
        /// </summary>
        public static SceneIndex HubScene => IsActive ? SceneIndex.Event : SceneIndex.Menu;
    }
}
