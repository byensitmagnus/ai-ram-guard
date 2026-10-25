<p align="center">
  <img src="assets/banner.png" alt="AI RAM Guard" width="100%">
</p>

<p align="center"><a href="README.md">English</a> · <b>Dansk</b></p>

# AI RAM Guard: når Claude Code, Codex og MCP-servere æder din RAM

**AI RAM Guard** er et gratis open source-program til Windows. Det viser, hvor meget RAM dine AI-værktøjer faktisk bruger. Det finder **dublerede og forældreløse MCP-servere** og giver hukommelsen tilbage, uden at lukke det, du arbejder i.

> Det startede med en gaming-PC med 32 GB RAM, der løb tør midt i et spil. Synderen var ikke spillet. Det var **36 kopier af én MCP-server** og **25 af en anden**, startet af AI-sessioner i baggrunden.

## Hvorfor sker det?

Hver AI-session (en Claude Code-chat, en Codex-tråd, et Cursor-vindue) **starter sin egen kopi af alle dine MCP-servere**. Hver kopi er en Node.js- eller Python-proces på 100–250 MB. Ti sessioner med fem MCP-servere giver 50 processer.

## Det gør AI RAM Guard

- 🔍 Viser RAM pr. AI-program og navngiver hver MCP-server, også dem med mange kopier.
- 🧹 Lukker MCP-servere, hvis AI-session allerede er lukket.
- 🪶 Flytter ubrugt AI-hukommelse til swapfilen (pagefile), når der er for lidt fri RAM. Programmerne kører videre.
- 🎮 Spiltilstand: strammere grænse, mens et spil kører i fuld skærm.
- 🖥️ Lille tray-program (~30 MB) og et kommandolinjeværktøj (`airg`).

## Kom i gang

Hent `AiRamGuard-win-x64.zip` under [Releases](https://github.com/byensitmagnus/ai-ram-guard/releases/latest), pak den ud og start `AiRamGuard.exe`. Højreklik på ikonet → **Start with Windows**.

```text
airg scan     se RAM pr. AI-program
airg clean    ryd op nu
```

## Om

Lavet af **Magnus Steinmeier Olsen**, [Byens IT](https://byens-it.dk). Vi bygger gaming-PC'er og bruger selv mange AI-agenter. Værktøjet kom ud af at rette vores egne maskiner.

Har det sparet dig RAM? Giv repoet en ⭐. Så kan andre finde det.

MIT © 2026 Magnus Steinmeier Olsen / Byens IT
