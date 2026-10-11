using System.Net;
using CountTogether.MacroDeckPlugin.Api;
using CountTogether.MacroDeckPlugin.ConfigFlow;
using CountTogether.MacroDeckPlugin.Tests.Support;
using MacroDeck.Plugin.Testing;
using MacroDeck.Sdk.ConfigFlow;
using NUnit.Framework;

namespace CountTogether.MacroDeckPlugin.Tests;

[TestFixture]
public sealed class ConfigFlowTests
{
	private static ApiTokenConfigFlow Flow(FakeCountTogetherClient client)
		=> new(new CountTogetherConnection(new FakeClientFactory(client), new StubHttpClientFactory(), new ManualTimeProvider(),
			Serilog.Core.Logger.None));

	private static Dictionary<string, object?> Input(string? token) => new() { [ApiTokenConfigFlow.TokenKey] = token };

	[Test]
	public async Task An_accepted_token_completes_the_setup_as_a_secret()
	{
		var result = await Flow(new FakeCountTogetherClient())
			.SubmitAsync(ApiTokenConfigFlow.StepId, Input("  abc  "), new Context(null), CancellationToken.None);

		Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Complete));
		Assert.That(result.Values![ApiTokenConfigFlow.TokenKey], Is.EqualTo(ConfigFlowValue.Secret("abc")));
	}

	[Test]
	public async Task A_rejected_token_is_reported_on_the_field()
	{
		var client = new FakeCountTogetherClient { StartFailure = FakeCountTogetherClient.ApiError(HttpStatusCode.Unauthorized) };

		var result = await Flow(client).SubmitAsync(ApiTokenConfigFlow.StepId, Input("abc"), new Context(null), CancellationToken.None);

		Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Error));
		Assert.That(result.FieldErrors, Contains.Key(ApiTokenConfigFlow.TokenKey));
	}

	[Test]
	public async Task An_outage_is_reported_on_the_step_and_keeps_nothing()
	{
		var client = new FakeCountTogetherClient { StartFailure = new HttpRequestException("No route to host") };

		var result = await Flow(client).SubmitAsync(ApiTokenConfigFlow.StepId, Input("abc"), new Context(null), CancellationToken.None);

		Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Error));
		Assert.That(result.FieldErrors, Is.Null);
		Assert.That(result.ErrorMessage.IsEmpty, Is.False);
	}

	[Test]
	public async Task A_new_setup_requires_a_token()
	{
		var result = await Flow(new FakeCountTogetherClient())
			.SubmitAsync(ApiTokenConfigFlow.StepId, Input(" "), new Context(null), CancellationToken.None);

		Assert.That(result.FieldErrors, Contains.Key(ApiTokenConfigFlow.TokenKey));
	}

	[Test]
	public async Task Editing_rechecks_the_token_the_host_passes_back()
	{
		var client = new FakeCountTogetherClient();

		var result = await Flow(client).SubmitAsync(ApiTokenConfigFlow.StepId, Input("stored"), new Context("CountTogether"),
			CancellationToken.None);

		Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Complete));
		Assert.That(client.StartedWithToken, Is.EqualTo("stored"));
	}

	[Test]
	public async Task Editing_without_any_token_still_asks_for_one()
	{
		var result = await Flow(new FakeCountTogetherClient())
			.SubmitAsync(ApiTokenConfigFlow.StepId, Input(""), new Context("CountTogether"), CancellationToken.None);

		Assert.That(result.FieldErrors, Contains.Key(ApiTokenConfigFlow.TokenKey));
	}

	private sealed class Context(string? entryTitle) : IConfigFlowEntryContext
	{
		public string? EntryTitle { get; } = entryTitle;

		public IOAuthSession OAuth => throw new NotSupportedException();
	}
}
