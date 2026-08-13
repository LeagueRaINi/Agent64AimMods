using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace Agent64AimMods
{
    /// <summary>
    /// Copies the aim target onto the reticle every frame, replacing the game's easing.
    /// </summary>
    public class ReticleController : MonoBehaviour
    {
        /// <summary>How long to wait before retrying the scene lookups, in seconds.</summary>
        private const float SearchInterval = 0.5f;

        /// <summary>Squared pixel distance under which the target counts as centred.</summary>
        private const float CentredThreshold = 1e-6f;

        /// <summary>Deepest ancestor included when building a transform's path.</summary>
        private const int MaxPathDepth = 8;

        /// <summary>IL2CPP type the aim target field is required to have.</summary>
        private const string TargetTypeName = "UnityEngine.Vector2";

        private Component agent;
        private RectTransform reticle;
        private Behaviour reticleImage;
        private OffsetDetector detector;
        private IntPtr targetField;
        private bool bindFailed;
        private float lastSearch;
        private string lastWarning;

        public ReticleController(IntPtr pointer) : base(pointer)
        {
        }

        private void Update()
        {
            HandleHotkeys();
            Plugin.Toasts.Prune();

            // Unity runs every Update before any LateUpdate, so writing here is what
            // Agent.LateUpdate reads when it poses the weapon. Without this the weapon
            // trails the mouse by one frame.
            Apply(afterGameEasing: false);
        }

        private void OnGUI()
        {
            Plugin.Toasts.Draw();
        }

        private void LateUpdate()
        {
            // The game eases the reticle in its own LateUpdate. Writing again afterwards
            // is what makes the un-eased position the one the canvas actually renders.
            Apply(afterGameEasing: true);
        }

        private void HandleHotkeys()
        {
            var options = Plugin.Options;

            Toggle(options.ToggleAimKey, options.InstantAim, "Instant aim");
            Toggle(options.ToggleRecentreKey, options.InstantRecentre, "Instant recentre");
            Toggle(options.ToggleShowKey, options.AlwaysShowReticle, "Always show reticle");
        }

        private static void Toggle(ConfigEntry<KeyCode> key, ConfigEntry<bool> setting, string label)
        {
            if (!Input.GetKeyDown(key.Value))
            {
                return;
            }

            setting.Value = !setting.Value;
            Announce($"{label}: {Settings.OnOff(setting.Value)}");
        }

        /// <summary>Puts a line in the console and on screen at the same time.</summary>
        private static void Announce(string message)
        {
            Plugin.Logger.LogInfo(message);
            Plugin.Toasts.Show(message);
        }

        /// <param name="afterGameEasing">
        /// True for the LateUpdate pass, the only one where the reticle's position reflects
        /// the game's own easing and is therefore worth measuring.
        /// </param>
        private void Apply(bool afterGameEasing)
        {
            var options = Plugin.Options;
            if (options.Idle)
            {
                Forget();
                return;
            }

            try
            {
                if (!Resolve())
                {
                    return;
                }

                // The widget hides itself by disabling this Image, so re-enabling it after
                // that has run is all it takes to keep the reticle on screen. Done before
                // anything else so it still works while the offset is being detected.
                if (options.AlwaysShowReticle.Value && reticleImage != null)
                {
                    reticleImage.enabled = true;
                }

                if (!BindOrDetect())
                {
                    // Detection measures how the game eases the reticle, so it can only
                    // observe, never write, until it has an answer.
                    if (afterGameEasing && detector != null)
                    {
                        Detect();
                    }

                    return;
                }

                Vector2 target = ReadTarget();

                // The game snaps the target to exactly (0, 0) the moment aim is released,
                // so a centred target is precisely the not-aiming case. Each mod owns one
                // side of that split.
                bool centred = target.sqrMagnitude < CentredThreshold;
                bool snap = centred ? options.InstantRecentre.Value : options.InstantAim.Value;
                if (snap)
                {
                    reticle.anchoredPosition = target;
                }
            }
            catch (Exception e)
            {
                Warn($"Failed to apply aim mods: {e.Message}");
                Forget();
            }
        }

        /// <summary>
        /// Ensures every reference is live, re-searching at most once per
        /// <see cref="SearchInterval"/> because each lookup sweeps the whole scene.
        /// </summary>
        private bool Resolve()
        {
            if (reticle != null && agent != null && agent.Pointer != IntPtr.Zero)
            {
                return true;
            }

            if (Time.unscaledTime - lastSearch < SearchInterval)
            {
                return false;
            }

            lastSearch = Time.unscaledTime;

            if (reticle == null)
            {
                reticle = FindReticle(Plugin.Options.ReticlePath.Value);
                reticleImage = reticle == null
                    ? null
                    : FindComponent(reticle, Plugin.Options.ReticleImageType.Value);
            }

            agent = FindPlayerAgent(Plugin.Options.AgentType.Value);

            // A different Agent instance means anything bound to the old one is stale.
            targetField = IntPtr.Zero;
            detector = null;
            bindFailed = false;

            return reticle != null && agent != null;
        }

        /// <summary>
        /// Binds the aim target, falling back to detecting it by behaviour when the
        /// configured offset does not describe a real field.
        /// </summary>
        /// <returns><c>true</c> once the field is bound and can be read.</returns>
        private bool BindOrDetect()
        {
            if (targetField != IntPtr.Zero)
            {
                return true;
            }

            if (detector != null || bindFailed)
            {
                return false;
            }

            targetField = BindTargetField(agent.Pointer);
            if (targetField != IntPtr.Zero)
            {
                return true;
            }

            if (!Plugin.Options.AutoDetectOffset.Value)
            {
                bindFailed = true;
                return false;
            }

            detector = new OffsetDetector(agent.Pointer);
            Plugin.Logger.LogInfo(
                $"Detecting the aim target from {detector.CandidateCount} candidate fields. " +
                "Aim and move the mouse for a few seconds; the mods are inactive until it resolves.");
            Plugin.Toasts.Show("Finding the aim target, keep aiming...");

            return false;
        }

        /// <summary>Feeds the detector one frame, and adopts the result once it is sure.</summary>
        private void Detect()
        {
            if (!detector.Observe(reticle.anchoredPosition, out int offset))
            {
                return;
            }

            detector = null;

            // Persisted so the search happens once per game version rather than per launch.
            Plugin.Options.TargetOffset.Value = $"0x{offset:X}";
            targetField = BindTargetField(agent.Pointer);

            Plugin.Toasts.Show($"Aim target found at 0x{offset:X}, mods active");
        }

        /// <summary>Finds the named behaviour on a transform's own GameObject.</summary>
        private static Behaviour FindComponent(Component owner, string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return null;
            }

            foreach (var candidate in owner.GetComponents(Il2CppType.Of<Behaviour>()))
            {
                var behaviour = candidate == null ? null : candidate.TryCast<Behaviour>();
                if (behaviour != null && behaviour.GetIl2CppType().Name == typeName)
                {
                    return behaviour;
                }
            }

            return null;
        }

        /// <summary>
        /// Looks the aim target up in IL2CPP's own field table rather than trusting the
        /// configured offset blindly. A game update that moves or retypes the field then
        /// fails with a readable message instead of silently reading whatever now lives
        /// at that address.
        /// </summary>
        /// <returns>The matching field, or <see cref="IntPtr.Zero"/> if there is none.</returns>
        private IntPtr BindTargetField(IntPtr instance)
        {
            string configured = Plugin.Options.TargetOffset.Value;

            int wanted = ParseOffset(configured);
            if (wanted < 0)
            {
                Warn($"TargetOffset '{configured}' is not a hex offset.");
                return IntPtr.Zero;
            }

            for (IntPtr klass = IL2CPP.il2cpp_object_get_class(instance);
                 klass != IntPtr.Zero;
                 klass = IL2CPP.il2cpp_class_get_parent(klass))
            {
                IntPtr iterator = IntPtr.Zero;
                IntPtr field;

                while ((field = IL2CPP.il2cpp_class_get_fields(klass, ref iterator)) != IntPtr.Zero)
                {
                    if (IL2CPP.il2cpp_field_get_offset(field) != wanted)
                    {
                        continue;
                    }

                    string typeName = TypeNameOf(field);
                    if (typeName != TargetTypeName)
                    {
                        Warn($"Field at {configured} is {typeName}, expected {TargetTypeName}. " +
                             "The game has probably been updated; correct TargetOffset in the config.");
                        return IntPtr.Zero;
                    }

                    lastWarning = null;
                    return field;
                }
            }

            Warn($"No field at offset {configured} on '{Plugin.Options.AgentType.Value}'. " +
                 "The game has probably been updated; correct TargetOffset in the config.");
            return IntPtr.Zero;
        }

        /// <summary>Reads the aim target through IL2CPP, which knows the field's size and layout.</summary>
        private unsafe Vector2 ReadTarget()
        {
            Vector2 target = default;
            IL2CPP.il2cpp_field_get_value(agent.Pointer, targetField, &target);
            return target;
        }

        /// <summary>Drops the cached references so the next <see cref="Apply"/> re-resolves them.</summary>
        private void Forget()
        {
            agent = null;
            reticle = null;
            reticleImage = null;
            detector = null;
            targetField = IntPtr.Zero;
            bindFailed = false;
        }

        /// <summary>Logs a message once, so a persistent fault cannot spam the console every frame.</summary>
        private void Warn(string message)
        {
            if (lastWarning == message)
            {
                return;
            }

            lastWarning = message;
            Plugin.Logger.LogWarning(message);

            // A warning means the mods have stopped working, which is exactly the case
            // nobody would think to check the console for.
            Plugin.Toasts.Show(message);
        }

        private static string TypeNameOf(IntPtr field)
        {
            IntPtr type = IL2CPP.il2cpp_field_get_type(field);
            return type == IntPtr.Zero
                ? "<unknown>"
                : Marshal.PtrToStringUTF8(IL2CPP.il2cpp_type_get_name(type));
        }

        private static RectTransform FindReticle(string path)
        {
            foreach (var candidate in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<RectTransform>()))
            {
                var rect = candidate == null ? null : candidate.TryCast<RectTransform>();
                if (rect != null && PathOf(rect.transform) == path)
                {
                    return rect;
                }
            }

            return null;
        }

        /// <summary>
        /// Finds the local player. Every character in the game runs the same script, so the
        /// instance closest to the camera is the one being played.
        /// </summary>
        private static Component FindPlayerAgent(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return null;
            }

            var camera = Camera.main;
            Vector3 eye = camera != null ? camera.transform.position : Vector3.zero;

            Component nearest = null;
            float nearestDistance = float.MaxValue;

            foreach (var candidate in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<MonoBehaviour>()))
            {
                var behaviour = candidate == null ? null : candidate.TryCast<MonoBehaviour>();
                if (behaviour == null || behaviour.GetIl2CppType().Name != typeName)
                {
                    continue;
                }

                float distance = Vector3.Distance(eye, behaviour.transform.position);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = behaviour;
                }
            }

            return nearest;
        }

        private static string PathOf(Transform transform)
        {
            var names = new List<string>();
            for (Transform node = transform; node != null && names.Count < MaxPathDepth; node = node.parent)
            {
                names.Add(node.name);
            }

            names.Reverse();
            return string.Join("/", names);
        }

        private static int ParseOffset(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return -1;
            }

            string trimmed = text.Trim();
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(2);
            }

            return int.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int offset)
                ? offset
                : -1;
        }
    }
}
