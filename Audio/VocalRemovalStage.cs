using System;

namespace PitchPerfect.Audio;

/// <summary>
/// Stateless, UI-agnostic audio stage that performs "vocal removal" (a.k.a. the
/// karaoke / instrumental-only effect) using the classic <c>(L − R)</c> phase
/// cancellation method.
/// </summary>
/// <remarks>
/// <para>
/// In most commercial recordings the lead vocal is mixed centered (identical in
/// the left and right channels, i.e. it is the mono component), while the
/// accompaniment/instruments are stereo (the difference between channels). By
/// computing <c>mono = (L − R) / 2</c> and writing that value to BOTH the left
/// and right channels, the centered vocal is cancelled while the stereo
/// accompaniment is preserved.
/// </para>
/// <para>
/// This is the industry-standard, zero-dependency approximation. Its quality is
/// inherently limited: any vocal panned slightly off-center, or any instrument
/// present in the mono component, will not be fully removed (or will be
/// partially lost). That behavior is expected and is NOT a bug.
/// </para>
/// <para>
/// The method REQUIRES a stereo signal. On a mono source <c>(L − R)</c> is always
/// zero, which would silence the entire output. Callers must therefore only
/// invoke this stage on stereo input (or rely on the mono guard inside
/// <see cref="Process"/>, which silently no-ops). Use <see cref="IsApplicable"/>
/// to decide whether to offer the feature to the user.
/// </para>
/// </remarks>
public static class VocalRemovalStage
{
    /// <summary>
    /// Determines whether vocal removal is applicable to the given channel count.
    /// The <c>(L − R)</c> technique is only meaningful for stereo sources; for a
    /// mono source it would cancel the whole signal, so it must be skipped.
    /// </summary>
    /// <param name="channels">The number of interleaved channels in the signal.</param>
    /// <returns><c>true</c> when <paramref name="channels"/> equals 2; otherwise <c>false</c>.</returns>
    public static bool IsApplicable(int channels) => channels == 2;

    /// <summary>
    /// Processes interleaved float samples in place, suppressing the centered
    /// (mono) component and keeping the stereo difference (the backing track).
    /// </summary>
    /// <param name="samples">The interleaved float sample buffer (L,R,L,R,...). Mutated in place.</param>
    /// <param name="channels">The number of channels. Only <c>2</c> is processed.</param>
    /// <remarks>
    /// For every stereo frame: <c>diff = (L − R) / 2</c>, then
    /// <c>L' = diff</c> and <c>R' = diff</c>. The <c>/ 2</c> keeps the resulting
    /// level within the <c>[-1, 1]</c> range (since each of L,R is already in that
    /// range, their difference is within <c>[-2, 2]</c>). Non-stereo inputs are
    /// left untouched (silent no-op).
    /// </remarks>
    public static void Process(float[] samples, int channels)
    {
        if (samples == null || samples.Length == 0) return;
        if (channels != 2) return; // Mono (or >2): nothing safe to do — silently skip.

        int frameCount = samples.Length / 2;
        for (int i = 0; i < frameCount; i++)
        {
            float left = samples[i * 2];
            float right = samples[i * 2 + 1];
            float diff = (left - right) * 0.5f;
            samples[i * 2] = diff;
            samples[i * 2 + 1] = diff;
        }
    }
}
