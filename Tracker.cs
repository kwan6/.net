using OpenCvSharp;

public class Track
{
    public int Id { get; init; }
    public Rect Box { get; set; }
    public int Missed { get; set; }
    public bool Walking { get; private set; }

    // --- deteksi HP dan timer ---
    public bool HasPhone { get; set; }
    public long? SuspectStartMs { get; private set; }
    public long LastPhoneMs { get; private set; }
    public long LastWalkMs { get; private set; } = -1_000_000;   // kapan terakhir terdeteksi berjalan

    // riwayat: titik kaki (tengah-bawah kotak), waktu, tinggi kotak
    private readonly List<(Point2d Pos, long T, int H)> _history = new();

    public void Push(Rect box, long nowMs, double memoryMs, double walkThreshold,
                     double walkRatio = 0.2, double minAspect = 1.7,
                     double minSpanMs = 700, double maxHeightChange = 0.25)
    {
        Box = box;
        Missed = 0;

        // pakai titik kaki, bukan titik tengah: lebih stabil saat orang duduk, berdiri, atau tangan bergerak
        var pos = new Point2d(box.X + box.Width / 2.0, box.Bottom);
        _history.RemoveAll(h => nowMs - h.T > memoryMs);
        _history.Add((pos, nowMs, box.Height));

        Walking = false;
        var oldest = _history[0];
        double span = nowMs - oldest.T;
        if (span < minSpanMs) return;   // riwayat belum cukup panjang untuk menilai

        double dx = pos.X - oldest.Pos.X, dy = pos.Y - oldest.Pos.Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        // ambang geser mengikuti ukuran orang (orang jauh = kotak kecil = ambang kecil)
        double thr = Math.Max(walkThreshold, walkRatio * box.Height);

        // orang berdiri/jalan kotaknya tinggi; orang duduk kotaknya lebih gemuk
        double aspect = box.Height / (double)Math.Max(1, box.Width);

        // duduk/berdiri/membungkuk mengubah tinggi kotak; jalan biasa hampir tidak
        double hChange = Math.Abs(box.Height - oldest.H) / (double)Math.Max(1, oldest.H);

        bool berjalan = dist > thr && aspect >= minAspect && hChange <= maxHeightChange;
        Walking = berjalan;
        if (berjalan) LastWalkMs = nowMs;
    }

    public void UpdateViolation(long nowMs, long resetMs = 1000, long walkGraceMs = 500)
    {
        // dianggap masih berjalan kalau barusan jalan (toleransi singkat supaya tidak kedip)
        bool berjalanBaru = Walking || nowMs - LastWalkMs <= walkGraceMs;

        // timer mulai dan terus jalan hanya selama HP + berjalan
        if (HasPhone && berjalanBaru)
        {
            SuspectStartMs ??= nowMs;
            LastPhoneMs = nowMs;
        }

        // reset kalau HP hilang lebih dari resetMs, atau orangnya sudah berhenti jalan
        if (SuspectStartMs != null && (nowMs - LastPhoneMs > resetMs || !berjalanBaru))
            SuspectStartMs = null;
    }

    public double ElapsedSec(long nowMs) =>
        SuspectStartMs == null ? 0 : (nowMs - SuspectStartMs.Value) / 1000.0;
}

public sealed class SimpleTracker
{
    private readonly List<Track> _tracks = new();
    private int _nextId = 1;
    private readonly float _minIou;
    private readonly int _maxMissed;
    private readonly double _memoryMs, _walkThreshold;
    private readonly double _walkRatio, _minAspect, _minSpanMs, _maxHeightChange;

    public SimpleTracker(float minIou = 0.25f, int maxMissed = 10,
                         double memoryMs = 1500, double walkThreshold = 11,
                         double walkRatio = 0.2, double minAspect = 1.7,
                         double minSpanMs = 700, double maxHeightChange = 0.25)
    {
        _minIou = minIou; _maxMissed = maxMissed;
        _memoryMs = memoryMs; _walkThreshold = walkThreshold;
        _walkRatio = walkRatio; _minAspect = minAspect;
        _minSpanMs = minSpanMs; _maxHeightChange = maxHeightChange;
    }

    public List<Track> Update(List<Detection> dets, long nowMs)
    {
        // 1) hitung kecocokan semua pasangan (track lama, deteksi baru)
        var pairs = new List<(int Ti, int Di, float Iou)>();
        for (int ti = 0; ti < _tracks.Count; ti++)
            for (int di = 0; di < dets.Count; di++)
            {
                float v = Iou(_tracks[ti].Box, dets[di].Box);
                if (v >= _minIou) pairs.Add((ti, di, v));
            }

        // 2) pasangkan mulai dari yang paling cocok, satu track hanya satu deteksi
        var usedT = new HashSet<int>();
        var usedD = new HashSet<int>();
        foreach (var p in pairs.OrderByDescending(x => x.Iou))
        {
            if (usedT.Contains(p.Ti) || usedD.Contains(p.Di)) continue;
            usedT.Add(p.Ti); usedD.Add(p.Di);
            _tracks[p.Ti].Push(dets[p.Di].Box, nowMs, _memoryMs, _walkThreshold,
                _walkRatio, _minAspect, _minSpanMs, _maxHeightChange);
        }

        // 3) track yang tidak cocok: dihitung "hilang", dibuang kalau terlalu lama
        for (int ti = 0; ti < _tracks.Count; ti++)
            if (!usedT.Contains(ti)) _tracks[ti].Missed++;

        // 4) deteksi yang tidak cocok: orang baru, beri ID baru
        for (int di = 0; di < dets.Count; di++)
            if (!usedD.Contains(di))
            {
                var t = new Track { Id = _nextId++ };
                t.Push(dets[di].Box, nowMs, _memoryMs, _walkThreshold,
                    _walkRatio, _minAspect, _minSpanMs, _maxHeightChange);
                _tracks.Add(t);
            }

        _tracks.RemoveAll(t => t.Missed > _maxMissed);
        return _tracks.Where(t => t.Missed == 0).ToList();
    }

    private static float Iou(Rect a, Rect b)
    {
        int x1 = Math.Max(a.X, b.X), y1 = Math.Max(a.Y, b.Y);
        int x2 = Math.Min(a.Right, b.Right), y2 = Math.Min(a.Bottom, b.Bottom);
        int inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        float union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }
}