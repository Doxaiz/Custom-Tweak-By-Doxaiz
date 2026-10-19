using System.Net;
using PingForge.Analysis;
using PingForge.App.ViewModels;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Network.Windows;
using PingForge.Probing;
using PingForge.Tweaks;
using PingForge.Tweaks.Windows;

namespace PingForge.App;

/// <summary>
/// Сборка анализа для окна (задача F): готовый код PingForge.Analysis получает настоящие порты
/// и подключается к интерфейсу. До этого советники были «мёртвым» кодом — написаны, покрыты
/// тестами и не использовались ни окном, ни службой.
///
/// Что здесь честно:
///   • адрес игрового сервера для совета по MTU берётся из живой сессии оптимизации (что показано
///     на главной кнопке), а не «из интернета» — нет сессии, нет совета, есть честный отказ;
///   • оба анализа запускаются ТОЛЬКО по кнопке: чтение и замеры по нажатию, ничего фонового;
///   • ни один из этих классов не применяет изменения — это только чтение и измерение.
/// </summary>
internal static class AnalysisWiring
{
    /// <summary>Собрать всё, что нужно карточке «Задержка ввода» в разделе «Твики».</summary>
    public static IInputLatencyAnalysis BuildInputLatency(WindowsTweakPlatform platform, TweakCatalog catalog) =>
        new AdvisorInputLatencyAnalysis(new InputLatencyAdvisor(
            new SystemNetworkContextReader(),
            new WindowsPeripheralInventoryReader(platform.Command),
            catalog));

    /// <summary>Собрать блок «Проверка настроек» в разделе «Настройки».</summary>
    public static ISettingsCheckAnalysis BuildSettingsCheck(
        WindowsTweakPlatform platform,
        TweakCatalog catalog,
        Func<string?> currentEndpoint)
    {
        var networkAnalyzer = new NetworkAnalyzer(
            new MtuAdviceProvider(
                new DfMtuDiscoveryService(new SystemNetIcmpProbe()),
                new LiveSessionEndpointProvider(currentEndpoint),
                new SystemNetworkContextReader()),
            // Список резолверов — тот же, что в «Твиках» и «Проверить сеть»: один источник правды.
            new DnsAdviceProvider(new DnsResolverBenchmarkRunner(
                new DnsResolverBenchmarker(new UdpDnsQueryTransport()),
                GameRegionsViewModel.BenchmarkCandidates)),
            new SystemNetworkContextReader(),
            catalog);

        return new AnalyzerSettingsCheckAnalysis(
            networkAnalyzer,
            new TweakAnalyzer(catalog),
            platform.ActiveAdapter);
    }

    /// <summary>Обёртка советника задержки ввода: интерфейс окна + готовый анализатор.</summary>
    private sealed class AdvisorInputLatencyAnalysis(InputLatencyAdvisor advisor) : IInputLatencyAnalysis
    {
        public Task<NetworkAnalysisReport> CheckAsync(CancellationToken ct = default) =>
            advisor.AdviseAsync(ct);
    }

    /// <summary>
    /// Адрес игрового сервера из живой сессии оптимизации: та же строка, что показана на главной
    /// кнопке («34.16.1.5:3074»). Нет сессии или адрес не IPv4 — null, и совет по MTU честно
    /// откажет: «универсального MTU из интернета не существует».
    /// </summary>
    private sealed class LiveSessionEndpointProvider(Func<string?> currentEndpoint) : IGameServerEndpointProvider
    {
        public Task<IPAddress?> TryGetGameServerAddressAsync(CancellationToken ct = default)
        {
            string? endpoint = currentEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return Task.FromResult<IPAddress?>(null);
            }

            // «адрес:порт» — берём часть до двоеточия; без порта — строку целиком.
            string host = endpoint.Contains(':') ? endpoint[..endpoint.LastIndexOf(':')] : endpoint;
            return Task.FromResult<IPAddress?>(IPAddress.TryParse(host, out IPAddress? parsed) ? parsed : null);
        }
    }

    /// <summary>
    /// Проверка настроек: предупреждения сети (NetworkAnalyzer) + разбор каталога (TweakAnalyzer).
    /// Итог честно разделяет «мешает», «стоит изменить» и «не проверено» — «всё хорошо»
    /// звучит только когда данные действительно собраны.
    /// </summary>
    private sealed class AnalyzerSettingsCheckAnalysis(
        NetworkAnalyzer network,
        TweakAnalyzer tweaks,
        IActiveAdapterResolver adapterResolver) : ISettingsCheckAnalysis
    {
        public async Task<SettingsCheckReport> CheckAsync(CancellationToken ct = default)
        {
            TweakTarget? target = null;
            string? problem = null;

            try
            {
                TweakTarget resolved = await adapterResolver.ResolveActiveAdapterAsync(ct).ConfigureAwait(false);
                if (resolved.IsAdapterBound)
                {
                    target = resolved;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problem = $"активный адаптер не определён ({ex.Message})";
            }

            TweakAnalysisReport tweakReport = await tweaks.AnalyzeAsync(target, ct).ConfigureAwait(false);

            NetworkAnalysisReport networkReport = target is null
                ? new NetworkAnalysisReport(
                    [],
                    [],
                    ["Активный адаптер не определён — разбор окружения сети и советы MTU/DNS пропущены: проверять «наугад» нельзя."],
                    ["Проверка настроек выполнена частично: без активного адаптера советы по сети не собираются."],
                    "Данные о сети собраны не полностью.")
                : await network.AnalyzeAsync(target, ct).ConfigureAwait(false);

            bool contextComplete = networkReport.Warnings.All(warning =>
                !string.Equals(warning.Code, NetworkAnalyzer.WarningContextIncomplete, StringComparison.Ordinal));

            return new SettingsCheckReport(
                networkReport,
                tweakReport,
                DataCollected: target is not null && contextComplete,
                problem is null && target is not null ? null : problem ?? "адаптер не определён");
        }
    }
}
