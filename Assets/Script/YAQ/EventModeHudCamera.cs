using UnityEngine;

namespace YARG.YAQ
{
    /// <summary>
    /// Event/Ads HUD is IMGUI. URP MSAA makes <see cref="GUI.DrawTexture"/> sample the
    /// camera target as Texture2DMS, which warns and skips regular Texture2Ds
    /// (album art, QR, generated HUD shapes).
    /// </summary>
    internal static class EventModeHudCamera
    {
        internal static void Bind()
        {
            Camera.onPreCull -= DisableIfEventHud;
            Camera.onPreCull += DisableIfEventHud;
            DisableMsaaForImgui();
        }

        internal static void DisableMsaaForImgui()
        {
            var cameras = Camera.allCameras;
            for (var i = 0; i < cameras.Length; i++)
            {
                DisableIfEventHud(cameras[i]);
            }
        }

        private static void DisableIfEventHud(Camera camera)
        {
            if (camera == null || camera.targetTexture != null) return;
            var scene = GlobalVariables.Instance?.CurrentScene;
            if (scene is not SceneIndex.Event and not SceneIndex.Ads) return;
            camera.allowMSAA = false;
        }
    }
}
