using BonikPOS.VoiceSearch.Audio;
using BonikPOS.VoiceSearch.Core.Audio;

namespace BonikPOS.VoiceSearch.UnitTests.Audio;

public sealed class AudioFrameQueueTests
{
    private const int FrameSamples = 8;

    private static AudioFrameQueue NewQueue(int capacity = 4) => new(AudioStreamFormat.Speech16kMono, FrameSamples, capacity);

    private static float[] Frame(float value)
    {
        var f = new float[FrameSamples];
        Array.Fill(f, value);
        return f;
    }

    [Fact]
    public async Task Overrun_DropsTheOldestFrames_KeepsTheNewest_AndCountsEveryDrop()
    {
        var queue = NewQueue(capacity: 4);
        for (int i = 0; i < 10; i++)
        {
            Assert.True(queue.TryWrite(Frame(i), captureTimestamp: 1000 + i));
        }

        var stats = queue.GetStatistics();
        Assert.Equal(10, stats.FramesWritten);
        Assert.Equal(6, stats.FramesDropped);
        Assert.Equal(0, stats.FramesRead);
        Assert.Equal(4, stats.Depth);
        Assert.Equal(4, stats.MaxDepth);
        Assert.Equal(4, stats.Capacity);

        var dest = new float[FrameSamples];
        for (int expected = 6; expected < 10; expected++)
        {
            var read = await queue.ReadFrameAsync(dest, TestContext.Current.CancellationToken);
            Assert.True(read.HasFrame);
            Assert.Equal(expected, read.Info.Sequence);
            Assert.Equal(1000 + expected, read.Info.CaptureTimestamp);
            Assert.All(dest, s => Assert.Equal(expected, s));
        }

        stats = queue.GetStatistics();
        Assert.Equal(4, stats.FramesRead);
        Assert.Equal(0, stats.Depth);
        Assert.Equal(stats.FramesWritten, stats.FramesRead + stats.FramesDropped);
    }

    [Fact]
    public async Task Discontinuity_IsFlaggedOnlyOnTheFirstFrameAfterDrops_WithTheNumberOfDroppedFrames()
    {
        var queue = NewQueue(capacity: 4);
        var dest = new float[FrameSamples];
        var ct = TestContext.Current.CancellationToken;
        for (int i = 0; i < 3; i++)
        {
            queue.TryWrite(Frame(i), i);
        }

        for (int i = 0; i < 3; i++)
        {
            var r = await queue.ReadFrameAsync(dest, ct);
            Assert.False(r.Info.Discontinuity);
            Assert.Equal(0, r.Info.DroppedFramesBefore);
        }

        // Six more frames into a queue of four: sequences 3 and 4 are dropped.
        for (int i = 3; i < 9; i++)
        {
            queue.TryWrite(Frame(i), i);
        }

        var first = await queue.ReadFrameAsync(dest, ct);
        Assert.Equal(5, first.Info.Sequence);
        Assert.True(first.Info.Discontinuity);
        Assert.Equal(2, first.Info.DroppedFramesBefore);

        for (int expected = 6; expected < 9; expected++)
        {
            var r = await queue.ReadFrameAsync(dest, ct);
            Assert.Equal(expected, r.Info.Sequence);
            Assert.False(r.Info.Discontinuity);
            Assert.Equal(0, r.Info.DroppedFramesBefore);
        }

        Assert.Equal(2, queue.GetStatistics().FramesDropped);
    }

    [Fact]
    public async Task ReadFrameAsync_HonoursCancellation_WhileWaitingForAFrame()
    {
        var queue = NewQueue();
        using var cts = new CancellationTokenSource();
        var pending = queue.ReadFrameAsync(new float[FrameSamples], cts.Token).AsTask();
        Assert.False(pending.IsCompleted);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, queue.GetStatistics().FramesRead);
    }

    [Fact]
    public async Task ReadFrameAsync_WithAnAlreadyCancelledToken_ThrowsWhenNoFrameIsQueued()
    {
        var queue = NewQueue();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await queue.ReadFrameAsync(new float[FrameSamples], cts.Token));
    }

    [Fact]
    public async Task Complete_DrainsQueuedFramesFirst_ThenReportsCompletion_AndRejectsFurtherWrites()
    {
        var queue = NewQueue();
        var ct = TestContext.Current.CancellationToken;
        queue.TryWrite(Frame(1), 1);
        queue.TryWrite(Frame(2), 2);
        queue.Complete();

        Assert.False(queue.TryWrite(Frame(3), 3));

        var dest = new float[FrameSamples];
        Assert.True((await queue.ReadFrameAsync(dest, ct)).HasFrame);
        Assert.Equal(1f, dest[0]);
        Assert.True((await queue.ReadFrameAsync(dest, ct)).HasFrame);
        Assert.Equal(2f, dest[0]);

        var end = await queue.ReadFrameAsync(dest, ct);
        Assert.False(end.HasFrame);
        Assert.False((await queue.ReadFrameAsync(dest, ct)).HasFrame);

        var stats = queue.GetStatistics();
        Assert.Equal(2, stats.FramesWritten);
        Assert.Equal(2, stats.FramesRead);
    }

    [Fact]
    public async Task Complete_WakesAPendingReader_WithCompletion()
    {
        var queue = NewQueue();
        var pending = queue.ReadFrameAsync(new float[FrameSamples], TestContext.Current.CancellationToken).AsTask();
        Assert.False(pending.IsCompleted);

        queue.Complete();

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(result.HasFrame);
    }

    [Fact]
    public async Task PooledBuffers_NeverLeakSamplesBetweenFrames()
    {
        var queue = NewQueue(capacity: 3);
        var ct = TestContext.Current.CancellationToken;
        var dest = new float[FrameSamples];
        var source = new float[FrameSamples];
        var rng = new Random(1234);
        long lastSequence = -1;

        // Mixed writes and reads with frequent overruns, so buffers cycle through the pool, the channel and the drop path.
        for (int round = 0; round < 200; round++)
        {
            int writes = rng.Next(1, 7);
            for (int w = 0; w < writes; w++)
            {
                long seq = round * 10 + w;
                for (int i = 0; i < FrameSamples; i++)
                {
                    source[i] = seq + i / 100f; // every sample identifies its frame and position
                }

                queue.TryWrite(source, seq);
                Array.Fill(source, -1f); // the queue must have copied: later changes to the caller's buffer are invisible
            }

            int reads = rng.Next(0, 4);
            for (int r = 0; r < reads && queue.GetStatistics().Depth > 0; r++)
            {
                var read = await queue.ReadFrameAsync(dest, ct);
                Assert.True(read.HasFrame);
                Assert.True(read.Info.Sequence > lastSequence);
                lastSequence = read.Info.Sequence;
                long tag = read.Info.CaptureTimestamp;
                for (int i = 0; i < FrameSamples; i++)
                {
                    Assert.Equal(tag + i / 100f, dest[i]);
                }
            }
        }
    }

    [Fact]
    public async Task ReadFrameAsync_IntoALargerDestination_WritesExactlyOneFrame()
    {
        var queue = NewQueue();
        queue.TryWrite(Frame(7), 0);
        var dest = new float[FrameSamples + 4];
        Array.Fill(dest, 42f);

        var r = await queue.ReadFrameAsync(dest, TestContext.Current.CancellationToken);

        Assert.True(r.HasFrame);
        Assert.All(dest.Take(FrameSamples), s => Assert.Equal(7f, s));
        Assert.All(dest.Skip(FrameSamples), s => Assert.Equal(42f, s));
    }

    [Fact]
    public async Task InvalidSizes_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioFrameQueue(AudioStreamFormat.Speech16kMono, 0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioFrameQueue(AudioStreamFormat.Speech16kMono, 8, 1));

        var queue = NewQueue();
        Assert.Throws<ArgumentException>(() => queue.TryWrite(new float[FrameSamples - 1], 0));
        Assert.Throws<ArgumentException>(() => queue.TryWrite(new float[FrameSamples + 1], 0));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await queue.ReadFrameAsync(new float[FrameSamples - 1], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentProducerAndSlowConsumer_CountersAndDiscontinuitiesStayConsistent()
    {
        const int total = 5000;
        var queue = new AudioFrameQueue(AudioStreamFormat.Speech16kMono, FrameSamples, 16);
        var ct = TestContext.Current.CancellationToken;
        var producer = Task.Run(() =>
        {
            var f = new float[FrameSamples];
            for (int i = 0; i < total; i++)
            {
                Array.Fill(f, i);
                queue.TryWrite(f, i);
                if (i % 64 == 0)
                {
                    Thread.Yield();
                }
            }

            queue.Complete();
        }, ct);

        var dest = new float[FrameSamples];
        long reads = 0, reportedDrops = 0, previous = -1;
        while (true)
        {
            var r = await queue.ReadFrameAsync(dest, ct);
            if (!r.HasFrame)
            {
                break;
            }

            reads++;
            Assert.All(dest, s => Assert.Equal(r.Info.Sequence, s));
            long gap = r.Info.Sequence - previous - 1;
            Assert.Equal(gap, r.Info.DroppedFramesBefore);
            Assert.Equal(gap > 0, r.Info.Discontinuity);
            reportedDrops += r.Info.DroppedFramesBefore;
            previous = r.Info.Sequence;
            if (reads % 7 == 0)
            {
                await Task.Yield();
            }
        }

        await producer;
        var stats = queue.GetStatistics();
        Assert.Equal(total, stats.FramesWritten);
        Assert.Equal(reads, stats.FramesRead);
        Assert.Equal(total, stats.FramesRead + stats.FramesDropped);
        // The newest frame is never the one dropped, so every drop is reported by a later frame.
        Assert.Equal(total - 1, previous);
        Assert.Equal(stats.FramesDropped, reportedDrops);
        // Depth counts frames written but not yet read; a frame the consumer is still copying out counts until the copy
        // is done, so under concurrency the high-water mark can exceed the channel capacity by that one frame.
        Assert.InRange(stats.MaxDepth, 1, 16 + 1);
    }
}
