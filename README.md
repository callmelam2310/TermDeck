<p align="center">
  <img src="src/TermDeck/Assets/logo.png" width="96" alt="TermDeck logo">
</p>

<h1 align="center">TermDeck</h1>

A MobaXterm-style Windows app for running command-line tools per project. Add Windows executables or WSL
commands as tools, group them into collections, and run each in its own terminal tab. Every run — command,
exit code and full colored output — is saved in the project folder so you can reopen, search and report on it
later. It can also chain tools automatically and drive everything from an AI agent.

## Features

- **Per-project tools** — add Windows `.exe` or WSL commands, group them into collections, open each in its own
  terminal tab (right-click a tool → **Open in new tab** to run it in several terminals at once). Set a per-tool
  **Run as** account (WSL `-u`, or Windows logon).
- **Saved history** — every run's command, arguments, exit code and full colored output is stored under the
  project (`<project>\.termdeck\`). Reopen, re-run, copy or export any past run.
- **Search & report** — full-text search across every run's output and commands (Ctrl+Shift+F); export an
  HTML or Markdown report, filtered by tool/date.
- **Shell tabs** — open an interactive WSL / PowerShell / cmd shell in the project folder (Ctrl+T).
- **Scan / import** — discover executables in WSL (`~/go/bin`, pipx, cargo…) or a Windows folder and add them
  in bulk; import/export tools as JSON.
- **Autorun** — when a tool finishes, filter its output (grep / awk / sed / sort / uniq / wsl / ps…) and feed
  the result into the next tool, with placeholders like `{{line}}` and `{{file}}`. Design and dry-run rules in a
  visual editor; chains stop safely at a depth limit.
- **AI agent** — a chat tab that drives TermDeck through its own MCP server: it can list and run tools, read run
  output, search history and build autorun chains. Run/edit actions are confirmed in the UI (or auto-approved).
  Switch Claude model/provider with saved **profiles** (Settings → AI agent). Chats are saved per project.

## Install

Download from the [latest release](../../releases/latest):

- **`TermDeck-Setup-<version>.exe`** — installer (choose *Install for me only* if you don't have admin rights).
- **`TermDeck-<version>-portable-win-x64.zip`** — portable: unzip and run `TermDeck.exe`.

Requires Windows 10 1809+ or Windows 11 (x64). WSL is only needed for Linux tools.

The AI agent needs the [Claude Code CLI](https://docs.anthropic.com/en/docs/claude-code) installed and signed in
(`claude /login`), or a provider configured in Settings → AI agent. Files the agent creates at runtime stay in
`<project>\.termdeck\agent\scratch`.

## Build

Needs the .NET 10 SDK and [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`):

```powershell
powershell -ExecutionPolicy Bypass -File build-release.ps1
```

The installer and portable zip are written to `dist\release\`. (`bin/`, `obj/` and `dist/` are gitignored.)

## License

See [LICENSE](LICENSE).
