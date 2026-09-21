using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;
using YARG.Core;
using YARG.Core.Engine.Guitar;
using YARG.Core.Game;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Helpers.Extensions;
using YARG.Input;
using YARG.Menu;
using YARG.Menu.Main;
using YARG.Menu.MusicLibrary;
using YARG.Menu.ScoreScreen;
using YARG.Menu.Persistent;
using YARG.Player;
using YARG.Settings;
using YARG.Song;

namespace YARG.YAQ
{
    /// <summary>
    /// Event-mode runtime: idle HUD with up-next, YAQ bridge, hot mics, and song launch into Difficulty Select.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class EventModeController : MonoBehaviour
    {
        public static EventModeController Instance { get; private set; }

        private YaqBridgeClient _bridge;
        private YaqQueuePreview _preview = new();
        private YaqPlaySet _currentSet;
        private List<YaqSetPlayer> _currentPlayers = new();
        private readonly Dictionary<string, YargProfile> _venueProfiles = new();
        private List<YaqVenueProfile> _venueSlots = new();
        private string _phase = "idle";
        private string _status = "Connecting to YAQ…";
        private string _pendingSetId;
        private readonly HashSet<string> _readyKeys = new(StringComparer.OrdinalIgnoreCase);
        private string _readySetId;
        private bool _launchQueued;
        private bool _librarySynced;
        private GUIStyle _titleStyle;
        private GUIStyle _artistStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _playerNameStyle;
        private GUIStyle _avatarInitialStyle;
        private GUIStyle _mutedStyle;
        private GUIStyle _qrCaptionStyle;
        private GUIStyle _nextTitleStyle;
        private GUIStyle _nextArtistStyle;
        private GUIStyle _nextPlayersStyle;
        private GUIStyle _nextCaptionStyle;
        private GUIStyle _readyStyle;
        private GUIStyle _chipNameStyle;
        private GUIStyle _fitScratchStyle;
        private readonly ConcurrentQueue<Action> _mainThread = new();

        private Texture2D _currentCover;
        private Texture2D _previewCover;
        private string _currentCoverHash;
        private string _previewCoverHash;
        private int _currentCoverGeneration;
        private int _previewCoverGeneration;
        private bool _currentCoverFlipped;
        private bool _previewCoverFlipped;
        private float _nextCoverRetryAt;

        private Texture2D _qrTexture;
        private string _qrJoinUrl;
        private int _qrLoadGeneration;
        private float _nextQrRetryAt;
        private readonly Dictionary<string, Sprite> _instrumentIcons = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Texture2D> _instrumentIconTex = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Texture2D> _hudShapes = new();
        private Texture2D _whiteCircle;

        private static readonly Color PanelFill = new(0.04f, 0.09f, 0.14f, 0.94f);
        private static readonly Color PanelBorder = new(0.72f, 0.76f, 0.80f, 0.95f);
        private static readonly Color CardTeal = new(0.07f, 0.55f, 0.56f, 1f);
        private static readonly Color ReadyGreen = new(0.20f, 0.78f, 0.32f, 1f);
        private static readonly Color ReadyRed = new(0.86f, 0.12f, 0.20f, 1f);
        private static readonly Color ReadyGlyph = new(0.95f, 0.93f, 0.28f, 1f);
        private static readonly Color NextBarDark = new(0.42f, 0.02f, 0.07f, 1f);
        private static readonly Color NextBarLight = new(0.98f, 0.30f, 0.36f, 1f);
        private static readonly Color QrGoldDark = new(0.45f, 0.32f, 0.04f, 1f);
        private static readonly Color QrGoldLight = new(0.96f, 0.84f, 0.32f, 1f);
        private static readonly Color NextArtistBlue = new(0.45f, 0.72f, 0.95f, 1f);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            // CLI only here — settings load later and invoke YaqStreamEnabledCallback.
            if (!CommandLineArgs.YaqEvent) return;
            SetStreamEnabled(true);
        }

        /// <summary>
        /// Connect or disconnect the YAQ websocket stream (and event-mode HUD/menu gating).
        /// </summary>
        public static void SetStreamEnabled(bool enabled)
        {
            if (enabled)
            {
                EnsureExists();
                Instance?.StartBridge();
                Instance?.GoToEventSceneIfIdle();
            }
            else
            {
                Instance?.StopBridge();
                Instance?.GoToMenuSceneIfIdle();
            }
        }

        private static void EnsureExists()
        {
            if (Instance != null) return;

            var go = new GameObject("YAQ Event Mode");
            DontDestroyOnLoad(go);
            go.AddComponent<EventModeController>();
        }

        private void Awake()
        {
            Instance = this;

            if (!string.IsNullOrEmpty(CommandLineArgs.YaqUrl))
            {
                EventMode.YaqWebSocketUrl = CommandLineArgs.YaqUrl;
            }

            _bridge = new YaqBridgeClient();
            InputManager.MenuInput += OnMenuReadyInput;
            _bridge.Connected += () => Enqueue(() =>
            {
                _status = EventMode.Suspended
                    ? "Connected to YAQ — Event Mode off"
                    : "Connected to YAQ — waiting for next set";
                _phase = "idle";
                ReportFlags();
                ReportEventModeState();
                if (!EventMode.Suspended)
                {
                    SendState("idle");
                    TrySyncLibrary();
                    RequestQr();
                }
            });
            _bridge.Disconnected += () => Enqueue(() =>
            {
                _status = "YAQ disconnected — retrying…";
                _librarySynced = false;
            });
            _bridge.MessageReceived += msg => Enqueue(() => OnBridgeMessage(msg));
        }

        private void StartBridge()
        {
            EventMode.Enabled = true;
            EventMode.Suspended = false;
            _status = "Connecting to YAQ…";
            _bridge?.Start(EventMode.YaqWebSocketUrl);
            ApplyMainMenuVisibility();
            EnsureHotMics();
            ReportEventModeState();
            RequestQr();
        }

        private void StopBridge()
        {
            EventMode.Enabled = false;
            EventMode.Suspended = false;
            _bridge?.Stop();
            _librarySynced = false;
            _pendingSetId = null;
            _currentSet = null;
            _currentPlayers = new List<YaqSetPlayer>();
            _preview = new YaqQueuePreview();
            _phase = "idle";
            ClearReadyState();
            _status = "YAQ stream off";
            ClearCovers();
            ClearQr();
            ClearHudShapes();
            YaqProfileAvatar.ClearCache();
            ApplyMainMenuVisibility();
        }

        /// <summary>
        /// Enter Event Mode behaviors while keeping the WebSocket connected.
        /// </summary>
        public void EnterEventMode()
        {
            EventMode.Suspended = false;
            EventMode.Enabled = true;
            _status = "Event Mode on — waiting for next set";
            _phase = "idle";
            ApplyMainMenuVisibility();
            EnsureHotMics();
            ApplyVenueProfiles(_venueSlots, EventMode.Flags.addTestBots);
            TrySyncLibrary();
            SendState("idle");
            ReportEventModeState();
            RequestQr();
            GoToEventSceneIfIdle();
            YargLogger.LogInfo("YAQ entered Event Mode");
        }

        /// <summary>
        /// Exit Event Mode behaviors but keep the WebSocket connected for re-entry.
        /// </summary>
        public void ExitEventMode()
        {
            EventMode.Suspended = true;
            _pendingSetId = null;
            _currentSet = null;
            _currentPlayers = new List<YaqSetPlayer>();
            _phase = "idle";
            ClearReadyState();
            _status = "Event Mode off (bridge still connected)";
            ClearCovers();
            RestoreVenueProfileNames();
            ApplyMainMenuVisibility();
            ReportEventModeState();
            GoToMenuSceneIfIdle();
            YargLogger.LogInfo("YAQ exited Event Mode (bridge remains connected)");
        }

        private void ReportEventModeState()
        {
            _bridge?.Send(new
            {
                type = "eventmode.state",
                enabled = EventMode.IsActive,
                suspended = EventMode.Suspended
            });
        }

        private void OnDestroy()
        {
            InputManager.MenuInput -= OnMenuReadyInput;
            ClearCovers();
            ClearQr();
            ClearHudShapes();
            YaqProfileAvatar.ClearCache();
            _bridge?.Dispose();
            if (Instance == this) Instance = null;
        }

        private void Enqueue(Action action) => _mainThread.Enqueue(action);

        private void Update()
        {
            while (_mainThread.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    YargLogger.LogException(ex, "YAQ event mode main-thread action failed");
                }
            }

            if (!EventMode.Enabled) return;

            if (EventMode.Suspended) return;

            if (!_librarySynced && SongContainer.Count > 0)
            {
                TrySyncLibrary();
            }

            EnsureHotMics();
            EnableSeatedPlayerInputs();

            if (_qrTexture == null && Time.unscaledTime >= _nextQrRetryAt)
            {
                _nextQrRetryAt = Time.unscaledTime + 5f;
                RequestQr();
            }

            if (Time.unscaledTime >= _nextCoverRetryAt)
            {
                _nextCoverRetryAt = Time.unscaledTime + 3f;
                RetryMissingCovers();
            }
        }

        internal void DrawIdleHud()
        {
            if (!EventMode.IsActive || !EventMode.Flags.showUpNextHud) return;
            if (GlobalVariables.Instance == null ||
                GlobalVariables.Instance.CurrentScene != SceneIndex.Event)
            {
                return;
            }

            EnsureStyles();
            var layout = ComputeHudLayout(Screen.width, Screen.height);

            DrawCurrentHeader(layout.TitleRect);
            if (TryGetCurrentSong(out var cover, out _, out _, out _))
            {
                DrawAlbumArt(layout.ArtRect, cover);
            }

            DrawPlayerPanel(layout.PlayersRect, CurrentHudPlayers());
            DrawNextBar(layout.NextRect);
            DrawQrBlock(layout.QrRect);
        }

        internal readonly struct EventHudLayout
        {
            public readonly Rect TitleRect;
            public readonly Rect ArtRect;
            public readonly Rect PlayersRect;
            public readonly Rect NextRect;
            public readonly Rect QrRect;

            public EventHudLayout(
                Rect titleRect,
                Rect artRect,
                Rect playersRect,
                Rect nextRect,
                Rect qrRect)
            {
                TitleRect = titleRect;
                ArtRect = artRect;
                PlayersRect = playersRect;
                NextRect = nextRect;
                QrRect = qrRect;
            }
        }

        /// <summary>
        /// Mockup layout: title top-left, album + rounded player panel, yellow QR
        /// bottom-right, red UP NEXT bar flush to the bottom of the Game view.
        /// </summary>
        internal static EventHudLayout ComputeHudLayout(float screenW, float screenH)
        {
            const float artMax = 260f;
            const float qrMax = 220f;
            const float gap = 18f;

            var pad = Mathf.Clamp(Mathf.Min(screenW, screenH) * 0.04f, 16f, 40f);
            var areaX = pad;
            var areaY = pad;
            var areaW = Mathf.Max(1f, screenW - pad * 2f);

            var nextH = Mathf.Clamp(screenH * 0.16f, 100f, 136f);
            var qrSize = Mathf.Clamp(Mathf.Min(qrMax, areaW * 0.22f, screenH * 0.36f), 150f, qrMax);
            var qrX = Mathf.Max(areaX, screenW - pad - qrSize);
            var contentW = Mathf.Max(0f, qrX - gap - areaX);

            var nextRect = new Rect(0f, screenH - nextH, qrX, nextH);
            var qrRect = new Rect(qrX, screenH - qrSize, qrSize, qrSize);

            var titleH = Mathf.Clamp(Mathf.Max(1f, nextRect.y - areaY) * 0.18f, 72f, 108f);
            var titleRect = new Rect(areaX, areaY, contentW, titleH);

            var midY = areaY + titleH + gap;
            var midBottom = nextRect.y - gap;
            var midH = Mathf.Max(0f, midBottom - midY);
            var artSize = Mathf.Min(artMax, midH, contentW * 0.34f);
            var artRect = new Rect(areaX, midY, artSize, artSize);
            var playersRect = new Rect(
                areaX + artSize + gap,
                midY,
                Mathf.Max(0f, contentW - artSize - gap),
                midH);

            return new EventHudLayout(titleRect, artRect, playersRect, nextRect, qrRect);
        }

        private bool TryGetCurrentSong(
            out Texture2D cover,
            out string title,
            out string artist,
            out List<string> names)
        {
            cover = null;
            title = null;
            artist = null;
            names = null;

            if (_currentSet != null && (_phase == "ready" || _phase == "score"))
            {
                cover = _currentCover;
                title = _currentSet.songName;
                artist = _currentSet.songArtist;
                names = NamesFrom(_currentPlayers?.Select(player => player?.name));
                return !string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(artist);
            }

            // Idle kiosk: the on-deck preview is the featured current song.
            if (_preview == null ||
                (string.IsNullOrEmpty(_preview.songName) && string.IsNullOrEmpty(_preview.songArtist)))
            {
                return false;
            }

            cover = _previewCover;
            title = _preview.songName;
            artist = _preview.songArtist;
            names = NamesFrom(_preview.players?.Select(player => player?.name));
            return true;
        }

        private bool TryGetNextSong(out string title, out string artist, out List<string> names)
        {
            title = null;
            artist = null;
            names = null;

            var featuredSetId = _currentSet != null && (_phase == "ready" || _phase == "score")
                ? _currentSet.id
                : _preview?.setId;

            if (_preview != null &&
                (!string.IsNullOrEmpty(_preview.songName) || !string.IsNullOrEmpty(_preview.songArtist)) &&
                _preview.setId != featuredSetId)
            {
                title = _preview.songName;
                artist = _preview.songArtist;
                names = NamesFrom(_preview.players?.Select(player => player?.name));
                return true;
            }

            var following = _preview?.following;
            if (following != null &&
                (!string.IsNullOrEmpty(following.songName) || !string.IsNullOrEmpty(following.songArtist)))
            {
                title = following.songName;
                artist = following.songArtist;
                names = NamesFrom(following.players?.Select(player => player?.name));
                return true;
            }

            return false;
        }

        private static List<string> NamesFrom(IEnumerable<string> names)
        {
            return names?.Where(name => !string.IsNullOrEmpty(name)).ToList() ?? new List<string>();
        }

        private static string FormatSongLine(string title, string artist)
        {
            if (string.IsNullOrEmpty(title)) return artist ?? string.Empty;
            if (string.IsNullOrEmpty(artist)) return title;
            return $"{title} — {artist}";
        }

        private void DrawCurrentHeader(Rect titleRect)
        {
            if (!TryGetCurrentSong(out _, out var title, out var artist, out _))
            {
                DrawFittedLabel(titleRect, "Waiting for the next group…", _bodyStyle, 16);
                return;
            }

            var hasTitle = !string.IsNullOrEmpty(title);
            var hasArtist = !string.IsNullOrEmpty(artist);
            if (hasTitle && hasArtist)
            {
                const float lineGap = 4f;
                var titleH = Mathf.Max(1f, (titleRect.height - lineGap) * 0.62f);
                var titleArea = new Rect(titleRect.x, titleRect.y, titleRect.width, titleH);
                var artistArea = new Rect(
                    titleRect.x,
                    titleRect.y + titleH + lineGap,
                    titleRect.width,
                    Mathf.Max(1f, titleRect.height - titleH - lineGap));
                DrawFittedLabel(titleArea, title, _titleStyle, 18);
                DrawFittedLabel(artistArea, artist, _artistStyle, 14);
                return;
            }

            DrawFittedLabel(titleRect, hasTitle ? title : artist, _titleStyle, 18);
        }

        private void DrawFittedLabel(Rect rect, string text, GUIStyle baseStyle, int minSize)
        {
            if (string.IsNullOrEmpty(text) || rect.width < 1f || rect.height < 1f) return;

            _fitScratchStyle ??= new GUIStyle(baseStyle);
            _fitScratchStyle.font = baseStyle.font;
            _fitScratchStyle.fontStyle = baseStyle.fontStyle;
            _fitScratchStyle.normal.textColor = baseStyle.normal.textColor;
            _fitScratchStyle.alignment = TextAnchor.MiddleLeft;
            _fitScratchStyle.wordWrap = false;
            _fitScratchStyle.clipping = TextClipping.Clip;
            _fitScratchStyle.padding = new RectOffset(0, 0, 0, 0);
            _fitScratchStyle.fontSize = FontSizeToFit(
                _fitScratchStyle,
                text,
                rect.width,
                rect.height,
                minSize,
                baseStyle.fontSize);
            GUI.Label(rect, text, _fitScratchStyle);
        }

        /// <summary>
        /// Largest font size at or below <paramref name="preferredSize"/> that fits in the box.
        /// </summary>
        internal static int FontSizeToFit(
            GUIStyle style,
            string text,
            float maxWidth,
            float maxHeight,
            int minSize,
            int preferredSize)
        {
            if (style == null || string.IsNullOrEmpty(text)) return preferredSize;

            minSize = Mathf.Max(1, minSize);
            preferredSize = Mathf.Max(minSize, preferredSize);
            var content = new GUIContent(text);
            var originalSize = style.fontSize;
            var originalWrap = style.wordWrap;
            try
            {
                style.wordWrap = false;
                var lo = minSize;
                var hi = preferredSize;
                var best = minSize;
                while (lo <= hi)
                {
                    var mid = (lo + hi) / 2;
                    style.fontSize = mid;
                    var size = style.CalcSize(content);
                    if (size.x <= maxWidth && size.y <= maxHeight)
                    {
                        best = mid;
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }

                return best;
            }
            finally
            {
                style.fontSize = originalSize;
                style.wordWrap = originalWrap;
            }
        }

        private void DrawAlbumArt(Rect rect, Texture2D texture)
        {
            if (rect.width < 8f || rect.height < 8f) return;

            if (texture != null)
            {
                var flipped = (texture == _previewCover && _previewCoverFlipped) ||
                              (texture == _currentCover && _currentCoverFlipped);
                if (flipped)
                {
                    GUI.DrawTextureWithTexCoords(rect, texture, new Rect(0f, 1f, 1f, -1f));
                }
                else
                {
                    GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit);
                }
            }
            else
            {
                GUI.Box(rect, GUIContent.none);
            }
        }

        private readonly struct HudPlayer
        {
            public readonly string Name;
            public readonly string Id;
            public readonly string SlotId;
            public readonly string Instrument;
            public readonly bool Ready;

            public HudPlayer(string name, string id, string slotId, string instrument, bool ready)
            {
                Name = name ?? string.Empty;
                Id = id;
                SlotId = slotId;
                Instrument = instrument;
                Ready = ready;
            }
        }

        private List<HudPlayer> CurrentHudPlayers()
        {
            if (_currentSet != null && (_phase == "ready" || _phase == "score"))
            {
                return HudPlayersFromSet(_currentPlayers);
            }

            return HudPlayersFromPreview(_preview?.players);
        }

        private List<HudPlayer> NextHudPlayers()
        {
            var featuredSetId = _currentSet != null && (_phase == "ready" || _phase == "score")
                ? _currentSet.id
                : _preview?.setId;

            if (_preview != null &&
                (!string.IsNullOrEmpty(_preview.songName) || !string.IsNullOrEmpty(_preview.songArtist)) &&
                _preview.setId != featuredSetId)
            {
                return HudPlayersFromPreview(_preview.players);
            }

            return HudPlayersFromPreview(_preview?.following?.players);
        }

        private List<HudPlayer> HudPlayersFromSet(List<YaqSetPlayer> players)
        {
            var list = new List<HudPlayer>();
            if (players == null) return list;
            foreach (var player in players)
            {
                if (player == null || string.IsNullOrEmpty(player.name)) continue;
                list.Add(new HudPlayer(
                    player.name,
                    player.id ?? player.slotId,
                    player.slotId,
                    player.instrument,
                    PlayerIsReady(player.id, player.slotId, player.name, player.isBot)));
            }

            return list;
        }

        private List<HudPlayer> HudPlayersFromPreview(List<YaqPreviewPlayer> players)
        {
            var list = new List<HudPlayer>();
            if (players == null) return list;
            foreach (var player in players)
            {
                if (player == null || string.IsNullOrEmpty(player.name)) continue;
                list.Add(new HudPlayer(
                    player.name,
                    player.id ?? player.slotId,
                    player.slotId,
                    player.instrument,
                    PlayerIsReady(player.id, player.slotId, player.name, player.isBot)));
            }

            return list;
        }

        internal static string ReadyBarLabel(bool ready)
        {
            return ready ? "Ready" : "Ready ?";
        }

        private bool PlayerIsReady(string id, string slotId, string name, bool isBot)
        {
            if (isBot) return true;
            if (_phase is "playing" or "score") return true;
            return HasReadyKey(id) || HasReadyKey(slotId) || HasReadyKey(name);
        }

        private bool HasReadyKey(string key)
        {
            return !string.IsNullOrEmpty(key) && _readyKeys.Contains(key);
        }

        private void BindReadySet(string setId)
        {
            if (string.IsNullOrEmpty(setId))
            {
                ClearReadyState();
                return;
            }

            if (string.Equals(_readySetId, setId, StringComparison.Ordinal)) return;

            _readySetId = setId;
            _readyKeys.Clear();
            _launchQueued = false;
        }

        private void ClearReadyState()
        {
            _readySetId = null;
            _readyKeys.Clear();
            _launchQueued = false;
        }

        private void MarkPlayerReady(HudPlayer player)
        {
            if (!string.IsNullOrEmpty(player.Id)) _readyKeys.Add(player.Id);
            if (!string.IsNullOrEmpty(player.SlotId)) _readyKeys.Add(player.SlotId);
            if (!string.IsNullOrEmpty(player.Name)) _readyKeys.Add(player.Name);
            YargLogger.LogFormatInfo("YAQ Event HUD ready: {0}", player.Name);
        }

        private bool AllFeaturedPlayersReady()
        {
            var players = CurrentHudPlayers();
            if (players.Count == 0) return false;
            foreach (var player in players)
            {
                if (!player.Ready) return false;
            }

            return true;
        }

        private void TryLaunchWhenAllReady()
        {
            if (_launchQueued) return;
            if (!EventMode.IsActive) return;
            if (_currentSet == null || _phase != "ready") return;
            if (!AllFeaturedPlayersReady()) return;

            _launchQueued = true;
            _status = $"Ready: {_currentSet.songArtist} — {_currentSet.songName}";
            YargLogger.LogFormatInfo(
                "YAQ Event HUD all players ready — launching {0}",
                _currentSet.songName);

            if (GlobalVariables.Instance != null &&
                GlobalVariables.Instance.CurrentScene != SceneIndex.Menu)
            {
                GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
            }

            if (EventMode.Flags.openDifficultySelect)
            {
                StartCoroutine(OpenReadyWhenPossible());
            }
        }

        private void OnMenuReadyInput(YargPlayer player, ref GameInput input)
        {
            if (!input.Button) return;
            if ((MenuAction) input.Action != MenuAction.Green) return;
            if (!EventMode.IsActive) return;
            if (GlobalVariables.Instance == null ||
                GlobalVariables.Instance.CurrentScene != SceneIndex.Event)
            {
                return;
            }

            if (_phase is "playing" or "score") return;

            var players = CurrentHudPlayers();
            if (players.Count == 0) return;

            if (player == null)
            {
                HudPlayer? fallback = null;
                var unready = 0;
                foreach (var hud in players)
                {
                    if (hud.Ready) continue;
                    unready++;
                    fallback = hud;
                }

                if (unready == 1 && fallback.HasValue)
                {
                    MarkPlayerReady(fallback.Value);
                    TryLaunchWhenAllReady();
                }

                return;
            }

            if (!TryMatchHudPlayer(player, players, out var matched)) return;

            MarkPlayerReady(matched);
            TryLaunchWhenAllReady();
        }

        private bool TryMatchHudPlayer(YargPlayer yargPlayer, List<HudPlayer> players, out HudPlayer match)
        {
            match = default;
            var profile = yargPlayer?.Profile;
            if (profile == null || players == null || players.Count == 0) return false;

            var slotId = SlotIdForProfile(profile);
            foreach (var hud in players)
            {
                if (string.IsNullOrEmpty(slotId)) continue;
                if (string.Equals(hud.SlotId, slotId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(hud.Id, slotId, StringComparison.OrdinalIgnoreCase))
                {
                    match = hud;
                    return true;
                }
            }

            foreach (var hud in players)
            {
                if (string.IsNullOrEmpty(profile.Name)) continue;
                if (string.Equals(hud.Name, profile.Name, StringComparison.OrdinalIgnoreCase))
                {
                    match = hud;
                    return true;
                }
            }

            var instrument = profile.CurrentInstrument;
            HudPlayer? unique = null;
            var matches = 0;
            foreach (var hud in players)
            {
                if (!TryParseHudInstrument(hud.Instrument, out var parsed) || parsed != instrument)
                {
                    continue;
                }

                unique = hud;
                matches++;
            }

            if (matches == 1 && unique.HasValue)
            {
                match = unique.Value;
                return true;
            }

            foreach (var hud in players)
            {
                if (hud.Ready) continue;
                if (!TryParseHudInstrument(hud.Instrument, out var parsed) || parsed != instrument)
                {
                    continue;
                }

                match = hud;
                return true;
            }

            return false;
        }

        private string SlotIdForProfile(YargProfile profile)
        {
            if (profile == null) return null;
            foreach (var pair in _venueProfiles)
            {
                if (pair.Value == profile) return pair.Key;
            }

            return null;
        }

        private void EnableSeatedPlayerInputs()
        {
            if (GlobalVariables.Instance == null ||
                GlobalVariables.Instance.CurrentScene != SceneIndex.Event)
            {
                return;
            }

            try
            {
                foreach (var seated in PlayerContainer.Players)
                {
                    if (seated == null || seated.SittingOut) continue;
                    if (seated.Profile?.IsBot == true) continue;
                    seated.EnableInputs();
                }
            }
            catch
            {
                // Bindings may not be ready during early boot
            }
        }

        private void DrawPlayerPanel(Rect rect, List<HudPlayer> players)
        {
            if (rect.width < 16f || rect.height < 16f) return;

            DrawRounded(rect, 18, PanelFill, PanelBorder, 3);
            if (players == null || players.Count == 0) return;

            const float pad = 16f;
            const float cardH = 88f;
            const float cardW = 248f;
            const float gap = 14f;
            var inner = new Rect(rect.x + pad, rect.y + pad, rect.width - pad * 2f, rect.height - pad * 2f);
            if (inner.width < 8f || inner.height < 8f) return;

            var perRow = Mathf.Max(1, Mathf.FloorToInt((inner.width + gap) / (cardW + gap)));
            for (var i = 0; i < players.Count; i++)
            {
                var col = i % perRow;
                var row = i / perRow;
                var x = inner.x + col * (cardW + gap);
                var y = inner.y + row * (cardH + gap);
                if (y + cardH > inner.yMax + 1f) break;
                DrawPlayerCard(new Rect(x, y, cardW, cardH), players[i]);
            }
        }

        private void DrawPlayerCard(Rect rect, HudPlayer player)
        {
            const float readyH = 28f;
            var topH = Mathf.Max(8f, rect.height - readyH);
            DrawTwoToneRounded(rect, 14, topH, CardTeal, player.Ready ? ReadyGreen : ReadyRed, Color.clear, 0);

            var row = new Rect(rect.x + 12f, rect.y, rect.width - 24f, topH);
            DrawPackedPlayer(row, player, 44f, 40f, _playerNameStyle ?? _bodyStyle);

            var readyRect = new Rect(rect.x + 10f, rect.yMax - readyH, rect.width - 20f, readyH);
            GUI.Label(readyRect, ReadyBarLabel(player.Ready), _readyStyle);
        }

        private void DrawHudAvatar(Rect rect, string name, string id)
        {
            if (rect.width < 2f) return;
            try
            {
                var tex = YaqProfileAvatar.ForPlayer(name, id);
                if (tex != null)
                {
                    GUI.DrawTexture(rect, tex, ScaleMode.ScaleToFit, true);
                    return;
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex, "YAQ profile avatar draw failed");
            }

            DrawCircle(rect, new Color(0.12f, 0.2f, 0.26f, 0.95f));
            if (_avatarInitialStyle != null)
            {
                GUI.Label(rect, InitialGlyph(name), _avatarInitialStyle);
            }
        }

        private float DrawPackedPlayer(Rect row, HudPlayer player, float avatar, float icon, GUIStyle nameStyle)
        {
            var x = row.x;
            var avatarRect = new Rect(x, row.y + (row.height - avatar) * 0.5f, avatar, avatar);
            DrawHudAvatar(avatarRect, player.Name, player.Id);
            x = avatarRect.xMax + 10f;

            var style = nameStyle ?? _playerNameStyle ?? _bodyStyle;
            var nameW = style != null
                ? Mathf.Ceil(style.CalcSize(new GUIContent(player.Name ?? string.Empty)).x)
                : 80f;
            nameW = Mathf.Min(nameW, Mathf.Max(8f, row.xMax - x - icon - 10f));
            var nameRect = new Rect(x, row.y, nameW, row.height);
            GUI.Label(nameRect, player.Name, style);
            x = nameRect.xMax + 10f;

            var iconRect = new Rect(x, row.y + (row.height - icon) * 0.5f, icon, icon);
            DrawInstrumentIcon(iconRect, player.Instrument);
            return iconRect.xMax - row.x;
        }

        private void DrawInstrumentIcon(Rect rect, string instrument)
        {
            if (rect.width < 4f) return;
            var tex = InstrumentIconTexture(instrument);
            if (tex == null) return;
            GUI.DrawTexture(rect, tex, ScaleMode.ScaleToFit, true);
        }

        private static string InitialGlyph(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
            }

            return name[0].ToString();
        }

        private void PrefetchInstrumentIcons(IEnumerable<YaqPreviewPlayer> players)
        {
            if (players == null) return;
            foreach (var player in players)
            {
                InstrumentIconTexture(player?.instrument);
            }
        }

        private void PrefetchInstrumentIcons(IEnumerable<YaqSetPlayer> players)
        {
            if (players == null) return;
            foreach (var player in players)
            {
                InstrumentIconTexture(player?.instrument);
            }
        }

        private Sprite LoadInstrumentIcon(string instrument)
        {
            if (string.IsNullOrWhiteSpace(instrument)) return null;
            if (_instrumentIcons.TryGetValue(instrument, out var cached)) return cached;

            Sprite sprite = null;
            try
            {
                var address = InstrumentIconAddress(instrument);
                if (!string.IsNullOrEmpty(address))
                {
                    sprite = Addressables.LoadAssetAsync<Sprite>(address).WaitForCompletion();
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex, "YAQ instrument icon load failed");
            }

            _instrumentIcons[instrument] = sprite;
            return sprite;
        }

        private Texture2D InstrumentIconTexture(string instrument)
        {
            if (string.IsNullOrWhiteSpace(instrument)) return null;
            if (_instrumentIconTex.TryGetValue(instrument, out var cached)) return cached;

            Texture2D tex = null;
            try
            {
                tex = CopySprite(LoadInstrumentIcon(instrument));
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex, "YAQ instrument icon copy failed");
            }

            _instrumentIconTex[instrument] = tex;
            return tex;
        }

        private static Texture2D CopySprite(Sprite sprite)
        {
            if (sprite == null) return null;

            var source = sprite.texture;
            var tr = sprite.textureRect;
            if (source == null || tr.width < 1f || tr.height < 1f) return null;

            var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(source, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var full = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            full.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            full.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            var x = Mathf.Clamp(Mathf.RoundToInt(tr.x), 0, Mathf.Max(0, full.width - 1));
            var y = Mathf.Clamp(Mathf.RoundToInt(tr.y), 0, Mathf.Max(0, full.height - 1));
            var w = Mathf.Clamp(Mathf.RoundToInt(tr.width), 1, full.width - x);
            var h = Mathf.Clamp(Mathf.RoundToInt(tr.height), 1, full.height - y);
            var crop = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            crop.SetPixels(full.GetPixels(x, y, w, h));
            crop.Apply(false, false);
            UnityEngine.Object.Destroy(full);
            return crop;
        }

        internal static string InstrumentIconAddress(string instrument)
        {
            if (!TryParseHudInstrument(instrument, out var parsed)) return null;

            var key = parsed switch
            {
                Instrument.ProGuitar_22Fret => "realGuitar",
                Instrument.ProBass_22Fret => "realBass",
                _ => parsed.ToResourceName()
            };
            return string.IsNullOrEmpty(key) ? null : $"InstrumentIcons[{key}]";
        }

        internal static bool TryParseHudInstrument(string value, out Instrument instrument)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                instrument = default;
                return false;
            }

            if (Enum.TryParse(value, true, out instrument)) return true;

            switch (value)
            {
                case "FiveFretCoop":
                    instrument = Instrument.FiveFretCoopGuitar;
                    return true;
                case "ProGuitar_17":
                    instrument = Instrument.ProGuitar_17Fret;
                    return true;
                case "ProGuitar_22":
                    instrument = Instrument.ProGuitar_22Fret;
                    return true;
                case "ProBass_17":
                    instrument = Instrument.ProBass_17Fret;
                    return true;
                case "ProBass_22":
                    instrument = Instrument.ProBass_22Fret;
                    return true;
                default:
                    instrument = default;
                    return false;
            }
        }

        private void DrawQrBlock(Rect rect)
        {
            if (rect.width < 8f || rect.height < 8f) return;

            DrawDiagonalGradient(rect, QrGoldDark, QrGoldLight);
            var captionH = Mathf.Min(28f, rect.height * 0.16f);
            GUI.Label(
                new Rect(rect.x, rect.y + 4f, rect.width, captionH),
                "SCAN TO JOIN",
                _qrCaptionStyle);

            var pad = Mathf.Max(10f, rect.width * 0.08f);
            var qrSize = Mathf.Min(rect.width, rect.height - captionH) - pad * 2f;
            var qrRect = new Rect(
                rect.x + (rect.width - qrSize) * 0.5f,
                rect.y + captionH + (rect.height - captionH - qrSize) * 0.5f,
                qrSize,
                qrSize);
            if (_qrTexture != null)
            {
                GUI.DrawTexture(qrRect, _qrTexture, ScaleMode.ScaleToFit);
            }
            else
            {
                FillRect(qrRect, Color.white);
            }
        }

        private void DrawNextBar(Rect nextRect)
        {
            if (nextRect.width < 8f || nextRect.height < 8f) return;

            DrawHorizontalGradient(nextRect, NextBarDark, NextBarLight);

            var pad = 20f;
            var hasNext = TryGetNextSong(out var title, out var artist, out _);
            var players = NextHudPlayers();
            if (!hasNext && (_preview?.following == null))
            {
                players = new List<HudPlayer>();
            }

            var captionH = 22f;
            var titleW = MeasureLabelWidth(title, _nextTitleStyle, 80f);
            var artistW = MeasureLabelWidth(artist, _nextArtistStyle, 80f);
            var textW = Mathf.Clamp(Mathf.Max(titleW, artistW, 80f), 80f, nextRect.width * 0.55f);
            GUI.Label(
                new Rect(nextRect.x + pad, nextRect.y + 8f, textW, captionH),
                "UP NEXT",
                _nextCaptionStyle);

            var body = new Rect(
                nextRect.x + pad,
                nextRect.y + captionH + 6f,
                textW,
                Mathf.Max(1f, nextRect.height - captionH - 14f));

            if (!hasNext)
            {
                DrawFittedLabel(body, "Waiting for the next song…", _mutedStyle, 14);
                return;
            }

            var hasTitle = !string.IsNullOrEmpty(title);
            var hasArtist = !string.IsNullOrEmpty(artist);
            if (hasTitle && hasArtist)
            {
                var titleH = Mathf.Max(1f, body.height * 0.55f);
                DrawFittedLabel(new Rect(body.x, body.y, body.width, titleH), title, _nextTitleStyle, 16);
                DrawFittedLabel(
                    new Rect(body.x, body.y + titleH, body.width, Mathf.Max(1f, body.height - titleH)),
                    artist,
                    _nextArtistStyle,
                    14);
            }
            else
            {
                DrawFittedLabel(body, hasTitle ? title : artist, _nextTitleStyle, 16);
            }

            if (players.Count == 0) return;

            var chipH = Mathf.Min(44f, nextRect.height - 20f);
            var chipY = nextRect.y + (nextRect.height - chipH) * 0.5f;
            var chipX = nextRect.x + pad + textW + 24f;
            const float chipGap = 20f;
            for (var i = 0; i < players.Count; i++)
            {
                var remaining = nextRect.xMax - 8f - chipX;
                if (remaining < 72f) break;
                var used = DrawPackedPlayer(
                    new Rect(chipX, chipY, remaining, chipH),
                    players[i],
                    36f,
                    36f,
                    _chipNameStyle ?? _playerNameStyle);
                chipX += used + chipGap;
            }
        }

        private static float MeasureLabelWidth(string text, GUIStyle style, float fallback)
        {
            if (string.IsNullOrEmpty(text) || style == null) return fallback;
            return Mathf.Ceil(style.CalcSize(new GUIContent(text)).x);
        }

        private static void FillRect(Rect rect, Color color)
        {
            var prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = prev;
        }

        private void DrawHorizontalGradient(Rect rect, Color left, Color right)
        {
            if (Event.current.type != EventType.Repaint) return;
            var tex = HorizontalGradientTexture(left, right);
            if (tex != null) GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, false);
        }

        private void DrawDiagonalGradient(Rect rect, Color bottomRight, Color topLeft)
        {
            if (Event.current.type != EventType.Repaint) return;
            var tex = DiagonalGradientTexture(bottomRight, topLeft);
            if (tex != null) GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, false);
        }

        private Texture2D HorizontalGradientTexture(Color left, Color right)
        {
            var key = $"hg:{ColorKey(left)}:{ColorKey(right)}";
            if (_hudShapes.TryGetValue(key, out var cached) && cached != null) return cached;

            const int width = 256;
            var tex = new Texture2D(width, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            for (var x = 0; x < width; x++)
            {
                tex.SetPixel(x, 0, Color.Lerp(left, right, x / (width - 1f)));
            }

            tex.Apply(false, false);
            _hudShapes[key] = tex;
            return tex;
        }

        private Texture2D DiagonalGradientTexture(Color bottomRight, Color topLeft)
        {
            var key = $"dg:{ColorKey(bottomRight)}:{ColorKey(topLeft)}";
            if (_hudShapes.TryGetValue(key, out var cached) && cached != null) return cached;

            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            var last = size - 1f;
            for (var y = 0; y < size; y++)
            {
                var v = y / last;
                for (var x = 0; x < size; x++)
                {
                    var u = x / last;
                    // u=1,v=0 bottom-right; u=0,v=1 top-left. Texture y=0 is bottom.
                    var t = Mathf.Clamp01(((1f - u) + v) * 0.5f);
                    tex.SetPixel(x, y, Color.Lerp(bottomRight, topLeft, t));
                }
            }

            tex.Apply(false, false);
            _hudShapes[key] = tex;
            return tex;
        }

        private void DrawRounded(Rect rect, int radius, Color fill, Color border, int borderWidth)
        {
            if (Event.current.type != EventType.Repaint) return;
            var tex = RoundedTexture(
                Mathf.Max(8, Mathf.RoundToInt(rect.width)),
                Mathf.Max(8, Mathf.RoundToInt(rect.height)),
                radius,
                fill,
                border,
                borderWidth);
            if (tex != null) GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, true);
        }

        private void DrawTwoToneRounded(Rect rect, int radius, float splitY, Color top, Color bottom, Color border, int borderWidth)
        {
            if (Event.current.type != EventType.Repaint) return;
            var tex = TwoToneRoundedTexture(
                Mathf.Max(8, Mathf.RoundToInt(rect.width)),
                Mathf.Max(8, Mathf.RoundToInt(rect.height)),
                radius,
                Mathf.Clamp(Mathf.RoundToInt(splitY), 1, Mathf.Max(1, Mathf.RoundToInt(rect.height) - 1)),
                top,
                bottom,
                border,
                borderWidth);
            if (tex != null) GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, true);
        }

        private void DrawCircle(Rect rect, Color color)
        {
            var circle = WhiteCircle();
            if (circle == null) return;
            var prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, circle, ScaleMode.ScaleToFit, true);
            GUI.color = prev;
        }

        private Texture2D WhiteCircle()
        {
            if (_whiteCircle != null) return _whiteCircle;
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            var radius = (size - 1) * 0.5f;
            var center = new Vector2(radius, radius);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var d = Vector2.Distance(new Vector2(x, y), center);
                    var a = Mathf.Clamp01(radius + 0.5f - d);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }

            tex.Apply(false, false);
            _whiteCircle = tex;
            return tex;
        }

        private Texture2D RoundedTexture(int w, int h, int radius, Color fill, Color border, int borderWidth)
        {
            var key = $"r:{w}x{h}:{radius}:{borderWidth}:{ColorKey(fill)}:{ColorKey(border)}";
            if (_hudShapes.TryGetValue(key, out var cached) && cached != null) return cached;
            var tex = BuildRounded(w, h, radius, y => fill, border, borderWidth);
            _hudShapes[key] = tex;
            return tex;
        }

        private Texture2D TwoToneRoundedTexture(
            int w,
            int h,
            int radius,
            int splitY,
            Color top,
            Color bottom,
            Color border,
            int borderWidth)
        {
            var key = $"t:{w}x{h}:{radius}:{splitY}:{ColorKey(top)}:{ColorKey(bottom)}";
            if (_hudShapes.TryGetValue(key, out var cached) && cached != null) return cached;
            var tex = BuildRounded(w, h, radius, y => y < splitY ? top : bottom, border, borderWidth);
            _hudShapes[key] = tex;
            return tex;
        }

        private static string ColorKey(Color c)
        {
            return $"{c.r:0.00}{c.g:0.00}{c.b:0.00}{c.a:0.00}";
        }

        private static Texture2D BuildRounded(
            int w,
            int h,
            int radius,
            Func<int, Color> fillAtY,
            Color border,
            int borderWidth)
        {
            w = Mathf.Clamp(w, 8, 1024);
            h = Mathf.Clamp(h, 8, 1024);
            radius = Mathf.Clamp(radius, 0, Mathf.Min(w, h) / 2);
            borderWidth = Mathf.Max(0, borderWidth);

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            var r = radius + 0.5f;
            var inner = Mathf.Max(0f, r - borderWidth);
            for (var y = 0; y < h; y++)
            {
                var fill = fillAtY(h - 1 - y);
                for (var x = 0; x < w; x++)
                {
                    var d = CornerDistance(x, y, w, h, radius);
                    if (d > r)
                    {
                        tex.SetPixel(x, y, Color.clear);
                        continue;
                    }

                    var alpha = Mathf.Clamp01(r - d);
                    var onEdge = x < borderWidth || x >= w - borderWidth ||
                                 y < borderWidth || y >= h - borderWidth;
                    Color pixel;
                    if (borderWidth > 0 && (d > inner || (d < 0.01f && onEdge)))
                    {
                        pixel = border;
                    }
                    else
                    {
                        pixel = fill;
                    }

                    pixel.a *= alpha;
                    tex.SetPixel(x, y, pixel);
                }
            }

            tex.Apply(false, false);
            return tex;
        }

        private static float CornerDistance(int x, int y, int w, int h, int radius)
        {
            var cx = x;
            var cy = y;
            if (x < radius && y < radius)
            {
                cx = x;
                cy = y;
                return Vector2.Distance(new Vector2(cx, cy), new Vector2(radius, radius));
            }

            if (x >= w - radius && y < radius)
            {
                return Vector2.Distance(new Vector2(x, y), new Vector2(w - 1 - radius, radius));
            }

            if (x < radius && y >= h - radius)
            {
                return Vector2.Distance(new Vector2(x, y), new Vector2(radius, h - 1 - radius));
            }

            if (x >= w - radius && y >= h - radius)
            {
                return Vector2.Distance(new Vector2(x, y), new Vector2(w - 1 - radius, h - 1 - radius));
            }

            return 0f;
        }

        private void ClearHudShapes()
        {
            foreach (var tex in _hudShapes.Values)
            {
                if (tex != null) Destroy(tex);
            }

            _hudShapes.Clear();
            foreach (var tex in _instrumentIconTex.Values)
            {
                if (tex != null) Destroy(tex);
            }

            _instrumentIconTex.Clear();
            if (_whiteCircle != null)
            {
                Destroy(_whiteCircle);
                _whiteCircle = null;
            }
        }

        private void EnsureStyles()
        {
            if (_titleStyle == null)
            {
                _titleStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 42,
                    fontStyle = FontStyle.Bold,
                    normal = { textColor = Color.white },
                    wordWrap = false,
                    clipping = TextClipping.Clip
                };
                _artistStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 28,
                    fontStyle = FontStyle.Normal,
                    normal = { textColor = new Color(0.9f, 0.95f, 1f) },
                    wordWrap = false,
                    clipping = TextClipping.Clip
                };
                _bodyStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 28,
                    normal = { textColor = new Color(0.9f, 0.95f, 1f) },
                    wordWrap = true,
                    clipping = TextClipping.Clip
                };
                _playerNameStyle = new GUIStyle(_bodyStyle)
                {
                    alignment = TextAnchor.MiddleLeft,
                    fontSize = 24,
                    fontStyle = FontStyle.Bold,
                    wordWrap = false,
                    clipping = TextClipping.Clip,
                    stretchWidth = false
                };
                _avatarInitialStyle = new GUIStyle(_bodyStyle)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 22,
                    fontStyle = FontStyle.Bold,
                    wordWrap = false,
                    clipping = TextClipping.Overflow
                };
                _mutedStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 18,
                    normal = { textColor = new Color(0.65f, 0.75f, 0.8f) },
                    wordWrap = true,
                    clipping = TextClipping.Clip
                };
                _qrCaptionStyle = new GUIStyle(_mutedStyle)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 16,
                    fontStyle = FontStyle.Bold,
                    normal = { textColor = new Color(0.9f, 0.95f, 0.35f) },
                    wordWrap = false,
                    clipping = TextClipping.Clip
                };
                _readyStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 20,
                    fontStyle = FontStyle.Bold,
                    normal = { textColor = ReadyGlyph },
                    wordWrap = false,
                    clipping = TextClipping.Clip
                };
                _chipNameStyle = new GUIStyle(_playerNameStyle)
                {
                    fontSize = 20,
                    fontStyle = FontStyle.Normal,
                    normal = { textColor = Color.white }
                };
            }

            if (_nextCaptionStyle != null && _nextTitleStyle != null) return;
            _nextCaptionStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.85f, 0.95f, 1f) },
                wordWrap = false,
                clipping = TextClipping.Clip
            };
            _nextTitleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 32,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
                wordWrap = false,
                clipping = TextClipping.Clip
            };
            _nextArtistStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                fontStyle = FontStyle.Normal,
                normal = { textColor = NextArtistBlue },
                wordWrap = false,
                clipping = TextClipping.Clip
            };
            _nextPlayersStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                normal = { textColor = new Color(0.85f, 0.9f, 0.95f) },
                wordWrap = false,
                clipping = TextClipping.Clip
            };
        }

        private void OnBridgeMessage(JObject msg)
        {
            var type = msg.Value<string>("type");
            switch (type)
            {
                case "eventmode.enter":
                    EnterEventMode();
                    break;
                case "eventmode.exit":
                    ExitEventMode();
                    break;
                case "queue.preview":
                    if (EventMode.Suspended) break;
                    _preview = msg["preview"]?.ToObject<YaqQueuePreview>() ?? new YaqQueuePreview();
                    RememberPreviewPortraits(_preview?.players);
                    PrefetchInstrumentIcons(_preview?.players);
                    PrefetchInstrumentIcons(_preview?.following?.players);
                    RequestCover(true, _preview?.songHash);
                    if (_currentSet == null || _phase == "idle")
                    {
                        BindReadySet(_preview?.setId);
                    }
                    break;
                case "set.prepare":
                    if (EventMode.Suspended)
                    {
                        SendError("eventmode_suspended", null, null, "Event Mode is off; send eventmode.enter first");
                        break;
                    }

                    PrepareSet(msg);
                    break;
                case "set.launch":
                    if (EventMode.Suspended) break;
                    TryLaunchWhenAllReady();
                    break;
                case "settings.update":
                    ApplyEventFlags(msg["flags"]?.ToObject<EventFlags>());
                    break;
                case "profiles.setup":
                    ApplyVenueProfiles(
                        msg["profiles"]?.ToObject<List<YaqVenueProfile>>() ?? new List<YaqVenueProfile>(),
                        msg.Value<bool?>("addTestBots") ?? EventMode.Flags.addTestBots);
                    break;
                case "player.images":
                case "profile.images":
                    RememberImageList(msg["players"] as JArray);
                    break;
                case "player.image":
                case "profile.image":
                    RememberImageToken(msg);
                    break;
                case "library.request":
                    _librarySynced = false;
                    TrySyncLibrary();
                    break;
                case "hello":
                    ReportFlags();
                    ReportEventModeState();
                    TrySyncLibrary();
                    if (!EventMode.Suspended)
                    {
                        SendState("idle");
                    }

                    break;
            }
        }

        private void ApplyEventFlags(EventFlags flags)
        {
            EventMode.Flags.CopyFrom(flags ?? EventFlags.Defaults);
            ApplyMainMenuVisibility();
            EnsureHotMics();
            if (_venueSlots.Count > 0)
            {
                ApplyVenueProfiles(_venueSlots, EventMode.Flags.addTestBots);
            }
            else
            {
                SyncTestBots(GlobalVariables.State.CurrentSong);
            }
            _bridge?.Send(new
            {
                type = "settings.ack",
                flags = new
                {
                    EventMode.Flags.hotMic,
                    EventMode.Flags.showUpNextHud,
                    EventMode.Flags.skipMainMenu,
                    EventMode.Flags.openDifficultySelect,
                    EventMode.Flags.addTestBots
                }
            });
            YargLogger.LogFormatInfo(
                "YAQ event flags applied (hotMic={0}, hud={1}, skipMenu={2}, difficulty={3}, testBots={4})",
                EventMode.Flags.hotMic,
                EventMode.Flags.showUpNextHud,
                EventMode.Flags.skipMainMenu,
                EventMode.Flags.openDifficultySelect,
                EventMode.Flags.addTestBots);
        }

        private void ReportFlags()
        {
            _bridge?.Send(new
            {
                type = "settings.report",
                flags = new
                {
                    EventMode.Flags.hotMic,
                    EventMode.Flags.showUpNextHud,
                    EventMode.Flags.skipMainMenu,
                    EventMode.Flags.openDifficultySelect,
                    EventMode.Flags.addTestBots
                }
            });
        }

        private void PrepareSet(JObject msg)
        {
            var set = msg["set"]?.ToObject<YaqPlaySet>();
            var players = msg["players"]?.ToObject<List<YaqSetPlayer>>() ?? new List<YaqSetPlayer>();
            if (set == null || string.IsNullOrEmpty(set.songHash))
            {
                YargLogger.LogWarning("YAQ set.prepare missing song hash");
                SendError("invalid_set", set?.id, set?.songHash, "set.prepare missing song hash");
                return;
            }

            if (!SongContainer.SongsByHash.TryGetValue(HashWrapper.FromString(set.songHash), out var songs) ||
                songs == null || songs.Count == 0)
            {
                _status = $"Song hash not in library: {set.songHash}";
                YargLogger.LogFormatError("YAQ could not find song hash {0}", set.songHash);
                SendError("song_not_found", set.id, set.songHash, "Song hash not in YARG library");
                return;
            }

            var song = songs[0];
            if (_venueSlots.Count > 0)
            {
                ApplyPlayers(players);
            }
            else
            {
                RemoveTestBots();
                ApplyPlayersSequential(players);
                SyncTestBots(song);
            }

            GlobalVariables.State.CurrentSong = song;
            GlobalVariables.State.ShowSongs.Clear();
            GlobalVariables.State.ShowSongs.Add(song);
            GlobalVariables.State.PlayingAShow = false;
            MusicLibraryMenu.CurrentlyPlaying = song;

            _pendingSetId = set.id;
            _currentSet = set;
            _currentPlayers = players;
            RememberSetPortraits(players);
            PrefetchInstrumentIcons(players);
            BindReadySet(set.id);
            _phase = "ready";
            _status = AllFeaturedPlayersReady()
                ? $"Ready: {set.songArtist} — {set.songName}"
                : $"Waiting for players to ready: {set.songArtist} — {set.songName}";
            RequestCover(false, set.songHash);
            EnableSeatedPlayerInputs();
            TryLaunchWhenAllReady();

            SendState("ready");
            _bridge.Send(new { type = "ready", setId = set.id });
        }

        private System.Collections.IEnumerator OpenReadyWhenPossible()
        {
            for (var i = 0; i < 180; i++)
            {
                if (MenuManager.Instance != null)
                {
                    MenuManager.Instance.PushMenu(MenuManager.Menu.DifficultySelect);
                    yield break;
                }

                yield return null;
            }
        }

        private void RestoreVenueProfileNames()
        {
            foreach (var slot in _venueSlots)
            {
                if (string.IsNullOrEmpty(slot.slotId)) continue;
                if (!_venueProfiles.TryGetValue(slot.slotId, out var profile) || profile == null) continue;
                profile.Name = slot.name;
                profile.IsBot = false;
                var player = PlayerContainer.GetPlayerFromProfile(profile);
                if (player != null)
                {
                    player.SittingOut = false;
                }
            }
        }

        private void ApplyVenueProfiles(List<YaqVenueProfile> slots, bool addTestBots)
        {
            EventMode.Flags.addTestBots = addTestBots;
            if (slots == null || slots.Count == 0)
            {
                return;
            }

            _venueSlots = slots;
            RemoveLegacyTestBots();

            var keep = new HashSet<string>();
            foreach (var slot in _venueSlots)
            {
                if (string.IsNullOrEmpty(slot.slotId)) continue;
                keep.Add(slot.slotId);
                var instrument = ParseInstrument(slot.instrument);
                if (!_venueProfiles.TryGetValue(slot.slotId, out var profile) || profile == null)
                {
                    profile = PlayerContainer.Profiles.FirstOrDefault(existing => existing.Name == slot.name)
                        ?? new YargProfile
                        {
                            Name = slot.name,
                            NoteSpeed = 5,
                            HighwayLength = 1,
                        };
                    _venueProfiles[slot.slotId] = profile;
                }

                profile.Name = slot.name;
                profile.IsBot = slot.isBot;
                profile.CurrentInstrument = instrument;
                profile.PreferredInstrument = instrument;
                profile.CurrentDifficulty = Difficulty.Expert;
                profile.DifficultyFallback = Difficulty.Expert;
                profile.GameMode = instrument.ToNativeGameMode();

                if (!PlayerContainer.Profiles.Contains(profile))
                {
                    PlayerContainer.AddProfile(profile);
                }

                if (!PlayerContainer.IsProfileTaken(profile))
                {
                    PlayerContainer.CreatePlayerFromProfile(profile, true);
                }

                var player = PlayerContainer.GetPlayerFromProfile(profile);
                if (player != null)
                {
                    player.SittingOut = false;
                }

                YaqProfileAvatar.Remember(slot.slotId, slot.name, slot.dataUrl);
            }

            foreach (var slotId in _venueProfiles.Keys.ToList())
            {
                if (keep.Contains(slotId)) continue;
                if (_venueProfiles.TryGetValue(slotId, out var extra) && extra != null)
                {
                    var player = PlayerContainer.GetPlayerFromProfile(extra);
                    if (player != null) PlayerContainer.DisposePlayer(player);
                    PlayerContainer.RemoveProfile(extra);
                }

                _venueProfiles.Remove(slotId);
            }

            YargLogger.LogFormatInfo("YAQ venue profiles applied ({0} slots)", _venueSlots.Count);
        }

        private void ApplyPlayers(List<YaqSetPlayer> players)
        {
            players ??= new List<YaqSetPlayer>();
            var usedSlots = new HashSet<string>();

            for (var i = 0; i < players.Count; i++)
            {
                var request = players[i];
                var slotId = string.IsNullOrEmpty(request.slotId) ? $"legacy_{i}" : request.slotId;
                usedSlots.Add(slotId);

                var instrument = ParseInstrument(request.instrument);
                var asBot = request.isBot && !request.isSongMaster;
                var profile = GetOrCreateVenueProfile(slotId, request.name, instrument, asBot);

                profile.Name = request.name;
                profile.IsBot = asBot;
                profile.CurrentInstrument = instrument;
                profile.PreferredInstrument = instrument;
                profile.CurrentDifficulty = ParseDifficulty(request.difficulty);
                profile.DifficultyFallback = profile.CurrentDifficulty;
                profile.GameMode = instrument.ToNativeGameMode();

                if (!PlayerContainer.IsProfileTaken(profile))
                {
                    PlayerContainer.CreatePlayerFromProfile(profile, true);
                }

                var player = PlayerContainer.GetPlayerFromProfile(profile);
                if (player != null)
                {
                    player.SittingOut = false;
                }
            }

            foreach (var (slotId, profile) in _venueProfiles)
            {
                if (usedSlots.Contains(slotId)) continue;
                var player = PlayerContainer.GetPlayerFromProfile(profile);
                if (player != null)
                {
                    player.SittingOut = true;
                }
            }
        }

        private void ApplyPlayersSequential(List<YaqSetPlayer> players)
        {
            var profiles = PlayerContainer.Players.ToList();

            for (var i = profiles.Count - 1; i >= players.Count; i--)
            {
                PlayerContainer.DisposePlayer(profiles[i]);
            }

            profiles = PlayerContainer.Players.ToList();
            for (var i = 0; i < players.Count; i++)
            {
                var request = players[i];
                if (i >= profiles.Count)
                {
                    var profile = new YargProfile
                    {
                        Name = request.name,
                        CurrentInstrument = ParseInstrument(request.instrument),
                        PreferredInstrument = ParseInstrument(request.instrument),
                        CurrentDifficulty = ParseDifficulty(request.difficulty),
                        DifficultyFallback = ParseDifficulty(request.difficulty),
                    };
                    if (PlayerContainer.AddProfile(profile))
                    {
                        PlayerContainer.CreatePlayerFromProfile(profile, true);
                    }

                    continue;
                }

                var existing = profiles[i];
                existing.Profile.Name = request.name;
                existing.Profile.CurrentInstrument = ParseInstrument(request.instrument);
                existing.Profile.PreferredInstrument = existing.Profile.CurrentInstrument;
                existing.Profile.CurrentDifficulty = ParseDifficulty(request.difficulty);
                existing.Profile.DifficultyFallback = existing.Profile.CurrentDifficulty;
            }
        }

        private YargProfile GetOrCreateVenueProfile(string slotId, string name, Instrument instrument, bool isBot)
        {
            if (_venueProfiles.TryGetValue(slotId, out var existing) && existing != null)
            {
                return existing;
            }

            var profile = new YargProfile
            {
                Name = name,
                IsBot = isBot,
                NoteSpeed = 5,
                HighwayLength = 1,
                CurrentInstrument = instrument,
                PreferredInstrument = instrument,
                CurrentDifficulty = Difficulty.Expert,
                DifficultyFallback = Difficulty.Expert,
                GameMode = instrument.ToNativeGameMode(),
            };
            PlayerContainer.AddProfile(profile);
            _venueProfiles[slotId] = profile;
            return profile;
        }

        private static void RemoveLegacyTestBots()
        {
            const string prefix = "YAQ Bot ";
            foreach (var player in PlayerContainer.Players.ToList())
            {
                if (player.Profile == null || !player.Profile.IsBot) continue;
                if (string.IsNullOrEmpty(player.Profile.Name) || !player.Profile.Name.StartsWith(prefix)) continue;
                var profile = player.Profile;
                PlayerContainer.DisposePlayer(player);
                PlayerContainer.RemoveProfile(profile);
            }

            foreach (var profile in PlayerContainer.Profiles.ToList())
            {
                if (profile.IsBot && !string.IsNullOrEmpty(profile.Name) && profile.Name.StartsWith(prefix))
                {
                    PlayerContainer.RemoveProfile(profile);
                }
            }
        }

        private void RememberSetPortraits(List<YaqSetPlayer> players)
        {
            if (players == null) return;
            foreach (var player in players)
            {
                if (player == null) continue;
                YaqProfileAvatar.Remember(player.id ?? player.slotId, player.name, player.dataUrl);
            }
        }

        private void RememberPreviewPortraits(List<YaqPreviewPlayer> players)
        {
            if (players == null) return;
            foreach (var player in players)
            {
                if (player == null) continue;
                YaqProfileAvatar.Remember(player.id ?? player.slotId, player.name, player.dataUrl);
            }
        }

        private static void RememberImageList(JArray players)
        {
            if (players == null) return;
            foreach (var token in players)
            {
                if (token is JObject obj)
                {
                    RememberImageToken(obj);
                }
            }
        }

        private static void RememberImageToken(JObject obj)
        {
            if (obj == null) return;
            var dataUrl = obj.Value<string>("dataUrl") ?? obj.Value<string>("imageBase64") ??
                          obj.Value<string>("profileImage");
            YaqProfileAvatar.Remember(
                obj.Value<string>("playerId") ?? obj.Value<string>("id") ?? obj.Value<string>("slotId"),
                obj.Value<string>("name"),
                dataUrl);
        }

        private const string TestBotPrefix = "YAQ Bot ";

        private static readonly (Instrument instrument, string label)[] TestBotParts =
        {
            (Instrument.FiveFretGuitar, "Guitar"),
            (Instrument.FiveFretBass, "Bass"),
            (Instrument.FourLaneDrums, "Drums"),
            (Instrument.Vocals, "Vocals"),
        };

        private void SyncTestBots(SongEntry song)
        {
            RemoveTestBots();
            if (EventMode.Flags.addTestBots)
            {
                AddTestBots(song);
            }
        }

        private void RemoveTestBots()
        {
            foreach (var player in PlayerContainer.Players.ToList())
            {
                if (!IsTestBot(player.Profile)) continue;
                var profile = player.Profile;
                PlayerContainer.DisposePlayer(player);
                PlayerContainer.RemoveProfile(profile);
            }

            foreach (var profile in PlayerContainer.Profiles.ToList())
            {
                if (IsTestBot(profile))
                {
                    PlayerContainer.RemoveProfile(profile);
                }
            }
        }

        private void AddTestBots(SongEntry song)
        {
            var humans = PlayerContainer.Players
                .Where(player => !IsTestBot(player.Profile))
                .Select(player => player.Profile.CurrentInstrument)
                .ToList();

            foreach (var (instrument, label) in TestBotParts)
            {
                if (humans.Any(human => OccupiesTestPart(human, instrument))) continue;
                if (!SongHasTestPart(song, instrument)) continue;

                var name = TestBotPrefix + label;
                var profile = PlayerContainer.Profiles.FirstOrDefault(p => p.Name == name && p.IsBot)
                    ?? new YargProfile
                    {
                        Name = name,
                        IsBot = true,
                        NoteSpeed = 5,
                        HighwayLength = 1,
                        CurrentInstrument = instrument,
                        PreferredInstrument = instrument,
                        CurrentDifficulty = Difficulty.Expert,
                        DifficultyFallback = Difficulty.Expert,
                        GameMode = instrument.ToNativeGameMode(),
                    };

                profile.CurrentInstrument = instrument;
                profile.PreferredInstrument = instrument;
                profile.CurrentDifficulty = Difficulty.Expert;
                profile.DifficultyFallback = Difficulty.Expert;
                profile.GameMode = instrument.ToNativeGameMode();
                profile.IsBot = true;

                if (!PlayerContainer.Profiles.Contains(profile))
                {
                    PlayerContainer.AddProfile(profile);
                }

                if (!PlayerContainer.IsProfileTaken(profile))
                {
                    PlayerContainer.CreatePlayerFromProfile(profile, true);
                }
            }
        }

        private static bool SongHasTestPart(SongEntry song, Instrument instrument)
        {
            if (song == null) return true;
            try
            {
                if (song.HasInstrument(instrument)) return true;
                return instrument switch
                {
                    Instrument.Vocals => song.HasInstrument(Instrument.Harmony),
                    Instrument.FourLaneDrums =>
                        song.HasInstrument(Instrument.ProDrums) ||
                        song.HasInstrument(Instrument.FiveLaneDrums) ||
                        song.HasInstrument(Instrument.EliteDrums),
                    _ => false
                };
            }
            catch
            {
                return false;
            }
        }

        private static bool OccupiesTestPart(Instrument playerInstrument, Instrument botPart)
        {
            return botPart switch
            {
                Instrument.FiveFretGuitar => playerInstrument is
                    Instrument.FiveFretGuitar or Instrument.SixFretGuitar or
                    Instrument.FiveFretRhythm or Instrument.FiveFretCoopGuitar or
                    Instrument.SixFretRhythm or Instrument.SixFretCoopGuitar or
                    Instrument.ProGuitar_17Fret or Instrument.ProGuitar_22Fret,
                Instrument.FiveFretBass => playerInstrument is
                    Instrument.FiveFretBass or Instrument.SixFretBass or
                    Instrument.ProBass_17Fret or Instrument.ProBass_22Fret,
                Instrument.FourLaneDrums => playerInstrument is
                    Instrument.FourLaneDrums or Instrument.FiveLaneDrums or
                    Instrument.ProDrums or Instrument.EliteDrums,
                Instrument.Vocals => playerInstrument is Instrument.Vocals or Instrument.Harmony,
                _ => playerInstrument == botPart
            };
        }

        private static bool IsTestBot(YargProfile profile)
        {
            return profile != null && profile.IsBot &&
                   !string.IsNullOrEmpty(profile.Name) &&
                   profile.Name.StartsWith(TestBotPrefix);
        }

        private static Instrument ParseInstrument(string value)
        {
            return Enum.TryParse(value, true, out Instrument instrument)
                ? instrument
                : Instrument.FiveFretGuitar;
        }

        private static Difficulty ParseDifficulty(string value)
        {
            return Enum.TryParse(value, true, out Difficulty difficulty)
                ? difficulty
                : Difficulty.Expert;
        }

        private void TrySyncLibrary()
        {
            if (_bridge == null || !_bridge.IsConnected) return;
            if (SongContainer.Count <= 0) return;

            var songs = new List<object>();
            foreach (var song in SongContainer.Songs)
            {
                var instruments = new List<string>();
                foreach (Instrument instrument in Enum.GetValues(typeof(Instrument)))
                {
                    try
                    {
                        if (song.HasInstrument(instrument))
                        {
                            instruments.Add(instrument.ToString());
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                }

                songs.Add(new
                {
                    hash = song.Hash.ToString(),
                    name = song.Name.ToString(),
                    artist = song.Artist.ToString(),
                    album = song.Album.ToString(),
                    year = song.UnmodifiedYear ?? song.ParsedYear ?? "",
                    genre = song.Genre.ToString(),
                    charter = song.Charter.ToString(),
                    folderPath = FirstNonEmpty(song.ActualLocation, song.Location, song.SortBasedLocation),
                    instruments,
                    source = "yarg",
                    verified = true
                });
            }

            _bridge.Send(new { type = "library.sync", songs });
            _librarySynced = true;
            YargLogger.LogFormatInfo("YAQ library sync sent ({0} songs)", songs.Count);
        }

        public void NotifySongEnded(object scores = null)
        {
            scores ??= BuildScorePayload();
            _bridge?.Send(new { type = "song.ended", setId = _pendingSetId, scores });
            SendState("score");
            _phase = "score";
            _status = "Score — continue when ready for the next group";
        }

        public void NotifyIdle()
        {
            SendState("idle");
            _phase = "idle";
            _status = "Connected to YAQ — waiting for next set";
            _pendingSetId = null;
            _currentSet = null;
            _currentPlayers = new List<YaqSetPlayer>();
            RestoreVenueProfileNames();
            ClearCover(false);
            ClearReadyState();
            BindReadySet(_preview?.setId);
        }

        public void NotifyPlaying()
        {
            _phase = "playing";
            SendState("playing");
        }

        private static object BuildScorePayload()
        {
            if (GlobalVariables.State.ScoreScreenStats is not { } stats)
            {
                return null;
            }

            return new
            {
                bandScore = stats.BandScore,
                bandStars = stats.BandStars,
                players = stats.PlayerScores?.Select(card =>
                {
                    var guitar = card.Stats as GuitarStats;
                    return new
                    {
                        name = card.Player?.Profile?.Name,
                        instrument = card.Player?.Profile?.CurrentInstrument.ToString(),
                        difficulty = card.Player?.Profile?.CurrentDifficulty.ToString(),
                        score = card.Stats?.TotalScore ?? 0,
                        stars = card.Stats?.Stars ?? 0f,
                        percent = card.Stats?.Percent ?? 0f,
                        notesHit = card.Stats?.NotesHit ?? 0,
                        totalNotes = card.Stats?.TotalNotes ?? 0,
                        notesMissed = card.Stats?.NotesMissed ?? 0,
                        maxCombo = card.Stats?.MaxCombo ?? 0,
                        starPowerPhrasesHit = card.Stats?.StarPowerPhrasesHit ?? 0,
                        totalStarPowerPhrases = card.Stats?.TotalStarPowerPhrases ?? 0,
                        averageMultiplier = card.Stats?.AverageMultiplier ?? 0f,
                        overstrums = guitar?.Overstrums ?? 0,
                        ghostInputs = guitar?.GhostInputs ?? 0,
                        starPowerActivations = card.Stats?.StarPowerActivationCount ?? 0,
                        timeInStarPower = card.Stats?.TimeInStarPower ?? 0d,
                        enginePreset = EnginePresetLabel(card.Player),
                        modifiersUsed = card.Player?.Profile != null &&
                            card.Player.Profile.CurrentModifiers != Modifier.None,
                        isFullCombo = card.Stats?.IsFullCombo ?? false,
                        isHighScore = card.IsHighScore,
                        isBot = card.Player?.Profile?.IsBot ?? false
                    };
                }).ToArray()
            };
        }

        private static string EnginePresetLabel(YargPlayer player)
        {
            var preset = player?.EnginePreset;
            if (preset == null || preset.Id == EnginePreset.Default.Id)
            {
                return "Default Engine";
            }

            if (preset.Id == EnginePreset.Casual.Id) return "Casual Engine";
            if (preset.Id == EnginePreset.Precision.Id) return "Precision Engine";
            if (preset.Id == EnginePreset.SoloTaps.Id) return "Solo Taps Engine";
            return "Custom Engine Preset";
        }

        private void SendState(string state)
        {
            _bridge?.Send(new { type = "state", state });
        }

        private void SendError(string code, string setId, string songHash, string message)
        {
            _bridge?.Send(new
            {
                type = "error",
                code,
                setId,
                songHash,
                message
            });
        }

        private void RequestCover(bool preview, string songHash)
        {
            if (string.IsNullOrEmpty(songHash))
            {
                ClearCover(preview);
                return;
            }

            if (preview)
            {
                if (_previewCoverHash == songHash && _previewCover != null) return;
                _previewCoverHash = songHash;
                LoadCoverAsync(true, songHash, ++_previewCoverGeneration).Forget();
                return;
            }

            if (_currentCoverHash == songHash && _currentCover != null) return;
            _currentCoverHash = songHash;
            LoadCoverAsync(false, songHash, ++_currentCoverGeneration).Forget();
        }

        private void RetryMissingCovers()
        {
            if (_previewCover == null && !string.IsNullOrEmpty(_preview?.songHash))
            {
                _previewCoverHash = null;
                RequestCover(true, _preview.songHash);
            }

            if (_currentCover == null && !string.IsNullOrEmpty(_currentSet?.songHash) &&
                (_phase == "ready" || _phase == "score"))
            {
                _currentCoverHash = null;
                RequestCover(false, _currentSet.songHash);
            }
        }

        private int CoverGeneration(bool preview)
        {
            return preview ? _previewCoverGeneration : _currentCoverGeneration;
        }

        private async UniTaskVoid LoadCoverAsync(bool preview, string songHash, int generation)
        {
            byte[] httpBytes = null;
            try
            {
                var url = CoverApiUrlFromBridge(EventMode.YaqWebSocketUrl, songHash);
                httpBytes = await UniTask.RunOnThreadPool(() =>
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var response = client.GetAsync(url).GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode) return null;
                    return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                });
            }
            catch (Exception ex)
            {
                YargLogger.LogFormatWarning("YAQ album art HTTP fetch failed: {0}", ex.Message);
            }

            YARG.Core.IO.YARGImage image = null;
            if (httpBytes == null || httpBytes.Length == 0)
            {
                if (SongContainer.SongsByHash.TryGetValue(HashWrapper.FromString(songHash), out var songs) &&
                    songs != null && songs.Count > 0)
                {
                    try
                    {
                        image = await UniTask.RunOnThreadPool(() => songs[0].LoadAlbumData());
                    }
                    catch (Exception ex)
                    {
                        YargLogger.LogException(ex, "YAQ album art load failed");
                    }
                }
            }

            Enqueue(() =>
            {
                if (generation != CoverGeneration(preview))
                {
                    image?.Dispose();
                    return;
                }

                ClearCover(preview);
                if (httpBytes != null && httpBytes.Length > 0)
                {
                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (texture.LoadImage(httpBytes))
                    {
                        AssignCover(preview, songHash, texture, false);
                        image?.Dispose();
                        return;
                    }

                    Destroy(texture);
                }

                if (image == null) return;

                try
                {
                    AssignCover(preview, songHash, image.LoadTexture(false), true);
                }
                finally
                {
                    image.Dispose();
                }
            });
        }

        private void AssignCover(bool preview, string songHash, Texture2D texture, bool flipped)
        {
            if (preview)
            {
                _previewCover = texture;
                _previewCoverHash = songHash;
                _previewCoverFlipped = flipped;
            }
            else
            {
                _currentCover = texture;
                _currentCoverHash = songHash;
                _currentCoverFlipped = flipped;
            }
        }

        private void ClearCover(bool preview)
        {
            if (preview)
            {
                if (_previewCover != null)
                {
                    Destroy(_previewCover);
                    _previewCover = null;
                }

                _previewCoverHash = null;
                _previewCoverFlipped = false;
            }
            else
            {
                if (_currentCover != null)
                {
                    Destroy(_currentCover);
                    _currentCover = null;
                }

                _currentCoverHash = null;
                _currentCoverFlipped = false;
            }
        }

        private void ClearCovers()
        {
            _previewCoverGeneration++;
            _currentCoverGeneration++;
            ClearCover(false);
            ClearCover(true);
        }

        private void RequestQr()
        {
            var generation = ++_qrLoadGeneration;
            LoadQrAsync(generation).Forget();
        }

        private async UniTaskVoid LoadQrAsync(int generation)
        {
            string json = null;
            try
            {
                var apiUrl = QrApiUrlFromBridge(EventMode.YaqWebSocketUrl);
                json = await UniTask.RunOnThreadPool(() =>
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    return client.GetStringAsync(apiUrl).GetAwaiter().GetResult();
                });
            }
            catch (Exception ex)
            {
                YargLogger.LogFormatWarning("YAQ QR fetch failed: {0}", ex.Message);
            }

            Enqueue(() =>
            {
                if (generation != _qrLoadGeneration) return;
                ApplyQrPayload(json);
            });
        }

        private void ApplyQrPayload(string json)
        {
            if (string.IsNullOrEmpty(json)) return;

            try
            {
                var obj = JObject.Parse(json);
                var joinUrl = obj.Value<string>("url");
                var dataUrl = obj.Value<string>("dataUrl");
                if (string.IsNullOrEmpty(dataUrl)) return;

                var comma = dataUrl.IndexOf(',');
                var b64 = comma >= 0 ? dataUrl.Substring(comma + 1) : dataUrl;
                var bytes = Convert.FromBase64String(b64);

                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(bytes))
                {
                    Destroy(texture);
                    return;
                }

                ClearQrTexture();
                _qrTexture = texture;
                _qrJoinUrl = joinUrl;
            }
            catch (Exception ex)
            {
                YargLogger.LogFormatWarning("YAQ QR decode failed: {0}", ex.Message);
            }
        }

        private void ClearQr()
        {
            _qrLoadGeneration++;
            _qrJoinUrl = null;
            ClearQrTexture();
        }

        private void ClearQrTexture()
        {
            if (_qrTexture == null) return;
            Destroy(_qrTexture);
            _qrTexture = null;
        }

        internal static string QrApiUrlFromBridge(string wsUrl)
        {
            if (Uri.TryCreate(wsUrl, UriKind.Absolute, out var uri))
            {
                var scheme = uri.Scheme == "wss" ? "https" : "http";
                return $"{scheme}://{uri.Authority}/api/qr";
            }

            return "http://127.0.0.1:3000/api/qr";
        }

        internal static string CoverApiUrlFromBridge(string wsUrl, string songHash)
        {
            var hash = Uri.EscapeDataString(songHash ?? string.Empty);
            if (Uri.TryCreate(wsUrl, UriKind.Absolute, out var uri))
            {
                var scheme = uri.Scheme == "wss" ? "https" : "http";
                return $"{scheme}://{uri.Authority}/api/songs/{hash}/cover";
            }

            return $"http://127.0.0.1:3000/api/songs/{hash}/cover";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }

            return string.Empty;
        }

        private void ApplyMainMenuVisibility()
        {
            var mainMenu = FindFirstObjectByType<MainMenu>(FindObjectsInactive.Include);
            if (mainMenu == null) return;

            var hide = EventMode.IsActive && EventMode.Flags.skipMainMenu;
            mainMenu.gameObject.SetActive(!hide);
        }

        private void GoToEventSceneIfIdle()
        {
            if (!CanSwitchEventHub()) return;
            if (GlobalVariables.Instance.CurrentScene == SceneIndex.Event) return;
            GlobalVariables.Instance.LoadScene(SceneIndex.Event);
        }

        private void GoToMenuSceneIfIdle()
        {
            if (!CanSwitchEventHub()) return;
            if (GlobalVariables.Instance.CurrentScene == SceneIndex.Menu) return;
            GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
        }

        private static bool CanSwitchEventHub()
        {
            var global = GlobalVariables.Instance;
            if (global == null) return false;

            // Persistent is boot. Don't yank gameplay or the score screen.
            return global.CurrentScene is not SceneIndex.Persistent
                and not SceneIndex.Gameplay
                and not SceneIndex.Score;
        }

        private void EnsureHotMics()
        {
            if (!EventMode.Flags.hotMic) return;

            try
            {
                if (SettingsManager.Settings.VocalMonitoring.Value < 0.35f)
                {
                    SettingsManager.Settings.VocalMonitoring.Value = 0.7f;
                }

                foreach (var player in PlayerContainer.Players)
                {
                    foreach (var mic in player.Bindings.Microphones)
                    {
                        mic.SetMonitoringLevel(SettingsManager.Settings.VocalMonitoring.Value);
                    }
                }
            }
            catch
            {
                // Bindings may not be ready during early boot
            }
        }
    }
}
