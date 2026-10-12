using CountTogether.MacroDeckPlugin.Api;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;

namespace CountTogether.MacroDeckPlugin.ConfigFlow;

/// <summary>Asks for the personal API token and only stores it once CountTogether accepted it.</summary>
public sealed class ApiTokenConfigFlow : IConfigFlow
{
	public const string StepId = "token";
	public const string TokenKey = "token";
	public const string EntryTitle = "CountTogether";
	internal const string DocumentationUrl = "https://developers.counttogether.app/docs/overview.html";

	private readonly CountTogetherConnection _connection;

	public ApiTokenConfigFlow(CountTogetherConnection connection) => _connection = connection;

	public Task<ConfigFlowResult> StartAsync(IConfigFlowContext context, CancellationToken cancellationToken)
		=> Task.FromResult(ConfigFlowResult.Step(Step(IsEditing(context))));

	public async Task<ConfigFlowResult> SubmitAsync(
		string stepId,
		IReadOnlyDictionary<string, object?> input,
		IConfigFlowContext context,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(input);

		var editing = IsEditing(context);
		var token = (input.GetValueOrDefault(TokenKey) as string)?.Trim();
		// When editing, the host fills an empty secret field with the stored token before it calls the flow,
		// so an empty token here means there is none at all.
		if (string.IsNullOrEmpty(token))
		{
			return Error(editing, TokenError(MacroDeckStrings.Validation.Required(Strings.ConfigFlow.Token.Field.Label())));
		}

		return await _connection.ValidateTokenAsync(token, cancellationToken).ConfigureAwait(false) switch
		{
			TokenCheckResult.Valid => ConfigFlowResult.Complete(EntryTitle,
				new Dictionary<string, ConfigFlowValue> { [TokenKey] = ConfigFlowValue.Secret(token) }),
			TokenCheckResult.Rejected => Error(editing, TokenError(Strings.ConfigFlow.Errors.Rejected())),
			_ => ConfigFlowResult.Error(Step(editing), Strings.ConfigFlow.Errors.Unreachable()),
		};
	}

	private static bool IsEditing(IConfigFlowContext context) => (context as IConfigFlowEntryContext)?.EntryTitle is not null;

	private static ConfigFlowResult Error(bool editing, IReadOnlyDictionary<string, LocalizedText> fieldErrors)
		=> ConfigFlowResult.Error(Step(editing), fieldErrors: fieldErrors);

	private static Dictionary<string, LocalizedText> TokenError(LocalizedText message) => new() { [TokenKey] = message };

	private static ConfigFlowStep Step(bool editing) => new()
	{
		StepId = StepId,
		Title = Strings.ConfigFlow.Token.Title(),
		Description = Strings.ConfigFlow.Token.Description(),
		Instructions = [new ConfigFlowInstruction { Text = Strings.ConfigFlow.Token.Instruction() }],
		Links = [new ConfigFlowLink { Label = Strings.ConfigFlow.Token.DocsLink(), Url = DocumentationUrl }],
		Fields =
		[
			ActionParameter.Secret(TokenKey,
				label: Strings.ConfigFlow.Token.Field.Label(),
				description: Strings.ConfigFlow.Token.Field.Description(),
				required: !editing),
		],
	};
}
