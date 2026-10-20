using System.Collections.Generic;

namespace PermisArmePlugin;

public class ExamQuestion
{
	public string Texte { get; set; }

	public List<string> Reponses { get; set; } = new List<string>();


	public int BonneReponse { get; set; }
}
