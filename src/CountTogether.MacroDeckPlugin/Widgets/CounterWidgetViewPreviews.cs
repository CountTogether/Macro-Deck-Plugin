using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Previews;
using MacroDeck.Ui.Runtime;

namespace CountTogether.MacroDeckPlugin.Widgets;

internal static class CounterWidgetViewPreviews
{
	[UiPreview("Up/down counter", Profile = UiPreviewProfiles.Widget)]
	public static UiElement UpDown() => Counter(new() { Status = CounterWidgetStatus.Ready, Name = "Push-ups", Number = 42 });

	[UiPreview("Large value", Profile = UiPreviewProfiles.Widget)]
	public static UiElement LargeValue()
		=> Counter(new() { Status = CounterWidgetStatus.Ready, Name = "Visitors", Number = 1_234_567_890 });

	[UiPreview("Long name", Profile = UiPreviewProfiles.Widget)]
	public static UiElement LongName()
		=> Counter(new()
		{
			Status = CounterWidgetStatus.Ready,
			Name = "Cups of coffee in the office kitchen this week",
			Number = 17,
		});

	[UiPreview("Days left", Profile = UiPreviewProfiles.Widget)]
	public static UiElement DaysLeft()
		=> Counter(new() { Status = CounterWidgetStatus.Ready, Name = "Vacation", Number = 12, Caption = CounterCaption.DaysLeft });

	[UiPreview("One day left", Profile = UiPreviewProfiles.Widget)]
	public static UiElement OneDayLeft()
		=> Counter(new() { Status = CounterWidgetStatus.Ready, Name = "Release", Number = 1, Caption = CounterCaption.DaysLeft });

	[UiPreview("Today", Profile = UiPreviewProfiles.Widget)]
	public static UiElement Today() => Counter(new() { Status = CounterWidgetStatus.Ready, Name = "Birthday", IsToday = true });

	[UiPreview("Overdue", Profile = UiPreviewProfiles.Widget)]
	public static UiElement Overdue()
		=> Counter(new()
		{
			Status = CounterWidgetStatus.Ready,
			Name = "Tax return",
			Number = 3,
			Caption = CounterCaption.DaysOverdue,
			Overdue = true,
		});

	[UiPreview("Days ago", Profile = UiPreviewProfiles.Widget)]
	public static UiElement DaysAgo()
		=> Counter(new() { Status = CounterWidgetStatus.Ready, Name = "Smoke free", Number = 365, Caption = CounterCaption.DaysAgo });

	[UiPreview("Accent colour", Profile = UiPreviewProfiles.Widget)]
	public static UiElement Accent()
		=> Counter(new() { Status = CounterWidgetStatus.Ready, Name = "Push-ups", Number = 42 },
			CounterWidgetSettings.Default with { Accent = "#e91e63" });

	[UiPreview("Buttons", Profile = UiPreviewProfiles.Widget)]
	public static UiElement Buttons()
		=> Counter(new() { Status = CounterWidgetStatus.Ready, Name = "Push-ups", Number = 42, CanChange = true }, withButtons: true);

	[UiPreview("Buttons, change failed", Profile = UiPreviewProfiles.Widget)]
	public static UiElement ButtonsFailed()
		=> Counter(new()
		{
			Status = CounterWidgetStatus.Ready,
			Name = "Push-ups",
			Number = 42,
			CanChange = true,
			ChangeFailed = true,
		}, withButtons: true);

	[UiPreview("Buttons on a date counter", Profile = UiPreviewProfiles.Widget)]
	public static UiElement ButtonsOnDateCounter()
		=> Counter(new() { Status = CounterWidgetStatus.Ready, Name = "Vacation", Number = 12, Caption = CounterCaption.DaysLeft },
			withButtons: true);

	[UiPreview("Picker sample", Profile = UiPreviewProfiles.Widget)]
	public static UiElement PickerSample() => Counter(CounterWidgetUi.Sample(withButtons: true), withButtons: true);

	[UiPreview("Not configured", Profile = UiPreviewProfiles.Widget)]
	public static UiElement NotConfigured() => Counter(new() { Status = CounterWidgetStatus.NotConfigured });

	[UiPreview("Not set up", Profile = UiPreviewProfiles.Widget)]
	public static UiElement NotSetUp() => Counter(new() { Status = CounterWidgetStatus.NotSetUp });

	[UiPreview("Connecting", Profile = UiPreviewProfiles.Widget)]
	public static UiElement Connecting() => Counter(new() { Status = CounterWidgetStatus.Connecting });

	[UiPreview("Token rejected", Profile = UiPreviewProfiles.Widget)]
	public static UiElement TokenRejected() => Counter(new() { Status = CounterWidgetStatus.TokenRejected });

	[UiPreview("Unreachable", Profile = UiPreviewProfiles.Widget)]
	public static UiElement Unreachable() => Counter(new() { Status = CounterWidgetStatus.Unreachable });

	[UiPreview("Counter unavailable", Profile = UiPreviewProfiles.Widget)]
	public static UiElement Unavailable() => Counter(new() { Status = CounterWidgetStatus.Unavailable });

	private static UiElement Counter(CounterWidgetState state, CounterWidgetSettings? settings = null, bool withButtons = false)
		=> CounterWidgetView.Build(new UiState<CounterWidgetState>(state), settings ?? CounterWidgetSettings.Default,
			withButtons, commands: null, WidgetLayout.DefaultPadding);
}
