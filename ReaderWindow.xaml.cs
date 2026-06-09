using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace MangaManager
{
    public partial class ReaderWindow : Window
    {
        // ── Data model ────────────────────────────────────────────────────────
        private record PageEntry(
            string ImagePath,
            string VolumeName,       // "Vol. 05"
            string ChapterName,      // "Capitulo 023"
            string VolumePath,       // full directory path of the volume
            int    ChapterIndex,     // 0-based index of this page within its chapter
            int    TotalInChapter,   // total pages in this chapter
            bool   IsLastChapterInVolume,
            int    GlobalIndex);     // position in the flat _pages list

        private readonly List<PageEntry> _pages = new();
        private int    _current            = 0;
        private readonly string _mangaPath;
        private bool   _suppressComboEvents = false;

        // Track which volumes we've already updated this session (avoid file I/O spam)
        private readonly HashSet<string> _updatedThisSession = new();

        // ── Constructor ───────────────────────────────────────────────────────
        public ReaderWindow(string mangaPath, string mangaName)
        {
            InitializeComponent();
            _mangaPath  = mangaPath;
            TitleText.Text = mangaName;

            LoadStructure();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (_pages.Count > 0)
                ShowPage(0);
            else
            {
                EmptyPanel.Visibility = Visibility.Visible;
                EmptyText.Text =
                    "No extracted pages found.\n\n" +
                    "Run 'Extract' on the main window first to unpack the chapter files,\n" +
                    "then open the reader again.";
            }
        }

        // ── Build page list ───────────────────────────────────────────────────
        private void LoadStructure()
        {
            _pages.Clear();

            if (!Directory.Exists(_mangaPath)) return;

            // Find volume folders: "* - Volume *"  or  "* Volume *"
            var volumes = Directory.GetDirectories(_mangaPath)
                .Where(d => Regex.IsMatch(Path.GetFileName(d),
                    @"[Vv]olume\s*\d+", RegexOptions.IgnoreCase))
                .OrderBy(d => d)
                .ToArray();

            int globalIndex = 0;

            foreach (var vol in volumes)
            {
                // Short name: "Vol. 05"
                var m = Regex.Match(Path.GetFileName(vol), @"[Vv]olume\s*(\d+)");
                string shortVol = m.Success
                    ? $"Vol. {int.Parse(m.Groups[1].Value):D2}"
                    : Path.GetFileName(vol);

                // Chapter folders: "Capitulo *"
                var chapters = Directory.GetDirectories(vol, "Capitulo *")
                    .OrderBy(c => c).ToArray();

                for (int ci = 0; ci < chapters.Length; ci++)
                {
                    string chName      = Path.GetFileName(chapters[ci]);
                    bool   isLastCh    = ci == chapters.Length - 1;

                    var images = Directory.GetFiles(chapters[ci])
                        .Where(IsImageFile)
                        .OrderBy(f => f)
                        .ToArray();

                    for (int pi = 0; pi < images.Length; pi++)
                    {
                        _pages.Add(new PageEntry(
                            images[pi], shortVol, chName, vol,
                            pi, images.Length, isLastCh, globalIndex));
                        globalIndex++;
                    }
                }
            }

            if (_pages.Count > 0)
                BuildVolumeCombo();
        }

        private static bool IsImageFile(string f) =>
            f.EndsWith(".jpg",  StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".png",  StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);

        // ── Combo boxes ───────────────────────────────────────────────────────
        private void BuildVolumeCombo()
        {
            _suppressComboEvents = true;
            VolumeCombo.Items.Clear();
            foreach (var v in _pages.Select(p => p.VolumeName).Distinct())
                VolumeCombo.Items.Add(v);
            if (VolumeCombo.Items.Count > 0)
                VolumeCombo.SelectedIndex = 0;
            _suppressComboEvents = false;
        }

        private void BuildChapterCombo(string volumeName)
        {
            _suppressComboEvents = true;
            ChapterCombo.Items.Clear();
            foreach (var c in _pages
                .Where(p => p.VolumeName == volumeName)
                .Select(p => p.ChapterName)
                .Distinct())
                ChapterCombo.Items.Add(c);
            if (ChapterCombo.Items.Count > 0)
                ChapterCombo.SelectedIndex = 0;
            _suppressComboEvents = false;
        }

        private void VolumeCombo_Changed(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressComboEvents || VolumeCombo.SelectedItem is not string vol) return;
            BuildChapterCombo(vol);
            var first = _pages.FirstOrDefault(p => p.VolumeName == vol);
            if (first != null) ShowPage(first.GlobalIndex);
        }

        private void ChapterCombo_Changed(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressComboEvents
                || ChapterCombo.SelectedItem  is not string ch
                || VolumeCombo.SelectedItem   is not string vol) return;

            var first = _pages.FirstOrDefault(p => p.VolumeName == vol && p.ChapterName == ch);
            if (first != null) ShowPage(first.GlobalIndex);
        }

        // ── Page display ──────────────────────────────────────────────────────
        private void ShowPage(int index)
        {
            if (_pages.Count == 0 || index < 0 || index >= _pages.Count) return;

            _current = index;
            var page = _pages[_current];

            // Page counter: "8 / 32"
            PageCounter.Text = $"{page.ChapterIndex + 1} / {page.TotalInChapter}";

            // Status bar
            StatusText.Text = $"{page.VolumeName}  ›  {page.ChapterName}";

            // Overall progress through entire manga
            ChapterProgress.Value = _pages.Count > 1
                ? (_current * 100.0 / (_pages.Count - 1))
                : 100.0;

            // Sync combos without triggering events
            _suppressComboEvents = true;
            if (VolumeCombo.SelectedItem as string != page.VolumeName)
            {
                VolumeCombo.SelectedItem = page.VolumeName;
                BuildChapterCombo(page.VolumeName);
            }
            if (ChapterCombo.SelectedItem as string != page.ChapterName)
                ChapterCombo.SelectedItem = page.ChapterName;
            _suppressComboEvents = false;

            // Load image (fast, file already on disk)
            try
            {
                PageImage.Source    = LoadBitmap(page.ImagePath);
                EmptyPanel.Visibility = Visibility.Collapsed;
                PageImage.Visibility  = Visibility.Visible;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"⚠ Could not load image: {ex.Message}";
            }

            // Mark volume status in reading_progress.json
            UpdateReadingProgress(page);

            // Preload next page in background (warms the OS file cache)
            int next = _current + 1;
            if (next < _pages.Count)
                _ = Task.Run(() =>
                {
                    try { LoadBitmap(_pages[next].ImagePath); } catch { }
                });
        }

        private static BitmapImage LoadBitmap(string path)
        {
            var bmp = new BitmapImage();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            bmp.BeginInit();
            bmp.CacheOption    = BitmapCacheOption.OnLoad;
            bmp.StreamSource   = fs;
            bmp.EndInit();
            bmp.Freeze();   // make it usable from any thread
            return bmp;
        }

        // ── Read-progress tracking ────────────────────────────────────────────
        private void UpdateReadingProgress(PageEntry page)
        {
            bool isLastPageOfVolume = page.IsLastChapterInVolume &&
                                      page.ChapterIndex == page.TotalInChapter - 1;

            string newStatus = isLastPageOfVolume ? "Read" : "Reading";
            string key       = page.VolumeName + ":" + newStatus;

            // Only write if status changed for this volume this session
            if (_updatedThisSession.Contains(key)) return;

            // Don't downgrade "Read" → "Reading"
            if (newStatus == "Reading" && _updatedThisSession.Contains(page.VolumeName + ":Read"))
                return;

            _updatedThisSession.Add(key);

            string jsonPath = Path.Combine(_mangaPath, "reading_progress.json");
            try
            {
                Dictionary<string, string> dict;
                if (File.Exists(jsonPath))
                {
                    dict = JsonSerializer.Deserialize<Dictionary<string, string>>(
                        File.ReadAllText(jsonPath)) ?? new();
                }
                else
                {
                    dict = new();
                }

                // Don't downgrade existing "Read" entry
                if (dict.TryGetValue(page.VolumeName, out var existing) &&
                    existing == "Read" && newStatus == "Reading")
                    return;

                dict[page.VolumeName] = newStatus;
                File.WriteAllText(jsonPath,
                    JsonSerializer.Serialize(dict,
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* non-critical */ }
        }

        // ── Navigation ────────────────────────────────────────────────────────
        private void NextPage()
        {
            if (_current < _pages.Count - 1) ShowPage(_current + 1);
        }

        private void PrevPage()
        {
            if (_current > 0) ShowPage(_current - 1);
        }

        private void NextZone_Click(object sender, MouseButtonEventArgs e) => NextPage();
        private void PrevZone_Click(object sender, MouseButtonEventArgs e) => PrevPage();

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Right:
                case Key.Down:
                case Key.PageDown:
                case Key.Space:
                    NextPage();
                    e.Handled = true;
                    break;

                case Key.Left:
                case Key.Up:
                case Key.PageUp:
                case Key.Back:
                    PrevPage();
                    e.Handled = true;
                    break;

                case Key.Escape:
                    Close();
                    break;

                case Key.Home:
                    ShowPage(0);
                    break;

                case Key.End:
                    ShowPage(_pages.Count - 1);
                    break;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
