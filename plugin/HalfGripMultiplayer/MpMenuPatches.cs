using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HalfGripMultiplayer;

/// <summary>Hooks the game's MainMenu Multiplayer stub + injects a start-menu button.</summary>
[HarmonyPatch(typeof(MainMenu), nameof(MainMenu.OpenMultiplayer))]
internal static class MainMenuOpenMultiplayerPatches
{
	[HarmonyPrefix]
	private static bool Prefix(MainMenu __instance)
	{
		if (__instance == null) return false;
		try
		{
			var ui = MpMenuUi.Ensure();
			ui.OpenForMainMenu(__instance);
			__instance.ShowMultiplayer();
			MpRuntime.Instance?.SetUiVisibleFromMenu(true);
			Plugin.Log.LogInfo("MainMenu.OpenMultiplayer → ShowMultiplayer + MpMenuUi");
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning($"OpenMultiplayer: {ex.Message}");
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
			// Pre-build section so Pull/ShowSection can activate it.
			var ui = MpMenuUi.Ensure();
			ui.OpenForMainMenu(__instance);
			ui.Show(false);
			EnsureMainMenuMultiplayerButton(__instance);
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning($"MainMenu MP awake: {ex.Message}");
		}
	}

	private static void EnsureMainMenuMultiplayerButton(MainMenu menu)
	{
		// If a scene button already calls OpenMultiplayer, we're done.
		foreach (var btn in menu.GetComponentsInChildren<Button>(true))
		{
			if (btn == null) continue;
			int count = btn.onClick.GetPersistentEventCount();
			for (int i = 0; i < count; i++)
			{
				if (btn.onClick.GetPersistentMethodName(i) == "OpenMultiplayer")
				{
					btn.gameObject.SetActive(true);
					btn.interactable = true;
					var tmp = btn.GetComponentInChildren<TMP_Text>(true);
					if (tmp != null && string.IsNullOrWhiteSpace(tmp.text))
						tmp.text = "MULTIPLAYER";
					return;
				}
			}
			var label = btn.GetComponentInChildren<TMP_Text>(true);
			if (label != null && label.text != null &&
			    label.text.IndexOf("MULTI", System.StringComparison.OrdinalIgnoreCase) >= 0)
			{
				btn.gameObject.SetActive(true);
				btn.interactable = true;
				btn.onClick.RemoveAllListeners();
				btn.onClick.AddListener(menu.OpenMultiplayer);
				return;
			}
		}

		// No scene button — clone style from Continue / Collection if possible.
		var skin = menu.Skin;
		if (skin == null) return;
		var canvas = menu.GetComponentInParent<Canvas>();
		Transform parent = canvas != null ? canvas.transform : menu.transform;
		// Prefer sitting near other main buttons (left column).
		var anchor = FindMainButtonColumn(menu) ?? parent;
		var btnGo = UiKit.Btn("HGMP_MultiplayerBtn", anchor, skin.menuButton, skin.buttonSize,
			"MULTIPLAYER", skin.font, skin.buttonFontSize, skin.textColor, skin.menuButtonPressed,
			() =>
			{
				menu.PlayClick();
				menu.OpenMultiplayer();
			});
		var rt = (RectTransform)btnGo.transform;
		rt.anchoredPosition = new Vector2(0f, -220f);
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
		MpMenuUi.Instance?.Show(false);
		MpRuntime.Instance?.SetUiVisibleFromMenu(false);
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
		if (__instance == null || __instance.Skin == null) return;
		try
		{
			if (MainButtonsField?.GetValue(__instance) is not List<Button> buttons) return;
			// Avoid duplicates on domain reload.
			foreach (var b in buttons)
			{
				if (b != null && b.name.IndexOf("MULTI", System.StringComparison.OrdinalIgnoreCase) >= 0)
					return;
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
						MpRuntime.Instance?.SetUiVisibleFromMenu(true);
						MpMenuUi.Ensure().OpenOverlay(__instance.Skin, __instance.Root);
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
						MpRuntime.Instance?.SetUiVisibleFromMenu(true);
						MpMenuUi.Ensure().OpenOverlay(skin, __instance.Root);
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
			Plugin.Log.LogWarning($"StartMenu MP button: {ex.Message}");
		}
	}
}
