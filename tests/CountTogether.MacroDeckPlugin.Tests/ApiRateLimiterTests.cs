using CountTogether.MacroDeckPlugin.Api;
using MacroDeck.Plugin.Testing;
using NUnit.Framework;

namespace CountTogether.MacroDeckPlugin.Tests;

[TestFixture]
public sealed class ApiRateLimiterTests
{
	private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));

	[Test]
	public async Task Requests_beyond_the_limit_wait_for_the_window_to_move_on()
	{
		var limiter = new ApiRateLimiter(_clock, permits: 3);
		for (var i = 0; i < 3; i++)
		{
			Assert.That(await limiter.WaitAsync(CancellationToken.None), Is.True);
		}

		var fourth = limiter.WaitAsync(CancellationToken.None);
		Assert.That(fourth.IsCompleted, Is.False);

		_clock.Advance(TimeSpan.FromSeconds(1));

		Assert.That(await fourth, Is.True);
	}

	[Test]
	public async Task A_full_queue_refuses_instead_of_piling_up()
	{
		var limiter = new ApiRateLimiter(_clock, permits: 1, queueLimit: 2);
		await limiter.WaitAsync(CancellationToken.None);
		var queued = new[] { limiter.WaitAsync(CancellationToken.None), limiter.WaitAsync(CancellationToken.None) };

		Assert.That(await limiter.WaitAsync(CancellationToken.None), Is.False);
		Assert.That(queued.All(task => !task.IsCompleted), Is.True);
	}

	[Test]
	public async Task The_default_stays_below_the_api_limit_of_four_per_second()
	{
		var limiter = new ApiRateLimiter(_clock);
		var granted = 0;

		// The clock never moves here, so every request after the last free slot stays pending.
		while (limiter.WaitAsync(CancellationToken.None) is { IsCompleted: true } request && await request)
		{
			granted++;
		}

		Assert.That(granted, Is.LessThan(4));
	}
}
