using System.Collections.Generic;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Makes a strip of scenery endless for the world scroll (<see cref="GameManager.worldScroll"/>). The strip is
    /// one period of scenery, built once around <see cref="HomeX"/>; every frame it moves a whole number of periods
    /// so that it stays under the camera. Scenery that is periodic — the same at x and at x + <see cref="Period"/> —
    /// looks exactly the same before a jump and after it, so the camera never meets an edge, a seam or a pop,
    /// however far it goes.
    ///
    /// Two ways for a strip to be periodic:
    ///   • Copies (<c>copies: true</c>): each child of the strip holds one period of scenery, and two copies of it
    ///     are made, a period to either side, so three periods lie side by side. A copy draws exactly what its
    ///     original does, whatever the shader reads (world position, object position or vertex data), and the
    ///     scenery at any one place in the world is the same before a jump and after it. Children named in
    ///     <c>single</c> are left alone: wide, even pieces (a glow along the horizon, the water) that already span
    ///     every copy, and would be drawn three times over if copied.
    ///   • Self-periodic (<c>copies: false</c>): the children already repeat with the period across a span wide
    ///     enough for the view at every jump — a texture that wraps, a row of towers dealt by their place in the
    ///     period — so the strip only jumps.
    ///
    /// Only X is moved, and the jump is decided from the camera's rest X (<see cref="CameraRig.RestX"/>), never its
    /// shaken or swung pose: a shake must not jump the strip back and forth. Everything is made at Start, so the
    /// scroll itself allocates nothing.
    /// </summary>
    [DefaultExecutionOrder(-40)]   // after CameraRig (-50) has set this frame's rest X, before anything draws
    public sealed class WorldRepeat : MonoBehaviour
    {
        /// <summary>The length that repeats, world units.</summary>
        public float Period { get; private set; }

        /// <summary>The world X the strip's middle stood at when it was built.</summary>
        public float HomeX { get; private set; }

        /// <summary>Whole periods the strip has moved from home (positive: to the right).</summary>
        public int Jumps { get; private set; }

        /// <summary>The world X of the strip's middle now.</summary>
        public float CentreX => HomeX + Jumps * Period;

        float _rootX;   // the strip root's own X at home

        /// <summary>
        /// Makes <paramref name="root"/> an endless strip of period <paramref name="period"/>, its middle at
        /// <paramref name="homeX"/> as built. With <paramref name="copies"/>, every child not in
        /// <paramref name="single"/> gets two copies a period to either side (see the class summary). Call once,
        /// after everything in the strip is built; the strip moves under the camera at once.
        /// </summary>
        public static WorldRepeat Attach(GameObject root, float period, float homeX, bool copies,
                                         ICollection<GameObject> single = null)
        {
            var strip = root.GetComponent<WorldRepeat>();
            if (strip == null) strip = root.AddComponent<WorldRepeat>();
            strip.Period = Mathf.Max(1f, period);
            strip.HomeX  = homeX;
            strip._rootX = root.transform.position.x;
            if (copies) strip.MakeCopies(single);
            strip.Follow();
            return strip;
        }

        void MakeCopies(ICollection<GameObject> single)
        {
            // The originals first: the copies are made under the same root and must not be copied again.
            var originals = new List<Transform>(transform.childCount);
            for (int i = 0; i < transform.childCount; i++) originals.Add(transform.GetChild(i));

            // The root is never scaled or turned (the builders leave it at identity), so a period in the world is
            // a period along its local X.
            foreach (Transform child in originals)
            {
                if (single != null && single.Contains(child.gameObject)) continue;
                for (int side = -1; side <= 1; side += 2)
                {
                    GameObject copy = Instantiate(child.gameObject, transform);
                    copy.name = child.name + (side < 0 ? "_Left" : "_Right");
                    Transform t = copy.transform;
                    t.localPosition = child.localPosition + new Vector3(side * Period, 0f, 0f);
                    t.localRotation = child.localRotation;
                    t.localScale    = child.localScale;

                    // A copied particle system does not take its original's playing over (the city's sparks are
                    // started by hand, not on awake): start each one its original is playing. Same hierarchy, so the
                    // two lists line up.
                    ParticleSystem[] from = child.GetComponentsInChildren<ParticleSystem>(true);
                    ParticleSystem[] to   = copy.GetComponentsInChildren<ParticleSystem>(true);
                    for (int i = 0; i < from.Length && i < to.Length; i++)
                        if (from[i].isPlaying && !to[i].isPlaying) to[i].Play(false);
                }
            }
        }

        void LateUpdate() => Follow();

        // Jumps the strip, if the camera has crossed into another period, so that the camera is within half a
        // period of the strip's middle. Any number of periods at once: a pan that crosses several lands right.
        void Follow()
        {
            CameraRig rig = CameraRig.Instance;
            float camX;
            if (rig != null) camX = rig.RestX;
            else if (Camera.main != null) camX = Camera.main.transform.position.x;
            else return;

            int jumps = Mathf.RoundToInt((camX - HomeX) / Period);
            if (jumps == Jumps && _placed) return;
            Jumps   = jumps;
            _placed = true;

            Vector3 p = transform.position;
            p.x = _rootX + jumps * Period;
            transform.position = p;
        }

        bool _placed;
    }
}
