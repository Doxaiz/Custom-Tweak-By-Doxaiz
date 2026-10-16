using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Probing;

namespace PingForge.Analysis;

/// <summary>Оценка пути до игрового сервера: те же метрики, что показывает вкладка «Замеры».</summary>
public sealed record PathQualitySummary(
    string RouteLabel,
    double AverageRttMs,
    double JitterMs,
    double PacketLossPercent,
    double StabilityScore,
    bool HasEnoughSamples,
    string SummaryRu)
{
    public static PathQualitySummary NotMeasured(string reason) =>
        new(reason, 0, 0, 0, 0, false, "Путь до игрового сервера ещё не измерялся.");
}

public interface IPathQualityProbe
{
    Task<PathQualitySummary?> TryMeasureAsync(CancellationToken ct = default);
}

/// <summary>
/// Полный диагноз: состояние Windows и сети + предупреждения о том, что мешает,
/// + разбор каталога твиков. Это единственный источник данных для вкладки «Анализ».
/// </summary>
public sealed class NetworkAnalysisService : INetworkSnapshot
{
    private readonly ISystemStateReader _stateReader;
    private readonly TweakAnalyzer _tweakAnalyzer;
    private readonly IPathQualityProbe? _pathProbe;
    private readonly IActiveAdapterResolver? _adapterResolver;

    public NetworkAnalysisService(
        ISystemStateReader stateReader,
        TweakAnalyzer tweakAnalyzer,
        IPathQualityProbe? pathProbe = null,
        IActiveAdapterResolver? adapterResolver = null)
    {
        _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
        _tweakAnalyzer = tweakAnalyzer ?? throw new ArgumentNullException(nameof(tweakAnalyzer));
        _pathProbe = pathProbe;
        _adapterResolver = adapterResolver;
    }

    public async Task<NetworkAndOsSnapshot> CaptureFullDiagnosticAsync(CancellationToken ct = default)
    {
        SystemStateReading state = await _stateReader.ReadAsync(ct).ConfigureAwait(false);
        var warnings = new List<InterferenceWarning>(BuildWarnings(state));

        TweakTarget? adapterTarget = null;
        if (_adapterResolver is not null)
        {
            try
            {
                TweakTarget resolved = await _adapterResolver.ResolveActiveAdapterAsync(ct).ConfigureAwait(false);
                if (resolved.IsAdapterBound)
                {
                    adapterTarget = resolved;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add(new InterferenceWarning(
                    "ADAPTER_RESOLVE_FAILED",
                    "Активный адаптер не определён",
                    $"Не удалось определить сетевую карту, через которую идёт игровой трафик: {ex.Message}",
                    "Проверьте, что сеть подключена, и повторите анализ.",
                    TweakRiskLevel.Medium));
            }
        }

        if (_pathProbe is not null)
        {
            PathQualitySummary? path = await _pathProbe.TryMeasureAsync(ct).ConfigureAwait(false);
            if (path is not null)
            {
                warnings.AddRange(BuildPathWarnings(path));
            }
        }

        TweakAnalysisReport tweaks = await _tweakAnalyzer.AnalyzeAsync(adapterTarget, ct).ConfigureAwait(false);

        return new NetworkAndOsSnapshot(
            WindowsEditionAndBuild: state.WindowsEditionAndBuild,
            IsRunningAsAdministrator: state.IsRunningAsAdministrator,
            ActiveAdapterName: state.ActiveAdapter?.DisplayName ?? string.Empty,
            ActiveAdapterInterfaceGuid: state.ActiveAdapter?.InterfaceGuid ?? string.Empty,
            ActiveAdapterDescription: state.ActiveAdapter?.DisplayName ?? string.Empty,
            ActiveAdapterDriverVersion: null,
            ConnectionMedium: state.LinkSpeedMbps is null ? "не определено" : $"Ethernet/Wi-Fi ({state.LinkSpeedMbps} Мбит/с)",
            LinkSpeedMbps: state.LinkSpeedMbps,
            CurrentMtuBytes: state.CurrentMtuBytes,
            ActivePowerSchemeGuid: state.PowerSchemeGuid,
            ActivePowerSchemeName: state.PowerSchemeName ?? string.Empty,
            TcpAutoTuningLevel: state.TcpAutoTuningLevel,
            CongestionProvider: state.CongestionProvider,
            IsRssEnabled: state.IsRssEnabled,
            IsRscEnabled: state.IsRscEnabled,
            IsEcnEnabled: state.IsEcnEnabled,
            IsNagleDisabledOnActiveNic: state.IsNagleDisabledOnActiveNic,
            IsGameModeEnabled: state.IsGameModeEnabled,
            IsDeliveryOptimizationP2pEnabled: state.IsDeliveryOptimizationP2pEnabled,
            IsSystemRestoreEnabledOnSystemDrive: state.IsSystemRestoreEnabledOnSystemDrive,
            ConfiguredDnsServers: state.ConfiguredDnsServers,
            AttachedNdisFilters: state.AttachedNdisFilters,
            UnresolvedFields: state.UnresolvedFields,
            Warnings: warnings,
            AlreadyOptimalItems: tweaks.AlreadyOptimal,
            RecommendedToChangeItems: tweaks.RecommendedToChange,
            DoNotTouchItems: tweaks.DoNotTouch,
            AnalysisNotes: tweaks.Notes);
    }

    private static IEnumerable<InterferenceWarning> BuildWarnings(SystemStateReading state)
    {
        if (!state.IsRunningAsAdministrator)
        {
            yield return new InterferenceWarning(
                "NOT_ELEVATED",
                "Программа запущена без прав администратора",
                "Без прав администратора нельзя ни прочитать все настройки, ни применить твики. " +
                "Замеры маршрута при этом сделать можно.",
                "Запустите PingForge от имени администратора.",
                TweakRiskLevel.Low);
        }

        if (state.IsDeliveryOptimizationP2pEnabled == true)
        {
            yield return new InterferenceWarning(
                "DELIVERY_OPTIMIZATION_P2P",
                "Windows раздаёт обновления другим машинам",
                "Оптимизация доставки отдаёт ваши мегабиты в интернет и создаёт фоновую нагрузку на канал " +
                "во время игры — это видно как всплески джиттера.",
                "Твик «Ограничить раздачу обновлений» выключает раздачу, не отключая сами обновления Windows.",
                TweakRiskLevel.Low);
        }

        if (state.IsTimerResolutionRaised == false)
        {
            yield return new InterferenceWarning(
                "SYSTEM_TIMER_LOW",
                "Системный таймер оставлен в режиме энергосбережения",
                "Прерывания обрабатываются реже — при большом числе сетевых пакетов это добавляет джиттер.",
                "План электропитания «Максимальная производительность» или твик системного таймера.",
                TweakRiskLevel.Medium);
        }

        if (state.AttachedNdisFilters.Count > 0)
        {
            yield return new InterferenceWarning(
                "EXTRA_NDIS_FILTERS",
                "На сетевой карте есть сторонние NDIS-фильтры",
                $"Обнаружены фильтры: {string.Join(", ", state.AttachedNdisFilters)}. " +
                "Каждый фильтр обрабатывает каждый пакет до игры — это лишняя задержка.",
                "PingForge не удаляет чужие драйверы. Уберите лишние антивирусные/VPN-фильтры вручную либо " +
                "исключите игру в их настройках.",
                TweakRiskLevel.Medium);
        }

        if (state.IsSystemRestoreEnabledOnSystemDrive == false)
        {
            yield return new InterferenceWarning(
                "NO_RESTORE_POINT",
                "Защита системы отключена — точку восстановления создать не получится",
                "Без точки восстановления откат возможен только средствами самого PingForge (снапшот значений реестра).",
                "Это ваше решение: включать защиту системы или нет. PingForge напомнит ещё раз перед применением.",
                TweakRiskLevel.Low);
        }
    }

    private static IEnumerable<InterferenceWarning> BuildPathWarnings(PathQualitySummary path)
    {
        if (path.PacketLossPercent >= 1.0)
        {
            yield return new InterferenceWarning(
                "PATH_PACKET_LOSS",
                "На пути до игрового сервера есть потери пакетов",
                $"Потери {path.PacketLossPercent:F1}% при среднем RTT {path.AverageRttMs:F1} мс " +
                $"({path.RouteLabel}). Потери на маршруте твиками не лечатся.",
                "Прямое подключение уже испорчено за пределами вашего компьютера. Вариант — релейный путь " +
                "в обход проблемного участка (нужен сервер) либо смена провайдера.",
                TweakRiskLevel.High);
        }
        else if (path.JitterMs >= 8.0)
        {
            yield return new InterferenceWarning(
                "PATH_JITTER",
                "Высокий джиттер на маршруте",
                $"Разброс задержки {path.JitterMs:F1} мс при среднем RTT {path.AverageRttMs:F1} мс " +
                $"({path.RouteLabel}). В игре это ощущается как «резинка».",
                "Часть джиттера уходит после твиков сетевой карты и отключения фоновой нагрузки. " +
                "Остальное — вопрос маршрута.",
                TweakRiskLevel.Medium);
        }

        if (!path.HasEnoughSamples)
        {
            yield return new InterferenceWarning(
                "PATH_TOO_FEW_SAMPLES",
                "Слишком мало замеров для вывода",
                path.SummaryRu,
                $"Нужно минимум {StatisticalMetricsCalculator.MinimumSamplesForFullReport} сэмплов, иначе вывод будет гаданием.",
                TweakRiskLevel.Low);
        }
    }
}
