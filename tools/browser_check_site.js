// Проверка сайта в настоящем браузере (headless Chrome): переключение разделов кликами по меню.
//
// jsdom не считает CSS-каскад с селекторами-сиблингами (`#nav-x:checked ~ .body #page-x`), поэтому
// работоспособность меню и пресетов подтверждается здесь: кликаем как пользователь и смотрим,
// какой экран реально виден на странице (offsetParent + размеры), и сохраняем скриншоты.
//
// Запуск: NODE_PATH=/var/tmp node tools/browser_check_site.js docs/custom-tweak-by-doxaiz.html /var/tmp/shots

const fs = require('fs');
const path = require('path');
const puppeteer = require('puppeteer');

const FILE = path.resolve(process.argv[2] || 'docs/custom-tweak-by-doxaiz.html');
const OUT = process.argv[3] || '/var/tmp/shots';

let ok = 0;
const problems = [];
const check = (name, condition, detail) => {
  if (condition) ok++;
  else problems.push(name + (detail ? ` — ${detail}` : ''));
};

(async () => {
  fs.mkdirSync(OUT, { recursive: true });
  const browser = await puppeteer.launch({
    args: ['--no-sandbox', '--disable-dev-shm-usage', '--font-render-hinting=none'],
  });
  const page = await browser.newPage();
  await page.setViewport({ width: 1280, height: 900, deviceScaleFactor: 1 });
  await page.goto('file://' + FILE, { waitUntil: 'load' });

  // Анимации выключаем до замеров: переход в 0,15 с на ползунках ловится в середине и портит
  // вычисленные стили (замер «сразу после клика» показывал ещё старое положение).
  await page.addStyleTag({ content: '*, *::before, *::after { transition: none !important; animation: none !important; }' });

  const visibleSections = () => page.$$eval('section.page', (sections) =>
    sections.filter(s => s.offsetParent !== null && s.getBoundingClientRect().height > 0).map(s => s.id));

  // ── какой это файл: витрина (разделы на радиокнопках) или рабочий документ (разделы на скриптах) ──
  const mode = await page.evaluate(() => {
    if (document.querySelector('nav.side label[for]')) return 'showcase';
    if (document.querySelector('nav.side-nav a[data-group]')) return 'working';
    return 'unknown';
  });
  check('файл узнаваем: витрина или рабочий документ', mode !== 'unknown', mode);

  if (mode === 'working') {
    // Рабочий документ переключает разделы скриптом; без скриптов обязаны быть видны все разделы.
    const groups = await page.$$eval('nav.side-nav a[data-group]', (links) =>
      links.map(a => ({ group: a.dataset.group, title: a.textContent.replace(/\s+/g, ' ').trim().slice(0, 24) })));
    check('в меню рабочего документа есть пункты', groups.length >= 6, `${groups.length}`);

    const noJsVisible = await page.evaluate(() => {
      document.documentElement.classList.remove('js');
      const visible = [...document.querySelectorAll('section.tab')]
        .filter(s => getComputedStyle(s).display !== 'none').length;
      document.documentElement.classList.add('js');
      return visible;
    });
    const allSections = await page.$$eval('section.tab', (s) => s.length);
    check('без скриптов видны все разделы рабочего документа',
      noJsVisible === allSections, `${noJsVisible} из ${allSections}`);

    // Один пункт меню может показывать две части раздела (например «Сеть и маршрут» —
    // маршрут и анализ). Поэтому проверяем не «показан ровно этот раздел», а два настоящих
    // свойства: пункт становится выбранным, и разделы по всем пунктам делятся без повторов.
    const seen = new Map();
    for (const { group, title } of groups) {
      await page.evaluate((g) => {
        document.querySelector(`nav.side-nav a[data-group="${g}"]`).click();
      }, group);
      await new Promise(r => setTimeout(r, 60));

      const state = await page.evaluate((g) => {
        const link = document.querySelector(`nav.side-nav a[data-group="${g}"]`);
        return {
          chosen: link.getAttribute('aria-current') === 'true',
          shown: [...document.querySelectorAll('section.tab')]
            .filter(s => getComputedStyle(s).display !== 'none')
            .map(s => s.id || s.dataset.part || s.className),
        };
      }, group);

      check(`клик «${title}» отмечает пункт выбранным`, state.chosen);
      check(`клик «${title}» открывает хотя бы один раздел`, state.shown.length >= 1, state.shown.join(','));
      seen.set(group, state.shown);
      await page.screenshot({ path: path.join(OUT, `рабочий-${group}.png`) });
    }

    const all = [...seen.values()].flat();
    const unique = new Set(all);
    check('разделы между пунктами меню не повторяются', unique.size === all.length,
      all.join(','));
    check('все разделы документа достижимы из меню', unique.size === allSections,
      `${unique.size} из ${allSections}`);

    await browser.close();
    console.log(`Файл: ${FILE} (рабочий документ интерфейса)`);
    console.log(`Скриншоты: ${OUT}`);
    console.log(`=== пройдено ${ok}, проблем ${problems.length} ===`);
    problems.forEach(p => console.log('  ✗ ' + p));
    if (problems.length === 0) console.log('  все проверки пройдены в настоящем браузере');
    process.exit(problems.length === 0 ? 0 : 1);
  }

  // ── стартовое состояние: виден только главный экран ────────────────────────────────────────
  check('при открытии виден ровно один экран — «Оптимизация»',
    JSON.stringify(await visibleSections()) === JSON.stringify(['page-optimize']),
    JSON.stringify(await visibleSections()));

  const menuItems = await page.$$eval('nav.side label', (labels) => labels.map(l => l.textContent.trim()));
  check('в меню семь пунктов', menuItems.length === 7, menuItems.join(', '));

  // ── кликаем по каждому пункту меню и смотрим, что реально показалось ───────────────────────
  const expected = {
    'Оптимизация': 'page-optimize',
    'Твики': 'page-tweaks',
    'Маршрут': 'page-routes',
    'Журнал': 'page-journal',
    'Трей': 'page-tray',
    'Настройки': 'page-settings',
    'Автор': 'page-support',
  };

  for (const [title, id] of Object.entries(expected)) {
    await page.evaluate((label) => {
      const target = [...document.querySelectorAll('nav.side label')]
        .find(l => l.textContent.trim() === label);
      target.click();
    }, title);
    const visible = await visibleSections();
    check(`клик «${title}» показывает только свой раздел`,
      visible.length === 1 && visible[0] === id, visible.join(','));

    const highlighted = await page.evaluate((label) => {
      const target = [...document.querySelectorAll('nav.side label')]
        .find(l => l.textContent.trim() === label);
      const back = getComputedStyle(target).backgroundColor;
      return back !== 'rgba(0, 0, 0, 0)' && back !== 'transparent';
    }, title);
    check(`выбранный пункт «${title}» подсвечен`, highlighted);

    await page.screenshot({ path: path.join(OUT, `${id}.png`) });
  }

  // ── пресеты: выбор подсвечивает состав ────────────────────────────────────────────────────
  await page.evaluate(() => [...document.querySelectorAll('nav.side label')]
    .find(l => l.textContent.trim() === 'Твики').click());

  const clickSeg = (needle) => page.evaluate((text) => {
    const label = [...document.querySelectorAll('label.seg')].find(l => l.textContent.includes(text));
    label.click();
    return label.getAttribute('for');
  }, needle);

  const dimmed = () => page.$$eval('#page-tweaks .tw', (cards) =>
    cards.map(c => Number(getComputedStyle(c).opacity)).filter(o => o < 0.9).length);

  // состав пресетов прямо из разметки: класс на карточке обязан читаться целиком
  // (раньше классы склеивались в «in-lowin-fps» и подсветка пресета не срабатывала)
  const membership = await page.$$eval('#page-tweaks .tw', (cards) => {
    const count = (cls) => cards.filter(c => c.classList.contains(cls)).length;
    const glued = cards.filter(c => [...c.classList].some(t => (t.match(/in-/g) || []).length > 1)).length;
    return { low: count('in-low'), speed: count('in-speed'), fps: count('in-fps'), glued };
  });
  check('классы пресетов на карточках не склеены', membership.glued === 0, `склеено ${membership.glued}`);
  check('состав пресетов: 19 «Низкий пинг» / 5 «Скорость» / 23 «Кадры»',
    membership.low === 19 && membership.speed === 5 && membership.fps === 23, JSON.stringify(membership));

  await clickSeg('Низкий пинг');
  const lowDim = await dimmed();
  check('пресет «Низкий пинг» приглушает то, что в него не входит (47 − 19 = 28)',
    lowDim === 28, `приглушено ${lowDim}`);
  await page.screenshot({ path: path.join(OUT, 'preset-low.png') });

  await clickSeg('Кадры');
  const fpsDim = await dimmed();
  check('пресет «Кадры» приглушает 47 − 23 = 24 карточки', fpsDim === 24, `приглушено ${fpsDim}`);

  await clickSeg('Скорость');
  const speedDim = await dimmed();
  check('пресет «Скорость» приглушает 47 − 5 = 42 карточки', speedDim === 42, `приглушено ${speedDim}`);

  await clickSeg('Всё');
  check('возврат к «Всё» показывает все карточки полностью', await dimmed() === 0);

  // ── массовый выбор: режимы меняют вид ползунков ───────────────────────────────────────────
  // Берём карточку, которая выключена по умолчанию: на ней видно все три режима.
  const offIndex = await page.evaluate(() => [...document.querySelectorAll('#page-tweaks .tw')]
    .findIndex(card => !card.querySelector('.sw input').checked));
  check('в каталоге есть твики, выключенные по умолчанию', offIndex > 0, `индекс ${offIndex}`);

  const sliderLook = (index) => page.evaluate((i) => {
    const sl = document.querySelectorAll('#page-tweaks .tw .sl')[i];
    const styles = getComputedStyle(sl);
    const knob = getComputedStyle(sl, '::before');
    return {
      gradient: styles.backgroundImage.includes('linear-gradient'),
      knobMoved: knob.transform !== 'none' && !knob.transform.includes(', 0, 0)'),
      pointer: getComputedStyle(sl.parentElement.querySelector('input')).pointerEvents,
    };
  }, index);

  const before = await sliderLook(offIndex);
  check('выключенный твик выглядит как выключенный (серый, кружок слева)',
    !before.gradient && !before.knobMoved, JSON.stringify(before));

  await clickSeg('включить все');
  const allOn = await sliderLook(offIndex);
  check('«включить все» включает выключенные твики — ползунок стал зелёным, кружок справа',
    allOn.gradient && allOn.knobMoved, JSON.stringify(allOn));
  await page.screenshot({ path: path.join(OUT, 'selection-all.png') });

  await clickSeg('отключить все');
  const noneOff = await sliderLook(offIndex);
  check('«отключить все» выключает все твики — ползунок снова серый',
    !noneOff.gradient && !noneOff.knobMoved, JSON.stringify(noneOff));
  await page.screenshot({ path: path.join(OUT, 'selection-none.png') });

  const onCardOff = await page.evaluate(() => {
    const card = [...document.querySelectorAll('#page-tweaks .tw')]
      .find(c => c.querySelector('.sw input').checked);
    const sl = card.querySelector('.sl');
    return getComputedStyle(sl).backgroundImage.includes('linear-gradient');
  });
  check('режим «отключить все» распространяется и на включённые по умолчанию твики', !onCardOff);

  await clickSeg('только рекомендуемое');
  const back = await sliderLook(offIndex);
  check('возврат к «только рекомендуемое» восстанавливает исходный вид',
    back.gradient === before.gradient && back.knobMoved === before.knobMoved, JSON.stringify(back));

  const recommendedOn = await page.evaluate(() => [...document.querySelectorAll('#page-tweaks .tw .sw input')]
    .filter(i => i.checked).length);
  check('по умолчанию включено 23 твика из 47', recommendedOn === 23, `включено ${recommendedOn}`);

  // ── живой ползунок отдельного твика всё ещё работает ──────────────────────────────────────
  const toggled = await page.evaluate(() => {
    const input = document.querySelectorAll('#page-tweaks .tw .sw input')[1];
    const was = input.checked;
    input.click();
    return { was, now: input.checked };
  });
  check('отдельный ползунок твика переключается кликом', toggled.was !== toggled.now,
    `${toggled.was} → ${toggled.now}`);

  // ── раскрытие подробностей ────────────────────────────────────────────────────────────────
  const details = await page.evaluate(() => {
    const box = document.querySelector('#page-tweaks details.tw-more');
    box.querySelector('summary').click();
    return box.open && box.getBoundingClientRect().height > 0;
  });
  check('блок «Подробнее» раскрывается', details);

  // ── никаких ошибок в консоли и ни одного скрипта ──────────────────────────────────────────
  const scripts = await page.$$eval('script', (list) => list.length);
  check('на странице нет ни одного <script>', scripts === 0, `${scripts}`);

  await page.screenshot({ path: path.join(OUT, 'tweaks-open.png') });

  // ── проверка отсутствия горизонтальной прокрутки на узком экране ──────────────────────────
  await page.setViewport({ width: 420, height: 900 });
  await new Promise(r => setTimeout(r, 120));
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  check('на узком экране нет горизонтальной прокрутки', overflow <= 1, `шире на ${overflow} px`);
  await page.screenshot({ path: path.join(OUT, 'mobile.png'), fullPage: false });

  await browser.close();

  console.log(`Файл: ${FILE}`);
  console.log(`Скриншоты: ${OUT}`);
  console.log(`=== пройдено ${ok}, проблем ${problems.length} ===`);
  problems.forEach(p => console.log('  ✗ ' + p));
  if (problems.length === 0) console.log('  все проверки пройдены в настоящем браузере');
  process.exit(problems.length === 0 ? 0 : 1);
})();
