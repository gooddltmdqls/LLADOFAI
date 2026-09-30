using NAudio.Wave;
using System.Collections.Generic;

namespace LLADOFAI
{
    public class AsioDriverManager
    {
        private static readonly List<string> _drivers = new List<string>();

        public void LoadAsioDrivers()
        {
            _drivers.Clear();

            string[] asioDriverNames = AsioOut.GetDriverNames();

            NAudioHost.Log($"Found {asioDriverNames.Length} ASIO drivers: {string.Join(", ", asioDriverNames)}");

            foreach (string name in asioDriverNames)
            {
                _drivers.Add(name);
            }
        }

        public List<string> GetAsioDrivers()
        {
            return _drivers;
        }
    }
}
