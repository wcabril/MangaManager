using System;
using System.IO;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Docnet.Core;
using Docnet.Core.Models;
using System.Linq;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Xml.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace MangaManager
{
    public partial class MainWindow : Window
    {
        string basePath = string.Empty;
        string zipPath = @"C:\Program Files\7-Zip\7z.exe";
        CancellationTokenSource? _cts;
        FileSystemWatcher? _watcher;
        string _currentMangaPath = string.Empty;
        bool _isAnimeUnitedMode = false;
        bool _isAnimeUnitedAdmin = false;
        // Catálogo da nuvem na última leitura. Null = não deu para perguntar.
        HashSet<string>? _cloudMangaNames;

        // Constantes para detecção de USB
        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

        public MainWindow()
        {
            InitializeComponent();
            ResetButtonStates();
            InitializeKccProfileCombo();
            InitializeCloudSizeCombo();

            // Limpa KccPath salvo se não for o executável c2e (ex: apontava para KCC.exe antigo)
            var savedKcc = Properties.Settings.Default.KccPath;
            if (!string.IsNullOrEmpty(savedKcc) &&
                Path.GetFileName(savedKcc).IndexOf("c2e", StringComparison.OrdinalIgnoreCase) < 0)
            {
                Properties.Settings.Default.KccPath = "";
                Properties.Settings.Default.Save();
            }

            // Registra o hook para detectar dispositivos USB
            SourceInitialized += (s, e) =>
            {
                var source = System.Windows.Interop.HwndSource.FromHwnd(
                    new System.Windows.Interop.WindowInteropHelper(this).Handle);
                source?.AddHook(WndProc);
            };

            var saved = Properties.Settings.Default.BasePath;
            if (!string.IsNullOrEmpty(saved) && Directory.Exists(saved))
            {
                basePath = saved;
                BasePathBox.Text = basePath;
                LoadMangas();
            }

            // Carrega configurações de email
            SenderEmailBox.Text     = Properties.Settings.Default.SenderEmail;
            KindleEmailBox.Text     = Properties.Settings.Default.KindleEmail;
            AppPasswordBox.Password = Properties.Settings.Default.SenderAppPassword;

            // Habilita botão se os campos já estiverem preenchidos
            EmailFields_Changed(this, null!);

            // Salva ao sair de cada campo
            SenderEmailBox.LostFocus += (s, e) => {
                Properties.Settings.Default.SenderEmail = SenderEmailBox.Text.Trim();
                Properties.Settings.Default.Save();
            };
            KindleEmailBox.LostFocus += (s, e) => {
                Properties.Settings.Default.KindleEmail = KindleEmailBox.Text.Trim();
                Properties.Settings.Default.Save();
            };
            AppPasswordBox.LostFocus += (s, e) => {
                Properties.Settings.Default.SenderAppPassword = AppPasswordBox.Password;
                Properties.Settings.Default.Save();
            };

            // Carrega configurações do AnimeUnited (a API URL vem fixa nas settings)
            AnimeUnitedEmailBox.Text = Properties.Settings.Default.AnimeUnitedUserEmail;
            AnimeUnitedEmailBox.LostFocus += (s, e) => RefreshAnimeUnitedAdminStatus();

            // O valor que vem compilado é só um marcador; mostrá-lo no campo faria
            // parecer que existe uma chave configurada.
            string savedKey = Properties.Settings.Default.AnimeUnitedApiKey ?? "";
            AnimeUnitedApiKeyBox.Password =
                savedKey.StartsWith("TROQUE", StringComparison.OrdinalIgnoreCase) ? "" : savedKey;
            AnimeUnitedApiKeyBox.LostFocus += (s, e) =>
            {
                AnimeUnitedFields_Changed(s, e);
                RefreshAnimeUnitedAdminStatus();
            };
            RefreshAnimeUnitedAdminStatus();

            Loaded += async (s, e) => await RunFirstRunWizardIfNeeded();
        }

        // ==============================
        // 🚀 PRIMEIRA EXECUÇÃO
        // ==============================
        private async Task RunFirstRunWizardIfNeeded()
        {
            bool hasFolder = !string.IsNullOrEmpty(Properties.Settings.Default.BasePath) &&
                             Directory.Exists(Properties.Settings.Default.BasePath);
            if (hasFolder) return;

            MessageBox.Show("Selecione a pasta onde estão seus mangás locais.", "MangaManager",
                MessageBoxButton.OK, MessageBoxImage.Information);
            SelectFolder_Click(this, new RoutedEventArgs());

            var isSupporter = MessageBox.Show("Você é Apoiador UnitedCast?", "UnitedCast",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (isSupporter != MessageBoxResult.Yes) return;

            var emailWindow = new EmailPromptWindow { Owner = this };
            if (emailWindow.ShowDialog() != true || string.IsNullOrWhiteSpace(emailWindow.Email)) return;

            string email = emailWindow.Email.Trim();
            AnimeUnitedEmailBox.Text = email;
            Properties.Settings.Default.AnimeUnitedUserEmail = email;
            Properties.Settings.Default.Save();

            try
            {
                var client = new AnimeUnitedClient(Properties.Settings.Default.AnimeUnitedApiUrl,
                    Properties.Settings.Default.AnimeUnitedApiKey, email);
                var access = await client.CheckAccessAsync();

                if (access.HasAccess)
                {
                    string greeting = string.IsNullOrWhiteSpace(access.Name) ? "" : $" {access.Name}";
                    MessageBox.Show(
                        $"Olá{greeting}. Obrigado por fazer parte do grupo de apoiadores do UnitedCast. Aproveite a leitura.",
                        "UnitedCast", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("Infelizmente não foi possível autenticar seu email", "UnitedCast",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Log($"AnimeUnited: could not validate email ({ex.Message}).");
                MessageBox.Show("Infelizmente não foi possível autenticar seu email", "UnitedCast",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            RefreshAnimeUnitedAdminStatus();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DEVICECHANGE)
            {
                int eventType = wParam.ToInt32();
                if (eventType == DBT_DEVICEARRIVAL || eventType == DBT_DEVICEREMOVECOMPLETE)
                {
                    // Aguarda o Windows montar o drive antes de verificar
                    Task.Delay(3000).ContinueWith(_ =>
                        Dispatcher.Invoke(() => RefreshKindleStatus()));
                }
            }
            return IntPtr.Zero;
        }

        private void RefreshKindleStatus()
        {
            string? kindlePath = FindKindlePath();
            bool kindleConnected = kindlePath != null;

            if (kindleConnected)
                Log($"📱 Kindle detected at: {kindlePath}");
            else
            {
                // Log de debug: lista todos os drives disponíveis
                var drives = DriveInfo.GetDrives().Where(d => d.IsReady)
                    .Select(d => $"{d.Name} [{d.VolumeLabel}]");
                Log($"📱 Kindle not found. Drives: {string.Join(", ", drives)}");
            }

            foreach (var item in MangaList.Items.OfType<MangaItem>())
            {
                if (kindlePath != null)
                {
                    string kindleDest = Path.Combine(kindlePath, "Mangas", item.Name);
                    item.ChkKindle = Directory.Exists(kindleDest) &&
                                     (Directory.GetFiles(kindleDest, "*.mobi").Length > 0 ||
                                      Directory.GetFiles(kindleDest, "*.epub").Length > 0 ||
                                      Directory.GetFiles(kindleDest, "*.azw3").Length > 0)
                                     ? "📱" : "";
                }
                else
                    item.ChkKindle = "";
            }
        }

        // ==============================
        // ⚙ KCC CLI
        // ==============================
        private void InitializeKccProfileCombo()
        {
            var profiles = new[]
            {
                "Kindle Paperwhite 1/2/3 (KPW)",
                "Kindle Paperwhite 4/5 (KPW5)",
                "Kindle Paperwhite 11th Gen (KPW5)",
                "Kindle Oasis (KO)",
                "Kindle Scribe (KS)",
                "Kindle Basic 2022 (K11)",
            };

            KccProfileCombo.ItemsSource = profiles;

            var saved = Properties.Settings.Default.KindleDevice;
            var match = profiles.FirstOrDefault(p => p == saved);
            KccProfileCombo.SelectedItem = match ?? profiles[1]; // default KPW5

            KccProfileCombo.SelectionChanged += (s, e) =>
            {
                if (KccProfileCombo.SelectedItem is string selected)
                {
                    Properties.Settings.Default.KindleDevice = selected;
                    Properties.Settings.Default.Save();
                }
            };
        }

        // Teto de resolução do Process & Convert. Separado do perfil Kindle de propósito:
        // o que sai daqui é o que vai para a nuvem, lido no celular e no PC.
        private static readonly (string Label, int Pixels)[] CloudSizes =
        {
            ("Nuvem: 1200px (menor arquivo)", 1200),
            ("Nuvem: 1600px (recomendado)",   1600),
            ("Nuvem: 2000px",                 2000),
            ("Nuvem: 2400px",                 2400),
            ("Nuvem: tamanho original",          0),
        };

        private void InitializeCloudSizeCombo()
        {
            CloudSizeCombo.ItemsSource = CloudSizes.Select(s => s.Label).ToArray();

            int saved = Properties.Settings.Default.CloudMaxPixels;
            int index = Array.FindIndex(CloudSizes, s => s.Pixels == saved);
            CloudSizeCombo.SelectedIndex = index >= 0 ? index : 1; // default 1600

            CloudSizeCombo.SelectionChanged += (s, e) =>
            {
                if (CloudSizeCombo.SelectedIndex >= 0)
                {
                    Properties.Settings.Default.CloudMaxPixels = CloudSizes[CloudSizeCombo.SelectedIndex].Pixels;
                    Properties.Settings.Default.Save();
                }
            };
        }

        // 0 = sem limite (mantém o tamanho original da página).
        private int GetCloudMaxPixels() =>
            CloudSizeCombo.SelectedIndex >= 0 ? CloudSizes[CloudSizeCombo.SelectedIndex].Pixels
                                              : Properties.Settings.Default.CloudMaxPixels;

        // Resolução do aparelho, usada pelo Process for Kindle. O KCC é chamado com -n
        // (--noprocessing), então quem dimensiona para o Kindle é o app, aqui.
        private (int W, int H) GetKindleDimensions() => KccProfileCombo.SelectedItem?.ToString() switch
        {
            "Kindle Paperwhite 1/2/3 (KPW)"        => (758,  1024),
            "Kindle Paperwhite 4/5 (KPW5)"         => (1072, 1448),
            "Kindle Paperwhite 11th Gen (KPW5)"    => (1072, 1448),
            "Kindle Oasis (KO)"                    => (1264, 1680),
            "Kindle Scribe (KS)"                   => (1860, 2480),
            "Kindle Basic 2022 (K11)"              => (1072, 1448),
            _                                      => (1072, 1448),
        };

        private string GetKccProfile() => KccProfileCombo.SelectedItem?.ToString() switch
        {
            "Kindle Paperwhite 1/2/3 (KPW)"        => "KPW",
            "Kindle Paperwhite 4/5 (KPW5)"         => "KPW5",
            "Kindle Paperwhite 11th Gen (KPW5)"    => "KPW5",
            "Kindle Oasis (KO)"                    => "KO",
            "Kindle Scribe (KS)"                   => "KS",
            "Kindle Basic 2022 (K11)"              => "K11",
            _                                      => "KPW5",
        };

        // kcc_c2e_*.exe do GitHub releases — usado para conversão headless
        private string? FindKccCli()
        {
            // 1. Caminho salvo nas settings — só aceita se for realmente o c2e (não o GUI)
            var saved = Properties.Settings.Default.KccPath;
            if (!string.IsNullOrEmpty(saved) && File.Exists(saved) &&
                Path.GetFileName(saved).IndexOf("c2e", StringComparison.OrdinalIgnoreCase) >= 0)
                return saved;

            // 2. Pastas comuns onde o usuário pode ter salvo o executável
            string[] searchRoots = {
                @"D:\Mangas",
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                @"C:\Program Files\KCC",
                @"C:\Program Files (x86)\KCC",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "KCC"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KCC"),
            };

            // Procura kcc_c2e_*.exe (GitHub releases) e kcc-c2e.exe (pip legado)
            string[] patterns = { "kcc_c2e_*.exe", "kcc-c2e.exe", "kcc-c2e" };

            foreach (var root in searchRoots.Where(Directory.Exists))
            {
                foreach (var pattern in patterns)
                {
                    var exe = Directory.GetFiles(root, pattern, SearchOption.TopDirectoryOnly)
                                       .OrderByDescending(f => f)
                                       .FirstOrDefault();
                    if (exe != null)
                    {
                        Properties.Settings.Default.KccPath = exe;
                        Properties.Settings.Default.Save();
                        return exe;
                    }
                }
            }

            return null;
        }

        // KCC_*.exe standalone — usado para abrir a GUI
        private string? FindKccGui()
        {
            string[] searchRoots = {
                @"C:\Program Files\KCC",
                @"C:\Program Files (x86)\KCC",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "KCC"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KCC"),
            };

            foreach (var root in searchRoots.Where(Directory.Exists))
            {
                var exe = Directory.GetFiles(root, "KCC*.exe", SearchOption.AllDirectories)
                                   .OrderByDescending(f => f)
                                   .FirstOrDefault();
                if (exe != null) return exe;
            }

            return null;
        }

        // Cada alvo grava na sua própria pasta, espelhando a árvore volume/capítulo.
        // Antes a conversão era feita no lugar: o segundo alvo encontrava os .jpg do
        // primeiro ("name already taken") e já não havia página original para converter.
        private const string CloudFolderName  = "Cloud";
        private const string KindleFolderName = "Kindle";

        // Volumes de uma raiz. O padrão "* - Volume *" já exclui Cloud/, Kindle/,
        // Converted/ e Originals/, que ficam lado a lado na pasta do mangá.
        private static string[] GetVolumeDirs(string root) =>
            Directory.Exists(root)
                ? Directory.GetDirectories(root, "* - Volume *").OrderBy(v => v).ToArray()
                : Array.Empty<string>();

        private static bool IsPageImage(string f) =>
            f.EndsWith(".jpg",  StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".png",  StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);

        private static bool HasPages(string dir) =>
            Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any(IsPageImage);

        // Volume já empacotado: há <Volume>*.mobi em /Converted mais novo que a página
        // mais recente do volume. Capítulo novo envelhece o MOBI, que então é refeito.
        private static bool HasFreshMobi(string convertedFolder, string volumeDir)
        {
            try
            {
                var mobis = Directory.Exists(convertedFolder)
                    ? Directory.GetFiles(convertedFolder, Path.GetFileName(volumeDir) + "*.mobi")
                    : Array.Empty<string>();
                if (mobis.Length == 0) return false;

                var pages = Directory.GetFiles(volumeDir, "*.*", SearchOption.AllDirectories);
                if (pages.Length == 0) return true;

                return mobis.Max(File.GetLastWriteTimeUtc) >= pages.Max(File.GetLastWriteTimeUtc);
            }
            catch { return false; }
        }

        // Saída já pronta: existe e não é mais velha que a origem. Comparar a data
        // evita tratar como pronta uma página que foi reextraída depois.
        private static bool IsUpToDate(string output, string source)
        {
            try
            {
                return File.Exists(output) &&
                       File.GetLastWriteTimeUtc(output) >= File.GetLastWriteTimeUtc(source);
            }
            catch { return false; }
        }

        // De onde o KCC/upload devem ler: a saída daquele alvo, quando o processamento
        // já rodou; senão os volumes extraídos, como era antes.
        private static string ResolveSourceRoot(string mangaPath, string outputFolder)
        {
            string candidate = Path.Combine(mangaPath, outputFolder);
            return GetVolumeDirs(candidate).Length > 0 ? candidate : mangaPath;
        }

        // ComicInfo.xml acompanha as páginas: o KCC tira dele os metadados do MOBI e o
        // upload manda o do volume para o servidor — ambos leem a pasta de saída.
        private static int MirrorComicInfo(string sourceRoot, string outputRoot)
        {
            int copied = 0;
            foreach (var vol in GetVolumeDirs(sourceRoot))
                foreach (var xml in Directory.GetFiles(vol, "ComicInfo.xml", SearchOption.AllDirectories))
                {
                    string dest = Path.Combine(outputRoot, Path.GetRelativePath(sourceRoot, xml));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(xml, dest, overwrite: true);
                    copied++;
                }
            return copied;
        }

        // ==============================
        // 🔞 NSFW  ·  🔄 EM ANDAMENTO  (marcas por mangá)
        // ==============================

        // O selo mora no site do AnimeUnited. Baixamos uma vez para o LocalAppData
        // para não depender da rede a cada capa desenhada.
        private const string NsfwBadgeUrl =
            "https://www.animeunited.com.br/stybycmo/inc/temporada/assets/img/NSFW.png";

        private static string NsfwBadgeCachePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MangaManager", "NSFW.png");

        private const string CoverName         = "cover.jpg";
        private const string CoverOriginalName = "cover_original.jpg";

        private record MangaFlags
        {
            public bool Nsfw { get; init; }
            public bool Ongoing { get; init; }
        }

        private static string FlagsFilePath(string mangaPath) =>
            Path.Combine(mangaPath, "manga_flags.json");

        private static MangaFlags LoadMangaFlags(string mangaPath)
        {
            try
            {
                string path = FlagsFilePath(mangaPath);
                if (!File.Exists(path)) return new MangaFlags();
                return JsonSerializer.Deserialize<MangaFlags>(File.ReadAllText(path)) ?? new MangaFlags();
            }
            catch { return new MangaFlags(); }
        }

        private static void SaveMangaFlags(string mangaPath, MangaFlags flags)
        {
            try
            {
                File.WriteAllText(FlagsFilePath(mangaPath),
                    JsonSerializer.Serialize(flags, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        // Obra em andamento: cada passo do pipeline refaz só o que falta.
        private static bool IsOngoing(string mangaPath) => LoadMangaFlags(mangaPath).Ongoing;

        private System.Windows.Media.ImageSource? _nsfwBadgeImage;

        private async Task<string?> EnsureNsfwBadgeFileAsync()
        {
            string cache = NsfwBadgeCachePath;
            if (File.Exists(cache) && new FileInfo(cache).Length > 0) return cache;

            var bytes = await DownloadImageBytes(NsfwBadgeUrl);
            if (bytes == null)
            {
                Log("⚠ Could not download the NSFW badge — check the connection.");
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            await File.WriteAllBytesAsync(cache, bytes);
            return cache;
        }

        private async Task EnsureNsfwBadgeImageAsync()
        {
            if (_nsfwBadgeImage != null) return;
            string? path = await EnsureNsfwBadgeFileAsync();
            if (path != null) _nsfwBadgeImage = LoadImageFromFile(path);
        }

        // Image.FromFile segura o arquivo aberto; lemos para a memória e soltamos.
        private static System.Drawing.Bitmap LoadBitmapCopy(string path)
        {
            using var fs = File.OpenRead(path);
            using var loaded = System.Drawing.Image.FromStream(fs);
            return new System.Drawing.Bitmap(loaded);
        }

        // Compõe o selo no canto superior direito da capa. A capa limpa fica em
        // cover_original.jpg e é sempre dela que partimos: assim o selo não é
        // aplicado duas vezes nem a capa perde qualidade a cada clique.
        private async Task<bool> ApplyNsfwBadgeToCover(string mangaPath)
        {
            string cover    = Path.Combine(mangaPath, CoverName);
            string original = Path.Combine(mangaPath, CoverOriginalName);

            if (!File.Exists(cover) && !File.Exists(original))
            {
                Log("⚠ This manga has no cover.jpg — the badge was saved as a mark only.");
                return false;
            }

            string? badgeFile = await EnsureNsfwBadgeFileAsync();
            if (badgeFile == null) return false;

            try
            {
                if (!File.Exists(original)) File.Copy(cover, original);

                using var baseImg = LoadBitmapCopy(original);
                using var seal    = LoadBitmapCopy(badgeFile);

                // Capa indexada (alguns PNG) não aceita Graphics: redesenhamos num
                // canvas de 32 bits antes de compor.
                using var canvas = new System.Drawing.Bitmap(baseImg.Width, baseImg.Height,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                int sealW  = Math.Max(1, (int)(baseImg.Width * 0.30f));
                int sealH  = Math.Max(1, (int)(seal.Height * ((float)sealW / seal.Width)));
                int margin = Math.Max(2, (int)(baseImg.Width * 0.03f));

                using (var g = System.Drawing.Graphics.FromImage(canvas))
                {
                    g.InterpolationMode   = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.CompositingQuality  = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    g.DrawImage(baseImg, 0, 0, baseImg.Width, baseImg.Height);
                    g.DrawImage(seal, baseImg.Width - sealW - margin, margin, sealW, sealH);
                }

                string temp = cover + ".tmp";
                SaveJpeg(canvas, temp, CoverJpegQuality, grayscale: false);
                if (File.Exists(cover)) File.Delete(cover);
                File.Move(temp, cover);
                return true;
            }
            catch (Exception ex)
            {
                Log($"⚠ Could not apply the NSFW badge: {ex.Message}");
                return false;
            }
        }

        // Volta a capa limpa. Sem cover_original.jpg não há o que restaurar — a capa
        // atual nunca recebeu o selo.
        private bool RemoveNsfwBadgeFromCover(string mangaPath)
        {
            string cover    = Path.Combine(mangaPath, CoverName);
            string original = Path.Combine(mangaPath, CoverOriginalName);
            if (!File.Exists(original)) return false;

            try
            {
                File.Copy(original, cover, overwrite: true);
                File.Delete(original);
                return true;
            }
            catch (Exception ex)
            {
                Log($"⚠ Could not restore the clean cover: {ex.Message}");
                return false;
            }
        }

        // A capa serve o catálogo do site: vale mais qualidade que nas páginas.
        private const int CoverJpegQuality = 92;

        // WebP na mesma qualidade nominal do JPEG. Medido em 10 páginas deste acervo,
        // contra o que o app gravava antes (JPEG q70, cinza de 1 canal nas páginas P&B):
        // 1,87 MB → 1,19 MB, 36% menor, e com erro RMS 2,48 contra 5,32. Ganha nos dois
        // lados ao mesmo tempo, então não há troca a ponderar aqui.
        private const int ProcessWebpQuality = 70;

        // Só a nuvem recebe WebP. O Kindle continua em JPEG: o KCC não lê WebP e
        // reconverteria tudo, e o MOBI não ganharia nada com isso.
        private static bool UsesWebp(string outputFolder) => outputFolder == CloudFolderName;

        // Não convertemos para cinza antes de gravar: o WebP guarda a crominância em
        // plano próprio, e numa página P&B esse plano fica liso e custa quase nada —
        // o arquivo sai do tamanho de um de canal único sem o trabalho da conversão.
        private static void SaveWebp(System.Drawing.Bitmap bmp, string destPath, int quality) =>
            WebpWriter.Save(bmp, destPath, quality);

        // Tira da saída a mesma página gravada em outra extensão. Sem isto, trocar o
        // formato do alvo deixaria 001.jpg ao lado de 001.webp e a nuvem receberia a
        // página duas vezes.
        private static void DropOtherExtensions(string keepFile)
        {
            try
            {
                string dir  = Path.GetDirectoryName(keepFile)!;
                string name = Path.GetFileNameWithoutExtension(keepFile);
                foreach (var f in Directory.GetFiles(dir, name + ".*"))
                    if (IsPageImage(f) && !f.Equals(keepFile, StringComparison.OrdinalIgnoreCase))
                        File.Delete(f);
            }
            catch { }
        }

        // ── O que já subiu para a nuvem ──────────────────────────────────────
        // A API lista volumes, não capítulos; este arquivo é o que diz, capítulo a
        // capítulo, o que já foi enviado e se mudou desde então.
        private static string UploadStateFilePath(string mangaPath) =>
            Path.Combine(mangaPath, "upload_state.json");

        private static string UploadKey(string volumeName, string chapterName) =>
            $"{volumeName}/{chapterName}";

        private static Dictionary<string, string> LoadUploadState(string mangaPath)
        {
            try
            {
                string path = UploadStateFilePath(mangaPath);
                if (!File.Exists(path)) return new();
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
            }
            catch { return new(); }
        }

        private static void SaveUploadState(string mangaPath, Dictionary<string, string> state)
        {
            try
            {
                File.WriteAllText(UploadStateFilePath(mangaPath),
                    JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        // Digital do capítulo: quantidade de arquivos, bytes e a data mais recente.
        // Página acrescentada ou reprocessada muda a digital e o capítulo volta a subir.
        private static string ChapterFingerprint(string chapterDir)
        {
            try
            {
                var files = Directory.GetFiles(chapterDir);
                long bytes = 0, newest = 0;
                foreach (var f in files)
                {
                    var info = new FileInfo(f);
                    bytes += info.Length;
                    newest = Math.Max(newest, info.LastWriteTimeUtc.Ticks);
                }
                return $"{files.Length}:{bytes}:{newest}";
            }
            catch { return Guid.NewGuid().ToString("N"); }  // ilegível = trata como mudado
        }

        // ☁ Nuvem: teto próprio, cor preservada, sem tratamento de e-ink (lê-se em celular e PC).
        private async void ProcessCloud_Click(object sender, RoutedEventArgs e)
        {
            int max = GetCloudMaxPixels();
            await ProcessImages(max, max, applyEInk: false, outputFolder: CloudFolderName,
                sizeLabel: max > 0 ? $"cloud, max {max}px" : "cloud, original size");
        }

        // ⚙ Kindle: tamanho do perfil selecionado + tratamento de e-ink. O KCC só empacota.
        private async void ProcessKindle_Click(object sender, RoutedEventArgs e)
        {
            var (w, h) = GetKindleDimensions();
            await ProcessImages(w, h, applyEInk: true, outputFolder: KindleFolderName,
                sizeLabel: $"Kindle {GetKccProfile()}, {w}×{h}");
        }

        private async Task ProcessImages(int maxW, int maxH, bool applyEInk,
                                         string outputFolder, string sizeLabel)
        {
            var path = GetSelectedPath();
            if (path == null) return;

            string outputRoot = Path.Combine(path, outputFolder);

            // Sempre a partir dos volumes extraídos — nunca da saída do outro alvo.
            var images = GetVolumeDirs(path)
                .SelectMany(v => Directory.GetFiles(v, "*.*", SearchOption.AllDirectories))
                .Where(f => f.EndsWith(".jpg",  StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".png",  StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                // A capa é colorida e serve o catálogo (GET /api/mangas/{name}/cover).
                // Passá-la pelo tratamento de e-ink a deixaria cinza.
                .Where(f => !Path.GetFileNameWithoutExtension(f)
                                 .Equals("cover", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToArray();

            if (images.Length == 0)
            {
                Log("No images found. Please extract first.");
                return;
            }

            // Em andamento: página que já existe na saída DESTE alvo e é mais nova que a
            // origem fica como está. Como cada alvo tem a sua pasta, ter feito a nuvem
            // não adianta nada para o Kindle — esse lado processa tudo na primeira vez.
            bool ongoing = IsOngoing(path);

            bool webp         = UsesWebp(outputFolder);
            string outputExt  = webp ? ".webp" : ".jpg";
            string formatLabel = webp ? $"WebP q{ProcessWebpQuality}" : $"JPEG q{ProcessJpegQuality}";

            Log($"Processing {images.Length} image(s) — {sizeLabel}, {formatLabel}...");
            Log($"  Output → {outputRoot}");
            if (ongoing) Log($"  🔄 Ongoing: only pages missing from /{outputFolder} will be processed.");
            BtnOpenKCC.IsEnabled = false;
            BtnResize.IsEnabled = false;
            BtnCloud.IsEnabled = false;
            var token = StartOperation();

            await Task.Run(() =>
            {
                int total = images.Length;
                int current = 0, converted = 0, failed = 0, skipped = 0, colorPages = 0, kept = 0, reused = 0;
                long bytesBefore = 0, bytesAfter = 0;

                // Destinos já gravados NESTA passada. Rodar de novo sobrescreve a saída
                // anterior; o conflito só existe entre duas origens do mesmo capítulo.
                var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var img in images)
                {
                    if (token.IsCancellationRequested) break;

                    string relative = Path.GetRelativePath(path, img);
                    string dest     = Path.Combine(outputRoot, Path.ChangeExtension(relative, outputExt));
                    // Quando o original é mantido como está, ele vai para a saída com a
                    // extensão de origem — um .png renomeado para .jpg não abriria.
                    string keepDest = Path.ChangeExtension(dest, Path.GetExtension(img));

                    // Ex.: existe 001.png e 001.jpg no mesmo capítulo — são páginas
                    // distintas que dariam o mesmo destino. A primeira fica; esta é pulada.
                    if (!written.Add(dest))
                    {
                        skipped++;
                        Dispatcher.Invoke(() => Log($"  ⚠ skipped (name already taken): {Path.GetFileName(img)}"));
                    }
                    else if (ongoing && (IsUpToDate(dest, img) || IsUpToDate(keepDest, img)))
                    {
                        reused++;
                    }
                    else
                    {
                        try
                        {
                            long sizeBefore = new FileInfo(img).Length;
                            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                            // Copia para a memória e solta o arquivo: Image.FromFile mantém o
                            // handle aberto, e gravar no mesmo caminho falharia com erro do GDI+.
                            System.Drawing.Bitmap original;
                            using (var fs = File.OpenRead(img))
                            using (var loaded = System.Drawing.Image.FromStream(fs))
                                original = new System.Drawing.Bitmap(loaded);

                            using (original)
                            using (var cropped = CropBorders(original))
                            {
                                // Nenhum lado passa do teto, e nunca amplia: página menor que o
                                // teto já está no limite do detalhe que o scan tem — ampliar só
                                // engorda o arquivo e borra. maxW <= 0 = manter original.
                                float ratio = maxW <= 0 ? 1f
                                    : Math.Min(1f, Math.Min((float)maxW / cropped.Width,
                                                            (float)maxH / cropped.Height));
                                int newW = Math.Max(1, (int)(cropped.Width  * ratio));
                                int newH = Math.Max(1, (int)(cropped.Height * ratio));

                                using var resized = new System.Drawing.Bitmap(newW, newH);
                                using (var g = System.Drawing.Graphics.FromImage(resized))
                                {
                                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                    g.DrawImage(cropped, 0, 0, newW, newH);
                                }

                                // Grava num temporário para poder comparar antes de
                                // substituir: quando a origem já é um JPEG, gravar direto
                                // no destino destrói o original e não há como voltar atrás.
                                string temp = dest + ".tmp";

                                if (applyEInk)
                                {
                                    // Kindle: tela de e-ink em tons de cinza — gama e sharpen
                                    // valem para todas as páginas, inclusive as coloridas.
                                    using var processed = ProcessForEInk(resized);
                                    SaveJpeg(processed, temp, ProcessJpegQuality, grayscale: true);
                                }
                                else if (webp)
                                {
                                    // Nuvem: lida em celular e PC, onde o WebP é lido por
                                    // qualquer navegador. A cor é preservada sempre.
                                    SaveWebp(resized, temp, ProcessWebpQuality);
                                }
                                else
                                {
                                    // Nuvem em JPEG: página colorida fica colorida;
                                    // a P&B vira JPEG de 1 canal só para economizar espaço.
                                    bool color = HasColor(resized);
                                    SaveJpeg(resized, temp, ProcessJpegQuality, grayscale: !color);
                                    if (color) colorPages++;
                                }

                                // Rede de proteção: se nada foi redimensionado e o arquivo
                                // regravado ficou MAIOR, a origem já estava mais comprimida
                                // que o nosso alvo. Reescrever só perderia qualidade e
                                // ocuparia mais espaço — o original fica.
                                long tamNovo = new FileInfo(temp).Length;
                                bool semRedimensionar = ratio >= 1f;

                                if (semRedimensionar && tamNovo >= sizeBefore)
                                {
                                    File.Delete(temp);
                                    // A saída precisa da página mesmo assim: copia o
                                    // original como está, sem reencodar.
                                    File.Copy(img, keepDest, overwrite: true);
                                    DropOtherExtensions(keepDest);
                                    kept++;
                                    bytesBefore += sizeBefore;
                                    bytesAfter  += sizeBefore;
                                    current++;
                                    Dispatcher.Invoke(() =>
                                    {
                                        ProgressBar.Value = (double)current / total * 100;
                                        ProgressText.Text = $"{current} / {total}";
                                    });
                                    continue;
                                }

                                if (File.Exists(dest)) File.Delete(dest);
                                File.Move(temp, dest);
                                // O original fica intacto na pasta do volume — é a origem
                                // do outro alvo. O que sai é a mesma página gravada antes
                                // em outro formato, aqui na saída.
                                DropOtherExtensions(dest);

                                bytesBefore += sizeBefore;
                                bytesAfter  += tamNovo;
                                converted++;
                            }
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            if (failed <= 5)
                                Dispatcher.Invoke(() => Log($"  ⚠ {Path.GetFileName(img)}: {ex.Message}"));
                        }
                    }

                    current++;
                    Dispatcher.Invoke(() =>
                    {
                        ProgressBar.Value = (double)current / total * 100;
                        ProgressText.Text = $"{current} / {total}";
                    });
                }

                int comicInfos = MirrorComicInfo(path, outputRoot);

                Dispatcher.Invoke(() =>
                {
                    if (applyEInk)
                        Log($"✓ Processed {converted} image(s) — {sizeLabel}, grayscale for e-ink.");
                    else if (webp)
                        Log($"✓ Processed {converted} image(s) — {sizeLabel}, WebP q{ProcessWebpQuality}.");
                    else
                        Log($"✓ Processed {converted} image(s) — {sizeLabel} " +
                            $"({colorPages} in color, {converted - colorPages} grayscale).");
                    if (bytesBefore > 0)
                    {
                        // Antes isto sempre dizia "smaller", mesmo quando o volume
                        // crescia — e foi assim que o problema passou despercebido.
                        double variacao = (1 - (double)bytesAfter / bytesBefore) * 100;
                        string veredito = variacao >= 0
                            ? $"{variacao:N0}% smaller"
                            : $"{-variacao:N0}% LARGER";
                        Log($"  {bytesBefore / 1024.0 / 1024.0:N1} MB → {bytesAfter / 1024.0 / 1024.0:N1} MB ({veredito})");
                    }
                    if (reused     > 0) Log($"  🔄 {reused} page(s) already in /{outputFolder} were kept.");
                    if (kept       > 0) Log($"  {kept} kept as-is (already smaller than re-encoding).");
                    if (skipped    > 0) Log($"  {skipped} skipped (two pages with the same name in one chapter).");
                    if (comicInfos > 0) Log($"  {comicInfos} ComicInfo.xml copied to /{outputFolder}.");
                    if (failed  > 0) Log($"  ⚠ {failed} failed.");

                    BtnOpenKCC.IsEnabled = true;
                    BtnResize.IsEnabled = true;
                    BtnCloud.IsEnabled = true;
                    EndOperation();
                    RefreshSelectedStatus();
                });
            });
        }

        // Qualidade do JPEG gravado pelo processamento.
        //
        // NÃO USE 75. Medido numa página de 1125x1600 com o JpegBitmapEncoder:
        //     q50 143 KB · q60 156 KB · q70 176 KB · q75 270 KB · q80 212 KB · q85 235 KB
        // O 75 sai MAIOR que o 80 e que o 85. É o valor padrão da classe, e ao
        // recebê-lo explicitamente o encoder do WPF toma outro caminho interno.
        // Era o que fazia o passo engordar os volumes em vez de reduzi-los.
        private const int ProcessJpegQuality = 70;

        // Uma página é "colorida" se uma fração relevante dos pixels amostrados tiver canais
        // realmente diferentes entre si. A tolerância alta evita que ruído de JPEG num scan
        // P&B (que sai levemente colorido) faça a página inteira ser tratada como colorida.
        private static bool HasColor(System.Drawing.Bitmap bmp,
            int channelTolerance = 24, float minColoredRatio = 0.005f)
        {
            int step = Math.Max(1, Math.Min(bmp.Width, bmp.Height) / 128);
            int sampled = 0, colored = 0;

            for (int y = 0; y < bmp.Height; y += step)
                for (int x = 0; x < bmp.Width; x += step)
                {
                    var c = bmp.GetPixel(x, y);
                    int spread = Math.Max(Math.Abs(c.R - c.G),
                                 Math.Max(Math.Abs(c.G - c.B), Math.Abs(c.R - c.B)));
                    if (spread > channelTolerance) colored++;
                    sampled++;
                }

            return sampled > 0 && (float)colored / sampled >= minColoredRatio;
        }

        // Grava JPEG. Páginas P&B saem com um canal só (Gray8) — depois do ProcessForEInk
        // guardar três canais idênticos é desperdício, e o encoder do WPF aceita Gray8
        // direto, coisa que o System.Drawing não faz.
        private static void SaveJpeg(System.Drawing.Bitmap bmp, string destPath, int quality,
                                     bool grayscale)
        {
            BitmapSource source;
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
                ms.Position = 0;
                source = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }

            if (grayscale)
                source = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);

            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(source));

            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write);
            encoder.Save(fs);
        }

        // ==============================
        // 🖼 IMAGE PROCESSING
        // ==============================
        // blackThreshold : pixel é "borda preta"  se R,G,B < blackThreshold
        // whiteThreshold : pixel é "borda branca" se R,G,B > 255-whiteThreshold
        //   → valor alto captura artefatos JPEG (~200,200,200) que ficam em margens "brancas"
        // borderRatio    : % mínima de pixels de borda para a linha/coluna ser cortada
        private System.Drawing.Bitmap CropBorders(System.Drawing.Bitmap bmp,
            int blackThreshold = 20, int whiteThreshold = 45, float borderRatio = 0.95f)
        {
            int top = 0, bottom = bmp.Height - 1, left = 0, right = bmp.Width - 1;

            bool IsBorderPixel(System.Drawing.Color c) =>
                (c.R < blackThreshold  && c.G < blackThreshold  && c.B < blackThreshold) ||
                (c.R > 255 - whiteThreshold && c.G > 255 - whiteThreshold && c.B > 255 - whiteThreshold);

            bool RowIsBorder(int y)
            {
                int count = 0;
                for (int x = 0; x < bmp.Width; x++)
                    if (IsBorderPixel(bmp.GetPixel(x, y))) count++;
                return (float)count / bmp.Width >= borderRatio;
            }

            bool ColIsBorder(int x)
            {
                int count = 0;
                for (int y = 0; y < bmp.Height; y++)
                    if (IsBorderPixel(bmp.GetPixel(x, y))) count++;
                return (float)count / bmp.Height >= borderRatio;
            }

            while (top    < bottom && RowIsBorder(top))    top++;
            while (bottom > top    && RowIsBorder(bottom)) bottom--;
            while (left   < right  && ColIsBorder(left))   left++;
            while (right  > left   && ColIsBorder(right))  right--;

            var rect = new System.Drawing.Rectangle(left, top, right - left + 1, bottom - top + 1);

            // Numa página em branco TODA linha é borda, então os laços acima andam até
            // top == bottom e left == right: a sobra é 1×1 e a página desaparece. Só
            // vale cortar o que ainda deixa a maior parte da página de pé.
            bool degenerado = rect.Width  < bmp.Width  * 0.1f ||
                              rect.Height < bmp.Height * 0.1f;

            return (rect.Width <= 0 || rect.Height <= 0 || degenerado)
                ? bmp
                : bmp.Clone(rect, bmp.PixelFormat);
        }

        private System.Drawing.Bitmap ProcessForEInk(System.Drawing.Bitmap src)
        {
            var result = new System.Drawing.Bitmap(src.Width, src.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            var cm = new System.Drawing.Imaging.ColorMatrix(new float[][]
            {
                new float[] { 0.299f, 0.299f, 0.299f, 0, 0 },
                new float[] { 0.587f, 0.587f, 0.587f, 0, 0 },
                new float[] { 0.114f, 0.114f, 0.114f, 0, 0 },
                new float[] { 0,      0,      0,      1, 0 },
                new float[] { 0,      0,      0,      0, 1 },
            });
            var attrs = new System.Drawing.Imaging.ImageAttributes();
            attrs.SetColorMatrix(cm);

            using (var g = System.Drawing.Graphics.FromImage(result))
                g.DrawImage(src, new System.Drawing.Rectangle(0, 0, src.Width, src.Height),
                    0, 0, src.Width, src.Height, System.Drawing.GraphicsUnit.Pixel, attrs);

            ApplyGamma(result, 1.5f);
            return ApplySharpen(result);
        }

        private void ApplyGamma(System.Drawing.Bitmap bmp, float gamma)
        {
            byte[] table = new byte[256];
            for (int i = 0; i < 256; i++)
                table[i] = (byte)Math.Min(255, (int)(255.0 * Math.Pow(i / 255.0, 1.0 / gamma)));

            var data = bmp.LockBits(
                new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
                System.Drawing.Imaging.ImageLockMode.ReadWrite,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            unsafe
            {
                byte* ptr = (byte*)data.Scan0;
                int bytes = Math.Abs(data.Stride) * bmp.Height;
                for (int i = 0; i < bytes; i += 4)
                {
                    ptr[i]     = table[ptr[i]];
                    ptr[i + 1] = table[ptr[i + 1]];
                    ptr[i + 2] = table[ptr[i + 2]];
                }
            }
            bmp.UnlockBits(data);
        }

        private System.Drawing.Bitmap ApplySharpen(System.Drawing.Bitmap src)
        {
            float[,] k = { { 0, -1, 0 }, { -1, 5, -1 }, { 0, -1, 0 } };
            var result = new System.Drawing.Bitmap(src.Width, src.Height);
            for (int y = 1; y < src.Height - 1; y++)
                for (int x = 1; x < src.Width - 1; x++)
                {
                    float r = 0, g = 0, b = 0;
                    for (int ky = -1; ky <= 1; ky++)
                        for (int kx = -1; kx <= 1; kx++)
                        {
                            var px = src.GetPixel(x + kx, y + ky);
                            r += px.R * k[ky + 1, kx + 1];
                            g += px.G * k[ky + 1, kx + 1];
                            b += px.B * k[ky + 1, kx + 1];
                        }
                    result.SetPixel(x, y, System.Drawing.Color.FromArgb(Clamp(r), Clamp(g), Clamp(b)));
                }
            return result;
        }

        private static int Clamp(float v) => Math.Max(0, Math.Min(255, (int)v));

        private async void OpenKCC_Click(object sender, RoutedEventArgs e)
        {
            var path = GetSelectedPath();
            if (path == null) return;

            string? kccExe = FindKccCli();
            if (kccExe == null)
            {
                Log("⚠ kcc_c2e not found.");
                var result = MessageBox.Show(
                    "kcc_c2e not found.\n\n" +
                    "You need the CLI tool (not the GUI).\n\n" +
                    "Download kcc_c2e_10.1.3.exe from:\n" +
                    "https://github.com/ciromattia/kcc/releases\n\n" +
                    "Click OK to locate the file manually.",
                    "kcc_c2e Not Found",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.OK) return;

                var dialog = new WinForms.OpenFileDialog
                {
                    Title = "Locate kcc_c2e_*.exe",
                    Filter = "kcc_c2e|kcc_c2e*.exe;kcc-c2e.exe|All executables|*.exe",
                };
                if (dialog.ShowDialog() != WinForms.DialogResult.OK || !File.Exists(dialog.FileName))
                {
                    Log("⚠ kcc_c2e not selected. Aborting.");
                    return;
                }
                kccExe = dialog.FileName;
                Properties.Settings.Default.KccPath = kccExe;
                Properties.Settings.Default.Save();
                Log($"✓ kcc_c2e saved: {kccExe}");
            }

            // Empacota as páginas tratadas para e-ink, se o passo Kindle já rodou;
            // senão os volumes extraídos, como era antes.
            string sourceRoot = ResolveSourceRoot(path, KindleFolderName);
            var volumes = GetVolumeDirs(sourceRoot);
            if (volumes.Length == 0)
            {
                Log("No volume folders found.");
                return;
            }

            string profile      = GetKccProfile();
            string convertedFolder = Path.Combine(path, "Converted");
            Directory.CreateDirectory(convertedFolder);

            // Em andamento: volume cujo MOBI já está em dia não volta para o KCC.
            if (IsOngoing(path))
            {
                var pending = volumes.Where(v => !HasFreshMobi(convertedFolder, v)).ToArray();
                int ready = volumes.Length - pending.Length;
                if (ready > 0) Log($"🔄 Ongoing: {ready} volume(s) already have an up-to-date MOBI.");

                if (pending.Length == 0)
                {
                    Log("✓ Nothing to convert — every volume is already in /Converted.");
                    return;
                }
                volumes = pending;
            }

            // -n  = no image processing (images already resized in previous step)
            // -m  = manga style (right-to-left)
            // -f MOBI = output format
            // -o  = output folder
            var volArgs = string.Join(" ", volumes.Select(v => $"\"{v}\""));
            string args = $"-p {profile} -m -n -f MOBI -o \"{convertedFolder}\" {volArgs}";

            Log($"✓ kcc_c2e: {Path.GetFileName(kccExe)}");
            Log($"▶ Sending {volumes.Length} volume(s) to KCC ({profile})...");
            Log(sourceRoot == path
                ? "  Source → extracted volumes (Kindle processing not run yet)"
                : $"  Source → /{KindleFolderName}");
            Log($"  Output → {convertedFolder}");
            Log($"  CMD: {args}");

            var token = StartOperation();

            BtnOpenKCC.IsEnabled = false;
            ProgressBar.IsIndeterminate = true;
            ProgressText.Text = "Running...";

            await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = kccExe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        ProgressBar.IsIndeterminate = false;
                        BtnOpenKCC.IsEnabled = true;
                        Log("⚠ Failed to start kcc_c2e.");
                        EndOperation();
                    });
                    return;
                }

                proc.OutputDataReceived += (_, ev) => { if (!string.IsNullOrEmpty(ev.Data)) Dispatcher.Invoke(() => Log($"  {ev.Data}")); };
                proc.ErrorDataReceived  += (_, ev) => { if (!string.IsNullOrEmpty(ev.Data)) Dispatcher.Invoke(() => Log($"  {ev.Data}")); };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();
                int exitCode = proc.ExitCode;

                Dispatcher.Invoke(() =>
                {
                    ProgressBar.IsIndeterminate = false;
                    BtnOpenKCC.IsEnabled = true;
                    ProgressBar.Value = 100;
                    ProgressText.Text = exitCode == 0 ? "Concluído" : $"Erro ({exitCode})";

                    if (exitCode == 0)
                        Log("✓ KCC conversion complete. MOBI(s) saved to /Converted.");
                    else
                        Log($"⚠ kcc_c2e finished with exit code {exitCode}.");

                    EndOperation();
                    RefreshSelectedStatus();
                });
            });
        }

        // ==============================
        // 📂 SELECT FOLDER
        // ==============================
        private void StartWatcher(string path)
        {
            _watcher?.Dispose();

            if (!Directory.Exists(path)) return;

            _watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };

            // Debounce — evita múltiplos refreshes em sequência
            System.Timers.Timer? debounce = null;

            void OnChanged(object s, FileSystemEventArgs e)
            {
                debounce?.Stop();
                debounce = new System.Timers.Timer(800) { AutoReset = false };
                debounce.Elapsed += (_, _) =>
                    Dispatcher.Invoke(() => RefreshSelectedStatus());
                debounce.Start();
            }

            _watcher.Created += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += (s, e) => OnChanged(s, e);
        }

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
                           _chk1 = "", _chk2 = "", _chk3 = "", _chk4 = "", _chk5 = "",
                           _chk6 = "", _chk7 = "", _chkKindle = "", _chkAnimeUnited = "", _chkLanguage = "", _readProgress = "";
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
            public string Chk6 { get => _chk6; set { _chk6 = value; OnChanged(nameof(Chk6)); } }
            public string Chk7 { get => _chk7; set { _chk7 = value; OnChanged(nameof(Chk7)); } }
            public string ChkKindle { get => _chkKindle; set { _chkKindle = value; OnChanged(nameof(ChkKindle)); } }
            public string ChkAnimeUnited { get => _chkAnimeUnited; set { _chkAnimeUnited = value; OnChanged(nameof(ChkAnimeUnited)); } }
            public string ChkLanguage { get => _chkLanguage; set { _chkLanguage = value; OnChanged(nameof(ChkLanguage)); OnChanged(nameof(HasLanguage)); } }
            public Visibility HasLanguage => string.IsNullOrEmpty(ChkLanguage) ? Visibility.Collapsed : Visibility.Visible;
            // "3/5" = 3 volumes lidos de 5 no Kindle; "" quando não há dados
            public string ReadProgress { get => _readProgress; set { _readProgress = value; OnChanged(nameof(ReadProgress)); } }
            public System.Windows.Media.ImageSource? CoverImage { get => _coverImage; set { _coverImage = value; OnChanged(nameof(CoverImage)); } }

            // Selo 🔞 sobreposto no canto da capa. Null = obra sem a marca, e a
            // Image do card simplesmente não desenha nada.
            private System.Windows.Media.ImageSource? _nsfwBadge;
            public System.Windows.Media.ImageSource? NsfwBadge { get => _nsfwBadge; set { _nsfwBadge = value; OnChanged(nameof(NsfwBadge)); } }

            // Obra ainda em publicação: os passos do pipeline só tocam o que falta.
            private bool _isOngoing;
            public bool IsOngoing { get => _isOngoing; set { _isOngoing = value; OnChanged(nameof(IsOngoing)); OnChanged(nameof(OngoingMark)); } }
            public string OngoingMark => _isOngoing ? "🔄" : "";

            // Standalone MOBI file (not a manga folder)
            public string? MobiFilePath { get; set; }
            public bool IsMobiOnly => !string.IsNullOrEmpty(MobiFilePath);

            // AnimeUnited: item veio do catálogo remoto e ainda não foi baixado
            // IsRemote = "está no catálogo e eu ainda NÃO baixei" — controla o botão Download.
            // IsOnCloud = "existe no servidor", independente de eu ter a cópia local. É este
            // que vale para apagar da nuvem: quase todo mangá do catálogo também está no disco
            // do admin, e nesse caso IsRemote é false.
            public bool IsRemote { get; set; }
            public bool IsOnCloud { get; set; }
            public int RemoteVolumeCount { get; set; }
            public string? RemoteCoverUrl { get; set; }
        }

        private enum ReadStatus { Unread, Reading, Read }

        private class KindleVolumeRow : System.ComponentModel.INotifyPropertyChanged
        {
            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
            private void OnChanged(string p) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(p));

            public string VolumeName { get; set; } = "";

            // -1 = desconhecido; 0-100 = percentual do Kindle
            private int _percentage = -1;
            public int Percentage
            {
                get => _percentage;
                set { _percentage = value; OnChanged(nameof(PercentText)); OnChanged(nameof(StatusIcon)); OnChanged(nameof(StatusColor)); }
            }

            private ReadStatus _status = ReadStatus.Unread;
            public ReadStatus Status
            {
                get => _status;
                set { _status = value; OnChanged(nameof(StatusIcon)); OnChanged(nameof(StatusColor)); OnChanged(nameof(PercentText)); }
            }

            public string StatusIcon => "●";

            public string StatusColor => Status switch
            {
                ReadStatus.Read    => "#95b634", // verde
                ReadStatus.Reading => "#F4A300", // amarelo
                _                  => "#00A8E1", // azul
            };

            // Mostra percentual do Kindle quando disponível
            public string PercentText => Percentage >= 0 ? $" {Percentage}%" : "";

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

            // 5. Cleanup feito (sem arquivos de manga soltos dentro dos volumes)
            bool noCbzInVolumes = !volumes.Any(v => HasMangaFiles(v));
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

            // Detecta Kindle uma vez para todos os cards
            string? kindleDocsPath = FindKindlePath();

            // O selo 🔞 é o mesmo para todos os cards: carrega uma vez só.
            await EnsureNsfwBadgeImageAsync();

            // Catálogo do AnimeUnited: marca quem já está no servidor. Vale para
            // qualquer usuário, não só admin — o painel de detalhes também mostra.
            // Null = não deu para perguntar, e aí a resposta é "não sei", não "não".
            HashSet<string>? animeUnitedNames = null;
            {
                string apiUrl = Properties.Settings.Default.AnimeUnitedApiUrl;
                if (!string.IsNullOrWhiteSpace(apiUrl))
                {
                    try
                    {
                        var client = new AnimeUnitedClient(apiUrl, Properties.Settings.Default.AnimeUnitedApiKey,
                            Properties.Settings.Default.AnimeUnitedUserEmail);
                        var remoteMangas = await client.GetMangasAsync();
                        animeUnitedNames = remoteMangas.Select(m => m.Name).ToHashSet();
                    }
                    catch (Exception ex)
                    {
                        Log($"AnimeUnited: could not check server status ({ex.Message}).");
                    }
                }
            }
            _cloudMangaNames = animeUnitedNames;

            var tasks = new List<Task>();
            foreach (var name in dirs)
            {
                string fullPath = Path.Combine(basePath, name!);
                var item = BuildMangaItem(name!, fullPath, kindleDocsPath, animeUnitedNames);
                MangaList.Items.Add(item);
                tasks.Add(LoadCoverAsync(item, fullPath));
            }

            // Standalone source files directly in basePath (MOBI, CBZ, EPUB)
            var standaloneFiles = Directory.GetFiles(basePath)
                .Where(f => f.EndsWith(".mobi", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".cbz",  StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f).ToArray();

            foreach (var file in standaloneFiles)
            {
                string ext  = Path.GetExtension(file).TrimStart('.').ToUpper();
                string name = Path.GetFileNameWithoutExtension(file);
                var item = new MangaItem
                {
                    Name             = name,
                    StatusColor      = "#1a1a2a",
                    StatusForeground = "#E85D04",
                    StatusWeight     = "Bold",
                    StatusTip        = $"Standalone {ext} file",
                    Chk7             = ext,
                    MobiFilePath     = file,
                };
                MangaList.Items.Add(item);
                tasks.Add(LoadCoverAsync(item, Path.GetDirectoryName(file)!));
            }

            Log($"{MangaList.Items.Count} manga(s) found. Loading covers...");

            // Carrega capas em background sem bloquear a UI
            _ = Task.WhenAll(tasks).ContinueWith(_ =>
                Dispatcher.Invoke(() => Log("Covers loaded.")));
        }

        private MangaItem BuildMangaItem(string name, string fullPath, string? kindleDocsPath = null,
                                          HashSet<string>? animeUnitedNames = null)
        {
            var volumes = Directory.GetDirectories(fullPath, "* - Volume *");

            bool s1 = volumes.Length > 0;
            bool s2 = s1 && volumes.Any(v => Directory.GetDirectories(v, "Capitulo *").Length > 0);
            bool s3 = s2 && volumes.Any(v => Directory.GetDirectories(v, "Capitulo *").Any(ch =>
                        Directory.GetFiles(ch, "*.jpg").Length > 0 ||
                        Directory.GetFiles(ch, "*.png").Length > 0 ||
                        Directory.GetFiles(ch, "*.webp").Length > 0));
            bool s4 = s3 && volumes.Any(v => Directory.GetDirectories(v, "Capitulo *").Any(ch =>
                        File.Exists(Path.Combine(ch, "ComicInfo.xml"))));
            bool s5 = s4 && !volumes.Any(v => HasMangaFiles(v));

            bool hasMobi = HasConvertedFiles(Path.Combine(fullPath, "Converted"));

            // S6 — KCC: pasta Converted com .mobi gerado pelo KCC
            bool s6 = hasMobi;

            // S7 — não usado
            bool s7 = false;

            // Verifica se já está no Kindle (usa path passado para evitar múltiplas detecções)
            bool onKindle = false;
            if (kindleDocsPath != null)
            {
                string kindleDest = Path.Combine(kindleDocsPath, "Mangas", name);
                onKindle = Directory.Exists(kindleDest) &&
                           (Directory.GetFiles(kindleDest, "*.mobi").Length > 0 ||
                            Directory.GetFiles(kindleDest, "*.epub").Length > 0 ||
                            Directory.GetFiles(kindleDest, "*.azw3").Length > 0);
            }

            // Idioma gravado no ComicInfo.xml (LanguageISO), se já existir
            string languageBadge = LanguageBadge(ReadLanguageFromComicInfo(fullPath));

            var flags = LoadMangaFlags(fullPath);

            // Quando o selo foi gravado na capa (cover_original.jpg é a prova de que
            // isso aconteceu), a sobreposição do card seria um segundo selo em cima
            // do primeiro. Ela só entra quando a capa não pôde receber a marca.
            bool coverCarriesBadge = File.Exists(Path.Combine(fullPath, CoverOriginalName));

            // Progresso de leitura a partir do JSON salvo (atualizado quando Kindle conectado)
            string readProgress = "";
            if (hasMobi)
            {
                int totalMobi = Directory.GetFiles(Path.Combine(fullPath, "Converted"), "*.mobi").Length;
                if (totalMobi > 0)
                {
                    var saved = LoadReadingProgress(fullPath);
                    int readCount  = saved.Values.Count(s => s == ReadStatus.Read);
                    int readingCount = saved.Values.Count(s => s == ReadStatus.Reading);
                    if (readCount > 0 || readingCount > 0)
                        readProgress = readCount == totalMobi
                            ? $"✅ {readCount}/{totalMobi}"
                            : $"{readCount}/{totalMobi}";
                }
            }

            return new MangaItem
            {
                Name = name,
                StatusColor = hasMobi ? "#95b634" : "Transparent",
                StatusForeground = hasMobi ? "#1a1a1a" : "White",
                StatusWeight = hasMobi ? "Bold" : "Normal",
                Chk1 = s1 ? "✔" : "",
                Chk2 = s2 ? "✔" : "",
                Chk3 = s3 ? "✔" : "",
                Chk4 = s4 ? "✔" : "",
                Chk5 = s5 ? "✔" : "",
                Chk6 = s6 ? "✔" : "",
                Chk7 = s7 ? "✔" : "",
                ChkKindle = onKindle ? "📱" : "",
                ChkAnimeUnited = (animeUnitedNames != null && animeUnitedNames.Contains(name)) ? "☁" : "",
                ChkLanguage = languageBadge,
                ReadProgress = readProgress,
                NsfwBadge = flags.Nsfw && !coverCarriesBadge ? _nsfwBadgeImage : null,
                IsOngoing = flags.Ongoing,
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

        // Helper: retorna todos os arquivos de manga suportados (.cbz, .epub)
        private static bool IsMangaFile(string f) =>
            f.EndsWith(".cbz",  StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".epub", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".pdf",  StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".mobi", StringComparison.OrdinalIgnoreCase);

        private static string[] GetMangaFiles(string path, string searchPattern = "*",
            SearchOption option = SearchOption.TopDirectoryOnly)
        {
            return Directory.GetFiles(path, "*.*", option)
                            .Where(IsMangaFile)
                            .OrderBy(f => f)
                            .ToArray();
        }

        private static bool HasMangaFiles(string path,
            SearchOption option = SearchOption.TopDirectoryOnly)
        {
            return Directory.GetFiles(path, "*.*", option).Any(IsMangaFile);
        }

        private static bool HasConvertedFiles(string convertedFolder) =>
            Directory.Exists(convertedFolder) &&
            Directory.GetFiles(convertedFolder, "*.mobi").Length > 0;

        private static string[] GetConvertedFiles(string convertedFolder) =>
            Directory.Exists(convertedFolder)
                ? Directory.GetFiles(convertedFolder, "*.mobi").OrderBy(x => x).ToArray()
                : Array.Empty<string>();

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

                // As capas do AnimeUnited deixaram de ser públicas. Sem a chave, o
                // servidor responde 401 e o catálogo fica sem imagem. Só mandamos os
                // cabeçalhos para o nosso servidor — este método também busca capas
                // do AniList, e credencial nossa não tem o que fazer lá.
                var apiUrl = Properties.Settings.Default.AnimeUnitedApiUrl;
                if (!string.IsNullOrWhiteSpace(apiUrl) &&
                    url.StartsWith(apiUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                {
                    var apiKey = Properties.Settings.Default.AnimeUnitedApiKey;
                    var email  = Properties.Settings.Default.AnimeUnitedUserEmail;
                    if (!string.IsNullOrEmpty(apiKey))
                        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
                    if (!string.IsNullOrEmpty(email))
                        client.DefaultRequestHeaders.Add("X-User-Email", email);
                }

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
            int semVolume = 0;   // capítulos que o MangaDex tem, mas sem volume definido

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
                            ? vProp.GetString() ?? "" : "";

                        // Capítulo sem volume no MangaDex (campo null) NÃO entra no mapa.
                        // Antes virava a string "0", e lá na organização "0" significa
                        // "capítulo especial" — então toda série sem volume preenchido
                        // tinha todos os capítulos despejados no último volume, com o log
                        // alegando que estavam "mapeados". Ficando de fora, o fluxo cai no
                        // fallback que pergunta quantos capítulos por volume.
                        if (string.IsNullOrEmpty(volNum) || volNum == "0") semVolume++;
                        else if (!string.IsNullOrEmpty(chNum) && !map.ContainsKey(chNum))
                            map[chNum] = volNum;
                    }

                    offset += count;
                    int total = feedDoc.RootElement.TryGetProperty("total", out var totalProp)
                        ? totalProp.GetInt32() : 0;

                    if (offset >= total || count == 0) break;
                }

                Log(semVolume > 0
                    ? $"MangaDex: {map.Count} chapter(s) with volume info ({semVolume} without)."
                    : $"MangaDex: {map.Count} chapter(s) mapped.");
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

            // Extrai o número do capítulo de cada arquivo.
            static (string Texto, double Valor)? NumeroDoCapitulo(string caminho)
            {
                string fileName = Path.GetFileNameWithoutExtension(caminho);
                var match = System.Text.RegularExpressions.Regex.Match(
                    fileName,
                    @"(?:cap[ií]tulo|capitulo|cap\.?|chapter|ch\.?)\s*(\d+(?:\.\d+)?)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (!match.Success)
                    match = System.Text.RegularExpressions.Regex.Match(fileName, @"(\d+(?:\.\d+)?)");

                if (!match.Success) return null;

                string chNum = match.Groups[1].Value.TrimStart('0');
                if (string.IsNullOrEmpty(chNum)) chNum = "0";

                double valor = double.TryParse(chNum, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : double.MaxValue;

                return (chNum, valor);
            }

            // Ordena pelo NÚMERO do capítulo antes de distribuir.
            //
            // A ordem que chega aqui é alfabética — Cap_1, Cap_10, Cap_11, ..., Cap_19,
            // Cap_2 — e distribuir nessa sequência colocava o capítulo 2 no volume 3 e o
            // capítulo 9 no volume 15, enquanto o 10 ao 14 iam para o volume 1. Capítulo
            // 8.5 entra entre o 8 e o 9, que é onde ele pertence na leitura.
            var ordenados = cbzFiles
                .Select(f => NumeroDoCapitulo(f))
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .OrderBy(x => x.Valor)
                .ToList();

            int vol = 1;
            int count = 0;

            foreach (var (texto, _) in ordenados)
            {
                if (map.ContainsKey(texto)) continue;   // duplicata não consome vaga

                map[texto] = vol.ToString();
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

                var cbzFiles = GetMangaFiles(path);
                if (cbzFiles.Length == 0)
                {
                    Log("No .cbz or .epub files found in manga folder.");
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
                // Quantos dos MEUS arquivos o mapa realmente cobre.
                //
                // Antes isto comparava volumeMap.Count com a quantidade de arquivos, o que
                // é a pergunta errada: o mapa traz a série inteira da fonte, inclusive
                // capítulos que você não tem. Numa série com 108 capítulos mapeados e 90
                // arquivos, a conta dizia "cobertura total" — mas os mapeados eram os
                // capítulos 100+, e quase nenhum dos 1 a 90 estava lá. Resultado: o app
                // não perguntava nada e chutava sozinho.
                int cobertos = cbzFiles.Count(f =>
                {
                    var m = System.Text.RegularExpressions.Regex.Match(
                        Path.GetFileNameWithoutExtension(f), @"(\d+(?:\.\d+)?)");
                    if (!m.Success) return false;
                    var ch = m.Groups[1].Value.TrimStart('0');
                    if (string.IsNullOrEmpty(ch)) ch = "0";
                    return volumeMap.ContainsKey(ch);
                });

                if (cobertos < cbzFiles.Length)
                {
                    Log($"⚠ {cobertos} of {cbzFiles.Length} of your chapters have volume info in the source.");

                    string input = Microsoft.VisualBasic.Interaction.InputBox(
                        $"Only {cobertos} of your {cbzFiles.Length} chapters have volume info in the source.\n\n" +
                        "For the rest, how many chapters per volume?\n(Leave empty to skip unmatched files)",
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
                var token = StartOperation();

                await Task.Run(() =>
                {
                    int total = cbzFiles.Length;
                    int current = 0;
                    int unmatched = 0;

                    foreach (var cbz in cbzFiles)
                    {
                        if (token.IsCancellationRequested) break;
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
                            // Arquivo especial/extra sem número — coloca no último volume
                            var lastVol = Directory.GetDirectories(path, "* - Volume *")
                                                   .OrderBy(x => x).LastOrDefault();
                            if (lastVol != null)
                            {
                                string lastVolNum = new string(Path.GetFileName(lastVol)
                                    .Where(char.IsDigit).ToArray()).TrimStart('0');
                                if (string.IsNullOrEmpty(lastVolNum)) lastVolNum = "1";

                                string specialVolFolder = $"{title} - Volume {int.Parse(lastVolNum):D2}";
                                string volPath2 = Path.Combine(path, specialVolFolder);
                                Directory.CreateDirectory(volPath2);

                                string chName = Path.GetFileNameWithoutExtension(cbz);
                                string chDest2 = Path.Combine(volPath2, chName);
                                Directory.CreateDirectory(chDest2);

                                string fileDest = Path.Combine(volPath2, Path.GetFileName(cbz));
                                if (!File.Exists(fileDest)) File.Move(cbz, fileDest);

                                if (fileDest.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                                {
                                    ExtractPdf(fileDest, chDest2);
                                }
                                else if (fileDest.EndsWith(".mobi", StringComparison.OrdinalIgnoreCase))
                                {
                                    MobiExtractor.ExtractTo(fileDest, chDest2);
                                }
                                else
                                {
                                    var psi2 = new ProcessStartInfo
                                    {
                                        FileName = zipPath,
                                        Arguments = $"x \"{fileDest}\" -o\"{chDest2}\" -y",
                                        CreateNoWindow = true,
                                        UseShellExecute = false
                                    };
                                    Process.Start(psi2)?.WaitForExit();
                                }
                                current++;

                                Dispatcher.Invoke(() =>
                                {
                                    ProgressBar.Value = (double)current / total * 100;
                                    ProgressText.Text = $"{current} / {total}";
                                    Log($"⚠ Special/Extra → {specialVolFolder}\\{chName}: {Path.GetFileName(cbz)}");
                                });
                            }
                            else
                            {
                                Dispatcher.Invoke(() => Log($"⚠ Not recognized (no volumes): {Path.GetFileName(cbz)}"));
                                unmatched++;
                                current++;
                            }
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
                            // Sem volume no mapa: usa o do capítulo mapeado mais PRÓXIMO.
                            //
                            // O padrão antigo era o último volume, o que é o pior palpite
                            // justamente nos casos comuns: numa série onde o MangaDex não
                            // preencheu os capítulos 1 a 4, eles iam parar no fim da
                            // coleção. Pelo vizinho, o capítulo 4 acompanha o 5, que é
                            // quase sempre o volume certo.
                            if (double.TryParse(chapterNum, System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out double alvoCap))
                            {
                                string? vizinho = null;
                                double menorDist = double.MaxValue;

                                foreach (var par in volumeMap)
                                {
                                    if (!double.TryParse(par.Key, System.Globalization.NumberStyles.Any,
                                            System.Globalization.CultureInfo.InvariantCulture, out double c)) continue;
                                    double dist = Math.Abs(c - alvoCap);
                                    if (dist < menorDist) { menorDist = dist; vizinho = par.Value; }
                                }

                                // Só interpola em lacuna PEQUENA. Se o capítulo mapeado mais
                                // próximo está a mais de 3 de distância, o palpite deixa de
                                // ser interpolação e vira invenção: numa série cujo mapa só
                                // cobre os capítulos 100+, isso amontoaria 44 capítulos num
                                // volume só, com cara de resultado legítimo.
                                if (vizinho != null && menorDist <= 3)
                                {
                                    volNum = vizinho;
                                    Dispatcher.Invoke(() => Log($"  ↳ Ch {chapterNum} sem volume na fonte → volume {vizinho} (capítulo mais próximo, distância {menorDist:0.#})"));
                                }
                            }
                        }

                        if (volNum == null || volNum == "0")
                        {
                            // Coloca no último volume disponível.
                            //
                            // O número vem SÓ do trecho "Volume N" no fim do nome. Antes isto
                            // juntava todos os dígitos da pasta, e um título com número — como
                            // "2.5 Dimensional Seduction" — contaminava a conta: o "2" e o "5"
                            // do título grudavam no "25" do volume e viravam "2525". Como a
                            // pasta criada virava a última da lista, o erro se multiplicava a
                            // cada capítulo especial até estourar o Int32.
                            static int NumeroDoVolume(string caminho)
                            {
                                var m = System.Text.RegularExpressions.Regex.Match(
                                    Path.GetFileName(caminho), @"Volume\s*(\d+)\s*$",
                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : 0;
                            }

                            // Ordena pelo número, não pelo texto: "Volume 9" vem antes de "Volume 10".
                            var lastVol = Directory.GetDirectories(path, "* - Volume *")
                                                   .OrderBy(NumeroDoVolume).LastOrDefault();
                            if (lastVol != null)
                            {
                                int n = NumeroDoVolume(lastVol);
                                volNum = (n > 0 ? n : 1).ToString();
                                Dispatcher.Invoke(() => Log($"⚠ Special chapter → last volume {volNum}: {Path.GetFileName(cbz)}"));
                            }
                            else
                            {
                                Dispatcher.Invoke(() => Log($"⚠ Not recognized and no volumes found: {Path.GetFileName(cbz)}"));
                                unmatched++;
                                current++;
                                continue;
                            }
                        }

                        // int.TryParse em vez de Parse: um volNum inesperado derrubava a
                        // organização inteira com "Value was either too large or too small
                        // for an Int32", no meio do processamento e sem dizer qual arquivo.
                        if (!int.TryParse(volNum, out int volNumero) || volNumero <= 0)
                        {
                            Dispatcher.Invoke(() => Log($"⚠ Volume inválido (\"{volNum}\") para {Path.GetFileName(cbz)} — ignorado."));
                            unmatched++;
                            current++;
                            continue;
                        }

                        string volFolder = $"{title} - Volume {volNumero:D2}";
                        string volPath = Path.Combine(path, volFolder);
                        Directory.CreateDirectory(volPath);

                        // Sempre 3 dígitos ANTES do ponto, decimal preservado depois:
                        //   16 → "Capitulo 016"   ·   16.5 → "Capitulo 016.5"
                        // Antes o decimal era preenchido até 6 caracteres e saía
                        // "Capitulo 0016.5", com um dígito a mais que os demais — o que
                        // quebra a ordenação por nome no leitor, no Explorer e no catálogo
                        // da nuvem, colocando o 16.5 antes do 13.
                        string chFormatted;
                        if (!chMatch.Success)
                        {
                            chFormatted = Path.GetFileNameWithoutExtension(cbz); // especial/extra
                        }
                        else
                        {
                            var partes = chapterNum.Split('.');
                            string inteiro = int.TryParse(partes[0], out int pi) ? pi.ToString("D3") : partes[0].PadLeft(3, '0');
                            chFormatted = partes.Length > 1
                                ? $"Capitulo {inteiro}.{partes[1]}"
                                : $"Capitulo {inteiro}";
                        }

                        string chDest = Path.Combine(volPath, chFormatted);
                        Directory.CreateDirectory(chDest);

                        string cbzDest = Path.Combine(volPath, Path.GetFileName(cbz));
                        if (!File.Exists(cbzDest))
                            File.Move(cbz, cbzDest);

                        if (cbzDest.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                        {
                            ExtractPdf(cbzDest, chDest);
                        }
                        else if (cbzDest.EndsWith(".mobi", StringComparison.OrdinalIgnoreCase))
                        {
                            MobiExtractor.ExtractTo(cbzDest, chDest);
                        }
                        else
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = zipPath,
                                Arguments = $"x \"{cbzDest}\" -o\"{chDest}\" -y",
                                CreateNoWindow = true,
                                UseShellExecute = false
                            };
                            Process.Start(psi)?.WaitForExit();
                        }
                        current++;

                        Dispatcher.Invoke(() =>
                        {
                            ProgressBar.Value = (double)current / total * 100;
                            ProgressText.Text = $"{current} / {total}";
                            Log($"✓ Ch {chapterNum} → {volFolder}\\{chFormatted}");
                        });
                    }

                    // O passo anterior cria uma pasta para CADA volume que o AniList
                    // informa (25, nesta série), mas só se preenchem os que você tem
                    // capítulos. As vazias não são inofensivas: aparecem no catálogo da
                    // nuvem como volumes de 0 páginas e confundem a contagem.
                    int vazias = 0;
                    foreach (var dir in Directory.GetDirectories(path, "* - Volume *"))
                    {
                        try
                        {
                            if (Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length == 0)
                            {
                                Directory.Delete(dir, recursive: true);
                                vazias++;
                            }
                        }
                        catch { /* pasta em uso: deixa quieta */ }
                    }

                    Dispatcher.Invoke(() => {
                        Log($"Organization complete. {current - unmatched} ok, {unmatched} unmatched.");
                        if (vazias > 0) Log($"  {vazias} empty volume folder(s) removed.");
                        EndOperation();
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
        // 📄 EXTRACT PDF → IMAGES
        // ==============================
        private static void ExtractPdf(string pdfPath, string destFolder)
        {
            Directory.CreateDirectory(destFolder);

            using var lib = DocLib.Instance;
            using var doc = lib.GetDocReader(pdfPath, new PageDimensions(1080, 1536));

            int pageCount = doc.GetPageCount();
            for (int i = 0; i < pageCount; i++)
            {
                using var page = doc.GetPageReader(i);
                int w = page.GetPageWidth();
                int h = page.GetPageHeight();
                var rawBytes = page.GetImage(); // BGRA

                using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var bmpData = bmp.LockBits(
                    new System.Drawing.Rectangle(0, 0, w, h),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                System.Runtime.InteropServices.Marshal.Copy(rawBytes, 0, bmpData.Scan0, rawBytes.Length);
                bmp.UnlockBits(bmpData);

                string outPath = Path.Combine(destFolder, $"page{i + 1:D4}.jpg");
                bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Jpeg);
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

            var token = StartOperation();
            bool ongoing = IsOngoing(path);
            if (ongoing) Log("🔄 Ongoing: chapters already extracted will be left alone.");

            await Task.Run(() =>
            {
                var volumes = Directory.GetDirectories(path, "* - Volume *").OrderBy(x => x).ToArray();
                int total = volumes.Sum(v => GetMangaFiles(v).Length);
                int reused = 0;

                if (total == 0)
                {
                    Dispatcher.Invoke(() => { Log("No .cbz or .epub files found."); EndOperation(); });
                    return;
                }

                int current = 0;

                foreach (var vol in volumes)
                {
                    if (token.IsCancellationRequested) break;
                    var cbzs = GetMangaFiles(vol);

                    foreach (var cbz in cbzs)
                    {
                        if (token.IsCancellationRequested) break;
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

                        // Obra em andamento: capítulo que já tem páginas no disco fica
                        // como está — só os novos passam pelo 7-Zip.
                        if (ongoing && HasPages(dest))
                        {
                            reused++;
                            current++;
                            Dispatcher.Invoke(() =>
                            {
                                ProgressBar.Value = (double)current / total * 100;
                                ProgressText.Text = $"{current} / {total}";
                            });
                            continue;
                        }

                        Directory.CreateDirectory(dest);

                        bool isPdf  = cbz.EndsWith(".pdf",  StringComparison.OrdinalIgnoreCase);
                        bool isMobi = cbz.EndsWith(".mobi", StringComparison.OrdinalIgnoreCase);
                        if (isPdf)
                        {
                            ExtractPdf(cbz, dest);
                        }
                        else if (isMobi)
                        {
                            int pages = MobiExtractor.ExtractTo(cbz, dest);
                            Dispatcher.Invoke(() => Log($"  ✓ {Path.GetFileName(cbz)} → {pages} pages"));
                        }
                        else
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = zipPath,
                                Arguments = $"x \"{cbz}\" -o\"{dest}\" -y",
                                CreateNoWindow = true,
                                UseShellExecute = false
                            };
                            Process.Start(psi)?.WaitForExit();
                        }

                        current++;

                        Dispatcher.Invoke(() =>
                        {
                            ProgressBar.Value = (double)current / total * 100;
                            ProgressText.Text = $"{current} / {total}";
                            Log($"Extracted: {Path.GetFileName(cbz)} → {chapterName}");
                        });
                    }
                }

                if (reused > 0)
                    Dispatcher.Invoke(() => Log($"  🔄 {reused} chapter(s) already extracted were kept."));
            });

            Log("Extraction complete.");
            EndOperation();
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
            var language = (LanguageCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "pt-BR";

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

                // ComicInfo na raiz do volume — KCC usa para os metadados do MOBI (título, autor)
                File.WriteAllText(Path.Combine(vol, "ComicInfo.xml"),
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<ComicInfo>
  <Title>{mangaName} - Volume {int.Parse(string.IsNullOrEmpty(volNum) ? "1" : volNum):D2}</Title>
  <Series>{mangaName}</Series>
  <Volume>{volNum}</Volume>
  <Writer>{author}</Writer>
  <Genre>Manga</Genre>
  <LanguageISO>{language}</LanguageISO>
  <Format>Manga</Format>
</ComicInfo>");

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
  <LanguageISO>{language}</LanguageISO>
  <Format>Manga</Format>
</ComicInfo>");

                    generated++;
                }
            }

            Log($"ComicInfo generated for {generated} chapter(s) + {volumes.Length} volume root(s).");

            // As saídas já criadas também precisam do XML novo — o KCC e o upload
            // leem de lá, não dos volumes extraídos.
            foreach (var outputFolder in new[] { CloudFolderName, KindleFolderName })
            {
                string outputRoot = Path.Combine(path, outputFolder);
                if (GetVolumeDirs(outputRoot).Length == 0) continue;
                int copied = MirrorComicInfo(path, outputRoot);
                if (copied > 0) Log($"  {copied} ComicInfo.xml copied to /{outputFolder}.");
            }

            RefreshSelectedStatus();
        }

        // ==============================
        // 🧹 CLEANUP
        // ==============================
        private void Cleanup_Click(object sender, RoutedEventArgs e)
        {
            var path = GetSelectedPath();
            if (path == null) return;

            var cbzFiles = GetMangaFiles(path, "*", SearchOption.AllDirectories);
            string cbzBackupFolder = Path.Combine(path, "Originals");

            int moved = 0;
            int skipped = 0;

            // Move arquivos CBZ/EPUB para pasta de backup
            if (cbzFiles.Length > 0)
            {
                Directory.CreateDirectory(cbzBackupFolder);

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
            }

            // Remove pastas de volume vazias (sem capítulos)
            int removedVols = 0;
            var volumes = Directory.GetDirectories(path, "* - Volume *");
            foreach (var vol in volumes)
            {
                var chapters = Directory.GetDirectories(vol);
                bool hasContent = chapters.Any(ch =>
                    Directory.GetFiles(ch).Length > 0 ||
                    Directory.GetDirectories(ch).Length > 0);

                if (!hasContent && chapters.Length == 0)
                {
                    Directory.Delete(vol, recursive: true);
                    Log($"🗑 Removed empty volume: {Path.GetFileName(vol)}");
                    removedVols++;
                }
            }

            Log($"Cleanup done. {moved} file(s) moved to /Originals, {skipped} skipped, {removedVols} empty volume(s) removed.");
            RefreshSelectedStatus();
        }

        // ==============================
        // ==============================
        // 📱 SEND TO KINDLE
        // ==============================
        private string? FindKindlePath()
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;

                    string root = drive.RootDirectory.FullName;
                    string label = drive.VolumeLabel?.ToLower() ?? "";

                    // Possíveis caminhos do Kindle
                    string[] candidates = {
                        Path.Combine(root, "documents"),
                        Path.Combine(root, "Books"),
                        Path.Combine(root, "Kindle", "documents"),
                        Path.Combine(root, "Internal Storage", "documents"),
                        Path.Combine(root, "Internal Storage", "Books"),
                    };

                    // Marcadores que identificam um Kindle
                    string[] markers = {
                        Path.Combine(root, "system"),
                        Path.Combine(root, "documents"),
                        Path.Combine(root, "active_process"),
                        Path.Combine(root, "kindle.info"),
                    };

                    bool looksLikeKindle = label.Contains("kindle") ||
                                           markers.Any(File.Exists) ||
                                           markers.Any(Directory.Exists);

                    if (looksLikeKindle)
                    {
                        // Retorna o primeiro caminho de documentos que existe
                        var docsPath = candidates.FirstOrDefault(Directory.Exists);
                        if (docsPath != null) return docsPath;

                        // Se não achou subpasta, retorna a raiz
                        return root;
                    }
                }
                catch { }
            }
            return null;
        }

        private async void SendToKindle_Click(object sender, RoutedEventArgs e)
        {
            var path = GetSelectedPath();
            if (path == null) return;

            string mangaName = (MangaList.SelectedItem as MangaItem)!.Name;

            // Busca MOBIs na pasta Converted
            string convertedFolder = Path.Combine(path, "Converted");
            var files = Directory.Exists(convertedFolder)
                ? Directory.GetFiles(convertedFolder, "*.mobi").OrderBy(x => x).ToArray()
                : Array.Empty<string>();

            if (files.Length == 0)
            {
                Log("No .mobi files found in /Converted. Convert the CBZ in KCC first.");
                return;
            }

            // Filtra por volume se Range estiver selecionado
            if (ChkSendRange.IsChecked == true)
            {
                var selected = ParseVolumeSelection(VolumeRangeBox.Text);
                if (selected.Count == 0)
                {
                    Log("Please enter a valid volume range (e.g. 1,2,3 or 1-6).");
                    return;
                }

                files = files.Where(f =>
                {
                    // Extrai número do volume do nome do arquivo
                    var match = System.Text.RegularExpressions.Regex.Match(
                        Path.GetFileNameWithoutExtension(f), @"Volume (\d+)");
                    return match.Success && selected.Contains(int.Parse(match.Groups[1].Value));
                }).ToArray();

                Log($"Sending {files.Length} volume(s) based on selection: {VolumeRangeBox.Text}");
            }
            else
            {
                Log($"Sending all {files.Length} volume(s).");
            }

            if (files.Length == 0)
            {
                Log("No matching volumes found for the selected range.");
                return;
            }

            // Detecta o Kindle
            Log("Looking for Kindle...");
            string? kindlePath = FindKindlePath();

            if (kindlePath == null)
            {
                Log("Kindle not found. Make sure it's connected via USB and unlocked.");
                MessageBox.Show(
                    "Kindle not detected.\n\nMake sure your Kindle is:\n• Connected via USB\n• Unlocked\n• Recognized by Windows",
                    "Kindle Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Log($"Kindle found at: {kindlePath}");

            // Destino: \documents\Mangas\<nome do mangá>\
            string mangasFolder = Path.Combine(kindlePath, "Mangas");
            string destFolder = Path.Combine(mangasFolder, mangaName);

            if (!Directory.Exists(mangasFolder))
                Log($"Creating /Mangas folder on Kindle...");
            if (!Directory.Exists(destFolder))
                Log($"Creating manga folder: {mangaName}");

            Directory.CreateDirectory(destFolder);
            Log($"Destination: {destFolder}");

            var token = StartOperation();

            await Task.Run(() =>
            {
                int total = files.Length;
                int current = 0;
                int skipped = 0;

                foreach (var file in files)
                {
                    if (token.IsCancellationRequested) break;
                    string dest = Path.Combine(destFolder, Path.GetFileName(file));

                    if (File.Exists(dest))
                    {
                        Dispatcher.Invoke(() => Log($"⚠ Already exists, skipping: {Path.GetFileName(file)}"));
                        skipped++;
                        current++;
                        continue;
                    }

                    File.Copy(file, dest);
                    current++;

                    Dispatcher.Invoke(() =>
                    {
                        ProgressBar.Value = (double)current / total * 100;
                        ProgressText.Text = $"{current} / {total}";
                        Log($"✓ {Path.GetFileName(file)}");
                    });
                }

                Dispatcher.Invoke(() => {
                    Log($"Done! {current - skipped} file(s) sent to Kindle, {skipped} skipped.");
                    TriggerKindleRescan(kindlePath);
                    EndOperation();
                    RefreshSelectedStatus();
                });
            });
        }

        // ==============================
        // 📧 SEND BY EMAIL
        // ==============================
        private void EmailFields_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (BtnSendByEmail == null || SenderEmailBox == null ||
                AppPasswordBox == null || KindleEmailBox == null) return;

            BtnSendByEmail.IsEnabled =
                !string.IsNullOrWhiteSpace(SenderEmailBox.Text) &&
                !string.IsNullOrWhiteSpace(AppPasswordBox.Password) &&
                !string.IsNullOrWhiteSpace(KindleEmailBox.Text);
        }

        private void AnimeUnitedFields_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (AnimeUnitedEmailBox == null) return;

            Properties.Settings.Default.AnimeUnitedUserEmail = AnimeUnitedEmailBox.Text.Trim();

            // A chave vai para o user.config (perfil do Windows), nunca para o
            // Settings.settings do projeto — o repositório é público.
            if (AnimeUnitedApiKeyBox != null)
                Properties.Settings.Default.AnimeUnitedApiKey = AnimeUnitedApiKeyBox.Password.Trim();

            Properties.Settings.Default.Save();
        }

        private void AnimeUnitedSave_Click(object sender, RoutedEventArgs e)
        {
            AnimeUnitedFields_Changed(sender, e);
            Log("AnimeUnited: configurações salvas.");
            RefreshAnimeUnitedAdminStatus();
        }

        // Consulta o servidor (tabela de usuários) para saber se o email atual tem nível de acesso admin.
        private async void RefreshAnimeUnitedAdminStatus()
        {
            string apiUrl = Properties.Settings.Default.AnimeUnitedApiUrl;
            string email  = Properties.Settings.Default.AnimeUnitedUserEmail;

            string apiKey = Properties.Settings.Default.AnimeUnitedApiKey;

            // Sem chave o servidor nega tudo e os botões de admin somem sem explicação.
            // Dizer isso em voz alta evita procurar o botão de upload que nunca aparece.
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("TROQUE", StringComparison.OrdinalIgnoreCase))
                Log("AnimeUnited: API key missing — paste it in the AnimeUnited panel (API key field) and hit 💾 Salvar. " +
                    "Upload, Delete and Users stay hidden until then.");

            bool isAdmin = false;
            if (!string.IsNullOrWhiteSpace(apiUrl) && !string.IsNullOrWhiteSpace(email))
            {
                try
                {
                    var client = new AnimeUnitedClient(apiUrl, Properties.Settings.Default.AnimeUnitedApiKey, email);
                    isAdmin = await client.CheckIsAdminAsync();
                }
                catch (Exception ex)
                {
                    Log($"AnimeUnited: could not check access level ({ex.Message}).");
                }
            }

            _isAnimeUnitedAdmin = isAdmin;
            BtnUpload.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
            BtnDeleteCloud.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
            BtnUsuarios.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
            LanguageFieldRow.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
            if (!_isAnimeUnitedMode) LoadMangas();
        }
        private async void SendByEmail_Click(object sender, RoutedEventArgs e)
        {
            var path = GetSelectedPath();
            if (path == null) return;

            string senderEmail  = SenderEmailBox.Text.Trim();
            string appPassword  = AppPasswordBox.Password;
            string kindleEmail  = KindleEmailBox.Text.Trim();

            if (string.IsNullOrEmpty(senderEmail) || string.IsNullOrEmpty(appPassword) || string.IsNullOrEmpty(kindleEmail))
            {
                MessageBox.Show(
                    "Please fill in all three email fields:\n• Your email\n• App password\n• Kindle email",
                    "Missing Email Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Detecta provedor SMTP pelo domínio do remetente
            string domain = senderEmail.Split('@').LastOrDefault()?.ToLower() ?? "";
            var (smtpHost, smtpPort) = domain switch
            {
                "gmail.com"   => ("smtp.gmail.com",    587),
                "icloud.com"  => ("smtp.mail.me.com",  587),
                "me.com"      => ("smtp.mail.me.com",  587),
                "mac.com"     => ("smtp.mail.me.com",  587),
                _ => (null, 0)
            };

            if (smtpHost == null)
            {
                MessageBox.Show(
                    $"Unsupported email provider: @{domain}\n\nCurrently supported: Gmail (@gmail.com) and iCloud (@icloud.com / @me.com / @mac.com)",
                    "Unsupported Provider", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Busca MOBIs respeitando o Range selector
            string convertedFolder = Path.Combine(path, "Converted");
            var files = GetConvertedFiles(convertedFolder);

            if (files.Length == 0)
            {
                Log("No .mobi files found in /Converted. Run Generate mobi first.");
                return;
            }

            if (ChkSendRange.IsChecked == true)
            {
                var selected = ParseVolumeSelection(VolumeRangeBox.Text);
                if (selected.Count == 0) { Log("Please enter a valid volume range."); return; }
                files = files.Where(f =>
                {
                    var m = System.Text.RegularExpressions.Regex.Match(
                        Path.GetFileNameWithoutExtension(f), @"Volume (\d+)");
                    return m.Success && selected.Contains(int.Parse(m.Groups[1].Value));
                }).ToArray();
            }

            if (files.Length == 0) { Log("No matching volumes for the selected range."); return; }

            Log($"📧 Sending {files.Length} MOBI(s) via {smtpHost} → {kindleEmail}...");
            BtnSendByEmail.IsEnabled = false;
            var token = StartOperation();

            await Task.Run(async () =>
            {
                int total   = files.Length;
                int current = 0;
                int failed  = 0;

                using var client = new SmtpClient(smtpHost, smtpPort)
                {
                    EnableSsl   = true,
                    Credentials = new NetworkCredential(senderEmail, appPassword),
                    Timeout     = 120_000,
                };

                foreach (var file in files)
                {
                    if (token.IsCancellationRequested) break;
                    string fileName = Path.GetFileName(file);

                    try
                    {
                        using var message = new MailMessage(senderEmail, kindleEmail)
                        {
                            Subject = Path.GetFileNameWithoutExtension(file),
                            Body    = "Sent via Manga Manager",
                        };
                        message.Attachments.Add(new Attachment(file));
                        await client.SendMailAsync(message);

                        current++;
                        Dispatcher.Invoke(() =>
                        {
                            ProgressBar.Value = (double)current / total * 100;
                            ProgressText.Text = $"{current} / {total}";
                            Log($"  ✓ Sent: {fileName}");
                        });
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        current++;
                        Dispatcher.Invoke(() => Log($"  ⚠ Failed: {fileName} — {ex.Message}"));
                    }

                    // Pequena pausa entre envios para não saturar o SMTP
                    if (current < total)
                        await Task.Delay(1500);
                }

                Dispatcher.Invoke(() =>
                {
                    int sent = current - failed;
                    Log(failed == 0
                        ? $"✓ All {sent} file(s) sent! They will appear in your Kindle app after Amazon processes them (usually a few minutes)."
                        : $"⚠ {sent} sent, {failed} failed. Check your app password and approved sender list.");
                    BtnSendByEmail.IsEnabled = true;
                    EndOperation();
                });
            });
        }

        private void TriggerKindleRescan(string kindleDocsPath)
        {
            try
            {
                // O Kindle monitora a pasta raiz do dispositivo
                // Criar/tocar o arquivo .system/RESCAN força o re-index
                string kindleRoot = Path.GetDirectoryName(kindleDocsPath) ?? kindleDocsPath;

                // Método 1: arquivo de trigger na raiz
                string triggerFile = Path.Combine(kindleRoot, "system", "RESCAN");
                string systemFolder = Path.Combine(kindleRoot, "system");

                if (Directory.Exists(systemFolder))
                {
                    File.WriteAllText(triggerFile, "");
                    Log("📱 Kindle rescan triggered. Books should appear shortly.");
                    return;
                }

                // Método 2: atualiza o timestamp da pasta documents
                // O Kindle detecta mudanças no diretório e re-indexa
                Directory.SetLastWriteTime(kindleDocsPath, DateTime.Now);
                Log("📱 Kindle index refresh requested. Eject and reconnect if books don't appear.");
            }
            catch (Exception ex)
            {
                Log($"⚠ Could not trigger Kindle rescan: {ex.Message}");
            }
        }

        private void RemoveFromKindle_Click(object sender, RoutedEventArgs e)
        {
            if (MangaList.SelectedItem is not MangaItem selected) return;

            string? kindlePath = FindKindlePath();
            if (kindlePath == null)
            {
                Log("Kindle not found. Make sure it's connected via USB and unlocked.");
                return;
            }

            string mangaFolder = Path.Combine(kindlePath, "Mangas", selected.Name);

            if (!Directory.Exists(mangaFolder))
            {
                Log($"Folder not found on Kindle: {selected.Name}");
                return;
            }

            var result = MessageBox.Show(
                $"Remove \"{selected.Name}\" from Kindle?\n\nThis will permanently delete:\n{mangaFolder}",
                "Remove from Kindle",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                // 1. Remove a pasta do mangá
                Directory.Delete(mangaFolder, recursive: true);
                Log($"🗑 Removed from Kindle: {selected.Name}");

                // 2. Remove thumbnails relacionados ao mangá
                string kindleRoot = Path.GetDirectoryName(kindlePath) ?? kindlePath;
                string thumbnailsFolder = Path.Combine(kindleRoot, "system", "thumbnails");
                int thumbsDeleted = 0;

                if (Directory.Exists(thumbnailsFolder))
                {
                    // Thumbnails têm nome baseado no path do arquivo
                    // Formato: thumbnail_HASH_EBOK_portrait.jpg
                    // Deleta todos os thumbs — o Kindle regenera os válidos no rescan
                    var thumbs = Directory.GetFiles(thumbnailsFolder, "*.jpg");
                    foreach (var thumb in thumbs)
                    {
                        try { File.Delete(thumb); thumbsDeleted++; }
                        catch { }
                    }
                    Log($"🗑 Removed {thumbsDeleted} thumbnail(s).");
                }

                // 3. Trigger de rescan
                TriggerKindleRescan(kindlePath);

                RefreshSelectedStatus();
            }
            catch (Exception ex)
            {
                Log($"⚠ Error removing from Kindle: {ex.Message}");
            }
        }

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
            // Arquivos soltos (pdf/cbz/epub/mobi) sentados direto na pasta do volume, ainda sem
            // capítulos extraídos — é exatamente o que o Extract processa e transforma em capítulos.
            bool hasFilesInVolumes = volumes.Any(v => GetMangaFiles(v).Length > 0);
            bool hasImages = volumes.Any(v =>
                Directory.GetDirectories(v, "Capitulo *").Any(ch =>
                    Directory.GetFiles(ch, "*.jpg").Length > 0 ||
                    Directory.GetFiles(ch, "*.png").Length > 0 ||
                    Directory.GetFiles(ch, "*.webp").Length > 0));
            bool hasComicInfo = volumes.Any(v =>
                Directory.GetDirectories(v, "Capitulo *").Any(ch =>
                    File.Exists(Path.Combine(ch, "ComicInfo.xml"))));
            bool cleaned = volumes.Length > 0 && !volumes.Any(v => HasMangaFiles(v));
            bool kindleConnected = FindKindlePath() != null;

            // Fetch Author: sempre habilitado
            BtnFetchAuthor.IsEnabled = true;
            SetCheck(BtnFetchAuthor, hasAuthor);

            // Organize: sempre habilitado (pode ter CBZ sem autor preenchido)
            BtnOrganize.IsEnabled = true;
            SetCheck(BtnOrganize, hasChapters);

            BtnExtract.IsEnabled = hasChapters || hasFilesInVolumes;
            SetCheck(BtnExtract, hasImages);

            BtnResize.IsEnabled = hasImages;
            BtnCloud.IsEnabled  = hasImages;

            BtnComicInfo.IsEnabled = hasImages;
            SetCheck(BtnComicInfo, hasComicInfo);

            BtnCleanup.IsEnabled = hasComicInfo;
            SetCheck(BtnCleanup, cleaned);

            // Os dois processamentos ficam disponíveis após o cleanup. São destinos
            // alternativos: o mesmo mangá vai para o Kindle ou para a nuvem.
            BtnResize.IsEnabled = cleaned;
            BtnCloud.IsEnabled  = cleaned;
            string convertedDir = Path.Combine(mangaPath, "Converted");
            bool hasMobiInConverted = Directory.Exists(convertedDir) &&
                                      Directory.GetFiles(convertedDir, "*.mobi").Length > 0;
            SetCheck(BtnResize, hasMobiInConverted);

            BtnOpenKCC.IsEnabled = cleaned;
            BtnRead.IsEnabled = hasImages;
            BtnSendToKindle.IsEnabled = hasMobiInConverted;
            BtnRemoveFromKindle.IsEnabled = kindleConnected;
            BtnUpload.IsEnabled = hasImages;
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
            UpdateDetailsPanel(fullPath);
            var updated = BuildMangaItem(selected.Name, fullPath, FindKindlePath());

            selected.StatusColor = updated.StatusColor;
            selected.StatusForeground = updated.StatusForeground;
            selected.StatusWeight = updated.StatusWeight;
            selected.Chk1 = updated.Chk1;
            selected.Chk2 = updated.Chk2;
            selected.Chk3 = updated.Chk3;
            selected.Chk4 = updated.Chk4;
            selected.Chk5 = updated.Chk5;
            selected.Chk6 = updated.Chk6;
            selected.Chk7 = updated.Chk7;
            selected.ChkKindle = updated.ChkKindle;
            selected.ChkLanguage = updated.ChkLanguage;
            selected.ReadProgress = updated.ReadProgress;
            selected.NsfwBadge = updated.NsfwBadge;
            selected.IsOngoing = updated.IsOngoing;

            UpdateButtonStates(fullPath);
        }

        // ==============================
        // 📋 DETAILS PANEL
        // ==============================

        // Retorna um arquivo JSON de progresso para o mangá
        private static string ProgressFilePath(string mangaPath) =>
            Path.Combine(mangaPath, "reading_progress.json");

        private static Dictionary<string, ReadStatus> LoadReadingProgress(string mangaPath)
        {
            try
            {
                string path = ProgressFilePath(mangaPath);
                if (!File.Exists(path)) return new();
                var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                return raw?.ToDictionary(
                    kv => kv.Key,
                    kv => Enum.TryParse<ReadStatus>(kv.Value, out var s) ? s : ReadStatus.Unread)
                    ?? new();
            }
            catch { return new(); }
        }

        private static void SaveReadingProgress(string mangaPath, IEnumerable<KindleVolumeRow> rows)
        {
            try
            {
                var dict = rows.ToDictionary(r => r.VolumeName, r => r.Status.ToString());
                File.WriteAllText(ProgressFilePath(mangaPath),
                    JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        // Lê o reader.db do Kindle e retorna percentual de leitura por nome de arquivo (0–100).
        // Copia para temp antes de abrir para evitar conflito de lock.
        // Detecta o schema automaticamente entre os formatos conhecidos dos firmwares Kindle.
        private Dictionary<string, int> ReadKindleDb(string kindleRoot)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string dbPath = Path.Combine(kindleRoot, "system", "reader.db");
            if (!File.Exists(dbPath))
            {
                var alt = Directory.GetFiles(kindleRoot, "reader.db", SearchOption.AllDirectories)
                                   .FirstOrDefault();
                if (alt != null)
                    dbPath = alt;
                else
                    return result; // Kindle sem reader.db — fallback para .sdr
            }

            string tmp = Path.Combine(Path.GetTempPath(), "mm_reader_tmp.db");
            try
            {
                File.Copy(dbPath, tmp, overwrite: true);
                using var conn = new SqliteConnection($"Data Source={tmp};Mode=ReadOnly;");
                conn.Open();

                // Descobre quais tabelas existem
                var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) tables.Add(r.GetString(0));
                }

                // tabelas encontradas no reader.db

                // Tenta cada schema conhecido
                TryReadSchema(conn, tables, result,
                    table: "books",
                    keyCol: "key", pctCol: "percentage_read", scale: 100.0);

                TryReadSchema(conn, tables, result,
                    table: "BOOK_INFO",
                    keyCol: "CONTENT_PATH", pctCol: "PERCENTAGE_READ", scale: 100.0);

                TryReadSchema(conn, tables, result,
                    table: "BOOK_READING_STATE",
                    keyCol: "CONTENT_PATH", pctCol: "PERCENTAGE_READ", scale: 100.0);

                // Formato alternativo onde percentual é 0–1 (float)
                if (result.Count == 0)
                {
                    TryReadSchema(conn, tables, result,
                        table: "books",
                        keyCol: "key", pctCol: "percentageRead", scale: 100.0);
                }

                if (result.Count > 0) Log($"📱 Kindle reader.db — {result.Count} livro(s) com progresso.");
            }
            catch (Exception ex)
            {
                // reader.db ilegível — fallback para .sdr
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
            return result;
        }

        private static void TryReadSchema(SqliteConnection conn,
            HashSet<string> tables, Dictionary<string, int> result,
            string table, string keyCol, string pctCol, double scale)
        {
            if (!tables.Contains(table)) return;
            try
            {
                // Verifica se as colunas existem
                var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var pragma = conn.CreateCommand())
                {
                    pragma.CommandText = $"PRAGMA table_info({table})";
                    using var pr = pragma.ExecuteReader();
                    while (pr.Read()) cols.Add(pr.GetString(1)); // coluna "name"
                }
                if (!cols.Contains(keyCol) || !cols.Contains(pctCol)) return;

                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT {keyCol}, {pctCol} FROM {table} WHERE {pctCol} IS NOT NULL AND {pctCol} > 0";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string key = reader.GetString(0);
                    double raw = reader.GetDouble(1);
                    // Normaliza: se scale=100 e raw>1, é 0-100; se raw<=1, multiplica
                    int pct = raw > 1.0 ? (int)Math.Round(raw) : (int)Math.Round(raw * scale);
                    string fileName = Path.GetFileNameWithoutExtension(key);
                    if (!string.IsNullOrEmpty(fileName))
                        result.TryAdd(fileName, Math.Clamp(pct, 0, 100));
                }
            }
            catch { }
        }

        private List<KindleVolumeRow> GetKindleVolumeStatuses(string mangaPath, string? kindlePath)
        {
            string mangaName = Path.GetFileName(mangaPath);
            string convertedFolder = Path.Combine(mangaPath, "Converted");
            if (!Directory.Exists(convertedFolder)) return new();

            var mobiFiles = Directory.GetFiles(convertedFolder, "*.mobi")
                .OrderBy(f => f).ToArray();
            if (mobiFiles.Length == 0) return new();

            var saved = LoadReadingProgress(mangaPath);

            // Tenta ler o reader.db do Kindle para obter percentuais precisos
            string? kindleRoot = kindlePath != null ? Path.GetDirectoryName(kindlePath) : null;
            var kindleDb = kindleRoot != null ? ReadKindleDb(kindleRoot) : new();

            string? kindleMangaFolder = kindlePath != null
                ? Path.Combine(kindlePath, "Mangas", mangaName) : null;

            var rows = new List<KindleVolumeRow>();
            foreach (var mobi in mobiFiles)
            {
                string baseName = Path.GetFileNameWithoutExtension(mobi);
                var m = System.Text.RegularExpressions.Regex.Match(baseName, @"[Vv]olume\s+(\d+)");
                string shortName = m.Success ? $"Vol. {int.Parse(m.Groups[1].Value):D2}" : baseName;

                ReadStatus status = ReadStatus.Unread;
                int percentage = -1;
                bool kindleDetected = false;

                // 1. Kindle reader.db (mais preciso — só disponível em alguns firmwares)
                if (kindleDb.TryGetValue(baseName, out int dbPct))
                {
                    percentage = dbPct;
                    status = dbPct >= 90 ? ReadStatus.Read
                           : dbPct > 0  ? ReadStatus.Reading
                                        : ReadStatus.Unread;
                    kindleDetected = true;
                }
                // 2. Arquivos .sdr no Kindle (funciona em todos os firmwares)
                else if (kindleMangaFolder != null)
                {
                    string sdrPath    = Path.Combine(kindleMangaFolder, baseName + ".sdr");
                    string kindleBook = Path.Combine(kindleMangaFolder, Path.GetFileName(mobi));
                    if (Directory.Exists(sdrPath))
                    {
                        var (detectedStatus, detectedPct) = GetKindleReadProgress(sdrPath, kindleBook);
                        status         = detectedStatus;
                        percentage     = detectedPct;
                        kindleDetected = true;
                    }
                }

                // 3. Status salvo manualmente — aplica só quando Kindle não detectou nada,
                //    ou quando usuário marcou explicitamente como ✅ Read (respeita conclusão manual)
                if (saved.TryGetValue(shortName, out var savedStatus))
                {
                    if (!kindleDetected)
                        status = savedStatus;
                    else if (savedStatus == ReadStatus.Read && status != ReadStatus.Read)
                        status = ReadStatus.Read; // usuário marcou como lido — mantém
                }

                rows.Add(new KindleVolumeRow { VolumeName = shortName, Status = status, Percentage = percentage });
            }

            return rows;
        }

        // Lê o progresso de leitura dos arquivos binários do .sdr (.azw3f, .azw3r, .han)
        // mobiPath = caminho do MOBI no Kindle, usado para calcular % via tamanho do arquivo
        private static (ReadStatus status, int percentage) GetKindleReadProgress(string sdrPath, string? mobiPath = null)
        {
            try
            {
                foreach (var file in Directory.GetFiles(sdrPath))
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();

                    // Abre com FileShare.ReadWrite para não bloquear enquanto Kindle está conectado
                    byte[] bytes;
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        bytes = new byte[fs.Length];
                        _ = fs.Read(bytes, 0, bytes.Length);
                    }
                    var text = System.Text.Encoding.Latin1.GetString(bytes);

                    if (ext == ".azw3f" || ext == ".azw3r")
                    {
                        // Formato binário Kindle: contém "fpr" seguido de bytes não-dígito e depois o número
                        var m = System.Text.RegularExpressions.Regex.Match(text, @"fpr[^\d]{1,10}(\d+)");
                        if (m.Success && int.TryParse(m.Groups[1].Value, out int fpr) && fpr > 0)
                        {
                            // Percentual aproximado: posição ÷ (tamanho_mobi / 167 bytes por location)
                            if (mobiPath != null && File.Exists(mobiPath))
                            {
                                long mobiSize = new FileInfo(mobiPath).Length;
                                long totalLocs = Math.Max(1, mobiSize / 167);
                                int pct = (int)Math.Min(100, fpr * 100L / totalLocs);
                                var st = pct >= 90 ? ReadStatus.Read : ReadStatus.Reading;
                                return (st, pct);
                            }
                            return (ReadStatus.Reading, -1);
                        }
                    }
                    else if (ext == ".han")
                    {
                        // Formato JSON (firmwares mais antigos)
                        var m = System.Text.RegularExpressions.Regex.Match(
                            text, @"""percentageRead""\s*:\s*([\d.]+)");
                        if (m.Success && double.TryParse(m.Groups[1].Value,
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out double pct))
                        {
                            int pctInt = (int)(pct * 100);
                            return (pctInt >= 90 ? ReadStatus.Read : ReadStatus.Reading, pctInt);
                        }
                    }
                }
            }
            catch { }

            // .sdr existe mas sem dados legíveis = pelo menos foi aberto
            return (ReadStatus.Reading, -1);
        }

        private void UpdateDetailsPanel(string mangaPath)
        {
            string mangaName = Path.GetFileName(mangaPath);
            var volumes = Directory.GetDirectories(mangaPath, "* - Volume *");

            int totalChapters = volumes.Sum(v =>
                Directory.GetDirectories(v, "Capitulo *").Length);

            string author = ReadAuthorFromComicInfo(mangaPath);

            DetailMangaName.Text = mangaName;
            DetailAuthor.Text    = string.IsNullOrEmpty(author) ? "—" : author;
            DetailVolumes.Text   = volumes.Length > 0 ? volumes.Length.ToString() : "—";
            DetailChapters.Text  = totalChapters > 0 ? totalChapters.ToString() : "—";

            _currentMangaPath = mangaPath;
            SyncFlagCheckboxes(mangaPath);

            if (_isAnimeUnitedMode)
            {
                // Modo AnimeUnited: nada de pipeline local/Kindle, só progresso de leitura.
                LocalPipelineStatusSection.Visibility = Visibility.Collapsed;
                KindleVolumeList.ItemsSource = null;
                KindleVolumeSection.Visibility = Visibility.Collapsed;

                var readRows = GetReadVolumeStatuses(mangaPath);
                ReadVolumeList.ItemsSource = readRows;
                ReadVolumeSection.Visibility = readRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            LocalPipelineStatusSection.Visibility = Visibility.Visible;
            ReadVolumeList.ItemsSource = null;
            ReadVolumeSection.Visibility = Visibility.Collapsed;

            bool extracted = volumes.Any(v =>
                Directory.GetDirectories(v, "Capitulo *").Any(ch =>
                    Directory.GetFiles(ch, "*.jpg").Length > 0 ||
                    Directory.GetFiles(ch, "*.png").Length > 0 ||
                    Directory.GetFiles(ch, "*.webp").Length > 0));

            bool hasComicInfo = volumes.Any(v =>
                Directory.GetDirectories(v, "Capitulo *").Any(ch =>
                    File.Exists(Path.Combine(ch, "ComicInfo.xml"))));

            bool cleaned = volumes.Length > 0 && !volumes.Any(v => HasMangaFiles(v));

            bool hasMobi = HasConvertedFiles(Path.Combine(mangaPath, "Converted"));

            string? kindlePath = FindKindlePath();
            bool onKindle = kindlePath != null &&
                Directory.Exists(Path.Combine(kindlePath, "Mangas", mangaName)) &&
                Directory.GetFiles(Path.Combine(kindlePath, "Mangas", mangaName), "*.mobi").Length > 0;

            SetDetailYesNo(DetailExtracted,  extracted);
            SetDetailYesNo(DetailComicInfo,  hasComicInfo);
            SetDetailYesNo(DetailCleanup,    cleaned);
            SetDetailYesNo(DetailMobi,       hasMobi);
            SetDetailYesNo(DetailKindle,     onKindle);

            // Sem catálogo (API fora do ar, chave errada) a resposta honesta é "—".
            if (_cloudMangaNames == null)
                SetDetailUnknown(DetailCloud);
            else
                SetDetailYesNo(DetailCloud, _cloudMangaNames.Contains(mangaName));

            var kindleRows = GetKindleVolumeStatuses(mangaPath, kindlePath);
            KindleVolumeList.ItemsSource = kindleRows;
            KindleVolumeSection.Visibility = kindleRows.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;

            // Persiste status detectado pelo Kindle para uso offline
            if (kindlePath != null && kindleRows.Count > 0)
                SaveReadingProgress(mangaPath, kindleRows);
        }

        // Status de leitura por volume sem depender de MOBI/Kindle — usado no modo AnimeUnited.
        private List<KindleVolumeRow> GetReadVolumeStatuses(string mangaPath)
        {
            var volumes = Directory.GetDirectories(mangaPath, "* - Volume *").OrderBy(v => v).ToArray();
            if (volumes.Length == 0) return new();

            var saved = LoadReadingProgress(mangaPath);

            var rows = new List<KindleVolumeRow>();
            foreach (var vol in volumes)
            {
                string volName = Path.GetFileName(vol);
                var m = System.Text.RegularExpressions.Regex.Match(volName, @"[Vv]olume\s+(\d+)");
                string shortName = m.Success ? $"Vol. {int.Parse(m.Groups[1].Value):D2}" : volName;

                ReadStatus status = saved.TryGetValue(shortName, out var s) ? s : ReadStatus.Unread;
                rows.Add(new KindleVolumeRow { VolumeName = shortName, Status = status, Percentage = -1 });
            }
            return rows;
        }

        private void ClearDetailsPanel()
        {
            DetailMangaName.Text  = "Select a manga";
            DetailAuthor.Text     = "—";
            DetailVolumes.Text    = "—";
            DetailChapters.Text   = "—";
            foreach (var tb in new[] { DetailExtracted, DetailComicInfo, DetailCleanup,
                                       DetailMobi, DetailKindle, DetailCloud })
                SetDetailUnknown(tb);
            LocalPipelineStatusSection.Visibility = Visibility.Visible;
            KindleVolumeList.ItemsSource = null;
            KindleVolumeSection.Visibility = Visibility.Collapsed;
            ReadVolumeList.ItemsSource = null;
            ReadVolumeSection.Visibility = Visibility.Collapsed;
            SyncFlagCheckboxes(null);
        }

        // Estado que não dá para afirmar — diferente de "não".
        private static void SetDetailUnknown(System.Windows.Controls.TextBlock tb)
        {
            tb.Text = "—";
            tb.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));
        }

        // ── Marcas da obra ───────────────────────────────────────────────────
        // Marcar as caixas em código dispara Checked/Unchecked como se fosse
        // clique do usuário; esta trava evita regravar o JSON e remexer na capa.
        private bool _syncingFlags;

        private void SyncFlagCheckboxes(string? mangaPath)
        {
            _syncingFlags = true;
            try
            {
                bool enabled = mangaPath != null && Directory.Exists(mangaPath) && !_isAnimeUnitedMode;
                var flags = enabled ? LoadMangaFlags(mangaPath!) : new MangaFlags();

                ChkNsfw.IsEnabled    = enabled;
                ChkOngoing.IsEnabled = enabled;
                ChkNsfw.IsChecked    = flags.Nsfw;
                ChkOngoing.IsChecked = flags.Ongoing;
            }
            finally { _syncingFlags = false; }
        }

        private async void ChkNsfw_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingFlags || string.IsNullOrEmpty(_currentMangaPath)) return;

            bool nsfw = ChkNsfw.IsChecked == true;
            var flags = LoadMangaFlags(_currentMangaPath) with { Nsfw = nsfw };
            SaveMangaFlags(_currentMangaPath, flags);

            bool badgeOnCover = false;
            if (nsfw)
            {
                await EnsureNsfwBadgeImageAsync();
                badgeOnCover = await ApplyNsfwBadgeToCover(_currentMangaPath);
                if (badgeOnCover)
                    Log($"🔞 {Path.GetFileName(_currentMangaPath)} marked NSFW — badge applied to cover.jpg.");
            }
            else
            {
                if (RemoveNsfwBadgeFromCover(_currentMangaPath))
                    Log($"{Path.GetFileName(_currentMangaPath)} is no longer NSFW — clean cover restored.");
            }

            if (MangaList.SelectedItem is MangaItem item)
            {
                // Selo gravado na capa dispensa a sobreposição; sem capa, ela é a marca.
                item.NsfwBadge = nsfw && !badgeOnCover ? _nsfwBadgeImage : null;
                item.CoverImage = LoadImageFromFile(Path.Combine(_currentMangaPath, CoverName));
            }
        }

        private void ChkOngoing_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingFlags || string.IsNullOrEmpty(_currentMangaPath)) return;

            bool ongoing = ChkOngoing.IsChecked == true;
            SaveMangaFlags(_currentMangaPath, LoadMangaFlags(_currentMangaPath) with { Ongoing = ongoing });

            if (MangaList.SelectedItem is MangaItem item) item.IsOngoing = ongoing;

            Log(ongoing
                ? $"🔄 {Path.GetFileName(_currentMangaPath)}: ongoing — each step will only handle what is missing."
                : $"{Path.GetFileName(_currentMangaPath)}: no longer ongoing — every step reprocesses in full.");
        }

        private void SetDetailYesNo(System.Windows.Controls.TextBlock tb, bool value)
        {
            tb.Text = value ? "Yes" : "No";
            tb.Foreground = new System.Windows.Media.SolidColorBrush(
                value ? System.Windows.Media.Color.FromRgb(0x95, 0xb6, 0x34)
                      : System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));
        }

        private string ReadAuthorFromComicInfo(string mangaPath)
        {
            var volumes = Directory.GetDirectories(mangaPath, "* - Volume *");
            foreach (var vol in volumes)
            {
                // Volume root first (mais confiável)
                var author = ParseWriterFromComicInfo(Path.Combine(vol, "ComicInfo.xml"));
                if (!string.IsNullOrEmpty(author)) return author;

                // Fallback: primeiro capítulo
                foreach (var ch in Directory.GetDirectories(vol, "Capitulo *"))
                {
                    author = ParseWriterFromComicInfo(Path.Combine(ch, "ComicInfo.xml"));
                    if (!string.IsNullOrEmpty(author)) return author;
                }
            }
            return "";
        }

        private static string ParseWriterFromComicInfo(string xmlPath)
        {
            try
            {
                if (!File.Exists(xmlPath)) return "";
                return XDocument.Load(xmlPath).Root?.Element("Writer")?.Value ?? "";
            }
            catch { return ""; }
        }

        private string ReadLanguageFromComicInfo(string mangaPath)
        {
            var volumes = Directory.GetDirectories(mangaPath, "* - Volume *");
            foreach (var vol in volumes)
            {
                var lang = ParseLanguageFromComicInfo(Path.Combine(vol, "ComicInfo.xml"));
                if (!string.IsNullOrEmpty(lang)) return lang;

                foreach (var ch in Directory.GetDirectories(vol, "Capitulo *"))
                {
                    lang = ParseLanguageFromComicInfo(Path.Combine(ch, "ComicInfo.xml"));
                    if (!string.IsNullOrEmpty(lang)) return lang;
                }
            }
            return "";
        }

        private static string ParseLanguageFromComicInfo(string xmlPath)
        {
            try
            {
                if (!File.Exists(xmlPath)) return "";
                return XDocument.Load(xmlPath).Root?.Element("LanguageISO")?.Value ?? "";
            }
            catch { return ""; }
        }

        private static string LanguageBadge(string? language) => language?.ToUpperInvariant() switch
        {
            "PT-BR" => "PT-BR",
            "EN-US" => "EN-US",
            _ => "",
        };

        private void MangaList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (MangaList.SelectedItem is MangaItem selected)
            {
                if (selected.IsMobiOnly)
                {
                    ResetButtonStates();
                    BtnRead.IsEnabled = true;
                    ClearDetailsPanel();
                    DetailMangaName.Text = selected.Name;
                    DetailAuthor.Text = "MOBI file";
                    _watcher?.Dispose();
                }
                else if (selected.IsRemote)
                {
                    ResetButtonStates();
                    BtnFetchAuthor.IsEnabled = false;
                    BtnOrganize.IsEnabled = false;
                    BtnRead.IsEnabled = true;
                    BtnDownload.IsEnabled = true;
                    BtnDeleteCloud.IsEnabled = _isAnimeUnitedAdmin && selected.IsOnCloud;
                    ClearDetailsPanel();
                    DetailMangaName.Text = selected.Name;
                    DetailAuthor.Text = $"{selected.RemoteVolumeCount} volume(s) on AnimeUnited";
                    _watcher?.Dispose();
                }
                else
                {
                    string fullPath = Path.Combine(basePath, selected.Name);
                    UpdateButtonStates(fullPath);
                    UpdateDetailsPanel(fullPath);
                    StartWatcher(fullPath);

                    // Item do catálogo AnimeUnited que também existe no disco: IsRemote é
                    // false (nada a baixar), mas continua apagável na nuvem.
                    BtnDeleteCloud.IsEnabled = _isAnimeUnitedAdmin && selected.IsOnCloud;
                }
            }
            else
            {
                ResetButtonStates();
                ClearDetailsPanel();
                _watcher?.Dispose();
            }
        }

        // ── Manga Reader ───────────────────────────────────────────────────────
        private void MangaList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => BtnRead_Click(sender, e);

        private async void BtnRead_Click(object sender, RoutedEventArgs e)
        {
            if (MangaList.SelectedItem is not MangaItem item) return;

            if (item.IsRemote)
            {
                bool downloaded = await DownloadMangaAsync(item, volumeFilter: null);
                if (!downloaded || MangaList.SelectedItem is not MangaItem refreshed) return;
                item = refreshed;
            }

            ReaderWindow reader;

            if (item.IsMobiOnly)
            {
                reader = new ReaderWindow(item.MobiFilePath!); // handles MOBI, CBZ, EPUB
            }
            else
            {
                string path = Path.Combine(basePath, item.Name);
                if (!Directory.Exists(path)) return;

                bool hasImages = Directory.GetDirectories(path, "* - Volume *")
                    .Any(vol => Directory.GetDirectories(vol, "Capitulo *")
                        .Any(ch => Directory.GetFiles(ch)
                            .Any(f => f.EndsWith(".jpg",  StringComparison.OrdinalIgnoreCase) ||
                                      f.EndsWith(".png",  StringComparison.OrdinalIgnoreCase) ||
                                      f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))));

                bool hasMobi = Directory.Exists(Path.Combine(path, "Converted")) &&
                               Directory.GetFiles(Path.Combine(path, "Converted"), "*.mobi").Length > 0;

                if (!hasImages && !hasMobi)
                {
                    MessageBox.Show(
                        "No extracted pages or MOBI files found for this manga.\n\n" +
                        "Run 'Extract' first to unpack the chapter files.",
                        "Manga Reader", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                reader = new ReaderWindow(path, item.Name);
                reader.Closed += (_, _) =>
                {
                    if (MangaList.SelectedItem is MangaItem cur && !cur.IsMobiOnly)
                        UpdateDetailsPanel(Path.Combine(basePath, cur.Name));
                };
            }

            reader.Owner = this;
            reader.Show();
        }

        private void ResetButtonStates()
        {
            BtnFetchAuthor.IsEnabled = true;
            BtnOrganize.IsEnabled = true;
            BtnRead.IsEnabled = false;
            BtnDownload.IsEnabled = false;
            BtnExtract.IsEnabled = false;
            BtnComicInfo.IsEnabled = false;
            BtnResize.IsEnabled = false;
            BtnCloud.IsEnabled = false;
            BtnCleanup.IsEnabled = false;
            BtnOpenKCC.IsEnabled = false;
            BtnSendToKindle.IsEnabled = false;
            BtnRemoveFromKindle.IsEnabled = false;
            BtnUpload.IsEnabled = false;
            BtnDeleteCloud.IsEnabled = false;
        }


        private void ChkSendAll_Checked(object sender, RoutedEventArgs e)
        {
            if (ChkSendRange == null || VolumeRangeBox == null) return;
            ChkSendRange.IsChecked = false;
            VolumeRangeBox.IsEnabled = false;
        }

        private void ChkSendAll_Unchecked(object sender, RoutedEventArgs e)
        {
            if (ChkSendRange == null || VolumeRangeBox == null) return;
            ChkSendRange.IsChecked = true;
            VolumeRangeBox.IsEnabled = true;
        }

        private void ChkSendRange_Checked(object sender, RoutedEventArgs e)
        {
            if (ChkSendAll == null || VolumeRangeBox == null) return;
            ChkSendAll.IsChecked = false;
            VolumeRangeBox.IsEnabled = true;
        }

        private void ChkSendRange_Unchecked(object sender, RoutedEventArgs e)
        {
            if (ChkSendAll == null || VolumeRangeBox == null) return;
            ChkSendAll.IsChecked = true;
            VolumeRangeBox.IsEnabled = false;
        }

        // Retorna lista de números de volume selecionados
        // Suporta: "1,2,3" ou "1-6" ou "1,3,5-8"
        private HashSet<int> ParseVolumeSelection(string input)
        {
            var result = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(input)) return result;

            foreach (var part in input.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Contains('-'))
                {
                    var bounds = trimmed.Split('-');
                    if (int.TryParse(bounds[0].Trim(), out int start) &&
                        int.TryParse(bounds[1].Trim(), out int end))
                    {
                        for (int i = start; i <= end; i++)
                            result.Add(i);
                    }
                }
                else if (int.TryParse(trimmed, out int num))
                {
                    result.Add(num);
                }
            }
            return result;
        }

        private void MangaList_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            MangaScrollViewer.ScrollToVerticalOffset(MangaScrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        // ==============================
        // ⏹ CANCEL
        // ==============================
        private CancellationToken StartOperation()
        {
            _cts = new CancellationTokenSource();
            BtnCancel.IsEnabled = true;
            ProgressBar.Value = 0;
            ProgressText.Text = "";
            return _cts.Token;
        }

        private void EndOperation()
        {
            _cts = null;
            BtnCancel.IsEnabled = false;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_cts == null) return;

            var result = MessageBox.Show(
                "Are you sure you want to cancel the current operation?\n\nProgress so far will be kept.",
                "Cancel Operation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                _cts.Cancel();
                Log("⏹ Operation cancelled by user.");
                BtnCancel.IsEnabled = false;
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (_isAnimeUnitedMode) LoadRemoteMangas();
            else LoadMangas();
        }

        private static System.Windows.Media.Brush HexBrush(string hex) =>
            (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;

        private void SourceLocal_Click(object sender, RoutedEventArgs e)
        {
            _isAnimeUnitedMode = false;
            BtnSourceLocal.Background = HexBrush("#E85D04");
            BtnSourceAnimeUnited.Background = HexBrush("#2D2D2D");
            DownloadColumn.Width = new GridLength(0);
            BtnDownload.Visibility = Visibility.Collapsed;
            FolderRow.Visibility = Visibility.Visible;
            LocalOnlyActionsPanel.Visibility = Visibility.Visible;
            LoadMangas();
        }

        private void SourceAnimeUnited_Click(object sender, RoutedEventArgs e)
        {
            _isAnimeUnitedMode = true;
            BtnSourceAnimeUnited.Background = HexBrush("#E85D04");
            BtnSourceLocal.Background = HexBrush("#2D2D2D");
            DownloadColumn.Width = new GridLength(1, System.Windows.GridUnitType.Star);
            BtnDownload.Visibility = Visibility.Visible;
            FolderRow.Visibility = Visibility.Collapsed;
            LocalOnlyActionsPanel.Visibility = Visibility.Collapsed;
            LoadRemoteMangas();
        }

        private void SelectMangaByName(string name)
        {
            foreach (var obj in MangaList.Items)
            {
                if (obj is MangaItem mi && mi.Name == name)
                {
                    MangaList.SelectedItem = mi;
                    return;
                }
            }
        }

        // ==============================
        // ☁ ANIMEUNITED
        // ==============================
        private async void LoadRemoteMangas()
        {
            MangaList.Items.Clear();

            string apiUrl = Properties.Settings.Default.AnimeUnitedApiUrl;
            string apiKey = Properties.Settings.Default.AnimeUnitedApiKey;
            if (string.IsNullOrWhiteSpace(apiUrl))
            {
                Log("Configure the AnimeUnited API URL in the settings panel (right side) first.");
                return;
            }

            Log("Loading AnimeUnited catalog...");
            try
            {
                var client = new AnimeUnitedClient(apiUrl, apiKey,
                    Properties.Settings.Default.AnimeUnitedUserEmail);
                var mangas = await client.GetMangasAsync();

                var tasks = new List<Task>();
                foreach (var m in mangas.OrderBy(x => x.Name))
                {
                    bool alreadyLocal = Directory.Exists(Path.Combine(basePath, m.Name));
                    var item = new MangaItem
                    {
                        Name = m.Name,
                        IsRemote = !alreadyLocal,
                        IsOnCloud = true,   // veio de GET /api/mangas, então está no servidor
                        RemoteVolumeCount = m.VolumeCount,
                        RemoteCoverUrl = m.CoverUrl,
                        StatusColor = "#1a1a2a",
                        StatusForeground = "#00A8E1",
                        StatusWeight = "Normal",
                        StatusTip = alreadyLocal ? "Already downloaded" : $"{m.VolumeCount} volume(s) on AnimeUnited",
                        Chk7 = alreadyLocal ? "" : "☁",
                        ChkLanguage = LanguageBadge(m.Language),
                    };
                    MangaList.Items.Add(item);
                    if (!string.IsNullOrEmpty(m.CoverUrl))
                        tasks.Add(LoadRemoteCoverAsync(item, m.CoverUrl!));
                }

                Log($"{mangas.Count} manga(s) found on AnimeUnited. Loading covers...");
                _ = Task.WhenAll(tasks).ContinueWith(_ =>
                    Dispatcher.Invoke(() => Log("Covers loaded.")));
            }
            catch (Exception ex)
            {
                Log($"AnimeUnited error: {ex.Message}");
            }
        }

        private async Task LoadRemoteCoverAsync(MangaItem item, string coverUrl)
        {
            var bytes = await DownloadImageBytes(coverUrl);
            if (bytes != null)
                item.CoverImage = LoadImageFromBytes(bytes);
        }

        private void BtnUsuarios_Click(object sender, RoutedEventArgs e)
        {
            string apiUrl = Properties.Settings.Default.AnimeUnitedApiUrl;
            string email  = Properties.Settings.Default.AnimeUnitedUserEmail;

            if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(email))
            {
                Log("AnimeUnited: configure a URL da API e o seu email antes de gerenciar apoiadores.");
                return;
            }

            var janela = new UsersWindow(apiUrl, Properties.Settings.Default.AnimeUnitedApiKey, email)
            {
                Owner = this
            };
            janela.ShowDialog();
        }

        private async void BtnDeleteCloud_Click(object sender, RoutedEventArgs e)
        {
            if (MangaList.SelectedItem is not MangaItem item || !item.IsOnCloud)
            {
                Log("Select a manga from the AnimeUnited library first.");
                return;
            }

            string apiUrl = Properties.Settings.Default.AnimeUnitedApiUrl;
            if (string.IsNullOrWhiteSpace(apiUrl))
            {
                Log("AnimeUnited: API URL not configured.");
                return;
            }

            var confirm = MessageBox.Show(
                $"Delete \"{item.Name}\" from the AnimeUnited server?\n\n" +
                $"All {item.RemoteVolumeCount} volume(s) will be removed for every user.\n" +
                "The server has no trash — this cannot be undone.\n\n" +
                "Your local copy is not affected.",
                "Delete from Cloud",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes)
            {
                Log("Delete cancelled.");
                return;
            }

            BtnDeleteCloud.IsEnabled = false;
            try
            {
                var client = new AnimeUnitedClient(apiUrl,
                    Properties.Settings.Default.AnimeUnitedApiKey,
                    Properties.Settings.Default.AnimeUnitedUserEmail);

                Log($"▶ Deleting \"{item.Name}\" from AnimeUnited...");
                var (files, bytes) = await client.DeleteMangaAsync(item.Name);
                Log($"✓ Deleted \"{item.Name}\" — {files} file(s), {bytes / 1024.0 / 1024.0:N1} MB freed.");

                LoadRemoteMangas();
            }
            catch (Exception ex)
            {
                Log($"⚠ Could not delete: {ex.Message}");
                BtnDeleteCloud.IsEnabled = true;
            }
        }

        private async void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            if (MangaList.SelectedItem is not MangaItem item || !item.IsRemote) return;

            HashSet<int>? volumeFilter = null;
            if (ChkSendRange.IsChecked == true)
            {
                volumeFilter = ParseVolumeSelection(VolumeRangeBox.Text);
                if (volumeFilter.Count == 0)
                {
                    Log("Please enter a valid volume range (e.g. 1,2,3 or 1-6).");
                    return;
                }
            }

            await DownloadMangaAsync(item, volumeFilter);
        }

        private async Task<bool> DownloadMangaAsync(MangaItem item, HashSet<int>? volumeFilter)
        {
            string apiUrl = Properties.Settings.Default.AnimeUnitedApiUrl;
            string apiKey = Properties.Settings.Default.AnimeUnitedApiKey;
            if (string.IsNullOrWhiteSpace(apiUrl))
            {
                Log("Configure the AnimeUnited API URL in the settings panel (right side) first.");
                return false;
            }

            var client = new AnimeUnitedClient(apiUrl, apiKey,
                Properties.Settings.Default.AnimeUnitedUserEmail);
            var token = StartOperation();
            try
            {
                var volumes = await client.GetVolumesAsync(item.Name);

                if (volumeFilter != null)
                {
                    volumes = volumes.Where(v =>
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(v.Name, @"Volume (\d+)");
                        return match.Success && volumeFilter.Contains(int.Parse(match.Groups[1].Value));
                    }).ToList();
                }

                if (volumes.Count == 0)
                {
                    Log("No matching volumes found to download.");
                    return false;
                }

                string destFolder = Path.Combine(basePath, item.Name);
                int done = 0;
                foreach (var vol in volumes)
                {
                    if (token.IsCancellationRequested) break;

                    Log($"Downloading {item.Name} — {vol.Name}...");
                    var progress = new Progress<double>(p => ProgressBar.Value = p);
                    await client.DownloadVolumeAsync(item.Name, vol.Name, destFolder, progress);
                    done++;
                    ProgressText.Text = $"{done}/{volumes.Count}";
                }

                Log($"Downloaded {done} volume(s) of {item.Name}.");

                SourceLocal_Click(this, new RoutedEventArgs());
                SelectMangaByName(item.Name);
                return done > 0;
            }
            catch (Exception ex)
            {
                Log($"AnimeUnited download error: {ex.Message}");
                return false;
            }
            finally
            {
                EndOperation();
            }
        }

        private async void BtnUpload_Click(object sender, RoutedEventArgs e)
        {
            if (MangaList.SelectedItem is not MangaItem item || item.IsRemote || item.IsMobiOnly) return;

            string mangaPath = Path.Combine(basePath, item.Name);

            // Sobe as páginas da nuvem, se o passo ☁ já rodou; senão as extraídas.
            string sourceRoot = ResolveSourceRoot(mangaPath, CloudFolderName);
            var volumeDirs = GetVolumeDirs(sourceRoot);

            if (volumeDirs.Length == 0)
            {
                Log("No volume folders found to upload.");
                return;
            }

            if (ChkSendRange.IsChecked == true)
            {
                var volumeFilter = ParseVolumeSelection(VolumeRangeBox.Text);
                if (volumeFilter.Count == 0)
                {
                    Log("Please enter a valid volume range (e.g. 1,2,3 or 1-6).");
                    return;
                }

                volumeDirs = volumeDirs.Where(v =>
                {
                    var match = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(v), @"Volume (\d+)");
                    return match.Success && volumeFilter.Contains(int.Parse(match.Groups[1].Value));
                }).ToArray();
            }

            if (volumeDirs.Length == 0)
            {
                Log("No matching volumes found to upload.");
                return;
            }

            string apiUrl = Properties.Settings.Default.AnimeUnitedApiUrl;
            string apiKey = Properties.Settings.Default.AnimeUnitedApiKey;
            if (string.IsNullOrWhiteSpace(apiUrl))
            {
                Log("Configure the AnimeUnited API URL in the settings panel (right side) first.");
                return;
            }

            // Envia um zip por capítulo (Content-Type: application/octet-stream — "application/zip"
            // é o que o WAF do servidor bloqueia; o conteúdo continua sendo um zip normal).
            var chapters = new List<(string VolumeName, string ChapterName, string ChapterDir)>();
            foreach (var volDir in volumeDirs)
            {
                string volumeName = Path.GetFileName(volDir);
                foreach (var chDir in Directory.GetDirectories(volDir, "Capitulo *").OrderBy(c => c))
                    chapters.Add((volumeName, Path.GetFileName(chDir), chDir));
            }

            if (chapters.Count == 0)
            {
                Log("No chapter folders found to upload.");
                return;
            }

            Log(sourceRoot == mangaPath
                ? "Source → extracted volumes (cloud processing not run yet)"
                : $"Source → /{CloudFolderName}");

            var client = new AnimeUnitedClient(apiUrl, apiKey,
                Properties.Settings.Default.AnimeUnitedUserEmail);
            var token = StartOperation();

            // Registro do que já subiu. O servidor lista volumes, não capítulos, então
            // o que diz se um capítulo mudou é a digital guardada aqui; o volume vindo
            // do servidor confirma que aquilo realmente chegou lá.
            var uploadState = LoadUploadState(mangaPath);
            int upToDate = 0;

            if (IsOngoing(mangaPath))
            {
                var remoteVolumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var v in await client.GetVolumesAsync(item.Name)) remoteVolumes.Add(v.Name);
                }
                catch (Exception ex)
                {
                    Log($"🔄 Could not list what is on the server ({ex.Message}) — sending everything.");
                    remoteVolumes = null!;
                }

                if (remoteVolumes != null)
                {
                    var pending = chapters.Where(c =>
                        !(remoteVolumes.Contains(c.VolumeName) &&
                          uploadState.TryGetValue(UploadKey(c.VolumeName, c.ChapterName), out var fp) &&
                          fp == ChapterFingerprint(c.ChapterDir))).ToList();

                    upToDate = chapters.Count - pending.Count;
                    if (upToDate > 0) Log($"🔄 Ongoing: {upToDate} chapter(s) already on the server were skipped.");

                    if (pending.Count == 0)
                    {
                        Log("✓ Nothing to upload — the server already has every chapter.");
                        EndOperation();
                        return;
                    }
                    chapters = pending;
                }
            }

            try
            {
                int done = 0;
                int failed = 0;
                foreach (var (volumeName, chapterName, chapterDir) in chapters)
                {
                    if (token.IsCancellationRequested) break;

                    Log($"Uploading {item.Name} — {volumeName} — {chapterName}...");

                    try
                    {
                        await client.UploadChapterAsync(item.Name, volumeName, chapterName, chapterDir);
                        done++;

                        // Grava a cada capítulo: interromper no meio não faz o próximo
                        // envio recomeçar do zero.
                        uploadState[UploadKey(volumeName, chapterName)] = ChapterFingerprint(chapterDir);
                        SaveUploadState(mangaPath, uploadState);
                    }
                    catch (Exception chapterEx)
                    {
                        failed++;
                        Log($"  FAILED: {volumeName} — {chapterName} — {chapterEx.Message}");
                    }

                    // Progresso geral (capítulos concluídos), não por byte de um capítulo só —
                    // o relatório 0%/100% por request fazia a barra "resetar" a cada capítulo.
                    ProgressBar.Value = (done + failed) * 100.0 / chapters.Count;
                    ProgressText.Text = $"{done + failed}/{chapters.Count}";
                }

                foreach (var volDir in volumeDirs)
                {
                    string volumeName = Path.GetFileName(volDir);
                    string localVolumeComicInfo = Path.Combine(volDir, "ComicInfo.xml");
                    // Gerado depois do processamento? Ainda está só no volume extraído.
                    if (!File.Exists(localVolumeComicInfo))
                        localVolumeComicInfo = Path.Combine(mangaPath, volumeName, "ComicInfo.xml");
                    if (!File.Exists(localVolumeComicInfo)) continue;

                    try
                    {
                        await client.UploadVolumeComicInfoAsync(item.Name, volumeName, localVolumeComicInfo);
                        Log($"Uploaded ComicInfo.xml for {volumeName}.");
                    }
                    catch (Exception ciEx)
                    {
                        Log($"  FAILED to upload ComicInfo.xml for {volumeName} — {ciEx.Message}");
                    }
                }

                string localCover = Path.Combine(mangaPath, "cover.jpg");
                if (File.Exists(localCover))
                {
                    try
                    {
                        await client.UploadCoverAsync(item.Name, localCover);
                        Log("Uploaded cover.jpg to AnimeUnited.");
                    }
                    catch (Exception coverEx)
                    {
                        Log($"  FAILED to upload cover.jpg — {coverEx.Message}");
                    }
                }

                string reusedNote = upToDate > 0 ? $" ({upToDate} already there)" : "";
                Log(failed == 0
                    ? $"Uploaded {done} chapter(s) of {item.Name} to AnimeUnited{reusedNote}."
                    : $"Uploaded {done} chapter(s), {failed} failed, of {item.Name} to AnimeUnited{reusedNote}.");
                LoadMangas();
            }
            catch (Exception ex)
            {
                Log($"AnimeUnited upload error: {ex.Message}");
            }
            finally
            {
                EndOperation();
            }
        }

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