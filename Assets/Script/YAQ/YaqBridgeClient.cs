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
        private int _generation;

        public bool IsConnected =>
            _socket != null && _socket.State == WebSocketState.Open;

        public void Start(string url)
        {
            _url = url;
            var cts = new CancellationTokenSource();
            Stop();
            _cts = cts;
            var generation = ++_generation;
            _loop = Task.Run(() => RunAsync(generation, cts.Token));
        }

        public void Stop()
        {
            _generation++;
            try
            {
                _cts?.Cancel();
            }
            catch
            {
                // ignored
            }

            TryDisposeSocket(_socket);
            _socket = null;
            _cts = null;
        }

        public void Send(object payload)
        {
            var json = JsonConvert.SerializeObject(payload);
            _outbound.Enqueue(json);
        }

        public void Dispose() => Stop();

        private const int MaxInboundBytes = 8 * 1024 * 1024;
        private const int ReconnectDelayMs = 2000;

        private static void TryDisposeSocket(ClientWebSocket socket)
        {
            if (socket == null) return;
            try
            {
                socket.Abort();
            }
            catch
            {
                // ignored
            }

            try
            {
                socket.Dispose();
            }
            catch
            {
                // ignored
            }
        }

        private static async Task<string> ReceiveTextAsync(
            ClientWebSocket socket,
            byte[] buffer,
            CancellationToken token)
        {
            using var payload = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                payload.Write(buffer, 0, result.Count);
                if (payload.Length > MaxInboundBytes)
                {
                    throw new InvalidOperationException("YAQ bridge message exceeded 8 MB");
                }
            } while (!result.EndOfMessage);

            return Encoding.UTF8.GetString(payload.GetBuffer(), 0, (int)payload.Length);
        }

        private async Task RunAsync(int generation, CancellationToken token)
        {
            while (!token.IsCancellationRequested && generation == _generation)
            {
                ClientWebSocket socket = null;
                var announced = false;
                try
                {
                    socket = new ClientWebSocket();
                    await socket.ConnectAsync(new Uri(_url), token);
                    if (generation != _generation || token.IsCancellationRequested)
                    {
                        TryDisposeSocket(socket);
                        return;
                    }

                    _socket = socket;
                    YargLogger.LogFormatInfo("YAQ bridge connected to {0}", _url);
                    announced = true;
                    Connected?.Invoke();
                    Send(new
                    {
                        type = "hello",
                        version = "yarg-event-1",
                        capabilities = new[]
                        {
                            "player.image",
                            "player.images",
                            "profile.image"
                        }
                    });

                    var buffer = new byte[64 * 1024];
                    while (socket.State == WebSocketState.Open &&
                           !token.IsCancellationRequested &&
                           generation == _generation)
                    {
                        while (_outbound.TryDequeue(out var outbound))
                        {
                            var bytes = Encoding.UTF8.GetBytes(outbound);
                            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
                        }

                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(250));
                        try
                        {
                            var json = await ReceiveTextAsync(socket, buffer, timeoutCts.Token);
                            if (json == null)
                            {
                                break;
                            }

                            var obj = JObject.Parse(json);
                            MessageReceived?.Invoke(obj);
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            // receive timeout — loop to flush outbound
                        }
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested && generation == _generation)
                {
                    YargLogger.LogFormatWarning("YAQ bridge disconnected: {0}", ex.Message);
                    if (announced)
                    {
                        Disconnected?.Invoke();
                    }
                }
                finally
                {
                    if (ReferenceEquals(_socket, socket))
                    {
                        _socket = null;
                    }

                    TryDisposeSocket(socket);
                }

                if (token.IsCancellationRequested || generation != _generation)
                {
                    return;
                }

                try
                {
                    await Task.Delay(ReconnectDelayMs, token);
                }
                catch (OperationCanceledException)
                {
                    return;
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
        public YaqFollowingSong following;
    }

    [Serializable]
    public class YaqFollowingSong
    {
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
        public string slotId;
        public string dataUrl;
        public bool isBot;
    }

    [Serializable]
    public class YaqSetPlayer
    {
        public string id;
        public string name;
        public string songHash;
        public string instrument;
        public string difficulty;
        public string slotId;
        public string dataUrl;
        public bool isBot;
        public bool isSongMaster;
    }

    [Serializable]
    public class YaqVenueProfile
    {
        public string slotId;
        public string name;
        public string instrument;
        public string dataUrl;
        public bool isBot;
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
