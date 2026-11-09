using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace JndUfo
{
    /// <summary>
    /// <c>Invasion.exe -jndRobot</c>: a stand-in participant, for checking a build before anyone sits down. It plays a real
    /// session through the real game (ExperimentDirector, QUEST+, the logs, the frame limiter), pressing keys and moving
    /// the mouse through the Input System as a person would, with some presses deliberately too early, too late, inside the
    /// cooldown and with firing off, so every path into the shot log gets exercised. Audit the logs it leaves with
    /// Analysis/audit_logs.py. It does not advance the participant number. Inert without the flag; never active in the
    /// Editor. Shorten a run by lowering maxTrials in the build's own Data/ExperimentConfig.csv.
    /// </summary>
    public sealed class QaRobot : MonoBehaviour
    {
        /// <summary>True when this run of the game is being played by the robot.</summary>
        public static bool Running { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot()
        {
            Running = false;
            if (Application.isEditor || Array.IndexOf(Environment.GetCommandLineArgs(), "-jndRobot") < 0) return;
            Running = true;
            var go = new GameObject("QaRobot");
            DontDestroyOnLoad(go);
            go.AddComponent<QaRobot>();
        }

        // The stand-in's own eyes: a threshold of 6 ms of frame time, a lapse now and then, and a reaction time.
        const float ThetaMs = 6f, Beta = 2.5f, Lapse = 0.05f;

        readonly System.Random _rng = new System.Random(20261105);
        float _nextKey, _pressAt = float.PositiveInfinity, _again = float.PositiveInfinity;
        int   _seenFlips, _seenCrossings, _plannedRound = -1;
        bool  _keyDown;

        float Roll => (float)_rng.NextDouble();

        void Start()
        {
            Application.runInBackground = true;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
        }

        bool Notices(float diffMs)
        {
            double p = (1.0 - Lapse) * (1.0 - Math.Exp(-Math.Pow(Mathf.Max(0.01f, diffMs) / ThetaMs, Beta)));
            return Roll < p;
        }

        void Update()
        {
            ExperimentDirector d = ExperimentDirector.Instance;
            Keyboard kb = Keyboard.current;
            Mouse mouse = Mouse.current;
            if (d == null || kb == null || mouse == null || d.laser == null || d.perturbation == null) return;

            float now = Time.unscaledTime;
            bool  tapKey = false, click = false;
            Vector2 delta = Vector2.zero;

            if (!d.PhaseOpen)
            {
                // At a prompt: Space, unhurried.
                if (now >= _nextKey) { tapKey = true; _nextKey = now + 1.5f; }
                _plannedRound = -1;
            }
            else if (!d.laser.FiringEnabled)
            {
                // Between rounds, the odd press that the game must ignore and the log must still show.
                if (now >= _nextKey) { click = Roll < 0.15f; _nextKey = now + 0.7f; }
                _pressAt = _again = float.PositiveInfinity;
                _seenFlips = 0; _seenCrossings = 0; _plannedRound = -1;
            }
            else
            {
                PerturbationController pc = d.perturbation;
                float diff = pc.CurrentRound.diffMs;
                if (_plannedRound < 0)
                {
                    _plannedRound = 1;
                    _pressAt = float.PositiveInfinity;
                    // One round in six, a press before the frame rate has changed.
                    if (Roll < 0.16f) { _pressAt = now + 0.3f + 0.4f * Roll; _again = _pressAt + 0.08f; }
                }

                if (d.Config.IsShockwave)
                {
                    ShockwaveController sw = ShockwaveController.Instance;
                    int flips = sw != null ? sw.FlipCount : 0;
                    if (flips != _seenFlips)
                    {
                        _seenFlips = flips;
                        if (float.IsInfinity(_pressAt))
                        {
                            if (Notices(diff))    _pressAt = now + 0.30f + 0.35f * Roll;   // inside the window
                            else if (Roll < 0.3f) _pressAt = now + 1.05f + 0.4f * Roll;    // after it has closed
                        }
                    }
                }
                else
                {
                    // Fly back and forth across the tower by mouse, and fire on noticing the change.
                    float x = d.ufo.transform.position.x, tower = d.towerManager.TowerBase.x;
                    float target = tower + 9f * Mathf.Sin(now * 1.6f);
                    delta.x = Mathf.Clamp((target - x) * 3f, -60f, 60f);
                    int crossings = pc.RoundCrossings;
                    if (crossings != _seenCrossings)
                    {
                        _seenCrossings = crossings;
                        if (float.IsInfinity(_pressAt) && (Notices(diff) || crossings >= 8))
                            _pressAt = now + 0.15f + 0.2f * Roll;
                    }
                }

                if (now >= _pressAt) { click = true; _pressAt = float.PositiveInfinity; }
                else if (now >= _again) { click = true; _again = float.PositiveInfinity; }
            }

            MouseState ms = new MouseState { position = mouse.position.ReadValue() + delta, delta = delta };
            ms = ms.WithButton(MouseButton.Left, click);
            InputSystem.QueueStateEvent(mouse, ms);

            if (tapKey) { InputSystem.QueueStateEvent(kb, new KeyboardState(Key.Space)); _keyDown = true; }
            else if (_keyDown) { InputSystem.QueueStateEvent(kb, new KeyboardState()); _keyDown = false; }
        }
    }
}
