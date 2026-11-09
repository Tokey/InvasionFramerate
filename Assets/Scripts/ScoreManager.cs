using System;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Scores each shot by horizontal distance from the tower base (or by shockwave timing window):
    /// full points for a hit, then a fixed miss penalty for a miss. A round that ran out with no
    /// shot scores as a miss; a forgiven early shockwave press leaves the score and tally untouched.
    /// </summary>
    public class ScoreManager : MonoBehaviour
    {
        [Header("Scoring Curve")]
        [Tooltip("Score awarded for a hit. Also defines a hit: isHit is shotScore >= hitPoints.")]
        [SerializeField, Min(0f)] float hitPoints = 100f;
        [SerializeField, Min(0f)] float closeRadius = 1f;
        [SerializeField] float missPoints = -10f;

        public float TotalScore       { get; private set; }
        public float LastShotScore    { get; private set; }
        public float LastShotDistance { get; private set; }
        public float HitPoints        => hitPoints;

        /// <summary>
        /// Responses this phase that landed, and responses that did not — the HUD's tally. Kept here
        /// rather than derived from the score, because the score is weighted and two different
        /// tallies can produce the same total. Practice and the main run are counted separately:
        /// ExperimentDirector calls <see cref="ResetScore"/> between them.
        /// </summary>
        public int Hits   { get; private set; }
        public int Misses { get; private set; }

        /// <summary>True for a hit, false for a miss — whichever the last scored response was.</summary>
        public bool LastWasHit { get; private set; }

        /// <summary>How the last scored round ended — a shot, or running out. The tally colours a
        /// ran-out round apart from a shot that missed.</summary>
        public ResponseOutcome LastOutcome { get; private set; }

        /// <summary>(lastShotScore, totalScore, distance)</summary>
        public event Action<float, float, float> OnScored;

        public void SetScoreRadius(float newCloseRadius)
        {
            closeRadius = Mathf.Max(0f, newCloseRadius);
        }

        /// <summary>Applies the scoring columns of the study config (see StudyConfig).</summary>
        public void SetScoring(float newHitPoints, float newMissPoints)
        {
            hitPoints  = Mathf.Max(0f, newHitPoints);
            missPoints = newMissPoints;
        }

        public float ScoreShot(Vector3 hitPoint, Vector3 towerBase)
        {
            Vector3 a = new Vector3(hitPoint.x, 0f, hitPoint.z);
            Vector3 b = new Vector3(towerBase.x, 0f, towerBase.z);
            float dist = Vector3.Distance(a, b);
            LastShotDistance = dist;

            bool  hit = dist <= closeRadius;
            float pts = hit ? hitPoints : missPoints;

            LastShotScore = pts;
            TotalScore   += pts;
            LastOutcome   = ResponseOutcome.Shot;
            Tally(hit);
            OnScored?.Invoke(LastShotScore, TotalScore, dist);
            return pts;
        }

        /// <summary>
        /// Scores a shockwave or ran-out outcome. When <paramref name="countInTally"/> is false
        /// (a laser shot before any crossing), neither the score nor the HUD pip tally is changed.
        /// </summary>
        public float ScoreOutcome(ResponseOutcome outcome, bool isHit = false, bool countInTally = true)
        {
            LastShotDistance = float.NaN;
            if (!countInTally)
            {
                LastShotScore = 0f;
                return 0f;
            }

            float pts = isHit ? hitPoints : missPoints;
            LastShotScore = pts;
            TotalScore   += pts;
            LastOutcome   = outcome;
            Tally(isHit);
            OnScored?.Invoke(LastShotScore, TotalScore, float.NaN);
            return pts;
        }

        void Tally(bool hit)
        {
            LastWasHit = hit;
            if (hit) Hits++;
            else     Misses++;
        }

        public void ResetScore()
        {
            TotalScore       = 0f;
            LastShotScore    = 0f;
            LastShotDistance = 0f;
            Hits             = 0;
            Misses           = 0;
            LastWasHit       = false;
            LastOutcome      = ResponseOutcome.Shot;
            OnScored?.Invoke(0f, 0f, 0f);
        }
    }
}
