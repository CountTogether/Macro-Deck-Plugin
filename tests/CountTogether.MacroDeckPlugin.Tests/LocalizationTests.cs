using NUnit.Framework;

namespace CountTogether.MacroDeckPlugin.Tests;

/// <summary>
/// The localization set is generated from <c>Localization/*.resx</c>, so these guard the wiring rather
/// than any wording: a missing catalog registration leaves every label showing its raw key.
/// </summary>
[TestFixture]
public sealed class LocalizationTests
{
	[Test]
	public void The_catalog_is_scoped_to_the_plugin_id()
		=> Assert.That(Strings.LocalizationCatalog.Scope, Is.EqualTo("plugin:app.counttogether.counttogether-macro-deck-plugin"));

	[Test]
	public void English_is_the_default_culture()
	{
		Assert.That(Strings.LocalizationCatalog.DefaultCulture, Is.EqualTo("en"));
		Assert.That(Strings.LocalizationCatalog.Cultures, Does.Contain("en").And.Contain("de"));
	}

	[TestCase("en")]
	[TestCase("de")]
	public void Every_key_resolves_to_text(string culture)
	{
		foreach (var key in Strings.LocalizationCatalog.KeysOf("en"))
		{
			Assert.That(Strings.LocalizationCatalog.TryGetTemplate(culture, key, out var text), Is.True, key);
			Assert.That(text, Is.Not.Empty, key);
		}
	}
}
