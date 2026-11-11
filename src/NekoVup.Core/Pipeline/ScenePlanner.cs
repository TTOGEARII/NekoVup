using NekoVup.Core.Chunks;

namespace NekoVup.Core.Pipeline;

/// <summary>원본의 프레임 구간 [First, First + Count). 프레임 번호는 0부터 센다.</summary>
public readonly record struct FrameSpan(long First, long Count)
{
    public long End => First + Count;
    public bool Contains(long frame) => frame >= First && frame < End;

    /// <summary>프레임 번호를 이 구간 안으로 당긴다.</summary>
    public long Clamp(long frame) => Math.Clamp(frame, First, Math.Max(First, End - 1));
}

/// <summary>
/// 시각(초)과 프레임 번호를 오간다. 구간의 시작을 초로만 넘기면 결과의 첫 프레임이 "그 시각 뒤의 첫 프레임"이라
/// 원본과 결과를 같은 시각으로 뽑았을 때 한 프레임씩 어긋난다 — 프레임 번호로 맞춘다.
/// </summary>
public static class FrameGrid
{
    /// <summary>시각 <paramref name="seconds"/>에 화면에 나오는 프레임 (그 시각이거나 바로 뒤의 첫 프레임).</summary>
    public static long FrameAt(double seconds, double fps) => Math.Max(0, (long)Math.Ceiling(seconds * fps - 1e-6));

    public static double TimeOf(long frame, double fps) => frame / fps;

    /// <summary>
    /// 구간을 <paramref name="startSeconds"/>(ffmpeg -ss)에서 잘라 일정한 간격으로 편 뒤의 <paramref name="index"/>번째 프레임이
    /// 원본의 몇 번째 프레임인지. 프레임은 가장 가까운 칸에 놓이므로, 시작이 프레임 사이에 있으면 "시작 뒤의 첫 프레임"에서
    /// 차례로 세는 것과 한 장 어긋날 수 있다 (그때 0번 칸은 첫 프레임을 한 번 더 쓴다).
    /// </summary>
    public static long SourceFrame(double? startSeconds, long index, double fps)
    {
        double start = startSeconds ?? 0;
        return Math.Max(FrameAt(start, fps), (long)Math.Ceiling(index + start * fps - 0.5));
    }

    /// <summary>
    /// 프레임 <paramref name="first"/>부터 처리하게 하는 시작 시각 (ffmpeg -ss). 프레임 사이(1/4 프레임 앞)를 가리켜
    /// 시각의 반올림 오차로 한 프레임 앞뒤가 바뀌지 않게 한다. 처음부터면 null.
    /// </summary>
    public static double? SeekSeconds(long first, double fps) => first > 0 ? (first - 0.25) / fps : null;

    /// <summary><see cref="SeekSeconds"/>에서 시작해 정확히 <paramref name="count"/>프레임을 담는 길이 (ffmpeg -t).</summary>
    public static double LengthSeconds(long first, long count, double fps) => (first > 0 ? count : count - 0.25) / fps;

    /// <summary>파일에서 프레임 <paramref name="frame"/>의 그림을 뽑을 시각 (그 프레임과 앞 프레임 사이).</summary>
    public static double PickSeconds(long frame, double fps) => Math.Max(0, (frame - 0.5) / fps);
}

/// <summary>빠른 미리보기가 처리할 구간(<see cref="File"/>)과, 그 가운데 실제 렌더링과 같게 나오는 부분(<see cref="Exact"/>).</summary>
public readonly record struct ScenePlan(FrameSpan File, FrameSpan Exact);

/// <summary>
/// 현재 장면 빠른 미리보기로 처리할 구간을 정한다. 프레임마다 따로 처리하는 방식(delogo, Video2X)은 재생 헤드 둘레만 잘라도
/// 결과가 같지만, 앞뒤 프레임을 함께 보는 방식은 잘라 낸 길이에 따라 결과가 달라진다 —
/// 실제 작업이 그 프레임을 처리할 때 쓰는 조각과 똑같은 조각을 처리해야 미리보기가 렌더링 결과와 같다.
/// </summary>
public static class ScenePlanner
{
    /// <summary>옛 비디오(RealBasicVSR) 스크립트가 한 번에 넣는 프레임 수 (realbasicvsr.py의 --chunk 기본값).</summary>
    public const int OldVideoChunk = 24;

    /// <param name="playhead">재생 헤드의 프레임 (원본 기준).</param>
    /// <param name="job">실제 작업이 처리할 구간 — 조각은 이 구간의 처음부터 센다.</param>
    /// <param name="sceneFrames">프레임마다 따로 처리하는 방식에서 잘라 낼 길이.</param>
    public static ScenePlan Plan(long playhead, FrameSpan job, int sceneFrames, bool proPainter, bool oldVideo, int chunkLength, int overlap)
    {
        long total = Math.Max(1, job.Count);
        long p = Math.Clamp(playhead - job.First, 0, total - 1);

        if (proPainter)
        {
            // 재생 헤드를 덮는 조각(겹치는 자리면 둘)을 통째로 처리한다. 잘라 낸 구간을 다시 나눠도 같은 조각이 나온다.
            var chunks = ChunkPlanner.Plan((int)Math.Min(total, int.MaxValue), chunkLength, overlap);
            int first = 0;
            while (!chunks[first].Contains((int)p)) first++;
            int last = first;
            while (last + 1 < chunks.Count && chunks[last + 1].Contains((int)p)) last++;

            // 이웃 조각과 겹치는 자리는 실제로는 그 조각과 섞이므로, 미리보기에서는 겹치지 않는 가운데만 같다.
            long exactStart = first > 0 ? Math.Max(chunks[first].Start, chunks[first - 1].End) : 0;
            long exactEnd = last + 1 < chunks.Count ? Math.Min(chunks[last].End, chunks[last + 1].Start) : chunks[last].End;
            return new ScenePlan(
                new FrameSpan(job.First + chunks[first].Start, chunks[last].End - chunks[first].Start),
                new FrameSpan(job.First + exactStart, exactEnd - exactStart));
        }

        if (oldVideo)
        {
            // 조각은 앞 조각의 끝 프레임 몇 장을 문맥으로 물려받는다 — 한 조각 앞에서 시작해야 재생 헤드의 조각이 실제와 같은 문맥을 받는다.
            long chunk = p / OldVideoChunk * OldVideoChunk;
            long start = Math.Max(0, chunk - OldVideoChunk);
            long end = Math.Min(chunk + OldVideoChunk, total);
            return new ScenePlan(new FrameSpan(job.First + start, end - start), new FrameSpan(job.First + chunk, end - chunk));
        }

        long count = Math.Clamp(sceneFrames, 1, total);
        long begin = Math.Clamp(p - count / 2, 0, total - count);
        var span = new FrameSpan(job.First + begin, count);
        return new ScenePlan(span, span);
    }
}
