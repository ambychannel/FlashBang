namespace Brio.Config;

public class SyncConfiguration
{
    // Our relay on Cloudflare. Players can point FlashBang at another one in the Sync window.
    public const string DefaultRelayUrl = "https://flashbang-relay.flashbang-relay.workers.dev";

    public string RelayUrl { get; set; } = DefaultRelayUrl;
}
