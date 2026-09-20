using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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

        private const int MaxMessageBytes = 8 * 1024 * 1024;

        private static async Task<string> ReadRemainingMessageAsync(
            ClientWebSocket socket,
            byte[] buffer,
            WebSocketReceiveResult first,
            CancellationToken token)
        {
            using var ms = new MemoryStream();
            ms.Write(buffer, 0, first.Count);
            var result = first;
            while (!result.EndOfMessage)
            {
                if (ms.Length > MaxMessageBytes)
                {
                    throw new InvalidOperationException("YAQ stream message exceeded 8MB");
                }

                result = await socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                ms.Write(buffer, 0, result.Count);
            }

            return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }

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
                    Send(new
                    {
                        type = "hello",
                        version = "yarg-event-1",
                        capabilities = new[] { "player.image", "player.images", "profile.image" }
                    });

                    var buffer = new byte[64 * 1024];
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

                            string json;
                            if (result.EndOfMessage)
                            {
                                json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            }
                            else
                            {
                                json = await ReadRemainingMessageAsync(_socket, buffer, result, token);
                            }

                            if (string.IsNullOrEmpty(json)) continue;
                            try
                            {
                                var obj = JObject.Parse(json);
                                MessageReceived?.Invoke(obj);
                            }
                            catch (Exception parseEx)
                            {
                                YargLogger.LogFormatWarning("YAQ stream message parse failed: {0}", parseEx.Message);
                            }
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
        public string dataUrl;
        public string imageBase64;

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
        public string dataUrl;
        public string imageBase64;

        [JsonExtensionData]
        public IDictionary<string, JToken> Extra;
    }

    internal static class YaqPlayerMedia
    {
        private static readonly string[] ExtraKeys =
        {
            "dataUrl", "data_url", "imageBase64", "image_base64", "base64",
            "imageData", "image_data", "profileImage", "profileImageUrl",
            "imageUrl", "image_url", "avatarUrl", "avatar_url",
            "photoUrl", "photo_url", "profile_image", "profile_image_url",
            "pictureUrl", "picture_url", "image", "avatar", "picture", "photo"
        };

        public static string[] KnownImageFields(YaqPreviewPlayer player)
        {
            if (player == null) return Array.Empty<string>();
            return new[]
            {
                player.dataUrl, player.imageBase64, player.profileImage,
                player.imageUrl, player.avatarUrl, player.photoUrl, player.profileImageUrl
            };
        }

        public static string[] KnownImageFields(YaqSetPlayer player)
        {
            if (player == null) return Array.Empty<string>();
            return new[]
            {
                player.dataUrl, player.imageBase64, player.profileImage,
                player.imageUrl, player.avatarUrl, player.photoUrl, player.profileImageUrl
            };
        }

        public static string ExtractImageRef(
            string id,
            IEnumerable<string> known,
            IDictionary<string, JToken> extra)
        {
            string fallback = null;

            if (known != null)
            {
                foreach (var value in known)
                {
                    var resolved = NormalizeRef(value);
                    if (resolved == null) continue;
                    if (IsInlineImage(resolved)) return resolved;
                    fallback ??= resolved;
                }
            }

            if (extra != null)
            {
                foreach (var key in ExtraKeys)
                {
                    if (!TryGetExtra(extra, key, out var token)) continue;
                    var resolved = NormalizeToken(token);
                    if (resolved == null) continue;
                    if (IsInlineImage(resolved)) return resolved;
                    fallback ??= resolved;
                }
            }

            return fallback;
        }

        public static bool IsInlineImage(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return true;
            return IsRawBase64(value);
        }

        public static bool IsHttpUrl(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        public static string ToDataUrl(string payload, string mime = null)
        {
            var value = NormalizeRef(payload);
            if (value == null) return null;
            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return value;
            if (!IsRawBase64(value)) return null;

            var type = string.IsNullOrWhiteSpace(mime) || mime.Contains('/') == false
                ? GuessMimeFromBase64(value)
                : mime.Trim();
            if (string.IsNullOrEmpty(type) ||
                type.StartsWith("player.", StringComparison.OrdinalIgnoreCase) ||
                type.StartsWith("profile.", StringComparison.OrdinalIgnoreCase))
            {
                type = GuessMimeFromBase64(value);
            }

            return $"data:{type};base64,{value}";
        }

        public static string ToAbsoluteUrl(string wsUrl, string imageRef)
        {
            if (string.IsNullOrEmpty(imageRef) || IsInlineImage(imageRef)) return imageRef;
            if (IsHttpUrl(imageRef)) return imageRef;

            var origin = HttpOriginFromBridge(wsUrl);
            if (imageRef.StartsWith("/")) return origin + imageRef;
            return origin + "/" + imageRef;
        }

        public static List<string> ImageUrlsToTry(string wsUrl, string explicitUrl, string id, string name)
        {
            var urls = new List<string>();
            void Add(string url)
            {
                if (string.IsNullOrEmpty(url) || urls.Contains(url)) return;
                urls.Add(url);
            }

            Add(explicitUrl);

            var origin = HttpOriginFromBridge(wsUrl);
            if (!string.IsNullOrEmpty(id))
            {
                var enc = Uri.EscapeDataString(id);
                Add($"{origin}/api/players/{enc}/image");
                Add($"{origin}/api/players/{enc}/avatar");
                Add($"{origin}/api/avatars/{enc}");
            }

            if (!string.IsNullOrEmpty(name))
            {
                var enc = Uri.EscapeDataString(name.Trim());
                Add($"{origin}/api/players/{enc}/image");
                Add($"{origin}/api/avatars/{enc}");
            }

            return urls;
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

        public static string PlayerCacheKey(string id, string name)
        {
            if (!string.IsNullOrEmpty(id)) return "id:" + id;
            if (!string.IsNullOrEmpty(name)) return "name:" + name.Trim().ToLowerInvariant();
            return null;
        }

        public static string NameCacheKey(string name)
        {
            return string.IsNullOrWhiteSpace(name) ? null : "name:" + name.Trim().ToLowerInvariant();
        }

        private static bool IsRawBase64(string value)
        {
            if (value.Length < 64) return false;
            if (value.IndexOf("://", StringComparison.Ordinal) >= 0) return false;
            if (value.IndexOf('\\') >= 0 || value.IndexOf(' ') >= 0) return false;

            var padding = 0;
            for (var i = value.Length - 1; i >= 0 && value[i] == '='; i--)
            {
                padding++;
                if (padding > 2) return false;
            }

            var dataLength = value.Length - padding;
            if (dataLength < 64) return false;

            for (var i = 0; i < dataLength; i++)
            {
                var c = value[i];
                var ok = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '-' or '_';
                if (!ok) return false;
            }

            return dataLength % 4 != 1;
        }

        private static string GuessMimeFromBase64(string value)
        {
            if (value.StartsWith("iVBOR", StringComparison.Ordinal)) return "image/png";
            if (value.StartsWith("R0lGOD", StringComparison.Ordinal)) return "image/gif";
            if (value.StartsWith("UklGR", StringComparison.Ordinal)) return "image/webp";
            if (value.StartsWith("/9j/", StringComparison.Ordinal)) return "image/jpeg";
            return "image/jpeg";
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

        private static string NormalizeToken(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.String) return NormalizeRef(token.Value<string>());
            if (token is JObject obj)
            {
                return NormalizeRef(obj.Value<string>("dataUrl"))
                    ?? NormalizeRef(obj.Value<string>("data_url"))
                    ?? NormalizeRef(obj.Value<string>("imageBase64"))
                    ?? NormalizeRef(obj.Value<string>("image_base64"))
                    ?? NormalizeRef(obj.Value<string>("base64"))
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
