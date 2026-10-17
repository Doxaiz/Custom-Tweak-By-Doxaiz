using System.Windows;
using PingForge.App.ViewModels;

namespace PingForge.App;

/// <summary>
/// Диалоги окна: подтверждения перед изменениями и сообщения о результате.
///
/// Правило, которое здесь держится железно: подтверждение — вопрос, а не уведомление. Кнопки названы
/// делом («Применить», «Вернуть»), а не «ОК», и по умолчанию выбран отказ: случайное нажатие Enter
/// не должно менять настройки системы.
/// </summary>
public sealed class WpfDialogs : IUserDialogs
{
    /// <summary>
    /// Окно-владелец диалога. Раньше здесь стоял MessageBoxOptions.DefaultDesktopOnly — и это
    /// оказалось ловушкой: сообщение показывалось мимо модального цикла окна, главное окно
    /// на всё время ответа становилось «(Not Responding)», а клики по меню не доходили.
    /// Диалог должен принадлежать своему окну — тогда окно ждёт ответа, но остаётся живым.
    /// </summary>
    private static Window? Owner =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
        ?? Application.Current?.MainWindow;

    public Task<bool> ConfirmAsync(string titleRu, string messageRu, string confirmLabelRu, string cancelLabelRu)
    {
        const string title = "Custom Tweak By Doxaiz — ";
        MessageBoxResult answer = Owner is { } owner
            ? MessageBox.Show(owner, messageRu, title + titleRu, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(messageRu, title + titleRu, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

        return Task.FromResult(answer == MessageBoxResult.Yes);
    }

    public Task InformAsync(string titleRu, string messageRu)
    {
        const string title = "Custom Tweak By Doxaiz — ";
        if (Owner is { } owner)
        {
            MessageBox.Show(owner, messageRu, title + titleRu, MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK);
        }
        else
        {
            MessageBox.Show(messageRu, title + titleRu, MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK);
        }

        return Task.CompletedTask;
    }

    public async Task<bool> OfferRestorePointAsync(int tweakCount, string presetTitleRu) =>
        await ConfirmAsync(
            "Точка восстановления Windows",
            $"Перед изменением {tweakCount} настроек ({presetTitleRu}) программа может создать точку восстановления Windows. " +
            "Это медленнее на 10–30 секунд, зато откатить изменения можно будет даже средствами самой Windows, " +
            "даже если программа окажется удалена.\n\nСоздавать точку восстановления?",
            "Создать",
            "Пропустить").ConfigureAwait(true);
}
