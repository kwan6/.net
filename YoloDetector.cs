using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using System.Runtime.InteropServices;
using SessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions; 

public record Detection(Rect Box, float Score);

public sealed class YoloDetector : IDisposable
{
    private const int InputSize = 640;
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly float[] _input = new float[3 * InputSize * InputSize];

    public string Provider { get; }

    public YoloDetector(string modelPath, bool useGpu = false, int gpuId = 0)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Model tidak ditemukan: " + Path.GetFullPath(modelPath));

        Provider = "CPU";
        InferenceSession? session = null;
        if (useGpu)
        {
            try
            {
                var gpuOpts = new SessionOptions();
                gpuOpts.EnableMemoryPattern = false;
                gpuOpts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                gpuOpts.AppendExecutionProvider_DML(gpuId);
                session = new InferenceSession(modelPath, gpuOpts);   // gagal di sini kalau PC tidak punya GPU yang cocok
                Provider = $"GPU (DirectML, id {gpuId})";
            }
            catch (Exception e)
            {
                Console.WriteLine("GPU tidak bisa dipakai, pindah ke CPU: " + e.Message);
            }
        }
        _session = session ?? new InferenceSession(modelPath, new SessionOptions());
        _inputName = _session.InputMetadata.Keys.First();
    }

    public List<Detection> Detect(Mat frame, int classId, float conf = 0.35f, float iou = 0.45f)
    {
        // 1) Letterbox: kecilkan gambar tanpa merusak rasio, sisanya diisi abu-abu
        float scale = Math.Min((float)InputSize / frame.Width, (float)InputSize / frame.Height);
        int nw = (int)Math.Round(frame.Width * scale);
        int nh = (int)Math.Round(frame.Height * scale);
        int padX = (InputSize - nw) / 2, padY = (InputSize - nh) / 2;

        using var resized = new Mat();
        Cv2.Resize(frame, resized, new Size(nw, nh));
        using var canvas = new Mat(new Size(InputSize, InputSize), MatType.CV_8UC3, new Scalar(114, 114, 114));
        using (var roi = new Mat(canvas, new Rect(padX, padY, nw, nh)))
            resized.CopyTo(roi);

        // 2) Ubah ke format yang dimengerti model: RGB, skala 0-1, susunan [1,3,640,640]
        using var blob = CvDnn.BlobFromImage(canvas, 1 / 255.0, new Size(InputSize, InputSize),
            new Scalar(), swapRB: true, crop: false);
        Marshal.Copy(blob.Data, _input, 0, _input.Length);

        // 3) Jalankan model
        var tensor = new DenseTensor<float>(_input, new[] { 1, 3, InputSize, InputSize });
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
        var output = results.First().AsTensor<float>();   // bentuk [1, 84, 8400]
        if (output.Dimensions[1] > output.Dimensions[2])
            throw new InvalidOperationException("Bentuk output model tidak sesuai harapan");
        int n = output.Dimensions[2];
        var data = output.ToArray();

        // 4) Baca hasil: baris 0-3 = kotak (tengah x, tengah y, lebar, tinggi), baris 4+ = skor tiap kelas
        var found = new List<Detection>();
        int scoreRow = 4 + classId;
        for (int i = 0; i < n; i++)
        {
            float score = data[scoreRow * n + i];
            if (score < conf) continue;
            float cx = data[i], cy = data[n + i], w = data[2 * n + i], h = data[3 * n + i];
            int x1 = Math.Clamp((int)((cx - w / 2 - padX) / scale), 0, frame.Width - 1);
            int y1 = Math.Clamp((int)((cy - h / 2 - padY) / scale), 0, frame.Height - 1);
            int x2 = Math.Clamp((int)((cx + w / 2 - padX) / scale), 0, frame.Width - 1);
            int y2 = Math.Clamp((int)((cy + h / 2 - padY) / scale), 0, frame.Height - 1);
            if (x2 - x1 < 2 || y2 - y1 < 2) continue;
            found.Add(new Detection(new Rect(x1, y1, x2 - x1, y2 - y1), score));
        }
        return Nms(found, iou);
    }

    // NMS: buang kotak kembar pada orang yang sama, simpan yang skornya tertinggi
    private static List<Detection> Nms(List<Detection> dets, float iouThr)
    {
        var keep = new List<Detection>();
        foreach (var d in dets.OrderByDescending(x => x.Score))
            if (keep.All(k => Iou(k.Box, d.Box) < iouThr))
                keep.Add(d);
        return keep;
    }

    private static float Iou(Rect a, Rect b)
    {
        int x1 = Math.Max(a.X, b.X), y1 = Math.Max(a.Y, b.Y);
        int x2 = Math.Min(a.Right, b.Right), y2 = Math.Min(a.Bottom, b.Bottom);
        int inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        float union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }

    public void Dispose() => _session.Dispose();
}