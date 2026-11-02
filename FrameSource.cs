using System.Collections.Concurrent;
using OpenCvSharp;

public sealed class FrameSource : IDisposable
{
    private readonly string _url;
    private readonly int _w, _h, _maxFrames;   // _w x _h = batas maksimum, bukan ukuran paksa
    private readonly ConcurrentQueue<Mat> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly bool _isFile;   // true kalau sumbernya file lokal (bukan http/rtsp)

    public FrameSource(string url, int width = 1280, int height = 720, int maxFrames = 150)
    {
        _url = url; _w = width; _h = height; _maxFrames = maxFrames;
        _isFile = !url.Contains("://");

        // RTSP secara default sering pakai UDP -> frame pecah/putus. Paksa TCP.
        // Harus diset sebelum VideoCapture pertama dibuat. Tidak menimpa kalau sudah diset manual.
        if (url.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENCV_FFMPEG_CAPTURE_OPTIONS")))
        {
            Environment.SetEnvironmentVariable("OPENCV_FFMPEG_CAPTURE_OPTIONS", "rtsp_transport;tcp");
        }

        _thread = new Thread(Loop) { IsBackground = true };
        _thread.Start();
    }

    public int Buffered => _queue.Count;
    public bool IsFile => _isFile;
    public double Fps { get; private set; }   // FPS asli sumber (hanya dipakai untuk file lokal)

    public Mat? Next() => _queue.TryDequeue(out var m) ? m : null;

    private void Loop()
    {
        var cap = new VideoCapture(_url);
        Fps = cap.Fps;
        using var raw = new Mat();
        int fail = 0;
        while (!_cts.IsCancellationRequested)
        {
            if (!cap.IsOpened() || !cap.Read(raw) || raw.Empty())
            {
                fail++;
                Thread.Sleep(100);
                if (fail >= 30)
                {
                    Console.WriteLine("Stream putus, menyambung ulang...");
                    cap.Dispose();
                    cap = new VideoCapture(_url);
                    Fps = cap.Fps;
                    fail = 0;
                }
                continue;
            }
            fail = 0;

            // Perkecil sampai muat di batas _w x _h dengan RASIO TETAP (tidak melar), dan tidak memperbesar
            double scale = Math.Min(1.0, Math.Min((double)_w / raw.Width, (double)_h / raw.Height));
            Mat f;
            if (scale < 1.0)
            {
                f = new Mat();
                Cv2.Resize(raw, f, new Size((int)Math.Round(raw.Width * scale), (int)Math.Round(raw.Height * scale)));
            }
            else
            {
                f = raw.Clone();
            }
            _queue.Enqueue(f);
            if (_isFile)
            {
                // file lokal dibaca jauh lebih cepat dari kecepatan putar: tunggu, jangan buang frame
                while (_queue.Count >= _maxFrames && !_cts.IsCancellationRequested) Thread.Sleep(20);
            }
            else
            {
                // stream live: buang frame terlama supaya selalu dekat dengan kondisi terkini
                while (_queue.Count > _maxFrames && _queue.TryDequeue(out var old)) old.Dispose();
            }
        }
        cap.Dispose();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(2000);
        while (_queue.TryDequeue(out var m)) m.Dispose();
    }
}