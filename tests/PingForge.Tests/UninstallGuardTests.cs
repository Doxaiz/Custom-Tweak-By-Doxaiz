using PingForge.Service;
using PingForge.Service.Protocol;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Логика удаления без следов (задача T). Чистая — без Windows, без файлов, без канала: ровно то,
/// что обязаны гарантировать и скрипт снятия, и команда службы. Порядок шагов отката и правила
/// -RemoveData здесь — единственное место, откуда их берут и текст, и действие.
/// </summary>
public sealed class UninstallGuardTests
{
    // ── когда спрашивать ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Удаление_без_применённых_изменений_без_лишних_вопросов()
    {
        var state = new UninstallState(AppliedChanges: 0, OpenOperations: 0, Snapshots: 0, OptimizationRunning: false);

        Assert.False(UninstallGuard.MustAsk(state));
        Assert.Contains("можно удалять спокойно", UninstallGuard.QuestionRu(state));
    }

    [Fact]
    public void Незакрытая_операция_без_записей_журнала_тоже_спрашивает()
    {
        // Операция начата и не закрыта: состояние системы неизвестно — молча удалять нельзя.
        var state = new UninstallState(0, 1, 0, false);

        Assert.True(UninstallGuard.MustAsk(state));
        Assert.Contains("незакрытых операций: 1", UninstallGuard.QuestionRu(state));
    }

    [Fact]
    public void Идущая_оптимизация_спрашивает_даже_с_пустым_журналом()
    {
        var state = new UninstallState(0, 0, 0, OptimizationRunning: true);

        Assert.True(UninstallGuard.MustAsk(state));
        Assert.Contains("живая оптимизация идёт", UninstallGuard.QuestionRu(state));
    }

    [Fact]
    public void С_применёнными_изменениями_предлагают_три_варианта_и_первый_по_умолчанию()
    {
        var state = new UninstallState(AppliedChanges: 3, OpenOperations: 1, Snapshots: 2, OptimizationRunning: false);

        Assert.True(UninstallGuard.MustAsk(state));
        Assert.Equal(3, UninstallGuard.OptionsRu.Count);
        Assert.Equal("Вернуть настройки Windows как было и удалить программу", UninstallGuard.OptionsRu[0]);
        Assert.Contains("Удалить программу, оставив изменения", UninstallGuard.OptionsRu[1]);
        Assert.Equal("Отмена", UninstallGuard.OptionsRu[2]);
        Assert.Contains("применённых изменений: 3", UninstallGuard.QuestionRu(state));
        Assert.Contains("Снапшотов для отката: 2", UninstallGuard.QuestionRu(state));
    }

    [Fact]
    public void Без_снапшотов_говорят_что_возвращать_автоматически_нечем()
    {
        var state = new UninstallState(5, 0, 0, false);

        Assert.False(state.CanRollback);
        Assert.Contains("Снапшотов нет", UninstallGuard.QuestionRu(state));
    }

    // ── подтверждение опасного варианта ────────────────────────────────────────────────────

    [Fact]
    public void Оставление_изменений_требует_явного_подтверждения()
    {
        Assert.True(UninstallGuard.ChoiceNeedsExplicitConfirmation(UninstallChoice.KeepChangesAndRemove));
        Assert.False(UninstallGuard.ChoiceNeedsExplicitConfirmation(UninstallChoice.RollbackThenRemove));
        Assert.False(UninstallGuard.ChoiceNeedsExplicitConfirmation(UninstallChoice.Cancel));

        string? refusal = UninstallGuard.ValidateChoice(UninstallChoice.KeepChangesAndRemove, explicitlyConfirmed: false);

        Assert.NotNull(refusal);
        Assert.Contains("нечем", refusal!);
        Assert.Contains("-ConfirmKeepChanges", refusal);
    }

    [Fact]
    public void Оставление_изменений_с_явным_подтверждением_разрешено()
    {
        Assert.Null(UninstallGuard.ValidateChoice(UninstallChoice.KeepChangesAndRemove, explicitlyConfirmed: true));
        Assert.Null(UninstallGuard.ValidateChoice(UninstallChoice.RollbackThenRemove, explicitlyConfirmed: false));
        Assert.Null(UninstallGuard.ValidateChoice(UninstallChoice.Cancel, explicitlyConfirmed: false));
    }

    [Fact]
    public void Предупреждение_об_оставленных_изменениях_говорит_правду_о_снапшотах()
    {
        var withSnaps = new UninstallState(2, 0, 4, false);
        var withoutSnaps = new UninstallState(2, 0, 0, false);

        Assert.Contains("4", UninstallGuard.KeepChangesWarningRu(withSnaps));
        Assert.DoesNotContain("снапшотов, которых сейчас 0", UninstallGuard.KeepChangesWarningRu(withoutSnaps));
        Assert.Contains("только вручную", UninstallGuard.KeepChangesWarningRu(withSnaps));
    }

    // ── правило -RemoveData (требование 2) ─────────────────────────────────────────────────

    [Fact]
    public void RemoveData_без_отката_при_незакрытых_операциях_отказывается_без_Force()
    {
        var state = new UninstallState(3, 2, 1, false);

        string? refusal = UninstallGuard.ValidateRemoveData(state, rolledBackOrNothingToDo: false, removeData: true, force: false);

        Assert.NotNull(refusal);
        // Отказ объясняет последствия и выход, а не просто «нет».
        Assert.Contains("исчезнут снапшоты", refusal!);
        Assert.Contains("-Force", refusal);
        Assert.Contains("незакрытых операций: 2", refusal);
    }

    [Fact]
    public void Force_снимает_запрет_но_не_скрывает_что_будет()
    {
        var state = new UninstallState(3, 2, 1, false);

        Assert.Null(UninstallGuard.ValidateRemoveData(state, rolledBackOrNothingToDo: false, removeData: true, force: true));
    }

    [Fact]
    public void После_успешного_отката_RemoveData_разрешён_без_Force()
    {
        var state = new UninstallState(3, 2, 1, false);

        Assert.Null(UninstallGuard.ValidateRemoveData(state, rolledBackOrNothingToDo: true, removeData: true, force: false));
    }

    [Fact]
    public void Без_изменений_RemoveData_не_требует_ни_отката_ни_Force()
    {
        var state = new UninstallState(0, 0, 0, false);

        Assert.Null(UninstallGuard.ValidateRemoveData(state, rolledBackOrNothingToDo: false, removeData: true, force: false));
        Assert.Null(UninstallGuard.ValidateRemoveData(state, rolledBackOrNothingToDo: false, removeData: false, force: false));
    }

    // ── порядок откатa и проверка остатков ─────────────────────────────────────────────────

    [Fact]
    public void Порядок_отката_канонический_и_все_шаги_существуют_у_службы()
    {
        Assert.Equal(
            new[] { "realtime-stop", "repair-network", "rollback", "status" },
            UninstallGuard.RollbackSequenceWireNames);

        foreach (string wire in UninstallGuard.RollbackSequenceWireNames)
        {
            EngineCommandDescriptor? found = EngineCommandCatalog.Find(wire);
            Assert.NotNull(found);
            EngineCommandDescriptor descriptor = found!;

            // Последний шаг — проверка, а не изменение; остановка своих изменений доступна без прав.
            if (wire is "status" or "realtime-stop")
            {
                Assert.False(descriptor.NeedsElevation);
            }
        }
    }

    [Fact]
    public void Остатки_называют_что_именно_не_снялось()
    {
        Assert.Contains("rollback", UninstallGuard.ResidualCheckRu(openOperationsAfter: null, failedStep: "rollback"));
        Assert.Contains("Удаление остановлено", UninstallGuard.ResidualCheckRu(openOperationsAfter: null, failedStep: "rollback"));
        Assert.Contains("2", UninstallGuard.ResidualCheckRu(openOperationsAfter: 2, failedStep: null));
        Assert.Contains("repair-network", UninstallGuard.ResidualCheckRu(openOperationsAfter: 2, failedStep: null));
        Assert.Contains("Остатков нет", UninstallGuard.ResidualCheckRu(openOperationsAfter: 0, failedStep: null));
    }

    // ── команда службы и протокол ──────────────────────────────────────────────────────────

    [Fact]
    public void Команда_uninstall_check_объявлена_как_чтение_и_живёт_в_канале_просмотра()
    {
        EngineCommandDescriptor descriptor = EngineCommandCatalog.Get(EngineCommandKind.UninstallCheck);

        Assert.Equal("uninstall-check", descriptor.WireName);
        Assert.False(descriptor.NeedsElevation);
        Assert.True(EngineCommandCatalog.IsAllowedOnReadOnlyChannel(descriptor));
        Assert.Empty(descriptor.RequiredArgs);
    }

    [Fact]
    public void Запрос_uninstall_check_ходит_через_JSON_как_остальные()
    {
        string json = EngineJson.Encode(new EngineRequest("7", EngineCommandKind.UninstallCheck));

        Assert.Contains("\"uninstall-check\"", json);
        Assert.True(EngineJson.TryDecodeRequest(json, out EngineRequest? decoded, out string error));
        Assert.Equal(string.Empty, error);
        Assert.Equal(EngineCommandKind.UninstallCheck, decoded!.Command);
    }

    [Fact]
    public void Скрипт_снятия_не_переписывает_логику_а_зовёт_её_же_текстом()
    {
        // Текст варианта и порядок шагов — один источник. Скрипт на PowerShell печатает ровно эти
        // строки; тест привязывает их, чтобы правка в C# не разъехалась с подсказкой в консоли.
        string script = File.ReadAllText(ScriptPath("uninstall-service.ps1"));

        Assert.Contains("RollbackThenRemove", script);
        Assert.Contains("realtime-stop", script);
        Assert.Contains("repair-network", script);
        Assert.Contains("uninstall-check", script);
        Assert.Contains("-ConfirmKeepChanges", script);
    }

    private static string ScriptPath(string name) =>
        // Файл прикован к тесту через CopyToOutputDirectory: сборка теста идёт с artifacts-path вне
        // репозитория, обход каталогов вверх тут не работает — так же, как с checked-app.manifest.
        Path.Combine(AppContext.BaseDirectory, "checked-" + name);
}
