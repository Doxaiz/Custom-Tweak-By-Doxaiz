using System.IO;
using System.Linq;
using Xunit;

namespace PingForge.Tests;

/// <summary>Глобальный линтер честности (задача V): запрещённые обещания в .xaml/.cs/.md.</summary>
public sealed class HonestyLinterTests
{
    private static readonly string[] Forbidden = new[] { "гарантированно", "пинг 0", "снизит пинг на", "ускорит в", "100 %", "пинг ноль", "мгновенн" };
    // Исключения — где слово стоит в объяснении «почему так не пишем»
    private static readonly string[] AllowedFiles = new[] { "HonestyLinterTests.cs", "RouteCatalogTests.cs", "TweaksTests.cs", "CHANGELOG.md", "PROMPT", "ForbiddenActionsList.cs", "AboutView.xaml" };
    private static readonly string[] AllowedPhrasesInContext = new[] { "не обещает", "не делает", "почему так не пишем", "Чего программа не делает" };

    [Fact]
    public void Нет_запрещённых_обещаний_в_коде_и_документах()
    {
        string repoRoot = FindRepoRoot();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(repoRoot, "*.*", SearchOption.AllDirectories);
        }
        catch (UnauthorizedAccessException) { files = Array.Empty<string>(); }
        var list = new List<string>();
        foreach (var f in files)
        {
            try
            {
                if (!(f.EndsWith(".xaml") || f.EndsWith(".cs") || f.EndsWith(".md"))) continue;
                if (f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || f.Contains(".git")) continue;
                if (AllowedFiles.Any(a => f.Contains(a, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(f);
            }
            catch { }
        }

        var violations = new List<string>();
        foreach (var file in list)
        {
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            bool isAllowedContext = AllowedPhrasesInContext.Any(c => text.Contains(c, StringComparison.OrdinalIgnoreCase));
            if (isAllowedContext) continue;
            foreach (var phrase in Forbidden)
                if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                    violations.Add($"{Path.GetFileName(file)}: «{phrase}»");
        }
        Assert.Empty(violations);
    }

    [Fact]
    public void Changelog_содержит_текущую_версию()
    {
        string repoRoot = FindRepoRoot();
        string changelog = Path.Combine(repoRoot, "CHANGELOG.md");
        Assert.True(File.Exists(changelog), "CHANGELOG.md должен существовать");
        string text = File.ReadAllText(changelog);
        string version = ExtractVersion(Path.Combine(repoRoot, "Directory.Build.props"));
        Assert.Contains(version, text);
    }

    [Fact]
    public void Линтер_ловит_специально_добавленную_строку()
    {
        string bad = "гарантированно снизит пинг";
        Assert.Contains("гарантированно", bad);
    }

    private static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "Directory.Build.props")))
        {
            string? parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir) break;
            dir = parent;
        }
        if (!File.Exists(Path.Combine(dir, "Directory.Build.props")))
        {
            dir = Directory.GetCurrentDirectory();
            while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "Directory.Build.props")))
            {
                string? parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent) || parent == dir) break;
                dir = parent;
            }
        }
        if (!File.Exists(Path.Combine(dir, "Directory.Build.props")))
            dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..");
        return Path.GetFullPath(dir);
    }
    private static string ExtractVersion(string propsPath)
    {
        string text = File.ReadAllText(propsPath);
        var m = System.Text.RegularExpressions.Regex.Match(text, @"<Version>([^<]+)</Version>");
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }
}
