using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Life.Network;
using Life.UI;
using ModKit;
using ModKit.Helper;
using ModKit.Helper.PointHelper;
using ModKit.ORM;
using SQLite;

namespace PermisArmePlugin;

public class ExamPattern : ModEntity<ExamPattern>, PatternData
{
	[AutoIncrement]
	[PrimaryKey]
	public int Id { get; set; }

	public string PatternName { get; set; }

	[Ignore]
	public string TypeName { get; set; } = "ExamPattern";


	[Ignore]
	public ModKit Context { get; set; }

	private ModKit Ctx => (ModKit)(((object)Context) ?? ((object)PermisArme.Instance));

	public ExamPattern()
	{
	}

	public ExamPattern(bool isCreated)
	{
		TypeName = "ExamPattern";
	}

	public void OnPlayerTrigger(Player player)
	{
		PermisArme.Examen.Proposer(player);
	}

	public async Task SetProperties(int id)
	{
		ExamPattern examPattern = await ModEntity<ExamPattern>.Query(id);
		Id = id;
		TypeName = "ExamPattern";
		PatternName = examPattern?.PatternName;
	}

	public async void CreateOrGenerate(Player player)
	{
		if (!player.IsAdmin)
		{
			return;
		}
		List<ExamPattern> list = await ModEntity<ExamPattern>.QueryAll();
		Panel panel = Ctx.PanelHelper.Create("Permis d'arme - Points", (PanelType)2, player, (Action)delegate
		{
			CreateOrGenerate(player);
		});
		if (list.Count == 0)
		{
			((UIPanel)panel).AddTabLine(NovaBridge.Petit("Aucun modèle : cliquez « Nouveau modèle »"), (Action<UIPanel>)delegate
			{
			});
		}
		foreach (ExamPattern pattern in list)
		{
			((UIPanel)panel).AddTabLine(NovaBridge.Petit("[" + pattern.Id + "] " + pattern.PatternName), (Action<UIPanel>)async delegate
			{
				pattern.TypeName = "ExamPattern";
				pattern.Context = Ctx;
				if (await Ctx.PointHelper.CreateNPoint(player, (PatternData)(object)pattern))
				{
					NovaBridge.Notify(player, "Point « " + pattern.PatternName + " » placé à votre position.");
				}
				else
				{
					NovaBridge.Notify(player, "Erreur lors de la création du point.", success: false);
				}
			});
		}
		if (list.Count > 0)
		{
			panel.NextButton("Placer ici", (Action)delegate
			{
				((UIPanel)panel).SelectTab();
			});
		}
		panel.NextButton("Nouveau modèle", (Action)delegate
		{
			SetPatternData(player);
		});
		panel.NextButton("Modèles", (Action)async delegate
		{
			await GetPatternData(player, forEdit: true);
		});
		panel.NextButton("Points", (Action)async delegate
		{
			await GetNPoints(player);
		});
		panel.CloseButton();
		panel.Display();
	}

	public void SetPatternData(Player player)
	{
		Panel panel = Ctx.PanelHelper.Create("Nouveau point d'examen", (PanelType)1, player, (Action)delegate
		{
			SetPatternData(player);
		});
		panel.TextLines.Add(NovaBridge.Petit("Nom du point (ex. Armurerie centre)"));
		((UIPanel)panel).SetInputPlaceholder("Examen permis d'arme");
		panel.PreviousButtonWithAction("Créer", (Func<Task<bool>>)async delegate
		{
			string name = ((UIPanel)panel).inputText?.Trim();
			if (string.IsNullOrEmpty(name))
			{
				NovaBridge.Notify(player, "Le nom ne peut pas être vide.", success: false);
				return false;
			}
			if (!(await ((ModEntity<ExamPattern>)new ExamPattern(isCreated: false)
			{
				PatternName = name
			}).Save()))
			{
				NovaBridge.Notify(player, "Erreur lors de l'enregistrement.", success: false);
				return false;
			}
			NovaBridge.Notify(player, "Modèle « " + name + " » créé. Choisissez-le puis « Placer ici ».");
			return true;
		});
		panel.PreviousButton();
		panel.CloseButton();
		panel.Display();
	}

	public async Task GetPatternData(Player player, bool forEdit)
	{
		List<ExamPattern> list = await ModEntity<ExamPattern>.QueryAll();
		string action = "";
		Panel panel = Ctx.PanelHelper.Create("Permis d'arme - Modèles", (PanelType)2, player, (Action)async delegate
		{
			await GetPatternData(player, forEdit);
		});
		if (list.Count == 0)
		{
			((UIPanel)panel).AddTabLine(NovaBridge.Petit("Aucun modèle"), (Action<UIPanel>)delegate
			{
			});
		}
		foreach (ExamPattern pattern in list)
		{
			((UIPanel)panel).AddTabLine(NovaBridge.Petit("[" + pattern.Id + "] " + pattern.PatternName), (Action<UIPanel>)async delegate
			{
				pattern.TypeName = "ExamPattern";
				pattern.Context = Ctx;
				if (action == "rename")
				{
					RenamePattern(player, pattern);
				}
				else if (action == "delete")
				{
					await Ctx.PointHelper.DeleteNPointsByPattern(player, (PatternData)(object)pattern);
					if (await ((ModEntity<ExamPattern>)pattern).Delete())
					{
						NovaBridge.Notify(player, "Modèle « " + pattern.PatternName + " » et ses points supprimés.");
					}
					else
					{
						NovaBridge.Notify(player, "Erreur lors de la suppression.", success: false);
					}
					panel.Refresh();
				}
			});
		}
		if (list.Count > 0 && forEdit)
		{
			panel.NextButton("Renommer", (Action)delegate
			{
				action = "rename";
				((UIPanel)panel).SelectTab();
			});
			((UIPanel)panel).AddButton(TextFormattingHelper.Color("Supprimer", (Colors)14495822), (Action<UIPanel>)delegate
			{
				action = "delete";
				((UIPanel)panel).SelectTab();
			});
		}
		panel.PreviousButton();
		panel.CloseButton();
		panel.Display();
	}

	private void RenamePattern(Player player, ExamPattern pattern)
	{
		Panel panel = Ctx.PanelHelper.Create("Renommer le point", (PanelType)1, player, (Action)delegate
		{
			RenamePattern(player, pattern);
		});
		panel.TextLines.Add(NovaBridge.Petit("Nouveau nom pour « " + pattern.PatternName + " »"));
		((UIPanel)panel).SetInputPlaceholder(pattern.PatternName ?? "Examen permis d'arme");
		panel.PreviousButtonWithAction("Valider", (Func<Task<bool>>)async delegate
		{
			string text = ((UIPanel)panel).inputText?.Trim();
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}
			pattern.PatternName = text;
			return await ((ModEntity<ExamPattern>)pattern).Save();
		});
		panel.PreviousButton();
		panel.CloseButton();
		panel.Display();
	}

	public async Task GetNPoints(Player player)
	{
		List<NPoint> points = await ModEntity<NPoint>.Query((Expression<Func<NPoint, bool>>)((NPoint p) => p.TypeName == "ExamPattern"));
		Dictionary<int, string> dictionary = (await ModEntity<ExamPattern>.QueryAll()).ToDictionary((ExamPattern p) => p.Id, (ExamPattern p) => p.PatternName);
		string action = "";
		Panel panel = Ctx.PanelHelper.Create("Permis d'arme - Points placés", (PanelType)2, player, (Action)async delegate
		{
			await GetNPoints(player);
		});
		if (points.Count == 0)
		{
			((UIPanel)panel).AddTabLine(NovaBridge.Petit("Aucun point placé"), (Action<UIPanel>)delegate
			{
			});
		}
		foreach (NPoint point in points)
		{
			string value;
			string text = (dictionary.TryGetValue(point.PatternId, out value) ? value : "?");
			((UIPanel)panel).AddTabLine(NovaBridge.Petit("Point #" + point.Id + " - " + text), (Action<UIPanel>)async delegate
			{
				switch (action)
				{
				case "tp":
					Ctx.PointHelper.PlayerSetPositionToNPoint(player, point);
					break;
				case "move":
					if (await Ctx.PointHelper.SetNPointPosition(player, point))
					{
						NovaBridge.Notify(player, "Point déplacé à votre position.");
					}
					break;
				case "delete":
					await Ctx.PointHelper.DeleteNPoint(point);
					NovaBridge.Notify(player, "Point #" + point.Id + " supprimé.");
					panel.Refresh();
					break;
				}
			});
		}
		if (points.Count > 0)
		{
			((UIPanel)panel).AddButton("Se téléporter", (Action<UIPanel>)delegate
			{
				action = "tp";
				((UIPanel)panel).SelectTab();
			});
			((UIPanel)panel).AddButton("Déplacer ici", (Action<UIPanel>)delegate
			{
				action = "move";
				((UIPanel)panel).SelectTab();
			});
			((UIPanel)panel).AddButton(TextFormattingHelper.Color("Supprimer", (Colors)14495822), (Action<UIPanel>)delegate
			{
				action = "delete";
				((UIPanel)panel).SelectTab();
			});
		}
		panel.PreviousButton();
		panel.CloseButton();
		panel.Display();
	}
}
