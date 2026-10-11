using System.Net;
using CountTogether.MacroDeckPlugin.Api;
using CountTogether.PublicApiSdk.Abstractions.Client;
using CountTogether.PublicApiSdk.Models;

namespace CountTogether.MacroDeckPlugin.Tests.Support;

/// <summary>An in-memory CountTogether account. Each test decides what the API answers.</summary>
internal sealed class FakeCountTogetherClient : ICountTogetherClient
{
	private readonly Lock _sync = new();
	private readonly List<Counter> _counters;

	public FakeCountTogetherClient(params Counter[] counters) => _counters = [.. counters];

	public event Action<Counter>? CounterUpdated;

	public event Action<Guid>? CounterDeleted;

	public event Action<Guid>? CounterMemberlistChanged;

	public Exception? StartFailure { get; set; }

	public Exception? ChangeFailure { get; set; }

	public Exception? ListFailure { get; set; }

	/// <summary>Runs after the list was taken, before it is returned, as a push racing a request would.</summary>
	public Action? AfterListTaken { get; set; }

	public long? ChangeResult { get; set; }

	public string? StartedWithToken { get; private set; }

	public bool Disposed { get; private set; }

	public List<(Guid CounterId, bool Increment, int? Amount)> Changes { get; } = [];

	public Task StartAsync(Action<CountTogetherClientConfiguration> configureClient, HttpClient? httpClient = null)
	{
		var configuration = new CountTogetherClientConfiguration();
		configureClient(configuration);
		StartedWithToken = configuration.ApiToken;
		return StartFailure is null ? Task.CompletedTask : Task.FromException(StartFailure);
	}

	public Task<List<Counter>> GetCountersAsync()
	{
		if (ListFailure is { } failure)
		{
			return Task.FromException<List<Counter>>(failure);
		}

		List<Counter> list;
		lock (_sync)
		{
			list = _counters.Select(Copy).ToList();
		}

		AfterListTaken?.Invoke();
		return Task.FromResult(list);
	}

	public Task<Counter> GetCounterAsync(Guid counterId)
	{
		lock (_sync)
		{
			return _counters.FirstOrDefault(counter => counter.Id == counterId) is { } counter
				? Task.FromResult(Copy(counter))
				: Task.FromException<Counter>(ApiError(HttpStatusCode.NotFound));
		}
	}

	public Task<long> IncrementCounterAsync(Guid counterId) => Change(counterId, true, null);

	public Task<long> IncrementCounterAsync(Guid counterId, int amount) => Change(counterId, true, amount);

	public Task<long> DecrementCounterAsync(Guid counterId) => Change(counterId, false, null);

	public Task<long> DecrementCounterAsync(Guid counterId, int amount) => Change(counterId, false, amount);

	public Task<Counter> UpdateCounterAsync(Guid counterId, UpdateCounterRequest request)
		=> throw new NotSupportedException();

	public void Dispose() => Disposed = true;

	/// <summary>Changes the account without telling the client, as a missed push or a new day would.</summary>
	public void SetSilently(params Counter[] counters)
	{
		lock (_sync)
		{
			_counters.Clear();
			_counters.AddRange(counters);
		}
	}

	public void PushUpdate(Counter counter)
	{
		lock (_sync)
		{
			_counters.RemoveAll(existing => existing.Id == counter.Id);
			_counters.Add(counter);
		}

		CounterUpdated?.Invoke(Copy(counter));
	}

	public void PushDelete(Guid counterId)
	{
		lock (_sync)
		{
			_counters.RemoveAll(existing => existing.Id == counterId);
		}

		CounterDeleted?.Invoke(counterId);
	}

	public void PushMemberlistChanged(Guid counterId) => CounterMemberlistChanged?.Invoke(counterId);

	public static CountTogetherApiException ApiError(HttpStatusCode status)
		=> new($"The CountTogether API responded with {(int)status}.", status, null, null);

	private Task<long> Change(Guid counterId, bool increment, int? amount)
	{
		Changes.Add((counterId, increment, amount));
		if (ChangeFailure is { } failure)
		{
			return Task.FromException<long>(failure);
		}

		lock (_sync)
		{
			var current = _counters.First(counter => counter.Id == counterId).Value ?? 0;
			return Task.FromResult(ChangeResult ?? current + ((increment ? 1 : -1) * (amount ?? 1)));
		}
	}

	private static Counter Copy(Counter counter) => new()
	{
		Id = counter.Id,
		Name = counter.Name,
		Type = counter.Type,
		Value = counter.Value,
		StartDate = counter.StartDate,
		EndDate = counter.EndDate,
		Created = counter.Created,
	};
}

/// <summary>Hands out prepared clients in order; the last one is reused once the queue is empty.</summary>
internal sealed class FakeClientFactory : ICountTogetherClientFactory
{
	private readonly Queue<FakeCountTogetherClient> _clients;
	private FakeCountTogetherClient _last;

	public FakeClientFactory(params FakeCountTogetherClient[] clients)
	{
		_clients = new Queue<FakeCountTogetherClient>(clients);
		_last = clients[^1];
	}

	public int Created { get; private set; }

	public ICountTogetherClient Create()
	{
		Created++;
		if (_clients.TryDequeue(out var next))
		{
			_last = next;
		}

		return _last;
	}
}

internal sealed class StubHttpClientFactory : IHttpClientFactory
{
	public HttpClient CreateClient(string name) => new();
}

internal static class Counters
{
	public static Counter UpDown(string name, long value, Guid? id = null) => new()
	{
		Id = id ?? Guid.NewGuid(),
		Name = name,
		Type = CounterType.UpDown,
		Value = value,
		Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
	};

	public static Counter ToDate(string name, long daysLeft) => new()
	{
		Id = Guid.NewGuid(),
		Name = name,
		Type = CounterType.ToDate,
		Value = daysLeft,
		Created = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
	};
}
