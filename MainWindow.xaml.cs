using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
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
        }

        // ==============================
        // 📂 SELECIONAR PASTA
        // ==============================
        private void SelectFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new WinForms.FolderBrowserDialog
            {
                Description = "Selecione a pasta raiz dos mangás",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            {
                basePath = dialog.SelectedPath ?? string.Empty;
                BasePathBox.Text = basePath;
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
        // 📁 CRIAR VOLUMES
        // ==============================
        private void CreateVolumes_Click(object sender, RoutedEventArgs e)
        {
            var path = GetSelectedPath();
            if (path == null) return;

            string input = Microsoft.VisualBasic.Interaction.InputBox(
                "Quantos volumes deseja criar?",
                "Criar Volumes",
                "5");

            if (!int.TryParse(input, out int total) || total <= 0)
            {
                Log("Número inválido.");
                return;
            }

            int criados = 0;

            for (int i = 1; i <= total; i++)
            {
                string volName = $"Volume {i:D2}";
                string volPath = Path.Combine(path, volName);

                if (!Directory.Exists(volPath))
                {
                    Directory.CreateDirectory(volPath);
                    criados++;
                }
            }

            Log($"{criados} volume(s) criado(s) (de {total} solicitados).");
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

                // Volumes
                int? volumes = null;
                if (media.TryGetProperty("volumes", out var volProp) &&
                    volProp.ValueKind == JsonValueKind.Number)
                {
                    volumes = volProp.GetInt32();
                }

                // Autor
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

        // Retorna dicionário: número do capítulo (ex: "1", "1.5") -> número do volume (ex: "1")
        private async Task<Dictionary<string, string>> GetVolumeMapFromMangaDex(string title)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "MangaManager/1.0");

            var map = new Dictionary<string, string>();

            try
            {
                // 1. Buscar o mangá pelo título
                var searchUrl = $"https://api.mangadex.org/manga?title={Uri.EscapeDataString(title)}&limit=5";
                var searchResp = await client.GetStringAsync(searchUrl);
                using var searchDoc = JsonDocument.Parse(searchResp);

                var results = searchDoc.RootElement.GetProperty("data");

                if (results.GetArrayLength() == 0)
                {
                    Log("MangaDex: nenhum resultado encontrado.");
                    return map;
                }

                // Loga todos os resultados para debug
                for (int i = 0; i < results.GetArrayLength(); i++)
                {
                    var r = results[i];
                    var rId = r.GetProperty("id").GetString();
                    var rTitle = r.GetProperty("attributes").GetProperty("title")
                                  .EnumerateObject().First().Value.GetString();
                    Log($"[{i}] {rTitle} ({rId})");
                }

                // Pega o primeiro resultado
                var mangaId = results[0].GetProperty("id").GetString();
                var mangaTitle = results[0].GetProperty("attributes")
                                           .GetProperty("title")
                                           .EnumerateObject()
                                           .First().Value.GetString();

                Log($"MangaDex: usando \"{mangaTitle}\" ({mangaId})");

                // 2. Buscar feed paginado (capítulos com info de volume)
                int offset = 0;
                const int limit = 500;

                while (true)
                {
                    var feedUrl = $"https://api.mangadex.org/manga/{mangaId}/feed" +
                                  $"?limit={limit}&offset={offset}&order[chapter]=asc" +
                                  $"&contentRating[]=safe&contentRating[]=suggestive&contentRating[]=erotica&contentRating[]=pornographic";

                    var feedResp = await client.GetStringAsync(feedUrl);
                    Log($"[DEBUG] Feed JSON: {feedResp.Substring(0, Math.Min(300, feedResp.Length))}");
                    using var feedDoc = JsonDocument.Parse(feedResp);

                    var data = feedDoc.RootElement.GetProperty("data");
                    int count = data.GetArrayLength();

                    foreach (var ch in data.EnumerateArray())
                    {
                        var attrs = ch.GetProperty("attributes");

                        var chNum = attrs.TryGetProperty("chapter", out var chProp) && chProp.ValueKind == JsonValueKind.String
                            ? chProp.GetString() ?? ""
                            : "";

                        var volNum = attrs.TryGetProperty("volume", out var vProp) && vProp.ValueKind == JsonValueKind.String
                            ? vProp.GetString() ?? "0"
                            : "0";

                        if (!string.IsNullOrEmpty(chNum) && !map.ContainsKey(chNum))
                            map[chNum] = volNum;
                    }

                    offset += count;

                    // Verifica se há mais páginas
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

        private async void OrganizeMangaDex_Click(object sender, RoutedEventArgs e)
        {
            var path = GetSelectedPath();
            if (path == null) return;

            if (!File.Exists(zipPath))
            {
                Log($"7-Zip não encontrado em: {zipPath}");
                return;
            }

            string title = MangaList.SelectedItem!.ToString()!;
            Log($"Buscando mapa de volumes no MangaDex para \"{title}\"...");

            var volumeMap = await GetVolumeMapFromMangaDex(title);

            if (volumeMap.Count == 0)
            {
                Log("Não foi possível obter o mapa de volumes.");
                return;
            }

            // Buscar .cbz soltos na pasta do mangá
            var cbzFiles = Directory.GetFiles(path, "*.cbz").OrderBy(x => x).ToArray();

            if (cbzFiles.Length == 0)
            {
                Log("Nenhum .cbz encontrado na pasta do mangá.");
                return;
            }

            Log($"{cbzFiles.Length} arquivo(s) .cbz encontrado(s). Iniciando organização...");

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

                    // Extrai número do capítulo do nome do arquivo
                    // Suporta: "Cap 001", "Chapter 1", "Capítulo 12.5", "[Titulo] Cap 003", etc.
                    var match = System.Text.RegularExpressions.Regex.Match(
                        fileName,
                        @"(?:cap[ií]tulo|capitulo|cap\.?|chapter|ch\.?)\s*(\d+(?:\.\d+)?)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    if (!match.Success)
                    {
                        // Tenta pegar só número no nome
                        match = System.Text.RegularExpressions.Regex.Match(fileName, @"(\d+(?:\.\d+)?)");
                    }

                    if (!match.Success)
                    {
                        Dispatcher.Invoke(() => Log($"⚠ Não reconhecido: {Path.GetFileName(cbz)}"));
                        naoCasados++;
                        current++;
                        continue;
                    }

                    string chapterNum = match.Groups[1].Value.TrimStart('0');
                    if (string.IsNullOrEmpty(chapterNum)) chapterNum = "0";

                    // Procura no mapa (tenta com e sem decimais)
                    string? volNum = null;
                    volumeMap.TryGetValue(chapterNum, out volNum);

                    if (volNum == null)
                    {
                        // Tenta formato inteiro (ex: "1" para "1.0")
                        if (chapterNum.Contains('.'))
                            volumeMap.TryGetValue(chapterNum.Split('.')[0], out volNum);
                    }

                    if (volNum == null || volNum == "0")
                    {
                        Dispatcher.Invoke(() => Log($"⚠ Volume não encontrado para cap {chapterNum}: {Path.GetFileName(cbz)}"));
                        naoCasados++;
                        current++;
                        continue;
                    }

                    // Formata número do volume e capítulo
                    string volFolder = $"Volume {int.Parse(volNum):D2}";
                    string volPath = Path.Combine(path, volFolder);
                    Directory.CreateDirectory(volPath);

                    // Formata número do capítulo para nome da pasta
                    string chFormatted = chapterNum.Contains('.')
                        ? $"Capitulo {chapterNum.PadLeft(6, '0')}"
                        : $"Capitulo {int.Parse(chapterNum):D3}";

                    string chDest = Path.Combine(volPath, chFormatted);
                    Directory.CreateDirectory(chDest);

                    // Move o .cbz para dentro da pasta do volume
                    string cbzDest = Path.Combine(volPath, Path.GetFileName(cbz));
                    if (!File.Exists(cbzDest))
                        File.Move(cbz, cbzDest);

                    // Extrai o .cbz para a pasta do capítulo
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
                {
                    Log($"Organização finalizada. {current - naoCasados} ok, {naoCasados} não processado(s).");
                });
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