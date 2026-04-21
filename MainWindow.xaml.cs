using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Windows;
using WinForms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace MangaManager
{
    public partial class MainWindow : Window
    {
        string basePath = string.Empty;
        string zipPath = @"C:\Program Files\7-Zip\7z.exe";

        public MainWindow()
        {
            InitializeComponent();

            var saved = Properties.Settings.Default.BasePath;
            if (!string.IsNullOrEmpty(saved) && Directory.Exists(saved))
            {
                basePath = saved;
                BasePathBox.Text = basePath;
                LoadMangas();
            }
        }

        // ==============================
        // 📂 SELECT FOLDER
        // ==============================
        private void SelectFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new WinForms.FolderBrowserDialog
            {
                Description = "Select the root manga folder",
                UseDescriptionForTitle = true,
                SelectedPath = basePath
            };

            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            {
                basePath = dialog.SelectedPath ?? string.Empty;
                BasePathBox.Text = basePath;

                Properties.Settings.Default.BasePath = basePath;
                Properties.Settings.Default.Save();

                LoadMangas();
            }
        }

        // ==============================
        // 📋 LOAD LIST
        // ==============================
        private void LoadMangas()
        {
            MangaList.Items.Clear();

            if (string.IsNullOrEmpty(basePath) || !Directory.Exists(basePath))
            {
                Log("Invalid or non-existent folder.");
                return;
            }

            var mangas = Directory.GetDirectories(basePath)
                                  .Select(Path.GetFileName)
                                  .Where(x => x != null)
                                  .OrderBy(x => x);

            foreach (var m in mangas)
                MangaList.Items.Add(m!);

            Log($"{MangaList.Items.Count} manga(s) found.");
        }

        private string? GetSelectedPath()
        {
            if (MangaList.SelectedItem == null)
            {
                Log("Please select a manga.");
                return null;
            }

            return Path.Combine(basePath, MangaList.SelectedItem.ToString()!);
        }

        // ==============================
        // 🔍 ANILIST
        // ==============================
        private record AniListInfo(string? Author, int? Volumes);

        private async Task<AniListInfo> GetInfoFromAniList(string title)
        {
            using var client = new HttpClient();

            var query = @"
            query ($search: String) {
              Media (search: $search, type: MANGA) {
                volumes
                staff {
                  edges {
                    role
                    node {
                      name {
                        full
                      }
                    }
                  }
                }
              }
            }";

            var json = JsonSerializer.Serialize(new
            {
                query,
                variables = new { search = title }
            });

            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var response = await client.PostAsync("https://graphql.anilist.co", content);
                var str = await response.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(str);

                var media = doc.RootElement
                    .GetProperty("data")
                    .GetProperty("Media");

                int? volumes = null;
                if (media.TryGetProperty("volumes", out var volProp) &&
                    volProp.ValueKind == JsonValueKind.Number)
                    volumes = volProp.GetInt32();

                string? author = null;
                var edges = media.GetProperty("staff").GetProperty("edges");

                foreach (var edge in edges.EnumerateArray())
                {
                    var role = edge.GetProperty("role").GetString() ?? "";

                    if (role.Contains("Story") || role.Contains("Original"))
                    {
                        author = edge.GetProperty("node")
                                     .GetProperty("name")
                                     .GetProperty("full")
                                     .GetString();
                        break;
                    }
                }

                return new AniListInfo(author, volumes);
            }
            catch (Exception ex)
            {
                Log($"AniList error: {ex.Message}");
            }

            return new AniListInfo(null, null);
        }

        private async void FetchAuthor_Click(object sender, RoutedEventArgs e)
        {
            if (MangaList.SelectedItem == null)
            {
                Log("Please select a manga first.");
                return;
            }

            string title = MangaList.SelectedItem.ToString()!;
            Log($"Fetching info for \"{title}\"...");

            var info = await GetInfoFromAniList(title);

            if (info.Author == null && info.Volumes == null)
            {
                Log("No information found on AniList.");
                return;
            }

            string msg = "";
            if (info.Author != null) msg += $"Author: {info.Author}\n";
            if (info.Volumes != null) msg += $"Volumes: {info.Volumes}\n";
            msg += "\nConfirm?";

            if (MessageBox.Show(msg, "AniList", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                if (info.Author != null)
                {
                    AuthorBox.Text = info.Author;
                    Log($"Author set: {info.Author}");
                }

                if (info.Volumes != null)
                {
                    var path = GetSelectedPath();
                    if (path != null)
                    {
                        int created = 0;
                        for (int i = 1; i <= info.Volumes; i++)
                        {
                            string volPath = Path.Combine(path, $"Volume {i:D2}");
                            if (!Directory.Exists(volPath))
                            {
                                Directory.CreateDirectory(volPath);
                                created++;
                            }
                        }
                        Log($"{created} volume(s) created (of {info.Volumes} from AniList).");
                    }
                }
            }
        }

        // ==============================
        // 🌐 MANGADEX
        // ==============================
        private async Task<Dictionary<string, string>> GetVolumeMapFromMangaDex(string title)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "MangaManager/1.0");

            var map = new Dictionary<string, string>();

            try
            {
                var searchUrl = $"https://api.mangadex.org/manga?title={Uri.EscapeDataString(title)}&limit=5";
                var searchResp = await client.GetStringAsync(searchUrl);
                using var searchDoc = JsonDocument.Parse(searchResp);

                var results = searchDoc.RootElement.GetProperty("data");
                if (results.GetArrayLength() == 0) return map;

                var mangaId = results[0].GetProperty("id").GetString();
                var mangaTitle = results[0].GetProperty("attributes")
                                           .GetProperty("title")
                                           .EnumerateObject().First().Value.GetString();

                Log($"MangaDex: found \"{mangaTitle}\"");

                int offset = 0;
                const int limit = 500;

                while (true)
                {
                    var feedUrl = $"https://api.mangadex.org/manga/{mangaId}/feed" +
                                  $"?limit={limit}&offset={offset}&order[chapter]=asc" +
                                  $"&contentRating[]=safe&contentRating[]=suggestive" +
                                  $"&contentRating[]=erotica&contentRating[]=pornographic";

                    var feedResp = await client.GetStringAsync(feedUrl);
                    using var feedDoc = JsonDocument.Parse(feedResp);

                    var data = feedDoc.RootElement.GetProperty("data");
                    int count = data.GetArrayLength();

                    foreach (var ch in data.EnumerateArray())
                    {
                        var attrs = ch.GetProperty("attributes");

                        var chNum = attrs.TryGetProperty("chapter", out var chProp) && chProp.ValueKind == JsonValueKind.String
                            ? chProp.GetString() ?? "" : "";

                        var volNum = attrs.TryGetProperty("volume", out var vProp) && vProp.ValueKind == JsonValueKind.String
                            ? vProp.GetString() ?? "0" : "0";

                        if (!string.IsNullOrEmpty(chNum) && !map.ContainsKey(chNum))
                            map[chNum] = volNum;
                    }

                    offset += count;
                    int total = feedDoc.RootElement.TryGetProperty("total", out var totalProp)
                        ? totalProp.GetInt32() : 0;

                    if (offset >= total || count == 0) break;
                }

                Log($"MangaDex: {map.Count} chapter(s) mapped.");
            }
            catch (Exception ex)
            {
                Log($"MangaDex error: {ex.Message}");
            }

            return map;
        }

        // ==============================
        // 📚 MANGAUPDATES
        // ==============================
        private async Task<Dictionary<string, string>> GetVolumeMapFromMangaUpdates(string title)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "MangaManager/1.0");

            var map = new Dictionary<string, string>();

            try
            {
                var searchJson = JsonSerializer.Serialize(new { search = title, stype = "title" });
                var searchContent = new StringContent(searchJson, Encoding.UTF8, "application/json");
                var searchResp = await client.PostAsync("https://api.mangaupdates.com/v1/series/search", searchContent);
                var searchStr = await searchResp.Content.ReadAsStringAsync();

                using var searchDoc = JsonDocument.Parse(searchStr);
                var results = searchDoc.RootElement.GetProperty("results");

                if (results.GetArrayLength() == 0)
                {
                    Log("MangaUpdates: no results found.");
                    return map;
                }

                var seriesId = results[0].GetProperty("record").GetProperty("series_id").GetInt64();
                var seriesTitle = results[0].GetProperty("record").GetProperty("title").GetString();
                Log($"MangaUpdates: found \"{seriesTitle}\"");

                int page = 1;
                while (true)
                {
                    var relJson = JsonSerializer.Serialize(new
                    {
                        search = "",
                        series_id = seriesId,
                        perpage = 100,
                        page
                    });
                    var relContent = new StringContent(relJson, Encoding.UTF8, "application/json");
                    var relResp = await client.PostAsync("https://api.mangaupdates.com/v1/releases/search", relContent);
                    var relStr = await relResp.Content.ReadAsStringAsync();

                    using var relDoc = JsonDocument.Parse(relStr);
                    var releases = relDoc.RootElement.GetProperty("results");
                    int count = releases.GetArrayLength();

                    foreach (var rel in releases.EnumerateArray())
                    {
                        var record = rel.GetProperty("record");

                        var volNum = record.TryGetProperty("volume", out var vp) && vp.ValueKind == JsonValueKind.String
                            ? vp.GetString() ?? "0" : "0";
                        var chNum = record.TryGetProperty("chapter", out var cp) && cp.ValueKind == JsonValueKind.String
                            ? cp.GetString() ?? "" : "";

                        if (string.IsNullOrEmpty(chNum) || string.IsNullOrEmpty(volNum) || volNum == "0") continue;

                        if (chNum.Contains('-'))
                        {
                            var parts = chNum.Split('-');
                            if (int.TryParse(parts[0].Trim(), out int start) &&
                                int.TryParse(parts[1].Trim(), out int end))
                            {
                                for (int i = start; i <= end; i++)
                                    map.TryAdd(i.ToString(), volNum);
                            }
                        }
                        else
                        {
                            string key = chNum.TrimStart('0');
                            if (string.IsNullOrEmpty(key)) key = "0";
                            map.TryAdd(key, volNum);
                        }
                    }

                    int totalResults = relDoc.RootElement.TryGetProperty("total_hits", out var th)
                        ? th.GetInt32() : 0;

                    if (page * 100 >= totalResults || count == 0) break;
                    page++;
                }

                Log($"MangaUpdates: {map.Count} chapter(s) mapped.");
            }
            catch (Exception ex)
            {
                Log($"MangaUpdates error: {ex.Message}");
            }

            return map;
        }

        // ==============================
        // 🗂 MANUAL FALLBACK
        // ==============================
        private Dictionary<string, string> BuildManualMap(string[] cbzFiles, int chaptersPerVolume)
        {
            var map = new Dictionary<string, string>();
            int vol = 1;
            int count = 0;

            foreach (var cbz in cbzFiles)
            {
                string fileName = Path.GetFileNameWithoutExtension(cbz);
                var match = System.Text.RegularExpressions.Regex.Match(
                    fileName,
                    @"(?:cap[ií]tulo|capitulo|cap\.?|chapter|ch\.?)\s*(\d+(?:\.\d+)?)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (!match.Success)
                    match = System.Text.RegularExpressions.Regex.Match(fileName, @"(\d+(?:\.\d+)?)");

                if (!match.Success) continue;

                string chNum = match.Groups[1].Value.TrimStart('0');
                if (string.IsNullOrEmpty(chNum)) chNum = "0";

                map[chNum] = vol.ToString();
                count++;

                if (count >= chaptersPerVolume)
                {
                    vol++;
                    count = 0;
                }
            }

            return map;
        }

        // ==============================
        // 📂 ORGANIZE CHAPTERS IN VOLUMES
        // ==============================
        private async void OrganizeMangaDex_Click(object sender, RoutedEventArgs e)
        {
            var path = GetSelectedPath();
            if (path == null) return;

            if (!File.Exists(zipPath))
            {
                Log($"7-Zip not found at: {zipPath}");
                return;
            }

            var cbzFiles = Directory.GetFiles(path, "*.cbz").OrderBy(x => x).ToArray();
            if (cbzFiles.Length == 0)
            {
                Log("No .cbz files found in manga folder.");
                return;
            }

            string title = MangaList.SelectedItem!.ToString()!;
            Dictionary<string, string> volumeMap;

            Log("[1/3] Searching MangaDex...");
            volumeMap = await GetVolumeMapFromMangaDex(title);

            if (volumeMap.Count == 0)
            {
                Log("[2/3] MangaDex empty. Trying MangaUpdates...");
                volumeMap = await GetVolumeMapFromMangaUpdates(title);
            }

            if (volumeMap.Count == 0)
            {
                Log("[3/3] No data found. Using manual distribution...");

                string input = Microsoft.VisualBasic.Interaction.InputBox(
                    "Could not fetch chapter map automatically.\n\nHow many chapters per volume?",
                    "Manual Distribution",
                    "5");

                if (!int.TryParse(input, out int chapPerVol) || chapPerVol <= 0)
                {
                    Log("Operation cancelled.");
                    return;
                }

                volumeMap = BuildManualMap(cbzFiles, chapPerVol);
                Log($"Manual map: {volumeMap.Count} chapters, {chapPerVol} per volume.");
            }

            Log($"{cbzFiles.Length} file(s) found. Starting organization...");
            ProgressBar.Value = 0;
            ProgressText.Text = "";

            await Task.Run(() =>
            {
                int total = cbzFiles.Length;
                int current = 0;
                int unmatched = 0;

                foreach (var cbz in cbzFiles)
                {
                    string fileName = Path.GetFileNameWithoutExtension(cbz);

                    var volMatch = System.Text.RegularExpressions.Regex.Match(
                        fileName,
                        @"[Vv]ol\.?\s*(\d+)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    var chMatch = System.Text.RegularExpressions.Regex.Match(
                        fileName,
                        @"(?:cap[ií]tulo|capitulo|cap\.?|chapter|ch\.?)\s*(\d+(?:\.\d+)?)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    if (!chMatch.Success)
                        chMatch = System.Text.RegularExpressions.Regex.Match(fileName, @"(\d+(?:\.\d+)?)");

                    if (!chMatch.Success)
                    {
                        Dispatcher.Invoke(() => Log($"⚠ Not recognized: {Path.GetFileName(cbz)}"));
                        unmatched++;
                        current++;
                        continue;
                    }

                    string chapterNum = chMatch.Groups[1].Value.TrimStart('0');
                    if (string.IsNullOrEmpty(chapterNum)) chapterNum = "0";

                    string? volNum = null;

                    if (volMatch.Success)
                    {
                        volNum = volMatch.Groups[1].Value.TrimStart('0');
                        if (string.IsNullOrEmpty(volNum)) volNum = "1";
                    }

                    if (volNum == null)
                        volumeMap.TryGetValue(chapterNum, out volNum);

                    if (volNum == null && chapterNum.Contains('.'))
                        volumeMap.TryGetValue(chapterNum.Split('.')[0], out volNum);

                    if (volNum == null || volNum == "0")
                    {
                        Dispatcher.Invoke(() => Log($"⚠ Volume not found for chapter {chapterNum}: {Path.GetFileName(cbz)}"));
                        unmatched++;
                        current++;
                        continue;
                    }

                    string volFolder = $"Volume {int.Parse(volNum):D2}";
                    string volPath = Path.Combine(path, volFolder);
                    Directory.CreateDirectory(volPath);

                    string chFormatted = chapterNum.Contains('.')
                        ? $"Capitulo {chapterNum.PadLeft(6, '0')}"
                        : $"Capitulo {int.Parse(chapterNum):D3}";

                    string chDest = Path.Combine(volPath, chFormatted);
                    Directory.CreateDirectory(chDest);

                    string cbzDest = Path.Combine(volPath, Path.GetFileName(cbz));
                    if (!File.Exists(cbzDest))
                        File.Move(cbz, cbzDest);

                    var psi = new ProcessStartInfo
                    {
                        FileName = zipPath,
                        Arguments = $"x \"{cbzDest}\" -o\"{chDest}\" -y",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };

                    Process.Start(psi)?.WaitForExit();
                    current++;

                    Dispatcher.Invoke(() =>
                    {
                        ProgressBar.Value = (double)current / total * 100;
                        ProgressText.Text = $"{current} / {total}";
                        Log($"✓ Ch {chapterNum} → {volFolder}\\{chFormatted}");
                    });
                }

                Dispatcher.Invoke(() =>
                    Log($"Organization complete. {current - unmatched} ok, {unmatched} unmatched."));
            });
        }

        // ==============================
        // 📦 EXTRACT CBZ
        // ==============================
        private async void Extract_Click(object sender, RoutedEventArgs e)
        {
            var path = GetSelectedPath();
            if (path == null) return;

            if (!File.Exists(zipPath))
            {
                Log($"7-Zip not found at: {zipPath}");
                return;
            }

            ProgressBar.Value = 0;
            ProgressText.Text = "";

            await Task.Run(() =>
            {
                var volumes = Directory.GetDirectories(path, "Volume *").OrderBy(x => x).ToArray();
                int total = volumes.Sum(v => Directory.GetFiles(v, "*.cbz").Length);

                if (total == 0)
                {
                    Dispatcher.Invoke(() => Log("No .cbz files found."));
                    return;
                }

                int current = 0;

                foreach (var vol in volumes)
                {
                    var cbzs = Directory.GetFiles(vol, "*.cbz").OrderBy(x => x).ToArray();

                    foreach (var cbz in cbzs)
                    {
                        string fileName = Path.GetFileNameWithoutExtension(cbz);

                        // Extrai número do capítulo do nome do arquivo
                        var chMatch = System.Text.RegularExpressions.Regex.Match(
                            fileName,
                            @"(?:cap[ií]tulo|capitulo|cap\.?|chapter|ch\.?)\s*(\d+(?:\.\d+)?)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                        if (!chMatch.Success)
                            chMatch = System.Text.RegularExpressions.Regex.Match(fileName, @"(\d+(?:\.\d+)?)");

                        string chapterName;
                        if (chMatch.Success)
                        {
                            string chNum = chMatch.Groups[1].Value.TrimStart('0');
                            if (string.IsNullOrEmpty(chNum)) chNum = "0";

                            chapterName = chNum.Contains('.')
                                ? $"Capitulo {chNum.PadLeft(6, '0')}"
                                : $"Capitulo {int.Parse(chNum):D3}";
                        }
                        else
                        {
                            // Fallback: usa nome original do arquivo
                            chapterName = fileName;
                        }

                        string dest = Path.Combine(vol, chapterName);
                        Directory.CreateDirectory(dest);

                        var psi = new ProcessStartInfo
                        {
                            FileName = zipPath,
                            Arguments = $"x \"{cbz}\" -o\"{dest}\" -y",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };

                        Process.Start(psi)?.WaitForExit();
                        current++;

                        Dispatcher.Invoke(() =>
                        {
                            ProgressBar.Value = (double)current / total * 100;
                            ProgressText.Text = $"{current} / {total}";
                            Log($"Extracted: {Path.GetFileName(cbz)} → {chapterName}");
                        });
                    }
                }
            });

            Log("Extraction complete.");
        }

        // ==============================
        // 📋 GENERATE COMICINFO
        // ==============================
        private void ComicInfo_Click(object sender, RoutedEventArgs e)
        {
            var path = GetSelectedPath();
            if (path == null) return;

            var author = AuthorBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(author))
            {
                Log("Please enter the author before generating ComicInfo.");
                return;
            }

            string mangaName = MangaList.SelectedItem?.ToString() ?? "";
            var volumes = Directory.GetDirectories(path, "Volume *");
            int generated = 0;

            foreach (var vol in volumes)
            {
                string volNum = new string(Path.GetFileName(vol).Where(char.IsDigit).ToArray());

                foreach (var ch in Directory.GetDirectories(vol))
                {
                    string chNum = new string(Path.GetFileName(ch).Where(char.IsDigit).ToArray());

                    File.WriteAllText(Path.Combine(ch, "ComicInfo.xml"),
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<ComicInfo>
  <Title>{mangaName}</Title>
  <Series>{mangaName}</Series>
  <Number>{chNum}</Number>
  <Volume>{volNum}</Volume>
  <Writer>{author}</Writer>
  <Genre>Manga</Genre>
  <LanguageISO>pt</LanguageISO>
  <Format>Manga</Format>
</ComicInfo>");

                    generated++;
                }
            }

            Log($"ComicInfo generated for {generated} chapter(s).");
        }

        // ==============================
        // 🧹 CLEANUP
        // ==============================
        private void Cleanup_Click(object sender, RoutedEventArgs e)
        {
            var path = GetSelectedPath();
            if (path == null) return;

            var cbzFiles = Directory.GetFiles(path, "*.cbz", SearchOption.AllDirectories)
                                    .OrderBy(x => x)
                                    .ToArray();

            if (cbzFiles.Length == 0)
            {
                Log("No CBZ files found inside volume folders.");
                return;
            }

            string cbzBackupFolder = Path.Combine(path, "CBZ");
            Directory.CreateDirectory(cbzBackupFolder);

            int moved = 0;
            int skipped = 0;

            foreach (var cbz in cbzFiles)
            {
                if (Path.GetDirectoryName(cbz) == cbzBackupFolder)
                {
                    skipped++;
                    continue;
                }

                string dest = Path.Combine(cbzBackupFolder, Path.GetFileName(cbz));

                if (File.Exists(dest))
                {
                    Log($"⚠ Already exists, skipping: {Path.GetFileName(cbz)}");
                    skipped++;
                    continue;
                }

                File.Move(cbz, dest);
                Log($"Moved: {Path.GetFileName(cbz)}");
                moved++;
            }

            Log($"Cleanup done. {moved} file(s) moved to /CBZ, {skipped} skipped.");
        }

        // ==============================
        // 🔄 OTHER
        // ==============================
        private void Refresh_Click(object sender, RoutedEventArgs e) => LoadMangas();

        private void Log(string msg)
        {
            Dispatcher.Invoke(() =>
            {
                LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
                LogBox.ScrollToEnd();
            });
        }
    }
}
