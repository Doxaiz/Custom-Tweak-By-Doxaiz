namespace PingForge.Probing.Access;

/// <summary>Что показал замер доступа к одному имени. Ровно то, что измерено, — без догадок.</summary>
public enum AccessOutcome
{
    /// <summary>Не измеряли.</summary>
    NotMeasured,

    /// <summary>Соединение прошло, сертификат предъявлен — на этом пути блокировки не видно.</summary>
    Ok,

    /// <summary>TCP открылся, но TLS-рукопожатие оборвано до сертификата.</summary>
    TlsReset,

    /// <summary>TCP открылся, TLS-рукопожатие молчит до таймаута.</summary>
    TlsTimeout,

    /// <summary>Вместо сайта пришёл HTTP-ответ с признаками заглушки провайдера.</summary>
    HttpStub,

    /// <summary>Порт закрыт: соединение отвергнуто.</summary>
    ConnectionRefused,

    /// <summary>Соединение не установилось: таймаут или сеть недоступна.</summary>
    Unreachable,

    /// <summary>Имя не разрешилось в адрес.</summary>
    DnsFailed,
}

/// <summary>Сырые наблюдения одного замера. Классификатор ничего не додумывает — только читает эти поля.</summary>
public sealed record AccessObservation(
    string Host,
    int Port = 443,
    int? DnsMilliseconds = null,
    int AddressCount = 0,
    string? FirstAddress = null,
    int? ConnectMilliseconds = null,
    string? ConnectFailure = null,
    bool ConnectionRefused = false,
    int? TlsMilliseconds = null,
    string? TlsFailure = null,
    int? HttpStatus = null,
    string? HttpLocation = null,
    bool HttpLooksLikeStub = false,
    string? DnsFailure = null)
{
    public static AccessObservation NotMeasured(string host) => new(host);
}

/// <summary>Вывод: что произошло, что это значит и что с этим делать. Три разных поля, а не одно.</summary>
public sealed record AccessDiagnosis(
    AccessOutcome Outcome,
    string TitleRu,
    string DetailRu,
    string NextStepRu)
{
    public bool IsProblem => Outcome is not (AccessOutcome.Ok or AccessOutcome.NotMeasured);
}

/// <summary>
/// Классификатор доступа к сайту.
///
/// Отдельно от сети — чтобы правила проверялись тестами без интернета. Главное правило: программа
/// называет только то, что видно в замере. «TLS оборван» и «провайдер блокирует» — не одно и то же:
/// так же ведёт себя сервер, который сам рвёт соединение, поэтому вывод честно говорит про два варианта
/// и советует проверить в другой сети.
///
/// Программа не обходит блокировки: у неё нет туннеля, релея и перехвата пакетов. Здесь только
/// диагностика и честный следующий шаг.
/// </summary>
public static class AccessClassifier
{
    /// <summary>Слова, по которым видно страницу-заглушку вместо сайта.</summary>
    public static readonly IReadOnlyList<string> StubWords = new[]
    {
        "Роскомнадзор",
        "Роскомнадзора",
        "заблокирован",
        "запрещён",
        "blocked by",
        "restricted access",
        "zapret-info",
    };

    /// <summary>Признаки того, что ответ — заглушка, а не сайт.</summary>
    public static bool LooksLikeStub(int? status, string? location, string? body)
    {
        if (status is 451)
        {
            return true;
        }

        if (!string.IsNullOrEmpty(location))
        {
            string low = location.ToLowerInvariant();
            if (low.Contains("zapret-info", StringComparison.Ordinal)
                || low.Contains("blocked", StringComparison.Ordinal)
                || low.Contains("warning.", StringComparison.Ordinal)
                || low.Contains("block.", StringComparison.Ordinal)
                || low.Contains("eais.", StringComparison.Ordinal)
                || low.Contains("roskomnadzor", StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        foreach (string word in StubWords)
        {
            if (body.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Единственное место, где наблюдения превращаются в текст для человека.</summary>
    public static AccessDiagnosis Classify(AccessObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return Describe(observation);
    }

    private static AccessDiagnosis Describe(AccessObservation o)
    {
        string host = o.Host;
        string port = $"{host}:{o.Port}";

        if (o.DnsFailure is not null)
        {
            return new AccessDiagnosis(
                AccessOutcome.DnsFailed,
                "Имя не разрешилось в адрес",
                $"Запрос DNS по имени «{host}» не дал адреса ({o.DnsFailure}). Так бывает при подмене или " +
                "фильтрации DNS у провайдера — и точно так же при опечатке в имени или когда DNS-сервер " +
                "просто не отвечает.",
                "Проверьте написание имени и повторите замер. Если не разрешается только одно имя, а другие " +
                "работают — попробуйте другой DNS-сервер в разделе «Твики» (DNS меняется с откатом).");
        }

        if (o.AddressCount == 0 && o.ConnectMilliseconds is null && o.ConnectFailure is null)
        {
            return new AccessDiagnosis(
                AccessOutcome.NotMeasured,
                "Замер не выполнялся",
                "Программа ещё не проверяла это имя. Сам по себе запуск программы ничего не измеряет.",
                "Впишите имя сайта и нажмите «Проверить доступ».");
        }

        if (o.ConnectionRefused)
        {
            return new AccessDiagnosis(
                AccessOutcome.ConnectionRefused,
                "Порт закрыт",
                $"Адрес {o.FirstAddress ?? "неизвестен"} ответил отказом на порт {o.Port}. Это не блокировка: " +
                "порт просто закрыт — так бывает у сайта, который не принимает соединения по этому порту.",
                "Если адрес получен от провайдера подменой, отказ бывает и при блокировке по IP: проверьте " +
                "то же имя в другой сети. Если и там отказ — дело в самом сайте.");
        }

        if (o.ConnectFailure is not null)
        {
            return new AccessDiagnosis(
                AccessOutcome.Unreachable,
                "Соединение не установилось",
                $"Порт {o.Port} по адресу {o.FirstAddress ?? "неизвестен"} не открылся: {o.ConnectFailure}. " +
                "Это не обязательно блокировка: так же ведёт себя и выключенный сервер, и чужой файрвол, " +
                "и обрыв связи на пути.",
                "Проверьте интернет в целом (откройте любой другой сайт). Если остальное открывается — " +
                "проверьте это имя в другой сети: мобильный интернет покажет, дело в провайдере или в самом сайте.");
        }

        if (o.ConnectMilliseconds is null)
        {
            return new AccessDiagnosis(
                AccessOutcome.Unreachable,
                "Соединение не установилось",
                $"Адрес {(o.FirstAddress ?? "неизвестен")} не ответил на порт {o.Port} за отведённое время. " +
                "Это не обязательно блокировка: так же ведёт себя выключенный сервер, чужой файрвол и " +
                "обрыв связи на пути.",
                "Проверьте интернет в целом (откройте любой другой сайт). Если остальное открывается — " +
                "проверьте это имя в другой сети: мобильный интернет покажет, дело в провайдере или в самом сайте.");
        }

        if (o.HttpLooksLikeStub)
        {
            string where = o.HttpLocation is null ? string.Empty : $" Перенаправление: {o.HttpLocation}.";
            return new AccessDiagnosis(
                AccessOutcome.HttpStub,
                "Вместо сайта пришла заглушка",
                $"На обычный HTTP-запрос по адресу {port} пришёл ответ {(o.HttpStatus?.ToString() ?? "без кода")} " +
                $"с признаками страницы-заглушки.{where} Это уже не догадка: провайдер подменил ответ сайта.",
                "Обход блокировки программа не делает — у неё нет ни туннеля, ни перехвата пакетов. " +
                "Что делать: сменить DNS в разделе «Твики», открыть сайт через мобильный интернет или " +
                "воспользоваться отдельным средством обхода DPI либо VPN — это ваше решение и ваша " +
                "ответственность, программа такие средства не запускает.");
        }

        if (o.TlsMilliseconds is not null)
        {
            return new AccessDiagnosis(
                AccessOutcome.Ok,
                "Доступ есть",
                $"Соединение с {port} установлено, TLS-рукопожатие прошло за {o.TlsMilliseconds} мс, сертификат " +
                $"предъявлен сервером. Значит на этом пути блокировки именно этого имени сейчас не видно.",
                "Ничего делать не нужно. Если сайт всё равно не открывается в браузере — дело в браузере " +
                "(расширение, кэш, QUIC) или в самом сайте: проверьте тот же адрес в другом браузере.");
        }

        if (o.TlsFailure is not null)
        {
            // Самый важный случай: соединение открылось, а TLS оборвался. Два варианта, и мы не врём,
            // что знаем, какой именно.
            bool looksTimedOut = o.TlsFailure.Contains("таймаут", StringComparison.OrdinalIgnoreCase)
                || o.TlsFailure.Contains("timeout", StringComparison.OrdinalIgnoreCase);

            return new AccessDiagnosis(
                looksTimedOut ? AccessOutcome.TlsTimeout : AccessOutcome.TlsReset,
                looksTimedOut ? "TLS-рукопожатие молчит" : "TLS-рукопожатие оборвано",
                $"TCP-соединение с {port} открылось, но сертификат не пришёл: {o.TlsFailure}. " +
                "Так выглядит фильтрация по имени сайта в TLS — и точно так же выглядит сервер, который сам " +
                "рвёт соединение. По одному запросу эти два случая не различаются, и программа не делает вид, " +
                "что различает.",
                "Проверьте тот же адрес в другой сети (мобильный интернет или другое подключение). " +
                "Открывается там — дело в вашем провайдере. Не открывается нигде — дело в сайте. " +
                "Обхода блокировки в программе нет; если фильтрация подтвердилась, работающие способы — " +
                "отдельное средство обхода DPI или VPN, ваше решение и ваша ответственность.");
        }

        if (o.HttpStatus is not null)
        {
            return new AccessDiagnosis(
                AccessOutcome.Unreachable,
                "Сайт ответил только по HTTP",
                $"Порт {o.Port} не ответил TLS, а обычный HTTP дал код {o.HttpStatus}. Значит сервер жив, " +
                "но защищённое соединение не поднялось.",
                "Повторите замер через минуту: возможно, сайт был перегружен. Если повторяется — проверьте " +
                "адрес в другой сети, чтобы понять, провайдер это или сайт.");
        }

        return new AccessDiagnosis(
            AccessOutcome.Unreachable,
            "Ответа нет",
            $"Замер до {port} не дал ни TLS, ни HTTP-ответа.",
            "Проверьте интернет и повторите замер. Если не отвечает только это имя — проверьте его в другой сети.");
    }
}
