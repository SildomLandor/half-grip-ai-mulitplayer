using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace HalfGripMultiplayer;

[BepInPlugin(PluginInfo.GUID, PluginInfo.Name, PluginInfo.Version)]
public sealed class Plugin : BaseUnityPlugin
{
	internal static Plugin Instance { get; private set; } = null!;
	internal static ManualLogSource Log => Instance.Logger;

	internal ConfigEntry<bool> Enabled = null!;
	internal ConfigEntry<KeyCode> ToggleUiKey = null!;
	internal ConfigEntry<KeyCode> HostKey = null!;
	internal ConfigEntry<KeyCode> LeaveKey = null!;
	internal ConfigEntry<float> PoseHz = null!;
	internal ConfigEntry<bool> GhostTransparent = null!;
	internal ConfigEntry<bool> FriendsOnly = null!;
	internal ConfigEntry<int> LanPort = null!;

	private Harmony? _harmony;

	private void Awake()
	{
		Instance = this;
		Enabled = Config.Bind("General", "Enabled", true, "Enable multiplayer systems");
		ToggleUiKey = Config.Bind("Hotkeys", "ToggleUi", KeyCode.F6, "Show/hide overlay");
		HostKey = Config.Bind("Hotkeys", "HostSteam", KeyCode.F7, "Host Steam friends lobby");
		LeaveKey = Config.Bind("Hotkeys", "Leave", KeyCode.F8, "Leave session");
		PoseHz = Config.Bind("Net", "PoseHz", 20f, new ConfigDescription("Pose sync Hz", new AcceptableValueRange<float>(5f, 60f)));
		GhostTransparent = Config.Bind("Visual", "GhostTransparent", true, "Tint remote cars");
		FriendsOnly = Config.Bind("Steam", "FriendsOnly", true, "Friends-only vs private lobby");
		LanPort = Config.Bind("LAN", "Port", NetProtocol.DefaultLanPort, "Default UDP port");

		_harmony = new Harmony(PluginInfo.GUID);
		_harmony.PatchAll();

		var go = new GameObject("HalfGripMultiplayer");
		DontDestroyOnLoad(go);
		go.AddComponent<MpRuntime>();

		Logger.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} — protocol v9, blast/ram/rocket/pickup sync.");
	}

	private void OnDestroy() => _harmony?.UnpatchSelf();
}

internal static class PluginInfo
{
	public const string GUID = "landor.halfgrip.multiplayer";
	public const string Name = "Half Grip Multiplayer";
	public const string Version = "0.9.0";
}
