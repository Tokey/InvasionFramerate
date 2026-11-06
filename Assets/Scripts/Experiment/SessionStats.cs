using System.Collections.Generic;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Accumulates a block's performance, exploration, movement and frame-delivery metrics so the
    /// session log can be written as a single row when the QUEST+ run ends.
    ///
    /// Fed by <see cref="AddTick"/> (once per rendered frame) and <see cref="AddShot"/> (once per
    /// round); the read-only properties derive the averages and spreads on demand.
    ///
    /// The frame-delivery figures are the ones to check first. The stimulus in this study IS the
    /// frame rate, so a block whose base side measured 340 fps against a 500 fps cap did not present
    /// the differences QUEST+ thinks it did — the threshold it produced is not interpretable.
    /// </summary>
    public class SessionStats
    {
        // ── Rounds ───────────────────────────────────────────────────────────
        /// <summary>Responses logged: shots plus rounds that ran out.</summary>
        public int   Trials               { get; private set; }
        public int   ShotsFired           { get; private set; }
        public int   Hits                 { get; private set; }
        public int   RoundsExpired        { get; private set; }
        public int   RoundsCrossingCapped { get; private set; }
        public float Score                { get; private set; }
        public float Accuracy => Trials > 0 ? (float)Hits / Trials : 0f;

        /// <summary>Shockwave presses and run-outs, and presses that fired nothing, this block.</summary>
        public int   Detections { get; private set; }
        public int   EarlyFires { get; private set; }
        public int   LateFires  { get; private set; }
        public int   Timeouts   { get; private set; }
        public int   LockoutPresses  { get; private set; }
        public int   CooldownPresses { get; private set; }
        public int   NotLivePresses  { get; private set; }
        /// <summary>Rounds that ended without reaching QUEST+: a shot fired before the frame rate had changed.</summary>
        public int   RoundsNotCounted { get; private set; }

        // Hits' times from the last change of frame rate to the shot.
        readonly List<float> _reaction = new List<float>(128);
        public float AvgReactionSec    => Mean(_reaction);
        public float SdReactionSec     => StdDev(_reaction);
        public float MedianReactionSec => Median(_reaction);
        public float MinReactionSec    => _reaction.Count > 0 ? Mathf.Min(_reaction.ToArray()) : float.NaN;
        public float MaxReactionSec    => _reaction.Count > 0 ? Mathf.Max(_reaction.ToArray()) : float.NaN;
        public float ShotsPerMinute    => ElapsedSeconds > 1f ? ShotsFired * 60f / ElapsedSeconds : float.NaN;

        /// <summary>Δ of the last round logged, in frame time (ms) and in frame rate (fps).</summary>
        public float LastDiffMs  { get; private set; } = float.NaN;
        public float LastDiffFps { get; private set; } = float.NaN;

        readonly List<float> _roundDurations = new List<float>(128);
        public float AvgRoundDurationSec => Mean(_roundDurations);

        // ── Exploration ──────────────────────────────────────────────────────
        readonly List<int>   _crossings      = new List<int>(128);
        readonly List<float> _sinceCrossing  = new List<float>(128);

        /// <summary>Mean tower crossings per round — how much comparing the participant did.</summary>
        public float AvgCrossingsPerRound => MeanInt(_crossings);
        /// <summary>Shots fired without a single crossing that round: the participant only ever saw
        /// one side's frame rate, so the shot had nothing to compare.</summary>
        public int   ShotsWithoutCrossing { get; private set; }
        /// <summary>Mean seconds from the last crossing to the shot.</summary>
        public float AvgSinceLastCrossingSec => Mean(_sinceCrossing);
        /// <summary>Median of the same — the latency is skewed by shots that waited for a later pass.</summary>
        public float MedianSinceLastCrossingSec => Median(_sinceCrossing);

        readonly List<float> _firstCrossing = new List<float>(128);
        /// <summary>Mean seconds from a round arming to its first crossing, over rounds with one.</summary>
        public float AvgFirstCrossingSec => Mean(_firstCrossing);

        double _crossSpeedSum;
        int    _crossSpeedN;
        /// <summary>Mean horizontal speed of the UFO at the block's crossings (world units a second): how fast the
        /// boundary was crossed. The autopilot's speed in an auto level; the participant's own otherwise.</summary>
        public float AvgCrossingSpeed => _crossSpeedN > 0 ? (float)(_crossSpeedSum / _crossSpeedN) : float.NaN;

        readonly List<float> _shotSpeed  = new List<float>(128);
        readonly List<float> _missAlong  = new List<float>(128);
        /// <summary>Mean abs(ufoVelX) at the shots.</summary>
        public float AvgShotUfoSpeed => Mean(_shotSpeed);
        /// <summary>Mean of missDistX × sign(ufoVelX) over shots fired on the move: positive = the shot landed
        /// past the tower in the direction of travel (late), negative = before reaching it (early).</summary>
        public float AvgMissAlongTravelX => Mean(_missAlong);

        /// <summary>The UFO's horizontal speed at one crossing — fed by the director on the crossing's frame.</summary>
        public void AddCrossingSpeed(float speed)
        {
            if (float.IsNaN(speed) || float.IsInfinity(speed)) return;
            _crossSpeedSum += speed;
            _crossSpeedN++;
        }

        // ── Shot geometry (world units on X) ─────────────────────────────────
        readonly List<float> _missAbs = new List<float>(128);
        float _missAbsMissesSum;
        int   _missesWithGeometry;

        public float AvgMissDistX           => Mean(_missAbs);
        public float AvgMissDistXMissesOnly => _missesWithGeometry > 0 ? _missAbsMissesSum / _missesWithGeometry : float.NaN;
        public float MedianMissDistX        => Median(_missAbs);
        public float SdMissDistX            => StdDev(_missAbs);
        public float MaxMissDistX           => _missAbs.Count > 0 ? _maxMiss : float.NaN;
        float _maxMiss;

        // ── Movement ─────────────────────────────────────────────────────────
        public float TotalMousePathPx       { get; private set; }
        public float PeakMouseSpeedPxPerSec { get; private set; }
        public float TotalUfoPathWorld      { get; private set; }
        public float ElapsedSeconds         { get; private set; }
        public float AvgMouseSpeedPxPerSec  => ElapsedSeconds > 0.001f ? TotalMousePathPx / ElapsedSeconds : 0f;

        // ── Frame delivery ───────────────────────────────────────────────────
        /// <summary>Every frame of the phase, rounds and the gaps between them alike.</summary>
        public int   FrameCount  { get; private set; }
        /// <summary>Frames paced by one side's cap while a round's split was in force.</summary>
        public int   LiveFrames  => _baseErr.N + _alteredErr.N;
        public int   BaseFrames    => _baseErr.N;
        public int   AlteredFrames => _alteredErr.N;

        /// <summary>Frame rate actually delivered on the base side: frames over frame time. The base
        /// cap is the same every round, so this compares straight against baseFps.</summary>
        public float BaseSideMeasuredFps => _baseSec > 0.0 ? (float)(_baseErr.N / _baseSec) : float.NaN;

        /// <summary>Frame-to-frame jitter on the base side: SD of (frame time − cap period), ms.</summary>
        public float BaseFrameTimeSdMs => _baseErr.Sd;

        /// <summary>
        /// Mean of (frame time − cap period) / cap period on the altered side, in percent. Positive
        /// = frames ran long, i.e. the altered side was delivered slower than its cap. Relative
        /// rather than a frame rate because the altered cap changes every round.
        /// </summary>
        public float AlteredFrameTimeErrPct => _alteredRel.N > 0 ? 100f * _alteredRel.Mean : float.NaN;
        /// <summary>Frame-to-frame jitter on the altered side: SD of (frame time − cap period), ms.</summary>
        public float AlteredFrameTimeSdMs   => _alteredErr.Sd;

        /// <summary>Live frames that took more than 1.5× their cap's period — hitches.</summary>
        public int   SlowFrames          { get; private set; }
        public float MaxLiveFrameTimeMs  { get; private set; }

        double  _baseSec;
        Welford _baseErr, _alteredErr, _alteredRel;

        // Frame-time histograms of the live frames, 0.05 ms a bin up to 200 ms, for the percentiles without keeping frames.
        const float HistBinMs = 0.05f;
        readonly int[] _histAll = new int[4000], _histBase = new int[4000], _histAltered = new int[4000];
        public float P95FrameTimeMs        => Percentile(_histAll, 0.95f);
        public float P99FrameTimeMs        => Percentile(_histAll, 0.99f);
        public float BaseP99FrameTimeMs    => Percentile(_histBase, 0.99f);
        public float AlteredP99FrameTimeMs => Percentile(_histAltered, 0.99f);

        static float Percentile(int[] h, float q)
        {
            long n = 0;
            for (int i = 0; i < h.Length; i++) n += h[i];
            if (n == 0) return float.NaN;
            long want = (long)System.Math.Ceiling(q * n), seen = 0;
            for (int i = 0; i < h.Length; i++)
            {
                seen += h[i];
                if (seen >= want) return (i + 0.5f) * HistBinMs;
            }
            return h.Length * HistBinMs;
        }

        // ── Feed ─────────────────────────────────────────────────────────────

        public void AddTick(in TickSample t)
        {
            FrameCount++;
            ElapsedSeconds = t.timeSinceStartSec;

            float dtMs = t.unscaledDeltaMs;

            float px = t.mouseDelta.magnitude;
            TotalMousePathPx += px;
            if (dtMs > 0.001f)
            {
                float speed = px / (dtMs * 0.001f);
                if (speed > PeakMouseSpeedPxPerSec) PeakMouseSpeedPxPerSec = speed;
            }

            if (t.pacedBy == PerturbationController.Pace.None || t.targetFps <= 0) return;

            float periodMs = 1000f / t.targetFps;
            float err      = dtMs - periodMs;

            if (dtMs > MaxLiveFrameTimeMs) MaxLiveFrameTimeMs = dtMs;
            if (dtMs > 1.5f * periodMs)    SlowFrames++;

            int bin = Mathf.Clamp((int)(dtMs / HistBinMs), 0, _histAll.Length - 1);
            _histAll[bin]++;
            if (t.pacedBy == PerturbationController.Pace.Base) _histBase[bin]++;
            else                                               _histAltered[bin]++;

            if (t.pacedBy == PerturbationController.Pace.Base)
            {
                _baseErr.Add(err);
                _baseSec += dtMs * 0.001;
            }
            else
            {
                _alteredErr.Add(err);
                _alteredRel.Add(err / periodMs);
            }
        }

        /// <summary>The UFO's world displacement this frame — read from its transform, not the mouse.</summary>
        public void AddUfoDisplacement(float worldUnits) => TotalUfoPathWorld += worldUnits;

        public void AddShot(in ShotSample s)
        {
            switch (s.outcome)
            {
                // A press that fired nothing changes nothing; it is only counted.
                case TrialOutcome.PressLockout:  LockoutPresses++;  return;
                case TrialOutcome.PressCooldown: CooldownPresses++; return;
                case TrialOutcome.PressNotLive:  NotLivePresses++;  return;
                case TrialOutcome.Detected: Detections++; break;
                case TrialOutcome.Early:    EarlyFires++; break;
                case TrialOutcome.Late:     LateFires++;  break;
                case TrialOutcome.Timeout:  Timeouts++;   break;
            }

            if (s.playerFired) ShotsFired++;
            if (s.isHit) Hits++;
            Score       = s.totalScore;
            LastDiffMs  = s.diffMs;
            LastDiffFps = s.diffFps;

            if (s.isHit && !float.IsNaN(s.sinceLastCrossingSec)) _reaction.Add(s.sinceLastCrossingSec);

            if (s.roundEnded)
            {
                Trials++;
                if (s.playerFired && s.crossings == 0) ShotsWithoutCrossing++;
                if (!float.IsNaN(s.sinceLastCrossingSec)) _sinceCrossing.Add(s.sinceLastCrossingSec);
                if (!s.countedByStaircase) RoundsNotCounted++;
                if (s.outcome == TrialOutcome.Expired || s.outcome == TrialOutcome.Timeout) RoundsExpired++;
                if (s.outcome == TrialOutcome.CrossingCap) RoundsCrossingCapped++;
                if (!float.IsNaN(s.roundDurationSec)) _roundDurations.Add(s.roundDurationSec);
                _crossings.Add(Mathf.Max(0, s.crossings));
                if (!float.IsNaN(s.firstCrossingAtSec) && !float.IsNaN(s.trialStartSec))
                    _firstCrossing.Add(s.firstCrossingAtSec - s.trialStartSec);
            }

            if (s.playerFired && !float.IsNaN(s.ufoVelX))
            {
                _shotSpeed.Add(Mathf.Abs(s.ufoVelX));
                // Below half a unit a second the ship is as good as still, and the sign says nothing.
                if (Mathf.Abs(s.ufoVelX) > 0.5f && !float.IsNaN(s.missDistX))
                    _missAlong.Add(s.missDistX * Mathf.Sign(s.ufoVelX));
            }

            // A round that ran out has no landing point; folding it in as zero would read as a
            // perfectly centred shot and drag every miss average toward the tower.
            if (float.IsNaN(s.missDistX)) return;

            float absX = Mathf.Abs(s.missDistX);
            _missAbs.Add(absX);
            if (absX > _maxMiss) _maxMiss = absX;
            if (!s.isHit) { _missAbsMissesSum += absX; _missesWithGeometry++; }
        }

        // ── Small stats helpers ──────────────────────────────────────────────

        /// <summary>Running mean and SD in one pass, without keeping every frame.</summary>
        struct Welford
        {
            public int N;
            double _mean, _m2;

            public void Add(double x)
            {
                N++;
                double d = x - _mean;
                _mean += d / N;
                _m2   += d * (x - _mean);
            }

            public float Mean => N > 0 ? (float)_mean : float.NaN;
            public float Sd   => N > 1 ? (float)System.Math.Sqrt(_m2 / (N - 1)) : float.NaN;
        }

        static float Mean(List<float> v)
        {
            if (v.Count == 0) return float.NaN;
            double sum = 0.0;
            for (int i = 0; i < v.Count; i++) sum += v[i];
            return (float)(sum / v.Count);
        }

        static float MeanInt(List<int> v)
        {
            if (v.Count == 0) return float.NaN;
            long sum = 0;
            for (int i = 0; i < v.Count; i++) sum += v[i];
            return (float)((double)sum / v.Count);
        }

        static float StdDev(List<float> v)
        {
            if (v.Count < 2) return float.NaN;
            float m = Mean(v);
            double sum = 0.0;
            for (int i = 0; i < v.Count; i++) { double d = v[i] - m; sum += d * d; }
            return Mathf.Sqrt((float)(sum / (v.Count - 1)));   // sample SD
        }

        static float Median(List<float> v)
        {
            if (v.Count == 0) return float.NaN;
            float[] a = v.ToArray();
            System.Array.Sort(a);
            int mid = a.Length / 2;
            return a.Length % 2 == 1 ? a[mid] : 0.5f * (a[mid - 1] + a[mid]);
        }
    }
}
