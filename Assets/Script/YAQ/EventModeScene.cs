using UnityEngine;

namespace YARG.YAQ
{
    /// <summary>
    /// Scene-local Event Mode idle host. The YAQ bridge stays on a persistent
    /// <see cref="EventModeController"/>; this object owns the idle HUD while the
    /// Event scene is loaded.
    /// </summary>
    public class EventModeScene : MonoBehaviour
    {
        private void OnGUI()
        {
            EventModeController.Instance?.DrawIdleHud();
        }
    }
}
