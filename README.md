# Lyricod for Windows

English | [简体中文](README.zh-CN.md)

Synced, word-by-word lyrics for Windows: in a pill at the top of your screen, floating over your desktop, or in their own window. A native app for x64 and ARM64 PCs, in English and 简体中文.

## What it shows

- **Lyricod Island**: a black pill at the top of the screen with the cover, the line being sung sweeping word by word in the album's colour, and six bars that move with the music. It fits each line, can show the translation, pronunciation or next line underneath, and splits long lines into timed pieces. Hover to expand it into a mini player, to hide it so what's underneath is usable, or to do nothing. It goes away a moment after the music stops and comes back when it plays.
- **Desktop lyrics**: a click-through strip over every app, one or two lines. Unlock it in Settings to move it.
- **Lyrics window**: the whole song scrolling over the album art. Click a line to jump to it. In a wide window or full screen (F11) the cover and controls sit beside the lyrics, and the text grows with the window; the controls fade while the pointer rests.
- **Tray panel**: click Lyricod's icon in the notification area for the song with its controls, switches for the island and desktop lyrics, and this song's lyrics and timing.
- **Pronunciation**: romaji, romanised Korean or pinyin under each line. Japanese uses the lyrics source's own romaji, or Windows' Japanese reader when the Japanese input method is installed.
- **Global shortcuts**: play/pause, skip, show or hide each view, and nudge the current song's timing, from any app. None are set until you record them in Settings › Shortcuts.

## Players

Lyricod follows any player that shows up in Windows' media controls (the media panel above the volume slider): Spotify, QQ Music, NetEase Cloud Music, Apple Music, browsers and more. It follows the one that's playing, or the one you choose in Settings › General.

Most players tell Windows where they are in the song, so the lyrics stay in step even when you skip around. For one that doesn't, Lyricod times the song from when it starts. NetEase Cloud Music on its own doesn't report its position; with the InfLink-rs plugin for BetterNCM, with its SMTC option on, it does, and hands over the full-size cover too.

## Lyrics

From **NetEase Cloud Music**, **QQ Music** (including its word-by-word lyrics and both sources' translations and romaji) and **[LRCLIB](https://lrclib.net)**. Results are saved on your PC.

When a song gets the wrong lyrics, open **Choose Lyrics** (tray panel, lyrics window or Settings): it searches all three sources and lets you preview and pick a result. Lyricod remembers your choice and any timing adjustment for that song, even after you clear the saved lyrics.

## Settings

- **General**: open at login, animation frame rate, language, which player to follow
- **Desktop lyrics**: show and lock the strip, its position, text size, sung colour, what goes under the line
- **Lyricod Island**: show it, what hover does, album-colour lyrics, hide when paused, bars that follow the music, what goes under the line; style (size, maximum width, font weight, how sung lyrics and the next line appear)
- **Lyrics window**: background, text size, translations, pronunciation, centred lines, blur, sung-lyrics effect, auto-hiding controls
- **Lyrics**: the lyrics font, timing for all songs and the current one, pronunciation and its languages, songwriter credits, the current song's source, saved lyrics
- **Shortcuts**: global keys for playback, the three views and timing
- **About**: version, update checks, diagnostics to copy into a problem report, credits

## Privacy

- **Bars that follow the music** listen only to the player Lyricod is following, through Windows' audio APIs. Nothing is recorded, stored or sent. Turn it off in Settings › Lyricod Island.
- To find lyrics, Lyricod sends the song's title, artist and length to NetEase, QQ Music and LRCLIB.
- Once a day it asks GitHub whether there's a newer version. Turn that off in Settings › About.

## Requirements

- Windows 10 (version 1809) or later, x64 or ARM64
- Bars that follow the music need Windows 11 (they move on their own on Windows 10)

## Install

Download from this repository's Releases:

- **Lyricod-X.Y.Z-windows-x64-setup.exe** for most PCs (Intel and AMD), or **…-arm64-setup.exe** for Arm PCs (Snapdragon, Copilot+ PCs). It installs for you alone, with no administrator prompt, and adds Lyricod to the Start menu.
- **Lyricod-X.Y.Z-windows-x64.zip** / **…-arm64.zip** to run it without installing: unzip it anywhere and run `Lyricod.exe` in the Lyricod folder, keeping the `app` folder next to it.

Lyricod isn't signed yet, so Windows may warn the first time: choose **More info › Run anyway**. Lyricod lives in the notification area.

To update, run the new installer, or quit Lyricod and replace the whole Lyricod folder. Your settings and saved lyrics, in `%LOCALAPPDATA%\Lyricod`, are kept either way. Uninstall from Settings › Apps; that folder stays, so delete it too if you want everything gone.

## Font

Lyrics use Microsoft YaHei by default. In Settings › Lyrics you can choose any font installed on your PC, or MiSans by Xiaomi, which is recommended: one design for Latin, Chinese and Japanese, so a line and its translation match. Lyricod doesn't include MiSans; choosing it downloads it from Xiaomi (about 15 MB) under Xiaomi's licence.

## Credits

Inspired by [Lyricify](https://github.com/WXRIW/Lyricify-App); Lyricod Island is inspired by its Dynamic Lyrics Island, a design by WXRIW licensed under CC BY-SA 4.0. QQ Music's word-by-word lyrics are decrypted with code ported from [Lyricify Lyrics Helper](https://github.com/WXRIW/Lyricify-Lyrics-Helper) (Apache License 2.0). Pinyin comes from [ToolGood.Words](https://github.com/toolgood/ToolGood.Words) (Apache License 2.0); the tray icon and window helpers from [WinUIEx](https://github.com/dotMorten/WinUIEx) (MIT). Lyrics belong to their copyright holders. Lyricod is not affiliated with Microsoft, NetEase or Tencent.

## Licence

Lyricod for Windows is licensed under the [Apache License 2.0](LICENSE). The code and assets it uses from others, and their licences, are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
