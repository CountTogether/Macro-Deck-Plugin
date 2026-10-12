using CountTogether.MacroDeckPlugin.Actions;
using CountTogether.MacroDeckPlugin.Api;
using CountTogether.MacroDeckPlugin.Calendar;
using CountTogether.MacroDeckPlugin.ConfigFlow;
using CountTogether.MacroDeckPlugin.Events;
using CountTogether.MacroDeckPlugin.Migration;
using CountTogether.MacroDeckPlugin.Variables;
using CountTogether.MacroDeckPlugin.Widgets;
using MacroDeck.Plugin.Hosting;
using MacroDeck.Plugin.Hosting.Integrations.HostApis;
using MacroDeck.Plugin.Hosting.Transport;
using MacroDeck.Plugin.Protocol.Handshake;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Calendar;
using MacroDeck.Sdk.ConfigFlow;
using MacroDeck.Sdk.Events;
using MacroDeck.Sdk.Issues;
using MacroDeck.Sdk.Migration;
using MacroDeck.Sdk.Ui;
using MacroDeck.Sdk.Variables;
using MacroDeck.Sdk.Widgets;
using MacroDeck.Ui.Model.Surfaces;
using Serilog;

namespace CountTogether.MacroDeckPlugin;

/// <summary>
/// The CountTogether integration: the capability opt-ins and the lifecycle. The work happens in
/// <see cref="CountTogetherConnection" /> and the classes it feeds.
/// </summary>
public sealed class CountTogetherIntegration : IPluginIntegration, IConfigFlowProvider, IVariableProvider, IEventProvider,
	IDynamicEventOptionsProvider, IIntegrationIssueProvider, IMigrationProvider, IWidgetTypeProvider, IUiProvider, ICalendarProvider
{
	internal const string TokenRejectedIssueId = "token-rejected";

	private static readonly TimeSpan CalendarConnectWait = TimeSpan.FromSeconds(10);

	private readonly CountTogetherConnection _connection;
	private readonly CounterValueWriter _writer;
	private readonly IPluginCatalogNotifier _catalog;
	private readonly CounterWidgetUi _widgets;
	private readonly ILogger _logger;
	private readonly Lock _sync = new();

	private IReadOnlyList<VariableDefinition> _variables = CounterVariables.Build([]);
	private IReadOnlyList<CalendarAccount> _calendarAccounts = [];
	private IEventPublisher? _events;

	public CountTogetherIntegration(CountTogetherConnection connection, CounterValueWriter writer,
		IPluginCatalogNotifier catalog, PluginMetadata metadata, TimeProvider time, ILogger logger)
	{
		ArgumentNullException.ThrowIfNull(metadata);

		_connection = connection;
		_writer = writer;
		_catalog = catalog;
		_widgets = new CounterWidgetUi(connection, time);
		_logger = logger.ForContext<CountTogetherIntegration>();
		Actions = [ChangeCounterAction.Increment(connection), ChangeCounterAction.Decrement(connection)];
		Migrations = [new MacroDeck2Migration(metadata.Id)];
	}

	public IReadOnlyList<IActionDefinition> Actions { get; }

	public bool AllowsMultipleConfigurations => false;

	public IReadOnlyList<VariableDefinition> Variables => Volatile.Read(ref _variables);

	public IReadOnlyList<EventDefinition> EventDefinitions => CounterEvents.Definitions;

	public IReadOnlyList<IIntegrationMigration> Migrations { get; }

	public IReadOnlyList<UiSurfaceDeclaration> Surfaces => CounterWidgetUi.Surfaces;

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

		Volatile.Write(ref _calendarAccounts, token is null
			? []
			: [new CalendarAccount { Id = CountdownCalendar.AccountId, DisplayName = ApiTokenConfigFlow.EntryTitle }]);
		_catalog.CatalogChanged(CapabilityKinds.Calendar, reason: "CountTogether configuration read");

		if (token is null)
		{
			await _connection.StopAsync().ConfigureAwait(false);
			return;
		}

		await _connection.StartAsync(token).ConfigureAwait(false);
	}

	/// <summary>
	/// Detaches from the host but keeps the CountTogether connection: Macro Deck calls this around every
	/// configuration change and reconnect, and the following <see cref="InitializeAsync" /> keeps a connection
	/// with the same token instead of leaving a gap. The process owns the connection and disposes it on exit.
	/// </summary>
	public Task ShutdownAsync()
	{
		Detach();
		return Task.CompletedTask;
	}

	public IConfigFlow CreateConfigFlow() => new ApiTokenConfigFlow(_connection);

	public ValueTask<VariableReading> ReadAsync(string localId, CancellationToken cancellationToken = default)
	{
		if (localId == CounterVariables.ConnectedId)
		{
			return ValueTask.FromResult(VariableReading.Of(_connection.State == ConnectionState.Connected));
		}

		var counter = _connection.State == ConnectionState.Connected &&
			CounterVariables.TryParseId(localId, out var counterId, out var isDate)
				? _connection.Find(counterId) is { } found ? (Counter: found, IsDate: isDate) : default
				: default;

		return ValueTask.FromResult(counter switch
		{
			({ Date: { } date }, true) => VariableReading.Of(CounterVariables.FormatDate(date)),
			({ } snapshot, false) when (_writer.HeldValue(snapshot.Id, snapshot.Value) ?? snapshot.Value) is { } value
				=> VariableReading.Of(value),
			_ => VariableReading.Unavailable,
		});
	}

	public async ValueTask<VariableWriteResult> SetValueAsync(string localId, object? value,
		CancellationToken cancellationToken = default)
	{
		_logger.Debug("Write to {Variable} requested with {Value} ({Type}).", localId, value, value?.GetType().Name);

		if (!CounterVariables.TryParseId(localId, out var counterId, out var isDate))
		{
			_logger.Information("Refused a write to {Variable}: not a CountTogether variable.", localId);
			return VariableWriteResult.NotFound();
		}

		CounterChangeResult result;
		if (isDate)
		{
			if (!CounterVariables.TryParseDate(value, out var date))
			{
				return VariableWriteResult.InvalidValue(Strings.Errors.InvalidDate());
			}

			result = await _connection.SetDateAsync(counterId, date, cancellationToken).ConfigureAwait(false);
		}
		else
		{
			if (!CounterVariables.TryParseValue(value, out var number))
			{
				_logger.Information("Refused a write to {Variable}: {Value} is not a whole number.", localId, value);
				return VariableWriteResult.InvalidValue(Strings.Errors.InvalidNumber());
			}

			// Held locally until it settles, so a slider sends one change instead of one per step.
			result = _connection.CheckValueWrite(counterId) is { } failure
				? CounterChangeResult.Failed(failure)
				: CounterChangeResult.Success(number);
			if (result.Succeeded)
			{
				_writer.Request(counterId, number);
			}
		}

		if (result.Failure is { } refused)
		{
			_logger.Information("Refused a write to {Variable}: {Failure}.", localId, refused);
		}

		return result.Failure switch
		{
			null => VariableWriteResult.Applied(),
			CountTogetherFailure.NotFound => VariableWriteResult.NotFound(CounterActionResults.Message(CountTogetherFailure.NotFound)),
			CountTogetherFailure.NotConfigured or CountTogetherFailure.NotConnected or CountTogetherFailure.Unreachable
				or CountTogetherFailure.RateLimited or CountTogetherFailure.Timeout
				=> VariableWriteResult.Unavailable(CounterActionResults.Message(result.Failure.Value)),
			CountTogetherFailure.InvalidValue or CountTogetherFailure.Rejected or CountTogetherFailure.NotUpDown
				or CountTogetherFailure.NotDateCounter
				=> VariableWriteResult.InvalidValue(CounterActionResults.Message(result.Failure.Value)),
			{ } failure => VariableWriteResult.Failed(CounterActionResults.Message(failure)),
		};
	}

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

	public IReadOnlyList<CalendarAccount> GetAccounts() => Volatile.Read(ref _calendarAccounts);

	public async Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(string accountId, CancellationToken cancellationToken)
		=> CountdownCalendar.Calendars(await CountersOfAsync(accountId, cancellationToken).ConfigureAwait(false));

	public async Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(string accountId, CalendarEventQuery query,
		CancellationToken cancellationToken)
		=> CountdownCalendar.Events(await CountersOfAsync(accountId, cancellationToken).ConfigureAwait(false), query);

	public async Task<CalendarEvent?> GetEventAsync(string accountId, string calendarId, string eventId,
		CancellationToken cancellationToken)
		=> CountdownCalendar.Event(await CountersOfAsync(accountId, cancellationToken).ConfigureAwait(false), calendarId,
			eventId);

	public async Task InitializeAsync(IWidgetTypeProviderContext context, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);

		foreach (var descriptor in CounterWidgetTypes.Descriptors)
		{
			await context.RegisterWidgetTypeAsync(descriptor, cancellationToken).ConfigureAwait(false);
		}
	}

	public IReadOnlyList<WidgetTypeDescriptor> GetWidgetTypes() => CounterWidgetTypes.Descriptors;

	public Task<IUiSession?> CreateSessionAsync(UiSessionRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		return Task.FromResult(_widgets.CreateSession(request.Surface));
	}

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

	// The host reads calendars right after the session starts, often before CountTogether answered, so a read
	// waits briefly for that. It keeps an account's last events when a read fails, so a read that still finds
	// no connection throws rather than reporting an empty calendar.
	private async Task<IReadOnlyList<CounterSnapshot>> CountersOfAsync(string accountId, CancellationToken cancellationToken)
	{
		if (accountId != CountdownCalendar.AccountId || GetAccounts().Count == 0)
		{
			throw new InvalidOperationException($"Unknown calendar account '{accountId}'.");
		}

		await _connection.WhenSettledAsync(CalendarConnectWait, cancellationToken).ConfigureAwait(false);
		return _connection.State == ConnectionState.Connected
			? _connection.Counters
			: throw new InvalidOperationException("Not connected to CountTogether.");
	}

	private void OnCatalogChanged()
	{
		Volatile.Write(ref _variables, CounterVariables.Build(_connection.Counters));
		_catalog.CatalogChanged(CapabilityKinds.Variables, reason: "CountTogether counters changed");
		_catalog.CatalogChanged(CapabilityKinds.Calendar, reason: "CountTogether counters changed");
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
