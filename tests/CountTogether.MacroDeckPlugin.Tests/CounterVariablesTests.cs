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

		var counterVariables = variables.Where(variable => variable.Id != CounterVariables.ConnectedId).ToList();
		Assert.That(counterVariables.Select(variable => variable.Name), Is.EqualTo(new[] { "counttogether_coffee", "counttogether_coffee_2" }));
		Assert.That(counterVariables.Select(variable => variable.Id), Is.EqualTo(new[] { CounterVariables.IdFor(first.Id), CounterVariables.IdFor(second.Id) }));
		Assert.That(variables.All(variable => System.Text.RegularExpressions.Regex.IsMatch(variable.Name!, "^[a-z][a-z0-9_]*$")), Is.True);
	}

	[Test]
	public void A_variable_id_leads_back_to_its_counter()
	{
		var counterId = Guid.NewGuid();

		Assert.That(CounterVariables.TryParseId(CounterVariables.IdFor(counterId), out var parsed, out var isDate), Is.True);
		Assert.That((parsed, isDate), Is.EqualTo((counterId, false)));
		Assert.That(CounterVariables.TryParseId(CounterVariables.DateIdFor(counterId), out parsed, out isDate), Is.True);
		Assert.That((parsed, isDate), Is.EqualTo((counterId, true)));
		Assert.That(CounterVariables.TryParseId("something-else", out _, out _), Is.False);
	}

	[Test]
	public void Date_counters_also_get_a_writable_date_variable_without_renaming_value_variables()
	{
		var vacation = new CounterSnapshot(Guid.NewGuid(), "Vacation", CounterKind.ToDate, 12, Date: new DateOnly(2026, 10, 23));
		var vacationDate = new CounterSnapshot(Guid.NewGuid(), "Vacation date", CounterKind.UpDown, 1);

		var variables = CounterVariables.Build([vacation, vacationDate]);

		Assert.That(variables.Where(variable => variable.Id != CounterVariables.ConnectedId).Select(variable => variable.Name),
			Is.EqualTo(new[] { "counttogether_vacation", "counttogether_vacation_date", "counttogether_vacation_date_2" }));
		var date = variables.Single(variable => variable.Id == CounterVariables.DateIdFor(vacation.Id));
		Assert.That(date.Type, Is.EqualTo(MacroDeck.Sdk.Variables.VariableType.Text));
		Assert.That(date.CanWrite, Is.True);
	}

	[Test]
	public void The_connection_variable_exists_even_without_counters()
	{
		var variables = CounterVariables.Build([]);

		Assert.That(variables.Select(variable => (variable.Id, variable.Name)),
			Is.EqualTo(new[] { (CounterVariables.ConnectedId, "counttogether_connected") }));
	}

	[Test]
	public void Only_regular_counter_values_are_writable()
	{
		var coffee = new CounterSnapshot(Guid.NewGuid(), "Coffee", CounterKind.UpDown, 1);
		var vacation = new CounterSnapshot(Guid.NewGuid(), "Vacation", CounterKind.ToDate, 12, Date: new DateOnly(2026, 10, 23));

		var variables = CounterVariables.Build([coffee, vacation]);

		Assert.That(variables.Single(variable => variable.Id == CounterVariables.IdFor(coffee.Id)).CanWrite, Is.True);
		Assert.That(variables.Single(variable => variable.Id == CounterVariables.IdFor(vacation.Id)).CanWrite, Is.False);
	}

	[TestCase(12.0, true, 12L)]
	[TestCase("-3", true, -3L)]
	[TestCase(1.5, false, 0L)]
	[TestCase("abc", false, 0L)]
	public void Written_values_must_be_whole_numbers(object value, bool valid, long expected)
	{
		Assert.That(CounterVariables.TryParseValue(value, out var number), Is.EqualTo(valid));
		Assert.That(number, Is.EqualTo(expected));
	}

	[TestCase("2026-12-24", true)]
	[TestCase("24.12.2026", false)]
	[TestCase(20261224.0, false)]
	public void Written_dates_use_the_iso_format(object value, bool valid)
		=> Assert.That(CounterVariables.TryParseDate(value, out _), Is.EqualTo(valid));
}
