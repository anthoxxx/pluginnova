using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace PermisArmePlugin;

public static class PermisStore
{
	public static string Dir;

	public static PermisConfig Config = new PermisConfig();

	public static List<PermisEntry> Liste = new List<PermisEntry>();

	private static readonly object _lock = new object();

	private static string ConfigPath => Path.Combine(Dir, "config.json");

	private static string PermisPath => Path.Combine(Dir, "permis.json");

	public static void Init(string dir)
	{
		Dir = dir;
		Directory.CreateDirectory(Dir);
		LoadConfig();
		LoadPermis();
	}

	public static void LoadConfig()
	{
		lock (_lock)
		{
			if (!File.Exists(ConfigPath))
			{
				Config = new PermisConfig();
				SaveConfig();
				return;
			}
			Config = JsonConvert.DeserializeObject<PermisConfig>(File.ReadAllText(ConfigPath)) ?? new PermisConfig();
			if (Config.Questions == null || Config.Questions.Count == 0)
			{
				Config.Questions = PermisConfig.QuestionsDeBase();
				SaveConfig();
			}
		}
	}

	public static void SaveConfig()
	{
		lock (_lock)
		{
			File.WriteAllText(ConfigPath, JsonConvert.SerializeObject((object)Config, (Formatting)1));
		}
	}

	public static void LoadPermis()
	{
		lock (_lock)
		{
			if (!File.Exists(PermisPath))
			{
				Liste = new List<PermisEntry>();
			}
			else
			{
				Liste = JsonConvert.DeserializeObject<List<PermisEntry>>(File.ReadAllText(PermisPath)) ?? new List<PermisEntry>();
			}
		}
	}

	public static void SavePermis()
	{
		lock (_lock)
		{
			File.WriteAllText(PermisPath, JsonConvert.SerializeObject((object)Liste, (Formatting)1));
		}
	}

	public static bool APermis(int characterId)
	{
		return Liste.Any((PermisEntry p) => p.CharacterId == characterId);
	}

	public static void Ajouter(int characterId, string nom)
	{
		if (!APermis(characterId))
		{
			Liste.Add(new PermisEntry
			{
				CharacterId = characterId,
				Nom = nom,
				DateObtention = DateTime.Now.ToString("dd/MM/yyyy HH:mm")
			});
			SavePermis();
		}
	}

	public static bool Retirer(int characterId)
	{
		int num = Liste.RemoveAll((PermisEntry p) => p.CharacterId == characterId);
		if (num > 0)
		{
			SavePermis();
		}
		return num > 0;
	}
}
