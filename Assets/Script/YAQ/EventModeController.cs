using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using YARG.Core;
using YARG.Core.Engine.Guitar;
using YARG.Core.Game;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Helpers.Extensions;
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
        private string _phase = "idle";
        private string _status = "Connecting to YAQ…";
        private string _pendingSetId;
        private bool _librarySynced;
        private GUIStyle _titleStyle;
        private GUIStyle _artistStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _mutedStyle;
        private GUIStyle _qrCaptionStyle;
        private GUIStyle _nextTitleStyle;
        private GUIStyle _nextPlayersStyle;
        private GUIStyle _fitScratchStyle;
        private readonly ConcurrentQueue<Action> _mainThread = new();

        private Texture2D _currentCover;
        private Texture2D _previewCover;
        private string _currentCoverHash;
        private string _previewCoverHash;
        private int _coverLoadGeneration;

        private Texture2D _qrTexture;
        private string _qrJoinUrl;
        private int _qrLoadGeneration;
        private float _nextQrRetryAt;

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
            _status = "YAQ stream off";
            ClearCovers();
            ClearQr();
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
            _status = "Event Mode off (bridge still connected)";
            ClearCovers();
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
            ClearCovers();
            ClearQr();
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

            if (_qrTexture == null && Time.unscaledTime >= _nextQrRetryAt)
            {
                _nextQrRetryAt = Time.unscaledTime + 5f;
                RequestQr();
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
            var layout = ComputeHudLayout(Screen.width, Screen.height, MeasureHelpBarHeight());

            DrawCurrentHeader(layout.TitleRect);
            if (TryGetCurrentSong(out var cover, out _, out _, out var currentNames))
            {
                DrawAlbumArt(layout.ArtRect, cover);
                DrawPlayerGrid(layout.PlayersRect, currentNames);
            }

            DrawNextSong(layout.NextRect);
            DrawJoinQr(layout.QrCaptionRect, layout.QrRect);
        }

        internal readonly struct EventHudLayout
        {
            public readonly Rect TitleRect;
            public readonly Rect ArtRect;
            public readonly Rect PlayersRect;
            public readonly Rect NextRect;
            public readonly Rect QrCaptionRect;
            public readonly Rect QrRect;

            public EventHudLayout(
                Rect titleRect,
                Rect artRect,
                Rect playersRect,
                Rect nextRect,
                Rect qrCaptionRect,
                Rect qrRect)
            {
                TitleRect = titleRect;
                ArtRect = artRect;
                PlayersRect = playersRect;
                NextRect = nextRect;
                QrCaptionRect = qrCaptionRect;
                QrRect = qrRect;
            }
        }

        /// <summary>
        /// Persistent Canvas Help Bar height at 1920×1080 (see PersistentScene).
        /// </summary>
        internal const float HelpBarReferenceHeight = 75f;

        internal const float HelpBarGap = 8f;

        /// <summary>
        /// Mockup layout: current title on top, art + two-column players, next song
        /// bottom-left, compact QR bottom-right (~176px) above the player Help Bar.
        /// </summary>
        internal static EventHudLayout ComputeHudLayout(
            float screenW,
            float screenH,
            float helpBarH = HelpBarReferenceHeight)
        {
            const float artMax = 280f;
            const float qrMax = 176f;
            const float gap = 24f;
            const float captionH = 22f;

            var pad = Mathf.Clamp(Mathf.Min(screenW, screenH) * 0.045f, 16f, 48f);
            var bottomReserve = Mathf.Max(0f, helpBarH) + HelpBarGap;
            var areaX = pad;
            var areaY = pad;
            var areaW = Mathf.Max(1f, screenW - pad * 2f);
            var areaH = Mathf.Max(1f, screenH - pad - bottomReserve);

            var titleH = Mathf.Clamp(areaH * 0.2f, 80f, 128f);
            var nextH = Mathf.Clamp(areaH * 0.2f, 72f, 110f);

            var qrSize = Mathf.Min(qrMax, areaW * 0.2f, areaH * 0.42f);
            qrSize = Mathf.Clamp(qrSize, 96f, qrMax);

            var qrX = areaX + areaW - qrSize;
            var qrY = areaY + areaH - qrSize;
            if (qrY < areaY + titleH + captionH + gap)
            {
                qrY = areaY + titleH + captionH + gap;
                qrSize = Mathf.Clamp(areaY + areaH - qrY, 32f, qrMax);
                qrX = areaX + areaW - qrSize;
                qrY = areaY + areaH - qrSize;
            }

            var captionY = Mathf.Max(areaY, qrY - captionH);
            var contentW = Mathf.Max(0f, qrX - gap - areaX);

            // Title/artist sit on the top row and can use the full width (QR is bottom-right).
            var titleRect = new Rect(areaX, areaY, areaW, titleH);
            var nextRect = new Rect(areaX, areaY + areaH - nextH, contentW, nextH);

            var midY = areaY + titleH + gap;
            var midH = Mathf.Max(0f, nextRect.y - gap - midY);
            var artSize = Mathf.Min(artMax, midH, contentW * 0.38f);
            var artRect = new Rect(areaX, midY, artSize, artSize);
            var playersRect = new Rect(
                areaX + artSize + gap,
                midY,
                Mathf.Max(0f, contentW - artSize - gap),
                Mathf.Max(0f, artSize));
            var qrCaptionRect = new Rect(qrX, captionY, qrSize, captionH);
            var qrRect = new Rect(qrX, qrY, qrSize, qrSize);

            return new EventHudLayout(titleRect, artRect, playersRect, nextRect, qrCaptionRect, qrRect);
        }

        internal static float DefaultHelpBarHeight(float screenW, float screenH)
        {
            var scale = Mathf.Min(screenW / 1920f, screenH / 1080f);
            return HelpBarReferenceHeight * Mathf.Max(scale, 0.01f);
        }

        private static float MeasureHelpBarHeight()
        {
            var fallback = DefaultHelpBarHeight(Screen.width, Screen.height);
            if (HelpBar.Instance == null) return fallback;
            if (HelpBar.Instance.transform is not RectTransform rect) return fallback;

            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var height = Mathf.Abs(corners[1].y - corners[0].y);
            return height > 1f ? height : fallback;
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

            if (_currentSet == null)
            {
                return false;
            }

            if (string.IsNullOrEmpty(_preview?.songName) && string.IsNullOrEmpty(_preview?.songArtist))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(_preview?.setId) && _preview.setId == _currentSet.id)
            {
                return false;
            }

            title = _preview.songName;
            artist = _preview.songArtist;
            names = NamesFrom(_preview.players?.Select(player => player?.name));
            return true;
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

        private static void DrawAlbumArt(Rect rect, Texture2D texture)
        {
            if (rect.width < 8f || rect.height < 8f) return;

            if (texture != null)
            {
                // Album textures are loaded flipped for RawImage; flip for OnGUI too.
                GUI.DrawTextureWithTexCoords(rect, texture, new Rect(0f, 1f, 1f, -1f));
            }
            else
            {
                GUI.Box(rect, GUIContent.none);
            }
        }

        private void DrawPlayerGrid(Rect rect, List<string> names)
        {
            if (names == null || names.Count == 0 || rect.width < 8f) return;

            GUILayout.BeginArea(rect);
            var leftCount = names.Count <= 1 ? names.Count : (names.Count + 1) / 2;
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            for (var i = 0; i < leftCount; i++)
            {
                GUILayout.Label(names[i], _bodyStyle);
            }

            GUILayout.EndVertical();
            if (leftCount < names.Count)
            {
                GUILayout.Space(32f);
                GUILayout.BeginVertical();
                for (var i = leftCount; i < names.Count; i++)
                {
                    GUILayout.Label(names[i], _bodyStyle);
                }

                GUILayout.EndVertical();
            }

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void DrawJoinQr(Rect captionRect, Rect qrRect)
        {
            GUI.Label(captionRect, "SCAN TO JOIN", _qrCaptionStyle);
            if (_qrTexture != null)
            {
                GUI.DrawTexture(qrRect, _qrTexture, ScaleMode.ScaleToFit);
            }
            else
            {
                GUI.Box(qrRect, GUIContent.none);
            }
        }

        private void DrawNextSong(Rect nextRect)
        {
            if (!TryGetNextSong(out var title, out var artist, out var names)) return;

            var line = FormatSongLine(title, artist);
            var titleH = Mathf.Max(1f, nextRect.height * 0.55f);
            var titleArea = new Rect(nextRect.x, nextRect.y, nextRect.width, titleH);
            DrawFittedLabel(titleArea, line, _nextTitleStyle, 14);
            if (names.Count > 0)
            {
                var namesArea = new Rect(
                    nextRect.x,
                    nextRect.y + titleH,
                    nextRect.width,
                    Mathf.Max(1f, nextRect.height - titleH));
                DrawFittedLabel(namesArea, string.Join("  ", names), _nextPlayersStyle, 12);
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
                    wordWrap = false,
                    clipping = TextClipping.Clip
                };
            }

            if (_nextTitleStyle != null) return;
            _nextTitleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 32,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
                wordWrap = false,
                clipping = TextClipping.Clip
            };
            _nextPlayersStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                normal = { textColor = new Color(0.85f, 0.9f, 0.95f) },
                wordWrap = true,
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
                    RequestCover(true, _preview?.songHash);
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
                    EnsureDifficultySelectOpen();
                    break;
                case "settings.update":
                    ApplyEventFlags(msg["flags"]?.ToObject<EventFlags>());
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
            SyncTestBots(GlobalVariables.State.CurrentSong);
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
            RemoveTestBots();
            ApplyPlayers(players);
            SyncTestBots(song);

            GlobalVariables.State.CurrentSong = song;
            GlobalVariables.State.ShowSongs.Clear();
            GlobalVariables.State.ShowSongs.Add(song);
            GlobalVariables.State.PlayingAShow = false;
            MusicLibraryMenu.CurrentlyPlaying = song;

            _pendingSetId = set.id;
            _currentSet = set;
            _currentPlayers = players;
            _phase = "ready";
            _status = $"Ready: {set.songArtist} — {set.songName}";
            RequestCover(false, set.songHash);

            if (GlobalVariables.Instance.CurrentScene != SceneIndex.Menu)
            {
                GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
            }

            if (EventMode.Flags.openDifficultySelect)
            {
                StartCoroutine(OpenReadyWhenPossible());
            }

            SendState("ready");
            _bridge.Send(new { type = "ready", setId = set.id });
        }

        private void EnsureDifficultySelectOpen()
        {
            if (!EventMode.Flags.openDifficultySelect) return;
            if (_currentSet == null) return;
            StartCoroutine(OpenReadyWhenPossible());
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

        private void ApplyPlayers(List<YaqSetPlayer> players)
        {
            var profiles = PlayerContainer.Players.ToList();

            // Trim extras when YAQ sends fewer players than currently active
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
                    folderPath = song.Location ?? song.ActualLocation ?? "",
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
            ClearCover(false);
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
            }
            else
            {
                if (_currentCoverHash == songHash && _currentCover != null) return;
                _currentCoverHash = songHash;
            }

            var generation = ++_coverLoadGeneration;
            LoadCoverAsync(preview, songHash, generation).Forget();
        }

        private async UniTaskVoid LoadCoverAsync(bool preview, string songHash, int generation)
        {
            if (!SongContainer.SongsByHash.TryGetValue(HashWrapper.FromString(songHash), out var songs) ||
                songs == null || songs.Count == 0)
            {
                Enqueue(() =>
                {
                    if (generation != _coverLoadGeneration) return;
                    ClearCover(preview);
                });
                return;
            }

            var song = songs[0];
            YARG.Core.IO.YARGImage image = null;
            try
            {
                image = await UniTask.RunOnThreadPool(() => song.LoadAlbumData());
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex, "YAQ album art load failed");
            }

            Enqueue(() =>
            {
                if (generation != _coverLoadGeneration)
                {
                    image?.Dispose();
                    return;
                }

                ClearCover(preview);
                if (image == null) return;

                try
                {
                    var texture = image.LoadTexture(false);
                    if (preview)
                    {
                        _previewCover = texture;
                        _previewCoverHash = songHash;
                    }
                    else
                    {
                        _currentCover = texture;
                        _currentCoverHash = songHash;
                    }
                }
                finally
                {
                    image.Dispose();
                }
            });
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
            }
            else
            {
                if (_currentCover != null)
                {
                    Destroy(_currentCover);
                    _currentCover = null;
                }

                _currentCoverHash = null;
            }
        }

        private void ClearCovers()
        {
            _coverLoadGeneration++;
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
