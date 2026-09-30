using System;

namespace LLADOFAI
{
    // The Unity mod assembly supplies these services. The output code keeps its
    // existing namespace and behavior without taking a dependency on the host.
    public interface INAudioConfiguration
    {
        bool asioEnabled { get; }
        bool wasapiEnabled { get; }
        int asioQueueSafetyMilliseconds { get; }
        string audioDeviceName { get; }
    }

    public static class NAudioHost
    {
        public static INAudioConfiguration Configuration;
        public static volatile bool IsEnabled;
        public static volatile bool IsShuttingDown;
        public static Action<string> Log;
        public static Action<string> Error;
        public static Func<string, string> Get;
        public static Func<string, object[], string> Formatter;

        public static string Format(string key, params object[] arguments)
        {
            return Formatter(key, arguments);
        }
    }
}
