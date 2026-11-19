# DmxCtrl — DMX light console for cheap Bluetooth DMX boxes

**A Windows light console that drives your fixtures through a cheap Bluetooth
"USB-RDM-DMX" box — no DMX dongle, no cable to the PC, nothing to install.**

🇫🇷 Version française plus bas : [lire en français](#-version-française) · [README.fr.md](README.fr.md)

![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-blue)
![.NET](https://img.shields.io/badge/.NET-10%20WinForms-purple)
![Portable](https://img.shields.io/badge/exe-portable%2C%20single%20file-brightgreen)
![License](https://img.shields.io/badge/license-MIT-green)
![Contributions](https://img.shields.io/badge/fixture%20profiles-contributions%20welcome-orange)

<!--
  SCREENSHOTS — to add from the Windows PC: put the PNG files in docs/images/ and
  uncomment this block.

<p align="center">
  <img src="docs/images/show-tab.png" width="49%" alt="Show tab">
  <img src="docs/images/sweep-tab.png" width="49%" alt="Sweep tab">
  <img src="docs/images/laser-tab.png" width="49%" alt="Laser tab">
  <img src="docs/images/fog-tab.png" width="49%" alt="Smoke tab">
</p>
-->

DmxCtrl talks to the Bluetooth DMX boxes sold with the **RY-AI** Android app (Jieli
chip, `JL_SPP` serial port), using the protocol documented in
[JL_SPP-ProtocolLib-DMX](https://github.com/Tom-Rfid/JL_SPP-ProtocolLib-DMX). Pair the
box, pick its COM port, and play your show from the PC.

## Features

**Show tab** — everything for a party in one place: colours, movements, chaser,
strobe, master, blackout, flash, tap tempo, scenes and auto mode. The **microphone**
can set the tempo and make the dimmer follow the music volume.

**Sweep tab** — moving heads (and the Spider wings) sweep left / right together, from a
pad or automatically (delay, BPM or microphone tempo). Crossed mode depending on each
fixture's place on stage (left / centre / right), mirrored Spider, shared or separate
dimmers.

**One tab per fixture**, generated from its profile:

| Fixture type | What the tab gives you |
|---|---|
| Moving head | pan/tilt pad, one control per channel with the presets of its profile (colours, gobos…) |
| Spider | wings pad, colour mask, left/right effect |
| LED PAR | A/B/C groups, ALL OFF, tempo effect |
| Laser | 32 drawn patterns, colours by type (fixed, alternating, scrolling), zoom, X/Y flip |
| Smoke machine | hold, 0.5–5 s burst, auto mode (one shot every 1–30 min), estimated heat-up, "all machines" controls |

**And also**

- **Patch**: free addresses suggested automatically, fixtures that can be turned off,
  place on stage, address check.
- **Profile editor**: describe any DMX fixture channel by channel, import / export it
  as a `.dmxprofile.json` file.
- **Debug tab**: raw faders, frame monitor, send rate.
- Option to send **only on change** (+ keep-alive frame).
- **Automatic Bluetooth reconnection** every 3 s if the link drops.
- **English and French** interface (Settings > Langue / Language).

## Download and run

1. Build the portable exe with `publish.cmd` (see below), or use a `DmxController.exe`
   someone built for you. It is a single 52 MB file with .NET included: **nothing to install**. If SmartScreen
   warns, click *More info* > *Run anyway*.
2. Pair the Bluetooth box in Windows (Settings > Bluetooth, device `RY-…`).
3. Start DmxController, pick the box's **outgoing** COM port ("JL_SPP") in the
   connection bar, then **Connect**.
4. *File > Open / import* one of the example configurations from the `Examples`
   folder, then check the addresses in *Fixtures > Patch / addressing…* against the
   menus of your fixtures.

Your configurations are saved in `%APPDATA%\DmxController\Configurations`.

## Supported fixtures

Built-in models (pick them in the patch, the channel count and a free address are
filled in for you):

| Fixture | Channels | Example address |
|---|---|---|
| Chauvet DJ Intimidator Spot 260X (×2) | 14 (8-channel mode available) | 1 and 15 |
| Mindi Mini Spider 8x3W RGBW | 15 | 29 |
| U'King LED PAR 36 UV (ZQ01087) | 7 | 44 |
| Showtec Galactic RGY-140 MKII laser (red/green version available) | 10 | 51 |
| Ayra WSM02 MKIII smoke machine | 1 | 61 |

Any other DMX fixture can be added with the **profile editor**, without writing code.

> ⚠ **Galactic laser:** the "Sound red" program (value 195 on channel 1) puts the
> laser in "tst" test mode, where it stops listening to DMX. To leave it: FUNC → DMX
> address → ENTER on the laser, or switch it off and on. Keep the zoom at 60 or more
> (60 by default), below that the pattern gets distorted.
>
> **Smoke machine:** DMX gives no feedback, so the "ready" state shown is an estimate.

## Add your fixture — contributions welcome!

**Everybody is welcome to add a fixture.** Two ways, from the simplest:

1. **A profile file (no code).** *Fixtures > Profile editor (modules)…*: create a
   profile, one row per DMX channel in the fixture order, give each channel its
   **role** (dimmer, pan, red, gobo, strobe, fog, laser pattern…). The role is what
   lets the Show tab, the sweeps and the fixture tab drive it automatically. Mark the
   channels you tested as *Verified*, then **Export…** the profile to a
   `.dmxprofile.json` file and open a pull request adding it to `examples/`
   (or attach it to an issue).
2. **A built-in model (C#).** Add a method to `Core/Model/FixtureLibrary.cs` next to
   the existing ones (`MiniSpider()`, `AyraWsm02()`, `GalacticRgy140()`…), register it
   in `All()`, and add a test in `Tests/`. Texts are written in French, they are the
   keys of the English table `Core/Localization/Tr.En.cs`: add the English line too.

Please say which fixture you tested with and which channels are still unverified.
Fixture charts that would also be useful for other programs are welcome in
[JL_SPP-ProtocolLib-DMX](https://github.com/Tom-Rfid/JL_SPP-ProtocolLib-DMX) as
library modules.

## Build from source

Requirements: Windows, [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bat
dotnet build DmxController.slnx
dotnet test Tests/DmxController.Tests.csproj
dotnet run --project App
```

Portable exe: run `publish.cmd`. It produces `publish\win-x64\DmxController.exe`
(single file, .NET included) with `README.txt` and the `Examples` folder next to it.

### Code layout

```
Core/            no WinForms, reusable (Mac / Linux port possible)
  Protocol/      scrambling, 521-byte frame, serial link with a 10 Hz send thread
  Model/         profiles, built-in fixture library, patch, scenes, colours
  Engine/        ShowEngine: computes the universe on every frame
  Audio/         beat detection (microphone tempo and volume)
  Localization/  translations (French keys, English table in Tr.En.cs)
  Storage/       configuration and profile files
App/UI/          WinForms interface (Show, Sweep, fixture tabs, laser, smoke, patch, debug)
Tests/           xUnit tests (protocol, engine, configuration, localisation)
examples/        example configurations shipped with the exe
```

Code comments are in English.

## Roadmap ideas

- Make the laser react to the microphone tempo.
- Drive a non-DMX smoke machine through a DMX relay on its remote button.
- RDM discovery, Mac port of `Core`, MIDI controller.

## Related project

[**JL_SPP-ProtocolLib-DMX**](https://github.com/Tom-Rfid/JL_SPP-ProtocolLib-DMX) — the
protocol specification and libraries (Python, C, C#, Android) behind DmxCtrl.

## License

[MIT](LICENSE): free to use, modify and redistribute, including in commercial
products, as long as the copyright notice and the licence text are kept. Provided
without any warranty.

## Disclaimer

Independent project, not affiliated with the box manufacturer, Jieli, or the fixture
brands mentioned. Lasers and smoke machines need care: follow their safety
instructions.

---

## 🇫🇷 Version française

**Une console de lumière Windows qui pilote vos projecteurs via un boîtier Bluetooth
« USB-RDM-DMX » bon marché — sans interface DMX USB, sans câble jusqu'au PC, rien à
installer.**

Onglet Soirée (couleurs, mouvements, chenillard, strobe, scènes, tempo au micro),
balayages des lyres, un onglet par appareil (lyres, Spider, PAR, laser, fumée), patch
avec adresses libres proposées, éditeur de profils pour ajouter n'importe quel
appareil sans coder. **Tout le monde est le bienvenu pour ajouter le profil de son
appareil.**

👉 **[Lire le README complet en français](README.fr.md)**
