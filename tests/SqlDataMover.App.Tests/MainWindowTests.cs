using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SqlDataMover.App.ViewModels;
using SqlDataMover.App.Views;
using SqlDataMover.Core.Abstractions;
using SqlDataMover.TestHelpers;

namespace SqlDataMover.App.Tests;

/// <summary>
/// Полноценные GUI-тесты: приложение запускается в headless-режиме (Avalonia.Headless),
/// окно создаётся по-настоящему, взаимодействие идёт через контролы (клики, ввод),
/// биндинги и команды работают как в реальном приложении.
/// </summary>
[Collection("app")]
public class MainWindowTests
{
    private static ProviderDescriptor FakeDescriptor(string key) =>
        DbProviderFactory.SupportedProviders.First(p => p.Key == key);

    [AvaloniaFact]
    public async Task Wizard_full_flow_via_real_ui()
    {
        DbProviderFactory.Register(
            "fake-ui",
            "Fake UI",
            cs =>
                cs == "src"
                    ? new FakeProvider(
                        WizardData.CustomerTable([
                            new object?[] { 10, "Alice" },
                            new object?[] { 20, "Bob" },
                        ]),
                        WizardData.OrderTable([new object?[] { 1, 10 }])
                    )
                    : new FakeProvider(WizardData.CustomerTable(), WizardData.OrderTable())
        );

        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        // Старт: «Далее» и «Назад» заблокированы.
        var next = window.GetControl<Button>("NextButton")!;
        var back = window.GetControl<Button>("BackButton")!;
        Assert.False(next.IsEnabled);
        Assert.False(back.IsEnabled);

        // Шаг 1: выбираем fake-СУБД и заполняем строки подключения прямо в контролах.
        WaitUntil(() => window.GetVisualDescendants().OfType<ConnectionPageView>().Any());
        PageCombo(window, "ProviderBox").SelectedItem = FakeDescriptor("fake-ui");
        PageCombo(window, "SourceConnectionBox").Text = "src";
        PageCombo(window, "TargetConnectionBox").Text = "tgt";
        Assert.Equal("src", vm.Connection.SourceConnectionString); // биндинг сработал

        Click(window, PageButton(window, "ConnectButton"));
        await WaitUntilAsync(() => !vm.Connection.IsBusy);
        Assert.True(vm.Connection.IsReady);
        Assert.Equal("✓ Connected", vm.Connection.SourceStatus);
        Assert.True(next.IsEnabled);

        // → Шаг 2: таблицы загружены.
        Click(window, next);
        await WaitUntilAsync(() => vm.CurrentStep == 1);
        Assert.Equal(2, vm.Tables.TotalCount);

        // Выбираем таблицы (чекбоксы дерева) — «Далее» разблокируется.
        foreach (var schema in vm.Tables.Schemas)
        foreach (var table in schema.AllTables)
            table.IsChecked = true;
        Assert.True(next.IsEnabled);

        // → Шаг 3: сопоставление, поля выбраны по умолчанию.
        Click(window, next);
        await WaitUntilAsync(() => vm.CurrentStep == 2);
        Assert.True(vm.Mapping.AllHaveMatchColumns);

        // → Шаг 4: предпросмотр (dry run) и итоговая строка в UI.
        Click(window, next);
        await WaitUntilAsync(() => vm.CurrentStep == 3 && !vm.Preview.IsBusy);
        Assert.True(vm.Preview.HasPreview);
        var summary = FindVisual<TextBlock>(window, "PreviewSummaryText");
        Assert.NotNull(summary);
        Assert.Contains("Preview complete", summary!.Text);

        // «Заново» возвращает мастера в начало.
        Click(window, window.GetControl<Button>("RestartButton")!);
        await WaitUntilAsync(() => vm.CurrentStep == 0);
        Assert.Empty(vm.Tables.Schemas);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Connect_error_is_shown_in_the_ui()
    {
        DbProviderFactory.Register(
            "fake-ui-fail",
            "Fake UI Fail",
            _ => new FakeProvider(failOnConnect: true)
        );

        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        WaitUntil(() => window.GetVisualDescendants().OfType<ConnectionPageView>().Any());
        PageCombo(window, "ProviderBox").SelectedItem = FakeDescriptor("fake-ui-fail");
        PageCombo(window, "SourceConnectionBox").Text = "src";
        PageCombo(window, "TargetConnectionBox").Text = "tgt";
        Click(window, PageButton(window, "ConnectButton"));
        await WaitUntilAsync(() => !vm.Connection.IsBusy);

        Assert.False(vm.Connection.IsReady);
        Assert.Contains("Connection error", vm.Connection.StatusMessage);
        Assert.False(window.GetControl<Button>("NextButton")!.IsEnabled);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Window_renders_a_frame()
    {
        var window = new MainWindow { DataContext = new MainViewModel() };
        window.Show();

        // Проверка, что окно реально отрендерилось (XAML, стили, шрифты не упали).
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame!.PixelSize.Width > 0 && frame.PixelSize.Height > 0);

        window.Close();
    }

    /// <summary>Контрол страницы подключения: ищем по визуальному дереву, т.к. у страницы свой namescope.</summary>
    private static ComboBox PageCombo(Window window, string name) =>
        FindVisual<ComboBox>(window, name)
        ?? throw new InvalidOperationException($"ComboBox '{name}' не найден");

    private static Button PageButton(Window window, string name) =>
        FindVisual<Button>(window, name)
        ?? throw new InvalidOperationException($"Button '{name}' не найден");

    private static T? FindVisual<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    /// <summary>Настоящий клик левой кнопкой мыши по центру кнопки (через headless-платформу).</summary>
    private static void Click(Window window, Button button)
    {
        // Отработать отложенные задачи (layout): после смены шага трансформации
        // контролов ещё не актуализированы, иначе TranslatePoint даст точку вне окна.
        Dispatcher.UIThread.RunJobs();

        var point = button.TranslatePoint(
            new Point(button.Bounds.Width / 2, button.Bounds.Height / 2),
            window
        );
        Assert.NotNull(point);
        window.MouseDown(point.Value, MouseButton.Left);
        window.MouseUp(point.Value, MouseButton.Left);
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs = 5_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("Условие не выполнилось за отведённое время.");
            Thread.Sleep(20);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("Условие не выполнилось за отведённое время.");
            await Task.Delay(20);
        }
    }
}
