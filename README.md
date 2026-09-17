# XM Control

Unofficial Windows controller for Sony 1000X headphones and earbuds.

![XM Control screenshot](docs/screenshot.png)

## Supported Devices

- WH-1000XM5
- WF-1000XM5
- WH-1000XM6
- WF-1000XM6

## Features

- Automatic device detection
- Noise cancelling, ambient sound, and off modes
- Ambient sound level control
- Voice (focus on voice) ambient mode
- Auto ambient sound with Low/Standard/High sensitivity (WF-1000XM6)
- Equalizer presets and live EQ band control, 6-band or 10-band per device
- Clear Bass control (6-band models)
- DSEE Extreme Auto/Off
- Bluetooth connection quality mode
- Active codec display
- Live sound pressure reading
- Multipoint toggle
- Speak-to-Chat toggle
- Wearing sensor pause toggle
- Touch sensor panel toggle
- Automatic power off setting
- Tray quick actions
- Configurable global keyboard shortcuts

## Download

Download the latest release zip from the Releases page, extract it, and run:

```text
ui\xm5ui.exe
```

Pair and connect the headphones in Windows Bluetooth settings first.

## Build

Requirements:

- Windows 10 or Windows 11
- Visual Studio C++ Build Tools for the native Bluetooth backend
- .NET Framework 4.x compiler, or Visual Studio Build Tools with C# support

Build everything and create a release zip:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\build-release.ps1 -Version v1.0
```

Build manually:

```bat
cd src\c
build.bat

cd ..\ui
build-ui.bat
```

The UI expects the backend at `..\c\xm5ctl.exe`, so keep the `c` and `ui` folders together.

## Notes

This project is not affiliated with Sony.

Noise control uses the MDR `NCASM` command family. Older models answer inquired
type `0x17` with five parameter bytes; the WF-1000XM6 answers type `0x19` with
seven, adding the auto ambient toggle and its sensitivity. The backend asks for
`0x19` first and falls back to `0x17`, and every change is applied as a
read-modify-write so that setting one field does not clear the others.

Battery is reported per inquired type: `22 00` is a single level, `22 01` is the
two earbuds, and `22 02` is the case. On the WF-1000XM6 the case level is only
current while an earbud is docked, and an earbud in the case stops reporting, so
its level freezes at the last value the connected side saw. Neither the case nor
the earbuds expose a usable charging state on that model, so the case level is
reported without one.

The active codec comes from the device information blob returned by `12 00`,
which reports it as a letter: `S` is SBC, `A` is AAC and `L` is LDAC. It follows
whichever connection last played audio rather than a fixed link, so on a
multipoint setup it changes as audio moves between devices, and it keeps the
last value once playback stops.

The equalizer band layout is read from the device rather than assumed. `5A 00`
returns `5B 00 <count>` followed by one `01 <freq16>` group per band: six bands
on the WH/WF-1000XM5, ten on the WF-1000XM6. Six-band devices keep the Clear
Bass shelf and a 0-20 range centred on 10; ten-band devices have no Clear Bass
and run 0-12 centred on 6. The capability reply carries no range or step field,
so the range is keyed off the band count. The headset stores out-of-range values
verbatim rather than rejecting them, so values are clamped before being sent.

Presets are selected with inquired type `04` (`58 04 <preset> 00`) — the
preset-only form `58 00 <preset> 00` used by six-band devices draws no response
on the WF-1000XM6. Writing band values with `58 00` always coerces the preset to
Manual, so editing a fixed preset moves the curve into Manual, while Manual and
the two custom slots keep whatever is dialled into them.

Preset ids differ by model. The WF-1000XM6 does not answer the `0x10` block the
older models use, and offers these instead:

```text
Off 0x00   Heavy 0x30   Clear 0x31   Hard 0x32   Soft 0x33
Game 0x20  Manual 0xA0  Custom 1 0xA1   Custom 2 0xA2
```

Sound pressure is the level of the audio playing through the headset,
measured after the volume stage and ignoring noise cancelling. It is read
with `5A 03` on `DATA_MDR_NO2` rather than `DATA_MDR`, which is why it is
polled on its own rather than in the state batch. The reply is
`5B 03 <dB> <status>`, where the level is a plain unsigned byte with no
scaling and status `03` marks a real reading. Any other status means the
headset has no level to report, which is what it answers while nothing is
playing and for the first few seconds after playback starts, so it is shown
as a dash rather than 0 dB. A dropped link is shown differently again: the last
reading stays on screen and dims, because losing the link says nothing about
whether anything is playing.

The reading only works while Safe Listening is switched on in Sony's Sound
Connect app. With it off the headset does not stop answering: it repeats one
frozen level and still marks it valid, so the number alone cannot be told from a
live one. The setting is read separately with `52 03`, whose reply carries two
bytes that are both zero while it is off, and the meter shows `Off` rather than a
level that is not moving. It is asked alongside every fifth reading, so
switching Safe Listening back on is picked up within a few seconds.

The headset does not push the value, so it has to be asked, and asking once per
reading does not work: opening the control channel can take longer than the
interval worth asking for, as described below. One backend run therefore streams
readings for as long as the meter is on screen instead of reconnecting for each
one. Only one program can hold that channel at a time, so any command that talks
to the headset stops the stream first and it resumes afterwards.

Useful when adding support for a new model:

```text
xm5ctl ncasm                 show the current noise control state
xm5ctl listen --hex          dump notification frames as the headset sends them
xm5ctl raw "66 19" --hex     send one payload and print the raw reply bytes
xm5ctl soundpressure --samples 10
                             read the sound pressure meter ten times
```

The app has no Bluetooth code of its own: every action runs `xm5ctl` once and
reads the result back from its output. Opening the headset's control channel is
the slow part of that, and how slow depends on what else is connected to the
headset — roughly 280 ms when the PC is its only link, and 1.8 s or more when a
phone is connected as well.

Waiting for a reply is the opposite: once the channel is open, an answer arrives
in milliseconds. The two therefore have separate budgets. `--connect-timeout`
covers opening the channel and defaults to 5000 ms; `--timeout` covers waiting
for an acknowledgement and a reply and defaults to 900 ms. A single shared value
cannot serve both — set low enough to keep the reply wait short it fails to
connect at all, and set high enough to connect it makes every unanswered command
wait that long. Commands the headset does not answer are normal rather than
exceptional, because a batch sweeps inquired types that not every model
implements.

Bluetooth control depends on Windows being able to open the headset's RFCOMM service. If commands fail, make sure the device is paired, connected, and selected as an audio device in Windows.
