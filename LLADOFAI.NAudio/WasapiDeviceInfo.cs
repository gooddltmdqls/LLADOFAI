namespace LLADOFAI
{
    public sealed class WasapiDeviceInfo
    {
        public string Id { get; private set; }
        public string Name { get; private set; }

        public WasapiDeviceInfo(string id, string name)
        {
            Id = id;
            Name = name;
        }
    }
}
