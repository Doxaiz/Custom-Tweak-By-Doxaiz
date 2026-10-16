#!/usr/bin/env python3
"""Сборка сайта «как будет выглядеть готовая программа».

Отличие от docs/pingforge-ui-preview.html: это не рабочий документ проекта, а витрина
будущего интерфейса. Тексты короткие: у карточек твиков — одна строка, остальное прячется
в «Подробнее». Данные (твики, пресеты, маршруты, профили, показатели графика) по-прежнему
берутся из выгрузки каталога, а не пишутся руками.

Запуск:
    python3 tools/render_app_site.py /var/tmp/catalog.json docs/custom-tweak-by-doxaiz.html
"""

from __future__ import annotations

import json
import sys
from html import escape
from pathlib import Path

ACCENT = "#38bdf8"
ACCENT2 = "#4ade80"


def esc(value) -> str:
    return escape(str(value if value is not None else ""))


def trim(text: str, limit: int) -> str:
    """Обрезает текст по границе предложения или слова: лишнее не мешает читать."""
    text = " ".join((text or "").split())
    if len(text) <= limit:
        return text.strip(" ,;")
    cut = text[:limit]
    for stop in (". ", "! ", "? ", "; ", ", "):
        pos = cut.rfind(stop)
        if pos > limit * 0.5:
            return cut[: pos + 1].strip().rstrip(",;") if stop != ". " else cut[: pos + 1].strip()
    return cut.rsplit(" ", 1)[0].strip(" ,;") + "…"


def focus_class(code: str) -> str:
    return {"Ping": "f-ping", "Speed": "f-speed", "Fps": "f-fps",
            "Input": "f-input", "System": "f-system"}.get(code, "f-system")


def verdict_class(badge: str) -> str:
    return {"Recommended": "v-yes", "Optional": "v-maybe",
            "Caution": "v-care", "NotRecommended": "v-no"}.get(badge, "v-maybe")


def impact(value: int, title: str) -> str:
    if value < 0:
        return f'<span class="ok">{title} лучше</span>'
    if value > 0:
        return f'<span class="ok">{title} больше</span>'
    return f'<span class="dim">{title} без изменений</span>'


# ── карточка твика ────────────────────────────────────────────────────────────────────────────
def tweak_card(t: dict) -> str:
    checked = " checked" if t.get("enabledByDefault") else ""
    value = "подберут замеры" if t.get("isComputed") else f'<code>{esc(t["recommendedRawValue"])}</code>'
    presets = []
    if t.get("inLowPingPreset"):
        presets.append("низкий пинг")
    if t.get("inMaxSpeedPreset"):
        presets.append("скорость")
    if t.get("inFpsPreset"):
        presets.append("кадры")
    preset_chip = f'<span class="chip tiny">пресет: {esc(", ".join(presets))}</span>' if presets else ""
    # пробел между классами обязателен: без него получается «in-lowin-fps» и пресеты не подсвечиваются
    preset_classes = " ".join(code for code, present in (
        ("in-low", t.get("inLowPingPreset")), ("in-speed", t.get("inMaxSpeedPreset")),
        ("in-fps", t.get("inFpsPreset"))) if present)

    return f"""<article class="tw {focus_class(t.get("focus", "System"))} {preset_classes}">
  <label class="sw" title="Включить твик">
    <input type="checkbox"{checked} aria-label="Включить: {esc(t["titleRu"])}">
    <span class="sl"></span>
  </label>
  <div class="tw-main">
    <div class="tw-head">
      <span class="tw-title">{esc(t["titleRu"])}</span>
      <span class="v {verdict_class(t["badge"])}">{esc(t.get("verdictRu", ""))}</span>
      {preset_chip}
    </div>
    <p class="tw-sub">{esc(trim(t["whatItDoesRu"], 150))}</p>
    <details class="tw-more">
      <summary>Подробнее</summary>
      <p>{esc(trim(t["gamingJustificationRu"], 260))}</p>
      <p class="meta">{impact(t["ping"], "пинг")} · {impact(t["speed"], "скорость")} ·
        {impact(t["stability"], "стабильность")} · записываем: {value}</p>
      <p class="meta dim"><code>{esc(t["resourceLocator"])}</code> · {esc(trim(t["defaultValueNoteRu"], 90))}</p>
    </details>
  </div>
</article>"""


# ── график ───────────────────────────────────────────────────────────────────────────────────
PING_BEFORE = [120, 126, 118, 131, 124, 129, 143, 122, 127, 119, 135, 121, 124]
PING_AFTER = [88, 80, 78, 82, 79, 77, 86, 79, 78, 76, 81, 77, 78]


def polyline(values: list[float], top: float, width: int, height: int) -> str:
    step = width / max(1, len(values) - 1)
    scale = height / top
    return " ".join(f"{i * step:.1f},{height - min(v, top) * scale:.1f}" for i, v in enumerate(values))


def chart(title: str, unit: str, top: float, series: list[tuple[str, str, list]], width: int = 380, height: int = 120) -> str:
    grid = "".join(
        f'<line x1="0" y1="{height * k / 4:.0f}" x2="{width}" y2="{height * k / 4:.0f}" stroke="#1d2635" stroke-width="1"/>'
        for k in range(5))
    lines = "".join(
        f'<polyline fill="none" stroke="{color}" stroke-width="2.4" stroke-linejoin="round" '
        f'points="{polyline(values, top, width, height)}"/>'
        for _, color, values in series)
    legend = "".join(f'<span><i style="background:{color}"></i>{esc(label)}</span>' for label, color, _ in series)
    return f"""<figure class="chart">
  <figcaption>{esc(title)} <span class="dim">· {esc(unit)}</span></figcaption>
  <svg viewBox="-30 -6 {width + 46} {height + 24}" role="img" aria-label="{esc(title)}">{grid}{lines}
    <text x="0" y="{height + 16}" class="ax">0 с</text>
    <text x="{width}" y="{height + 16}" class="ax" text-anchor="end">60 с</text>
    <text x="-6" y="10" class="ax" text-anchor="end">{top:g}</text>
  </svg>
  <div class="legend">{legend}</div>
</figure>"""


def build(data: dict) -> str:
    tweaks = data["tweaks"]
    presets = {p["preset"]: p for p in data["presets"]}
    routes = data["routes"]
    perf = data["perf"]["profiles"]
    tray = data["tray"]
    chart_data = data["chart"]
    product = data["product"]

    groups: list[tuple[str, str, list]] = []
    order = [("Ping", "Пинг и сеть"), ("Speed", "Скорость"), ("Fps", "ФПС и кадры"),
             ("Input", "Задержка ввода"), ("System", "Система")]
    for code, title in order:
        items = [t for t in tweaks if t.get("focus") == code]
        if items:
            groups.append((code, title, items))

    groups_html = "\n".join(
        f'<h2 class="grp {focus_class(code)}">{esc(title)} <span class="count">{len(items)}</span></h2>\n'
        + "\n".join(tweak_card(t) for t in items)
        for code, title, items in groups)

    games_rows = "\n".join(
        f'<tr><td>{esc(g["gameRu"])}{" <span class=\'chip tiny\'>free</span>" if g.get("isFreeToPlay") else ""}</td>'
        f'<td class="dim mono">{esc(g["processName"])}</td>'
        f'<td class="dim">{esc(trim(", ".join(g.get("regionsRu") or []), 46))}</td>'
        f'<td class="dim">{esc(trim("; ".join(g.get("portsRu") or []) or "по факту соединения", 44))}</td></tr>'
        for g in routes["games"])

    dns_chips = "".join(
        f'<div class="chip card"><b>{esc(d["providerRu"].split(" (")[0])}</b>'
        f'<span class="mono">{esc(d["primary"])}</span><span class="dim">{esc(trim(d["noteRu"], 48))}</span></div>'
        for d in routes["dns"])

    path_rows = "\n".join(
        f'<tr><td>{esc(p["kindRu"])}</td><td>{"<span class=\'chip tiny free\'>0 ₽</span>" if p.get("isFree") else esc(p["costRu"])}</td>'
        f'<td class="dim">{esc(trim(p["safetyRu"], 60))}</td></tr>'
        for p in routes["pathKinds"])

    indicators = "".join(
        f'<div class="ind"><span class="ind-t">{esc(i["titleRu"])}</span>'
        f'<span class="ind-u">{esc(i["unitRu"])}</span></div>'
        for i in chart_data["indicators"])

    def stat(value: str, label: str, tone: str = "") -> str:
        return f'<div class="stat {tone}"><b>{esc(value)}</b><span>{esc(label)}</span></div>'

    stats = "".join([
        stat("78 мс", "пинг сейчас", "good"),
        stat("−42 мс", "было 120 мс"),
        stat("2,4 мс", "джиттер"),
        stat("0,0 %", "потери"),
        stat("92 Мбит/с", "скорость"),
        stat("360", "проб за минуту"),
    ])

    actions = "".join(
        f'<li><b>{esc(a["titleRu"])}</b><span class="dim">{esc(trim(a["detailRu"], 120))}</span></li>'
        for a in data["realtime"]["plannerActions"])

    profile_cards = "".join(
        f'<div class="card prof"><b>{esc(p["titleRu"])}</b>'
        f'<span class="dim">{p["monitorIntervalSeconds"]} с · {p["measurementsPerMinute"]} проб/мин</span>'
        f'<span class="dim">{"пауза на батарее" if p.get("pauseOnBattery") else "на батарее работает"}' +
        f'{" · пауза, если игра свёрнута" if p.get("pauseWhenMinimized") else ""}</span></div>'
        for p in perf)

    tray_items = "".join(
        f'<li>{esc(m["titleRu"])}</li>' for m in tray["runningMenu"]
    )

    # Пресеты — рабочие переключатели: выбор пресета подсвечивает, что в него входит.
    preset_items = [
        ("all", "Всё", len(tweaks)),
        ("low", "Низкий пинг", presets["LowLatencyGaming"]["tweakCount"]),
        ("speed", "Скорость", presets["MaxThroughput"]["tweakCount"]),
        ("fps", "Кадры", presets["FpsAndResponsiveness"]["tweakCount"]),
    ]
    preset_inputs = "".join(
        f'<input class="state" type="radio" name="ps" id="ps-{key}"{" checked" if index == 0 else ""}>'
        for index, (key, _, _) in enumerate(preset_items))
    preset_labels = "".join(
        f'<label class="seg" for="ps-{key}">{esc(title)}<span class="count">{count}</span></label>'
        for key, title, count in preset_items)
    preset_css = "\n".join([
        *[f'#ps-{key}:checked ~ .body .tw:not(.in-{key}){{opacity:.3}}' for key, _, _ in preset_items[1:]],
        *[f'#ps-{key}:checked ~ .body .tw.in-{key}{{box-shadow:0 0 0 1px #2ea8e6 inset}}'
          for key, _, _ in preset_items[1:]],
        *[f'#ps-{key}:checked ~ .body label[for="ps-{key}"]{{background:#1b2a44;color:var(--text)}}'
          for key, _, _ in preset_items],
    ])

    # Массовый выбор: три режима, которые действительно меняют вид ползунков — без скриптов.
    select_items = [("rec", "только рекомендуемое"), ("all", "включить все"), ("none", "отключить все")]
    select_inputs = "".join(
        f'<input class="state" type="radio" name="sel" id="sel-{key}"{" checked" if index == 0 else ""}>'
        for index, (key, _) in enumerate(select_items))
    select_labels = "".join(
        f'<label class="seg" for="sel-{key}">{esc(title)}</label>' for key, title in select_items)
    select_css = "\n".join([
        '#sel-all:checked ~ .body .tw .sl{background:linear-gradient(135deg,var(--accent2),#38bdf8);'
        'border-color:transparent}',
        '#sel-all:checked ~ .body .tw .sl::before{transform:translateX(19px);background:#04101f}',
        '#sel-none:checked ~ .body .tw .sl{background:#1b2740;border-color:var(--line2)}',
        '#sel-none:checked ~ .body .tw .sl::before{transform:none;background:#8fa0bd}',
        '#sel-all:checked ~ .body .tw .sw input,#sel-none:checked ~ .body .tw .sw input{pointer-events:none}',
        *[f'#sel-{key}:checked ~ .body label[for="sel-{key}"]{{background:#1b2a44;color:var(--text)}}'
          for key, _ in select_items],
    ])
    enabled_by_default = sum(1 for t in tweaks if t.get("enabledByDefault"))

    nav_items = [
        ("optimize", "Оптимизация"), ("tweaks", "Твики"), ("routes", "Маршрут"),
        ("journal", "Журнал"), ("tray", "Трей"), ("settings", "Настройки"), ("support", "Автор"),
    ]

    # Переключение экранов: скрытые радиокнопки и метки. Ни JavaScript, ни переходов по якорю —
    # поэтому меню работает и в предпросмотре, и в песочнице, и с выключенными скриптами.
    nav_inputs = "".join(
        f'<input class="state" type="radio" name="nav" id="nav-{anchor}"'
        f'{" checked" if index == 0 else ""}>'
        for index, (anchor, _) in enumerate(nav_items))
    nav_html = "".join(f'<label for="nav-{anchor}">{esc(title)}</label>' for anchor, title in nav_items)
    nav_css = "\n".join([
        *[f'#nav-{anchor}:checked ~ .body #page-{anchor}{{display:block}}' for anchor, _ in nav_items],
        *[f'#nav-{anchor}:checked ~ .body nav label[for="nav-{anchor}"]{{background:#182338;color:#e8eefc}}'
          for anchor, _ in nav_items],
        *[f'#nav-{anchor}:focus-visible ~ .body nav label[for="nav-{anchor}"]'
          f'{{outline:2px solid var(--accent);outline-offset:1px}}' for anchor, _ in nav_items],
    ])


    return f"""<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{esc(product["name"])} — как выглядит программа</title>
<style>
{CSS}
{nav_css}
{preset_css}
{select_css}
</style>
</head>
<body>

<div class="win">
  {nav_inputs}
  {preset_inputs}
  {select_inputs}

  <header class="bar">
    <span class="dots"><i></i><i></i><i></i></span>
    <span class="brand">
      <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="#04101f" stroke-width="2.6" stroke-linecap="round"><path d="M3 17l5-7 4 4 4-8 5 6"/></svg>
    </span>
    <span class="name">{esc(product["name"])}</span>
    <span class="chip live"><i class="dot"></i>оптимизация работает</span>
    <span class="grow"></span>
    <span class="chip dim">Fortnite · сервер 3.4.5.6</span>
    <a class="btn small" href="{esc(product["supportUrl"])}" target="_blank" rel="noopener">{esc(product["supportTitleRu"])} ↗</a>
  </header>

  <div class="body">
    <nav class="side">
      {nav_html}
      <div class="side-foot">{esc(product["shortName"])}<br><span class="dim">версия 1.0</span></div>
    </nav>

    <main class="content">

      <section class="page" id="page-optimize">
        <h1>Оптимизация соединения</h1>
        <div class="hero">
          <button class="pb">Оптимизировать соединение</button>
          <div class="hero-text">
            <div class="stats">{stats}</div>
            <p class="dim small">Fortnite · 3.4.5.6:9000 · маршрут для игрового сервера · QoS DSCP 46 · 6 твиков из 47</p>
          </div>
        </div>

        <div class="charts">
          {chart("Задержка до игрового сервера", "мс", 150, [("до", "#64748b", PING_BEFORE), ("после", ACCENT, PING_AFTER)])}
          {chart("Джиттер", "мс", 12, [("до", "#64748b", [9.8, 8.4, 7.6, 10.2, 6.9, 8.1, 7.2, 6.4, 7.8, 6.1, 7.4, 6.8, 6.2]),
                                        ("после", ACCENT2, [2.4, 2.1, 1.9, 2.6, 2.0, 1.8, 2.2, 1.7, 1.9, 2.1, 1.8, 1.6, 1.8])])}
        </div>
        <p class="dim small">Окно 60 секунд · 360 проб · замеры ICMP и TCP одновременно.</p>

        <h2 class="grp">Что сделала программа</h2>
        <ul class="acts">{actions}</ul>

        <h2 class="grp">Показатели, которые всегда на графике <span class="count">{len(chart_data["indicators"])}</span></h2>
        <div class="inds">{indicators}</div>

        <div class="row">
          <button class="btn ghost">Остановить и вернуть как было</button>
          <span class="dim small">Изменения снимаются сразу после остановки.</span>
        </div>
      </section>

      <section class="page" id="page-tweaks">
        <h1>Твики</h1>
        <div class="row toolbar">
          <span class="seg-title">Пресет</span>
          <div class="seg-group">{preset_labels}</div>
          <span class="chip">состав подсвечивается</span>
        </div>
        <div class="row toolbar">
          <span class="seg-title">Выбор</span>
          <div class="seg-group">{select_labels}</div>
          <span class="chip">по умолчанию включено {enabled_by_default} из {len(tweaks)}</span>
        </div>
        {groups_html}
        <p class="dim small">Перед применением — снапшот и точка восстановления. Твик никогда не пишется молча.</p>
      </section>

      <section class="page" id="page-routes">
        <h1>Маршрут до игры</h1>
        <div class="row">
          <label class="pick"><input type="radio" name="mode" checked><span>Автоматически по замерам</span></label>
          <label class="pick"><input type="radio" name="mode"><span>Вручную</span></label>
          <span class="chip">время теста: 60 с</span>
          <span class="chip ok">выигрыш подтверждён: 42 мс</span>
        </div>

        <h2 class="grp">Игры <span class="count">{len(routes["games"])}</span></h2>
        <table class="tbl">
          <thead><tr><th>Игра</th><th>Процесс</th><th>Регионы</th><th>Порты</th></tr></thead>
          <tbody>{games_rows}</tbody>
        </table>

        <h2 class="grp">Бесплатные публичные DNS <span class="count">{len(routes["dns"])}</span></h2>
        <div class="cards">{dns_chips}</div>

        <h2 class="grp">Способы сократить путь <span class="count">{len(routes["pathKinds"])}</span></h2>
        <table class="tbl">
          <thead><tr><th>Способ</th><th>Цена</th><th>Безопасность</th></tr></thead>
          <tbody>{path_rows}</tbody>
        </table>
        <p class="dim small">Бесплатных и безопасных способов — четыре. Настройки роутера программа не меняет: показывает, что сделать руками.</p>
      </section>

      <section class="page" id="page-journal">
        <h1>Журнал и откат</h1>
        <div class="row">
          <button class="btn primary">Вернуть всё как было (6)</button>
          <span class="chip ok">снапшот есть</span>
          <span class="chip">точка восстановления создана</span>
        </div>
        <table class="tbl">
          <thead><tr><th>Время</th><th>Что изменено</th><th>Действие</th></tr></thead>
          <tbody>
            <tr><td class="dim">14:02:11</td><td>Маршрут для 3.4.5.6 · QoS DSCP 46</td><td><button class="btn tiny ghost">отменить</button></td></tr>
            <tr><td class="dim">14:02:14</td><td>TCP: подтверждения, ускоренные подтверждения, начальный RTT</td><td><button class="btn tiny ghost">отменить</button></td></tr>
            <tr><td class="dim">14:02:20</td><td>Карта: энергосбережение, Green Ethernet, буферы приёма</td><td><button class="btn tiny ghost">отменить</button></td></tr>
            <tr><td class="dim">14:02:26</td><td>План питания «Высокая производительность»</td><td><button class="btn tiny ghost">отменить</button></td></tr>
          </tbody>
        </table>
        <p class="dim small">История хранится локально. Значения возвращаются ровно те, что были до применения.</p>
      </section>

      <section class="page" id="page-tray">
        <h1>Трей и ресурсы</h1>
        <div class="tray-wrap">
          <div class="tray">
            <b>{esc(product["shortName"])}</b>
            <ul>{tray_items}</ul>
          </div>
          <div class="cards">{profile_cards}</div>
        </div>
        <p class="dim small">Закрытие окна оптимизацию не выключает. Выход из трея — снимает изменения и останавливает замеры.</p>
      </section>

      <section class="page" id="page-settings">
        <h1>Настройки</h1>
        <div class="card">
          <label class="pick"><input type="checkbox" checked><span>Пауза на батарее</span></label>
          <label class="pick"><input type="checkbox" checked><span>Пауза, когда игра свёрнута</span></label>
          <label class="pick"><input type="checkbox" checked><span>Останавливать через 5 минут после выхода из игры</span></label>
          <label class="pick"><input type="checkbox" checked><span>Спрашивать точку восстановления перед применением</span></label>
          <label class="pick"><input type="checkbox" disabled><span class="dim">Телеметрия — недоступна: данные никуда не отправляются</span></label>
        </div>
        <div class="row">
          <button class="btn">Сохранить профиль в файл…</button>
          <button class="btn ghost">Загрузить профиль…</button>
        </div>
      </section>

      <section class="page" id="page-support">
        <h1>Поддержать автора</h1>
        <div class="card support">
          <p>Программа бесплатная, без телеметрии и подписок.</p>
          <a class="btn primary big" href="{esc(product["supportUrl"])}" target="_blank" rel="noopener">{esc(product["supportTitleRu"])} ↗</a>
          <p class="dim small">{esc(product["supportUrl"])}</p>
        </div>
      </section>

    </main>
  </div>

  <footer class="foot">
    <span>{esc(product["name"])}</span>
    <span class="dim">Визуальный макет интерфейса. Данные — из каталога проекта: {len(tweaks)} твиков, {len(routes["games"])} игр, {len(routes["dns"])} DNS, {len(perf)} профиля.</span>
  </footer>
</div>

</body>
</html>
"""


CSS = """
:root{
  --bg:#0a0e17;--panel:#0f1523;--card:#131b2b;--line:#1e2941;--line2:#2a3a58;
  --text:#e8eefc;--muted:#9fb0cc;--dim:#7b8aa6;--accent:#38bdf8;--accent2:#4ade80;
  --warn:#fbbf24;--bad:#f87171;--radius:14px;
}
*{box-sizing:border-box}
body{margin:0;background:radial-gradient(1200px 600px at 15% -10%,#132038,#0a0e17 60%);color:var(--text);
  font:14px/1.5 "Segoe UI",Roboto,system-ui,-apple-system,sans-serif;padding:24px}
a{color:var(--accent);text-decoration:none}
code{font-family:"Cascadia Mono",Consolas,monospace;font-size:12px;color:#cfe0f5}
.dim{color:var(--dim)}
.small{font-size:12.5px}
.mono{font-family:"Cascadia Mono",Consolas,monospace}
.grow{flex:1}

.win{max-width:1180px;margin:0 auto;background:var(--panel);border:1px solid var(--line);border-radius:18px;
  overflow:hidden;box-shadow:0 30px 80px rgba(0,0,0,.5)}
.bar{display:flex;align-items:center;gap:12px;padding:11px 16px;background:#0c1220;border-bottom:1px solid var(--line)}
.dots{display:flex;gap:6px}.dots i{width:10px;height:10px;border-radius:50%;background:#22304a;display:block}
.dots i:first-child{background:#3b4a68}
.brand{width:26px;height:26px;border-radius:8px;display:grid;place-items:center;
  background:linear-gradient(135deg,var(--accent),var(--accent2))}
.name{font-weight:650;font-size:15px}

.chip{display:inline-flex;align-items:center;gap:6px;background:#131c2c;border:1px solid var(--line);
  border-radius:999px;padding:4px 10px;font-size:12px;color:var(--muted)}
.chip.tiny{padding:1px 7px;font-size:11px}
.chip.ok{color:var(--accent2);border-color:#1f4034}
.chip.live{color:var(--accent2);border-color:#1f4034}
.chip .dot{width:7px;height:7px;border-radius:50%;background:var(--accent2);display:block}
.chip.free{color:var(--accent2)}

.body{display:grid;grid-template-columns:186px 1fr;min-height:560px}
nav.side{display:flex;flex-direction:column;gap:2px;padding:14px 10px;background:#0c1220;border-right:1px solid var(--line)}
nav.side label{padding:9px 11px;border-radius:10px;color:var(--muted);font-size:13.5px;cursor:pointer;display:block}
nav.side label:hover{background:#121a2b;color:var(--text)}
.side-foot{margin-top:auto;padding:12px 10px 4px;font-size:12px;border-top:1px solid var(--line)}

.content{padding:20px 22px 26px;max-width:100%}
h1{font-size:21px;margin:0 0 14px}
h2.grp{font-size:14px;text-transform:uppercase;letter-spacing:.08em;color:var(--muted);
  margin:22px 0 10px;padding-bottom:6px;border-bottom:1px solid var(--line);display:flex;gap:8px;align-items:baseline}
h2.grp .count{font-size:12px;color:var(--dim);letter-spacing:0}
h2.grp.f-ping{border-color:#22506e}h2.grp.f-speed{border-color:#1f4c33}
h2.grp.f-fps{border-color:#3b2f5e}h2.grp.f-input{border-color:#5a4a1c}

.page{display:none}
.state{position:absolute;width:1px;height:1px;opacity:0;pointer-events:none}
.state:focus-visible ~ .body{outline:none}

.hero{display:flex;gap:20px;align-items:center;flex-wrap:wrap;background:linear-gradient(150deg,#152036,#101725);
  border:1px solid var(--line2);border-radius:16px;padding:18px 20px}
.pb{flex:0 0 auto;border:0;cursor:pointer;font:700 15px/1 inherit;color:#04101f;padding:20px 26px;border-radius:999px;
  background:linear-gradient(135deg,var(--accent),var(--accent2));box-shadow:0 12px 30px rgba(56,189,248,.25)}
.hero-text{flex:1 1 320px;min-width:280px}
.stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(96px,1fr));gap:10px}
.stat{background:#0f1725;border:1px solid var(--line);border-radius:12px;padding:9px 11px}
.stat b{display:block;font-size:17px}
.stat span{font-size:11.5px;color:var(--dim)}
.stat.good b{color:var(--accent2)}

.charts{display:grid;grid-template-columns:repeat(auto-fit,minmax(320px,1fr));gap:14px;margin:16px 0 6px}
.chart{margin:0;background:#0d1420;border:1px solid var(--line);border-radius:14px;padding:10px 12px 6px}
.chart figcaption{font-size:12.5px;color:var(--muted);margin-bottom:4px}
.chart svg{width:100%;height:auto;display:block}
.ax{fill:#6f7f9c;font-size:10px}
.legend{display:flex;gap:14px;font-size:11.5px;color:var(--dim)}
.legend i{display:inline-block;width:12px;height:3px;border-radius:2px;margin-right:5px;vertical-align:middle}

.acts{list-style:none;margin:0;padding:0;display:grid;gap:8px}
.acts li{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:10px 13px;font-size:13.5px}
.acts li span{display:block;margin-top:2px}

.inds{display:flex;flex-wrap:wrap;gap:7px}
.ind{display:inline-flex;gap:6px;align-items:baseline;background:#0f1725;border:1px solid var(--line);
  border-radius:9px;padding:5px 9px;font-size:12.5px}
.ind-t{font-weight:600}
.ind-u{color:var(--dim);font-size:11.5px}
.row.presets{gap:8px;margin-bottom:6px}

.row{display:flex;gap:10px;align-items:center;flex-wrap:wrap;margin:12px 0}
.btn{background:#182338;border:1px solid var(--line2);color:var(--text);border-radius:11px;padding:9px 14px;
  font:600 13.5px/1.2 inherit;cursor:pointer}
.btn:hover{border-color:#3b527d}
.btn.ghost{background:transparent}
.btn.primary{background:linear-gradient(135deg,var(--accent),#2ea8e6);border-color:transparent;color:#04101f}
.btn.small{padding:7px 11px;font-size:12.5px;font-weight:500}
.btn.tiny{padding:4px 9px;font-size:12px;border-radius:8px}
.btn.big{padding:12px 20px;font-size:14.5px;display:inline-block}
.seg-title{font-size:12px;color:var(--dim);letter-spacing:.06em;text-transform:uppercase}
.seg-group{display:flex;gap:6px;flex-wrap:wrap}
label.seg{display:inline-flex;gap:7px;align-items:center;background:#131c2c;border:1px solid var(--line);
  border-radius:10px;padding:7px 11px;font-size:12.5px;color:var(--muted);cursor:pointer}
label.seg:hover{border-color:var(--line2);color:var(--text)}
label.seg .count{background:#0d1524;border-radius:7px;padding:1px 6px;font-size:11.5px;color:var(--accent)}
.toolbar{background:var(--card);border:1px solid var(--line);border-radius:13px;padding:10px 12px}
.search{background:#0d1524;border:1px solid var(--line2);border-radius:10px;color:var(--text);
  padding:9px 12px;font:13.5px inherit;min-width:220px}

.tw{position:relative;display:flex;gap:12px;align-items:flex-start;background:var(--card);border:1px solid var(--line);
  border-radius:13px;padding:12px 14px;margin:8px 0;border-left:3px solid #2b3a57}
.tw.f-ping{border-left-color:#38bdf8}.tw.f-speed{border-left-color:#4ade80}
.tw.f-fps{border-left-color:#a78bfa}.tw.f-input{border-left-color:#fbbf24}.tw.f-system{border-left-color:#64748b}
.tw-main{flex:1;min-width:0}
.tw-head{display:flex;gap:8px;align-items:baseline;flex-wrap:wrap}
.tw-title{font-weight:640;font-size:14.5px}
.tw-sub{margin:3px 0 0;color:var(--muted);font-size:13px;overflow-wrap:anywhere}
.v{font-size:12px;font-weight:650}
.v-yes{color:var(--accent2)}.v-maybe{color:var(--accent)}.v-care{color:var(--warn)}.v-no{color:var(--bad)}
.tw-more{margin-top:6px;border-top:1px dashed var(--line);padding-top:6px}
.tw-more summary{cursor:pointer;color:var(--dim);font-size:12.5px}
.tw-more p{font-size:13px;color:var(--muted);margin:7px 0 0;overflow-wrap:anywhere}
.meta{font-size:12px}
.ok{color:var(--accent2)}

.sw{position:relative;width:44px;height:25px;flex:0 0 44px;margin-top:2px;display:inline-block}
.sw input{position:absolute;inset:0;opacity:0;margin:0;cursor:pointer}
.sw .sl{position:absolute;inset:0;background:#1b2740;border:1px solid var(--line2);border-radius:999px;transition:.15s}
.sw .sl::before{content:"";position:absolute;width:17px;height:17px;left:4px;top:3px;border-radius:50%;background:#8fa0bd;transition:.15s}
.sw input:checked + .sl{background:linear-gradient(135deg,var(--accent2),#38bdf8);border-color:transparent}
.sw input:checked + .sl::before{transform:translateX(19px);background:#04101f}

.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:10px}
.card{background:var(--card);border:1px solid var(--line);border-radius:13px;padding:12px 14px;font-size:13px}
.chip.card{display:flex;flex-direction:column;align-items:flex-start;gap:3px;border-radius:12px;padding:10px 12px}
.card.prof b{display:block;margin-bottom:3px}
.card.prof span{display:block;font-size:12px}
.tbl{width:100%;border-collapse:collapse;font-size:13px}
.tbl th{text-align:left;font-size:11.5px;text-transform:uppercase;letter-spacing:.06em;color:var(--dim);
  padding:6px 8px;border-bottom:1px solid var(--line2)}
.tbl td{padding:7px 8px;border-bottom:1px solid var(--line);vertical-align:top}
.pick{display:inline-flex;gap:8px;align-items:center;background:var(--card);border:1px solid var(--line);
  border-radius:10px;padding:8px 12px;font-size:13.5px}
#settings .pick{display:flex;margin:6px 0;background:transparent;border:0;padding:2px 0}
.tray-wrap{display:grid;grid-template-columns:minmax(220px,260px) 1fr;gap:16px;align-items:start}
.tray{background:#0d1524;border:1px solid var(--line2);border-radius:13px;padding:11px 13px;font-size:13px}
.tray b{font-size:12.5px;color:var(--dim);font-weight:600;text-transform:uppercase;letter-spacing:.06em}
.tray ul{list-style:none;margin:8px 0 0;padding:0}
.tray li{padding:6px 2px;border-top:1px solid var(--line)}
.support{text-align:center;padding:22px}
.support p{margin:0 0 12px;color:var(--muted)}
.foot{display:flex;gap:12px;flex-wrap:wrap;justify-content:space-between;align-items:center;
  padding:11px 16px;border-top:1px solid var(--line);background:#0c1220;font-size:12.5px}
#settings .row{margin-top:14px}
@media(max-width:900px){.body{grid-template-columns:1fr}nav.side{flex-direction:row;flex-wrap:wrap;border-right:0;
  border-bottom:1px solid var(--line)}.side-foot{display:none}.tray-wrap{grid-template-columns:1fr}}
@media(max-width:640px){body{padding:10px}h1{font-size:19px}.tw{flex-direction:row;gap:10px}
  .bar{flex-wrap:wrap;gap:8px;padding:10px 12px}.grow{display:none}
  .name{font-size:14px;white-space:nowrap}.chip.dim{margin-left:auto;order:4}.chip.live{order:5}
  .bar .btn.small{order:6;flex:1 1 100%;text-align:center}}
"""


def main() -> int:
    src = Path(sys.argv[1] if len(sys.argv) > 1 else "/var/tmp/catalog.json")
    out = Path(sys.argv[2] if len(sys.argv) > 2 else "docs/custom-tweak-by-doxaiz.html")

    data = json.loads(src.read_text(encoding="utf-8"))
    html = build(data)

    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(html, encoding="utf-8")

    words = len(html.split())
    print(f"Готово: {out} ({len(html) // 1024} КБ)")
    print(f"Твиков на странице: {data['tweakCount']} · слов разметки: {words}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
