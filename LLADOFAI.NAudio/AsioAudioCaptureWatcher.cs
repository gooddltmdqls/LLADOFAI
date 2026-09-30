using UnityEngine;

namespace LLADOFAI
{
    public class AsioAudioCaptureWatcher : MonoBehaviour
    {
        private const float ListenerScanInterval = 0.5f;

        private GameObject _captureListenerObject;
        private AudioListener _captureListener;
        private AudioListener _routedListener;
        private bool _routedListenerWasEnabled;
        private float _nextListenerScanTime;

        private void Awake()
        {
            // Keep the ASIO filter on its own listener object. Some game objects
            // contain both an AudioSource and an AudioListener, and Unity cannot
            // bind one OnAudioFilterRead component to both DSP chains.
            _captureListenerObject = new GameObject("ASIOCaptureListener");
            _captureListenerObject.transform.SetParent(transform, false);
            _captureListenerObject.SetActive(false);

            _captureListener = _captureListenerObject.AddComponent<AudioListener>();
            _captureListener.enabled = false;
            _captureListenerObject.AddComponent<AsioAudioFilter>();

            _captureListenerObject.SetActive(true);
        }

        private void LateUpdate()
        {
            if (!NAudioHost.IsEnabled || NAudioHost.Configuration == null ||
                !NAudioHost.Configuration.asioEnabled ||
                AsioAudioFilter.AsioDevice == null || AsioAudioFilter.Bridge == null)
            {
                StopCapture();
                return;
            }

            AudioListener sourceListener = FindSourceListener();
            if (sourceListener == null)
            {
                StopCapture();
                return;
            }

            if (_routedListener != sourceListener)
            {
                RestoreRoutedListener();
                _routedListener = sourceListener;
                _routedListenerWasEnabled = sourceListener.enabled;
            }

            _captureListenerObject.transform.SetPositionAndRotation(
                sourceListener.transform.position,
                sourceListener.transform.rotation);
            _captureListener.velocityUpdateMode = sourceListener.velocityUpdateMode;

            // Disable the game's listener before enabling the capture listener so
            // Unity never has two active listeners during ASIO routing.
            sourceListener.enabled = false;
            _captureListener.enabled = true;
        }

        public void StopCapture()
        {
            if (_captureListener != null)
            {
                _captureListener.enabled = false;
            }

            RestoreRoutedListener();
        }

        private AudioListener FindSourceListener()
        {
            // Prefer the current source while its GameObject is alive. It is
            // intentionally disabled during routing, so another listener must not
            // cause the watcher to switch sources and leave the old one enabled.
            if (_routedListener != null && _routedListener.gameObject.activeInHierarchy)
            {
                return _routedListener;
            }

            if (Time.unscaledTime >= _nextListenerScanTime)
            {
                _nextListenerScanTime = Time.unscaledTime + ListenerScanInterval;

                AudioListener[] listeners = Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None);
                foreach (AudioListener listener in listeners)
                {
                    if (listener != null && listener != _captureListener && listener.isActiveAndEnabled)
                    {
                        return listener;
                    }
                }
            }

            return null;
        }

        private void RestoreRoutedListener()
        {
            if (_routedListener != null)
            {
                _routedListener.enabled = _routedListenerWasEnabled;
            }

            _routedListener = null;
            _routedListenerWasEnabled = false;
        }

        private void OnDestroy()
        {
            if (NAudioHost.IsShuttingDown)
            {
                if (_captureListener != null)
                {
                    _captureListener.enabled = false;
                }

                _routedListener = null;
                _routedListenerWasEnabled = false;
                return;
            }

            StopCapture();
        }

        private void OnApplicationQuit()
        {
            if (NAudioHost.IsShuttingDown)
            {
                return;
            }

            // Stop capture before releasing the ASIO driver. Do not restore the
            // game's listener during shutdown; Unity is tearing down its audio graph.
            NAudioHost.IsShuttingDown = true;

            if (_captureListener != null)
            {
                _captureListener.enabled = false;
            }

            _routedListener = null;
            _routedListenerWasEnabled = false;

            AsioAudioFilter.SetDevice(false, null);
        }
    }
}
