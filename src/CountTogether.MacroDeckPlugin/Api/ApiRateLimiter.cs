namespace CountTogether.MacroDeckPlugin.Api;

/// <summary>
/// Keeps the plugin below the CountTogether Public API's rate limit (a sliding window of four requests per second
/// per user) by spacing requests locally. A request that would have to wait behind too many others is refused
/// instead, so nothing piles up and nothing is sent that the API would reject anyway.
/// </summary>
public sealed class ApiRateLimiter
{
	public const int DefaultPermits = 3;
	public const int DefaultQueueLimit = 10;

	private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

	private readonly TimeProvider _time;
	private readonly int _permits;
	private readonly int _queueLimit;
	private readonly Queue<DateTimeOffset> _granted = new();
	private readonly Lock _sync = new();
	private int _waiting;

	public ApiRateLimiter(TimeProvider time, int permits = DefaultPermits, int queueLimit = DefaultQueueLimit)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(permits, 1);

		_time = time;
		_permits = permits;
		_queueLimit = queueLimit;
	}

	/// <summary>Waits for a free slot. <c>false</c> means the queue was full and the request must not be sent.</summary>
	public async Task<bool> WaitAsync(CancellationToken cancellationToken)
	{
		lock (_sync)
		{
			if (TryGrant())
			{
				return true;
			}

			if (_waiting >= _queueLimit)
			{
				return false;
			}

			_waiting++;
		}

		try
		{
			while (true)
			{
				TimeSpan wait;
				lock (_sync)
				{
					if (TryGrant())
					{
						return true;
					}

					wait = _granted.Peek() + Window - _time.GetUtcNow();
				}

				await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1), _time, cancellationToken)
					.ConfigureAwait(false);
			}
		}
		finally
		{
			lock (_sync)
			{
				_waiting--;
			}
		}
	}

	// Called under _sync.
	private bool TryGrant()
	{
		var now = _time.GetUtcNow();
		while (_granted.Count > 0 && _granted.Peek() + Window <= now)
		{
			_granted.Dequeue();
		}

		if (_granted.Count >= _permits)
		{
			return false;
		}

		_granted.Enqueue(now);
		return true;
	}
}
