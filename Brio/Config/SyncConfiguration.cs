namespace Brio.Config;

public class SyncConfiguration
{
    // Left empty until the relay is deployed; the Sync window asks for it when empty.
    public const string DefaultRelayUrl = "";

    public string RelayUrl { get; set; } = DefaultRelayUrl;
}
