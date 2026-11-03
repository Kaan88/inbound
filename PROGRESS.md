# Progress — session handoff

## 2026-11-04 — Session 1: baseline and setup

### State

- Working directory: `C:\Users\kaank\Downloads\reference app` (new, not yet a git repo at session start).
- Goal/Todo tools: **unavailable** in OpenCode 2.2.5. Fallback tracking is
  `GOAL.md` + `ROADMAP.md`; parity is `reference notes`.
- GitHub: authenticated as `Kaan88`; private repo `Kaan88/download-manager` created.
- reference app reference pinned: **6.43.12.3** (`v6.43b12 Full`), SHA-256
  `B601350AF954D23DFD989295F9FB398AFCD972FEEC4F300A7CF1284CBB2B2C93`.
- disassembler: 12.1.4 + disassemblerMCP 2.0.0, headless server on `http://127.0.0.1:8081/mcp`
  with project `reference_project`. `get_program_info` confirms the loaded binary.
- .NET SDK 10.0.403; runtimes 8/9/10; Windows App Runtime 1.8/2.x installed;
  Node 26.11.1; Chrome 156.
- Reference extension extracted for analysis to a temp directory only:
  manifest 6.43.1, ID `ngpampappnmepgilojfohadhhmbhlaek`, native host
  `com.the vendor.reference app`. No proprietary files committed.

### Environment facts

| Tool | Result |
| --- | --- |
| `dotnet --list-sdks` | 10.0.403 |
| .NET runtimes | 6, 7, 8, 9, 10 (desktop included) |
| Windows App Runtime | 1.5–1.8 and 2.x present (x64) |
| Node / npm | v26.11.1 / 11.20.0 |
| Chrome | 156.0.8078.80 |
| disassembler | 12.1.4; headless MCP on port 8081 |
| GitHub CLI | authenticated `Kaan88`, repo scope present |
| 7-Zip | available |

### disassembler notes

`opencode mcp list` initially failed because no disassembler backend was running. A
headless disassemblerMCP server was started with:

```powershell
C:\Users\kaank\tools\disassembler-mcp\start-reference app-headless.cmd
```

OpenCode's MCP config now points at `http://127.0.0.1:8081/mcp`. The project
holds `/reference app.exe` (x86, image base `0x00400000`, 18,988 functions). Renames and
comments are saved to the project. Do not run a second headless session against
the same project (project lock).

### Decisions made

- One Host process owns engine + persistence + scheduler + tray; UI/CLI/native
  host are IPC clients.
- Named-pipe JSON protocol with current-user ACL and `protocolVersion`.
- Atomic per-job JSON persistence for M0/M1; migration layer from day one.
- Own Chrome extension identity; own JSON native-messaging protocol.
- No binary compatibility with reference app's `MSG#` protocol; no licensing code.

### Next actions

1. Finish P0.3 (push baseline), P0.6 (solution scaffold), P0.7 (test server).
2. Complete P0.4/P0.5 inventory of reference app UI and browser handoff.
3. Start Phase 1 single-stream engine + CLI.
4. Commit and push each coherent increment; update this file at checkpoints.

### Commands

```powershell
dotnet build DownloadManager.sln -c Debug
dotnet test DownloadManager.sln -c Debug
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

(Commands become valid as the scaffold lands.)

### Open questions / blockers

- NuGet connectivity needs verification (only a Visual Studio offline source was
  listed by `dotnet package search`); required for WinUI and test packages.
- Windows App SDK version to pin for `net10.0-windows`; verify by building.
- Chrome extension loading requires manual “Load unpacked” in this environment;
  installer will document and register the native host.
