using System.Collections.Immutable;
using CountTogether.PublicApiSdk.Abstractions.Client;
using CountTogether.PublicApiSdk.Models;
using MacroDeck.Sdk.Logging;
using Serilog;

namespace CountTogether.MacroDeckPlugin.Api;

public enum ConnectionState
{
	NotConfigured,
	Connecting,
	Connected,
	Unauthorized,
	Unreachable
}

/// <summary>
/// The one live connection to CountTogether, shared by actions, variables, events and widgets. It keeps an
/// in-memory snapshot of every counter so readers never wait for the network.
/// </summary>
public sealed class CountTogetherConnection : IAsyncDisposable
{
	// Date counters change value at midnight without a push, and the SDK refetches silently after it
	// reconnects, so the snapshot is compared against a full list regularly. Slightly longer than the SDK's
	// one minute cache lifetime, so a refresh actually reaches the API.
	internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(65);
	internal static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(30);
	internal const string HttpClientName = "CountTogether";

	/// <summary>The largest step the API accepts in one increment or decrement.</summary>
	public const int MaxAmount = 1_000_000;
	internal static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(5);
	internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(1);

	private readonly ICountTogetherClientFactory _factory;
	private readonly IHttpClientFactory _httpClients;
	private readonly ApiRateLimiter _rateLimiter;
	private readonly TimeProvider _time;
	private readonly ILogger _logger;
	private readonly FailureEpisodeTracker _failures;
	private readonly SemaphoreSlim _lifecycle = new(1, 1);
	private readonly Lock _sync = new();

	private ImmutableDictionary<Guid, CounterSnapshot> _counters = ImmutableDictionary<Guid, CounterSnapshot>.Empty;
	private IReadOnlyList<CounterSnapshot> _ordered = [];
	private ImmutableDictionary<Guid, long> _pushedAt = ImmutableDictionary<Guid, long>.Empty;
	private ImmutableDictionary<Guid, (long Value, long Sequence)> _changedTo =
		ImmutableDictionary<Guid, (long Value, long Sequence)>.Empty;
	private long _pushSequence;
	private ConnectionState _state = ConnectionState.NotConfigured;
	private Session? _session;
	private string? _token;
	private CancellationTokenSource? _lifetime;
	private Task _connectLoop = Task.CompletedTask;

	public CountTogetherConnection(ICountTogetherClientFactory factory, IHttpClientFactory httpClients,
		ApiRateLimiter rateLimiter, TimeProvider time, ILogger logger)
	{
		_factory = factory;
		_httpClients = httpClients;
		_rateLimiter = rateLimiter;
		_time = time;
		_logger = logger.ForContext<CountTogetherConnection>();
		_failures = new FailureEpisodeTracker(time: time);
	}

	/// <summary>Raised after the state or any counter changed.</summary>
	public event Action? Changed;

	/// <summary>Raised when counters were added or removed, or a name, kind or date changed.</summary>
	public event Action? CatalogChanged;

	/// <summary>Raised when the value of a known counter changed.</summary>
	public event Action<CounterSnapshot, CounterSnapshot>? ValueChanged;

	/// <summary>Raised when a counter was deleted or the user lost access to it.</summary>
	public event Action<CounterSnapshot>? Removed;

	public ConnectionState State
	{
		get
		{
			lock (_sync)
			{
				return _state;
			}
		}
	}

	/// <summary>All counters, oldest first.</summary>
	public IReadOnlyList<CounterSnapshot> Counters => Volatile.Read(ref _ordered);

	public CounterSnapshot? Find(Guid counterId) => Volatile.Read(ref _counters).GetValueOrDefault(counterId);

	/// <summary>Waits until a connection attempt that is still running has ended, at most <paramref name="timeout" />.</summary>
	public async Task WhenSettledAsync(TimeSpan timeout, CancellationToken cancellationToken)
	{
		var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnChanged()
		{
			if (State != ConnectionState.Connecting)
			{
				settled.TrySetResult();
			}
		}

		Changed += OnChanged;
		try
		{
			OnChanged();
			await Task.WhenAny(settled.Task, Task.Delay(timeout, _time, cancellationToken)).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
		}
		finally
		{
			Changed -= OnChanged;
		}
	}

	/// <summary>
	/// Connects in the background. Calling it again with the same token keeps the running connection, unless
	/// that token was rejected: the user may have confirmed it again after the account fixed it.
	/// </summary>
	public async Task StartAsync(string token, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(token);

		await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_lifetime is not null && string.Equals(_token, token, StringComparison.Ordinal) &&
				State != ConnectionState.Unauthorized)
			{
				return;
			}

			await StopCoreAsync().ConfigureAwait(false);
			Start(token);
		}
		finally
		{
			_lifecycle.Release();
		}
	}

	/// <summary>Drops the connection and every counter.</summary>
	public async Task StopAsync()
	{
		await _lifecycle.WaitAsync().ConfigureAwait(false);
		try
		{
			await StopCoreAsync().ConfigureAwait(false);
			ClearCounters();
			SetState(ConnectionState.NotConfigured);
		}
		finally
		{
			_lifecycle.Release();
		}
	}

	/// <summary>Checks a token with a throwaway client, without touching the running connection.</summary>
	public async Task<TokenCheckResult> ValidateTokenAsync(string token, CancellationToken cancellationToken)
	{
		var client = _factory.Create();
		try
		{
			// The SDK takes no cancellation token, so the wait is bounded here.
			await ThrottledAsync(() => StartClientAsync(client, token), cancellationToken)
				.WaitAsync(ValidationTimeout, _time, cancellationToken)
				.ConfigureAwait(false);
			return TokenCheckResult.Valid;
		}
		catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
		{
			var failure = CountTogetherFailures.Classify(exception);
			_logger.Information("Token validation failed with {Failure}: {Error}", failure, exception.Message);
			return failure is CountTogetherFailure.Unauthorized or CountTogetherFailure.PermissionDenied
				? TokenCheckResult.Rejected
				: TokenCheckResult.Unreachable;
		}
		finally
		{
			await DisposeClientAsync(client).ConfigureAwait(false);
		}
	}

	public Task<CounterChangeResult> IncrementAsync(Guid counterId, int? amount, CancellationToken cancellationToken)
		=> ChangeAsync(counterId, increment: true, amount, cancellationToken);

	public Task<CounterChangeResult> DecrementAsync(Guid counterId, int? amount, CancellationToken cancellationToken)
		=> ChangeAsync(counterId, increment: false, amount, cancellationToken);

	/// <summary>
	/// Sets a regular counter to <paramref name="value" /> by incrementing or decrementing by the difference.
	/// Setting the value directly would reset the counter's step size and needs admin rights on it.
	/// </summary>
	public Task<CounterChangeResult> SetValueAsync(Guid counterId, long value, CancellationToken cancellationToken)
	{
		if (CheckValueWrite(counterId) is { } notPossible)
		{
			return Task.FromResult(CounterChangeResult.Failed(notPossible));
		}

		var counter = Find(counterId);

		// Right after a change the push may not have reached the snapshot yet; the API's answer is newer then.
		var current = Volatile.Read(ref _changedTo).TryGetValue(counterId, out var changed) &&
			changed.Sequence >= Volatile.Read(ref _pushedAt).GetValueOrDefault(counterId)
				? changed.Value
				: counter!.Value!.Value;
		var difference = value - current;
		if (difference == 0)
		{
			return Task.FromResult(CounterChangeResult.Success(value));
		}

		return Math.Abs(difference) > MaxAmount
			? Task.FromResult(CounterChangeResult.Failed(CountTogetherFailure.InvalidValue))
			: ChangeAsync(counterId, difference > 0, (int)Math.Abs(difference), cancellationToken);
	}

	/// <summary>Why the value of this counter cannot be set right now, or <c>null</c> when it can.</summary>
	public CountTogetherFailure? CheckValueWrite(Guid counterId)
	{
		var (session, state, counter) = Current(counterId);
		return Unavailable(state, session) ?? counter switch
		{
			null => CountTogetherFailure.NotFound,
			{ Kind: not CounterKind.UpDown } => CountTogetherFailure.NotUpDown,
			{ Value: null } => CountTogetherFailure.NotConnected,
			_ => null,
		};
	}

	/// <summary>Moves the start date of a from-date counter or the target date of a to-date counter.</summary>
	public async Task<CounterChangeResult> SetDateAsync(Guid counterId, DateOnly date, CancellationToken cancellationToken)
	{
		var (session, state, counter) = Current(counterId);

		if (Unavailable(state, session) is { } notReady)
		{
			return CounterChangeResult.Failed(notReady);
		}

		var request = counter?.Kind switch
		{
			null => null,
			CounterKind.FromDate => new UpdateCounterRequest { StartDate = date },
			CounterKind.ToDate => new UpdateCounterRequest { EndDate = date },
			_ => new UpdateCounterRequest(),
		};

		if (request is null)
		{
			return CounterChangeResult.Failed(CountTogetherFailure.NotFound);
		}

		if (request.StartDate is null && request.EndDate is null)
		{
			return CounterChangeResult.Failed(CountTogetherFailure.NotDateCounter);
		}

		try
		{
			var client = session!.Client;
			var updated = await ThrottledAsync(() => client.UpdateCounterAsync(counterId, request), cancellationToken)
				.ConfigureAwait(false);
			Upsert(session, updated);
			SetState(session, ConnectionState.Connected);
			return CounterChangeResult.Success(updated.Value ?? 0);
		}
		catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
		{
			return Failed(session!, counterId, exception);
		}
	}

	public async ValueTask DisposeAsync()
	{
		await _lifecycle.WaitAsync().ConfigureAwait(false);
		try
		{
			await StopCoreAsync().ConfigureAwait(false);
		}
		finally
		{
			_lifecycle.Release();
		}

		_lifecycle.Dispose();
	}

	private void Start(string token)
	{
		_token = token;
		_lifetime = new CancellationTokenSource();
		SetState(ConnectionState.Connecting);

		var lifetime = _lifetime.Token;
		_connectLoop = Task.Run(() => ConnectLoopAsync(token, lifetime), CancellationToken.None);
	}

	private async Task StopCoreAsync()
	{
		if (_lifetime is { } lifetime)
		{
			await lifetime.CancelAsync().ConfigureAwait(false);
			await _connectLoop.ConfigureAwait(false);
			lifetime.Dispose();
		}

		_lifetime = null;
		_token = null;
		_connectLoop = Task.CompletedTask;

		Session? session;
		lock (_sync)
		{
			session = _session;
			_session = null;
		}

		if (session is not null)
		{
			await session.DisposeAsync().ConfigureAwait(false);
		}
	}

	private async Task ConnectLoopAsync(string token, CancellationToken cancellationToken)
	{
		var delay = FirstRetryDelay;

		while (!cancellationToken.IsCancellationRequested)
		{
			var client = _factory.Create();
			try
			{
				await ThrottledAsync(() => StartClientAsync(client, token), cancellationToken).ConfigureAwait(false);
				// Pushes that arrive before the session subscribes are not raised as events, but the SDK has
				// already merged them into the cache this list is read from, so the snapshot is current.
				var counters = await ThrottledAsync(client.GetCountersAsync, cancellationToken).ConfigureAwait(false);

				var session = new Session(this, client);
				lock (_sync)
				{
					_session = session;
				}

				ReplaceCounters(session, counters);
				session.StartRefreshing();
				SetState(ConnectionState.Connected);

				if (_failures.RecordSuccess() is { } recovered)
				{
					_logger.Information("Connected to CountTogether after {Failures} failed attempts.", recovered.Failures);
				}
				else
				{
					_logger.Information("Connected to CountTogether with {Count} counters.", Counters.Count);
				}

				return;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				await DisposeClientAsync(client).ConfigureAwait(false);
				return;
			}
			catch (Exception exception)
			{
				await DisposeClientAsync(client).ConfigureAwait(false);

				var failure = CountTogetherFailures.Classify(exception);
				if (failure == CountTogetherFailure.Unauthorized)
				{
					_logger.Warning("CountTogether rejected the API token. The integration needs a new token.");
					SetState(ConnectionState.Unauthorized);
					return;
				}

				LogFailure("Connecting to CountTogether failed", exception);
			}

			// The delay is scheduled before the state is published, so whoever reacts to the state already
			// finds the retry pending.
			var retry = Task.Delay(delay, _time, cancellationToken);
			SetState(ConnectionState.Unreachable);

			try
			{
				await retry.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
		}
	}

	private async Task<CounterChangeResult> ChangeAsync(Guid counterId, bool increment, int? amount,
		CancellationToken cancellationToken)
	{
		var (session, state, counter) = Current(counterId);

		if (Unavailable(state, session) is { } notReady)
		{
			return CounterChangeResult.Failed(notReady);
		}

		if (counter is null)
		{
			return CounterChangeResult.Failed(CountTogetherFailure.NotFound);
		}

		if (counter.Kind != CounterKind.UpDown)
		{
			return CounterChangeResult.Failed(CountTogetherFailure.NotUpDown);
		}

		var client = session!.Client;
		try
		{
			Task<long> Request() => (increment, amount) switch
			{
				(true, { } value) => client.IncrementCounterAsync(counterId, value),
				(true, null) => client.IncrementCounterAsync(counterId),
				(false, { } value) => client.DecrementCounterAsync(counterId, value),
				(false, null) => client.DecrementCounterAsync(counterId),
			};

			// The snapshot is left to the push the API sends every client, including this one: applying the
			// response here could overwrite a newer pushed value when presses overlap.
			var newValue = await ThrottledAsync(Request, cancellationToken).ConfigureAwait(false);
			lock (_sync)
			{
				Volatile.Write(ref _changedTo, _changedTo.SetItem(counterId, (newValue, Interlocked.Read(ref _pushSequence))));
			}

			SetState(session, ConnectionState.Connected);
			return CounterChangeResult.Success(newValue);
		}
		catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
		{
			return Failed(session, counterId, exception);
		}
	}

	private (Session? Session, ConnectionState State, CounterSnapshot? Counter) Current(Guid counterId)
	{
		lock (_sync)
		{
			return (_session, _state, _counters.GetValueOrDefault(counterId));
		}
	}

	private static CountTogetherFailure? Unavailable(ConnectionState state, Session? session)
		=> state switch
		{
			ConnectionState.NotConfigured => CountTogetherFailure.NotConfigured,
			ConnectionState.Unauthorized => CountTogetherFailure.Unauthorized,
			ConnectionState.Connected or ConnectionState.Unreachable when session is not null => null,
			_ => CountTogetherFailure.NotConnected,
		};

	private CounterChangeResult Failed(Session session, Guid counterId, Exception exception)
	{
		var failure = CountTogetherFailures.Classify(exception);
		_logger.Warning("Changing counter {CounterId} failed with {Failure}: {Error}", counterId, failure, exception.Message);

		if (failure == CountTogetherFailure.Unauthorized)
		{
			SetState(session, ConnectionState.Unauthorized);
		}
		else if (failure == CountTogetherFailure.NotFound)
		{
			Remove(session, counterId);
		}

		return CounterChangeResult.Failed(failure);
	}

	private async Task RefreshAsync(Session session)
	{
		try
		{
			// A push received while the list was in flight is newer than the list, so it wins.
			var listedAfter = Interlocked.Read(ref _pushSequence);
			var counters = await ThrottledAsync(session.Client.GetCountersAsync, CancellationToken.None).ConfigureAwait(false);
			foreach (var counter in counters)
			{
				if (Volatile.Read(ref _pushedAt).GetValueOrDefault(counter.Id) <= listedAfter)
				{
					Upsert(session, counter);
				}
			}

			var listed = counters.Select(counter => counter.Id).ToHashSet();
			foreach (var missing in Volatile.Read(ref _counters).Keys.Where(id => !listed.Contains(id)).ToList())
			{
				await VerifyAsync(session, missing).ConfigureAwait(false);
			}

			SetState(session, ConnectionState.Connected);
			if (_failures.RecordSuccess() is { } recovered)
			{
				_logger.Information("Refreshing counters works again after {Failures} failures.", recovered.Failures);
			}
		}
		catch (Exception exception)
		{
			if (CountTogetherFailures.Classify(exception) == CountTogetherFailure.Unauthorized)
			{
				_logger.Warning("CountTogether no longer accepts the API token. The integration needs a new token.");
				SetState(session, ConnectionState.Unauthorized);
				return;
			}

			// The SDK keeps reconnecting on its own; until a refresh works again the values may be stale.
			LogFailure("Refreshing counters failed", exception);
			SetState(session, ConnectionState.Unreachable);
		}
	}

	// The SDK drops a counter from its cache when its member list changes, so a counter missing from a list
	// is only gone once the API itself says so.
	private async Task VerifyAsync(Session session, Guid counterId)
	{
		try
		{
			var counter = await ThrottledAsync(() => session.Client.GetCounterAsync(counterId), CancellationToken.None)
				.ConfigureAwait(false);
			Upsert(session, counter);
		}
		catch (CountTogetherApiException exception)
			when (CountTogetherFailures.Classify(exception) is CountTogetherFailure.NotFound or CountTogetherFailure.PermissionDenied)
		{
			Remove(session, counterId);
		}
		catch (Exception exception)
		{
			_logger.Debug("Checking counter {CounterId} failed: {Error}", counterId, exception.Message);
		}
	}

	private void ReplaceCounters(Session session, IEnumerable<Counter> counters)
	{
		lock (_sync)
		{
			if (!ReferenceEquals(_session, session))
			{
				return;
			}

			var builder = ImmutableDictionary.CreateBuilder<Guid, CounterSnapshot>();
			foreach (var counter in counters)
			{
				if (CounterSnapshot.From(counter) is { } snapshot)
				{
					builder[snapshot.Id] = snapshot;
				}
			}

			Publish(builder.ToImmutable());
		}

		Raise(CatalogChanged);
		Raise(Changed);
	}

	private void Upsert(Session session, Counter counter)
	{
		CounterSnapshot? next;
		lock (_sync)
		{
			next = CounterSnapshot.From(counter, _counters.GetValueOrDefault(counter.Id));
		}

		if (next is not null)
		{
			Upsert(session, next);
		}
	}

	private void Upsert(Session session, CounterSnapshot next)
	{
		CounterSnapshot? previous;
		lock (_sync)
		{
			if (!ReferenceEquals(_session, session))
			{
				return;
			}

			previous = _counters.GetValueOrDefault(next.Id);
			if (next == previous)
			{
				return;
			}

			Publish(_counters.SetItem(next.Id, next));
		}

		if (previous is not null && previous.Value != next.Value)
		{
			Raise(ValueChanged, previous, next);
		}

		if (previous is null || previous.Name != next.Name || previous.Kind != next.Kind || previous.Date != next.Date)
		{
			Raise(CatalogChanged);
		}

		Raise(Changed);
	}

	private void Remove(Session session, Guid counterId)
	{
		CounterSnapshot? removed;
		lock (_sync)
		{
			if (!ReferenceEquals(_session, session) || !_counters.TryGetValue(counterId, out removed))
			{
				return;
			}

			Publish(_counters.Remove(counterId));
		}

		Raise(Removed, removed);
		Raise(CatalogChanged);
		Raise(Changed);
	}

	private void ClearCounters()
	{
		lock (_sync)
		{
			if (_counters.IsEmpty)
			{
				return;
			}

			Publish(ImmutableDictionary<Guid, CounterSnapshot>.Empty);
		}

		Raise(CatalogChanged);
		Raise(Changed);
	}

	private void MarkPushed(Guid counterId)
	{
		var sequence = Interlocked.Increment(ref _pushSequence);
		lock (_sync)
		{
			Volatile.Write(ref _pushedAt, _pushedAt.SetItem(counterId, sequence));
		}
	}

	// Called under _sync.
	private void Publish(ImmutableDictionary<Guid, CounterSnapshot> counters)
	{
		Volatile.Write(ref _counters, counters);
		Volatile.Write(ref _ordered, counters.Values
			.OrderBy(counter => counter.Created ?? DateTime.MaxValue)
			.ThenBy(counter => counter.Name, StringComparer.CurrentCultureIgnoreCase)
			.ThenBy(counter => counter.Id)
			.ToList());
	}

	/// <summary>Changes the state only while <paramref name="session" /> is still the current one, so a late
	/// answer from a replaced client cannot mark the new connection.</summary>
	private void SetState(Session session, ConnectionState state)
	{
		lock (_sync)
		{
			if (!ReferenceEquals(_session, session) || _state == state || _state == ConnectionState.Unauthorized)
			{
				return;
			}

			_state = state;
		}

		Raise(Changed);
	}

	private void SetState(ConnectionState state)
	{
		lock (_sync)
		{
			if (_state == state)
			{
				return;
			}

			_state = state;
		}

		Raise(Changed);
	}

	private void LogFailure(string message, Exception exception)
	{
		var signal = _failures.RecordFailure(exception.Message);
		switch (signal.Kind)
		{
			case FailureEpisodeSignalKind.Onset:
				_logger.Warning("{Message}: {Error}", message, exception.Message);
				break;
			case FailureEpisodeSignalKind.SummaryDue:
				_logger.Warning("{Message} {Failures} times in a row since {StartedAt}: {Error}",
					message, signal.ConsecutiveFailures, signal.StartedAt, signal.LastError);
				break;
		}
	}

	// Subscribers run on SDK and timer threads; one that throws must not stop the receive loop.
	private void Raise(Action? handler)
	{
		try
		{
			handler?.Invoke();
		}
		catch (Exception exception)
		{
			_logger.Error(exception, "A connection subscriber failed.");
		}
	}

	private void Raise<T>(Action<T>? handler, T argument)
	{
		try
		{
			handler?.Invoke(argument);
		}
		catch (Exception exception)
		{
			_logger.Error(exception, "A connection subscriber failed.");
		}
	}

	private void Raise<T1, T2>(Action<T1, T2>? handler, T1 first, T2 second)
	{
		try
		{
			handler?.Invoke(first, second);
		}
		catch (Exception exception)
		{
			_logger.Error(exception, "A connection subscriber failed.");
		}
	}

	private async Task<T> ThrottledAsync<T>(Func<Task<T>> request, CancellationToken cancellationToken)
	{
		if (!await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false))
		{
			throw new LocalRateLimitException();
		}

		return await request().WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task ThrottledAsync(Func<Task> request, CancellationToken cancellationToken)
		=> await ThrottledAsync(async () =>
		{
			await request().ConfigureAwait(false);
			return true;
		}, cancellationToken).ConfigureAwait(false);

	private Task StartClientAsync(ICountTogetherClient client, string token)
		=> client.StartAsync(configuration => configuration.ApiToken = token, _httpClients.CreateClient(HttpClientName));

	// The SDK's Dispose blocks on closing the WebSocket, so it runs off the caller's thread.
	private async Task DisposeClientAsync(ICountTogetherClient client)
	{
		try
		{
			await Task.Run(client.Dispose).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			_logger.Debug("Disposing the CountTogether client failed: {Error}", exception.Message);
		}
	}

	private sealed class Session : IAsyncDisposable
	{
		private readonly CountTogetherConnection _owner;
		private readonly Action<Counter> _onUpdated;
		private readonly Action<Guid> _onDeleted;
		private readonly Action<Guid> _onMemberlistChanged;
		private ITimer? _timer;
		private int _refreshing;

		public Session(CountTogetherConnection owner, ICountTogetherClient client)
		{
			_owner = owner;
			Client = client;
			_onUpdated = counter =>
			{
				owner.MarkPushed(counter.Id);
				owner.Upsert(this, counter);
			};
			_onDeleted = counterId =>
			{
				owner.MarkPushed(counterId);
				owner.Remove(this, counterId);
			};
			_onMemberlistChanged = counterId => _ = owner.VerifyAsync(this, counterId);

			client.CounterUpdated += _onUpdated;
			client.CounterDeleted += _onDeleted;
			client.CounterMemberlistChanged += _onMemberlistChanged;
		}

		public ICountTogetherClient Client { get; }

		public void StartRefreshing()
			=> _timer = _owner._time.CreateTimer(_ => Refresh(), null, RefreshInterval, RefreshInterval);

		public async ValueTask DisposeAsync()
		{
			if (_timer is not null)
			{
				await _timer.DisposeAsync().ConfigureAwait(false);
			}

			Client.CounterUpdated -= _onUpdated;
			Client.CounterDeleted -= _onDeleted;
			Client.CounterMemberlistChanged -= _onMemberlistChanged;
			await _owner.DisposeClientAsync(Client).ConfigureAwait(false);
		}

		private void Refresh()
		{
			if (Interlocked.Exchange(ref _refreshing, 1) == 1)
			{
				return;
			}

			_ = RunAsync();

			async Task RunAsync()
			{
				try
				{
					await _owner.RefreshAsync(this).ConfigureAwait(false);
				}
				finally
				{
					Volatile.Write(ref _refreshing, 0);
				}
			}
		}
	}
}
