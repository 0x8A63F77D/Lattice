using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Lattice.App.Localization;
using Lattice.App.ViewModels;
using Lattice.App.Views;
using Lattice.Core;
using Lattice.Tests;
using Xunit;
using static Lattice.Tests.HeadlessLayout;

namespace Lattice.App.Tests.Headless;

public class EditHostDialogTests
{
    [AvaloniaFact]
    public async Task Auth_failed_edit_dialog_shows_error_and_focuses_password()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-test-{Guid.NewGuid():N}.json");
        var registry = new HostRegistry(new LatticeConfig(5, []), path);
        var cfg = TestData.MakeHostConfig(name: "mini-01");
        registry.AddHost(cfg);
        var vm = AddHostViewModel.ForEdit(registry, () => new FakeGuiRpcClient(), cfg, authError: true);
        var dialog = new AddHostDialog { DataContext = vm };
        var window = new Window { Width = 600, Height = 500 };
        window.Show();
        Layout(window);
        _ = dialog.ShowAsync(window);
        Layout(window);

        Assert.Equal(Strings.EditHostDialogTitle, dialog.Title);
        var pwd = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PasswordBox");
        Assert.Contains("danger", pwd.Classes);
        Assert.True(pwd.IsFocused);
        // Secondary "Test connection" button is present in edit mode.
        Assert.Equal(Strings.SettingsTestConnectionButton, dialog.SecondaryButtonText);
        dialog.Hide();
        window.Close();
    }
}
