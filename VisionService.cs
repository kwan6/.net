using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using OpenCvSharp;

public class VisionOptions
{
    public string Url { get; set; } = "";
    public string ModelPath { get; set; } = "yolov8s.onnx";
    public bool UseGpu { get; set; } = false;      // false = CPU saja (aman untuk PC tanpa GPU)
    public int GpuId { get; set; } = 0;
    public double FileFps { get; set; } = 0;       // 0 = otomatis; isi (misal 30) kalau video file diputar terlalu cepat/lambat
    public int MaxPhoneChecks { get; set; } = 10;  // jumlah maksimum orang yang dicek HP per siklus (turunkan kalau CPU lambat)
    public double ValidationSec { get; set; } = 3.0;
    public int AlertCooldownSec { get; set; } = 20;
    public string AlertDir { get; set; } = "alerts";


}

public sealed class VisionService : BackgroundService
{
    private const double Interval = 1000.0 / 25;   // putar di 25 FPS
    private readonly VisionOptions _opt;
    private readonly NotifyOptions _notify;
    private readonly AlertNotifier _notifier;
    private readonly ILogger<VisionService> _log;
    private readonly object _lock = new();
    private readonly HashSet<int> _alarmed = new();
    private byte[]? _jpeg;
    private object _state = new { waktu = "", fps = 0.0, orang = Array.Empty<object>(), total_alert = 0 };
    private int _totalAlert;

    [DllImport("winmm.dll")] private static extern int timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern int timeEndPeriod(uint ms);
    
    public VisionService(IOptions<VisionOptions> opt, IOptions<NotifyOptions> notify,
                         AlertNotifier notifier, ILogger<VisionService> log)
    {
        _opt = opt.Value;
        _notify = notify.Value;
        _notifier = notifier;
        _log = log;
    }

    public string AlertDir => _opt.AlertDir;
    public byte[]? GetJpeg() { lock (_lock) return _jpeg; }
    public object GetState() { lock (_lock) return _state; }

    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.Factory.StartNew(() => Run(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken ct)
    {
        try
        {
            timeBeginPeriod(1); 

            if (string.IsNullOrWhiteSpace(_opt.Url))
            {
                _log.LogError("Vision:Url (alamat kamera) belum diisi di appsettings.json");
                return;
            }
            Directory.CreateDirectory(_opt.AlertDir);

            var clock = Stopwatch.StartNew();
            using var detector = new YoloDetector(_opt.ModelPath, useGpu: _opt.UseGpu, gpuId: _opt.GpuId);
            _log.LogInformation("YOLO berjalan di: {Provider}", detector.Provider);
            using var source = new FrameSource(_opt.Url);
            using var worker = new DetectionWorker(detector, clock, _opt.MaxPhoneChecks);

            _log.LogInformation("Mengisi buffer...");
            var tunggu = Stopwatch.StartNew();
            while (source.Buffered < 40 && tunggu.Elapsed.TotalSeconds < 30 && !ct.IsCancellationRequested)
                Thread.Sleep(100);
            _log.LogInformation("Buffer terisi ({N} frame), mulai memutar", source.Buffered);

            double nextT = clock.ElapsedMilliseconds;
            // File video (mis. rekaman webcam .mkv) sering melaporkan FPS yang salah (misal 1000), sehingga
            // diputar jauh lebih cepat dari aslinya. FPS di luar rentang wajar diganti 30, atau isi Vision:FileFps.
            double playFps = _opt.FileFps > 0 ? _opt.FileFps
                           : (source.Fps >= 5 && source.Fps <= 60 ? source.Fps : 30.0);
            double interval = source.IsFile ? 1000.0 / playFps : Interval;
            if (source.IsFile)
                _log.LogInformation("Sumber file: FPS terbaca {Baca:F1}, diputar di {Putar:F1} FPS", source.Fps, playFps);
            long last = clock.ElapsedMilliseconds;
            long lastAlertMs = -_opt.AlertCooldownSec * 1000L;

            while (!ct.IsCancellationRequested)
            {
                var frame = source.Next();
                if (frame == null)
                {
                    Thread.Sleep(100);
                    nextT = clock.ElapsedMilliseconds;
                    continue;
                }

                using (frame)
                {
                    long nowMs = clock.ElapsedMilliseconds;
                    if (worker.WantsFrame) worker.Submit(frame);
                    var tracks = worker.GetResult();

                    var orangState = new List<object>();
                    bool pelanggaran = false;
                    foreach (var t in tracks)
                    {
                        double el = (t.HasPhone && t.SuspectStartMs != null) ? (nowMs - t.SuspectStartMs.Value) / 1000.0 : 0;
                        if (el >= _opt.ValidationSec) pelanggaran = true;

                        // alarm: sekali per orang saat timer mencapai AlarmAfterSec
                        if (el > 0 && el >= _notify.AlarmAfterSec && _alarmed.Add(t.Id)) _notifier.TriggerAlarm();
                        if (el == 0) _alarmed.Remove(t.Id);

                        Scalar warna;
                        string teks;
                        if (t.HasPhone && t.Walking) { warna = new Scalar(0, 0, 255); teks = "HP + JALAN"; }
                        else if (t.HasPhone) { warna = new Scalar(0, 165, 255); teks = "HP (DIAM)"; }
                        else { warna = new Scalar(0, 255, 0); teks = t.Walking ? "JALAN" : "DIAM"; }
                        if (el > 0) teks += $" {el:F1}s";

                        Cv2.Rectangle(frame, t.Box, warna, el >= _opt.ValidationSec ? 4 : 2);
                        Cv2.PutText(frame, $"ID{t.Id} {teks}", new Point(t.Box.X, t.Box.Y - 5),
                            HersheyFonts.HersheySimplex, 0.45, warna, 1);

                        orangState.Add(new
                        {
                            id = t.Id,
                            box = new[] { t.Box.X, t.Box.Y, t.Box.Right, t.Box.Bottom },
                            walking = t.Walking,
                            hp = t.HasPhone,
                            elapsed = Math.Round(el, 1)
                        });
                    }
                    _alarmed.RemoveWhere(id => !tracks.Any(x => x.Id == id));

                    if (pelanggaran && nowMs - lastAlertMs > _opt.AlertCooldownSec * 1000L)
                    {
                        lastAlertMs = nowMs;
                        _totalAlert++;
                        var waktu = DateTime.Now;
                        string nama = $"alert_{waktu:yyyyMMdd_HHmmss}.jpg";
                        Cv2.ImWrite(Path.Combine(_opt.AlertDir, nama), frame);   // arsip bukti
                        _notifier.EnqueueAlert(frame, waktu);                     // antrean WhatsApp
                        _log.LogWarning("ALERT! foto disimpan: {Nama}", nama);
                    }

                    double fps = 1000.0 / Math.Max(1, nowMs - last);
                    last = nowMs;
                    Cv2.PutText(frame, $"FPS: {fps:F0}  Deteksi: {worker.LastMs:F0} ms",
                        new Point(10, 30), HersheyFonts.HersheySimplex, 0.8, new Scalar(0, 255, 255), 2);

                    Cv2.ImEncode(".jpg", frame, out var buf);
                    lock (_lock)
                    {
                        _jpeg = buf;
                        _state = new
                        {
                            waktu = DateTime.Now.ToString("s"),
                            fps = Math.Round(fps, 1),
                            orang = orangState,
                            total_alert = _totalAlert
                        };
                    }
                }

                nextT += interval;
                double sisa = nextT - clock.ElapsedMilliseconds;
                if (sisa < 0) { nextT = clock.ElapsedMilliseconds; sisa = 0; }
                if (sisa >= 1) Thread.Sleep((int)sisa);
            }
        }
        catch (Exception e)
        {
            _log.LogError(e, "Proses deteksi berhenti karena error");
        }

        finally
        {
            timeEndPeriod(1);
        }
    }
}