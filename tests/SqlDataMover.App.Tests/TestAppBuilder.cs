using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Skia;

// Регистрация Avalonia-тестового фреймворка (xunit v3) для всей сборки:
// обычные [Fact] и [AvaloniaFact] выполняются на диспетчере Avalonia.
[assembly: AvaloniaTestFramework]
// Точка входа AppBuilder для headless-сессии (нужен статический BuildAvaloniaApp()).
[assembly: AvaloniaTestApplication(typeof(SqlDataMover.App.Tests.TestAppBuilder))]

namespace SqlDataMover.App.Tests;

/// <summary>
/// Запуск приложения в headless-режиме: окна создаются и рендерятся без дисплея.
/// UseSkia() обязателен: даёт шрифтовой менеджер (текстовая разметка) и настоящий рендер;
/// UseHeadlessDrawing=false — чтобы CaptureRenderedFrame возвращал реальный кадр.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
