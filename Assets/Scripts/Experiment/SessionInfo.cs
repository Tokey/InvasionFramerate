using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.InputSystem;

namespace JndUfo
{
    /// <summary>
    /// What a session ran on and with, written once at its start as <c>SessionInfo_&lt;id&gt;.csv</c> (key, value)
    /// beside the three logs: the build, the machine, the display, the three config files (by hash), and every
    /// public setting of the game's own components in the scene.
    ///
    /// Those settings are the Inspector values that are not in ExperimentConfig.csv but change the stimulus or
    /// the data's worth: the rain, the parallax swing and the hand-held wander (whole-screen motion that shows the
    /// frame rate), the world scroll's period and follow, the UFO's size, the tower spawn band — and the debug
    /// readout, which spells out the very difference being measured and must be off for a participant. Without
    /// this file a session's logs could not say how the scene was set. Gathered by reflection, so a setting added
    /// to a component later is recorded without anyone remembering to add it here.
    ///
    /// Keys: <c>section.name</c> for the run itself, <c>Type.field</c> for a component's setting
    /// (<c>Type[GameObject].field</c> when the scene has more than one of that type). Values: invariant culture;
    /// booleans true/false; vectors and colours as their components joined with ';'.
    /// </summary>
    public static class SessionInfo
    {
        public static List<KeyValuePair<string, string>> Gather(int sessionId, string runId)
        {
            var e = new List<KeyValuePair<string, string>>(256);
            void Add(string k, object v) => e.Add(new KeyValuePair<string, string>(k, Format(v)));

            Add("session.sessionId", sessionId);
            Add("session.runId", runId);
            Add("session.writtenIso", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));

            Add("app.productName", Application.productName);
            Add("app.version", Application.version);
            Add("app.unityVersion", Application.unityVersion);
            Add("app.platform", Application.platform);
            Add("app.isEditor", Application.isEditor);
            Add("app.buildGuid", Application.buildGUID);

            Add("system.os", SystemInfo.operatingSystem);
            Add("system.cpu", SystemInfo.processorType);
            Add("system.cpuCount", SystemInfo.processorCount);
            Add("system.cpuFrequencyMHz", SystemInfo.processorFrequency);
            Add("system.memoryMB", SystemInfo.systemMemorySize);
            Add("system.gpu", SystemInfo.graphicsDeviceName);
            Add("system.gpuVendor", SystemInfo.graphicsDeviceVendor);
            Add("system.gpuApi", SystemInfo.graphicsDeviceType);
            Add("system.gpuMemoryMB", SystemInfo.graphicsMemorySize);
            Add("system.deviceModel", SystemInfo.deviceModel);

            Add("display.width", Screen.width);
            Add("display.height", Screen.height);
            Add("display.fullScreenMode", Screen.fullScreenMode);
            Add("display.refreshHz", Screen.currentResolution.refreshRateRatio.value);
            Add("display.dpi", Screen.dpi);
            Add("display.name", Screen.mainWindowDisplayInfo.name);
            Add("quality.level", QualitySettings.names.Length > 0
                                     ? QualitySettings.names[QualitySettings.GetQualityLevel()] : "");
            Add("quality.vSyncCount", QualitySettings.vSyncCount);
            Add("quality.antiAliasing", QualitySettings.antiAliasing);

            Mouse mouse = Mouse.current;
            Add("input.mouse", mouse != null ? mouse.description.product + " / " + mouse.displayName : "");
            Add("input.keyboard", Keyboard.current != null ? Keyboard.current.displayName : "");

            AddFile(e, "config.experimentConfig", ExperimentPaths.Config);
            AddFile(e, "config.latinSquare", Path.Combine(Path.GetDirectoryName(ExperimentPaths.Config) ?? "", "LatinSquare.csv"));
            AddFile(e, "config.practiceRounds", Path.Combine(Path.GetDirectoryName(ExperimentPaths.Config) ?? "", "PracticeRounds.csv"));

            AddComponents(e);
            return e;
        }

        // Which config file a session read: its path, size, last write and SHA-256, so two sessions can be told to
        // have run the same file or not.
        static void AddFile(List<KeyValuePair<string, string>> e, string key, string path)
        {
            void Add(string k, object v) => e.Add(new KeyValuePair<string, string>(k, Format(v)));
            Add(key + ".path", path);
            try
            {
                if (!File.Exists(path)) { Add(key + ".exists", false); return; }
                byte[] bytes = File.ReadAllBytes(path);
                using (var sha = SHA256.Create())
                    Add(key + ".sha256", BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant());
                Add(key + ".bytes", bytes.Length);
                Add(key + ".lastWriteIso", File.GetLastWriteTime(path).ToString("o", CultureInfo.InvariantCulture));
            }
            catch (Exception ex) { Add(key + ".error", ex.Message); }
        }

        // Every public instance field of a simple type on every component of the game's own (namespace JndUfo, or
        // none, which is where UIManager lives), in the loaded scenes, inactive ones included; plus whether each is
        // enabled and on an active object.
        static void AddComponents(List<KeyValuePair<string, string>> e)
        {
            var all = new List<MonoBehaviour>();
            foreach (MonoBehaviour mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include))
            {
                if (mb == null) continue;
                string ns = mb.GetType().Namespace;
                if (ns == null || ns == "JndUfo" || ns.StartsWith("JndUfo.", StringComparison.Ordinal)) all.Add(mb);
            }
            all.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.GetType().Name, b.GetType().Name);
                return c != 0 ? c : string.CompareOrdinal(a.gameObject.name, b.gameObject.name);
            });

            var counts = new Dictionary<Type, int>();
            foreach (MonoBehaviour mb in all)
                counts[mb.GetType()] = counts.TryGetValue(mb.GetType(), out int n) ? n + 1 : 1;

            foreach (MonoBehaviour mb in all)
            {
                Type t = mb.GetType();
                string prefix = counts[t] > 1 ? $"{t.Name}[{mb.gameObject.name}]" : t.Name;
                e.Add(new KeyValuePair<string, string>(prefix + ".enabled", Format(mb.enabled && mb.gameObject.activeInHierarchy)));

                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (f.IsInitOnly && !f.FieldType.IsPrimitive) continue;
                    if (!IsSimple(f.FieldType)) continue;
                    object v;
                    try { v = f.GetValue(mb); }
                    catch { continue; }
                    e.Add(new KeyValuePair<string, string>(prefix + "." + f.Name, Format(v)));
                }
            }
        }

        static bool IsSimple(Type t) =>
            t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(Vector2) || t == typeof(Vector3) ||
            t == typeof(Color) || t == typeof(Key);

        static string Format(object v)
        {
            switch (v)
            {
                case null:     return "";
                case bool b:   return b ? "true" : "false";
                case float f:  return f.ToString("R", CultureInfo.InvariantCulture);
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case Vector2 a: return Join(a.x, a.y);
                case Vector3 a: return Join(a.x, a.y, a.z);
                case Color c:   return Join(c.r, c.g, c.b, c.a);
                case IFormattable fm: return fm.ToString(null, CultureInfo.InvariantCulture);
                default:       return v.ToString();
            }
        }

        static string Join(params float[] xs)
        {
            var parts = new string[xs.Length];
            for (int i = 0; i < xs.Length; i++) parts[i] = xs[i].ToString("R", CultureInfo.InvariantCulture);
            return string.Join(";", parts);
        }
    }
}
