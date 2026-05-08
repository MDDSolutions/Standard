using MDDFoundation;

namespace FileRelay.TestClient;

public class ClientSettings : CustomConfiguration
{
    public string ServerUrl          { get; set; } = "https://mdd-trident1:61489/";
    public string ApiKey             { get; set; } = "";
    public int    ParallelConnections { get; set; } = 4;
    public double ThrottleMBps       { get; set; } = 0;

    public override void ApplyDefaults()
    {
        ServerUrl           = "https://mdd-trident1:61489/";
        ApiKey              = "";
        ParallelConnections = 4;
        ThrottleMBps        = 0;
    }

    public static ClientSettings Load() =>
        CustomConfiguration.Load<ClientSettings>("FileRelayClient.xml");
}
