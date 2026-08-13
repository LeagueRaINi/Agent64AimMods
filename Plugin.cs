using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace Agent64AimMods
{
    /// <summary>
    /// Removes the frame-rate dependent easing Agent 64 applies to its aim reticle.
    /// </summary>
    /// <remarks>
    /// While the aim button is held the camera is locked and the mouse drags a reticle
    /// around a fixed screen box. The <c>Agent</c> script stores the mouse's position in
    /// that box as a Vector2 at <c>+0x220</c>, updated 1:1 with no smoothing, and reset to
    /// <c>(0, 0)</c> the moment aim is released.
    /// <para>
    /// What lags is the visible reticle: a RectTransform that eases toward <c>+0x220</c> at
    /// a hardcoded 0.1 per frame, leaving it roughly seven frames behind the mouse and
    /// taking about 0.3s to settle. Being per-frame rather than per-second, it gets worse
    /// the higher the framerate.
    /// </para>
    /// <para>
    /// That reticle is not only cosmetic. <c>Agent.LateUpdate</c> walks <c>Agent+0x158</c>
    /// (HUD) to <c>+0x30</c> (widget) to <c>+0x18</c> (RectTransform), reads its position
    /// and builds the weapon's aim rotation from it, so the easing also drives where the
    /// gun points. This plugin writes <c>+0x220</c> straight onto the RectTransform
    /// instead, without patching the binary.
    /// </para>
    /// </remarks>
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BasePlugin
    {
        public const string Guid = "agent64.aimmods";
        public const string Name = "Agent 64 Aim Mods";
        public const string Version = "1.2.0";

        internal static ManualLogSource Logger { get; private set; }
        internal static Settings Options { get; private set; }

        public override void Load()
        {
            Logger = Log;
            Options = new Settings(Config);

            ClassInjector.RegisterTypeInIl2Cpp<ReticleController>();

            var host = new GameObject(nameof(Agent64AimMods))
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            Object.DontDestroyOnLoad(host);
            host.AddComponent<ReticleController>();

            Logger.LogInfo($"{Name} {Version} loaded. {Options.Describe()}");
        }
    }

    /// <summary>Configuration surface, bound once at load.</summary>
    internal sealed class Settings
    {
        private const string Mods = "Mods";
        private const string Keys = "Hotkeys";
        private const string Advanced = "Advanced";

        internal Settings(ConfigFile config)
        {
            InstantAim = config.Bind(Mods, "InstantAim", true,
                "Removes the easing while you are aiming, so the reticle and the weapon " +
                "follow the mouse exactly. This is the main one.");

            InstantRecentre = config.Bind(Mods, "InstantRecentre", true,
                "Removes the leftover drift after you release aim. The reticle keeps " +
                "coasting for about 0.3s once the game has already recentred it, and the " +
                "weapon aims along that stale position, so shots taken immediately after " +
                "releasing aim can land up to ~40px off. Does not affect idle weapon sway.");

            AlwaysShowReticle = config.Bind(Mods, "AlwaysShowReticle", false,
                "Keeps the aim reticle on screen instead of only while aiming. The game " +
                "hides it by disabling its Image; this re-enables it every frame. Note it " +
                "marks the aim target the game feeds the weapon, which sits dead centre " +
                "while hip firing, and it stays visible in menus and cutscenes.");

            ToggleAimKey = config.Bind(Keys, "ToggleInstantAim", KeyCode.F7,
                "Toggles InstantAim in game, for comparing against stock behaviour.");

            ToggleRecentreKey = config.Bind(Keys, "ToggleInstantRecentre", KeyCode.F8,
                "Toggles InstantRecentre in game.");

            ToggleShowKey = config.Bind(Keys, "ToggleAlwaysShowReticle", KeyCode.F9,
                "Toggles AlwaysShowReticle in game.");

            ReticlePath = config.Bind(Advanced, "ReticlePath", "$GUI/Canvas/Ingame/Target",
                "Hierarchy path of the reticle RectTransform.");

            AgentType = config.Bind(Advanced, "AgentType", "Agent",
                "Script holding the aim target. Every character uses it, so the instance " +
                "nearest the camera is taken to be the player.");

            TargetOffset = config.Bind(Advanced, "TargetOffset", "0x220",
                "Offset of the Vector2 aim target on that script, in reticle pixels.");

            ReticleImageType = config.Bind(Advanced, "ReticleImageType", "Image",
                "Component on the reticle whose 'enabled' flag controls its visibility.");

            AutoDetectOffset = config.Bind(Advanced, "AutoDetectOffset", true,
                "If TargetOffset no longer names a Vector2 field, which is what a game " +
                "update would cause, find the aim target by watching which field the " +
                "reticle eases toward and save the result back to TargetOffset. Takes a " +
                "few seconds of aiming, once.");
        }

        internal ConfigEntry<bool> InstantAim { get; }
        internal ConfigEntry<bool> InstantRecentre { get; }
        internal ConfigEntry<bool> AlwaysShowReticle { get; }
        internal ConfigEntry<KeyCode> ToggleAimKey { get; }
        internal ConfigEntry<KeyCode> ToggleRecentreKey { get; }
        internal ConfigEntry<KeyCode> ToggleShowKey { get; }
        internal ConfigEntry<string> ReticlePath { get; }
        internal ConfigEntry<string> AgentType { get; }
        internal ConfigEntry<string> TargetOffset { get; }
        internal ConfigEntry<string> ReticleImageType { get; }
        internal ConfigEntry<bool> AutoDetectOffset { get; }

        /// <summary>True while no option is active and the plugin should stay out of the way.</summary>
        internal bool Idle => !InstantAim.Value && !InstantRecentre.Value && !AlwaysShowReticle.Value;

        internal string Describe() =>
            $"InstantAim {OnOff(InstantAim.Value)} ({ToggleAimKey.Value}), " +
            $"InstantRecentre {OnOff(InstantRecentre.Value)} ({ToggleRecentreKey.Value}), " +
            $"AlwaysShowReticle {OnOff(AlwaysShowReticle.Value)} ({ToggleShowKey.Value}).";

        internal static string OnOff(bool value) => value ? "ON" : "OFF";
    }
}
