#!/usr/bin/env python3
"""Сборка макета интерфейса PingForge.

Принцип: страница полностью отрисовывается статически, JavaScript только улучшает её
(вкладки, фильтры, калькулятор). Если скрипты в предпросмотре отключены — видно всё
содержимое подряд, а не пустые таблицы.

Запуск:
    python3 tools/render_ui_preview.py /var/tmp/catalog.json docs/pingforge-ui-preview.html
"""

import html as html_lib
import json
import sys
from collections import Counter
import re
from pathlib import Path

# На Windows консоль по умолчанию работает в cp1252, и печать кириллицы валит скрипт с
# UnicodeEncodeError (на Linux такой беды нет). Переключаем вывод в UTF-8, чтобы инструмент
# вёл себя одинаково на обеих системах.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

CSS = """
:root{
  /* Палитра — ровно та же, что в App.xaml: макет показывает программу, а не «похожий на неё»
     сайт. Совпадение проверяет tools/check_ui_preview.js, сверяя эти значения с темой. */
  --bg:#070b14; --bg2:#0d1930; --card:#111a2a; --line:#1e2a42; --line2:#24405c;
  --text:#e9effb; --muted:#8fa1bd; --dim:#74869f;
  --accent:#4ade80; --accent2:#38bdf8; --ok:#4ade80; --warn:#f2c94c; --bad:#f2545b; --info:#38bdf8;
  --radius:14px;
}
*{box-sizing:border-box}
html,body{margin:0;padding:0;background:var(--bg);color:var(--text);
  font-family:"Segoe UI",Roboto,-apple-system,"Helvetica Neue",Arial,sans-serif;font-size:15px;line-height:1.55}
a{color:var(--accent);text-decoration:none}
.wrap{max-width:1220px;margin:0 auto;padding:0 20px 64px}

/* ── шапка ── */
.appbar{position:sticky;top:0;z-index:20;background:#0d1420;border-bottom:1px solid var(--line)}
.appbar-inner{max-width:1220px;margin:0 auto;padding:14px 20px 0;display:flex;align-items:center;gap:14px;flex-wrap:wrap}
.logo{display:flex;align-items:center;gap:10px;font-weight:700;font-size:18px}
.logo .mark{width:30px;height:30px;border-radius:9px;background:linear-gradient(135deg,var(--accent),var(--accent2));
  display:flex;align-items:center;justify-content:center}
.spacer{flex:1}
.chip{font-size:12.5px;color:var(--muted);border:1px solid var(--line2);border-radius:999px;padding:4px 11px;display:inline-block}
.chip.ok{color:var(--ok);border-color:#1d4d3c}
.chip.warn{color:var(--warn);border-color:#4d3c14}
nav.tabs{max-width:1220px;margin:0 auto;padding:10px 20px 0;display:flex;gap:4px;overflow-x:auto}

/* ── разделы: без JS видны все, с JS — по одному ── */
section.tab{display:block;padding-top:6px;border-top:1px solid var(--line);margin-top:14px}
html.js section.tab{display:none;border-top:0;margin-top:0}
html.js section.tab.active{display:block}
.section-note{color:var(--dim);font-size:13px;margin-top:4px}

h1{font-size:24px;margin:22px 0 6px}
h2{font-size:18px;margin:26px 0 10px}
h3{font-size:15.5px;margin:18px 0 8px}
h4{font-size:14px;margin:12px 0 4px;color:var(--muted);text-transform:uppercase;letter-spacing:.4px}
p{margin:8px 0}
.muted{color:var(--muted)}
.dim{color:var(--dim)}
.small{font-size:13px}
.card{background:var(--card);border:1px solid var(--line);border-radius:var(--radius);padding:18px;margin:14px 0}
.card.tight{padding:14px}
.grid{display:grid;gap:14px}
.g2{grid-template-columns:repeat(auto-fit,minmax(320px,1fr))}
.g3{grid-template-columns:repeat(auto-fit,minmax(230px,1fr))}
.g4{grid-template-columns:repeat(auto-fit,minmax(170px,1fr))}
.row{display:flex;gap:10px;align-items:center;flex-wrap:wrap}
.between{display:flex;gap:12px;align-items:center;justify-content:space-between;flex-wrap:wrap}
code{font-family:"Cascadia Mono",Consolas,monospace;font-size:12.5px;background:#0b1220;border:1px solid var(--line);
  border-radius:6px;padding:1.5px 6px;color:#bcd3f7;word-break:break-all}
.metric{background:var(--bg2);border:1px solid var(--line);border-radius:12px;padding:14px}
.metric .label{font-size:12.5px;color:var(--muted);text-transform:uppercase;letter-spacing:.4px}
.metric .value{font-size:23px;font-weight:700;margin-top:6px}
.metric .value.na{color:var(--dim);font-size:18px;font-weight:600}
.btn{border:1px solid var(--line2);background:#16203a;color:var(--text);font:inherit;font-weight:600;
  padding:11px 18px;border-radius:11px;cursor:pointer;display:inline-block}
.btn.primary{background:linear-gradient(135deg,var(--accent),var(--accent2));border-color:transparent;color:#04121d}
.btn.big{font-size:16px;padding:15px 26px;border-radius:13px}
.btn.small{padding:7px 12px;font-size:13.5px;border-radius:9px}
.btn[disabled]{opacity:.55;cursor:not-allowed}

.badge{display:inline-block;font-size:12px;font-weight:700;padding:3px 9px;border-radius:999px;border:1px solid;
  white-space:nowrap}
.b-recommended{color:#0a2b1e;background:var(--ok);border-color:var(--ok)}
.b-optional{color:#06283d;background:var(--info);border-color:var(--info)}
.b-caution{color:#3a2a00;background:var(--warn);border-color:var(--warn)}
.tag{display:inline-block;font-size:12px;color:var(--muted);border:1px solid var(--line2);border-radius:999px;
  padding:2px 9px;margin:0 4px 4px 0;white-space:nowrap}
.tag.ok{color:var(--ok);border-color:#1d4d3c}
.tag.warn{color:var(--warn);border-color:#4d3c14}
.tag.bad{color:var(--bad);border-color:#4d2226}
.impact .down{color:var(--bad)} .impact .up{color:var(--ok)} .impact .flat{color:var(--dim)}
.impact{font-family:"Cascadia Mono",Consolas,monospace;font-size:12.5px}

table{width:100%;border-collapse:collapse;font-size:14px}
th,td{text-align:left;padding:10px 12px;border-bottom:1px solid var(--line);vertical-align:top}
th{color:var(--muted);font-size:12.5px;text-transform:uppercase;letter-spacing:.4px;font-weight:600}
tr:hover>td{background:#121a29}
td.num,th.num{text-align:right;font-family:"Cascadia Mono",Consolas,monospace}
.scroll{overflow-x:auto}

input[type=text],select{font:inherit;color:var(--text);background:#0b1220;border:1px solid var(--line2);
  border-radius:9px;padding:9px 11px;min-width:180px}
label.check{display:flex;gap:9px;align-items:flex-start;cursor:pointer}
input[type=checkbox]{margin-top:3px;width:17px;height:17px;accent-color:var(--accent)}

.callout{border-left:3px solid var(--accent);background:#101a2b;border-radius:0 12px 12px 0;padding:13px 16px;margin:14px 0}
/* Примеры файлов: то, что программа реально пишет. Цвета — только из темы. */
.snippet{background:var(--bg2);border:1px solid var(--line);border-radius:10px;padding:10px 12px;margin:8px 0 0;
  font-family:Consolas,"Cascadia Mono",monospace;font-size:12.5px;line-height:1.5;color:var(--text);
  white-space:pre-wrap;word-break:break-word}
.snippet .dim{color:var(--dim)}
.callout.warn{border-left-color:var(--warn)}
.callout.ok{border-left-color:var(--ok)}
.callout.bad{border-left-color:var(--bad)}
.callout b{color:var(--text)}

.window{background:var(--bg2);border:1px solid var(--line2);border-radius:16px;overflow:hidden;margin:14px 0;
  box-shadow:0 18px 50px rgba(0,0,0,.45)}
.window .titlebar{display:flex;align-items:center;gap:10px;padding:11px 14px;background:#0b1220;border-bottom:1px solid var(--line)}
.window .dots{display:flex;gap:6px}
.window .dots i{width:11px;height:11px;border-radius:50%;display:block;background:#31405c}
.window .body{padding:18px}

.bar{height:9px;border-radius:999px;background:#0b1220;border:1px solid var(--line);overflow:hidden;min-width:110px}
.bar i{display:block;height:100%;background:linear-gradient(90deg,var(--accent),var(--accent2))}
.bar i.done{background:linear-gradient(90deg,#2f9e6f,var(--ok))}
.bar i.none{background:#26334a}

/* карточки твиков: <details> работает без JS */
details.tweak{background:var(--card);border:1px solid var(--line);border-radius:12px;margin:8px 0;overflow:hidden}
details.tweak>summary{cursor:pointer;padding:13px 16px;display:flex;gap:10px;align-items:center;flex-wrap:wrap;list-style:none}
details.tweak>summary::-webkit-details-marker{display:none}
details.tweak>summary::before{content:"▸";color:var(--dim);font-size:13px}
details.tweak[open]>summary::before{content:"▾"}
details.tweak[open]>summary{border-bottom:1px solid var(--line);background:#121a29}
.t-title{font-weight:700}
.t-body{padding:14px 16px}
.t-grid{display:grid;gap:14px;grid-template-columns:repeat(auto-fit,minmax(280px,1fr))}
.t-body h4{margin-top:0}
details.tweak[hidden]{display:none}

.verdict{border-radius:12px;padding:12px 14px;margin-top:12px;font-size:14px}
.verdict.relay{background:#10243a;border:1px solid #1d4a72}
.verdict.direct{background:#102b21;border:1px solid #1d5138}
.verdict.none{background:#2b1a1c;border:1px solid #5a2a2e}

.calc{display:grid;gap:16px;grid-template-columns:1fr 1fr}
@media(max-width:820px){.calc{grid-template-columns:1fr}}
.calc .col{background:#0b1220;border:1px solid var(--line);border-radius:12px;padding:14px}
.calc label{display:flex;justify-content:space-between;font-size:13px;color:var(--muted);margin:10px 0 2px}
.calc input[type=range]{width:100%;padding:0}
.score{font-size:26px;font-weight:700}
ul.tight{margin:8px 0;padding-left:20px}
ul.tight li{margin:5px 0}
.legend{display:flex;gap:14px;flex-wrap:wrap;font-size:13px;color:var(--muted);margin:8px 0}
footer.foot{color:var(--dim);font-size:13px;border-top:1px solid var(--line);margin-top:34px;padding-top:16px}

/* ── каркас: меню слева столбиком, содержимое справа ── */
.app{display:grid;grid-template-columns:298px minmax(0,1fr);gap:26px;max-width:1360px;margin:0 auto;
  padding:16px 20px 64px;align-items:start}
.content{min-width:0}
.sidebar{position:sticky;top:76px;display:flex;flex-direction:column;gap:14px;
  max-height:calc(100vh - 96px);overflow:auto;padding-right:6px}
.side-title{font-size:11.5px;font-weight:700;letter-spacing:1px;text-transform:uppercase;color:var(--dim)}
nav.side-nav{display:flex;flex-direction:column;gap:4px}
nav.side-nav a.step{display:flex;gap:11px;align-items:flex-start;padding:9px 11px;border-radius:11px;
  border:1px solid transparent;color:var(--muted)}
.stepnum{flex:0 0 auto;display:inline-flex;align-items:center;justify-content:center;width:21px;height:21px;
  border-radius:7px;background:#16203a;border:1px solid var(--line2);font-size:11.5px;font-weight:700;color:var(--muted)}
nav.side-nav a.step .steptext{display:flex;flex-direction:column;line-height:1.25}
nav.side-nav a.step .steptext b{color:var(--text);font-size:14.5px;font-weight:650}
nav.side-nav a.step .steptext span{font-size:12px;color:var(--dim)}
nav.side-nav a.step:hover{background:#121a29;border-color:var(--line)}
nav.side-nav a.step[aria-current="true"]{background:#16223a;border-color:var(--line2)}
nav.side-nav a.step[aria-current="true"] .stepnum{background:linear-gradient(135deg,var(--accent),var(--accent2));
  border-color:transparent;color:#04121d}
.side-card{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:13px 14px;
  display:flex;flex-direction:column;gap:9px}
.side-card .btn{width:100%;text-align:center}
.side-foot{font-size:12px;color:var(--dim);border-top:1px solid var(--line);padding-top:11px}
.valset .pick{display:flex;gap:9px;align-items:baseline;padding:6px 0;border-bottom:1px solid var(--line)}
.valset .pick:last-child{border-bottom:0}
.valset input[type=radio]{margin-top:4px}
.valset .mine input[type=text]{min-width:120px;max-width:180px}
.note-inline{font-size:12.5px;color:var(--dim);margin-top:6px}
@media(max-width:1040px){.app{grid-template-columns:1fr}.sidebar{position:static;max-height:none;overflow:visible}}
.eyebrow{font-size:11.5px;font-weight:700;letter-spacing:1px;text-transform:uppercase;color:var(--accent);margin:20px 0 0}
h1{font-size:25px;margin:4px 0 6px}
.section-tools{display:flex;gap:12px;align-items:flex-start;flex-wrap:wrap;background:#0f1724;
  border:1px solid var(--line);border-radius:12px;padding:11px 14px;margin:12px 0 4px}
ul.dotlist{display:flex;gap:16px;flex-wrap:wrap;list-style:none;margin:0;padding:0}
ul.dotlist li{display:flex;gap:7px;align-items:baseline}
ul.dotlist li::before{content:"\25c6";color:var(--accent);font-size:9px;line-height:1.9}
.grouphd{display:flex;align-items:baseline;gap:12px;flex-wrap:wrap;margin:26px 0 4px;
  padding-bottom:8px;border-bottom:1px solid var(--line2)}
.grouphd h2{margin:0}
.grouphd .cnt{font-size:12.5px;color:var(--dim)}
.groupnote{color:var(--muted);font-size:13.5px;margin:6px 0 10px;max-width:84ch}
.hero{border:1px solid var(--line2);border-radius:16px;padding:18px 20px;margin:16px 0;
  background:linear-gradient(155deg,#141d2e,#0f1623)}
.hero h2{margin:0 0 4px}
.toolbar{display:flex;gap:10px;align-items:center;flex-wrap:wrap;background:var(--card);
  border:1px solid var(--line);border-radius:12px;padding:12px 14px;margin:14px 0}
.legendbox{display:flex;gap:18px;flex-wrap:wrap;font-size:13px;color:var(--muted);margin:2px 0}
.legendbox div{display:flex;gap:8px;align-items:center}
.up{display:inline-block;margin-top:12px;font-size:13px;color:var(--muted)}
a.up:hover{color:var(--text)}
:focus-visible{outline:2px solid var(--accent);outline-offset:2px;border-radius:6px}
h2.parthead{font-size:20px;margin:22px 0 6px;padding-top:14px;border-top:1px solid var(--line)}
.simple{border-left:3px solid var(--ok);background:#0f1a17;border-radius:0 12px 12px 0;padding:12px 15px;margin:14px 0}
.simple b{color:var(--text)}
.three{display:grid;gap:12px;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));margin:12px 0}
.three .step{background:#0b1220;border:1px solid var(--line);border-radius:12px;padding:13px 15px}
.three .step b{display:block;color:var(--text);margin-bottom:4px}
.three .step .n{display:inline-flex;width:22px;height:22px;align-items:center;justify-content:center;
  border-radius:8px;background:linear-gradient(135deg,var(--accent),var(--accent2));color:#04121d;
  font-weight:700;font-size:12.5px;margin-bottom:7px}
.glossary dt{font-weight:700;margin-top:10px}
.glossary dd{margin:2px 0 0;color:var(--muted);font-size:13.5px}
@media(max-width:760px){nav.side-nav a.step{font-size:13px;padding:8px 10px;gap:6px}}

/* то, что появляется только при работающих скриптах */
.js-only{display:none}
html.js .js-only{display:block}
html.js .js-only.flex{display:flex}
@media print{html.js section.tab{display:block !important}}

/* ── меню и облик: понятнее с первого взгляда ── */
html{scroll-behavior:smooth}
section.tab{scroll-margin-top:88px}

aside.sidebar{position:sticky;top:64px}

.side-title{font-size:11.5px;letter-spacing:.16em;text-transform:uppercase;color:var(--dim)}

.navgroup{margin:14px 0 5px 4px;font-size:11px;letter-spacing:.14em;text-transform:uppercase;color:var(--dim);
  display:flex;align-items:center;gap:8px}
.navgroup::after{content:"";flex:1;height:1px;background:var(--line)}

nav.side-nav a.step{position:relative;transition:background .15s,border-color .15s,transform .15s;
  padding:10px 12px;gap:10px;border:1px solid transparent}
nav.side-nav a.step:hover{background:#121a29;border-color:var(--line);transform:translateX(2px)}
nav.side-nav a.step[aria-current="true"]{background:#16223a;border-color:var(--line2)}
nav.side-nav a.step[aria-current="true"]::before{content:"";position:absolute;left:-9px;top:11px;bottom:11px;
  width:3px;border-radius:3px;background:linear-gradient(180deg,var(--accent),var(--accent2))}
nav.side-nav a.step .stepnum{flex:0 0 24px;width:24px;height:24px;border-radius:9px;display:grid;place-items:center;
  font-size:12.5px;font-weight:700;color:var(--muted);background:#0b1220;border:1px solid var(--line);margin-top:1px}
nav.side-nav a.step[aria-current="true"] .stepnum{color:#04121d;border-color:transparent;
  background:linear-gradient(135deg,var(--accent),var(--accent2))}
nav.side-nav a.step .steptext{flex:1}
nav.side-nav a.step .steptext span{color:var(--dim);font-size:11.5px;margin-top:2px}
nav.side-nav a.step .navbadge{flex:0 0 auto;align-self:flex-start;margin-top:2px;font-size:11px;font-weight:650;
  color:var(--muted);background:#0b1220;border:1px solid var(--line);border-radius:999px;padding:1px 8px}
nav.side-nav a.step[aria-current="true"] .navbadge{color:var(--text);border-color:var(--line2)}

.side-tip{margin-top:14px;padding:11px 13px;border:1px dashed var(--line2);border-radius:12px;
  font-size:12.5px;color:var(--muted);background:#0d1420}
.side-tip b{color:var(--text)}

/* оглавление в начале страницы: видно без скриптов */
.toc{margin:18px 0 6px}
.toc-title{font-size:13px;color:var(--muted);margin:0 0 10px 2px}
.toc-title b{color:var(--text);font-size:15px}
.tocgrid{display:grid;gap:12px;grid-template-columns:repeat(auto-fit,minmax(300px,1fr))}
a.toc-tile{display:flex;gap:12px;align-items:flex-start;text-decoration:none;color:inherit;
  background:#0f1725;border:1px solid var(--line);border-radius:var(--radius);padding:13px 15px;
  transition:border-color .15s,transform .15s,background .15s}
a.toc-tile:hover{border-color:var(--line2);background:#131c2c;transform:translateY(-2px)}
a.toc-tile .toc-num{flex:0 0 28px;width:28px;height:28px;border-radius:10px;display:grid;place-items:center;
  font-weight:700;font-size:13.5px;color:#04121d;background:linear-gradient(135deg,var(--accent),var(--accent2))}
a.toc-tile b{display:block;font-size:14.5px}
a.toc-tile .toc-desc{display:block;color:var(--muted);font-size:13px;margin-top:3px}
a.toc-tile .toc-inside{display:block;color:var(--dim);font-size:12px;margin-top:6px}

/* переходы «назад / дальше» в конце каждого раздела */
.secnav{display:flex;flex-wrap:wrap;gap:10px;align-items:center;margin:26px 0 8px;padding-top:14px;
  border-top:1px dashed var(--line);font-size:13px}
.secnav a,.secnav span.end{border:1px solid var(--line);border-radius:10px;padding:7px 12px;color:var(--muted);
  background:#0b1220}
.secnav a:hover{color:var(--text);border-color:var(--line2)}
.secnav .secnav-mid{margin:0 auto}
.secnav span.end{color:var(--dim)}
@media(max-width:760px){.secnav{font-size:12.5px}.secnav .secnav-mid{margin:0}}

/* ── мини-оглавления и цветовые метки подкатегорий ── */
.toc-mini{background:#0f1725;border:1px solid var(--line);border-radius:12px;margin:12px 0;overflow:hidden}
.toc-mini > summary{cursor:pointer;padding:10px 13px;font-size:13px;font-weight:640;color:var(--text)}
.toc-mini[open] > summary{border-bottom:1px solid var(--line);background:#101a29}
.toc-mini-links{display:flex;flex-wrap:wrap;gap:8px;padding:11px 13px}
.toc-mini-links a{background:#0b1220;border:1px solid var(--line);border-radius:999px;padding:5px 11px;
  font-size:12.5px;color:var(--muted)}
.toc-mini-links a:hover{border-color:var(--line2);color:var(--text)}
.whatsnew{border-color:var(--line2)}
.whatsnew ul{margin:6px 0 2px;padding-left:16px}
.whatsnew li{margin:4px 0}
.tweak[data-focus="Ping"]{border-left:4px solid #38bdf8}
.tweak[data-focus="Speed"]{border-left:4px solid #4ade80}
.tweak[data-focus="Fps"]{border-left:4px solid #a78bfa}
.tweak[data-focus="Input"]{border-left:4px solid #fbbf24}
.tweak[data-focus="System"]{border-left:4px solid #64748b}

/* ── название продукта в шапке ── */
.logo-text{display:flex;flex-direction:column;line-height:1.15}
.logo-name{font-size:16px;font-weight:700;color:var(--text)}
.appbar-sub{display:block;font-size:11px;color:var(--dim);font-weight:500;letter-spacing:.02em;margin-top:1px}
.logo{align-items:center}
.chart{background:#0c1420;border:1px solid var(--line);border-radius:12px;padding:10px 12px 6px}
.chart h4{margin:2px 0 6px;font-size:12.5px;color:var(--muted);text-transform:uppercase;letter-spacing:.05em}
.chart svg{display:block;width:100%;height:auto}
.chart .axis{fill:#74869f;font-size:9px}
.chart .grid{stroke:#1c2534;stroke-width:.6}
.legend{display:flex;flex-wrap:wrap;gap:12px;margin:6px 0 0;font-size:11.5px;color:var(--muted)}
.legend i{display:inline-block;width:12px;height:3px;border-radius:2px;margin-right:5px;vertical-align:middle}

/* ── карточки твиков: ползунок включения, блоки, ничего не вылезает за рамки ── */
.tweak{display:block;background:var(--card);border:1px solid var(--line);border-radius:var(--radius);
  margin:10px 0;overflow:hidden}
.tweak.on{border-color:var(--line2);box-shadow:0 0 0 1px #1d2b44 inset}
.tweak *{min-width:0}
.t-head{display:flex;gap:12px;align-items:flex-start;padding:12px 14px;background:#111a29}
.t-titles{flex:1 1 auto;min-width:0}
.t-title{font-weight:650;font-size:15px;overflow-wrap:anywhere}
.t-sub{color:var(--muted);font-size:13px;margin-top:3px;overflow-wrap:anywhere}
.t-badges{display:flex;flex-wrap:wrap;gap:6px;justify-content:flex-end;max-width:46%}
@media(max-width:760px){.t-head{flex-wrap:wrap}.t-badges{max-width:100%;justify-content:flex-start}}
.switch{position:relative;display:inline-block;width:48px;height:27px;flex:0 0 48px;margin-top:2px}
.switch input{position:absolute;opacity:0;width:100%;height:100%;margin:0;cursor:pointer}
.switch .slider{position:absolute;inset:0;background:#1b2740;border:1px solid var(--line2);border-radius:999px;transition:.18s}
.switch .slider::before{content:"";position:absolute;width:19px;height:19px;left:4px;top:3px;border-radius:50%;
  background:#93a3bd;transition:.18s}
.switch input:checked + .slider{background:linear-gradient(135deg,var(--ok),var(--accent));border-color:transparent}
.switch input:checked + .slider::before{transform:translateX(21px);background:#04121d}
.switch input:focus-visible + .slider{outline:2px solid var(--accent);outline-offset:2px}
.switch input:disabled + .slider{opacity:.6}
.t-state{font-size:12px;color:var(--dim);margin-top:6px;text-align:center}
.tweak.on .t-state{color:var(--ok)}
.t-details{border-top:1px solid var(--line)}
.t-details > summary{cursor:pointer;padding:9px 14px;font-size:13px;color:var(--muted);background:#0b1220}
.t-details > summary:hover{color:var(--text)}
.t-blocks{display:grid;gap:12px;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));padding:12px 14px 16px}
.t-block{background:#0b1220;border:1px solid var(--line);border-radius:12px;padding:12px 14px;max-width:100%;overflow-wrap:anywhere}
.t-block.wide{grid-column:1/-1}
.t-block h4{margin:0 0 6px;font-size:12px;color:var(--muted);text-transform:uppercase;letter-spacing:.06em}
.t-block p{margin:0;font-size:13.5px;overflow-wrap:anywhere}
.t-block code{overflow-wrap:anywhere;word-break:break-word}
.verdict{display:inline-flex;align-items:center;gap:6px;font-weight:650}
.v-yes{color:var(--ok)}.v-maybe{color:var(--accent)}.v-care{color:var(--warn)}.v-no{color:var(--bad)}

/* ── подкатегории ── */
.focusbar{display:flex;flex-wrap:wrap;gap:8px;margin:14px 0 6px}
.focusbar a{display:inline-flex;gap:8px;align-items:center;background:#0f1725;border:1px solid var(--line);
  border-radius:999px;padding:6px 12px;color:var(--muted);font-size:13px}
.focusbar a:hover{border-color:var(--line2);color:var(--text)}
.focusbar b{color:var(--text)}
.bulkbar{display:flex;flex-wrap:wrap;gap:10px;align-items:center;background:#101a29;border:1px solid var(--line2);
  border-radius:12px;padding:11px 13px;margin:12px 0}
.presetbox{display:grid;gap:12px;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));margin:12px 0}
.presetcard{background:#0f1725;border:1px solid var(--line);border-radius:12px;padding:12px 14px}
.presetcard h4{margin:0 0 4px;font-size:14px}
.presetcard .cnt{color:var(--accent);font-weight:700}
.scaleopts{display:flex;gap:10px;margin-top:10px;flex-wrap:wrap}
.scaleopts label{display:inline-flex;align-items:center;gap:7px;font-size:12px;color:var(--muted);border:1px solid var(--line2);border-radius:999px;padding:6px 13px;cursor:pointer}
.scaleopts input{margin:0;accent-color:var(--accent)}
.scaleopts label:has(input:checked){color:var(--text);border-color:var(--accent);background:#18243c;font-weight:600}
.chiprow{display:flex;flex-wrap:wrap;gap:6px;margin-top:8px}
.chiprow span{background:#0b1220;border:1px solid var(--line);border-radius:8px;padding:2px 7px;font-size:11.5px;color:var(--muted)}
.chiprow span.on{background:#16223a;border-color:var(--line2);color:var(--text);font-weight:650}
.overflow-safe{overflow-wrap:anywhere;word-break:break-word}
"""

SCRIPT = r"""
// Скрипты — только улучшение. Всё содержимое уже есть в разметке.
document.documentElement.classList.add('js');

const badgeClass = b => 'b-' + String(b).toLowerCase();

/* ── разделы: один пункт меню может показывать одну или две части ── */
const GROUPS = {
  overview: ['overview'],
  tweaks:   ['apply', 'tweaks'],
  network:  ['routing', 'diagnostics'],
  journal:  ['journal'],
  settings: ['settings'],
  limits:   ['progress', 'about']
};
const links = [...document.querySelectorAll('nav.side-nav a[data-group]')];

function activate(group, push){
  const parts = GROUPS[group] || [group];
  document.querySelectorAll('section.tab').forEach(s => s.classList.toggle('active', parts.includes(s.id)));
  links.forEach(a => a.setAttribute('aria-current', String(a.dataset.group === group)));
  const chip = document.getElementById('whereAmI');
  if (chip) {
    const label = document.querySelector('nav.side-nav a[data-group="' + group + '"] .steptext b');
    chip.textContent = 'открыт раздел: ' + (label ? label.textContent : group);
  }
  // history и прокрутка доступны не всегда (предпросмотр в iframe) — это не повод ломать переключение
  if (push && history.replaceState) { try { history.replaceState(null, '', '#' + parts[0]); } catch (e) { } }
}

links.forEach(a => a.addEventListener('click', ev => {
  ev.preventDefault();
  activate(a.dataset.group, true);
  try { window.scrollTo({top:0, behavior:'smooth'}); } catch (e) { }
}));

/* Оглавление в начале страницы и переходы «назад/далее» в конце частей — обычные ссылки с #якорем.
   Раньше их нажатие меняло адрес, но не раздел: слушателя hashchange не было. Теперь есть, поэтому
   работают и они, и кнопки «назад/вперёд» в браузере. */
window.addEventListener('hashchange', () => {
  const key = groupOf((location.hash || '').slice(1));
  if (key) { activate(key, false); }
});

function groupOf(hash){
  for (const key in GROUPS) { if (GROUPS[key].includes(hash)) return key; }
  return null;
}
const initial = (location.hash || '').slice(1);
activate(groupOf(initial) || 'overview', false);

/* ── ползунки твиков и массовый выбор ── */
const tweakToggles = [...document.querySelectorAll('.tweak .t-toggle')];
const pickCount = document.getElementById('pickCount');

function refreshTweaks(){
  let on = 0;
  tweakToggles.forEach(t => {
    const card = t.closest('.tweak');
    if (card) card.classList.toggle('on', t.checked);
    if (t.checked) on++;
  });
  if (pickCount) pickCount.textContent = 'выбрано: ' + on + ' из ' + tweakToggles.length;
}

tweakToggles.forEach(t => t.addEventListener('change', refreshTweaks));

document.getElementById('pickRecommended')?.addEventListener('click', () => {
  tweakToggles.forEach(t => { t.checked = t.dataset.recommended === '1'; });
  refreshTweaks();
});

document.getElementById('pickAll')?.addEventListener('click', () => {
  tweakToggles.forEach(t => { t.checked = true; });
  refreshTweaks();
});

document.getElementById('pickNone')?.addEventListener('click', () => {
  tweakToggles.forEach(t => { t.checked = false; });
  refreshTweaks();
});

refreshTweaks();

/* ── фильтры каталога ── */
const cards = [...document.querySelectorAll('details.tweak')];
function applyFilters(){
  const q = document.getElementById('twSearch').value.trim().toLowerCase();
  const cat = document.getElementById('twCat').value;
  const badge = document.getElementById('twBadge').value;
  const lowPing = document.getElementById('twLowPing').checked;
  let shown = 0;
  cards.forEach(c => {
    const text = c.dataset.text;
    const ok = (!q || text.includes(q)) &&
               (!cat || c.dataset.cat === cat) &&
               (!badge || c.dataset.badge === badge) &&
               (!lowPing || c.dataset.lowping === '1');
    c.hidden = !ok;
    if (ok) shown++;
  });
  document.querySelectorAll('.grouphd').forEach(hd => {
    let node = hd, visible = 0;
    while ((node = node.nextElementSibling) && !node.classList.contains('grouphd')) {
      if (node.classList.contains('tweak') && !node.hidden) visible++;
    }
    const note = hd.nextElementSibling && hd.nextElementSibling.classList.contains('groupnote')
      ? hd.nextElementSibling : null;
    hd.hidden = visible === 0;
    if (note) note.hidden = visible === 0;
  });
  document.getElementById('twCount').textContent =
    'Показано ' + shown + ' из ' + cards.length + ' твиков каталога.';
}
['twSearch','twCat','twBadge','twLowPing'].forEach(id => {
  const el = document.getElementById(id);
  el.addEventListener('input', applyFilters);
  el.addEventListener('change', applyFilters);
});
document.getElementById('twExpand').addEventListener('click', () => {
  const anyClosed = cards.some(c => !c.hidden && !c.open);
  cards.forEach(c => { if (!c.hidden) c.open = anyClosed; });
});
applyFilters();

/* ── калькулятор маршрутов: веса берутся из кода проекта ── */
const ROUTING = JSON.parse(document.getElementById('routingData').textContent);
function readSide(prefix){
  return {
    rtt: +document.getElementById(prefix+'Rtt').value,
    jitter: +document.getElementById(prefix+'Jitter').value,
    loss: +document.getElementById(prefix+'Loss').value,
    hops: +document.getElementById(prefix+'Hops').value
  };
}
function scoreOf(c){
  const w = ROUTING.weights;
  return c.rtt*w.perRttMs + c.jitter*w.perJitterMs + c.loss*c.loss*w.perLossPercentSquared
       + Math.max(0, c.hops-1)*w.perHop;
}
function updateCalc(){
  const d = readSide('d'), r = readSide('r');
  const sd = scoreOf(d), sr = scoreOf(r);
  document.getElementById('dRttV').textContent = d.rtt + ' мс';
  document.getElementById('dJitterV').textContent = d.jitter + ' мс';
  document.getElementById('dLossV').textContent = d.loss + ' %';
  document.getElementById('dHopsV').textContent = d.hops;
  document.getElementById('rRttV').textContent = r.rtt + ' мс';
  document.getElementById('rJitterV').textContent = r.jitter + ' мс';
  document.getElementById('rLossV').textContent = r.loss + ' %';
  document.getElementById('rHopsV').textContent = r.hops;
  document.getElementById('dScore').textContent = sd.toFixed(2);
  document.getElementById('rScore').textContent = sr.toFixed(2);

  const t = ROUTING.thresholds, f = ROUTING.failover;
  const dOk = d.loss <= t.maxLossPercent && d.jitter <= t.maxJitterMs && d.rtt <= t.maxRttMs;
  const rOk = r.loss <= t.maxLossPercent && r.jitter <= t.maxJitterMs && r.rtt <= t.maxRttMs;
  const gain = (sd - sr).toFixed(2);

  let cls = 'direct', text;
  if (!dOk && !rOk){
    cls = 'none';
    text = 'Ни один путь не проходит пороги пригодности (потери ≤ ' + t.maxLossPercent + ' %, джиттер ≤ ' +
           t.maxJitterMs + ' мс, RTT ≤ ' + t.maxRttMs + ' мс). Приложение скажет «маршрутизировать некуда» ' +
           'и не будет включать оптимизацию ради галочки.';
  } else if (!dOk){
    cls = 'relay';
    text = 'Прямой путь непригоден — предлагается релей с объяснением причины, а не голая надпись «оптимизировано».';
  } else if (rOk && sr < sd - 1){
    cls = 'relay';
    text = 'Релей выигрывает ' + gain + ' балла (порог 1.00) — предлагается переключение. Если выигрыш исчезнет, ' +
           'приложение вернёт прямой путь после ' + f.goodSamplesBeforeReturn + ' замеров и предложит выключить оптимизацию.';
  } else {
    text = 'Прямое подключение не хуже релея: выигрыш ' + gain + ' балла меньше порога 1.00. Рекомендация — играть ' +
           'напрямую. Релей не включается «для отчёта».';
  }
  const box = document.getElementById('calcVerdict');
  box.className = 'verdict ' + cls;
  box.textContent = text;
}
['dRtt','dJitter','dLoss','dHops','rRtt','rJitter','rLoss','rHops'].forEach(id => {
  document.getElementById(id).addEventListener('input', updateCalc);
});
updateCalc();

/* ── главная кнопка: показывает, как будет выглядеть работа, и честный итог ── */
const RT = JSON.parse(document.getElementById('realtimeData').textContent);
const rtChip = document.getElementById('rtChip');
const rtHint = document.getElementById('optimizeHint');
const rtText = {
  off: 'состояние: выключено',
  on: 'состояние: работает — контроль качества каждые ' + RT.monitorIntervalSeconds + ' с',
  stopped: 'состояние: остановлено, изменения сняты'
};
let rtRunning = false;

function rtRender(running, stopped) {
  rtChip.textContent = running ? rtText.on : (stopped ? rtText.stopped : rtText.off);
  rtHint.innerHTML = running
    ? 'Игра: Fortnite (пример), поток UDP на 3.4.5.6:9000. Идёт контроль: окна по ' + RT.samplesPerWindow +
      ' проб, просадка → переключение пути, восстановление → возврат в штатный режим. ' +
      '<b>Числа ниже — пример, а не замер этой страницы.</b>'
    : 'Кнопка работает с <b>уже запущенной игрой</b>: находит её живой сетевой поток, меряет исходный пинг, ' +
      'применяет маршрут только для адреса игрового сервера и политику QoS, а дальше непрерывно следит за качеством ' +
      'каждые ' + RT.monitorIntervalSeconds + ' с. Пока кнопка не нажата, программа не делает ничего.';
}

document.getElementById('btnOptimize').addEventListener('click', () => {
  const wasRunning = rtRunning;
  rtRunning = !rtRunning;
  rtRender(rtRunning, wasRunning && !rtRunning);
  if (rtRunning) {
    rtHint.insertAdjacentHTML('beforeend',
      '<br><span class="tag ok">было 120 мс → стало 84 мс (пример)</span> ' +
      '<span class="tag">физический предел 78 мс</span>');
  } else {
    rtHint.insertAdjacentHTML('beforeend',
      '<br><span class="tag warn">Остановлено: маршрут и политика QoS сняты, сеть вернулась в исходное состояние.</span> ' +
      '<span class="tag">Итог: 120 → 86 мс (медиана), предел пути 78 мс</span>');
  }
});
const routeChip = document.getElementById('routeChip');
const routeStart = document.getElementById('btnPickRoute');
if (routeStart) routeStart.addEventListener('click', () => {
  routeChip.className = 'chip warn';
  routeChip.textContent = 'меряем пути: окно 1 из 15 (пример)';
  setTimeout(() => { routeChip.textContent = 'замеры идут: окно 4 из 15, осталось ~22 с'; }, 1200);
  setTimeout(() => {
    routeChip.className = 'chip ok';
    routeChip.textContent = 'прямой путь лучше на 7.2 балла — предложен для применения';
  }, 2600);
});
const routeStop = document.getElementById('btnStopRoute');
if (routeStop) routeStop.addEventListener('click', () => {
  routeChip.className = 'chip';
  routeChip.textContent = 'замеры остановлены: сделано 6 окон из 15 (предварительная оценка)';
});

const sideSave = document.getElementById('sideSaveCfg');
if (sideSave) sideSave.addEventListener('click', () => { activate('settings', true); window.scrollTo({top:0, behavior:'smooth'}); });
const sideLoad = document.getElementById('sideLoadCfg');
if (sideLoad) sideLoad.addEventListener('click', () => { activate('settings', true); window.scrollTo({top:0, behavior:'smooth'}); });

rtRender(false, false);
"""


# ── Значения твиков: что бывает, что означает, когда выбирать ─────────────────────────────
# Данные для макета. Для вычисляемых твиков (MTU, DNS, буферы) список — «как это выглядит»,
# реальные варианты читаются у драйвера или берутся из замеров, и это подписано в таблице.
VALUE_OPTIONS: dict[str, list[tuple[str, str, str]]] = {
    "TCP_ACK_FREQUENCY": [
        ("1", "Подтверждать каждый пришедший пакет сразу", "Рекомендуется для игр: нет ожидания подтверждения"),
        ("2", "Подтверждать каждый второй пакет (значение Windows по умолчанию)", "Экономит трафик, добавляет ожидание"),
        ("0", "Убрать параметр и вернуться к поведению стека по умолчанию", "Если сомневаетесь или нужен «как было»"),
    ],
    "TCP_NO_DELAY": [
        ("1", "Отправлять пакеты сразу, не собирая мелкие в один (Нейгл выключен)", "Рекомендуется для игр: движение и выстрелы уходят без ожидания"),
        ("0", "Алгоритм Нейгла включён — пакеты копятся перед отправкой", "Значение по умолчанию Windows; меньше трафика, больше задержка"),
    ],
    "TCP_INITIAL_RTT": [
        ("300", "Стек стартует с оценки 300 мс (значение Microsoft для игровых профилей)", "Рекомендуется; параметр помечен как «эффект не подтверждён» на новых сборках"),
        ("3000", "Старт с 3000 мс (значение по умолчанию на многих сборках)", "Осторожный вариант: медленнее старт передачи"),
    ],
    "TCP_SYN_RETRANSMISSIONS": [
        ("2", "Две попытки установить соединение до отказа", "Рекомендуется: быстрее отказ при мёртвом сервере матча"),
        ("3", "Три попытки (часто значение по умолчанию)", "Терпеливее на нестабильном канале"),
        ("1", "Одна попытка", "Только для тестов: растут ложные отказы подключения"),
    ],
    "MMCSS_NETWORK_THROTTLING": [
        ("4294967295", "Троттлинг сети снят (0xFFFFFFFF)", "Рекомендуется: Windows не режет сетевой трафик играм"),
        ("10", "Значение по умолчанию — ограничение включено", "Возврат к заводскому поведению"),
    ],
    "MMCSS_SYSTEM_RESPONSIVENESS": [
        ("10", "Под мультимедиа резервируется минимум ресурсов", "Рекомендуется для игр"),
        ("20", "Значение по умолчанию", "Возврат к заводскому поведению"),
        ("0", "Максимально агрессивный режим", "Не рекомендуем: может душить звук в плеерах и браузере"),
    ],
    "GAME_TASK_SCHEDULING_CATEGORY": [
        ("High", "Игровая задача получает высокую категорию планирования", "Рекомендуется"),
        ("Medium", "Значение по умолчанию", "Возврат к заводскому поведению"),
        ("Low", "Пониженная категория", "Хуже для игр — показываем, чтобы было видно направление"),
    ],
    "GAME_TASK_PRIORITY": [
        ("6", "Приоритет 6 — принятое игровое значение", "Рекомендуется"),
        ("2", "Часто значение по умолчанию", "Возврат к заводскому поведению"),
        ("8", "Максимум", "Риск: система может обделять другие задачи"),
    ],
    "GAME_TASK_SFIO_PRIORITY": [
        ("High", "Высокий приоритет операций ввода-вывода для игры", "Рекомендуется"),
        ("Normal", "Значение по умолчанию", "Возврат к заводскому поведению"),
    ],
    "NIC_ENERGY_EFFICIENT_ETHERNET": [
        ("Disabled", "EEE выключен: карта не засыпает между кадрами", "Рекомендуется для игр: меньше задержка на «пробуждении»"),
        ("Enabled", "Режим энергосбережения (часто по умолчанию)", "Экономия электричества, но лишняя задержка"),
        ("Auto", "Драйвер решает сам", "Компромисс, если кабель длинный и нестабильный"),
    ],
    "NIC_INTERRUPT_MODERATION": [
        ("Disabled", "Прерывания обрабатываются сразу", "Рекомендуется: минимальная задержка"),
        ("Enabled", "Прерывания группируются (меньше нагрузка на CPU)", "Значение по умолчанию на многих адаптерах"),
    ],
    "NIC_FLOW_CONTROL": [
        ("Disabled", "Кадры паузы не запрашиваются", "Рекомендуется в играх: pause-кадры добавляют задержку"),
        ("Enabled", "Управление потоком включено", "Полезно при перегрузке канала, но повышает задержку"),
    ],
    "NIC_RECEIVE_BUFFERS": [
        ("максимум драйвера", "Наибольшее допустимое число буферов", "Рекомендуется; программа читает список значений у драйвера"),
        ("значение драйвера", "Текущая настройка", "Оставить, если нормально работают другие устройства"),
        ("ниже максимума", "Меньше памяти под приём", "Если драйвер требует запас памяти — видно в списке значений"),
    ],
    "NIC_TRANSMIT_BUFFERS": [
        ("максимум драйвера", "Наибольшее допустимое число буферов передачи", "Рекомендуется; список значений читается у драйвера"),
        ("значение драйвера", "Текущая настройка", "Оставить, если есть проблемы с драйвером"),
    ],
    "TCP_RSS_ENABLED": [
        ("enabled", "Приём распределяется по ядрам", "Рекомендуется на многоядерных системах"),
        ("disabled", "Приём обрабатывает одно ядро", "Только для диагностики"),
    ],
    "TCP_AUTOTUNING_NORMAL": [
        ("normal", "Окно приёма подстраивается автоматически", "Рекомендуется"),
        ("restricted", "Автоподстройка ограничена", "Хуже пропускная способность"),
        ("disabled", "Автоподстройка выключена", "Резко хуже загрузки"),
        ("highlyrestricted", "Максимально ограничена", "Не для игр"),
    ],
    "TCP_ECN_DISABLED": [
        ("disabled", "ECN выключен", "Рекомендуется: с провайдерами и серверами совместимо чаще"),
        ("enabled", "ECN включён (часто по умолчанию на новых сборках)", "Если знаете, что путь поддерживает ECN"),
    ],
    "MTU_SUBINTERFACE_OPTIMAL": [
        ("значение из DF-проб", "Наибольший размер пакета, который реально проходит без фрагментации", "Рекомендуется; программа показывает, какими пробами это найдено"),
        ("1500", "Стандарт Ethernet", "Оставляем, если пробы показали 1500"),
        ("1492", "Типовое значение для PPPoE", "Если ваш провайдер использует PPPoE"),
        ("1420", "Типовое значение для VPN/WireGuard", "Если трафик идёт через туннель"),
        ("1280", "Безопасный минимум", "Когда провайдер режет крупные пакеты молча"),
    ],
    "DNS_SERVERS_FASTEST": [
        ("победители бенчмарка", "Адреса, которые быстрее всех ответили и не подменяют ответы", "Рекомендуется; видно, сколько запросов сделано"),
        ("DNS от роутера/провайдера", "Как сейчас (получены автоматически)", "Оставить, если публичные оказались хуже"),
        ("публичные (1.1.1.1, 8.8.8.8, 9.9.9.9…)", "Известные резолверы из списка кандидатов", "Если нужен предсказуемый DNS вместо провайдерского"),
    ],
    "MOUSE_ACCEL_SPEED": [
        ("0", "Ускорение указателя выключено — движение 1:1 с рукой", "Рекомендуется для игр"),
        ("1", "Ускорение включено (значение Windows по умолчанию)", "Возврат к заводскому поведению"),
        ("2", "Ускорение усилено", "Только для рабочего стола, в шутерах вредит"),
    ],
    "MOUSE_ACCEL_THRESHOLD1": [
        ("0", "Первый порог ускорения выключен", "Рекомендуется вместе с остальными тремя параметрами"),
        ("6", "Порог включён (значение по умолчанию)", "Возврат к заводскому поведению"),
    ],
    "MOUSE_ACCEL_THRESHOLD2": [
        ("0", "Второй порог ускорения выключен", "Рекомендуется"),
        ("10", "Порог включён (значение по умолчанию)", "Возврат к заводскому поведению"),
    ],
    "MOUSE_SENSITIVITY_NEUTRAL": [
        ("10", "Нейтральное положение — Windows не домножает движение (6/11 на шкале указателя)", "Рекомендуется"),
        ("1…20", "Системная скорость указателя; значение ниже 10 замедляет, выше — ускоряет", "Своё значение: чувствительность лучше настраивать в игре"),
    ],
    "KEYBOARD_FILTERKEYS_OFF": [
        ("126", "Фильтр ввода выключен (штатное выключенное состояние)", "Рекомендуется: нажатия не теряются"),
        ("127 и подобные", "Фильтр включён в одном из режимов — короткие и повторные нажатия игнорируются", "Именно это состояние и надо убрать"),
        ("2147483711", "Фильтр включается удержанием правого Shift", "Встречается после случайного долгого нажатия"),
    ],
    "KEYBOARD_REPEAT_DELAY_MIN": [
        ("0", "Минимальная пауза перед автоповтором", "Для чата и макросов; на игровой отклик не влияет"),
        ("1", "Средняя пауза (значение по умолчанию)", "Возврат к заводскому поведению"),
        ("3", "Длинная пауза", "Показываем для полноты картины"),
    ],
    "GAME_DVR_OFF": [
        ("0", "Фоновая запись Game DVR выключена", "Рекомендуется: буфер записи не отбирает кадры"),
        ("1", "Фоновая запись включена (значение по умолчанию)", "Оставить, если пользуетесь записью"),
    ],
    "GAME_DVR_CAPTURE_OFF": [
        ("0", "Захват клипов выключен", "Рекомендуется вместе с фоновая записью"),
        ("1", "Захват включён (Win+Alt+R работает)", "Оставить, если пишете клипы"),
    ],
    "NIC_PACKET_PRIORITY_ON": [
        ("Packet Priority & VLAN Enabled", "Приоритет пакетов включён", "Рекомендуется, если роутер уважает приоритет"),
        ("Packet Priority Enabled / VLAN Disabled", "Приоритет включён, теги VLAN не ставятся", "Если домашняя сеть не использует VLAN"),
        ("Disabled", "Свойство выключено (часто по умолчанию)", "Показываем как состояние «до»"),
        ("варианты драйвера", "Точные строки зависят от производителя: у Intel, Realtek и MediaTek формулировки разные", "Программа читает список значений у драйвера и не угадывает"),
    ],
}

# ── Пример текущего значения: в макете это подписано как пример ──
CURRENT_EXAMPLE: dict[str, str] = {
    "TCP_ACK_FREQUENCY": "2",
    "TCP_NO_DELAY": "0",
    "TCP_INITIAL_RTT": "3000",
    "TCP_SYN_RETRANSMISSIONS": "3",
    "MMCSS_NETWORK_THROTTLING": "10",
    "MMCSS_SYSTEM_RESPONSIVENESS": "20",
    "GAME_TASK_SCHEDULING_CATEGORY": "Medium",
    "GAME_TASK_PRIORITY": "2",
    "GAME_TASK_SFIO_PRIORITY": "Normal",
    "NIC_ENERGY_EFFICIENT_ETHERNET": "Enabled",
    "NIC_INTERRUPT_MODERATION": "Enabled",
    "NIC_FLOW_CONTROL": "Enabled",
    "NIC_RECEIVE_BUFFERS": "512",
    "NIC_TRANSMIT_BUFFERS": "128",
    "TCP_RSS_ENABLED": "enabled",
    "TCP_AUTOTUNING_NORMAL": "normal",
    "TCP_ECN_DISABLED": "enabled",
    "MTU_SUBINTERFACE_OPTIMAL": "1500",
    "DNS_SERVERS_FASTEST": "от роутера (192.168.1.1)",
    "MOUSE_ACCEL_SPEED": "1",
    "MOUSE_ACCEL_THRESHOLD1": "6",
    "MOUSE_ACCEL_THRESHOLD2": "10",
    "MOUSE_SENSITIVITY_NEUTRAL": "10",
    "KEYBOARD_FILTERKEYS_OFF": "126",
    "KEYBOARD_REPEAT_DELAY_MIN": "1",
    "GAME_DVR_OFF": "1",
    "GAME_DVR_CAPTURE_OFF": "1",
    "NIC_PACKET_PRIORITY_ON": "Disabled",
}


def esc(text: str) -> str:
    return html_lib.escape(str(text), quote=True)


def tweak_card(t: dict, index: int) -> str:
    """Карточка твика: ползунок включения сверху, всё остальное — в разворачиваемых блоках.

    Правила оформления: ничего не выходит за рамки блока (overflow-wrap), длинные пути не ломают сетку,
    таблица значений прокручивается по горизонтали, а вердикт «стоит включать или нет» виден сразу.
    """
    # Класс — оформление макета; сами слова вердикта приходят из каталога (TweakVerdict),
    # как и правило «включён по умолчанию».
    verdict_class = VERDICT_CLASS[t["badge"]]
    verdict = (verdict_class,
               t.get("verdictRu") or "нет вердикта",
               t.get("verdictWhyRu") or "Вердикт смотрите в блоке «Зачем для игры»")

    presets = []
    if t["inLowPingPreset"]:
        presets.append("низкий пинг")
    if t["inMaxSpeedPreset"]:
        presets.append("скорость")
    if t.get("inFpsPreset"):
        presets.append("кадры")
    preset_tag = (f'<span class="tag ok">пресеты: {esc(", ".join(presets))}</span>' if presets
                  else '<span class="tag">в пресеты не входит</span>')

    extra_tags = []
    if t["isComputed"]:
        extra_tags.append('<span class="tag">значение считается замерами</span>')
    if t["requiresReboot"]:
        extra_tags.append('<span class="tag warn">нужен перезапуск</span>')
    if t["effectUnconfirmed"]:
        extra_tags.append('<span class="tag warn">эффект не подтверждён</span>')

    def impact(v, kind):
        if v < 0:
            return f'<span class="down">{kind} ↓</span>'
        if v > 0:
            return f'<span class="up">{kind} ↑</span>'
        return f'<span class="flat">{kind} =</span>'

    search_text = " ".join([t["code"], t["titleRu"], t["whatItDoesRu"], t["gamingJustificationRu"],
                            t.get("focusRu", "")]).lower()

    options = VALUE_OPTIONS.get(t["code"], [])
    recommended = t["recommendedRawValue"]
    rec_badge = " <span class='tag ok'>рекомендуем</span>"
    option_rows = "".join(
        f'<tr><td class="overflow-safe"><code>{esc(value)}</code>'
        f'{rec_badge if value == recommended and not t["isComputed"] else ""}</td>'
        f'<td class="small">{esc(meaning)}</td><td class="small muted">{esc(when)}</td></tr>'
        for value, meaning, when in options)
    if option_rows:
        option_rows += (f'<tr><td class="small muted">по умолчанию</td>'
                        f'<td class="small muted" colspan="2">{esc(t["defaultValueNoteRu"])}</td></tr>')

    current = CURRENT_EXAMPLE.get(t["code"], "читается перед применением")
    mine_hint = ("Значение считается пробами перед применением — своё число задаётся в окне «Применить твики»."
                 if t["isComputed"]
                 else "Своё значение проверится на допустимость: система получит то же, что и от штатной настройки Windows.")

    # «включён по умолчанию» — только у тех твиков, что мы действительно советуем включать
    default_on = bool(t.get("enabledByDefault", t["badge"] == "Recommended"))
    default_state = t.get("defaultStateRu") or ("включён в план по умолчанию" if default_on
                                                else "по умолчанию выключен: решаете вы")
    checked = " checked" if default_on else ""
    toggle_attr = f'data-recommended="{1 if default_on else 0}"'

    return f"""      <article class="tweak{(' on' if default_on else '')}" data-code="{esc(t["code"])}"
               data-focus="{esc(t.get("focus", "System"))}" data-badge="{esc(t["badge"])}"
               data-lowping="{1 if t["inLowPingPreset"] else 0}" data-text="{esc(search_text)}">
        <div class="t-head">
          <label class="switch" title="Включить твик в план применения">
            <input type="checkbox" class="t-toggle" {toggle_attr}{checked}
                   aria-label="Включить твик {esc(t["titleRu"])}">
            <span class="slider"></span>
          </label>
          <div class="t-titles">
            <div class="t-title">{esc(t["titleRu"])}</div>
            <div class="t-sub">{esc(t["whatItDoesRu"])}</div>
          </div>
          <div class="t-badges">
            <span class="badge {badge_class(t["badge"])}"><span class="verdict {verdict[0]}">{esc(verdict[1])}</span></span>
            <span class="tag">{esc(t.get("focusRu", ""))}</span>
            <span class="tag">{esc(t["categoryRu"])}</span>
            {preset_tag}
            {"".join(extra_tags)}
          </div>
        </div>
        <div class="t-state">{esc(verdict[2])} · {esc(default_state)}</div>

        <details class="t-details">
          <summary>Подробно: что делает, зачем для игры, какие значения бывают и что писать</summary>
          <div class="t-blocks">
            <div class="t-block"><h4>Что делает</h4><p>{esc(t["whatItDoesRu"])}</p></div>
            <div class="t-block"><h4>Зачем для игры и чего это стоит</h4><p>{esc(t["gamingJustificationRu"])}</p></div>
            <div class="t-block"><h4>Значение по умолчанию</h4><p class="muted">{esc(t["defaultValueNoteRu"])}</p></div>
            <div class="t-block"><h4>Что будет записано</h4><p>{
                "Значение считается замерами до применения — в журнал попадёт и объяснение, почему выбрано именно оно."
                if t["isComputed"] else "Рекомендуемое: <code>" + esc(recommended) + "</code>"}</p></div>
            <div class="t-block"><h4>Куда пишем</h4><p class="small"><code>{esc(t["resourceLocator"])}</code></p></div>
            <div class="t-block"><h4>Влияние и условия</h4><p class="small">
              <span class="impact">{impact(t["ping"], "пинг")} · {impact(t["speed"], "скорость")} · {impact(t["stability"], "стабильность")}</span><br>
              <span class="small dim">{esc(t["code"])} · {esc(t["mechanismRu"])} · риск: {esc(t["risk"])} ·
              {"нужен перезапуск Windows" if t["requiresReboot"] else "без перезапуска"} ·
              {"требуются права администратора" if True else ""}</span></p></div>

            <div class="t-block wide"><h4>Какие значения бывают и что делает каждое</h4>
              <div class="scroll">
                <table>
                  <thead><tr><th>Значение</th><th>Что означает</th><th>Когда выбирать</th></tr></thead>
                  <tbody>{option_rows if option_rows else '<tr><td colspan="3" class="small muted">Список значений читается у драйвера карты или берётся из замеров.</td></tr>'}</tbody>
                </table>
              </div>
            </div>

            <div class="t-block wide"><h4>Что применить</h4>
              <div class="valset">
                <label class="pick"><input type="radio" name="pick-{esc(t["code"])}" checked>
                  <span><b>Рекомендуемое:</b> <code>{esc(recommended) if not t["isComputed"] else "из замеров"}</code>
                  <span class="small muted"> — значение подобрано под игры, объяснение попадёт в журнал</span></span></label>
                <label class="pick"><input type="radio" name="pick-{esc(t["code"])}">
                  <span><b>Оставить как есть:</b> <code>{esc(current)}</code>
                  <span class="small muted"> — твик попадёт в отчёт как «не меняли»</span></span></label>
                <label class="pick mine"><input type="radio" name="pick-{esc(t["code"])}">
                  <span><b>Своё значение:</b> <input type="text" value="{esc(recommended)}" aria-label="Своё значение для {esc(t["code"])}">
                  <span class="small muted"> — {esc(mine_hint)}</span></span></label>
              </div>
              <p class="note-inline">Это макет: переключатели никуда не записываются. В программе «применить» работает только
              через окно со снапшотом и точкой восстановления — твик никогда не пишется в систему молча.</p>
            </div>
          </div>
        </details>
      </article>"""


_ROUTES_DATA: dict = {}
_PERF_DATA: dict = {}


# ── график сети ───────────────────────────────────────────────────────────────────────────────
# Наборы точек ниже — демонстрационные: в макете нет живого замера, поэтому подписи честно
# говорят «пример». Показатели выбраны по обязательному минимуму для такого графика:
# скорость приёма и передачи, RTT (min/средний/p95), джиттер, потери, задержка под нагрузкой,
# время ответа DNS, повторы TCP и окно наблюдения с числом проб.
CHART_WINDOW_SECONDS = 60
CHART_STEP_SECONDS = 5
CHART_SERIES = {
    "down_before": [92, 88, 95, 90, 86, 93, 89, 91, 87, 94, 90, 88, 92],
    "down_after": [92, 90, 95, 93, 91, 94, 92, 93, 90, 95, 93, 92, 94],
    "ping_before": [34, 36, 33, 38, 41, 35, 52, 37, 34, 39, 44, 36, 35],
    "ping_after": [29, 30, 28, 31, 32, 29, 34, 30, 28, 31, 33, 29, 30],
    "jitter_before": [3.1, 3.4, 3.0, 4.2, 5.6, 3.6, 9.8, 4.0, 3.2, 4.4, 6.1, 3.8, 3.5],
    "jitter_after": [2.0, 2.1, 1.9, 2.3, 2.6, 2.2, 3.1, 2.4, 2.0, 2.5, 2.8, 2.2, 2.1],
    "loss_before": [0, 0, 0.4, 0, 1.2, 0, 3.4, 0, 0, 0.6, 1.8, 0, 0],
    "loss_after": [0, 0, 0, 0, 0.2, 0, 0.4, 0, 0, 0, 0.2, 0, 0],
}


def _points(values: list[float], top: float, width: int, height: int) -> str:
    """Точки ломаной линии для SVG: значения в координатах графика."""
    span = max(1, len(values) - 1)
    step = width / span
    scale = height / max(top, 0.001)
    return " ".join(f"{i * step:.1f},{height - min(v, top) * scale:.1f}" for i, v in enumerate(values))


def _chart(title: str, unit: str, top: float, series: list[tuple[str, str, str]], bars: bool = False) -> str:
    """Одна панель графика: сетка, ось времени, ломаные «до» и «после» (или столбики потерь)."""
    w, h = 320, 132
    grid = "".join(f'<line x1="0" y1="{h * k / 4:.0f}" x2="{w}" y2="{h * k / 4:.0f}" stroke="#1c2534" stroke-width="1"/>'
                   for k in range(5))
    body = []
    for label, color, values in series:
        pts = _points(CHART_SERIES[values], top, w, h)
        if bars:
            step = w / max(1, len(CHART_SERIES[values]) - 1)
            scale = h / max(top, 0.001)
            body.append(f'<g fill="{color}" opacity=".85">' + "".join(
                f'<rect x="{i * step - 3:.1f}" y="{h - min(v, top) * scale:.1f}" width="6" '
                f'height="{max(1, min(v, top) * scale):.1f}"/>'
                for i, v in enumerate(CHART_SERIES[values])) + "</g>")
        else:
            body.append(f'<polyline fill="none" stroke="{color}" stroke-width="2" '
                        f'stroke-linejoin="round" points="{pts}"/>')
    ticks = "".join(
        f'<text class="axis" x="{w * i / 2:.0f}" y="{h + 11}" text-anchor="{"start" if i == 0 else ("middle" if i == 1 else "end")}">'
        f'{int(CHART_WINDOW_SECONDS * i / 2)} с</text>' for i in range(3))
    labels = "".join(
        f'<text class="axis" x="2" y="{h - h * k / 4 + 10:.0f}">{top * (4 - k) / 4:g}</text>' for k in range(5))
    legend = "".join(f'<span><i style="background:{color}"></i>{esc(label)}</span>' for label, color, _ in series)
    return (f'<div class="chart"><h4>{esc(title)} · {esc(unit)}</h4>'
            f'<svg viewBox="-26 -6 {w + 40} {h + 22}" role="img" aria-label="{esc(title)}">'
            f'{grid}{labels}{"".join(body)}{ticks}</svg>'
            f'<div class="legend">{legend}</div></div>')


def network_chart(data: dict) -> str:
    """Блок «график сети»: панели показателей и таблицы, собранные из кода проекта.

    Панели рисуют демонстрационный пример (в макете нет живой машины), а подписи показателей,
    их единицы и правило свёртки берутся из NetworkChart.RequiredIndicators — то есть из кода.
    """
    chart = (data or {}).get("chart") or {}
    indicators = chart.get("indicators") or []
    demo = (chart.get("demo") or {})
    demo_series = {s["indicator"]: s for s in (demo.get("series") or [])}

    def number(value) -> str:
        if value is None:
            return "измерить не удалось"
        if isinstance(value, float) and value.is_integer():
            return f"{int(value)}"
        return f"{value}".replace(".", ",")

    def demo_cell(indicator: str, position: int) -> str:
        values = (demo_series.get(indicator) or {}).get("values") or []
        values = [v for v in values if v is not None]
        if not values:
            return "—"
        if len(values) == 1:
            return number(values[0])
        return number(values[0] if position == 0 else values[-1])

    fact_rows = "".join(
        f'<tr><td><b>{esc(i["titleRu"])}</b></td><td class="small">{esc(i["unitRu"])}</td>'
        f'<td class="num">{esc(demo_cell(i["indicator"], 0))}</td>'
        f'<td class="num">{esc(demo_cell(i["indicator"], 1))}</td>'
        f'<td class="small muted">{esc("последнее подтверждённое значение" if i["aggregation"] == "Last" else "среднее по замерам")}</td></tr>'
        for i in indicators)

    why_rows = "".join(
        f'<tr><td><b>{esc(i["titleRu"])}</b></td><td class="small">{esc(i["unitRu"])}</td>'
        f'<td class="small">{esc(i["whyRu"])}</td></tr>'
        for i in indicators)

    panels = "".join([
        _chart("Скорость приёма", "Мбит/с", 120, [("было", "#74869f", "down_before"),
                                                   ("стало", "#4ade80", "down_after")]),
        _chart("Задержка: средняя", "мс", 60, [("было", "#74869f", "ping_before"),
                                                                ("стало", "#38bdf8", "ping_after")]),
        _chart("Джиттер (MAD) — разброс задержки", "мс", 12, [("было", "#74869f", "jitter_before"),
                                                        ("стало", "#a78bfa", "jitter_after")]),
        _chart("Потери пакетов", "%", 4, [("было", "#f87171", "loss_before"),
                                          ("стало", "#4ade80", "loss_after")], bars=True),
    ])
    notes = demo.get("notesRu") or []
    notes_html = "".join(f"<li>{esc(n)}</li>" for n in notes)

    return f"""
  <h2 id="net-chart">График сети: что происходит со соединением</h2>
  <p class="small muted">{esc(chart.get("describeRu", ""))}</p>
  <p class="small muted">Показатели ниже — обязательный минимум: их список приходит из кода
  (<code>NetworkChart.RequiredIndicators</code>, сейчас {len(indicators)} штук), поэтому если в программе
  появится новый показатель, он появится и в этом макете. Без подписи «окно наблюдения и число проб»
  любые цифры ничего не значат: на двадцати пробах график красивее, но менее правдив.
  Окно наблюдения: <b>{esc(demo.get("windowRu", "—"))}</b>.</p>
  <div class="grid g2" style="margin:12px 0">{panels}</div>

  <div class="scroll">
    <table>
      <thead><tr><th>Показатель</th><th>Единица</th><th class="num">Было</th><th class="num">Стало</th><th>Как считается</th></tr></thead>
      <tbody>{fact_rows}</tbody>
    </table>
  </div>
  <p class="small dim">Числа «было/стало» — демонстрационные (пример 60-секундного окна), но подписи, единицы
  и правило свёртки настоящие: они взяты из кода. На живом запуске сюда подставляются ваши пробы.</p>

  <h3>Зачем каждый показатель в графике</h3>
  <div class="scroll">
    <table>
      <thead><tr><th>Показатель</th><th>Единица</th><th>Почему он в обязательном списке</th></tr></thead>
      <tbody>{why_rows}</tbody>
    </table>
  </div>

  <div class="card tight" style="margin-top:12px">
    <h4>Правила честности этого графика</h4>
    <p class="small muted">{esc(chart.get("honestyRu", ""))}</p>
    <ul class="tight small muted" style="margin:8px 0 0">{notes_html}</ul>
  </div>
'''
"""


def routes_block(data: dict) -> str:
    """Справочник маршрутов: игры, бесплатные публичные DNS и способы сократить путь.

    Все данные приходят из кода (GameRouteCatalog): если игра не описана там, её здесь не будет.
    """
    r = data.get("routes") or {}
    games = r.get("games") or []
    dns = r.get("dns") or []
    paths = r.get("pathKinds") or []

    free_tag = " <span class='tag ok'>бесплатная</span>"
    game_rows = "".join(
        f'<tr><td><b>{esc(g["gameRu"])}</b>{free_tag if g.get("isFreeToPlay") else ""}</td>'
        f'<td class="small"><code class="overflow-safe">{esc(g["processName"])}</code></td>'
        f'<td class="small">{esc(", ".join(g.get("regionsRu") or []) or "регионы не публикуются")}</td>'
        f'<td class="small">{esc("; ".join(g.get("portsRu") or []) or "официальных портов нет — определяется замером")}</td>'
        f'<td class="small muted">{esc(g.get("noteRu", ""))}</td></tr>'
        for g in games)

    dns_rows = "".join(
        f'<tr><td><b>{esc(d["providerRu"])}</b></td>'
        f'<td class="small"><code>{esc(d["primary"])}</code> / <code>{esc(d["secondary"])}</code></td>'
        f'<td class="small muted">{esc(d.get("noteRu", ""))}</td>'
        f'<td class="small">0 ₽</td></tr>'
        for d in dns)

    free = [p for p in paths if p.get("isFree") or p.get("costRu", "").startswith("0 ₽")]
    free_count = r.get("freePathCount", len(free))
    free_names = ", ".join(k.lower() for k in (r.get("freePathKindsRu") or [p["kindRu"] for p in free]))
    warn_marks = ("Осторожно", "Не советуем")

    free_label = "<br><span class='tag ok'>бесплатный способ</span>"
    path_rows = "".join(
        f'<tr><td><b>{esc(p["kindRu"])}</b></td><td class="small">{esc(p.get("whatRu", ""))}</td>'
        f'<td class="small">{esc(p.get("costRu", ""))}'
        f'{free_label if p.get("isFree") or str(p.get("costRu", "")).startswith("0 ₽") else ""}</td>'
        f'<td class="small {("warn" if p.get("safetyRu", "").startswith(warn_marks) else "ok")}">'
        f'{esc(p.get("safetyRu", ""))}</td></tr>'
        for p in paths)

    return f"""
  <h2>Справочник маршрутов: игры, адреса и способы сократить путь</h2>
  <div class="simple">
    <b>Простыми словами.</b> Это не «секретные серверы», а список того, что известно о путях к популярным
    играм: какая программа используется, какие регионы и порты заявлены разработчиком, что измеряется у вас.
    {esc(r.get("honestNoteRu", ""))}
  </div>

  <div class="grid g4">
    <div class="metric"><div class="label">Игр в справочнике</div><div class="value">{len(games)}</div>
      <div class="small dim">все популярные, включая бесплатные</div></div>
    <div class="metric"><div class="label">Публичных DNS</div><div class="value">{len(dns)}</div>
      <div class="small dim">бесплатные, с замерами скорости ответа</div></div>
    <div class="metric"><div class="label">Способов сократить путь</div><div class="value">{len(paths)}</div>
      <div class="small dim">из них бесплатных: {free_count}</div></div>
    <div class="metric"><div class="label">Платных и сторонних</div><div class="value">{len(paths) - free_count}</div>
      <div class="small dim">честно помечены: VPN, VPS, «ускорители»</div></div>
  </div>

  <h3 id="routes-games">Игры: что известно точно</h3>
  <div class="scroll">
    <table>
      <thead><tr><th>Игра</th><th>Процесс</th><th>Регионы (заявлены разработчиком)</th><th>Официальные порты</th><th>Честная оговорка</th></tr></thead>
      <tbody>{game_rows}</tbody>
    </table>
  </div>
  <p class="small muted">Порт нужен не для «ускорения», а чтобы программа измеряла задержку именно до игрового
  трафика и не трогала всё остальное. Если разработчик портов не публикует, программа работает от факта:
  смотрит, куда реально идёт соединение, и измеряет этот адрес.</p>

  <div class="card tight" style="margin-top:12px">
    <h3 id="routes-custom-game">Своя игра: подойдёт и файл с рабочего стола</h3>
    <p class="small muted">«Добавить игру» открывает папку рабочего стола и показывает не только .exe: ярлык игры
    и файл ссылки тоже подойдут. Из ярлыка программа читает путь к настоящему .exe и говорит, на что именно
    указывает выбор — «Ярлык ведёт на «MyGame.exe»» — и только потом добавляет игру. Если ярлык ведёт на удалённую
    игру, на лаунчер, или выбран обычный документ, игра не добавляется, а причина написана словами: что именно
    не так и что выбрать вместо этого. После добавления программа называет процесс, который будет искать
    (<code>mygame.exe</code>), и честно говорит, когда служба его увидит: уже запущенную игру — сразу, остальные —
    когда игра запустится. Регионы, порты и маршрут показываются по тому же правилу, что у встроенных игр;
    анти-чит у своей игры помечен «не проверялся», а не «безопасно».</p>

    <div class="callout" style="margin-top:12px">
      <b>Примеры того, что человек увидит</b> — дословно из программы, а не пересказ:
      <ul class="small" style="margin:8px 0 0 18px;padding:0">
        <li><span class="tag ok">добавлено</span> «Ярлык ведёт на «MyGame.exe». Своя игра «Моя игра» добавлена. Процесс
        «mygame.exe» появится в списке, когда игра запустится.»</li>
        <li><span class="tag ok">добавлено</span> «Процесс «mygame.exe» уже запущен — служба видит его в списке процессов.»</li>
        <li><span class="tag warn">отказ</span> «Игра не добавлена: «заметки.txt» (.txt) — не программа и не ярлык.
        Игра запускается .exe; ярлык с рабочего стола подойдёт — программа найдёт .exe по нему.»</li>
        <li><span class="tag warn">отказ</span> «Игра не добавлена: ярлык ведёт на «…\Deleted\GoneGame.exe», но такого
        файла на диске нет: игра перемещена или удалена. Укажите сам .exe игры.»</li>
        <li><span class="tag warn">отказ</span> «Игра не добавлена: «steam.exe» — служебный процесс или лаунчер,
        а не игра: такие процессы программа игрой не называет.»</li>
      </ul>
    </div>

    <h3 style="margin-top:16px">Что будет, если выбрать…</h3>
    <div class="scroll">
      <table id="routes-pick-examples">
        <thead><tr><th>Что выбрали</th><th>Что скажет программа</th><th>Итог</th></tr></thead>
        <tbody>
          <tr><td><code>MyGame.exe</code></td><td class="small">«Своя игра «MyGame» добавлена. Процесс «mygame.exe» появится в списке, когда игра запустится.»</td><td><span class="tag ok">добавлено</span></td></tr>
          <tr><td>ярлык <code>MyGame.lnk</code></td><td class="small">«Ярлык ведёт на «MyGame.exe». Своя игра «MyGame» добавлена…»</td><td><span class="tag ok">добавлено</span></td></tr>
          <tr><td>ярлык на ярлык</td><td class="small">два ярлыка подряд разбираются: «Ярлык ведёт на «MyGame.lnk». Ярлык ведёт на «MyGame.exe».»</td><td><span class="tag ok">добавлено</span></td></tr>
          <tr><td>ярлык на <code>steam.exe</code></td><td class="small">«steam.exe» — служебный процесс или лаунчер, а не игра: такие процессы программа игрой не называет.»</td><td><span class="tag warn">отказ</span></td></tr>
          <tr><td>ярлык на <code>start.bat</code></td><td class="small">«Ярлык ведёт на «start.bat» — это скрипт, а не игра: скрипт запускает что-то другое, и процесс игры называется иначе. Укажите сам .exe игры.»</td><td><span class="tag warn">отказ</span></td></tr>
          <tr><td>ярлык на удалённую игру</td><td class="small">«…но такого файла на диске нет: игра перемещена или удалена. Укажите сам .exe игры.»</td><td><span class="tag warn">отказ</span></td></tr>
          <tr><td>круг из ярлыков</td><td class="small">«Цепочка ярлыков начинается с «Второй.lnk» и не доходит до файла игры. Укажите сам .exe игры…»</td><td><span class="tag warn">отказ</span></td></tr>
          <tr><td><code>MyGame-Setup.msi</code></td><td class="small">«…— установщик, а не игра. Сначала установите игру, потом укажите её .exe.»</td><td><span class="tag warn">отказ</span></td></tr>
          <tr><td><code>MyGame-portable.zip</code></td><td class="small">«…— архив с игрой, а не игра. Распакуйте его и укажите .exe внутри.»</td><td><span class="tag warn">отказ</span></td></tr>
          <tr><td><code>run.cmd</code></td><td class="small">«…— это скрипт, а не игра: скрипт запускает что-то другое, и процесс игры называется иначе.»</td><td><span class="tag warn">отказ</span></td></tr>
          <tr><td>файл ссылки на <code>.exe</code></td><td class="small">«Ссылка ведёт на «MyGame.exe». Своя игра добавлена…»</td><td><span class="tag ok">добавлено</span></td></tr>
          <tr><td>файл ссылки на сайт</td><td class="small">«Ссылка ведёт не на файл игры, а на «https://example.com/play». Укажите сам .exe игры или её ярлык…»</td><td><span class="tag warn">отказ</span></td></tr>
          <tr><td><code>заметки.txt</code></td><td class="small">«…— не программа и не ярлык. Игра запускается .exe; ярлык с рабочего стола подойдёт — программа найдёт .exe по нему.»</td><td><span class="tag warn">отказ</span></td></tr>
        </tbody>
      </table>
    </div>
  </div>

  <h3 id="routes-dns">Бесплатные публичные DNS</h3>
  <div class="scroll">
    <table>
      <thead><tr><th>Резолвер</th><th>Адреса</th><th>Комментарий</th><th>Стоимость</th></tr></thead>
      <tbody>{dns_rows}</tbody>
    </table>
  </div>
  <p class="small muted">Публичный DNS не сокращает пинг в бою — он ускоряет только поиск имён: вход в лобби,
  обновление списка серверов, магазин. Программа сначала измеряет время ответа вашего текущего резолвера и
  кандидатов, и только потом предлагает замену. Насильно ничего не подставляется.</p>

  <h3 id="routes-paths">Способы сократить путь — с ценой и безопасностью</h3>
  <div class="scroll">
    <table>
      <thead><tr><th>Способ</th><th>Что это даёт</th><th>Цена</th><th>Безопасность</th></tr></thead>
      <tbody>{path_rows}</tbody>
    </table>
  </div>
  <p class="note-inline">Итог справочника честный: бесплатных и безопасных способов — {free_count}. Это те,
  за которые не нужно платить ни подписки, ни аренды: {free_names}. Аренда своего сервера, публичные VPN
  и «бесплатные ускорители» из интернета в рекомендации не попадают: первые требуют денег и настройки,
  вторые чаще ухудшают маршрут, третьи перехватывают трафик. Второй канал безопасен и часто помогает,
  но это отдельный тариф провайдеру — в списке бесплатных он не стоит и не притворяется бесплатным.</p>
"""


def insert_routes(html: str) -> str:
    """Подставляет справочник маршрутов вместо метки в разделе «Сеть и маршрут»."""
    assert "{routes_block}" in html, "не нашёл метку справочника маршрутов"
    return html.replace("{routes_block}", routes_block(_ROUTES_DATA), 1)



def consumption_block(data: dict) -> str:
    """Экономный режим, профили потребления и работа в трее — с честными цифрами из кода."""
    perf = data.get("perf") or {}
    profiles = perf.get("profiles") or []
    tray = data.get("tray") or {}

    prof_rows = "".join(
        f'<tr><td><b>{esc(p["titleRu"])}</b><div class="small dim">{esc(p["profile"])}</div></td>'
        f'<td class="num">{p["monitorIntervalSeconds"]} с</td>'
        f'<td class="num">{p["measurementsPerMinute"]}</td>'
        f'<td class="small">{"да" if p.get("pauseOnBattery") else "нет"}</td>'
        f'<td class="small">{"да" if p.get("pauseWhenMinimized") else "нет"}</td>'
        f'<td class="num">{p.get("trayIconRefreshSeconds", 0)} с</td></tr>'
        for p in profiles)

    def menu(title: str, items: list, note: str) -> str:
        rows = "".join(
            f'<li><b>{esc(i["titleRu"])}</b><br><span class="small muted">{esc(i.get("tooltipRu", ""))}</span></li>'
            for i in items)
        return (f'<div class="card tight"><h4>{esc(title)}</h4><ul class="tight small">{rows}</ul>'
                f'<p class="small muted" style="margin-top:8px">{esc(note)}</p></div>')

    tray_cards = "".join([
        menu("Трей: оптимизация работает", tray.get("runningMenu") or [],
             "Пункты меняются по состоянию: когда оптимизация идёт, первым стоит «остановить»."),
        menu("Трей: оптимизация не запущена", tray.get("idleMenu") or [],
             "В покое программа ничего не делает — меню это показывает прямо."),
        menu("Трей: запуск без прав администратора", tray.get("noAdminMenu") or [],
             "Честное ограничение: без прав можно только измерять."),
    ])

    return f"""
  <h2 id="consumption">Чтобы программа не грузила компьютер</h2>
  <div class="simple">
    <b>Простыми словами.</b> Приложение не должно быть тяжёлым: оно либо измеряет, либо спит.
    Замер — это один-два пакета раз в несколько секунд, а не постоянная нагрузка. Когда игра свёрнута или
    вы на батарее, замеры останавливаются; когда игра закрыта, оптимизация снимается сама через пять минут.
  </div>

  <div class="scroll">
    <table>
      <thead><tr><th>Профиль потребления</th><th class="num">Как часто проверяем</th><th class="num">Проб в минуту</th>
      <th>Пауза на батарее</th><th>Пауза, когда игра свёрнута</th><th class="num">Обновление значка</th></tr></thead>
      <tbody>{prof_rows}</tbody>
    </table>
  </div>
  <div class="grid g3" style="margin-top:12px">{"".join(
      f'<div class="card tight"><p class="small muted">{esc(p.get("describeRu", ""))}</p></div>' for p in profiles)}</div>
  <p class="note-inline">Что реально ест ресурсы в такой программе: частые замеры ICMP/TCP (самое дешёвое),
  чтение таблицы сетевых соединений на каждом такте (заметно дешевле замеров), значок в трее (обновляется
  раз в две секунды и только текстом) и запись журнала (только при изменениях). Никаких постоянных перерисовок,
  фоновых обновлений из интернета и опросов «на всякий случай» нет — в экономном профиле такт растянут втрое.</p>

  <h2 id="tray">Работа в трее</h2>
  <p class="small muted">Закрытие окна на крестик не выключает оптимизацию: программа остаётся в трее и
  продолжает работу. Полный выход — только через пункт меню, и он честно снимает изменения, которые поставил.</p>
  <div class="grid g3">{tray_cards}</div>
  <div class="grid g2" style="margin-top:12px">
    <div class="card tight"><h4>Подсказка, когда всё работает</h4><p class="small">{esc(tray.get("tooltipRunningRu", ""))}</p></div>
    <div class="card tight"><h4>Подсказка, когда замеров ещё не было</h4><p class="small">{esc(tray.get("tooltipNoDataRu", ""))}</p></div>
  </div>
  <p class="note-inline">{esc(tray.get("honestNoteRu", ""))}</p>
"""


def insert_consumption(html: str) -> str:
    """Подставляет блок экономного режима и трея в раздел «Настройки»."""
    assert "{consumption_block}" in html, "не нашёл метку блока потребления"
    return html.replace("{consumption_block}", consumption_block(_PERF_DATA), 1)


def insert_network_chart(html: str, data: dict) -> str:
    """Вставляет график сети в раздел диагностики, сразу после примеров отчёта."""
    anchor = '  <div class="grid g2">\n    <div class="card tight">\n      <h3>Определение MTU пробами с флагом DF</h3>'
    assert anchor in html, "не нашёл место для графика сети"
    return html.replace(anchor, network_chart(data) + anchor, 1)


def mini_toc(title: str, items: list) -> str:
    """Мини-оглавление внутри раздела: ссылки на его подзаголовки. Работает без скриптов."""
    links = "".join(f'<a href="#{a}">{esc(t)}</a>' for a, t in items)
    return (f'<details class="toc-mini"><summary>{esc(title)}</summary>'
            f'<div class="toc-mini-links">{links}</div></details>')


def badge_class(badge: str) -> str:
    return {"Recommended": "b-recommended", "Optional": "b-optional",
            "Caution": "b-caution", "NotRecommended": "b-notrecommended"}.get(badge, "b-optional")


def risk_class(risk: str) -> str:
    return {"Low": "ok", "Medium": "warn", "High": "bad"}.get(risk, "ok")


def bar(done: int) -> str:
    cls = "done" if done >= 99 else ("none" if done == 0 else "")
    return f'<div class="bar"><i class="{cls}" style="width:{max(2, done)}%"></i></div>'


SECTIONS_TOTAL = 6

# Оформление вердиктов — единственное, что макет решает сам: слова приходят из каталога проекта.
VERDICT_CLASS = {"Recommended": "v-yes", "Optional": "v-maybe",
                 "Caution": "v-care", "NotRecommended": "v-no"}

GROUP_NAMES = {
    1: "Оптимизация",
    2: "Твики",
    3: "Сеть и маршрут",
    4: "Журнал и откат",
    5: "Настройки",
    6: "Границы и готовность",
}


def section_head(num: int, title: str, part: str | None = None) -> str:
    """Шапка раздела: «Раздел N из 6: название», при необходимости с указанием части."""
    tail = f" · {esc(part)}" if part else ""
    return (f'<div class="eyebrow">Раздел {num} из {SECTIONS_TOTAL}: {esc(GROUP_NAMES[num])}{tail}</div>\n'
            f"  <h1>{esc(title)}</h1>")


def part_head(num: int, title: str, part: str) -> str:
    """Вторая часть того же пункта меню — идёт как подзаголовок, а не как отдельный раздел."""
    return (f'<div class="eyebrow">Раздел {num} из {SECTIONS_TOTAL}: {esc(GROUP_NAMES[num])} · {esc(part)}</div>\n'
            f'  <h2 class="parthead">{esc(title)}</h2>')


def section_tools(actions: list[str]) -> str:
    """Три конкретных действия этого экрана — чтобы по заголовку не приходилось догадываться."""
    acts = "".join(f"<li>{esc(a)}</li>" for a in actions)
    return ('<div class="section-tools"><span class="small dim">Что здесь можно сделать:</span>'
            f'<ul class="dotlist small">{acts}</ul></div>')


def up_link() -> str:
    return '<a class="up" href="#top">\u2191 к списку разделов</a>'


def category_group_heading(ru: str, count: int, note: str, anchor: str = "", on: int = 0) -> str:
    """Заголовок группы карточек: сколько твиков, сколько из них включено и зачем группа нужна.

    Число включённых считается по бейджам каталога ровно так же, как в программе: по умолчанию
    включено рекомендованное. Поэтому подпись не может разойтись с карточками.
    """
    id_attr = f' id="{esc(anchor)}"' if anchor else ""
    return (f'<div class="grouphd"{id_attr}><h2>{esc(ru)}</h2>'
            f'<span class="cnt">твиков: {count} · включено {on}</span></div>\n'
            f'<p class="groupnote">{esc(note)}</p>')


def build(data: dict, progress: dict) -> str:
    tweaks = data["tweaks"]
    by_cat = Counter(t["categoryRu"] for t in tweaks)
    low_ping = next(p for p in data["presets"] if p["preset"] == "LowLatencyGaming")
    max_speed = next(p for p in data["presets"] if p["preset"] == "MaxThroughput")
    default_preset = next(p for p in data["presets"] if p["preset"] == "RestoreSnapshotDefault")
    fps_preset = next(p for p in data["presets"] if p["preset"] == "FpsAndResponsiveness")
    titles = {t["code"]: t["titleRu"] for t in tweaks}

    def preset_chips(codes: list) -> str:
        """Состав пресета: сначала первые восемь твиков, дальше честное «и ещё N»."""
        head = codes[:8]
        chips = "".join(f'<span>{esc(titles.get(c, c))}</span>' for c in head)
        rest = len(codes) - len(head)
        return chips + (f'<span>и ещё {rest}</span>' if rest > 0 else "")

    low_ping_chips = preset_chips(low_ping["tweakCodes"])
    max_speed_chips = preset_chips(max_speed["tweakCodes"])
    fps_chips = preset_chips(fps_preset["tweakCodes"])
    caution = [t for t in tweaks if t["badge"] == "Caution"]
    unconfirmed = [t for t in tweaks if t["effectUnconfirmed"]]
    computed = [t for t in tweaks if t["isComputed"]]

    categories = sorted({(t["category"], t["categoryRu"]) for t in tweaks})
    cat_options = "".join(
        f'<option value="{esc(code)}">{esc(ru)}</option>' for code, ru in categories)

    cfg_example = esc(json.dumps({
        "schema": 1,
        "app": "Custom Tweak by Doxaiz",
        "preset": "LowLatencyGaming",
        "tweaks": {
            "TCP_ACK_FREQUENCY": "1",
            "TCP_NO_DELAY": "1",
            "MOUSE_ACCEL_SPEED": "0",
            "MOUSE_ACCEL_THRESHOLD1": "0",
            "MOUSE_ACCEL_THRESHOLD2": "0",
            "MOUSE_SENSITIVITY_NEUTRAL": "10",
            "KEYBOARD_FILTERKEYS_OFF": "126",
            "GAME_DVR_OFF": "0",
            "GAME_DVR_CAPTURE_OFF": "0",
        },
        "servers": ["<ваш игровой сервер>"],
        "note": "значения из макета; программа сверит их с каталогом и с этой машиной",
    }, ensure_ascii=False, indent=2))

    rt = data["realtime"]
    rt_steps = "".join(
        f'<li>{esc(step)}</li>' for step in rt["stepsRu"])
    rt_rules = "".join(
        f'<li>{esc(rule)}</li>' for rule in rt["rulesRu"])
    rt_bands = "".join(
        f'<span class="tag">{esc(b["textRu"])}</span>' for b in rt["qualityBands"])
    rt_actions = "".join(
        f'<div class="card tight"><h3>{esc(a["titleRu"])}</h3>'
        f'<p class="small muted">{esc(a["detailRu"])}</p></div>'
        for a in rt["plannerActions"])
    rt_kernel_note = esc(rt["plannerKernelAntiCheatNotes"][0]) if rt["plannerKernelAntiCheatNotes"] else ""
    rt_noadmin = esc(rt["plannerNoAdminBlockRu"] or "")
    rt_interval = rt["monitorIntervalSeconds"]
    rt_window = rt["samplesPerWindow"]
    rt_baseline_windows = rt["baselineWindows"]

    preset_cards = "".join(f"""
        <div class="card tight">
          <div class="between" style="margin-bottom:6px">
            <b>{esc(p["titleRu"])}</b><span class="score">{len(p["tweakCodes"])}</span>
          </div>
          <p class="small muted">Твиков в пресете: {len(p["tweakCodes"])} из {data["tweakCount"]}.
          {"Пресет отката: значения берутся из снапшота, а не из «заводских». " if p["preset"] == "RestoreSnapshotDefault" else ""}
          {"Проходит проверку: в пресет не попадают твики с бейджем «с осторожностью»." if p["preset"] in ("LowLatencyGaming", "MaxThroughput") else ""}</p>
        </div>""" for p in sorted(data["presets"], key=lambda x: x["preset"]))

    comp_tag = "<span class='tag'>вычисляется замерами</span>"
    static_note = "статичное значение из каталога"
    computed_note = "значение считается пробами до вашего игрового сервера"
    apply_rows = "".join(f"""
              <tr>
                <td><b>{esc(t["titleRu"])}</b><div class="dim small">{esc(t["mechanismRu"])}</div></td>
                <td class="small dim">{esc(t["resourceLocator"])}</td>
                <td>{comp_tag if t["isComputed"] else "<code>" + esc(t["recommendedRawValue"]) + "</code>"}</td>
                <td class="small muted">{computed_note if t["isComputed"] else static_note}</td>
              </tr>"""
        for t in [x for x in tweaks if x["code"] in low_ping["tweakCodes"]])

    journal_rows = """
              <tr><td class="num">14:02:11</td><td>Частота подтверждений TCP</td><td class="num">2</td><td class="num">1</td><td><span class="tag ok">применено и проверено</span></td></tr>
              <tr><td class="num">14:02:11</td><td>Отключение алгоритма Нейгла</td><td class="num">1</td><td class="num">1</td><td><span class="tag">уже стояло — не трогали</span></td></tr>
              <tr><td class="num">14:02:12</td><td>Energy-Efficient Ethernet</td><td>Enabled</td><td>Disabled</td><td><span class="tag ok">применено и проверено</span></td></tr>
              <tr><td class="num">14:02:13</td><td>Оптимальный MTU</td><td class="num">1500</td><td class="num">1492</td><td><span class="tag ok">применено (значение найдено пробами)</span></td></tr>
              <tr><td class="num">14:02:14</td><td>Начальный RTT (InitialRtt)</td><td class="num">3000</td><td class="num">300</td><td><span class="tag warn">требует ручной проверки</span></td></tr>
              <tr><td class="num">14:31:02</td><td>Откат снапшота «низкий пинг»</td><td class="num">1</td><td class="num">2</td><td><span class="tag ok">возвращено как было</span></td></tr>"""

    dns_rows = "".join(
        f'<tr><td>{esc(c["name"])}</td><td><code>{esc(c["primary"])}</code></td><td><code>{esc(c["secondary"])}</code></td></tr>'
        for c in data["probing"]["dnsCandidates"])

    progress_rows = "".join(f"""
              <tr>
                <td>{esc(r["name"])}</td>
                <td class="num">{r["weight"]}%</td>
                <td class="num">{r["done"]}%</td>
                <td>{bar(r["done"])}</td>
                <td class="small muted">{esc(r["note"])}</td>
              </tr>""" for r in progress["rows"])

    rules = "".join(f"<li>{esc(r)}</li>" for r in data["routing"]["rulesRu"])
    notes = "".join(f"<li>{esc(n)}</li>" for n in data["probing"]["notesRu"])
    focus_order = [
        ("Ping", "Пинг и стабильность сети",
         "Сокращают задержку и джиттер: подтверждения TCP, таймеры, параметры драйвера карты, DNS."),
        ("Speed", "Скорость",
         "Влияют на пропускную способность: буферы карты, окно приёма, масштабирование приёма."),
        ("Fps", "ФПС и ровность кадра",
         "Питание, фон Windows, планировщик: чтобы система не отбирала ресурсы в бою."),
        ("Input", "Задержка ввода",
         "Между рукой и картинкой: акселерация указателя, «фильтр ввода» клавиатуры, отзывчивость интерфейса."),
        ("System", "Система и служебные правки",
         "Помогают косвенно: шум NetBIOS, кэш DNS, разрешение имён."),
    ]

    cards_by_focus: dict[str, list[dict]] = {}
    for t in tweaks:
        cards_by_focus.setdefault(t.get("focus", "System"), []).append(t)

    grouped: list[str] = []
    for code, ru, note in focus_order:
        items = cards_by_focus.get(code)
        if not items:
            continue
        grouped.append(category_group_heading(
            ru, len(items), note, anchor=f"focus-{code}",
            on=sum(1 for t in items if t.get("badge") == "Recommended")))
        grouped.extend(tweak_card(t, i) for i, t in enumerate(items))
    for code, items in cards_by_focus.items():
        if code in {c for c, _, _ in focus_order}:
            continue
        grouped.append(category_group_heading(
            items[0].get("focusRu", "Прочее"), len(items), "Подкатегория из каталога.",
            on=sum(1 for t in items if t.get("badge") == "Recommended")))
        grouped.extend(tweak_card(t, i) for i, t in enumerate(items))

    cards_grouped = "\n".join(grouped)

    focus_toc = [("focus-Ping", "Пинг и стабильность сети"), ("focus-Speed", "Скорость"),
                 ("focus-Fps", "ФПС и ровность кадра"), ("focus-Input", "Задержка ввода"),
                 ("focus-System", "Система и служебные правки")]
    verdict_data = data.get("verdicts") or {}
    verdict_legend = "".join(
        f'<div><span class="verdict {VERDICT_CLASS[v["badge"]]}">{esc(v["shortRu"])}</span> '
        f'{esc(v["whyRu"].split(":", 1)[-1].strip() if ":" in v["whyRu"] else v["whyRu"])}</div>'
        for v in (verdict_data.get("legend") or []))
    verdict_honesty = verdict_data.get("honestyRu") or "Вердикт берётся из каталога проекта."

    mini_toc_for_tweaks = mini_toc(f"Что внутри раздела твиков: {len(tweaks)} карточек по подкатегориям",
                                   focus_toc + [("tweak-presets", "Составы пресетов (19 / 5 / 23)")])
    mini_toc_for_routing = mini_toc("Что внутри раздела «Сеть и маршрут»", [
        ("routes-games", "22 игры: процессы, регионы, порты"),
        ("routes-dns", "5 бесплатных публичных DNS"),
        ("routes-paths", "8 способов сократить путь"),
        ("route-howto", "Как выбирается путь и настройки замеров")])
    mini_toc_for_settings = mini_toc("Что внутри раздела «Настройки»", [
        ("consumption", "Чтобы программа не грузила компьютер"),
        ("tray", "Работа в трее"),
        ("settings-misc", "Прочее: журнал, приватность, импорт")])
    focus_counts = [(ru, len(cards_by_focus.get(code, []))) for code, ru, _ in focus_order]
    focus_links = "".join(
        f'<a href="#focus-{code}"><b>{esc(ru)}</b> {len(cards_by_focus.get(code, []))} шт.</a>'
        for code, ru, _ in focus_order if cards_by_focus.get(code))
    recommend_count = sum(1 for t in tweaks if t["badge"] == "Recommended")
    optional_count = sum(1 for t in tweaks if t["badge"] == "Optional")
    not_recommended = sum(1 for t in tweaks if t["badge"] == "NotRecommended")
    caution_count = sum(1 for t in tweaks if t["badge"] == "Caution")

    w = data["routing"]["weights"]
    th = data["routing"]["thresholds"]
    f = data["routing"]["failover"]

    return f"""<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Custom Tweak By Doxaiz — макет интерфейса (данные из кода проекта)</title>
<style>{CSS}</style>
</head>
<body>

<header class="appbar" id="top">
  <div class="appbar-inner">
    <div class="logo">
      <span class="mark">
        <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="#04121d" stroke-width="2.6" stroke-linecap="round">
          <path d="M3 17l5-7 4 4 4-8 5 6"/>
        </svg>
      </span>
      <span class="logo-text">
        <span class="logo-name">Custom Tweak By Doxaiz</span>
        <span class="appbar-sub">макет интерфейса · движок уже работает</span>
      </span>
    </div>
    <a class="btn small" href="https://dalink.to/doxaiz" target="_blank" rel="noopener">Поддержать автора ↗</a>
    <span class="spacer"></span>
    <span class="chip js-only" id="whereAmI"></span>
    <span class="chip">данных в каталоге: {data["tweakCount"]}</span>
    <span class="chip ok">проверок каталога: {len(data["catalogProblems"]) + len(data["safetyProblems"])} нарушений</span>
  </div>
</header>

<div class="app">

<aside class="sidebar">
  <div class="side-title">Разделы</div>
  <nav class="side-nav" aria-label="Разделы программы">
    <div class="navgroup">Работа</div>
    <a class="step" href="#overview" data-group="overview" aria-current="true"><span class="stepnum">1</span>
      <span class="steptext"><b>Оптимизация</b><span>Одна кнопка: программа сама измерит и попробует сделать лучше</span></span>
      <span class="navbadge">живой режим</span></a>
    <a class="step" href="#apply" data-group="tweaks"><span class="stepnum">2</span>
      <span class="steptext"><b>Мои твики</b><span>Настройки Windows: что делает каждая и стоит ли её включать</span></span>
      <span class="navbadge">{data["tweakCount"]} шт.</span></a>

    <div class="navgroup">Что получилось</div>
    <a class="step" href="#routing" data-group="network"><span class="stepnum">3</span>
      <span class="steptext"><b>Сеть и маршрут</b><span>Как трафик идёт до игры, 22 игры и способы короче</span></span>
      <span class="navbadge">22 игры</span></a>
    <a class="step" href="#diagnostics" data-group="network"><span class="stepnum">3b</span>
      <span class="steptext"><b>Анализ и график сети</b><span>Замеры: скорость, пинг, джиттер, потери — до и после</span></span>
      <span class="navbadge">график</span></a>
    <a class="step" href="#journal" data-group="journal"><span class="stepnum">4</span>
      <span class="steptext"><b>Журнал и откат</b><span>Что изменили и как вернуть всё как было</span></span>
      <span class="navbadge">откат</span></a>

    <div class="navgroup">Настройка и правда</div>
    <a class="step" href="#settings" data-group="settings"><span class="stepnum">5</span>
      <span class="steptext"><b>Настройки</b><span>Профиль потребления, работа в трее, конфиг в файл</span></span>
      <span class="navbadge">экономно</span></a>
    <a class="step" href="#progress" data-group="limits"><span class="stepnum">6</span>
      <span class="steptext"><b>Готовность</b><span>Что уже работает, а что ещё нет — по слоям</span></span>
      <span class="navbadge">честно</span></a>
    <a class="step" href="#about" data-group="limits"><span class="stepnum">7</span>
      <span class="steptext"><b>Чего программа не делает</b><span>Запреты, поддержка автора, приватность</span></span>
      <span class="navbadge">границы</span></a>
  </nav>
  </nav>

  <div class="side-tip">
    <b>С чего начать.</b> Раздел 1 — нажать кнопку и посмотреть замеры. Раздел 2 — если хотите править
    настройки руками. Раздел 4 — если нужно вернуть всё как было.
  </div>

  <div class="side-card whatsnew">
    <b>Что в этой версии</b>
    <ul class="tight small muted">
      <li>твиков стало <b>{data["tweakCount"]}</b> — сеть, скорость, кадры, ввод, система;</li>
      <li>подкатегории «пинг / скорость / ФПС / ввод / система» с плитками-переходами;</li>
      <li>пресеты больше не одинаковые: 19 / 5 / 23 твика по разным признакам;</li>
      <li>работа в трее: меню трёх состояний и честные подсказки;</li>
      <li>экономный профиль: пауза на батарее и когда игра свёрнута;</li>
      <li>справочник маршрутов: 22 игры, 5 бесплатных DNS, 8 способов;</li>
      <li>график сети: скорость, пинг, джиттер, потери, нагрузка, DNS, MTU.</li>
    </ul>
    <a class="btn small" href="https://dalink.to/doxaiz" target="_blank" rel="noopener">Поддержать автора ↗</a>
  </div>

  <div class="side-foot">
    <b>Это макет, а не работающая программа.</b> Страница собрана из реального каталога проекта: названия твиков,
    значения, веса маршрутизации и пресеты берутся из кода. Окно и служба уже написаны и собираются, но на живой
    Windows ещё не запускались — поэтому числа в примерах помечены как демонстрационные.
  </div>
</aside>

<main class="content">

<noscript>
  <div class="callout warn" style="margin-top:16px">
    <b>Скрипты в предпросмотре отключены.</b> Поэтому все разделы показаны подряд одним потоком, а фильтры,
    вкладки и калькулятор маршрутов недоступны. Содержимое то же самое — просто листайте вниз.
  </div>
</noscript>

<section class="tab active" id="overview">
  {section_head(1, "Оптимизация соединения", "живой режим")}
  <p class="muted">При запуске программа <b>ничего не делает</b>: ни фильтра трафика, ни изменений в системе.
  Всё начинается только с этой кнопки.</p>
  {section_tools(["нажать одну кнопку и увидеть результат", "посмотреть замеры до и после", "остановить и вернуть всё как было"])}

  <div class="simple">
    <b>Простыми словами.</b> Пакеты от вашей игры идут до игрового сервера как машина по дорогам: где-то
    светофор, где-то пробка. Программа измеряет этот путь, пробует более короткие дороги и говорит цифрами,
    стало ли быстрее. Если быстрее не стало — она так и скажет, а не напишет «оптимизировано» для галочки.
  </div>

  <div class="three">
    <div class="step"><span class="n">1</span><b>Нажмите кнопку</b>
      Игра уже запущена? Тогда просто нажмите «Оптимизировать соединение» — программа сама найдёт игровой поток.</div>
    <div class="step"><span class="n">2</span><b>Смотрите цифры</b>
      Появятся два числа: пинг «до» и «после», и физический предел пути — ниже него не даст даже идеальный маршрут.</div>
    <div class="step"><span class="n">3</span><b>Не помогло — верните</b>
      Нажмите «Остановить»: маршрут и политика снимаются, сеть возвращается в исходное состояние.</div>
  </div>

  <h2>Словарь (если слышите эти слова впервые)</h2>
  <div class="card tight">
    <dl class="glossary small">
      <dt>Пинг</dt><dd>Время, за которое пакет доходит до сервера и возвращается. Меньше — лучше: 20 мс быстрее, чем 120 мс.</dd>
      <dt>Джиттер</dt><dd>Насколько пинг «дрожит»: 25, 27, 24 мс — это стабильно, 20, 90, 35 — уже дёргано.</dd>
      <dt>Твик</dt><dd>Одна настройка Windows. Программа показывает, что она делает и что будет, если её поменять.</dd>
      <dt>Снапшот</dt><dd>Сохранённая копия ваших настроек «до». Из неё программа возвращает всё как было.</dd>
      <dt>Маршрут</dt><dd>Дорога, по которой пакеты идут до игрового сервера.</dd>
      <dt>Релей</dt><dd>Промежуточный сервер: иногда даёт более короткий путь. Нужен отдельный сервер и оплата.</dd>
    </dl>
  </div>

  <div class="card">
    <div class="row" style="gap:18px">
      <button class="btn primary big" id="btnOptimize">Оптимизировать соединение</button>
      <a class="btn" href="#apply">Применить твики</a>
      <span class="chip" id="rtChip">состояние: выключено</span>
    </div>
    <p class="small muted" id="optimizeHint" style="margin-top:12px">
      Кнопка работает с <b>уже запущенной игрой</b>: находит её живой сетевой поток, меряет исходный пинг,
      применяет маршрут только для адреса игрового сервера и политику QoS, а дальше непрерывно следит за качеством
      каждые {rt_interval:.0f} с. Пока кнопка не нажата, программа не делает ничего.
    </p>

    <div class="grid g4" style="margin-top:14px">
      <div class="metric"><div class="label">Пинг до нажатия</div><div class="value na">—</div><div class="small dim">мерим живой поток игры</div></div>
      <div class="metric"><div class="label">Пинг после</div><div class="value na">—</div><div class="small dim">медиана, а не лучший кадр</div></div>
      <div class="metric"><div class="label">Физический предел</div><div class="value na">—</div><div class="small dim">минимальный RTT пути</div></div>
      <div class="metric"><div class="label">Качество</div><div class="value na">нет данных</div><div class="small dim">шкала для шутеров</div></div>
    </div>

    <div class="callout warn" style="margin-top:14px">
      <b>Пример того, как это выглядит в работе</b> (числа примера, не ваши): игра Fortnite,
      сервер 3.4.5.6:9000, было <b>120 мс</b> → стало <b>84 мс</b> (медиана живого потока),
      физический предел этого пути — <b>78 мс</b>, качество «Терпимо».
      Сколько получится у вас — зависит от вашего маршрута: твики дают единицы миллисекунд,
      основные десятки даёт смена пути. Если выигрыша нет — приложение так и напишет.
    </div>
  </div>

  <div class="grid g4">
    <div class="metric"><div class="label">Пинг</div><div class="value na">нет данных</div><div class="small dim">до нажатия кнопки</div></div>
    <div class="metric"><div class="label">Джиттер</div><div class="value na">нет данных</div><div class="small dim">по MAD, 30 сэмплов</div></div>
    <div class="metric"><div class="label">Потери</div><div class="value na">нет данных</div><div class="small dim">(отправлено − принято) / отправлено</div></div>
    <div class="metric"><div class="label">Стабильность</div><div class="value na">нет данных</div><div class="small dim">шкала 0…100</div></div>
  </div>

  <div class="callout">
    <b>Честность вместо красивых цифр.</b> Если измерить не получилось (ICMP закрыт, нет прав, сервер не отвечает),
    приложение пишет «измерить не удалось» и указывает причину. Нули и «пинг 0 мс» не рисуются никогда.
  </div>

  <h2>Что делает кнопка</h2>
  <div class="grid g3">
    <div class="card tight"><h3>1. Меряет</h3><p class="small muted">Живой поток игры → ICMP → TCP-рукопожатие.
      Замеры идут до вашего игрового сервера, а не до случайного «пинг-теста».</p></div>
    <div class="card tight"><h3>2. Считает</h3><p class="small muted">Скоринг маршрутов с явными весами и порогами:
      выигрыш меньше 1 балла — релей не предлагается.</p></div>
    <div class="card tight"><h3>3. Применяет или отказывается</h3><p class="small muted">Твик, который не поддержан драйвером,
      не применяется и попадает в отчёт как «не поддерживается».</p></div>
  </div>

  <h2>Что делает кнопка по шагам</h2>
  <div class="card"><ul class="clean">{rt_steps}</ul></div>

  <div class="grid g2">
    <div class="card tight">
      <h3>Действия, которые будут применены</h3>
      <div class="grid g2">{rt_actions}</div>
      <p class="small muted" style="margin-top:10px">{rt_kernel_note}</p>
    </div>
    <div class="card tight">
      <h3>Жёсткие правила живого режима</h3>
      <ul class="clean">{rt_rules}</ul>
    </div>
  </div>

  <div class="grid g2">
    <div class="card tight">
      <h3>Полосы качества (для шутеров)</h3>
      <div class="row" style="gap:8px;flex-wrap:wrap">{rt_bands}</div>
      <p class="small muted" style="margin-top:10px">Пороги взяты из кода программы: интерфейс и отчёты
      показывают ровно те оценки, что считает движок.</p>
    </div>
    <div class="card tight">
      <h3>Если прав администратора нет</h3>
      <p class="small muted">{rt_noadmin}</p>
      <p class="small muted" style="margin-top:10px">Это безопасный режим «только посмотреть»: замеры идут,
      сеть не меняется. Кнопка при этом не «серая», а честная — она объясняет причину.</p>
    </div>
  </div>

  <h2>Пресеты</h2>
  <p class="small muted">Состав каждого пресета берётся из того же вызова каталога, которым пользуется служба:
  число в подписи и список твиков не могут разойтись. Кнопка <b>«Показать состав»</b> отбирает карточки
  пресета и ничего не применяет — ползунки остаются там, где стояли.</p>
  <div class="grid g3">{preset_cards}</div>
  {up_link()}
</section>

<section class="tab" id="apply">
  {section_head(2, "Применить твики", "часть 1 — окно применения")}
  <p class="muted">Отдельное окно: сначала анализ текущих настроек и предложение точки восстановления, потом изменения.</p>
  <p class="small muted">Подтверждение называет не только число, но и состав выбора по назначению —
  «Пинг и сеть — 5 · Кадры — 4 · Ввод — 3»: число правок ввода вместо сетевых человек видит до нажатия «Применить».</p>

  <div class="simple">
    <b>Простыми словами.</b> Твик — это одна настройка Windows. Применяются они не молча: сначала программа
    запоминает, как было (снапшот), предлагает точку восстановления, и только после вашего «да» меняет значения —
    с проверкой каждого. Не понравилось — одно нажатие возвращает всё назад.
  </div>
  {section_tools(["посмотреть, что предлагается применить", "снять галочки с ненужного", "создать точку восстановления и применить"])}

  <div class="window">
    <div class="titlebar">
      <span class="dots"><i></i><i></i><i></i></span>
      <b>Применение твиков — низкий пинг</b>
      <span class="spacer"></span>
      <span class="chip">батч из {len(low_ping["tweakCodes"])} твиков</span>
    </div>
    <div class="body">
      <div class="card tight" style="margin-top:0">
        <label class="check">
          <input type="checkbox" checked>
          <span><b>Создать точку восстановления Windows</b> перед изменениями
            <div class="small muted">Статус показывается честно: «создана», «не запрошена», «пропущена из-за суточного
            лимита Windows», «защита системы отключена». Приложение не делает вид, что точка есть, если её нет.</div>
          </span>
        </label>
      </div>

      <div class="row" style="margin:14px 0">
        <span class="small muted">Пресет:</span>
        <span class="btn small" style="background:#1d2a40">Низкий пинг · {len(low_ping["tweakCodes"])}</span>
        <span class="btn small">Максимальная скорость · {len(max_speed["tweakCodes"])}</span>
        <span class="btn small">Вернуть как было · {len(default_preset["tweakCodes"])}</span>
        <span class="btn small">Свой набор</span>
      </div>

      <div class="row" style="margin:10px 0">
        <span class="tag ok">будет применено: {len(low_ping["tweakCodes"])}</span>
        <span class="tag">уже стоит (no-op): определится на вашей машине</span>
        <span class="tag">не поддерживается драйвером: определится на вашей машине</span>
        <span class="tag warn">требует ручной проверки: {len([t for t in unconfirmed if t["inLowPingPreset"]])}</span>
        <span class="tag warn">нужен перезапуск: {len([t for t in tweaks if t["requiresReboot"]])}</span>
      </div>

      <h3>Анализ текущих настроек и план</h3>
      <div class="scroll">
        <table>
          <thead><tr><th>Параметр</th><th>Где меняется</th><th>Будет записано</th><th>Почему так</th></tr></thead>
          <tbody>{apply_rows}</tbody>
        </table>
      </div>

      <div class="row" style="justify-content:flex-end;margin-top:16px">
        <span class="btn">Отмена</span>
        <span class="btn primary">Применить и проверить</span>
      </div>
    </div>
  </div>

  <div class="callout bad">
    <b>Откат делается автоматически.</b> Перед каждой записью в систему в журнал предзаписи ставится метка (PENDING).
    Если приложение упало или компьютер перезагрузился, при следующем запуске эта метка приводит к откату —
    включая маршруты, туннели, фильтры и DNS.
  </div>
  {up_link()}
</section>

<section class="tab" id="tweaks">
  {part_head(2, "Мои твики — ручной выбор", "часть 2 — каталог, значения и свой выбор")}
  <p class="muted">Полный каталог проекта: {data["tweakCount"]} твиков. Ниже — реальные данные из кода
  (заголовки, описания, куда пишем, значение, рекомендация для игр). Клик по карточке раскрывает подробности.</p>
  {section_tools(["найти твик поиском и фильтрами", "прочитать обоснование для игр", "применить только выбранные вручную"])}
  {mini_toc_for_tweaks}

  <div class="grid g4">
    <div class="metric"><div class="label">Всего твиков</div><div class="value">{data["tweakCount"]}</div>
      <div class="small dim">сеть, скорость, кадры, ввод, система</div></div>
    <div class="metric"><div class="label">Рекомендуем включать</div><div class="value">{recommend_count}</div>
      <div class="small dim">безопасно и полезно для игр</div></div>
    <div class="metric"><div class="label">По желанию</div><div class="value">{optional_count}</div>
      <div class="small dim">зависит от железа и роутера</div></div>
    <div class="metric"><div class="label">Осторожно</div><div class="value">{caution_count}</div>
      <div class="small dim">есть побочный эффект, читайте причину</div></div>
    <div class="metric"><div class="label">Не советуем</div><div class="value">{not_recommended}</div>
      <div class="small dim">популярные советы, которые вредят</div></div>
  </div>

  <div class="focusbar">{focus_links}</div>
  <p class="small dim">Подкатегории — это ответ на вопрос «что станет лучше»: пинг, скорость, ФПС,
  ввод или общее состояние системы. Категория из каталога (стек TCP/IP, драйвер карты, планировщик)
  показана на карточке отдельной меткой.</p>

  <div class="card tight">
    <h4>Порядок и поиск</h4>
    <p class="small muted" style="margin:6px 0 0">Порядок меняет только последовательность карточек,
    поиск — видимую часть списка. Отметки и пресеты при этом не трогаются: поиск не применяет и не
    сбрасывает ничего. В заголовке группы стоит «твиков: N · включено M» — M считается по ползункам.</p>
    <div class="chiprow" style="margin-top:10px">
      <span class="on">По назначению — выбрано</span>
      <span>Сначала полезное</span>
      <span>По алфавиту</span>
    </div>
    <p class="small dim" style="margin:12px 0 0">Фильтры — это не порядок и не поиск: они убирают из списка
    то, что не подходит. Включать можно несколько сразу, и отметки с пресетами они не меняют.</p>
    <div class="chiprow" style="margin-top:8px">
      <span>Только рекомендуемое</span>
      <span>Скрыть «не советуем»</span>
      <span>Только из пресетов</span>
    </div>
    <p class="small dim" style="margin:8px 0 0">Поиск идёт по названию, описанию, коду и назначению —
    например, «DNS» или «TCP_ACK». Ничего не нашлось — так и написано, а не «показан весь каталог».</p>
  </div>

  <div class="card tight">
    <h4>Остальные метки на карточке</h4>
    <div class="legendbox">
      <div><span class="tag ok">риск: Low</span> откатывается в один шаг</div>
      <div><span class="tag warn">риск: Medium</span> перед применением прочитайте причину</div>
      <div><span class="tag">Стек TCP/IP</span> категория каталога и механизм правки</div>
      <div><span class="tag">значение вычисляется замерами</span> число подберут пробы, а не мы</div>
      <div><span class="tag warn">нужен перезапуск</span> без перезагрузки не заработает</div>
      <div><span class="tag warn">эффект не подтверждён</span> параметр может не читаться современной Windows</div>
    </div>
    <p class="small muted" style="margin:8px 0 0">Названия плашек каталога («Рекомендую для игр», «По желанию»,
    «С осторожностью», «Не рекомендую») в карточку не дублируются: вместо них стоит короткий вердикт — он берётся
    из того же места в коде, что и плашка, поэтому разойтись они не могут.</p>
  </div>

  <div class="hero">
    <h2>Раздел «Задержка ввода»: что правим и чего не обещаем</h2>
    <p class="small muted">Твики этого раздела предлагаются только тем, у кого устройство действительно подключено:
    список мышей и клавиатур читается из системы. Если прочитать не удалось — проверка честно уходит в «пропущено»,
    а не предлагается вслепую.</p>
    <div class="grid g2">
      <div class="card tight"><h4>Что убираем на вашей стороне</h4><ul class="tight small muted">
        <li>Акселерацию указателя — четыре параметра, включая нейтральную скорость 6/11</li>
        <li>«Фильтр ввода» клавиатуры (Flags = 126): короткие нажатия перестают теряться</li>
        <li>Фоновую запись Game DVR — буфер последних секунд больше не отбирает кадры</li>
        <li>Очередь пакетов в драйвере карты (802.1p/DSCP), если роутер уважает приоритет</li>
      </ul></div>
      <div class="card tight"><h4>Чего эти правки не делают</h4><ul class="tight small muted">
        <li>Не настраивают роутер и модем: в устройство программа не заходит — список ручных проверок
        приходит вместе с предупреждением <code>INPUT_PATH_THROUGH_ROUTER</code></li>
        <li>Не сокращают задержку радиоканала беспроводной мыши (обычно 125 Гц ≈ 8 мс)</li>
        <li>Не ускоряют интернет и не снижают пинг внутри игрового сервера</li>
      </ul></div>
    </div>
    <p class="small dim">Автоповтор клавиш (KeyboardDelay) в предложения не попадает: на игровой отклик он не влияет —
    и это сказано прямо, а не умолчано.</p>
  </div>

  <div class="toolbar js-only flex">
    <input type="text" id="twSearch" placeholder="Поиск: «MTU», «DNS», «буферы»…">
    <select id="twCat"><option value="">Все категории</option>{cat_options}</select>
    <select id="twBadge">
      <option value="">Все рекомендации</option>
      <option value="Recommended">Рекомендую для игр</option>
      <option value="Optional">По желанию</option>
      <option value="Caution">С осторожностью</option>
    </select>
    <label class="check small"><input type="checkbox" id="twLowPing"> только из пресета «низкий пинг»</label>
    <span class="btn small" id="twExpand">Раскрыть всё</span>
    <span class="small muted" id="twCount"></span>
  </div>

  <h2>Категории каталога</h2>
  <div class="card tight">
    <div class="scroll">
      <table>
        <thead><tr><th>Категория</th><th class="num">Твиков</th></tr></thead>
        <tbody>{"".join(f'<tr><td>{esc(k)}</td><td class="num">{v}</td></tr>' for k, v in sorted(by_cat.items()))}</tbody>
      </table>
    </div>
  </div>

  <h2>Каталог: {data["tweakCount"]} твиков по категориям</h2>
  <p class="small muted">Карточка раскрывается щелчком: что делает, стоит ли применять для игр, что именно будет записано
  и куда (ветка реестра, свойство драйвера или команда netsh). Заголовки групп объясняют, зачем нужна категория.</p>
  <div class="card tight">
    <h4>Как читать карточку: вердикты «стоит включать или нет»</h4>
    <div class="legendbox">
      {verdict_legend}
      <div><span class="tag">категория</span> где живёт параметр</div>
      <div><span class="tag ok">пресеты: …</span> в какие пресеты входит</div>
    </div>
    <p class="small muted" style="margin:8px 0 0">{verdict_honesty}</p>
    <p class="small muted" style="margin:10px 0 0">Ползунок слева включает твик в план применения; подробности —
    в раскрывающемся блоке под ним. Текст в карточке никогда не выходит за рамки: длинные пути параметров
    переносятся, таблица значений прокручивается по горизонтали. Это макет: ползунки ничего не записывают в систему.</p>
  </div>

  <div class="bulkbar js-only">
    <span class="small dim">Массовый выбор:</span>
    <button class="btn small" id="pickRecommended" type="button">только рекомендуемое</button>
    <button class="btn small ghost" id="pickAll" type="button">включить все</button>
    <button class="btn small ghost" id="pickNone" type="button">отключить все</button>
    <span class="chip" id="pickCount">выбрано: 0 из 0</span>
    <span class="small muted">Кнопки только расставляют ползунки в макете. В программе такой выбор превращается
    в план применения, а он всегда идёт через снапшот: настроек «включить всё и сразу в систему» нет.</span>
  </div>

  <div class="presetbox" id="tweak-presets">
    <div class="presetcard">
      <h4>Пресет «Низкий пинг» <span class="cnt">{len(low_ping["tweakCodes"])}</span></h4>
      <p class="small muted">Только то, что сокращает задержку и джиттер: подтверждения TCP, таймеры, драйвер
      карты, DNS. Твики с бейджем «осторожно» и «не советуем» в пресет не попадают никогда — это проверяется тестом.</p>
      <div class="chiprow">{low_ping_chips}</div>
    </div>
    <div class="presetcard">
      <h4>Пресет «Максимальная скорость» <span class="cnt">{len(max_speed["tweakCodes"])}</span></h4>
      <p class="small muted">Пропускная способность: буферы приёма и передачи, окно TCP, масштабирование приёма.
      Составы пресетов разные — 19, 5 и 23 твика, а не «одинаковые наборы».</p>
      <div class="chiprow">{max_speed_chips}</div>
    </div>
    <div class="presetcard">
      <h4>Пресет «Отзывчивость и кадры» <span class="cnt">{len(fps_preset["tweakCodes"])}</span></h4>
      <p class="small muted">Питание, фон Windows, планировщик и задержка ввода: чтобы система не отбирала ресурсы
      в бою. Берётся по правилу подкатегории, а не по ручным пометкам.</p>
      <div class="chiprow">{fps_chips}</div>
    </div>
    <div class="presetcard">
      <h4>Пресет «Вернуть как было» <span class="cnt">{len(default_preset["tweakCodes"])}</span></h4>
      <p class="small muted">Откат идёт не по каталогу, а по снапшоту: возвращаются те значения, которые были
      у вас до применения, включая параметры, которых в каталоге нет.</p>
      <div class="chiprow"><span>значения из снапшота</span><span>проверяется после откатa</span><span>без перезагрузки</span></div>
    </div>
  </div>

  {cards_grouped}
  {up_link()}
</section>

<section class="tab" id="routing">
  {section_head(3, "Как выбирается путь до игры", "часть 1 — маршрут")}
  <p class="muted">Приложение не «ускоряет интернет» магией: оно выбирает путь и объясняет выбор числами.</p>

  <div class="simple">
    <b>Простыми словами.</b> Путь до игрового сервера — как дорога с перекрёстками. Программа измеряет несколько
    дорог и говорит, какая короче; переключается только если разница подтвердилась замерами, и всегда возвращается назад,
    если стало хуже. Интернет она не «ускоряет» — она выбирает дорогу.
  </div>
  {section_tools(["выбрать путь автоматически", "задать время замеров", "разобрать формулу выбора и калькулятор"])}
  {mini_toc_for_routing}
  {{routes_block}}

  <div class="hero">
    <h2 id="route-howto">Как выбирается путь</h2>
    <p class="small muted">Есть два способа. Первый — доверить выбор замерам: программа сама мерит пути и переключается
    только при подтверждённой деградации. Второй — указать путь вручную, тогда она просто проверяет задержку и не решает за вас.
    Время замеров влияет на надёжность вывода: чем дольше, тем устойчивее.</p>

    <div class="grid g2">
      <div class="card tight">
        <h4>Способ выбора</h4>
        <div class="valset">
          <label class="pick"><input type="radio" name="routeMode" checked>
            <span><b>Автоматически по замерам</b><span class="small muted"> — рекомендовано: решение принимают числа,
            а не «оптимизировано» ради галочки</span></span></label>
          <label class="pick"><input type="radio" name="routeMode">
            <span><b>Вручную</b><span class="small muted"> — вы выбираете прямой путь или узел; программа только измеряет</span></span></label>
        </div>
      </div>

      <div class="card tight">
        <h4>Время теста</h4>
        <div class="valset">
          <label class="pick"><input type="radio" name="routeTime">
            <span><b>Мгновенно</b> — 1 окно, ~{rt_interval} с<span class="small muted"> — одиночный всплеск может дать
            неверный вывод, поэтому переключение всё равно ждёт подтверждения</span></span></label>
          <label class="pick"><input type="radio" name="routeTime">
            <span><b>Быстро</b> — 5 окон, ~10 с<span class="small muted"> — компромисс, когда нужно решить сразу</span></span></label>
          <label class="pick"><input type="radio" name="routeTime" checked>
            <span><b>Для лучшего результата</b> — 15 окон, ~30 с<span class="small muted"> — рекомендуем: медиана по окнам
            не портится одиночными скачками</span></span></label>
          <label class="pick"><input type="radio" name="routeTime">
            <span><b>Максимальная точность</b> — 30 окон, ~60 с<span class="small muted"> — самый надёжный вывод, но ждать дольше</span></span></label>
        </div>
        <p class="note-inline">В отчёте всегда видно, сколько замеров сделано. Меньше 30 — результат помечается
        как предварительная оценка, а не как готовый вывод.</p>
      </div>
    </div>

    <div class="row" style="margin-top:14px">
      <button class="btn primary big" id="btnPickRoute">Выбрать автоматически</button>
      <span class="btn" id="btnStopRoute">Остановить замеры</span>
      <span class="chip" id="routeChip">замеров ещё не было</span>
    </div>
    <p class="small dim">Ничего в системе не меняется до вашего согласия: выбранный путь применяется только после
    показа чисел «до» и «после», а откат возвращает прежний маршрут.</p>
  </div>

  <div class="grid g2">
    <div class="card">
      <h3>Что делает</h3>
      <ul class="tight small muted">
        <li>Меряет прямой путь и пути через узлы релея до <b>вашего</b> игрового сервера.</li>
        <li>Считает баллы по одной формуле с явными весами — их видно пользователю.</li>
        <li>Переключает путь только при подтверждённой деградации и с запасом по баллам.</li>
        <li>Никогда не удаляет маршруты, созданные не PingForge (метка владельца).</li>
        <li>Если релей не выигрывает — так и говорит, вместо «оптимизировано» ради галочки.</li>
      </ul>
    </div>
    <div class="card">
      <h3>Формула</h3>
      <p class="small"><code>{esc(data["routing"]["formulaRu"])}</code></p>
      <p class="small muted">Веса (из <code>RouteScoreWeights</code>): RTT {w["perRttMs"]} за мс · джиттер {w["perJitterMs"]} ·
      потери² {w["perLossPercentSquared"]} · хоп {w["perHop"]} · нагрузка узла {w["perNodeLoadOver50"]} ·
      доп. трафик {w["perExtraTrafficPercent"]}.</p>
      <div class="legend">
        <span>потери ≤ {th["maxLossPercent"]} %</span>
        <span>джиттер ≤ {th["maxJitterMs"]} мс</span>
        <span>RTT ≤ {th["maxRttMs"]} мс</span>
      </div>
    </div>
  </div>

  <div class="card">
    <h3>Правила переключения (гистерезис)</h3>
    <ul class="tight small muted">{rules}</ul>
    <p class="small dim">Числа из кода: плохих замеров подряд — {f["badSamplesBeforeSwitch"]},
    запас по баллам — {f["switchMarginPoints"]}, замеров для возврата — {f["goodSamplesBeforeReturn"]},
    период покоя — {int(f["minTimeOnRouteSeconds"])} с.</p>
  </div>

  <h2>Что увидит пользователь</h2>
  <div class="window">
    <div class="titlebar"><span class="dots"><i></i><i></i><i></i></span><b>Выбор маршрута</b></div>
    <div class="body">
      <div class="scroll">
        <table>
          <thead><tr><th>Путь</th><th class="num">RTT</th><th class="num">Джиттер</th><th class="num">Потери</th><th class="num">Хопов</th><th class="num">Балл</th><th>Вывод</th></tr></thead>
          <tbody>
            <tr><td>Прямое подключение</td><td class="num">24.0</td><td class="num">6.0</td><td class="num">1.0</td><td class="num">11</td><td class="num">33.90</td>
                <td><span class="tag ok">выбрано</span></td></tr>
            <tr><td>Узел релея «Франкфурт»</td><td class="num">21.0</td><td class="num">3.0</td><td class="num">0.0</td><td class="num">13</td><td class="num">30.30</td>
                <td><span class="tag">выигрыш 3.60 — предложено</span></td></tr>
            <tr><td>Узел релея «Стокгольм»</td><td class="num">34.0</td><td class="num">4.0</td><td class="num">0.5</td><td class="num">12</td><td class="num">44.50</td>
                <td><span class="tag bad">хуже прямого — не предлагается</span></td></tr>
          </tbody>
        </table>
      </div>
      <p class="small muted">Пример заполнения таблицы (демонстрационные числа).</p>
    </div>
  </div>

  <div class="calc js-only">
    <div class="col">
      <div class="between"><b>Прямое подключение</b><span class="score" id="dScore">—</span></div>
      <label>RTT <span id="dRttV"></span></label><input type="range" id="dRtt" min="1" max="200" value="24">
      <label>Джиттер <span id="dJitterV"></span></label><input type="range" id="dJitter" min="0" max="60" value="6">
      <label>Потери <span id="dLossV"></span></label><input type="range" id="dLoss" min="0" max="20" value="1">
      <label>Хопов <span id="dHopsV"></span></label><input type="range" id="dHops" min="1" max="20" value="11">
    </div>
    <div class="col">
      <div class="between"><b>Через узел релея</b><span class="score" id="rScore">—</span></div>
      <label>RTT <span id="rRttV"></span></label><input type="range" id="rRtt" min="1" max="200" value="21">
      <label>Джиттер <span id="rJitterV"></span></label><input type="range" id="rJitter" min="0" max="60" value="3">
      <label>Потери <span id="rLossV"></span></label><input type="range" id="rLoss" min="0" max="20" value="0">
      <label>Хопов <span id="rHopsV"></span></label><input type="range" id="rHops" min="1" max="20" value="13">
    </div>
  </div>
  <div class="verdict direct" id="calcVerdict">Прямое подключение не хуже релея при значениях по умолчанию:
    выигрыш меньше порога 1.00 балла. Подвиньте ползунки (нужны работающие скрипты), чтобы увидеть расчёт.</div>

  <div class="callout warn">
    <b>Когда релей НЕ помогает:</b> если путь до сервера и так короткий; если провайдер не «капризничает» в часы пик;
    если игровые серверы стоят рядом с вами. Тогда честный ответ — «играйте напрямую», и приложение его даст.
  </div>
  {up_link()}
</section>

<section class="tab" id="diagnostics">
  {part_head(3, "Что программа проверяет сама", "часть 2 — анализ сети и адресов")}
  <p class="muted">Программа сама смотрит на состояние сети и решает, что имеет смысл применить.
  Числа в примерах ниже — демонстрационные, помечены как пример.</p>
  {section_tools(["список проверок и их формула", "коды предупреждений из кода программы", "что происходит, если измерить не удалось"])}

  <div class="grid g2">
    <div class="card"><h3>Что проверяется</h3><ul class="tight small muted">{notes}</ul></div>
    <div class="card">
      <h3>Формула стабильности</h3>
      <p class="small"><code>{esc(data["probing"]["stabilityFormulaRu"])}</code></p>
      <p class="small muted">Минимум для полноценного отчёта: <b>{data["probing"]["samplesForFullReport"]}</b> сэмплов.
      Меньше — результат помечается как предварительный, и это видно в интерфейсе.</p>
    </div>
  </div>

  <h2 id="report-example">Пример отчёта</h2>
  <div class="grid g4">
    <div class="metric"><div class="label">RTT средний</div><div class="value">27.4 мс</div><div class="small dim">min 24.1 / p95 39.8</div></div>
    <div class="metric"><div class="label">Джиттер (MAD)</div><div class="value">3.1 мс</div><div class="small dim">одиночные скачки не портят картину</div></div>
    <div class="metric"><div class="label">Потери</div><div class="value">1.3 %</div><div class="small dim">39 из 40 сэмплов</div></div>
    <div class="metric"><div class="label">Стабильность</div><div class="value">87</div><div class="small dim">из 100</div></div>
  </div>

  <div class="grid g2">
    <div class="card tight">
      <h3>Определение MTU пробами с флагом DF</h3>
      <p class="small muted">Найденный путь: <b>1492</b> (PPPoE). Если ICMP заблокирован, приложение пишет
      «определить не удалось» и не подставляет MTU наугад.</p>
    </div>
    <div class="card tight">
      <h3>Предупреждения о конфликтах</h3>
      <p class="small muted">Пример: «Активен сторонний VPN — он перехватывает трафик, выигрыш от оптимизации может быть
      нулевым». Такие предупреждения сохраняются в журнале и переживают перезапуск.</p>
    </div>
  </div>

  <h2 id="analyzer-codes">Проверки анализатора (коды из кода программы)</h2>
  <div class="card tight">
    <div class="scroll">
      <table>
        <thead><tr><th>Код</th><th>Что проверяется</th><th>Если измерить нельзя</th></tr></thead>
        <tbody>
          <tr><td><code>TUNNEL_ACTIVE</code></td><td>Активен VPN или туннель одновременно с физическим подключением:
            игровой трафик идёт длинным путём, выигрыш оптимизации может быть нулевым</td>
            <td>Предупреждение остаётся, но помечается как «требует вашего решения», а не как ошибка</td></tr>
          <tr><td><code>MULTIPLE_ACTIVE_PATHS</code></td><td>Несколько физических подключений с шлюзом:
            оптимизируется только то, через которое реально идёт игра</td>
            <td>Показывается список адаптеров и текущее основное подключение</td></tr>
          <tr><td><code>CONTEXT_INCOMPLETE</code></td><td>Часть данных о сети прочитать не удалось (нет прав, драйвер не ответил)</td>
            <td>Пункт уходит в «пропущено» с причиной — значения по умолчанию не подставляются</td></tr>
          <tr><td>MTU / DNS</td><td>MTU считается DF-пробами до игрового сервера, DNS — бенчмарком резолверов
            с отбраковкой перехватчиков</td>
            <td>Совет не выдаётся: твик просто не предлагается, причина видна в списке пропущенных проверок</td></tr>
        </tbody>
      </table>
    </div>
    <p class="small muted" style="margin-top:10px">Итог анализа всегда заканчивается пометкой:
    эти правки убирают лишние задержки на вашей стороне, но <b>пинг внутри боя не снижают</b> —
    скорость света и маршрут провайдера от настроек Windows не зависят.</p>
  </div>

  <h2 id="input-codes">Проверки советника задержки ввода (коды из кода программы)</h2>
  <div class="card tight">
    <div class="scroll">
      <table>
        <thead><tr><th>Код</th><th>Когда появляется</th><th>Что делает приложение</th></tr></thead>
        <tbody>
          <tr><td><code>PERIPHERALS_UNKNOWN</code></td><td>Список мышей и клавиатур прочитать не удалось</td>
            <td>Твики мыши и клавиатуры уходят в «пропущено»: устройства «по памяти» не подставляются</td></tr>
          <tr><td><code>WIRELESS_POINTER</code></td><td>Указатель подключён по радио — Bluetooth или донгл</td>
            <td>Акселерацию всё равно убираем, но прямо сказано: задержку радиоканала правки не сокращают</td></tr>
          <tr><td><code>INPUT_OVER_WIFI</code></td><td>Активное подключение — Wi-Fi</td>
            <td>Предупреждение про повторы передачи в эфире и совет про кабель, без обещаний «как по кабелю»</td></tr>
          <tr><td><code>INPUT_PATH_THROUGH_ROUTER</code></td><td>У активного подключения есть шлюз — роутер или модем</td>
            <td>Список ручных проверок для панели устройства: приоритет трафика, энергосбережение Wi-Fi, режим моста.
              Программа в роутер не заходит и не обещает этого делать</td></tr>
        </tbody>
      </table>
    </div>
    <p class="small muted" style="margin-top:10px">Итог этого разбора тоже заканчивается честной строкой: правки убирают
    задержку на вашей стороне, но скорость интернета и задержку внутри игрового сервера не меняют.</p>
  </div>

  <h3>Бенчмарк DNS: кандидаты берутся из кода проекта</h3>
  <div class="card tight">
    <div class="scroll">
      <table><thead><tr><th>Резолвер</th><th>Основной</th><th>Резервный</th></tr></thead><tbody>{dns_rows}</tbody></table>
    </div>
    <p class="small muted" style="margin-top:10px">Домены для замеров: {esc(", ".join(data["probing"]["dnsProbeDomains"]))}.
    Каждый ответ проверяется (ID транзакции, имя вопроса, адрес источника), плюс делается canary-запрос в зону
    <code>.invalid</code>: если резолвер на него отвечает — это видно в отчёте как подмена DNS.</p>
  </div>
  {up_link()}
</section>

<section class="tab" id="journal">
  {section_head(4, "Журнал и откат")}
  <p class="muted">Каждое изменение имеет «до», «после» и причину. Ниже — пример заполнения журнала;
  схема базы (v4) в проекте уже такая.</p>

  <div class="simple">
    <b>Простыми словами.</b> Снапшот — это фотография ваших настроек «до». Журнал — список того, что программа
    сделала: какое значение стояло, какое стало и почему. Отсюда же работает откат: одно нажатие — и всё как было.
    Если компьютер выключится в момент изменения, программа при следующем запуске сама откатит незаконченное.
  </div>
  {section_tools(["раскрыть строку и увидеть причину", "вернуть состояние снапшота одним шагом", "убедиться, что после сбоя откат идёт сам"])}

  <div class="callout ok">
    <b>Снапшот подписан контрольной суммой SHA-256.</b> Если файл базы подменить, кнопка отката откажется работать,
    а не вернёт наугад чужое значение.
  </div>

  <div class="card">
    <h3>Журнал изменений (пример)</h3>
    <div class="scroll">
      <table><thead><tr><th>Время</th><th>Твик</th><th>Было</th><th>Стало</th><th>Результат</th></tr></thead>
      <tbody>{journal_rows}</tbody></table>
    </div>
  </div>

  <div class="grid g2">
    <div class="card tight">
      <h3>Точки восстановления</h3>
      <p class="small muted">Статусы различаются: создана · не запрошена · пропущена из-за суточного лимита Windows ·
      защита системы отключена · ошибка с текстом. Пользователь видит именно свой случай.</p>
    </div>
    <div class="card tight">
      <h3>Что будет после аварии</h3>
      <p class="small muted">Журнал предзаписи закрывает все категории: твики, добавленные маршруты, поднятые туннели,
      запущенный фильтр, изменённый DNS. При следующем старте остатки снимаются, а если снять нельзя — пишется
      «требуется ручная проверка» и появляется кнопка «Починить сеть».</p>
    </div>
  </div>

  <div class="card tight" style="margin-top:12px">
    <h3>Выгрузка в CSV</h3>
    <p class="small muted">Кнопка «Экспорт CSV» пишет таблицу целиком во временный файл рядом и подменяет его одним
    движением: недописанной выгрузки не остаётся. Существующий файл программа не затирает молча — второй экспорт
    с тем же именем честно скажет, что имя занято. Значения, которые таблица приняла бы за формулу («=», «+», «@»,
    табуляция, минус перед текстом), помечены апострофом: это текст, а не вычисление. Число строк подписано
    по-русски — «1 строка», «3 строки», «20 строк».</p>
  </div>

  <h3 id="journal-files" style="margin-top:16px">Примеры файлов, которые оставляет программа</h3>
  <p class="small muted">Шесть разных файлов. Ни один из них программа никуда не отправляет — все лежат на вашем диске.</p>
  <div class="grid g2">
    <div class="card tight">
      <b>Журнал с поворотом</b>
      <pre class="snippet">2026-11-18 12:24:51 Слушаю канал (права администратора: есть)
2026-11-18 12:25:02 Команда «optimize»: готово — маршрут до 3.4.5.6
2026-11-18 12:25:34 Клиент отключился
<span class="dim">service.log      5 МБ — текущий
service.log.1    5 МБ — предыдущий
service.log.2    5 МБ — самый старый, следующий поворот его удалит</span></pre>
      <p class="small muted">Строка не рвётся: поворот идёт до записи, а не посреди неё. Если повернуть не удалось (файл занят),
      программа дописывает в текущий — лишняя строка лучше потерянной.</p>
    </div>
    <div class="card tight">
      <b>Выгрузка журнала: journal-20261118-122451.csv</b>
      <pre class="snippet">Дата,Твик,Код,Предыдущее,Применено,Проверено,Статус
2026-10-16 12:10:00,Отключение алгоритма Нейгла,TCP_NO_DELAY,0,1,1,Применено
2026-10-16 12:09:00,'=HYPERLINK("http://зло"),DNS_CACHE,0,1,1,Применено</pre>
      <p class="small muted">Апостроф в начале — защита от формулы: таблица покажет текст, а не вычисление.</p>
    </div>
    <div class="card tight">
      <b>Экспорт настроек: PingForge-настройки.json</b>
      <pre class="snippet">{{{{
  "SchemaVersion": 1,
  "ExportedUtc": "2026-11-18T12:24:51.208Z",
  "NoteRu": "Пути к играм проверьте на новом компьютере.",
  "Settings": {{ "PerfProfile": "balanced", "CloseToTray": true }}
}}}}</pre>
      <p class="small muted">Пишется целиком во временный файл и подменяет прежний: половины копии не остаётся.</p>
    </div>
    <div class="card tight">
      <b>Отчёт о падении: crash-20261118-123456-789.txt</b>
      <pre class="snippet">Custom Tweak By Doxaiz — отчёт об ошибке
Этот файл лежит на вашем диске. Программа никуда его не отправляет.

Время: 2026-11-18 12:34:56
Что случилось: проверка записи отчёта
── Версии и окружение ──
Окно: 1.0.0 · Служба: 1.0.0 · Windows 11</pre>
      <p class="small muted">Два падения в одну миллисекунду не съедают отчёты друг друга: второй так и скажет, что имя занято.</p>
    </div>
    <div class="card tight">
      <b>Пакет для поддержки: PingForge-поддержка-20261118-122451.zip</b>
      <pre class="snippet">service.log   последние 500 строк из 12 480 — показаны не все
сводка.txt    версии, права, что проверяла программа
crash/…txt    отчёт о падении, если он был
<span class="dim">предел 20 МБ: слишком большой журнал выкидывается,
и план говорит, почему именно</span></pre>
      <p class="small muted">Внутри адреса игровых серверов и пути к файлам на этом компьютере — пакет никуда не уходит сам.
      Собирается рядом и подменяет прежний целиком: сбой не оставит человека без старого пакета.</p>
    </div>
    <div class="card tight">
      <b>Резервная копия при сбросе: settings.json.bak-20261118-122451</b>
      <pre class="snippet">settings.json                 ← текущие настройки
settings.json.bak-20261118-122451  ← копия «до сброса»</pre>
      <p class="small muted">Снапшоты и журнал при сбросе не удаляются — и это написано в вопросе до нажатия.</p>
    </div>
  </div>
  {up_link()}
</section>

<section class="tab" id="settings">
  {section_head(5, "Настройки")}
  <p class="muted">Конфиг профиля: сохранить подобранные настройки в файл или загрузить чужой набор — с проверкой
  значений и честным предупреждением, что чужие настройки на этой машине никто не проверял.</p>

  <div class="simple">
    <b>Простыми словами.</b> Конфиг (профиль) — это небольшой файл со списком: какой твик включён и какое значение
    вы выбрали. Его можно сохранить, перенести на другой компьютер или отдать другу. Ничего личного в файл не попадает:
    ни паролей, ни ключей, ни истории. При загрузке чужого файла программа сначала показывает, что изменится,
    и пропускает то, что не подходит вашей машине.
  </div>
  {section_tools(["сохранить текущий конфиг в файл", "импортировать чужой профиль", "выбрать, что именно переносить"])}
  {mini_toc_for_settings}

  <div class="grid g2">
    <div class="card">
      <h3>Сохранить текущий конфиг</h3>
      <p class="small muted">В файл попадают только настройки самого приложения: выбранные твики и их значения,
      пресет, игровые серверы для замеров. Пароли, ключи и данные аккаунтов в профиль не сохраняются.</p>
      <div class="valset">
        <label class="pick"><input type="checkbox" checked><span>Выбранные твики и их значения</span></label>
        <label class="pick"><input type="checkbox" checked><span>Активный пресет</span></label>
        <label class="pick"><input type="checkbox" checked><span>Игровые серверы и порты для замеров</span></label>
        <label class="pick"><input type="checkbox"><span>Журнал измерений — чтобы сравнить результаты на другой машине</span></label>
        <label class="pick"><input type="checkbox"><span>Снапшоты и точки восстановления</span></label>
      </div>
      <p class="note-inline">Снапшоты по умолчанию не переносятся: на другой машине они указывают на чужие ключи реестра
      и могли бы вернуть неподходящие значения.</p>
      <div class="row" style="margin-top:12px">
        <span class="btn primary">Сохранить в файл…</span>
        <span class="btn">Скопировать текстом</span>
        <span class="chip" id="cfgSaved">файл ещё не создан</span>
      </div>
    </div>

    <div class="card">
      <h3>Импорт конфига</h3>
      <p class="small muted">Профиль — обычный JSON. Перед применением программа показывает разницу: что появится,
      что изменится и какие значения этой машине не подходят.</p>
      <div class="row" style="gap:8px;margin-bottom:10px">
        <span class="btn small">Выбрать файл…</span>
        <span class="small dim">или вставьте текст профиля в поле ниже</span>
      </div>
      <textarea rows="12" spellcheck="false" style="width:100%;font-family:'Cascadia Mono',Consolas,monospace;font-size:12.5px;background:#0b1220;color:var(--text);border:1px solid var(--line2);border-radius:9px;padding:10px">{cfg_example}</textarea>
      <div class="row" style="margin-top:10px">
        <span class="btn primary">Проверить и применить</span>
        <span class="btn">Отмена</span>
      </div>
      <p class="note-inline">Импорт не пишет в систему сразу: сначала снапшот и точка восстановления, затем поштучная
      проверка. Неподходящие значения (свойство, которого нет у этого драйвера; твик, отсутствующий в каталоге сборки)
      пропускаются с причиной, а не подставляются вслепую.</p>
    </div>
  </div>

  {{consumption_block}}

  <!-- Масштаб интерфейса: та же карточка, что в программе (раздел «Настройки»). -->
  <div class="card">
    <h3>Масштаб интерфейса</h3>
    <p class="small muted">Раскладка пересобирается, текст остаётся резким, карточки тянутся.
    Окно не меньше 860×560 точек при 100 %; при 125 % и 150 % минимум окна растёт вместе с содержимым
    и не выходит за рабочую область экрана. Выбранный масштаб запоминается в настройках.</p>
    <div class="scaleopts" role="group" aria-label="Масштаб интерфейса">
      <label><input type="radio" name="uiscale" checked><span>100 %</span></label>
      <label><input type="radio" name="uiscale"><span>125 %</span></label>
      <label><input type="radio" name="uiscale"><span>150 %</span></label>
    </div>
    <p class="note-inline">Контраст основного текста #E9EFFB на фоне #070B14 — 17,1:1, подписей #8FA1BD — 7,5:1:
    выше требования AA (4,5:1) и выше AAA (7:1) для обычного текста.</p>
  </div>

  <h2 id="settings-misc">Прочее</h2>
  <div class="card tight">
    <div class="valset">
      <label class="pick"><input type="checkbox" checked><span><b>Спрашивать точку восстановления</b>
        <span class="small muted"> — перед каждым применением твиков (рекомендуется)</span></span></label>
      <label class="pick"><input type="checkbox" checked><span><b>Сверять значения с каталогом при импорте</b>
        <span class="small muted"> — чужое значение проверяется по текущей сборке, а не принимается на веру</span></span></label>
      <label class="pick"><input type="checkbox" checked><span><b>Вести журнал измерений локально</b>
        <span class="small muted"> — история «до/после» хранится на вашей машине</span></span></label>
      <label class="pick"><input type="checkbox" disabled><span><b>Отправлять телеметрию</b>
        <span class="small muted"> — недоступно: программа не собирает и не отправляет данные (см. раздел 8)</span></span></label>
    </div>
  </div>

  <div class="callout">
    <b>Коды твиков в профиле — те же, что в каталоге этой страницы.</b> Поэтому чужой профиль, собранный на другой
    версии программы, можно сравнить: программа покажет, какие коды она не знает, и предложит ничего по ним не делать.
  </div>
  {up_link()}
</section>

<section class="tab" id="progress">
  {section_head(6, "Что уже готово", "часть 1 — готовность проекта")}
  <div class="simple">
    <b>Зачем этот раздел.</b> Здесь честно написано, что уже работает, что написано, но не проверено на живых
    Windows-машинах, и чего пока нет. Так вы не примете макет за готовую программу.
  </div>
  <p class="muted">Оценка по объёму работы до состояния «этим можно пользоваться» (PingForge Lite).
  «Готово» — честная оценка с учётом того, что проверено автотестами, а что только написано.</p>
  {section_tools(["строки с весом и процентом готовности", "что покрыто автотестами", "что стоит следующим в очереди"])}

  <div class="card">
    <div class="between" style="margin-bottom:12px">
      <b>Готовность PingForge Lite</b>
      <span class="score">{progress["total"]:.1f}%</span>
    </div>
    <div class="scroll">
      <table>
        <thead><tr><th>Слой</th><th class="num">Вес</th><th class="num">Готово</th><th>Полоса</th><th>Комментарий</th></tr></thead>
        <tbody>{progress_rows}</tbody>
      </table>
    </div>
    <p class="small muted" style="margin-top:12px">Сумма весов ровно 100 %; итог считается по весам
    (Σ вес × готово / Σ весов). Раньше веса сходились к 108, и цифра выходила завышенной примерно на 3 пункта —
    арифметику исправили, поэтому итог ниже прежнего, и это правильный итог.</p>
    <p class="small muted" style="margin-top:8px">Оставшиеся проценты — это в основном то, что видит пользователь:
    служба Windows, интерфейс и проверка на живом железе. Логика, которую не видно (снапшоты, проверки, откат, скоринг),
    уже работает и покрыта тестами.</p>
  </div>

  <div class="grid g2">
    <div class="card tight">
      <h3>Проверено тестами прямо сейчас</h3>
      <p class="small muted">169 автотестов: снапшоты и откат на реальной базе, канонические форматы значений,
      разбор русского вывода netsh, гистерезис маршрутов, MAD-джиттер, DF-MTU, DNS с ловлей подмены,
      аудит запретов в каталоге, советы по MTU/DNS и разбор каталога относительно текущих настроек.</p>
    </div>
    <div class="card tight">
      <h3>Проверено только компилятором</h3>
      <p class="small muted">Поведение на живом Windows: реестр, netsh, свойства адаптера, определение активного адаптера,
      и все три сценария аварии (убили службу, перезагрузились с активным туннелем, отозвали права администратора).</p>
    </div>
  </div>
  {up_link()}
</section>

<section class="tab" id="about">
  {part_head(6, "Что программа не делает", "часть 2 — запреты и их причина")}
  <p class="muted">Список запретов — часть продукта, а не мелкий шрифт. В проекте он проверяется автотестом каталога.</p>
  {section_tools(["список запретов из кода", "чем заменены «чит»-подходы", "про подпись драйвера и анти-читы"])}

  <div class="grid g2">
    <div class="card">
      <h3>Никогда</h3>
      <ul class="tight small muted">
        <li>Не инжектит DLL в игру и не читает память игры.</li>
        <li>Не трогает анти-читы и не пытается их обойти.</li>
        <li>Не отключает Defender, брандмауэр, обновления безопасности, UAC и проверку подписи драйверов.</li>
        <li>Не подменяет MAC- или IP-адрес.</li>
        <li>Не меняет файл подкачки и не отключает системные службы «для скорости».</li>
        <li>Не отправляет телеметрию: наружу уходит только то, что вы отправите сами.</li>
      </ul>
    </div>
    <div class="card">
      <h3>Чего не обещает</h3>
      <ul class="tight small muted">
        <li>«Пинг 0 мс» не существует. Выигрыш показывается только если он измерен.</li>
        <li>Релей помогает не всем и не всегда: если выигрыша нет — так и написано.</li>
        <li>DNS влияет на поиск матча и лобби, а не на пинг внутри боя — это написано в карточке твика.</li>
        <li>Изменения не делаются без подтверждения и без снапшота.</li>
      </ul>
    </div>
  </div>

  <h2>Лицензии третьих сторон</h2>
  <div class="card tight">
    <div class="scroll">
      <table>
        <thead><tr><th>Компонент</th><th>Назначение</th><th>Лицензия</th><th>Статус</th></tr></thead>
        <tbody>
          <tr><td>WinDivert</td><td>Перехват трафика игры по 5-tuple</td><td>LGPL v3</td><td class="muted">загружается динамически, файл THIRD-PARTY-NOTICES обязателен</td></tr>
          <tr><td>SQLite (Microsoft.Data.Sqlite)</td><td>Локальная база и журнал</td><td>Public Domain</td><td class="muted">уже используется</td></tr>
          <tr><td>wireguard.dll</td><td>Туннель (только в сборке с релеем)</td><td>GPL v2</td><td class="muted">не вшивается в продукт</td></tr>
        </tbody>
      </table>
    </div>
  </div>

  <div class="callout warn">
    <b>Это макет, а не работающая программа.</b> Страница собрана из реальных данных каталога твиков
    (сгенерирована {data["generatedUtc"][:10]} из кода проекта), но окон WPF и службы пока нет — они следующие в очереди.
    Числа в примерах отчётов помечены как демонстрационные.
  </div>
  <h2>Поддержать автора</h2>
  <div class="card tight">
    <p class="small">Программа бесплатная и без телеметрии. Если она оказалась полезной — автору можно
    сказать спасибо по ссылке ниже. Кнопка просто открывает страницу в браузере: ничего не списывается,
    данные никуда не отправляются, доступ к деньгам и аккаунтам программа не запрашивает.</p>
    <div class="row" style="margin-top:10px">
      <a class="btn primary" href="https://dalink.to/doxaiz" target="_blank" rel="noopener">Поддержать автора ↗</a>
      <span class="small dim">Ссылка ведёт на страницу автора: dalink.to/doxaiz</span>
    </div>
  </div>
  {up_link()}
</section>

<footer class="foot">
  Собрано автоматически из каталога проекта {data["generatedUtc"][:10]} · твиков: {data["tweakCount"]} ·
  нарушений запретов и проблем каталога: {len(data["catalogProblems"]) + len(data["safetyProblems"])}.<br>
  Страница самодостаточна: без внешних скриптов, шрифтов и картинок. Если скрипты в предпросмотре отключены,
  все разделы показаны подряд — содержимое то же.<br>
  Продуктовое имя для релиза — <b>Custom Tweak by Doxaiz</b>; в коде и заголовке макета пока рабочее имя
  PingForge, переименование отложено осознанно: имена службы и базы уже стоят на дисках у людей.
</footer>

</main>
</div>

<script type="application/json" id="routingData">{json.dumps({"weights": w, "thresholds": th, "failover": f}, ensure_ascii=False)}</script>
<script type="application/json" id="realtimeData">{json.dumps(rt, ensure_ascii=False)}</script>
<script>{SCRIPT}</script>
</body>
</html>
"""


# ── экскурсия по странице: оглавление и переходы «назад/далее» ───────────────────────────────────
# Меню — это шесть пунктов, но внутри трёх из них по две части. Оглавление показывает это сразу,
# а переходы в конце каждой части дают листать страницу как книгу — и всё это без скриптов.

TOUR = [
    ("overview", 1, "Оптимизация", "Нажать одну кнопку и увидеть, стало ли лучше",
     "живой режим · замеры до и после · остановка с возвратом"),
    ("apply", 2, "Твики", "Настройки Windows: что делает каждая и что можно выбрать",
     "пресеты · применение · ручной выбор 28 твиков"),
    ("routing", 3, "Сеть и маршрут", "Как трафик идёт до игры и можно ли сократить путь",
     "выбор пути · время теста · проверки сети"),
    ("journal", 4, "Журнал и откат", "Что изменили, что получилось и как вернуть назад",
     "история операций · точка восстановления · откат"),
    ("settings", 5, "Настройки", "Профиль, конфиг, язык и поведение программы",
     "профиль · импорт и экспорт · приватность"),
    ("progress", 6, "Границы и готовность", "Чего программа не делает и что ещё не готово",
     "готовность по слоям · запреты · что дальше"),
]

# Порядок частей на странице: (якорь, название, короткая подпись для переходов)
SECTION_CHAIN = [
    ("overview", "Оптимизация соединения", "Оптимизация"),
    ("apply", "Применить твики", "Применение твиков"),
    ("tweaks", "Мои твики — ручной выбор", "Ручной выбор твиков"),
    ("routing", "Как выбирается путь до игры", "Маршрут"),
    ("diagnostics", "Что программа проверяет сама", "Анализ сети"),
    ("journal", "Журнал и откат", "Журнал и откат"),
    ("settings", "Настройки", "Настройки"),
    ("progress", "Что уже готово", "Готовность"),
    ("about", "Что программа не делает", "Запреты"),
]


def render_toc() -> str:
    """Оглавление из шести плиток: видно сразу, что где лежит, и работает без скриптов."""
    tiles = []
    for anchor, num, title, desc, inside in TOUR:
        tiles.append(
            f'<a class="toc-tile" href="#{anchor}"><span class="toc-num">{num}</span>'
            f'<span><b>{esc(title)}</b><span class="toc-desc">{esc(desc)}</span>'
            f'<span class="toc-inside">{esc(inside)}</span></span></a>')
    return ('<nav class="toc" aria-label="Разделы страницы">'
            '<div class="toc-title"><b>Шесть разделов.</b> Каждый — про одно дело: '
            'нажмите, чтобы перейти сразу к нему.</div>'
            f'<div class="tocgrid">{"".join(tiles)}</div></nav>')


def render_section_nav(index: int) -> str:
    """Переходы в конце части: назад, ко оглавлению, дальше. Последняя часть закрывает страницу."""
    _, _, short = SECTION_CHAIN[index]

    if index == 0:
        back = '<a href="#top">\u2191 Начало страницы</a>'
    else:
        back = f'<a href="#{SECTION_CHAIN[index - 1][0]}">\u2190 Назад: {esc(SECTION_CHAIN[index - 1][2])}</a>'

    if index == len(SECTION_CHAIN) - 1:
        forward = '<span class="end">Это последний раздел</span>'
    else:
        forward = (f'<a href="#{SECTION_CHAIN[index + 1][0]}">'
                   f'Далее: {esc(SECTION_CHAIN[index + 1][2])} \u2192</a>')

    return (f'<div class="secnav"><span class="small dim">{esc(short)} — конец части.</span>'
            f'{back}<a class="secnav-mid" href="#top">\u2191 Оглавление</a>{forward}</div>')


def decorate(html: str) -> str:
    """Вставляет оглавление в начало и переходы в конец каждой части. Проверяет, что частей ровно девять."""
    assert '<main class="content">' in html, "не нашёл начало содержимого"
    html = html.replace('<main class="content">', '<main class="content">\n' + render_toc(), 1)

    counter = {"i": 0}

    def insert(match: "re.Match[str]") -> str:
        index = counter["i"]
        counter["i"] += 1
        assert index < len(SECTION_CHAIN), "в разметке больше частей, чем описано в SECTION_CHAIN"
        return render_section_nav(index) + match.group(0)

    html = re.sub(r"</section>", insert, html)
    assert counter["i"] == len(SECTION_CHAIN), f"частей на странице: {counter['i']}, ожидалось {len(SECTION_CHAIN)}"
    return html


def main() -> int:
    data_path = Path(sys.argv[1] if len(sys.argv) > 1 else "/var/tmp/catalog.json")
    out_path = Path(sys.argv[2] if len(sys.argv) > 2 else "docs/pingforge-ui-preview.html")

    data = json.loads(data_path.read_text(encoding="utf-8"))

    # ── честная оценка готовности ─────────────────────────────────────────────
    progress_rows = [
        ("Ядро: модели, контракты, канонический формат значений, целостность снапшота", 7, 100,
         "19 тестов на форматы и хеши"),
        ("Пробы и статистика: ICMP/TCP, MAD-джиттер, DF-MTU, DNS-бенчмарк, трассировка", 8, 85,
         "логика покрыта тестами; на живом железе не прогонялось"),
        ("Скоринг маршрутов, фейловер, планировщик изменений", 7, 80,
         "маршрут ставится и снимается командой route с проверкой и откатом; переключение на живой машине не проверялось"),
        ("База данных, журнал предзаписи, снапшоты", 5, 100, "схема v4, реальная SQLite в тестах"),
        ("Оркестрация: снапшот → применение → проверка → автооткат", 8, 100,
         "включая восстановление после падения приложения"),
        ("Каталог твиков: 47 твиков с честными описаниями, подкатегориями, вердиктами и правилами пресетов", 9, 92,
         "вердикты «стоит включать или нет» и правило «по умолчанию включено только рекомендованное» живут "
         "в коде (TweakVerdict) и проверены тестами; пресеты 19 / 5 / 23 различаются по составу"),
        ("Windows-бэкенды твиков: реестр, netsh, свойства NIC", 8, 45,
         "код написан, типы проверены сборкой; поведение проверяется только на Windows"),
        ("Сетевой слой: детектор игр, маршрут для игры, политика QoS (DSCP 46)", 7, 95,
         "движок собран целиком + UDP-наблюдатель ETW (0.5.5: события 42/43/58/59, 30с/4096, отсечение своих/приватных), "
         "решение автовыбора кэшируется по процессу+адресу:порту (DecisionKey); на живой Windows кнопка отвечает «игра не найдена» — "
         "это ответ работающего движка, прямой UDP-тест на живой игре ещё не делался"),
        ("Слой анализа: что применить исходя из состояния сети", 7, 88,
         "советники MTU/DNS, анализатор сети и советник задержки ввода готовы и покрыты тестами"),
        ("Задержка ввода: 10 твиков, советник и чтение устройств", 3, 65,
         "каталог, советник и 12 тестов готовы; чтение списка устройств и интерфейс проверяются на Windows"),
        ("Трей, значки и экономный профиль потребления", 4, 80,
         "меню, подсказки и профили проверены тестами; значок создаётся и меню собирается на живой Windows — "
         "это проверяет самопроверка при каждой сборке"),
        ("Справочник маршрутов (22 игры, 5 DNS, 8 способов) и график сети", 3, 92,
         "данные в коде и в макете (0.5.5: регионы/порты в карточке Игры, DNS-карточка в Твиках, один список резолверов на программу); живой замер для графика идёт из статуса оптимизации"),
        ("Чтение состояния Windows и сети (реестр, netsh, электропитание, DNS)", 3, 100,
         "читатель собран (SystemStateReader: реестр, netsh, схема питания, DNS, NDIS-фильтры) и отдаётся "
         "службой командой system-state; чтение настоящей Windows проверяется в CI утилитой EngineSmoke"),
        ("Служба Windows, права, канал связи с интерфейсом", 8, 78,
         "протокол канала, таблица команд, разборщик, правила доступа и «мозги» службы собраны и покрыты "
         "тестами; установка, запуск и живой разговор со службой проверяются на Windows в CI; журналы с ротацией 5 МБ×3, "
         "отчёт о падении в ProgramData\\PingForge\\crash и пакет для поддержки (задача Q) готовы и проверены самопроверкой"),
        ("Интерфейс WPF (этот макет — его будущий вид)", 9, 92,
         "окно с 6 разделами, карточки твиков, регионы/DNS (0.5.5), UDP-наблюдатель ETW, трей, диалоги и файл настроек написаны и собираются; решения интерфейса "
         "покрыты тестами моделей представления; на живой Windows проверено при каждой сборке: окно открывается, "
         "6 разделов на месте, привязки работают, раскладка не сжимается, ничего не выходит за правый край "
         "окна, тексты переносятся, значок в трее "
         "создаётся, снимок окна кладётся в артефакты; регионы/DNS и честный блок «Пакет для поддержки» проверены самопроверкой"),
        ("Проверка на живом железе, установщик, подпись EV", 4, 60,
         "установщик собран и в CI проверен: тихая установка, файлы, ярлыки, запись в списке программ, "
         "служба, удаление без следов; программа после установки запускается и находит службу; живая проверка UDP-игр и пакета поддержки — самое дорогое непроверенное место"),
    ]
    weight_sum = sum(w for _, w, _, _ in progress_rows)
    assert weight_sum == 100, f"сумма весов должна быть 100, а не {weight_sum}"
    total = sum(w * d for _, w, d, _ in progress_rows) / weight_sum
    progress = {
        "rows": [{"name": n, "weight": w, "done": d, "note": note} for n, w, d, note in progress_rows],
        "total": total,
    }

    # данные для вставок: справочник маршрутов, профили потребления, меню трея
    _ROUTES_DATA.clear()
    _ROUTES_DATA.update(data)
    _PERF_DATA.clear()
    _PERF_DATA.update(data)
    html_out = build(data, progress)
    html_out = insert_consumption(html_out)
    html_out = insert_routes(html_out)
    html_out = insert_network_chart(html_out, data)
    html_out = decorate(html_out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(html_out, encoding="utf-8")
    (out_path.parent / "catalog.json").write_text(
        json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")

    print(f"Готово: {out_path} ({len(html_out) // 1024} КБ)")
    print(f"Твиков в макете: {data['tweakCount']}")
    print(f"Готовность PingForge Lite: {total:.1f}%")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
