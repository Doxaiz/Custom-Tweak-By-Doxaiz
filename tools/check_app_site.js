// Проверка сайта «как выглядит готовая программа» (docs/custom-tweak-by-doxaiz.html).
//
// Навигация здесь проверяется по частям, потому что вся она держится на трёх вещах:
//   1) метка в меню связана с радиокнопкой (for → id) — проверяем и кликаем в jsdom;
//   2) радиокнопка стоит ВЫШЕ блока .body — иначе CSS-селектор `~` не сработал бы;
//   3) в стилях есть правило `#nav-X:checked ~ .body #page-X{display:block}`.
// Если все три условия выполнены, переключение обязано работать в любом браузере — без JavaScript.
//
// Отдельный блок проверок — против лишнего текста: сайт показывает программу, а не рассказывает о процессе.
//
// Запуск: NODE_PATH=/var/tmp/domtest/node_modules node tools/check_app_site.js docs/custom-tweak-by-doxaiz.html

const fs = require('fs');
const path = require('path');
const { JSDOM } = require('jsdom');

const FILE = process.argv[2] || 'docs/custom-tweak-by-doxaiz.html';
const html = fs.readFileSync(FILE, 'utf8');

let ok = 0;
const problems = [];
const check = (name, condition, detail) => {
  if (condition) ok++;
  else problems.push(name + (detail ? ` — ${detail}` : ''));
};

const dom = new JSDOM(html);
const w = dom.window;
const d = w.document;
const text = (el) => (el ? el.textContent.replace(/\s+/g, ' ').trim() : '');
const css = html.slice(html.indexOf('<style>') + 7, html.indexOf('</style>'));

// ── шапка окна ────────────────────────────────────────────────────────────────────────────────
check('заголовок страницы — название программы', /Custom Tweak By Doxaiz/.test(d.title), d.title);
check('в шапке стоит название программы', text(d.querySelector('.name')) === 'Custom Tweak By Doxaiz',
  text(d.querySelector('.name')));
check('в шапке есть состояние живой оптимизации', /оптимизация работает/.test(text(d.querySelector('.bar'))));
check('показан текущий сервер, а не абстрактный текст', /3\.4\.5\.6/.test(text(d.querySelector('.bar'))));

// ── навигация: метка → радиокнопка → экран ───────────────────────────────────────────────────
const labels = [...d.querySelectorAll('nav.side label')];
const navRadios = [...d.querySelectorAll('input[name="nav"]')];
const pages = [...d.querySelectorAll('section.page')];

check('в боковом меню семь пунктов', labels.length === 7, `${labels.length}`);
check('названия пунктов меню — одно-два слова',
  labels.every(l => text(l).split(' ').length <= 2), labels.map(text).join(' | '));
check('у каждого пункта меню есть своя радиокнопка',
  labels.every(l => l.getAttribute('for') && d.getElementById(l.getAttribute('for'))),
  labels.filter(l => !d.getElementById(l.getAttribute('for') || '')).map(text).join(','));
check('радиокнопок меню ровно семь и все с одним именем группы',
  navRadios.length === 7 && new Set(navRadios.map(r => r.name)).size === 1, `${navRadios.length}`);
check('по умолчанию выбрана одна радиокнопка — главный экран',
  navRadios.filter(r => r.checked).length === 1 && d.getElementById('nav-optimize').checked);
check('экранов программы семь', pages.length === 7, `${pages.length}`);
check('у каждой радиокнопки есть экран с тем же суффиксом',
  navRadios.every(r => d.getElementById('page-' + r.id.replace('nav-', ''))),
  navRadios.filter(r => !d.getElementById('page-' + r.id.replace('nav-', ''))).map(r => r.id).join(','));

// условие 2: радиокнопки обязаны быть выше .body, иначе селектор ~ не дойдёт до экранов
const body = d.querySelector('.body');
const allBeforeBody = navRadios.every(r =>
  (r.compareDocumentPosition(body) & w.Node.DOCUMENT_POSITION_FOLLOWING) !== 0);
check('все радиокнопки переключения стоят выше блока с экранами (иначе CSS не сработает)', allBeforeBody);

// условие 3: правила в стилях, по одному на каждый экран
const showRules = navRadios.filter(r => {
  const anchor = r.id.replace('nav-', '');
  return new RegExp(`#nav-${anchor}:checked\\s*~\\s*\\.body\\s+#page-${anchor}\\{display:block\\}`).test(css);
});
check('для каждого экрана есть правило показа по выбранной радиокнопке',
  showRules.length === 7, `${showRules.length} из 7`);
check('экраны скрыты по умолчанию, показывается только выбранный',
  /\.page\{display:none\}/.test(css) && !/\.page\{display:block/.test(css));
check('переключение не зависит от :target и :has (работает и в песочнице, и в старых браузерах)',
  !/:has\(/.test(css) && !/:target/.test(css));
check('JavaScript на странице нет вообще', !/<script/i.test(html));
check('внешних ресурсов нет: только инлайновые стили и SVG',
  !/<link[^>]+href="http/i.test(html) && !/<img[^>]+src="http/i.test(html));

// условие 1: клик по метке действительно переключает радиокнопку (jsdom это делает как браузер)
check('клик по «Твики» включает нужную радиокнопку (как в браузере)', (() => {
  const label = labels.find(l => text(l) === 'Твики');
  label.click();
  return d.getElementById('nav-tweaks').checked && !d.getElementById('nav-optimize').checked;
})(), d.getElementById('nav-tweaks').checked ? '' : 'радиокнопка не переключилась');
check('клик по «Автор» тоже переключает', (() => {
  labels.find(l => text(l) === 'Автор').click();
  return d.getElementById('nav-support').checked;
})());
check('возврат на главный экран работает', (() => {
  labels.find(l => text(l) === 'Оптимизация').click();
  return d.getElementById('nav-optimize').checked;
})());
check('подсветка активного пункта меню описана в стилях',
  /#nav-optimize:checked ~ \.body nav label\[for="nav-optimize"\]/.test(css));

// ── главный экран ─────────────────────────────────────────────────────────────────────────────
const hero = d.getElementById('page-optimize');
check('на главном экране есть большая кнопка оптимизации',
  /Оптимизировать соединение/.test(text(hero.querySelector('.pb'))));
check('показан результат «до/после» числами: 78 мс и было 120 мс',
  /78 мс/.test(text(hero)) && /120/.test(text(hero)));
check('рядом подписаны джиттер, потери и число проб',
  /2,4 мс/.test(text(hero)) && /0,0 %/.test(text(hero)) && /360/.test(text(hero)));
check('подписано окно наблюдения', /Окно 60 секунд/.test(text(hero)));
check('сказано, что изменения снимаются при остановке', /Изменения снимаются/.test(text(hero)));

const charts = [...hero.querySelectorAll('.chart')];
check('на главном экране два графика: задержка и джиттер', charts.length === 2, `${charts.length}`);
check('в каждом графике две линии (было и стало) и подписи',
  charts.every(c => c.querySelectorAll('polyline').length === 2 && /до/.test(text(c)) && /после/.test(text(c))));
check('графики подписаны единицами измерения',
  charts.every(c => /мс/.test(text(c.querySelector('figcaption')))));
check('перечислены 11 обязательных показателей', hero.querySelectorAll('.ind').length === 11,
  `${hero.querySelectorAll('.ind').length}`);
check('у каждого показателя есть единица измерения',
  [...hero.querySelectorAll('.ind')].every(i => text(i.querySelector('.ind-u')).length > 0));
check('сказано, что именно сделала программа, а не «оптимизировано»',
  /Что сделала программа/.test(text(hero)) && hero.querySelectorAll('.acts li').length === 2);

// ── твики ─────────────────────────────────────────────────────────────────────────────────────
const tweaks = d.getElementById('page-tweaks');
const cards = [...tweaks.querySelectorAll('.tw')];
check('карточек твиков 47', cards.length === 47, `${cards.length}`);
check('у каждой карточки живой ползунок включения', cards.every(c => c.querySelector('.sw input')));
check('по умолчанию включено 23 (только рекомендованные)',
  cards.filter(c => c.querySelector('.sw input').checked).length === 23,
  `${cards.filter(c => c.querySelector('.sw input').checked).length}`);
check('у каждой карточки есть вердикт словами',
  cards.every(c => /включать|по желанию|осторожно|не советуем/.test(text(c.querySelector('.v')))));
check('подкатегории подписаны',
  ['Пинг и сеть', 'Скорость', 'ФПС и кадры', 'Задержка ввода', 'Система']
    .every(t => text(tweaks).includes(t)));

// пресеты: выбор действительно подсвечивает состав
const presetLabels = [...tweaks.querySelectorAll('label.seg')].filter(l => /^(Всё|Низкий пинг|Скорость|Кадры)\b/.test(text(l)));
check('пресетов четыре, и в каждом подписано число твиков',
  presetLabels.length === 4 && presetLabels.every(l => /\d/.test(text(l.querySelector('.count')))),
  presetLabels.map(text).join(' | '));
check('числа в пресетах совпадают с каталогом: 47 / 19 / 5 / 23',
  ['47', '19', '5', '23'].every((n, i) => text(presetLabels[i].querySelector('.count')) === n),
  presetLabels.map(l => text(l.querySelector('.count'))).join('/'));
check('у каждого пресета есть радиокнопка, и по умолчанию выбран показ всего',
  presetLabels.every(l => d.getElementById(l.getAttribute('for'))) && d.getElementById('ps-all').checked);
check('выбор пресета в стилях приглушает то, что в него не входит',
  /#ps-low:checked ~ \.body \.tw:not\(\.in-low\)\{opacity/.test(css) &&
  /#ps-speed:checked ~ \.body \.tw:not\(\.in-speed\)/.test(css) &&
  /#ps-fps:checked ~ \.body \.tw:not\(\.in-fps\)/.test(css));
// Ловушка, на которой чекер однажды уже промахнулся: если классы карточек собрать конкатенацией
// без пробела, получается «in-lowin-fps» — класс выглядит как «есть in», а подсветка пресета молча не работает.
// Поэтому проверяем не наличие подстроки, а разбор классов на токены.
const presetTokens = (card) => (card.className || '').split(/\s+/).filter(t => /^in-(low|speed|fps)$/.test(t));
const countWith = (token) => cards.filter(c => presetTokens(c).includes(token)).length;
const gluedCards = cards.filter(c => presetTokens(c).length === 0 && /in-[a-z]*in-/.test(c.className || ''));
check('классы пресетов на карточках не склеены («in-lowin-fps» — ошибка)', gluedCards.length === 0,
  `склеено ${gluedCards.length}`);
check('состав пресетов считается по токенам классов: 19 / 5 / 23',
  countWith('in-low') === 19 && countWith('in-speed') === 5 && countWith('in-fps') === 23,
  `low ${countWith('in-low')}, speed ${countWith('in-speed')}, fps ${countWith('in-fps')}`);

check('карточки помечены составом пресетов (34 твика входят хотя бы в один)',
  cards.filter(c => /in-low|in-speed|in-fps/.test(c.className)).length === 34,
  `${cards.filter(c => /in-low|in-speed|in-fps/.test(c.className)).length}`);
check('клик по пресету «Низкий пинг» переключает режим показа', (() => {
  presetLabels.find(l => /Низкий пинг/.test(text(l))).click();
  return d.getElementById('ps-low').checked;
})());
check('возврат к показу всего работает', (() => {
  presetLabels.find(l => /^Всё/.test(text(l))).click();
  return d.getElementById('ps-all').checked;
})());

// массовый выбор: три режима, меняющие вид ползунков
const selLabels = [...tweaks.querySelectorAll('label.seg')]
  .filter(l => /только рекомендуемое|включить все|отключить все/.test(text(l)));
check('три кнопки массового выбора на месте', selLabels.length === 3, selLabels.map(text).join(' | '));
check('«включить все» и «отключить все» действительно меняют вид ползунков в стилях',
  /#sel-all:checked ~ \.body \.tw \.sl\{/.test(css) && /#sel-none:checked ~ \.body \.tw \.sl\{/.test(css));
check('в режимах «все» и «ничего» ползунки не притворяются кликабельными',
  /#sel-all:checked ~ \.body \.tw \.sw input,#sel-none:checked ~ \.body \.tw \.sw input\{pointer-events:none\}/.test(css));
check('клик по «включить все» переключает режим', (() => {
  selLabels.find(l => /включить все/.test(text(l))).click();
  return d.getElementById('sel-all').checked;
})());
check('клик по «отключить все» переключает режим', (() => {
  selLabels.find(l => /отключить все/.test(text(l))).click();
  return d.getElementById('sel-none').checked;
})());
check('возврат к «только рекомендуемое» работает', (() => {
  selLabels.find(l => /только рекомендуемое/.test(text(l))).click();
  return d.getElementById('sel-rec').checked;
})());
check('честно сказано, сколько включено по умолчанию в программе',
  /по умолчанию включено 23 из 47/.test(text(tweaks)));
check('длинные описания спрятаны в «Подробнее»',
  cards.every(c => c.querySelector('details.tw-more')));
check('сказано, что твик не пишется молча',
  /снапшот и точка восстановления/.test(text(tweaks)));

// ── маршрут, журнал, трей, настройки, автор ───────────────────────────────────────────────────
const routes = d.getElementById('page-routes');
check('в справочнике 22 игры', routes.querySelectorAll('.tbl tbody tr').length >= 22,
  `${routes.querySelectorAll('.tbl tbody tr').length}`);
check('в справочнике 5 DNS-чипов', routes.querySelectorAll('.cards .chip.card').length === 5,
  `${routes.querySelectorAll('.cards .chip.card').length}`);
check('честно сказано, что бесплатных способов четыре',
  /Бесплатных и безопасных способов — четыре/.test(text(routes)));
check('сказано, что роутер программа не настраивает',
  /Настройки роутера программа не меняет/.test(text(routes)));

const journal = d.getElementById('page-journal');
check('в журнале есть большая кнопка возврата всех изменений',
  /Вернуть всё как было \(6\)/.test(text(journal)));
check('в журнале видно, что именно менялось, а не «настройки»',
  /Маршрут для 3\.4\.5\.6/.test(text(journal)) && /TCP/.test(text(journal)));
check('сказано, что значения возвращаются те, что были',
  /ровно те, что были/.test(text(journal)));

const tray = d.getElementById('page-tray');
check('в трее показана панель меню', /Custom Tweak/.test(text(tray.querySelector('.tray'))));
check('в меню трея пять пунктов: остановка, возврат, окно, настройки, выход',
  tray.querySelectorAll('.tray li').length === 5 && /Остановить оптимизацию/.test(text(tray)) &&
  /Выход/.test(text(tray)), `${tray.querySelectorAll('.tray li').length}`);
check('показаны три профиля потребления с интервалом и числом проб',
  tray.querySelectorAll('.prof').length === 3 && /проб\/мин/.test(text(tray)));
check('сказано, что закрытие окна не выключает оптимизацию',
  /Закрытие окна оптимизацию не выключает/.test(text(tray)));

const settings = d.getElementById('page-settings');
check('в настройках есть паузы и точка восстановления',
  /Пауза на батарее/.test(text(settings)) && /точку восстановления/.test(text(settings)));
check('телеметрия показана выключенной и недоступной', (() => {
  const box = [...settings.querySelectorAll('input[type=checkbox]')].pop();
  return box && box.disabled && /недоступна/.test(text(settings));
})());

const support = d.getElementById('page-support');
const supportLinks = [...d.querySelectorAll('a[href="https://dalink.to/doxaiz"]')];
check('кнопка поддержки ведёт на ссылку автора и открывается в новой вкладке',
  supportLinks.length === 2 && supportLinks.every(a => a.getAttribute('target') === '_blank' &&
    /noopener/.test(a.getAttribute('rel') || '')), `${supportLinks.length}`);
check('на экране автора написано, что программа бесплатная и без телеметрии',
  /бесплатная, без телеметрии/.test(text(support)));

// ── против лишнего текста ─────────────────────────────────────────────────────────────────────
check('внизу одна честная строка про макет',
  /Визуальный макет интерфейса/.test(text(d.querySelector('.foot'))) &&
  text(d.querySelector('.foot')).split('.').length <= 4);

const paragraphs = [...d.querySelectorAll('p, li, td, .tw-more p')].map(text).filter(t => t.length > 0);
const longParagraphs = paragraphs.filter(t => t.length > 320);
check('нет ни одного абзаца длиннее 320 символов', longParagraphs.length === 0,
  longParagraphs.slice(0, 2).map(t => t.slice(0, 60) + '…').join(' || '));

const allText = text(d.body);
const metaWords = ['раздел 1 из', 'готовность pingforge', 'сумма весов', 'рендерер',
  'check_ui_preview', 'сборка succeeded', 'тестов пройдено', 'docs/', 'README', 'это макет',
  'демонстрационные', 'каталог проекта: 47 твиков'];
const foundMeta = metaWords.filter(word => allText.toLowerCase().includes(word.toLowerCase()));
check('нет текста из рабочих документов проекта', foundMeta.length === 0, foundMeta.join(', '));
check('про макет сказано один раз и мелко, в подвале',
  (allText.match(/макет интерфейса/gi) || []).length === 1 &&
  /макет интерфейса/i.test(text(d.querySelector('.foot'))));
check('запрещённых обещаний нет',
  !/пинг 0|нулевой пинг|мгновенный пинг|мгновенный отклик|гарантированн|обходит блокировк/i.test(allText));

const words = allText.split(/\s+/).length;
check('страница остаётся компактной: не больше 4000 слов текста', words <= 4000, `${words} слов`);
check('нет мёртвых элементов: ни одной кнопки без действия и без подписи',
  [...d.querySelectorAll('button')].every(b => text(b).length > 0));

console.log(`Файл: ${path.resolve(FILE)} (${(html.length / 1024).toFixed(0)} КБ, ${words} слов)`);
console.log(`=== пройдено ${ok}, проблем ${problems.length} ===`);
problems.forEach(p => console.log('  ✗ ' + p));
if (problems.length === 0) console.log('  все проверки пройдены');
process.exit(problems.length === 0 ? 0 : 1);
