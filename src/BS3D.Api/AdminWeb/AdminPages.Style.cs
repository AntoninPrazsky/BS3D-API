namespace BS3D.Api.AdminWeb;

/// <summary>
/// The admin page's one style sheet, served as /style.css: the CSP allows no inline style and no font from anywhere, so
/// the faces are the system's own. A centred column, panels for tables, light and dark after the system's setting, and
/// on a narrow screen the tabs on a row of their own and every table scrolling inside its panel.
/// </summary>
public static partial class AdminPages
{
    public const string Css = """
        :root {
          color-scheme: light dark;
          --bg: #f3f5f8; --surface: #ffffff; --raised: #f7f9fb; --fg: #17212c; --muted: #5d6977; --faint: #8994a1;
          --line: #e2e7ec; --line-strong: #cdd5de; --accent: #1b6bd6; --accent-soft: #e7f0fd;
          --ok: #17784c; --ok-soft: #e2f3e9; --bad: #c0352b; --bad-soft: #fcebea;
          --sans: system-ui, -apple-system, "Segoe UI", Roboto, "Noto Sans", Cantarell, "Helvetica Neue", Arial, sans-serif;
          --mono: ui-monospace, "SF Mono", "Cascadia Mono", "JetBrains Mono", "DejaVu Sans Mono", "Liberation Mono", Menlo, Consolas, monospace;
          --radius: 10px; --shadow: 0 1px 2px rgb(15 23 42 / .05), 0 2px 8px rgb(15 23 42 / .04);
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --bg: #0e1218; --surface: #151a22; --raised: #1b222c; --fg: #e3e8ee; --muted: #97a3b1; --faint: #6b7785;
            --line: #252d38; --line-strong: #344050; --accent: #6eabff; --accent-soft: #172a46;
            --ok: #62d293; --ok-soft: #13291e; --bad: #ff847b; --bad-soft: #321a1b; --shadow: none;
          }
        }
        *, *::before, *::after { box-sizing: border-box; }
        html { -webkit-text-size-adjust: 100%; }
        body { margin: 0; min-height: 100vh; display: flex; flex-direction: column; font: 15px/1.5 var(--sans); color: var(--fg); background: var(--bg); }
        a { color: var(--accent); text-decoration: none; }
        a:hover { text-decoration: underline; text-underline-offset: 2px; }
        a:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; border-radius: 4px; }
        code { font-family: var(--mono); font-size: .84em; }

        header.top { position: sticky; top: 0; z-index: 10; background: var(--surface); border-bottom: 1px solid var(--line); }
        .bar, main { width: 100%; max-width: 76rem; margin: 0 auto; padding-left: 1.5rem; padding-right: 1.5rem; }
        .bar { display: flex; flex-wrap: wrap; align-items: center; gap: .5rem 1.5rem; min-height: 3.5rem; padding-top: .5rem; padding-bottom: .5rem; }
        .brand { display: inline-flex; align-items: center; gap: .55rem; color: var(--fg); font-weight: 650; }
        .brand:hover { text-decoration: none; }
        .mark { font: 700 .7rem/1 var(--mono); letter-spacing: .06em; color: var(--surface); background: var(--fg); padding: .32rem .42rem; border-radius: 6px; }
        nav { display: flex; gap: .2rem; overflow-x: auto; scrollbar-width: none; }
        nav a { color: var(--muted); font-weight: 500; padding: .38rem .75rem; border-radius: 7px; white-space: nowrap; }
        nav a:hover { color: var(--fg); background: var(--raised); text-decoration: none; }
        nav a[aria-current] { color: var(--accent); background: var(--accent-soft); }
        .badge { margin-left: auto; display: inline-flex; align-items: center; gap: .45rem; font: .72rem/1 var(--mono); color: var(--muted); border: 1px solid var(--line-strong); border-radius: 999px; padding: .35rem .7rem; white-space: nowrap; }
        .badge::before { content: ""; width: .45rem; height: .45rem; border-radius: 50%; background: var(--ok); }

        main { flex: 1; padding-top: 2rem; padding-bottom: 3rem; }
        .crumb { display: inline-block; margin-bottom: .5rem; font: 600 .72rem/1 var(--mono); text-transform: uppercase; letter-spacing: .1em; color: var(--muted); }
        .crumb::after { content: " /"; color: var(--faint); }
        h1 { display: flex; flex-wrap: wrap; align-items: center; gap: .4rem .9rem; margin: 0 0 1.5rem; font-size: 1.75rem; line-height: 1.2; font-weight: 650; letter-spacing: -.015em; overflow-wrap: anywhere; }
        h2 { margin: 2.25rem 0 .75rem; font-size: .75rem; font-weight: 650; text-transform: uppercase; letter-spacing: .09em; color: var(--muted); }
        p { margin: 0 0 1rem; max-width: 62rem; }
        .note { color: var(--muted); font-size: .9rem; }
        .key { display: inline-block; padding: .2rem .5rem; border: 1px solid var(--line); border-radius: 6px; background: var(--surface); }
        .id { color: var(--faint); margin-left: .4rem; }

        .panel { background: var(--surface); border: 1px solid var(--line); border-radius: var(--radius); box-shadow: var(--shadow); overflow-x: auto; }
        .panel + .note { margin-top: .6rem; }
        table { width: 100%; border-collapse: collapse; font-size: .875rem; }
        th, td { padding: .6rem 1rem; text-align: left; vertical-align: baseline; white-space: nowrap; border-bottom: 1px solid var(--line); }
        th { background: var(--raised); color: var(--muted); font-size: .7rem; font-weight: 650; text-transform: uppercase; letter-spacing: .07em; }
        tr:last-child td { border-bottom: 0; }
        tr:hover td { background: var(--raised); }
        .n { text-align: right; font-variant-numeric: tabular-nums; }
        td.n, td.t { font-family: var(--mono); font-size: .82rem; }
        td.t { color: var(--muted); }
        .sorted { color: var(--fg); }
        .sorted::after { content: " \25BE"; }
        table.facts th { width: 14rem; background: transparent; color: var(--muted); font-size: .85rem; font-weight: 500; text-transform: none; letter-spacing: 0; }
        table.facts td { white-space: normal; }

        .stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(13.5rem, 1fr)); gap: 1rem; }
        .stat { display: flex; flex-direction: column; gap: .3rem; min-width: 0; padding: 1rem 1.15rem 1.1rem; background: var(--surface); border: 1px solid var(--line); border-radius: var(--radius); box-shadow: var(--shadow); }
        .stat span { font-size: .7rem; font-weight: 650; text-transform: uppercase; letter-spacing: .08em; color: var(--muted); }
        .stat strong { font: 600 1.6rem/1.2 var(--mono); letter-spacing: -.02em; font-variant-numeric: tabular-nums; }
        .stat small { color: var(--muted); font-size: .8rem; overflow-wrap: anywhere; }
        .cols { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 26rem), 1fr)); gap: 0 1.5rem; align-items: start; }

        .chip, .flag { display: inline-block; padding: .1rem .5rem; border-radius: 999px; font-size: .72rem; font-weight: 600; line-height: 1.45; letter-spacing: .02em; }
        .chip.ok { color: var(--ok); background: var(--ok-soft); }
        .chip.bad, .flag { color: var(--bad); background: var(--bad-soft); }
        .chip.muted { color: var(--muted); background: var(--raised); border: 1px solid var(--line); }
        .flag { margin-left: .4rem; }
        .warn { padding: .8rem 1rem; border: 1px solid var(--line); border-left: 3px solid var(--bad); border-radius: var(--radius); background: var(--bad-soft); font-weight: 500; }
        .live { display: inline-flex; align-items: center; gap: .45rem; padding: .38rem .7rem; border-radius: 999px; font: 500 .72rem/1 var(--mono); color: var(--ok); background: var(--ok-soft); letter-spacing: 0; }
        .live::before { content: ""; width: .5rem; height: .5rem; border-radius: 50%; background: currentColor; animation: pulse 1.6s ease-in-out infinite; }
        @keyframes pulse { 50% { opacity: .25; } }
        @media (prefers-reduced-motion: reduce) { .live::before { animation: none; } }

        footer { padding: 1.25rem 1rem 1.75rem; border-top: 1px solid var(--line); color: var(--faint); font: .74rem/1.5 var(--mono); text-align: center; }

        @media (max-width: 44rem) {
          .bar, main { padding-left: 1rem; padding-right: 1rem; }
          .bar { gap: .35rem .75rem; }
          nav { order: 3; width: 100%; }
          main { padding-top: 1.4rem; }
          h1 { font-size: 1.4rem; margin-bottom: 1.1rem; }
          h2 { margin-top: 1.75rem; }
          th, td { padding: .5rem .7rem; }
          .stats { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: .65rem; }
          .stat { padding: .8rem .9rem; }
          .stat strong { font-size: 1.25rem; }
          table.facts th, table.facts td { display: block; width: auto; }
          table.facts th { padding-bottom: 0; border-bottom: 0; }
        }
        """;
}
