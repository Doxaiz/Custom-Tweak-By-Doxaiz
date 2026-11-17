using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PingForge.Core.Security;

/// <summary>
/// Итог одной записи общего файла настроек.
/// </summary>
/// <param name="Written">Файл записан.</param>
/// <param name="ProblemRu">Что не так. Пусто — всё прошло чисто; при <c>Written = true</c> это предупреждение.</param>
/// <param name="BackupPathRu">Где лежит копия прежней версии (пусто, если прежней версии не было).</param>
public sealed record SharedFileWriteResult(bool Written, string ProblemRu, string BackupPathRu)
{
    public static SharedFileWriteResult Ok(string backupPathRu, string warningRu = "") =>
        new(true, warningRu, backupPathRu);

    public static SharedFileWriteResult Failed(string problemRu) => new(false, problemRu, string.Empty);
}

/// <summary>
/// Общий файл настроек, который пишут ДВА процесса: окно программы и служба Windows.
///
/// Зачем этот класс. Оба писателя делали одно и то же: «прочитать файл целиком → поменять своё поле →
/// записать временный → переименовать». Переименование атомарно только для одного писателя: если окно
/// сохраняет пути к играм в тот момент, когда служба кладёт в файл свой профиль, последний записавший
/// затирает поле первого — и человек теряет либо пути к играм, либо пройденный мастер первого запуска.
/// Потеря молчаливая: программа продолжает работать, просто «забывает» то, что ей сказали.
///
/// Что делается здесь:
///   • «прочитать → изменить → записать» целиком под одним именованным мьютексом, общим для обоих
///     процессов. На Windows имя с префиксом <c>Global\</c>: служба и окно живут в разных сеансах,
///     а префикс делает мьютекс общим на всю машину. Если такого мьютекса создать нельзя (другая
///     платформа или не хватило прав), берётся то же имя без префикса;
///   • если блокировку взять не удалось ВОВСЕ, запись всё равно выполняется, но вызывающий получает
///     предупреждение: молча делать вид, что защита есть, нельзя;
///   • если файл держит другой писатель дольше отведённого времени, запись НЕ выполняется — затереть
///     чужие данные хуже, чем не сохранить свои, и об этом сообщается;
///   • перед записью прежняя версия кладётся в <c>.bak</c> — одна копия, перезаписывается;
///   • битый JSON не затирается молча: файл переименовывается в <c>.broken-&lt;метка времени&gt;</c>,
///     чтобы причину потери настроек можно было разобрать, а не угадать;
///   • сама запись идёт через временный файл и переименование: половина файла на диске не появляется
///     ни при обрыве записи, ни при выключении питания.
/// </summary>
public sealed class SharedSettingsFileWriter
{
    /// <summary>Имя общего мьютекса. Совпадает у окна и у службы — иначе защита не общая.</summary>
    public const string MutexName = @"Global\PingForge.Settings.v1";

    /// <summary>То же имя без префикса сеанса: запасной вариант, если общий создать не удалось.</summary>
    public const string SessionMutexName = "PingForge.Settings.v1";

    /// <summary>
    /// Сколько ждём освободившийся файл. Чтение и запись маленького JSON — миллисекунды;
    /// пять секунд означает, что второй писатель завис, и ждать его дальше бесполезно.
    /// </summary>
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    private readonly string _path;
    private readonly string _mutexName;
    private readonly string _fallbackMutexName;

    public SharedSettingsFileWriter(string path, string? mutexName = null, string? fallbackMutexName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _mutexName = mutexName ?? MutexName;
        _fallbackMutexName = fallbackMutexName ?? SessionMutexName;
    }

    /// <summary>Путь к файлу, который защищает этот писатель.</summary>
    public string FilePath => _path;

    /// <summary>Путь к копии прежней версии.</summary>
    public string BackupPath => _path + ".bak";

    /// <summary>
    /// Прочитать файл под блокировкой. Файла нет — <paramref name="text"/> пустой, это не ошибка:
    /// первый запуск программы выглядит именно так.
    /// </summary>
    public bool TryRead(out string text, out string problemRu)
    {
        text = string.Empty;
        problemRu = string.Empty;

        using WriteLock guard = WriteLock.Create(_mutexName, _fallbackMutexName, WriteTimeout, out string lockProblem, out bool mayRead);
        problemRu = lockProblem;
        if (!mayRead)
        {
            return false;
        }

        try
        {
            if (File.Exists(_path))
            {
                text = File.ReadAllText(_path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problemRu = $"файл настроек не прочитался: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Прочитать → изменить → записать. Всё под одной блокировкой, поэтому второй писатель не может
    /// вклиниться между чтением и записью и затереть чужое поле.
    /// </summary>
    /// <param name="update">Правка содержимого: получает разобранный корень (или новый, если файла не было).</param>
    /// <param name="quarantineBroken">
    /// Что делать с битым JSON. <c>true</c> — переименовать файл в <c>.broken-&lt;метка&gt;</c> и начать
    /// с чистого; <c>false</c> — не записывать ничего и вернуть отказ (нужно там, где затирать чужой файл
    /// нельзя, например при записи профиля службой: файл принадлежит окну).
    /// </param>
    public SharedFileWriteResult Update(Func<JsonObject, JsonObject> update, bool quarantineBroken = true)
    {
        ArgumentNullException.ThrowIfNull(update);

        using WriteLock guard = WriteLock.Create(_mutexName, _fallbackMutexName, WriteTimeout, out string lockProblem, out bool mayWrite);
        if (!mayWrite)
        {
            return SharedFileWriteResult.Failed(lockProblem);
        }

        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string backupPath = string.Empty;
            string existing = File.Exists(_path) ? File.ReadAllText(_path) : string.Empty;
            JsonObject root;

            if (existing.Length == 0)
            {
                // Файла ещё нет — это первый запуск, а не поломка.
                root = new JsonObject();
            }
            else
            {
                JsonNode? parsed = null;
                try
                {
                    parsed = JsonNode.Parse(existing);
                }
                catch (JsonException ex)
                {
                    if (!quarantineBroken)
                    {
                        return SharedFileWriteResult.Failed(
                            $"файл настроек повреждён ({ex.Message}) — записывать поверх не стал, чтобы не добить чужие данные");
                    }

                    // Битый файл не затирается: он переименовывается, и причина потери настроек остаётся на диске.
                    string broken = QuarantinePath(DateTime.Now);
                    File.Move(_path, broken, overwrite: true);
                    backupPath = broken;
                }

                root = parsed as JsonObject ?? new JsonObject();
            }

            // Копия прежней версии — до записи новой. Одна копия, перезаписывается: это не архив,
            // а способ вернуть последние рабочие настройки.
            if (backupPath.Length == 0 && existing.Length > 0)
            {
                File.WriteAllText(BackupPath, existing, Encoding.UTF8);
                backupPath = BackupPath;
            }

            JsonObject updated = update(root) ?? root;

            // Временный файл — со своим именем на каждую запись. Раньше имя было постоянным
            // («settings.json.tmp»), а писать без блокировки здесь разрешено (mayWrite = true с
            // предупреждением). Два таких писателя затирали временный файл друг друга, и на место
            // настроек вставал обрывок — файл, который потом честно считался «повреждённым».
            string temp = TempPathNear(_path);
            try
            {
                File.WriteAllText(temp, updated.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                File.Move(temp, _path, overwrite: true);
            }
            finally
            {
                // Обрыв записи не должен оставлять мусор рядом с настройками.
                TryDelete(temp);
            }

            return SharedFileWriteResult.Ok(backupPath, lockProblem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return SharedFileWriteResult.Failed($"настройки не сохранились: {ex.Message}");
        }
    }

    /// <summary>
    /// Убрать битый файл в сторону, ничего не записывая взамен. Возвращает путь к копии или пусто,
    /// если убирать было нечего (файл отсутствует или читается нормально).
    /// </summary>
    public string QuarantineIfBroken(string reasonRu)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonRu);

        using WriteLock guard = WriteLock.Create(_mutexName, _fallbackMutexName, WriteTimeout, out _, out bool mayWrite);
        if (!mayWrite || !File.Exists(_path))
        {
            return string.Empty;
        }

        try
        {
            string text = File.ReadAllText(_path);
            try
            {
                if (JsonNode.Parse(text) is JsonObject)
                {
                    return string.Empty;
                }
            }
            catch (JsonException)
            {
                // Не разбирается как JSON — значит файл битый, уходим на переименование.
            }

            string broken = QuarantinePath(DateTime.Now);
            File.Move(_path, broken, overwrite: true);
            return broken;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Путь временного файла рядом с целевым: переименование в пределах одной папки атомарно,
    /// а уникальное имя не даёт двум писателям затереть друг друга.
    /// </summary>
    public static string TempPathNear(string path) =>
        path + "." + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture) + ".tmp";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не смогли убрать временный файл — это не повод объявлять запись неудачной:
            // настройки уже на месте, а мусор уберёт следующая запись или человек.
        }
    }

    private string QuarantinePath(DateTime moment) =>
        _path + ".broken-" + moment.ToString("yyyy-MM-dd-HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Блокировка общего файла. Мьютекс создаётся заново на каждую операцию и переиспользуется
    /// вторым писателем через имя: именно так мьютекс работает между процессами.
    /// </summary>
    private sealed class WriteLock : IDisposable
    {
        private readonly Mutex? _mutex;
        private readonly bool _owned;

        private WriteLock(Mutex? mutex, bool owned)
        {
            _mutex = mutex;
            _owned = owned;
        }

        /// <summary>
        /// <paramref name="mayProceed"/> = false только в одном случае: файл держит другой писатель
        /// дольше отведённого времени. Отсутствие мьютекса — это «продолжай, но предупреди»: потерять
        /// настройки хуже, чем записать их без защиты от гонки.
        /// </summary>
        public static WriteLock Create(
            string name,
            string fallbackName,
            TimeSpan timeout,
            out string problemRu,
            out bool mayProceed)
        {
            problemRu = string.Empty;
            mayProceed = true;

            Mutex? mutex = TryOpen(name) ?? TryOpen(fallbackName);
            if (mutex is null)
            {
                problemRu = "общая блокировка настроек недоступна в этой системе — запись выполнена без защиты от одновременной записи";
                return new WriteLock(null, false);
            }

            try
            {
                if (!mutex.WaitOne(timeout))
                {
                    mutex.Dispose();
                    problemRu = "файл настроек занят другим процессом дольше отведённого времени — запись отменена, чтобы не затереть чужие данные";
                    mayProceed = false;
                    return new WriteLock(null, false);
                }
            }
            catch (AbandonedMutexException)
            {
                // Второй писатель умер, не отпустив блокировку. Мьютекс при этом переходит нам:
                // файл мог остаться недописанным, но временный файл и переименование это переживают.
                problemRu = "предыдущий писатель настроек завершился, не отпустив блокировку — продолжаю с ней";
                return new WriteLock(mutex, true);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
            {
                mutex.Dispose();
                problemRu = $"блокировка настроек не взята ({ex.Message}) — запись выполнена без защиты от одновременной записи";
                return new WriteLock(null, false);
            }

            return new WriteLock(mutex, true);
        }

        private static Mutex? TryOpen(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            try
            {
                return new Mutex(initiallyOwned: false, name);
            }
            catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or IOException
                or NotSupportedException or PlatformNotSupportedException)
            {
                // Имя могло не подойти платформе (префикс Global\, слишком длинное, недопустимые символы) —
                // это не причина терять настройки: попробуем запасное имя, а потом запишем с предупреждением.
                return null;
            }
        }

        public void Dispose()
        {
            if (_mutex is null)
            {
                return;
            }

            try
            {
                if (_owned)
                {
                    _mutex.ReleaseMutex();
                }
            }
            catch (ApplicationException)
            {
                // Разблокировать не удалось (владение потеряно) — падать из-за этого нельзя.
            }
            finally
            {
                _mutex.Dispose();
            }
        }
    }
}
