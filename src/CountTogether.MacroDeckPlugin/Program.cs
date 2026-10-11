using CountTogether.MacroDeckPlugin;
using CountTogether.MacroDeckPlugin.Api;
using MacroDeck.Plugin.Hosting;
using MacroDeck.Plugin.Serilog;

// Identity, description and icon are not set here: they come from manifest.json at the content root.
// Strings is generated from Localization/*.resx, so UseLocalization is what makes every LocalizedString
// below resolve in the user's language rather than falling back to its key.
var builder = MacroDeckPlugin.CreatePlugin(args)
	.UseMacroDeckLogging()
	.UseLocalization(Strings.LocalizationCatalog)
	.RegisterIntegration<CountTogetherIntegration>();

builder.Services.AddCountTogether();

var plugin = builder.Build();

await plugin.RunAsync();
