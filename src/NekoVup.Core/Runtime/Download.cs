namespace NekoVup.Core.Runtime;

/// <summary>큰 파일 내려받기 (ProPainter 환경, 모델 가중치).</summary>
public static class Download
{
    /// <summary>
    /// 이만큼 아무것도 오지 않으면 그만둔다. HttpClient의 시간 제한은 응답 머리까지만 본다 —
    /// 본문을 받다가 연결이 소리 없이 끊기면(무선 끊김) 읽기가 끝없이 기다려 진행률이 그 자리에 멈춘다.
    /// </summary>
    public static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// <paramref name="url"/>을 <paramref name="path"/>에 받는다 (있으면 덮어쓴다). 진행은 (받은 바이트, 전체 — 모르면 -1).
    /// 실패는 <see cref="HttpRequestException"/>(응답 코드·연결), <see cref="IOException"/>(디스크), <see cref="TimeoutException"/>(응답 없음)으로 알린다.
    /// 사용자가 취소한 것만 <see cref="OperationCanceledException"/>이다.
    /// </summary>
    public static async Task ToFileAsync(HttpClient http, Uri url, string path, Action<long, long>? progress, CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(StallTimeout);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1;
            await using var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
            {
                stall.CancelAfter(StallTimeout);
                await target.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                done += read;
                progress?.Invoke(done, total);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient 자체의 시간 제한(TaskCanceledException)도 여기로 온다. "취소"로 보이면 안 된다.
            throw new TimeoutException($"서버가 {StallTimeout.TotalSeconds:0}초 동안 응답하지 않습니다 (인터넷 연결 확인): {url.Host}");
        }
    }
}
