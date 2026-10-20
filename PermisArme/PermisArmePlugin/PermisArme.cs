using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AAMenu;
using Life;
using Life.BizSystem;
using Life.DB;
using Life.Network;
using Life.UI;
using Mirror;
using ModKit;
using ModKit.Helper;
using ModKit.Helper.JobHelper;
using ModKit.Helper.PointHelper;
using ModKit.Interfaces;
using ModKit.Internal;

namespace PermisArmePlugin;

public class PermisArme : ModKit
{
	public static class Examen
	{
		private class Session
		{
			public List<ExamQuestion> Qs;

			public int Index;

			public int Score;
		}

		public static void Proposer(Player p)
		{
			//IL_0111: Unknown result type (might be due to invalid IL or missing references)
			//IL_011b: Expected O, but got Unknown
			if (p == null || p.character == null)
			{
				return;
			}
			PermisConfig config = PermisStore.Config;
			int num = NovaBridge.CharacterId(p);
			if (PermisStore.APermis(num))
			{
				NovaBridge.Notify(p, config.Messages.PermisDejaPossede, success: false);
				return;
			}
			if (DernierEchec.TryGetValue(num, out var value))
			{
				double totalMinutes = (value.AddMinutes(config.DelaiNouvelEssaiMinutes) - DateTime.Now).TotalMinutes;
				if (totalMinutes > 0.0)
				{
					NovaBridge.Notify(p, Fmt(config.Messages.DelaiAttente, "minutes", Math.Ceiling(totalMinutes)), success: false);
					return;
				}
			}
			if (NovaBridge.Money(p) < config.PrixPermis)
			{
				NovaBridge.Notify(p, Fmt(config.Messages.PasAssezArgent, "prix", config.PrixPermis), success: false);
				return;
			}
			UIPanel panel = new UIPanel("Permis de port d'arme", (PanelType)0);
			panel.SetText(NovaBridge.Petit("Examen : " + config.NombreQuestions + " questions\nMinimum : " + config.NoteMinimale + " bonnes réponses\nPrix (débité si réussite) : " + config.PrixPermis + "€"));
			panel.AddButton("Commencer", (Action<UIPanel>)delegate
			{
				NovaBridge.Close(p, panel);
				Demarrer(p);
			});
			panel.AddButton("Annuler", (Action<UIPanel>)delegate
			{
				NovaBridge.Close(p, panel);
			});
			NovaBridge.Show(p, panel);
		}

		private static void Demarrer(Player p)
		{
			PermisConfig config = PermisStore.Config;
			List<ExamQuestion> list = config.Questions.ToList();
			if (config.MelangerQuestions)
			{
				list = list.OrderBy((ExamQuestion x) => Rng.Next()).ToList();
			}
			list = list.Take(Math.Min(config.NombreQuestions, list.Count)).ToList();
			if (list.Count == 0)
			{
				NovaBridge.Notify(p, "Aucune question configurée.", success: false);
				return;
			}
			Poser(p, new Session
			{
				Qs = list
			});
		}

		private static void Poser(Player p, Session s)
		{
			//IL_00ff: Unknown result type (might be due to invalid IL or missing references)
			//IL_0109: Expected O, but got Unknown
			if (s.Index >= s.Qs.Count)
			{
				Terminer(p, s);
				return;
			}
			PermisConfig config = PermisStore.Config;
			ExamQuestion q = s.Qs[s.Index];
			List<KeyValuePair<string, bool>> list = q.Reponses.Select((string txt, int i) => new KeyValuePair<string, bool>(txt, i == q.BonneReponse)).ToList();
			if (config.MelangerReponses)
			{
				list = list.OrderBy((KeyValuePair<string, bool> x) => Rng.Next()).ToList();
			}
			UIPanel panel = new UIPanel("Question " + (s.Index + 1) + "/" + s.Qs.Count, (PanelType)2);
			foreach (string item in NovaBridge.Couper(q.Texte, config.CaracteresParLigne))
			{
				panel.AddTabLine(NovaBridge.Petit("<color=#f0c040>" + item + "</color>"), (Action<UIPanel>)delegate
				{
				});
			}
			char c = 'A';
			foreach (KeyValuePair<string, bool> item2 in list)
			{
				bool bonne = item2.Value;
				panel.AddTabLine(NovaBridge.Petit(c + ") " + item2.Key), (Action<UIPanel>)delegate
				{
					if (bonne)
					{
						s.Score++;
					}
					s.Index++;
					NovaBridge.Close(p, panel);
					Poser(p, s);
				});
				c = (char)(c + 1);
			}
			panel.AddButton("Valider", (Action<UIPanel>)delegate(UIPanel ui)
			{
				ui.SelectTab();
			});
			panel.AddButton("Abandonner", (Action<UIPanel>)delegate
			{
				NovaBridge.Close(p, panel);
				DernierEchec[NovaBridge.CharacterId(p)] = DateTime.Now;
			});
			NovaBridge.Show(p, panel);
		}

		private static void Terminer(Player p, Session s)
		{
			PermisConfig config = PermisStore.Config;
			int num = NovaBridge.CharacterId(p);
			bool flag = s.Score >= config.NoteMinimale;
			if (flag && NovaBridge.Money(p) < config.PrixPermis)
			{
				NovaBridge.Notify(p, Fmt(config.Messages.PasAssezArgent, "prix", config.PrixPermis), success: false);
			}
			else if (flag)
			{
				NovaBridge.RemoveMoney(p, config.PrixPermis, "Permis de port d'arme");
				PermisStore.Ajouter(num, NovaBridge.FullName(p));
				DernierEchec.Remove(num);
				NovaBridge.Notify(p, Fmt(config.Messages.Reussite, "note", s.Score, "total", s.Qs.Count));
			}
			else
			{
				DernierEchec[num] = DateTime.Now;
				NovaBridge.Notify(p, Fmt(config.Messages.Echec, "note", s.Score, "total", s.Qs.Count, "min", config.NoteMinimale), success: false);
			}
		}
	}

	public static class PoliceMenu
	{
		public static void Ouvrir(Player police)
		{
			//IL_0019: Unknown result type (might be due to invalid IL or missing references)
			//IL_0023: Expected O, but got Unknown
			PermisStore.LoadPermis();
			UIPanel panel = new UIPanel("Registre permis d'arme", (PanelType)2);
			if (PermisStore.Liste.Count == 0)
			{
				panel.AddTabLine(NovaBridge.Petit("(aucun permis délivré)"), (Action<UIPanel>)delegate
				{
				});
			}
			foreach (PermisEntry item in PermisStore.Liste.ToList())
			{
				PermisEntry cur = item;
				panel.AddTabLine(NovaBridge.Petit(cur.Nom + " - " + cur.DateObtention), (Action<UIPanel>)delegate
				{
					NovaBridge.Close(police, panel);
					Confirmer(police, cur);
				});
			}
			panel.AddButton("Sélectionner", (Action<UIPanel>)delegate(UIPanel ui)
			{
				ui.SelectTab();
			});
			panel.AddButton("Fermer", (Action<UIPanel>)delegate
			{
				NovaBridge.Close(police, panel);
			});
			NovaBridge.Show(police, panel);
		}

		private static void Confirmer(Player police, PermisEntry pm)
		{
			//IL_001b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0025: Expected O, but got Unknown
			UIPanel panel = new UIPanel("Retirer le permis ?", (PanelType)0);
			panel.SetText(NovaBridge.Petit(pm.Nom + "\nObtenu le " + pm.DateObtention));
			panel.AddButton("Retirer le permis", (Action<UIPanel>)delegate
			{
				if (PermisStore.Retirer(pm.CharacterId))
				{
					NovaBridge.Notify(police, "Permis de " + pm.Nom + " retiré.");
					Player val = NovaBridge.OnlinePlayers().FirstOrDefault((Func<Player, bool>)((Player x) => x != null && x.character != null && NovaBridge.CharacterId(x) == pm.CharacterId));
					if (val != null)
					{
						NovaBridge.Notify(val, PermisStore.Config.Messages.PermisRetire, success: false);
					}
				}
				else
				{
					NovaBridge.Notify(police, "Ce permis n'est plus dans le registre.", success: false);
				}
				NovaBridge.Close(police, panel);
				Ouvrir(police);
			});
			panel.AddButton("Retour", (Action<UIPanel>)delegate
			{
				NovaBridge.Close(police, panel);
				Ouvrir(police);
			});
			NovaBridge.Show(police, panel);
		}
	}

	public static class AdminMenu
	{
		public static void Ouvrir(Player staff)
		{
			//IL_001f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0029: Expected O, but got Unknown
			PermisConfig c = PermisStore.Config;
			UIPanel panel = new UIPanel("Permis d'arme - Config", (PanelType)2);
			panel.AddTabLine(NovaBridge.Petit("Prix du permis : " + c.PrixPermis + "€"), (Action<UIPanel>)delegate
			{
				NovaBridge.Close(staff, panel);
				Saisir(staff, "Prix du permis (€)", delegate(int v)
				{
					c.PrixPermis = v;
				});
			});
			panel.AddTabLine(NovaBridge.Petit("Note minimale : " + c.NoteMinimale), (Action<UIPanel>)delegate
			{
				NovaBridge.Close(staff, panel);
				Saisir(staff, "Note minimale", delegate(int v)
				{
					c.NoteMinimale = v;
				});
			});
			panel.AddTabLine(NovaBridge.Petit("Questions par examen : " + c.NombreQuestions), (Action<UIPanel>)delegate
			{
				NovaBridge.Close(staff, panel);
				Saisir(staff, "Nombre de questions", delegate(int v)
				{
					c.NombreQuestions = v;
				});
			});
			panel.AddTabLine(NovaBridge.Petit("Délai nouvel essai : " + c.DelaiNouvelEssaiMinutes + " min"), (Action<UIPanel>)delegate
			{
				NovaBridge.Close(staff, panel);
				Saisir(staff, "Délai (minutes)", delegate(int v)
				{
					c.DelaiNouvelEssaiMinutes = v;
				});
			});
			panel.AddTabLine(NovaBridge.Petit("Taille du texte : " + c.TailleTexte + "%"), (Action<UIPanel>)delegate
			{
				NovaBridge.Close(staff, panel);
				Saisir(staff, "Taille du texte (%)", delegate(int v)
				{
					c.TailleTexte = Math.Max(30, Math.Min(100, v));
				});
			});
			panel.AddTabLine(NovaBridge.Petit("Points bleus d'examen (placer / gérer)"), (Action<UIPanel>)delegate
			{
				NovaBridge.Close(staff, panel);
				OuvrirPoints(staff);
			});
			panel.AddTabLine(NovaBridge.Petit("Registre des permis (" + PermisStore.Liste.Count + ")"), (Action<UIPanel>)delegate
			{
				NovaBridge.Close(staff, panel);
				PoliceMenu.Ouvrir(staff);
			});
			panel.AddTabLine(NovaBridge.Petit("Recharger config.json"), (Action<UIPanel>)delegate
			{
				PermisStore.LoadConfig();
				NovaBridge.Notify(staff, "Config rechargée.");
			});
			panel.AddButton("Sélectionner", (Action<UIPanel>)delegate(UIPanel ui)
			{
				ui.SelectTab();
			});
			panel.AddButton("Fermer", (Action<UIPanel>)delegate
			{
				NovaBridge.Close(staff, panel);
			});
			NovaBridge.Show(staff, panel);
		}

		private static void Saisir(Player staff, string titre, Action<int> set)
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_0021: Expected O, but got Unknown
			UIPanel panel = new UIPanel(titre, (PanelType)1);
			panel.SetInputPlaceholder("Entrez un nombre");
			panel.AddButton("Valider", (Action<UIPanel>)delegate(UIPanel ui)
			{
				if (int.TryParse(ui.inputText, out var result) && result >= 0)
				{
					set(result);
					PermisStore.SaveConfig();
					NovaBridge.Notify(staff, "Enregistré.");
					NovaBridge.Close(staff, panel);
					Ouvrir(staff);
				}
				else
				{
					NovaBridge.Notify(staff, "Nombre invalide.", success: false);
				}
			});
			panel.AddButton("Annuler", (Action<UIPanel>)delegate
			{
				NovaBridge.Close(staff, panel);
				Ouvrir(staff);
			});
			NovaBridge.Show(staff, panel);
		}
	}

	public static PermisArme Instance;

	private static readonly Random Rng = new Random();

	private static readonly Dictionary<int, DateTime> DernierEchec = new Dictionary<int, DateTime>();

	public PermisArme(IGameAPI api)
		: base(api)
	{
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Expected O, but got Unknown
		base.PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.1.0", "Matheo Mercier");
		Instance = this;
	}

	public override void OnPluginInit()
	{
		//IL_0106: Unknown result type (might be due to invalid IL or missing references)
		//IL_013e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0176: Unknown result type (might be due to invalid IL or missing references)
		((Plugin)this).OnPluginInit();
		PermisStore.Init(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "PermisArme"));
		((ModKit)this).Orm.RegisterTable<ExamPattern>();
		ExamPattern examPattern = new ExamPattern(isCreated: false)
		{
			Context = (ModKit)(object)this
		};
		((ModKit)this).PointHelper.AddPattern("ExamPattern", (PatternData)(object)examPattern);
		if (AAMenu.menu != null)
		{
			AAMenu.menu.AddBuilder(base.PluginInformations, "Permis d'arme", (PatternData)(object)examPattern, (ModKit)(object)this);
		}
		else
		{
			Logger.LogWarning(base.PluginInformations.SourceName, "AAMenu introuvable : utilisez /permisarmepoint.");
		}
		Func<UIPanel, Player> getPlayer = (UIPanel ui) => ((ModKit)this).PanelHelper.ReturnPlayerFromPanel(ui);
		NovaBridge.RegisterAdminMenu(base.PluginInformations, PermisStore.Config.NiveauAdminMin, AdminMenu.Ouvrir, getPlayer);
		new SChatCommand("/permisarme", "Registre des permis d'arme (police)", "/permisarme", (Action<Player, string[]>)delegate(Player player, string[] args)
		{
			OuvrirPolice(player);
		}).Register();
		new SChatCommand("/monpermisarme", "Consulter votre permis de port d'arme", "/monpermisarme", (Action<Player, string[]>)delegate(Player player, string[] args)
		{
			MonPermis(player);
		}).Register();
		new SChatCommand("/permisarmepoint", "Placer les points d'examen (staff)", "/permisarmepoint", (Action<Player, string[]>)delegate(Player player, string[] args)
		{
			OuvrirPoints(player);
		}).Register();
		Menu.AddDocumentTabLine(base.PluginInformations, "Mon permis de port d'arme", (Action<UIPanel>)delegate(UIPanel ui)
		{
			MonPermis(getPlayer(ui));
		});
		Menu.AddBizTabLine(base.PluginInformations, new List<Type> { (Type)1 }, (CustomActivity)null, "Registre permis d'arme", (Action<UIPanel>)delegate(UIPanel ui)
		{
			OuvrirPolice(getPlayer(ui));
		});
		Logger.LogSuccess(base.PluginInformations.SourceName, "Permis d'arme chargé - By Matheo Mercier");
	}

	public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
	{
		((ModKit)this).OnPlayerSpawnCharacter(player, conn, character);
		((ModKit)this).PointHelper.InitAllNPoint(player);
	}

	public static void OuvrirPoints(Player staff)
	{
		if (!staff.IsAdmin || !staff.serviceAdmin)
		{
			NovaBridge.Notify(staff, "Vous devez être staff et en service admin.", success: false);
			return;
		}
		ExamPattern examPattern = new ExamPattern(isCreated: false);
		examPattern.Context = (ModKit)(object)Instance;
		examPattern.CreateOrGenerate(staff);
	}

	public static void OuvrirPolice(Player player)
	{
		if (player != null && player.character != null)
		{
			if (!NovaBridge.IsPolice(player, PermisStore.Config.ServicesPolice))
			{
				string text = (player.IsAdmin ? (" (vos activités : " + string.Join(", ", NovaBridge.Activites(player)) + ")") : "");
				NovaBridge.Notify(player, "Réservé à la police." + text, success: false);
			}
			else
			{
				PoliceMenu.Ouvrir(player);
			}
		}
	}

	public static void MonPermis(Player player)
	{
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Expected O, but got Unknown
		if (player != null && player.character != null)
		{
			PermisStore.LoadPermis();
			PermisEntry permisEntry = PermisStore.Liste.FirstOrDefault((PermisEntry x) => x.CharacterId == NovaBridge.CharacterId(player));
			UIPanel panel = new UIPanel("Mon permis d'arme", (PanelType)0);
			panel.SetText(NovaBridge.Petit((permisEntry == null) ? "Vous ne possédez pas de permis de port d'arme." : ("Titulaire : " + permisEntry.Nom + "\nDate d'obtention : " + permisEntry.DateObtention)));
			panel.AddButton("Fermer", (Action<UIPanel>)delegate
			{
				NovaBridge.Close(player, panel);
			});
			NovaBridge.Show(player, panel);
		}
	}

	private static string Fmt(string s, string k1, object v1, string k2 = null, object v2 = null, string k3 = null, object v3 = null)
	{
		s = s.Replace("{" + k1 + "}", Convert.ToString(v1));
		if (k2 != null)
		{
			s = s.Replace("{" + k2 + "}", Convert.ToString(v2));
		}
		if (k3 != null)
		{
			s = s.Replace("{" + k3 + "}", Convert.ToString(v3));
		}
		return s;
	}
}
