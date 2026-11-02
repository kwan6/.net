using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using OpenCvSharp;

public class NotifyOptions
{
    public string CameraName { get; set; } = "Kamera 1";
    public string TargetName { get; set; } = "Sahabat Safety";

    // Saklar utama pengiriman notifikasi (berlaku untuk WhatsApp maupun Telegram)
    public bool WhatsAppEnabled { get; set; } = false;

    // "WhatsApp" atau "Telegram"
    public string Provider { get; set; } = "WhatsApp";

    // --- WhatsApp ---
    public string WhatsAppBaseUrl { get; set; } = "";
    public string WhatsAppToken { get; set; } = "";
    public string[] WhatsAppTargets { get; set; } = Array.Empty<string>();

    // --- Telegram ---
    public string TelegramBotToken { get; set; } = "";
    public string[] TelegramChatIds { get; set; } = Array.Empty<string>();

    public string QueueDir { get; set; } = "alerts/queue";
    public bool SkipCertificateValidation { get; set; } = false;
    public bool AlarmEnabled { get; set; } = false;
    public string AlarmHost { get; set; } = "";
    public int AlarmPort { get; set; } = 9999;
    public double AlarmAfterSec { get; set; } = 1.0;
}

public sealed class AlertNotifier : BackgroundService
{
    private readonly NotifyOptions _opt;
    private readonly ILogger<AlertNotifier> _log;
    private readonly HttpClient _http;

    private bool IsTelegram => _opt.Provider.Equals("Telegram", StringComparison.OrdinalIgnoreCase);

    public AlertNotifier(IOptions<NotifyOptions> opt, ILogger<AlertNotifier> log)
    {
        _opt = opt.Value;
        _log = log;
        var handler = new HttpClientHandler();
        if (_opt.SkipCertificateValidation)
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        if (_opt.WhatsAppEnabled) Directory.CreateDirectory(_opt.QueueDir);
    }

    // Dipanggil saat ada pelanggaran: simpan foto + keterangan ke antrean
    public void EnqueueAlert(Mat frame, DateTime waktu)
    {
        if (!_opt.WhatsAppEnabled) return;
        try
        {
            string baseName = Path.Combine(_opt.QueueDir, $"alert_{waktu:yyyyMMdd_HHmmss}");
            File.WriteAllLines(baseName + ".txt",
                new[] { _opt.TargetName, _opt.CameraName, waktu.ToString("yyyy-MM-dd HH:mm:ss") });
            Cv2.ImWrite(baseName + ".jpg", frame);   // .jpg terakhir, supaya pekerja tidak membaca setengah jadi
        }
        catch (Exception e) { _log.LogWarning("Gagal menyimpan antrean alert: {Msg}", e.Message); }
    }

    // Bunyikan alarm di Raspberry Pi (tidak menunggu hasilnya)
    public void TriggerAlarm()
    {
        if (!_opt.AlarmEnabled || string.IsNullOrWhiteSpace(_opt.AlarmHost)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                using var client = new TcpClient();
                await client.ConnectAsync(_opt.AlarmHost, _opt.AlarmPort, cts.Token);
                await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes("PLAY_ALARM"), cts.Token);
                _log.LogInformation("Alarm dikirim ke perangkat");
            }
            catch (Exception e) { _log.LogWarning("Gagal membunyikan alarm: {Msg}", e.Message); }
        });
    }

    // Dipakai endpoint /api/test-wa: kirim pesan teks percobaan
    public async Task<(bool Ok, string Info)> SendTestAsync(CancellationToken ct)
    {
        if (!IsTelegram)
            return (false, $"Provider saat ini '{_opt.Provider}'. Tes ini baru mendukung Telegram.");
        if (string.IsNullOrWhiteSpace(_opt.TelegramBotToken) || _opt.TelegramChatIds.Length == 0)
            return (false, "TelegramBotToken atau TelegramChatIds belum diisi di appsettings.json (section Notify).");

        var hasil = new List<string>();
        bool semuaOk = true;
        foreach (var chat in _opt.TelegramChatIds)
        {
            try
            {
                using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["chat_id"] = chat.Trim(),
                    ["text"] = $"Tes notifikasi dari {_opt.CameraName} ({DateTime.Now:yyyy-MM-dd HH:mm:ss})"
                });
                using var res = await _http.PostAsync(TelegramUrl("sendMessage"), form, ct);
                string body = await res.Content.ReadAsStringAsync(ct);
                if (!res.IsSuccessStatusCode) semuaOk = false;
                hasil.Add($"chat {chat}: HTTP {(int)res.StatusCode} {body}");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                semuaOk = false;
                hasil.Add($"chat {chat}: gagal ({e.Message})");
            }
        }
        return (semuaOk, string.Join(" | ", hasil));
    }

    // Pekerja antrean: kirim foto, ulangi kalau gagal
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.WhatsAppEnabled) return;

        if (IsTelegram)
        {
            if (string.IsNullOrWhiteSpace(_opt.TelegramBotToken) || _opt.TelegramChatIds.Length == 0)
            {
                _log.LogError("Telegram aktif tetapi BotToken/ChatIds belum lengkap. Pengiriman dimatikan.");
                return;
            }
        }
        else if (string.IsNullOrWhiteSpace(_opt.WhatsAppBaseUrl) || string.IsNullOrWhiteSpace(_opt.WhatsAppToken)
                 || _opt.WhatsAppTargets.Length == 0)
        {
            _log.LogError("WhatsApp aktif tetapi BaseUrl/Token/Targets belum lengkap. Pengiriman dimatikan.");
            return;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool gagal = false;
                foreach (var jpg in Directory.GetFiles(_opt.QueueDir, "*.jpg").OrderBy(f => f))
                {
                    string txt = Path.ChangeExtension(jpg, ".txt");
                    string nama = _opt.TargetName, lokasi = _opt.CameraName, waktu = "Unknown";
                    if (File.Exists(txt))
                    {
                        var l = File.ReadAllLines(txt);
                        if (l.Length >= 3) { nama = l[0]; lokasi = l[1]; waktu = l[2]; }
                    }

                    bool ok = IsTelegram
                        ? await SendPhotoTelegramAsync(jpg, nama, lokasi, waktu, ct)
                        : await SendPhotoAsync(jpg, nama, lokasi, waktu, ct);

                    if (ok)
                    {
                        TryDelete(jpg); TryDelete(txt);
                        await Task.Delay(1000, ct);
                    }
                    else { gagal = true; break; }
                }
                await Task.Delay(gagal ? 5000 : 3000, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private string TelegramUrl(string method) =>
        $"https://api.telegram.org/bot{_opt.TelegramBotToken}/{method}";

    private async Task<bool> SendPhotoTelegramAsync(string jpgPath, string nama, string lokasi, string waktu, CancellationToken ct)
    {
        bool adaBerhasil = false;
        string caption = $"Pelanggaran terdeteksi\nTarget: {nama}\nLokasi: {lokasi}\nWaktu: {waktu}";
        foreach (var chat in _opt.TelegramChatIds)
        {
            try
            {
                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(chat.Trim()), "chat_id");
                form.Add(new StringContent(caption), "caption");
                var file = new ByteArrayContent(await File.ReadAllBytesAsync(jpgPath, ct));
                file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                form.Add(file, "photo", Path.GetFileName(jpgPath));

                using var res = await _http.PostAsync(TelegramUrl("sendPhoto"), form, ct);
                if (res.IsSuccessStatusCode)
                {
                    adaBerhasil = true;
                    _log.LogInformation("Foto terkirim ke Telegram: {File}", Path.GetFileName(jpgPath));
                }
                else
                {
                    string body = await res.Content.ReadAsStringAsync(ct);
                    _log.LogWarning("Telegram menolak (status {Code}): {Body}", (int)res.StatusCode, body);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning("Gagal mengirim Telegram: {Msg}", e.Message);   // sengaja tidak mencatat URL (ada token)
            }
        }
        return adaBerhasil;
    }

    private async Task<bool> SendPhotoAsync(string jpgPath, string nama, string lokasi, string waktu, CancellationToken ct)
    {
        bool adaBerhasil = false;
        string url = $"{_opt.WhatsAppBaseUrl.TrimEnd('/')}/api/WA/SendMessageImage/{_opt.WhatsAppToken}";
        foreach (var nomor in _opt.WhatsAppTargets)
        {
            try
            {
                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(nomor.Trim()), "to");
                form.Add(new StringContent(nama), "full_name");
                form.Add(new StringContent(lokasi), "location");
                form.Add(new StringContent(waktu), "incident_time");
                var file = new ByteArrayContent(await File.ReadAllBytesAsync(jpgPath, ct));
                file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                form.Add(file, "file", Path.GetFileName(jpgPath));

                using var res = await _http.PostAsync(url, form, ct);
                if (res.IsSuccessStatusCode)
                {
                    adaBerhasil = true;
                    _log.LogInformation("Foto terkirim ke WhatsApp: {File}", Path.GetFileName(jpgPath));
                }
                else _log.LogWarning("WhatsApp menolak (status {Code})", (int)res.StatusCode);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning("Gagal mengirim WhatsApp: {Msg}", e.Message);   // sengaja tidak mencatat URL (ada token)
            }
        }
        return adaBerhasil;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}