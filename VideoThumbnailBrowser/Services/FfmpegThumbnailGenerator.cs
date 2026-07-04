using System.Diagnostics;
using System.Globalization;
using System.IO;
using VideoThumbnailBrowser.Models;

namespace VideoThumbnailBrowser.Services;

/// <summary>
/// ffmpeg.exe / ffprobe.exe を外部プロセスとして呼び出し、
/// 1本の動画から等間隔でN枚のサムネイル（JPEG）を生成する。
///
/// 同時に立ち上げるプロセス数はSemaphoreSlimで制限し、
/// 大量の動画を一度にスキャンしてもCPU/ディスクを食いつぶさないようにする。
/// </summary>
public class FfmpegThumbnailGenerator
{
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly string _cacheDir;
    private readonly SemaphoreSlim _concurrencyLimiter;

    public int ThumbnailWidth { get; set; } = 320;
    public int ThumbnailCount { get; set; } = 10;

    /// <summary>NVIDIA CUDAハードウェアデコードが利用可能かどうか。起動時に一度だけ判定する。</summary>
    private static readonly Lazy<bool> _cudaAvailable = new(() => DetectCuda());

    private static bool DetectCuda()
    {
        try
        {
            // nvidia-smiが存在すればNVIDIA GPUあり
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=name --format=csv,noheader")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    public FfmpegThumbnailGenerator(string ffmpegPath, string ffprobePath, string cacheDir, int maxConcurrency = 0)
    {
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
        _cacheDir = cacheDir;
        Directory.CreateDirectory(_cacheDir);

        var concurrency = maxConcurrency > 0 ? maxConcurrency : Math.Max(1, Environment.ProcessorCount / 2);
        _concurrencyLimiter = new SemaphoreSlim(concurrency);
    }

    public async Task<VideoItem?> GenerateAsync(string filePath, CancellationToken ct = default)
    {
        // アプリ終了時のキャンセルとも連携する
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, App.AppCts.Token);
        ct = linked.Token;

        await _concurrencyLimiter.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists) return null;

            var duration = await GetDurationAsync(filePath, ct).ConfigureAwait(false);
            if (duration <= 0) return null;

            var hash = ComputeHash(filePath);
            var videoCacheDir = Path.Combine(_cacheDir, hash);
            Directory.CreateDirectory(videoCacheDir);

            var thumbnailPaths = new List<string>();
            var count = Math.Max(1, ThumbnailCount);

            for (var i = 0; i < count; i++)
            {
                // 最初と最後の暗転フレームを避けるため、全体の2%〜98%の範囲で等間隔に取る。
                var fraction = count == 1 ? 0.5 : 0.02 + (0.96 * i / (count - 1));
                var timestamp = duration * fraction;
                var outputPath = Path.Combine(videoCacheDir, $"thumb_{i:D3}.jpg");

                var ok = await ExtractFrameAsync(filePath, timestamp, outputPath, ct).ConfigureAwait(false);
                if (ok) thumbnailPaths.Add(outputPath);
            }

            if (thumbnailPaths.Count == 0) return null;

            return new VideoItem
            {
                FilePath = filePath,
                FileSize = fileInfo.Length,
                LastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks,
                DurationSeconds = duration,
                ThumbnailPaths = thumbnailPaths
            };
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    private async Task<double> GetDurationAsync(string filePath, CancellationToken ct)
    {
        var args = $"-v error -show_entries format=duration -of csv=p=0 \"{filePath}\"";
        var (success, stdout, _) = await RunProcessAsync(_ffprobePath, args, ct).ConfigureAwait(false);
        if (!success) return 0;

        var text = stdout.Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : 0;
    }

    private async Task<bool> ExtractFrameAsync(string filePath, double timestampSeconds, string outputPath, CancellationToken ct)
    {
        var ts = timestampSeconds.ToString("F2", CultureInfo.InvariantCulture);

        // NVIDIA GPUが使える場合はCUDAハードウェアデコードで高速化
        var hwaccel = _cudaAvailable.Value ? "-hwaccel cuda -hwaccel_output_format cuda " : "";
        var vf = _cudaAvailable.Value
            ? $"scale_cuda={ThumbnailWidth}:-1,hwdownload,format=nv12"
            : $"scale={ThumbnailWidth}:-1";

        var args = $"-y {hwaccel}-ss {ts} -i \"{filePath}\" -frames:v 1 -vf \"{vf}\" -q:v 4 \"{outputPath}\"";
        var (success, _, _) = await RunProcessAsync(_ffmpegPath, args, ct).ConfigureAwait(false);

        // CUDAで失敗した場合はソフトウェアデコードでリトライ
        if (!success && _cudaAvailable.Value)
        {
            var fallbackArgs = $"-y -ss {ts} -i \"{filePath}\" -frames:v 1 -vf \"scale={ThumbnailWidth}:-1\" -q:v 4 \"{outputPath}\"";
            var (s2, _, _) = await RunProcessAsync(_ffmpegPath, fallbackArgs, ct).ConfigureAwait(false);
            return s2 && File.Exists(outputPath);
        }

        return success && File.Exists(outputPath);
    }

    private static async Task<(bool success, string stdout, string stderr)> RunProcessAsync(
        string exePath, string arguments, CancellationToken ct)
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process = new Process { StartInfo = psi };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            return (process.ExitCode == 0, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            try { process?.Kill(entireProcessTree: true); } catch { }
            return (false, string.Empty, string.Empty);
        }
        catch (Exception)
        {
            try { process?.Kill(entireProcessTree: true); } catch { }
            return (false, string.Empty, string.Empty);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string ComputeHash(string filePath)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(filePath.ToLowerInvariant());
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash)[..16];
    }
}
