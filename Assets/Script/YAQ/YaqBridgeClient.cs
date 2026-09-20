using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using YARG.Core.Logging;

namespace YARG.YAQ
{
    /// <summary>
    /// WebSocket client that talks to the YAQ server bridge (<c>/ws?role=yarg</c>).
    /// </summary>
    public sealed class YaqBridgeClient : IDisposable
    {
        public event Action Connected;
        public event Action Disconnected;
        public event Action<JObject> MessageReceived;

        private ClientWebSocket _socket;
        private CancellationTokenSource _cts;
        private readonly ConcurrentQueue<string> _outbound = new();
        private Task _loop;
        private string _url;

        public bool IsConnected =>
            _socket != null && _socket.State == WebSocketState.Open;

        public void Start(string url)
        {
            _url = url;
            Stop();
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
            }
            catch
            {
                // ignored
            }

            try
            {
                _socket?.Abort();
                _socket?.Dispose();
            }
            catch
            {
                // ignored
            }

            _socket = null;
            _cts = null;
        }

        public void Send(object payload)
        {
            var json = JsonConvert.SerializeObject(payload);
            _outbound.Enqueue(json);
        }

        public void Dispose() => Stop();

        private async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    _socket = new ClientWebSocket();
                    await _socket.ConnectAsync(new Uri(_url), token);
                    YargLogger.LogFormatInfo("YAQ bridge connected to {0}", _url);
                    Connected?.Invoke();
                    Send(new { type = "hello", version = "yarg-event-1" });

                    var buffer = new byte[1024 * 256];
                    while (_socket.State == WebSocketState.Open && !token.IsCancellationRequested)
                    {
                        while (_outbound.TryDequeue(out var outbound))
                        {
                            var bytes = Encoding.UTF8.GetBytes(outbound);
                            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
                        }

                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(250));
                        try
                        {
                            var result = await _socket.ReceiveAsync(buffer, timeoutCts.Token);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                break;
                            }

                            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            var obj = JObject.Parse(json);
                            MessageReceived?.Invoke(obj);
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            // receive timeout — loop to flush outbound
                        }
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    YargLogger.LogFormatWarning("YAQ bridge disconnected: {0}", ex.Message);
                    Disconnected?.Invoke();
                    await Task.Delay(2000, token);
                }
            }
        }
    }

    [Serializable]
    public class YaqQueuePreview
    {
        public string setId;
        public string songHash;
        public string songName;
        public string songArtist;
        public List<YaqPreviewPlayer> players = new();
    }

    [Serializable]
    public class YaqPreviewPlayer
    {
        public string id;
        public string name;
        public string instrument;
        public string difficulty;
        public string imageUrl;
        public string avatarUrl;
        public string photoUrl;
        public string profileImage;
        public string profileImageUrl;

        [JsonExtensionData]
        public IDictionary<string, JToken> Extra;
    }

    [Serializable]
    public class YaqSetPlayer
    {
        public string id;
        public string name;
        public string songHash;
        public string instrument;
        public string difficulty;
        public string imageUrl;
        public string avatarUrl;
        public string photoUrl;
        public string profileImage;
        public string profileImageUrl;

        [JsonExtensionData]
        public IDictionary<string, JToken> Extra;
    }

    internal static class YaqPlayerMedia
    {
        private static readonly string[] ExtraKeys =
        {
            "imageUrl", "image_url", "avatarUrl", "avatar_url",
            "photoUrl", "photo_url", "profileImage", "profileImageUrl",
            "profile_image", "profile_image_url", "pictureUrl", "picture_url",
            "dataUrl", "data_url", "image", "avatar", "picture", "photo"
        };

        public static string ExtractImageRef(
            string id,
            IEnumerable<string> known,
            IDictionary<string, JToken> extra)
        {
            if (known != null)
            {
                foreach (var value in known)
                {
                    var resolved = NormalizeRef(value);
                    if (resolved != null) return resolved;
                }
            }

            if (extra != null)
            {
                foreach (var key in ExtraKeys)
                {
                    if (!TryGetExtra(extra, key, out var token)) continue;
                    var resolved = NormalizeToken(token);
                    if (resolved != null) return resolved;
                }
            }

            return string.IsNullOrEmpty(id) ? null : $"/api/players/{Uri.EscapeDataString(id)}/image";
        }

        private static bool TryGetExtra(
            IDictionary<string, JToken> extra,
            string key,
            out JToken token)
        {
            if (extra.TryGetValue(key, out token)) return true;
            foreach (var pair in extra)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    token = pair.Value;
                    return true;
                }
            }

            token = null;
            return false;
        }

        public static string ToAbsoluteUrl(string wsUrl, string imageRef)
        {
            if (string.IsNullOrEmpty(imageRef)) return null;
            if (imageRef.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return imageRef;
            if (Uri.TryCreate(imageRef, UriKind.Absolute, out _)) return imageRef;

            var origin = HttpOriginFromBridge(wsUrl);
            if (imageRef.StartsWith("/")) return origin + imageRef;
            return origin + "/" + imageRef;
        }

        public static string HttpOriginFromBridge(string wsUrl)
        {
            if (Uri.TryCreate(wsUrl, UriKind.Absolute, out var uri))
            {
                var scheme = uri.Scheme == "wss" ? "https" : "http";
                return $"{scheme}://{uri.Authority}";
            }

            return "http://127.0.0.1:3000";
        }

        private static string NormalizeToken(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.String) return NormalizeRef(token.Value<string>());
            if (token is JObject obj)
            {
                return NormalizeRef(obj.Value<string>("dataUrl"))
                    ?? NormalizeRef(obj.Value<string>("data_url"))
                    ?? NormalizeRef(obj.Value<string>("url"))
                    ?? NormalizeRef(obj.Value<string>("src"))
                    ?? NormalizeRef(obj.Value<string>("href"));
            }

            return null;
        }

        private static string NormalizeRef(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    [Serializable]
    public class YaqPlaySet
    {
        public string id;
        public string songHash;
        public string songName;
        public string songArtist;
        public List<string> playerIds;
    }
}
