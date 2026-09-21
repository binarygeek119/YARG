using System;
using System.Collections.Generic;
using UnityEngine;
using YARG.Core.Logging;

namespace YARG.YAQ
{
    /// <summary>
    /// Event HUD portraits decoded from YAQ <c>dataUrl</c> / <c>player.images</c>.
    /// </summary>
    internal static class YaqProfileAvatar
    {
        internal const int Size = 64;

        private static readonly Dictionary<string, Texture2D> ByKey = new();
        private static readonly HashSet<Texture2D> Owned = new();

        public static void Remember(string playerId, string name, string dataUrl)
        {
            if (string.IsNullOrEmpty(dataUrl)) return;

            try
            {
                var tex = Decode(dataUrl);
                if (tex == null) return;

                tex.name = $"YAQ Portrait {playerId ?? name}";
                tex.hideFlags = HideFlags.HideAndDontSave;
                Owned.Add(tex);

                Replace("n:" + (name ?? string.Empty), tex);
                if (!string.IsNullOrEmpty(playerId))
                {
                    Replace(playerId, tex);
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogFormatWarning("YAQ portrait decode failed: {0}", ex.Message);
            }
        }

        public static Texture2D ForPlayer(string name, string playerId = null)
        {
            if (!string.IsNullOrEmpty(playerId) &&
                ByKey.TryGetValue(playerId, out var byId) &&
                byId != null)
            {
                return byId;
            }

            if (!string.IsNullOrEmpty(name) &&
                ByKey.TryGetValue("n:" + name, out var byName) &&
                byName != null)
            {
                return byName;
            }

            return null;
        }

        public static void ClearCache()
        {
            foreach (var tex in Owned)
            {
                if (tex != null)
                {
                    UnityEngine.Object.Destroy(tex);
                }
            }

            Owned.Clear();
            ByKey.Clear();
        }

        private static void Replace(string key, Texture2D tex)
        {
            if (string.IsNullOrEmpty(key) || tex == null) return;
            ByKey.TryGetValue(key, out var previous);
            ByKey[key] = tex;
            if (previous != null && previous != tex && !StillReferenced(previous))
            {
                Owned.Remove(previous);
                UnityEngine.Object.Destroy(previous);
            }
        }

        private static bool StillReferenced(Texture2D tex)
        {
            foreach (var existing in ByKey.Values)
            {
                if (existing == tex) return true;
            }

            return false;
        }

        private static Texture2D Decode(string dataUrl)
        {
            var payload = dataUrl.Trim();
            var comma = payload.IndexOf(',');
            var b64 = comma >= 0 ? payload.Substring(comma + 1) : payload;
            if (string.IsNullOrEmpty(b64)) return null;

            var bytes = Convert.FromBase64String(b64);
            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!source.LoadImage(bytes))
            {
                UnityEngine.Object.Destroy(source);
                return null;
            }

            var rounded = ToRound(source, Size);
            if (rounded != source)
            {
                UnityEngine.Object.Destroy(source);
            }

            return rounded;
        }

        private static Texture2D ToRound(Texture2D source, int size)
        {
            if (source == null) return null;

            try
            {
                Texture2D readable = source;
                var copied = false;
                if (source.width != size || source.height != size)
                {
                    readable = CopyScaled(source, size);
                    copied = true;
                }

                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };

                var radius = (size - 1) * 0.5f;
                var inner = Mathf.Max(1f, radius - 3f);
                var center = new Vector2(radius, radius);
                var frame = new Color(1f, 1f, 1f, 0.95f);

                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        var distance = Vector2.Distance(new Vector2(x, y), center);
                        if (distance > radius + 0.5f)
                        {
                            tex.SetPixel(x, y, Color.clear);
                            continue;
                        }

                        var outerAlpha = Mathf.Clamp01(radius + 0.5f - distance);
                        if (distance > inner)
                        {
                            tex.SetPixel(x, y, new Color(frame.r, frame.g, frame.b, frame.a * outerAlpha));
                            continue;
                        }

                        var u = (x - (center.x - inner)) / (inner * 2f);
                        var v = (y - (center.y - inner)) / (inner * 2f);
                        var pixel = readable.GetPixelBilinear(Mathf.Clamp01(u), Mathf.Clamp01(v));
                        pixel.a *= Mathf.Clamp01(inner + 0.5f - distance) * outerAlpha;
                        tex.SetPixel(x, y, pixel);
                    }
                }

                tex.Apply(false, false);
                if (copied && readable != null)
                {
                    UnityEngine.Object.Destroy(readable);
                }

                return tex;
            }
            catch (Exception)
            {
                return source;
            }
        }

        private static Texture2D CopyScaled(Texture source, int size)
        {
            var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            var copy = new Texture2D(size, size, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            copy.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return copy;
        }
    }
}
