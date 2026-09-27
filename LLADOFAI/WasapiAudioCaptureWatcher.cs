using UnityEngine;

namespace LLADOFAI
{
    public class WasapiAudioCaptureWatcher : MonoBehaviour
    {
        private const float ListenerScanInterval = 0.5f;

        private GameObject _captureListenerObject;
        private AudioListener _captureListener;
        private AudioListener _routedListener;
        private bool _routedListenerWasEnabled;
        private float _nextListenerScanTime;

        private void Awake()
        {
            _captureListenerObject = new GameObject("WASAPICaptureListener");
            _captureListenerObject.transform.SetParent(transform, false);
            _captureListenerObject.SetActive(false);

            _captureListener = _captureListenerObject.AddComponent<AudioListener>();
            _captureListener.enabled = false;
            _captureListenerObject.AddComponent<WasapiAudioFilter>();

            _captureListenerObject.SetActive(true);
        }

        private void LateUpdate()
        {
            if (!ModEntryPoint.IsEnabled || ModConfiguration.instance == null ||
                !ModConfiguration.instance.wasapiEnabled)
            {
                StopCapture();
                WasapiOutputController.SetCaptureMessage(null);
                return;
            }

            if (WasapiOutputController.Device == null || WasapiOutputController.Bridge == null || WasapiOutputController.Error)
            {
                StopCapture();
                WasapiOutputController.SetCaptureMessage("WASAPI output is not running.");
                return;
            }

            AudioListener sourceListener = FindSourceListener();
            if (sourceListener == null)
            {
                StopCapture();
                WasapiOutputController.SetCaptureMessage("WASAPI output is ready; waiting for a game AudioListener.");
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

            sourceListener.enabled = false;
            _captureListener.enabled = true;
            if (WasapiOutputController.CaptureMessage == null ||
                WasapiOutputController.CaptureMessage == "Waiting for a game AudioListener." ||
                WasapiOutputController.CaptureMessage == "WASAPI output is ready; waiting for a game AudioListener.")
            {
                WasapiOutputController.SetCaptureMessage("Game AudioListener connected; waiting for audio buffers.");
            }
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
            if (ModEntryPoint.IsShuttingDown)
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
            if (ModEntryPoint.IsShuttingDown)
            {
                return;
            }

            ModEntryPoint.IsShuttingDown = true;

            if (_captureListener != null)
            {
                _captureListener.enabled = false;
            }

            _routedListener = null;
            _routedListenerWasEnabled = false;

            WasapiOutputController.SetDevice(false, null);
        }
    }
}
