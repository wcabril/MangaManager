# Contributing to Manga Manager

Thanks for your interest in contributing! Here's how to get started.

## Requirements

- Visual Studio 2022+ with **.NET Desktop Development** workload
- .NET 8 SDK
- 7-Zip installed at `C:\Program Files\7-Zip\7z.exe`
- `kcc_c2e_*.exe` from [KCC GitHub releases](https://github.com/ciromattia/kcc/releases) (for testing the KCC flow)

## Getting Started

```bash
git clone https://github.com/wcabril/MangaManager.git
cd MangaManager
```

1. Open `MangaManager.sln` in Visual Studio
2. Build with `Ctrl+Shift+B`
3. Run with `F5` (Debug) or `Ctrl+F5` (without debugger)

## How to Contribute

1. **Fork** the repository
2. **Create a branch** from `master`:
   ```bash
   git checkout -b feature/your-feature-name
   ```
3. **Make your changes** — keep commits focused and descriptive
4. **Test manually** — run through the full workflow (organize → extract → ComicInfo → cleanup → resize → generate mobi)
5. **Push** and open a **Pull Request** against `master`

## Guidelines

- Follow the existing code style (C# naming conventions, WPF patterns already in use)
- Keep UI changes consistent with the current dark theme
- If adding a new button, wire up its enabled/disabled state in both `UpdateButtonStates()` and `ResetButtonStates()`
- Prefer async/await for any file or network operations to keep the UI responsive
- Write clear PR descriptions explaining what changed and why

## Reporting Bugs

Use the [Bug Report](.github/ISSUE_TEMPLATE/bug_report.md) issue template.

## Suggesting Features

Use the [Feature Request](.github/ISSUE_TEMPLATE/feature_request.md) issue template.
