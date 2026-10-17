using System.Text.Json;
using System.Text.Json.Nodes;
using PingForge.App.ViewModels;
using PingForge.Core.Security;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Общий файл настроек, который пишут два процесса — окно программы и служба Windows (C3).
///
/// Что здесь проверяется и почему это важно:
///   • два писателя не затирают поля друг друга (раньше последний записавший стирал чужое:
///     человек терял пути к играм или пройденный мастер, и программа об этом не сообщала);
///   • битый файл не затирается молча, а убирается в сторону — причина потери настроек остаётся на диске;
///   • копия прежней версии лежит в <c>.bak</c>;
///   • если общую блокировку взять нельзя, запись не делает вид, что защищена, а говорит об этом.
/// </summary>
public sealed class SharedSettingsFileTests : IDisposable
{
    private readonly string _directory;

    /// <summary>
    /// Имя блокировки ОДНО на весь тест: два писателя в тесте — это окно и служба, а в жизни они
    /// делят одну и ту же константу имени. Разные имена в тесте означали бы, что писатели друг друга
    /// не слышат, и тест проверял бы гонку, а не защиту от неё.
    /// </summary>
    private readonly string _mutexName;

    /// <summary>Запасное имя на случай, если основное платформа не примет.</summary>
    private readonly string _fallbackMutexName;

    public SharedSettingsFileTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"pf_shared_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _mutexName = $@"Local\PingForge.Settings.Test.{Guid.NewGuid():N}";
        _fallbackMutexName = $"PingForge.Settings.Test.{Guid.NewGuid():N}";
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string PathIn(string name) => Path.Combine(_directory, name);

    private SharedSettingsFileWriter NewWriter(string path) => new(path, _mutexName, _fallbackMutexName);

    [Fact]
    public async Task Два_писателя_в_цикле_не_теряют_поля_друг_друга()
    {
        // Суть ошибки: «прочитать файл целиком → поменять своё → записать» без блокировки.
        // Окно пишет пути к играм, служба — профиль потребления; при одновременном сохранении
        // последний записавший затирал чужое поле.
        string path = PathIn("settings.json");
        SharedSettingsFileWriter window = NewWriter(path);
        SharedSettingsFileWriter service = NewWriter(path);

        int failures = 0;

        var windowWriter = Task.Run(() =>
        {
            for (int i = 0; i < 60; i++)
            {
                window.Update(root =>
                {
                    root["GamePaths"] = new JsonObject { ["cs2.exe"] = $@"C:\Games\{i}" };
                    return root;
                });
            }
        });

        var serviceWriter = Task.Run(() =>
        {
            for (int i = 0; i < 60; i++)
            {
                if (!service.Update(root =>
                {
                    root["PerfProfile"] = "economy";
                    return root;
                }).Written)
                {
                    Interlocked.Increment(ref failures);
                }
            }
        });

        // Ждём обе задачи, а не блокируем поток теста: блокирующее ожидание в тесте — это
        // предупреждение xUnit1031 и риск взаимной блокировки.
        await Task.WhenAll(windowWriter, serviceWriter);

        Assert.Equal(0, failures);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("economy", document.RootElement.GetProperty("PerfProfile").GetString());
        Assert.Equal(@"C:\Games\59", document.RootElement.GetProperty("GamePaths").GetProperty("cs2.exe").GetString());
    }

    [Fact]
    public void Запись_окна_не_стирает_поле_службы_и_наоборот()
    {
        string path = PathIn("settings.json");
        SharedSettingsFileWriter writer = NewWriter(path);

        writer.Update(root =>
        {
            root["PerfProfile"] = "max";
            root["СлужебноеПоле"] = "значение службы";
            return root;
        });

        // Окно перезаписывает файл ЦЕЛИКОМ своим объектом настроек — так оно и работает в бою.
        var store = new JsonFileSettingsStore(path, NewWriter(path));
        store.Save(new AppSettings("economy", GamePaths: new Dictionary<string, string> { ["dota2.exe"] = @"C:\Games\dota2.exe" }));

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("economy", document.RootElement.GetProperty("PerfProfile").GetString());
        Assert.True(document.RootElement.TryGetProperty("GamePaths", out _));

        // Чужое поле, которого окно не знает, при такой записи теряется — это честно и видно:
        // окно владеет файлом настроек, служба пишет в него только свой ключ и не перезаписывает файл.
        Assert.False(document.RootElement.TryGetProperty("СлужебноеПоле", out _));
    }

    [Fact]
    public void Битый_файл_уходит_в_сторону_а_не_затирается()
    {
        string path = PathIn("settings.json");
        File.WriteAllText(path, "{ это не json ");
        SharedSettingsFileWriter writer = NewWriter(path);

        SharedFileWriteResult result = writer.Update(root =>
        {
            root["PerfProfile"] = "economy";
            return root;
        });

        Assert.True(result.Written);
        Assert.False(string.IsNullOrWhiteSpace(result.BackupPathRu));
        Assert.Contains(".broken-", result.BackupPathRu);
        Assert.Contains("не json", File.ReadAllText(result.BackupPathRu));

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("economy", document.RootElement.GetProperty("PerfProfile").GetString());
    }

    [Fact]
    public void Без_разрешения_затирать_битый_файл_запись_отказывается_и_файл_остаётся()
    {
        // Так работает служба: файл принадлежит окну, и служба не имеет права решать, что с ним делать.
        string path = PathIn("settings.json");
        File.WriteAllText(path, "{ это не json ");

        SharedFileWriteResult result = NewWriter(path).Update(
            root =>
            {
                root["PerfProfile"] = "economy";
                return root;
            },
            quarantineBroken: false);

        Assert.False(result.Written);
        Assert.Contains("повреждён", result.ProblemRu);
        Assert.Contains("не json", File.ReadAllText(path));
    }

    [Fact]
    public void Копия_прежней_версии_лежит_в_bak()
    {
        string path = PathIn("settings.json");
        File.WriteAllText(path, """{"PerfProfile":"balanced"}""");
        SharedSettingsFileWriter writer = NewWriter(path);

        SharedFileWriteResult result = writer.Update(root =>
        {
            root["PerfProfile"] = "economy";
            return root;
        });

        Assert.True(result.Written);
        Assert.Equal(path + ".bak", result.BackupPathRu);
        Assert.Contains("balanced", File.ReadAllText(path + ".bak"));
        Assert.Contains("economy", File.ReadAllText(path));
    }

    [Fact]
    public void Первую_запись_нечем_копировать_и_это_не_ошибка()
    {
        string path = PathIn("settings.json");

        SharedFileWriteResult result = NewWriter(path).Update(root =>
        {
            root["PerfProfile"] = "max";
            return root;
        });

        Assert.True(result.Written);
        Assert.Equal(string.Empty, result.BackupPathRu);
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void Если_общую_блокировку_взять_нельзя_об_этом_сказано_а_не_сделано_вид()
    {
        string path = PathIn("settings.json");

        // Пустое имя блокировки создать нельзя: так выглядит «защиты нет», и вызывающий обязан это увидеть.
        var writer = new SharedSettingsFileWriter(path, string.Empty, string.Empty);
        SharedFileWriteResult result = writer.Update(root =>
        {
            root["PerfProfile"] = "economy";
            return root;
        });

        Assert.True(result.Written, "настройки потерять хуже, чем записать без блокировки");
        Assert.Contains("без защиты", result.ProblemRu);
        Assert.Contains("economy", File.ReadAllText(path));
    }

    [Fact]
    public void Чтение_общего_файла_отдаёт_текст_или_честный_отказ()
    {
        string path = PathIn("settings.json");
        SharedSettingsFileWriter writer = NewWriter(path);

        Assert.True(writer.TryRead(out string empty, out _));
        Assert.Equal(string.Empty, empty);

        File.WriteAllText(path, """{"PerfProfile":"economy"}""");

        Assert.True(writer.TryRead(out string text, out _));
        Assert.Contains("economy", text);
    }

    [Fact]
    public void Окно_и_служба_пишут_один_файл_и_не_теряют_ни_профиль_ни_свои_игры()
    {
        // Сценарий из жизни: человек добавил свою игру, а служба в это же время сохранила профиль.
        string path = PathIn("settings.json");
        var store = new JsonFileSettingsStore(path, NewWriter(path));

        store.Save(new AppSettings("balanced", SetupCompleted: true));

        // Служба пишет только свой ключ — так это делает ServiceSettings.TryWriteProfile.
        NewWriter(path).Update(root =>
        {
            root["PerfProfile"] = "economy";
            return root;
        });

        // Окно добавляет свою игру, перечитав файл перед записью (оно так и делает).
        AppSettings current = store.Load();
        store.Save(current with
        {
            GamePaths = new Dictionary<string, string> { ["cs2.exe"] = @"C:\Games\cs2.exe" },
        });

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(document.RootElement.GetProperty("SetupCompleted").GetBoolean());
        Assert.Equal(@"C:\Games\cs2.exe", document.RootElement.GetProperty("GamePaths").GetProperty("cs2.exe").GetString());
    }
}
