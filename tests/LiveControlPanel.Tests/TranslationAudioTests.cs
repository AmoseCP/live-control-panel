using LiveControlPanel.Translate;
using Xunit;

namespace LiveControlPanel.Tests;

/// <summary>
/// The two pieces of audio arithmetic that sit in the hot path. Both are pure so they can be pinned
/// here rather than guessed at through a sound card.
/// </summary>
public sealed class TranslationAudioTests
{
    private static byte[] Pcm(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            bytes[i * 2] = (byte)(samples[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)((samples[i] >> 8) & 0xFF);
        }
        return bytes;
    }

    [Fact]
    public void Silence_reads_as_zero() => Assert.Equal(0d, AudioLevel.Peak(Pcm(0, 0, 0)));

    [Fact]
    public void Full_scale_reads_as_one() => Assert.Equal(1.0, AudioLevel.Peak(Pcm(short.MaxValue)), 3);

    [Fact]
    public void The_peak_is_the_loudest_sample_regardless_of_sign() =>
        Assert.Equal(AudioLevel.Peak(Pcm(16384)), AudioLevel.Peak(Pcm(100, -16384, 50)), 6);

    /// <summary>
    /// short.MinValue has no positive counterpart, so negating it overflows back to itself and a
    /// naive Math.Abs produces a negative peak — which would read as "silent" and send an operator
    /// looking at a mixer that is working.
    /// </summary>
    [Fact]
    public void The_most_negative_sample_does_not_overflow_into_a_negative_peak()
    {
        var peak = AudioLevel.Peak(Pcm(short.MinValue));

        Assert.True(peak > 0.99, $"peak was {peak}");
        Assert.True(peak <= 1.0);
    }

    [Fact]
    public void A_trailing_odd_byte_is_ignored_rather_than_read_past_the_end() =>
        Assert.Equal(0d, AudioLevel.Peak(new byte[] { 0x00 }));

    [Fact]
    public void An_empty_block_reads_as_zero() => Assert.Equal(0d, AudioLevel.Peak(Array.Empty<byte>()));

    // ---------------------------------------------------------------- re-framing

    [Fact]
    public void Frames_are_emitted_only_when_complete()
    {
        var splitter = new PcmFrameSplitter(4);
        var frames = new List<byte[]>();

        splitter.Add(new byte[] { 1, 2, 3 }, frames.Add);

        Assert.Empty(frames);
        Assert.Equal(3, splitter.Pending);

        splitter.Add(new byte[] { 4 }, frames.Add);

        Assert.Single(frames);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, frames[0]);
        Assert.Equal(0, splitter.Pending);
    }

    /// <summary>
    /// WASAPI delivers whatever its period produced — this is the case that used to lose the tail of
    /// every block and turn continuous speech into clipped words.
    /// </summary>
    [Fact]
    public void A_large_block_yields_whole_frames_and_carries_the_remainder()
    {
        var splitter = new PcmFrameSplitter(4);
        var frames = new List<byte[]>();

        splitter.Add(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, frames.Add);

        Assert.Equal(2, frames.Count);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, frames[0]);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, frames[1]);
        Assert.Equal(1, splitter.Pending);
    }

    [Fact]
    public void Nothing_is_dropped_across_many_ragged_blocks()
    {
        var splitter = new PcmFrameSplitter(10);
        var frames = new List<byte[]>();

        var next = 0;
        foreach (var size in new[] { 3, 7, 1, 21, 4, 14 })
        {
            var block = new byte[size];
            for (var i = 0; i < size; i++) block[i] = (byte)(next++ % 251);
            splitter.Add(block, frames.Add);
        }

        var emitted = frames.SelectMany(f => f).ToArray();

        Assert.Equal(50 / 10, frames.Count);
        Assert.Equal(0, splitter.Pending);
        for (var i = 0; i < emitted.Length; i++) Assert.Equal((byte)(i % 251), emitted[i]);
    }

    /// <summary>
    /// Each frame is queued for the socket well after the callback returns, so they must not share
    /// the splitter's working buffer.
    /// </summary>
    [Fact]
    public void Each_frame_is_its_own_buffer()
    {
        var splitter = new PcmFrameSplitter(2);
        var frames = new List<byte[]>();

        splitter.Add(new byte[] { 1, 1, 2, 2 }, frames.Add);

        Assert.Equal(new byte[] { 1, 1 }, frames[0]);
        Assert.Equal(new byte[] { 2, 2 }, frames[1]);
        Assert.NotSame(frames[0], frames[1]);
    }

    [Fact]
    public void Reset_discards_a_partial_frame()
    {
        var splitter = new PcmFrameSplitter(4);
        var frames = new List<byte[]>();

        splitter.Add(new byte[] { 1, 2 }, frames.Add);
        splitter.Reset();
        splitter.Add(new byte[] { 3, 4, 5, 6 }, frames.Add);

        Assert.Single(frames);
        Assert.Equal(new byte[] { 3, 4, 5, 6 }, frames[0]);
    }

    [Fact]
    public void A_frame_size_must_be_positive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcmFrameSplitter(0));
}
