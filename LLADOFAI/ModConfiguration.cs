using System;
using System.IO;
using System.Xml.Serialization;
using UnityModManagerNet;

namespace LLADOFAI
{
    [XmlRoot("Configuration")]
    public class ModConfiguration : UnityModManager.ModSettings, INAudioConfiguration
    {
        [NonSerialized]
        public static ModConfiguration instance;

        public string audioDeviceName = null;
        public bool asioEnabled = false;
        public bool asioLowLatencyDsp = false;
        public int asioQueueSafetyMilliseconds = 10;
        public string wasapiDeviceId = null;
        public bool wasapiEnabled = false;
        public bool wasapiExclusiveMode = false;
        public bool wasapiLowLatencyDsp = false;
        public string language = "auto";
        public bool showStatistics = false;
        public string audioEngine = "naudio";
        public string fmodOutput = "wasapi";
        public string fmodDeviceId = null;
        public int fmodDspBufferFrames = 512;
        public int fmodDspBufferCount = 3;

        bool INAudioConfiguration.asioEnabled => asioEnabled;
        bool INAudioConfiguration.wasapiEnabled => wasapiEnabled;
        int INAudioConfiguration.asioQueueSafetyMilliseconds => asioQueueSafetyMilliseconds;
        string INAudioConfiguration.audioDeviceName => audioDeviceName;

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            string filePath = GetPath(modEntry);

            try
            {
                using (StreamWriter writer = new StreamWriter(filePath))
                {
                    XmlSerializer serializer = new XmlSerializer(GetType());

                    serializer.Serialize(writer, this);

                    ModEntryPoint.Logger.Log("Successfully saved configuration.");
                }
            }
            catch (Exception ex)
            {
                ModEntryPoint.Logger.Error($"There was an error while saving configuration: {ex.Message}");
            }
        }

        public override string GetPath(UnityModManager.ModEntry modEntry)
        {
            // Keep the existing settings filename when the class name changes.
            return Path.Combine(modEntry.Path, "Configuration.xml");
        }
    }
}
