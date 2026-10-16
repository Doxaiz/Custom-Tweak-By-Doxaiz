using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using PingForge.Orchestration.Realtime;

namespace PingForge.App;

/// <summary>
/// Значок в трее: программа живёт фоном и не требует открытого окна.
///
/// Меню и подсказка собираются из <see cref="TrayMenuBuilder"/> — того же кода, что проверен тестами.
/// Здесь только перевод пунктов в элементы WinForms: своей логики у этого файла нет намеренно,
/// иначе подсказка рядом со значком разошлась бы с тем, что делает программа.
/// </summary>
public sealed class TrayPresence : IDisposable
{
    private readonly App _app;
    private readonly Window _window;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;

    public TrayPresence(App app, Window window)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _window = window ?? throw new ArgumentNullException(nameof(window));

        _menu = new ContextMenuStrip();
        _menu.Opening += (_, _) => Rebuild();

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Visible = true,
            ContextMenuStrip = _menu,
            Text = "Custom Tweak Dozaiz",
        };

        _icon.DoubleClick += (_, _) => ShowWindow();

        // Первое наполнение меню: до открытия оно должно быть готово, а не пусто.
        Rebuild();
    }

    /// <summary>
    /// Собрать меню по текущему состоянию. Каждый пункт получает действие из того же списка, что
    /// показан пользователю: нажатие не может сделать не то, что написано.
    /// </summary>
    private void Rebuild()
    {
        RealtimeSnapshot snapshot = BuildSnapshot();

        IReadOnlyList<TrayMenuItem> items = TrayMenuBuilder.Build(
            snapshot,
            isAdministrator: _app.MainScreen.HasRights,
            revertableChangeCount: _app.Tweaks.CheckedCount);

        _menu.Items.Clear();
        foreach (TrayMenuItem item in items)
        {
            var menuItem = new ToolStripMenuItem(item.TitleRu)
            {
                Enabled = item.IsEnabled,
                ToolTipText = item.TooltipRu,
            };

            TrayCommand command = item.Command;
            menuItem.Click += (_, _) => Execute(command);
            _menu.Items.Add(menuItem);
        }

        string tip = TrayMenuBuilder.TooltipFor(snapshot);
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    /// <summary>
    /// Состояние для сборщика меню — из того, что реально показывает окно. Нет измерений — так и записано:
    /// «замеров пока нет», а не «0 мс».
    /// </summary>
    private RealtimeSnapshot BuildSnapshot()
    {
        RealtimeOptimizationState state = _app.MainScreen.IsOptimizing
            ? _app.MainScreen.HasRights ? RealtimeOptimizationState.ActiveMonitoring : RealtimeOptimizationState.ReadOnlyNoAdmin
            : RealtimeOptimizationState.Idle;

        return new RealtimeSnapshot(
            state,
            _app.MainScreen.StatusRu,
            _app.MainScreen.GameTitleRu,
            _app.MainScreen.Endpoint,
            _app.MainScreen.BaselineRttMs,
            _app.MainScreen.CurrentRttMs,
            FloorRttMs: null,
            CurrentJitterMs: null,
            CurrentLossPercent: null,
            _app.MainScreen.ImprovementMs,
            _app.MainScreen.QualityRu,
            [],
            [],
            DateTime.UtcNow);
    }

    private async void Execute(TrayCommand command)
    {
        try
        {
            switch (command)
            {
                case TrayCommand.StartOptimization:
                case TrayCommand.StopOptimization:
                    await _app.MainScreen.ToggleOptimizationAsync().ConfigureAwait(true);
                    break;

                case TrayCommand.RevertAllChanges:
                    await _app.Tweaks.RollbackAsync().ConfigureAwait(true);
                    break;

                case TrayCommand.ShowWindow:
                    ShowWindow();
                    break;

                case TrayCommand.OpenSettings:
                    ShowWindow();
                    ShowBalloon(
                        "Настройки — в окне программы",
                        "Откройте раздел «Настройки»: там профиль потребления и путь к файлу конфига.");
                    break;

                case TrayCommand.Exit:
                    _app.ExitApplication();
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowBalloon("Не получилось", ex.Message);
        }
    }

    private void ShowWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    public void ShowBalloon(string titleRu, string messageRu)
    {
        _icon.BalloonTipTitle = titleRu;
        _icon.BalloonTipText = messageRu;
        _icon.ShowBalloonTip(4000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
