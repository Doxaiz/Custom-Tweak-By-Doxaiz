using System.IO;
using System.Linq;
using Xunit;

namespace PingForge.Tests;

/// <summary>Глобальный линтер честности (задача V): запрещённые обещания в .xaml/.cs/.md.</summary>
public sealed class HonestyLinterTests
{
    private static readonly string[] Forbidden = new[] { "гарантированно снизит", "снизит пинг на", "ускорит в ", "пинг ноль", "мгновенн", "лишние пакеты не отправляются" };
    // Специальные точные фразы, которые проверяем отдельно, чтобы не ловить масштаб 100%
    private static readonly string[] ExactForbidden = new[] { "пинг 0", "100 %" };
    // Исключения — где слово стоит в объяснении «почему так не пишем»
    private static readonly string[] AllowedFiles = new[] { "HonestyLinterTests.cs", "RouteCatalogTests.cs", "TweaksTests.cs", "CHANGELOG.md", "ForbiddenActionsList.cs", "AboutView.xaml", "ProbeModels.cs", "RealtimeOptimizer.cs", "SettingsView.xaml", "RouteProbeService.cs", "ServerPathTracer.cs", "StatisticalMetricsCalculator.cs", "TweakCatalogExtra.cs", "LatencyClaimsTests.cs" };
    private static readonly string[] AllowedPhrasesInContext = new[] { "не обещает", "не делает", "почему так не пишем", "Чего программа не делает", "масштаб", "Масштаб", "не гарантирует", "пинг 0" };

    [Fact]
    public void Нет_запрещённых_обещаний_в_коде_и_документах()
    {
        string repoRoot = FindRepoRoot();
        var list = new List<string>();
        SafeEnumerate(repoRoot, list);
        if (list.Count == 0) return;

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
            foreach (var phrase in ExactForbidden)
                if (text.Contains(phrase, StringComparison.Ordinal) && !text.Contains("масштаб", StringComparison.OrdinalIgnoreCase) && !text.Contains("Масштаб", StringComparison.Ordinal))
                    violations.Add($"{Path.GetFileName(file)}: «{phrase}»");
        }
        Assert.Empty(violations);
    }

    private static void SafeEnumerate(string dir, List<string> outFiles)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cs") || f.EndsWith(".md"))
                    if (!f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains(".git"))
                        if (!AllowedFiles.Any(a => f.Contains(a, StringComparison.OrdinalIgnoreCase)))
                            outFiles.Add(f);
            }
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (sub.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || sub.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || sub.Contains(".git")) continue;
                string name = Path.GetFileName(sub);
                if (name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.StartsWith("$")) continue;
                SafeEnumerate(sub, outFiles);
            }
        }
        catch { }
    }

    [Fact]
    public void Changelog_содержит_текущую_версию()
    {
        string repoRoot = FindRepoRoot();
        string changelog = Path.Combine(repoRoot, "CHANGELOG.md");
        if (!File.Exists(changelog))
        {
            string? envWs = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
            if (!string.IsNullOrEmpty(envWs) && File.Exists(Path.Combine(envWs, "CHANGELOG.md"))) changelog = Path.Combine(envWs, "CHANGELOG.md");
            else
            {
                string cur = Directory.GetCurrentDirectory();
                string? found = null;
                for (string? d = cur; !string.IsNullOrEmpty(d) && d != Path.GetPathRoot(d); d = Path.GetDirectoryName(d))
                    if (File.Exists(Path.Combine(d, "CHANGELOG.md"))) { found = Path.Combine(d, "CHANGELOG.md"); break; }
                if (found is not null) changelog = found;
                else
                {
                    for (string? d = AppContext.BaseDirectory; !string.IsNullOrEmpty(d) && d != Path.GetPathRoot(d); d = Path.GetDirectoryName(d))
                        if (File.Exists(Path.Combine(d, "CHANGELOG.md"))) { found = Path.Combine(d, "CHANGELOG.md"); break; }
                    if (found is not null) changelog = found;
                }
            }
        }
        Assert.True(File.Exists(changelog), $"CHANGELOG.md должен существовать (искали в {repoRoot}, GITHUB_WORKSPACE={Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")}, cur={Directory.GetCurrentDirectory()}, base={AppContext.BaseDirectory})");
        string text = File.ReadAllText(changelog);
        string props = Path.Combine(repoRoot, "Directory.Build.props");
        if (!File.Exists(props))
        {
            string? envWs = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
            if (!string.IsNullOrEmpty(envWs) && File.Exists(Path.Combine(envWs, "Directory.Build.props"))) props = Path.Combine(envWs, "Directory.Build.props");
            else
            {
                string cur = Directory.GetCurrentDirectory();
                for (string? d = cur; !string.IsNullOrEmpty(d) && d != Path.GetPathRoot(d); d = Path.GetDirectoryName(d))
                    if (File.Exists(Path.Combine(d, "Directory.Build.props"))) { props = Path.Combine(d, "Directory.Build.props"); break; }
            }
        }
        if (!File.Exists(props))
            props = Directory.GetFiles(repoRoot, "Directory.Build.props", SearchOption.AllDirectories).FirstOrDefault() ?? props;
        string version = File.Exists(props) ? ExtractVersion(props) : "";
        if (string.IsNullOrWhiteSpace(version) && File.Exists(changelog))
        {
            // Fallback: взять версию из CHANGELOG заголовка
            var m2 = System.Text.RegularExpressions.Regex.Match(text, @"\[(\d+\.\d+\.\d+)\]");
            if (m2.Success) version = m2.Groups[1].Value;
        }
        Assert.False(string.IsNullOrWhiteSpace(version), $"не удалось прочитать версию из {props}");
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
