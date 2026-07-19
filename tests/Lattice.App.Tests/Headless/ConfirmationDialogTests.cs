using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Lattice.App.Aggregation;
using Lattice.App.Views;
using Xunit;

namespace Lattice.App.Tests.Headless;

// Machine-side gates for the generic confirmation dialog: request rendering,
// severity → danger-class/default-button routing, and the bool result mapping.
// VISUAL fidelity (what the danger button actually looks like) is owner-eyeball
// territory, deliberately not asserted here.
public class ConfirmationDialogTests
{
    private static ConfirmationRequest Destructive() =>
        new("Abort task?", "Abort \"wu_1\" on host-a? Computed work will be lost.", "Abort",
            ConfirmSeverity.Destructive);

    private static ConfirmationRequest Caution() =>
        new("Suspend on 3 hosts?", "Suspend Einstein@Home on: a, b, c.", "Suspend",
            ConfirmSeverity.Caution);

    private static (Window Window, ConfirmationDialog Dialog, Task<bool> Result) Show(ConfirmationRequest request)
    {
        var window = new Window { Width = 800, Height = 600 };
        window.Show();
        var dialog = new ConfirmationDialog { DataContext = request };
        var result = ConfirmationDialog.ConfirmAsync(window, dialog);
        Dispatcher.UIThread.RunJobs();
        return (window, dialog, result);
    }

    private static Button PrimaryButton(Window window) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PrimaryButton");

    [AvaloniaFact]
    public async Task Destructive_request_renders_danger_primary_with_safe_default()
    {
        var (window, dialog, result) = Show(Destructive());

        Assert.Equal("Abort task?", dialog.Title as string);
        Assert.Equal("Abort", dialog.PrimaryButtonText);
        var primary = PrimaryButton(window);
        Assert.Contains("danger", primary.Classes);
        // Enter must not destroy work: the safe (Close) button is the default.
        Assert.Equal(FAContentDialogButton.Close, dialog.DefaultButton);
        var body = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == Destructive().Body);
        Assert.NotNull(body);

        window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CloseButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(await result);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Caution_request_has_no_danger_class_and_confirms_on_primary()
    {
        var (window, dialog, result) = Show(Caution());

        var primary = PrimaryButton(window);
        Assert.DoesNotContain("danger", primary.Classes);
        Assert.Equal(FAContentDialogButton.Primary, dialog.DefaultButton);

        primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(await result);
        window.Close();
    }
}
