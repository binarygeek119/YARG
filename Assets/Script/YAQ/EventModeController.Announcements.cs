using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Settings;

namespace YARG.YAQ
{
    public partial class EventModeController
    {
        private readonly Queue<string> _announcementQueue = new();
        private StemMixer _announcementMixer;
        private int _announcementGeneration;
        private bool _adsPausedForAnnouncement;
        private bool _announcementLoading;

        private bool AnnouncementBusy => _announcementMixer != null || _announcementLoading;

        private void HandleAnnouncementPlay(JObject msg)
        {
            var id = msg.Value<string>("id");
            if (string.IsNullOrEmpty(id)) return;
            QueueOrPlayAnnouncement(id);
        }

        private void QueueOrPlayAnnouncement(string id)
        {
            var scene = GlobalVariables.Instance?.CurrentScene;
            if (scene is SceneIndex.Gameplay or SceneIndex.Score || AnnouncementBusy)
            {
                if (!_announcementQueue.Contains(id))
                {
                    _announcementQueue.Enqueue(id);
                }

                YargLogger.LogFormatInfo(
                    scene is SceneIndex.Gameplay or SceneIndex.Score
                        ? "YAQ announcement queued until Event/Ads: {0}"
                        : "YAQ announcement queued behind current clip: {0}",
                    id);
                return;
            }

            PlayAnnouncement(id);
        }

        private void TickAnnouncements()
        {
            var scene = GlobalVariables.Instance?.CurrentScene;
            var venueIdle = scene is SceneIndex.Event or SceneIndex.Ads or SceneIndex.Menu;
            if (AnnouncementBusy)
            {
                if (scene == SceneIndex.Ads)
                {
                    PauseAdsForAnnouncement();
                }

                return;
            }

            ResumeAdsAfterAnnouncement();

            if (!venueIdle || _announcementQueue.Count == 0) return;
            PlayAnnouncement(_announcementQueue.Dequeue());
        }

        private void PauseAdsForAnnouncement()
        {
            if (_adsMixer == null) return;
            if (!_adsPausedForAnnouncement)
            {
                _adsHoldFrozenAt = Time.unscaledTime;
            }

            try
            {
                _adsMixer.Pause();
            }
            catch
            {
                // Mixer may already be gone.
            }

            _adsPausedForAnnouncement = true;
        }

        private void ResumeAdsAfterAnnouncement()
        {
            if (!_adsPausedForAnnouncement) return;
            var scene = GlobalVariables.Instance?.CurrentScene;
            if (_adsHoldFrozenAt > 0f)
            {
                _adsHoldUntil += Time.unscaledTime - _adsHoldFrozenAt;
                _adsHoldFrozenAt = 0f;
            }

            _adsPausedForAnnouncement = false;
            if (_adsMixer == null || scene != SceneIndex.Ads) return;
            try
            {
                _adsMixer.Play();
            }
            catch
            {
                // Mixer may have been replaced while the announcement played.
            }
        }

        private void PlayAnnouncement(string id)
        {
            _announcementLoading = true;
            LoadAnnouncementAsync(id, ++_announcementGeneration).Forget();
        }

        private void StopAnnouncement()
        {
            _announcementGeneration++;
            _announcementLoading = false;
            _announcementQueue.Clear();
            DisposeAnnouncementMixer();
        }

        private void DisposeAnnouncementMixer()
        {
            if (_announcementMixer == null) return;
            try
            {
                _announcementMixer.Dispose();
            }
            catch
            {
                // Audio engine may already have torn the mixer down.
            }

            _announcementMixer = null;
        }

        private static string AnnouncementApiUrl(string wsUrl, string id)
        {
            var escaped = Uri.EscapeDataString(id ?? string.Empty);
            if (Uri.TryCreate(wsUrl, UriKind.Absolute, out var uri))
            {
                var scheme = uri.Scheme == "wss" ? "https" : "http";
                return $"{scheme}://{uri.Authority}/api/messages/{escaped}/audio";
            }

            return $"http://127.0.0.1:3000/api/messages/{escaped}/audio";
        }

        private static bool LooksLikeMp3(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 3) return false;
            if (bytes[0] == (byte)'I' && bytes[1] == (byte)'D' && bytes[2] == (byte)'3') return true;
            return bytes.Length >= 2 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0;
        }

        private async UniTaskVoid LoadAnnouncementAsync(string id, int generation)
        {
            byte[] bytes = null;
            try
            {
                var url = AnnouncementApiUrl(EventMode.YaqWebSocketUrl, id);
                bytes = await UniTask.RunOnThreadPool(() =>
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    var response = client.GetAsync(url).GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode) return null;
                    return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                });
            }
            catch (Exception ex)
            {
                YargLogger.LogFormatWarning("YAQ announcement fetch failed: {0}", ex.Message);
            }

            Enqueue(() =>
            {
                if (generation != _announcementGeneration) return;
                if (bytes == null || bytes.Length < 32)
                {
                    _announcementLoading = false;
                    YargLogger.LogFormatWarning("YAQ announcement audio missing for {0}", id);
                    return;
                }

                DisposeAnnouncementMixer();
                try
                {
                    var volume = SettingsManager.Settings?.PreviewVolume.Value ?? 0.5f;
                    if (volume < 0.2f) volume = 0.5f;
                    var stream = new MemoryStream(bytes, writable: false);
                    var fileName = LooksLikeMp3(bytes) ? "yaq-announcement.mp3" : "yaq-announcement.wav";
                    _announcementMixer = GlobalAudioHandler.LoadCustomFile(
                        fileName,
                        stream,
                        1f,
                        volume,
                        false,
                        SongStem.Song);
                    _announcementLoading = false;
                    if (_announcementMixer == null)
                    {
                        YargLogger.LogWarning("YAQ announcement mixer failed");
                        return;
                    }

                    _announcementMixer.SongEnd += OnAnnouncementEnded;
                    PauseAdsForAnnouncement();

                    _announcementMixer.Play();
                    YargLogger.LogFormatInfo("YAQ announcement playing {0}", id);
                }
                catch (Exception ex)
                {
                    _announcementLoading = false;
                    YargLogger.LogFormatWarning("YAQ announcement play failed: {0}", ex.Message);
                    DisposeAnnouncementMixer();
                }
            });
        }

        private void OnAnnouncementEnded()
        {
            Enqueue(() =>
            {
                DisposeAnnouncementMixer();
            });
        }
    }
}
