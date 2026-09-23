using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Nexus;

/// <summary>一次下载的结果。失败时 <see cref="Message"/> 里是可读原因。</summary>
public sealed record DownloadResult(bool Success, string? FilePath, long BytesReceived, string Message);

/// <summary>
/// 支持断点续传的下载器。先写 <c>目标文件.part</c>，全部成功后再原子改名，
/// 半截文件不会被当成完整文件；失败可重试，服务器支持 Range 时能从上次断点继续。
/// Mod 包可能很大，因此客户端超时设为 10 分钟。
///
/// 服务器支持 Range 且文件超过 <see cref="SegmentThresholdBytes"/> 时会切成多片并发下载
/// （最多 <see cref="MaxSegments"/> 个连接）：多个连接各占一段、写进同一个预分配 .part 文件的不同偏移，
/// 每片的进度记在 <c>.part.json</c> 里，中断后重来能接着下。服务器不支持 Range 时自动退回单连接。
/// </summary>
public static class ResumableDownloader
{
    private const int BufferSize = 81920;

    /// <summary>小于这个大小就不值得分片（多连接的开销比收益还大）。</summary>
    private const long SegmentThresholdBytes = 4L * 1024 * 1024;

    /// <summary>单片的目标大小；实际会用「总长 ÷ 片数」均分，避免尾部出现很小的片。</summary>
    private const long SegmentSizeBytes = 2L * 1024 * 1024;

    /// <summary>分片并发上限。再多对 CDN 不友好，收益也趋于饱和。</summary>
    private const int MaxSegments = 6;

    /// <summary>重试等待：最多重试 3 次，指数退避；命中 Retry-After 时按服务器要求等待。</summary>
    private static readonly int[] RetryDelaysMs = [1000, 3000, 7000];

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    /// <summary>
    /// 下载到目标文件，支持断点续传。目标已存在且没有残留 .part 时不重复下载。
    /// 任何异常都被捕获并写进结果，绝不向外抛。
    /// </summary>
    public static async Task<DownloadResult> DownloadAsync(string url, string destinationPath,
        IProgress<double>? progress = null, CancellationToken token = default,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new DownloadResult(false, null, 0, "下载地址为空");

        if (string.IsNullOrWhiteSpace(destinationPath))
            return new DownloadResult(false, null, 0, "目标文件路径为空");

        string partPath;
        try
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            partPath = destinationPath + ".part";
        }
        catch (Exception ex)
        {
            Log.Warn($"准备下载目录失败：{destinationPath}（{ex.Message}）");
            return new DownloadResult(false, null, 0, $"准备下载目录失败：{ex.Message}");
        }

        // 目标文件已在、且没有残留分片，视为上一次已下完
        if (File.Exists(destinationPath) && !File.Exists(partPath))
        {
            var size = SafeLength(destinationPath);
            progress?.Report(1);
            return new DownloadResult(true, destinationPath, size, "目标文件已存在，跳过下载");
        }

        if (!HasFreeSpace(destinationPath, 1, out var spaceError))
            return new DownloadResult(false, null, 0, spaceError!);

        // 服务器支持 Range 且文件够大 → 分片并发；探测失败或文件太小就退回单连接
        try
        {
            var probe = await ProbeAsync(url, headers, token);

            if (probe.SupportsRange && probe.Total >= SegmentThresholdBytes)
                return await DownloadSegmentedAsync(url, destinationPath, partPath, probe.Total,
                    probe.Validator, headers, progress, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Log.Info($"下载已取消：{url}");
            return new DownloadResult(false, null, SafeLength(partPath), "下载已取消");
        }
        catch (Exception ex)
        {
            Log.Warn($"分片探测失败，改用单连接下载：{url}（{ex.Message}）");
        }

        var received = File.Exists(partPath) ? SafeLength(partPath) : 0;
        var lastMessage = "下载失败";

        for (var attempt = 0; attempt <= RetryDelaysMs.Length; attempt++)
        {
            AttemptOutcome outcome;

            try
            {
                outcome = await TryOnceAsync(url, destinationPath, partPath, progress, headers, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Log.Info($"下载已取消：{url}");
                return new DownloadResult(false, null, SafeLength(partPath), "下载已取消");
            }
            catch (OperationCanceledException)
            {
                outcome = new AttemptOutcome(true, null, "下载超时");
            }
            catch (Exception ex)
            {
                outcome = new AttemptOutcome(true, null, ex.Message);
            }

            // 兜底：正常路径下 Retry=false 一定带结果，这里避免任何情况下返回 null
            if (!outcome.Retry)
                return outcome.Result ?? new DownloadResult(false, null, SafeLength(partPath),
                    outcome.Message ?? "下载失败");

            lastMessage = outcome.Message ?? lastMessage;
            received = outcome.BytesReceived ?? SafeLength(partPath);
            Log.Warn($"下载失败（第 {attempt + 1} 次）：{url}（{lastMessage}）");

            if (attempt >= RetryDelaysMs.Length) break;

            var delay = outcome.RetryDelay ?? TimeSpan.FromMilliseconds(RetryDelaysMs[attempt]);

            try
            {
                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException)
            {
                return new DownloadResult(false, null, received, "下载已取消");
            }
        }

        return new DownloadResult(false, null, received, $"重试 {RetryDelaysMs.Length} 次后仍失败：{lastMessage}");
    }

    /// <summary>单次尝试：返回是否应重试，以及最终结果。</summary>
    private static async Task<AttemptOutcome> TryOnceAsync(string url, string destinationPath, string partPath,
        IProgress<double>? progress, IReadOnlyDictionary<string, string>? headers, CancellationToken token)
    {
        var startAt = File.Exists(partPath) ? SafeLength(partPath) : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        ApplyHeaders(request, headers);

        if (startAt > 0) request.Headers.Range = new RangeHeaderValue(startAt, null);

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            var retryAfter = response.Headers.RetryAfter;
            var message = $"HTTP {code} {response.ReasonPhrase}";
            if (retryAfter is not null) message += $"（Retry-After：{retryAfter}）";

            // 429 与 5xx 属于临时故障，可重试；其余（404/403 等）重试也没用
            var retryable = code is 408 or 429 or >= 500;
            var failure = new DownloadResult(false, null, startAt, message);

            return retryable
                ? new AttemptOutcome(true, failure, message,
                    retryAfter?.Delta is { } delta ? delta : null, startAt)
                : new AttemptOutcome(false, failure, message);
        }

        bool append;
        long total;

        if (response.StatusCode == HttpStatusCode.PartialContent && startAt > 0)
        {
            // 服务器接受了 Range，从断点续写
            append = true;
            total = response.Content.Headers.ContentRange?.Length
                    ?? startAt + (response.Content.Headers.ContentLength ?? 0);
        }
        else
        {
            // 服务器不支持 Range（返回 200），丢掉分片从头开始
            if (startAt > 0)
            {
                Log.Info($"服务器不支持断点续传，将从头下载：{url}");
                TryDelete(partPath);
                startAt = 0;
            }

            append = false;
            total = response.Content.Headers.ContentLength ?? -1;
        }

        if (total > 0 && !HasFreeSpace(destinationPath, total - startAt, out var spaceError))
            return new AttemptOutcome(false, new DownloadResult(false, null, startAt, spaceError!));

        var received = startAt;

        await using (var source = await response.Content.ReadAsStreamAsync(token))
        await using (var target = new FileStream(partPath,
                         append ? FileMode.Append : FileMode.Create, FileAccess.Write,
                         FileShare.None, BufferSize, useAsync: true))
        {
            var buffer = new byte[BufferSize];
            int read;

            while ((read = await source.ReadAsync(buffer, token)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), token);
                received += read;

                // 拿不到总长度时回报不确定值 0
                progress?.Report(total > 0 ? Math.Clamp((double)received / total, 0d, 1d) : 0d);
            }
        }

        if (total > 0 && received < total)
        {
            // 连接被中途掐断：保留分片，下次续传
            return new AttemptOutcome(true, null,
                $"下载中断：已收 {FormatSize(received)} / {FormatSize(total)}", null, received);
        }

        File.Move(partPath, destinationPath, overwrite: true);
        progress?.Report(1);
        Log.Info($"下载完成：{destinationPath}（{FormatSize(received)}）");

        return new AttemptOutcome(false, new DownloadResult(true, destinationPath, received, "下载完成"));
    }

    // ————— 分片并发 —————

    /// <summary>探测服务器是否支持 Range，并取回总长度与校验值（ETag / Last-Modified）。</summary>
    private static async Task<ProbeResult> ProbeAsync(string url,
        IReadOnlyDictionary<string, string>? headers, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(request, headers);

        // 只请求第 1 个字节：支持 Range 的服务器会回 206 + Content-Range（带总长度）
        request.Headers.Range = new RangeHeaderValue(0, 0);

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

        if (!response.IsSuccessStatusCode)
            return new ProbeResult(false, -1, string.Empty,
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        var validator = ReadValidator(response);

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var length = response.Content.Headers.ContentRange?.Length ?? -1;
            return new ProbeResult(length > 0, length, validator, null);
        }

        // 回 200：不支持 Range（或直接忽略了 Range 头），只能单连接下
        return new ProbeResult(false, response.Content.Headers.ContentLength ?? -1, validator, null);
    }

    /// <summary>
    /// 分片并发下载。各连接写进同一个预分配 .part 文件的不同偏移，互不重叠；
    /// 每片进度写进 .part.json，中断后按同一方案重来即可接着下。
    /// </summary>
    private static async Task<DownloadResult> DownloadSegmentedAsync(string url, string destinationPath,
        string partPath, long total, string validator, IReadOnlyDictionary<string, string>? headers,
        IProgress<double>? progress, CancellationToken token)
    {
        if (!HasFreeSpace(destinationPath, total, out var spaceError))
            return new DownloadResult(false, null, 0, spaceError!);

        var count = (int)Math.Clamp((total + SegmentSizeBytes - 1) / SegmentSizeBytes, 1, MaxSegments);
        var segmentSize = (total + count - 1) / count;
        var done = new long[count];

        // 续传前提：链接、总长度、校验值都对得上，且 .part 已经预分配到正确长度
        var resume = LoadState(partPath);

        if (resume is { Done.Length: var savedCount }
            && savedCount == count
            && resume.Total == total
            && string.Equals(resume.Url, url, StringComparison.Ordinal)
            && string.Equals(resume.Validator, validator, StringComparison.Ordinal)
            && SafeLength(partPath) == total)
        {
            Array.Copy(resume.Done, done, count);
            Log.Info($"分片续传：{Path.GetFileName(destinationPath)}" +
                     $"（已完成 {FormatSize(done.Sum())} / {FormatSize(total)}）");
        }
        else
        {
            // 换了文件就整份重来，绝不能把两段内容拼在一起
            TryDelete(partPath);
            TryDelete(StatePath(partPath));
        }

        try
        {
            using var allocate = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
            if (allocate.Length != total) allocate.SetLength(total);
        }
        catch (Exception ex)
        {
            return new DownloadResult(false, null, 0, $"无法创建分片文件：{ex.Message}");
        }

        var received = new long[] { done.Sum() };
        var tasks = new List<Task>(count);

        for (var index = 0; index < count; index++)
        {
            var start = (long)index * segmentSize;
            var end = Math.Min(total, start + segmentSize) - 1;
            if (start > end) continue;

            var slot = index;
            tasks.Add(Task.Run(() => RunSegmentAsync(slot, start, end), CancellationToken.None));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            SaveState(partPath, url, total, validator, done);
            Log.Info($"分片下载已取消，进度已保留：{Path.GetFileName(destinationPath)}");
            throw;
        }
        catch (Exception ex)
        {
            SaveState(partPath, url, total, validator, done);
            return new DownloadResult(false, null, Interlocked.Read(ref received[0]), $"分片下载失败：{ex.Message}");
        }

        var finished = done.Sum();
        if (finished < total)
        {
            SaveState(partPath, url, total, validator, done);
            return new DownloadResult(false, null, finished,
                $"下载不完整：{FormatSize(finished)} / {FormatSize(total)}");
        }

        TryDelete(StatePath(partPath));
        File.Move(partPath, destinationPath, overwrite: true);
        progress?.Report(1);
        Log.Info($"下载完成（{count} 连接分片）：{destinationPath}（{FormatSize(total)}）");

        return new DownloadResult(true, destinationPath, total, $"下载完成（{count} 连接分片）");

        async Task RunSegmentAsync(int slot, long start, long end)
        {
            var from = start + done[slot];

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    ApplyHeaders(request, headers);
                    request.Headers.Range = new RangeHeaderValue(from, end);

                    using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

                    if (response.StatusCode != HttpStatusCode.PartialContent)
                    {
                        var code = (int)response.StatusCode;

                        // 临时故障可重试，其余（403/404 等）重试也没用
                        if (code is 408 or 429 or >= 500)
                            throw new RetryableDownloadException($"HTTP {code} {response.ReasonPhrase}");

                        throw new InvalidOperationException($"服务器未按分片响应：HTTP {code} {response.ReasonPhrase}");
                    }

                    // 服务器必须从我们要的位置开始给，否则写进去就是错位的数据
                    if (response.Content.Headers.ContentRange?.From is { } actualFrom && actualFrom != from)
                    {
                        throw new InvalidOperationException(
                            $"服务器返回的区间不符：请求 {from}，实际 {actualFrom}");
                    }

                    await using var source = await response.Content.ReadAsStreamAsync(token);
                    await using (var target = new FileStream(partPath, FileMode.Open, FileAccess.Write,
                                     FileShare.ReadWrite, BufferSize, useAsync: true))
                    {
                        target.Seek(from, SeekOrigin.Begin);

                        var buffer = new byte[BufferSize];
                        int read;

                        while ((read = await source.ReadAsync(buffer, token)) > 0)
                        {
                            await target.WriteAsync(buffer.AsMemory(0, read), token);

                            from += read;
                            done[slot] += read;

                            var now = Interlocked.Add(ref received[0], read);
                            progress?.Report(Math.Clamp((double)now / total, 0d, 1d));
                        }
                    }

                    if (from > end) return;   // 本片收完

                    throw new RetryableDownloadException(
                        $"分片被中断：已收 {FormatSize(from - start)} / {FormatSize(end - start + 1)}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var retryable = ex is RetryableDownloadException or HttpRequestException or IOException;

                    if (!retryable || attempt >= RetryDelaysMs.Length) throw;

                    Log.Warn($"分片 {slot + 1}/{count} 失败（第 {attempt + 1} 次）：{ex.Message}");
                    await Task.Delay(RetryDelaysMs[attempt], token);
                }
            }
        }
    }

    /// <summary>校验值：优先 ETag，退回 Last-Modified；都拿不到就空串（此时仅比对长度）。</summary>
    private static string ReadValidator(HttpResponseMessage response)
        => response.Headers.ETag?.Tag
           ?? response.Content.Headers.LastModified?.ToString("R")
           ?? string.Empty;

    private static string StatePath(string partPath) => partPath + ".json";

    private static SegmentState? LoadState(string partPath)
    {
        var path = StatePath(partPath);

        try
        {
            if (!File.Exists(path)) return null;

            var state = JsonSerializer.Deserialize<SegmentState>(File.ReadAllText(path));
            return state is { Done.Length: > 0 } ? state : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取分片进度失败，将重新下载：{ex.Message}");
            return null;
        }
    }

    private static void SaveState(string partPath, string url, long total, string validator, long[] done)
    {
        try
        {
            var json = JsonSerializer.Serialize(new SegmentState(url, total, validator, [.. done]));
            File.WriteAllText(StatePath(partPath), json);
        }
        catch (Exception ex)
        {
            Log.Warn($"保存分片进度失败：{ex.Message}");
        }
    }

    private static void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null) return;

        foreach (var pair in headers)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key)) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }
    }

    /// <summary>目标盘剩余空间预检查。</summary>
    private static bool HasFreeSpace(string path, long requiredBytes, out string? error)
    {
        error = null;

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return true;

            var drive = new DriveInfo(root);
            if (!drive.IsReady) return true;

            if (requiredBytes > 0 && drive.AvailableFreeSpace < requiredBytes)
            {
                error = $"磁盘剩余空间不足：需要 {FormatSize(requiredBytes)}，可用 {FormatSize(drive.AvailableFreeSpace)}";
                Log.Warn($"磁盘空间不足：{path}（{error}）");
                return false;
            }
        }
        catch (Exception ex)
        {
            // 空间检查失败不阻断下载
            Log.Warn($"磁盘空间检查失败：{path}（{ex.Message}）");
        }

        return true;
    }

    private static long SafeLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"清理分片文件失败：{path}（{ex.Message}）");
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024 / 1024:0.##} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / 1024d / 1024:0.##} MB";
        if (bytes >= 1024) return $"{bytes / 1024d:0.##} KB";
        return $"{bytes} B";
    }

    /// <summary>单次尝试的结果：要么给出最终结果，要么给出可重试的原因。</summary>
    private sealed record AttemptOutcome(bool Retry, DownloadResult? Result, string? Message = null,
        TimeSpan? RetryDelay = null, long? BytesReceived = null);

    /// <summary>Range 探测结果。<see cref="SupportsRange"/> 为假时只能单连接下载。</summary>
    private sealed record ProbeResult(bool SupportsRange, long Total, string Validator, string? Message);

    /// <summary>分片续传的进度记录（<c>.part.json</c>）。</summary>
    private sealed record SegmentState(string Url, long Total, string Validator, long[] Done);

    /// <summary>临时故障，值得重试（区别于 403/404 这类重试也没用的失败）。</summary>
    private sealed class RetryableDownloadException(string message) : Exception(message);
}
