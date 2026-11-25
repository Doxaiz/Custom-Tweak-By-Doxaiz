/*
 Проверка макета интерфейса: переходы по меню, оглавление, переходы «назад/далее», чтение без скриптов,
 а также тема: палитра макета сверяется с App.xaml, а контраст текста — по WCAG 2.1.
 Запуск (нужен jsdom):
     npm install jsdom            # или NODE_PATH=/путь/к/node_modules
     node tools/check_ui_preview.js docs/pingforge-ui-preview.html
 Скрипт падает с кодом 1, если хоть одна проверка не прошла.
*/
const fs = require('fs');
const path = require('path');
const { JSDOM } = require('jsdom');

const FILE = process.argv[2] || 'docs/pingforge-ui-preview.html';
const html = fs.readFileSync(FILE, 'utf8');

const problems = [];
const ok = [];
const check = (name, cond, extra = '') => (cond ? ok : problems).push(name + (extra ? ' — ' + extra : ''));

const dom = new JSDOM(html, { runScripts: 'dangerously', pretendToBeVisual: true, url: 'https://preview.local/' });
const errs = [];
dom.window.addEventListener('error', e => errs.push(String(e.message || e)));
const { window } = dom;
window.addEventListener('unhandledrejection', e => errs.push('promise: ' + e.reason));

setTimeout(async () => {
  const d = window.document;
  const visible = () => [...d.querySelectorAll('section.tab')].filter(s => s.classList.contains('active')).map(s => s.id);
  const menu = [...d.querySelectorAll('nav.side-nav a[data-group]')];
  const ids = [...d.querySelectorAll('section.tab')].map(s => s.id);

  // ── клики по меню: страница не должна перезагружаться, а раздел — открываться ────────────────
  check('пунктов меню 8: работа, результат, настройка и правда', menu.length === 8, `нашли ${menu.length}`);
  menu.forEach(a => {
    a.dispatchEvent(new window.MouseEvent('click', { bubbles: true, cancelable: true }));
    const shown = visible();
    check(`клик «${a.dataset.group}» показывает раздел`, shown.length > 0, shown.join(','));
  });
  check('после клика в адресе есть якорь', /^#/.test(window.location.hash), window.location.hash);
  const chip = d.getElementById('whereAmI');
  check('чип «открыт раздел» заполнен', !!chip && chip.textContent.trim().length > 0);
  check('ошибок JS после кликов по меню нет', errs.length === 0, errs.join(' | '));

  // ── оглавление и переходы: обычные ссылки с якорем ──────────────────────────────────────────
  const tiles = [...d.querySelectorAll('a.toc-tile')];
  check('плиток в оглавлении 6', tiles.length === 6, `нашли ${tiles.length}`);
  check('ссылки оглавления ведут в существующие разделы',
    tiles.every(t => ids.includes(t.getAttribute('href').slice(1))));

  const navs = [...d.querySelectorAll('.secnav')];
  check('переходы есть во всех 9 частях', navs.length === 9, `нашли ${navs.length}`);
  check('в первой части «далее» ведёт во вторую', navs[0].innerHTML.includes('href="#apply"'));
  check('в последней части стоит «это последний раздел»', !!navs[navs.length - 1].querySelector('span.end'));
  check('в каждой части есть ссылка на оглавление', navs.every(n => !!n.querySelector('a[href="#top"]')));

  // hashchange приходит асинхронно — как в браузере
  const wait = () => new Promise(r => setTimeout(r, 30));
  window.location.hash = '#routing';
  await wait();
  check('переход по якорю из оглавления открывает нужный раздел', visible().includes('routing'), visible().join(','));
  window.location.hash = '#overview';
  await wait();
  check('возврат на первый раздел работает', visible().join() === 'overview', visible().join());

  // ── меню: группы, значки, подписи ──────────────────────────────────────────────────────────
  check('групп в меню 3: работа, что получилось, настройка и правда',
    d.querySelectorAll('.navgroup').length === 3, `${d.querySelectorAll('.navgroup').length}`);
  check('значков у пунктов 8', d.querySelectorAll('nav.side-nav .navbadge').length === 8,
    `${d.querySelectorAll('nav.side-nav .navbadge').length}`);
  check('у каждого пункта есть номер, подпись и значок',
    menu.every(a => a.querySelector('.stepnum') && a.querySelector('.steptext b')
      && a.querySelector('.steptext span') && a.querySelector('.navbadge')));
  check('текущий раздел помечен aria-current ровно один',
    menu.filter(a => a.getAttribute('aria-current') === 'true').length === 1);

  // ── содержимое, которое обязано остаться на месте ──────────────────────────────────────────
  const TWEAKS = 47; // столько твиков в каталоге проекта на момент рендера
  const cards = [...d.querySelectorAll('#tweaks .tweak')];
  check(`карточек твиков ${TWEAKS}`, cards.length === TWEAKS, `${cards.length}`);
  check('таблиц значений у карточек столько же, сколько карточек',
    d.querySelectorAll('#tweaks .tweak .t-block table').length === TWEAKS,
    `${d.querySelectorAll('#tweaks .tweak .t-block table').length}`);
  check('строк «что применить» по три на карточку', d.querySelectorAll('#tweaks .valset .pick').length === TWEAKS * 3,
    `${d.querySelectorAll('#tweaks .valset .pick').length}`);
  check('карточек «пинг» 12, «скорость» 7, «кадры» 13, «ввод» 10, «система» 5',
    ['Ping:12', 'Speed:7', 'Fps:13', 'Input:10', 'System:5'].every(pair => {
      const [focus, expected] = pair.split(':');
      return cards.filter(c => c.dataset.focus === focus).length === Number(expected);
    }),
    [...new Set(cards.map(c => c.dataset.focus))].join(','));

  // ── карточки: ползунки вместо старых details-строк ────────────────────────────────────────
  const toggles = [...d.querySelectorAll('#tweaks .t-toggle')];
  check('ползунков включения столько же, сколько карточек', toggles.length === TWEAKS, `${toggles.length}`);
  check('по умолчанию включено только рекомендованное (23)', toggles.filter(t => t.checked).length === 23,
    `${toggles.filter(t => t.checked).length}`);
  check('у каждой карточки ровно один вердикт «стоит включать или нет»',
    cards.every(c => c.querySelectorAll('.verdict').length === 1),
    cards.filter(c => c.querySelectorAll('.verdict').length !== 1).map(c => c.dataset.code).join(','));
  check('в каждой карточке не меньше шести блоков подробностей',
    cards.every(c => c.querySelectorAll('.t-block').length >= 6),
    `${Math.min(...cards.map(c => c.querySelectorAll('.t-block').length))}`);
  check('блоки подробностей не оформлены тегом section (иначе ломается счёт частей)',
    d.querySelectorAll('#tweaks .tweak section').length === 0);
  check('длинный текст переносится, а не вылезает за рамку',
    /overflow-wrap:anywhere/.test(html) && /word-break:break-word/.test(html));
  check('метки карточки переносятся по строкам, а не растягивают шапку',
    /t-badges\{display:flex;flex-wrap:wrap/.test(html));

  // ── подкатегории и массовый выбор ─────────────────────────────────────────────────────────
  const focusLinks = [...d.querySelectorAll('.focusbar a')];
  check('плиток подкатегорий 5: пинг, скорость, фпс, ввод, система', focusLinks.length === 5, `${focusLinks.length}`);
  check('каждая плитка подкатегории ведёт на существующий заголовок',
    focusLinks.every(a => !!d.querySelector(a.getAttribute('href'))));
  check('заголовки подкатегорий пронумерованы якорями focus-*',
    d.querySelectorAll('#tweaks [id^="focus-"]').length === 5,
    `${d.querySelectorAll('#tweaks [id^="focus-"]').length}`);

  const pickCounter = d.getElementById('pickCount');
  check('кнопка «только рекомендуемое» оставляет 23 включённых', (() => {
    d.getElementById('pickRecommended').click();
    return toggles.filter(t => t.checked).length === 23;
  })(), `${toggles.filter(t => t.checked).length}`);
  check('кнопка «включить все» включает все твики', (() => {
    d.getElementById('pickAll').click();
    return toggles.every(t => t.checked);
  })());
  check('кнопка «отключить все» снимает все ползунки', (() => {
    d.getElementById('pickNone').click();
    return toggles.every(t => !t.checked);
  })());
  check('счётчик выбора обновляется после массовых кнопок',
    pickCounter && pickCounter.textContent.includes('0 из ' + TWEAKS), pickCounter && pickCounter.textContent);
  check('карточки помечаются включёнными', (() => {
    toggles[0].checked = true;
    toggles[0].dispatchEvent(new d.defaultView.Event('change'));
    return toggles[0].closest('.tweak').classList.contains('on');
  })());

  // ── пресеты: составы обязаны быть разными (отдельное требование) ───────────────────────────
  const presetCounts = [...d.querySelectorAll('.presetcard .cnt')].map(e => parseInt(e.textContent, 10));
  check('в блоке пресетов показаны четыре состава', presetCounts.length === 4, presetCounts.join('/'));
  check('составы пресетов разные: пинг 19, скорость 5, кадры 23', (() => {
    const [low, fast, fps] = presetCounts;
    return low === 19 && fast === 5 && fps === 23 && new Set([low, fast, fps]).size === 3;
  })(), presetCounts.join('/'));

  check('кнопка сохранения конфига лежит внутри «Настроек»',
    !!d.querySelector('#settings .btn'));
  check('выбор времени теста маршрута на месте', d.querySelectorAll('#routing input[name=routeTime]').length > 0);
  check('калькулятор маршрутов считает: чип заполняется', (() => {
    const btn = d.getElementById('btnPickRoute');
    if (!btn) return false;
    btn.click();
    return (d.getElementById('routeChip').textContent || '').trim().length > 0;
  })());

  // ── чтение без скриптов ────────────────────────────────────────────────────────────────────
  const d2 = new JSDOM(html).window.document;
  const shownWithoutJs = [...d2.querySelectorAll('section.tab')].filter(s => !s.hasAttribute('hidden')).length;
  check('без скриптов видно все 9 частей', shownWithoutJs === 9, `${shownWithoutJs}`);
  check('оглавление есть и без скриптов', d2.querySelectorAll('a.toc-tile').length === 6);
  check('переходы есть и без скриптов', d2.querySelectorAll('.secnav').length === 9);
  check('блоков «простыми словами» 8', d2.querySelectorAll('.simple').length === 8,
    `${d2.querySelectorAll('.simple').length}`);
  check('шагов «как пользоваться» 3', d2.querySelectorAll('.three .step').length === 3);
  check('терминов в словаре 6', d2.querySelectorAll('.glossary dt').length === 6);
  check('кнопок конфига в боковой колонке нет', d2.querySelectorAll('.side-tip .btn').length === 0);

  // ── готовность: веса сходятся к 100 ────────────────────────────────────────────────────────
  const weights = [...d2.querySelectorAll('#progress tbody tr')].map(tr =>
    parseInt(tr.children[1].textContent, 10));
  const weightSum = weights.reduce((a, b) => a + b, 0);
  check('сумма весов в таблице готовности ровно 100', weightSum === 100, `${weightSum}`);


  // ── название, поддержка автора, справочник маршрутов, график, экономный режим ───────────────
  check('в шапке новое название: Custom Tweak By Doxaiz',
    /Custom Tweak By Doxaiz/.test(d.querySelector('.logo').textContent), d.querySelector('.logo').textContent.trim());
  check('название стоит и в заголовке страницы', /Custom Tweak By Doxaiz/.test(d.title), d.title);
  const support = [...d.querySelectorAll('a[href="https://dalink.to/doxaiz"]')];
  check('кнопки «Поддержать автора» ведут только на dalink.to/doxaiz (не меньше трёх мест)',
    support.length >= 3 && support.length === [...d.querySelectorAll('a.btn')].filter(a => /Поддержать автора/.test(a.textContent)).length,
    `${support.length}`);
  check('ссылка поддержки открывается в новой вкладке и без передачи источника',
    support.every(a => a.getAttribute('target') === '_blank' && /noopener/.test(a.getAttribute('rel') || '')));

  const routeTables = [...d.querySelectorAll('#routing table')];
  check('справочник маршрутов: три таблицы (игры, DNS, способы)', routeTables.length >= 3, `${routeTables.length}`);
  check('в справочнике 22 игры', (() => {
    const first = routeTables.find(t => /Игра/.test(t.querySelector('thead').textContent));
    return first && first.querySelectorAll('tbody tr').length === 22;
  })(), 'строк: ' + (routeTables[0] ? routeTables[0].querySelectorAll('tbody tr').length : 0));
  check('в справочнике 5 бесплатных DNS', (() => {
    const t = routeTables.find(x => /Резолвер/.test(x.querySelector('thead').textContent));
    return t && t.querySelectorAll('tbody tr').length === 5;
  })());
  check('в справочнике 8 способов сократить путь', (() => {
    const t = routeTables.find(x => /Способ/.test(x.querySelector('thead').textContent));
    return t && t.querySelectorAll('tbody tr').length === 8;
  })());
  check('сказано, что бесплатных и безопасных способов четыре (а не «пять», как было ошибочно)',
    /бесплатных и безопасных способов — 4/i.test(d.getElementById('routing').textContent) &&
    !/способов — пять/i.test(d.getElementById('routing').textContent));
  check('второй канал честно помечен платным: не в списке бесплатных',
    (() => {
      const t = [...d.querySelectorAll('#routing table')].find(x => /Способ/.test(x.querySelector('thead').textContent));
      if (!t) return false;
      const row = [...t.querySelectorAll('tbody tr')].find(r => /Второй канал/.test(r.textContent));
      return !!row && !/бесплатный способ/.test(row.textContent);
    })());
  check('у бесплатных способов стоит метка «бесплатный способ»',
    (() => {
      const t = [...d.querySelectorAll('#routing table')].find(x => /Способ/.test(x.querySelector('thead').textContent));
      if (!t) return false;
      return [...t.querySelectorAll('tbody tr')].filter(r => /бесплатный способ/.test(r.textContent)).length === 4;
    })());

  const charts = [...d.querySelectorAll('#diagnostics .chart')];
  check('панелей графика сети 4', charts.length === 4, `${charts.length}`);
  check('в каждой панели есть нарисованный ряд данных',
    charts.every(c => c.querySelector('polyline, rect')), charts.map(c => c.querySelector('polyline, rect') ? 'да' : 'нет').join(','));
  check('под каждой панелью подписаны «было» и «стало»',
    charts.every(c => /было/.test(c.textContent) && /стало/.test(c.textContent)));
  check('в графике подписаны единицы измерения',
    ['Мбит/с', 'мс', '%'].every(u => d.getElementById('diagnostics').textContent.includes(u)));
  check('таблица «было / стало» показывает не меньше восьми показателей', (() => {
    const t = [...d.querySelectorAll('#diagnostics table')].find(x => /Показатель/.test(x.querySelector('thead').textContent));
    return t && t.querySelectorAll('tbody tr').length >= 8;
  })());
  check('график честно помечен как демонстрационный пример',
    /демонстрационные|пример макета/i.test(d.getElementById('diagnostics').textContent));
  check('таблица обязательных показателей пришла из кода (11 строк)', (() => {
    const t = [...d.querySelectorAll('#diagnostics table')].find(x => /Почему он в обязательном списке/.test(x.textContent));
    return t && t.querySelectorAll('tbody tr').length === 11;
  })(), (() => {
    const t = [...d.querySelectorAll('#diagnostics table')].find(x => /Почему он в обязательном списке/.test(x.textContent));
    return t ? `${t.querySelectorAll('tbody tr').length}` : 'таблицы нет';
  })());
  check('в графике подписано окно наблюдения и число проб',
    /окно наблюдения/i.test(d.getElementById('diagnostics').textContent) &&
    /точек в окне/i.test(d.getElementById('diagnostics').textContent));
  check('правило «нет измерения — нет нуля» написано и в графике',
    /нет измерения — нет нуля/i.test(d.getElementById('diagnostics').textContent));

  check('профилей потребления три: экономный, обычный, максимум', (() => {
    const t = [...d.querySelectorAll('#settings table')].find(x => /Профиль потребления/.test(x.querySelector('thead').textContent));
    return t && t.querySelectorAll('tbody tr').length === 3 &&
      /экономно/i.test(t.textContent) && /обычн/i.test(t.textContent) && /максим|производительн/i.test(t.textContent);
  })());
  check('пауза на батарее и при свёрнутой игре подписаны в таблице профилей',
    /Пауза на батарее/.test(d.getElementById('settings').textContent) &&
    /свёрнута/i.test(d.getElementById('settings').textContent));
  const trayCards = [...d.querySelectorAll('#settings .card')].filter(c => /^Трей:/.test((c.querySelector('h4') || {}).textContent || ''));
  check('меню трея показаны тремя состояниями', trayCards.length === 3, `${trayCards.length}`);
  check('в меню трея есть пункт возврата изменений', /Вернуть как было/.test(d.getElementById('settings').textContent));
  check('подсказка трея не пишет «0 мс», когда замеров не было',
    /Пинга нет — замеров пока не было/.test(d.getElementById('settings').textContent));

  check('строк в таблице готовности 16', d2.querySelectorAll('#progress tbody tr').length === 16,
    `${d2.querySelectorAll('#progress tbody tr').length}`);


  // ── меню и оформление: понятность для новичка ──────────────────────────────────────────────
  check('в боковой колонке есть карточка «Что в этой версии»', !!d.querySelector('.side-card.whatsnew'));
  check('в карточке «Что в этой версии» семь пунктов-новинок',
    d.querySelectorAll('.side-card.whatsnew li').length === 7,
    `${d.querySelectorAll('.side-card.whatsnew li').length}`);
  check('у каждого пункта меню есть номер, название, пояснение и значок',
    menu.every(a => a.querySelector('.stepnum') && (a.querySelector('.steptext b') || {}).textContent &&
      a.querySelector('.steptext span') && a.querySelector('.navbadge')));
  check('мини-оглавлений внутри разделов три и они раскрываются без скриптов',
    d.querySelectorAll('.toc-mini > summary').length === 3,
    `${d.querySelectorAll('.toc-mini > summary').length}`);
  check('ссылки мини-оглавлений ведут в существующие места',
    [...d.querySelectorAll('.toc-mini-links a')].every(a => !!d.querySelector(a.getAttribute('href'))),
    [...d.querySelectorAll('.toc-mini-links a')].filter(a => !d.querySelector(a.getAttribute('href'))).map(a => a.getAttribute('href')).join(','));
  check('подкатегории твиков помечены цветом (пять цветов слева)',
    ['Ping', 'Speed', 'Fps', 'Input', 'System'].every(f => new RegExp('tweak\\[data-focus="' + f + '"]').test(html)));


  // ── вердикты «стоит включать или нет» приходят из каталога, а не нарисованы в макете ────────
  check('в легенде карточек четыре вердикта из каталога', (() => {
    const box = [...d.querySelectorAll('#tweaks .legendbox')].find(b => b.querySelector('.verdict'));
    return !!box && box.querySelectorAll('.verdict').length === 4;
  })(), `${[...d.querySelectorAll('#tweaks .legendbox')].filter(b => b.querySelector('.verdict')).length} легенд с вердиктами`);
  check('плашки каталога в карточках не дублируются вместе с вердиктами',
    !/Рекомендую для игр/.test(d.getElementById('tweaks').textContent.split('Как читать карточку')[1] || ''));
  check('подписи вердиктов совпадают с формулировками кода (TweakVerdict)',
    ['включать: да', 'по желанию', 'осторожно', 'не советуем']
      .every(v => d.getElementById('tweaks').textContent.includes(v)));
  check('под ползунком написано состояние по умолчанию для каждого твика',
    cards.every(c => /по умолчанию|включён в план по умолчанию/.test(c.querySelector('.t-state').textContent)),
    cards.filter(c => !/по умолчанию|включён в план/.test(c.querySelector('.t-state').textContent)).map(c => c.dataset.code).join(','));
  check('«не советуем» выключены по умолчанию и объяснены внутри карточки',
    cards.filter(c => c.dataset.badge === 'NotRecommended').every(c => {
      const box = c.querySelector('.t-toggle');
      return box && !box.checked && /не советуем/i.test(c.querySelector('.t-state').textContent);
    }));
  check('в карточках есть блок «Зачем для игры и чего это стоит» с пометкой стоит ли включать',
    cards.every(c => /Зачем для игры/.test(c.textContent)));

  // ── порядок и поиск в разделе «Твики» ───────────────────────────────────────────────────────────
  const tweaksSection = d.getElementById('tweaks');
  const orderTitles = ['По назначению', 'Сначала полезное', 'По алфавиту'];
  const orderChips = tweaksSection ? [...tweaksSection.querySelectorAll('.chiprow span')].map(node => node.textContent.trim()) : [];
  check('в разделе «Твики» есть три режима порядка, как в программе',
    orderTitles.every(title => orderChips.some(chip => chip.startsWith(title))),
    `найдено: ${orderChips.join(' | ') || 'ничего'}`);
  check('выбранный порядок видно на экране',
    tweaksSection !== null && tweaksSection.querySelector('.chiprow span.on') !== null,
    'ни одна пилюля порядка не помечена выбранной');

  const filterTitles = ['Только рекомендуемое', 'Скрыть «не советуем»', 'Только из пресетов'];
  const filterChips = tweaksSection ? [...tweaksSection.querySelectorAll('.chiprow span')].map(n => n.textContent.trim()) : [];
  check('в разделе «Твики» есть три быстрых фильтра, как в программе',
    filterTitles.every(title => filterChips.some(chip => chip === title)),
    `найдено: ${filterChips.filter(chip => filterTitles.includes(chip)).join(' | ') || 'ничего'}`);

  check('подтверждение применения называет состав выбора по назначению',
    /Пинг и сеть — \d+ · Кадры — \d+/.test(html),
    'в макете нет подписи состава выбора');

  check('у выгрузки CSV описано, что файл не затирается молча и числа склоняются',
    /Выгрузка в CSV/.test(html) && /не затирает молча/.test(html) && /«1 строка»/.test(html),
    'в макете нет абзаца про CSV');

  check('у пресетов описан показ состава без применения',
    /«Показать состав»/.test(html),
    'в макете нет кнопки состава пресета');

  check('у своих игр описано, что подойдёт ярлык с рабочего стола',
    /Своя игра: подойдёт и файл с рабочего стола/.test(html) &&
      /Ярлык ведёт на/.test(html) &&
      /когда игра запустится/.test(html),
    'в макете нет абзаца про выбор ярлыка вместо .exe');

  check('таблица «Что будет, если выбрать…» показывает не меньше 12 разных случаев',
    (() => {
      const table = d.getElementById('routes-pick-examples');
      return !!table && table.querySelectorAll('tbody tr').length >= 12;
    })(),
    (() => {
      const table = d.getElementById('routes-pick-examples');
      return 'строк: ' + (table ? table.querySelectorAll('tbody tr').length : 0);
    })());

  check('у своих игр в макете есть дословные примеры ответов — удача и отказы',
    /Примеры того, что человек увидит/.test(html) &&
      (html.match(/Игра не добавлена:/g) || []).length >= 3 &&
      /служба видит его в списке процессов/.test(html),
    'в макете нет примеров ответов для своих игр');

  const groupHeaders = html.match(/твиков:\s*\d+\s*·\s*включено\s*\d+/g) || [];
  check('в заголовках групп стоит и число твиков, и число включённых',
    groupHeaders.length >= 5,
    `таких заголовков: ${groupHeaders.length}`);

  // ── тема: палитра макета совпадает с программой, и текст на ней читается ─────────────────────
  const rootBlock = (html.match(/:root\{([\s\S]*?)\}/) || [])[1] || '';
  const vars = {};
  for (const m of rootBlock.matchAll(/--([a-z0-9]+)\s*:\s*(#[0-9a-fA-F]{6})/g)) vars[m[1]] = m[2].toLowerCase();

  const appXamlPath = path.resolve(path.dirname(FILE), '..', 'src', 'PingForge.App', 'App.xaml');
  let appXaml = '';
  try { appXaml = fs.readFileSync(appXamlPath, 'utf8'); } catch { appXaml = ''; }
  const appColor = key => {
    const found = appXaml.match(new RegExp('<Color x:Key="' + key + '">(#[0-9A-Fa-f]{6})</Color>'));
    return found ? found[1].toLowerCase() : '';
  };

  // Макет — это витрина программы, а не «похожий на неё» сайт: фон, панели, линии, текст и
  // акценты обязаны совпадать с темой. Иначе по макету нельзя судить, как выглядит программа.
  const themePairs = [
    ['bg', 'BgColor'], ['card', 'PanelColor'], ['line', 'LineColor'], ['line2', 'LineStrongColor'],
    ['text', 'TextColor'], ['muted', 'MutedColor'], ['dim', 'DimColor'],
    ['accent', 'AccentColor'], ['accent2', 'Accent2Color'], ['warn', 'WarnColor'], ['bad', 'DangerColor']
  ];
  const misplaced = themePairs
    .filter(([v, k]) => vars[v] !== appColor(k))
    .map(([v, k]) => `--${v}=${vars[v] || '—'}, в теме ${appColor(k) || '—'}`);
  check('палитра макета совпадает с темой программы (App.xaml)', appXaml.length > 0 && misplaced.length === 0,
    misplaced.join('; '));

  // Контраст по WCAG 2.1: (L1 + 0,05) / (L2 + 0,05). Требование AA для обычного текста — 4,5:1.
  const channel = part => {
    const value = parseInt(part, 16) / 255;
    return value <= 0.03928 ? value / 12.92 : Math.pow((value + 0.055) / 1.055, 2.4);
  };
  const luminance = hex => 0.2126 * channel(hex.slice(1, 3)) + 0.7152 * channel(hex.slice(3, 5)) + 0.0722 * channel(hex.slice(5, 7));
  const contrast = (first, second) => {
    const a = luminance(first);
    const b = luminance(second);
    return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
  };

  const contrastPairs = [
    ['основной текст на фоне', vars.text, vars.bg],
    ['основной текст на панели', vars.text, vars.card],
    ['подписи на панели', vars.muted, vars.card],
    ['третий уровень текста на панели', vars.dim, vars.card],
    // Карточка — градиент: верхняя кромка светлее панели, и именно на ней текст слабее всего.
    ['третий уровень текста на верхней кромке карточки', vars.dim, appColor('PanelTopColor')],
    ['ссылка (accent) на панели', vars.accent, vars.card],
    ['accent2 на панели', vars.accent2, vars.card]
  ];
  contrastPairs.forEach(([name, fg, bg]) => {
    const ratio = fg && bg ? contrast(fg, bg) : 0;
    check(`контраст: ${name} не ниже 4,5:1`, ratio >= 4.5, `${ratio.toFixed(2)}:1`);
  });

  const primaryText = '#04121d';
  check('тёмный текст основной кнопки читается на обоих концах градиента',
    contrast(primaryText, vars.accent) >= 4.5 && contrast(primaryText, vars.accent2) >= 4.5,
    `${contrast(primaryText, vars.accent).toFixed(2)}:1 и ${contrast(primaryText, vars.accent2).toFixed(2)}:1`);

  // Подпись о контрасте — обещание, а не украшение: её числа обязаны совпадать с расчётом по палитре
  // самой страницы. В программе эта подпись считается тем же способом (PingForge.Core.Ui.PaletteNote).
  const note = html.match(
    /Контраст основного текста #([0-9A-Fa-f]{6}) на фоне #([0-9A-Fa-f]{6}) — (\d+,\d):1, подписей #([0-9A-Fa-f]{6}) — (\d+,\d):1/);
  const ru = value => value.toFixed(1).replace('.', ',') + ':1';
  check('числа в подписи о контрасте совпадают с палитрой макета',
    !!note && `#${note[1]}`.toLowerCase() === vars.text && `#${note[2]}`.toLowerCase() === vars.bg
      && `#${note[4]}`.toLowerCase() === vars.muted
      && `${note[3]}:1` === ru(contrast(vars.text, vars.bg))
      && `${note[5]}:1` === ru(contrast(vars.muted, vars.bg)),
    note
      ? `в тексте ${note[3]}:1 и ${note[5]}:1, по палитре ${ru(contrast(vars.text, vars.bg))} и ${ru(contrast(vars.muted, vars.bg))}`
      : 'фразы о контрасте в макете нет');

  const snippetNames = ['service.log.2', 'Дата,Твик,Код', '"SchemaVersion"', 'отчёт об ошибке', 'PingForge-поддержка', 'settings.json.bak-'];
  const snippets = [...d.querySelectorAll('.snippet')];
  check('в примерах файлов шесть разных выгрузок программы',
    !!d.getElementById('journal-files') && snippets.length >= 6 && snippetNames.every(name => html.includes(name)),
    `блок: ${!!d.getElementById('journal-files')}, примеров: ${snippets.length}, не хватает: ` +
      snippetNames.filter(name => !html.includes(name)).join(', '));

  // Многоточие без подсказки — потерянный текст: макет обязан сказать, что это проверяется.
  const trimProse = 'сокращённое многоточием читается целиком в подсказке';
  check('в макете сказано, что сокращённое многоточием читается в подсказке', html.includes(trimProse),
    `нет фразы «${trimProse}»`);

  // Сравнение сессий: макет называет случаи, в которых программа честно отказывается сравнивать.
  const comparisonProse = 'отказывается сравнивать одну и ту же сессию';
  check('в макете сказано, что одну и ту же сессию не сравнивают', html.includes(comparisonProse),
    `нет фразы «${comparisonProse}»`);

  // Имена для экранного диктора — обещание программы, и макет обязан говорить о нём прямо.
  const accessibleProse = 'у каждого поля ввода и списка есть имя для экранного диктора';
  check('в макете сказано про имена для экранного диктора', html.includes(accessibleProse),
    `нет фразы «${accessibleProse}»`);

  // Одна программа: макет не должен выдавать за отдельный продукт ни «Lite», ни «Pro»,
  // иначе человек читает страницу как две программы и ждёт вторую.
  const oneProgram = 'Программа одна —';
  const liteHitsInPage = (html.match(/PingForge\s+Lite/g) || []).length;
  check('в макете сказано, что программа одна, а не две',
    html.includes(oneProgram) && html.includes('второй программы нет') && liteHitsInPage === 0,
    `фраза «${oneProgram}»: ${html.includes(oneProgram)}, ` +
      `«второй программы нет»: ${html.includes('второй программы нет')}, «PingForge Lite»: ${liteHitsInPage}`);
  check('готовность подписана как готовность программы, без второго имени',
    html.includes('Готовность программы') && !/Готовность\s+PingForge/.test(html),
    `подпись «Готовность программы»: ${html.includes('Готовность программы')}`);

  // «Сумма весов ровно 100 %, итог считается по весам» — это утверждение, а не украшение: считаем его
  // по самой таблице страницы. Если кто-то поправит процент руками, подпись с цифрой разойдутся.
  const progressTable = [...d.querySelectorAll('#progress table')].find(t => /Готово/.test(t.textContent));
  const progressRows = progressTable ? [...progressTable.querySelectorAll('tbody tr')] : [];
  const rowNumbers = progressRows.map(tr => ({
    weight: parseInt(tr.children[1].textContent, 10),
    done: parseInt(tr.children[2].textContent, 10),
  }));
  const weightsTotal = rowNumbers.reduce((sum, row) => sum + row.weight, 0);
  const byFormula = weightsTotal > 0
    ? rowNumbers.reduce((sum, row) => sum + row.weight * row.done, 0) / weightsTotal
    : 0;
  const scoreCell = d.querySelector('#progress .score');
  const scoreOnPage = scoreCell ? parseFloat(scoreCell.textContent.replace(',', '.')) : NaN;
  check('итог готовности на странице сходится с весами и процентами таблицы',
    progressRows.length >= 10 && weightsTotal === 100 && Math.abs(byFormula - scoreOnPage) < 0.05,
    `строк: ${progressRows.length}, сумма весов: ${weightsTotal}, по формуле: ${byFormula.toFixed(1)}, ` +
      `на странице: ${Number.isNaN(scoreOnPage) ? 'нет числа' : scoreOnPage}`);

  // Число автотестов: либо оно пришло из журнала прогона и совпадает с подписью, либо страница честно
  // говорит, что числа нет. Вписанная руками цифра — это то, как «169» прожило полтора года при 1147.
  const testsChip = d.querySelector('[data-tests]');
  const testsHonest = 'Число автотестов здесь не вписано: оно берётся из журнала прогона';
  const testsAttr = testsChip ? testsChip.getAttribute('data-tests') : '';
  const testsFromRun = !!testsChip
    && /^\d+$/.test(testsAttr)
    && Number(testsAttr) >= 100
    && testsChip.textContent.includes(testsAttr);
  check('число автотестов взято из журнала прогона, а не вписано руками',
    testsChip ? testsFromRun : html.includes(testsHonest),
    testsChip
      ? `подпись «${testsChip.textContent.trim()}», атрибут ${testsAttr}`
      : `нет подписи с числом и нет фразы «${testsHonest}»`);

  // Числа в текстах: каждое «N твиков», «N игры», «N способов», «N бесплатных DNS» на странице обязано
  // совпадать с каталогом, из которого страница собрана. Раньше здесь висело «ручной выбор 28 твиков»
  // при 47 в каталоге — цифру вписали руками и забыли. Теперь расхождение ловится, а не живёт годами.
  const catalogPath = path.join(path.dirname(path.resolve(FILE)), 'catalog.json');
  let catalog = null;
  try {
    catalog = JSON.parse(fs.readFileSync(catalogPath, 'utf8'));
  } catch (e) {
    catalog = null;
  }

  if (!catalog) {
    check('числа в текстах сверены с каталогом', false, `нет ${catalogPath} — сверить не с чем`);
  } else {
    const allowedTweaks = new Set([catalog.tweakCount]);
    (catalog.presets || []).forEach(p => allowedTweaks.add(p.tweakCount));
    if (catalog.verdicts && catalog.verdicts.recommendCount) allowedTweaks.add(catalog.verdicts.recommendCount);
    (catalog.focusCounts || []).forEach(f => allowedTweaks.add(f.count));

    const routes = catalog.routes || {};
    const allowedGames = new Set([routes.gameCount || (routes.games || []).length]);
    const allowedDns = new Set([(routes.dns || []).length]);
    const allowedWays = new Set([(routes.pathKinds || []).length, routes.freePathCount]);

    // Сверяем только те блоки, которые не меняются скриптами: оглавление, боковое меню и карточку
    // «Что в этой версии». Счётчики фильтров в разделе твиков живые («показано 0 твиков»), их считать
    // по каталогу нельзя — и не нужно.
    const staticBlocks = [...d.querySelectorAll('nav.toc, nav.side-nav, .side-card.whatsnew, header')];
    const text = staticBlocks.map(block => block.textContent).join(' \n ');

    const bad = [];
    const families = [
      { re: /(\d+)\s+твик[а-я]*/g, allowed: allowedTweaks, what: 'твиков' },
      { re: /(\d+)\s+игр[а-я]*/g, allowed: allowedGames, what: 'игр' },
      { re: /(\d+)\s+бесплатн[а-я]*\s+DNS/g, allowed: allowedDns, what: 'DNS' },
      { re: /(\d+)\s+способ[а-я]*/g, allowed: allowedWays, what: 'способов' },
    ];
    families.forEach(family => {
      for (const match of text.matchAll(family.re)) {
        const value = Number(match[1]);
        if (!family.allowed.has(value)) {
          bad.push(`«${match[0].trim()}» (в каталоге: ${[...family.allowed].join(', ')})`);
        }
      }
    });
    check('числа в текстах сверены с каталогом', bad.length === 0, bad.slice(0, 4).join(' | '));
  }

  console.log(`Файл: ${path.resolve(FILE)} (${(html.length / 1024).toFixed(0)} КБ)`);
  console.log('порядок разделов:', ids.join(' → '));
  console.log(`=== пройдено ${ok.length}, проблем ${problems.length} ===`);
  problems.forEach(p => console.log('  ✗ ' + p));
  if (problems.length === 0) console.log('  все проверки пройдены');
  process.exit(problems.length === 0 ? 0 : 1);
}, 250);
