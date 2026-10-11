using CountTogether.MacroDeckPlugin.Actions;
using CountTogether.MacroDeckPlugin.Api;
using CountTogether.MacroDeckPlugin.ConfigFlow;
using CountTogether.MacroDeckPlugin.Events;
using CountTogether.MacroDeckPlugin.Variables;
using MacroDeck.Plugin.Hosting.Integrations.HostApis;
using MacroDeck.Plugin.Hosting.Transport;
using MacroDeck.Plugin.Protocol.Handshake;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using MacroDeck.Sdk.Events;
using MacroDeck.Sdk.Issues;
using MacroDeck.Sdk.Variables;
using Serilog;

namespace CountTogether.MacroDeckPlugin;

/// <summary>
/// The CountTogether integration: the capability opt-ins and the lifecycle. The work happens in
/// <see cref="CountTogetherConnection" /> and the classes it feeds.
/// </summary>
public sealed class CountTogetherIntegration : IPluginIntegration, IConfigFlowProvider, IVariableProvider, IEventProvider,
	IDynamicEventOptionsProvider, IIntegrationIssueProvider
{
	internal const string TokenRejectedIssueId = "token-rejected";

	private readonly CountTogetherConnection _connection;
	private readonly IPluginCatalogNotifier _catalog;
	private readonly ILogger _logger;
	private readonly Lock _sync = new();

	private IReadOnlyList<VariableDefinition> _variables = [];
	private IEventPublisher? _events;

	public CountTogetherIntegration(CountTogetherConnection connection, IPluginCatalogNotifier catalog, ILogger logger)
	{
		_connection = connection;
		_catalog = catalog;
		_logger = logger.ForContext<CountTogetherIntegration>();
		Actions = [ChangeCounterAction.Increment(connection), ChangeCounterAction.Decrement(connection)];
	}

	public IReadOnlyList<IActionDefinition> Actions { get; }

	public bool AllowsMultipleConfigurations => false;

	public IReadOnlyList<VariableDefinition> Variables => Volatile.Read(ref _variables);

	public IReadOnlyList<EventDefinition> EventDefinitions => CounterEvents.Definitions;

	/// <summary>
	/// Runs after every (re)connection to Macro Deck and after the configuration changed, so it detaches
	/// before it attaches and leaves a running connection alone when the token is the same.
	/// </summary>
	public async Task InitializeAsync(IIntegrationContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		Detach();
		lock (_sync)
		{
			_events = context.Events;
		}

		_connection.CatalogChanged += OnCatalogChanged;
		_connection.ValueChanged += OnValueChanged;
		_connection.Removed += OnRemoved;
		OnCatalogChanged();

		string? token;
		try
		{
			token = await ReadTokenAsync(context.Config).ConfigureAwait(false);
		}
		catch (HostInvocationException exception)
		{
			_logger.Warning("Reading the CountTogether configuration failed: {Error}", exception.Message);
			return;
		}

		if (token is null)
		{
			await _connection.StopAsync().ConfigureAwait(false);
			return;
		}

		await _connection.StartAsync(token).ConfigureAwait(false);
	}

	public async Task ShutdownAsync()
	{
		Detach();
		await _connection.StopAsync().ConfigureAwait(false);
	}

	public IConfigFlow CreateConfigFlow() => new ApiTokenConfigFlow(_connection);

	public ValueTask<VariableReading> ReadAsync(string localId, CancellationToken cancellationToken = default)
		=> ValueTask.FromResult(_connection.State == ConnectionState.Connected &&
			CounterVariables.TryParseId(localId, out var counterId) &&
			_connection.Find(counterId)?.Value is { } value
				? VariableReading.Of(value)
				: VariableReading.Unavailable);

	public Task<DynamicOptionsResult> GetEventOptionsAsync(EventOptionsContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		// Counter names are the user's own data, so the option labels are literals.
		var options = _connection.Counters
			.Where(counter => string.IsNullOrWhiteSpace(context.Filter) ||
				counter.Name.Contains(context.Filter, StringComparison.CurrentCultureIgnoreCase))
			.Select(counter => new ActionParameterOption { Value = CounterEvents.FormatCounterId(counter.Id), Label = counter.Name })
			.ToList();

		return Task.FromResult(new DynamicOptionsResult { Options = options });
	}

	public Task<IReadOnlyList<IntegrationIssue>> GetIssuesAsync(CancellationToken cancellationToken = default)
	{
		IReadOnlyList<IntegrationIssue> issues = _connection.State == ConnectionState.Unauthorized
			?
			[
				new IntegrationIssue
				{
					Id = TokenRejectedIssueId,
					Title = Strings.Issues.TokenRejected.Title(),
					Description = Strings.Issues.TokenRejected.Description(),
					Severity = IntegrationIssueSeverity.Error,
					ActionLabel = Strings.Issues.TokenRejected.Action(),
				},
			]
			: [];

		return Task.FromResult(issues);
	}

	public Task<IssueResolution> ResolveIssueAsync(string issueId, CancellationToken cancellationToken = default)
		=> Task.FromResult(issueId == TokenRejectedIssueId
			? IssueResolution.Ok(followUp: IssueResolutionFollowUp.StartConfigFlow)
			: IssueResolution.Failed());

	private static async Task<string?> ReadTokenAsync(IIntegrationConfig config)
	{
		var entries = await config.GetEntriesAsync().ConfigureAwait(false);
		if (entries.Count == 0)
		{
			return null;
		}

		var token = await config.GetSecretAsync(entries[0].Id, ApiTokenConfigFlow.TokenKey).ConfigureAwait(false);
		return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
	}

	private void Detach()
	{
		_connection.CatalogChanged -= OnCatalogChanged;
		_connection.ValueChanged -= OnValueChanged;
		_connection.Removed -= OnRemoved;

		lock (_sync)
		{
			_events = null;
		}
	}

	private void OnCatalogChanged()
	{
		Volatile.Write(ref _variables, CounterVariables.Build(_connection.Counters));
		_catalog.CatalogChanged(CapabilityKinds.Variables, reason: "CountTogether counters changed");
	}

	private void OnValueChanged(CounterSnapshot previous, CounterSnapshot current)
		=> Publish(CounterEvents.ValueChangedId, CounterEvents.ValueChangedPayload(previous, current));

	private void OnRemoved(CounterSnapshot counter) => Publish(CounterEvents.DeletedId, CounterEvents.DeletedPayload(counter));

	private void Publish(string eventId, IReadOnlyDictionary<string, object?> payload)
	{
		IEventPublisher? events;
		lock (_sync)
		{
			events = _events;
		}

		events?.Publish(eventId, payload);
	}
}
