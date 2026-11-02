using OpenCvSharp;

public sealed class PhoneChecker
{
    private const int PhoneClass = 67;   // "cell phone" di COCO
    private readonly YoloDetector _detector;
    private readonly float _conf;
    private readonly double _activeZoneRatio;
    private readonly int _minPersonH, _maxCheck;

    public PhoneChecker(YoloDetector detector, float conf = 0.15f, double activeZoneRatio = 0.5,
                        int minPersonH = 60, int maxCheck = 10)
    {
        _detector = detector; _conf = conf; _activeZoneRatio = activeZoneRatio;
        _minPersonH = minPersonH; _maxCheck = maxCheck;
    }

    public void Check(Mat frame, List<Track> tracks, long nowMs, long walkGraceMs = 1000)
    {
        foreach (var t in tracks) t.HasPhone = false;

        // HP hanya dicek untuk orang yang sedang (atau barusan) berjalan: yang duduk/berdiri tidak mungkin
        // melanggar, jadi inferensi untuk mereka dilewati. Ini yang membuat CPU biasa sanggup.
        // Lalu hanya orang yang cukup besar, dan paling banyak _maxCheck orang terbesar.
        var kandidat = tracks.Where(t => t.Box.Height >= _minPersonH
                                         && (t.Walking || nowMs - t.LastWalkMs <= walkGraceMs))
                             .OrderByDescending(t => t.Box.Height)
                             .Take(_maxCheck);

        foreach (var t in kandidat)
        {
            int x1 = t.Box.X, y1 = t.Box.Y, y2 = t.Box.Bottom, x2 = t.Box.Right;
            int cx1 = Math.Max(0, x1 - 50), cy1 = Math.Max(0, y1 - 50);
            int cx2 = Math.Min(frame.Width, x2 + 50), cy2 = Math.Min(frame.Height, y2 + 50);
            if (cx2 - cx1 < 2 || cy2 - cy1 < 2) continue;

            using var crop = new Mat(frame, new Rect(cx1, cy1, cx2 - cx1, cy2 - cy1));
            using var zoom = new Mat();
            Cv2.Resize(crop, zoom, new Size(), 2.0, 2.0, InterpolationFlags.Cubic);

            var phones = _detector.Detect(zoom, PhoneClass, _conf);

            // garis aktif: batas separuh atas tubuh (dalam koordinat gambar yang sudah di-zoom)
            double garis = ((y1 - cy1) + (y2 - y1) * _activeZoneRatio) * 2;
            foreach (var p in phones)
            {
                double pusatY = p.Box.Y + p.Box.Height / 2.0;
                if (pusatY <= garis) { t.HasPhone = true; break; }
            }
        }
    }
}