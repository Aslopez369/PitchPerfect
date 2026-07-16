# PitchPerfect · Audio Pitch Controller

> A Windows desktop utility that shifts the pitch of application audio in real time (-12 key ~ +12 key). Supports **Global mode** (shift the pitch of everything) and **Per-App mode** (shift the pitch of a single chosen application only).

---

## 1. What it does

- 🎚️ **Real-time pitch shifting**: range **-12 key ~ +12 key**, adjustable in semitone steps. Changes apply instantly while dragging — no app restart needed.
- 🌐 **Global mode**: routes the system default playback device to VB-Cable so that *all* playing audio (music, video, games, voice) is pitch-shifted together.
- 🎯 **Per-App mode**: pitch-shifts only the selected application; all other audio is unaffected.
- 📋 **Auto session scan**: one click lists currently playing / running applications with icons and process names for precise selection.
- 📊 **Live status**: the bottom bar shows whether VB-Cable is ready, the current output device, and processing latency (ms).
- ♻️ **Safe restore**: after stopping, the system default playback device is restored automatically — no leftover silence or audio loops.

---

## 2. How it works (why VB-Cable is required)

Windows' public audio APIs **do not support capturing audio per-process**, so PitchPerfect uses a virtual audio cable, **VB-Cable**, as a relay:

```
Target app / System default  ──▶  VB-Cable Input (virtual speaker)
                                     │
                                     ▼   captured by PitchPerfect (CABLE Output)
                               ┌──────────────┐
                               │  SoundTouch  │  real-time pitch shift (no speed change)
                               └──────────────┘
                                     │
                                     ▼   output to real speakers (what you hear)
```

- **Global mode**: the app automatically sets the system default render device to `VB-Cable Input`, captures `CABLE Output`, shifts the pitch, and writes it back to the real speakers; on stop it restores the default.
- **Per-App mode**: the system default is left untouched. You manually point the target app's output to `VB-Cable Input`; PitchPerfect captures and shifts it, then outputs to the real speakers — so only that one app is affected.

> ⚠️ **VB-Cable is a required dependency.** When it is not installed, the app shows a warning and disables the Start button. It is a free virtual audio driver (VB-Audio Virtual Cable).

---

## 3. Requirements

| Item | Requirement |
|------|-------------|
| OS | Windows 10 / 11 (64-bit) |
| Runtime | If using the .NET-installed build, install the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0); the self-contained exe needs nothing |
| Required component | **VB-Audio Virtual Cable** (free, [download](https://vb-audio.com/Cable/)) |
| Privileges | Standard user is enough (`asInvoker`) — no admin needed |

---

## 4. Install & Run

### Option A: Use the packaged exe (recommended)
If you already have `PitchPerfect.exe` (self-contained single file), just double-click to run — **no .NET install required**.

### Option B: Build from source
```bash
# 1. Install VB-Cable (see link above); confirm "CABLE Input" / "CABLE Output" appear in playback devices.

# 2. Enter the project directory
cd PitchPerfect

# 3. Restore dependencies and build
dotnet build -c Release

# 4. Run (debug)
dotnet run -c Release
```

### Package as a single-file exe (for distribution)
```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
# Output: bin/Release/net8.0-windows/win-x64/publish/PitchPerfect.exe
```

---

## 5. Usage

### Step 1: Confirm VB-Cable is ready
After launching, check the **bottom-left status light**:
- 🟢 Green = VB-Cable detected, you can start.
- 🔴 Red = not detected. Install / reinstall VB-Cable and restart the app.

### Mode 1: Global pitch shift (all audio)
1. Select **「全局模式」 (Global mode)** at the top.
2. Drag the center slider to set the pitch (-12 ~ +12, 0 = original).
3. Click **▶ 开始变调 (Start)** — the system default device switches to VB-Cable and all audio starts shifting.
4. You can drag the slider in real time while processing.
5. Click **■ 停止变调 (Stop)** — the system default playback device is restored automatically.

> Tip: before starting, make sure your normal playback device (e.g. "Speakers") is actually producing sound for the most immediate effect.

### Mode 2: Per-App pitch shift (one app only)
1. Select **「单应用模式」 (Per-App mode)** — the app scans the session list automatically once.
2. If the list is empty, click **🔄 刷新列表 (Refresh)** (it helps if the target app is already playing audio before refreshing).
3. Find the target app in the list, drag its pitch slider, and check **「变调」 (Shift)**.
4. **Critical manual step**: because Windows cannot route audio per-process automatically, you must point the target app's output to VB-Cable yourself:
   - Open Windows **Settings → System → Sound → More sound settings** (or right-click the taskbar speaker → Sound settings).
   - Go to **"App volume and device preferences"**.
   - Find the target app and set its **Output** device to **`CABLE Input (VB-Audio Virtual Cable)`**.
   - Its audio now flows through VB-Cable into PitchPerfect, gets shifted, and outputs to your speakers — other apps are unaffected.
5. Unchecking **「变调」** stops processing for that app.

> 💡 Per-App mode processes only one app at a time; selecting a new app automatically disables the previous one.

---

## 6. FAQ

**Q: No sound after starting?**
- Make sure the normal playback device is actually playing. Check the bottom-left light is green (VB-Cable ready).
- Global mode: the "no sound on restart" issue was fixed in the latest build — use the most recent version.

**Q: Per-App checked but pitch doesn't change?**
- You most likely skipped the manual routing step above — the target app's output must be set to `CABLE Input`, otherwise its audio never passes through PitchPerfect.

**Q: Why does global mode affect all sound? Is that a bug?**
- No. Global mode is designed to take over the system default device and shift everything. Use Per-App mode if you only want one app changed.

**Q: What if I rename my real speaker to include "CABLE"?**
- Older versions could mis-detect it. The latest build uses **exact device-ID matching** to exclude VB-Cable, so renaming won't hurt a real device.

**Q: Do I need administrator rights?**
- No. Standard user is enough.

---

## 7. Tech stack & project structure

**Tech stack**
- C# / WPF (.NET 8, Windows Desktop)
- [NAudio](https://github.com/naudio/NAudio) 2.2.1 — Windows Core Audio device enumeration, capture & playback
- [SoundTouch.Net](https://github.com/BlindCoder/soundtouch-net) 2.3.2 — real-time pitch shifting (SoundTouch: tempo unchanged when pitch changes, pitch unchanged when tempo changes)
- [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/) 8.3.2 — MVVM bindings & commands
- [System.Drawing.Common](https://www.nuget.org/packages/System.Drawing.Common/) 8.0.0 — app icon extraction

**Main directories**
```
PitchPerfect/
├── App.xaml / App.xaml.cs        # Entry point & dependency injection
├── MainWindow.xaml(.cs)          # Main UI (mode switch / sliders / list)
├── ViewModels/
│   ├── MainViewModel.cs          # Main VM: modes, commands, status
│   └── AudioSessionViewModel.cs  # Per-app session item VM
├── Services/
│   ├── AudioProcessingService.cs # Core: pitch pipeline & device routing (global / per-app)
│   ├── AudioSessionService.cs    # Active audio session enumeration
│   └── PolicyConfigService.cs    # System default device switching (COM)
├── Audio/
│   ├── AudioPipeline.cs          # WASAPI capture → SoundTouch → output
│   └── SoundTouchProcessor.cs    # SoundTouch wrapper
├── Utils/
│   └── VBCableDetector.cs        # VB-Cable device detection
├── Models/                       # Data models
├── Converters/                   # XAML value converters (icon / visibility)
└── app.manifest                  # Manifest (asInvoker, DPI aware)
```

---

## 8. UI Overview

> Note: these are layout diagrams, not real screenshots (the build environment has no display). Run the app to see the actual window.

### Global mode
```
┌──────────────────────────────────────────────────────────────┐
│ 🎵  PitchPerfect - 音频变调控制器                                │
├──────────────────────────────────────────────────────────────┤
│  (●) 全局模式     ( ) 单应用模式                                 │
├──────────────────────────────────────────────────────────────┤
│                                                              │
│                    音调控制                                     │
│                                                              │
│                   +3 key                                     │
│   ──────────────────────────────────────────────            │
│   -12                              0                      +12 │
│                                                              │
│        [ ▶ 开始变调 ]        [ ■ 停止变调 ]                    │
│                                                              │
│                 🟢 正在处理中                                  │
│                                                              │
│   ┌─ VB-Cable warning box (shown only if not installed) ─┐   │
│   │ VB-Cable not detected. Please install VB-Cable...   │   │
│   └────────────────────────────────────────────────────┘   │
├──────────────────────────────────────────────────────────────┤
│ 🟢 VB-Cable detected   设备: 扬声器    延迟: 12 ms             │
└──────────────────────────────────────────────────────────────┘
```

### Per-App mode
```
┌──────────────────────────────────────────────────────────────┐
│ 🎵  PitchPerfect - 音频变调控制器                                │
├──────────────────────────────────────────────────────────────┤
│  ( ) 全局模式     (●) 单应用模式                                 │
├──────────────────────────────────────────────────────────────┤
│ 正在运行的应用程序              [ 🔄 刷新列表 ]                 │
│ ┌────────────────────────────────────────────────────────┐  │
│ │ [🎵] Spotify        [─────●────] 0 key   [✔] 变调 🟢   │  │
│ │ [🎵] Edge           [───────●──] +2 key  [ ] 变调 ⚪   │  │
│ │ [🎵] 网易云音乐      [──●───────] -5 key  [ ] 变调 ⚪   │  │
│ │   (icon, display name, process name, pitch slider,     │  │
│ │    value, "变调" checkbox + status dot per row)         │  │
│ └────────────────────────────────────────────────────────┘  │
├──────────────────────────────────────────────────────────────┤
│ 🟢 VB-Cable detected   设备: 扬声器    延迟: —                  │
└──────────────────────────────────────────────────────────────┘
```

**Window parts**
- **Title bar** — app name and logo.
- **Mode switcher** — radio buttons to switch between Global and Per-App. Switching modes stops any active processing.
- **Global panel** — large pitch readout, slider (-12 ~ +12), Start/Stop buttons, processing indicator, and a VB-Cable warning box (only when not installed).
- **Per-App panel** — refresh button + a scrollable list of running apps. Each row has the app icon, name/process, its own pitch slider + value, and a "变调" toggle with a status dot.
- **Bottom status bar** — VB-Cable readiness light + text, current device, and latency.

---

## 9. License

This project is intended for personal / demo use. Third-party libraries such as SoundTouch and NAudio follow their own licenses (LGPL / MIT, etc.). Verify compliance before any commercial use.
