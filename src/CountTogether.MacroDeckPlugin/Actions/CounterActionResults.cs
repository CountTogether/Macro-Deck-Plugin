using CountTogether.MacroDeckPlugin.Api;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;

namespace CountTogether.MacroDeckPlugin.Actions;

internal static class CounterActionResults
{
	public static ActionResult Failed(CountTogetherFailure failure) => ActionResult.Failed(Code(failure), Message(failure));

	public static string Code(CountTogetherFailure failure)
		=> failure switch
		{
			CountTogetherFailure.NotConfigured => ActionErrorCodes.NotConfigured,
			CountTogetherFailure.NotConnected or CountTogetherFailure.Unreachable => ActionErrorCodes.NotConnected,
			CountTogetherFailure.Unauthorized or CountTogetherFailure.PermissionDenied => ActionErrorCodes.PermissionDenied,
			CountTogetherFailure.NotFound => ActionErrorCodes.NotFound,
			CountTogetherFailure.NotUpDown => ActionErrorCodes.InvalidParameter,
			CountTogetherFailure.RateLimited => ActionErrorCodes.Unavailable,
			CountTogetherFailure.Timeout => ActionErrorCodes.Timeout,
			CountTogetherFailure.Rejected => ActionErrorCodes.ProviderRejected,
			_ => ActionErrorCodes.ProviderError,
		};

	public static LocalizedText Message(CountTogetherFailure failure)
		=> failure switch
		{
			CountTogetherFailure.NotConfigured => Strings.Errors.NotConfigured(),
			CountTogetherFailure.NotConnected or CountTogetherFailure.Unreachable => Strings.Errors.NotConnected(),
			CountTogetherFailure.Unauthorized => Strings.Errors.TokenRejected(),
			CountTogetherFailure.PermissionDenied => Strings.Errors.PermissionDenied(),
			CountTogetherFailure.NotFound => Strings.Errors.CounterNotFound(),
			CountTogetherFailure.NotUpDown => Strings.Errors.NotUpDown(),
			CountTogetherFailure.RateLimited => Strings.Errors.RateLimited(),
			CountTogetherFailure.Timeout => Strings.Errors.Timeout(),
			CountTogetherFailure.Rejected => Strings.Errors.Rejected(),
			_ => Strings.Errors.Failed(),
		};
}
