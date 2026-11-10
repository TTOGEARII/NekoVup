namespace NekoVup.Core.Chunks;

/// <summary>프레임 구간 [Start, End). 프레임 번호는 0부터 센다.</summary>
public readonly record struct ChunkRange(int Index, int Start, int End)
{
    public int Length => End - Start;
    public bool Contains(int frame) => frame >= Start && frame < End;
}

/// <summary>긴 영상을 ProPainter가 한 번에 올릴 수 있는 길이로 나눈다. 이웃 조각은 overlap만큼 겹친다.</summary>
public static class ChunkPlanner
{
    public static IReadOnlyList<ChunkRange> Plan(int frameCount, int chunkLength = 100, int overlap = 10)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkLength, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(overlap);
        if ((long)overlap * 2 > chunkLength)
            throw new ArgumentException($"겹침({overlap})은 조각 길이({chunkLength})의 절반 이하여야 합니다.");

        var chunks = new List<ChunkRange>();
        int step = chunkLength - overlap;
        for (int start = 0; ; start += step)
        {
            int end = Math.Min(start + chunkLength, frameCount);
            chunks.Add(new ChunkRange(chunks.Count, start, end));
            if (end == frameCount) break;
        }

        // 마지막 조각이 너무 짧으면 시간축 문맥이 모자라 품질이 떨어지므로 앞 조각에 붙인다.
        int minTail = Math.Max(overlap * 2, 16);
        if (chunks.Count > 1 && chunks[^1].Length < minTail)
        {
            chunks.RemoveAt(chunks.Count - 1);
            chunks[^1] = chunks[^1] with { End = frameCount };
        }

        return chunks;
    }
}

/// <summary>겹치는 구간에서 조각들을 섞는 가중치.</summary>
public static class Crossfade
{
    /// <summary>조각 안에서 프레임 i의 가중치: 조각 가장자리에서 1, 안쪽으로 갈수록 커진다.</summary>
    public static int RawWeight(ChunkRange chunk, int frame) =>
        chunk.Contains(frame) ? Math.Min(frame - chunk.Start + 1, chunk.End - frame) : 0;

    /// <summary>프레임을 덮는 조각들과 합이 1이 되도록 정규화한 가중치.</summary>
    public static IReadOnlyList<(ChunkRange Chunk, float Weight)> Weights(IReadOnlyList<ChunkRange> chunks, int frame)
    {
        var covering = new List<(ChunkRange Chunk, float Weight)>(2);
        long total = 0;
        foreach (var chunk in chunks)
        {
            int w = RawWeight(chunk, frame);
            if (w <= 0) continue;
            covering.Add((chunk, w));
            total += w;
        }
        if (total == 0) throw new ArgumentOutOfRangeException(nameof(frame), $"프레임 {frame}을 덮는 조각이 없습니다.");
        for (int i = 0; i < covering.Count; i++)
            covering[i] = (covering[i].Chunk, covering[i].Weight / total);
        return covering;
    }
}
