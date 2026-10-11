using System.Net;
using System.Net.WebSockets;
using CountTogether.PublicApiSdk.Abstractions.Client;

namespace CountTogether.MacroDeckPlugin.Api;

public enum CountTogetherFailure
{
	NotConfigured,
	NotConnected,
	Unauthorized,
	PermissionDenied,
	NotFound,
	NotUpDown,
	RateLimited,
	Timeout,
	Rejected,
	Unreachable,
	Failed
}

public static class CountTogetherFailures
{
	public static CountTogetherFailure Classify(Exception exception)
		=> exception switch
		{
			CountTogetherApiException api => FromStatus(api.StatusCode),
			// The SDK opens the WebSocket before any REST call, so a rejected token surfaces as a refused upgrade.
			WebSocketException { WebSocketErrorCode: WebSocketError.NotAWebSocket } socket
				when socket.Message.Contains("'401'", StringComparison.Ordinal) ||
					socket.Message.Contains("'403'", StringComparison.Ordinal) => CountTogetherFailure.Unauthorized,
			TimeoutException or TaskCanceledException => CountTogetherFailure.Timeout,
			WebSocketException or HttpRequestException or IOException => CountTogetherFailure.Unreachable,
			ArgumentException => CountTogetherFailure.Rejected,
			_ => CountTogetherFailure.Failed,
		};

	private static CountTogetherFailure FromStatus(HttpStatusCode? status)
		=> status switch
		{
			HttpStatusCode.Unauthorized => CountTogetherFailure.Unauthorized,
			HttpStatusCode.Forbidden => CountTogetherFailure.PermissionDenied,
			HttpStatusCode.NotFound => CountTogetherFailure.NotFound,
			HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => CountTogetherFailure.RateLimited,
			HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => CountTogetherFailure.Timeout,
			>= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError => CountTogetherFailure.Rejected,
			>= HttpStatusCode.InternalServerError => CountTogetherFailure.Unreachable,
			_ => CountTogetherFailure.Failed,
		};
}

/// <summary>The outcome of incrementing or decrementing a counter.</summary>
public readonly record struct CounterChangeResult(long? Value, CountTogetherFailure? Failure)
{
	public bool Succeeded => Failure is null;

	public static CounterChangeResult Success(long value) => new(value, null);

	public static CounterChangeResult Failed(CountTogetherFailure failure) => new(null, failure);
}

public enum TokenCheckResult
{
	Valid,
	Rejected,
	Unreachable
}
