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

            // Carrega pasta salva anteriormente
            var saved = Properties.Settings.Default.BasePath;
            if (!string.IsNullOrEmpty(saved) && Directory.Exists(saved))
            {
                basePath = saved;
                BasePathBox.Text = basePath;
                LoadMangas();
            }
        }

        // ==============================
        // 📂 SELECIONAR PASTA
        // ==============================
        private void SelectFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new WinForms.FolderBrowserDialog
            {
                Description = "Selecione a pasta raiz dos mangás",
                UseDescriptionForTitle = true,
                SelectedPath = basePath
            };

            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            {
                basePath = dialog.SelectedPath ?? string.Empty;
                BasePathBox.Text = basePath;

                // Salva para próxima vez
                Properties.Settings.Default.BasePath = basePath;
                Properties.Settings.Default.Save();

                LoadMangas();
            }
        }

        // ==============================
        // 📋 CARREGAR LISTA
        // ==============================
        private void LoadMangas()
        {
            MangaList.Items.Clear();

            if (string.IsNullOrEmpty(basePath) || !Directory.Exists(basePath))
            {
                Log("Pasta inválida ou não encontrada.");
                return;
            }

            var mangas = Directory.GetDirectories(basePath)
                                  .Select(Path.GetFileName)
                                  .Where(x => x != null)
                                  .OrderBy(x => x);

            foreach (var m in mangas)
                MangaList.Items.Add(m!);

            Log($"{MangaList.Items.Count} mangá(s) encontrado(s).");
        }

        private string? GetSelectedPath()
        {
            if (MangaList.SelectedItem == null)
            {
                Log("Selecione um mangá.");
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
                Log($"Erro AniList: {ex.Message}");
            }

            return new AniListInfo(null, null);
        }

        private async void FetchAuthor_Click(object sender, RoutedEventArgs e)
        {
            if (MangaList.SelectedItem == null)
            {
                Log("Selecione um mangá primeiro.");
                return;
            }

            string title = MangaList.SelectedItem.ToString()!;
            Log($"Buscando informações de \"{title}\"...");

            var info = await GetInfoFromAniList(title);

            if (info.Author == null && info.Volumes == null)
            {
                Log("Nenhuma informação encontrada no AniList.");
                return;
            }

            string msg = "";
            if (info.Author != null) msg += $"Autor: {info.Author}\n";
            if (info.Volumes != null) msg += $"Volumes: {info.Volumes}\n";
            msg += "\nConfirmar?";

            if (MessageBox.Show(msg, "AniList", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                if (info.Author != null)
                {
                    AuthorBox.Text = info.Author;
                    Log($"Autor definido: {info.Author}");
                }

                if (info.Volumes != null)
                {
                    var path = GetSelectedPath();
                    if (path != null)
                    {
                        int criados = 0;
                        for (int i = 1; i <= info.Volumes; i++)
                        {
                            string volPath = Path.Combine(path, $"Volume {i:D2}");
                            if (!Directory.Exists(volPath))
                            {
                                Directory.CreateDirectory(volPath);
                                criados++;
                            }
                        }
                        Log($"{criados} volume(s) criado(s) (de {info.Volumes} do AniList).");
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

                Log($"MangaDex: encontrado \"{mangaTitle}\"");

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

                Log($"MangaDex: {map.Count} capítulos mapeados.");
            }
            catch (Exception ex)
            {
                Log($"Erro MangaDex: {ex.Message}");
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
                // 1. Buscar o mangá
                var searchJson = JsonSerializer.Serialize(new { search = title, stype = "title" });
                var searchContent = new StringContent(searchJson, Encoding.UTF8, "application/json");
                var searchResp = await client.PostAsync("https://api.mangaupdates.com/v1/series/search", searchContent);
                var searchStr = await searchResp.Content.ReadAsStringAsync();

                using var searchDoc = JsonDocument.Parse(searchStr);
                var results = searchDoc.RootElement.GetProperty("results");

                if (results.GetArrayLength() == 0)
                {
                    Log("MangaUpdates: nenhum resultado encontrado.");
                    return map;
                }

                var seriesId = results[0].GetProperty("record").GetProperty("series_id").GetInt64();
                var seriesTitle = results[0].GetProperty("record").GetProperty("title").GetString();
                Log($"MangaUpdates: encontrado \"{seriesTitle}\"");

                // 2. Buscar releases com volume e capítulo
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

                        // Expande ranges ex: "1-5" → 1,2,3,4,5
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

                Log($"MangaUpdates: {map.Count} capítulos mapeados.");
            }
            catch (Exception ex)
            {
                Log($"Erro MangaUpdates: {ex.Message}");
            }

            return map;
        }

        // ==============================
        // 🗂 FALLBACK MANUAL
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
        // 🌐 ORGANIZAR COM MANGADEX
        // ==============================
        private async void OrganizeMangaDex_Click(object sender, RoutedEventArgs e)
        {
            var path = GetSelectedPath();
            if (path == null) return;

            if (!File.Exists(zipPath))
            {
                Log($"7-Zip não encontrado em: {zipPath}");
                return;
            }

            var cbzFiles = Directory.GetFiles(path, "*.cbz").OrderBy(x => x).ToArray();
            if (cbzFiles.Length == 0)
            {
                Log("Nenhum .cbz encontrado na pasta do mangá.");
                return;
            }

            string title = MangaList.SelectedItem!.ToString()!;
            Dictionary<string, string> volumeMap;

            // 1. Tenta MangaDex
            Log("[1/3] Buscando no MangaDex...");
            volumeMap = await GetVolumeMapFromMangaDex(title);

            // 2. Fallback MangaUpdates
            if (volumeMap.Count == 0)
            {
                Log("[2/3] MangaDex sem dados. Tentando MangaUpdates...");
                volumeMap = await GetVolumeMapFromMangaUpdates(title);
            }

            // 3. Fallback manual
            if (volumeMap.Count == 0)
            {
                Log("[3/3] Nenhuma fonte encontrou dados. Usando distribuição manual...");

                string input = Microsoft.VisualBasic.Interaction.InputBox(
                    "Não foi possível obter o mapa de capítulos automaticamente.\n\n" +
                    "Quantos capítulos por volume?",
                    "Distribuição Manual",
                    "5");

                if (!int.TryParse(input, out int chapPerVol) || chapPerVol <= 0)
                {
                    Log("Operação cancelada.");
                    return;
                }

                volumeMap = BuildManualMap(cbzFiles, chapPerVol);
                Log($"Mapa manual: {volumeMap.Count} capítulos, {chapPerVol} por volume.");
            }

            Log($"{cbzFiles.Length} arquivo(s) encontrado(s). Iniciando organização...");
            ProgressBar.Value = 0;
            ProgressText.Text = "";

            await Task.Run(() =>
            {
                int total = cbzFiles.Length;
                int current = 0;
                int naoCasados = 0;

                foreach (var cbz in cbzFiles)
                {
                    string fileName = Path.GetFileNameWithoutExtension(cbz);

                    // Tenta extrair Vol e Ch diretamente do nome: "Vol. 01 Ch. 001"
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
                        Dispatcher.Invoke(() => Log($"⚠ Não reconhecido: {Path.GetFileName(cbz)}"));
                        naoCasados++;
                        current++;
                        continue;
                    }

                    string chapterNum = chMatch.Groups[1].Value.TrimStart('0');
                    if (string.IsNullOrEmpty(chapterNum)) chapterNum = "0";

                    string? volNum = null;

                    // Prioridade 1: volume no nome do arquivo
                    if (volMatch.Success)
                    {
                        volNum = volMatch.Groups[1].Value.TrimStart('0');
                        if (string.IsNullOrEmpty(volNum)) volNum = "1";
                    }

                    // Prioridade 2: mapa do MangaDex/MangaUpdates/manual
                    if (volNum == null)
                        volumeMap.TryGetValue(chapterNum, out volNum);

                    if (volNum == null && chapterNum.Contains('.'))
                        volumeMap.TryGetValue(chapterNum.Split('.')[0], out volNum);

                    if (volNum == null || volNum == "0")
                    {
                        Dispatcher.Invoke(() => Log($"⚠ Volume não encontrado para cap {chapterNum}: {Path.GetFileName(cbz)}"));
                        naoCasados++;
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
                        Log($"✓ Cap {chapterNum} → {volFolder}\\{chFormatted}");
                    });
                }

                Dispatcher.Invoke(() =>
                    Log($"Organização finalizada. {current - naoCasados} ok, {naoCasados} não processado(s)."));
            });
        }

        // ==============================
        // 📦 EXTRAIR CBZ
        // ==============================
        private async void Extract_Click(object sender, RoutedEventArgs e)
        {
            var path = GetSelectedPath();
            if (path == null) return;

            if (!File.Exists(zipPath))
            {
                Log($"7-Zip não encontrado em: {zipPath}");
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
                    Dispatcher.Invoke(() => Log("Nenhum .cbz encontrado."));
                    return;
                }

                int current = 0;

                foreach (var vol in volumes)
                {
                    var cbzs = Directory.GetFiles(vol, "*.cbz").OrderBy(x => x).ToArray();
                    int index = 1;

                    foreach (var cbz in cbzs)
                    {
                        string chapter = $"Capitulo {index:D3}";
                        string dest = Path.Combine(vol, chapter);
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
                            Log($"Extraído: {Path.GetFileName(cbz)} → {chapter}");
                        });

                        index++;
                    }
                }
            });

            Log("Extração finalizada.");
        }

        // ==============================
        // 📋 COMICINFO
        // ==============================
        private void ComicInfo_Click(object sender, RoutedEventArgs e)
        {
            var path = GetSelectedPath();
            if (path == null) return;

            var author = AuthorBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(author))
            {
                Log("Informe o autor antes de gerar o ComicInfo.");
                return;
            }

            string mangaName = MangaList.SelectedItem?.ToString() ?? "";
            var volumes = Directory.GetDirectories(path, "Volume *");
            int gerados = 0;

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

                    gerados++;
                }
            }

            Log($"ComicInfo gerado em {gerados} capítulo(s).");
        }

        // ==============================
        // 🧹 CLEANUP
        // ==============================
        private void Cleanup_Click(object sender, RoutedEventArgs e)
        {
            var path = GetSelectedPath();
            if (path == null) return;

            // Busca todos os .cbz dentro das pastas de volume
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
                // Ignora arquivos que já estão na pasta CBZ de destino
                if (Path.GetDirectoryName(cbz) == cbzBackupFolder)
                {
                    skipped++;
                    continue;
                }

                string dest = Path.Combine(cbzBackupFolder, Path.GetFileName(cbz));

                // Evita sobrescrever se já existe
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
        // 🔄 OUTROS
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