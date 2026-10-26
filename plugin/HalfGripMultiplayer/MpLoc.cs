using System.Collections.Generic;

namespace HalfGripMultiplayer;

/// <summary>
/// Thin wrapper: prefers game Loc.T, with a Russian overlay for our own strings
/// when the CSV has no row yet.
/// </summary>
internal static class MpLoc
{
	private static readonly Dictionary<string, string> Ru = new()
	{
		["MULTIPLAYER"] = "МУЛЬТИПЛЕЕР",
		["BACK"] = "НАЗАД",
		["STEAM"] = "STEAM",
		["LAN"] = "LAN",
		["LOBBY"] = "ЛОББИ",
		["RULES"] = "ПРАВИЛА",
		["SANDBOX"] = "ПЕСОЧНИЦА",
		["CHAT"] = "ЧАТ",
		["HOST LOBBY"] = "СОЗДАТЬ ЛОББИ",
		["INVITE"] = "ПРИГЛАСИТЬ",
		["LEAVE"] = "ВЫЙТИ",
		["JOIN"] = "ВОЙТИ",
		["HOST LAN"] = "ХОСТ LAN",
		["JOIN LAN"] = "ВОЙТИ LAN",
		["READY"] = "ГОТОВ",
		["UNREADY"] = "НЕ ГОТОВ",
		["FORCE START"] = "ФОРС СТАРТ",
		["COPY ID"] = "КОПИРОВАТЬ ID",
		["REFRESH"] = "ОБНОВИТЬ",
		["PLAYGROUND"] = "ПЛОЩАДКА",
		["SEND"] = "ОТПРАВИТЬ",
		["Free play (no auto-finish)"] = "Свободная игра (без финиша)",
		["No bots (humans only)"] = "Без ботов (только игроки)",
		["No guns"] = "Без оружия",
		["Shared shop pool"] = "Общий пул магазина",
		["Short race"] = "Короткая гонка",
		["Ceasefire (sec)"] = "Перемирие (сек)",
		["STANDARD"] = "ОБЫЧНЫЙ",
		["TIME ATTACK"] = "НА ВРЕМЯ",
		["LAST MAN"] = "ПОСЛЕДНИЙ",
		["ASSASSINATE CO-OP"] = "УБИЙСТВО КО-ОП",
		["Race mode"] = "Режим гонки",
		["Offline — host a Steam lobby or LAN session"] = "Оффлайн — создайте Steam-лобби или LAN",
		["Not connected"] = "Нет соединения",
		["Steam lobbies"] = "Лобби Steam",
		["No lobbies found"] = "Лобби не найдены",
		["YOU"] = "ВЫ",
		["Trade"] = "ОБМЕН",
		["Offer trade"] = "Предложить обмен",
		["Pool"] = "Пул",
		["Copied lobby ID"] = "ID лобби скопирован",
		["Friends lobbies with hgmp tag"] = "Дружеские лобби с тегом hgmp",
	};

	public static string T(string english)
	{
		if (string.IsNullOrEmpty(english)) return english;
		try
		{
			string lang = Loc.Language ?? "english";
			if (lang.IndexOf("russian", System.StringComparison.OrdinalIgnoreCase) >= 0
			    || lang.Equals("ru", System.StringComparison.OrdinalIgnoreCase))
			{
				if (Ru.TryGetValue(english, out var ru))
					return ru;
			}
			return Loc.T(english);
		}
		catch
		{
			return english;
		}
	}
}
