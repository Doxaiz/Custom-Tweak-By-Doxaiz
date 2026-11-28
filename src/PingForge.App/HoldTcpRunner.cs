using System.Net.Sockets;
using System.Text;
using PingForge.Probing;

namespace PingForge.App;

/// <summary>
/// Дочерний процесс для живой проверки iphlpapi: держит TCP-соединение с настоящим сервером и шлёт
/// данные, пока родитель читает статистику этого соединения.
///
/// Почему именно так: сетевой стек считает задержку только по сегментам, отправленным после включения
/// сбора статистики. Молчащее соединение дало бы «стек ещё не собрал измерений» — это свойство стенда,
/// а не игры, и проверять на нём нечего. Поэтому данные идут всё время: по одному запросу в секунду,
/// ответ читается, соединение остаётся живым (keep-alive).
/// </summary>
internal static class HoldTcpRunner
{
    /// <summary>Код возврата: 0 — соединение держалось, 3 — подключиться не удалось.</summary>
    public static int Run(HoldTcpRequest request)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            client.ConnectAsync(request.Address, request.Port, timeout.Token).GetAwaiter().GetResult();

            NetworkStream stream = client.GetStream();
            byte[] payload = Encoding.ASCII.GetBytes(
                $"GET / HTTP/1.1\r\nHost: {request.Address}\r\nConnection: keep-alive\r\n\r\n");
            var drain = new byte[2048];

            DateTime deadline = DateTime.UtcNow.AddSeconds(request.Seconds);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    stream.Write(payload, 0, payload.Length);
                }
                catch (System.IO.IOException)
                {
                    // Сервер закрыл соединение — держать больше нечего, и врать об этом не надо.
                    return 3;
                }

                Thread.Sleep(700);

                try
                {
                    while (stream.DataAvailable)
                    {
#pragma warning disable CA2022 // читаем то, что уже пришло: ждать полного буфера нельзя
                        stream.Read(drain, 0, drain.Length);
#pragma warning restore CA2022
                    }
                }
                catch (System.IO.IOException)
                {
                    return 3;
                }
            }

            return 0;
        }
        catch (Exception ex) when (ex is SocketException or System.IO.IOException or OperationCanceledException)
        {
            return 3;
        }
    }
}
