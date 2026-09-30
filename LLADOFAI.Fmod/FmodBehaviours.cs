using UnityEngine;

namespace LLADOFAI.Fmod
{
    // Drives FMOD once per frame on the main thread.
    internal sealed class FmodUpdater : MonoBehaviour
    {
        private void Update() { FmodBackend.Update(); }

        private void OnApplicationQuit() { FmodBackend.ApplicationQuitting(); }
    }

    // Added to GameObjects whose AudioSources play through FMOD. Unity stops a
    // source's playback when its GameObject is deactivated or destroyed; this
    // mirrors that for the FMOD channels in the same frame.
    internal sealed class FmodSourceLink : MonoBehaviour
    {
        private void OnDisable() { FmodEngine.Active?.GameObjectDisabled(this); }

        private void OnDestroy() { FmodEngine.Active?.LinkDestroyed(this); }
    }
}
