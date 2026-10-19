using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PingForge.Analysis;
using PingForge.App.ViewModels;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Network.Windows;
using PingForge.Probing;
using PingForge.Tweaks.Windows;

namespace PingForge.App;

/// <summary>
/// Живая сборка «Проверить сеть» (задача G): те же измерения, что уже живут в проекте, —
/// DF-MTU (<see cref="DfMtuDiscoveryService"/>), бенчмарк DNS (<see cref="DnsResolverBenchmarker"/>),
/// трассировка первых хопов (<see cref="HopTracerouteEngine"/>), чтение окружения
/// (<see cref="SystemNetworkContextReader"/>) и netsh только на чтение.
///
/// Правила честности здесь:
///   • прогон идёт только по кнопке и ничего не меняет — все вызванные вещи читают и меряют;
///   • каждый пункт защищён отдельно: упал один замер — пункт честно «проверить нельзя»,
///     а не весь отчёт и не молчаливое «всё хорошо»;
///   • буферблоат нагружает канал только после согласия человека (его спрашивает модель окна);
///     здесь метод уже вызван по согласию и просто меряет.
/// </summary>
internal static class NetworkCheckWiring
{
    /// <summary>Собрать проверку сети с настоящими транспортами Windows.</summary>
    public static INetworkSelfCheck Build(
        WindowsTweakPlatform platform,
        EngineClient engine,
        Func<string?> currentEndpoint,
        bool runsAsAdministrator) =>
        new LiveNetworkSelfCheck(platform.Command, engine, currentEndpoint, runsAsAdministrator);

    private sealed class LiveNetworkSelfCheck(
        ICommandPort command,
        EngineClient engine,
        Func<string?> currentEndpoint,
        bool runsAsAdministrator) : INetworkSelfCheck
    {
        /// <summary>Публичный якорь замеров, когда игрового сервера нет: Cloudflare anycast.</summary>
        private static readonly IPAddress Anchor = IPAddress.Parse("1.1.1.1");

        public async Task<NetworkSelfCheckReport> RunAsync(CancellationToken ct = default)
        {
            NetworkContextSnapshot context = await new SystemNetworkContextReader().ReadAsync(ct).ConfigureAwait(false);
            AdapterInventoryItem? active = context.ActiveAdapterName is { } name
                ? context.Adapters.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal))
                : null;

            var items = new List<NetworkCheckItemResult>
            {
                await GuardedAsync(NetworkCheckItemKind.Mtu, "MTU и фрагментация", () => CheckMtuAsync(context, ct), ct).ConfigureAwait(false),
                await GuardedAsync(NetworkCheckItemKind.Dns, "DNS", () => CheckDnsAsync(context, ct), ct).ConfigureAwait(false),
                await GuardedAsync(NetworkCheckItemKind.DoubleNat, "Двойной NAT", () => CheckDoubleNatAsync(active, ct), ct).ConfigureAwait(false),
                await GuardedAsync(NetworkCheckItemKind.Wifi, "Шум Wi-Fi и каналы", () => CheckWifiAsync(active, ct), ct).ConfigureAwait(false),
                await GuardedAsync(NetworkCheckItemKind.BackgroundDownloads, "Фоновые загрузки", () => CheckBackgroundAsync(active, ct), ct).ConfigureAwait(false),
                await GuardedAsync(NetworkCheckItemKind.RightsAndService, "Права и служба", () => CheckRightsAsync(ct), ct).ConfigureAwait(false),
            };

            return new NetworkSelfCheckReport(items);
        }

        public async Task<NetworkCheckItemResult> RunBufferbloatAsync(CancellationToken ct = default)
        {
            var tester = new BufferbloatLoadTester(new SystemNetIcmpProbe(), new HttpDownloadLoadSource());
            BufferbloatResult result = await tester.MeasureAsync(Anchor, ct).ConfigureAwait(false);
            return NetworkCheckEvaluators.EvaluateBufferbloat(result);
        }

        /// <summary>
        /// Одна проверка под защитой: её сбой — это «проверить нельзя» с причиной, а не падение
        /// всего отчёта. Отмену не глушим: отмена — это отмена.
        /// </summary>
        private static async Task<NetworkCheckItemResult> GuardedAsync(
            NetworkCheckItemKind kind, string titleRu, Func<Task<NetworkCheckItemResult>> check, CancellationToken ct)
        {
            try
            {
                return await check().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new NetworkCheckItemResult(kind, titleRu, NetworkCheckStatus.CannotCheck,
                    NetworkCheckPriority.Info,
                    $"Замер не выполнился ({ex.GetType().Name}: {ex.Message}).",
                    "Выводов по этому пункту не делаем.");
            }
        }

        // ── MTU: DF-пробы до игрового сервера живой сессии, а без неё — до публичного якоря ──

        private async Task<NetworkCheckItemResult> CheckMtuAsync(NetworkContextSnapshot context, CancellationToken ct)
        {
            IPAddress? gameServer = ParseEndpoint(currentEndpoint());
            IPAddress target = gameServer ?? Anchor;
            string targetRu = gameServer is not null ? $"игрового сервера {gameServer}" : $"публичного узла {Anchor}";

            if (context.ActiveAdapterMtuBytes is not { } currentMtu)
            {
                return NetworkCheckEvaluators.EvaluateMtu(null, null, targetRu);
            }

            PathMtuDiscoveryResult result = await new DfMtuDiscoveryService(new SystemNetIcmpProbe())
                .DiscoverAsync(target, currentMtu, ct: ct)
                .ConfigureAwait(false);

            return NetworkCheckEvaluators.EvaluateMtu(result, currentMtu, targetRu);
        }

        private static IPAddress? ParseEndpoint(string? endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return null;
            }

            // «адрес:порт» — берём часть до последнего двоеточия; без порта — строку целиком.
            string host = endpoint.Contains(':') ? endpoint[..endpoint.LastIndexOf(':')] : endpoint;
            return IPAddress.TryParse(host, out IPAddress? parsed) ? parsed : null;
        }

        // ── DNS: бенчмарк публичных резолверов ПЛЮС сейчас настроенный — сравнивать можно только измеренное ──

        private async Task<NetworkCheckItemResult> CheckDnsAsync(NetworkContextSnapshot context, CancellationToken ct)
        {
            var candidates = new List<(string Name, string Primary, string Secondary)>(DnsResolverBenchmarker.DefaultCandidates);

            string? current = context.ConfiguredDnsServers.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(current) && candidates.All(c => c.Primary != current))
            {
                string secondary = context.ConfiguredDnsServers.Skip(1).FirstOrDefault() ?? string.Empty;
                candidates.Add((NetworkCheckEvaluators.CurrentDnsName, current, secondary));
            }

            IReadOnlyList<DnsResolverBenchmarkResult> results = await new DnsResolverBenchmarker(new UdpDnsQueryTransport())
                .BenchmarkAsync(candidates, ct)
                .ConfigureAwait(false);

            return NetworkCheckEvaluators.EvaluateDns(results);
        }

        // ── Двойной NAT: свои адреса + первые три хопа к публичному якорю ────────────────────

        private async Task<NetworkCheckItemResult> CheckDoubleNatAsync(AdapterInventoryItem? active, CancellationToken ct)
        {
            List<string> ownIps = ReadOwnIpv4Addresses(active);
            IReadOnlyList<RouteHopMetric> hops = await new HopTracerouteEngine(new SystemNetIcmpProbe())
                .TraceAsync(Anchor, maxHops: 3, ct)
                .ConfigureAwait(false);

            return NetworkCheckEvaluators.EvaluateDoubleNat(ownIps, active?.Ipv4GatewayAddress, hops);
        }

        private static List<string> ReadOwnIpv4Addresses(AdapterInventoryItem? active)
        {
            var addresses = new List<string>();

            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up
                    || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                if (active is not null && !string.Equals(ni.Id, active.InterfaceGuid, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation unicast in ni.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        addresses.Add(unicast.Address.ToString());
                    }
                }

                if (active is not null)
                {
                    break;
                }
            }

            return addresses;
        }

        // ── Wi-Fi: netsh только на чтение и только когда активное подключение беспроводное ───

        private async Task<NetworkCheckItemResult> CheckWifiAsync(AdapterInventoryItem? active, CancellationToken ct)
        {
            bool wireless = active?.IsWireless == true;
            if (!wireless)
            {
                return NetworkCheckEvaluators.EvaluateWifi(false, null, null);
            }

            CommandResult output = await command
                .RunAsync("netsh", ["wlan", "show", "networks", "mode=bssid"], ct)
                .ConfigureAwait(false);

            if (!output.Succeeded || string.IsNullOrWhiteSpace(output.StandardOutput))
            {
                string why = string.IsNullOrWhiteSpace(output.StandardError)
                    ? $"netsh не отдал список сетей (код {output.ExitCode})."
                    : $"netsh не отдал список сетей: {output.StandardError.Trim()}";
                return NetworkCheckEvaluators.EvaluateWifi(true, null, why);
            }

            return NetworkCheckEvaluators.EvaluateWifi(true, WifiNetworksParser.Parse(output.StandardOutput), null);
        }

        // ── Фон: Delivery Optimization по имени + измеренная скорость адаптера за 2 секунды ──

        private async Task<NetworkCheckItemResult> CheckBackgroundAsync(AdapterInventoryItem? active, CancellationToken ct)
        {
            bool? doDownloading = null;
            int doFiles = 0;

            try
            {
                CommandResult output = await command
                    .RunAsync("powershell", ["-NoProfile", "-NonInteractive", "-Command",
                        "Get-DeliveryOptimizationStatus | Select-Object -ExpandProperty Status"], ct)
                    .ConfigureAwait(false);

                if (output.Succeeded)
                {
                    var statuses = output.StandardOutput
                        .Split('\n')
                        .Select(line => line.Trim())
                        .Where(line => line.Length > 0)
                        .ToList();
                    doFiles = statuses.Count;
                    doDownloading = statuses.Any(line => line.Contains("Downloading", StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Нет PowerShell или командлета — пункт решает замер скорости ниже.
            }

            (double? down, double? up) = await SampleThroughputAsync(active, ct).ConfigureAwait(false);

            return NetworkCheckEvaluators.EvaluateBackground(doDownloading, doFiles, down, up);
        }

        private static async Task<(double? DownMbps, double? UpMbps)> SampleThroughputAsync(
            AdapterInventoryItem? active, CancellationToken ct)
        {
            NetworkInterface? ni = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.OperationalStatus == OperationalStatus.Up
                && (active is null || string.Equals(n.Id, active.InterfaceGuid, StringComparison.Ordinal)));

            if (ni is null)
            {
                return (null, null);
            }

            IPInterfaceStatistics before = ni.GetIPStatistics();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            watch.Stop();
            IPInterfaceStatistics after = ni.GetIPStatistics();

            double seconds = Math.Max(0.1, watch.Elapsed.TotalSeconds);
            double down = Math.Max(0, after.BytesReceived - before.BytesReceived) * 8.0 / seconds / 1_000_000.0;
            double up = Math.Max(0, after.BytesSent - before.BytesSent) * 8.0 / seconds / 1_000_000.0;
            return (Math.Round(down, 1), Math.Round(up, 1));
        }

        // ── Права и служба: тот же разговор по каналу, что у окна всегда ─────────────────────

        private async Task<NetworkCheckItemResult> CheckRightsAsync(CancellationToken ct)
        {
            try
            {
                (bool ok, string messageRu) = await engine.PingAsync(ct).ConfigureAwait(false);
                return NetworkCheckEvaluators.EvaluateRightsAndService(runsAsAdministrator, ok, ok ? null : messageRu);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return NetworkCheckEvaluators.EvaluateRightsAndService(runsAsAdministrator, false, ex.Message);
            }
        }
    }
}
