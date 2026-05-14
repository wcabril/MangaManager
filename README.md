# 📚 Manga Manager

A Windows desktop application built with **C# and WPF** to automate the organization, extraction, metadata generation, image processing, and Kindle conversion of manga CBZ files.

---

## ✨ Features

- 🔍 **Fetch Author & Volumes (AniList)** — Pulls author name and total volume count from AniList and creates volume folders automatically
- 📂 **Organize Chapters in Volumes** — Moves CBZ files into the correct volume folders using a 3-step fallback (filename → MangaDex → MangaUpdates → manual input)
- 📦 **Extract CBZ** — Extracts each chapter into its own folder using 7-Zip
- 📋 **Generate ComicInfo.xml** — Creates properly formatted metadata files (title, author, volume, chapter) at both chapter and volume level for correct MOBI metadata
- 🧹 **Cleanup** — Moves processed CBZ files to `/Originals` backup folder, leaving the structure clean for conversion
- ⚙️ **Process & Convert** — Resizes images to the selected Kindle profile resolution, crops white/black borders, converts to grayscale, applies gamma correction and sharpening — all in C# without touching KCC
- 🔄 **Generate mobi** — Passes the processed volume folders to `kcc_c2e` CLI headlessly, outputting `.mobi` files to the `/Converted` folder
- 📱 **Send to Kindle** — Copies `.mobi` files directly to the connected Kindle via USB (supports All or Range selection), triggers Kindle rescan
- 🗑️ **Remove from Kindle** — Deletes the manga folder from the Kindle and clears related thumbnails
- 💾 **Persistent settings** — Remembers base folder, selected Kindle profile, and `kcc_c2e` path between sessions
- 🖼️ **Cover art** — Loads covers from AniList or MangaDex automatically; falls back to a local file picker
- 📡 **Auto Kindle detection** — Detects Kindle connected via USB and updates the list status in real time

---

## 🔧 Requirements

- **Windows 10** or later
- [**.NET 8 Runtime**](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- [**7-Zip**](https://www.7-zip.org/) installed at `C:\Program Files\7-Zip\7z.exe`
- [**kcc_c2e CLI**](https://github.com/ciromattia/kcc/releases) — the standalone CLI executable from the KCC GitHub releases

### ⚠️ Important: KCC CLI version required

Manga Manager uses `kcc_c2e` in **headless/CLI mode** to convert processed images to `.mobi`.

> **Do NOT install `kcc` from pip** — the package on PyPI (`kcc 0.0.9`) is an unrelated project.  
> **Do NOT use `KCC.exe`** — the standalone GUI executable does not support CLI arguments.

**Download the correct file:**

1. Go to [https://github.com/ciromattia/kcc/releases](https://github.com/ciromattia/kcc/releases)
2. Download `kcc_c2e_X.X.X.exe` (e.g. `kcc_c2e_10.1.3.exe`)
3. Save it anywhere (e.g. `D:\Mangas\kcc_c2e_10.1.3.exe`)

Manga Manager will automatically detect it in common locations. If not found, it will prompt you to locate it manually and save the path for future sessions.

---

## 🚀 Installation

1. Download the latest release from the [Releases](../../releases) page
2. Extract the `.zip` file to a folder of your choice
3. Install [.NET 8 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) if not already installed
4. Install [7-Zip](https://www.7-zip.org/) at the default path
5. Download `kcc_c2e_*.exe` from [KCC releases](https://github.com/ciromattia/kcc/releases)
6. Run `MangaManager.exe`

---

## 📖 How to Use

### Recommended Workflow

```
1. Select Folder        → Point to your root manga folder (e.g. D:\Mangas)
2. Select a manga       → Click a title in the list
3. Fetch Author         → Auto-fills author from AniList and creates volume folders
4. Organize Chapters    → Sorts CBZ files into Volume folders and extracts them
5. Generate ComicInfo   → Creates ComicInfo.xml metadata in each chapter and volume folder
6. Cleanup              → Moves CBZ originals to /Originals backup folder
7. Process & Convert    → Resizes and optimizes images for the selected Kindle profile
8. Generate mobi        → Runs kcc_c2e headlessly to produce .mobi files in /Converted
9. Send to Kindle       → Copies .mobi files directly to the Kindle via USB
```

### Kindle Profile Selection

Choose your Kindle model from the dropdown before running **Process & Convert**:

| Profile | Resolution |
|---|---|
| Kindle Paperwhite 1/2/3 (KPW) | 758 × 1024 |
| Kindle Paperwhite 4/5 / 11th Gen (KPW5) | 1072 × 1448 |
| Kindle Oasis (KO) | 1264 × 1680 |
| Kindle Scribe (KS) | 1860 × 2480 |
| Kindle Basic 2022 (K11) | 1072 × 1448 |

### Expected Folder Structure

**Before:**
```
D:\Mangas\
  └── One Piece\
        ├── Vol.01 Ch.001.cbz
        ├── Vol.01 Ch.002.cbz
        ├── Vol.02 Ch.011.cbz
        └── ...
```

**After (full pipeline):**
```
D:\Mangas\
  └── One Piece\
        ├── One Piece - Volume 01\
        │     ├── ComicInfo.xml          ← volume-level (used by KCC for MOBI metadata)
        │     ├── Capitulo 001\
        │     │     ├── page001.jpg      ← resized & optimized for Kindle
        │     │     └── ComicInfo.xml
        │     └── Capitulo 002\
        │           ├── page001.jpg
        │           └── ComicInfo.xml
        ├── One Piece - Volume 02\
        │     └── ...
        ├── Converted\
        │     ├── One Piece - Volume 01.mobi
        │     └── One Piece - Volume 02.mobi
        └── Originals\
              ├── Vol.01 Ch.001.cbz
              └── ...
```

---

## 🌐 API Integrations

| Service | Usage |
|---|---|
| [AniList](https://anilist.gitbook.io/anilist-apiv2-docs/) | Author name, total volume count, cover art |
| [MangaDex](https://api.mangadex.org/docs/) | Chapter-to-volume mapping, cover art fallback |
| [MangaUpdates](https://api.mangaupdates.com/) | Fallback chapter-to-volume mapping |

### Chapter Organization Fallback Logic

```
1. Filename contains "Vol. XX"?   → Use it directly
2. MangaDex has chapter data?     → Build map from feed
3. MangaUpdates has release data? → Build map from releases
4. All sources failed?            → Ask user: how many chapters per volume?
```

---

## 🛠 Building from Source

### Prerequisites

- [Visual Studio 2022+](https://visualstudio.microsoft.com/) with **.NET Desktop Development** workload
- .NET 8 SDK

### Steps

```bash
git clone https://github.com/YOUR_USERNAME/MangaManager.git
cd MangaManager
```

1. Open `MangaManager.sln` in Visual Studio
2. Restore NuGet packages (automatic on first build)
3. Set configuration to **Release**
4. Build → `Ctrl+Shift+B`

### Publish as single executable

```bash
dotnet publish -c Release -r win-x64 --no-self-contained
```

Output:
```
bin\Release\net8.0-windows\win-x64\publish\MangaManager.exe
```

---

## 📦 Dependencies

| Package | Purpose |
|---|---|
| Microsoft.VisualBasic | InputBox dialog |
| System.Windows.Forms | FolderBrowserDialog, OpenFileDialog |
| System.Text.Json | JSON parsing for API responses |
| System.Drawing.Common | Image processing (resize, crop, grayscale, sharpen) |

---

## 🐛 Known Limitations

- 7-Zip must be installed at the default path (`C:\Program Files\7-Zip\7z.exe`)
- `kcc_c2e` must be the **CLI version** from GitHub releases — not the GUI `KCC.exe` and not the PyPI package
- Licensed manga may have incomplete chapter data on MangaDex/MangaUpdates, requiring the manual fallback

---

## 📄 License

This project is licensed under the **MIT License** — see the [LICENSE](LICENSE) file for details.

---

## 👤 Author

**Wagner Colozzo Abril**
- Portfolio: [wcabril.com](https://wcabril.com)
- LinkedIn: [linkedin.com/in/wcabril](https://linkedin.com/in/wcabril)

---

> Built with C#, WPF, and a lot of manga to organize. 📖
