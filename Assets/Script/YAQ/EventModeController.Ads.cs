using System;
using System.Collections.Generic;
using System.Net.Http;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Helpers.Extensions;
using YARG.Settings;
using YARG.Song;

namespace YARG.YAQ
{
    public partial class EventModeController
    {
        private const float AdsIdleEmptySeconds = 60f;
        private const float SceneFadeSeconds = 0.45f;
        private const float AdsArtFadeSeconds = 0.55f;
        private const float AdsReturnSongFadeSeconds = 20f;

        private enum SceneFadePhase
        {
            None,
            ToBlack,
            WaitLoad,
            FromBlack
        }

        private enum AdsArtPhase
        {
            Idle,
            Hold,
            FadeOut,
            FadeIn
        }

        private float _queueEmptySince = -1f;
        private SceneFadePhase _sceneFade;
        private SceneIndex _sceneFadeDest;
        private float _sceneFadeT;
        private float _sceneFadeAlpha;
        private bool _sceneFadeLoadSent;
        private SceneIndex _sceneFadeLoadSentFor;

        private SongEntry _adsSong;
        private Texture2D _adsCover;
        private bool _adsCoverFlipped;
        private string _adsCoverHash;
        private int _adsCoverGeneration;
        private float _adsArtAlpha;
        private AdsArtPhase _adsArtPhase;
        private float _adsArtT;
        private float _adsHoldUntil;
        private bool _adsSlideshowActive;
        private bool _adsReturningToEvent;
        private float _adsReturnStartAlpha;
        private StemMixer _adsMixer;
        private int _adsAudioGeneration;
        private string _adsAudioHash;
        private float _adsHoldFrozenAt;
        private bool _adsStemSettingsCaptured;
        private bool _adsStemSettingsRestore;

        private void ApplyAdsSeconds(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return;
            try
            {
                EventMode.SetAdsSeconds(token.Value<int>());
            }
            catch
            {
                // Ignore malformed payloads; keep the last good duration.
            }
        }

        private void ApplyAdsPlayFullSong(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return;
            try
            {
                EventMode.SetAdsPlayFullSong(token.Value<bool>());
            }
            catch
            {
                // Ignore malformed payloads; keep the last good flag.
            }
        }

        internal void DrawAdsHud()
        {
            if (!EventMode.IsActive) return;
            if (GlobalVariables.Instance == null ||
                GlobalVariables.Instance.CurrentScene != SceneIndex.Ads)
            {
                return;
            }

            EnsureStyles();
            var screenW = Screen.width;
            var screenH = Screen.height;
            var pad = Mathf.Clamp(Mathf.Min(screenW, screenH) * 0.04f, 16f, 40f);
            var barH = Mathf.Clamp(screenH * 0.16f, 100f, 136f);
            var barRect = new Rect(0f, screenH - barH, screenW, barH);

            var midTop = pad;
            var midBottom = barRect.y - pad;
            var midH = Mathf.Max(1f, midBottom - midTop);
            var artSize = Mathf.Min(screenW - pad * 2f, midH);
            var artRect = new Rect(
                (screenW - artSize) * 0.5f,
                midTop + (midH - artSize) * 0.5f,
                artSize,
                artSize);

            var songAlpha = Mathf.Clamp01(_adsArtAlpha);
            var prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, songAlpha);
            DrawAdsAlbumArt(artRect);
            GUI.color = prev;

            DrawScrollingHorizontalGradient(barRect, NextBarDark, NextBarLight);
            if (_adsSong == null || songAlpha <= 0.001f) return;

            var innerPad = 24f;
            var inner = new Rect(
                barRect.x + innerPad,
                barRect.y + 10f,
                Mathf.Max(1f, barRect.width - innerPad * 2f),
                Mathf.Max(1f, barRect.height - 20f));
            var title = (string) _adsSong.Name;
            var artist = (string) _adsSong.Artist;
            var hasTitle = !string.IsNullOrEmpty(title);
            var hasArtist = !string.IsNullOrEmpty(artist);
            GUI.color = new Color(1f, 1f, 1f, songAlpha);
            if (hasTitle && hasArtist)
            {
                var titleH = Mathf.Max(1f, inner.height * 0.55f);
                DrawFittedLabel(
                    new Rect(inner.x, inner.y, inner.width, titleH),
                    title,
                    _nextTitleStyle,
                    16,
                    TextAnchor.MiddleCenter);
                DrawFittedLabel(
                    new Rect(inner.x, inner.y + titleH, inner.width, Mathf.Max(1f, inner.height - titleH)),
                    artist,
                    _nextArtistStyle,
                    14,
                    TextAnchor.MiddleCenter);
            }
            else if (hasTitle || hasArtist)
            {
                DrawFittedLabel(inner, hasTitle ? title : artist, _nextTitleStyle, 16, TextAnchor.MiddleCenter);
            }

            GUI.color = prev;
        }

        internal void DrawSceneFadeOverlay()
        {
            if (_sceneFade == SceneFadePhase.None && _sceneFadeAlpha <= 0.001f) return;

            var prevDepth = GUI.depth;
            GUI.depth = -100;
            FillRect(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0f, 0f, 0f, _sceneFadeAlpha));
            GUI.depth = prevDepth;
        }

        private void TickAds()
        {
            if (!EventMode.IsActive)
            {
                _queueEmptySince = -1f;
                StopAdsSlideshow();
                if (_sceneFade != SceneFadePhase.None || _sceneFadeAlpha > 0f)
                {
                    _sceneFade = SceneFadePhase.None;
                    _sceneFadeAlpha = 0f;
                    _sceneFadeT = 0f;
                    _sceneFadeLoadSent = false;
                }

                return;
            }

            TickSceneFade();

            var global = GlobalVariables.Instance;
            if (global == null) return;

            var scene = global.CurrentScene;
            if (scene is SceneIndex.Gameplay or SceneIndex.Score or SceneIndex.Persistent)
            {
                _queueEmptySince = -1f;
                StopAdsSlideshow();
                return;
            }

            if (QueueHasWork())
            {
                _queueEmptySince = -1f;
                if (scene == SceneIndex.Ads)
                {
                    TickAdsReturnToEvent();
                }
                else
                {
                    StopAdsSlideshow();
                    if (_sceneFade != SceneFadePhase.None && _sceneFadeDest == SceneIndex.Ads)
                    {
                        FadeToScene(SceneIndex.Event);
                    }
                }

                return;
            }

            if (scene == SceneIndex.Event)
            {
                if (_queueEmptySince < 0f) _queueEmptySince = Time.unscaledTime;
                if (Time.unscaledTime - _queueEmptySince >= AdsIdleEmptySeconds)
                {
                    FadeToScene(SceneIndex.Ads);
                }

                StopAdsSlideshow();
                return;
            }

            if (scene != SceneIndex.Ads) return;

            if (AnnouncementBusy)
            {
                PauseAdsForAnnouncement();
                return;
            }

            if (_adsReturningToEvent)
            {
                _adsReturningToEvent = false;
                _adsArtPhase = AdsArtPhase.FadeIn;
                _adsArtT = Mathf.Clamp01(_adsArtAlpha);
                ApplyAdsMusicMute();
            }

            if (!_adsSlideshowActive)
            {
                StartAdsSlideshow();
            }

            TickAdsSlideshow();
        }

        private void TickAdsReturnToEvent()
        {
            if (_sceneFade != SceneFadePhase.None && _sceneFadeDest == SceneIndex.Event)
            {
                return;
            }

            if (!_adsReturningToEvent)
            {
                _adsReturningToEvent = true;
                _adsArtPhase = AdsArtPhase.Idle;
                _adsArtT = 0f;
                _adsReturnStartAlpha = Mathf.Clamp01(_adsArtAlpha);
                FadeAdsMusic(AdsReturnSongFadeSeconds);
            }

            if (_adsReturnStartAlpha <= 0.001f)
            {
                _adsArtAlpha = 0f;
                FadeToScene(SceneIndex.Event);
                return;
            }

            _adsArtT += Time.unscaledDeltaTime / AdsReturnSongFadeSeconds;
            _adsArtAlpha = Mathf.Lerp(_adsReturnStartAlpha, 0f, Mathf.Clamp01(_adsArtT));
            if (_adsArtAlpha > 0.001f) return;

            _adsArtAlpha = 0f;
            FadeToScene(SceneIndex.Event);
        }

        private bool QueueHasWork()
        {
            if (HasActiveEventSession()) return true;
            if (_preview == null) return false;
            if (!string.IsNullOrEmpty(_preview.songHash)) return true;
            if (!string.IsNullOrEmpty(_preview.songName)) return true;
            if (!string.IsNullOrEmpty(_preview.setId)) return true;
            var following = _preview.following;
            if (following == null) return false;
            return !string.IsNullOrEmpty(following.songHash) ||
                   !string.IsNullOrEmpty(following.songName);
        }

        private void FadeToScene(SceneIndex dest)
        {
            var global = GlobalVariables.Instance;
            if (global == null) return;
            if (!CanSwitchEventHub() && dest != global.CurrentScene) return;

            if (_sceneFade != SceneFadePhase.None && _sceneFadeDest == dest) return;

            if (_sceneFadeDest != dest)
            {
                _sceneFadeLoadSent = false;
            }

            _sceneFadeDest = dest;
            if (_sceneFade is SceneFadePhase.WaitLoad || _sceneFadeAlpha >= 1f)
            {
                _sceneFade = SceneFadePhase.WaitLoad;
                _sceneFadeAlpha = 1f;
                return;
            }

            if (_sceneFade == SceneFadePhase.FromBlack)
            {
                _sceneFadeT = _sceneFadeAlpha;
                _sceneFade = SceneFadePhase.ToBlack;
                return;
            }

            if (_sceneFade == SceneFadePhase.None)
            {
                _sceneFadeT = 0f;
                _sceneFade = SceneFadePhase.ToBlack;
            }
        }

        private void TickSceneFade()
        {
            switch (_sceneFade)
            {
                case SceneFadePhase.None:
                    return;
                case SceneFadePhase.ToBlack:
                    _sceneFadeT += Time.unscaledDeltaTime / SceneFadeSeconds;
                    _sceneFadeAlpha = Mathf.Clamp01(_sceneFadeT);
                    if (_sceneFadeAlpha < 1f) return;
                    _sceneFadeAlpha = 1f;
                    _sceneFade = SceneFadePhase.WaitLoad;
                    RequestFadeLoad();
                    return;
                case SceneFadePhase.WaitLoad:
                    _sceneFadeAlpha = 1f;
                    RequestFadeLoad();
                    if (GlobalVariables.Instance != null &&
                        GlobalVariables.Instance.CurrentScene == _sceneFadeDest)
                    {
                        _sceneFadeT = 0f;
                        _sceneFade = SceneFadePhase.FromBlack;
                    }

                    return;
                case SceneFadePhase.FromBlack:
                    _sceneFadeT += Time.unscaledDeltaTime / SceneFadeSeconds;
                    _sceneFadeAlpha = 1f - Mathf.Clamp01(_sceneFadeT);
                    if (_sceneFadeAlpha > 0f) return;
                    _sceneFadeAlpha = 0f;
                    _sceneFade = SceneFadePhase.None;
                    _sceneFadeLoadSent = false;
                    return;
            }
        }

        private void RequestFadeLoad()
        {
            var global = GlobalVariables.Instance;
            if (global == null) return;
            if (global.CurrentScene == _sceneFadeDest) return;
            if (!CanSwitchEventHub()) return;
            if (_sceneFadeLoadSent && _sceneFadeLoadSentFor == _sceneFadeDest) return;
            _sceneFadeLoadSent = true;
            _sceneFadeLoadSentFor = _sceneFadeDest;
            global.LoadScene(_sceneFadeDest);
        }

        private void StartAdsSlideshow()
        {
            _adsSlideshowActive = true;
            _adsReturningToEvent = false;
            _adsSong = PickAdsSong(null);
            _adsArtAlpha = 0f;
            _adsArtPhase = AdsArtPhase.FadeIn;
            _adsArtT = 0f;
            _adsHoldUntil = 0f;
            RequestAdsCover(_adsSong);
            RequestAdsAudio(_adsSong);
        }

        private void StopAdsSlideshow()
        {
            if (!_adsSlideshowActive && _adsSong == null && _adsCover == null &&
                !_adsReturningToEvent && _adsMixer == null)
            {
                return;
            }

            _adsSlideshowActive = false;
            _adsReturningToEvent = false;
            _adsSong = null;
            _adsArtPhase = AdsArtPhase.Idle;
            _adsArtAlpha = 0f;
            _adsHoldUntil = 0f;
            ClearAdsCover();
            StopAdsAudio();
        }

        private void TickAdsSlideshow()
        {
            if (_adsSong == null && SongContainer.Count > 0)
            {
                _adsSong = PickAdsSong(null);
                RequestAdsCover(_adsSong);
                RequestAdsAudio(_adsSong);
                _adsArtPhase = AdsArtPhase.FadeIn;
                _adsArtT = 0f;
                _adsArtAlpha = 0f;
            }

            switch (_adsArtPhase)
            {
                case AdsArtPhase.Hold:
                    if (Time.unscaledTime < _adsHoldUntil) return;
                    _adsArtPhase = AdsArtPhase.FadeOut;
                    _adsArtT = 0f;
                    FadeAdsMusic(AdsArtFadeSeconds);
                    return;
                case AdsArtPhase.FadeOut:
                    _adsArtT += Time.unscaledDeltaTime / AdsArtFadeSeconds;
                    _adsArtAlpha = 1f - Mathf.Clamp01(_adsArtT);
                    if (_adsArtAlpha > 0f) return;
                    _adsArtAlpha = 0f;
                    _adsSong = PickAdsSong(_adsSong);
                    RequestAdsCover(_adsSong);
                    RequestAdsAudio(_adsSong);
                    _adsArtPhase = AdsArtPhase.FadeIn;
                    _adsArtT = 0f;
                    return;
                case AdsArtPhase.FadeIn:
                    _adsArtT += Time.unscaledDeltaTime / AdsArtFadeSeconds;
                    _adsArtAlpha = Mathf.Clamp01(_adsArtT);
                    if (_adsArtAlpha < 1f) return;
                    _adsArtAlpha = 1f;
                    _adsArtPhase = AdsArtPhase.Hold;
                    _adsHoldUntil = Time.unscaledTime + AdsHoldSeconds(_adsSong);
                    return;
            }
        }

        private float AdsHoldSeconds(SongEntry song)
        {
            if (EventMode.AdsPlayFullSong)
            {
                if (_adsMixer != null && _adsMixer.Length >= EventMode.MinAdsSeconds)
                {
                    return (float) _adsMixer.Length;
                }

                if (song != null)
                {
                    var length = (float) song.SongLengthSeconds;
                    if (length >= EventMode.MinAdsSeconds) return length;
                }
            }

            return Mathf.Max(EventMode.MinAdsSeconds, EventMode.AdsSeconds);
        }

        private static SongEntry PickAdsSong(SongEntry current)
        {
            var songs = SongContainer.Songs;
            if (songs == null || songs.Length == 0) return current;

            var currentArtist = ArtistKey(current);
            var otherArtists = CollectAdsCandidates(songs, current, currentArtist, true);
            if (otherArtists.Count > 0)
            {
                return otherArtists[UnityEngine.Random.Range(0, otherArtists.Count)];
            }

            var otherSongs = CollectAdsCandidates(songs, current, currentArtist, false);
            if (otherSongs.Count > 0)
            {
                return otherSongs[UnityEngine.Random.Range(0, otherSongs.Count)];
            }

            return current ?? songs[0];
        }

        private static List<SongEntry> CollectAdsCandidates(
            SongEntry[] songs,
            SongEntry current,
            string currentArtist,
            bool requireOtherArtist)
        {
            var list = new List<SongEntry>();
            foreach (var song in songs)
            {
                if (song == null) continue;
                if (current != null && song.Hash.Equals(current.Hash)) continue;
                if (requireOtherArtist &&
                    !string.IsNullOrEmpty(currentArtist) &&
                    string.Equals(ArtistKey(song), currentArtist, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                list.Add(song);
            }

            return list;
        }

        private static string ArtistKey(SongEntry song)
        {
            if (song == null) return string.Empty;
            var artist = song.Artist.Original;
            return string.IsNullOrWhiteSpace(artist) ? string.Empty : artist.Trim();
        }

        private void DrawAdsAlbumArt(Rect rect)
        {
            if (rect.width < 8f || rect.height < 8f) return;
            if (_adsCover == null)
            {
                GUI.Box(rect, GUIContent.none);
                return;
            }

            if (_adsCoverFlipped)
            {
                GUI.DrawTextureWithTexCoords(rect, _adsCover, new Rect(0f, 1f, 1f, -1f));
            }
            else
            {
                GUI.DrawTexture(rect, _adsCover, ScaleMode.ScaleToFit);
            }
        }

        private void RequestAdsCover(SongEntry song)
        {
            if (song == null)
            {
                ClearAdsCover();
                return;
            }

            var songHash = song.Hash.ToString();
            if (_adsCoverHash == songHash && _adsCover != null) return;
            _adsCoverHash = songHash;
            LoadAdsCoverAsync(songHash, ++_adsCoverGeneration).Forget();
        }

        private void ClearAdsCover()
        {
            _adsCoverGeneration++;
            if (_adsCover != null)
            {
                Destroy(_adsCover);
                _adsCover = null;
            }

            _adsCoverHash = null;
            _adsCoverFlipped = false;
        }

        private async UniTaskVoid LoadAdsCoverAsync(string songHash, int generation)
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
                YargLogger.LogWarning($"YAQ ads album art HTTP fetch failed: {ex.Message}");
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
                        YargLogger.LogException(ex, "YAQ ads album art load failed");
                    }
                }
            }

            Enqueue(() =>
            {
                if (generation != _adsCoverGeneration)
                {
                    image?.Dispose();
                    return;
                }

                if (_adsCover != null)
                {
                    Destroy(_adsCover);
                    _adsCover = null;
                }

                _adsCoverFlipped = false;
                if (httpBytes != null && httpBytes.Length > 0)
                {
                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (texture.LoadImage(httpBytes))
                    {
                        _adsCover = texture;
                        _adsCoverHash = songHash;
                        image?.Dispose();
                        return;
                    }

                    Destroy(texture);
                }

                if (image == null) return;

                try
                {
                    _adsCover = image.LoadTexture(false);
                    _adsCoverHash = songHash;
                    _adsCoverFlipped = true;
                }
                finally
                {
                    image.Dispose();
                }
            });
        }

        internal void ApplyAdsMusicMute()
        {
            if (_adsMixer == null) return;
            if (AnnouncementBusy)
            {
                PauseAdsForAnnouncement();
                return;
            }

            var target = EventMode.AdsMusicVolume;
            if (target <= 0.0001)
            {
                _adsMixer.SetVolume(0);
                return;
            }

            BeginAdsStemMix();
            _adsMixer.SetVolume(target);
        }

        private void FadeAdsMusic(float duration)
        {
            if (_adsMixer == null) return;
            if (EventMode.AdsMusicVolume <= 0.0001)
            {
                _adsMixer.SetVolume(0);
                return;
            }

            _adsMixer.FadeOut(Mathf.Max(0.05f, duration));
        }

        private void RequestAdsAudio(SongEntry song)
        {
            if (song == null)
            {
                StopAdsAudio();
                return;
            }

            var songHash = song.Hash.ToString();
            if (_adsAudioHash == songHash && _adsMixer != null) return;
            _adsAudioHash = songHash;
            LoadAdsAudioAsync(song, songHash, ++_adsAudioGeneration).Forget();
        }

        private void StopAdsAudio()
        {
            _adsAudioGeneration++;
            _adsAudioHash = null;
            DisposeAdsMixer();
        }

        private void DisposeAdsMixer()
        {
            if (_adsMixer == null)
            {
                EndAdsStemMix();
                return;
            }
            try
            {
                _adsMixer.Dispose();
            }
            catch
            {
                // Mixer may already be torn down with the audio engine.
            }

            _adsMixer = null;
            EndAdsStemMix();
        }

        private void BeginAdsStemMix()
        {
            if (!_adsStemSettingsCaptured)
            {
                _adsStemSettingsRestore = StemSettings.ApplySettings;
                _adsStemSettingsCaptured = true;
            }

            // Gameplay mute-on-miss can leave stem volumes ducked. Ads uses the
            // full mix at EventMode.AdsFullVolume, same as a song at DEFAULT_VOLUME.
            StemSettings.ApplySettings = false;
        }

        private void EndAdsStemMix()
        {
            if (!_adsStemSettingsCaptured) return;
            StemSettings.ApplySettings = _adsStemSettingsRestore;
            _adsStemSettingsCaptured = false;
        }

        private async UniTaskVoid LoadAdsAudioAsync(SongEntry song, string songHash, int generation)
        {
            StemMixer mixer = null;
            try
            {
                var censor = SettingsManager.Settings?.CensorMatureContent.Value ?? false;
                // Full song stems, not preview.ogg / preview.mp3.
                mixer = await UniTask.RunOnThreadPool(() =>
                    song.LoadAudio(1f, EventMode.AdsFullVolume, censor, SongStem.Crowd));
            }
            catch (Exception ex)
            {
                YargLogger.LogWarning($"YAQ ads audio load failed: {ex.Message}");
            }

            Enqueue(() =>
            {
                if (generation != _adsAudioGeneration)
                {
                    mixer?.Dispose();
                    return;
                }

                DisposeAdsMixer();
                _adsAudioHash = songHash;
                _adsMixer = mixer;
                if (_adsMixer == null)
                {
                    YargLogger.LogWarning($"YAQ ads audio missing stems for {song.Name}");
                    return;
                }

                var volume = EventMode.AdsMusicVolume;
                try
                {
                    BeginAdsStemMix();
                    _adsMixer.SetPosition(0);
                    _adsMixer.SetVolume(volume);
                    if (AnnouncementBusy)
                    {
                        PauseAdsForAnnouncement();
                    }
                    else
                    {
                        var playResult = _adsMixer.Play();
                        if (playResult != 0)
                        {
                            YargLogger.LogWarning(
                                $"YAQ ads audio play failed ({playResult}) for {song.Name}");
                            StopAdsAudio();
                            return;
                        }
                    }

                    if (EventMode.AdsPlayFullSong &&
                        _adsMixer.Length >= EventMode.MinAdsSeconds)
                    {
                        _adsHoldUntil = Time.unscaledTime + (float) _adsMixer.Length;
                    }

                    YargLogger.LogInfo(
                        $"YAQ ads audio playing {song.Name} (volume={volume:0.00})");
                }
                catch (Exception ex)
                {
                    YargLogger.LogWarning($"YAQ ads audio play failed: {ex.Message}");
                    StopAdsAudio();
                }
            });
        }
    }
}
