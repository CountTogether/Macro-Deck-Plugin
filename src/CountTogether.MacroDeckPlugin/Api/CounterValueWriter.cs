using Serilog;

namespace CountTogether.MacroDeckPlugin.Api;

/// <summary>
/// Turns a stream of written values (a slider being dragged) into one change per counter: the newest value is
/// held locally and shown as the counter's value, and only once it has stayed the same for
/// <see cref="SettleDelay" /> is it sent to CountTogether.
/// </summary>
public sealed class CounterValueWriter : IDisposable
{
	internal static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(500);

	// After a successful send the held value stays visible until the counter reports it, or this long at most,
	// so the variable does not briefly fall back to the old value while the push is on its way.
	internal static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(5);

	private readonly CountTogetherConnection _connection;
	private readonly TimeProvider _time;
	private readonly ILogger _logger;
	private readonly Dictionary<Guid, Pending> _pending = [];
	private readonly Lock _sync = new();

	public CounterValueWriter(CountTogetherConnection connection, TimeProvider time, ILogger logger)
	{
		_connection = connection;
		_time = time;
		_logger = logger.ForContext<CounterValueWriter>();
	}

	/// <summary>Holds <paramref name="value" /> and (re)starts the wait before it is sent.</summary>
	public void Request(Guid counterId, long value)
	{
		lock (_sync)
		{
			if (!_pending.TryGetValue(counterId, out var pending))
			{
				pending = new Pending(_time.CreateTimer(_ => Settled(counterId), null, Timeout.InfiniteTimeSpan,
					Timeout.InfiniteTimeSpan));
				_pending[counterId] = pending;
			}

			pending.Target = value;
			pending.SentAt = null;
			pending.Timer.Change(SettleDelay, Timeout.InfiniteTimeSpan);
		}
	}

	/// <summary>The value held for this counter, or <c>null</c> once CountTogether has caught up with it.</summary>
	public long? HeldValue(Guid counterId, long? reported)
	{
		lock (_sync)
		{
			if (!_pending.TryGetValue(counterId, out var pending))
			{
				return null;
			}

			if (pending.SentAt is { } sentAt &&
				(reported == pending.Target || _time.GetUtcNow() - sentAt >= ConfirmationTimeout))
			{
				Drop(counterId, pending);
				return null;
			}

			return pending.Target;
		}
	}

	public void Dispose()
	{
		lock (_sync)
		{
			foreach (var pending in _pending.Values)
			{
				pending.Timer.Dispose();
			}

			_pending.Clear();
		}
	}

	private void Settled(Guid counterId) => _ = SendAsync(counterId);

	private async Task SendAsync(Guid counterId)
	{
		Pending? pending;
		long target;
		lock (_sync)
		{
			if (!_pending.TryGetValue(counterId, out pending))
			{
				return;
			}

			target = pending.Target;
		}

		// One send per counter at a time, so each difference is taken from the result of the previous one.
		await pending.Sending.WaitAsync().ConfigureAwait(false);
		try
		{
			lock (_sync)
			{
				// A newer value arrived while waiting: its own timer sends it.
				if (!ReferenceEquals(_pending.GetValueOrDefault(counterId), pending) || pending.Target != target)
				{
					return;
				}
			}

			var result = await _connection.SetValueAsync(counterId, target, CancellationToken.None).ConfigureAwait(false);

			lock (_sync)
			{
				if (!ReferenceEquals(_pending.GetValueOrDefault(counterId), pending) || pending.Target != target)
				{
					return;
				}

				if (result.Succeeded)
				{
					pending.SentAt = _time.GetUtcNow();
				}
				else
				{
					// The held value is dropped, so the variable shows the counter's real value again.
					Drop(counterId, pending);
				}
			}

			if (result.Failure is { } failure)
			{
				_logger.Warning("Setting counter {CounterId} to {Value} failed with {Failure}.", counterId, target, failure);
			}
			else
			{
				_logger.Information("Set counter {CounterId} to {Value}.", counterId, target);
			}
		}
		catch (Exception exception)
		{
			_logger.Error(exception, "Setting counter {CounterId} failed.", counterId);
		}
		finally
		{
			pending.Sending.Release();
		}
	}

	// Called under _sync.
	private void Drop(Guid counterId, Pending pending)
	{
		_pending.Remove(counterId);
		pending.Timer.Dispose();
	}

	private sealed class Pending(ITimer timer)
	{
		public ITimer Timer { get; } = timer;

		public SemaphoreSlim Sending { get; } = new(1, 1);

		public long Target { get; set; }

		public DateTimeOffset? SentAt { get; set; }
	}
}
