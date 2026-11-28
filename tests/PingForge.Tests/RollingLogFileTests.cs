using System.Text;
using PingForge.Core.Diagnostics;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Журнал с поворотом: строки не теряются, копии сдвигаются, старые удаляются.
/// </summary>
/// <remarks>
/// Журнал читают люди, когда что-то пошло не так. Ошибка в повороте тихая: файлы на месте,
/// а нужной строки нет — и разбираться не с чем. Поэтому проверяется не «файл создан», а порядок:
/// что уехало в копию, что осталось в текущем, сколько копий держим и что строка никогда не рвётся.
/// </remarks>
public sealed class RollingLogFileTests
{
    private const int MaxBytes = 1024;

    [Fact]
    public void Строки_дописываются_и_читаются_по_русски()
    {
        using var dir = new TempFolder();
        string path = Path.Combine(dir.Path, "app-start.log");
        var log = new RollingLogFile(path, MaxBytes, 3, writeByteOrderMark: true);

        log.Append("первая строка");
        log.Append("вторая строка");

        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);

        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        Assert.Equal(["первая строка", "вторая строка"], lines);
    }

    [Fact]
    public void Достигнув_предела_журнал_поворачивается_и_старое_уезжает_в_копию()
    {
        using var dir = new TempFolder();
        string path = Path.Combine(dir.Path, "service.log");
        var log = new RollingLogFile(path, MaxBytes, 3);
        string longLine = new('ж', 200);

        for (int number = 1; number <= 6; number++)
        {
            log.Append($"строка {number} {longLine}");
        }

        string archive = log.ArchivePath(1);
        Assert.True(File.Exists(archive), "после превышения предела должна появиться копия .1");

        // Старые строки не потерялись: они уехали в копии, пусть и не в первую.
        string allArchives = string.Join("\n", Directory.GetFiles(dir.Path, "*.log.*")
            .Select(file => File.ReadAllText(file, Encoding.UTF8)));
        Assert.Contains("строка 1 ", allArchives, StringComparison.Ordinal);

        // Текущий файл начат заново: в нём последние строки, а не всё подряд.
        string current = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("строка 6", current, StringComparison.Ordinal);
        Assert.DoesNotContain("строка 1 ", current, StringComparison.Ordinal);
    }

    [Fact]
    public void Копий_держится_не_больше_предела_и_самая_старая_удаляется()
    {
        using var dir = new TempFolder();
        string path = Path.Combine(dir.Path, "service.log");
        var log = new RollingLogFile(path, MaxBytes, 2);
        string longLine = new('ж', 400);

        for (int number = 1; number <= 12; number++)
        {
            log.Append($"строка {number} {longLine}");
        }

        string[] files = Directory.GetFiles(dir.Path);
        Assert.True(files.Length <= 2, $"файлов {files.Length}, а держим не больше двух: {string.Join(", ", files.Select(Path.GetFileName))}");

        // Самая старая копия удалена: свежие строки на месте, древних нет.
        Assert.False(File.Exists(log.ArchivePath(2)), "копия .2 при пределе в два файла остаться не должна");
        Assert.Contains("строка 12", File.ReadAllText(path, Encoding.UTF8), StringComparison.Ordinal);
    }

    [Fact]
    public void Поворот_не_рвёт_строку()
    {
        using var dir = new TempFolder();
        string path = Path.Combine(dir.Path, "app-start.log");
        var log = new RollingLogFile(path, MaxBytes, 3, writeByteOrderMark: true);
        string longLine = new('ж', 300);

        for (int number = 1; number <= 8; number++)
        {
            log.Append($"целая строка {number} {longLine}");
        }

        foreach (string file in Directory.GetFiles(dir.Path))
        {
            string text = File.ReadAllText(file, Encoding.UTF8);
            Assert.EndsWith(Environment.NewLine, text, StringComparison.Ordinal);
            Assert.DoesNotContain("целая стро", text.Replace("целая строка", "\u0001", StringComparison.Ordinal), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Неудача_записи_видна_вызывающему_и_не_глотается_молча()
    {
        using var dir = new TempFolder();
        // Место журнала занято папкой: записать строку некуда. Ошибку видит вызывающий —
        // окно и служба уже сами решают, что сказать человеку (и обе это делают).
        string path = Path.Combine(dir.Path, "занято.log");
        Directory.CreateDirectory(path);
        var log = new RollingLogFile(path, MaxBytes, 3);

        Exception? caught = Record.Exception(() => log.Append("эта строка не запишется"));

        Assert.NotNull(caught);
        Assert.True(
            caught is IOException or UnauthorizedAccessException,
            $"ожидалась ошибка ввода-вывода или прав, а пришла {caught.GetType().Name}: {caught.Message}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Пустой_путь_отвергается(string path)
    {
        Assert.Throws<ArgumentException>(() => new RollingLogFile(path));
    }

    [Fact]
    public void Пределы_размеров_проверяются_на_входе()
    {
        using var dir = new TempFolder();
        string path = Path.Combine(dir.Path, "app-start.log");

        Assert.Throws<ArgumentOutOfRangeException>(() => new RollingLogFile(path, maxBytes: 512));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RollingLogFile(path, maxFiles: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RollingLogFile(path).ArchivePath(0));
    }

    /// <summary>Временная папка, которая убирает за собой всё, что в ней появилось.</summary>
    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pingforge-log-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

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
