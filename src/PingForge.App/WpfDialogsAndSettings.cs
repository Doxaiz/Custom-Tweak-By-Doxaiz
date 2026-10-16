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
    public Task<bool> ConfirmAsync(string titleRu, string messageRu, string confirmLabelRu, string cancelLabelRu)
    {
        MessageBoxResult answer = MessageBox.Show(
            messageRu,
            $"Custom Tweak By Doxaiz — {titleRu}",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No,
            MessageBoxOptions.DefaultDesktopOnly);

        return Task.FromResult(answer == MessageBoxResult.Yes);
    }

    public Task InformAsync(string titleRu, string messageRu)
    {
        MessageBox.Show(
            messageRu,
            $"Custom Tweak By Doxaiz — {titleRu}",
            MessageBoxButton.OK,
            MessageBoxImage.Information,
            MessageBoxResult.OK,
            MessageBoxOptions.DefaultDesktopOnly);

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
