using System.Text;
using System.Text.Json;
using PingForge.Core.Models;
using PingForge.Orchestration.Realtime;
using PingForge.Service;
using PingForge.Service.Protocol;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Протокол канала службы: кадры, разбор запросов, тексты ошибок.
///
/// Проверяется без Windows нарочно: ошибка в формате кадра — это не «мелочь реализации»,
/// а причина, по которой окно не сможет поговорить со службой вообще.
/// </summary>
public class ServiceProtocolTests
{
    // ── кадры ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Кадр_переживает_круговой_путь_включая_кириллицу()
    {
        var stream = new MemoryStream();
        const string payload = """{"id":"1","command":"ping","текст":"проверка кириллицы"}""";

        await EngineFrameCodec.WriteAsync(stream, payload);
        stream.Position = 0;
        string? read = await EngineFrameCodec.ReadAsync(stream);

        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task Заголовок_кадра_это_длина_в_четырёх_байтах_по_младшему_порядку()
    {
        var stream = new MemoryStream();
        await EngineFrameCodec.WriteAsync(stream, "abc");

        byte[] bytes = stream.ToArray();
        Assert.Equal(4 + 3, bytes.Length);
        Assert.Equal(3, bytes[0]);
        Assert.Equal(0, bytes[1]);
        Assert.Equal(0, bytes[2]);
        Assert.Equal(0, bytes[3]);
    }

    [Fact]
    public async Task Два_кадра_подряд_читаются_по_порядку()
    {
        var stream = new MemoryStream();
        await EngineFrameCodec.WriteAsync(stream, """{"id":"1"}""");
        await EngineFrameCodec.WriteAsync(stream, """{"id":"2"}""");
        stream.Position = 0;

        Assert.Equal("""{"id":"1"}""", await EngineFrameCodec.ReadAsync(stream));
        Assert.Equal("""{"id":"2"}""", await EngineFrameCodec.ReadAsync(stream));
        Assert.Null(await EngineFrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task Аккуратное_закрытие_потока_это_конец_связи_а_не_ошибка()
    {
        var stream = new MemoryStream();
        Assert.Null(await EngineFrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task Оборванный_заголовок_объясняется_числами()
    {
        var stream = new MemoryStream([0x10, 0x00]);

        EngineProtocolViolationException ex = await Assert.ThrowsAsync<EngineProtocolViolationException>(
            () => EngineFrameCodec.ReadAsync(stream));

        Assert.Contains("заголовке", ex.Message);
        Assert.Contains("2", ex.Message);
        Assert.Contains("4", ex.Message);
    }

    [Fact]
    public async Task Кадр_с_длиной_ноль_не_выполняется()
    {
        var stream = new MemoryStream([0x00, 0x00, 0x00, 0x00]);

        EngineProtocolViolationException ex = await Assert.ThrowsAsync<EngineProtocolViolationException>(
            () => EngineFrameCodec.ReadAsync(stream));

        Assert.Contains("непустым", ex.Message);
    }

    [Fact]
    public async Task Кадр_больше_предела_отклоняется_до_чтения_тела()
    {
        byte[] header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, EngineProtocol.MaxFrameBytes + 1);

        var stream = new MemoryStream(header);

        EngineProtocolViolationException ex = await Assert.ThrowsAsync<EngineProtocolViolationException>(
            () => EngineFrameCodec.ReadAsync(stream));

        Assert.Contains((EngineProtocol.MaxFrameBytes + 1).ToString(), ex.Message);
        Assert.Contains(EngineProtocol.MaxFrameBytes.ToString(), ex.Message);
    }

    [Fact]
    public async Task Кадр_оборванный_в_теле_сообщает_сколько_не_дошло()
    {
        var stream = new MemoryStream();
        await EngineFrameCodec.WriteAsync(stream, "0123456789");
        byte[] all = stream.ToArray();
        var cut = new MemoryStream(all[..^4]);

        EngineProtocolViolationException ex = await Assert.ThrowsAsync<EngineProtocolViolationException>(
            () => EngineFrameCodec.ReadAsync(cut));

        Assert.Contains("оборван", ex.Message);
        Assert.Contains("10", ex.Message);
        Assert.Contains("6", ex.Message);
    }

    [Fact]
    public async Task Отправка_пустого_кадра_запрещена()
    {
        var stream = new MemoryStream();
        await Assert.ThrowsAsync<EngineProtocolViolationException>(
            () => EngineFrameCodec.WriteAsync(stream, string.Empty));
    }

    [Fact]
    public async Task Отправка_кадра_больше_предела_запрещена()
    {
        var stream = new MemoryStream();
        string huge = new string('x', EngineProtocol.MaxFrameBytes + 1);
        await Assert.ThrowsAsync<EngineProtocolViolationException>(
            () => EngineFrameCodec.WriteAsync(stream, huge));
    }

    [Fact]
    public async Task Кадр_читается_кусками_и_собирается_целиком()
    {
        // Поток отдаёт по байту — так ведёт себя канал при медленном клиенте.
        var stream = new MemoryStream();
        await EngineFrameCodec.WriteAsync(stream, """{"id":"медленный"}""");
        stream.Position = 0;

        var dripping = new DrippingStream(stream, chunkSize: 1);
        Assert.Equal("""{"id":"медленный"}""", await EngineFrameCodec.ReadAsync(dripping));
    }

    // ── разбор запросов ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Запрос_с_аргументами_разбирается_в_объект()
    {
        string json = """{"id":"7","command":"apply-preset","args":{"preset":"low-latency","restorePoint":"true"}}""";

        Assert.True(EngineJson.TryDecodeRequest(json, out EngineRequest? request, out string errorRu));
        Assert.Empty(errorRu);
        Assert.Equal("7", request!.Id);
        Assert.Equal(EngineCommandKind.ApplyPreset, request.Command);
        Assert.Equal("low-latency", request.Argument("preset"));
        Assert.Equal("true", request.Argument("restorePoint"));
    }

    [Fact]
    public void Неизвестная_команда_отклоняется_с_перечнем_известных()
    {
        Assert.False(EngineJson.TryDecodeRequest("""{"id":"1","command":"format-c"}""", out EngineRequest? request, out string errorRu));

        Assert.Null(request);
        Assert.Contains("format-c", errorRu);
        Assert.Contains("apply-preset", errorRu);
        Assert.Contains("repair-network", errorRu);
    }

    [Fact]
    public void Запрос_без_id_отклоняется()
    {
        Assert.False(EngineJson.TryDecodeRequest("""{"command":"ping"}""", out _, out string errorRu));
        Assert.Contains("id", errorRu);
    }

    [Fact]
    public void Запрос_без_команды_отклоняется()
    {
        Assert.False(EngineJson.TryDecodeRequest("""{"id":"1"}""", out _, out string errorRu));
        Assert.Contains("не указана команда", errorRu);
    }

    [Fact]
    public void Битый_json_отклоняется_с_объяснением()
    {
        Assert.False(EngineJson.TryDecodeRequest("{ это не json ", out _, out string errorRu));
        Assert.Contains("JSON", errorRu);
    }

    [Fact]
    public void Пустой_запрос_отклоняется()
    {
        Assert.False(EngineJson.TryDecodeRequest("   ", out _, out string errorRu));
        Assert.Contains("Пустой запрос", errorRu);
    }

    [Fact]
    public void Лишние_аргументы_не_мешают_разбору()
    {
        // Так клиент новой версии сможет звать старую службу и наоборот.
        string json = """{"id":"1","command":"status","args":{"чего-то":"лишнее"}}""";
        Assert.True(EngineJson.TryDecodeRequest(json, out EngineRequest? request, out _));
        Assert.Equal(EngineCommandKind.Status, request!.Command);
        Assert.Equal("лишнее", request.Argument("чего-то"));
    }

    [Fact]
    public void Ответ_кодируется_с_именем_команды_строкой_а_не_числом()
    {
        string json = EngineJson.Encode(new EngineRequest("1", EngineCommandKind.ApplyTweaks));
        Assert.Contains("\"apply-tweaks\"", json);
    }

    [Fact]
    public void Ответ_службы_читается_клиентом()
    {
        var response = new EngineResponse("1", true, "готово", """{"ping":"0"}""");
        EngineResponse decoded = EngineJson.DecodeResponse(EngineJson.Encode(response));

        Assert.Equal("1", decoded.Id);
        Assert.True(decoded.Ok);
        Assert.Equal("готово", decoded.MessageRu);
        Assert.Equal("""{"ping":"0"}""", decoded.DataJson);
    }

    [Fact]
    public void Ошибка_не_пишет_поле_данных()
    {
        string json = EngineJson.Encode(EngineResponse.Failure("1", "нельзя"));
        Assert.DoesNotContain("dataJson", json);
        Assert.Contains("\"ok\":false", json);
    }

    // ── таблица команд ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Каждая_команда_описана_и_имеет_пример()
    {
        Assert.All(EngineCommandCatalog.All, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.WireName));
            Assert.False(string.IsNullOrWhiteSpace(item.TitleRu));
            Assert.False(string.IsNullOrWhiteSpace(item.WhatRu));
            Assert.Contains(item.WireName, item.ExampleRu);
        });
    }

    [Fact]
    public void Имя_команды_по_каналу_это_латиница_в_нижнем_регистре_без_пробелов()
    {
        Assert.All(EngineCommandCatalog.All, item =>
            Assert.Matches("^[a-z][a-z0-9-]*$", item.WireName));
    }

    [Fact]
    public void Имена_команд_не_повторяются_и_покрывают_все_значения_перечисления()
    {
        IReadOnlyList<string> names = EngineCommandCatalog.All.Select(item => item.WireName).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (EngineCommandKind kind in Enum.GetValues<EngineCommandKind>())
        {
            Assert.NotNull(EngineCommandCatalog.Find(EngineCommandCatalog.Get(kind).WireName));
        }
    }

    [Fact]
    public void Права_администратора_требуются_ровно_там_где_меняется_система()
    {
        Assert.False(EngineCommandCatalog.Get(EngineCommandKind.Ping).NeedsElevation);
        Assert.False(EngineCommandCatalog.Get(EngineCommandKind.Capabilities).NeedsElevation);
        Assert.False(EngineCommandCatalog.Get(EngineCommandKind.Status).NeedsElevation);
        Assert.False(EngineCommandCatalog.Get(EngineCommandKind.JournalTail).NeedsElevation);
        Assert.False(EngineCommandCatalog.Get(EngineCommandKind.RealtimeStop).NeedsElevation);
        Assert.False(EngineCommandCatalog.Get(EngineCommandKind.PerfProfile).NeedsElevation);

        Assert.True(EngineCommandCatalog.Get(EngineCommandKind.ApplyPreset).NeedsElevation);
        Assert.True(EngineCommandCatalog.Get(EngineCommandKind.ApplyTweaks).NeedsElevation);
        Assert.True(EngineCommandCatalog.Get(EngineCommandKind.RollbackSnapshot).NeedsElevation);
        Assert.True(EngineCommandCatalog.Get(EngineCommandKind.RepairNetwork).NeedsElevation);
        Assert.True(EngineCommandCatalog.Get(EngineCommandKind.RealtimeStart).NeedsElevation);
    }

    [Fact]
    public void Описание_команд_называет_числа_которые_считаются_из_таблицы()
    {
        string text = EngineCommandCatalog.DescribeRu();

        Assert.Contains($"Команд: {EngineCommandCatalog.All.Count}", text);
        Assert.Contains($"требуют прав администратора: {EngineCommandCatalog.ElevatedCommandCount}", text);
        Assert.Contains(EngineProtocol.PipeName, text);
        Assert.Contains("repair-network", text);
    }

    [Fact]
    public void Слова_пресетов_и_профилей_сходятся_с_перечислениями()
    {
        foreach (TweakProfilePreset preset in Enum.GetValues<TweakProfilePreset>())
        {
            string wire = EngineNames.PresetWireName(preset);
            Assert.True(EngineNames.TryParsePreset(wire, out TweakProfilePreset parsed), wire);
            Assert.Equal(preset, parsed);
            Assert.False(string.IsNullOrWhiteSpace(EngineNames.PresetTitleRu(preset)));
            Assert.False(string.IsNullOrWhiteSpace(EngineNames.PresetTitleEn(preset)));
        }

        foreach (PerfProfile profile in Enum.GetValues<PerfProfile>())
        {
            string wire = EngineNames.ProfileWireName(profile);
            Assert.True(EngineNames.TryParseProfile(wire, out PerfProfile parsed), wire);
            Assert.Equal(profile, parsed);
        }
    }

    // ── разбор аргументов ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Обязательный_аргумент_называет_команду_и_пример()
    {
        var args = new EngineArgs(null, "apply-preset");

        EngineArgumentException ex = Assert.Throws<EngineArgumentException>(() => args.Require("preset", """{"preset":"low-latency"}"""));
        Assert.Contains("apply-preset", ex.Message);
        Assert.Contains("preset", ex.Message);
        Assert.Contains("low-latency", ex.Message);
    }

    [Fact]
    public void Аргумент_из_пробелов_считается_отсутствующим()
    {
        var args = new EngineArgs(new Dictionary<string, string> { ["preset"] = "   " }, "apply-preset");
        Assert.Throws<EngineArgumentException>(() => args.Require("preset", "пример"));
    }

    [Fact]
    public void Числовой_аргумент_вне_предела_объясняет_предел()
    {
        var args = new EngineArgs(new Dictionary<string, string> { ["limit"] = "5000" }, "journal-tail");

        EngineArgumentException ex = Assert.Throws<EngineArgumentException>(() => args.RequireInt("limit", 1, EngineDispatcher.MaxJournalLimit, "пример"));
        Assert.Contains("1", ex.Message);
        Assert.Contains(EngineDispatcher.MaxJournalLimit.ToString(), ex.Message);
    }

    [Fact]
    public void Мусор_вместо_числа_это_ошибка_а_не_ноль()
    {
        var args = new EngineArgs(new Dictionary<string, string> { ["limit"] = "много" }, "journal-tail");

        EngineArgumentException ex = Assert.Throws<EngineArgumentException>(() => args.RequireInt("limit", 1, 500, "пример"));
        Assert.Contains("целым числом", ex.Message);
        Assert.Contains("много", ex.Message);
    }

    [Fact]
    public void Логический_аргумент_понимает_да_нет_и_единицу()
    {
        var args = new EngineArgs(new Dictionary<string, string>
        {
            ["a"] = "да",
            ["b"] = "0",
            ["c"] = "true",
            ["d"] = "непонятно",
        }, "apply-tweaks");

        Assert.True(args.GetBool("a", false));
        Assert.False(args.GetBool("b", true));
        Assert.True(args.GetBool("c", false));
        Assert.True(args.GetBool("отсутствует", true));
        Assert.Throws<EngineArgumentException>(() => args.GetBool("d", false));
    }

    [Fact]
    public void Список_твиков_чистится_от_повторов_и_мусора()
    {
        var args = new EngineArgs(new Dictionary<string, string>
        {
            ["tweaks"] = " TCP_NO_DELAY ,, game_mode_on ;TCP_NO_DELAY, ",
        }, "apply-tweaks");

        IReadOnlyList<string> codes = args.RequireList("tweaks", "пример", 200);

        Assert.Equal(2, codes.Count);
        Assert.Equal("TCP_NO_DELAY", codes[0]);
        Assert.Equal("game_mode_on", codes[1]);
    }

    [Fact]
    public void Список_твиков_с_превышением_предела_отклоняется()
    {
        string many = string.Join(',', Enumerable.Range(0, 5).Select(i => $"TWEAK_{i}"));
        var args = new EngineArgs(new Dictionary<string, string> { ["tweaks"] = many }, "apply-tweaks");

        EngineArgumentException ex = Assert.Throws<EngineArgumentException>(() => args.RequireList("tweaks", "пример", 3));
        Assert.Contains("5", ex.Message);
        Assert.Contains("3", ex.Message);
    }

    /// <summary>Поток, который отдаёт данные мелкими кусками — проверка «кадр собирается из частей».</summary>
    private sealed class DrippingStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _chunkSize;

        public DrippingStream(Stream inner, int chunkSize)
        {
            _inner = inner;
            _chunkSize = chunkSize;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int allowed = Math.Min(Math.Min(count, _chunkSize), buffer.Length - offset);
            return allowed <= 0 ? 0 : _inner.Read(buffer, offset, allowed);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
