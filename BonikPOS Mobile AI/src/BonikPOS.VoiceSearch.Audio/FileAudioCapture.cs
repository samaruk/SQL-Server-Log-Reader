using System.Diagnostics;
using BonikPOS.VoiceSearch.Core.Audio;

namespace BonikPOS.VoiceSearch.Audio;

/// <summary>
/// <see cref="IMicrophoneCapture"/> that replays recorded audio through the same conversion path as the native
/// captures. Used by integration tests and the offline evaluation harness (tools/BonikPOS.VoiceSearch.Eval).
/// It is not registered in the mobile app.
/// </summary>
public sealed class FileAudioCapture : IMicrophoneCapture
{
    private readonly PcmAudio _audio;
    private readonly bool _realTime;
    private readonly int _callbackFrames;
    private AudioFrameQueue _queue;
    private CancellationTokenSource? _cts;
    private Task? _pump;

    /// <param name="audio">Source audio (any rate; channel 0 is used).</param>
    /// <param name="realTime">Pace callbacks at the audio's real-time rate (true) or as fast as the consumer allows (false).</param>
    /// <param name="callbackMilliseconds">Simulated native callback size.</param>
    /// <param name="trailingSilenceMilliseconds">Silence appended so the end-pointer can close the last utterance.</param>
    public FileAudioCapture(PcmAudio audio, bool realTime, int callbackMilliseconds = 10, int trailingSilenceMilliseconds = 1000)
    {
        var mono = audio.ToMono();
        int pad = audio.SampleRate * trailingSilenceMilliseconds / 1000;
        var padded = new float[mono.Length + pad];
        mono.CopyTo(padded, 0);
        _audio = new PcmAudio(padded, audio.SampleRate, 1);
        _realTime = realTime;
        _callbackFrames = Math.Max(1, audio.SampleRate * callbackMilliseconds / 1000);
        _queue = new AudioFrameQueue(AudioStreamFormat.Speech16kMono, 320, 150);
    }

    public IAudioFrameSource Frames => _queue;

    public MicrophoneCaptureState State { get; private set; } = MicrophoneCaptureState.Stopped;

    public MicrophoneCaptureInfo? ActiveConfiguration { get; private set; }

    public event EventHandler<MicrophoneCaptureStateChangedEventArgs>? StateChanged;

    /// <summary>Completes when the whole file has been pushed into the queue.</summary>
    public Task Completion => _pump ?? Task.CompletedTask;

    public Task StartAsync(MicrophoneCaptureOptions options, CancellationToken cancellationToken)
    {
        if (State == MicrophoneCaptureState.Running)
        {
            throw new InvalidOperationException("Already running.");
        }

        int frameSamples = CaptureProcessor.PipelineSampleRate * options.FrameMilliseconds / 1000;
        // When not pacing in real time the queue must never overflow, so the producer waits for space instead.
        _queue = new AudioFrameQueue(AudioStreamFormat.Speech16kMono, frameSamples, options.QueueCapacityFrames);
        var processor = new CaptureProcessor(_audio.SampleRate, _queue, _callbackFrames);
        ActiveConfiguration = new MicrophoneCaptureInfo(_audio.SampleRate, 1, _callbackFrames, "file", processor.IsResampling);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetState(MicrophoneCaptureState.Running);
        _pump = Task.Run(() => Pump(processor, token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (State == MicrophoneCaptureState.Stopped)
        {
            return;
        }

        SetState(MicrophoneCaptureState.Stopping);
        _cts?.Cancel();
        if (_pump is not null)
        {
            try
            {
                await _pump.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _queue.Complete();
        _cts?.Dispose();
        _cts = null;
        SetState(MicrophoneCaptureState.Stopped);
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private async Task Pump(CaptureProcessor processor, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        var samples = _audio.Samples;
        int position = 0;
        while (position < samples.Length && !token.IsCancellationRequested)
        {
            int n = Math.Min(_callbackFrames, samples.Length - position);
            if (_realTime)
            {
                double due = (double)position / _audio.SampleRate * 1000;
                double wait = due - sw.Elapsed.TotalMilliseconds;
                if (wait > 1)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(wait), token).ConfigureAwait(false);
                }
            }
            else
            {
                // Back-pressure instead of dropping: wait while the queue is nearly full.
                while (_queue.GetStatistics().Depth >= _queue.Capacity - 4 && !token.IsCancellationRequested)
                {
                    await Task.Delay(1, token).ConfigureAwait(false);
                }
            }

            processor.Process(samples.AsSpan(position, n), Stopwatch.GetTimestamp());
            position += n;
        }

        if (!token.IsCancellationRequested)
        {
            _queue.Complete();
        }
    }

    private void SetState(MicrophoneCaptureState state)
    {
        State = state;
        StateChanged?.Invoke(this, new MicrophoneCaptureStateChangedEventArgs(state, null));
    }
}
