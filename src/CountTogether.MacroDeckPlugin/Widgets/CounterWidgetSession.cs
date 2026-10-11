using CountTogether.MacroDeckPlugin.Api;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;

namespace CountTogether.MacroDeckPlugin.Widgets;

/// <summary>A live tile: follows the connection and turns button presses into counter changes.</summary>
internal sealed class CounterWidgetSession : ViewSession
{
	internal static readonly TimeSpan FailureDisplay = TimeSpan.FromSeconds(5);

	private readonly CounterWidgetSettings _settings;
	private readonly bool _withButtons;
	private readonly CountTogetherConnection _connection;
	private readonly TimeProvider _time;
	private readonly UiState<CounterWidgetState> _state;
	private readonly ITimer _failureTimer;
	private long _failedUntilTicks;
	private int _disposed;

	private CounterWidgetSession(UiSurface surface, CounterWidgetSettings settings, bool withButtons,
		CountTogetherConnection connection, TimeProvider time, UiState<CounterWidgetState> state,
		CounterWidgetCommands commands)
		: base(new UiView(surface, CounterWidgetView.Build(state, settings, withButtons, commands,
			WidgetLayout.PaddingFor(surface))))
	{
		_settings = settings;
		_withButtons = withButtons;
		_connection = connection;
		_time = time;
		_state = state;
		_failureTimer = time.CreateTimer(_ => Refresh(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
		_connection.Changed += Refresh;
	}

	public static CounterWidgetSession Create(UiSurface surface, CounterWidgetSettings settings, bool withButtons,
		CountTogetherConnection connection, TimeProvider time)
	{
		ArgumentNullException.ThrowIfNull(surface);
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(connection);

		var state = new UiState<CounterWidgetState>(Resolve(settings, withButtons, connection, failed: false));

		// The view is built before the session exists, so the buttons reach it through this holder.
		CounterWidgetSession? session = null;
		var commands = new CounterWidgetCommands(
			cancellationToken => session?.ChangeAsync(increment: true, cancellationToken) ?? Task.CompletedTask,
			cancellationToken => session?.ChangeAsync(increment: false, cancellationToken) ?? Task.CompletedTask);

		session = new CounterWidgetSession(surface, settings, withButtons, connection, time, state, commands);
		return session;
	}

	public override async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 1)
		{
			return;
		}

		_connection.Changed -= Refresh;
		await _failureTimer.DisposeAsync().ConfigureAwait(false);
		await base.DisposeAsync().ConfigureAwait(false);
	}

	internal async Task ChangeAsync(bool increment, CancellationToken cancellationToken)
	{
		if (_settings.CounterId is not { } counterId)
		{
			return;
		}

		CounterChangeResult result;
		try
		{
			result = increment
				? await _connection.IncrementAsync(counterId, amount: null, cancellationToken).ConfigureAwait(false)
				: await _connection.DecrementAsync(counterId, amount: null, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// The session closed while the request ran; there is nobody left to tell.
			return;
		}

		if (!result.Succeeded)
		{
			Interlocked.Exchange(ref _failedUntilTicks, (_time.GetUtcNow() + FailureDisplay).UtcTicks);
			_failureTimer.Change(FailureDisplay, Timeout.InfiniteTimeSpan);
		}

		Refresh();
	}

	private static CounterWidgetState Resolve(CounterWidgetSettings settings, bool withButtons,
		CountTogetherConnection connection, bool failed)
	{
		var counter = settings.CounterId is { } id ? connection.Find(id) : null;
		var state = CounterWidgetState.Resolve(settings.CounterId, connection.State, counter, withButtons);
		return state.Status == CounterWidgetStatus.Ready ? state with { ChangeFailed = failed } : state;
	}

	private void Refresh()
	{
		if (Volatile.Read(ref _disposed) == 1)
		{
			return;
		}

		var failed = _time.GetUtcNow().UtcTicks < Interlocked.Read(ref _failedUntilTicks);
		_state.Value = Resolve(_settings, _withButtons, _connection, failed);
	}
}
