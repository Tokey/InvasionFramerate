using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace JndUfo.EditorTools
{
    /// <summary>
    /// Lets a tool outside the Editor ask the OPEN Editor to refresh, build the player, or render
    /// the main camera to a PNG — so changes can be built and checked without closing Unity (an
    /// open Editor holds the project lock, which rules out a second, batch-mode Unity).
    ///
    /// Drop a text file at <c>Temp/AgentBridge/request.txt</c> holding one command per line:
    ///
    ///   refresh              AssetDatabase.Refresh — imports changed files, recompiles scripts
    ///   build &lt;exePath&gt;     builds the enabled scenes for Windows 64-bit to that .exe
    ///   render &lt;pngPath&gt;    renders Camera.main in edit mode to a 1920x1080 PNG
    ///   setfloat &lt;Type&gt; &lt;field&gt; &lt;value&gt;   sets a float field on the open scene's first component of
    ///                        that type, through the Inspector's own machinery, and saves the scene
    ///   setcolor &lt;Type&gt; &lt;field&gt; &lt;r&gt; &lt;g&gt; &lt;b&gt; &lt;a&gt;   likewise for a Color field
    ///
    /// Progress and results go to <c>Temp/AgentBridge/status.txt</c>; the request file is deleted
    /// once taken. Nothing happens while the Editor is playing, compiling or importing — a request
    /// simply waits. Editor-only, inert unless that file exists, and Temp/ is never committed.
    /// Delete this script to remove the bridge entirely.
    /// </summary>
    [InitializeOnLoad]
    static class AgentBuildBridge
    {
        static readonly string Dir     = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Temp", "AgentBridge"));
        static readonly string Request = Path.Combine(Dir, "request.txt");
        static readonly string Status  = Path.Combine(Dir, "status.txt");

        static double s_nextPoll;
        static bool   s_busy;

        static AgentBuildBridge()
        {
            EditorApplication.update -= Poll;
            EditorApplication.update += Poll;
        }

        static void Poll()
        {
            if (s_busy) return;
            double now = EditorApplication.timeSinceStartup;
            if (now < s_nextPoll) return;
            s_nextPoll = now + 1.0;

            if (!File.Exists(Request)) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling ||
                EditorApplication.isUpdating) return;

            string[] commands;
            try
            {
                commands = File.ReadAllLines(Request)
                               .Select(l => l.Trim())
                               .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
                               .ToArray();
                File.Delete(Request);
            }
            catch (Exception e)
            {
                WriteStatus($"ERROR reading request: {e.Message}");
                return;
            }

            s_busy = true;
            var log = new StringBuilder();
            log.AppendLine($"started {DateTime.Now:O}");
            try
            {
                foreach (string cmd in commands) Run(cmd, log);
            }
            catch (Exception e)
            {
                log.AppendLine($"ERROR {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                log.AppendLine($"finished {DateTime.Now:O}");
                WriteStatus(log.ToString());
                s_busy = false;
            }
        }

        static void Run(string cmd, StringBuilder log)
        {
            int sp = cmd.IndexOf(' ');
            string verb = (sp < 0 ? cmd : cmd.Substring(0, sp)).ToLowerInvariant();
            string arg  = sp < 0 ? "" : cmd.Substring(sp + 1).Trim().Trim('"');

            switch (verb)
            {
                case "refresh":
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    log.AppendLine("refresh: done (scripts recompile on the next editor tick if any changed)");
                    break;

                case "build":
                    Build(arg, log);
                    break;

                case "render":
                    Render(arg, log);
                    break;

                case "openscene":
                    OpenScene(arg, log);
                    break;

                case "dump":
                    Dump(arg, log);
                    break;

                case "shadermsgs":
                    ShaderMessages(log);
                    break;

                case "setfloat":
                    SetFloat(arg, log);
                    break;

                case "setcolor":
                    SetColor(arg, log);
                    break;

                default:
                    log.AppendLine($"unknown command '{cmd}'");
                    break;
            }
        }

        static void Build(string exePath, StringBuilder log)
        {
            if (string.IsNullOrEmpty(exePath)) { log.AppendLine("build: no path given"); return; }

            // With an open scene modified, BuildPlayer stops in a modal dialog to ask whether to save it, and
            // the Editor waits there until someone answers. Refuse instead and say which scene.
            string dirty = string.Join(", ", Enumerable.Range(0, SceneManager.sceneCount)
                                                       .Select(SceneManager.GetSceneAt)
                                                       .Where(sc => sc.isDirty)
                                                       .Select(sc => sc.path));
            if (dirty.Length > 0)
            {
                log.AppendLine($"build: not started, {dirty} has unsaved changes and Unity would stop to ask " +
                               "about them. Save or revert it in the Editor, then build again.");
                return;
            }

            string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            var options = new BuildPlayerOptions
            {
                scenes           = scenes,
                locationPathName = exePath,
                target           = BuildTarget.StandaloneWindows64,
                targetGroup      = BuildTargetGroup.Standalone,
                options          = BuildOptions.None,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary s = report.summary;
            log.AppendLine($"build: {s.result}  errors={s.totalErrors} warnings={s.totalWarnings} " +
                           $"time={s.totalTime.TotalSeconds:0.0}s size={s.totalSize / (1024 * 1024)}MB -> {s.outputPath}");

            foreach (BuildStep step in report.steps)
                foreach (BuildStepMessage m in step.messages)
                    if (m.type == LogType.Error || m.type == LogType.Exception || m.type == LogType.Warning)
                        log.AppendLine($"  [{m.type}] {m.content}");
        }

        // "Type field value": the first component of that type in the open scenes, its serialized float field set
        // as an Inspector edit would (undoable, the scene marked dirty), then the scene saved.
        static void SetFloat(string arg, StringBuilder log)
        {
            string[] a = (arg ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (a.Length != 3 || !float.TryParse(a[2], System.Globalization.NumberStyles.Float,
                                                 System.Globalization.CultureInfo.InvariantCulture, out float value))
            {
                log.AppendLine("setfloat: expected <Type> <field> <value>");
                return;
            }
            foreach (MonoBehaviour mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include))
            {
                if (mb == null || mb.GetType().Name != a[0]) continue;
                var so = new SerializedObject(mb);
                SerializedProperty prop = so.FindProperty(a[1]);
                if (prop == null || prop.propertyType != SerializedPropertyType.Float)
                {
                    log.AppendLine($"setfloat: {a[0]} has no float field '{a[1]}'");
                    return;
                }
                float old = prop.floatValue;
                prop.floatValue = value;
                so.ApplyModifiedProperties();
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(mb.gameObject.scene);
                bool saved = UnityEditor.SceneManagement.EditorSceneManager.SaveScene(mb.gameObject.scene);
                log.AppendLine($"setfloat: {a[0]}.{a[1]} {old} -> {value} on '{mb.gameObject.name}', scene saved={saved}");
                return;
            }
            log.AppendLine($"setfloat: no {a[0]} in the open scenes");
        }

        // "Type field r g b a": as SetFloat, for a Color field.
        static void SetColor(string arg, StringBuilder log)
        {
            string[] a = (arg ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var c = new float[4];
            bool ok = a.Length == 6;
            for (int i = 0; ok && i < 4; i++)
                ok = float.TryParse(a[2 + i], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out c[i]);
            if (!ok) { log.AppendLine("setcolor: expected <Type> <field> <r> <g> <b> <a>"); return; }

            foreach (MonoBehaviour mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include))
            {
                if (mb == null || mb.GetType().Name != a[0]) continue;
                var so = new SerializedObject(mb);
                SerializedProperty prop = so.FindProperty(a[1]);
                if (prop == null || prop.propertyType != SerializedPropertyType.Color)
                {
                    log.AppendLine($"setcolor: {a[0]} has no Color field '{a[1]}'");
                    return;
                }
                Color old = prop.colorValue;
                prop.colorValue = new Color(c[0], c[1], c[2], c[3]);
                so.ApplyModifiedProperties();
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(mb.gameObject.scene);
                bool saved = UnityEditor.SceneManagement.EditorSceneManager.SaveScene(mb.gameObject.scene);
                log.AppendLine($"setcolor: {a[0]}.{a[1]} {old} -> {prop.colorValue} on '{mb.gameObject.name}', scene saved={saved}");
                return;
            }
            log.AppendLine($"setcolor: no {a[0]} in the open scenes");
        }

        // Every compiler message (errors and warnings) of the project's own shaders in Resources/Shaders.
        static void ShaderMessages(StringBuilder log)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Shader", new[] { "Assets/Resources/Shaders" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var sh = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (sh == null) continue;
                ShaderMessage[] msgs = ShaderUtil.GetShaderMessages(sh);
                log.AppendLine($"{path}: {msgs.Length} message(s), supported={sh.isSupported}");
                foreach (ShaderMessage m in msgs)
                    log.AppendLine($"  [{m.severity}] line {m.line} ({m.platform}): {m.message}");
            }
        }

        static void Render(string pngPath, StringBuilder log)
        {
            if (string.IsNullOrEmpty(pngPath)) { log.AppendLine("render: no path given"); return; }

            Camera cam = Camera.main;
            if (cam == null) { log.AppendLine("render: no Camera.main in the open scene"); return; }

            const int W = 1920, H = 1080;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            RenderTexture prevTarget = cam.targetTexture;
            RenderTexture prevActive = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pngPath)));
                File.WriteAllBytes(pngPath, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
                log.AppendLine($"render: wrote {pngPath}");
            }
            finally
            {
                cam.targetTexture    = prevTarget;
                RenderTexture.active = prevActive;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        // Reloads a scene from disk, discarding whatever the Editor holds in memory for it — the
        // fix for a scene whose components lost their values in memory while the file is intact.
        static void OpenScene(string path, StringBuilder log)
        {
            if (string.IsNullOrEmpty(path)) { log.AppendLine("openscene: no path given"); return; }

            UnityEngine.SceneManagement.Scene scene =
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                    path, UnityEditor.SceneManagement.OpenSceneMode.Single);
            log.AppendLine($"openscene: {scene.path} loaded={scene.isLoaded} dirty={scene.isDirty} " +
                           $"roots={scene.rootCount}");
            foreach (GameObject root in scene.GetRootGameObjects())
                log.AppendLine($"  root: {root.name} (active={root.activeSelf})");
        }

        // Every component in the open scene, serialized as the Editor holds it right now — to
        // compare live Inspector values against the scene file.
        static void Dump(string outPath, StringBuilder log)
        {
            if (string.IsNullOrEmpty(outPath)) { log.AppendLine("dump: no path given"); return; }

            var sb = new StringBuilder();
            UnityEngine.SceneManagement.Scene scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            sb.AppendLine($"scene {scene.path} dirty={scene.isDirty}");
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    sb.AppendLine($"== {GetPath(t)} active={t.gameObject.activeSelf} " +
                                  $"pos={t.localPosition} rot={t.localEulerAngles} scale={t.localScale}");
                    foreach (Component c in t.GetComponents<Component>())
                    {
                        if (c == null) { sb.AppendLine("   [MISSING SCRIPT]"); continue; }
                        if (c is Transform) continue;
                        string json = c is MonoBehaviour ? EditorJsonUtility.ToJson(c) : "";
                        sb.AppendLine($"   {c.GetType().Name} {json}");
                    }
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
            File.WriteAllText(outPath, sb.ToString());
            log.AppendLine($"dump: wrote {outPath}");
        }

        static string GetPath(Transform t) => t.parent == null ? t.name : GetPath(t.parent) + "/" + t.name;

        static void WriteStatus(string text)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(Status, text);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AgentBuildBridge] Could not write status: {e.Message}");
            }
        }
    }
}
