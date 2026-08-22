using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SqlDataMover.App.ViewModels;
using SqlDataMover.App.Views;

namespace SqlDataMover.App.Tests;

/// <summary>
/// Ширина полей строк подключения (editable ComboBox) не должна зависеть от
/// длины введённого текста: поле растягивается на всю ширину карточки.
/// </summary>
[Collection("app")]
public class ConnectionPageWidthTests
{
    [AvaloniaFact]
    public async Task Connection_string_fields_fill_the_card_width()
    {
        var window = new MainWindow { DataContext = new MainViewModel() };
        window.Show();

        var page = window.GetVisualDescendants().OfType<ConnectionPageView>().First();
        var source = page.FindControl<ComboBox>("SourceConnectionBox")!;
        var target = page.FindControl<ComboBox>("TargetConnectionBox")!;

        Dispatcher.UIThread.RunJobs();

        foreach (var combo in new[] { source, target })
        {
            var parent = (StackPanel)combo.Parent!;
            Assert.True(
                combo.Bounds.Width >= parent.Bounds.Width - 1,
                $"{combo.Name}: поле уже контейнера (combo={combo.Bounds.Width:F1}, parent={parent.Bounds.Width:F1})"
            );
        }

        // Длинный текст не должен «схлопывать» поле.
        source.Text = new string('x', 400);
        Dispatcher.UIThread.RunJobs();
        var sourceParent = (StackPanel)source.Parent!;
        Assert.True(
            source.Bounds.Width >= sourceParent.Bounds.Width - 1,
            $"Ширина схлопнулась после ввода текста: combo={source.Bounds.Width:F1}, parent={sourceParent.Bounds.Width:F1}"
        );

        window.Close();
    }
}
