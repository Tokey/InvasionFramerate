using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Computes the shockwave block's chance-level hit rate <c>guessRate</c> (γ) from the
    /// timing parameters in <see cref="StudyConfig"/>:
    ///
    ///     γ = swWindowSec / (swFlipDelayMaxSec − swFlipDelayMinSec)
    ///
    /// For the default timing (<c>swWindowSec = 0.9</c>, <c>swFlipDelayMinSec = 1.0</c>,
    /// <c>swFlipDelayMaxSec = 3.5</c>), <c>γ = 0.9 / 2.5 = 0.3600</c>.
    /// </summary>
    public static class ShockwaveGuessRate
    {
        public static float Compute(StudyConfig config, out string detail)
        {
            if (config == null)
            {
                detail = "no config";
                return float.NaN;
            }

            float dMin   = Mathf.Max(0f, config.swFlipDelayMinSec);
            float dMax   = Mathf.Max(dMin, config.swFlipDelayMaxSec);
            float w      = Mathf.Max(0f, config.swWindowSec);
            float spread = dMax - dMin;

            if (w <= 0f)
            {
                detail = "swWindowSec <= 0";
                return 0f;
            }

            float gamma = spread > 1e-4f ? Mathf.Clamp01(w / spread) : 1f;
            detail = $"window={w:0.##}s / spread=({dMax:0.##}-{dMin:0.##})={spread:0.##}s";
            return gamma;
        }
    }
}
