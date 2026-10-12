using System.Text.Json;
using CountTogether.MacroDeckPlugin.Actions;
using CountTogether.MacroDeckPlugin.ConfigFlow;
using MacroDeck.Sdk.Migration;

namespace CountTogether.MacroDeckPlugin.Migration;

/// <summary>Takes over the API token and the increment/decrement buttons of the Macro Deck 2 plugin.</summary>
public sealed class MacroDeck2Migration : IIntegrationMigration
{
	// The Macro Deck 2 plugin's assembly name, which is how its actions were serialized.
	internal const string LegacyAssemblyName = "CountTogether.MacroDeckPlugin";

	private const string LegacyNamespace = "CountTogether.MacroDeckPlugin.Actions.";
	private const string LegacyCredentialKey = "apiToken";

	private readonly string _integrationId;

	public MacroDeck2Migration(string integrationId) => _integrationId = integrationId;

	public MigrationSource Source => MigrationSource.MacroDeck2;

	public IReadOnlyList<string> ClaimedActionSources { get; } = [LegacyAssemblyName];

	// Macro Deck 2 stored credentials as "<author>_<assembly name>" without an extension, so the host reads
	// the part after the last dot as one and the key loses ".macrodeckplugin". Both spellings are claimed.
	public IReadOnlyList<string> ClaimedSettingsSources { get; } =
		["counttogether_counttogether", "counttogether_counttogether.macrodeckplugin"];

	public Task<ActionMigrationResult?> MigrateActionAsync(ForeignAction action, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(action);

		var (actionId, label) = action.TypeName switch
		{
			LegacyNamespace + "IncrementCounterAction" => (ChangeCounterAction.IncrementId, "Increment counter"),
			LegacyNamespace + "DecrementCounterAction" => (ChangeCounterAction.DecrementId, "Decrement counter"),
			_ => (null, null),
		};

		if (actionId is null || !Guid.TryParse(action.Configuration?.Trim(), out var counterId))
		{
			return Task.FromResult<ActionMigrationResult?>(null);
		}

		var parameters = new Dictionary<string, JsonElement>
		{
			[ChangeCounterAction.CounterParameter] = JsonSerializer.SerializeToElement(counterId.ToString()),
		};

		return Task.FromResult<ActionMigrationResult?>(
			new ActionMigrationResult(_integrationId, actionId, action.DisplayName ?? label!, parameters));
	}

	public Task<IReadOnlyList<MigratedConfiguration>> MigrateConfigurationAsync(
		ForeignPluginSettings settings,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(settings);

		var token = settings.Credentials
			.Select(credentials => credentials.GetValueOrDefault(LegacyCredentialKey))
			.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

		IReadOnlyList<MigratedConfiguration> result = token is null
			? []
			:
			[
				new MigratedConfiguration(_integrationId,
					ApiTokenConfigFlow.EntryTitle,
					new Dictionary<string, JsonElement>(),
					new Dictionary<string, MigratedSecret>
					{
						[ApiTokenConfigFlow.TokenKey] = new(token.Trim(), MigratedSecretKind.Secret),
					}),
			];

		return Task.FromResult(result);
	}
}
