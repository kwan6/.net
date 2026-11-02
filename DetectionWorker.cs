using OpenCvSharp;
using System.Diagnostics;

// potret hasil deteksi yang aman dibaca thread lain
public record TrackView(int Id, Rect Box, bool Walking, bool HasPhone, long? SuspectStartMs);

public sealed class DetectionWorker : IDisposable
{
    private const int PersonClass = 0;
    private readonly YoloDetector _detector;
    private readonly SimpleTracker _tracker = new(walkRatio: 0.3, minAspect: 1.9);
    private readonly PhoneChecker _phoneChecker;
    private readonly Stopwatch _clock;
    private readonly object _lock = new();
    private readonly Thread _thread;
    private Mat? _pending;
    private List<TrackView> _result = new();
    private volatile bool _busy;
    private volatile bool _running = true;

    public double LastMs { get; private set; }

    public DetectionWorker(YoloDetector detector, Stopwatch clock, int maxPhoneChecks = 10)
    {
        _detector = detector;
        _clock = clock;
        _phoneChecker = new PhoneChecker(detector, maxCheck: maxPhoneChecks);
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.Start();
    }

    public bool WantsFrame
    {
        get { lock (_lock) return _pending == null && !_busy; }
    }

    public void Submit(Mat frame)
    {
        var copy = frame.Clone();
        lock (_lock)
        {
            _pending?.Dispose();
            _pending = copy;
        }
    }

    public List<TrackView> GetResult()
    {
        lock (_lock) return _result;
    }

    private void Loop()
    {
        while (_running)
        {
            Mat? f;
            lock (_lock)
            {
                f = _pending;
                _pending = null;
                if (f != null) _busy = true;
            }
            if (f == null) { Thread.Sleep(2); continue; }

            try
            {
                using (f)
                {
                    var sw = Stopwatch.StartNew();
                    long now = _clock.ElapsedMilliseconds;

                    var dets = _detector.Detect(f, PersonClass);
                    var tracks = _tracker.Update(dets, now);
                    _phoneChecker.Check(f, tracks, now);
                    foreach (var t in tracks) t.UpdateViolation(now);

                    var view = tracks
                        .Select(t => new TrackView(t.Id, t.Box, t.Walking, t.HasPhone, t.SuspectStartMs))
                        .ToList();
                    lock (_lock) _result = view;
                    LastMs = sw.Elapsed.TotalMilliseconds;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error deteksi: " + e.Message);
            }
            finally
            {
                _busy = false;
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(3000);
        lock (_lock) { _pending?.Dispose(); _pending = null; }
    }
}