using System.Text;
using PingForge.App.ViewModels;
using PingForge.Network;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Выбор файла игры: не только .exe, но и ярлык с рабочего стола или файл ссылки.
/// </summary>
/// <remarks>
/// Требование жизни: человек не обязан помнить, где лежит исполняемый файл игры — на рабочем столе
/// у него ярлык. Программа обязана найти по ярлыку настоящий .exe, а если не может — сказать словами,
/// что именно не так, и не добавлять игру «наугад»: иначе оптимизация ищет процесс, которого не будет.
/// </remarks>
public sealed class GameFilePickerTests
{
    [Fact]
    public void Exe_принимается_как_есть()
    {
        string exe = Path.Combine("/games", "MyGame.exe");

        Assert.True(GameExecutableResolver.TryResolve(exe, _ => true, out string resolved, out string note, out string error), error);
        Assert.Equal(exe, resolved);
        Assert.Equal(string.Empty, note);
    }

    [Fact]
    public void Ярлык_ведёт_на_игру_и_путь_находится()
    {
        using var dir = new TempFolder();
        string exe = dir.CreateFile("MyGame.exe");
        string shortcut = dir.CreateShortcut("MyGame", exe);

        Assert.True(GameExecutableResolver.TryResolve(shortcut, File.Exists, out string resolved, out string note, out string error), error);
        Assert.Equal(Path.GetFileName(exe), Path.GetFileName(resolved));
        Assert.Contains("Ярлык ведёт на", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Ярлык_с_кириллицей_в_пути_тоже_разбирается()
    {
        using var dir = new TempFolder();
        string exe = dir.CreateFile(Path.Combine("Игры", "МояИгра.exe"));
        string shortcut = dir.CreateShortcut("Моя игра", exe);

        Assert.True(GameExecutableResolver.TryResolve(shortcut, File.Exists, out string resolved, out _, out string error), error);
        Assert.Equal(Path.GetFileName(exe), Path.GetFileName(resolved));
        Assert.Contains("Игры", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void Ярлык_на_удалённую_игру_честно_отказывает()
    {
        using var dir = new TempFolder();
        string gone = Path.Combine(dir.Path, "Deleted", "GoneGame.exe");
        string shortcut = dir.CreateShortcut("GoneGame", gone);

        Assert.False(GameExecutableResolver.TryResolve(shortcut, File.Exists, out string resolved, out _, out string error));
        Assert.Equal(string.Empty, resolved);
        Assert.Contains("на диске нет", error, StringComparison.Ordinal);
        Assert.Contains("GoneGame.exe", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Ярлык_без_пути_к_программе_объясняет_что_делать()
    {
        using var dir = new TempFolder();
        string shortcut = Path.Combine(dir.Path, "Пустой.lnk");
        // Пустой ярлык: в файле нет ни одного пути к .exe — выдумывать его программа не станет.
        File.WriteAllBytes(shortcut, Encoding.ASCII.GetBytes("L\0\0\0some icon.ico\0"));

        Assert.False(GameExecutableResolver.TryResolve(shortcut, File.Exists, out _, out _, out string error));
        Assert.Contains("не записан путь", error, StringComparison.Ordinal);
        Assert.Contains("Расположение файла", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Ссылка_на_файл_даёт_путь_к_игре()
    {
        using var dir = new TempFolder();
        string url = Path.Combine(dir.Path, "Игра.url");
        File.WriteAllText(url, "[InternetShortcut]\r\nURL=file:///C:/Games/MyGame.exe\r\n");

        Assert.True(GameExecutableResolver.TryResolve(url, File.Exists, out string resolved, out string note, out string error), error);
        Assert.EndsWith("MyGame.exe", resolved, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ссылка ведёт на", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Ссылка_на_сайт_отказывает()
    {
        using var dir = new TempFolder();
        string url = Path.Combine(dir.Path, "Сайт.url");
        File.WriteAllText(url, "[InternetShortcut]\r\nURL=https://example.com/play\r\n");

        Assert.False(GameExecutableResolver.TryResolve(url, File.Exists, out _, out _, out string error));
        Assert.Contains("не на файл игры", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Текстовый_файл_отказывает_и_подсказывает_ярлык()
    {
        using var dir = new TempFolder();
        string notes = dir.CreateFile("заметки.txt");

        Assert.False(GameExecutableResolver.TryResolve(notes, File.Exists, out _, out _, out string error));
        Assert.Contains("не программа и не ярлык", error, StringComparison.Ordinal);
        Assert.Contains("ярлык с рабочего стола", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Ярлык_на_скрипт_объясняет_что_это_скрипт()
    {
        using var dir = new TempFolder();
        string script = dir.CreateFile("start.bat");
        string shortcut = dir.CreateShortcut("Моя игра", script);

        Assert.False(GameExecutableResolver.TryResolve(shortcut, File.Exists, out _, out _, out string error));
        Assert.Contains("это скрипт, а не игра", error, StringComparison.Ordinal);
        Assert.Contains("start.bat", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Ярлык_на_ярлык_доходит_до_игры()
    {
        using var dir = new TempFolder();
        string exe = dir.CreateFile("MyGame.exe");
        string inner = dir.CreateShortcut("MyGame", exe);
        string outer = dir.CreateShortcut("Игра на рабочем столе", inner);

        Assert.True(GameExecutableResolver.TryResolve(outer, File.Exists, out string resolved, out string note, out string error), error);
        Assert.Equal(Path.GetFileName(exe), Path.GetFileName(resolved));
        Assert.Contains("Ярлык ведёт на", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Круг_ярлыков_останавливается_с_объяснением()
    {
        using var dir = new TempFolder();
        string first = Path.Combine(dir.Path, "Первый.lnk");
        string second = Path.Combine(dir.Path, "Второй.lnk");
        dir.WriteShortcutAt(first, second);
        dir.WriteShortcutAt(second, first);

        Assert.False(GameExecutableResolver.TryResolve(first, File.Exists, out _, out _, out string error));
        Assert.Contains("Цепочка ярлыков", error, StringComparison.Ordinal);
        Assert.Contains("Расположение файла", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Установщик_объясняет_что_он_установщик()
    {
        using var dir = new TempFolder();
        string installer = dir.CreateFile("MyGame-Setup.msi");

        Assert.False(GameExecutableResolver.TryResolve(installer, File.Exists, out _, out _, out string error));
        Assert.Contains("установщик, а не игра", error, StringComparison.Ordinal);
        Assert.Contains("потом укажите её .exe", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Архив_объясняет_что_его_надо_распаковать()
    {
        using var dir = new TempFolder();
        string archive = dir.CreateFile("MyGame-portable.zip");

        Assert.False(GameExecutableResolver.TryResolve(archive, File.Exists, out _, out _, out string error));
        Assert.Contains("архив с игрой", error, StringComparison.Ordinal);
        Assert.Contains("Распакуйте", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Скрипт_выбранный_напрямую_отказывает()
    {
        using var dir = new TempFolder();
        string script = dir.CreateFile("run.cmd");

        Assert.False(GameExecutableResolver.TryResolve(script, File.Exists, out _, out _, out string error));
        Assert.Contains("это скрипт, а не игра", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Ссылка_на_скрипт_отказывает_по_той_же_причине()
    {
        using var dir = new TempFolder();
        string script = dir.CreateFile(Path.Combine("launcher", "start.bat"));
        string url = Path.Combine(dir.Path, "Запуск.url");
        File.WriteAllText(url, "[InternetShortcut]\r\nURL=file:///" + script.Replace('\\', '/') + "\r\n");

        Assert.False(GameExecutableResolver.TryResolve(url, File.Exists, out _, out _, out string error));
        Assert.Contains("это скрипт, а не игра", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Добавление_ярлыка_добавляет_игру_по_настоящему_exe()
    {
        using var dir = new TempFolder();
        string exe = dir.CreateFile("MyGame.exe");
        string shortcut = dir.CreateShortcut("Моя игра", exe);
        (GameLauncherViewModel launcher, InMemorySettingsStore store) = BuildLauncher(shortcut);

        await launcher.AddGameFromPickerAsync();

        GameCardViewModel card = Assert.Single(launcher.Cards, c => c.IsCustom);
        Assert.Equal("mygame.exe", card.ProcessName);
        Assert.Equal("Моя игра", card.TitleRu);
        Assert.Contains("Ярлык ведёт на", launcher.StatusRu, StringComparison.Ordinal);
        Assert.Contains("когда игра запустится", launcher.StatusRu, StringComparison.Ordinal);
        Assert.Equal("mygame.exe", Assert.Single(store.Load().CustomGames!).ProcessName);
    }

    [Fact]
    public async Task Выбор_запущенной_игры_говорит_что_процесс_уже_виден()
    {
        using var dir = new TempFolder();
        string exe = dir.CreateFile("MyGame.exe");
        (GameLauncherViewModel launcher, _) = BuildLauncher(exe, running: true);

        await launcher.AddGameFromPickerAsync();

        Assert.Contains("уже запущен", launcher.StatusRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Текстовый_файл_не_становится_игрой()
    {
        using var dir = new TempFolder();
        string notes = dir.CreateFile("заметки.txt");
        (GameLauncherViewModel launcher, InMemorySettingsStore store) = BuildLauncher(notes);

        await launcher.AddGameFromPickerAsync();

        Assert.DoesNotContain(launcher.Cards, card => card.IsCustom);
        Assert.Null(store.Load().CustomGames);
        Assert.Contains("не программа и не ярлык", launcher.StatusRu, StringComparison.Ordinal);
    }

    private static (GameLauncherViewModel Launcher, InMemorySettingsStore Store) BuildLauncher(string pickedPath, bool running = false)
    {
        var store = new InMemorySettingsStore();
        var main = new MainViewModel(new EngineClient(new FakeEngineChannel()), new FakeDialogs());
        var launcher = new GameLauncherViewModel(
            new EmptyProbe(), new Scripted(running), new FixedPicker(pickedPath), store, main);
        return (launcher, store);
    }

    private sealed class EmptyProbe : IGameExecutableProbe
    {
        public IReadOnlyDictionary<string, string> FindInstalled(IReadOnlyCollection<string> processNames) =>
            new Dictionary<string, string>();
    }

    private sealed class Scripted(bool running) : IGameStarter
    {
        public bool TryStart(string executablePath, out string errorRu)
        {
            errorRu = string.Empty;
            return true;
        }

        public bool IsRunning(string processName) => running;
    }

    private sealed class FixedPicker(string path) : IExecutablePicker
    {
        public string? Pick(string titleRu) => path;
    }

    /// <summary>Временная папка с файлами и настоящими по форме ярлыками.</summary>
    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pingforge-pick-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateFile(string relativeName)
        {
            string full = System.IO.Path.Combine(Path, relativeName);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "не настоящая программа: тест не запускает игры");
            return full;
        }

        /// <summary>
        /// Ярлык по форме Windows: путь цели лежит и байтами, и в UTF-16 — как в настоящем .lnk.
        /// Содержимое нарочно не минимально: парсер обязан читать файл, а не угадывать по имени.
        /// </summary>
        public string CreateShortcut(string name, string target) =>
            WriteShortcutAt(System.IO.Path.Combine(Path, name + ".lnk"), target);

        /// <summary>Ярлык по заранее названному пути — так проверяется цепочка ярлыков и круг.</summary>
        public string WriteShortcutAt(string shortcut, string target)
        {
            var bytes = new List<byte>();
            bytes.AddRange(Encoding.ASCII.GetBytes("L\0\0\0"));
            bytes.AddRange(new byte[] { 0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00 });
            bytes.AddRange(Encoding.ASCII.GetBytes("C:\\Windows\\system32\\shell32.dll\0"));
            bytes.AddRange(Encoding.ASCII.GetBytes(target));
            bytes.Add(0);
            bytes.AddRange(Encoding.Unicode.GetBytes(target));
            bytes.Add(0);
            bytes.Add(0);
            File.WriteAllBytes(shortcut, bytes.ToArray());
            return shortcut;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Временная папка, которую не дали убрать, — не повод валить тест.
            }
        }
    }
}
