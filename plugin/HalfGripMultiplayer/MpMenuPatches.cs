using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HalfGripMultiplayer;

/// <summary>Hooks the game's MainMenu Multiplayer stub + injects start-menu / main-menu buttons.</summary>
[HarmonyPatch(typeof(MainMenu), nameof(MainMenu.OpenMultiplayer))]
internal static class MainMenuOpenMultiplayerPatches
{
	[HarmonyPrefix]
	private static bool Prefix(MainMenu __instance)
	{
		Plugin.Log.LogInfo("MainMenu.OpenMultiplayer invoked");
		try
		{
			MpRuntime.Ensure().ForceOpenMenu();
			// Keep game view on Main so animator/sections don't fight our overlay.
			try { __instance?.CloseMultiplayer(); } catch { /* */ }
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning($"OpenMultiplayer: {ex}");
		}
		return false; // skip empty original body
	}
}

[HarmonyPatch(typeof(MainMenu), "Awake")]
internal static class MainMenuAwakeMultiplayerPatches
{
	[HarmonyPostfix]
	private static void Postfix(MainMenu __instance)
	{
		if (__instance == null) return;
		try
		{
			// Dummy section so SetView(Multiplayer) never touches our DDOL UI.
			if (__instance.MultiplayerSection == null)
			{
				var dummy = new GameObject("HGMP_MultiplayerSectionDummy");
				dummy.transform.SetParent(__instance.transform, false);
				dummy.SetActive(false);
				__instance.MultiplayerSection = dummy;
			}

			WireAllMultiplayerButtons(__instance);
			InjectFallbackButton(__instance);
			Plugin.Log.LogInfo("MainMenu awake: MP buttons wired");
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning($"MainMenu MP awake: {ex}");
		}
	}

	private static void WireAllMultiplayerButtons(MainMenu menu)
	{
		int wired = 0;
		foreach (var btn in menu.GetComponentsInChildren<Button>(true))
		{
			if (btn == null) continue;
			bool match = false;
			int count = btn.onClick.GetPersistentEventCount();
			for (int i = 0; i < count; i++)
			{
				if (btn.onClick.GetPersistentMethodName(i) == "OpenMultiplayer")
				{
					match = true;
					break;
				}
			}
			var label = btn.GetComponentInChildren<TMP_Text>(true);
			string text = label != null ? label.text ?? "" : "";
			if (!match && text.IndexOf("MULTI", System.StringComparison.OrdinalIgnoreCase) < 0)
				continue;

			btn.gameObject.SetActive(true);
			btn.interactable = true;
			if (label != null && string.IsNullOrWhiteSpace(label.text))
				label.text = "MULTIPLAYER";
			btn.onClick.RemoveAllListeners();
			btn.onClick.AddListener(() =>
			{
				Plugin.Log.LogInfo($"MainMenu button '{btn.name}' → ForceOpenMenu");
				try { menu.PlayClick(); } catch { /* */ }
				MpRuntime.Ensure().ForceOpenMenu();
			});
			wired++;
		}
		Plugin.Log.LogInfo($"Wired {wired} MainMenu multiplayer button(s)");
	}

	private static void InjectFallbackButton(MainMenu menu)
	{
		if (menu.transform.Find("HGMP_MultiplayerBtn") != null) return;
		var skin = menu.Skin;
		if (skin == null)
		{
			Plugin.Log.LogWarning("MainMenu Skin null — skip inject");
			return;
		}
		var canvas = menu.GetComponentInParent<Canvas>();
		Transform parent = canvas != null ? canvas.transform : menu.transform;
		var anchor = FindMainButtonColumn(menu) ?? parent;
		var btnGo = UiKit.Btn("HGMP_MultiplayerBtn", anchor, skin.menuButton, skin.buttonSize,
			"MULTIPLAYER", skin.font, skin.buttonFontSize, skin.textColor, skin.menuButtonPressed,
			() =>
			{
				menu.PlayClick();
				Plugin.Log.LogInfo("Injected MainMenu MULTIPLAYER clicked");
				MpRuntime.Ensure().ForceOpenMenu();
			});
		((RectTransform)btnGo.transform).anchoredPosition = new Vector2(0f, -220f);
		Plugin.Log.LogInfo("Injected MainMenu MULTIPLAYER button");
	}

	private static Transform? FindMainButtonColumn(MainMenu menu)
	{
		foreach (var t in menu.GetComponentsInChildren<Transform>(true))
		{
			if (t == null) continue;
			string n = t.name ?? "";
			if (n.IndexOf("Button", System.StringComparison.OrdinalIgnoreCase) >= 0 &&
			    t.childCount >= 2)
				return t;
		}
		return null;
	}
}

[HarmonyPatch(typeof(MainMenu), nameof(MainMenu.CloseMultiplayer))]
internal static class MainMenuCloseMultiplayerPatches
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		// Do not auto-close IMGUI here — Escape / Hide owns that.
	}
}

[HarmonyPatch(typeof(StartMenuProgression), "BuildMain")]
internal static class StartMenuMultiplayerButtonPatches
{
	private static readonly FieldInfo? MainButtonsField =
		AccessTools.Field(typeof(StartMenuProgression), "mainButtons");
	private static readonly FieldInfo? MainRootField =
		AccessTools.Field(typeof(StartMenuProgression), "mainRoot");
	private static readonly MethodInfo? MakeMainButton =
		AccessTools.Method(typeof(StartMenuProgression), "MakeMainButton");
	private static readonly MethodInfo? LayoutMain =
		AccessTools.Method(typeof(StartMenuProgression), "LayoutMain");

	[HarmonyPostfix]
	private static void Postfix(StartMenuProgression __instance)
	{
		if (__instance == null) return;
		try
		{
			if (__instance.Skin == null)
			{
				Plugin.Log.LogWarning("StartMenu BuildMain: Skin null");
				return;
			}
			if (MainButtonsField?.GetValue(__instance) is not List<Button> buttons)
			{
				Plugin.Log.LogWarning("StartMenu BuildMain: mainButtons missing");
				return;
			}
			foreach (var b in buttons)
			{
				if (b != null && b.name.IndexOf("MULTI", System.StringComparison.OrdinalIgnoreCase) >= 0)
				{
					Plugin.Log.LogInfo("StartMenu already has MULTIPLAYER");
					return;
				}
			}

			Button? btn = null;
			if (MakeMainButton != null)
			{
				btn = MakeMainButton.Invoke(__instance, new object[]
				{
					"MULTIPLAYER",
					(System.Action)(() =>
					{
						__instance.PlayClick();
						Plugin.Log.LogInfo("StartMenu MULTIPLAYER clicked");
						MpRuntime.Ensure().ForceOpenMenu();
					}),
				}) as Button;
			}
			if (btn == null)
			{
				if (MainRootField?.GetValue(__instance) is not RectTransform root) return;
				var skin = __instance.Skin;
				btn = UiKit.Btn("MULTIPLAYER", root, skin.menuButton, skin.buttonSize, "MULTIPLAYER",
					skin.font, skin.buttonFontSize, skin.textColor, skin.menuButtonPressed, () =>
					{
						__instance.PlayClick();
						MpRuntime.Ensure().ForceOpenMenu();
					});
			}
			buttons.Add(btn);
			if (MainRootField?.GetValue(__instance) is RectTransform mainRoot && __instance.Skin != null)
			{
				float h = __instance.Skin.buttonSize.y * buttons.Count
				          + __instance.Skin.buttonSpacing * Mathf.Max(0, buttons.Count - 1);
				mainRoot.sizeDelta = new Vector2(__instance.Skin.buttonSize.x, h);
			}
			LayoutMain?.Invoke(__instance, null);
			Plugin.Log.LogInfo("Injected StartMenu MULTIPLAYER button");
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning($"StartMenu MP button: {ex}");
		}
	}
}
