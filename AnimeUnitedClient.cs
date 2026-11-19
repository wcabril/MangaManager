using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MangaManager
{
    public record RemoteManga(string Name, int VolumeCount, string? CoverUrl, string? Language);
    public record RemoteVolume(string Name, long? SizeBytes);
    public record AccessInfo(string? Name, string AccessLevel, bool IsAdmin, bool HasAccess);
    public record RemoteUser(int Id, string Name, string Email, string AccessLevel, string? CreatedAt);

    public class AnimeUnitedClient
    {
        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly string? _userEmail;

        public AnimeUnitedClient(string baseUrl, string apiKey, string? userEmail = null)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _apiKey = apiKey;
            _userEmail = userEmail;
        }

        private HttpClient CreateClient(TimeSpan? timeout = null)
        {
            var client = new HttpClient();
            client.Timeout = timeout ?? TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.Add("User-Agent", "MangaManager/1.0");
            if (!string.IsNullOrEmpty(_apiKey))
                client.DefaultRequestHeaders.Add("X-Api-Key", _apiKey);
            if (!string.IsNullOrEmpty(_userEmail))
                client.DefaultRequestHeaders.Add("X-User-Email", _userEmail);
            return client;
        }

        public async Task<List<RemoteManga>> GetMangasAsync()
        {
            using var client = CreateClient();
            var json = await client.GetStringAsync($"{_baseUrl}/api/mangas");
            using var doc = JsonDocument.Parse(json);

            var result = new List<RemoteManga>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.GetProperty("name").GetString() ?? "";
                var volumeCount = item.TryGetProperty("volumeCount", out var vc) ? vc.GetInt32() : 0;
                var coverUrl = item.TryGetProperty("coverUrl", out var cu) ? cu.GetString() : null;
                var language = item.TryGetProperty("language", out var lg) ? lg.GetString() : null;
                result.Add(new RemoteManga(name, volumeCount, coverUrl, language));
            }
            return result;
        }

        // GetStringAsync descarta o corpo da resposta quando o status é de erro; aqui o
        // texto que o servidor mandou ({"error": "..."}) vira a mensagem da exceção.
        private static async Task<string> GetJsonAsync(HttpClient client, string url)
        {
            using var resp = await client.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode) return body;

            var detail = body;
            try
            {
                using var errDoc = JsonDocument.Parse(body);
                if (errDoc.RootElement.TryGetProperty("error", out var err))
                    detail = err.GetString() ?? body;
            }
            catch (JsonException) { }

            if (detail.Length > 300) detail = detail.Substring(0, 300) + "…";
            throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {detail}");
        }

        public async Task<bool> CheckIsAdminAsync()
        {
            using var client = CreateClient();
            var json = await GetJsonAsync(client, $"{_baseUrl}/api/access");
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("isAdmin", out var isAdmin) && isAdmin.GetBoolean();
        }

        public async Task<AccessInfo> CheckAccessAsync()
        {
            using var client = CreateClient();
            var json = await GetJsonAsync(client, $"{_baseUrl}/api/access");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var name = root.TryGetProperty("name", out var nm) ? nm.GetString() : null;
            var accessLevel = root.TryGetProperty("accessLevel", out var al) ? al.GetString() ?? "none" : "none";
            var isAdmin = root.TryGetProperty("isAdmin", out var ia) && ia.GetBoolean();
            var hasAccess = root.TryGetProperty("hasAccess", out var ha) && ha.GetBoolean();
            return new AccessInfo(name, accessLevel, isAdmin, hasAccess);
        }

        // Apaga o mangá inteiro do servidor. Exige admin — o servidor confere o
        // access_level do X-User-Email, a chave da API sozinha não autoriza.
        //
        // Vai como POST em .../delete, e não como DELETE: o servidor do AnimeUnited
        // bloqueia o método DELETE para o site inteiro (403 em HTML, antes do PHP).
        // Mesmo motivo pelo qual o upload não declara "application/zip".
        public async Task<(int Files, long Bytes)> DeleteMangaAsync(string mangaName)
        {
            using var client = CreateClient();
            var url = $"{_baseUrl}/api/mangas/{Uri.EscapeDataString(mangaName)}/delete";

            var payload = new ByteArrayContent(System.Text.Encoding.ASCII.GetBytes("delete"));
            payload.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

            using var resp = await client.PostAsync(url, payload);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                var detail = body;
                try
                {
                    using var errDoc = JsonDocument.Parse(body);
                    if (errDoc.RootElement.TryGetProperty("error", out var err))
                        detail = err.GetString() ?? body;
                }
                catch (JsonException) { }
                throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {detail}");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            int files = root.TryGetProperty("filesDeleted", out var fd) ? fd.GetInt32() : 0;
            long bytes = root.TryGetProperty("bytesFreed", out var bf) ? bf.GetInt64() : 0;
            return (files, bytes);
        }

        // ── Gerenciamento de apoiadores ──────────────────────────────────────
        // Todas exigem admin no servidor: chave da API mais access_level do
        // X-User-Email. Vão como POST porque o host bloqueia PUT e DELETE.

        public async Task<List<RemoteUser>> GetUsersAsync()
        {
            using var client = CreateClient();
            var json = await GetJsonAsync(client, $"{_baseUrl}/api/users");
            using var doc = JsonDocument.Parse(json);

            var result = new List<RemoteUser>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                result.Add(new RemoteUser(
                    item.GetProperty("id").GetInt32(),
                    item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    item.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "",
                    item.TryGetProperty("accessLevel", out var a) ? a.GetString() ?? "none" : "none",
                    item.TryGetProperty("createdAt", out var c) ? c.GetString() : null));
            }
            return result;
        }

        private async Task PostJsonAsync(string url, object? corpo)
        {
            using var client = CreateClient();

            HttpContent conteudo;
            if (corpo == null)
            {
                // Corpo vazio faz o WAF do servidor recusar; um byte resolve.
                conteudo = new ByteArrayContent(new byte[] { 0x20 });
                conteudo.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            }
            else
            {
                conteudo = new StringContent(JsonSerializer.Serialize(corpo),
                    System.Text.Encoding.UTF8, "application/json");
            }

            using var resp = await client.PostAsync(url, conteudo);
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode) return;

            var detail = body;
            try
            {
                using var errDoc = JsonDocument.Parse(body);
                if (errDoc.RootElement.TryGetProperty("error", out var err))
                    detail = err.GetString() ?? body;
            }
            catch (JsonException) { }
            throw new HttpRequestException(detail);
        }

        public Task CreateUserAsync(string name, string email, string accessLevel) =>
            PostJsonAsync($"{_baseUrl}/api/users",
                          new { name, email, accessLevel });

        public Task UpdateUserAsync(int id, string name, string email, string accessLevel) =>
            PostJsonAsync($"{_baseUrl}/api/users/{id}",
                          new { name, email, accessLevel });

        public Task DeleteUserAsync(int id) =>
            PostJsonAsync($"{_baseUrl}/api/users/{id}/delete", null);

        public async Task<List<RemoteVolume>> GetVolumesAsync(string mangaName)
        {
            using var client = CreateClient();
            var url = $"{_baseUrl}/api/mangas/{Uri.EscapeDataString(mangaName)}/volumes";
            var json = await client.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            var result = new List<RemoteVolume>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.GetProperty("name").GetString() ?? "";
                long? sizeBytes = item.TryGetProperty("sizeBytes", out var sb) && sb.ValueKind == JsonValueKind.Number
                    ? sb.GetInt64() : null;
                result.Add(new RemoteVolume(name, sizeBytes));
            }
            return result;
        }

        public async Task DownloadVolumeAsync(string mangaName, string volumeName, string destMangaFolder,
                                               IProgress<double>? progress = null)
        {
            using var client = CreateClient(TimeSpan.FromMinutes(15)); // volume inteiro pode ser grande
            var url = $"{_baseUrl}/api/mangas/{Uri.EscapeDataString(mangaName)}/volumes/{Uri.EscapeDataString(volumeName)}/download";

            string tempZip = Path.Combine(Path.GetTempPath(), $"AnimeUnited_{Guid.NewGuid():N}.zip");
            try
            {
                using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    long? totalBytes = response.Content.Headers.ContentLength;

                    using var httpStream = await response.Content.ReadAsStreamAsync();
                    using var fileStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write);

                    var buffer = new byte[81920];
                    long totalRead = 0;
                    int read;
                    while ((read = await httpStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, read);
                        totalRead += read;
                        if (totalBytes.HasValue && totalBytes.Value > 0)
                            progress?.Report((double)totalRead / totalBytes.Value * 100.0);
                    }
                }

                Directory.CreateDirectory(destMangaFolder);
                string destVolumeFolder = Path.Combine(destMangaFolder, volumeName);
                if (Directory.Exists(destVolumeFolder)) Directory.Delete(destVolumeFolder, true);

                ZipFile.ExtractToDirectory(tempZip, destVolumeFolder);
            }
            finally
            {
                if (File.Exists(tempZip)) File.Delete(tempZip);
            }
        }

        public async Task UploadVolumeAsync(string mangaName, string volumeName, string localVolumeFolder,
                                             IProgress<double>? progress = null)
        {
            string tempZip = Path.Combine(Path.GetTempPath(), $"AnimeUnited_{Guid.NewGuid():N}.zip");
            try
            {
                ZipFile.CreateFromDirectory(localVolumeFolder, tempZip, CompressionLevel.Optimal, false);

                using var client = CreateClient();
                var url = $"{_baseUrl}/api/mangas/{Uri.EscapeDataString(mangaName)}/volumes/{Uri.EscapeDataString(volumeName)}/upload";

                using var fileStream = new FileStream(tempZip, FileMode.Open, FileAccess.Read);
                using var content = new StreamContent(fileStream);
                // "application/zip" literalmente aciona o bloqueio do WAF do servidor — o conteúdo
                // continua sendo um zip, só não declaramos como tal no header.
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                progress?.Report(0);
                using var response = await client.PostAsync(url, content);
                response.EnsureSuccessStatusCode();
                progress?.Report(100);
            }
            finally
            {
                if (File.Exists(tempZip)) File.Delete(tempZip);
            }
        }

        public async Task UploadChapterAsync(string mangaName, string volumeName, string chapterName, string localChapterFolder,
                                              IProgress<double>? progress = null)
        {
            string tempZip = Path.Combine(Path.GetTempPath(), $"AnimeUnited_{Guid.NewGuid():N}.zip");
            try
            {
                ZipFile.CreateFromDirectory(localChapterFolder, tempZip, CompressionLevel.Optimal, false);

                using var client = CreateClient();
                var url = $"{_baseUrl}/api/mangas/{Uri.EscapeDataString(mangaName)}/volumes/{Uri.EscapeDataString(volumeName)}" +
                          $"/chapters/{Uri.EscapeDataString(chapterName)}/upload";

                using var fileStream = new FileStream(tempZip, FileMode.Open, FileAccess.Read);
                using var content = new StreamContent(fileStream);
                // "application/zip" literalmente aciona o bloqueio do WAF do servidor — o conteúdo
                // continua sendo um zip, só não declaramos como tal no header.
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                progress?.Report(0);
                using var response = await client.PostAsync(url, content);
                response.EnsureSuccessStatusCode();
                progress?.Report(100);
            }
            finally
            {
                if (File.Exists(tempZip)) File.Delete(tempZip);
            }
        }

        public async Task UploadCoverAsync(string mangaName, string localCoverPath)
        {
            using var client = CreateClient();
            var url = $"{_baseUrl}/api/mangas/{Uri.EscapeDataString(mangaName)}/cover";

            using var fileStream = new FileStream(localCoverPath, FileMode.Open, FileAccess.Read);
            using var content = new StreamContent(fileStream);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");

            using var response = await client.PostAsync(url, content);
            response.EnsureSuccessStatusCode();
        }

        // ComicInfo.xml da raiz do volume (usado pelo KCC pros metadados do MOBI) — diferente do
        // ComicInfo.xml de cada capítulo, que já vai dentro do zip de UploadChapterAsync.
        public async Task UploadVolumeComicInfoAsync(string mangaName, string volumeName, string localComicInfoPath)
        {
            using var client = CreateClient();
            var url = $"{_baseUrl}/api/mangas/{Uri.EscapeDataString(mangaName)}/volumes/{Uri.EscapeDataString(volumeName)}/comicinfo";

            using var fileStream = new FileStream(localComicInfoPath, FileMode.Open, FileAccess.Read);
            using var content = new StreamContent(fileStream);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml");

            using var response = await client.PostAsync(url, content);
            response.EnsureSuccessStatusCode();
        }

        // Envia um arquivo (imagem de página ou ComicInfo.xml) sem zipar — usado quando a camada
        // de proxy/WAF na frente do servidor bloqueia/limita uploads de arquivos compactados.
        public async Task UploadFileAsync(string mangaName, string volumeName, string chapterName, string localFilePath,
                                           IProgress<double>? progress = null)
        {
            string fileName = Path.GetFileName(localFilePath);

            // Timeout curto de propósito: são arquivos pequenos, então se uma requisição travar
            // (rede, servidor sem responder) é melhor falhar rápido do que ficar preso minutos.
            using var client = CreateClient(TimeSpan.FromSeconds(60));
            var url = $"{_baseUrl}/api/mangas/{Uri.EscapeDataString(mangaName)}/volumes/{Uri.EscapeDataString(volumeName)}" +
                      $"/chapters/{Uri.EscapeDataString(chapterName)}/files/{Uri.EscapeDataString(fileName)}";

            using var fileStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read);
            using var content = new StreamContent(fileStream);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(GetMimeType(fileName));

            progress?.Report(0);
            using var response = await client.PostAsync(url, content);
            response.EnsureSuccessStatusCode();
            progress?.Report(100);
        }

        private static string GetMimeType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png"            => "image/png",
            ".webp"           => "image/webp",
            ".xml"            => "application/xml",
            _                 => "application/octet-stream",
        };
    }
}
