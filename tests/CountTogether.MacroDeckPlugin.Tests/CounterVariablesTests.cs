using CountTogether.MacroDeckPlugin.Api;
using CountTogether.MacroDeckPlugin.Variables;
using NUnit.Framework;

namespace CountTogether.MacroDeckPlugin.Tests;

[TestFixture]
public sealed class CounterVariablesTests
{
	[TestCase("Push-ups", ExpectedResult = "push_ups")]
	[TestCase("Größe Ü", ExpectedResult = "groesse_ue")]
	[TestCase("Café 2.0", ExpectedResult = "cafe_2_0")]
	[TestCase("Kaffee & Kuchen", ExpectedResult = "kaffee___kuchen")]
	[TestCase("  ---  ", ExpectedResult = "counter")]
	public string Names_follow_the_macro_deck_2_scheme(string name) => CounterVariables.Slug(name);

	[Test]
	public void Counters_with_the_same_name_get_distinct_variables_with_stable_ids()
	{
		var first = new CounterSnapshot(Guid.NewGuid(), "Coffee", CounterKind.UpDown, 1);
		var second = new CounterSnapshot(Guid.NewGuid(), "COFFEE", CounterKind.UpDown, 2);

		var variables = CounterVariables.Build([first, second]);

		Assert.That(variables.Select(variable => variable.Name), Is.EqualTo(new[] { "counttogether_coffee", "counttogether_coffee_2" }));
		Assert.That(variables.Select(variable => variable.Id), Is.EqualTo(new[] { CounterVariables.IdFor(first.Id), CounterVariables.IdFor(second.Id) }));
		Assert.That(variables.All(variable => System.Text.RegularExpressions.Regex.IsMatch(variable.Name!, "^[a-z][a-z0-9_]*$")), Is.True);
	}

	[Test]
	public void A_variable_id_leads_back_to_its_counter()
	{
		var counterId = Guid.NewGuid();

		Assert.That(CounterVariables.TryParseId(CounterVariables.IdFor(counterId), out var parsed), Is.True);
		Assert.That(parsed, Is.EqualTo(counterId));
		Assert.That(CounterVariables.TryParseId("something-else", out _), Is.False);
	}
}
