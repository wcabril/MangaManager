# 📚 Manga Manager

A Windows desktop application built with **C# and WPF** to automate the organization, extraction, and metadata generation of manga CBZ files — preparing them for Kindle conversion via KCC (Kindle Comic Converter).

---

## ✨ Features

- 📂 **Organize Chapters in Volumes** — Automatically moves CBZ files into the correct volume folders using a smart 3-step fallback system (file name → MangaDex → MangaUpdates → manual input)
- 🔍 **Fetch Author & Volumes (AniList)** — Pulls author name and total volume count directly from AniList, and creates volume folders automatically
- 📦 **Extract CBZ** — Extracts each CBZ file into its own chapter folder using 7-Zip
- 📋 **Generate ComicInfo.xml** — Creates properly formatted metadata files inside each chapter folder for Kindle and comic reader compatibility
- 🧹 **Cleanup** — Moves all processed CBZ files into a `/CBZ` backup folder, leaving the volume structure clean for KCC
- 💾 **Persistent folder selection** — Remembers the last used manga folder between sessions

---

## 🔧 Requirements

- Windows 10 or later
- [.NET 8 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- [7-Zip](https://www.7-zip.org/) installed at `C:\Program Files\7-Zip\7z.exe`

---

## 🚀 Installation

1. Download the latest release from the [Releases](../../releases) page
2. Extract the `.zip` file to a folder of your choice
3. Make sure [.NET 8 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) is installed
4. Make sure [7-Zip](https://www.7-zip.org/) is installed at the default path
5. Run `MangaManager.exe`

---

## 📖 How to Use

### Recommended Workflow

```
1. Select Folder       → Point to your root manga folder (e.g. D:\Manga\CBZ)
2. Select a manga      → Click on a title from the list
3. Fetch Author        → Use AniList to auto-fill author and create volume folders
4. Organize Chapters   → Moves CBZ files into the correct Volume folders and extracts them
5. Generate ComicInfo  → Creates ComicInfo.xml metadata in each chapter folder
6. Cleanup             → Moves CBZ files to a /CBZ backup folder
7. KCC                 → Use Kindle Comic Converter to finalize the conversion
```

### Expected Folder Structure

**Before:**
```
D:\Manga\CBZ\
  └── One Piece\
        ├── Vol. 01 Ch. 001.cbz
        ├── Vol. 01 Ch. 002.cbz
        ├── Vol. 02 Ch. 011.cbz
        └── ...
```

**After:**
```
D:\Manga\CBZ\
  └── One Piece\
        ├── Volume 01\
        │     ├── Capitulo 001\
        │     │     ├── page001.jpg
        │     │     └── ComicInfo.xml
        │     └── Capitulo 002\
        │           ├── page001.jpg
        │           └── ComicInfo.xml
        ├── Volume 02\
        │     └── ...
        └── CBZ\
              ├── Vol. 01 Ch. 001.cbz
              └── ...
```

---

## 🌐 API Integrations

| Service | Usage |
|---|---|
| [AniList](https://anilist.gitbook.io/anilist-apiv2-docs/) | Author name and total volume count |
| [MangaDex](https://api.mangadex.org/docs/) | Chapter-to-volume mapping |
| [MangaUpdates](https://api.mangaupdates.com/) | Fallback chapter-to-volume mapping |

### Chapter Organization Fallback Logic

```
1. File name contains "Vol. XX"?  → Use it directly
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

### Publish as executable

```bash
dotnet publish -c Release -r win-x64 --no-self-contained
```

Output will be at:
```
bin\Release\net8.0-windows\win-x64\publish\MangaManager.exe
```

---

## 📦 Dependencies

| Package | Purpose |
|---|---|
| Microsoft.VisualBasic | InputBox dialog |
| System.Windows.Forms | FolderBrowserDialog |
| System.Text.Json | JSON parsing for API responses |

---

## 🤝 Contributing

Contributions are welcome! If you'd like to improve Manga Manager:

1. Fork the repository
2. Create a new branch: `git checkout -b feature/your-feature-name`
3. Make your changes and commit: `git commit -m 'Add your feature'`
4. Push to your branch: `git push origin feature/your-feature-name`
5. Open a Pull Request

Please make sure your code follows the existing style and all features are tested before submitting.

---

## 🐛 Known Limitations

- 7-Zip must be installed at the default path (`C:\Program Files\7-Zip\7z.exe`). Custom paths are not yet configurable via UI.
- Licensed manga titles may not have chapter data on MangaDex or MangaUpdates, requiring the manual fallback.
- CBZ files without volume or chapter numbers in their names may not be recognized automatically.

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