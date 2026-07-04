using System.IO;
using ICSharpCode.SharpZipLib.Zip;

namespace VideoThumbnailBrowser.Services;

/// <summary>
/// ZIP/CBZ書庫から最初の画像ファイルを抽出してサムネイルとして保存する。
/// RAR/7z等はSharpZipLibが対応していないため、ZIP/CBZのみ直接対応。
/// それ以外は外部ソフト（7-Zip等）のCLIがあれば使う。
/// </summary>
public class ArchiveThumbnailExtractor
{
    private readonly string _cacheDir;
    private readonly SemaphoreSlim _limiter;

    public ArchiveThumbnailExtractor(string cacheDir, int maxConcurrency = 4)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
        _limiter = new SemaphoreSlim(maxConcurrency);
    }

    public async Task<string?> ExtractCoverAsync(string archivePath, CancellationToken ct = default)
    {
        await _limiter.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var hash = ComputeHash(archivePath);
            var outDir = Path.Combine(_cacheDir, hash);
            Directory.CreateDirectory(outDir);
            var coverPath = Path.Combine(outDir, "cover.jpg");

            if (File.Exists(coverPath)) return coverPath;

            var ext = Path.GetExtension(archivePath).ToLowerInvariant();
            return ext is ".zip" or ".cbz"
                ? await ExtractFromZipAsync(archivePath, coverPath, ct)
                : await ExtractWithSevenZipAsync(archivePath, coverPath, ct);
        }
        finally
        {
            _limiter.Release();
        }
    }

    private static async Task<string?> ExtractFromZipAsync(
        string archivePath, string coverPath, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var fs = File.OpenRead(archivePath);
                using var zip = new ZipFile(fs);

                // 画像エントリを名前順に並べて最初の1枚を取得
                var entries = zip.Cast<ZipEntry>()
                    .Where(e => e.IsFile && ArchiveFileTypes.IsImageFile(e.Name))
                    .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (entries.Count == 0) return null;

                using var entryStream = zip.GetInputStream(entries[0]);
                using var ms = new MemoryStream();
                entryStream.CopyTo(ms);
                ms.Position = 0;

                // JPEG変換（そのまま保存）
                File.WriteAllBytes(coverPath, ms.ToArray());
                return coverPath;
            }
            catch
            {
                return null;
            }
        }, ct);
    }

    private static async Task<string?> ExtractWithSevenZipAsync(
        string archivePath, string coverPath, CancellationToken ct)
    {
        // 7z.exe が PATH または Tools/ にあれば使用する
        var sevenZip = Find7Zip();
        if (sevenZip == null) return null;

        try
        {
            // まずファイル一覧を取得して最初の画像名を探す
            var listArgs = $"l \"{archivePath}\" -slt";
            var (ok, output, _) = await RunAsync(sevenZip, listArgs, ct);
            if (!ok) return null;

            var firstImage = output.Split('\n')
                .Where(l => l.StartsWith("Path = ", StringComparison.OrdinalIgnoreCase))
                .Select(l => l[7..].Trim())
                .Where(ArchiveFileTypes.IsImageFile)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (firstImage == null) return null;

            var outDir = Path.GetDirectoryName(coverPath)!;
            var extractArgs = $"e \"{archivePath}\" -o\"{outDir}\" \"{firstImage}\" -y";
            var (ok2, _, _) = await RunAsync(sevenZip, extractArgs, ct);
            if (!ok2) return null;

            // 抽出されたファイルをcover.jpgにリネーム
            var extracted = Path.Combine(outDir, Path.GetFileName(firstImage));
            if (File.Exists(extracted))
            {
                if (File.Exists(coverPath)) File.Delete(coverPath);
                File.Move(extracted, coverPath);
                return coverPath;
            }
        }
        catch { }
        return null;
    }

    private static string? Find7Zip()
    {
        var candidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "7z.exe"),
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe",
            "7z.exe"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<(bool ok, string stdout, string stderr)> RunAsync(
        string exe, string args, CancellationToken ct)
    {
        Process? proc = null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe, Arguments = args,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            proc = new System.Diagnostics.Process { StartInfo = psi };
            proc.Start();
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return (proc.ExitCode == 0, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            try { proc?.Kill(entireProcessTree: true); } catch { }
            return (false, "", "");
        }
        catch
        {
            try { proc?.Kill(entireProcessTree: true); } catch { }
            return (false, "", "");
        }
        finally
        {
            proc?.Dispose();
        }
    }

    private static string ComputeHash(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant());
        return Convert.ToHexString(sha.ComputeHash(bytes))[..16];
    }
}
