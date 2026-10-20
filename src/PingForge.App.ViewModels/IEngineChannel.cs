using PingForge.Network;
using PingForge.Network.Routes;
using PingForge.Service.Protocol;

namespace PingForge.App.ViewModels;

/// <summary>Не удалось поговорить со службой: канал закрыт, служба не запущена, ответ не пришёл.</summary>
public sealed class EngineUnavailableException : Exception
{
    public EngineUnavailableException(string messageRu, Exception? inner = null)
        : base(messageRu, inner)
    {
    }
}

/// <summary>
/// Канал связи со службой с точки зрения интерфейса. В бою это именованный канал
/// (<see cref="NamedPipeEngineChannel"/>), в тестах — двойник, который отвечает заранее.
/// </summary>
public interface IEngineChannel : IDisposable
{
    /// <summary>
    /// Отправить запрос и получить ответ. Бросает <see cref="EngineUnavailableException"/>, если связи нет.
    /// Предел ожидания — у каждой команды свой: долгая команда (точка восстановления, живой запуск)
    /// не должна выглядеть как «служба не ответила» через десять секунд.
    /// </summary>
    Task<EngineResponse> SendAsync(EngineRequest request, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>
/// Подтверждения пользователя. Каждое действие, меняющее систему, сначала спрашивает — и это правило
/// проверяется тестами: «применил без спроса» невозможно, потому что применить можно только через диалог.
/// </summary>
public interface IUserDialogs
{
    /// <summary>Вопрос «да/нет». Возвращает false, если пользователь отказался или закрыл окно.</summary>
    Task<bool> ConfirmAsync(string titleRu, string messageRu, string confirmLabelRu, string cancelLabelRu);

    /// <summary>Сообщение без выбора: «готово», «не вышло», «служба недоступна».</summary>
    Task InformAsync(string titleRu, string messageRu);

    /// <summary>Предложить точку восстановления Windows перед изменениями.</summary>
    Task<bool> OfferRestorePointAsync(int tweakCount, string presetTitleRu);
}

/// <summary>
/// Настройки приложения на диске. Файл лежит рядом с базой, а окно настроек показывает его путь —
/// по прямому требованию: конфиг должен быть виден в разделе «Настройки», а не спрятан.
/// </summary>
public interface IAppSettingsStore
{
    string SettingsFilePath { get; }

    AppSettings Load();

    void Save(AppSettings settings);
}

/// <summary>
/// Настройки, которые пользователь меняет в окне. Всё остальное — в каталоге твиков и профилях потребления.
///
/// Узлы маршрутизации (задача B) живут здесь же, в общем settings.json: файл один на окно и службу,
/// служба читает его терпимо. Пустой список — честный отказ действия «маршрут для игры», а не догадка.
/// </summary>
public sealed record AppSettings(
    string PerfProfile = "balanced",
    bool OfferRestorePoint = true,
    bool StartMinimized = false,
    bool CloseToTray = true,
    int JournalRowsShown = 200,
    bool SetupCompleted = false,
    string SetupAnswersRu = "",
    IReadOnlyDictionary<string, string>? GamePaths = null,
    IReadOnlyList<CustomGameEntry>? CustomGames = null,
    IReadOnlyList<RoutingNodeEntry>? RoutingNodes = null,
    string? RoutingDefaultNode = null,
    bool RoutingAutoSelect = false,
    bool RoutingMultipathEnabled = false,
    string? RoutingSecondaryNode = null);

/// <summary>Память вместо файла: так проверяются «сохранилось / не сохранилось» без диска.</summary>
public sealed class InMemorySettingsStore : IAppSettingsStore
{
    private AppSettings _settings;

    public InMemorySettingsStore(AppSettings? initial = null, string path = @"C:\ProgramData\PingForge\settings.json")
    {
        _settings = initial ?? new AppSettings();
        SettingsFilePath = path;
    }

    public string SettingsFilePath { get; }

    public int SaveCount { get; private set; }

    public AppSettings Load() => _settings;

    public void Save(AppSettings settings)
    {
        _settings = settings;
        SaveCount++;
    }
}
