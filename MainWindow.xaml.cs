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
            ResetButtonStates();

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

        // Modelo de item da lista com status visual
        private class MangaItem : System.ComponentModel.INotifyPropertyChanged
        {
            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
            private void OnChanged(string prop) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(prop));

            private string _name = "", _statusColor = "Transparent", _statusForeground = "White",
                           _statusWeight = "Normal", _statusTip = "",
                           _chk1 = "", _chk2 = "", _chk3 = "", _chk4 = "", _chk5 = "";
            private System.Windows.Media.ImageSource? _coverImage;

            public string Name { get => _name; set { _name = value; OnChanged(nameof(Name)); } }
            public string StatusColor { get => _statusColor; set { _statusColor = value; OnChanged(nameof(StatusColor)); } }
            public string StatusForeground { get => _statusForeground; set { _statusForeground = value; OnChanged(nameof(StatusForeground)); } }
            public string StatusWeight { get => _statusWeight; set { _statusWeight = value; OnChanged(nameof(StatusWeight)); } }
            public string StatusTip { get => _statusTip; set { _statusTip = value; OnChanged(nameof(StatusTip)); } }
            public string Chk1 { get => _chk1; set { _chk1 = value; OnChanged(nameof(Chk1)); } }
            public string Chk2 { get => _chk2; set { _chk2 = value; OnChanged(nameof(Chk2)); } }
            public string Chk3 { get => _chk3; set { _chk3 = value; OnChanged(nameof(Chk3)); } }
            public string Chk4 { get => _chk4; set { _chk4 = value; OnChanged(nameof(Chk4)); } }
            public string Chk5 { get => _chk5; set { _chk5 = value; OnChanged(nameof(Chk5)); } }
            public System.Windows.Media.ImageSource? CoverImage { get => _coverImage; set { _coverImage = value; OnChanged(nameof(CoverImage)); } }
        }

        private enum MangaStatus { None, Partial, Complete }

        private MangaStatus CheckMangaStatus(string mangaPath, out string tip)
        {
            tip = "";
            var steps = new List<string>();
            int completed = 0;

            // 1. Volumes criados
            var volumes = Directory.GetDirectories(mangaPath, "* - Volume *");
            if (volumes.Length == 0)
            {
                tip = "❌ No volume folders found";
                return MangaStatus.None;
            }
            completed++;
            steps.Add("✅ Volumes created");

            // 2. Capítulos organizados
            bool hasChapters = volumes.Any(v => Directory.GetDirectories(v, "Capitulo *").Length > 0);
            if (hasChapters) { completed++; steps.Add("✅ Chapters organized"); }
            else steps.Add("❌ Chapters not organized");

            // 3. CBZ extraídos (imagens dentro dos capítulos)
            bool hasImages = volumes.Any(v =>
                Directory.GetDirectories(v, "Capitulo *").Any(ch =>
                    Directory.GetFiles(ch, "*.jpg").Length > 0 ||
                    Directory.GetFiles(ch, "*.png").Length > 0 ||
                    Directory.GetFiles(ch, "*.webp").Length > 0));
            if (hasImages) { completed++; steps.Add("✅ CBZ extracted"); }
            else steps.Add("❌ CBZ not extracted");

            // 4. ComicInfo gerado
            bool hasComicInfo = volumes.Any(v =>
                Directory.GetDirectories(v, "Capitulo *").Any(ch =>
                    File.Exists(Path.Combine(ch, "ComicInfo.xml"))));
            if (hasComicInfo) { completed++; steps.Add("✅ ComicInfo generated"); }
            else steps.Add("❌ ComicInfo missing");

            // 5. Cleanup feito (sem CBZ soltos dentro dos volumes)
            bool noCbzInVolumes = !volumes.Any(v => Directory.GetFiles(v, "*.cbz").Length > 0);
            if (noCbzInVolumes) { completed++; steps.Add("✅ Cleanup done"); }
            else steps.Add("❌ CBZ files still inside volume folders");

            tip = string.Join("\n", steps);

            if (completed == 5) return MangaStatus.Complete;
            if (completed >= 1) return MangaStatus.Partial;
            return MangaStatus.None;
        }

        private async void LoadMangas()
        {
            MangaList.Items.Clear();

            if (string.IsNullOrEmpty(basePath) || !Directory.Exists(basePath))
            {
                Log("Invalid or non-existent folder.");
                return;
            }

            var dirs = Directory.GetDirectories(basePath)
                                .Select(Path.GetFileName)
                                .Where(x => x != null)
                                .OrderBy(x => x)
                                .ToArray();

            var tasks = new List<Task>();
            foreach (var name in dirs)
            {
                string fullPath = Path.Combine(basePath, name!);
                var item = BuildMangaItem(name!, fullPath);
                MangaList.Items.Add(item);
                tasks.Add(LoadCoverAsync(item, fullPath));
            }

            Log($"{MangaList.Items.Count} manga(s) found. Loading covers...");

            // Carrega capas em background sem bloquear a UI
            _ = Task.WhenAll(tasks).ContinueWith(_ =>
                Dispatcher.Invoke(() => Log("Covers loaded.")));
        }

        private MangaItem BuildMangaItem(string name, string fullPath)
        {
            var volumes = Directory.GetDirectories(fullPath, "* - Volume *");

            // S1 — Fetch Author: volumes criados (proxy — autor não é verificável via pasta)
            bool s1 = volumes.Length > 0;

            // S2 — Organize: pastas Capitulo dentro dos volumes
            bool s2 = s1 && volumes.Any(v => Directory.GetDirectories(v, "Capitulo *").Length > 0);

            // S3 — Extract: imagens dentro dos capítulos
            bool s3 = s2 && volumes.Any(v => Directory.GetDirectories(v, "Capitulo *").Any(ch =>
                        Directory.GetFiles(ch, "*.jpg").Length > 0 ||
                        Directory.GetFiles(ch, "*.png").Length > 0 ||
                        Directory.GetFiles(ch, "*.webp").Length > 0));

            // S4 — ComicInfo: arquivo ComicInfo.xml dentro dos capítulos
            bool s4 = s3 && volumes.Any(v => Directory.GetDirectories(v, "Capitulo *").Any(ch =>
                        File.Exists(Path.Combine(ch, "ComicInfo.xml"))));

            // S5 — Cleanup: sem CBZ soltos dentro dos volumes
            bool s5 = s4 && !volumes.Any(v => Directory.GetFiles(v, "*.cbz").Length > 0);

            bool complete = s1 && s2 && s3 && s4 && s5;

            return new MangaItem
            {
                Name = name,
                StatusColor = complete ? "#95b634" : "Transparent",
                StatusForeground = complete ? "#1a1a1a" : "White",
                StatusWeight = complete ? "Bold" : "Normal",
                Chk1 = s1 ? "✔" : "",
                Chk2 = s2 ? "✔" : "",
                Chk3 = s3 ? "✔" : "",
                Chk4 = s4 ? "✔" : "",
                Chk5 = s5 ? "✔" : "",
            };
        }

        private string? GetSelectedPath()
        {
            if (MangaList.SelectedItem is not MangaItem selected)
            {
                Log("Please select a manga.");
                return null;
            }

            return Path.Combine(basePath, selected.Name);
        }

        // ==============================
        // 🖼 COVER IMAGE
        // ==============================
        private async Task LoadCoverAsync(MangaItem item, string mangaPath)
        {
            // 1. Tenta local
            string localCover = Path.Combine(mangaPath, "cover.jpg");
            if (File.Exists(localCover))
            {
                item.CoverImage = LoadImageFromFile(localCover);
                return;
            }

            // 2. Tenta AniList
            string? url = await GetCoverFromAniList(item.Name);

            // 3. Fallback MangaDex
            if (url == null)
                url = await GetCoverFromMangaDex(item.Name);

            if (url != null)
            {
                var bytes = await DownloadImageBytes(url);
                if (bytes != null)
                {
                    await File.WriteAllBytesAsync(localCover, bytes);
                    item.CoverImage = LoadImageFromBytes(bytes);
                    return;
                }
            }

            // 4. Fallback: abre diálogo para escolher localmente
            Dispatcher.Invoke(() =>
            {
                var dialog = new WinForms.OpenFileDialog
                {
                    Title = $"Select cover for {item.Name}",
                    Filter = "Images|*.jpg;*.jpeg;*.png;*.webp"
                };

                if (dialog.ShowDialog() == WinForms.DialogResult.OK)
                {
                    File.Copy(dialog.FileName, localCover, overwrite: true);
                    item.CoverImage = LoadImageFromFile(localCover);
                }
            });
        }

        private System.Windows.Media.ImageSource? LoadImageFromFile(string path)
        {
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private System.Windows.Media.ImageSource? LoadImageFromBytes(byte[] bytes)
        {
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                using var ms = new MemoryStream(bytes);
                bmp.BeginInit();
                bmp.StreamSource = ms;
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private async Task<byte[]?> DownloadImageBytes(string url)
        {
            try
            {
                using var client = new HttpClient();
                return await client.GetByteArrayAsync(url);
            }
            catch { return null; }
        }

        private async Task<string?> GetCoverFromAniList(string title)
        {
            try
            {
                using var client = new HttpClient();
                var query = @"query ($search: String) {
                  Media (search: $search, type: MANGA) {
                    coverImage { large }
                  }
                }";

                var json = JsonSerializer.Serialize(new { query, variables = new { search = title } });
                var resp = await client.PostAsync("https://graphql.anilist.co",
                    new StringContent(json, Encoding.UTF8, "application/json"));
                var str = await resp.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(str);
                return doc.RootElement
                    .GetProperty("data").GetProperty("Media")
                    .GetProperty("coverImage").GetProperty("large")
                    .GetString();
            }
            catch { return null; }
        }

        private async Task<string?> GetCoverFromMangaDex(string title)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "MangaManager/1.0");

                var searchUrl = $"https://api.mangadex.org/manga?title={Uri.EscapeDataString(title)}&limit=1&includes[]=cover_art";
                var resp = await client.GetStringAsync(searchUrl);
                using var doc = JsonDocument.Parse(resp);

                var data = doc.RootElement.GetProperty("data");
                if (data.GetArrayLength() == 0) return null;

                var manga = data[0];
                var mangaId = manga.GetProperty("id").GetString();

                foreach (var rel in manga.GetProperty("relationships").EnumerateArray())
                {
                    if (rel.GetProperty("type").GetString() == "cover_art")
                    {
                        var fileName = rel.GetProperty("attributes").GetProperty("fileName").GetString();
                        return $"https://uploads.mangadex.org/covers/{mangaId}/{fileName}.256.jpg";
                    }
                }
            }
            catch { }
            return null;
        }
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
            if (MangaList.SelectedItem is not MangaItem selectedManga)
            {
                Log("Please select a manga first.");
                return;
            }

            string title = selectedManga.Name;
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
                            string volPath = Path.Combine(path, $"{title} - Volume {i:D2}");
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
            try
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

                string title = (MangaList.SelectedItem as MangaItem)!.Name;
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

                // Se o mapa não cobriu todos os arquivos, pergunta sobre fallback manual para os restantes
                if (volumeMap.Count > 0 && volumeMap.Count < cbzFiles.Length)
                {
                    Log($"⚠ {volumeMap.Count} of {cbzFiles.Length} chapters mapped. Some chapters may be missing from the source.");

                    string input = Microsoft.VisualBasic.Interaction.InputBox(
                        $"The source only mapped {volumeMap.Count} of {cbzFiles.Length} chapters.\n\n" +
                        "For unmatched chapters, how many chapters per volume?\n(Leave empty to skip unmatched files)",
                        "Partial Map — Manual Fallback",
                        "5");

                    if (int.TryParse(input, out int chapPerVol) && chapPerVol > 0)
                    {
                        // Adiciona ao mapa apenas os capítulos que ainda não estão mapeados
                        var manualMap = BuildManualMap(cbzFiles, chapPerVol);
                        foreach (var kvp in manualMap)
                            volumeMap.TryAdd(kvp.Key, kvp.Value);

                        Log($"Manual fallback applied. Total mapped: {volumeMap.Count} chapters.");
                    }
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
                        {
                            // Tenta "1.1" → "1" e também "1.10" → "1.1"
                            volumeMap.TryGetValue(chapterNum.Split('.')[0], out volNum);
                            if (volNum == null)
                            {
                                // Tenta remover zeros à direita do decimal: "1.10" → "1.1"
                                if (decimal.TryParse(chapterNum, System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out decimal d))
                                {
                                    volumeMap.TryGetValue(d.ToString("G", System.Globalization.CultureInfo.InvariantCulture), out volNum);
                                }
                            }
                        }

                        if (volNum == null || volNum == "0")
                        {
                            Dispatcher.Invoke(() => Log($"⚠ Volume not found for chapter {chapterNum}: {Path.GetFileName(cbz)}"));
                            unmatched++;
                            current++;
                            continue;
                        }

                        string volFolder = $"{title} - Volume {int.Parse(volNum):D2}";
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

                    Dispatcher.Invoke(() => {
                        Log($"Organization complete. {current - unmatched} ok, {unmatched} unmatched.");
                        RefreshSelectedStatus();
                    });
                });
            }
            catch (Exception ex)
            {
                Log($"Critical error in Organize: {ex.Message}");
                MessageBox.Show($"Error:\n{ex.Message}\n\n{ex.StackTrace}", "Organize Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
                var volumes = Directory.GetDirectories(path, "* - Volume *").OrderBy(x => x).ToArray();
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
            RefreshSelectedStatus();
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

            string mangaName = (MangaList.SelectedItem as MangaItem)?.Name ?? "";
            var volumes = Directory.GetDirectories(path, "* - Volume *");
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
            RefreshSelectedStatus();
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
            RefreshSelectedStatus();
        }

        // ==============================
        // 🔄 OTHER
        // ==============================
        private void UpdateButtonStates(string mangaPath)
        {
            // Garante execução na UI thread
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => UpdateButtonStates(mangaPath));
                return;
            }

            var volumes = Directory.GetDirectories(mangaPath, "* - Volume *");

            bool hasAuthor = !string.IsNullOrWhiteSpace(AuthorBox.Text);
            bool hasChapters = volumes.Length > 0 &&
                               volumes.Any(v => Directory.GetDirectories(v, "Capitulo *").Length > 0);
            bool hasImages = volumes.Any(v =>
                Directory.GetDirectories(v, "Capitulo *").Any(ch =>
                    Directory.GetFiles(ch, "*.jpg").Length > 0 ||
                    Directory.GetFiles(ch, "*.png").Length > 0 ||
                    Directory.GetFiles(ch, "*.webp").Length > 0));
            bool hasComicInfo = volumes.Any(v =>
                Directory.GetDirectories(v, "Capitulo *").Any(ch =>
                    File.Exists(Path.Combine(ch, "ComicInfo.xml"))));
            bool cleaned = volumes.Length > 0 &&
                           !volumes.Any(v => Directory.GetFiles(v, "*.cbz").Length > 0);

            // Fetch Author: sempre habilitado
            BtnFetchAuthor.IsEnabled = true;
            SetCheck(BtnFetchAuthor, hasAuthor);

            // Organize: sempre habilitado (pode ter CBZ sem autor preenchido)
            BtnOrganize.IsEnabled = true;
            SetCheck(BtnOrganize, hasChapters);

            // Extract: só após organizar
            BtnExtract.IsEnabled = hasChapters;
            SetCheck(BtnExtract, hasImages);

            // ComicInfo: só após extrair
            BtnComicInfo.IsEnabled = hasImages;
            SetCheck(BtnComicInfo, hasComicInfo);

            // Cleanup: só após ComicInfo
            BtnCleanup.IsEnabled = hasComicInfo;
            SetCheck(BtnCleanup, cleaned);
        }

        // Busca o primeiro TextBlock com Text="✔ " dentro do botão e alterna visibilidade
        private void SetCheck(System.Windows.Controls.Button btn, bool done)
        {
            btn.ApplyTemplate();
            var tb = FindCheckTextBlock(btn);
            if (tb != null)
                tb.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        }

        private System.Windows.Controls.TextBlock? FindCheckTextBlock(System.Windows.DependencyObject parent)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is System.Windows.Controls.TextBlock tb && tb.Text == "✔ ")
                    return tb;
                var result = FindCheckTextBlock(child);
                if (result != null) return result;
            }
            return null;
        }

        private void RefreshSelectedStatus()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(RefreshSelectedStatus);
                return;
            }

            if (MangaList.SelectedItem is not MangaItem selected) return;
            string fullPath = Path.Combine(basePath, selected.Name);
            var updated = BuildMangaItem(selected.Name, fullPath);

            selected.StatusColor = updated.StatusColor;
            selected.StatusForeground = updated.StatusForeground;
            selected.StatusWeight = updated.StatusWeight;
            selected.Chk1 = updated.Chk1;
            selected.Chk2 = updated.Chk2;
            selected.Chk3 = updated.Chk3;
            selected.Chk4 = updated.Chk4;
            selected.Chk5 = updated.Chk5;

            UpdateButtonStates(fullPath);
        }

        private void MangaList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (MangaList.SelectedItem is MangaItem selected)
            {
                string fullPath = Path.Combine(basePath, selected.Name);
                UpdateButtonStates(fullPath);
            }
            else
            {
                ResetButtonStates();
            }
        }

        private void ResetButtonStates()
        {
            BtnFetchAuthor.IsEnabled = true;
            BtnOrganize.IsEnabled = true;
            BtnExtract.IsEnabled = false;
            BtnComicInfo.IsEnabled = false;
            BtnCleanup.IsEnabled = false;
        }

        private void MangaList_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            MangaScrollViewer.ScrollToVerticalOffset(MangaScrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        }

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
