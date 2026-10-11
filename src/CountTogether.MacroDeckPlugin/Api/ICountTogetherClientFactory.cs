using CountTogether.PublicApiSdk.Abstractions.Client;
using CountTogether.PublicApiSdk.Client;

namespace CountTogether.MacroDeckPlugin.Api;

public interface ICountTogetherClientFactory
{
	ICountTogetherClient Create();
}

internal sealed class CountTogetherClientFactory : ICountTogetherClientFactory
{
	public ICountTogetherClient Create() => new CountTogetherClient();
}
