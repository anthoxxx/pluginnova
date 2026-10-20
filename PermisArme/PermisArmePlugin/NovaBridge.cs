using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AAMenu;
using Life;
using Life.Network;
using Life.UI;
using ModKit.Interfaces;

namespace PermisArmePlugin;

public static class NovaBridge
{
	public static int CharacterId(Player p)
	{
		return p.character.Id;
	}

	public static string FullName(Player p)
	{
		return p.FullName;
	}

	public static int Money(Player p)
	{
		return (int)p.Money;
	}

	public static void RemoveMoney(Player p, int amount, string reason)
	{
		p.AddMoney((double)(-amount), reason);
	}

	public static string Petit(string s)
	{
		return "<size=" + PermisStore.Config.TailleTexte + "%>" + s + "</size>";
	}

	public static List<string> Couper(string texte, int max)
	{
		List<string> list = new List<string>();
		string text = "";
		string[] array = (texte ?? "").Split(new char[1] { ' ' });
		foreach (string text2 in array)
		{
			if (text.Length > 0 && text.Length + 1 + text2.Length > max)
			{
				list.Add(text);
				text = text2;
			}
			else
			{
				text = ((text.Length == 0) ? text2 : (text + " " + text2));
			}
		}
		if (text.Length > 0)
		{
			list.Add(text);
		}
		return list;
	}

	public static bool IsPolice(Player p, List<string> servicesPolice)
	{
		return Activites(p).Any((string n) => servicesPolice.Any((string s) => !string.IsNullOrWhiteSpace(n) && !string.IsNullOrWhiteSpace(s) && (string.Equals(s, n, StringComparison.OrdinalIgnoreCase) || n.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)));
	}

	public static List<string> Activites(Player p)
	{
		List<string> list = new List<string>();
		try
		{
			foreach (object item in (IEnumerable)p.GetActivities())
			{
				if (item != null)
				{
					list.Add(item.ToString());
				}
			}
		}
		catch (Exception)
		{
		}
		return list;
	}

	public static void Notify(Player p, string msg, bool success = true)
	{
		p.Notify("Permis d'arme", msg, (Type)(success ? 1 : 3), 5f);
	}

	public static void Show(Player p, UIPanel panel)
	{
		p.ShowPanelUI(panel);
	}

	public static void Close(Player p, UIPanel panel)
	{
		p.ClosePanel(panel);
	}

	public static IEnumerable<Player> OnlinePlayers()
	{
		return Nova.server.Players;
	}

	public static void RegisterAdminMenu(PluginInformations infos, int minLevel, Action<Player> open, Func<UIPanel, Player> getPlayer)
	{
		Menu.AddAdminPluginTabLine(infos, minLevel, "Permis d'arme (config)", (Action<UIPanel>)delegate(UIPanel ui)
		{
			open(getPlayer(ui));
		});
	}
}
