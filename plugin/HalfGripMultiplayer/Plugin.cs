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
	internal ConfigEntry<bool> UiStartVisible = null!;
	internal ConfigEntry<KeyCode> ToggleUiKey = null!;
	internal ConfigEntry<KeyCode> HostKey = null!;
	internal ConfigEntry<KeyCode> LeaveKey = null!;
	internal ConfigEntry<KeyCode> PingKey = null!;
	internal ConfigEntry<KeyCode> ReadyKey = null!;
	internal ConfigEntry<KeyCode> ForceStartKey = null!;
	internal ConfigEntry<float> PoseHz = null!;
	internal ConfigEntry<bool> GhostTransparent = null!;
	internal ConfigEntry<bool> FriendsOnly = null!;
	internal ConfigEntry<int> LanPort = null!;

	private Harmony? _harmony;

	private void Awake()
	{
		Instance = this;
		Enabled = Config.Bind("General", "Enabled", true, "Enable multiplayer systems");
		// Hidden by default so IMGUI TextFields do not eat keyboard / Steam overlay (Shift+Tab).
		UiStartVisible = Config.Bind("General", "UiStartVisible", false, "Show multiplayer menu on launch (F6 toggles)");
		ToggleUiKey = Config.Bind("Hotkeys", "ToggleUi", KeyCode.F6, "Show/hide multiplayer menu");
		HostKey = Config.Bind("Hotkeys", "HostSteam", KeyCode.F7, "Host Steam friends lobby");
		LeaveKey = Config.Bind("Hotkeys", "Leave", KeyCode.F8, "Leave session");
		PingKey = Config.Bind("Hotkeys", "WorldPing", KeyCode.Mouse2, "Ping map marker for peers (middle mouse)");
		ReadyKey = Config.Bind("Hotkeys", "ReadyToggle", KeyCode.F5, "Toggle lobby Ready while linked");
		ForceStartKey = Config.Bind("Hotkeys", "ForceStart", KeyCode.F4, "Host: unlock force-start while waiting on Ready");
		PoseHz = Config.Bind("Net", "PoseHz", 20f, new ConfigDescription("Pose sync Hz", new AcceptableValueRange<float>(5f, 60f)));
		GhostTransparent = Config.Bind("Visual", "GhostTransparent", true, "Tint remote cars");
		FriendsOnly = Config.Bind("Steam", "FriendsOnly", true, "Friends-only vs private lobby");
		LanPort = Config.Bind("LAN", "Port", NetProtocol.DefaultLanPort, "Default UDP port");

		_harmony = new Harmony(PluginInfo.GUID);
		_harmony.PatchAll();

		// Attach to the BepInEx plugin object (already DDOL). A separate GO created in
		// Awake was destroyed on the first scene load, so F6/Update/OnGUI never ran and
		// button clicks hit a null MpRuntime.Instance.
		MpRuntime.Ensure(gameObject);

		Logger.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} — F6 / MP MENU corner button.");
	}

	private void Update()
	{
		// Recreate if something destroyed the component mid-session.
		if (Enabled != null && Enabled.Value)
			MpRuntime.Ensure(gameObject);
	}

	private void OnDestroy() => _harmony?.UnpatchSelf();
}

internal static class PluginInfo
{
	public const string GUID = "landor.halfgrip.multiplayer";
	public const string Name = "Half Grip Multiplayer";
	public const string Version = "0.10.31";
}
