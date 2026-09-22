using UnityEngine;

namespace YARG.YAQ
{
    /// <summary>
    /// Scene-local Ads host. The YAQ bridge stays on a persistent
    /// <see cref="EventModeController"/>; this object owns the ads HUD while the
    /// Ads scene is loaded.
    /// </summary>
    public class AdsModeScene : MonoBehaviour
    {
        private void OnEnable()
        {
            EventModeHudCamera.Bind();
        }

        private void OnGUI()
        {
            EventModeController.Instance?.DrawAdsHud();
        }
    }
}
