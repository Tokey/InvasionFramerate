using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace JndUfo
{
    /// <summary>
    /// Command-line checks of a built game — run them on the lab PC before participants come.
    /// Inert unless the game is started with one of:
    ///
    ///   Invasion.exe -jndBenchmark [folder]
    ///       Plays rounds at every base frame rate in ExperimentConfig.csv — the UFO sweeping back
    ///       and forth across the tower, every round shot and revealed — and times every frame;
    ///       then the fastest block again with the debug readout off, as a participant has it; then
    ///       uncapped, for this PC's ceiling. Answers "does this PC hold 500 fps?": how many frames
    ///       came in late at each rate, and how much of every frame was spare. About 90 seconds.
    ///   Invasion.exe -jndCapture [folder]
    ///       Plays the end-of-round reveal with the tower at a sweep of screen positions, edges
    ///       included, and saves screenshots of each one as it unfolds: the shot, the boundary tower
    ///       lighting up, the frame rates landing, the reset under the veil. First, the camera's
    ///       parallax swing: the UFO held at the left edge, the right edge and the middle; then five
    ///       frames 0.2 s apart with the UFO still, to see the scene's own motion.
    ///
    ///   Invasion.exe -jndSeams [folder]
    ///       Checks the endless world (the world scroll on) for seams: with time stopped and everything that
    ///       moves on the wall clock put still, it photographs the view a hair before and a hair after the
    ///       point where each strip of repeating scenery jumps a period, both ways, and compares the two
    ///       pictures pixel by pixel. A seamless jump changes nothing; the report gives how many pixels
    ///       changed and by how much, and both pictures are saved. Also a few views far along the world.
    ///   Invasion.exe -jndProfile [folder] [-jndWorldScroll]
    ///       What each part of the scene costs: uncapped, the UFO sweeping across the view (or, with
    ///       -jndWorldScroll, flying steadily across the endless world, the camera following), every
    ///       effect is switched off and on again in turn — three times each, interleaved with the scene
    ///       as it is, so a drift in the PC's speed cannot pass for a cost — and the frame time it saves
    ///       is reported, largest first, beside a control that switches nothing (the noise). With
    ///       -jndWorldScroll it also times the camera standing still against scrolling. About four
    ///       minutes. Run it with and without -jndWorldScroll to see what the endless world costs.
    ///
    /// Either can be run with parts of the scene switched off, to see what each costs or how the
    /// scene looks without it: -jndNoBackdrop (the city drawn in focus, by the main camera),
    /// -jndNoForeground (no grass), -jndNoParallax (no camera swing). The benchmark's report lists
    /// the switches it ran with. The world scroll (GameManager.worldScroll) is off for every check
    /// unless a profile is asked for it with -jndWorldScroll: it is settled before the scenery is
    /// built, which decides whether the world is endless.
    ///
    /// Neither is a session. ExperimentDirector stands down — no participant number is taken,
    /// nothing is written under Data/Logs, no prompts — and the rounds are driven from here
    /// through the game's own code: the split, the real laser shot, the real reveal. Results go to
    /// [folder], or Data/QA beside the game. The game quits when it is done. Never active in the
    /// Editor.
    /// </summary>
    public class QaRunner : MonoBehaviour
    {
        enum RunMode { None, Benchmark, Capture, Profile, Seams }

        static bool    s_parsed;
        static RunMode s_mode;
        static string  s_folder;

        /// <summary>True when the game was started for a command-line check instead of a session.</summary>
        public static bool Active
        {
            get
            {
                Parse();
                return s_mode != RunMode.None;
            }
        }

        static void Parse()
        {
            if (s_parsed) return;
            s_parsed = true;
            if (Application.isEditor) return;

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                RunMode m = args[i] == "-jndBenchmark" ? RunMode.Benchmark
                          : args[i] == "-jndCapture"   ? RunMode.Capture
                          : args[i] == "-jndProfile"   ? RunMode.Profile
                          : args[i] == "-jndSeams"     ? RunMode.Seams
                          : RunMode.None;
                if (m == RunMode.None) continue;

                s_mode = m;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                    s_folder = args[i + 1];
                return;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (!Active) return;

            // Every Awake has run here, but no Start: TowerManager builds the scenery in its Start, endless if the
            // world scroll is on by then. So it is settled now — on only for a profile asked to scroll. The other
            // checks place the UFO and the tower by fractions of the view, which the scroll would drag along.
            GameManager game = GameManager.Instance != null ? GameManager.Instance : FindAnyObjectByType<GameManager>();
            if (game != null) game.worldScroll = ScrollWanted;

            var go = new GameObject("QaRunner");
            DontDestroyOnLoad(go);
            go.AddComponent<QaRunner>();
        }

        // Where the UFO flies, as a fraction of the screen's height, and how far either side of the
        // tower each pass goes, as a fraction of its width.
        const float UfoHeight      = 0.6f;
        const float SweepHalfWidth = 0.16f;

        GameManager            _game;
        TowerManager           _tower;
        UfoController          _ufo;
        LaserFirer             _laser;
        PerturbationController _split;
        UIManager              _ui;
        List<StudyConfig>      _blocks;
        string                 _dir;
        float                  _shotAt;
        float                  _queuedVx = float.NaN;   // the spot the coming reset was asked to deal

        IEnumerator Start()
        {
            // A check must not stall when its window loses focus.
            Application.runInBackground = true;

            // Two frames: every Start has run, and the UFO has made its one-time snap to the pointer.
            yield return null;
            yield return null;

            Cursor.visible   = true;
            Cursor.lockState = CursorLockMode.None;

            _game   = GameManager.Instance != null ? GameManager.Instance : FindAnyObjectByType<GameManager>();
            _tower  = FindAnyObjectByType<TowerManager>();
            _ufo    = FindAnyObjectByType<UfoController>();
            _laser  = FindAnyObjectByType<LaserFirer>();
            _split  = FindAnyObjectByType<PerturbationController>();
            _ui     = FindAnyObjectByType<UIManager>();
            _blocks = StudyConfig.LoadAll();

            _dir = !string.IsNullOrEmpty(s_folder)
                ? Path.GetFullPath(s_folder)
                : Path.Combine(ExperimentPaths.Root, "QA");
            Directory.CreateDirectory(_dir);

            if (_game == null || _tower == null || _ufo == null || _laser == null || _split == null ||
                _blocks.Count == 0)
            {
                Debug.LogError("[QaRunner] The scene is missing something the checks need — stopping.");
                Application.Quit(1);
                yield break;
            }

            // The checks place the UFO and the tower by fractions of the view, which the world scroll would
            // drag along after the UFO every frame. Settled in Boot already; this catches a scene that turns it
            // on later.
            if (_game.worldScroll && !ScrollWanted)
            {
                Debug.Log("[QaRunner] World scroll is on in the scene — switched off for the checks.");
                _game.worldScroll = false;
                if (CameraRig.Instance != null) CameraRig.Instance.StopScrollNow();
            }

            // Shots come from here only: no stray click fires one, and with firing off the round
            // clock never arms.
            _laser.SetFiringEnabled(false);
            _ufo.FreezeMovement = true;
            ApplySwitches();

            Debug.Log($"[QaRunner] {s_mode} → {_dir}{(Switches.Length > 0 ? " with" + Switches : "")}");
            if      (s_mode == RunMode.Benchmark) yield return Benchmark();
            else if (s_mode == RunMode.Profile)   yield return Profile();
            else if (s_mode == RunMode.Seams)     yield return Seams();
            else                                  yield return Capture();

            Application.Quit(0);
        }

        // ── Scene switches ───────────────────────────────────────────────────

        static bool HasArg(string arg) => Array.IndexOf(Environment.GetCommandLineArgs(), arg) >= 0;

        // Whether this check runs with the world scroll on: the seam check always, a profile when asked.
        static bool ScrollWanted => s_mode == RunMode.Seams || (s_mode == RunMode.Profile && HasArg("-jndWorldScroll"));

        // The switches given, for the log and the report: " -jndNoBackdrop -jndNoParallax", or "".
        static string Switches
        {
            get
            {
                var sb = new StringBuilder();
                foreach (string a in new[] { "-jndNoBackdrop", "-jndNoForeground", "-jndNoParallax", "-jndWorldScroll" })
                    if (HasArg(a)) sb.Append(' ').Append(a);
                return sb.ToString();
            }
        }

        static void ApplySwitches()
        {
            if (HasArg("-jndNoBackdrop"))
                foreach (Backdrop b in FindObjectsByType<Backdrop>()) b.blur = false;
            if (HasArg("-jndNoForeground"))
                foreach (Foreground f in FindObjectsByType<Foreground>()) f.grass = false;
            if (HasArg("-jndNoParallax") && CameraRig.Instance != null)
                CameraRig.Instance.parallax = false;
        }

        // ── Driving the game ─────────────────────────────────────────────────

        /// <summary>What ExperimentDirector does at the top of every block, minus the session.</summary>
        void BeginBlock(StudyConfig cfg)
        {
            _split.SetPractice(false);
            _split.BeginBlock(cfg);
            _game.hitDisplayDuration = cfg.revealHoldSec;
            _game.readyBeatDuration  = cfg.readyBeatSec;
            _laser.SetCooldown(cfg.fireCooldown);
            ScoreManager score = FindAnyObjectByType<ScoreManager>();
            if (score != null) score.ResetScore();
        }

        /// <summary>
        /// One round, the way a participant plays it: the tower is placed, the split opens, the UFO
        /// sweeps back and forth across the tower and settles over the aim point, and the real laser
        /// fires — GameManager scores the shot and runs the reveal exactly as in a session.
        /// <paramref name="practice"/> fixes the two rates (a practice pair); default lets QUEST+
        /// choose, as in a main block. With <paramref name="nextTowerVx"/> the reset this shot starts
        /// deals the next round's tower there, during its pan, as a session's reset deals it — so the
        /// next round finds its tower in place and the scene changes once between rounds, not twice.
        /// </summary>
        IEnumerator PlayRound(float towerVx, float aimVx, float flySec, PracticePair practice,
                              float nextTowerVx = float.NaN)
        {
            while (_game.IsRevealing) yield return null;   // the last reveal, fog return and pan

            // The end of a reveal hands the UFO back to the pointer; take it again.
            _ufo.FreezeMovement = true;
            _ufo.SetHidden(false);

            // Placed already if the last reset dealt the spot it was asked for (the request is cleared
            // once used); otherwise placed here.
            bool dealt = _queuedVx == towerVx && !_tower.NextViewportX.HasValue;
            _tower.NextViewportX = null;
            _queuedVx = float.NaN;
            if (!dealt)
            {
                _tower.MoveTowerToViewportX(towerVx);
                _tower.ApplyVisibility();
            }
            _split.SetPractice(practice.highFps > 0, practice);
            _split.ResetSideTracking();
            _split.BeginTrial();

            float t0 = Time.realtimeSinceStartup;
            for (float t = 0f; t < 1f; t = (Time.realtimeSinceStartup - t0) / flySec)
            {
                // Two passes across the tower, drifting in to the aim point by the end.
                float sweep = towerVx + SweepHalfWidth * Mathf.Sin(t * Mathf.PI * 4f);
                PlaceUfo(Mathf.Lerp(sweep, aimVx, t * t));
                yield return null;
            }
            PlaceUfo(aimVx);
            yield return null;   // the side it settled on is taken in this frame's LateUpdate

            if (!float.IsNaN(nextTowerVx))
            {
                _tower.NextViewportX = nextTowerVx;
                _queuedVx            = nextTowerVx;
            }
            _shotAt = Time.realtimeSinceStartup;
            _laser.Fire();
        }

        // On the view at rest, as UfoController's own envelope is, which lets the ship's centre reach
        // either edge: so a spot here is one a participant can fly to, whatever the camera is doing.
        void PlaceUfo(float viewportX)
        {
            CameraRig rig = CameraRig.Instance;
            Camera    cam = rig != null ? rig.ActiveCamera : Camera.main;
            if (cam == null) return;
            float vx = Mathf.Clamp01(viewportX);
            _ufo.transform.position = rig != null
                ? rig.RestViewportPointAtZ(vx, UfoHeight, _ufo.fixedZ)
                : CameraRig.ViewportPointAtZ(cam, vx, UfoHeight, _ufo.fixedZ);
        }

        IEnumerator WaitUntil(float realtime)
        {
            while (Time.realtimeSinceStartup < realtime) yield return null;
        }

        // ── -jndCapture ──────────────────────────────────────────────────────

        IEnumerator Capture()
        {
            BeginBlock(_blocks[0]);
            yield return ParallaxShots();
            yield return MotionShots();

            var cases = new (string name, PracticePair pair)[]
            {
                ("500v85",  new PracticePair { highFps = 500, lowFps = 85 }),
                ("144v142", new PracticePair { highFps = 144, lowFps = 142 }),
                ("60v10",   new PracticePair { highFps = 60,  lowFps = 10 }),
            };
            // The game's spawn range runs 0.10–0.90 and never past 0.05–0.95.
            float[] spots   = { 0.10f, 0.90f, 0.05f, 0.95f, 0.30f, 0.50f, 0.75f };
            // After the shot: the tower lighting up, then the frame rates landing and settling.
            float[] moments = { 0.05f, 0.12f, 0.25f, 0.5f, 1.0f, 2.0f };
            // After the reveal hides: the reset under the veil — before the pan, through the pan and the
            // towers' crossfade, and the GO! beat after.
            float[] returns = { 0.05f, 0.15f, 0.25f, 0.35f, 0.5f, 0.7f };

            var rounds = new List<(string name, PracticePair pair, float vx)>();
            foreach (var c in cases)
            foreach (float vx in spots)
                rounds.Add((c.name, c.pair, vx));

            int saved = 0;
            for (int i = 0; i < rounds.Count; i++)
            {
                var   c  = rounds[i];
                float vx = c.vx;
                yield return PlayRound(vx, vx, 1.4f, c.pair, i + 1 < rounds.Count ? rounds[i + 1].vx : float.NaN);
                float shotAt = _shotAt;

                foreach (float m in moments)
                {
                    yield return WaitUntil(shotAt + m);
                    yield return SaveScreenshot(Name(vx, c.name, "t", m));
                    saved++;
                }

                float hiddenAt = shotAt + _game.fogRetreatDuration + _game.hitDisplayDuration;
                foreach (float r in returns)
                {
                    yield return WaitUntil(hiddenAt + r);
                    yield return SaveScreenshot(Name(vx, c.name, "r", r));
                    saved++;
                }
            }

            while (_game.IsRevealing) yield return null;
            Debug.Log($"[QaRunner] {saved} screenshot(s) in {_dir}");
        }

        // The camera's parallax swing (CameraRig), before the first round: the UFO held at the left edge of
        // the screen (its centre on the edge, half of it cut off, as far as a participant can fly it), then
        // the right, then the middle, each long enough for the swing to settle.
        IEnumerator ParallaxShots()
        {
            _ufo.FreezeMovement = true;
            _ufo.SetHidden(false);
            float[] spots = { 0f, 1f, 0.5f };
            foreach (float vx in spots)
            {
                for (float until = Time.realtimeSinceStartup + 2.5f; Time.realtimeSinceStartup < until; )
                {
                    PlaceUfo(vx);
                    yield return null;
                }
                yield return SaveScreenshot(string.Format(CultureInfo.InvariantCulture, "{0}x{1}_parallax_{2:0.00}.png",
                                                          Screen.width, Screen.height, vx));
            }
        }

        // The scene's own motion with nothing else moving: the UFO held in the middle, five frames a fifth of
        // a second apart — the grass in the wind, the windows, the aurora, the searchlights.
        IEnumerator MotionShots()
        {
            float t0 = Time.realtimeSinceStartup;
            for (int k = 0; k < 5; k++)
            {
                float at = t0 + 0.2f * k;
                while (Time.realtimeSinceStartup < at)
                {
                    PlaceUfo(0.5f);
                    yield return null;
                }
                yield return SaveScreenshot(string.Format(CultureInfo.InvariantCulture, "{0}x{1}_motion_{2:0.0}.png",
                                                          Screen.width, Screen.height, 0.2f * k));
            }
        }

        static string Name(float vx, string caseName, string phase, float sec) =>
            string.Format(CultureInfo.InvariantCulture, "{0}x{1}_s{2:0.00}_{3}_{4}{5:0.00}.png",
                          Screen.width, Screen.height, vx, caseName, phase, sec);

        IEnumerator SaveScreenshot(string fileName)
        {
            yield return new WaitForEndOfFrame();
            Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(_dir, fileName), shot.EncodeToPNG());
            Destroy(shot);
        }

        // ── -jndSeams ────────────────────────────────────────────────────────

        // How far either side of a jump the two pictures are taken, world units: far below a pixel anywhere.
        const float SeamDelta = 0.0005f;

        IEnumerator Seams()
        {
            // Stop everything that moves, so only a jump can change the picture: scaled time stops every shader
            // (they read _Time), the particles and the weather's fall; the camera's wander and swing run on the wall
            // clock, so off (they ease out); the UFO, the readout and the towers' beacons likewise.
            Time.timeScale = 0f;
            CameraRig rig = CameraRig.Instance;
            if (rig != null) { rig.handheld = false; rig.parallax = false; }
            _game.worldScrollHandheldFollow = false;   // locked on, so the camera stands exactly where it is put
            _ufo.FreezeMovement = true;
            _ufo.SetHidden(true);
            if (_ui != null && _ui.showDebug) _ui.SetDebugVisible(false);
            if (_tower.towerField != null)
                foreach (Renderer r in _tower.towerField.GetComponentsInChildren<Renderer>(true))
                    if (r.name.StartsWith("Beacon_", StringComparison.Ordinal)) r.enabled = false;
            for (float end = Time.realtimeSinceStartup + 2.5f; Time.realtimeSinceStartup < end; ) yield return null;

            var inv = CultureInfo.InvariantCulture;
            var sb  = new StringBuilder();
            sb.Append("# Invasion seam check, ").Append(DateTime.Now.ToString("u", inv)).Append(", screen ")
              .Append(Screen.width).Append('x').Append(Screen.height).Append('\n');
            sb.Append("# Each row: the view a hair before and a hair after a strip jumps a period (")
              .Append(SeamDelta.ToString("0.0000", inv)).Append(" world units either side), time stopped.\n");
            sb.Append("# 'control' rows move the camera the same distance without crossing a jump: sub-pixel detail (the\n");
            sb.Append("# towers' window mullions, thin grass) shimmers under any move, so a jump is seamless when it changes\n");
            sb.Append("# no more than its control.\n");
            sb.Append("strip,period,jump_at_x,direction,kind,pixels_changed_over_8,pixels_changed_over_24,max_channel_diff,mean_abs_diff\n");

            int n = 0;
            float y = _ufo.transform.position.y, z = _ufo.transform.position.z;
            foreach (WorldRepeat strip in FindObjectsByType<WorldRepeat>())
            {
                for (int dir = 1; dir >= -1; dir -= 2)
                {
                    // The next place this strip jumps, to the right or to the left of where the camera is. Three
                    // pictures: two short of it (the control), and one past it (the jump).
                    float edge = strip.CentreX + dir * strip.Period * 0.5f;
                    _ufo.transform.position = new Vector3(edge - 3f * dir * SeamDelta, y, z);
                    yield return Settle();
                    Texture2D c = Grab();
                    _ufo.transform.position = new Vector3(edge - dir * SeamDelta, y, z);
                    yield return Settle();
                    Texture2D a = Grab();
                    _ufo.transform.position = new Vector3(edge + dir * SeamDelta, y, z);
                    yield return Settle();
                    Texture2D b = Grab();

                    string tag = string.Format(inv, "seam_{0}_{1}_{2}", strip.name, dir > 0 ? "right" : "left", n++);
                    File.WriteAllBytes(Path.Combine(_dir, tag + "_a.png"), a.EncodeToPNG());
                    File.WriteAllBytes(Path.Combine(_dir, tag + "_b.png"), b.EncodeToPNG());
                    for (int kind = 0; kind < 2; kind++)
                    {
                        Compare(kind == 0 ? c : a, kind == 0 ? a : b, out int over8, out int over24, out int max, out double mean);
                        string what = kind == 0 ? "control" : "jump";
                        sb.Append(Csv(strip.name)).Append(',').Append(strip.Period.ToString("0.00", inv)).Append(',')
                          .Append(edge.ToString("0.00", inv)).Append(',').Append(dir > 0 ? "right" : "left").Append(',')
                          .Append(what).Append(',').Append(over8).Append(',').Append(over24).Append(',').Append(max).Append(',')
                          .Append(mean.ToString("0.0000", inv)).Append('\n');
                        Debug.Log($"[QaRunner] seam {strip.name} {(dir > 0 ? "right" : "left")} at x {edge:0.00}, {what}: " +
                                  $"{over8} px changed by more than 8/255, {over24} by more than 24/255, max {max}/255");
                    }
                    Destroy(a); Destroy(b); Destroy(c);
                }
            }

            // A few views far along the endless world, to look at.
            foreach (float x in new[] { 1000f, 5000f, -3000f })
            {
                _ufo.transform.position = new Vector3(x, y, z);
                yield return Settle();
                Texture2D v = Grab();
                File.WriteAllBytes(Path.Combine(_dir, string.Format(inv, "far_{0:0}.png", x)), v.EncodeToPNG());
                Destroy(v);
            }

            string path = Path.Combine(_dir, $"seams_{DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", inv)}.csv");
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"[QaRunner] Seam check written to {path}");
            Time.timeScale = 1f;
        }

        // A few frames for the camera to follow the UFO and every strip to settle where it jumps.
        static IEnumerator Settle()
        {
            for (int i = 0; i < 3; i++) yield return null;
            yield return new WaitForEndOfFrame();
        }

        static Texture2D Grab() => ScreenCapture.CaptureScreenshotAsTexture();

        static void Compare(Texture2D a, Texture2D b, out int over8, out int over24, out int max, out double mean)
        {
            Color32[] pa = a.GetPixels32(), pb = b.GetPixels32();
            over8 = over24 = max = 0;
            long sum = 0;
            int count = Mathf.Min(pa.Length, pb.Length);
            for (int i = 0; i < count; i++)
            {
                int d = Mathf.Max(Mathf.Abs(pa[i].r - pb[i].r), Mathf.Max(Mathf.Abs(pa[i].g - pb[i].g), Mathf.Abs(pa[i].b - pb[i].b)));
                sum += d;
                if (d > max) max = d;
                if (d > 8)  over8++;
                if (d > 24) over24++;
            }
            mean = count > 0 ? (double)sum / count : 0;
        }

        // ── -jndProfile ──────────────────────────────────────────────────────

        // How fast the UFO flies across the endless world while profiled, world units a second: a brisk search.
        const float ProfileScrollSpeed = 20f;
        // Each timed window, the part of it thrown away after a switch (a texture reallocated, a shader warmed),
        // and how many times each effect is switched off, every one beside a window of the scene as it is.
        const float ProfileWindowSec = 1.5f;
        const float ProfileSettleSec = 0.25f;
        const int   ProfileRepeats   = 3;

        // A part of the scene that can be switched off, and back on as it was. Set is null when the scene has none.
        sealed class Effect
        {
            public string       Name;
            public Action<bool> Set;
        }

        float[] _profBuf;              // the window being timed: frame deltas, ms
        int     _profN;
        bool    _profTiming;           // Update fills _profBuf while true
        bool    _profMoving = true;    // the UFO is flying (or sweeping)
        float   _profT0;

        IEnumerator Profile()
        {
            bool scroll = _game.worldScroll;
            _profBuf = new float[200_000];
            AudioListener.volume = 0f;   // minutes of the engine's drone help no one; sound is not what is timed

            // Uncapped and the split off, as in the benchmark's sweep: this PC's frame rate with the scene as it is.
            _split.CancelTrial();
            _split.enabled = false;
            FrameLimiter.Apply(0, FrameLimiterMode.Precise);
            _ufo.FreezeMovement = true;
            _ufo.SetHidden(false);
            _profT0 = Time.realtimeSinceStartup;

            List<Effect> effects = ProfileEffects(scroll);

            // Untimed: shaders, textures, the endless strips' first jumps.
            for (float end = Time.realtimeSinceStartup + 3f; Time.realtimeSinceStartup < end; )
            {
                FlyStep();
                yield return null;
            }

            var results = new List<(string name, bool na, double baseMs, double offMs, float baseMed, float offMed,
                                    int windows, int frames)>();
            double allBase = 0; int allBaseFrames = 0;
            foreach (Effect e in effects)
            {
                if (e.Set == null)
                {
                    results.Add((e.Name, true, 0, 0, 0, 0, 0, 0));
                    continue;
                }

                double baseMs = 0, offMs = 0;
                float  baseMed = 0f, offMed = 0f;
                int    baseN = 0, offN = 0;
                for (int k = 0; k < ProfileRepeats; k++)
                {
                    yield return TimeWindow();
                    baseMs += Sum(); baseN += _profN; baseMed += Median();

                    e.Set(false);
                    yield return TimeWindow();
                    offMs += Sum(); offN += _profN; offMed += Median();
                    e.Set(true);
                }
                allBase += baseMs; allBaseFrames += baseN;
                results.Add((e.Name, false, baseMs / Mathf.Max(1, baseN), offMs / Mathf.Max(1, offN),
                             baseMed / ProfileRepeats, offMed / ProfileRepeats, 2 * ProfileRepeats, baseN + offN));
                Debug.Log($"[QaRunner] profiled '{e.Name}'");
            }

            WriteProfile(results, scroll, allBase / Mathf.Max(1, allBaseFrames));
            _split.enabled = true;
        }

        // One timed window: the UFO keeps flying; frames are timed once the settle has passed.
        IEnumerator TimeWindow()
        {
            float start = Time.realtimeSinceStartup;
            _profN      = 0;
            _profTiming = false;
            while (Time.realtimeSinceStartup - start < ProfileSettleSec + ProfileWindowSec)
            {
                if (!_profTiming && Time.realtimeSinceStartup - start >= ProfileSettleSec) _profTiming = true;
                FlyStep();
                yield return null;
            }
            _profTiming = false;
        }

        double Sum()
        {
            double s = 0;
            for (int i = 0; i < _profN; i++) s += _profBuf[i];
            return s;
        }

        float Median()
        {
            if (_profN == 0) return float.NaN;
            Array.Sort(_profBuf, 0, _profN);
            return _profBuf[_profN / 2];
        }

        // The UFO's flight while profiled: across the endless world at a steady speed, the camera following, or
        // without the scroll sweeping across the view as the benchmark's uncapped sweep does.
        void FlyStep()
        {
            if (!_profMoving) return;
            if (_game.worldScroll)
                _ufo.transform.position += Vector3.right * (ProfileScrollSpeed * Time.unscaledDeltaTime);
            else
                PlaceUfo(0.5f + 0.42f * Mathf.Sin((Time.realtimeSinceStartup - _profT0) * 1.3f));
        }

        // Every part of the scene the profile switches, found now (so switching allocates nothing). Each is put back
        // exactly as it was.
        List<Effect> ProfileEffects(bool scroll)
        {
            var list = new List<Effect>();
            list.Add(new Effect { Name = "nothing (control: the noise)", Set = on => { } });

            TowerManager tm  = _tower;
            Camera       cam = CameraRig.Instance != null ? CameraRig.Instance.ActiveCamera : Camera.main;

            // The city: its far layers (drawn out of focus by the backdrop), its near layer, and its sparks. The night
            // sky's quads are left out — they are children of the city when it is not endless, and are timed on their
            // own below.
            var far = new List<Renderer>(); var near = new List<Renderer>(); var sparks = new List<GameObject>();
            if (tm.city != null)
                foreach (Renderer r in tm.city.GetComponentsInChildren<Renderer>(true))
                {
                    if (r is ParticleSystemRenderer) { sparks.Add(r.gameObject); continue; }
                    if (r.name.StartsWith("Sky", StringComparison.Ordinal)) continue;
                    (r.gameObject.layer == Backdrop.Layer ? far : near).Add(r);
                }

            Backdrop bd = tm.backdrop;
            list.Add(new Effect { Name = "backdrop blur (far city drawn sharp instead)",
                                  Set  = bd != null && bd.blur ? on => bd.blur = on : null });
            list.Add(RendererEffect("far city (5 layers, lights, searchlights)", far));
            list.Add(RendererEffect("near city layer", near));
            list.Add(ObjectsEffect("city sparks and embers", sparks));

            var stars = new List<Renderer>(); var aurora = new List<Renderer>(); var clouds = new List<Renderer>();
            if (tm.nightSky != null)
                foreach (Renderer r in tm.nightSky.GetComponentsInChildren<Renderer>(true))
                {
                    if      (r.name.StartsWith("SkyStars",  StringComparison.Ordinal)) stars.Add(r);
                    else if (r.name.StartsWith("SkyAurora", StringComparison.Ordinal)) aurora.Add(r);
                    else if (r.name.StartsWith("SkyClouds", StringComparison.Ordinal)) clouds.Add(r);
                }
            list.Add(RendererEffect("stars and shooting stars", stars));
            list.Add(RendererEffect("aurora", aurora));
            list.Add(RendererEffect("sky clouds", clouds));

            var weather = new List<Renderer>();
            if (tm.weather != null)
                foreach (Renderer r in tm.weather.GetComponentsInChildren<Renderer>(true))
                    if (r.enabled) weather.Add(r);   // off in the scene: nothing to time
            list.Add(RendererEffect("weather (rain/snow)", weather));

            Foreground fg = tm.foreground;
            list.Add(new Effect { Name = "foreground grass", Set = fg != null && fg.grass ? on => fg.grass = on : null });
            list.Add(new Effect { Name = "grass motes",      Set = fg != null && fg.grass && fg.motes ? on => fg.motes = on : null });

            var towers = new List<Renderer>();
            if (tm.towerField != null) towers.AddRange(tm.towerField.GetComponentsInChildren<Renderer>(true));
            list.Add(RendererEffect("tower field (towers, beacons)", towers));

            list.Add(new Effect { Name = "UFO (ship, flair, sight)", Set = on => _ufo.SetHidden(!on) });
            var trails = new List<Renderer>(_ufo.GetComponentsInChildren<TrailRenderer>(true));
            list.Add(RendererEffect("UFO trails", trails));
            WeaponSight sight = FindAnyObjectByType<WeaponSight>();
            if (sight != null)
            {
                var lines = new List<Renderer>(sight.GetComponentsInChildren<LineRenderer>(true));
                Effect sightLines = RendererEffect("weapon sight", lines);
                list.Add(new Effect { Name = sightLines.Name,
                                      Set  = sightLines.Set == null ? null : on => { sight.enabled = on; sightLines.Set(on); } });
            }
            else list.Add(new Effect { Name = "weapon sight" });

            var camData = cam != null ? cam.GetComponent<UniversalAdditionalCameraData>() : null;
            list.Add(new Effect { Name = "post-processing (bloom, tonemapping)",
                                  Set  = camData != null && camData.renderPostProcessing
                                         ? on => camData.renderPostProcessing = on : null });
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            int msaa = urp != null ? urp.msaaSampleCount : 1;
            list.Add(new Effect { Name = $"MSAA {msaa}x", Set = msaa > 1 ? on => urp.msaaSampleCount = on ? msaa : 1 : null });
            list.Add(new Effect { Name = "HDR", Set = cam != null && cam.allowHDR ? on => cam.allowHDR = on : null });
            CameraClearFlags clear = cam != null ? cam.clearFlags : CameraClearFlags.Nothing;
            list.Add(new Effect { Name = "skybox",
                                  Set  = clear == CameraClearFlags.Skybox
                                         ? on => cam.clearFlags = on ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor
                                         : null });

            CameraRig rig = CameraRig.Instance;
            bool parallax = rig != null && rig.parallax, handheld = rig != null && rig.handheld;
            list.Add(new Effect { Name = "camera parallax swing and handheld wander",
                                  Set  = parallax || handheld ? on => { rig.parallax = on && parallax; rig.handheld = on && handheld; } : null });

            list.Add(new Effect { Name = "debug readout (HUD)",
                                  Set  = _ui != null && _ui.showDebug ? on => _ui.SetDebugVisible(on) : null });

            // Everything behind and below the play plane at once: the floor under the scenery's cost.
            var scenery = new List<Effect>();
            foreach (Effect e in list)
                if (e.Set != null && (e.Name.StartsWith("far city", StringComparison.Ordinal) ||
                                      e.Name.StartsWith("near city", StringComparison.Ordinal) ||
                                      e.Name.StartsWith("city sparks", StringComparison.Ordinal) ||
                                      e.Name.StartsWith("stars", StringComparison.Ordinal) ||
                                      e.Name.StartsWith("aurora", StringComparison.Ordinal) ||
                                      e.Name.StartsWith("sky clouds", StringComparison.Ordinal) ||
                                      e.Name.StartsWith("weather", StringComparison.Ordinal) ||
                                      e.Name.StartsWith("foreground", StringComparison.Ordinal) ||
                                      e.Name.StartsWith("tower field", StringComparison.Ordinal)))
                    scenery.Add(e);
            list.Add(new Effect { Name = "ALL scenery (city, sky, weather, grass, towers)",
                                  Set  = on => { foreach (Effect e in scenery) e.Set(on); } });

            // With the scroll: the camera standing still against scrolling — what moving the world itself costs.
            if (scroll)
                list.Add(new Effect { Name = "scrolling (ship and camera stand still instead)",
                                      Set  = on => _profMoving = on });
            return list;
        }

        // Switches a set of renderers off, and back on as each one was.
        static Effect RendererEffect(string name, List<Renderer> renderers)
        {
            if (renderers.Count == 0) return new Effect { Name = name };
            var was = new bool[renderers.Count];
            return new Effect
            {
                Name = name,
                Set  = on =>
                {
                    for (int i = 0; i < renderers.Count; i++)
                    {
                        if (renderers[i] == null) continue;
                        if (!on) { was[i] = renderers[i].enabled; renderers[i].enabled = false; }
                        else     renderers[i].enabled = was[i];
                    }
                },
            };
        }

        // Deactivates a set of objects, and puts back the ones that were active.
        static Effect ObjectsEffect(string name, List<GameObject> objects)
        {
            if (objects.Count == 0) return new Effect { Name = name };
            var was = new bool[objects.Count];
            return new Effect
            {
                Name = name,
                Set  = on =>
                {
                    for (int i = 0; i < objects.Count; i++)
                    {
                        if (objects[i] == null) continue;
                        if (!on) { was[i] = objects[i].activeSelf; objects[i].SetActive(false); }
                        else     objects[i].SetActive(was[i]);
                    }
                },
            };
        }

        void WriteProfile(List<(string name, bool na, double baseMs, double offMs, float baseMed, float offMed,
                                int windows, int frames)> results, bool scroll, double baselineMs)
        {
            var inv = CultureInfo.InvariantCulture;
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", inv);
            string path  = Path.Combine(_dir, $"profile_{stamp}{(scroll ? "_scroll" : "")}.csv");

            var sb = new StringBuilder(8 * 1024);
            sb.Append("# Invasion effect profile, ").Append(DateTime.Now.ToString("u", inv)).Append('\n');
            sb.Append("# CPU ").Append(SystemInfo.processorType).Append(" (").Append(SystemInfo.processorCount)
              .Append(" threads), GPU ").Append(SystemInfo.graphicsDeviceName).Append(", ")
              .Append(SystemInfo.graphicsDeviceType).Append('\n');
            sb.Append("# screen ").Append(Screen.width).Append('x').Append(Screen.height).Append(", ")
              .Append(Screen.fullScreenMode).Append(", quality level ")
              .Append(QualitySettings.names[QualitySettings.GetQualityLevel()]).Append('\n');
            if (Switches.Length > 0) sb.Append("# switches:").Append(Switches).Append('\n');
            sb.Append("# world scroll ").Append(scroll ? "ON: endless world, UFO flying at " +
                      ProfileScrollSpeed.ToString("0", inv) + " u/s, camera following" : "off: UFO sweeping across the view")
              .Append("; uncapped; baseline ").Append(baselineMs.ToString("0.000", inv)).Append(" ms (")
              .Append((1000.0 / baselineMs).ToString("0", inv)).Append(" fps)\n");
            sb.Append("# saved = mean frame time with the effect on minus off, each over ").Append(ProfileRepeats)
              .Append(" windows of ").Append(ProfileWindowSec.ToString("0.0", inv))
              .Append(" s interleaved; compare with the control row for the noise.\n");
            sb.Append("effect,base_ms,off_ms,saved_ms,saved_pct,base_fps,off_fps,fps_gain,base_median_ms,off_median_ms,windows,frames\n");

            var lines = new List<(double saved, string text)>();
            foreach (var r in results)
            {
                if (r.na)
                {
                    sb.Append(Csv(r.name)).Append(",n/a,,,,,,,,,0,0\n");
                    continue;
                }
                double saved = r.baseMs - r.offMs;
                double bf = 1000.0 / r.baseMs, of = 1000.0 / r.offMs;
                sb.Append(Csv(r.name)).Append(',')
                  .Append(r.baseMs.ToString("0.0000", inv)).Append(',').Append(r.offMs.ToString("0.0000", inv)).Append(',')
                  .Append(saved.ToString("0.0000", inv)).Append(',').Append((100.0 * saved / r.baseMs).ToString("0.00", inv)).Append(',')
                  .Append(bf.ToString("0.0", inv)).Append(',').Append(of.ToString("0.0", inv)).Append(',')
                  .Append((of - bf).ToString("0.0", inv)).Append(',')
                  .Append(r.baseMed.ToString("0.0000", inv)).Append(',').Append(r.offMed.ToString("0.0000", inv)).Append(',')
                  .Append(r.windows).Append(',').Append(r.frames).Append('\n');
                lines.Add((saved, $"{r.name}: {saved:0.0000} ms ({100.0 * saved / r.baseMs:0.0}%), {bf:0} -> {of:0} fps"));
            }
            File.WriteAllText(path, sb.ToString());

            lines.Sort((a, b) => b.saved.CompareTo(a.saved));
            Debug.Log($"[QaRunner] Profile, world scroll {(scroll ? "on" : "off")}, baseline {baselineMs:0.000} ms " +
                      $"({1000.0 / baselineMs:0} fps) — frame time each effect costs, largest first:");
            foreach (var l in lines) Debug.Log("[QaRunner]   " + l.text);
            Debug.Log($"[QaRunner] Profile written to {path}");
        }

        // ── -jndBenchmark ────────────────────────────────────────────────────

        // What paced a frame, for the report's rows.
        enum Kind : byte { Base, Altered, BetweenRounds, Free }

        // Every timed frame, in arrays allocated up front so the timing costs nothing to record.
        const int MaxFrames = 600_000;
        float[] _dt, _spin;
        short[] _cap;
        byte[]  _kind, _phaseOf;
        int     _frames;
        int     _phase = -1;   // index into _phaseNames while timing; -1 = not timing
        readonly List<string> _phaseNames = new List<string>();

        void Update()
        {
            // The profile's window being timed (see Profile): this frame's delta, in a buffer made up front.
            if (_profTiming && _profN < _profBuf.Length) _profBuf[_profN++] = Time.unscaledDeltaTime * 1000f;

            if (_phase < 0 || _dt == null || _frames >= MaxFrames) return;

            // Read after PerturbationController.Update (order -200): PacedFps / PacedBy describe the
            // cap that produced this frame's delta; LastSpinMs is how long the limiter waited
            // before it, so delta minus spin is the time the frame before actually needed.
            bool paced = _split.enabled;
            Kind kind = _game.IsRevealing ? Kind.BetweenRounds
                      : !paced || !_split.SplitActive ? Kind.Free
                      : _split.PacedBy == PerturbationController.Pace.Altered ? Kind.Altered
                      : _split.PacedBy == PerturbationController.Pace.Base    ? Kind.Base
                      : Kind.Free;

            _dt[_frames]      = Time.unscaledDeltaTime * 1000f;
            _spin[_frames]    = FrameLimiter.LastSpinMs;
            _cap[_frames]     = (short)(paced ? _split.PacedFps : 0);
            _kind[_frames]    = (byte)kind;
            _phaseOf[_frames] = (byte)_phase;
            _frames++;
        }

        IEnumerator Benchmark()
        {
            _dt      = new float[MaxFrames];
            _spin    = new float[MaxFrames];
            _cap     = new short[MaxFrames];
            _kind    = new byte[MaxFrames];
            _phaseOf = new byte[MaxFrames];

            const float perBlock = 16f;

            // Warm-up, not timed: shaders, the first reveal, the first QUEST+ update.
            BeginBlock(_blocks[0]);
            yield return PlayRound(0.5f, 0.5f, 1f, default);
            while (_game.IsRevealing) yield return null;

            // Every block as the study runs it, the readout as the scene has it.
            StudyConfig fastest = _blocks[0];
            foreach (StudyConfig cfg in _blocks)
            {
                if (cfg.baseFps > fastest.baseFps) fastest = cfg;
                BeginBlock(cfg);
                yield return TimeRounds($"{cfg.baseFps} fps block", perBlock);
            }

            // The fastest block again with the debug readout off, as a participant plays it.
            if (_ui != null && _ui.showDebug)
            {
                _ui.SetDebugVisible(false);
                BeginBlock(fastest);
                yield return TimeRounds($"{fastest.baseFps} fps block, readout off", perBlock);
                _ui.SetDebugVisible(true);
            }

            // Uncapped: this PC's ceiling with the scene as it is.
            _split.CancelTrial();
            _split.enabled = false;              // its LateUpdate would re-apply the block's cap
            FrameLimiter.Apply(0, FrameLimiterMode.Precise);
            yield return TimeSweep("uncapped", 8f);
            _split.enabled = true;

            WriteReport();
        }

        IEnumerator TimeRounds(string label, float seconds)
        {
            _phaseNames.Add(label);
            _phase = _phaseNames.Count - 1;

            // Anywhere in the spawn range; half the shots on the tower, half a clear miss. Each round's
            // spot is drawn a round ahead, so the reset before it can deal it.
            float end = Time.realtimeSinceStartup + seconds;
            float vx  = UnityEngine.Random.Range(0.12f, 0.88f);
            while (Time.realtimeSinceStartup < end)
            {
                float aim  = UnityEngine.Random.value < 0.5f ? vx : vx + (vx < 0.5f ? 0.2f : -0.2f);
                float next = UnityEngine.Random.Range(0.12f, 0.88f);
                yield return PlayRound(vx, aim, 2.5f, default, next);
                vx = next;
            }
            while (_game.IsRevealing) yield return null;
            _phase = -1;
        }

        IEnumerator TimeSweep(string label, float seconds)
        {
            _phaseNames.Add(label);
            _phase = _phaseNames.Count - 1;

            float t0 = Time.realtimeSinceStartup;
            for (float t = 0f; t < seconds; t = Time.realtimeSinceStartup - t0)
            {
                PlaceUfo(0.5f + 0.42f * Mathf.Sin(t * 1.3f));
                yield return null;
            }
            _phase = -1;
        }

        void WriteReport()
        {
            var inv = CultureInfo.InvariantCulture;
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", inv);
            string summaryPath = Path.Combine(_dir, $"benchmark_{stamp}.csv");
            string framesPath  = Path.Combine(_dir, $"benchmark_{stamp}_frames.csv");

            var sb = new StringBuilder(16 * 1024);
            sb.Append("# Invasion frame-rate benchmark, ").Append(DateTime.Now.ToString("u", inv)).Append('\n');
            sb.Append("# CPU ").Append(SystemInfo.processorType).Append(" (").Append(SystemInfo.processorCount)
              .Append(" threads), GPU ").Append(SystemInfo.graphicsDeviceName).Append(", ")
              .Append(SystemInfo.graphicsDeviceType).Append('\n');
            sb.Append("# screen ").Append(Screen.width).Append('x').Append(Screen.height).Append(" @ ")
              .Append(Screen.currentResolution.refreshRateRatio.value.ToString("0.##", inv)).Append(" Hz, ")
              .Append(Screen.fullScreenMode).Append(", quality level ")
              .Append(QualitySettings.names[QualitySettings.GetQualityLevel()]).Append('\n');
            if (Switches.Length > 0) sb.Append("# switches:").Append(Switches).Append('\n');
            sb.Append("# late = a frame that took longer than its cap's period + 0.05 ms. busy = delta minus ")
              .Append("the limiter's wait: the time the game needed. spare = period minus busy.\n");
            sb.Append("phase,pace,frames,cap_fps,mean_fps,p50_ms,p99_ms,p999_ms,max_ms,late_frames,late_pct,")
              .Append("busy_p50_ms,busy_p99_ms,busy_max_ms,spare_at_busy_p99_ms\n");

            var lines = new List<string>();
            for (int p = 0; p < _phaseNames.Count; p++)
            for (int k = 0; k < 4; k++)
            {
                var dt   = new List<float>();
                var busy = new List<float>();
                long capSum = 0;
                int late = 0;
                for (int i = 0; i < _frames; i++)
                {
                    if (_phaseOf[i] != p || _kind[i] != k) continue;
                    dt.Add(_dt[i]);
                    busy.Add(Mathf.Max(0f, _dt[i] - _spin[i]));
                    capSum += _cap[i];
                    if (_cap[i] > 0 && _dt[i] > 1000f / _cap[i] + 0.05f) late++;
                }
                if (dt.Count == 0) continue;

                float meanDt = 0f;
                foreach (float v in dt) meanDt += v;
                meanDt /= dt.Count;
                float meanCap = capSum / (float)dt.Count;
                dt.Sort();
                busy.Sort();
                float busy99 = Pct(busy, 0.99f);
                string pace = ((Kind)k) switch
                {
                    Kind.Base          => "round, base side",
                    Kind.Altered       => "round, altered side",
                    Kind.BetweenRounds => "between rounds (reveal, fog, pan)",
                    _                  => meanCap > 0f ? "idle" : "uncapped",
                };
                float spare = meanCap > 0f ? 1000f / meanCap - busy99 : float.NaN;

                sb.Append(Csv(_phaseNames[p])).Append(',').Append(Csv(pace)).Append(',')
                  .Append(dt.Count).Append(',').Append(meanCap.ToString("0", inv)).Append(',')
                  .Append((1000f / meanDt).ToString("0.0", inv)).Append(',')
                  .Append(Pct(dt, 0.5f).ToString("0.000", inv)).Append(',')
                  .Append(Pct(dt, 0.99f).ToString("0.000", inv)).Append(',')
                  .Append(Pct(dt, 0.999f).ToString("0.000", inv)).Append(',')
                  .Append(dt[dt.Count - 1].ToString("0.000", inv)).Append(',')
                  .Append(late).Append(',').Append((100f * late / dt.Count).ToString("0.00", inv)).Append(',')
                  .Append(Pct(busy, 0.5f).ToString("0.000", inv)).Append(',')
                  .Append(busy99.ToString("0.000", inv)).Append(',')
                  .Append(busy[busy.Count - 1].ToString("0.000", inv)).Append(',')
                  .Append(float.IsNaN(spare) ? "" : spare.ToString("0.000", inv)).Append('\n');

                lines.Add($"{_phaseNames[p]} — {pace}: {dt.Count} frames at {1000f / meanDt:0.0} fps " +
                          (meanCap > 0f ? $"(cap {meanCap:0}), {100f * late / dt.Count:0.00}% late, " : ", ") +
                          $"worst {dt[dt.Count - 1]:0.00} ms; the game needed {busy99:0.000} ms at p99" +
                          (float.IsNaN(spare) ? "" : $", {spare:0.000} ms spare"));
            }
            File.WriteAllText(summaryPath, sb.ToString());

            var raw = new StringBuilder(_frames * 32);
            raw.Append("frame,phase,pace,cap_fps,delta_ms,limiter_wait_ms\n");
            for (int i = 0; i < _frames; i++)
                raw.Append(i).Append(',').Append(_phaseOf[i]).Append(',').Append(_kind[i]).Append(',')
                   .Append(_cap[i]).Append(',').Append(_dt[i].ToString("0.0000", inv)).Append(',')
                   .Append(_spin[i].ToString("0.0000", inv)).Append('\n');
            File.WriteAllText(framesPath, raw.ToString());

            foreach (string line in lines) Debug.Log("[QaRunner] " + line);
            Debug.Log($"[QaRunner] Benchmark written to {summaryPath}");
        }

        static float Pct(List<float> sorted, float q) =>
            sorted[Mathf.Clamp(Mathf.CeilToInt(q * sorted.Count) - 1, 0, sorted.Count - 1)];

        static string Csv(string s) => s.IndexOf(',') >= 0 ? "\"" + s + "\"" : s;
    }
}
